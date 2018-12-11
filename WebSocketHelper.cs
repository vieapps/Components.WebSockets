#region Related components
using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using net.vieapps.Components.Utility;
using net.vieapps.Components.WebSockets.Exceptions;
#endregion

namespace net.vieapps.Components.WebSockets
{
	public static class WebSocketHelper
	{
		/// <summary>
		/// Gets or sets the size (length) of the protocol buffer used to receive and parse frames
		/// </summary>
		public static int ReceiveBufferSize { get; internal set; } = 16 * 1024;

		/// <summary>
		/// Gets a factory to get recyclable memory stream with RecyclableMemoryStreamManager class to limit LOH fragmentation and improve performance
		/// </summary>
		/// <returns></returns>
		public static Func<MemoryStream> GetRecyclableMemoryStreamFactory()
			=> UtilityService.GetRecyclableMemoryStreamFactory(16 * 1024, 4, 128 * 1024);

		/// <summary>
		/// Reads the header
		/// </summary>
		/// <param name="stream">The stream to read from</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns>The HTTP header</returns>
		public static async Task<string> ReadHeaderAsync(this Stream stream, CancellationToken cancellationToken = default(CancellationToken))
		{
			var buffer = new byte[WebSocketHelper.ReceiveBufferSize];
			var offset = 0;
			var read = 0;

			do
			{
				if (offset >= WebSocketHelper.ReceiveBufferSize)
					throw new EntityTooLargeException("Header is too large to fit into the buffer");

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
		/// <param name="stream">The stream to write to</param>
		/// <param name="header">The header (without the new line characters)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static Task WriteHeaderAsync(this Stream stream, string header, CancellationToken cancellationToken = default(CancellationToken))
			=> stream.WriteAsync((header.Trim() + "\r\n\r\n").ToArraySegment(), cancellationToken);

		/// <summary>
		/// Computes a WebSocket accept key from a given key
		/// </summary>
		/// <param name="key">The WebSocket request key</param>
		/// <returns>A WebSocket accept key</returns>
		public static string ComputeAcceptKey(this string key) => (key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").GetHash("SHA1").ToBase64();

		/// <summary>
		/// Negotiates sub-protocol
		/// </summary>
		/// <param name="supportedSubProtocols"></param>
		/// <param name="requestedSubProtocols"></param>
		/// <returns></returns>
		public static string NegotiateSubProtocol(this IEnumerable<string> supportedSubProtocols, IEnumerable<string> requestedSubProtocols)
			=> !supportedSubProtocols.Any() || !requestedSubProtocols.Any()
				? null
				: requestedSubProtocols.Intersect(supportedSubProtocols).Any()
					? requestedSubProtocols.Intersect(supportedSubProtocols).First()
					: throw new SubProtocolNegotiationFailedException("Unable to negotiate a sub-protocol");

		/// <summary>
		/// Set keep-alive interval to something more reasonable (because the TCP keep-alive default values of Windows are huge ~7200s)
		/// </summary>
		/// <param name="socket"></param>
		/// <param name="keepaliveInterval"></param>
		/// <param name="retryInterval"></param>
		public static void SetKeepAliveInterval(this Socket socket, uint keepaliveInterval = 60000, uint retryInterval = 10000)
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				socket.IOControl(IOControlCode.KeepAliveValues, ((uint)1).ToBytes().Concat(keepaliveInterval.ToBytes(), retryInterval.ToBytes()), null);
		}
	}
}