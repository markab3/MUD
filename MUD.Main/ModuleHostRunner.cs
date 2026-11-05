using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MUD.Contracts;
using MUD.Core.Modules;

namespace MUD.Main
{
    /// <summary>
    /// Kestrel on two Unix domain sockets inside the core process (owner decision D22, spike findings in specs/V2_SPIKE_RESULTS.md):
    /// modules.sock serves the module service; frontends.sock is a stub in this card (the front-end service is a later card, D66)
    /// and serves no gRPC service yet. A call for a service that does not belong to an endpoint is answered 404.
    /// Started after the run directory lock is held; stopping it ends every module stream first.
    /// </summary>
    public sealed class ModuleHostRunner : IDisposable
    {
        private const string EndpointItem = "mud.endpoint";
        private const string ModulesEndpoint = "modules";
        private const string FrontEndsEndpoint = "frontends";

        private readonly RunDirectory _runDirectory;
        private readonly ModuleRegistry _registry;
        private readonly Func<Action, bool> _postToGameThread;
        private readonly Core.Modules.ModuleDispatcher _dispatcher;
        private readonly Action<string> _log;
        private WebApplication _application;

        public ModuleHostRunner(RunDirectory runDirectory, ModuleRegistry registry, Func<Action, bool> postToGameThread, Action<string> log = null, Core.Modules.ModuleDispatcher dispatcher = null)
        {
            _dispatcher = dispatcher;
            _runDirectory = runDirectory ?? throw new ArgumentNullException(nameof(runDirectory));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _postToGameThread = postToGameThread ?? throw new ArgumentNullException(nameof(postToGameThread));
            _log = log ?? Console.WriteLine;
        }

        public bool IsRunning { get { return _application != null; } }

        public void Start()
        {
            if (_application != null) { throw new InvalidOperationException("The module host is already running."); }

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
            builder.WebHost.ConfigureKestrel(options =>
            {
                // The socket file mode depends on the umask at bind time (spike Q1), so core sets 0600 after the bind and
                // treats a failure as fatal. The 0700 run directory is the control that holds before that.
                options.ListenUnixSocket(_runDirectory.ModulesSocketPath, listen =>
                {
                    listen.Protocols = HttpProtocols.Http2;
                    listen.Use(next => context => { context.Items[EndpointItem] = ModulesEndpoint; return next(context); });
                });
                options.ListenUnixSocket(_runDirectory.FrontEndsSocketPath, listen =>
                {
                    listen.Protocols = HttpProtocols.Http2;
                    listen.Use(next => context => { context.Items[EndpointItem] = FrontEndsEndpoint; return next(context); });
                });
            });
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(_registry);
            builder.Services.AddSingleton<ModuleHostService>(_ => new ModuleHostService(_registry, _postToGameThread, _dispatcher));
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = _registry.Options.StopTimeout);

            WebApplication application = builder.Build();
            application.Use(async (context, next) =>
            {
                // Module services belong on modules.sock only (D22). Front-end services will be mapped for frontends.sock by P4's later cards.
                var items = context.Features.Get<IConnectionItemsFeature>();
                object endpoint = null;
                if (items != null) { items.Items.TryGetValue(EndpointItem, out endpoint); }
                bool isModuleCall = context.Request.Path.StartsWithSegments("/mud.ModuleHost");
                if (isModuleCall && !ModulesEndpoint.Equals(endpoint))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                await next();
            });
            application.MapGrpcService<ModuleHostService>();

            application.StartAsync().GetAwaiter().GetResult();
            try
            {
                RunDirectory.RestrictSocket(_runDirectory.ModulesSocketPath);
                RunDirectory.RestrictSocket(_runDirectory.FrontEndsSocketPath);
            }
            catch
            {
                application.StopAsync().GetAwaiter().GetResult();
                application.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw;
            }
            _application = application;
            _log("Module host listening on " + _runDirectory.ModulesSocketPath + " (modules) and " + _runDirectory.FrontEndsSocketPath + " (front ends, stub).");
        }

        /// <summary>Ends every module stream, then stops Kestrel (the host would otherwise wait for open streams, spike Q6).</summary>
        public void Stop()
        {
            WebApplication application = Interlocked.Exchange(ref _application, null);
            if (application == null) { return; }
            _registry.DisconnectAll("core is stopping");
            try
            {
                application.StopAsync().GetAwaiter().GetResult();
                application.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _log("Module host did not stop cleanly: " + ex.Message);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
