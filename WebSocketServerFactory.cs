#region Related components
using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Net.Security;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

using net.vieapps.Components.WebSockets.Exceptions;
using net.vieapps.Components.WebSockets.Internal;
#endregion

namespace net.vieapps.Components.WebSockets
{
    /// <summary>
    /// Web socket server factory used to open web socket server connections
    /// </summary>
    public class WebSocketServerFactory : IWebSocketServerFactory
    {
        Func<MemoryStream> _recycledStreamFactory;

        /// <summary>
        /// Initialises a new instance of the WebSocketClientFactory class
        /// </summary>
        /// <param name="recycledStreamFactory">Used to get a recyclable memory stream. 
        /// This can be used with the RecyclableMemoryStreamManager class to limit LOH fragmentation and improve performance
        /// </param>
        public WebSocketServerFactory(Func<MemoryStream> recycledStreamFactory = null)
        {
			this._recycledStreamFactory = recycledStreamFactory ?? WebSocketConnection.GetRecyclableMemoryStreamFactory();
		}

		/// <summary>
		/// Reads a http header information from a stream and decodes the parts relating to the WebSocket protocot upgrade
		/// </summary>
		/// <param name="stream">The network stream</param>
		/// <param name="cancellationToken">The optional cancellation token</param>
		/// <returns>Http data read from the stream</returns>
		public async Task<WebSocketHttpContext> ReadHttpHeaderFromStreamAsync(Stream stream, CancellationToken cancellationToken)
        {
            var header = await HttpHelper.ReadHttpHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
			var path = HttpHelper.GetPathFromHeader(header);
			var isWebSocketRequest = HttpHelper.IsWebSocketUpgradeRequest(header);
            return new WebSocketHttpContext(isWebSocketRequest, header, path, stream);
        }

        /// <summary>
        /// Accept web socket with default options
        /// Call ReadHttpHeaderFromStreamAsync first to get WebSocketHttpContext
        /// </summary>
        /// <param name="context">The http context used to initiate this web socket request</param>
        /// <param name="cancellationToken">The optional cancellation token</param>
        /// <returns>A connected web socket</returns>
        public async Task<WebSocket> AcceptWebSocketAsync(WebSocketHttpContext context, CancellationToken cancellationToken)
        {
            return await this.AcceptWebSocketAsync(context, new WebSocketServerOptions(), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Accept web socket with options specified
        /// Call ReadHttpHeaderFromStreamAsync first to get WebSocketHttpContext
        /// </summary>
        /// <param name="context">The http context used to initiate this web socket request</param>
        /// <param name="options">The web socket options</param>
        /// <param name="cancellationToken">The optional cancellation token</param>
        /// <returns>A connected web socket</returns>
        public async Task<WebSocket> AcceptWebSocketAsync(WebSocketHttpContext context, WebSocketServerOptions options, CancellationToken cancellationToken)
        {
            var guid = Guid.NewGuid();
            Events.Log.AcceptWebSocketStarted(guid);
            await WebSocketServerFactory.PerformHandshakeAsync(guid, context.HttpHeader, context.Stream, cancellationToken).ConfigureAwait(false);
            Events.Log.ServerHandshakeSuccess(guid);
            string secWebSocketExtensions = null;
            return new WebSocketImplementation(guid, this._recycledStreamFactory, context.Stream, options.KeepAliveInterval, secWebSocketExtensions, options.IncludeExceptionInCloseResponse,  isClient: false);
        }

        static void CheckWebSocketVersion(string httpHeader)
        {
			// check the version. Support version 13 and above
			const int WebSocketVersion = 13;
			var webSocketVersionRegex = new Regex("Sec-WebSocket-Version: (.*)");
			var match = webSocketVersionRegex.Match(httpHeader);
            if (match.Success)
            {
				var secWebSocketVersion = Convert.ToInt32(match.Groups[1].Value.Trim());
                if (secWebSocketVersion < WebSocketVersion)
					throw new WebSocketVersionNotSupportedException(string.Format("WebSocket Version {0} not suported. Must be {1} or above", secWebSocketVersion, WebSocketVersion));
			}
			else
				throw new WebSocketVersionNotSupportedException("Cannot find \"Sec-WebSocket-Version\" in HTTP header");
		}

		static async Task PerformHandshakeAsync(Guid guid, String httpHeader, Stream stream, CancellationToken cancellationToken)
        {
            try
            {
				var webSocketKeyRegex = new Regex("Sec-WebSocket-Key: (.*)");
				WebSocketServerFactory.CheckWebSocketVersion(httpHeader);

				var match = webSocketKeyRegex.Match(httpHeader);
                if (match.Success)
                {
					var secWebSocketKey = match.Groups[1].Value.Trim();
					var setWebSocketAccept = HttpHelper.ComputeSocketAcceptString(secWebSocketKey);
					var response = "HTTP/1.1 101 Switching Protocols\r\n"
						+ "Connection: Upgrade\r\n"
						+ "Upgrade: websocket\r\n"
						+ "Sec-WebSocket-Accept: " + setWebSocketAccept + "\r\n"
						+ "Server: VIEApps NGX";
                    Events.Log.SendingHandshakeResponse(guid, response);
                    await HttpHelper.WriteHttpHeaderAsync(response, stream, cancellationToken).ConfigureAwait(false);
                }
                else
					throw new SecWebSocketKeyMissingException("Unable to read \"Sec-WebSocket-Key\" from HTTP header");
			}
			catch (WebSocketVersionNotSupportedException ex)
            {
                Events.Log.WebSocketVersionNotSupported(guid, ex.ToString());
                var response = "HTTP/1.1 426 Upgrade Required\r\nSec-WebSocket-Version: 13" + ex.Message;
                await HttpHelper.WriteHttpHeaderAsync(response, stream, cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                Events.Log.BadRequest(guid, ex.ToString());
                await HttpHelper.WriteHttpHeaderAsync("HTTP/1.1 400 Bad Request", stream, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
	}
}