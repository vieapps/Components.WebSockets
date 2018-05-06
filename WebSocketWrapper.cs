#region Related components
using System;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Components.WebSockets
{
	internal class WebSocketWrapper : ManagedWebSocket
	{

		#region Properties
		System.Net.WebSockets.WebSocket _websocket = null;
		ConcurrentQueue<ArraySegment<byte>> _buffers = new ConcurrentQueue<ArraySegment<byte>>();
		bool _writting = false;

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

		public WebSocketWrapper(System.Net.WebSockets.WebSocket websocket, Uri requestUri, EndPoint remoteEndPoint = null, EndPoint localEndPoint = null)
		{
			this._websocket = websocket;
			this.ID = Guid.NewGuid();
			this.RequestUri = requestUri;
			this.RemoteEndPoint = remoteEndPoint;
			this.LocalEndPoint = localEndPoint;
		}

		/// <summary>
		/// Receives data from the WebSocket connection asynchronously
		/// </summary>
		/// <param name="buffer">The buffer to copy data into</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
		{
			return this._websocket.ReceiveAsync(buffer, cancellationToken);
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
			// add into queue and check pending write operations
			this._buffers.Enqueue(buffer);
			if (this._writting)
			{
				Events.Log.PendingOperations(this.ID);
				Logger.Log<WebSocketWrapper>(LogLevel.Debug, LogLevel.Warning, $"Pending operations => {this._buffers.Count:#,##0} ({this.ID} @ {this.RemoteEndPoint})");
				return;
			}

			// put data to wire
			this._writting = true;
			try
			{
				while (this.State == WebSocketState.Open && this._buffers.Count > 0)
					if (this._buffers.TryDequeue(out buffer))
						await this._websocket.SendAsync(buffer, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception)
			{
				throw;
			}
			finally
			{
				this._writting = false;
			}
		}

		/// <summary>
		/// Polite close (use the close handshake)
		/// </summary>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="closeStatusDescription">A description of why we are closing</param>
		/// <param name="cancellationToken">The timeout cancellation token</param>
		/// <returns></returns>
		public override Task CloseAsync(WebSocketCloseStatus closeStatus, string closeStatusDescription, CancellationToken cancellationToken)
		{
			return this._websocket.CloseAsync(closeStatus, closeStatusDescription, cancellationToken);
		}

		/// <summary>
		/// Fire and forget close
		/// </summary>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="closeStatusDescription">A description of why we are closing</param>
		/// <param name="cancellationToken">The timeout cancellation token</param>
		/// <returns></returns>
		public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string closeStatusDescription, CancellationToken cancellationToken)
		{
			return this._websocket.CloseOutputAsync(closeStatus, closeStatusDescription, cancellationToken);
		}

		/// <summary>
		/// Aborts the WebSocket without sending a Close frame
		/// </summary>
		public override void Abort()
		{
			this._websocket.Abort();
		}

		internal override Task DisposeAsync(WebSocketCloseStatus closeStatus = WebSocketCloseStatus.EndpointUnavailable, string closeStatusDescription = "Service is unavailable", CancellationToken cancellationToken = default(CancellationToken), Action onCompleted = null)
		{
			return base.DisposeAsync(closeStatus, closeStatusDescription, cancellationToken, () =>
			{
				this.Close();
				onCompleted?.Invoke();
			});
		}

		internal override void Close()
		{
			if ("System.Net.WebSockets.ManagedWebSocket".Equals($"{this._websocket.GetType()}"))
				this._websocket.Dispose();
		}

		~WebSocketWrapper()
		{
			this.Dispose();
			GC.SuppressFinalize(this);
		}
	}
}