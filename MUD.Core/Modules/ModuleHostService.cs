using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using MUD.Contracts;

namespace MUD.Core.Modules
{
    /// <summary>
    /// The module service served on modules.sock (owner decision D22). Hello, Subscribe, Ready and Ping are card P4-2. SubmitVerdict, Emit,
    /// SetState, GetState, ScheduleTimer and CancelTimer are card P4-4 and are answered by the ModuleDispatcher; without one (the host
    /// started with no dispatcher) they answer Unimplemented, which is the generated base class's behavior. GetEntity, QueryEntities and
    /// Log (D58 reads, the structured log line) are not part of P4-4 and still answer Unimplemented.
    ///
    /// Every call after Hello names its connection by the connection_id that Hello returned.
    /// </summary>
    public sealed class ModuleHostService : ModuleHost.ModuleHostBase
    {
        private readonly ModuleRegistry _registry;
        private readonly Func<Action, bool> _postToGameThread;
        private readonly ModuleDispatcher _dispatcher;

        /// <param name="postToGameThread">
        /// Queues an action on the game thread's separate, bounded module-call queue (World.PostModuleCall). Returns false when
        /// the queue is full or the game has stopped.
        /// </param>
        /// <param name="dispatcher">The remote behavior dispatch (card P4-4). Null leaves the P4-4 calls unimplemented.</param>
        public ModuleHostService(ModuleRegistry registry, Func<Action, bool> postToGameThread, ModuleDispatcher dispatcher = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _postToGameThread = postToGameThread ?? throw new ArgumentNullException(nameof(postToGameThread));
            _dispatcher = dispatcher;
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

        public override async Task<VerdictReply> SubmitVerdict(VerdictRequest request, ServerCallContext context)
        {
            if (_dispatcher == null) { return await base.SubmitVerdict(request, context); }
            ModuleConnection connection = Require(request.ConnectionId);
            // The verdict is applied on the game thread, in the module-call queue (D16: one batch applies atomically there).
            return await RunOnGameThread(() => _dispatcher.SubmitVerdict(connection, request), context);
        }

        public override async Task<EmitReply> Emit(EmitRequest request, ServerCallContext context)
        {
            if (_dispatcher == null) { return await base.Emit(request, context); }
            ModuleConnection connection = Require(request.ConnectionId);
            return await RunOnGameThread(() => _dispatcher.Emit(connection, request), context);
        }

        public override Task<Ack> SetState(SetStateRequest request, ServerCallContext context)
        {
            if (_dispatcher == null) { return base.SetState(request, context); }
            ModuleConnection connection = Require(request.ConnectionId);
            string missing = Missing(connection, "state");
            if (missing != null) { return Task.FromResult(new Ack { Ok = false, Error = missing }); }
            // Module state is not world state: it is written through the repository path from the handler thread, under its own limits.
            string problem = _dispatcher.SetState(connection, request.Key, request.Doc);
            return Task.FromResult(problem == null ? new Ack { Ok = true } : new Ack { Ok = false, Error = problem });
        }

        public override Task<GetStateReply> GetState(GetStateRequest request, ServerCallContext context)
        {
            if (_dispatcher == null) { return base.GetState(request, context); }
            ModuleConnection connection = Require(request.ConnectionId);
            string missing = Missing(connection, "state");
            if (missing != null) { throw new RpcException(new Status(StatusCode.PermissionDenied, missing)); }
            Google.Protobuf.WellKnownTypes.Struct document;
            bool found;
            try { found = _dispatcher.TryGetState(connection, request.Key, out document); }
            catch (Exception ex) { throw new RpcException(new Status(StatusCode.Unavailable, "The state document could not be read: " + ex.Message)); }
            return Task.FromResult(found ? new GetStateReply { Found = true, Doc = document } : new GetStateReply { Found = false });
        }

        public override Task<Ack> ScheduleTimer(TimerRequest request, ServerCallContext context)
        {
            if (_dispatcher == null) { return base.ScheduleTimer(request, context); }
            ModuleConnection connection = Require(request.ConnectionId);
            string missing = Missing(connection, "timer.schedule");
            if (missing != null) { return Task.FromResult(new Ack { Ok = false, Error = missing }); }
            string problem = _dispatcher.ScheduleTimer(connection, request.TimerId, request.AtUnixMs, request.Payload);
            return Task.FromResult(problem == null ? new Ack { Ok = true } : new Ack { Ok = false, Error = problem });
        }

        public override Task<Ack> CancelTimer(CancelTimerRequest request, ServerCallContext context)
        {
            if (_dispatcher == null) { return base.CancelTimer(request, context); }
            ModuleConnection connection = Require(request.ConnectionId);
            string missing = Missing(connection, "timer.schedule");
            if (missing != null) { return Task.FromResult(new Ack { Ok = false, Error = missing }); }
            _dispatcher.CancelTimer(connection, request.TimerId);
            return Task.FromResult(new Ack { Ok = true });
        }

        // D65: a missing entry means the call is refused with a clear error naming the missing capability.
        private static string Missing(ModuleConnection connection, string capability)
        {
            return connection.HasCapability(capability)
                ? null
                : "Missing capability '" + capability + "': the grants file does not allow it for module '" + connection.ModuleName + "'.";
        }

        // Posts the work to the game thread's module-call queue and waits, at most GameThreadCallTimeout (the same rule as Ready).
        private async Task<T> RunOnGameThread<T>(Func<T> work, ServerCallContext context)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool queued = _postToGameThread(() =>
            {
                try { done.TrySetResult(work()); }
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
            return await done.Task;
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
