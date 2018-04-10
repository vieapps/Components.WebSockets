#region Related components
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography.X509Certificates;

using net.vieapps.Components.Utility;

using net.vieapps.Components.WebSockets.Exceptions;
using net.vieapps.Components.WebSockets.Internal;
#endregion

namespace net.vieapps.Components.WebSockets
{
    /// <summary>
    /// Web socket client factory used to open web socket client connections
    /// </summary>
    public class WebSocketClientFactory : IWebSocketClientFactory
    {
        Func<MemoryStream> _recycledStreamFactory;

        /// <summary>
        /// Initialises a new instance of the WebSocketClientFactory class
        /// </summary>
        /// <param name="recycledStreamFactory">Used to get a recyclable memory stream. This can be used with the RecyclableMemoryStreamManager class</param>
        public WebSocketClientFactory(Func<MemoryStream> recycledStreamFactory = null)
        {
            this._recycledStreamFactory = recycledStreamFactory ?? WebSocketConnection.GetRecyclableMemoryStreamFactory();
		}

		/// <summary>
		/// Connect with default options
		/// </summary>
		/// <param name="uri">The WebSocket uri to connect to (e.g. ws://example.com or wss://example.com for SSL)</param>
		/// <param name="cancellationToken">The optional cancellation token</param>
		/// <returns>A connected web socket instance</returns>
		public Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            return this.ConnectAsync(uri, new WebSocketClientOptions(), cancellationToken);
        }

        /// <summary>
        /// Connect with options specified
        /// </summary>
        /// <param name="uri">The WebSocket uri to connect to (e.g. ws://example.com or wss://example.com for SSL)</param>
        /// <param name="options">The WebSocket client options</param>
        /// <param name="cancellationToken">The optional cancellation token</param>
        /// <returns>A connected web socket instance</returns>
        public async Task<WebSocket> ConnectAsync(Uri uri, WebSocketClientOptions options, CancellationToken cancellationToken)
        {
			// prepare
            var guid = Guid.NewGuid();
			var host = uri.Host;
			var port = uri.Port;
			var tcpClient = new TcpClient() { NoDelay = options.NoDelay };

			// connect the TCP client
			using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
			{
				using (cts.Token.Register(() => throw new OperationCanceledException(cts.Token), useSynchronizationContext: false))
				{
					if (IPAddress.TryParse(host, out IPAddress ipAddress))
					{
						Events.Log.ClientConnectingToIpAddress(guid, ipAddress.ToString(), port);
						await tcpClient.ConnectAsync(ipAddress, port).ConfigureAwait(false);
					}
					else
					{
						Events.Log.ClientConnectingToHost(guid, host, port);
						await tcpClient.ConnectAsync(host, port).ConfigureAwait(false);
					}
				}
			}

			// get the connected stream
			Stream stream = null;
			using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
			{
				using (cts.Token.Register(() => throw new OperationCanceledException(cts.Token), useSynchronizationContext: false))
				{
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
						Events.Log.AttemtingToSecureSslConnection(guid);

						// will throw an AuthenticationException if the certificate is not valid
						await (stream as SslStream).AuthenticateAsClientAsync(host).ConfigureAwait(false);
						Events.Log.ConnectionSecured(guid);
					}
					else
					{
						Events.Log.ConnectionNotSecure(guid);
						stream = tcpClient.GetStream();
					}
				}
			}

			// send handsake
			var secWebSocketKey = CryptoService.GenerateRandomKey(16).ToBase64();
			using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
			{
				using (cts.Token.Register(() => throw new OperationCanceledException(cts.Token), useSynchronizationContext: false))
				{
					var handshakeHttpRequest = $"GET {uri.PathAndQuery} HTTP/1.1\r\n" +
						$"Host: {uri.Host}:{uri.Port}\r\n" +
						"Upgrade: websocket\r\n" +
						"Connection: Upgrade\r\n" +
						$"Sec-WebSocket-Key: {secWebSocketKey}\r\n" +
						$"Origin: http://{uri.Host}:{uri.Port}\r\n" +
						"Sec-WebSocket-Version: 13";
					if (options.AdditionalHttpHeaders != null && options.AdditionalHttpHeaders.Count > 0)
						foreach (var kvp in options.AdditionalHttpHeaders)
							handshakeHttpRequest += "\r\n" + $"{kvp.Key}: {kvp.Value}";
					var httpRequest = (handshakeHttpRequest.Trim() + "\r\n\r\n").ToBytes();

					cancellationToken.ThrowIfCancellationRequested();
					stream.Write(httpRequest, 0, httpRequest.Length);
					Events.Log.HandshakeSent(guid, handshakeHttpRequest);
				}
			}

			// do connect
			return await this.ConnectAsync(guid, stream, secWebSocketKey, options.KeepAliveInterval, options.SecWebSocketExtensions, options.IncludeExceptionInCloseResponse, cancellationToken).ConfigureAwait(false);
		}

        async Task<WebSocket> ConnectAsync(Guid guid, Stream stream, string secWebSocketKey, TimeSpan keepAliveInterval, string secWebSocketExtensions, bool includeExceptionInCloseResponse, CancellationToken cancellationToken)
        {
			// read response
            Events.Log.ReadingHttpResponse(guid);
            var response = string.Empty;
            try
            {
                response = await HttpHelper.ReadHttpHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Events.Log.ReadHttpResponseError(guid, ex.ToString());
                throw new WebSocketHandshakeFailedException("Handshake unexpected failure", ex);
            }

			// throw if got invalid response code
			var responseCode = HttpHelper.ReadHttpResponseCode(response);
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
			var regexPattern = "Sec-WebSocket-Accept: (.*)";
			var regex = new Regex(regexPattern);
			var actualAcceptString = regex.Match(response).Groups[1].Value.Trim();

			// check the accept string
			var expectedAcceptString = HttpHelper.ComputeSocketAcceptString(secWebSocketKey);
			if (expectedAcceptString != actualAcceptString)
			{
				var warning = string.Format($"Handshake failed because the accept string from the server '{expectedAcceptString}' was not the expected string '{actualAcceptString}'");
				Events.Log.HandshakeFailure(guid, warning);
				throw new WebSocketHandshakeFailedException(warning);
			}
			else
				Events.Log.ClientHandshakeSuccess(guid);

			// return the connection
			return new WebSocketImplementation(guid, this._recycledStreamFactory, stream, keepAliveInterval, secWebSocketExtensions, includeExceptionInCloseResponse, isClient: true);
        }

		/// <summary>
		/// Connect with a stream that has already been opened and HTTP websocket upgrade request sent
		/// This function will check the handshake response from the server and proceed if successful
		/// Use this function if you have specific requirements to open a conenction like using special http headers and cookies
		/// You will have to build your own HTTP websocket upgrade request
		/// You may not even choose to use TCP/IP and this function will allow you to do that
		/// </summary>
		/// <param name="stream">The full duplex response stream from the server</param>
		/// <param name="secWebSocketKey">The secWebSocketKey you used in the handshake request</param>
		/// <param name="options">The WebSocket client options</param>
		/// <param name="cancellationToken">The optional cancellation token</param>
		/// <returns></returns>
		public Task<WebSocket> ConnectAsync(Stream stream, string secWebSocketKey, WebSocketClientOptions options, CancellationToken cancellationToken)
		{
			return this.ConnectAsync(Guid.NewGuid(), stream, secWebSocketKey, options.KeepAliveInterval, options.SecWebSocketExtensions, options.IncludeExceptionInCloseResponse, cancellationToken);
		}
	}
}