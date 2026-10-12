using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

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

        // True when the last data byte consumed was a CR (used to handle CR LF / CR NUL split across reads).
        private bool _lastDataByteWasCR = false;

        // 0 until Disconnect has run; guards ClientDisconnected so it is raised exactly once.
        private int _disconnected = 0;

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
            data = data.Replace("\n", "\r").Replace("\r\r", "\r").Replace("\r", _lineEnding);

            // Ensure that it ends with line ending
            if (!data.EndsWith(_lineEnding)) { data = data + _lineEnding; }

            // Convert the string data to byte data using the client's encoding.  
            byte[] byteData = _clientEncoding.GetBytes(data);

            // A literal 0xFF data byte must be escaped as IAC IAC.
            if (Array.IndexOf(byteData, (byte)CommandCode.IAC) >= 0)
            {
                List<byte> escaped = new List<byte>(byteData.Length + 1);
                foreach (byte b in byteData)
                {
                    escaped.Add(b);
                    if (b == (byte)CommandCode.IAC) { escaped.Add(b); }
                }
                byteData = escaped.ToArray();
            }

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
            // Only the first call does any work, so ClientDisconnected is raised exactly once.
            if (Interlocked.Exchange(ref _disconnected, 1) != 0) { return; }

            if (_workSocket != null)
            {
                try
                {
                    if (_workSocket.Connected) { _workSocket.Shutdown(SocketShutdown.Both); }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
                finally
                {
                    try { _workSocket.Close(); }
                    catch (Exception ex) { Console.WriteLine(ex.ToString()); }
                }
            }

            ClientDisconnected?.Invoke(this, this);
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

                if (bytesRead == 0)
                {
                    // Remote end closed the connection.
                    Disconnect();
                    return;
                }

                ProcessReceivedBytes(_buffer, bytesRead);

                Read();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Disconnect();
            }
        }

        /// <summary>
        /// Parses raw bytes received from the remote end: handles telnet commands and
        /// raises DataReceived for each complete line of text.
        /// </summary>
        internal void ProcessReceivedBytes(byte[] buffer, int count)
        {
            // Decode.
            string receivedMessage = string.Empty;
            bool previousWasCR = _lastDataByteWasCR;

            // Handle telnet commands.
            for (int i = 0; i < count; i++)
            {
                byte currentByte = buffer[i];

                if (_currentCommand != null)
                {
                    if (_currentCommand.Command == null && currentByte == (byte)CommandCode.IAC)
                    {
                        // IAC IAC is a literal 0xFF data byte.
                        _currentCommand = null;
                        receivedMessage += _clientEncoding.GetString(new byte[] { currentByte });
                        previousWasCR = false;
                        continue;
                    }

                    if (_currentCommand.ProccessByte(currentByte))
                    {
                        if (!_currentCommand.IsComplete) { continue; }

                        // This byte (the option) completed the command; it is consumed, not text.
                        handleTelnetCommand(_currentCommand);
                        _currentCommand = null;
                        continue;
                    }

                    handleTelnetCommand(_currentCommand);
                    _currentCommand = null;
                }

                if (currentByte == (byte)CommandCode.IAC)
                {
                    _currentCommand = new TelnetCommand();
                    continue;
                }

                if (previousWasCR && currentByte == 0)
                {
                    // CR NUL is a bare CR; drop the NUL.
                    previousWasCR = false;
                    continue;
                }

                if (previousWasCR && currentByte == (byte)'\n' && receivedMessage.Length == 0)
                {
                    // LF completing a CRLF whose CR arrived in an earlier read; that line was already delivered.
                    _lineEnding = "\r\n";
                    previousWasCR = false;
                    continue;
                }

                previousWasCR = currentByte == (byte)'\r';
                receivedMessage += _clientEncoding.GetString(new byte[] { currentByte }); // hmm...
            }

            _lastDataByteWasCR = previousWasCR;

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
        }

        /// <summary>	
        /// Handles an incoming telnet command. May not need this here, but 	
        /// </summary>	
        private void handleTelnetCommand(TelnetCommand command)
        {
            Console.WriteLine("Client at " + _workSocket?.RemoteEndPoint + " sent: " + command.Command + " " + command.Option + " " + Encoding.Default.GetString(command.NegotiationData.ToArray()));
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