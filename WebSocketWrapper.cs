#region Related components
using System;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
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
		readonly ConcurrentQueue<Tuple<ArraySegment<byte>, WebSocketMessageType, bool>> _buffers = new ConcurrentQueue<Tuple<ArraySegment<byte>, WebSocketMessageType, bool>>();
		readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
		bool _pending = false;

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
			this.ID = Guid.NewGuid();
			this.RequestUri = requestUri;
			this.RemoteEndPoint = remoteEndPoint;
			this.LocalEndPoint = localEndPoint;
			this.Extra["Headers"] = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		}

		/// <summary>
		/// Receives data from the WebSocket connection asynchronously
		/// </summary>
		/// <param name="buffer">The buffer to copy data into</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
			=> this._websocket.ReceiveAsync(buffer, cancellationToken);

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
			// check disposed
			if (this._disposed)
				throw new ObjectDisposedException("WebSocketWrapper");

			// add into queue and check pending operations
			this._buffers.Enqueue(new Tuple<ArraySegment<byte>, WebSocketMessageType, bool>(buffer, messageType, endOfMessage));
			if (this._pending)
			{
				Events.Log.PendingOperations(this.ID);
				Logger.Log<WebSocketWrapper>(LogLevel.Debug, LogLevel.Warning, $"#{Thread.CurrentThread.ManagedThreadId} Pendings => {this._buffers.Count:#,##0} ({this.ID} @ {this.RemoteEndPoint})");
				return;
			}

			// put data to wire
			this._pending = true;
			await this._lock.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				while (this.State == WebSocketState.Open && this._buffers.Count > 0)
					if (this._buffers.TryDequeue(out Tuple<ArraySegment<byte>, WebSocketMessageType, bool> data))
						await this._websocket.SendAsync(buffer: data.Item1, messageType: data.Item2, endOfMessage: data.Item3, cancellationToken: cancellationToken).ConfigureAwait(false);
			}
			catch (Exception)
			{
				throw;
			}
			finally
			{
				this._pending = false;
				this._lock.Release();
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

		internal override Task DisposeAsync(WebSocketCloseStatus closeStatus = WebSocketCloseStatus.EndpointUnavailable, string closeStatusDescription = "Service is unavailable", CancellationToken cancellationToken = default(CancellationToken), Action onDisposed = null)
			=> base.DisposeAsync(closeStatus, closeStatusDescription, cancellationToken, () =>
			{
				this.Close();
				try
				{
					onDisposed?.Invoke();
				}
				catch { }
				try
				{
					this._lock.Dispose();
				}
				catch { }
			});

		internal override void Close()
		{
			if (!this._disposing && !this._disposed && "System.Net.WebSockets.ManagedWebSocket".Equals($"{this._websocket.GetType()}"))
				this._websocket.Dispose();
		}

		~WebSocketWrapper()
		{
			this.Dispose();
			GC.SuppressFinalize(this);
		}
	}
}