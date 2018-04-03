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

using Microsoft.IO;
using Microsoft.Extensions.Logging;

using net.vieapps.Components.Utility;
using net.vieapps.Components.WebSockets.Internal;
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
		/// Creates new instance of WebSocket Client
		/// </summary>
		/// <param name="location">The address of endpoint (ex: ws://localshost:8899/)</param>
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
		/// <param name="onSuccess">Fired when the client is started successfully</param>
		/// <param name="onFailed">Fired when the client is failed to start</param>
		/// <param name="onMessageReceived">Fired when got a message that sent from a server</param>
		/// <param name="onConnectionEstablished">Fired when the connection is established</param>
		/// <param name="onConnectionBroken">Fired when the connection is broken</param>
		/// <param name="onConnectionError">Fired when the connection got error</param>
		/// <returns></returns>
		public async Task StartAsync(Action onSuccess = null, Action<Exception> onFailed = null, Action<WebSocketConnection, WebSocketReceiveResult, ArraySegment<byte>> onMessageReceived = null, Action<WebSocketConnection> onConnectionEstablished = null, Action<WebSocketConnection> onConnectionBroken = null, Action<WebSocketConnection, Exception> onConnectionError = null)
		{
			// connect
			try
			{
				this._wsConnection = new WebSocketConnection()
				{
					WebSocket = await this._wsFactory.ConnectAsync(this._uri, this._cancellationTokenSource.Token).ConfigureAwait(false),
					Time = DateTime.Now,
					EndPoint = this._uri.AbsoluteUri
				};
				this._isRunning = true;
				WebSocketConnectionManager.Add(this._wsConnection);
				onSuccess?.Invoke();
				onConnectionEstablished?.Invoke(this._wsConnection);
				if (this._logger.IsEnabled(LogLevel.Information))
					this._logger.LogInformation($"Connection is opened, the stream is ready ({this._wsConnection.ID} @ {this._wsConnection.EndPoint})");
			}
			catch (Exception ex)
			{
				var message = $"Error occurred while attemping connect to \"{this._uri}\"";
				this._logger.LogError(ex, message);
				onFailed?.Invoke(new Exception(message, ex));
			}

			// receive messages
			if (this._wsConnection != null)
				await this.ReceiveAsync(onMessageReceived, onConnectionBroken, onConnectionError, this._cancellationTokenSource.Token).ConfigureAwait(false);
		}

		/// <summary>
		/// Starts this client
		/// </summary>
		/// <param name="onSuccess">Fired when the client is started successfully</param>
		/// <param name="onFailed">Fired when the client is failed to start</param>
		/// <param name="onMessageReceived">Fired when got a message that sent from a server</param>
		/// <param name="onConnectionEstablished">Fired when the connection is established</param>
		/// <param name="onConnectionBroken">Fired when the connection is broken</param>
		/// <param name="onConnectionError">Fired when the connection got an error</param>
		public void Start(Action onSuccess = null, Action<Exception> onFailed = null, Action<WebSocketConnection, WebSocketReceiveResult, ArraySegment<byte>> onMessageReceived = null, Action<WebSocketConnection> onConnectionEstablished = null, Action<WebSocketConnection> onConnectionBroken = null, Action<WebSocketConnection, Exception> onConnectionError = null)
		{
			Task.Run(async () =>
			{
				await this.StartAsync(onSuccess, onFailed, onMessageReceived, onConnectionEstablished, onConnectionBroken, onConnectionError).ConfigureAwait(false);
			}).ConfigureAwait(false);
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
		public async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			await this._wsConnection.WebSocket.SendAsync(buffer, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
		}

		async Task ReceiveAsync(Action<WebSocketConnection, WebSocketReceiveResult, ArraySegment<byte>> onMessageReceived, Action<WebSocketConnection> onConnectionBroken, Action<WebSocketConnection, Exception> onConnectionError, CancellationToken cancellationToken)
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
						onConnectionBroken?.Invoke(this._wsConnection);
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
						onConnectionBroken?.Invoke(this._wsConnection);
						onConnectionError?.Invoke(this._wsConnection, new InvalidOperationException(message));
						if (this._logger.IsEnabled(LogLevel.Information))
							this._logger.LogInformation($"Close the connection because {message} ({this._wsConnection.ID} @ {this._wsConnection.EndPoint})");
						break;
					}

					// got a message
					if (result.Count > 0)
					{
						if (this._logger.IsEnabled(LogLevel.Debug))
							this._logger.LogInformation($"Got a message - Type: {result.MessageType} - Length: {result.Count:#,##0} ({this._wsConnection.ID} @ {this._wsConnection.EndPoint})");
						onMessageReceived?.Invoke(this._wsConnection, result, buffer);
					}

					// wait for next interval
					if (this._awaitInterval > 0)
						await Task.Delay(this._awaitInterval, cancellationToken).ConfigureAwait(false);
				}

				// done
				WebSocketConnectionManager.Remove(this._wsConnection);
				onConnectionBroken?.Invoke(this._wsConnection);
				if (this._logger.IsEnabled(LogLevel.Information))
					this._logger.LogInformation($"Connection is closed ({this._wsConnection.ID} @ {this._wsConnection.EndPoint})");
			}
			catch (ObjectDisposedException) { }
			catch (IOException) { }
			catch (OperationCanceledException)
			{
				await this._wsConnection.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, $"WebSocket Client close the connection", CancellationToken.None).ConfigureAwait(false);
				WebSocketConnectionManager.Remove(this._wsConnection);
				if (this._logger.IsEnabled(LogLevel.Information))
					this._logger.LogInformation($"Connection is closed (cancellation) ({this._wsConnection.ID} @ {this._wsConnection.EndPoint})");
			}
			catch (Exception ex)
			{
				this._logger.LogError(ex, $"Unexpected error: {ex.Message}");
			}
		}
		#endregion

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
	}
}
