using System;
using System.Collections.Generic;
using System.Threading.Channels;
using MUD.Contracts;

namespace MUD.Core.Modules
{
    /// <summary>
    /// One accepted module connection (created by a successful Hello). Owns the bounded outbound queue that feeds the module's
    /// server stream: the game thread never blocks on a module (CORE_API 1), so a delivery that does not fit is refused and the
    /// caller applies the hook's fail policy. One writer drains the queue into the stream (CORE_API 7.1).
    /// </summary>
    public sealed class ModuleConnection
    {
        private readonly object _lock = new object();
        private readonly Channel<CoreEvent> _outbound;
        private ulong _sequence;
        private long _lastPingTicks;

        internal ModuleConnection(string connectionId, string moduleName, string moduleVersion, long tokenSequence,
            Manifest manifest, IReadOnlyList<string> granted, IReadOnlyList<string> rejected, int outboundCapacity)
        {
            ConnectionId = connectionId;
            ModuleName = moduleName;
            ModuleVersion = moduleVersion;
            TokenSequence = tokenSequence;
            Manifest = manifest;
            Granted = granted;
            Rejected = rejected;
            _outbound = Channel.CreateBounded<CoreEvent>(new BoundedChannelOptions(outboundCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            _lastPingTicks = DateTime.UtcNow.Ticks;
        }

        public string ConnectionId { get; private set; }
        public string ModuleName { get; private set; }
        public string ModuleVersion { get; private set; }
        public long TokenSequence { get; private set; }
        public Manifest Manifest { get; private set; }

        /// <summary>Capabilities from the manifest that the grants file allowed (D18, D65).</summary>
        public IReadOnlyList<string> Granted { get; private set; }

        /// <summary>Capabilities from the manifest that the grants file did not allow.</summary>
        public IReadOnlyList<string> Rejected { get; private set; }

        public bool IsSubscribed { get; internal set; }

        /// <summary>True when the grants file allowed this capability for the connection's manifest (D65: exact string, for example "state" or "emit:entity.move").</summary>
        public bool HasCapability(string capability)
        {
            for (int index = 0; index < Granted.Count; index++)
            {
                if (string.Equals(Granted[index], capability, System.StringComparison.Ordinal)) { return true; }
            }
            return false;
        }

        /// <summary>True once the module has called Ready. Changed on the game thread, read from any thread.</summary>
        public bool IsReady { get { return _isReady; } internal set { _isReady = value; } }
        private volatile bool _isReady;

        public bool IsClosed { get { return _closed; } }
        private volatile bool _closed;

        public DateTime LastPingUtc { get { return new DateTime(System.Threading.Interlocked.Read(ref _lastPingTicks), DateTimeKind.Utc); } }

        internal void NotePing() { System.Threading.Interlocked.Exchange(ref _lastPingTicks, DateTime.UtcNow.Ticks); }

        public ChannelReader<CoreEvent> Reader { get { return _outbound.Reader; } }

        /// <summary>Deliveries waiting for the stream.</summary>
        public int PendingDeliveries { get { return _outbound.Reader.Count; } }

        /// <summary>
        /// Queues a delivery without blocking. The sequence number is assigned here, so it is gap-free for the life of the
        /// connection. Returns false (and uses no sequence number) when the queue is full or the connection has closed.
        /// </summary>
        public bool TryEnqueue(CoreEvent delivery)
        {
            if (delivery == null) { throw new ArgumentNullException(nameof(delivery)); }
            lock (_lock)
            {
                if (_closed) { return false; }
                delivery.Seq = _sequence + 1;
                if (!_outbound.Writer.TryWrite(delivery)) { return false; }
                _sequence++;
                return true;
            }
        }

        /// <summary>Ends the stream: the queued deliveries are still sent, then the stream completes.</summary>
        internal void Close()
        {
            lock (_lock)
            {
                _closed = true;
                _outbound.Writer.TryComplete();
            }
        }
    }
}
