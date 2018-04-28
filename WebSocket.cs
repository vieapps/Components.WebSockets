#region Related components
using System;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using net.vieapps.Components.Utility;
using net.vieapps.Components.WebSockets.Implementation;
using net.vieapps.Components.WebSockets.Exceptions;
#endregion

namespace net.vieapps.Components.WebSockets
{
	/// <summary>
	/// The centralized WebSocket
	/// </summary>
	public class WebSocket : IDisposable
	{

		#region Properties
		ConcurrentDictionary<Guid, Implementation.WebSocket> _websockets = new ConcurrentDictionary<Guid, Implementation.WebSocket>();
		ILogger _logger = null;
		Func<MemoryStream> _recycledStreamFactory = null;
		TcpListener _tcpListener = null;
		bool _disposing = false, _disposed = false;
		CancellationTokenSource _processingCTS = null, _listeningCTS = null;

		/// <summary>
		/// Gets or sets await interval (miliseconds)
		/// </summary>
		public int AwaitInterval { get; set; } = 0;

		/// <summary>
		/// Gets or sets keep-alive interval (seconds) for sending ping messages
		/// </summary>
		public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(60);

		/// <summary>
		/// Gets the listening port of the listener
		/// </summary>
		public int Port { get; private set; } = 46429;

		/// <summary>
		/// Gets or sets the SSL certificate for securing connections
		/// </summary>
		public X509Certificate2 Certificate { get; set; } = null;

		/// <summary>
		/// Gets or sets the SSL protocol for securing connections with SSL Certificate
		/// </summary>
		public SslProtocols SslProtocol { get; set; } = SslProtocols.Tls;
		#endregion

		#region Event Handlers
		/// <summary>
		/// Action to fire when got an error while processing
		/// </summary>
		public Action<Implementation.WebSocket, Exception> OnError { get; set; }

		/// <summary>
		/// Action to fire when a connection is established
		/// </summary>
		public Action<Implementation.WebSocket> OnConnectionEstablished { get; set; }

		/// <summary>
		/// Action to fire when a connection is broken
		/// </summary>
		public Action<Implementation.WebSocket> OnConnectionBroken { get; set; }

		/// <summary>
		/// Action to fire when received a message
		/// </summary>
		public Action<Implementation.WebSocket, WebSocketReceiveResult, byte[]> OnMessageReceived { get; set; }
		#endregion

		/// <summary>
		/// Creates new an instance of the centralized <see cref="WebSocket">WebSocket</see>
		/// </summary>
		/// <param name="loggerFactory">The logger factory</param>
		/// <param name="recycledStreamFactory">Used to get a recyclable memory stream (this can be used with the Microsoft.IO.RecyclableMemoryStreamManager class)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		public WebSocket(ILoggerFactory loggerFactory = null, Func<MemoryStream> recycledStreamFactory = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			Logger.AssignLoggerFactory(loggerFactory);
			this._logger = Logger.CreateLogger<WebSocket>();
			this._recycledStreamFactory = recycledStreamFactory ?? WebSocketHelper.GetRecyclableMemoryStreamFactory();
			this._processingCTS = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		}

		#region Listen request as server
		/// <summary>
		/// Starts to listen for client requests as a <see cref="WebSocket">WebSocket</see> server
		/// </summary>
		/// <param name="port">The port for listening</param>
		/// <param name="certificate">The SSL Certificate to secure connections</param>
		/// <param name="onSuccess">Action to fire when start successful</param>
		/// <param name="onFailed">Action to fire when failed to start</param>
		public void StartListen(int port = 46429, X509Certificate2 certificate = null, Action onSuccess = null, Action<Exception> onFailed = null)
		{
			// check
			if (this._tcpListener != null)
			{
				onSuccess?.Invoke();
				return;
			}

			// listen
			try
			{
				// open the listener
				this.Port = port > 0 && port < 65535 ? port : 46429;
				this.Certificate = certificate ?? this.Certificate;

				this._tcpListener = new TcpListener(IPAddress.Any, this.Port);
				this._tcpListener.Start(1024);

				var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
					? "Linux"
					: RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
						? "Windows"
						: RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
							? "macOS"
							: $"VIEApps [{RuntimeInformation.OSDescription.Trim()}]";

				platform += $" ({RuntimeInformation.FrameworkDescription.Trim()}) - SSL: {this.Certificate != null}";
				if (this.Certificate != null)
					platform += $" ({this.Certificate.GetNameInfo(X509NameType.DnsName, false)} : Issued by {this.Certificate.GetNameInfo(X509NameType.DnsName, true)})";

				this._logger.LogInformation($"Listener is started - Listening port: {this.Port} - Platform: {platform}");
				onSuccess?.Invoke();

				// listen incomminng connection requests
				this.Listen();
			}
			catch (SocketException ex)
			{
				var message = $"Error occurred while listening on port \"{this.Port}\". Make sure another application is not running and consuming this port.";
				if (this._logger.IsEnabled(LogLevel.Debug))
					this._logger.LogError(ex, message);
				onFailed?.Invoke(new ListenerSocketException(message, ex));
			}
			catch (Exception ex)
			{
				if (this._logger.IsEnabled(LogLevel.Debug))
					this._logger.LogError(ex, $"Got an unexpected error while listening: {ex.Message}");
				onFailed?.Invoke(ex);
			}
		}

		/// <summary>
		/// Starts to listen for client requests as a <see cref="WebSocket">WebSocket</see> server
		/// </summary>
		/// <param name="port">The port for listening</param>
		/// <param name="onSuccess">Action to fire when start successful</param>
		/// <param name="onFailed">Action to fire when failed to start</param>
		public void StartListen(int port, Action onSuccess, Action<Exception> onFailed)
		{
			this.StartListen(port, null, onSuccess, onFailed);
		}

		/// <summary>
		/// Starts to listen for client requests as a <see cref="WebSocket">WebSocket</see> server
		/// </summary>
		/// <param name="port">The port for listening</param>
		public void StartListen(int port)
		{
			this.StartListen(port, null, null);
		}

		/// <summary>
		/// Stops listen
		/// </summary>
		/// <param name="doCancel">true to cancel the current listening process</param>
		public void StopListen(bool doCancel = true)
		{
			// cancel all pending connections
			if (doCancel)
				this._listeningCTS?.Cancel();

			// dispose
			try
			{
				this._tcpListener?.Server?.Close();
				this._tcpListener?.Stop();
			}
			catch (Exception ex)
			{
				if (this._logger.IsEnabled(LogLevel.Debug))
					this._logger.LogError(ex, $"Got an unexpected error when stop the listener: {ex.Message}");
			}
			finally
			{
				this._tcpListener = null;
			}
		}

		Task Listen()
		{
			this._listeningCTS = CancellationTokenSource.CreateLinkedTokenSource(this._processingCTS.Token);
			return this.ListenAsync();
		}

		async Task ListenAsync()
		{
			try
			{
				while (!this._listeningCTS.IsCancellationRequested)
				{
					var tcpClient = await this._tcpListener.AcceptTcpClientAsync().WithCancellationToken(this._listeningCTS.Token).ConfigureAwait(false);
					var accept = this.AcceptAsync(tcpClient);
				}
			}
			catch (Exception ex)
			{
				this.StopListen(false);
				if (ex is OperationCanceledException || ex is TaskCanceledException || ex is ObjectDisposedException || ex is SocketException || ex is IOException)
				{
					if (this._logger.IsEnabled(LogLevel.Debug))
						this._logger.LogInformation($"Listener is stoped ({ex.GetType().GetTypeName(true)})");
					else
						this._logger.LogInformation("Listener is stoped");
				}
				else
					this._logger.LogError(ex, $"Listener is stoped ({ex.Message})");
			}
		}

		async Task AcceptAsync(TcpClient tcpClient)
		{
			Implementation.WebSocket websocket = null;
			if (this._logger.IsEnabled(LogLevel.Trace))
				this._logger.LogInformation("The connection is opened, then reading HTTP header from the stream");

			try
			{
				// get stream
				Stream stream = null;
				if (this.Certificate != null)
					try
					{
						if (this._logger.IsEnabled(LogLevel.Trace))
							this._logger.LogInformation("Attempting to secure the connection...");

						stream = new SslStream(tcpClient.GetStream(), false);
						await (stream as SslStream).AuthenticateAsServerAsync(this.Certificate, false, this.SslProtocol, false).WithCancellationToken(this._listeningCTS.Token).ConfigureAwait(false);

						if (this._logger.IsEnabled(LogLevel.Trace))
							this._logger.LogInformation("The connection successfully secured");
					}
					catch (OperationCanceledException)
					{
						return;
					}
					catch (Exception ex)
					{
						if (ex is AuthenticationException)
							throw ex;
						else
							throw new AuthenticationException($"Cannot secure the connection: {ex.Message}", ex);
					}
				else
					stream = tcpClient.GetStream();

				// get context and verify WebSocket upgrade request
				var context = await WebSocketHelper.GetContextAsync(stream, this._listeningCTS.Token).ConfigureAwait(false);
				if (!context.IsWebSocketRequest)
				{
					if (this._logger.IsEnabled(LogLevel.Trace))
						this._logger.LogInformation("HTTP header contains no WebSocket upgrade request, then ignore");
					stream.Close();
					return;
				}

				// connect
				if (this._logger.IsEnabled(LogLevel.Trace))
					this._logger.LogInformation("HTTP header has requested an upgrade to WebSocket protocol, negotiating WebSocket handshake");

				websocket = await WebSocketHelper.AcceptAsync(context, this._recycledStreamFactory, new WebSocketOptions() { KeepAliveInterval = this.KeepAliveInterval }, this._listeningCTS.Token).ConfigureAwait(false);
				websocket.RequestUri = new Uri($"ws{(this.Certificate != null ? "s" : "")}://{context.Host}{context.Path}");
				websocket.LocalEndPoint = tcpClient.Client.LocalEndPoint;
				websocket.RemoteEndPoint = tcpClient.Client.RemoteEndPoint;

				if (this._logger.IsEnabled(LogLevel.Trace))
					this._logger.LogInformation($"WebSocket handshake response has been sent, the stream is ready ({websocket.ID} @ {websocket.RemoteEndPoint})");

				// receive messages
				this.Receive(websocket);

				// handling callback event
				this.OnConnectionEstablished?.Invoke(websocket);
				await this.AddWebSocketAsync(websocket).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				if (ex is OperationCanceledException || ex is TaskCanceledException || ex is ObjectDisposedException || ex is SocketException || ex is IOException)
				{
					if (this._logger.IsEnabled(LogLevel.Debug))
						this._logger.LogDebug(ex, $"Error occurred while accepting an incomming connection request: {ex.Message}");
				}
				else
				{
					if (this._logger.IsEnabled(LogLevel.Debug))
						this._logger.LogError(ex, $"Error occurred while accepting an incomming connection request: {ex.Message}");
					this.OnError?.Invoke(websocket, ex);
				}
			}
		}
		#endregion

		#region Connect to remote endpoints as client
		async Task ConnectAsync(Uri uri, Action<Implementation.WebSocket> onSuccess = null, Action<Exception> onFailed = null)
		{
			try
			{
				// connect
				if (this._logger.IsEnabled(LogLevel.Trace))
					this._logger.LogDebug($"Attempting to connect to \"{uri}\"...");

				var websocket = await WebSocketHelper.ConnectAsync(uri, new WebSocketOptions(), this._recycledStreamFactory, this._processingCTS.Token).ConfigureAwait(false);

				if (this._logger.IsEnabled(LogLevel.Trace))
					this._logger.LogDebug($"Endpoint is connected \"{uri}\" => {websocket.ID} @ {websocket.RemoteEndPoint}");

				// receive messages
				this.Receive(websocket);

				// handling callback event
				this.OnConnectionEstablished?.Invoke(websocket);
				onSuccess?.Invoke(websocket);
				await this.AddWebSocketAsync(websocket).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				if (this._logger.IsEnabled(LogLevel.Debug))
					this._logger.LogError(ex, $"Could not connect to \"{uri}\": {ex.Message}");
				onFailed?.Invoke(ex);
			}
		}

		/// <summary>
		/// Connects to a remote endpoint as a <see cref="WebSocket">WebSocket</see> client
		/// </summary>
		/// <param name="uri">The address of the remote endpoint to connect to</param>
		/// <param name="onSuccess">Action to fire when connect successful</param>
		/// <param name="onFailed">Action to fire when failed to connect</param>
		public void Connect(Uri uri, Action<Implementation.WebSocket> onSuccess = null, Action<Exception> onFailed = null)
		{
			Task.Run(() => this.ConnectAsync(uri, onSuccess, onFailed)).ConfigureAwait(false);
		}

		/// <summary>
		/// Connects to a remote endpoint as a <see cref="WebSocket">WebSocket</see> client
		/// </summary>
		/// <param name="location">The address of the remote endpoint to connect to</param>
		/// <param name="onSuccess">Action to fire when connect successful</param>
		/// <param name="onFailed">Action to fire when failed to connect</param>
		public void Connect(string location, Action<Implementation.WebSocket> onSuccess = null, Action<Exception> onFailed = null)
		{
			this.Connect(new Uri(location.Trim().ToLower()), onSuccess, onFailed);
		}
		#endregion

		#region Wrap a ASP.NET/ASP.NET Core WebSocket as server connection
		/// <summary>
		/// Wraps a <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection of ASP.NET / ASP.NET Core and acta like a <see cref="WebSocket">WebSocket</see> server
		/// </summary>
		/// <param name="webSocket">The <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection of ASP.NET / ASP.NET Core</param>
		/// <param name="requestUri">The request URI of the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection</param>
		/// <param name="localEndPoint">The local endpoint of the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection</param>
		/// <param name="remoteEndPoint">The remote endpoint of the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection</param>
		/// <param name="onSuccess">Action to fire when wrap successful</param>
		/// <param name="onFailed">Action to fire when failed to wrap</param>
		public void Wrap(System.Net.WebSockets.WebSocket webSocket, Uri requestUri, EndPoint localEndPoint = null, EndPoint remoteEndPoint = null, Action<Implementation.WebSocket> onSuccess = null, Action<Exception> onFailed = null)
		{
			try
			{
				// create new instance and receive messages
				var websocket = new WebSocketWrapper(webSocket, requestUri, localEndPoint, remoteEndPoint);
				this.Receive(websocket);

				// handling callback event
				this.OnConnectionEstablished?.Invoke(websocket);
				onSuccess?.Invoke(websocket);
				this.AddWebSocket(websocket);
			}
			catch (Exception ex)
			{
				if (this._logger.IsEnabled(LogLevel.Debug))
					this._logger.LogError(ex, $"Cannot wrap the WebSocket connection of ASP.NET / ASP.NET Core: {ex.Message}");
				onFailed?.Invoke(ex);
			}
		}
		#endregion

		#region Receive messages
		void Receive(Implementation.WebSocket websocket)
		{
			Task.Run(() => this.ReceiveAsync(websocket)).ConfigureAwait(false);
		}

		async Task ReceiveAsync(Implementation.WebSocket websocket)
		{
			var buffer = new ArraySegment<byte>(new byte[WebSocketHelper.BufferLength]);
			while (!this._processingCTS.IsCancellationRequested)
			{
				// check buffer
				if (!buffer.Array.Length.Equals(WebSocketHelper.BufferLength))
					buffer = new ArraySegment<byte>(new byte[WebSocketHelper.BufferLength]);

				// receive message from the WebSocket connection
				WebSocketReceiveResult result = null;
				try
				{
					result = await websocket.ReceiveAsync(buffer, this._processingCTS.Token).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					var closeStatus = WebSocketCloseStatus.InternalServerError;
					var closeStatusDescription = $"Got an unexpected error: {ex.Message}";
					if (ex is OperationCanceledException || ex is TaskCanceledException || ex is ObjectDisposedException || ex is SocketException || ex is IOException)
					{
						closeStatus = websocket.IsClient ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.EndpointUnavailable;
						closeStatusDescription = websocket.IsClient ? "Disconnected" : "Service is unavailable";
					}

					this.CloseWebSocket(websocket, closeStatus, closeStatusDescription);
					this.OnConnectionBroken?.Invoke(websocket);
					if (ex is OperationCanceledException || ex is TaskCanceledException || ex is ObjectDisposedException || ex is SocketException || ex is IOException)
					{
						if (this._logger.IsEnabled(LogLevel.Trace))
							this._logger.LogTrace(ex, $"Close the connection when got an error: {ex.Message}");
					}
					else
					{
						if (this._logger.IsEnabled(LogLevel.Debug))
							this._logger.LogError(ex, closeStatusDescription);
						this.OnError?.Invoke(websocket, ex);
					}
					return;
				}

				// message to close
				if (result.MessageType == WebSocketMessageType.Close)
				{
					if (this._logger.IsEnabled(LogLevel.Trace))
						this._logger.LogInformation($"The remote end-point is initiated to close - Status: {result.CloseStatus} - Description: {result.CloseStatusDescription ?? "N/A"} ({websocket.ID} @ {websocket.RemoteEndPoint})");
					this.CloseWebSocket(websocket);
					this.OnConnectionBroken?.Invoke(websocket);
					return;
				}

				// exceed buffer size
				if (result.Count > WebSocketHelper.BufferLength)
				{
					var message = $"WebSocket frame cannot exceed buffer size of {WebSocketHelper.BufferLength:#,##0} bytes";
					if (this._logger.IsEnabled(LogLevel.Debug))
						this._logger.LogInformation($"Close the connection because {message} ({websocket.ID} @ {websocket.RemoteEndPoint})");
					await websocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, $"{message}, send multiple frames instead.", CancellationToken.None).ConfigureAwait(false);
					this.CloseWebSocket(websocket, WebSocketCloseStatus.MessageTooBig, $"{message}, send multiple frames instead.");
					this.OnConnectionBroken?.Invoke(websocket);
					this.OnError?.Invoke(websocket, new BufferOverflowException(message));
					return;
				}

				// got a message
				if (result.Count > 0)
				{
					if (this._logger.IsEnabled(LogLevel.Trace))
						this._logger.LogTrace($"Got a message - Type: {result.MessageType} - Length: {result.Count:#,##0} ({websocket.ID} @ {websocket.RemoteEndPoint})");
					this.OnMessageReceived?.Invoke(websocket, result, buffer.Take(result.Count).ToArray());
				}

				// wait for next round
				if (this.AwaitInterval > 0)
					try
					{
						await Task.Delay(this.AwaitInterval, this._processingCTS.Token).ConfigureAwait(false);
					}
					catch
					{
						this.CloseWebSocket(websocket, websocket.IsClient ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.EndpointUnavailable, websocket.IsClient ? "Disconnected" : "Service is unavailable");
						this.OnConnectionBroken?.Invoke(websocket);
						return;
					}
			}
		}
		#endregion

		#region Send messages
		/// <summary>
		/// Sends the message to a <see cref="Implementation.WebSocket">WebSocket</see> connection
		/// </summary>
		/// <param name="id">The identity of a <see cref="Implementation.WebSocket">WebSocket</see> connection to send</param>
		/// <param name="buffer">The buffer containing message to send</param>
		/// <param name="messageType">The message type, can be Text or Binary</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public Task SendAsync(Guid id, ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this._websockets.TryGetValue(id, out Implementation.WebSocket websocket)
				? websocket.SendAsync(buffer, messageType, endOfMessage, cancellationToken)
				: Task.FromException(new InformationNotFoundException($"No WebSocket connection with identity \"{id}\" is found"));
		}

		/// <summary>
		/// Sends the message to a <see cref="Implementation.WebSocket">WebSocket</see> connection
		/// </summary>
		/// <param name="id">The identity of a <see cref="Implementation.WebSocket">WebSocket</see> connection to send</param>
		/// <param name="message">The text message to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public Task SendAsync(Guid id, string message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this._websockets.TryGetValue(id, out Implementation.WebSocket websocket)
				? websocket.SendAsync(message, endOfMessage, cancellationToken)
				: Task.FromException(new InformationNotFoundException($"No WebSocket connection with identity \"{id}\" is found"));
		}

		/// <summary>
		/// Sends the message to a <see cref="Implementation.WebSocket">WebSocket</see> connection
		/// </summary>
		/// <param name="id">The identity of a <see cref="Implementation.WebSocket">WebSocket</see> connection to send</param>
		/// <param name="message">The binary message to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public Task SendAsync(Guid id, byte[] message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this._websockets.TryGetValue(id, out Implementation.WebSocket websocket)
				? websocket.SendAsync(message, endOfMessage, cancellationToken)
				: Task.FromException(new InformationNotFoundException($"No WebSocket connection with identity \"{id}\" is found"));
		}

		/// <summary>
		/// Sends the message to the <see cref="Implementation.WebSocket">WebSocket</see> connections that matched with the predicate
		/// </summary>
		/// <param name="predicate">The predicate for selecting <see cref="Implementation.WebSocket">WebSocket</see> connections</param>
		/// <param name="buffer">The buffer containing message to send</param>
		/// <param name="messageType">The message type. Can be Text or Binary</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public Task SendAsync(Func<Implementation.WebSocket, bool> predicate, ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this.GetWebSockets(predicate).ToList().ForEachAsync((connection, token) => connection.SendAsync(buffer.Clone(), messageType, endOfMessage, token), cancellationToken);
		}

		/// <summary>
		/// Sends the message to the <see cref="Implementation.WebSocket">WebSocket</see> connections that matched with the predicate
		/// </summary>
		/// <param name="predicate">The predicate for selecting <see cref="Implementation.WebSocket">WebSocket</see> connections</param>
		/// <param name="message">The text message to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public Task SendAsync(Func<Implementation.WebSocket, bool> predicate, string message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this.SendAsync(predicate, message.ToArraySegment(), WebSocketMessageType.Text, endOfMessage, cancellationToken);
		}

		/// <summary>
		/// Sends the message to the <see cref="Implementation.WebSocket">WebSocket</see> connections that matched with the predicate
		/// </summary>
		/// <param name="predicate">The predicate for selecting <see cref="Implementation.WebSocket">WebSocket</see> connections</param>
		/// <param name="message">The binary message to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public Task SendAsync(Func<Implementation.WebSocket, bool> predicate, byte[] message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this.SendAsync(predicate, message.ToArraySegment(), WebSocketMessageType.Binary, endOfMessage, cancellationToken);
		}
		#endregion

		#region Connection management
		bool AddWebSocket(Implementation.WebSocket websocket)
		{
			return websocket != null
				? this._websockets.TryAdd(websocket.ID, websocket)
				: false;
		}

		async Task<bool> AddWebSocketAsync(Implementation.WebSocket websocket)
		{
			if (!this.AddWebSocket(websocket))
			{
				if (websocket != null)
					await Task.Delay(UtilityService.GetRandomNumber(123, 234)).ConfigureAwait(false);
				return this.AddWebSocket(websocket);
			}
			return true;
		}

		/// <summary>
		/// Gets a <see cref="Implementation.WebSocket">WebSocket</see> connection that specifed by identity
		/// </summary>
		/// <param name="id"></param>
		/// <returns></returns>
		public Implementation.WebSocket GetWebSocket(Guid id)
		{
			return this._websockets.TryGetValue(id, out Implementation.WebSocket websocket)
				? websocket
				: null;
		}

		/// <summary>
		/// Gets the collection of <see cref="Implementation.WebSocket">WebSocket</see> connections that matched with the predicate
		/// </summary>
		/// <param name="predicate">Predicate for selecting <see cref="Implementation.WebSocket">WebSocket</see> connections, if no predicate is provied then return all</param>
		/// <returns></returns>
		public IEnumerable<Implementation.WebSocket> GetWebSockets(Func<Implementation.WebSocket, bool> predicate = null)
		{
			return predicate != null
				? this._websockets.Values.Where(websocket => predicate(websocket))
				: this._websockets.Values;
		}

		/// <summary>
		/// Closes the <see cref="Implementation.WebSocket">WebSocket</see> connection and remove from the centralized collections
		/// </summary>
		/// <param name="id">The identity of a <see cref="Implementation.WebSocket">WebSocket</see> connection to close</param>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="closeStatusDescription">A description of why we are closing</param>
		/// <returns>true if closed and destroyed</returns>
		public bool CloseWebSocket(Guid id, WebSocketCloseStatus closeStatus = WebSocketCloseStatus.EndpointUnavailable, string closeStatusDescription = "Service is unavailable")
		{
			if (this._websockets.TryRemove(id, out Implementation.WebSocket websocket))
			{
				Task.Run(() => websocket.DisposeAsync(closeStatus, closeStatusDescription)).ConfigureAwait(false);
				return true;
			}
			return false;
		}

		/// <summary>
		/// Closes the <see cref="Implementation.WebSocket">WebSocket</see> connection and remove from the centralized collections
		/// </summary>
		/// <param name="websocket">The <see cref="Implementation.WebSocket">WebSocket</see> connection to close</param>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="closeStatusDescription">A description of why we are closing</param>
		/// <returns>true if closed and destroyed</returns>
		public bool CloseWebSocket(Implementation.WebSocket websocket, WebSocketCloseStatus closeStatus = WebSocketCloseStatus.EndpointUnavailable, string closeStatusDescription = "Service is unavailable")
		{
			return websocket != null
				? this.CloseWebSocket(websocket.ID, closeStatus, closeStatusDescription)
				: false;
		}
		#endregion

		#region Dispose
		public void Dispose()
		{
			// check state
			if (this._disposing || this._disposed)
				return;

			// update state
			this._disposing = true;

			// stop listener
			this.StopListen();

			// close all WebSocket connections
			using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
			{
				Task.WaitAll(this._websockets.Values.Select(websocket => websocket.DisposeAsync(WebSocketCloseStatus.NormalClosure, "Disconnected", cts.Token)).ToArray(), TimeSpan.FromSeconds(5));
				this._websockets.Clear();
			}

			// cancel all pending operations
			this._listeningCTS?.Dispose();
			this._processingCTS.Cancel();
			this._processingCTS.Dispose();

			// update state
			this._disposed = true;
			this._disposing = false;
		}

		~WebSocket()
		{
			this.Dispose();
			GC.SuppressFinalize(this);
		}
		#endregion

		/// <summary>
		/// Sets the length of the receiving buffer of all <see cref="Implementation.WebSocket">WebSocket</see> connections
		/// </summary>
		/// <param name="length">The buffer length (in bytes)</param>
		public static void SetBufferLength(int length = 16384)
		{
			WebSocketHelper.SetBufferLength(length);
		}
	}
}