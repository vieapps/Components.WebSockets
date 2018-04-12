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

using net.vieapps.Components.WebSockets.Internal;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Components.WebSockets
{
	public class WebSocketServer : IDisposable
	{

		#region Attributes
		IWebSocketServerFactory _wsFactory = null;
		Fleck.WebSocketServer _wsFleck = null;
		ILogger _logger = null;
		TcpListener _listener = null;
		int _port = 46429;
		TimeSpan _keepAliveInterval = TimeSpan.Zero;
		CancellationTokenSource _cancellationTokenSource = null;
		bool _disposed = false, _useFleck = true;
		#endregion

		#region Properties
		/// <summary>
		/// Sets the certificate for securing connections
		/// </summary>
		public X509Certificate2 Certificate { private get; set; } = null;

		/// <summary>
		/// Gets or Sets the maximum length of the pending connections queue
		/// </summary>
		public int Backlog { get; set; } = 1000;

		/// <summary>
		/// Gets the connections of all current WebSocket clients that are connected with this server
		/// </summary>
		public List<WebSocketConnection> Connections
		{
			get
			{
				return WebSocketConnectionManager.Connections.Values.Where(connection => !connection.IsClientConnection).ToList();
			}
		}
		#endregion

		#region Event Handlers
		/// <summary>
		/// Fired when the server is started successfully
		/// </summary>
		public Action OnStartSuccess { get; set; }

		/// <summary>
		/// Fired when the server is failed to start
		/// </summary>
		public Action<Exception> OnStartFailed { get; set; }

		/// <summary>
		/// Fired when the server got any error exception while processing/receiving
		/// </summary>
		public Action<Exception> OnError { get; set; }

		/// <summary>
		/// Fired when a connection is established
		/// </summary>
		public Action<WebSocketConnection> OnConnectionEstablished { get; set; }

		/// <summary>
		/// Fired when a connection is broken
		/// </summary>
		public Action<WebSocketConnection> OnConnectionBroken { get; set; }

		/// <summary>
		/// Fired when a connection got a message that sent from a client
		/// </summary>
		public Action<WebSocketConnection, WebSocketReceiveResult, byte[]> OnMessageReceived { get; set; }
		#endregion

		/// <summary>
		/// Creates new instance of WebSocket Server
		/// </summary>
		/// <param name="port">Port for listening</param>
		/// <param name="keepAliveInterval">The Keep-Alice interval (in seconds)</param>
		/// <param name="loggerFactory">The logger factory</param>
		/// <param name="useFleck">true to use Fleck WebSocket server instead of TcpWebSocket server</param>
		/// <param name="recycledStreamFactory">Used to get a recyclable memory stream (this can be used with the Microsoft.IO.RecyclableMemoryStreamManager class)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		public WebSocketServer(int port, TimeSpan keepAliveInterval, ILoggerFactory loggerFactory = null, bool useFleck = true, Func<MemoryStream> recycledStreamFactory = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			Fleck.Logger.AssignLoggerFactory(loggerFactory);
			WebSocketConnection.Logger = Fleck.Logger.CreateLogger<WebSocketConnection>();
			this._port = port > 0 && port < 65535 ? port : 46429;
			this._keepAliveInterval = keepAliveInterval;
			this._useFleck = useFleck;
			if (!useFleck)
				this._wsFactory = new WebSocketServerFactory(recycledStreamFactory);
			this._logger = Fleck.Logger.CreateLogger<WebSocketServer>();
			this._cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		}

		#region Start & Stop
		void ShowStartInfo()
		{
			var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
				? "Linux"
				: RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
					? "Windows"
					: RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
						? "macOS"
						: $"VIEApps [{RuntimeInformation.OSDescription.Trim()}]";

			platform += $" ({RuntimeInformation.FrameworkDescription.Trim()}) - SSL: {this.Certificate != null}";
			if (this.Certificate != null)
				platform += $" ({this.Certificate.GetNameInfo(X509NameType.DnsName, false)} by {this.Certificate.GetNameInfo(X509NameType.DnsName, true)})";

			this._logger.LogInformation($"WebSocket Server{(this._useFleck ? " (Fleck) " : " ")}is started - Listening port: {this._port} - Queue: {(this.Backlog > 0 && this.Backlog <= 5000 ? this.Backlog : 1000):#,##0} - Platform: {platform}");
		}

		void StartTcpWebSocketServer()
		{
			try
			{
				// start by open the listener
				this._listener = new TcpListener(IPAddress.Any, this._port);
				this._listener.Start(this.Backlog > 0 && this.Backlog <= 5000 ? this.Backlog : 1000);
				this.OnStartSuccess?.Invoke();

				// listen for incomming requests
				this.ShowStartInfo();
				this.ListenClientRequest();
			}
			catch (SocketException ex)
			{
				var message = $"Error occurred while listening on port \"{this._port}\". Make sure another application is not running and consuming this port.";
				this.OnStartFailed?.Invoke(new Exception(message, ex));
				if (this._logger.IsEnabled(LogLevel.Debug))
					this._logger.LogError(ex, message);
			}
			catch (Exception ex)
			{
				this.OnError?.Invoke(ex);
				if (this._logger.IsEnabled(LogLevel.Debug))
					this._logger.LogError(ex, $"Got an unexpected error when start server: {ex.Message}");
			}
		}

		void StartFleckWebSocketServer()
		{
			// initialize
			this._wsFleck = new Fleck.WebSocketServer($"ws{(this.Certificate != null ? "s" : "")}://{IPAddress.Any}:{this._port}/", Fleck.Logger.GetLoggerFactory());
			if (this.Certificate != null)
			{
				this._wsFleck.Certificate = this.Certificate;
				this._wsFleck.EnabledSslProtocols = SslProtocols.Tls;
			}

			// start
			try
			{
				this._wsFleck.Start(socket =>
				{
					socket.OnOpen = () =>
					{
						var wsConnection = new WebSocketConnection()
						{
							ID = socket.ConnectionInfo.Id,
							InnerSocket = socket,
							IsSecureConnection = this.Certificate != null,
							IsClientConnection = false,
							Time = DateTime.Now,
							EndPoint = $"{socket.ConnectionInfo.ClientIpAddress}:{socket.ConnectionInfo.ClientPort}"
						};
						WebSocketConnectionManager.Add(wsConnection);

						this.OnConnectionEstablished?.Invoke(wsConnection);
						if (this._logger.IsEnabled(LogLevel.Trace))
							this._logger.LogInformation($"WebSocket handshake response has been sent, the stream is ready ({wsConnection.ID} @ {wsConnection.EndPoint})");
					};

					socket.OnClose = () =>
					{
						var wsConnection = WebSocketConnectionManager.Get(socket.ConnectionInfo.Id);
						WebSocketConnectionManager.Remove(wsConnection);
						this.OnConnectionBroken?.Invoke(wsConnection);
					};

					socket.OnError = (ex) =>
					{
						if (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is OperationCanceledException)
						{
							var wsConnection = WebSocketConnectionManager.Get(socket.ConnectionInfo.Id);
							WebSocketConnectionManager.Remove(wsConnection);
							this.OnConnectionBroken?.Invoke(wsConnection);
							if (this._logger.IsEnabled(LogLevel.Trace))
								this._logger.LogInformation($"Disconnect when got an error ({wsConnection?.ID} @ {wsConnection?.EndPoint})");
						}
						else
						{
							this.OnError?.Invoke(ex);
							if (this._logger.IsEnabled(LogLevel.Debug))
								this._logger.LogError(ex, $"Got an unexpected error while processing: {ex.Message}");
						}
					};

					socket.OnMessage = (data) =>
					{
						var buffer = (data ?? "").ToBytes();
						this.OnMessageReceived?.Invoke(WebSocketConnectionManager.Get(socket.ConnectionInfo.Id), new WebSocketReceiveResult(buffer.Length, WebSocketMessageType.Text, true), buffer);
					};

					socket.OnBinary = (data) =>
					{
						this.OnMessageReceived?.Invoke(WebSocketConnectionManager.Get(socket.ConnectionInfo.Id), new WebSocketReceiveResult(data.Length, WebSocketMessageType.Binary, true), data);
					};
				}, this.Backlog > 0 && this.Backlog <= 5000 ? this.Backlog : 1000);

				this.OnStartSuccess?.Invoke();
				this.ShowStartInfo();
			}
			catch (SocketException ex)
			{
				var message = $"Error occurred while listening on port \"{this._port}\". Make sure another application is not running and consuming this port.";
				this.OnStartFailed?.Invoke(new Exception(message, ex));
				if (this._logger.IsEnabled(LogLevel.Debug))
					this._logger.LogError(ex, message);
			}
			catch (Exception ex)
			{
				this.OnError?.Invoke(ex);
				if (this._logger.IsEnabled(LogLevel.Debug))
					this._logger.LogError(ex, $"Got an unexpected error when start server: {ex.Message}");
			}
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
		public void Start(Action onStartSuccess = null, Action<Exception> onStartFailed = null, Action<Exception> onError = null, Action<WebSocketConnection> onConnectionEstablished = null, Action<WebSocketConnection> onConnectionBroken = null, Action<WebSocketConnection, WebSocketReceiveResult, byte[]> onMessageReceived = null)
		{
			// assign event handlers
			this.OnStartSuccess = onStartSuccess ?? this.OnStartSuccess;
			this.OnStartFailed = onStartFailed ?? this.OnStartFailed;
			this.OnError = onError ?? this.OnError;
			this.OnConnectionEstablished = onConnectionEstablished ?? this.OnConnectionEstablished;
			this.OnConnectionBroken = onConnectionBroken ?? this.OnConnectionBroken;
			this.OnMessageReceived = onMessageReceived ?? this.OnMessageReceived;

			// start the server
			if (this._useFleck)
				this.StartFleckWebSocketServer();
			else
				this.StartTcpWebSocketServer();
		}

		/// <summary>
		/// Starts this server
		/// </summary>
		/// <param name="onMessageReceived">Fired when a connection got a message that sent from a client</param>
		public void Start(Action<WebSocketConnection, WebSocketReceiveResult, byte[]> onMessageReceived)
		{
			this.Start(null, null, null, null, null, onMessageReceived);
		}

		/// <summary>
		/// Stops this server
		/// </summary>
		public void Stop()
		{
			// cancel all pending processes
			this._cancellationTokenSource.Cancel();

			// remove all connections that are connected to this server
			WebSocketConnectionManager.Remove(this.Connections);

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

			this._wsFleck?.Dispose();
		}
		#endregion

		#region Process incomming requests
		void ListenClientRequest()
		{
			if (!this._cancellationTokenSource.Token.IsCancellationRequested)
				Task.Run(async () =>
				{
					await this.ListenClientRequestAsync().ConfigureAwait(false);
				}).ConfigureAwait(false);
		}

		async Task ListenClientRequestAsync()
		{
			try
			{
				this._cancellationTokenSource.Token.ThrowIfCancellationRequested();
				using (var cts = CancellationTokenSource.CreateLinkedTokenSource(this._cancellationTokenSource.Token))
				{
					using (cts.Token.Register(() => throw new OperationCanceledException(cts.Token), useSynchronizationContext: false))
					{
						var client = await this._listener.AcceptTcpClientAsync().ConfigureAwait(false);
						this.AcceptClientRequest(client);
					}
				}
			}
			catch (Exception ex)
			{
				this.Stop();
				if (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is OperationCanceledException)
					this._logger.LogInformation("Server is stoped");
				else
					this._logger.LogError(ex, $"Server is stoped when got an unexpected error: {ex.Message}");
			}
		}

		void AcceptClientRequest(TcpClient client)
		{
			ListenClientRequest();
			Task.Run(async () =>
			{
				await this.AcceptClientRequestAsync(client).ConfigureAwait(false);
			}).ConfigureAwait(false);
		}

		async Task AcceptClientRequestAsync(TcpClient client)
		{
			WebSocketConnection wsConnection = null;
			if (this._logger.IsEnabled(LogLevel.Trace))
				this._logger.LogInformation("Connection is opened, then reading HTTP header from the stream");

			try
			{
				// get stream
				this._cancellationTokenSource.Token.ThrowIfCancellationRequested();
				Stream stream = null;
				if (this.Certificate != null)
					using (var cts = CancellationTokenSource.CreateLinkedTokenSource(this._cancellationTokenSource.Token))
					{
						using (cts.Token.Register(() => throw new OperationCanceledException(cts.Token), useSynchronizationContext: false))
						{
							try
							{
								if (this._logger.IsEnabled(LogLevel.Trace))
									this._logger.LogInformation("Attempting to secure connection...");

								stream = new SslStream(client.GetStream(), false);
								await (stream as SslStream).AuthenticateAsServerAsync(this.Certificate, false, SslProtocols.Tls, false).ConfigureAwait(false);

								if (this._logger.IsEnabled(LogLevel.Trace))
									this._logger.LogInformation("Connection successfully secured");
							}
							catch (Exception ex)
							{
								if (ex is AuthenticationException)
									throw ex;
								else
									throw new AuthenticationException($"Cannot secure the connection: {ex.Message}", ex);
							}
						}
					}
				else
					stream = client.GetStream();

				// connect
				var context = await this._wsFactory.ReadHttpHeaderFromStreamAsync(stream, this._cancellationTokenSource.Token).ConfigureAwait(false);
				if (!context.IsWebSocketRequest)
				{
					if (this._logger.IsEnabled(LogLevel.Trace))
						this._logger.LogInformation("HTTP header contains no WebSocket upgrade request, then close the connection");
					return;
				}

				if (this._logger.IsEnabled(LogLevel.Trace))
					this._logger.LogInformation("HTTP header has requested an upgrade to WebSocket protocol, negotiating WebSocket handshake");

				wsConnection = new WebSocketConnection()
				{
					InnerSocket = await this._wsFactory.AcceptWebSocketAsync(context, new WebSocketServerOptions() { KeepAliveInterval = this._keepAliveInterval }, this._cancellationTokenSource.Token).ConfigureAwait(false),
					IsSecureConnection = this.Certificate != null,
					IsClientConnection = false,
					Time = DateTime.Now,
					EndPoint = $"{client.Client.RemoteEndPoint}",
					OnError = this.OnError,
					OnConnectionBroken = this.OnConnectionBroken,
					OnMessageReceived = this.OnMessageReceived
				};
				wsConnection.ID = (wsConnection.InnerSocket as WebSocketImplementation).ID;
				WebSocketConnectionManager.Add(wsConnection);

				this.OnConnectionEstablished?.Invoke(wsConnection);
				if (this._logger.IsEnabled(LogLevel.Trace))
					this._logger.LogInformation($"WebSocket handshake response has been sent, the stream is ready ({wsConnection.ID} @ {wsConnection.EndPoint})");
			}
			catch (Exception ex)
			{
				if (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is OperationCanceledException)
				{
					// do nothing
				}
				else
				{
					this.OnError?.Invoke(ex);
					if (this._logger.IsEnabled(LogLevel.Debug))
						this._logger.LogError(ex, $"Error occurred while accepting request: {ex.Message}");
				}
				return;
			}

			// receive messages
			await wsConnection.ReceiveAsync(this._cancellationTokenSource.Token).ConfigureAwait(false);
		}
		#endregion

		#region Send messages
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
			return WebSocketConnectionManager.SendAsync(connection => !connection.IsClientConnection, buffer, messageType, endOfMessage, cancellationToken);
		}
		#endregion

		#region Dispose
		public void Dispose()
		{
			if (!this._disposed)
			{
				this.Stop();
				this._cancellationTokenSource.Dispose();
				this._wsFleck.Dispose();
				this._disposed = true;
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