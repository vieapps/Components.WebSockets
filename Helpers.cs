#region Related components
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

using net.vieapps.Components.Utility;
using net.vieapps.Components.WebSockets.Exceptions;
#endregion

namespace net.vieapps.Components.WebSockets
{
	public static class HttpHelper
	{
		const string _HTTP_GET_HEADER_REGEX = @"^GET(.*)HTTP\/1\.1";

		/// <summary>
		/// Calculates a random WebSocket key that can be used to initiate a WebSocket handshake
		/// </summary>
		/// <returns>A random websocket key</returns>
		public static string CalculateWebSocketKey()
		{
			// this is not used for cryptography so doing something simple like he code below is op
			var rand = new Random((int)DateTime.Now.Ticks);
			var keyAsBytes = new byte[16];
			rand.NextBytes(keyAsBytes);
			return keyAsBytes.ToBase64();
		}

		/// <summary>
		/// Computes a WebSocket accept string from a given key
		/// </summary>
		/// <param name="secWebSocketKey">The web socket key to base the accept string on</param>
		/// <returns>A web socket accept string</returns>
		public static string ComputeSocketAcceptString(string secWebSocketKey)
		{
			// this is a guid as per the web socket spec
			const string webSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
			return (secWebSocketKey + webSocketGuid).GetSHA1Hash().ToBase64();
		}

		/// <summary>
		/// Reads an HTTP header as per the HTTP specification
		/// </summary>
		/// <param name="stream">The stream to read UTF8 text from</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns>The HTTP header</returns>
		public static async Task<string> ReadHttpHeaderAsync(Stream stream, CancellationToken cancellationToken = default(CancellationToken))
		{
			var length = 1024 * 16; // 16KB buffer more than enough for HTTP header
			var buffer = new byte[length];
			var offset = 0;
			var read = 0;

			do
			{
				if (offset >= length)
					throw new EntityTooLargeException("Http header message too large to fit in buffer (16KB)");

				read = await stream.ReadAsync(buffer, offset, length - offset, cancellationToken).ConfigureAwait(false);
				offset += read;
				var header = Encoding.UTF8.GetString(buffer, 0, offset);

				// as per http specification, all headers should end this this
				if (header.Contains("\r\n\r\n"))
					return header;
			}
			while (read > 0);

			return string.Empty;
		}

		/// <summary>
		/// Decodes the header to detect is this is a web socket upgrade response
		/// </summary>
		/// <param name="header">The HTTP header</param>
		/// <returns>True if this is an http WebSocket upgrade response</returns>
		public static bool IsWebSocketUpgradeRequest(String header)
		{
			var getRegex = new Regex(_HTTP_GET_HEADER_REGEX, RegexOptions.IgnoreCase);
			var getRegexMatch = getRegex.Match(header);

			if (getRegexMatch.Success)
			{
				// check if this is a web socket upgrade request
				var webSocketUpgradeRegex = new Regex("Upgrade: websocket", RegexOptions.IgnoreCase);
				var webSocketUpgradeRegexMatch = webSocketUpgradeRegex.Match(header);
				return webSocketUpgradeRegexMatch.Success;
			}

			return false;
		}

		/// <summary>
		/// Gets the path from the HTTP header
		/// </summary>
		/// <param name="httpHeader">The HTTP header to read</param>
		/// <returns>The path</returns>
		public static string GetPathFromHeader(string httpHeader)
		{
			var getRegex = new Regex(_HTTP_GET_HEADER_REGEX, RegexOptions.IgnoreCase);
			var getRegexMatch = getRegex.Match(httpHeader);

			// extract the path attribute from the first line of the header
			if (getRegexMatch.Success)
				return getRegexMatch.Groups[1].Value.Trim();

			return null;
		}

		/// <summary>
		/// Reads the HTTP response code from the http response string
		/// </summary>
		/// <param name="response">The response string</param>
		/// <returns>the response code</returns>
		public static string ReadHttpResponseCode(string response)
		{
			var getRegex = new Regex(@"HTTP\/1\.1 (.*)", RegexOptions.IgnoreCase);
			var getRegexMatch = getRegex.Match(response);

			// extract the path attribute from the first line of the header
			if (getRegexMatch.Success)
				return getRegexMatch.Groups[1].Value.Trim();

			return null;
		}

		/// <summary>
		/// Writes an HTTP response string to the stream
		/// </summary>
		/// <param name="response">The response (without the new line characters)</param>
		/// <param name="stream">The stream to write to</param>
		/// <param name="cancellationToken">The cancellation token</param>
		public static async Task WriteHttpHeaderAsync(string response, Stream stream, CancellationToken cancellationToken = default(CancellationToken))
		{
			var bytes = (response.Trim() + "\r\n\r\n").ToBytes();
			await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
		}

		internal static ushort GetStatusCode(this WebSocketCloseStatus closeStatus)
		{
			switch (closeStatus)
			{
				case WebSocketCloseStatus.NormalClosure:
					return Fleck.WebSocketStatusCodes.NormalClosure;

				case WebSocketCloseStatus.ProtocolError:
					return Fleck.WebSocketStatusCodes.ProtocolError;

				case WebSocketCloseStatus.PolicyViolation:
					return Fleck.WebSocketStatusCodes.PolicyViolation;

				case WebSocketCloseStatus.MessageTooBig:
					return Fleck.WebSocketStatusCodes.MessageTooBig;

				case WebSocketCloseStatus.InvalidMessageType:
					return Fleck.WebSocketStatusCodes.UnsupportedDataType;

				case WebSocketCloseStatus.InvalidPayloadData:
					return Fleck.WebSocketStatusCodes.InvalidFramePayloadData;

				case WebSocketCloseStatus.MandatoryExtension:
					return Fleck.WebSocketStatusCodes.MandatoryExt;

				case WebSocketCloseStatus.InternalServerError:
					return Fleck.WebSocketStatusCodes.InternalServerError;

				default:
					return Fleck.WebSocketStatusCodes.GoingAway;
			}
		}
	}
}