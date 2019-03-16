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
		/// Gets or sets the agent name of the protocol for working with related headers
		/// </summary>
		public static string AgentName { get; internal set; } = "VIEApps NGX WebSockets";

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

		internal static string ComputeAcceptKey(this string key)
			=> (key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").GetHash("SHA1").ToBase64();

		internal static string NegotiateSubProtocol(this IEnumerable<string> requestedSubProtocols, IEnumerable<string> supportedSubProtocols)
			=> requestedSubProtocols == null || supportedSubProtocols == null || !requestedSubProtocols.Any() || !supportedSubProtocols.Any()
				? null
				: requestedSubProtocols.Intersect(supportedSubProtocols).FirstOrDefault() ?? throw new SubProtocolNegotiationFailedException("Unable to negotiate a sub-protocol");

		internal static void SetKeepAliveInterval(this Socket socket, uint keepaliveInterval = 60000, uint retryInterval = 10000)
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				socket.IOControl(IOControlCode.KeepAliveValues, ((uint)1).ToBytes().Concat(keepaliveInterval.ToBytes(), retryInterval.ToBytes()), null);
		}

		internal static Dictionary<string, string> ToDictionary(this string @string)
			=> string.IsNullOrWhiteSpace(@string)
				? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				: @string.Replace("\r", "").ToList("\n")
					.Where(header => header.IndexOf(":") > 0)
					.ToDictionary(header => header.Left(header.IndexOf(":")).Trim(), header => header.Right(header.Length - header.IndexOf(":") - 1).Trim(), StringComparer.OrdinalIgnoreCase);
	}
}