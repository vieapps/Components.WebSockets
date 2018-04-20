#region Related components
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.IO;
using Microsoft.Extensions.Logging;

using net.vieapps.Components.Utility;
using net.vieapps.Components.WebSockets.Internal;
using net.vieapps.Components.WebSockets.Exceptions;
#endregion

namespace net.vieapps.Components.WebSockets
{
	/// <summary>
	/// The WebSocket Connection
	/// </summary>
	public class WebSocketConnection : IDisposable
	{

		#region Static helpers
		const int DefaultBlockSize = 16 * 1024;
		const int MaxBufferSize = 128 * 1024;
		internal static ILogger Logger = Fleck.Logger.CreateLogger<WebSocketConnection>();

		/// <summary>
		/// Gets a factory to get recyclable memory stream with  RecyclableMemoryStreamManager class to limit LOH fragmentation and improve performance
		/// </summary>
		/// <returns></returns>
		public static Func<MemoryStream> GetRecyclableMemoryStreamFactory()
		{
			return new RecyclableMemoryStreamManager(WebSocketConnection.DefaultBlockSize, 4, WebSocketConnection.MaxBufferSize).GetStream;
		}

		/// <summary>
		/// Sets the lenght of the buffer for receiving/reading messages from network streams
		/// </summary>
		/// <param name="length">The length (in bytes)</param>
		public static void SetBufferLength(int length = 4096)
		{
			Fleck.WebSocketConnection.SetBufferLength(length);
		}
		#endregion

		#region Properties
		/// <summary>
		/// Gets the identity of this connection
		/// </summary>
		public Guid ID { get; internal set; }

		/// <summary>
		/// Gets the state that specified this is connection of a client
		/// </summary>
		public bool IsClientConnection { get; internal set; } = false;

		/// <summary>
		/// Gets the state that specified this connection is secure or not
		/// </summary>
		public bool IsSecureConnection { get; internal set; } = false;

		/// <summary>
		/// Gets the time when this connection is established
		/// </summary>
		public DateTime Time { get; internal set; } = DateTime.Now;

		/// <summary>
		/// Gets the remote endpoint of this connection
		/// </summary>
		public string EndPoint { get; internal set; }

		/// <summary>
		/// Gets the state of this connection
		/// </summary>
		public WebSocketState State
		{
			get
			{
				return this.InnerSocket == null
					? WebSocketState.Closed
					: this.InnerSocket is Fleck.WebSocketConnection
						? (this.InnerSocket as Fleck.WebSocketConnection).IsAvailable ? WebSocketState.Open : WebSocketState.Closed
						: (this.InnerSocket as WebSocketImplementation).State;
			}
		}

		internal object InnerSocket { get; set; }
		#endregion

		#region Dispose
		public void Dispose()
		{
			this.Dispose(WebSocketCloseStatus.EndpointUnavailable);
		}

		public void Dispose(WebSocketCloseStatus closeStatus, string closeStatusDescription = "Service is unavailable")
		{
			if (this.InnerSocket is Fleck.WebSocketConnection)
				(this.InnerSocket as Fleck.WebSocketConnection)?.Close(closeStatus.GetStatusCode());
			else
				(this.InnerSocket as WebSocketImplementation)?.Dispose(closeStatus, closeStatusDescription);
		}

		~WebSocketConnection()
		{
			this.Dispose();
		}
		#endregion

		#region Event Handlers
		internal Action<Exception> OnError { get; set; }

		internal Action<WebSocketConnection> OnConnectionBroken { get; set; }

		internal Action<WebSocketConnection, WebSocketReceiveResult, byte[]> OnMessageReceived { get; set; }
		#endregion

		#region Send messages
		/// <summary>
		/// Sends the message
		/// </summary>
		/// <param name="buffer">The buffer containing data to send</param>
		/// <param name="messageType">The message type. Can be Text or Binary</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (this.InnerSocket == null)
				return;

			if (this.InnerSocket is Fleck.WebSocketConnection && (this.InnerSocket as Fleck.WebSocketConnection).IsAvailable)
				using (cancellationToken.Register(() => throw new OperationCanceledException(cancellationToken), useSynchronizationContext: false))
				{
					if (messageType == WebSocketMessageType.Close)
						(this.InnerSocket as Fleck.WebSocketConnection).Close();
					else if (messageType == WebSocketMessageType.Binary)
						await (this.InnerSocket as Fleck.WebSocketConnection).Send(buffer.Array.Sub(buffer.Offset, buffer.Count)).ConfigureAwait(false);
					else
						await (this.InnerSocket as Fleck.WebSocketConnection).Send(buffer.Array.Sub(buffer.Offset, buffer.Count).GetString()).ConfigureAwait(false);
				}
			else if (this.InnerSocket is WebSocketImplementation && (this.InnerSocket as WebSocketImplementation).State == WebSocketState.Open)
				await (this.InnerSocket as WebSocketImplementation).SendAsync(buffer, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Sends the message
		/// </summary>
		/// <param name="message">The text message to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public Task SendAsync(string message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return message != null
				? this.SendAsync(message.ToArraySegment(), WebSocketMessageType.Text, endOfMessage, cancellationToken)
				: Task.CompletedTask;
		}

		/// <summary>
		/// Sends the message
		/// </summary>
		/// <param name="message">The binary message to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public Task SendAsync(byte[] message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return message != null
				? this.SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Binary, endOfMessage, cancellationToken)
				: Task.CompletedTask;
		}
		#endregion

		#region Receive messages
		internal async Task ReceiveAsync(CancellationToken cancellationToken)
		{
			// receive the message (infinity loop)
			var buffer = new ArraySegment<byte>(new byte[Fleck.WebSocketConnection.BufferLength]);
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!buffer.Array.Length.Equals(Fleck.WebSocketConnection.BufferLength))
					buffer = new ArraySegment<byte>(new byte[Fleck.WebSocketConnection.BufferLength]);

				WebSocketReceiveResult result = null;
				try
				{
					result = await (this.InnerSocket as WebSocketImplementation).ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					var closeStatus = WebSocketCloseStatus.InternalServerError;
					var closeStatusDescription = $"Close the connection when got an error: {ex.Message}";
					if (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is OperationCanceledException)
					{
						closeStatus = this.IsClientConnection ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.EndpointUnavailable;
						closeStatusDescription = this.IsClientConnection ? "Disconnected" : "Service is unavailable";
					}
					WebSocketConnectionManager.Remove(this, closeStatus, closeStatusDescription);

					this.OnConnectionBroken?.Invoke(this);
					if (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is OperationCanceledException)
					{
						if (WebSocketConnection.Logger.IsEnabled(LogLevel.Trace))
							WebSocketConnection.Logger.LogTrace(ex, $"Close the connection when got an error: {ex.Message}");
					}
					else
					{
						this.OnError?.Invoke(ex);
						if (WebSocketConnection.Logger.IsEnabled(LogLevel.Debug))
							WebSocketConnection.Logger.LogError(ex, closeStatusDescription);
					}
					return;
				}

				// message to close
				if (result.MessageType == WebSocketMessageType.Close)
				{
					WebSocketConnectionManager.Remove(this);
					this.OnConnectionBroken?.Invoke(this);
					if (WebSocketConnection.Logger.IsEnabled(LogLevel.Trace))
						WebSocketConnection.Logger.LogInformation($"Remote end-point is initiated to close - Status: {result.CloseStatus} - Description: {result.CloseStatusDescription ?? "N/A"} ({this.ID} @ {this.EndPoint})");
					return;
				}

				// exceed buffer size
				if (result.Count > Fleck.WebSocketConnection.BufferLength)
				{
					var message = $"WebSocket frame cannot exceed buffer size of {Fleck.WebSocketConnection.BufferLength:#,##0} bytes";
					await this.CloseAsync(WebSocketCloseStatus.MessageTooBig, $"{message}, send multiple frames instead.", CancellationToken.None).ConfigureAwait(false);
					WebSocketConnectionManager.Remove(this);
					this.OnConnectionBroken?.Invoke(this);
					this.OnError?.Invoke(new BufferOverflowException(message));
					if (WebSocketConnection.Logger.IsEnabled(LogLevel.Debug))
						WebSocketConnection.Logger.LogInformation($"Close the connection because {message} ({this.ID} @ {this.EndPoint})");
					return;
				}

				// got a message
				if (result.Count > 0)
				{
					this.OnMessageReceived?.Invoke(this, result, buffer.Take(result.Count).ToArray());
					if (WebSocketConnection.Logger.IsEnabled(LogLevel.Trace))
						WebSocketConnection.Logger.LogTrace($"Got a message - Type: {result.MessageType} - Length: {result.Count:#,##0} ({this.ID} @ {this.EndPoint})");
				}
			}
		}
		#endregion

		#region Close connection
		/// <summary>
		/// Closes this connection
		/// </summary>
		/// <param name="closeStatus"></param>
		/// <param name="statusDescription"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public async Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (this.InnerSocket != null)
			{
				if (this.InnerSocket is Fleck.WebSocketConnection)
					(this.InnerSocket as Fleck.WebSocketConnection).Close(closeStatus.GetStatusCode());
				else
					await (this.InnerSocket as WebSocketImplementation).CloseAsync(closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Fire and forget close
		/// </summary>
		/// <param name="closeStatus"></param>
		/// <param name="statusDescription"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (this.InnerSocket != null)
			{
				if (this.InnerSocket is Fleck.WebSocketConnection)
					(this.InnerSocket as Fleck.WebSocketConnection).Close(closeStatus.GetStatusCode());
				else
					await (this.InnerSocket as WebSocketImplementation).CloseOutputAsync(closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
			}
		}
		#endregion

	}
}