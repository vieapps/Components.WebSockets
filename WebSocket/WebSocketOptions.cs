using System;
using System.Collections.Generic;

namespace net.vieapps.Components.WebSockets.Implementation
{
	/// <summary>
	/// Initialize options for a server side WebSocket connection
	/// </summary>
	public class WebSocketServerOptions
	{
		/// <summary>
		/// How often to send ping requests to the Client
		/// The default is 60 seconds
		/// This is done to prevent proxy servers from closing your connection
		/// A timespan of zero will disable the automatic ping pong mechanism
		/// You can manually control ping pong messages using the PingPongManager class.
		/// If you do that it is advisible to set this KeepAliveInterval to zero
		/// </summary>
		public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(60);

		/// <summary>
		/// Include the full exception (with stack trace) in the close response 
		/// when an exception is encountered and the WebSocket connection is closed
		/// The default is false
		/// </summary>
		public bool IncludeExceptionInCloseResponse { get; set; } = false;

		public WebSocketServerOptions() { }
	}

	/// <summary>
	/// Initialize options for a client side WebSocket connection
	/// </summary>
	public class WebSocketClientOptions
	{
		/// <summary>
		/// How often to send ping requests to the Server
		/// This is done to prevent proxy servers from closing your connection
		/// The default is TimeSpan.Zero meaning that it is disabled.
		/// WebSocket servers usually send ping messages so it is not normally necessary for the client to send them (hence the TimeSpan.Zero default)
		/// You can manually control ping pong messages using the PingPongManager class.
		/// If you do that it is advisible to set this KeepAliveInterval to zero
		/// </summary>
		public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.Zero;

		/// <summary>
		/// Set to true to send a message immediately with the least amount of latency (typical usage for chat)
		/// This will disable Nagle's algorithm which can cause high tcp latency for small packets sent infrequently
		/// However, if you are streaming large packets or sending large numbers of small packets frequently it is advisable to set NoDelay to false
		/// This way data will be bundled into larger packets for better throughput
		/// </summary>
		public bool NoDelay { get; set; } = true;

		/// <summary>
		/// Add any additional http headers to this dictionary
		/// </summary>
		public Dictionary<string, string> AdditionalHttpHeaders { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Include the full exception (with stack trace) in the close response 
		/// when an exception is encountered and the WebSocket connection is closed
		/// The default is false
		/// </summary>
		public bool IncludeExceptionInCloseResponse { get; set; } = false;

		/// <summary>
		/// WebSocket Extensions as an HTTP header value
		/// </summary>
		public string SecWebSocketExtensions { get; set; }

		public WebSocketClientOptions() { }
	}
}