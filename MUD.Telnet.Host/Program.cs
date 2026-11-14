using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using MUD.CoreApi.Client;

namespace MUD.Telnet.Host
{
    /// <summary>
    /// The telnet front end as its own process (phase 7, card P7-2). It listens for telnet connections and talks to core only over
    /// frontends.sock through the Session service. It has no database access. Started by systemd (planar-realms-telnet.service), not by core.
    /// </summary>
    public static class Program
    {
        private const string WelcomeMessage =
@"
                          Welcome to Planar Realms 0.2

--------------------------------------------------------------------------------
    Use 'connect' to log in to an existing player. You will be prompted for a
    name and password; the password is not echoed.
    Use 'create' to create a new player (name, password and confirmation).
    Scripted clients may use 'connect <username> <password>' and
    'create <username> <password>', but then the password is visible.
    Use 'who' to see who is currently online.
    Use 'quit' to disconnect.
--------------------------------------------------------------------------------";

        public static int Main(string[] args)
        {
            TelnetHostOptions options = TelnetHostOptions.FromEnvironment();
            var shutdownRequested = new ManualResetEventSlim(false);

            // The token file is read at every connection attempt, so a core that creates it after this process started is found.
            Func<ICoreConnection> factory = () =>
            {
                string token = ReadToken(options.TokenFilePath);
                var api = new GrpcCoreApi(GrpcCoreApi.OpenChannel(options.SocketPath), TelnetFrontEnd.FrontEndName, token);
                return new GrpcCoreConnection(api);
            };

            using (var core = new ReconnectingCoreApi(factory))
            {
                var server = new Server(options.BindAddress, options.Port, options.Limits);
                new TelnetFrontEnd(core, WelcomeMessage).Attach(server);

                PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; shutdownRequested.Set(); });
                PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; shutdownRequested.Set(); });

                core.Start();
                var serverThread = new Thread(server.Start) { IsBackground = true };
                serverThread.Start();
                Console.WriteLine("TELNET HOST STARTED: " + DateTime.Now + " on " + options.BindAddress + ":" + options.Port + ", core socket " + options.SocketPath);

                shutdownRequested.Wait();
                Console.WriteLine("SHUTDOWN requested");
                server.StopAccepting();
                server.Stop();
            }
            return 0;
        }

        private static string ReadToken(string path)
        {
            if (!File.Exists(path)) { throw new FileNotFoundException("The front-end token file does not exist: " + path); }
            string token = File.ReadAllText(path).Trim();
            if (token.Length == 0) { throw new InvalidOperationException("The front-end token file is empty: " + path); }
            return token;
        }
    }
}
