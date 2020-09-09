using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MUD.Telnet
{
    public class Server
    {

        /// <summary>
        /// End of line constant.
        /// </summary>
        public const string END_LINE = "\r\n";

        public const string CURSOR = "> ";

        /// <summary>
        /// Server's main socket.
        /// </summary>
        private Socket _serverSocket;

        /// <summary>
        /// The IP on which to listen.
        /// </summary>
        private IPAddress _ip;

        /// <summary>
        /// Telnet's default port.
        /// </summary>
        private int _port;

        /// <summary>
        /// True for allowing incoming connections;
        /// false otherwise.
        /// </summary>
        private bool _acceptIncomingConnections { get; set; }

        /// <summary>
        /// Contains all connected clients indexed
        /// by their socket.
        /// </summary>
        private List<Client> _clients;

        public delegate void ConnectionEventHandler(Client c);


        public delegate void ConnectionBlockedEventHandler(IPEndPoint endPoint);

        /// <summary>
        /// Occurs when a client is connected.
        /// </summary>
        public event ConnectionEventHandler ClientConnected;

        /// <summary>
        /// Occurs when a client is disconnected.
        /// </summary>
        public event ConnectionEventHandler ClientDisconnected;

        /// <summary>
        /// Occurs when an incoming connection is blocked.
        /// </summary>
        public event ConnectionBlockedEventHandler ConnectionBlocked;

        /// <summary>
        /// Initializes a new instance of the <see cref="Server"/> class.
        /// </summary>
        /// <param name="ip">The IP on which to listen to.</param>
        /// <param name="dataSize">Data size for received data.</param>
        public Server(IPAddress ip, int port = 23)
        {
            _ip = ip;
            _port = port;
            _clients = new List<Client>();
            _acceptIncomingConnections = true;
            _serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }

        /// <summary>
        /// Starts the server.
        /// </summary>
        public void start()
        {
            _serverSocket.Bind(new IPEndPoint(_ip, _port));
            _serverSocket.Listen(0);
            _serverSocket.BeginAccept(new AsyncCallback(handleIncomingConnection), _serverSocket);
        }

        /// <summary>
        /// Stops the server.
        /// </summary>
        public void stop()
        {
            _serverSocket.Close();
        }

        /// <summary>
        /// Clears the screen for the specified
        /// client.
        /// </summary>
        /// <param name="c">The client on which
        /// to clear the screen.</param>
        public void clearClientScreen(Client c)
        {
            c.SendMessageToClient("\u001B[1J\u001B[H");
        }

        /// <summary>
        /// Sends a message to all connected clients.
        /// </summary>
        /// <param name="message">The message.</param>
        public void sendMessageToAll(string message)
        {
            foreach (Client c in _clients)
            {
                try
                {
                    c.SendMessageToClient(END_LINE + message + END_LINE + CURSOR);
                }
                catch
                {
                    _clients.Remove(c);
                }
            }
        }

        /// <summary>
        /// Kicks the specified client from the server.
        /// </summary>
        /// <param name="client">The client.</param>
        public void kickClient(Client client)
        {
            client.CloseSocket();
            _clients.Remove(client);
            ClientDisconnected(client);
        }

        /// <summary>
        /// Handles an incoming connection.
        /// If incoming connections are allowed,
        /// the client is added to the clients list
        /// and triggers the client connected event.
        /// Else, the connection blocked event is
        /// triggered.
        /// </summary>
        private void handleIncomingConnection(IAsyncResult result)
        {
            try
            {
                Socket oldSocket = (Socket)result.AsyncState;

                if (_acceptIncomingConnections)
                {
                    Socket newSocket = oldSocket.EndAccept(result);

                    Client client = new Client(newSocket);
                    _clients.Add(client);

                    client.SendMessageToClient(
                        new byte[] {
                            0xff, 0xfd, 0x01,   // Do Echo
                            0xff, 0xfd, 0x21,   // Do Remote Flow Control
                            0xff, 0xfb, 0x01,   // Will Echo
                            0xff, 0xfb, 0x03    // Will Supress Go Ahead
                        }
                    );

                    client.CloseConnection += kickClient;
                    ClientConnected(client);

                    _serverSocket.BeginAccept(new AsyncCallback(handleIncomingConnection), _serverSocket);
                }

                else
                {
                    ConnectionBlocked((IPEndPoint)oldSocket.RemoteEndPoint);
                }
            }

            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
    }
}