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
        // Captured at construction: the socket cannot be queried once it has been closed.
        private readonly IPEndPoint _remoteAddress;
        public IPEndPoint RemoteAddress { get { return _remoteAddress; } }
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

        private readonly TelnetLimits _limits;

        private readonly Func<DateTime> _clock;

        private readonly RateLimiter _rateLimiter;

        private readonly OptionRefusals _optionRefusals = new OptionRefusals();

        private DateTime _lastActivity;

        // 0 = open, 1 = disconnected. Makes Disconnect idempotent so ClientDisconnected fires exactly once.
        private int _disconnected = 0;

        public bool IsRemoteEcho { get; set; }

        public bool IsEchoSupressed { get; set; }

        /// <summary>
        /// True once the connection has logged in; selects the (longer) logged-in idle timeout.
        /// </summary>
        public bool IsLoggedIn { get; set; }

        public event EventHandler<Client> ClientDisconnected;

        public event EventHandler<String> DataReceived;

        public Client(Socket workSocket) : this(workSocket, null, null)
        {
        }

        /// <param name="limits">Input limits; defaults are used when null.</param>
        /// <param name="clock">Time source (UTC); replaceable so tests can use a fake clock.</param>
        public Client(Socket workSocket, TelnetLimits limits, Func<DateTime> clock)
        {
            _workSocket = workSocket;
            _limits = limits ?? new TelnetLimits();
            _clock = clock ?? (() => DateTime.UtcNow);
            _rateLimiter = new RateLimiter(_limits.RateLimitLines, _limits.RateLimitWindow);
            _lastActivity = _clock();
            try { _remoteAddress = (IPEndPoint)workSocket.RemoteEndPoint; } catch (Exception) { _remoteAddress = null; }
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
                if (_disconnected == 0 && _workSocket != null && _workSocket.Connected)
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
            if (Interlocked.Exchange(ref _disconnected, 1) != 0) { return; }

            try
            {
                _workSocket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception)
            {
                // Already closed or reset by the peer; nothing left to shut down.
            }
            try
            {
                _workSocket.Close();
            }
            catch (Exception)
            {
            }

            ClientDisconnected?.Invoke(this, this);
        }

        /// <summary>
        /// Sends a final message synchronously (so it is written before the socket closes) and disconnects.
        /// </summary>
        public void DisconnectWithMessage(string message)
        {
            if (_disconnected == 0)
            {
                try
                {
                    _workSocket.Send(_clientEncoding.GetBytes(message + _lineEnding));
                }
                catch (Exception)
                {
                    // Peer is already gone; just disconnect.
                }
            }
            Disconnect();
        }

        /// <summary>
        /// Disconnects with a notice if the connection has been silent for longer than the applicable idle
        /// timeout. Returns true if the client was timed out.
        /// </summary>
        public bool CheckIdle(DateTime now)
        {
            if (_disconnected != 0) { return false; }

            TimeSpan timeout = IsLoggedIn ? _limits.IdleTimeoutLoggedIn : _limits.IdleTimeoutPreLogin;
            if (now - _lastActivity < timeout) { return false; }

            DisconnectWithMessage("You have been idle for too long. Goodbye.");
            return true;
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
                    // The remote end closed the connection; release it so connection accounting stays correct.
                    Disconnect();
                    return;
                }

                if (bytesRead > 0)
                {
                    _lastActivity = _clock();

                    // Grab raw data.
                    byte[] receivedData = _buffer.Take(bytesRead).ToArray();

                    // Decode.
                    string receivedMessage = string.Empty;

                    // Handle telnet commands.
                    foreach (byte currentByte in receivedData)
                    {
                        if (_currentCommand != null)
                        {
                            bool consumed = _currentCommand.ProccessByte(currentByte);
                            if (consumed && !_currentCommand.IsComplete)
                            {
                                continue;
                            }
                            else
                            {
                                handleTelnetCommand(_currentCommand);
                                _currentCommand = null;

                                // The final byte of IAC <WILL|WONT|DO|DONT> <option> is the option itself;
                                // it must not also be treated as input text.
                                if (consumed) { continue; }
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

                        if (currentLine.Length > _limits.MaxLineLength)
                        {
                            rejectOverlongLine();
                            return;
                        }

                        if (!_rateLimiter.TryAcquire(_clock()))
                        {
                            _receivedData = String.Empty;
                            DisconnectWithMessage("You are sending commands too quickly. Goodbye.");
                            return;
                        }

                        DataReceived?.Invoke(this, currentLine);

                        // A handler may have disconnected us (e.g. quit).
                        if (_disconnected != 0) { return; }

                        //  Display it on the console.  
                        Console.WriteLine("Line received: {0}\r", currentLine);

                        // Echo the data back to the client.  
                        if (IsRemoteEcho && !IsEchoSupressed) { Send(currentLine); }

                        newlineIndex = _receivedData.IndexOf("\r");
                    }

                    // Whatever is left is an unterminated line; never buffer an over-long one.
                    if (_receivedData.Length > _limits.MaxLineLength)
                    {
                        rejectOverlongLine();
                        return;
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

        private void rejectOverlongLine()
        {
            // Discard the buffer, tell the client why, and hang up.
            _receivedData = String.Empty;
            DisconnectWithMessage(string.Format("Input line too long (maximum {0} bytes). Goodbye.", _limits.MaxLineLength));
        }

        /// <summary>	
        /// Handles an incoming telnet command. May not need this here, but 	
        /// </summary>	
        private void handleTelnetCommand(TelnetCommand command)
        {
            Console.WriteLine("Client at " + RemoteAddress?.ToString() + " sent: " + command.Command + " " + command.Option + " " + Encoding.Default.GetString(command.NegotiationData.ToArray()));
            // Durr do stuff.	
            switch (command.Command.GetValueOrDefault())
            {
                case CommandCode.DO:
                    switch (command.Option.GetValueOrDefault())
                    {
                        case OptionCode.Echo:
                            IsRemoteEcho = true;
                            break;
                        default:
                            // Unsupported option: refuse once with WONT (RFC 854), never repeat.
                            sendIfNotNull(_optionRefusals.RefuseDo((byte)command.Option.GetValueOrDefault()));
                            break;
                    }
                    break;
                case CommandCode.WILL:
                    switch (command.Option.GetValueOrDefault())
                    {
                        case OptionCode.TerminalType:
                            Send(new byte[] { (byte)CommandCode.IAC, (byte)CommandCode.SB, (byte)OptionCode.TerminalType, 0x01, (byte)CommandCode.IAC, (byte)CommandCode.SE });
                            break;
                        default:
                            // Unsupported option: refuse once with DONT.
                            sendIfNotNull(_optionRefusals.RefuseWill((byte)command.Option.GetValueOrDefault()));
                            break;
                    }
                    break;
                // WONT and DONT are never answered (RFC 854); answering them is what causes negotiation loops.
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

        private void sendIfNotNull(byte[] data)
        {
            if (data != null) { Send(data); }
        }
    }
}
