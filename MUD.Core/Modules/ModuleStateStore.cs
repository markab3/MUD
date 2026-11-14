using System;
using System.Collections.Generic;
using MongoDB.Bson;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;

namespace MUD.Core.Modules
{
    /// <summary>
    /// The module-state documents (owner decisions D12, D73): key to document, one namespace per module, whole-document replace,
    /// last write wins. Core is the only process that talks to MongoDB, so a module reaches this only through SetState and GetState.
    ///
    /// A write updates an in-memory copy at once (so a read right after a write sees it) and then goes to the database through the
    /// repository path: as a snapshot for the persistence worker when the repository has one (normal operation: retry and spool apply),
    /// otherwise synchronously. Limits protect the database (CORE_API 2.8): a largest document size and a write rate per module
    /// (token bucket). The numbers are ModuleHostOptions settings with flagged defaults. Safe to call from any thread.
    /// </summary>
    public sealed class ModuleStateStore
    {
        public const int MaxKeyLength = 128;

        private readonly IModuleStateRepository _repository;
        private readonly ModuleHostOptions _options;
        private readonly IClock _clock;
        private readonly object _lock = new object();
        private readonly Dictionary<string, BsonDocument> _cache = new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
        private readonly Dictionary<string, TokenBucket> _buckets = new Dictionary<string, TokenBucket>(StringComparer.Ordinal);

        public ModuleStateStore(IModuleStateRepository repository, ModuleHostOptions options, IClock clock = null)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _clock = clock ?? SystemClock.Instance;
        }

        /// <summary>Replaces the document stored under the key. Returns null on success, otherwise the reason it was refused.</summary>
        public string Set(string module, string key, BsonDocument document)
        {
            string problem = CheckKey(key);
            if (problem != null) { return problem; }
            if (document == null) { return "No document was sent."; }
            int size = document.ToBson().Length;
            if (size > _options.StateMaxDocumentBytes)
            {
                return "The state document is " + size + " bytes; the limit is " + _options.StateMaxDocumentBytes + " bytes.";
            }

            string id = ModuleStateDocument.IdFor(module, key);
            BsonDocument stored = document.DeepClone().AsBsonDocument;
            lock (_lock)
            {
                if (!TakeToken(module))
                {
                    return "Too many state writes: the limit is " + _options.StateWritesPerSecond + " per second with a burst of " + _options.StateWriteBurst + ".";
                }
                _cache[id] = stored;
            }

            var whole = new BsonDocument
            {
                { "_id", id },
                { "Module", module },
                { "Key", key },
                { "Document", stored },
                { "UpdatedUtc", DateTime.UtcNow }
            };
            try
            {
                var snapshots = _repository as ISnapshotRepository;
                if (snapshots != null && snapshots.Sink != null)
                {
                    snapshots.Sink.Post(new PersistRequest
                    {
                        Collection = snapshots.CollectionName,
                        Id = id,
                        Kind = PersistKind.Replace,
                        Doc = whole,
                        Reason = "module state"
                    });
                }
                else if (snapshots != null)
                {
                    if (!snapshots.ReplaceDocument(id, whole)) { return "The database did not accept the state document."; }
                }
                else
                {
                    var entity = new ModuleStateDocument { Id = id, Module = module, Key = key, Document = stored, UpdatedUtc = DateTime.UtcNow };
                    if (!_repository.Update(entity)) { return "The database did not accept the state document."; }
                }
            }
            catch (Exception ex)
            {
                return "The state document could not be written: " + ex.Message;
            }
            return null;
        }

        /// <summary>Reads the document under the key; false when there is none. Throws when the database cannot be read.</summary>
        public bool TryGet(string module, string key, out BsonDocument document)
        {
            document = null;
            if (CheckKey(key) != null) { return false; }
            string id = ModuleStateDocument.IdFor(module, key);
            lock (_lock)
            {
                BsonDocument cached;
                if (_cache.TryGetValue(id, out cached))
                {
                    document = cached.DeepClone().AsBsonDocument;
                    return true;
                }
            }

            BsonDocument raw = _repository.GetRawDocument(id);
            if (raw == null) { return false; }
            BsonValue inner;
            if (!raw.TryGetValue("Document", out inner) || !inner.IsBsonDocument) { return false; }
            lock (_lock)
            {
                // A write may have landed while the database was read: the newer in-memory copy wins.
                if (!_cache.ContainsKey(id)) { _cache[id] = inner.AsBsonDocument; }
                document = _cache[id].DeepClone().AsBsonDocument;
            }
            return true;
        }

        public static string CheckKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) { return "A state key is required."; }
            if (key.Length > MaxKeyLength) { return "A state key is at most " + MaxKeyLength + " characters."; }
            foreach (char letter in key)
            {
                if (char.IsControl(letter)) { return "A state key must not contain control characters."; }
            }
            return null;
        }

        // Caller holds _lock. A token bucket per module: StateWritesPerSecond refill, StateWriteBurst capacity.
        private bool TakeToken(string module)
        {
            TokenBucket bucket;
            if (!_buckets.TryGetValue(module, out bucket))
            {
                bucket = new TokenBucket(_options.StateWritesPerSecond, _options.StateWriteBurst);
                _buckets[module] = bucket;
            }
            return bucket.TryTake(_clock);
        }
    }
}
