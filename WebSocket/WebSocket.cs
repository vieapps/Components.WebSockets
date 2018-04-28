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
	/// An implementation or wrapper of the <see cref="System.Net.WebSockets.WebSocket">System.Net.WebSockets.WebSocket</see> abstract class with more useful properties and methods
	/// </summary>
	public abstract class WebSocket : System.Net.WebSockets.WebSocket, IDisposable
	{
		/// <summary>
		/// Gets or sets the keep-alive interval (seconds)
		/// </summary>
		public TimeSpan KeepAliveInterval { get; set; }

		/// <summary>
		/// Gets the identity of the WebSocket connection
		/// </summary>
		public Guid ID { get; internal set; }

		/// <summary>
		/// Gets the time when the WebSocket connection is established
		/// </summary>
		public DateTime Time { get; internal set; } = DateTime.Now;

		/// <summary>
		/// Gets the path from the requesting uri of the WebSocket connection
		/// </summary>
		public string UriPath { get; internal set; }

		/// <summary>
		/// Gets the local endpoint of the WebSocket connection
		/// </summary>
		public EndPoint LocalEndPoint { get; internal set; }

		/// <summary>
		/// Gets the remote endpoint of the WebSocket connection
		/// </summary>
		public EndPoint RemoteEndPoint { get; internal set; }

		/// <summary>
		/// Gets the state that indicates the WebSocket connection is client mode or not (client mode means the WebSocket connection is connected to a remote endpoint)
		/// </summary>
		public bool IsClient { get; internal set; }

		/// <summary>
		/// Gets the state to include the full exception (with stack trace) in the close response when an exception is encountered and the WebSocket connection is closed
		/// </summary>
		protected abstract bool IncludeExceptionInCloseResponse { get; }

		/// <summary>
		/// Sends data over the WebSocket connection asynchronously
		/// </summary>
		/// <param name="message">The buffer containing data to send</param>
		/// <param name="endOfMessage">True if this message is a standalone message (this is the norm)
		/// If it is a multi-part message then false (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public Task SendAsync(string message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this.SendAsync(message.ToArraySegment(), WebSocketMessageType.Text, endOfMessage, cancellationToken);
		}

		/// <summary>
		/// Sends data over the WebSocket connection asynchronously
		/// </summary>
		/// <param name="message">The buffer containing data to send</param>
		/// <param name="endOfMessage">True if this message is a standalone message (this is the norm)
		/// If it is a multi-part message then false (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public Task SendAsync(byte[] message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this.SendAsync(message.ToArraySegment(), WebSocketMessageType.Binary, endOfMessage, cancellationToken);
		}

		/// <summary>
		/// Closes the WebSocket connection automatically in response to some invalid data from the remote host
		/// </summary>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="closeStatusDescription">A description of why we are closing</param>
		/// <param name="ex">The exception (for logging)</param>
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
				// do not throw an exception because that will mask the original exception
				Events.Log.CloseOutputAutoTimeoutCancelled(this.ID, (int)timespan.TotalSeconds, closeStatus, closeStatusDescription, ex.ToString());
			}
			catch (Exception closeException)
			{
				// do not throw an exception because that will mask the original exception
				Events.Log.CloseOutputAutoTimeoutError(this.ID, closeException.ToString(), closeStatus, closeStatusDescription, ex.ToString());
			}
		}

		/// <summary>
		/// Closes the WebSocket connection with time-out cancellation token
		/// </summary>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="closeStatusDescription">A description of why we are closing</param>
		/// <param name="cancellationToken">The time-out cancellation token</param>
		/// <param name="onCanceled">The action to fire when cancellation token is raised</param>
		/// <param name="onError">The action to fire when got error</param>
		internal async Task CloseOutputTimeoutAsync(WebSocketCloseStatus closeStatus, string closeStatusDescription, CancellationToken cancellationToken, Action onCanceled = null, Action<Exception> onError = null)
		{
			try
			{
				await this.CloseOutputAsync(closeStatus, closeStatusDescription, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				onCanceled?.Invoke();
			}
			catch (Exception ex)
			{
				onError?.Invoke(ex);
			}
		}

		/// <summary>
		/// Dispose will send a close frame if the connection is still open
		/// </summary>
		/// <param name="closeStatus"></param>
		/// <param name="closeStatusDescription"></param>
		/// <param name="cancellationToken"></param>
		internal abstract Task DisposeAsync(WebSocketCloseStatus closeStatus = WebSocketCloseStatus.EndpointUnavailable, string closeStatusDescription = "Service is unavailable", CancellationToken cancellationToken = default(CancellationToken));

		~WebSocket()
		{
			this.Dispose();
			GC.SuppressFinalize(this);
		}
	}
}