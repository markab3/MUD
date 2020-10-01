using System;
using System.Collections.Generic;
using System.Linq;
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

        private List<string> _supportedTerminals = new List<string>();

        // Client socket.
        private Socket _workSocket = null;

        private Encoding _clientEncoding = Encoding.ASCII;

        private string _lineEnding = "\r\n";

        private TelnetCommand _currentCommand = null;

        public bool IsRemoteEcho { get; set; }

        public bool IsEchoSupressed { get; set; }

        public event EventHandler<Client> ClientDisconnected;

        public event EventHandler<String> DataReceived;

        public Client(Socket workSocket)
        {
            _workSocket = workSocket;
        }

        public void Send(TelnetCommand command)
        {
            // Convert the command to a byte array.
            byte[] byteData = command.ToByteArray();

            Send(byteData);
        }

        public void Send(String data)
        {
            // Unify line endings to be consistent with the client.
            data.Replace("\n", "\r").Replace("\r\r", "\r").Replace("\r", _lineEnding);

            // Ensure that it ends with line ending
            if (!data.EndsWith(_lineEnding)) { data = data + _lineEnding; }

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
                    // Grab raw data.
                    byte[] receivedData = _buffer.Take(bytesRead).ToArray();

                    // Decode.
                    string receivedMessage = string.Empty;

                    // Handle telnet commands.
                    foreach (byte currentByte in receivedData)
                    {
                        if (_currentCommand != null)
                        {
                            if (_currentCommand.ProccessByte(currentByte) && !_currentCommand.IsComplete)
                            {
                                continue;
                            }
                            else
                            {
                                handleTelnetCommand(_currentCommand);
                                _currentCommand = null;
                            }
                        }

                        if (currentByte == (byte)CommandCode.IAC)
                        {
                            _currentCommand = new TelnetCommand();
                            continue;
                        }
                        receivedMessage += _clientEncoding.GetString(new byte[] { currentByte }); // hmm...
                    }

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

                    string currentLine = string.Empty;

                    // Peel off each line, treating a newline as the terminator for the command.                 
                    while (newlineIndex > -1)
                    {
                        // TODO: Handle TelnetCommands
                        currentLine = _receivedData.Substring(0, newlineIndex);
                        _receivedData = _receivedData.Substring(newlineIndex + 1);

                        DataReceived?.Invoke(this, currentLine);

                        //  Display it on the console.  
                        Console.WriteLine("Line received: {0}\r", currentLine);

                        // Echo the data back to the client.  
                        if (IsRemoteEcho && !IsEchoSupressed) { Send(currentLine); }

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
                case CommandCode.WILL:
                    switch (command.Option.GetValueOrDefault())
                    {
                        case OptionCode.TerminalType:
                            Send(new byte[] { (byte)CommandCode.IAC, (byte)CommandCode.SB, (byte)OptionCode.TerminalType, 0x01, (byte)CommandCode.IAC, (byte)CommandCode.SE });
                            break;
                    }
                    break;
                case CommandCode.SB:
                    switch (command.Option.GetValueOrDefault())
                    {
                        case OptionCode.TerminalType:
                            string newTerminalType = _clientEncoding.GetString(command.NegotiationData.ToArray());
                            if (!_supportedTerminals.Contains(newTerminalType))
                            {
                                _supportedTerminals.Add(newTerminalType);
                                Send(new byte[] { (byte)CommandCode.IAC, (byte)CommandCode.SB, (byte)OptionCode.TerminalType, 0x01, (byte)CommandCode.IAC, (byte)CommandCode.SE });
                            }
                            break;
                    }
                    break;
            }
            return;
        }
    }
}