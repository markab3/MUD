using System;
using System.Collections.Generic;
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

        private readonly object _clientsLock = new object();

        // The listening socket, kept so Stop() can close it.
        private Socket _listener;

        public event EventHandler ServerStopped;

        public event EventHandler<Client> ClientConnected;

        public event EventHandler<Client> ClientDisconnected;

        public Server(IPAddress ipAddress, int port)
        {
            _ip = ipAddress;
            _port = port;
            _clients = new List<Client>();
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

            Console.WriteLine("Closing the listener...");
            ServerStopped?.Invoke(this, null);
        }

        public void Stop()
        {
            _isAcceptingConnections = false;
            _allDone.Set();

            // Closing the listener stops new connections being accepted.
            Socket listener = _listener;
            if (listener != null)
            {
                listener.Close();
            }

            // Iterate over a snapshot: Disconnect() removes the client from _clients.
            foreach (Client currentClient in snapshotClients())
            {
                currentClient.Send("The server is shutting down and your connection will now be closed.");
                currentClient.Disconnect();
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
                Client client = new Client(handler);
                client.ClientDisconnected += handleClientDisconnect;
                client.Read();

                lock (_clientsLock)
                {
                    _clients.Add(client);
                }

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
            lock (_clientsLock)
            {
                _clients.Remove(client);
            }
            ClientDisconnected?.Invoke(this, client);
        }
    }
}