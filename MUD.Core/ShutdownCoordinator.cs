using System;
using System.Threading;
using System.Threading.Tasks;

namespace MUD.Core
{
    /// <summary>
    /// Runs the graceful shutdown sequence exactly once, however many triggers fire (SIGTERM, Ctrl+C, 'q',
    /// ProcessExit): stop accepting connections, tell clients, save every online player and drain the
    /// persistence worker, then close what is left. The save step is bounded; if it overruns the timeout
    /// the sequence gives up, logs what was not saved, and still runs the final step.
    /// </summary>
    public class ShutdownCoordinator
    {
        private readonly Action _stopAccepting;
        private readonly Action _notifyClients;
        private readonly Action _saveAndDrain;
        private readonly Action _closeConnections;
        private readonly Func<string> _describeUnsaved;
        private readonly Action<string> _log;
        private readonly object _lock = new object();
        private readonly ManualResetEventSlim _done = new ManualResetEventSlim(false);
        private bool _started;

        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

        public TimeSpan Timeout { get; set; } = DefaultTimeout;

        /// <summary>True when the save step did not finish within the timeout.</summary>
        public bool TimedOut { get; private set; }

        public ShutdownCoordinator(Action stopAccepting, Action notifyClients, Action saveAndDrain,
            Action closeConnections, Func<string> describeUnsaved, Action<string> log)
        {
            _stopAccepting = stopAccepting;
            _notifyClients = notifyClients;
            _saveAndDrain = saveAndDrain;
            _closeConnections = closeConnections;
            _describeUnsaved = describeUnsaved;
            _log = log ?? Console.WriteLine;
        }

        /// <summary>
        /// Performs the shutdown. The first caller runs it; later callers wait (bounded by the timeout plus a
        /// margin) for it to finish and return. Never throws.
        /// </summary>
        public void Run()
        {
            bool first;
            lock (_lock)
            {
                first = !_started;
                _started = true;
            }
            if (!first)
            {
                _done.Wait(Timeout + TimeSpan.FromSeconds(5));
                return;
            }

            try
            {
                _log("SHUTDOWN: started.");
                Step("stop accepting connections", _stopAccepting);
                Step("notify clients", _notifyClients);

                Task save = Task.Run(() => Step("save players and drain persistence", _saveAndDrain));
                if (!save.Wait(Timeout))
                {
                    TimedOut = true;
                    string unsaved = null;
                    try { unsaved = _describeUnsaved != null ? _describeUnsaved() : null; }
                    catch (Exception ex) { unsaved = "(could not determine: " + ex.Message + ")"; }
                    _log("SHUTDOWN: timed out after " + Timeout.TotalSeconds + "s; exiting anyway. Not saved: "
                        + (string.IsNullOrEmpty(unsaved) ? "unknown" : unsaved));
                }

                Step("close connections", _closeConnections);
                _log("SHUTDOWN: finished" + (TimedOut ? " (timed out)." : "."));
            }
            finally
            {
                _done.Set();
            }
        }

        private void Step(string name, Action action)
        {
            if (action == null) { return; }
            try { action(); }
            catch (Exception ex) { _log("SHUTDOWN: step '" + name + "' failed: " + ex.Message); }
        }
    }
}
