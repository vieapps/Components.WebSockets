#region Related components
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Net.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using net.vieapps.Components.Utility;
using net.vieapps.Components.WebSockets.Exceptions;
#endregion

namespace net.vieapps.Components.WebSockets.Implementation
{
	internal static class WebSocketHelper
	{
		const string WEBSOCKET_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
		const int WEBSOCKET_VERSION = 13;

		static int _BufferLength = 16 * 1024;

		/// <summary>
		/// Gets the length of receiving buffer of all WebSocket connections
		/// </summary>
		public static int BufferLength { get { return WebSocketHelper._BufferLength; } }

		/// <summary>
		/// Sets the length of receiving buffer of all WebSocket connections
		/// </summary>
		/// <param name="length"></param>
		public static void SetBufferLength(int length = 16384)
		{
			if (length >= 1024)
				WebSocketHelper._BufferLength = length;
		}

		/// <summary>
		/// Gets a factory to get recyclable memory stream with  RecyclableMemoryStreamManager class to limit LOH fragmentation and improve performance
		/// </summary>
		/// <returns></returns>
		public static Func<MemoryStream> GetRecyclableMemoryStreamFactory()
		{
			return new Microsoft.IO.RecyclableMemoryStreamManager(16 * 1024, 4, 128 * 1024).GetStream;
		}

		/// <summary>
		/// Computes a WebSocket accept string from a given key
		/// </summary>
		/// <param name="secWebSocketKey">The web socket key to base the accept string on</param>
		/// <returns>A web socket accept string</returns>
		public static string ComputeSocketAcceptString(string secWebSocketKey)
		{
			return (secWebSocketKey + WebSocketHelper.WEBSOCKET_GUID).GetSHA1(true);
		}

		/// <summary>
		/// Decodes the header to detect is this is a web socket upgrade response
		/// </summary>
		/// <param name="header">The HTTP header</param>
		/// <returns>True if this is an http WebSocket upgrade response</returns>
		public static bool IsWebSocketUpgradeRequest(string header)
		{
			return new Regex(@"^GET(.*)HTTP\/1\.1", RegexOptions.IgnoreCase).Match(header).Success
				? new Regex("Upgrade: websocket", RegexOptions.IgnoreCase).Match(header).Success
				: false;
		}

		/// <summary>
		/// Gets the path from the HTTP header
		/// </summary>
		/// <param name="header">The HTTP header to read</param>
		/// <returns>The path</returns>
		public static string GetPathFromHeader(string header)
		{
			var match = new Regex(@"^GET(.*)HTTP\/1\.1", RegexOptions.IgnoreCase).Match(header);
			return match.Success
				? match.Groups[1].Value.Trim()
				: null;
		}

		/// <summary>
		/// Reads the HTTP response code from the http response string
		/// </summary>
		/// <param name="response">The response string</param>
		/// <returns>the response code</returns>
		public static string ReadHttpResponseCode(string response)
		{
			var match = new Regex(@"HTTP\/1\.1 (.*)", RegexOptions.IgnoreCase).Match(response);
			return match.Success
				? match.Groups[1].Value.Trim()
				: null;
		}

		/// <summary>
		/// Writes an HTTP header to the stream
		/// </summary>
		/// <param name="header">The header (without the new line characters)</param>
		/// <param name="stream">The stream to write to</param>
		/// <param name="cancellationToken">The cancellation token</param>
		public static async Task WriteHttpHeaderAsync(string header, Stream stream, CancellationToken cancellationToken = default(CancellationToken))
		{
			var bytes = (header.Trim() + "\r\n\r\n").ToBytes();
			await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Reads an HTTP header as per the HTTP specification
		/// </summary>
		/// <param name="stream">The stream to read text from</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns>The HTTP header</returns>
		public static async Task<string> ReadHttpHeaderAsync(Stream stream, CancellationToken cancellationToken = default(CancellationToken))
		{
			var buffer = new byte[WebSocketHelper.BufferLength];
			var offset = 0;
			var read = 0;

			do
			{
				if (offset >= WebSocketHelper.BufferLength)
					throw new EntityTooLargeException("HTTP header message too large to fit in buffer");

				read = await stream.ReadAsync(buffer, offset, WebSocketHelper.BufferLength - offset, cancellationToken).ConfigureAwait(false);
				offset += read;
				var header = buffer.GetString(offset);

				// as per HTTP specification, all headers should end like this
				if (header.Contains("\r\n\r\n"))
					return header;
			}
			while (read > 0);

			return string.Empty;
		}

		/// <summary>
		/// Reads a http header information from a stream and decodes the parts relating to the WebSocket protocol upgrade
		/// </summary>
		/// <param name="stream">The network stream</param>
		/// <param name="cancellationToken">The optional cancellation token</param>
		/// <returns>Http data read from the stream</returns>
		public static async Task<WebSocketContext> ReadHttpHeaderFromStreamAsync(Stream stream, CancellationToken cancellationToken = default(CancellationToken))
		{
			var header = await WebSocketHelper.ReadHttpHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
			return new WebSocketContext(WebSocketHelper.IsWebSocketUpgradeRequest(header), header, WebSocketHelper.GetPathFromHeader(header), stream);
		}

		/// <summary>
		/// Accept web socket with options specified
		/// </summary>
		/// <param name="context">The http context used to initiate this web socket request</param>
		/// <param name="options">The web socket options</param>
		/// <param name="cancellationToken">The optional cancellation token</param>
		/// <returns>A connected web socket</returns>
		public static async Task<WebSocket> AcceptAsync(WebSocketContext context, Func<MemoryStream> recycledStreamFactory, WebSocketOptions options, CancellationToken cancellationToken = default(CancellationToken))
		{
			// handshake
			var guid = Guid.NewGuid();
			Events.Log.AcceptWebSocketStarted(guid);
			try
			{
				// check the version (support version 13 and above)
				var match = new Regex("Sec-WebSocket-Version: (.*)").Match(context.HttpHeader);
				if (match.Success)
				{
					var secWebSocketVersion = match.Groups[1].Value.Trim().CastAs<int>();
					if (secWebSocketVersion < WEBSOCKET_VERSION)
						throw new VersionNotSupportedException($"WebSocket Version {secWebSocketVersion} not suported. Must be {WEBSOCKET_VERSION} or above.");
				}
				else
					throw new VersionNotSupportedException("Cannot find \"Sec-WebSocket-Version\" in HTTP header");

				// handshake
				match = new Regex("Sec-WebSocket-Key: (.*)").Match(context.HttpHeader);
				if (match.Success)
				{
					var secWebSocketKey = match.Groups[1].Value.Trim();
					var handshake =
						"HTTP/1.1 101 Switching Protocols\r\n" +
						"Connection: Upgrade\r\n" +
						"Upgrade: websocket\r\n" +
						"Server: VIEApps NGX WebSockets\r\n" +
						"Sec-WebSocket-Accept: " + WebSocketHelper.ComputeSocketAcceptString(secWebSocketKey) + "\r\n";
					options.AdditionalHttpHeaders?.ForEach(kvp => handshake += $"{kvp.Key}: {kvp.Value}\r\n");

					Events.Log.SendingHandshake(guid, handshake);
					await WebSocketHelper.WriteHttpHeaderAsync(handshake, context.Stream, cancellationToken).ConfigureAwait(false);
					Events.Log.HandshakeSent(guid, handshake);
				}
				else
					throw new KeyMissingException("Unable to read \"Sec-WebSocket-Key\" from HTTP header");
			}
			catch (VersionNotSupportedException ex)
			{
				Events.Log.WebSocketVersionNotSupported(guid, ex.ToString());
				var response = $"HTTP/1.1 426 Upgrade Required\r\nSec-WebSocket-Version: {WEBSOCKET_VERSION}\r\nException: {ex.Message}";
				await WebSocketHelper.WriteHttpHeaderAsync(response, context.Stream, cancellationToken).ConfigureAwait(false);
				throw;
			}
			catch (Exception ex)
			{
				Events.Log.BadRequest(guid, ex.ToString());
				await WebSocketHelper.WriteHttpHeaderAsync("HTTP/1.1 400 Bad Request", context.Stream, cancellationToken).ConfigureAwait(false);
				throw;
			}
			Events.Log.ServerHandshakeSuccess(guid);

			// create new instance
			return new WebSocketImplementation(guid, false, recycledStreamFactory, context.Stream, options.KeepAliveInterval, null, options.IncludeExceptionInCloseResponse);
		}

		/// <summary>
		/// Connect web socket with options specified
		/// </summary>
		/// <param name="uri">The WebSocket uri to connect to (e.g. ws://example.com or wss://example.com for SSL)</param>
		/// <param name="options">The WebSocket client options</param>
		/// <param name="recycledStreamFactory">Used to get a recyclable memory stream. This can be used with the RecyclableMemoryStreamManager class</param>
		/// <param name="cancellationToken">The optional cancellation token</param>
		/// <returns>A connected web socket instance</returns>
		public static async Task<WebSocket> ConnectAsync(Uri uri, WebSocketOptions options, Func<MemoryStream> recycledStreamFactory, CancellationToken cancellationToken = default(CancellationToken))
		{
			// connect the TCP client
			var guid = Guid.NewGuid();
			var tcpClient = new TcpClient()
			{
				NoDelay = options.NoDelay
			};

			if (IPAddress.TryParse(uri.Host, out IPAddress ipAddress))
			{
				Events.Log.ClientConnectingToIPAddress(guid, ipAddress.ToString(), uri.Port);
				await tcpClient.ConnectAsync(ipAddress, uri.Port).WithCancellationToken(cancellationToken).ConfigureAwait(false);
			}
			else
			{
				Events.Log.ClientConnectingToHost(guid, uri.Host, uri.Port);
				await tcpClient.ConnectAsync(uri.Host, uri.Port).WithCancellationToken(cancellationToken).ConfigureAwait(false);
			}

			// get the connected stream
			Stream stream = null;
			if (uri.Scheme.IsEquals("wss") || uri.Scheme.IsEquals("https"))
			{
				stream = new SslStream(
					tcpClient.GetStream(),
					false,
					(sender, certificate, chain, sslPolicyErrors) =>
					{
						// valid certificate
						if (sslPolicyErrors == SslPolicyErrors.None)
							return true;

						// do not allow this client to communicate with unauthenticated servers
						Events.Log.SslCertificateError(sslPolicyErrors);
						return false;
					},
					null
				);
				Events.Log.AttemptingToSecureSslConnection(guid);

				// will throw an AuthenticationException if the certificate is not valid
				await (stream as SslStream).AuthenticateAsClientAsync(uri.Host).WithCancellationToken(cancellationToken).ConfigureAwait(false);
				Events.Log.ConnectionSecured(guid);
			}
			else
			{
				Events.Log.ConnectionNotSecured(guid);
				stream = tcpClient.GetStream();
			}

			// send handshake
			var secWebSocketKey = CryptoService.GenerateRandomKey(16).ToBase64();
			var handshake =
				$"GET {uri.PathAndQuery} HTTP/1.1\r\n" +
				$"Host: {uri.Host}:{uri.Port}\r\n" +
				$"Origin: {uri.Scheme.Replace("ws", "http")}://{uri.Host}:{uri.Port}\r\n" +
				$"Connection: Upgrade\r\n" +
				$"Upgrade: websocket\r\n" +
				$"Client: VIEApps NGX WebSockets\r\n" +
				$"Sec-WebSocket-Key: {secWebSocketKey}\r\n" +
				$"Sec-WebSocket-Version: {WEBSOCKET_VERSION}\r\n";
			options.AdditionalHttpHeaders?.ForEach(kvp => handshake += $"{kvp.Key}: {kvp.Value}\r\n");

			Events.Log.SendingHandshake(guid, handshake);
			await WebSocketHelper.WriteHttpHeaderAsync(handshake, stream, cancellationToken).ConfigureAwait(false);
			Events.Log.HandshakeSent(guid, handshake);

			// read response
			Events.Log.ReadingHttpResponse(guid);
			var response = string.Empty;
			try
			{
				response = await WebSocketHelper.ReadHttpHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Events.Log.ReadHttpResponseError(guid, ex.ToString());
				throw new HandshakeFailedException("Handshake unexpected failure", ex);
			}

			// throw if got invalid response code
			var responseCode = WebSocketHelper.ReadHttpResponseCode(response);
			if (!"101 Switching Protocols".IsEquals(responseCode))
			{
				var lines = response.Split(new string[] { "\r\n" }, StringSplitOptions.None);
				for (var index = 0; index < lines.Length; index++)
				{
					// if there is more to the message than just the header
					if (string.IsNullOrWhiteSpace(lines[index]))
					{
						var builder = new StringBuilder();
						for (var idx = index + 1; idx < lines.Length - 1; idx++)
							builder.AppendLine(lines[idx]);

						var responseDetails = builder.ToString();
						throw new InvalidHttpResponseCodeException(responseCode, responseDetails, response);
					}
				}
			}

			// make sure we escape the accept string which could contain special regex characters
			var match = new Regex("Sec-WebSocket-Accept: (.*)").Match(response);
			var actualAcceptKey = match.Success
				? match.Groups[1].Value.Trim()
				: null;

			// check the accept string
			var expectedAcceptKey = WebSocketHelper.ComputeSocketAcceptString(secWebSocketKey);
			if (!expectedAcceptKey.IsEquals(actualAcceptKey))
			{
				var warning = $"Handshake failed because the accept key from the server \"{actualAcceptKey}\" was not the expected \"{expectedAcceptKey}\"";
				Events.Log.HandshakeFailure(guid, warning);
				throw new HandshakeFailedException(warning);
			}
			else
				Events.Log.ClientHandshakeSuccess(guid);

			// return the WebSocket connection
			return new WebSocketImplementation(guid, true, recycledStreamFactory, stream, options.KeepAliveInterval, options.SecWebSocketExtensions, options.IncludeExceptionInCloseResponse)
			{
				UriPath = $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.PathAndQuery}",
				LocalEndPoint = tcpClient.Client.LocalEndPoint,
				RemoteEndPoint = tcpClient.Client.RemoteEndPoint
			};
		}
	}
}