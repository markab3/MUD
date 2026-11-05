using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using MUD.Contracts;

namespace MUD.Core.Modules
{
    /// <summary>
    /// The module service served on modules.sock (owner decision D22). This card implements Hello, Subscribe, Ready and Ping.
    /// The other calls of the contract (verdicts, emit, state, timers, reads, log) are card P4-4: until then they answer
    /// Unimplemented, which is the generated base class's behavior.
    ///
    /// Every call after Hello names its connection by the connection_id that Hello returned.
    /// </summary>
    public sealed class ModuleHostService : ModuleHost.ModuleHostBase
    {
        private readonly ModuleRegistry _registry;
        private readonly Func<Action, bool> _postToGameThread;

        /// <param name="postToGameThread">
        /// Queues an action on the game thread's separate, bounded module-call queue (World.PostModuleCall). Returns false when
        /// the queue is full or the game has stopped.
        /// </param>
        public ModuleHostService(ModuleRegistry registry, Func<Action, bool> postToGameThread)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _postToGameThread = postToGameThread ?? throw new ArgumentNullException(nameof(postToGameThread));
        }

        public override Task<HelloReply> Hello(HelloRequest request, ServerCallContext context)
        {
            return Task.FromResult(_registry.Hello(request));
        }

        public override async Task Subscribe(SubscribeRequest request, IServerStreamWriter<CoreEvent> responseStream, ServerCallContext context)
        {
            ModuleConnection connection = Require(request.ConnectionId);
            lock (connection)
            {
                if (connection.IsSubscribed)
                {
                    throw new RpcException(new Status(StatusCode.AlreadyExists, "This connection already has a stream."));
                }
                connection.IsSubscribed = true;
            }

            string reason = "stream ended";
            try
            {
                // One writer drains the bounded queue into the stream. Completes when the connection is closed.
                await foreach (CoreEvent delivery in connection.Reader.ReadAllAsync(context.CancellationToken))
                {
                    await responseStream.WriteAsync(delivery);
                }
                reason = "core closed the stream";
            }
            catch (OperationCanceledException)
            {
                reason = "module closed the stream";
            }
            finally
            {
                // The event stream is open AND Ping answers is the liveness rule (MODULES 3): a lost stream ends the connection.
                _registry.Disconnect(connection, reason);
            }
        }

        public override async Task<Ack> Ready(ReadyRequest request, ServerCallContext context)
        {
            ModuleConnection connection = Require(request.ConnectionId);
            if (!connection.IsSubscribed)
            {
                return new Ack { Ok = false, Error = "Open the Subscribe stream before calling Ready." };
            }

            // The change of state runs on the game thread, through the module calls' own bounded queue (CORE_API 1).
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool queued = _postToGameThread(() =>
            {
                try { _registry.MarkReady(connection); done.TrySetResult(true); }
                catch (Exception ex) { done.TrySetException(ex); }
            });
            if (!queued)
            {
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "The core's queue for module calls is full or the game is stopping. Try again."));
            }

            Task finished = await Task.WhenAny(done.Task, Task.Delay(_registry.Options.GameThreadCallTimeout, context.CancellationToken));
            if (finished != done.Task)
            {
                throw new RpcException(new Status(StatusCode.DeadlineExceeded, "The game thread did not run the call in time."));
            }
            await done.Task;
            return new Ack { Ok = true };
        }

        public override Task<PingReply> Ping(PingRequest request, ServerCallContext context)
        {
            // Answered on the handler thread, not the game thread: a slow game loop must not look like a hung module (D16).
            ModuleConnection connection = Require(request.ConnectionId);
            connection.NotePing();
            return Task.FromResult(new PingReply { N = request.N });
        }

        private ModuleConnection Require(string connectionId)
        {
            ModuleConnection connection = _registry.Find(connectionId);
            if (connection == null)
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Unknown connection. Call Hello first."));
            }
            return connection;
        }
    }
}
