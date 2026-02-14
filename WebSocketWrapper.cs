#region Related components
using System;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Components.WebSockets
{
	internal class WebSocketWrapper : ManagedWebSocket
	{

		#region Properties
		readonly System.Net.WebSockets.WebSocket _websocket = null;
		readonly ILogger _logger;
		readonly CancellationTokenSource _processingCTS;
		readonly Channel<(ArraySegment<byte> Buffer, WebSocketMessageType MessageType, bool EndOfMessage)> _messageQueue;
		readonly Task _messageWriter;

		/// <summary>
		/// Gets the state that indicates the reason why the remote endpoint initiated the close handshake
		/// </summary>
		public override WebSocketCloseStatus? CloseStatus => this._websocket.CloseStatus;

		/// <summary>
		/// Gets the description to describe the reason why the connection was closed
		/// </summary>
		public override string CloseStatusDescription => this._websocket.CloseStatusDescription;

		/// <summary>
		/// Gets the current state of the WebSocket connection
		/// </summary>
		public override WebSocketState State => this._websocket.State;

		/// <summary>
		/// Gets the subprotocol that was negotiated during the opening handshake
		/// </summary>
		public override string SubProtocol => this._websocket.SubProtocol;

		/// <summary>
		/// Gets the state to include the full exception (with stack trace) in the close response when an exception is encountered and the WebSocket connection is closed
		/// </summary>
		protected override bool IncludeExceptionInCloseResponse { get; } = false;
		#endregion

		public WebSocketWrapper(System.Net.WebSockets.WebSocket websocket, Uri requestUri, EndPoint remoteEndPoint, EndPoint localEndPoint, Dictionary<string, string> headers)
		{
			this._websocket = websocket;
			this._logger = Logger.CreateLogger<WebSocketWrapper>();
			this._processingCTS = new CancellationTokenSource();
			this._messageQueue = Channel.CreateBounded<(ArraySegment<byte> Buffer, WebSocketMessageType MessageType, bool EndOfMessage)>(new BoundedChannelOptions(1024)
			{
				SingleReader = true,
				SingleWriter = false,
				FullMode = BoundedChannelFullMode.Wait
			});
			this._messageWriter = Task.Run(this.SendAsync);
			this.ID = Guid.NewGuid();
			this.RequestUri = requestUri;
			this.RemoteEndPoint = remoteEndPoint;
			this.LocalEndPoint = localEndPoint;
			this.Set("Headers", headers);
		}

		/// <summary>
		/// Receives data from the WebSocket connection asynchronously
		/// </summary>
		/// <param name="buffer">The buffer to copy data into</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
			=> this._websocket.ReceiveAsync(buffer, cancellationToken);

		async Task SendAsync()
		{
			try
			{
				while (await this._messageQueue.Reader.WaitToReadAsync(this._processingCTS.Token).ConfigureAwait(false))
				{
					while (this._messageQueue.Reader.TryRead(out var message))
					{
						if (this.State != WebSocketState.Open)
							continue;
						await this._websocket.SendAsync(message.Buffer, message.MessageType, message.EndOfMessage, this._processingCTS.Token).ConfigureAwait(false);
					}
				}
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				this._logger?.LogError(ex, $"Send loop crashed => {this.ID}");
				throw;
			}
		}

		/// <summary>
		/// Sends data over the WebSocket connection asynchronously
		/// </summary>
		/// <param name="buffer">The buffer containing data to send</param>
		/// <param name="messageType">The message type, can be Text or Binary</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), if its a multi-part message then false (and true for the last)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		/// <returns></returns>
		public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
		{
			if (this.IsDisposed)
			{
				if (this._logger.IsEnabled(LogLevel.Debug))
					this._logger.LogWarning($"Object disposed => {this.ID}");
				throw new ObjectDisposedException($"WebSocketWrapper => {this.ID}");
			}
			await this._messageQueue.Writer.WriteAsync((buffer, messageType, endOfMessage), cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Polite close (use the close handshake)
		/// </summary>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="closeStatusDescription">A description of why we are closing</param>
		/// <param name="cancellationToken">The timeout cancellation token</param>
		/// <returns></returns>
		public override Task CloseAsync(WebSocketCloseStatus closeStatus, string closeStatusDescription, CancellationToken cancellationToken)
			=> this._websocket.CloseAsync(closeStatus, closeStatusDescription, cancellationToken);

		/// <summary>
		/// Fire and forget close
		/// </summary>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="closeStatusDescription">A description of why we are closing</param>
		/// <param name="cancellationToken">The timeout cancellation token</param>
		/// <returns></returns>
		public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string closeStatusDescription, CancellationToken cancellationToken)
			=> this._websocket.CloseOutputAsync(closeStatus, closeStatusDescription, cancellationToken);

		/// <summary>
		/// Aborts the WebSocket without sending a Close frame
		/// </summary>
		public override void Abort()
			=> this._websocket.Abort();

		internal override async ValueTask DisposeAsync(WebSocketCloseStatus closeStatus, string closeStatusDescription = "Service is unavailable", Action<ManagedWebSocket> next = null)
		{
			this._processingCTS.Cancel();
			this._messageQueue.Writer.TryComplete();
			try
			{
				await this._messageWriter.ConfigureAwait(false);
			}
			catch { }
			this._processingCTS.Dispose();
			await base.DisposeAsync(closeStatus, closeStatusDescription, _ =>
			{
				if ("System.Net.WebSockets.ManagedWebSocket".Equals($"{this._websocket.GetType()}"))
					this._websocket.Dispose();
				next?.Invoke(this);
			}).ConfigureAwait(false);
		}

		public override ValueTask DisposeAsync()
		{
			GC.SuppressFinalize(this);
			return this.IsDisposed ? new ValueTask(Task.CompletedTask) : this.DisposeAsync(WebSocketCloseStatus.EndpointUnavailable);
		}

		public override void Dispose()
			=> this.DisposeAsync().Execute(true);

		~WebSocketWrapper()
			=> this.Dispose();
	}
}