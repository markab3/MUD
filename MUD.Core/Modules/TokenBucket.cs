using System;
using MUD.Data;

namespace MUD.Core.Modules
{
    /// <summary>
    /// A token bucket for the per-module limits (CORE_API 2.8): it holds at most burst tokens and refills at ratePerSecond.
    /// Each permitted action takes one token. Time comes from the game clock, so tests move it by hand. Not thread safe on its own:
    /// callers lock around it.
    /// </summary>
    public sealed class TokenBucket
    {
        private readonly double _ratePerSecond;
        private readonly double _burst;
        private double _tokens;
        private DateTime _lastRefill;
        private bool _started;

        public TokenBucket(double ratePerSecond, double burst)
        {
            _ratePerSecond = ratePerSecond;
            _burst = burst;
            _tokens = burst;
        }

        public bool TryTake(IClock clock)
        {
            DateTime now = clock.Now;
            if (!_started)
            {
                _started = true;
                _lastRefill = now;
            }
            double elapsed = (now - _lastRefill).TotalSeconds;
            if (elapsed > 0)
            {
                _tokens = Math.Min(_burst, _tokens + elapsed * _ratePerSecond);
                _lastRefill = now;
            }
            if (_tokens < 1) { return false; }
            _tokens -= 1;
            return true;
        }
    }
}
