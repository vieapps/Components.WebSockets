using System;
using System.Collections.Generic;

namespace net.vieapps.Components.WebSockets
{
	/// <summary>
	/// Options for initializing a WebSocket connection
	/// </summary>
	public class WebSocketOptions
	{
		/// <summary>
		/// Gets or sets how often to send ping requests to the remote endpoint
		/// </summary>
		/// <remarks>
		/// This is done to prevent proxy servers from closing your connection, the default is TimeSpan.Zero meaning that it is disabled.
		/// WebSocket servers usually send ping messages so it is not normally necessary for the client to send them (hence the TimeSpan.Zero default)
		/// You can manually control ping pong messages using the PingPongManager class. If you do that it is advisible to set this KeepAliveInterval to zero.
		/// </remarks>
		public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.Zero;

		/// <summary>
		/// Gets or Sets the sub-protocol (Sec-WebSocket-Protocol)
		/// </summary>
		public string SubProtocol { get; set; }

		/// <summary>
		/// Gets or Sets the extensions (Sec-WebSocket-Extensions)
		/// </summary>
		public string Extensions { get; set; }

		/// <summary>
		/// Gets or Sets state to send a message immediately or not
		/// </summary>
		/// <remarks>
		/// Set to true to send a message immediately with the least amount of latency (typical usage for chat)
		/// This will disable Nagle's algorithm which can cause high tcp latency for small packets sent infrequently
		/// However, if you are streaming large packets or sending large numbers of small packets frequently it is advisable to set NoDelay to false
		/// This way data will be bundled into larger packets for better throughput
		/// </remarks>
		public bool NoDelay { get; set; } = true;

		/// <summary>
		/// Gets or Sets the additional headers
		/// </summary>
		public Dictionary<string, string> AdditionalHeaders { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Gets or Sets the state to include the full exception (with stack trace) in the close response when an exception is encountered and the WebSocket connection is closed
		/// </summary>
		/// <remarks>
		/// The default is false
		/// </remarks>
		public bool IncludeExceptionInCloseResponse { get; set; } = false;

		/// <summary>
		/// Gets or Sets whether remote certificate errors should be ignored 
		/// </summary>
		/// <remarks>
		/// The default is false
		/// </remarks>
		public bool IgnoreCertificateErrors { get; set; } = false;

		/// <summary>
		/// Gets or Sets the function to prepare the custom 'PING' playload to send a 'PING' message
		/// </summary>
		public Func<ManagedWebSocket, byte[]> GetPingPayload { get; set; }

		/// <summary>
		/// Gets or Sets the function to prepare the custom 'PONG' playload to response to a 'PING' message
		/// </summary>
		public Func<ManagedWebSocket, byte[], byte[]> GetPongPayload { get; set; }

		/// <summary>
		/// Gets or Sets the action to fire when a 'PONG' message has been sent
		/// </summary>
		public Action<ManagedWebSocket, byte[]> OnPong { get; set; }
	}
}