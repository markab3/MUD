using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MUD.CoreApi;

namespace MUD.Telnet.Host
{
    /// <summary>
    /// The <see cref="ICoreApi"/> the telnet host gives to the telnet front end. It keeps one connection to core and, when that connection is
    /// lost (core stopped or restarted), connects again with a growing wait between attempts (card P7-2, decision D29).
    ///
    /// - While core is lost, the sessions that were open have already been ended by the connection with
    ///   <see cref="SessionEndReason.WorldStopping"/>, which makes the telnet sink write "world is restarting" and close (D29).
    /// - While core is not connected, a new session is refused the same way: its sink is told the session ended with WorldStopping, so a player
    ///   who connects during the restart sees the same message. The other calls fail soft (TryAgainLater, false, an empty list).
    /// - The wait after a failed attempt follows <c>backoff</c> (default 1, 2, 5, 10, 30 seconds, the last repeating) and starts over after a
    ///   successful connect. These numbers are an implementer choice flagged in the PR; no owner decision sets them.
    /// - Nothing here blocks the caller: the calls use the current connection or fail soft at once.
    /// </summary>
    public sealed class ReconnectingCoreApi : ICoreApi, IDisposable
    {
        public static readonly TimeSpan[] DefaultBackoff =
        {
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)
        };

        private readonly Func<ICoreConnection> _factory;
        private readonly TimeSpan[] _backoff;
        private readonly Action<string> _log;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private volatile ICoreConnection _current;
        private Task _loop;

        public ReconnectingCoreApi(Func<ICoreConnection> factory, Action<string> log = null, IReadOnlyList<TimeSpan> backoff = null)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _log = log ?? Console.WriteLine;
            _backoff = backoff != null && backoff.Count > 0 ? new List<TimeSpan>(backoff).ToArray() : DefaultBackoff;
        }

        /// <summary>True while a connection to core is up.</summary>
        public bool IsConnected { get { return _current != null; } }

        /// <summary>Raised when a connection to core is established (the first one and every reconnect). Raised on the connect loop's thread.</summary>
        public event Action Connected;

        /// <summary>Raised when the connection to core is lost. Raised on a pool thread.</summary>
        public event Action Disconnected;

        /// <summary>Starts the connect loop. Returns at once; the loop runs until <see cref="Dispose"/>.</summary>
        public void Start()
        {
            if (_loop != null) { throw new InvalidOperationException("Already started."); }
            _loop = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            int failures = 0;
            while (!_stop.IsCancellationRequested)
            {
                var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                ICoreConnection connection = null;
                string failure;
                try
                {
                    connection = _factory();
                    connection.ConnectionLost += reason => lost.TrySetResult(reason);
                    failure = connection.Connect();
                }
                catch (Exception ex)
                {
                    failure = ex.GetType().Name + (string.IsNullOrEmpty(ex.Message) ? string.Empty : ": " + ex.Message);
                }

                if (failure == null)
                {
                    failures = 0;
                    _current = connection;
                    _log("Connected to core.");
                    RaiseSafely(Connected);
                    string reason;
                    try { reason = await lost.Task.WaitAsync(_stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    _current = null;
                    _log("Lost core (" + reason + "); will reconnect.");
                    RaiseSafely(Disconnected);
                }
                else
                {
                    _log("Could not connect to core (" + failure + ").");
                }

                try { if (connection != null) { connection.Dispose(); } } catch (Exception) { }

                TimeSpan wait = _backoff[Math.Min(failures, _backoff.Length - 1)];
                failures++;
                _log("Next attempt to reach core in " + wait.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s.");
                try { await Task.Delay(wait, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private static void RaiseSafely(Action handler)
        {
            if (handler == null) { return; }
            try { handler(); } catch (Exception) { }
        }

        public SessionId OpenSession(string frontEnd, string remoteKey, IFrontEndSink sink)
        {
            ICoreConnection connection = _current;
            if (connection != null)
            {
                SessionId id = connection.OpenSession(frontEnd, remoteKey, sink);
                if (!id.IsEmpty) { return id; }
            }
            // Core is not there (or lost while opening): tell this player the world is restarting, as for a session that was open (D29).
            sink.SessionEnded(default(SessionId), SessionEndReason.WorldStopping);
            return default(SessionId);
        }

        public void SubmitLine(SessionId id, string rawLine) { _current?.SubmitLine(id, rawLine); }

        public void CloseSession(SessionId id, SessionEndReason reason) { _current?.CloseSession(id, reason); }

        public LoginResult Authenticate(SessionId id, string name, string password)
        {
            ICoreConnection connection = _current;
            return connection == null ? LoginResult.TryAgainLater : connection.Authenticate(id, name, password);
        }

        public LoginResult CreatePlayer(SessionId id, string name, string password)
        {
            ICoreConnection connection = _current;
            return connection == null ? LoginResult.TryAgainLater : connection.CreatePlayer(id, name, password);
        }

        public bool IsThrottled(string remoteKey)
        {
            ICoreConnection connection = _current;
            return connection != null && connection.IsThrottled(remoteKey);
        }

        public void RecordLoginOutcome(string remoteKey, bool success) { _current?.RecordLoginOutcome(remoteKey, success); }

        public IReadOnlyList<string> GetOnlineNames()
        {
            ICoreConnection connection = _current;
            return connection == null ? new List<string>() : connection.GetOnlineNames();
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { if (_loop != null) { _loop.Wait(TimeSpan.FromSeconds(2)); } } catch (Exception) { }
            ICoreConnection connection = _current;
            _current = null;
            try { if (connection != null) { connection.Dispose(); } } catch (Exception) { }
            _stop.Dispose();
        }
    }
}
