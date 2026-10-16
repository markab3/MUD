using System.Collections.Generic;

namespace MUD.Telnet
{
    /// <summary>
    /// Counts open connections in total and per remote address, enforcing the configured caps. Thread safe.
    /// </summary>
    public class ConnectionTracker
    {
        private readonly int _maxPerAddress;
        private readonly int _maxTotal;
        private readonly Dictionary<string, int> _perAddress = new Dictionary<string, int>();
        private int _total;
        private readonly object _lock = new object();

        public ConnectionTracker(int maxPerAddress, int maxTotal)
        {
            _maxPerAddress = maxPerAddress;
            _maxTotal = maxTotal;
        }

        public int Total { get { lock (_lock) { return _total; } } }

        public int CountFor(string address)
        {
            lock (_lock) { return _perAddress.TryGetValue(address, out int count) ? count : 0; }
        }

        /// <summary>Reserves a slot; false (and nothing reserved) if a cap would be exceeded.</summary>
        public bool TryAdd(string address)
        {
            lock (_lock)
            {
                _perAddress.TryGetValue(address, out int count);
                if (_total >= _maxTotal || count >= _maxPerAddress) { return false; }
                _perAddress[address] = count + 1;
                _total++;
                return true;
            }
        }

        /// <summary>Releases a slot previously reserved with TryAdd.</summary>
        public void Remove(string address)
        {
            lock (_lock)
            {
                if (!_perAddress.TryGetValue(address, out int count)) { return; }
                if (count <= 1) { _perAddress.Remove(address); } else { _perAddress[address] = count - 1; }
                _total--;
            }
        }
    }
}
