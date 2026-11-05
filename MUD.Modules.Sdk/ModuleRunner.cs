using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using MUD.Contracts;

namespace MUD.Modules.Sdk
{
    /// <summary>How a module process finds core. Core's supervisor sets all three in the child's environment (owner decision D17, CORE_API 7.3).</summary>
    public sealed class ModuleRunnerSettings
    {
        public const string NameVariable = "MUD_MODULE_NAME";
        public const string TokenVariable = "MUD_MODULE_TOKEN";
        public const string SocketVariable = "MUD_CORE_SOCKET";

        public string ModuleName { get; set; }
        public string Token { get; set; }
        public string SocketPath { get; set; }

        /// <summary>Time between Ping calls (D16). Core's supervisor treats a module with no ping for ModuleSupervisorOptions.PingTimeout (30 s) as hung.</summary>
        public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>The module exits when pings keep failing for this long. Flagged: the owner has not set a grace period (D20).</summary>
        public TimeSpan ConnectionLossGrace { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>Where the module's log lines go. Defaults to standard output, which core re-logs with a [module:name] prefix.</summary>
        public Action<string> Log { get; set; }

        public static ModuleRunnerSettings FromEnvironment()
        {
            return new ModuleRunnerSettings
            {
                ModuleName = Environment.GetEnvironmentVariable(NameVariable),
                Token = Environment.GetEnvironmentVariable(TokenVariable),
                SocketPath = Environment.GetEnvironmentVariable(SocketVariable)
            };
        }
    }

    /// <summary>
    /// The module's side of the wire: connection loop, Ping loop, one-at-a-time delivery handling and the verdict replies.
    /// Use ModuleRunner.RunProcessAsync from a module's Main, or `new ModuleRunner(...).RunAsync` from a test (ModuleTestHarness).
    ///
    /// The order is Hello, Subscribe, OnStart, Ready (MODULES 4): the module reconciles before core releases the required-module gate.
    /// Deliveries are handled one at a time in arrival order, so a handler never races another one of the same module (D62).
    /// A handler that throws gets no verdict: core applies the hook's fail policy when the deadline passes (D60), and the error is logged.
    /// </summary>
    public sealed class ModuleRunner
    {
        public const int ExitOk = 0;
        public const int ExitConnectionLost = 1;
        public const int ExitRefused = 2;
        public const int ExitBadSettings = 3;

        private readonly ModuleDefinition _definition;
        private readonly ModuleRunnerSettings _settings;
        private readonly Action<string> _log;
        private readonly TaskCompletionSource<bool> _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public ModuleRunner(ModuleDefinition definition, ModuleRunnerSettings settings)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _log = settings.Log ?? Console.WriteLine;
        }

        /// <summary>Completes when core has accepted this module's Ready call.</summary>
        public Task ReadyTask { get { return _ready.Task; } }

        /// <summary>The context, once Hello has succeeded.</summary>
        public ModuleContext Context { get; private set; }

        /// <summary>Deliveries handled so far (verdict or not).</summary>
        public int HandledCount { get { return Volatile.Read(ref _handled); } }
        private int _handled;

        // ---- the process entry point ----

        /// <summary>
        /// The whole Main of a module: `--describe` prints the description and returns (D67, without connecting to core); otherwise
        /// the settings come from the environment core set and the module runs until core stops it or the connection is lost.
        /// </summary>
        public static async Task<int> RunProcessAsync(ModuleDefinition definition, string[] args)
        {
            if (args != null && args.Contains("--describe"))
            {
                Console.Out.WriteLine(Describe(definition));
                return ExitOk;
            }

            ModuleRunnerSettings settings = ModuleRunnerSettings.FromEnvironment();
            if (string.IsNullOrEmpty(settings.Token) || string.IsNullOrEmpty(settings.SocketPath))
            {
                Console.Error.WriteLine("This module is started by core, which sets " + ModuleRunnerSettings.NameVariable + ", " + ModuleRunnerSettings.TokenVariable
                    + " and " + ModuleRunnerSettings.SocketVariable + ". There is no hand-run mode (D69). Use --describe to see what the module is.");
                return ExitBadSettings;
            }
            if (!string.IsNullOrEmpty(settings.ModuleName) && settings.ModuleName != definition.Name)
            {
                Console.Error.WriteLine("Core started this program as module '" + settings.ModuleName + "' but it is module '" + definition.Name + "'.");
                return ExitBadSettings;
            }
            settings.ModuleName = definition.Name;

            using (var stop = new CancellationTokenSource())
            using (PosixSignalRegistration terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); }))
            using (PosixSignalRegistration interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; stop.Cancel(); }))
            {
                return await new ModuleRunner(definition, settings).RunAsync(stop.Token);
            }
        }

        /// <summary>The JSON that `--describe` prints; the property names are the ones core reads (MUD.Core.Modules.ModuleDescription).</summary>
        public static string Describe(ModuleDefinition definition)
        {
            var description = new Dictionary<string, object>
            {
                { "name", definition.Name },
                { "version", definition.Version },
                { "contract_version", ContractVersion.Current },
                { "behavior_types", definition.Behaviors.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray() },
                { "required", definition.Required }
            };
            return JsonSerializer.Serialize(description);
        }

        // ---- the connection ----

        /// <summary>Runs until cancelled (returns ExitOk), refused by core (ExitRefused) or the connection is lost (ExitConnectionLost).</summary>
        public async Task<int> RunAsync(CancellationToken cancel)
        {
            using (GrpcChannel channel = CreateChannel(_settings.SocketPath))
            {
                var client = new ModuleHost.ModuleHostClient(channel);
                try
                {
                    return await RunConnectedAsync(client, cancel);
                }
                catch (OperationCanceledException)
                {
                    return ExitOk;
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && cancel.IsCancellationRequested)
                {
                    return ExitOk;
                }
                catch (RpcException ex)
                {
                    _log("The connection to core failed: " + ex.Status.Detail);
                    _ready.TrySetException(ex);
                    return ExitConnectionLost;
                }
            }
        }

        private async Task<int> RunConnectedAsync(ModuleHost.ModuleHostClient client, CancellationToken cancel)
        {
            var hello = new HelloRequest
            {
                Module = _definition.Name,
                Token = _settings.Token,
                ContractVersion = ContractVersion.Current,
                ModuleVersion = _definition.Version,
                Manifest = _definition.BuildManifest()
            };
            HelloReply reply = await client.HelloAsync(hello, cancellationToken: cancel);
            if (!reply.Ok)
            {
                _log("Core refused this module: " + reply.Refusal);
                _ready.TrySetException(new ModuleCallException(reply.Refusal));
                return ExitRefused;
            }
            if (reply.Rejected.Count > 0) { _log("Not granted by core: " + string.Join(", ", reply.Rejected)); }

            var context = new ModuleContext(client, _definition.Name, reply.ConnectionId, reply.Granted.ToList(), _log);
            Context = context;

            using (var lost = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            using (AsyncServerStreamingCall<CoreEvent> stream = client.Subscribe(new SubscribeRequest { ConnectionId = reply.ConnectionId }, cancellationToken: lost.Token))
            {
                Task pings = PingLoopAsync(client, reply.ConnectionId, lost);

                if (_definition.OnStart != null) { await _definition.OnStart(context); }
                await ReadyWhenSubscribedAsync(client, reply.ConnectionId, lost.Token);
                _log("Module '" + _definition.Name + "' is ready.");
                _ready.TrySetResult(true);

                try
                {
                    await foreach (CoreEvent delivery in stream.ResponseStream.ReadAllAsync(lost.Token))
                    {
                        await HandleAsync(delivery, context, lost.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }

                bool stoppedOnPurpose = cancel.IsCancellationRequested;
                lost.Cancel();
                try { await pings; } catch (Exception) { }
                if (stoppedOnPurpose) { return ExitOk; }
                _log("The connection to core was lost; the module is exiting.");
                return ExitConnectionLost;
            }
        }

        // The Subscribe call opens on the server a moment after the client starts it; Ready answers "open the stream first" until then.
        private static async Task ReadyWhenSubscribedAsync(ModuleHost.ModuleHostClient client, string connectionId, CancellationToken cancel)
        {
            DateTime giveUpAt = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                Ack ack = await client.ReadyAsync(new ReadyRequest { ConnectionId = connectionId }, cancellationToken: cancel);
                if (ack.Ok) { return; }
                if (DateTime.UtcNow > giveUpAt) { throw new ModuleCallException("Core did not accept Ready: " + ack.Error); }
                await Task.Delay(50, cancel);
            }
        }

        private async Task PingLoopAsync(ModuleHost.ModuleHostClient client, string connectionId, CancellationTokenSource lost)
        {
            ulong n = 0;
            DateTime lastGood = DateTime.UtcNow;
            while (!lost.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_settings.PingInterval, lost.Token);
                    await client.PingAsync(new PingRequest { ConnectionId = connectionId, N = ++n }, deadline: DateTime.UtcNow + _settings.PingInterval + TimeSpan.FromSeconds(5), cancellationToken: lost.Token);
                    lastGood = DateTime.UtcNow;
                }
                catch (OperationCanceledException) { return; }
                catch (RpcException)
                {
                    if (lost.IsCancellationRequested) { return; }
                    if (DateTime.UtcNow - lastGood > _settings.ConnectionLossGrace)
                    {
                        _log("Core has not answered Ping for " + _settings.ConnectionLossGrace.TotalSeconds + " seconds.");
                        lost.Cancel();
                        return;
                    }
                }
            }
        }

        // ---- one delivery ----

        private async Task HandleAsync(CoreEvent delivery, ModuleContext context, CancellationToken cancel)
        {
            try
            {
                if (delivery.Name == "timer.fired" && delivery.Behavior == null)
                {
                    if (_definition.OnTimer != null)
                    {
                        await _definition.OnTimer(new TimerFired(Payloads.GetText(delivery.Payload, "timer_id"), Payloads.GetMap(delivery.Payload, "payload")), context);
                    }
                    return;
                }

                BehaviorRegistration registration;
                string type = delivery.Behavior == null ? string.Empty : delivery.Behavior.Type;
                if (!_definition.Behaviors.TryGetValue(type, out registration))
                {
                    _log("A delivery named the behavior type '" + type + "', which this module does not provide: ignored.");
                    return;
                }

                BehaviorResult result = await registration.Handler(new BehaviorCall(delivery), context) ?? BehaviorResult.Allow();
                if (delivery.Phase == Phase.Before)
                {
                    var verdict = new VerdictRequest
                    {
                        ConnectionId = context.ConnectionId,
                        EventId = delivery.EventId,
                        Decision = result.Decision,
                        Message = result.Message,
                        Handled = result.Handled
                    };
                    verdict.Emitted.AddRange(result.Emitted);
                    VerdictReply reply = await context.Client.SubmitVerdictAsync(verdict, cancellationToken: cancel);
                    if (!reply.Ok) { _log("Core did not count the verdict for '" + delivery.EventId + "': " + reply.Error); }
                    foreach (EmitRefusal refusal in reply.Refusals) { _log("core refused emitted event " + refusal.Index + " '" + refusal.EventName + "': " + refusal.Reason); }
                }
                else if (result.Emitted.Count > 0)
                {
                    // An After delivery has no verdict to carry the events, so they go out through Emit, caused by this delivery.
                    await context.EmitAsync(result.Emitted, delivery.EventId, cancel);
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log("The handler for '" + delivery.Name + "' (" + delivery.EventId + ") failed and sent no verdict: " + ex.Message);
            }
            finally
            {
                Interlocked.Increment(ref _handled);
            }
        }

        // ---- the socket ----

        /// <summary>A gRPC channel over a Unix domain socket (spike: specs/V2_SPIKE_RESULTS.md).</summary>
        public static GrpcChannel CreateChannel(string socketPath)
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, cancellation) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellation);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
            return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
        }
    }
}
