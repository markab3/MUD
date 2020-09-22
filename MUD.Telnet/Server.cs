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
        }

        public void Stop()
        {
            _isAcceptingConnections = false;
            _allDone.Set();

            foreach (Client currentClient in _clients)
            {
                currentClient.Send("The server is shutting down and your connection will now be closeed.");
                currentClient.Disconnect();
            }
        }

        public void SendMessageToAll(string message)
        {
            foreach (Client currentClient in _clients)
            {
                currentClient.Send(message);
            }
        }

        private void acceptCallback(IAsyncResult ar)
        {
            _allDone.Set();

            Socket listener = (Socket)ar.AsyncState;
            Socket handler = listener.EndAccept(ar);

            // Create the client connection object  
            Client client = new Client(handler);
            client.ClientDisconnected += handleClientDisconnect;
            client.Read();

            _clients.Add(client);

            ClientConnected?.Invoke(this, client);
        }

        private void handleClientDisconnect(object sender, Client client)
        {
            _clients.Remove(client);
            ClientDisconnected?.Invoke(this, client);
        }
    }
}