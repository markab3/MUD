using System;
using System.Collections.Generic;
using System.Linq;

namespace MUD.Telnet
{
    /// <summary>
    /// In-memory throttle for failed logins. After <c>maxFailures</c> failures from one address, that address
    /// is blocked for <c>blockDuration</c>. Stale entries are pruned so memory stays bounded.
    /// Thread safe; time comes from the supplied clock.
    /// </summary>
    public class LoginThrottle
    {
        private class Entry
        {
            public int Failures;
            public DateTime LastFailure;
            public DateTime? BlockedUntil;
        }

        private readonly int _maxFailures;
        private readonly TimeSpan _blockDuration;
        private readonly Func<DateTime> _clock;
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private readonly object _lock = new object();

        public LoginThrottle(int maxFailures, TimeSpan blockDuration, Func<DateTime> clock = null)
        {
            _maxFailures = maxFailures;
            _blockDuration = blockDuration;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public bool IsBlocked(string address)
        {
            lock (_lock)
            {
                return _entries.TryGetValue(address, out Entry entry) && isBlocked(entry, _clock());
            }
        }

        public void RecordFailure(string address)
        {
            lock (_lock)
            {
                DateTime now = _clock();
                prune(now);
                if (!_entries.TryGetValue(address, out Entry entry))
                {
                    entry = new Entry();
                    _entries[address] = entry;
                }
                entry.Failures++;
                entry.LastFailure = now;
                if (entry.Failures >= _maxFailures)
                {
                    entry.BlockedUntil = now + _blockDuration;
                    entry.Failures = 0;
                }
            }
        }

        /// <summary>Clears the failure count for an address (does not lift an active block).</summary>
        public void RecordSuccess(string address)
        {
            lock (_lock)
            {
                if (_entries.TryGetValue(address, out Entry entry) && !isBlocked(entry, _clock()))
                {
                    _entries.Remove(address);
                }
            }
        }

        private static bool isBlocked(Entry entry, DateTime now)
        {
            return entry.BlockedUntil.HasValue && entry.BlockedUntil.Value > now;
        }

        private void prune(DateTime now)
        {
            if (_entries.Count < 256) { return; }
            foreach (string key in _entries.Where(e => now - e.Value.LastFailure > _blockDuration && !isBlocked(e.Value, now)).Select(e => e.Key).ToList())
            {
                _entries.Remove(key);
            }
        }
    }
}
