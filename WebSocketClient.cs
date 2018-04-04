#region Related components
using System;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

using Microsoft.Extensions.Logging;

using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Components.WebSockets
{
	public class WebSocketClient : IDisposable
	{

		#region Attributes
		WebSocketConnection _wsConnection;
		IWebSocketClientFactory _wsFactory;
		ILogger _logger;
		Uri _uri;
		int _awaitInterval = 0;
		CancellationTokenSource _cancellationTokenSource;
		bool _isDisposed = false, _isRunning = false;
		#endregion

		/// <summary>
		/// Gest the WebSocket connection that associates with this client
		/// </summary>
		public WebSocketConnection WebSocketConnection { get { return this._wsConnection; } }

		#region Event Handlers
		/// <summary>
		/// Fired when the client is started successfully
		/// </summary>
		public Action OnStartSuccess { get; set; } = () => { };

		/// <summary>
		/// Fired when the client is failed to start
		/// </summary>
		public Action<Exception> OnStartFailed { get; set; } = (ex) => { };

		/// <summary>
		/// Fired when the client got an error exception while processing/receiving
		/// </summary>
		public Action<Exception> OnError { get; set; } = (ex) => { };

		/// <summary>
		/// Fired when the connection is established
		/// </summary>
		public Action<WebSocketConnection> OnConnectionEstablished { get; set; } = (wsConnection) => { };

		/// <summary>
		/// Fired when the connection is broken
		/// </summary>
		public Action<WebSocketConnection> OnConnectionBroken { get; set; } = (wsConnection) => { };

		/// <summary>
		/// Fired when the client got a message
		/// </summary>
		public Action<WebSocketConnection, WebSocketReceiveResult, ArraySegment<byte>> OnMessageReceived { get; set; } = (wsConnection, wsReceiveResult, buffer) => { };
		#endregion

		/// <summary>
		/// Creates new instance of WebSocket Client
		/// </summary>
		/// <param name="location">The address of endpoint (ex: ws://localshost:56789/ or wss://example.com:443/)</param>
		/// <param name="awaitInterval">The awaiting interval while receiving messages (miniseconds)</param>
		/// <param name="loggerFactory">The logger factory</param>
		/// <param name="recycledStreamFactory">Used to get a recyclable memory stream (this can be used with the Microsoft.IO.RecyclableMemoryStreamManager class)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		public WebSocketClient(string location, int awaitInterval = 0, ILoggerFactory loggerFactory = null, Func<MemoryStream> recycledStreamFactory = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			Logger.AssignLoggerFactory(loggerFactory);
			this._uri = new Uri(location);
			this._awaitInterval = awaitInterval;
			this._wsFactory = new WebSocketClientFactory(recycledStreamFactory);
			this._logger = Logger.CreateLogger<WebSocketClient>();
			this._cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		}

		#region Start & Stop
		/// <summary>
		/// Starts this client
		/// </summary>
		/// <param name="onStartSuccess">Fired when the client is started successfully</param>
		/// <param name="onStartFailed">Fired when the client is failed to start</param>
		/// <param name="onError">Fired when the client got an error exception while processing/receiving</param>
		/// <param name="onConnectionEstablished">Fired when the connection is established</param>
		/// <param name="onConnectionBroken">Fired when the connection is broken</param>
		/// <param name="onMessageReceived">Fired when the client got a message</param>
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
				this._wsConnection = new WebSocketConnection()
				{
					WebSocket = await this._wsFactory.ConnectAsync(this._uri, this._cancellationTokenSource.Token).ConfigureAwait(false),
					Time = DateTime.Now,
					EndPoint = (IPAddress.TryParse(this._uri.Host, out IPAddress ipAddress) ? $"{ipAddress}" : $"{this._uri.Host}") + $":{this._uri.Port}"
				};

				this._isRunning = true;
				WebSocketConnectionManager.Add(this._wsConnection);

				// events
				try
				{
					this.OnStartSuccess?.Invoke();
				}
				catch (Exception uex)
				{
					this._logger.LogError(uex, $"(OnStartSuccess): {uex.Message}");
				}

				try
				{
					this.OnConnectionEstablished?.Invoke(this._wsConnection);
				}
				catch (Exception uex)
				{
					this._logger.LogError(uex, $"(OnConnectionEstablished): {uex.Message}");
				}

				if (this._logger.IsEnabled(LogLevel.Information))
					this._logger.LogInformation($"Connection is opened, the stream is ready ({this._wsConnection.ID} @ {this._wsConnection.EndPoint})");
			}
			catch (Exception ex)
			{
				var message = $"Error occurred while attemping connect to \"{this._uri}\"";
				this._logger.LogError(ex, message);

				// events
				try
				{
					this.OnStartFailed?.Invoke(new Exception(message, ex));
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

			// receive messages
			if (this._wsConnection != null)
				await this.ReceiveAsync(this._cancellationTokenSource.Token).ConfigureAwait(false);
		}

		/// <summary>
		/// Starts this client
		/// </summary>
		/// <param name="onStartSuccess">Fired when the client is started successfully</param>
		/// <param name="onStartFailed">Fired when the client is failed to start</param>
		/// <param name="onError">Fired when the client got an error exception while processing/receiving</param>
		/// <param name="onConnectionEstablished">Fired when the connection is established</param>
		/// <param name="onConnectionBroken">Fired when the connection is broken</param>
		/// <param name="onMessageReceived">Fired when the client got a message</param>
		public void Start(Action onStartSuccess = null, Action<Exception> onStartFailed = null, Action<Exception> onError = null, Action<WebSocketConnection> onConnectionEstablished = null, Action<WebSocketConnection> onConnectionBroken = null, Action<WebSocketConnection, WebSocketReceiveResult, ArraySegment<byte>> onMessageReceived = null)
		{
			Task.Run(async () =>
			{
				await this.StartAsync(onStartSuccess, onStartFailed, onError, onConnectionEstablished, onConnectionBroken, onMessageReceived).ConfigureAwait(false);
			}).ConfigureAwait(false);
		}

		/// <summary>
		/// Starts this client
		/// </summary>
		/// <param name="onMessageReceived">Fired when the client got a message</param>
		public void Start(Action<WebSocketConnection, WebSocketReceiveResult, ArraySegment<byte>> onMessageReceived)
		{
			this.Start(null, null, null, null, null, onMessageReceived);
		}

		/// <summary>
		/// Stops this client
		/// </summary>
		public void Stop()
		{
			this._cancellationTokenSource.Cancel();
			this._isRunning = false;
			WebSocketConnectionManager.Remove(this._wsConnection);
		}
		#endregion

		#region Send & Receive
		/// <summary>
		/// Sends a message to the connected endpoint
		/// </summary>
		/// <param name="buffer">The buffer containing data to send</param>
		/// <param name="messageType">The message type. Can be Text or Binary</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this._wsConnection.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
		}

		/// <summary>
		/// Sends a message to the connected endpoint
		/// </summary>
		/// <param name="message">The text message to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public Task SendAsync(string message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this._wsConnection.SendAsync(message, endOfMessage, cancellationToken);
		}

		/// <summary>
		/// Sends a message to the connected endpoint
		/// </summary>
		/// <param name="message">The binary message to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public Task SendAsync(byte[] message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this._wsConnection.SendAsync(message, endOfMessage, cancellationToken);
		}

		async Task ReceiveAsync(CancellationToken cancellationToken)
		{
			try
			{
				while (this._isRunning)
				{
					// receive the message
					cancellationToken.ThrowIfCancellationRequested();
					var buffer = new ArraySegment<byte>(new byte[WebSocketConnection.BufferLength]);
					var result = await this._wsConnection.WebSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

					// message to close
					if (result.MessageType == WebSocketMessageType.Close)
					{
						WebSocketConnectionManager.Remove(this._wsConnection);

						try
						{
							this.OnConnectionBroken?.Invoke(this._wsConnection);
						}
						catch (Exception uex)
						{
							this._logger.LogError(uex, $"(OnConnectionBroken): {uex.Message}");
						}

						if (this._logger.IsEnabled(LogLevel.Information))
							this._logger.LogInformation($"Server is initiated to close - Status: {result.CloseStatus} - Description: {result.CloseStatusDescription ?? "None"} ({this._wsConnection.ID} @ {this._wsConnection.EndPoint})");
						break;
					}

					// exceed buffer size
					if (result.Count > WebSocketConnection.BufferLength)
					{
						var message = $"WebSocket frame cannot exceed buffer size of {WebSocketConnection.BufferLength:#,##0} bytes";
						await this._wsConnection.WebSocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, $"{message}, send multiple frames instead.", cancellationToken).ConfigureAwait(false);
						WebSocketConnectionManager.Remove(this._wsConnection);

						try
						{
							this.OnConnectionBroken?.Invoke(this._wsConnection);
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
							this._logger.LogInformation($"Close the connection because {message} ({this._wsConnection.ID} @ {this._wsConnection.EndPoint})");
						break;
					}

					// got a message
					if (result.Count > 0)
					{
						if (this._logger.IsEnabled(LogLevel.Debug))
							this._logger.LogInformation($"Got a message - Type: {result.MessageType} - Length: {result.Count:#,##0} ({this._wsConnection.ID} @ {this._wsConnection.EndPoint})");

						try
						{
							this.OnMessageReceived?.Invoke(this._wsConnection, result, buffer);
						}
						catch (Exception uex)
						{
							this._logger.LogError(uex, $"(OnMessageReceived): {uex.Message}");
						}
					}

					// wait for next interval
					if (this._awaitInterval > 0)
						await Task.Delay(this._awaitInterval, cancellationToken).ConfigureAwait(false);
				}

				// done
				WebSocketConnectionManager.Remove(this._wsConnection);

				try
				{
					this.OnConnectionBroken?.Invoke(this._wsConnection);
				}
				catch (Exception uex)
				{
					this._logger.LogError(uex, $"(OnConnectionBroken): {uex.Message}");
				}

				if (this._logger.IsEnabled(LogLevel.Information))
					this._logger.LogInformation($"Connection is closed ({this._wsConnection.ID} @ {this._wsConnection.EndPoint})");
			}
			catch (ObjectDisposedException) { }
			catch (IOException) { }
			catch (OperationCanceledException)
			{
				await this._wsConnection.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, $"WebSocket Client close the connection", CancellationToken.None).ConfigureAwait(false);
				WebSocketConnectionManager.Remove(this._wsConnection);

				try
				{
					this.OnConnectionBroken?.Invoke(this._wsConnection);
				}
				catch (Exception uex)
				{
					this._logger.LogError(uex, $"(OnConnectionBroken): {uex.Message}");
				}

				if (this._logger.IsEnabled(LogLevel.Information))
					this._logger.LogInformation($"Connection is closed (cancellation) ({this._wsConnection.ID} @ {this._wsConnection.EndPoint})");
			}
			catch (Exception ex)
			{
				this._logger.LogError(ex, $"Unexpected error: {ex.Message}");
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
		#endregion

		#region Dispose
		public void Dispose()
		{
			if (!this._isDisposed)
			{
				this.Stop();
				this._isDisposed = true;
			}
		}

		~WebSocketClient()
		{
			this.Dispose();
			GC.SuppressFinalize(this);
		}
		#endregion

	}
}
