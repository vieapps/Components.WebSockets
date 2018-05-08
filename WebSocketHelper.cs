#region Related components
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

using net.vieapps.Components.Utility;
using net.vieapps.Components.WebSockets.Exceptions;
#endregion

namespace net.vieapps.Components.WebSockets
{
	internal static class WebSocketHelper
	{
		/// <summary>
		/// Gets or sets the size (length) of the protocol buffer used to receive and parse frames
		/// </summary>
		public static int ReceiveBufferSize { get; internal set; } = 16 * 1024;

		/// <summary>
		/// Gets a factory to get recyclable memory stream with RecyclableMemoryStreamManager class to limit LOH fragmentation and improve performance
		/// </summary>
		/// <returns></returns>
		public static Func<MemoryStream> GetRecyclableMemoryStreamFactory() => new Microsoft.IO.RecyclableMemoryStreamManager(16 * 1024, 4, 128 * 1024).GetStream;

		/// <summary>
		/// Reads the header
		/// </summary>
		/// <param name="stream">The stream to read from</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns>The HTTP header</returns>
		public static async Task<string> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken = default(CancellationToken))
		{
			var buffer = new byte[WebSocketHelper.ReceiveBufferSize];
			var offset = 0;
			var read = 0;

			do
			{
				if (offset >= WebSocketHelper.ReceiveBufferSize)
					throw new EntityTooLargeException("HTTP header message too large to fit in buffer");

				read = await stream.ReadAsync(buffer, offset, WebSocketHelper.ReceiveBufferSize - offset, cancellationToken).ConfigureAwait(false);
				offset += read;
				var header = buffer.GetString(offset);

				// as per specs, all headers should end like this
				if (header.Contains("\r\n\r\n"))
					return header;
			}
			while (read > 0);

			return string.Empty;
		}

		/// <summary>
		/// Writes the header
		/// </summary>
		/// <param name="header">The header (without the new line characters)</param>
		/// <param name="stream">The stream to write to</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task WriteHeaderAsync(string header, Stream stream, CancellationToken cancellationToken = default(CancellationToken))
		{
			// as per specs, all headers should end like this
			var bytes = (header.Trim() + "\r\n\r\n").ToBytes();
			await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Computes a WebSocket accept key from a given key
		/// </summary>
		/// <param name="key">The WebSocket request key</param>
		/// <returns>A WebSocket accept key</returns>
		public static string ComputeAcceptKey(string key) => (key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").GetHash("SHA1").ToBase64();

		/// <summary>
		/// Negotiates sub-protocol
		/// </summary>
		/// <param name="server"></param>
		/// <param name="client"></param>
		/// <returns></returns>
		public static string NegotiateSubProtocol(IEnumerable<string> server, IEnumerable<string> client)
		{
			if (!server.Any() || !client.Any())
				return null;
			var matches = client.Intersect(server);
			return matches.Any()
				? matches.First()
				: throw new SubProtocolNegotiationFailedException("Unable to negotiate a subprotocol");
		}
	}
}