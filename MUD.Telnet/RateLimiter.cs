using System;
using System.Collections.Generic;

namespace MUD.Telnet
{
    /// <summary>
    /// Sliding-window limiter: at most <c>limit</c> events in any <c>window</c>. The caller supplies the time
    /// so it can be driven by a fake clock.
    /// </summary>
    public class RateLimiter
    {
        private readonly int _limit;
        private readonly TimeSpan _window;
        private readonly Queue<DateTime> _events = new Queue<DateTime>();

        public RateLimiter(int limit, TimeSpan window)
        {
            _limit = limit;
            _window = window;
        }

        /// <summary>Records an event at <paramref name="now"/>; false if it exceeds the limit.</summary>
        public bool TryAcquire(DateTime now)
        {
            while (_events.Count > 0 && now - _events.Peek() >= _window) { _events.Dequeue(); }
            if (_events.Count >= _limit) { return false; }
            _events.Enqueue(now);
            return true;
        }
    }
}
