#region Related components
using System;
using System.Text;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Components.WebSockets.Implementation
{
	/// <summary>
	/// An implementation or a wrapper of the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> abstract class with more useful information
	/// </summary>
	public abstract class WebSocket : System.Net.WebSockets.WebSocket, IDisposable
	{
		/// <summary>
		/// Gets the identity of the <see cref="WebSocket">WebSocket</see> connection
		/// </summary>
		public Guid ID { get; internal set; }

		/// <summary>
		/// Gets the time-stamp when the <see cref="WebSocket">WebSocket</see> connection is established
		/// </summary>
		public DateTime Timestamp { get; internal set; } = DateTime.Now;

		/// <summary>
		/// Gets the original Uniform Resource Identifier (URI) of the <see cref="WebSocket">WebSocket</see> connection
		/// </summary>
		public Uri RequestUri { get; internal set; }

		/// <summary>
		/// Gets the local endpoint of the <see cref="WebSocket">WebSocket</see> connection
		/// </summary>
		public EndPoint LocalEndPoint { get; internal set; }

		/// <summary>
		/// Gets the remote endpoint of the <see cref="WebSocket">WebSocket</see> connection
		/// </summary>
		public EndPoint RemoteEndPoint { get; internal set; }

		/// <summary>
		/// Gets the state that indicates the <see cref="WebSocket">WebSocket</see> connection is client mode or not (client mode means the <see cref="WebSocket">WebSocket</see> connection is connected to a remote endpoint)
		/// </summary>
		public bool IsClient { get; internal set; }

		/// <summary>
		/// Gets or sets the keep-alive interval (seconds) the <see cref="WebSocket">WebSocket</see> connection (for send ping message from server)
		/// </summary>
		public TimeSpan KeepAliveInterval { get; set; }

		/// <summary>
		/// Gets the state to include the full exception (with stack trace) in the close response when an exception is encountered and the WebSocket connection is closed
		/// </summary>
		protected abstract bool IncludeExceptionInCloseResponse { get; }

		/// <summary>
		/// Sends data over the <see cref="WebSocket">WebSocket</see> connection asynchronously
		/// </summary>
		/// <param name="data">The text data send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), if its a multi-part message then false (and true for the last)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public Task SendAsync(string data, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this.SendAsync(data.ToArraySegment(), WebSocketMessageType.Text, endOfMessage, cancellationToken);
		}

		/// <summary>
		/// Sends data over the <see cref="WebSocket">WebSocket</see> connection asynchronously
		/// </summary>
		/// <param name="data">The binary data send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), if its a multi-part message then false (and true for the last)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public Task SendAsync(byte[] data, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this.SendAsync(data.ToArraySegment(), WebSocketMessageType.Binary, endOfMessage, cancellationToken);
		}

		/// <summary>
		/// Closes the <see cref="WebSocket">WebSocket</see> connection automatically in response to some invalid data from the remote host
		/// </summary>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="closeStatusDescription">A description of why we are closing</param>
		/// <param name="ex">The exception (for logging)</param>
		/// <returns></returns>
		internal async Task CloseOutputTimeoutAsync(WebSocketCloseStatus closeStatus, string closeStatusDescription, Exception ex)
		{
			var timespan = TimeSpan.FromSeconds(3);
			Events.Log.CloseOutputAutoTimeout(this.ID, closeStatus, closeStatusDescription, ex.ToString());

			try
			{
				using (var cts = new CancellationTokenSource(timespan))
				{
					await this.CloseOutputAsync(closeStatus, (closeStatusDescription ?? "") + (this.IncludeExceptionInCloseResponse ? "\r\n\r\n" + ex.ToString() : ""), cts.Token).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException)
			{
				Events.Log.CloseOutputAutoTimeoutCancelled(this.ID, (int)timespan.TotalSeconds, closeStatus, closeStatusDescription, ex.ToString());
			}
			catch (Exception closeException)
			{
				Events.Log.CloseOutputAutoTimeoutError(this.ID, closeException.ToString(), closeStatus, closeStatusDescription, ex.ToString());
			}
		}

		/// <summary>
		/// Cleans up unmanaged resources (will send a close frame if the connection is still open)
		/// </summary>
		public override void Dispose()
		{
			this.DisposeAsync().Wait(5123);
		}

		bool _disposing = false, _disposed = false;

		internal virtual async Task DisposeAsync(WebSocketCloseStatus closeStatus = WebSocketCloseStatus.EndpointUnavailable, string closeStatusDescription = "Service is unavailable", CancellationToken cancellationToken = default(CancellationToken), Action onCompleted = null)
		{
			if (this._disposing || this._disposed)
				return;

			this._disposing = true;
			Events.Log.WebSocketDispose(this.ID, this.State);
			if (this.State == WebSocketState.Open)
				try
				{
					using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token))
					{
						await this.CloseOutputAsync(closeStatus, closeStatusDescription, cts.Token).ConfigureAwait(false);
					}
				}
				catch (OperationCanceledException)
				{
					Events.Log.WebSocketDisposeCloseTimeout(this.ID, this.State);
				}
				catch (Exception ex)
				{
					Events.Log.WebSocketDisposeError(this.ID, this.State, ex.ToString());
				}
			onCompleted?.Invoke();
			this._disposed = true;
			this._disposing = false;
		}

		~WebSocket()
		{
			this.Dispose();
			GC.SuppressFinalize(this);
		}
	}
}