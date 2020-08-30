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
        /// Telnet's default port.
        /// </summary>
        private const int PORT = 23;

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
        /// The default data size for received data.
        /// </summary>
        private readonly int _dataSize;

        /// <summary>
        /// Contains the received data.
        /// </summary>
        private byte[] _incomingDataBuffer;

        /// <summary>
        /// True for allowing incoming connections;
        /// false otherwise.
        /// </summary>
        private bool _acceptIncomingConnections { get; set; }

        /// <summary>
        /// Contains all connected clients indexed
        /// by their socket.
        /// </summary>
        private Dictionary<Socket, Client> _clients;

        public delegate void ConnectionEventHandler(Client c);


        public delegate void ConnectionBlockedEventHandler(IPEndPoint endPoint);

        public delegate void MessageReceivedEventHandler(Client c, string message);

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
        /// Occurs when a message is received.
        /// </summary>
        public event MessageReceivedEventHandler MessageReceived;

        /// <summary>
        /// Initializes a new instance of the <see cref="Server"/> class.
        /// </summary>
        /// <param name="ip">The IP on which to listen to.</param>
        /// <param name="dataSize">Data size for received data.</param>
        public Server(IPAddress ip, int dataSize = 1024)
        {
            _ip = ip;
            _dataSize = dataSize;
            _incomingDataBuffer = new byte[dataSize];
            _clients = new Dictionary<Socket, Client>();
            _acceptIncomingConnections = true;
            _serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }

        /// <summary>
        /// Starts the server.
        /// </summary>
        public void start()
        {
            _serverSocket.Bind(new IPEndPoint(_ip, PORT));
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
            sendMessageToClient(c, "\u001B[1J\u001B[H");
        }

        /// <summary>
        /// Sends a text message to the specified
        /// client.
        /// </summary>
        /// <param name="c">The client.</param>
        /// <param name="message">The message.</param>
        public void sendMessageToClient(Client c, string message)
        {
            Socket clientSocket = getSocketByClient(c);
            //Console.WriteLine("Sending: " + message + "\nTo: " + c.getRemoteAddress().ToString());
            sendMessageToSocket(clientSocket, message);
        }

        /// <summary>
        /// Sends a text message to the specified
        /// socket.
        /// </summary>
        /// <param name="s">The socket.</param>
        /// <param name="message">The message.</param>
        private void sendMessageToSocket(Socket s, string message)
        {
            byte[] data = Encoding.ASCII.GetBytes(message);
            sendBytesToSocket(s, data);
        }

        /// TODO: Remove this function? Short functions are (arguably) a code smell.
        /// <summary>
        /// Sends bytes to the specified socket.
        /// </summary>
        /// <param name="s">The socket.</param>
        /// <param name="data">The bytes.</param>
        private void sendBytesToSocket(Socket s, byte[] data)
        {
            s.BeginSend(data, 0, data.Length, SocketFlags.None, new AsyncCallback(sendData), s);
        }

        /// <summary>
        /// Sends a message to all connected clients.
        /// </summary>
        /// <param name="message">The message.</param>
        public void sendMessageToAll(string message)
        {
            foreach (Socket s in _clients.Keys)
            {
                try
                {
                    Client c = _clients[s];

                    sendMessageToSocket(s, END_LINE + message + END_LINE + CURSOR);
                    c.resetReceivedData();
                }
                catch
                {
                    _clients.Remove(s);
                }
            }
        }

        /// <summary>
        /// Gets the client by socket.
        /// </summary>
        /// <param name="clientSocket">The client's socket.</param>
        /// <returns>If the socket is found, the client instance
        /// is returned; otherwise null is returned.</returns>
        private Client getClientBySocket(Socket clientSocket)
        {
            Client c;

            if (!_clients.TryGetValue(clientSocket, out c))
                c = null;

            return c;
        }

        /// <summary>
        /// Gets the socket by client.
        /// </summary>
        /// <param name="client">The client instance.</param>
        /// <returns>If the client is found, the socket is
        /// returned; otherwise null is returned.</returns>
        private Socket getSocketByClient(Client client)
        {
            Socket s;

            s = _clients.FirstOrDefault(x => x.Value.Id == client.Id).Key;

            return s;
        }

        /// <summary>
        /// Kicks the specified client from the server.
        /// </summary>
        /// <param name="client">The client.</param>
        public void kickClient(Client client)
        {
            closeSocket(getSocketByClient(client));
            ClientDisconnected(client);
        }

        /// <summary>
        /// Closes the socket and removes the client from
        /// the clients list.
        /// </summary>
        /// <param name="clientSocket">The client socket.</param>
        private void closeSocket(Socket clientSocket)
        {
            clientSocket.Close();
            _clients.Remove(clientSocket);
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

                    uint clientID = (uint)_clients.Count + 1;
                    Client client = new Client(clientID, (IPEndPoint)newSocket.RemoteEndPoint);
                    _clients.Add(newSocket, client);

                    sendBytesToSocket(
                        newSocket,
                        new byte[] {
                            0xff, 0xfd, 0x01,   // Do Echo
                            0xff, 0xfd, 0x21,   // Do Remote Flow Control
                            0xff, 0xfb, 0x01,   // Will Echo
                            0xff, 0xfb, 0x03    // Will Supress Go Ahead
                        }
                    );

                    client.resetReceivedData();

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

        /// <summary>
        /// Sends data to a socket.
        /// </summary>
        private void sendData(IAsyncResult result)
        {
            try
            {
                Socket clientSocket = (Socket)result.AsyncState;

                clientSocket.EndSend(result);
                // Why start receive after send? What if we send more than 1 message?
                clientSocket.BeginReceive(_incomingDataBuffer, 0, _dataSize, SocketFlags.None, new AsyncCallback(receiveData), clientSocket);
            }

            catch { }
        }

        /// <summary>
        /// Receives and processes data from a socket.
        /// It triggers the message received event in
        /// case the client pressed the return key.
        /// </summary>
        private void receiveData(IAsyncResult result)
        {
            try
            {
                Socket clientSocket = (Socket)result.AsyncState;
                Client client = getClientBySocket(clientSocket);

                int bytesReceived = clientSocket.EndReceive(result);

                if (bytesReceived == 0)
                {
                    closeSocket(clientSocket);
                    _serverSocket.BeginAccept(new AsyncCallback(handleIncomingConnection), _serverSocket);
                }

                // Trim null characters and copy to a new location.
                byte[] trimmedBytes = new byte[bytesReceived];
                Array.Copy(_incomingDataBuffer, trimmedBytes, bytesReceived);

                TelnetCommand currentCommand = null;

                // Loop through the input characters.
                for (int i = 0; i < bytesReceived; i++)
                {
                    // Command has been created - we are now parsing it.
                    if (currentCommand != null)
                    {
                        // Attempt to pass the byte to the command. If it uses it, move to the next.
                        if (currentCommand.ProccessByte(trimmedBytes[i]))
                        {
                            continue;
                        }
                        // If we get another IAC or the command is complete, handle the command and continue parsing as normal                        
                        else
                        {
                            handleTelnetCommand(client, currentCommand);
                            currentCommand = null;
                        }
                    }

                    // Interpret as Command (IAC) character. Begin command.
                    if (trimmedBytes[i] == 0xFF)
                    {
                        currentCommand = new TelnetCommand();
                        continue;
                    }

                    // Normal character? Add it to the current string.
                    client.appendReceivedData(Convert.ToChar(trimmedBytes[i]));

                    // Carriage return then new line. Accept this as a message.
                    if (i > 0 && trimmedBytes[i - 1] == 0x0D && trimmedBytes[i] == 0x0A)
                    {
                        if (client.IsRemoteEcho) { sendBytesToSocket(clientSocket, Encoding.Default.GetBytes(client.getReceivedData().TrimEnd('\n').TrimEnd('\r'))); }
                        MessageReceived(client, client.getReceivedData());
                        client.resetReceivedData();
                    }
                }

                clientSocket.BeginReceive(_incomingDataBuffer, 0, _dataSize, SocketFlags.None, new AsyncCallback(receiveData), clientSocket);
            }

            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private void handleTelnetCommand(Client originatingClient, TelnetCommand command)
        {
            Console.WriteLine("Client at " + originatingClient.RemoteAddress.ToString() + " sent: " + command.Command + " " + command.Option + " " + Encoding.Default.GetString(command.NegotiationData.ToArray()));
            // Durr do stuff.
            switch (command.Command.GetValueOrDefault())
            {
                case CommandCode.DO:
                    switch (command.Option.GetValueOrDefault())
                    {
                        case OptionCode.Echo:
                            originatingClient.IsRemoteEcho = true;
                            break;
                        // TODO: Handle other stuffs too?
                    }
                    break;
            }
            return;
        }
    }
}