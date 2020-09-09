using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MUD.Telnet
{

    public class Client
    {
        /// <summary>
        /// The client's remote address.
        /// </summary>
        public IPEndPoint RemoteAddress { get; set; }

        /// <summary>
        /// The connection datetime.
        /// </summary>
        private DateTime _connectedAt;

        private Socket _clientSocket { get; set; }

        /// <summary>
        /// The last received data from the client.
        /// </summary>
        private string _receivedData;

        /// <summary>
        /// The default data size for received data.
        /// </summary>
        private readonly int _dataSize;

        /// <summary>
        /// Contains the received data.
        /// </summary>
        private byte[] _incomingDataBuffer;

        public bool IsRemoteEcho { get; set; }

        public delegate void MessageReceivedEventHandler(Client c, string message);

        public delegate void CloseConnectionEventHandler(Client c);


        /// <summary>
        /// Occurs when a message is received from the client.
        /// </summary>
        public event MessageReceivedEventHandler MessageReceived;

        /// <summary>
        /// Occurs when a client needs to request the connection be closed.
        /// </summary>
        public event CloseConnectionEventHandler CloseConnection;

        /// <summary>
        /// Initializes a new instance of the <see cref="Client"/> class.
        /// </summary>
        /// <param name="remoteAddress">The remote address.</param>
        public Client(Socket clientSocket)
        {
            _clientSocket = clientSocket;
            RemoteAddress = (IPEndPoint) clientSocket.RemoteEndPoint;
            _connectedAt = DateTime.Now;
            _receivedData = string.Empty;
            _dataSize = 1024;
            _incomingDataBuffer = new byte[_dataSize];
        }

        public void CloseSocket() {
            _clientSocket.Close();
        }
        
        /// <summary>
        /// Sends a text message to the client.
        /// </summary>
        /// <param name="message">The message.</param>
        public void SendMessageToClient(string message)
        {
            Console.WriteLine("Sending: " + message + "\nTo: " + RemoteAddress.ToString());
            byte[] data = Encoding.ASCII.GetBytes(message);
            _clientSocket.BeginSend(data, 0, data.Length, SocketFlags.None, new AsyncCallback(sendMessageComplete), _clientSocket);
        }

        /// <summary>
        /// Sends raw bytes to the client.
        /// </summary>
        /// <param name="message">The message.</param>
        public void SendMessageToClient(byte[] message) {
            _clientSocket.BeginSend(message, 0, message.Length, SocketFlags.None, new AsyncCallback(sendMessageComplete), _clientSocket);
        }

        private void sendMessageComplete(IAsyncResult result)
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

                int bytesReceived = clientSocket.EndReceive(result);

                if (bytesReceived == 0) { CloseConnection(this); } // Unsure if this is necessary....

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
                            handleTelnetCommand(currentCommand);
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
                    _receivedData += Convert.ToChar(trimmedBytes[i]);

                    // Carriage return then new line. Accept this as a message.
                    if (i > 0 && trimmedBytes[i - 1] == 0x0D && trimmedBytes[i] == 0x0A)
                    {
                        if (IsRemoteEcho) { SendMessageToClient(_receivedData.TrimEnd('\n').TrimEnd('\r')); }
                        MessageReceived(this, _receivedData);
                        _receivedData = string.Empty;
                    }
                }

                clientSocket.BeginReceive(_incomingDataBuffer, 0, _dataSize, SocketFlags.None, new AsyncCallback(receiveData), clientSocket);
            }

            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        /// <summary>
        /// Handles an incoming telnet command. May not need this here, but 
        /// </summary>
        private void handleTelnetCommand(TelnetCommand command)
        {
            Console.WriteLine("Client at " + RemoteAddress.ToString() + " sent: " + command.Command + " " + command.Option + " " + Encoding.Default.GetString(command.NegotiationData.ToArray()));
            // Durr do stuff.
            switch (command.Command.GetValueOrDefault())
            {
                case CommandCode.DO:
                    switch (command.Option.GetValueOrDefault())
                    {
                        case OptionCode.Echo:
                            IsRemoteEcho = true;
                            break;
                        // TODO: Handle other stuffs too?
                    }
                    break;
            }
            return;
        }

        public override string ToString()
        {
            string ip = string.Format("{0}:{1}", RemoteAddress.Address.ToString(), RemoteAddress.Port);
            string res = string.Format("Client IP Address: {0}, Connection time: {1}", ip, _connectedAt);
            return res;
        }
    }
}