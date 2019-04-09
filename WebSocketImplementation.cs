#region Related components
using System;
using System.Net;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using net.vieapps.Components.WebSockets.Exceptions;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Components.WebSockets
{
	public class WebSocketImplementation : ManagedWebSocket
	{

		#region Properties
		readonly Func<MemoryStream> _recycledStreamFactory;
		readonly Stream _stream;
		readonly PingPongManager _pingpongManager;
		readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
		readonly string _subProtocol;
		readonly CancellationTokenSource _processingCTS;
		readonly ConcurrentQueue<ArraySegment<byte>> _buffers = new ConcurrentQueue<ArraySegment<byte>>();
		readonly ILogger _logger;
		WebSocketState _state;
		WebSocketMessageType _continuationMessageType = WebSocketMessageType.Binary;
		WebSocketCloseStatus? _closeStatus;
		string _closeStatusDescription;
		bool _isContinuationFrame = false;
		bool _pending = false;

		/// <summary>
		/// Gets the state that indicates the reason why the remote endpoint initiated the close handshake
		/// </summary>
		public override WebSocketCloseStatus? CloseStatus => this._closeStatus;

		/// <summary>
		/// Gets the description to describe the reason why the connection was closed
		/// </summary>
		public override string CloseStatusDescription => this._closeStatusDescription;

		/// <summary>
		/// Gets the current state of the WebSocket connection
		/// </summary>
		public override WebSocketState State => this._state;

		/// <summary>
		/// Gets the subprotocol that was negotiated during the opening handshake
		/// </summary>
		public override string SubProtocol => this._subProtocol;

		/// <summary>
		/// Gets the state to include the full exception (with stack trace) in the close response when an exception is encountered and the WebSocket connection is closed
		/// </summary>
		protected override bool IncludeExceptionInCloseResponse { get; }
		#endregion

		internal WebSocketImplementation(Guid id, bool isClient, Func<MemoryStream> recycledStreamFactory, Stream stream, WebSocketOptions options, Uri requestUri, EndPoint remoteEndPoint, EndPoint localEndPoint, Dictionary<string, string> headers)
		{
			this.ID = id;
			this.IsClient = isClient;
			this.IncludeExceptionInCloseResponse = options.IncludeExceptionInCloseResponse;
			this.KeepAliveInterval = options.KeepAliveInterval.Ticks < 0 ? TimeSpan.FromSeconds(60) : options.KeepAliveInterval;
			this.RequestUri = requestUri;
			this.RemoteEndPoint = remoteEndPoint;
			this.LocalEndPoint = localEndPoint;
			this.Set("Headers", headers);

			this._recycledStreamFactory = recycledStreamFactory ?? WebSocketHelper.GetRecyclableMemoryStreamFactory();
			this._stream = stream;
			this._state = WebSocketState.Open;
			this._subProtocol = options.SubProtocol;
			this._processingCTS = new CancellationTokenSource();
			this._pingpongManager = new PingPongManager(this, options, this._processingCTS.Token);
			this._logger = Logger.CreateLogger<WebSocketImplementation>();
		}

		/// <summary>
		/// Puts data on the wire
		/// </summary>
		/// <param name="stream"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		async Task PutOnTheWireAsync(MemoryStream stream, CancellationToken cancellationToken)
		{
			// check disposed
			if (this.IsDisposed)
			{
				if (this._logger.IsEnabled(LogLevel.Debug))
					this._logger.LogWarning($"Object disposed => {this.ID}");
				throw new ObjectDisposedException($"WebSocketImplementation => {this.ID}");
			}

			// add into queue and check pending operations
			this._buffers.Enqueue(stream.ToArraySegment());
			if (this._pending)
			{
				Events.Log.PendingOperations(this.ID);
				if (this._logger.IsEnabled(LogLevel.Debug))
					this._logger.LogWarning($"#{Thread.CurrentThread.ManagedThreadId} Pendings => {this._buffers.Count:#,##0} ({this.ID} @ {this.RemoteEndPoint})");
				return;
			}

			// put data to wire
			this._pending = true;
			await this._lock.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				while (this._buffers.Count > 0)
					if (this._buffers.TryDequeue(out ArraySegment<byte> buffer))
						await this._stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
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
		/// Receives data from the WebSocket connection asynchronously
		/// </summary>
		/// <param name="buffer">The buffer to copy data into</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
		{
			try
			{
				// we may receive control frames so reading needs to happen in an infinite loop
				while (true)
				{
					// allow this operation to be cancelled from iniside OR outside this instance
					using (var cts = CancellationTokenSource.CreateLinkedTokenSource(this._processingCTS.Token, cancellationToken))
					{
						WebSocketFrame frame = null;
						try
						{
							frame = await this._stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
							Events.Log.ReceivedFrame(this.ID, frame.OpCode, frame.IsFinBitSet, frame.Count);
						}
						catch (InternalBufferOverflowException ex)
						{
							await this.CloseOutputTimeoutAsync(WebSocketCloseStatus.MessageTooBig, "Frame is too large to fit in buffer. Use message fragmentation.", ex).ConfigureAwait(false);
							throw ex;
						}
						catch (ArgumentOutOfRangeException ex)
						{
							await this.CloseOutputTimeoutAsync(WebSocketCloseStatus.ProtocolError, "Payload length is out of range", ex).ConfigureAwait(false);
							throw ex;
						}
						catch (EndOfStreamException ex)
						{
							await this.CloseOutputTimeoutAsync(WebSocketCloseStatus.InvalidPayloadData, "Unexpected end of stream encountered", ex).ConfigureAwait(false);
							throw ex;
						}
						catch (OperationCanceledException ex)
						{
							await this.CloseOutputTimeoutAsync(WebSocketCloseStatus.EndpointUnavailable, "Operation cancelled", ex).ConfigureAwait(false);
							throw ex;
						}
						catch (Exception ex)
						{
							await this.CloseOutputTimeoutAsync(WebSocketCloseStatus.InternalServerError, "Error reading WebSocket frame", ex).ConfigureAwait(false);
							throw ex;
						}

						// process op-code
						switch (frame.OpCode)
						{
							case WebSocketOpCode.ConnectionClose:
								return await this.RespondToCloseFrameAsync(frame, buffer, cts.Token).ConfigureAwait(false);

							case WebSocketOpCode.Ping:
								await this._pingpongManager.SendPongAsync(buffer.Take(frame.Count).ToArray()).ConfigureAwait(false);
								break;

							case WebSocketOpCode.Pong:
								this._pingpongManager.OnPong(buffer.Take(frame.Count).ToArray());
								break;

							case WebSocketOpCode.Text:
								// continuation frames will follow, record the message type as text
								if (!frame.IsFinBitSet)
									this._continuationMessageType = WebSocketMessageType.Text;
								return new WebSocketReceiveResult(frame.Count, WebSocketMessageType.Text, frame.IsFinBitSet);

							case WebSocketOpCode.Binary:
								// continuation frames will follow, record the message type as binary
								if (!frame.IsFinBitSet)
									this._continuationMessageType = WebSocketMessageType.Binary;
								return new WebSocketReceiveResult(frame.Count, WebSocketMessageType.Binary, frame.IsFinBitSet);

							case WebSocketOpCode.Continuation:
								return new WebSocketReceiveResult(frame.Count, this._continuationMessageType, frame.IsFinBitSet);

							default:
								var ex = new NotSupportedException($"Unknown WebSocket op-code: {frame.OpCode}");
								await this.CloseOutputTimeoutAsync(WebSocketCloseStatus.ProtocolError, ex.Message, ex).ConfigureAwait(false);
								throw ex;
						}
					}
				}
			}
			catch (Exception ex)
			{
				// most exceptions will be caught closer to their source to send an appropriate close message (and set the WebSocketState)
				// however, if an unhandled exception is encountered and a close message not sent then send one here
				if (this._state == WebSocketState.Open)
					await this.CloseOutputTimeoutAsync(WebSocketCloseStatus.InternalServerError, "Got an unexpected error while reading from WebSocket", ex).ConfigureAwait(false);
				if (this._logger.IsEnabled(LogLevel.Trace))
					this._logger.LogError(ex, $"Error occurred while receiving ({this.ID} @ {this.RemoteEndPoint}) => {ex.Message}");
				throw ex;
			}
		}

		/// <summary>
		/// Called when a Close frame is received
		/// Send a response close frame if applicable
		/// </summary>
		async Task<WebSocketReceiveResult> RespondToCloseFrameAsync(WebSocketFrame frame, ArraySegment<byte> buffer, CancellationToken cancellationToken)
		{
			this._closeStatus = frame.CloseStatus;
			this._closeStatusDescription = frame.CloseStatusDescription;

			// this is a response to close handshake initiated by this instance
			if (this._state == WebSocketState.CloseSent)
			{
				this._state = WebSocketState.Closed;
				Events.Log.CloseHandshakeComplete(this.ID);
			}

			// this is in response to a close handshake initiated by the remote instance
			else if (this._state == WebSocketState.Open)
			{
				this._state = WebSocketState.CloseReceived;
				Events.Log.CloseHandshakeRespond(this.ID, frame.CloseStatus, frame.CloseStatusDescription);
				using (var stream = this._recycledStreamFactory())
				{
					var closePayload = new ArraySegment<byte>(buffer.Array, buffer.Offset, frame.Count);
					stream.Write(WebSocketOpCode.ConnectionClose, closePayload, true, this.IsClient);
					Events.Log.SendingFrame(this.ID, WebSocketOpCode.ConnectionClose, true, closePayload.Count, false);
					await this.PutOnTheWireAsync(stream, cancellationToken).ConfigureAwait(false);
				}
			}

			// unexpected state
			else
				Events.Log.CloseFrameReceivedInUnexpectedState(this.ID, this._state, frame.CloseStatus, frame.CloseStatusDescription);

			return new WebSocketReceiveResult(frame.Count, WebSocketMessageType.Close, frame.IsFinBitSet, frame.CloseStatus, frame.CloseStatusDescription);
		}

		/// <summary>
		/// Calls this when got ping messages (pong payload must be 125 bytes or less, pong should contain the same payload as the ping)
		/// </summary>
		/// <param name="payload"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public async Task SendPongAsync(ArraySegment<byte> payload, CancellationToken cancellationToken)
		{
			// exceeded max length
			if (payload.Count > 125)
			{
				var ex = new BufferOverflowException($"Max PONG message size is 125 bytes, exceeded: {payload.Count}");
				await this.CloseOutputTimeoutAsync(WebSocketCloseStatus.ProtocolError, ex.Message, ex).ConfigureAwait(false);
				throw ex;
			}

			try
			{
				if (this._state == WebSocketState.Open)
					using (var stream = this._recycledStreamFactory())
					{
						stream.Write(WebSocketOpCode.Pong, payload, true, this.IsClient);
						Events.Log.SendingFrame(this.ID, WebSocketOpCode.Pong, true, payload.Count, false);
						await this.PutOnTheWireAsync(stream, cancellationToken).ConfigureAwait(false);
					}
			}
			catch (Exception ex)
			{
				await this.CloseOutputTimeoutAsync(WebSocketCloseStatus.EndpointUnavailable, "Unable to send PONG response", ex).ConfigureAwait(false);
				throw;
			}
		}

		/// <summary>
		/// Calls this automatically from server side each KeepAliveInterval period (ping payload must be 125 bytes or less)
		/// </summary>
		/// <param name="payload"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public async Task SendPingAsync(ArraySegment<byte> payload, CancellationToken cancellationToken)
		{
			if (payload.Count > 125)
				throw new BufferOverflowException($"Max PING message size is 125 bytes, exceeded: {payload.Count}");

			if (this._state == WebSocketState.Open)
				using (var stream = this._recycledStreamFactory())
				{
					stream.Write(WebSocketOpCode.Ping, payload, true, this.IsClient);
					Events.Log.SendingFrame(this.ID, WebSocketOpCode.Ping, true, payload.Count, false);
					await this.PutOnTheWireAsync(stream, cancellationToken).ConfigureAwait(false);
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
			// prepare op-code
			WebSocketOpCode opCode;
			if (this._isContinuationFrame)
				opCode = WebSocketOpCode.Continuation;
			else
				switch (messageType)
				{
					case WebSocketMessageType.Binary:
						opCode = WebSocketOpCode.Binary;
						break;
					case WebSocketMessageType.Text:
						opCode = WebSocketOpCode.Text;
						break;
					case WebSocketMessageType.Close:
						throw new NotSupportedException("Cannot use Send function to send a close frame, change to use Close function");
					default:
						throw new NotSupportedException($"MessageType \"{messageType}\" is not supported");
				}

			// send
			if (this._state == WebSocketState.Open)
				using (var stream = this._recycledStreamFactory())
				{
					stream.Write(opCode, buffer, endOfMessage, this.IsClient);
					Events.Log.SendingFrame(this.ID, opCode, endOfMessage, buffer.Count, false);
					await this.PutOnTheWireAsync(stream, cancellationToken).ConfigureAwait(false);
					this._isContinuationFrame = !endOfMessage;
				}
		}

		/// <summary>
		/// As per the spec, write the close status followed by the close reason
		/// </summary>
		/// <param name="closeStatus">The close status</param>
		/// <param name="closeStatusDescription">Optional extra close details</param>
		/// <returns>The payload to sent in the close frame</returns>
		ArraySegment<byte> BuildClosePayload(WebSocketCloseStatus closeStatus, string closeStatusDescription)
		{
			var buffer = ((ushort)closeStatus).ToBytes();
			Array.Reverse(buffer); // network byte order (big endian)
			return string.IsNullOrWhiteSpace(closeStatusDescription)
				? buffer.ToArraySegment()
				: buffer.Concat(closeStatusDescription.ToBytes()).ToArraySegment();
		}

		/// <summary>
		/// Polite close (use the close handshake)
		/// </summary>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="closeStatusDescription">A description of why we are closing</param>
		/// <param name="cancellationToken">The timeout cancellation token</param>
		/// <returns></returns>
		public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string closeStatusDescription, CancellationToken cancellationToken)
		{
			if (this._state == WebSocketState.Open)
				using (var stream = this._recycledStreamFactory())
				{
					var buffer = this.BuildClosePayload(closeStatus, closeStatusDescription);
					stream.Write(WebSocketOpCode.ConnectionClose, buffer, true, this.IsClient);
					Events.Log.CloseHandshakeStarted(this.ID, closeStatus, closeStatusDescription);
					Events.Log.SendingFrame(this.ID, WebSocketOpCode.ConnectionClose, true, buffer.Count, false);
					await this.PutOnTheWireAsync(stream, cancellationToken).ConfigureAwait(false);
					this._state = WebSocketState.CloseSent;
				}
			else
				Events.Log.InvalidStateBeforeClose(this.ID, this._state);
		}

		/// <summary>
		/// Fire and forget close
		/// </summary>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="closeStatusDescription">A description of why we are closing</param>
		/// <param name="cancellationToken">The timeout cancellation token</param>
		/// <returns></returns>
		public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string closeStatusDescription, CancellationToken cancellationToken)
		{
			if (this._state == WebSocketState.Open)
			{
				// set the state before we write to the network because the write may fail
				this._state = WebSocketState.Closed;

				// send close frame
				using (var stream = this._recycledStreamFactory())
				{
					var buffer = this.BuildClosePayload(closeStatus, closeStatusDescription);
					stream.Write(WebSocketOpCode.ConnectionClose, buffer, true, this.IsClient);
					Events.Log.CloseOutputNoHandshake(this.ID, closeStatus, closeStatusDescription);
					Events.Log.SendingFrame(this.ID, WebSocketOpCode.ConnectionClose, true, buffer.Count, false);
					await this.PutOnTheWireAsync(stream, cancellationToken).ConfigureAwait(false);
				}
			}
			else
				Events.Log.InvalidStateBeforeCloseOutput(this.ID, this._state);

			// cancel pending reads
			this._processingCTS.Cancel();
		}

		/// <summary>
		/// Aborts the WebSocket without sending a close frame
		/// </summary>
		public override void Abort()
		{
			this._state = WebSocketState.Aborted;
			this._processingCTS.Cancel();
		}

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
			if (!this.IsDisposing && !this.IsDisposed)
			{
				this._processingCTS.Cancel();
				this._processingCTS.Dispose();
				this._stream.Close();
			}
		}

		~WebSocketImplementation()
		{
			this.Dispose();
			GC.SuppressFinalize(this);
		}
	}
}