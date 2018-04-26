﻿#region Related components
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
	public static class WebSocketHelper
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

		internal static async Task WithCancellationToken(this Task task, CancellationToken cancellationToken)
		{
			var tcs = new TaskCompletionSource<bool>();
			using (cancellationToken.Register(state => ((TaskCompletionSource<bool>)state).TrySetResult(true), tcs, false))
			{
				if (task != await Task.WhenAny(task, tcs.Task))
					throw new OperationCanceledException(cancellationToken);
			}
			await task;
		}

		internal static async Task<T> WithCancellationToken<T>(this Task<T> task, CancellationToken cancellationToken)
		{
			var tcs = new TaskCompletionSource<bool>();
			using (cancellationToken.Register(state => ((TaskCompletionSource<bool>)state).TrySetResult(true), tcs, false))
			{
				if (task != await Task.WhenAny(task, tcs.Task))
					throw new OperationCanceledException(cancellationToken);
			}
			return await task;
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
		/// <param name="httpHeader">The HTTP header to read</param>
		/// <returns>The path</returns>
		public static string GetPathFromHeader(string httpHeader)
		{
			var match = new Regex(@"^GET(.*)HTTP\/1\.1", RegexOptions.IgnoreCase).Match(httpHeader);
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

		/// <summary>
		/// Reads an HTTP header as per the HTTP specification
		/// </summary>
		/// <param name="stream">The stream to read UTF8 text from</param>
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

				// as per HTTP specification, all headers should end this
				if (header.Contains("\r\n\r\n"))
					return header;
			}
			while (read > 0);

			return string.Empty;
		}

		/// <summary>
		/// Reads a http header information from a stream and decodes the parts relating to the WebSocket protocot upgrade
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
		public static async Task<WebSocket> AcceptAsync(WebSocketContext context, Func<MemoryStream> recycledStreamFactory, WebSocketServerOptions options, CancellationToken cancellationToken = default(CancellationToken))
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
						throw new VersionNotSupportedException($"WebSocket Version {secWebSocketVersion} not suported. Must be {WEBSOCKET_VERSION} or above");
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
						"Sec-WebSocket-Accept: " + WebSocketHelper.ComputeSocketAcceptString(secWebSocketKey);
					Events.Log.SendingHandshakeResponse(guid, handshake);
					await WebSocketHelper.WriteHttpHeaderAsync(handshake, context.Stream, cancellationToken).ConfigureAwait(false);
					Events.Log.HandshakeSent(guid, handshake);
				}
				else
					throw new KeyMissingException("Unable to read \"Sec-WebSocket-Key\" from HTTP header");
			}
			catch (VersionNotSupportedException ex)
			{
				Events.Log.WebSocketVersionNotSupported(guid, ex.ToString());
				var response = "HTTP/1.1 426 Upgrade Required\r\nSec-WebSocket-Version: 13" + ex.Message;
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
			string secWebSocketExtensions = null;
			return new WebSocket(guid, recycledStreamFactory, context.Stream, options.KeepAliveInterval, secWebSocketExtensions, options.IncludeExceptionInCloseResponse);
		}

		/// <summary>
		/// Connect web socket with options specified
		/// </summary>
		/// <param name="guid"></param>
		/// <param name="recycledStreamFactory"></param>
		/// <param name="stream"></param>
		/// <param name="secWebSocketKey"></param>
		/// <param name="keepAliveInterval"></param>
		/// <param name="secWebSocketExtensions"></param>
		/// <param name="includeExceptionInCloseResponse"></param>
		/// <param name="localEndPoint"></param>
		/// <param name="remoteEndPoint"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static async Task<WebSocket> ConnectAsync(Guid guid, Func<MemoryStream> recycledStreamFactory, Stream stream, string secWebSocketKey, TimeSpan keepAliveInterval, string secWebSocketExtensions, bool includeExceptionInCloseResponse, EndPoint localEndPoint, EndPoint remoteEndPoint, CancellationToken cancellationToken = default(CancellationToken))
		{
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
			if (!string.Equals(responseCode, "101 Switching Protocols", StringComparison.InvariantCultureIgnoreCase))
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
			return new WebSocket(guid, recycledStreamFactory, stream, keepAliveInterval, secWebSocketExtensions, includeExceptionInCloseResponse)
			{
				IsClient = true,
				LocalEndPoint = localEndPoint,
				RemoteEndPoint = remoteEndPoint
			};
		}

		/// <summary>
		/// Connect web socket with options specified
		/// </summary>
		/// <param name="uri">The WebSocket uri to connect to (e.g. ws://example.com or wss://example.com for SSL)</param>
		/// <param name="options">The WebSocket client options</param>
		/// <param name="recycledStreamFactory">Used to get a recyclable memory stream. This can be used with the RecyclableMemoryStreamManager class</param>
		/// <param name="cancellationToken">The optional cancellation token</param>
		/// <returns>A connected web socket instance</returns>
		public static async Task<WebSocket> ConnectAsync(Uri uri, WebSocketClientOptions options, Func<MemoryStream> recycledStreamFactory, CancellationToken cancellationToken = default(CancellationToken))
		{
			// prepare
			var guid = Guid.NewGuid();
			var host = uri.Host;
			var port = uri.Port;
			var tcpClient = new TcpClient() { NoDelay = options.NoDelay };

			// connect the TCP client
			if (IPAddress.TryParse(host, out IPAddress ipAddress))
			{
				Events.Log.ClientConnectingToIPAddress(guid, ipAddress.ToString(), port);
				await tcpClient.ConnectAsync(ipAddress, port).WithCancellationToken(cancellationToken).ConfigureAwait(false);
			}
			else
			{
				Events.Log.ClientConnectingToHost(guid, host, port);
				await tcpClient.ConnectAsync(host, port).WithCancellationToken(cancellationToken).ConfigureAwait(false);
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
				await (stream as SslStream).AuthenticateAsClientAsync(host).WithCancellationToken(cancellationToken).ConfigureAwait(false);
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
				$"Origin: http://{uri.Host}:{uri.Port}\r\n" +
				$"Upgrade: websocket\r\n" +
				$"Connection: Upgrade\r\n" +
				$"Sec-WebSocket-Key: {secWebSocketKey}\r\n" +
				$"Sec-WebSocket-Version: {WEBSOCKET_VERSION}\r\n";
			if (options.AdditionalHttpHeaders != null && options.AdditionalHttpHeaders.Count > 0)
				foreach (var kvp in options.AdditionalHttpHeaders)
					handshake += $"{kvp.Key}: {kvp.Value}\r\n";

			await WebSocketHelper.WriteHttpHeaderAsync(handshake, stream, cancellationToken).ConfigureAwait(false);
			Events.Log.HandshakeSent(guid, handshake);

			// connect
			return await WebSocketHelper.ConnectAsync(guid, recycledStreamFactory, stream, secWebSocketKey, options.KeepAliveInterval, options.SecWebSocketExtensions, options.IncludeExceptionInCloseResponse, tcpClient.Client.LocalEndPoint, tcpClient.Client.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
		}
	}
}