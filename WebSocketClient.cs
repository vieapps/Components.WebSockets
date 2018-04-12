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

using net.vieapps.Components.WebSockets.Internal;
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
		CancellationTokenSource _cancellationTokenSource;

		/// <summary>
		/// Gest the <see cref="WebSocketConnection">WebSocketConnection</see> object that associates with this client
		/// </summary>
		public WebSocketConnection WebSocketConnection { get { return this._wsConnection; } }
		#endregion

		#region Event Handlers
		/// <summary>
		/// Fired when the client is started successfully
		/// </summary>
		public Action OnStartSuccess { get; set; }

		/// <summary>
		/// Fired when the client is failed to start
		/// </summary>
		public Action<Exception> OnStartFailed { get; set; }

		/// <summary>
		/// Fired when the client got an error exception while processing/receiving
		/// </summary>
		public Action<Exception> OnError { get; set; }

		/// <summary>
		/// Fired when the connection is established
		/// </summary>
		public Action<WebSocketConnection> OnConnectionEstablished { get; set; }

		/// <summary>
		/// Fired when the connection is broken
		/// </summary>
		public Action<WebSocketConnection> OnConnectionBroken { get; set; }

		/// <summary>
		/// Fired when the client got a message
		/// </summary>
		public Action<WebSocketConnection, WebSocketReceiveResult, byte[]> OnMessageReceived { get; set; }
		#endregion

		/// <summary>
		/// Creates new instance of WebSocket Client
		/// </summary>
		/// <param name="location">The address of a endpoint to connection to (ex: ws://localshost:46429/ or wss://example.com:46429/)</param>
		/// <param name="loggerFactory">The logger factory</param>
		/// <param name="recycledStreamFactory">Used to get a recyclable memory stream (this can be used with the Microsoft.IO.RecyclableMemoryStreamManager class)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		public WebSocketClient(string location, ILoggerFactory loggerFactory = null, Func<MemoryStream> recycledStreamFactory = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			Fleck.Logger.AssignLoggerFactory(loggerFactory);
			WebSocketConnection.Logger = Fleck.Logger.CreateLogger<WebSocketConnection>();
			this._logger = Fleck.Logger.CreateLogger<WebSocketClient>();
			this._uri = new Uri(location);
			this._wsFactory = new WebSocketClientFactory(recycledStreamFactory);
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
		public void Start(Action onStartSuccess = null, Action<Exception> onStartFailed = null, Action<Exception> onError = null, Action<WebSocketConnection> onConnectionEstablished = null, Action<WebSocketConnection> onConnectionBroken = null, Action<WebSocketConnection, WebSocketReceiveResult, byte[]> onMessageReceived = null)
		{
			// assign event handlers
			this.OnStartSuccess = onStartSuccess ?? this.OnStartSuccess;
			this.OnStartFailed = onStartFailed ?? this.OnStartFailed;
			this.OnError = onError ?? this.OnError;
			this.OnConnectionEstablished = onConnectionBroken ?? this.OnConnectionEstablished;
			this.OnConnectionBroken = onConnectionBroken ?? this.OnConnectionBroken;
			this.OnMessageReceived = onMessageReceived ?? this.OnMessageReceived;

			// connect
			Task.Run(async () =>
			{
				await this.ConnectAsync().ConfigureAwait(false);
			}).ConfigureAwait(false);
		}

		/// <summary>
		/// Starts this client
		/// </summary>
		/// <param name="onMessageReceived">Fired when the client got a message</param>
		public void Start(Action<WebSocketConnection, WebSocketReceiveResult, byte[]> onMessageReceived)
		{
			this.Start(null, null, null, null, null, onMessageReceived);
		}

		/// <summary>
		/// Stops this client
		/// </summary>
		public void Stop()
		{
			// cancel all pending process
			this._cancellationTokenSource.Cancel();

			// close the connection
			WebSocketConnectionManager.Remove(this._wsConnection, WebSocketCloseStatus.NormalClosure, "Disconnected");
		}
		#endregion

		#region Connect to remote end-point and receive messages
		async Task ConnectAsync()
		{
			try
			{
				if (this._logger.IsEnabled(LogLevel.Trace))
					this._logger.LogInformation($"Attempting to connect to \"{this._uri}\"");

				this._wsConnection = new WebSocketConnection()
				{
					InnerSocket = await this._wsFactory.ConnectAsync(this._uri, this._cancellationTokenSource.Token).ConfigureAwait(false),
					IsSecureConnection = this._uri.Scheme.IsEquals("wss") || this._uri.Scheme.IsEquals("https"),
					IsClientConnection = true,
					Time = DateTime.Now,
					EndPoint = (IPAddress.TryParse(this._uri.Host, out IPAddress ipAddress) ? $"{ipAddress}" : $"{this._uri.Host}") + $":{this._uri.Port}",
					OnError = this.OnError,
					OnConnectionBroken = this.OnConnectionBroken,
					OnMessageReceived = this.OnMessageReceived
				};
				this._wsConnection.ID = (this._wsConnection.InnerSocket as WebSocketImplementation).ID;
				WebSocketConnectionManager.Add(this._wsConnection);

				this.OnStartSuccess?.Invoke();
				this.OnConnectionEstablished?.Invoke(this._wsConnection);
				if (this._logger.IsEnabled(LogLevel.Trace))
					this._logger.LogInformation($"Connection is opened ({this._wsConnection.ID} @ {this._wsConnection.EndPoint})");
			}
			catch (Exception ex)
			{
				if (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is OperationCanceledException)
				{
					this.OnStartFailed?.Invoke(ex);
					if (this._logger.IsEnabled(LogLevel.Debug))
						this._logger.LogError(ex, ex.Message);
				}
				else
				{
					var message = $"Error occurred while attempting to connect to \"{this._uri}\"";
					this.OnStartFailed?.Invoke(new Exception(message, ex));
					if (this._logger.IsEnabled(LogLevel.Debug))
						this._logger.LogError(ex, message);
				}
				return;
			}

			// receive messages
			await this._wsConnection.ReceiveAsync(this._cancellationTokenSource.Token).ConfigureAwait(false);
		}
		#endregion

		#region Send messages
		/// <summary>
		/// Sends a message to the connected endpoint
		/// </summary>
		/// <param name="buffer">The buffer containing data to send</param>
		/// <param name="messageType">The message type. Can be Text or Binary</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		public Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage = true)
		{
			return this._wsConnection.SendAsync(buffer, messageType, endOfMessage, this._cancellationTokenSource.Token);
		}

		/// <summary>
		/// Sends a message to the connected endpoint
		/// </summary>
		/// <param name="message">The text message to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		public Task SendAsync(string message, bool endOfMessage = true)
		{
			return this._wsConnection.SendAsync(message, endOfMessage, this._cancellationTokenSource.Token);
		}

		/// <summary>
		/// Sends a message to the connected endpoint
		/// </summary>
		/// <param name="message">The binary message to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		public Task SendAsync(byte[] message, bool endOfMessage = true)
		{
			return this._wsConnection.SendAsync(message, endOfMessage, this._cancellationTokenSource.Token);
		}
		#endregion

		#region Close connection
		/// <summary>
		/// Closes this connection
		/// </summary>
		/// <param name="closeStatus"></param>
		/// <param name="statusDescription"></param>
		/// <returns></returns>
		public Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription)
		{
			return this._wsConnection.CloseAsync(closeStatus, statusDescription, this._cancellationTokenSource.Token);
		}

		/// <summary>
		/// Fire and forget close
		/// </summary>
		/// <param name="closeStatus"></param>
		/// <param name="statusDescription"></param>
		/// <returns></returns>
		public Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription)
		{
			return this._wsConnection.CloseOutputAsync(closeStatus, statusDescription, this._cancellationTokenSource.Token);
		}
		#endregion

		#region Dispose
		public void Dispose()
		{
			this.Stop();
			this._cancellationTokenSource.Dispose();
		}

		~WebSocketClient()
		{
			this.Dispose();
			GC.SuppressFinalize(this);
		}
		#endregion

	}
}