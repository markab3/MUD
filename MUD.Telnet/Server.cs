using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace MUD.Telnet
{
    public class Server
    {
        // Thread signal.  
        private static ManualResetEvent _allDone = new ManualResetEvent(false);

        /// <summary>
        /// True for allowing incoming connections;
        /// false otherwise.
        /// </summary>
        private bool _isAcceptingConnections { get; set; }
        /// <summary>
        /// The IP on which to listen.
        /// </summary>
        private IPAddress _ip;

        /// <summary>
        /// Telnet's default port.
        /// </summary>
        private int _port;

        private List<Client> _clients;

        private readonly TelnetLimits _limits;

        private readonly ConnectionTracker _connectionTracker;

        private Timer _idleTimer;

        private readonly object _clientsLock = new object();

        // The listening socket, kept so Stop() can close it.
        private Socket _listener;

        public event EventHandler ServerStopped;

        public event EventHandler<Client> ClientConnected;

        public event EventHandler<Client> ClientDisconnected;

        public Server(IPAddress ipAddress, int port) : this(ipAddress, port, null)
        {
        }

        public Server(IPAddress ipAddress, int port, TelnetLimits limits)
        {
            _ip = ipAddress;
            _port = port;
            _clients = new List<Client>();
            _limits = limits ?? new TelnetLimits();
            _connectionTracker = new ConnectionTracker(_limits.MaxConnectionsPerIp, _limits.MaxTotalConnections);
        }

        public TelnetLimits Limits { get { return _limits; } }

        private static string addressKey(Client client)
        {
            IPAddress address = client.RemoteAddress?.Address;
            if (address == null) { return string.Empty; }
            return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        }

        private void checkIdleClients(object state)
        {
            DateTime now = DateTime.UtcNow;
            foreach (Client currentClient in snapshotClients())
            {
                try { currentClient.CheckIdle(now); }
                catch (Exception e) { Console.WriteLine(e.ToString()); }
            }
        }

        public void Start()
        {
            _isAcceptingConnections = true;

            IPEndPoint localEP = new IPEndPoint(_ip, _port);

            Console.WriteLine($"Local address and port : {localEP.ToString()}");

            Socket listener = new Socket(localEP.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _listener = listener;

            try
            {
                listener.Bind(localEP);
                listener.Listen(10);

                // Sweep for idle connections every few seconds.
                _idleTimer = new Timer(checkIdleClients, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

                while (_isAcceptingConnections)
                {
                    _allDone.Reset();

                    Console.WriteLine("Waiting for a connection...");
                    listener.BeginAccept(new AsyncCallback(acceptCallback), listener);

                    _allDone.WaitOne();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }

            _idleTimer?.Dispose();
            Console.WriteLine("Closing the listener...");
            ServerStopped?.Invoke(this, null);
        }

        /// <summary>Stops accepting new connections; existing clients stay connected.</summary>
        public void StopAccepting()
        {
            _isAcceptingConnections = false;
            _allDone.Set();

            // Closing the listener stops new connections being accepted.
            Socket listener = _listener;
            if (listener != null)
            {
                listener.Close();
            }
        }

        public void Stop()
        {
            StopAccepting();

            // Iterate over a snapshot: Disconnect() removes the client from _clients.
            foreach (Client currentClient in snapshotClients())
            {
                currentClient.Send("The server is shutting down and your connection will now be closed.");
                currentClient.Disconnect(true);
            }
        }

        public void SendMessageToAll(TelnetCommand telnetCommand)
        {
            SendMessageToAll(telnetCommand.ToByteArray());
        }
        
        public void SendMessageToAll(byte[] data)
        {
            foreach (Client currentClient in snapshotClients())
            {
                currentClient.Send(data);
            }
        }

        public void SendMessageToAll(string message)
        {
            foreach (Client currentClient in snapshotClients())
            {
                currentClient.Send(message);
            }
        }

        private List<Client> snapshotClients()
        {
            lock (_clientsLock)
            {
                return new List<Client>(_clients);
            }
        }

        private void acceptCallback(IAsyncResult ar)
        {
            // Release the accept loop first so it always re-arms BeginAccept, even if this callback fails.
            _allDone.Set();

            try
            {
                Socket listener = (Socket)ar.AsyncState;
                Socket handler = listener.EndAccept(ar);

                // Create the client connection object  
                Client client = new Client(handler, _limits, null);

                // Enforce connection caps before the client is announced to the rest of the system.
                string key = addressKey(client);
                if (!_connectionTracker.TryAdd(key))
                {
                    Console.WriteLine("REFUSED (connection limit): " + key);
                    try
                    {
                        handler.Send(System.Text.Encoding.ASCII.GetBytes("Too many connections. Please try again later.\r\n"));
                        handler.Shutdown(SocketShutdown.Both);
                    }
                    catch (Exception) { }
                    finally { handler.Close(); }
                    return;
                }

                client.ClientDisconnected += handleClientDisconnect;
                lock (_clientsLock)
                {
                    _clients.Add(client);
                }
                client.Read();

                //client.Send(new byte[] {(byte) CommandCode.IAC, (byte) CommandCode.DO, (byte) OptionCode.TerminalType});

                ClientConnected?.Invoke(this, client);
            }
            catch (Exception e)
            {
                Console.WriteLine("Failed to accept a client connection: " + e.ToString());
            }
        }

        private void handleClientDisconnect(object sender, Client client)
        {
            bool wasTracked;
            lock (_clientsLock)
            {
                wasTracked = _clients.Remove(client);
            }
            if (wasTracked) { _connectionTracker.Remove(addressKey(client)); }
            ClientDisconnected?.Invoke(this, client);
        }
    }
}