using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MUD.Telnet
{
    public class Client
    {
        public IPEndPoint RemoteAddress { get { return (IPEndPoint)_workSocket.RemoteEndPoint; } }
        // Size of receive buffer.  
        public const int BufferSize = 1024;

        // Receive buffer.  
        private byte[] _buffer = new byte[BufferSize];

        // Received data string.
        private string _receivedData = String.Empty;

        // Client socket.
        private Socket _workSocket = null;

        private Encoding _clientEncoding = Encoding.ASCII;

        private string _lineEnding = "\r\n";

        public bool IsRemoteEcho { get; set; }

        public bool IsEchoSupressed { get; set; }

        public event EventHandler<Client> ClientDisconnected;

        public event EventHandler<String> DataReceived;

        public Client(Socket workSocket)
        {
            _workSocket = workSocket;
        }

        public void Send(String data)
        {
            // Unify line endings to be consistent with the client.
            data.Replace("\n", "\r").Replace("\r\r", "\r").Replace("\r", _lineEnding);

            // Convert the string data to byte data using the client's encoding.  
            byte[] byteData = _clientEncoding.GetBytes(data);

            Send(byteData);
        }

        public void Send(byte[] data)
        {

            try
            {
                // Begin sending the data to the remote device.  
                _workSocket.BeginSend(data, 0, data.Length, 0, new AsyncCallback(sendCallback), _workSocket);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                // Disconnect.
                Disconnect();
            }
        }

        public void Read()
        {
            try
            {
                if (_workSocket != null && _workSocket.Connected)
                {
                    _workSocket.BeginReceive(_buffer, 0, BufferSize, 0, new AsyncCallback(readCallback), this);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Disconnect();
            }

        }
        public void Disconnect()
        {
            if (_workSocket.Connected)
            {
                _workSocket.Shutdown(SocketShutdown.Both);
                _workSocket.Close();

                ClientDisconnected?.Invoke(this, this);
            }
        }

        private void sendCallback(IAsyncResult ar)
        {
            try
            {
                // Complete sending the data to the remote device.  
                int bytesSent = _workSocket.EndSend(ar);
                Console.WriteLine("Sent {0} bytes to client.", bytesSent);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
                Disconnect();
            }
        }
        private void readCallback(IAsyncResult ar)
        {
            try
            {
                // Read data from the client socket.
                int bytesRead = _workSocket.EndReceive(ar);

                if (bytesRead > 0)
                {
                    // Decode.
                    string receivedMessage = _clientEncoding.GetString(_buffer, 0, bytesRead);

                    // Get last used line ending..
                    if (receivedMessage.Contains("\r\n"))
                    {
                        _lineEnding = "\r\n";
                    }
                    else if (receivedMessage.Contains("\r"))
                    {
                        _lineEnding = "\r";
                    }
                    else if (receivedMessage.Contains("\n"))
                    {
                        _lineEnding = "\n";
                    }

                    // Unify the line endings.
                    receivedMessage = receivedMessage.Replace("\n", "\r").Replace("\r\r", "\r");

                    // There might be more data, so store the data received so far.  
                    _receivedData = _receivedData + receivedMessage;

                    int newlineIndex = _receivedData.IndexOf("\r");

                    string currentCommand = string.Empty;

                    // Peel off each line, treating a newline as the terminator for the command.                 
                    while (newlineIndex > -1)
                    {
                        // TODO: Handle TelnetCommands
                        currentCommand = _receivedData.Substring(0, newlineIndex);
                        _receivedData = _receivedData.Substring(newlineIndex + 1);

                        DataReceived?.Invoke(this, currentCommand);

                        //  Display it on the console.  
                        Console.WriteLine("Line received: {0}\r", currentCommand);

                        // Echo the data back to the client.  
                        if (IsRemoteEcho && !IsEchoSupressed) { Send(currentCommand); }

                        newlineIndex = _receivedData.IndexOf("\r");
                    }

                    Read();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Disconnect();
            }
        }
    }
}