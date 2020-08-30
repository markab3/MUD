using System;
using System.Net;

namespace MUD.Telnet
{

    public class Client
    {
        /// <summary>
        /// The client's identifier.
        /// </summary>
        public uint Id { get; set; }
        /// <summary>
        /// The client's remote address.
        /// </summary>
        public IPEndPoint RemoteAddress {get;set;}
        /// <summary>
        /// The connection datetime.
        /// </summary>
        public DateTime ConnectedAt;

        /// <summary>
        /// The last received data from the client.
        /// </summary>
        private string _receivedData;

        public bool IsRemoteEcho { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="Client"/> class.
        /// </summary>
        /// <param name="clientId">The client's identifier.</param>
        /// <param name="remoteAddress">The remote address.</param>
        public Client(uint clientId, IPEndPoint remoteAddress)
        {
            Id = clientId;
            RemoteAddress = remoteAddress;
            ConnectedAt = DateTime.Now;
            _receivedData = string.Empty;
        }

        /// <summary>
        /// Gets the client's last received data.
        /// </summary>
        /// <returns>Client's last received data.</returns>
        public string getReceivedData()
        {
            return _receivedData;
        }

        /// <summary>
        /// Appends a string to the client's last
        /// received data.
        /// </summary>
        /// <param name="dataToAppend">The data to append.</param>
        public void appendReceivedData(char dataToAppend)
        {
            this._receivedData += dataToAppend;
        }

        /// <summary>
        /// Resets the last received data.
        /// </summary>
        public void resetReceivedData()
        {
            _receivedData = string.Empty;
        }

        public override string ToString()
        {
            string ip = string.Format("{0}:{1}", RemoteAddress.Address.ToString(), RemoteAddress.Port);
            string res = string.Format("Client #{0} (From: {1}, Connection time: {2})", Id, ip, ConnectedAt);
            return res;
        }
    }
}