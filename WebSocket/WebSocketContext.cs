#region Related components
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
#endregion

namespace net.vieapps.Components.WebSockets.Implementation
{
	/// <summary>
	/// The WebSocket context used to initiate a WebSocket handshake
	/// </summary>
	internal class WebSocketContext
	{
		/// <summary>
		/// Gets the state that specified this is a valid WebSocket request or not
		/// </summary>
		public bool IsWebSocketRequest { get; private set; }

		/// <summary>
		/// Gets the Host from the HTTP header
		/// </summary>
		public string Host { get; private set; }

		/// <summary>
		/// Gets the Path from the HTTP header
		/// </summary>
		public string Path { get; private set; }

		/// <summary>
		/// Gets the raw HTTP header
		/// </summary>
		public string Header { get; private set; }

		/// <summary>
		/// Getst the stream AFTER the header has already been read
		/// </summary>
		public Stream Stream { get; private set; }

		/// <summary>
		/// Initialises a new instance of the WebSocketContext class
		/// </summary>
		/// <param name="isWebSocketRequest">true if this is a valid WebSocket request</param>
		/// <param name="host">The Host extracted from the HTTP header</param>
		/// <param name="path">The Path extracted from the HTTP header</param>
		/// <param name="header">The raw HTTP header extracted from the stream</param>
		/// <param name="stream">The stream AFTER the header has already been read</param>
		public WebSocketContext(bool isWebSocketRequest, string host, string path, string header, Stream stream)
		{
			this.IsWebSocketRequest = isWebSocketRequest;
			this.Host = host;
			this.Path = path;
			this.Header = header;
			this.Stream = stream;
		}

		/// <summary>
		/// Reads the HTTP header from a stream and decodes the parts relating to the context of a WebSocket connection request
		/// </summary>
		/// <param name="stream">The network stream</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<WebSocketContext> ParseAsync(Stream stream, CancellationToken cancellationToken = default(CancellationToken))
		{
			var isWebSocketUpgradeRequest = false;
			var path = string.Empty;
			var host = string.Empty;
			var header = await WebSocketHelper.ReadHttpHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
			var match = new Regex(@"^GET(.*)HTTP\/1\.1", RegexOptions.IgnoreCase).Match(header);
			if (match.Success)
			{
				isWebSocketUpgradeRequest = new Regex("Upgrade: websocket", RegexOptions.IgnoreCase).Match(header).Success;
				path = match.Groups[1].Value.Trim();
				match = new Regex("Host: (.*)").Match(header);
				host = match.Success
					? match.Groups[1].Value.Trim()
					: string.Empty;
			}
			return new WebSocketContext(isWebSocketUpgradeRequest, host, path, header, stream);
		}
	}
}