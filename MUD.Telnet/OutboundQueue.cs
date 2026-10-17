using System;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MUD.Telnet
{
    public enum EnqueueResult
    {
        Queued,
        /// <summary>The queue was full; the item was discarded.</summary>
        Dropped,
        /// <summary>The queue was completed (connection closing); the item was discarded.</summary>
        Closed
    }

    /// <summary>
    /// The outbound half of a connection (specs/GAME_LOOP.md section 7): any thread may Enqueue (never blocks),
    /// a single writer loop performs the writes one at a time, in queue order. A client that stalls until the
    /// queue is full twice in a row is reported through onSlowClient so it can be dropped.
    /// </summary>
    public class OutboundQueue
    {
        public const int DefaultCapacity = 256;

        private readonly Channel<byte[]> _channel;
        private readonly Func<byte[], Task> _write;
        private readonly Action<Exception> _onWriteFailed;
        private readonly Action _onSlowClient;
        private readonly TaskCompletionSource<bool> _done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        private int _consecutiveDrops;

        public OutboundQueue(Func<byte[], Task> write, Action<Exception> onWriteFailed, Action onSlowClient, int capacity = DefaultCapacity)
        {
            _write = write;
            _onWriteFailed = onWriteFailed;
            _onSlowClient = onSlowClient;
            _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait // TryWrite returns false when full; the item is then ours to drop
            });
        }

        /// <summary>Completes when the writer has written everything queued before Complete(), or has failed.</summary>
        public Task Completion { get { return _done.Task; } }

        public EnqueueResult Enqueue(byte[] data)
        {
            if (data == null || data.Length == 0) { return EnqueueResult.Queued; }

            if (!_channel.Writer.TryWrite(data))
            {
                if (_channel.Reader.Completion.IsCompleted) { return EnqueueResult.Closed; }
                if (System.Threading.Interlocked.Increment(ref _consecutiveDrops) >= 2) { _onSlowClient?.Invoke(); }
                return EnqueueResult.Dropped;
            }

            System.Threading.Interlocked.Exchange(ref _consecutiveDrops, 0);
            if (System.Threading.Interlocked.CompareExchange(ref _started, 1, 0) == 0)
            {
                Task.Run(WriterLoop);
            }
            return EnqueueResult.Queued;
        }

        /// <summary>Stops accepting items. The writer drains what is queued, then Completion fires.</summary>
        public void Complete()
        {
            _channel.Writer.TryComplete();
            if (System.Threading.Interlocked.CompareExchange(ref _started, 1, 0) == 0)
            {
                _done.TrySetResult(true); // nothing was ever queued, so there is no writer to wait for
            }
        }

        private async Task WriterLoop()
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    byte[] data;
                    while (_channel.Reader.TryRead(out data))
                    {
                        await _write(data).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _channel.Writer.TryComplete();
                try { _onWriteFailed?.Invoke(ex); } catch (Exception) { }
            }
            finally
            {
                _done.TrySetResult(true);
            }
        }
    }
}
