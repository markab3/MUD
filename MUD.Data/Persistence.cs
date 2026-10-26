using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using MongoDB.Bson;

namespace MUD.Data
{
    public enum PersistKind
    {
        /// <summary>Whole-document upsert by _id.</summary>
        Replace,
        /// <summary>Field-level $set of Doc; falls back to a Replace of Fallback when no document matched.</summary>
        SetFields
    }

    /// <summary>
    /// An immutable-by-convention snapshot handed from the game thread to the persistence worker
    /// (specs/GAME_LOOP.md section 8). The worker never sees live entities, only these.
    /// </summary>
    public class PersistRequest
    {
        public string Collection { get; set; }
        public string Id { get; set; }
        public PersistKind Kind { get; set; }
        public BsonDocument Doc { get; set; }
        /// <summary>Whole-entity snapshot used when a SetFields write matches no stored document.</summary>
        public BsonDocument Fallback { get; set; }
        /// <summary>Logout write: never coalesced away, never dropped, retried for as long as the process runs.</summary>
        public bool Final { get; set; }
        public string Reason { get; set; }
        /// <summary>Game thread, once, on the first failed attempt (argument: attempts so far).</summary>
        public Action<int> OnFailed { get; set; }
        /// <summary>Game thread, once, when the request leaves the worker: true = written, false = abandoned.</summary>
        public Action<bool> OnCompleted { get; set; }
        /// <summary>Worker thread, after a successful write (used to delete spool files).</summary>
        public Action AfterWrite { get; set; }
    }

    /// <summary>Where the game thread hands snapshots. Post never blocks.</summary>
    public interface IPersistSink
    {
        void Post(PersistRequest request);
    }

    /// <summary>Performs one write. Returns true when acknowledged (matched or upserted); may throw.</summary>
    public interface IPersistWriter
    {
        bool Write(PersistRequest request);
    }

    /// <summary>
    /// Snapshot-based access to a repository's collection that never touches the model cache, plus the hook
    /// the entities use to find the persistence sink. Implemented by Repository&lt;T&gt;.
    /// </summary>
    public interface ISnapshotRepository
    {
        string CollectionName { get; }
        /// <summary>When set, entities hand their saves to this sink instead of writing synchronously.</summary>
        IPersistSink Sink { get; set; }
        bool ReplaceDocument(string id, BsonDocument document);
        bool SetFields(string id, BsonDocument set);
        /// <summary>Drops the cached live instance so the next load reads the database.</summary>
        void Evict(string id);
        /// <summary>_ids of the live (cached) instances. Safe from any thread.</summary>
        List<string> CachedIds();
        /// <summary>
        /// Stored Rev (absent = 0) of each existing document among ids; ids with no stored document are absent from the
        /// result. Reads only _id and Rev, never touches the cache. Safe from any thread (specs/WEB_EDITING.md 6.1).
        /// </summary>
        Dictionary<string, int> GetRevisions(IEnumerable<string> ids);
    }

    /// <summary>Writes requests through the repositories' snapshot methods.</summary>
    public class RepositoryPersistWriter : IPersistWriter
    {
        private readonly Func<string, ISnapshotRepository> _resolve;

        public RepositoryPersistWriter(Func<string, ISnapshotRepository> resolve)
        {
            _resolve = resolve;
        }

        public bool Write(PersistRequest request)
        {
            var repository = _resolve(request.Collection);
            if (repository == null) { throw new InvalidOperationException("No repository for collection " + request.Collection); }

            if (request.Kind == PersistKind.Replace)
            {
                return repository.ReplaceDocument(request.Id, request.Doc);
            }

            if (repository.SetFields(request.Id, request.Doc)) { return true; }
            // No stored document matched: write the whole entity, as Room.SaveState always has.
            return request.Fallback != null && repository.ReplaceDocument(request.Id, request.Fallback);
        }
    }

    /// <summary>Extended-JSON files for snapshots that could not be written before the process exits.</summary>
    public class SpoolStore
    {
        private readonly string _directory;

        public SpoolStore(string directory)
        {
            _directory = directory;
        }

        public string Directory { get { return _directory; } }

        public string Write(PersistRequest request)
        {
            System.IO.Directory.CreateDirectory(_directory);
            var wrapper = new BsonDocument
            {
                { "kind", request.Kind.ToString() },
                { "collection", request.Collection },
                { "id", request.Id },
                { "final", request.Final },
                { "doc", request.Doc ?? new BsonDocument() }
            };
            if (request.Fallback != null) { wrapper["fallback"] = request.Fallback; }
            string path = PathFor(request);
            System.IO.File.WriteAllText(path, wrapper.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.CanonicalExtendedJson }));
            return path;
        }

        public IEnumerable<KeyValuePair<string, PersistRequest>> ReadAll()
        {
            if (!System.IO.Directory.Exists(_directory)) { yield break; }
            foreach (string path in System.IO.Directory.GetFiles(_directory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                PersistRequest request = null;
                try
                {
                    var wrapper = BsonDocument.Parse(System.IO.File.ReadAllText(path));
                    request = new PersistRequest
                    {
                        Kind = (PersistKind)Enum.Parse(typeof(PersistKind), wrapper["kind"].AsString),
                        Collection = wrapper["collection"].AsString,
                        Id = wrapper["id"].AsString,
                        Final = wrapper["final"].AsBoolean,
                        Doc = wrapper["doc"].AsBsonDocument,
                        Fallback = wrapper.Contains("fallback") ? wrapper["fallback"].AsBsonDocument : null,
                        Reason = "spool replay"
                    };
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Unreadable spool file " + path + ": " + ex.Message);
                }
                if (request != null) { yield return new KeyValuePair<string, PersistRequest>(path, request); }
            }
        }

        private string PathFor(PersistRequest request)
        {
            string name = request.Collection + "-" + request.Id;
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) { name = name.Replace(c, '_'); }
            return System.IO.Path.Combine(_directory, name + ".json");
        }
    }

    /// <summary>
    /// The single persistence worker (specs/GAME_LOOP.md section 8). Requests arrive on an unbounded channel
    /// and are applied in order. Newer snapshots of the same entity replace older queued ones (except Final
    /// requests), failures are retried with exponential backoff, and on shutdown whatever cannot be written
    /// is spooled to disk.
    ///
    /// Pump() does all the work for the current clock time and never blocks or sleeps, so tests drive it
    /// directly with a fake clock; Run() is the thin loop that calls it on the worker thread.
    /// </summary>
    public class PersistenceWorker : IPersistSink
    {
        public const int MaxAttempts = 5;
        public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

        private class Entry
        {
            public PersistRequest Request;
            public int Attempts;
            public DateTime NextAttemptAt = DateTime.MinValue;
            public bool FailureReported;
            public string Key { get { return Request.Collection + "/" + Request.Id; } }
        }

        private readonly IPersistWriter _writer;
        private readonly IClock _clock;
        private readonly Action<Action> _postToGame;
        private readonly SpoolStore _spool;
        private readonly Action<string> _log;
        private readonly Channel<PersistRequest> _channel = Channel.CreateUnbounded<PersistRequest>(new UnboundedChannelOptions { SingleReader = true });
        private readonly List<Entry> _pending = new List<Entry>();
        private volatile bool _shutdownRequested;
        private TimeSpan _shutdownTimeout = TimeSpan.FromSeconds(30);

        /// <param name="postToGame">Marshals a callback to the game thread (outcome callbacks). May be null.</param>
        public PersistenceWorker(IPersistWriter writer, IClock clock, Action<Action> postToGame, SpoolStore spool = null, Action<string> log = null)
        {
            _writer = writer;
            _clock = clock;
            _postToGame = postToGame;
            _spool = spool;
            _log = log ?? Console.WriteLine;
        }

        /// <summary>Requests queued in the worker (not counting items still in the channel). Worker thread only.</summary>
        public int PendingCount { get { return _pending.Count; } }

        public void Post(PersistRequest request)
        {
            if (request == null) { throw new ArgumentNullException(nameof(request)); }
            if (!_channel.Writer.TryWrite(request))
            {
                // Posted after shutdown completed: keep the snapshot rather than losing it.
                _log("Persistence request for " + request.Collection + "/" + request.Id + " arrived after shutdown; spooling.");
                Spool(request);
            }
        }

        /// <summary>Loads spooled snapshots from an earlier run and queues them. Files are deleted once written.</summary>
        public int ReplaySpool()
        {
            if (_spool == null) { return 0; }
            int count = 0;
            foreach (var item in _spool.ReadAll())
            {
                string path = item.Key;
                item.Value.AfterWrite = () => { try { System.IO.File.Delete(path); } catch (Exception) { } };
                Post(item.Value);
                count++;
            }
            return count;
        }

        /// <summary>
        /// Applies every queued request that is due at the current clock time. Returns the time of the next
        /// retry, or null when nothing is waiting.
        /// </summary>
        public DateTime? Pump()
        {
            PersistRequest incoming;
            while (_channel.Reader.TryRead(out incoming)) { Add(incoming); }

            DateTime now = _clock.Now;
            var blockedKeys = new HashSet<string>();
            int i = 0;
            while (i < _pending.Count)
            {
                Entry entry = _pending[i];
                string key = entry.Key;
                // Writes for one entity stay in order: nothing passes an earlier request that is still waiting.
                if (blockedKeys.Contains(key) || entry.NextAttemptAt > now)
                {
                    blockedKeys.Add(key);
                    i++;
                    continue;
                }

                bool ok = false;
                bool permanent = false;
                try { ok = _writer.Write(entry.Request); }
                catch (DuplicateKeyException ex)
                {
                    // Retrying cannot help (e.g. the account name was taken by someone else).
                    _log("Persist of " + key + " rejected as a duplicate: " + ex.Message);
                    permanent = true;
                }
                catch (Exception ex) { _log("Persist of " + key + " threw: " + ex.Message); }
                entry.Attempts++;

                if (ok)
                {
                    _pending.RemoveAt(i);
                    if (entry.Request.AfterWrite != null) { try { entry.Request.AfterWrite(); } catch (Exception) { } }
                    Complete(entry, true);
                    continue;
                }
                if (permanent)
                {
                    _pending.RemoveAt(i);
                    ReportFailure(entry);
                    Complete(entry, false);
                    continue;
                }

                _log("Persist of " + key + " failed (attempt " + entry.Attempts + ").");
                ReportFailure(entry);
                if (!entry.Request.Final && entry.Attempts >= MaxAttempts)
                {
                    _log("Giving up on " + key + " after " + entry.Attempts + " attempts; a newer snapshot will replace it.");
                    _pending.RemoveAt(i);
                    Complete(entry, false);
                    continue;
                }
                entry.NextAttemptAt = now + Backoff(entry.Attempts);
                blockedKeys.Add(key);
                i++;
            }

            DateTime? next = null;
            foreach (Entry e in _pending)
            {
                if (next == null || e.NextAttemptAt < next.Value) { next = e.NextAttemptAt; }
            }
            return next;
        }

        public static TimeSpan Backoff(int attempts)
        {
            double seconds = Math.Pow(2, Math.Max(0, attempts - 1));
            return seconds >= MaxBackoff.TotalSeconds ? MaxBackoff : TimeSpan.FromSeconds(seconds);
        }

        /// <summary>
        /// Worker thread body: pump, wait for work or the next retry, and on shutdown drain and spool.
        /// </summary>
        public void Run()
        {
            while (!_shutdownRequested)
            {
                DateTime? next = Pump();
                if (_shutdownRequested) { break; }

                TimeSpan wait = TimeSpan.FromSeconds(1);
                if (next != null)
                {
                    TimeSpan untilDue = next.Value - _clock.Now;
                    if (untilDue < wait) { wait = untilDue < TimeSpan.Zero ? TimeSpan.Zero : untilDue; }
                }
                WaitForWork(wait);
            }
            Drain(_shutdownTimeout, t => Thread.Sleep(t));
        }

        /// <summary>Asks the worker to flush what it can, spool the rest, and let Run() return.</summary>
        public void Shutdown(TimeSpan timeout)
        {
            _shutdownTimeout = timeout;
            _shutdownRequested = true;
            _channel.Writer.TryComplete();
        }

        /// <summary>
        /// Keeps pumping until nothing is pending or the deadline passes, then spools whatever is left.
        /// Returns the keys that were spooled.
        /// </summary>
        public List<string> Drain(TimeSpan timeout, Action<TimeSpan> wait)
        {
            _channel.Writer.TryComplete();
            DateTime deadline = _clock.Now + timeout;
            while (true)
            {
                DateTime? next = Pump();
                if (_pending.Count == 0) { return new List<string>(); }
                DateTime now = _clock.Now;
                if (now >= deadline) { break; }

                TimeSpan pause = TimeSpan.FromMilliseconds(50);
                if (next != null && next.Value - now > pause) { pause = next.Value - now; }
                if (now + pause > deadline) { pause = deadline - now; }
                wait(pause);
            }

            var spooled = new List<string>();
            foreach (Entry entry in _pending.ToList())
            {
                Spool(entry.Request);
                spooled.Add(entry.Key);
            }
            _pending.Clear();
            _log("SHUTDOWN: " + spooled.Count + " unsaved snapshot(s) could not be written and were spooled: " + string.Join(", ", spooled));
            return spooled;
        }

        private void Spool(PersistRequest request)
        {
            if (_spool == null) { _log("No spool directory configured; snapshot of " + request.Collection + "/" + request.Id + " is lost."); return; }
            try { _spool.Write(request); }
            catch (Exception ex) { _log("Could not spool " + request.Collection + "/" + request.Id + ": " + ex.Message); }
        }

        private void WaitForWork(TimeSpan wait)
        {
            if (wait <= TimeSpan.Zero) { return; }
            using (var cts = new CancellationTokenSource(wait))
            {
                try { _channel.Reader.WaitToReadAsync(cts.Token).AsTask().GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
            }
        }

        private void ReportFailure(Entry entry)
        {
            if (entry.FailureReported) { return; }
            entry.FailureReported = true;
            var callback = entry.Request.OnFailed;
            int attempts = entry.Attempts;
            if (callback != null) { OnGameThread(() => callback(attempts)); }
        }

        private void Complete(Entry entry, bool persisted)
        {
            var callback = entry.Request.OnCompleted;
            if (callback != null) { OnGameThread(() => callback(persisted)); }
        }

        private void OnGameThread(Action action)
        {
            Action invoke = () =>
            {
                try { action(); }
                catch (Exception ex) { _log("Persist outcome callback failed: " + ex.Message); }
            };
            if (_postToGame != null) { _postToGame(invoke); } else { invoke(); }
        }

        private void Add(PersistRequest request)
        {
            var entry = new Entry { Request = request };
            if (!request.Final)
            {
                // Latest snapshot wins: replace queued, non-final requests it makes redundant. A Replace
                // supersedes any earlier request for the entity; a SetFields supersedes an earlier SetFields of
                // the same fields, and never a queued Replace.
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    Entry old = _pending[i];
                    if (old.Key != entry.Key || old.Request.Final) { continue; }
                    bool supersedes = request.Kind == PersistKind.Replace
                        || (old.Request.Kind == PersistKind.SetFields && SameFields(old.Request.Doc, request.Doc));
                    if (!supersedes) { continue; }

                    // Keep retry state, and keep the older caller's completion callbacks.
                    entry.Attempts = Math.Max(entry.Attempts, old.Attempts);
                    entry.NextAttemptAt = old.NextAttemptAt > entry.NextAttemptAt ? old.NextAttemptAt : entry.NextAttemptAt;
                    entry.FailureReported = entry.FailureReported || old.FailureReported;
                    request.OnFailed = Combine(old.Request.OnFailed, request.OnFailed);
                    request.OnCompleted = Combine(old.Request.OnCompleted, request.OnCompleted);
                    request.AfterWrite = Combine(old.Request.AfterWrite, request.AfterWrite);
                    _pending.RemoveAt(i);
                }
            }
            _pending.Add(entry);
        }

        private static Action<T> Combine<T>(Action<T> a, Action<T> b)
        {
            if (a == null) { return b; }
            if (b == null) { return a; }
            return v => { a(v); b(v); };
        }

        private static Action Combine(Action a, Action b)
        {
            if (a == null) { return b; }
            if (b == null) { return a; }
            return () => { a(); b(); };
        }

        private static bool SameFields(BsonDocument a, BsonDocument b)
        {
            if (a == null || b == null) { return false; }
            return a.Names.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(b.Names.OrderBy(n => n, StringComparer.Ordinal));
        }
    }
}
