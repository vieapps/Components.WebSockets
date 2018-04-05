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
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Components.WebSockets
{
	public class WebSocketServer : IDisposable
	{

		#region Attributes
		IWebSocketServerFactory _wsFactory;
		ILogger _logger;
		TcpListener _listener;
		int _port = 56789;
		TimeSpan _keepAliveInterval = TimeSpan.Zero;
		int _awaitInterval = 0;
		public CancellationTokenSource _cancellationTokenSource = null;
		bool _isDisposed = false, _isRunning = false;
		#endregion

		/// <summary>
		/// Sets the certificate for securing connections
		/// </summary>
		public X509Certificate2 Certificate { private get; set; } = null;

		/// <summary>
		/// Gets the connections of all current WebSocket clients that are connected with this server
		/// </summary>
		public List<WebSocketConnection> Connections
		{
			get
			{
				return WebSocketConnectionManager.Connections.Where(kvp => !kvp.Value.IsWebSocketClientConnection).Select(kvp => kvp.Value).ToList();
			}
		}

		#region Event Handlers
		/// <summary>
		/// Fired when the server is started successfully
		/// </summary>
		public Action OnStartSuccess { get; set; } = () => { };

		/// <summary>
		/// Fired when the server is failed to start
		/// </summary>
		public Action<Exception> OnStartFailed { get; set; } = (ex) => { };

		/// <summary>
		/// Fired when the server got any error exception while processing/receiving
		/// </summary>
		public Action<Exception> OnError { get; set; } = (ex) => { };

		/// <summary>
		/// Fired when a connection is established
		/// </summary>
		public Action<WebSocketConnection> OnConnectionEstablished { get; set; } = (wsConnection) => { };

		/// <summary>
		/// Fired when a connection is broken
		/// </summary>
		public Action<WebSocketConnection> OnConnectionBroken { get; set; } = (wsConnection) => { };

		/// <summary>
		/// Fired when a connection got a message that sent from a client
		/// </summary>
		public Action<WebSocketConnection, WebSocketReceiveResult, ArraySegment<byte>> OnMessageReceived { get; set; } = (wsConnection, wsReceiveResult, buffer) => { };
		#endregion

		/// <summary>
		/// Creates new instance of WebSocket Server
		/// </summary>
		/// <param name="port">Port for listening</param>
		/// <param name="keepAliveInterval">The Keep-Alice interval (in seconds)</param>
		/// <param name="awaitInterval">The awaiting interval while receiving messages (miniseconds)</param>
		/// <param name="loggerFactory">The logger factory</param>
		/// <param name="recycledStreamFactory">Used to get a recyclable memory stream (this can be used with the Microsoft.IO.RecyclableMemoryStreamManager class)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		public WebSocketServer(int port, TimeSpan keepAliveInterval, int awaitInterval = 0, ILoggerFactory loggerFactory = null, Func<MemoryStream> recycledStreamFactory = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			Logger.AssignLoggerFactory(loggerFactory);
			this._port = port > 0 && port < 65535 ? port : 56789;
			this._keepAliveInterval = keepAliveInterval;
			this._awaitInterval = awaitInterval;
			this._wsFactory = new WebSocketServerFactory(recycledStreamFactory);
			this._logger = Logger.CreateLogger<WebSocketServer>();
			this._cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		}

		#region Start & Stop
		/// <summary>
		/// Starts this server
		/// </summary>
		/// <param name="onStartSuccess">Fired when the server is started successfully</param>
		/// <param name="onStartFailed">Fired when the server is failed to start</param>
		/// <param name="onError">Fired when the server got an error</param>
		/// <param name="onConnectionEstablished">Fired when a connection is established</param>
		/// <param name="onConnectionBroken">Fired when a connection is broken</param>
		/// <param name="onMessageReceived">Fired when a connection got a message that sent from a client</param>
		/// <returns></returns>
		public async Task StartAsync(Action onStartSuccess = null, Action<Exception> onStartFailed = null, Action<Exception> onError = null, Action<WebSocketConnection> onConnectionEstablished = null, Action<WebSocketConnection> onConnectionBroken = null, Action<WebSocketConnection, WebSocketReceiveResult, ArraySegment<byte>> onMessageReceived = null)
		{
			// assign events
			this.OnStartSuccess = onStartSuccess ?? this.OnStartSuccess;
			this.OnStartFailed = onStartFailed ?? this.OnStartFailed;
			this.OnError = onError ?? this.OnError;
			this.OnConnectionEstablished = onConnectionBroken ?? this.OnConnectionEstablished;
			this.OnConnectionBroken = onConnectionBroken ?? this.OnConnectionBroken;
			this.OnMessageReceived = onMessageReceived ?? this.OnMessageReceived;

			// connect
			try
			{
				// open the listener
				this._listener = new TcpListener(IPAddress.Any, this._port);
				this._listener.Start();
				this._isRunning = true;

				// event
				try
				{
					this.OnStartSuccess?.Invoke();
				}
				catch (Exception uex)
				{
					this._logger.LogError(uex, $"(OnStartSuccess): {uex.Message}");
				}

				if (this._logger.IsEnabled(LogLevel.Information))
				{
					var runtimeInfo = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
						? "Linux"
						: RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
							? "Windows"
							: RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
								? "macOS"
								: $"GenericOS ({RuntimeInformation.OSDescription})";
					runtimeInfo += $" with {RuntimeInformation.FrameworkDescription} - Use secure connections: {this.Certificate != null}";
					this._logger.LogInformation($"Server is started - Listening port: {this._port}");
					this._logger.LogInformation($"Runtime platform: {runtimeInfo}");
				}
			}
			catch (SocketException ex)
			{
				var message = $"Error occurred while listening on port \"{this._port}\". Make sure another application is not running and consuming this port.";
				this._logger.LogError(ex, message);

				// events
				try
				{
					this.OnStartFailed?.Invoke(new Exception(message, ex));
				}
				catch (Exception uex)
				{
					this._logger.LogError(uex, $"(OnStartSuccess): {uex.Message}");
				}

				try
				{
					this.OnError?.Invoke(new Exception(message, ex));
				}
				catch (Exception uex)
				{
					this._logger.LogError(uex, $"(OnError): {uex.Message}");
				}
			}
			catch (Exception ex)
			{
				// logs
				this._logger.LogError(ex, $"Got an unexpected error when start server: {ex.Message}");

				// events
				try
				{
					this.OnStartFailed?.Invoke(ex);
				}
				catch (Exception uex)
				{
					this._logger.LogError(uex, $"(OnStartFailed): {uex.Message}");
				}

				try
				{
					this.OnError?.Invoke(ex);
				}
				catch (Exception uex)
				{
					this._logger.LogError(uex, $"(OnError): {uex.Message}");
				}
			}

			// process requests
			while (this._listener != null && this._isRunning)
			{
				TcpClient tcpClient = null;
				try
				{
					tcpClient = await this._listener.AcceptTcpClientAsync().ConfigureAwait(false);
				}
				catch (ObjectDisposedException) { }
				catch (InvalidOperationException) { }
				catch (IOException) { }
				catch (Exception ex)
				{
					this._logger.LogError(ex, $"Got an unexpected error while listening: {ex.Message}");

					try
					{
						this.OnError?.Invoke(ex);
					}
					catch (Exception uex)
					{
						this._logger.LogError(uex, $"(OnError): {uex.Message}");
					}
				}

				if (tcpClient != null)
				{
					var handler = Task.Run(async () =>
					{
						await this.ProcessRequestAsync(tcpClient).ConfigureAwait(false);
					}).ConfigureAwait(false);
				}
			}

			if (this._logger.IsEnabled(LogLevel.Information))
				this._logger.LogInformation("Server is stoped");
		}

		/// <summary>
		/// Starts this server
		/// </summary>
		/// <param name="onStartSuccess">Fired when the server is started successfully</param>
		/// <param name="onStartFailed">Fired when the server is failed to start</param>
		/// <param name="onError">Fired when the server got an error</param>
		/// <param name="onConnectionEstablished">Fired when a connection is established</param>
		/// <param name="onConnectionBroken">Fired when a connection is broken</param>
		/// <param name="onMessageReceived">Fired when a connection got a message that sent from a client</param>
		public void Start(Action onStartSuccess = null, Action<Exception> onStartFailed = null, Action<Exception> onError = null, Action<WebSocketConnection> onConnectionEstablished = null, Action<WebSocketConnection> onConnectionBroken = null, Action<WebSocketConnection, WebSocketReceiveResult, ArraySegment<byte>> onMessageReceived = null)
		{
			Task.Run(async () =>
			{
				await this.StartAsync(onStartSuccess, onStartFailed, onError, onConnectionEstablished, onConnectionBroken, onMessageReceived).ConfigureAwait(false);
			}).ConfigureAwait(false);
		}

		/// <summary>
		/// Starts this server
		/// </summary>
		/// <param name="onMessageReceived">Fired when a connection got a message that sent from a client</param>
		public void Start(Action<WebSocketConnection, WebSocketReceiveResult, ArraySegment<byte>> onMessageReceived)
		{
			this.Start(null, null, null, null, null, onMessageReceived);
		}

		async Task ProcessRequestAsync(TcpClient tcpClient)
		{
			if (this._logger.IsEnabled(LogLevel.Information))
				this._logger.LogInformation("Connection is opened, then reading HTTP header from the stream");

			WebSocketConnection wsConnection = null;
			try
			{
				// get the stream
				var stream = tcpClient.GetStream() as Stream;
				if (this.Certificate != null)
					try
					{
						if (this._logger.IsEnabled(LogLevel.Information))
							this._logger.LogInformation("Attempting to secure connection...");

						var sslStream = new SslStream(stream, false);
						sslStream.AuthenticateAsServer(this.Certificate, false, SslProtocols.Tls, true);
						stream = sslStream as Stream;

						if (this._logger.IsEnabled(LogLevel.Information))
							this._logger.LogInformation("Connection successfully secured");
					}
					catch (Exception ex)
					{
						throw new AuthenticationException($"Cannot secure the connection with current certificate: {ex.Message}", ex);
					}

				// connect
				var context = await this._wsFactory.ReadHttpHeaderFromStreamAsync(stream, this._cancellationTokenSource.Token).ConfigureAwait(false);
				if (!context.IsWebSocketRequest)
				{
					if (this._logger.IsEnabled(LogLevel.Information))
						this._logger.LogInformation("HTTP header contains no WebSocket upgrade request, then close the connection");
					return;
				}

				if (this._logger.IsEnabled(LogLevel.Information))
					this._logger.LogInformation("HTTP header has requested an upgrade to WebSocket protocol, negotiating WebSocket handshake");

				wsConnection = new WebSocketConnection()
				{
					WebSocket = await this._wsFactory.AcceptWebSocketAsync(context, new WebSocketServerOptions() { KeepAliveInterval = this._keepAliveInterval }, this._cancellationTokenSource.Token).ConfigureAwait(false),
					IsSecureWebSocketConnection = this.Certificate != null,
					Time = DateTime.Now,
					EndPoint = $"{tcpClient.Client.RemoteEndPoint}"
				};
				WebSocketConnectionManager.Add(wsConnection);

				// event
				try
				{
					this.OnConnectionEstablished?.Invoke(wsConnection);
				}
				catch (Exception uex)
				{
					this._logger.LogError(uex, $"(OnConnectionEstablished): {uex.Message}");
				}

				if (this._logger.IsEnabled(LogLevel.Information))
				{
					this._logger.LogInformation($"WebSocket handshake response has been sent, the stream is ready ({wsConnection.ID} @ {wsConnection.EndPoint})");
					this._logger.LogInformation($"Total {WebSocketConnectionManager.Connections.Count:#,##0} open connection(s)");
				}

				// process messages
				var @continue = true;
				while (@continue)
				{
					@continue = await this.ReceiveAsync(wsConnection).ConfigureAwait(false);
					if (@continue && this._awaitInterval > 0)
						await Task.Delay(this._awaitInterval, this._cancellationTokenSource.Token).ConfigureAwait(false);
				}

				// no more
				if (this._logger.IsEnabled(LogLevel.Information))
					this._logger.LogInformation($"Connection is closed ({wsConnection.ID} @ {wsConnection.EndPoint})");
			}
			catch (IOException) { }
			catch (ObjectDisposedException) { }
			catch (OperationCanceledException)
			{
				await wsConnection.WebSocket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, $"Service is unavailable", CancellationToken.None).ConfigureAwait(false);
				WebSocketConnectionManager.Remove(wsConnection);

				try
				{
					this.OnConnectionBroken?.Invoke(wsConnection);
				}
				catch (Exception uex)
				{
					this._logger.LogError(uex, $"(OnConnectionBroken): {uex.Message}");
				}

				if (this._logger.IsEnabled(LogLevel.Information))
					this._logger.LogInformation($"Connection is closed (cancellation) ({wsConnection?.ID} @ {wsConnection?.EndPoint})");
			}
			catch (Exception ex)
			{
				if (wsConnection != null && wsConnection != null && wsConnection.WebSocket.State == WebSocketState.Open)
				{
					await wsConnection.WebSocket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, $"Service is unavailable", CancellationToken.None).ConfigureAwait(false);
					WebSocketConnectionManager.Remove(wsConnection);

					try
					{
						this.OnConnectionBroken?.Invoke(wsConnection);
					}
					catch (Exception uex)
					{
						this._logger.LogError(uex, $"(OnConnectionBroken): {uex.Message}");
					}
				}

				try
				{
					this.OnError?.Invoke(ex);
				}
				catch (Exception uex)
				{
					this._logger.LogError(uex, $"(OnError): {uex.Message}");
				}
			}
			finally
			{
				wsConnection?.Dispose();
				try
				{
					tcpClient?.Client.Close();
					tcpClient?.Close();
				}
				catch (Exception ex)
				{
					this._logger.LogError(ex, $"Failed to close the TCP connection: {ex.Message}");
					try
					{
						this.OnError?.Invoke(ex);
					}
					catch (Exception uex)
					{
						this._logger.LogError(uex, $"(OnError): {uex.Message}");
					}
				}
			}
		}

		/// <summary>
		/// Stops this server
		/// </summary>
		public void Stop()
		{
			// close all connections
			var connections = this.Connections;
			Task.Run(async () =>
			{
				await connections.ForEachAsync((connection, token) => connection.WebSocket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, $"Service is unavailable", token)).ConfigureAwait(false);
			}).ConfigureAwait(false);

			// call to cancel all pending processes
			this._cancellationTokenSource.Cancel();

			// remove all connections that are connected to this server
			connections.ForEach(connection => WebSocketConnectionManager.Remove(connection));

			// safely attempt to shut down the listener
			if (this._listener != null)
				try
				{
					this._listener.Server?.Close();
					this._listener.Stop();
				}
				catch (Exception ex)
				{
					this._logger.LogError(ex, $"Error occurred while disposing listener: {ex.Message}");
				}

			// update state
			this._isRunning = false;
		}
		#endregion

		#region Send & Receive
		/// <summary>
		/// Sends the message to a WebSocket connection that is connected with this server
		/// </summary>
		/// <param name="id">The identity of a WebSocket connection to send</param>
		/// <param name="buffer">The buffer containing data to send</param>
		/// <param name="messageType">The message type. Can be Text or Binary</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public Task SendAsync(Guid id, ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return WebSocketConnectionManager.SendAsync(id, buffer, messageType, endOfMessage, cancellationToken);
		}

		/// <summary>
		/// Sends the message to a WebSocket connection that is connected with this server
		/// </summary>
		/// <param name="id">The identity of a WebSocket connection to send</param>
		/// <param name="message">The text message to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public Task SendAsync(Guid id, string message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return WebSocketConnectionManager.SendAsync(id, message, endOfMessage, cancellationToken);
		}

		/// <summary>
		/// Sends the message to a WebSocket connection that is connected with this server
		/// </summary>
		/// <param name="id">The identity of a WebSocket connection to send</param>
		/// <param name="message">The binary message to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public Task SendAsync(Guid id, byte[] message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return WebSocketConnectionManager.SendAsync(id, message, endOfMessage, cancellationToken);
		}

		/// <summary>
		/// Sends the message to all WebSocket connections that are connected with this server
		/// </summary>
		/// <param name="buffer">The buffer containing data to send</param>
		/// <param name="messageType">The message type. Can be Text or Binary</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public Task SendAllAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return WebSocketConnectionManager.SendAsync(connection => !connection.IsWebSocketClientConnection, buffer, messageType, endOfMessage, cancellationToken);
		}

		async Task<bool> ReceiveAsync(WebSocketConnection wsConnection)
		{
			// receive the message
			this._cancellationTokenSource.Token.ThrowIfCancellationRequested();
			var buffer = new ArraySegment<byte>(new byte[WebSocketConnection.BufferLength]);
			var result = await wsConnection.WebSocket.ReceiveAsync(buffer, this._cancellationTokenSource.Token).ConfigureAwait(false);
			this._cancellationTokenSource.Token.ThrowIfCancellationRequested();

			// message to close
			if (result.MessageType == WebSocketMessageType.Close)
			{
				WebSocketConnectionManager.Remove(wsConnection);

				try
				{
					this.OnConnectionBroken?.Invoke(wsConnection);
				}
				catch (Exception uex)
				{
					this._logger.LogError(uex, $"(OnConnectionBroken): {uex.Message}");
				}

				if (this._logger.IsEnabled(LogLevel.Information))
					this._logger.LogInformation($"Client is initiated to close - Status: {result.CloseStatus} - Description: {result.CloseStatusDescription ?? "None"} ({wsConnection.ID} @ {wsConnection.EndPoint})");

				return false;
			}

			// exceed buffer size
			if (result.Count > WebSocketConnection.BufferLength)
			{
				var message = $"WebSocket frame cannot exceed buffer size of {WebSocketConnection.BufferLength:#,##0} bytes";
				await wsConnection.WebSocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, $"{message}, send multiple frames instead.", this._cancellationTokenSource.Token).ConfigureAwait(false);
				WebSocketConnectionManager.Remove(wsConnection);

				try
				{
					this.OnConnectionBroken?.Invoke(wsConnection);
				}
				catch (Exception uex)
				{
					this._logger.LogError(uex, $"(OnConnectionBroken): {uex.Message}");
				}

				try
				{
					this.OnError?.Invoke(new InvalidOperationException(message));
				}
				catch (Exception uex)
				{
					this._logger.LogError(uex, $"(OnError): {uex.Message}");
				}

				if (this._logger.IsEnabled(LogLevel.Information))
					this._logger.LogInformation($"Close the connection because {message} ({wsConnection.ID} @ {wsConnection.EndPoint})");

				return false;
			}

			// got a message
			if (result.Count > 0)
			{
				try
				{
					this.OnMessageReceived?.Invoke(wsConnection, result, buffer);
				}
				catch (Exception uex)
				{
					this._logger.LogError(uex, $"(OnMessageReceived): {uex.Message}");
				}

				if (this._logger.IsEnabled(LogLevel.Debug))
					this._logger.LogInformation($"Got a message - Type: {result.MessageType} - Length: {result.Count:#,##0} ({wsConnection.ID} @ {wsConnection.EndPoint})");
			}

			// true to continue
			return true;
		}
		#endregion

		#region Dispose
		public void Dispose()
		{
			if (!this._isDisposed)
			{
				this.Stop();
				this._cancellationTokenSource.Dispose();
				this._isDisposed = true;
			}
		}

		~WebSocketServer()
		{
			this.Dispose();
			GC.SuppressFinalize(this);
		}
		#endregion

	}
}