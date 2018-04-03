#region Related components
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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
        /// Initialises a new instance of the WebSocketClientFactory class without caring about internal buffers
        /// </summary>
        public WebSocketClientFactory()
        {
            this._recycledStreamFactory = WebSocketConnection.GetRecyclableMemoryStreamFactory();
        }

        /// <summary>
        /// Initialises a new instance of the WebSocketClientFactory class with control over internal buffer creation
        /// </summary>
        /// <param name="recycledStreamFactory">Used to get a recyclable memory stream. This can be used with the RecyclableMemoryStreamManager class</param>
        public WebSocketClientFactory(Func<MemoryStream> recycledStreamFactory)
        {
            this._recycledStreamFactory = recycledStreamFactory ?? WebSocketConnection.GetRecyclableMemoryStreamFactory();
        }

        /// <summary>
        /// Connect with default options
        /// </summary>
        /// <param name="uri">The WebSocket uri to connect to (e.g. ws://example.com or wss://example.com for SSL)</param>
        /// <param name="cancellationToken">The optional cancellation token</param>
        /// <returns>A connected web socket instance</returns>
        public async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            return await this.ConnectAsync(uri, new WebSocketClientOptions(), cancellationToken).ConfigureAwait(false);
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
            var guid = Guid.NewGuid();
			var host = uri.Host;
			var port = uri.Port;
			var tcpClient = new TcpClient()
			{
				NoDelay = options.NoDelay
			};
			var useSsl = uri.Scheme.IsEquals("wss") || uri.Scheme.IsEquals("https");
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

            cancellationToken.ThrowIfCancellationRequested();
            var stream = this.GetStream(guid, tcpClient, useSsl, host);
			return await this.PerformHandshakeAsync(guid, uri, stream, options, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Connect with a stream that has already been opened and HTTP websocket upgrade request sent
        /// This function will check the handshake response from the server and proceed if successful
        /// Use this function if you have specific requirements to open a conenction like using special http headers and cookies
        /// You will have to build your own HTTP websocket upgrade request
        /// You may not even choose to use TCP/IP and this function will allow you to do that
        /// </summary>
        /// <param name="responseStream">The full duplex response stream from the server</param>
        /// <param name="secWebSocketKey">The secWebSocketKey you used in the handshake request</param>
        /// <param name="options">The WebSocket client options</param>
        /// <param name="cancellationToken">The optional cancellation token</param>
        /// <returns></returns>
        public async Task<WebSocket> ConnectAsync(Stream responseStream, string secWebSocketKey, WebSocketClientOptions options, CancellationToken cancellationToken)
        {
            return await this.ConnectAsync(Guid.NewGuid(), responseStream, secWebSocketKey, options.KeepAliveInterval, options.SecWebSocketExtensions, options.IncludeExceptionInCloseResponse, cancellationToken).ConfigureAwait(false);
        }

        async Task<WebSocket> ConnectAsync(Guid guid, Stream responseStream, string secWebSocketKey, TimeSpan keepAliveInterval, string secWebSocketExtensions, bool includeExceptionInCloseResponse, CancellationToken cancellationToken)
        {
            Events.Log.ReadingHttpResponse(guid);
            var response = string.Empty;
            try
            {
                response = await HttpHelper.ReadHttpHeaderAsync(responseStream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Events.Log.ReadHttpResponseError(guid, ex.ToString());
                throw new WebSocketHandshakeFailedException("Handshake unexpected failure", ex);
            }

            this.ThrowIfInvalidResponseCode(response);
			this.ThrowIfInvalidAcceptString(guid, response, secWebSocketKey);
            return new WebSocketImplementation(guid, this._recycledStreamFactory, responseStream, keepAliveInterval, secWebSocketExtensions, includeExceptionInCloseResponse, isClient: true);
        }

        void ThrowIfInvalidAcceptString(Guid guid, string response, string secWebSocketKey)
        {
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
            {
                Events.Log.ClientHandshakeSuccess(guid);
            }
        }

        void ThrowIfInvalidResponseCode(string responseHeader)
        {
			var responseCode = HttpHelper.ReadHttpResponseCode(responseHeader);
            if (!string.Equals(responseCode, "101 Switching Protocols", StringComparison.InvariantCultureIgnoreCase))
            {
				var lines = responseHeader.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                for (var index = 0; index < lines.Length; index++)
                {
                    // if there is more to the message than just the header
                    if (string.IsNullOrWhiteSpace(lines[index]))
                    {
						var builder = new StringBuilder();
                        for (var idx = index + 1; idx < lines.Length - 1; idx++)
							builder.AppendLine(lines[idx]);

						var responseDetails = builder.ToString();
                        throw new InvalidHttpResponseCodeException(responseCode, responseDetails, responseHeader);
                    }
                }
            }
        }

        Stream GetStream(Guid guid, TcpClient tcpClient, bool isSecure, string host)
        {
            var stream = tcpClient.GetStream();
            if (isSecure)
            {
				var sslStream = new SslStream(stream, false, new RemoteCertificateValidationCallback(WebSocketClientFactory.ValidateServerCertificate), null);
                Events.Log.AttemtingToSecureSslConnection(guid);

                // This will throw an AuthenticationException if the certificate is not valid
                sslStream.AuthenticateAsClient(host);
                Events.Log.ConnectionSecured(guid);
                return sslStream;
            }
            else
            {
                Events.Log.ConnectionNotSecure(guid);
                return stream;
            }
        }

        /// <summary>
        /// Invoked by the RemoteCertificateValidationDelegate
        /// If you want to ignore certificate errors (for debugging) then return true
        /// </summary>
        static bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            if (sslPolicyErrors == SslPolicyErrors.None)
            {
                return true;
            }

            Events.Log.SslCertificateError(sslPolicyErrors);

            // Do not allow this client to communicate with unauthenticated servers.
            return false;
        }

        static string GetAdditionalHeaders(Dictionary<string, string> additionalHeaders)
        {
            if (additionalHeaders == null || additionalHeaders.Count == 0)
				return string.Empty;

			else
			{
                var builder = new StringBuilder();
                foreach(var kvp in additionalHeaders)
					builder.Append($"{kvp.Key}: {kvp.Value}\r\n");
				return builder.ToString();
            }
        }

        async Task<WebSocket> PerformHandshakeAsync(Guid guid, Uri uri, Stream stream, WebSocketClientOptions options, CancellationToken cancellationToken)
        {
            var rand = new Random();
			var keyAsBytes = new byte[16];
            rand.NextBytes(keyAsBytes);
			var secWebSocketKey = keyAsBytes.ToBase64();
			var additionalHeaders = WebSocketClientFactory.GetAdditionalHeaders(options.AdditionalHttpHeaders);
			var handshakeHttpRequest = $"GET {uri.PathAndQuery} HTTP/1.1\r\n" +
				$"Host: {uri.Host}:{uri.Port}\r\n" +
				"Upgrade: websocket\r\n" +
				"Connection: Upgrade\r\n" +
				$"Sec-WebSocket-Key: {secWebSocketKey}\r\n" +
				$"Origin: http://{uri.Host}:{uri.Port}\r\n" +
				additionalHeaders +
				"Sec-WebSocket-Version: 13\r\n\r\n";

			var httpRequest = Encoding.UTF8.GetBytes(handshakeHttpRequest);
            stream.Write(httpRequest, 0, httpRequest.Length);
            Events.Log.HandshakeSent(guid, handshakeHttpRequest);
            return await this.ConnectAsync(stream, secWebSocketKey, options, cancellationToken).ConfigureAwait(false);
        }
    }
}
