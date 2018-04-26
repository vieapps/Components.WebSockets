using System;
using System.IO;

namespace net.vieapps.Components.WebSockets.Implementation
{
    /// <summary>
    /// The WebSocket context used to initiate a WebSocket handshake
    /// </summary>
    public class WebSocketContext
    {
        /// <summary>
        /// True if this is a valid WebSocket request
        /// </summary>
        public bool IsWebSocketRequest { get; private set; }

        /// <summary>
        /// The raw http header extracted from the stream
        /// </summary>
        public string HttpHeader { get; private set; }

        /// <summary>
        /// The Path extracted from the http header
        /// </summary>
        public string Path { get; private set; }

        /// <summary>
        /// The stream AFTER the header has already been read
        /// </summary>
        public Stream Stream { get; private set; }

		/// <summary>
		/// Initialises a new instance of the WebSocketContext class
		/// </summary>
		/// <param name="isWebSocketRequest">True if this is a valid WebSocket request</param>
		/// <param name="httpHeader">The raw HTTP header extracted from the stream</param>
		/// <param name="path">The Path extracted from the HTTP header</param>
		/// <param name="stream">The stream AFTER the header has already been read</param>
		public WebSocketContext(bool isWebSocketRequest, string httpHeader, string path, Stream stream)
        {
            this.IsWebSocketRequest = isWebSocketRequest;
			this.HttpHeader = httpHeader;
			this.Path = path;
			this.Stream = stream;
        }
	}
}