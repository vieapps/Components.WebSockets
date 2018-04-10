#region Related components
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Components.WebSockets.Internal
{
	/// <summary>
	/// Main implementation of the WebSocket abstract class
	/// </summary>
	internal class WebSocketImplementation : WebSocket
	{
		readonly Guid _guid;
		readonly Func<MemoryStream> _recycledStreamFactory;
		readonly Stream _stream;
		readonly bool _includeExceptionInCloseResponse;
		readonly bool _isClient;
		CancellationTokenSource _internalReadCTS;
		WebSocketState _state;
		readonly IPingPongManager _pingPongManager;
		bool _isContinuationFrame;
		WebSocketMessageType _continuationFrameMessageType = WebSocketMessageType.Binary;
		readonly bool _usePerMessageDeflate = false;
		bool _tryGetBufferFailureLogged = false;
		WebSocketCloseStatus? _closeStatus;
		string _closeStatusDescription;

		const int MAX_PING_PONG_PAYLOAD_LEN = 125;

		public event EventHandler<PongEventArgs> Pong;

		internal static Microsoft.Extensions.Logging.ILogger Logger = Fleck.Logger.CreateLogger<WebSocketImplementation>();

		internal WebSocketImplementation(Guid guid, Func<MemoryStream> recycledStreamFactory, Stream stream, TimeSpan keepAliveInterval, string secWebSocketExtensions, bool includeExceptionInCloseResponse, bool isClient)
		{
			this._guid = guid;
			this._recycledStreamFactory = recycledStreamFactory;
			this._stream = stream;
			this._isClient = isClient;
			this._internalReadCTS = new CancellationTokenSource();
			this._state = WebSocketState.Open;

			if (secWebSocketExtensions?.IndexOf("permessage-deflate") >= 0)
			{
				this._usePerMessageDeflate = true;
				Events.Log.UsePerMessageDeflate(guid);
			}
			else
				Events.Log.NoMessageCompression(guid);

			this.KeepAliveInterval = keepAliveInterval;
			this._includeExceptionInCloseResponse = includeExceptionInCloseResponse;
			if (keepAliveInterval.Ticks < 0)
				throw new InvalidOperationException("KeepAliveInterval must be Zero or positive");

			if (keepAliveInterval == TimeSpan.Zero)
				Events.Log.KeepAliveIntervalZero(guid);
			else
				this._pingPongManager = new PingPongManager(guid, this, keepAliveInterval, this._internalReadCTS.Token);
		}

		public override WebSocketCloseStatus? CloseStatus => this._closeStatus;

		public override string CloseStatusDescription => this._closeStatusDescription;

		public override WebSocketState State { get { return this._state; } }

		public override string SubProtocol => null;

		public TimeSpan KeepAliveInterval { get; set; }

		public Guid ID { get { return this._guid; } }

		public bool IsClient { get { return this._isClient; } }

		/// <summary>
		/// Receive web socket result
		/// </summary>
		/// <param name="buffer">The buffer to copy data into</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns>The web socket result details</returns>
		public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
		{
			try
			{
				// we may receive control frames so reading needs to happen in an infinite loop
				while (true)
				{
					// allow this operation to be cancelled from iniside OR outside this instance
					using (var linkedCTS = CancellationTokenSource.CreateLinkedTokenSource(this._internalReadCTS.Token, cancellationToken))
					{
						WebSocketFrame frame = null;
						try
						{
							frame = await WebSocketFrameReader.ReadAsync(this._stream, buffer, linkedCTS.Token).ConfigureAwait(false);
							Events.Log.ReceivedFrame(this._guid, frame.OpCode, frame.IsFinBitSet, frame.Count);
						}
						catch (InternalBufferOverflowException ex)
						{
							await this.CloseOutputAutoTimeoutAsync(WebSocketCloseStatus.MessageTooBig, "Frame too large to fit in buffer. Use message fragmentation", ex).ConfigureAwait(false);
							throw;
						}
						catch (ArgumentOutOfRangeException ex)
						{
							await this.CloseOutputAutoTimeoutAsync(WebSocketCloseStatus.ProtocolError, "Payload length out of range", ex).ConfigureAwait(false);
							throw;
						}
						catch (EndOfStreamException ex)
						{
							await this.CloseOutputAutoTimeoutAsync(WebSocketCloseStatus.InvalidPayloadData, "Unexpected end of stream encountered", ex).ConfigureAwait(false);
							throw;
						}
						catch (OperationCanceledException ex)
						{
							await this.CloseOutputAutoTimeoutAsync(WebSocketCloseStatus.EndpointUnavailable, "Operation cancelled", ex).ConfigureAwait(false);
							throw;
						}
						catch (Exception ex)
						{
							if (Logger.IsEnabled(LogLevel.Debug) && ex is OperationCanceledException)
								Logger.LogDebug("Cancel (ReceiveAsync)");
							await this.CloseOutputAutoTimeoutAsync(WebSocketCloseStatus.InternalServerError, "Error reading WebSocket frame", ex).ConfigureAwait(false);
							throw;
						}
						finally
						{
							GC.Collect();
						}

						switch (frame.OpCode)
						{
							case WebSocketOpCode.ConnectionClose:
								return await this.RespondToCloseFrameAsync(frame, buffer, linkedCTS.Token).ConfigureAwait(false);

							case WebSocketOpCode.Ping:
								var pingPayload = new ArraySegment<byte>(buffer.Array, buffer.Offset, frame.Count);
								await this.SendPongAsync(pingPayload, linkedCTS.Token).ConfigureAwait(false);
								break;

							case WebSocketOpCode.Pong:
								var pongBuffer = new ArraySegment<byte>(buffer.Array, frame.Count, buffer.Offset);
								this.Pong?.Invoke(this, new PongEventArgs(pongBuffer));
								break;

							case WebSocketOpCode.TextFrame:
								if (!frame.IsFinBitSet)
									this._continuationFrameMessageType = WebSocketMessageType.Text; // continuation frames will follow, record the message type Text
								return new WebSocketReceiveResult(frame.Count, WebSocketMessageType.Text, frame.IsFinBitSet);

							case WebSocketOpCode.BinaryFrame:
								if (!frame.IsFinBitSet)
									this._continuationFrameMessageType = WebSocketMessageType.Binary; // continuation frames will follow, record the message type Binary
								return new WebSocketReceiveResult(frame.Count, WebSocketMessageType.Binary, frame.IsFinBitSet);

							case WebSocketOpCode.ContinuationFrame:
								return new WebSocketReceiveResult(frame.Count, this._continuationFrameMessageType, frame.IsFinBitSet);

							default:
								var ex = new NotSupportedException($"Unknown WebSocket opcode {frame.OpCode}");
								await this.CloseOutputAutoTimeoutAsync(WebSocketCloseStatus.ProtocolError, ex.Message, ex).ConfigureAwait(false);
								throw ex;
						}
					}
				}
			}
			catch (Exception catchAll)
			{
				// Most exceptions will be caught closer to their source to send an appropriate close message (and set the WebSocketState)
				// However, if an unhandled exception is encountered and a close message not sent then send one here
				if (this._state == WebSocketState.Open)
					await this.CloseOutputAutoTimeoutAsync(WebSocketCloseStatus.InternalServerError, "Unexpected error reading from WebSocket", catchAll).ConfigureAwait(false);
				throw;
			}
		}

		/// <summary>
		/// Send data to the web socket
		/// </summary>
		/// <param name="buffer">the buffer containing data to send</param>
		/// <param name="messageType">The message type. Can be Text or Binary</param>
		/// <param name="endOfMessage">True if this message is a standalone message (this is the norm)
		/// If it is a multi-part message then false (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
		{
			using (var stream = this._recycledStreamFactory())
			{
				var opCode = this.GetOppCode(messageType);

				// NOTE: Compression is currently work in progress and should NOT be used in this library.
				// The code below is very inefficient for small messages. Ideally we would like to have some sort of moving window of data to get the best compression.
				// And we don't want to create new buffers which is bad for GC.
				if (this._usePerMessageDeflate)
					using (var temp = new MemoryStream())
					{
						using (var deflateStream = new DeflateStream(temp, CompressionMode.Compress))
						{
							deflateStream.Write(buffer.Array, buffer.Offset, buffer.Count);
							deflateStream.Flush();
						}
						var compressedBuffer = new ArraySegment<byte>(temp.ToArray());
						WebSocketFrameWriter.Write(opCode, compressedBuffer, stream, endOfMessage, this._isClient);
						Events.Log.SendingFrame(this._guid, opCode, endOfMessage, compressedBuffer.Count, true);
					}

				else
				{
					WebSocketFrameWriter.Write(opCode, buffer, stream, endOfMessage, this._isClient);
					Events.Log.SendingFrame(this._guid, opCode, endOfMessage, buffer.Count, false);
				}

				await this.WriteStreamToNetworkAsync(stream, cancellationToken).ConfigureAwait(false);
				this._isContinuationFrame = !endOfMessage;
			}
		}

		/// <summary>
		/// Call this automatically from server side each keepAliveInterval period
		/// NOTE: ping payload must be 125 bytes or less
		/// </summary>
		public async Task SendPingAsync(ArraySegment<byte> payload, CancellationToken cancellationToken)
		{
			if (payload.Count > MAX_PING_PONG_PAYLOAD_LEN)
				throw new InvalidOperationException($"Cannot send Ping: Max ping message size {MAX_PING_PONG_PAYLOAD_LEN} exceeded: {payload.Count}");

			if (this._state == WebSocketState.Open)
				using (var stream = this._recycledStreamFactory())
				{
					WebSocketFrameWriter.Write(WebSocketOpCode.Ping, payload, stream, true, this._isClient);
					Events.Log.SendingFrame(this._guid, WebSocketOpCode.Ping, true, payload.Count, false);
					await this.WriteStreamToNetworkAsync(stream, cancellationToken).ConfigureAwait(false);
				}
		}

		/// <summary>
		/// Aborts the WebSocket without sending a Close frame
		/// </summary>
		public override void Abort()
		{
			this._state = WebSocketState.Aborted;
			this._internalReadCTS.Cancel();
		}

		/// <summary>
		/// Polite close (use the close handshake)
		/// </summary>
		public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
		{
			if (this._state == WebSocketState.Open)
			{
				using (var stream = this._recycledStreamFactory())
				{
					var buffer = this.BuildClosePayload(closeStatus, statusDescription);
					WebSocketFrameWriter.Write(WebSocketOpCode.ConnectionClose, buffer, stream, true, this._isClient);
					Events.Log.CloseHandshakeStarted(this._guid, closeStatus, statusDescription);
					Events.Log.SendingFrame(this._guid, WebSocketOpCode.ConnectionClose, true, buffer.Count, true);
					await this.WriteStreamToNetworkAsync(stream, cancellationToken).ConfigureAwait(false);
					this._state = WebSocketState.CloseSent;
				}
			}
			else
				Events.Log.InvalidStateBeforeClose(this._guid, this._state);
		}

		/// <summary>
		/// Fire and forget close
		/// </summary>
		public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
		{
			if (this._state == WebSocketState.Open)
			{
				// set this before we write to the network because the write may fail
				this._state = WebSocketState.Closed;

				using (var stream = this._recycledStreamFactory())
				{
					var buffer = this.BuildClosePayload(closeStatus, statusDescription);
					WebSocketFrameWriter.Write(WebSocketOpCode.ConnectionClose, buffer, stream, true, this._isClient);
					Events.Log.CloseOutputNoHandshake(this._guid, closeStatus, statusDescription);
					Events.Log.SendingFrame(this._guid, WebSocketOpCode.ConnectionClose, true, buffer.Count, true);
					await this.WriteStreamToNetworkAsync(stream, cancellationToken).ConfigureAwait(false);
				}
			}
			else
				Events.Log.InvalidStateBeforeCloseOutput(this._guid, this._state);

			// cancel pending reads
			this._internalReadCTS.Cancel();
		}

		/// <summary>
		/// Dispose will send a close frame if the connection is still open
		/// </summary>
		public override void Dispose()
		{
			this.Dispose(WebSocketCloseStatus.EndpointUnavailable);
		}

		/// <summary>
		/// Dispose will send a close frame if the connection is still open
		/// </summary>
		public void Dispose(WebSocketCloseStatus closeStatus, string closeStatusDescription = "Service is unavailable")
		{
			Events.Log.WebSocketDispose(this._guid, this._state);

			try
			{
				if (this._state == WebSocketState.Open)
					using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
					{
						try
						{
							this.CloseOutputAsync(closeStatus, closeStatusDescription, cts.Token).Wait();
						}
						catch (OperationCanceledException)
						{
							Events.Log.WebSocketDisposeCloseTimeout(this._guid, this._state);
						}
						catch (Exception)
						{
							Events.Log.WebSocketDispose(this._guid, this._state);
						}
					}

				// cancel pending reads - usually does nothing
				this._internalReadCTS.Cancel();
				this._stream.Close();
			}
			catch (Exception ex)
			{
				Events.Log.WebSocketDisposeError(this._guid, this._state, ex.ToString());
			}
		}

		/// <summary>
		/// Called when a Pong frame is received
		/// </summary>
		/// <param name="args"></param>
		protected virtual void OnPong(PongEventArgs args)
		{
			this.Pong?.Invoke(this, args);
		}

		/// <summary>
		/// As per the spec, write the close status followed by the close reason
		/// </summary>
		/// <param name="closeStatus">The close status</param>
		/// <param name="statusDescription">Optional extra close details</param>
		/// <returns>The payload to sent in the close frame</returns>
		ArraySegment<byte> BuildClosePayload(WebSocketCloseStatus closeStatus, string statusDescription)
		{
			var statusBuffer = BitConverter.GetBytes((ushort)closeStatus);
			Array.Reverse(statusBuffer); // network byte order (big endian)

			if (statusDescription == null)
				return new ArraySegment<byte>(statusBuffer);

			else
			{
				var descBuffer = statusDescription.ToBytes();
				var payload = new byte[statusBuffer.Length + descBuffer.Length];
				Buffer.BlockCopy(statusBuffer, 0, payload, 0, statusBuffer.Length);
				Buffer.BlockCopy(descBuffer, 0, payload, statusBuffer.Length, descBuffer.Length);
				return new ArraySegment<byte>(payload);
			}
		}

		/// NOTE: pong payload must be 125 bytes or less
		/// Pong should contain the same payload as the ping
		async Task SendPongAsync(ArraySegment<byte> payload, CancellationToken cancellationToken)
		{
			// as per websocket spec
			if (payload.Count > MAX_PING_PONG_PAYLOAD_LEN)
			{
				Exception ex = new InvalidOperationException($"Max ping message size {MAX_PING_PONG_PAYLOAD_LEN} exceeded: {payload.Count}");
				await this.CloseOutputAutoTimeoutAsync(WebSocketCloseStatus.ProtocolError, ex.Message, ex).ConfigureAwait(false);
				throw ex;
			}

			try
			{
				if (this._state == WebSocketState.Open)
				{
					using (var stream = this._recycledStreamFactory())
					{
						WebSocketFrameWriter.Write(WebSocketOpCode.Pong, payload, stream, true, this._isClient);
						Events.Log.SendingFrame(this._guid, WebSocketOpCode.Pong, true, payload.Count, false);
						await this.WriteStreamToNetworkAsync(stream, cancellationToken).ConfigureAwait(false);
					}
				}
			}
			catch (Exception ex)
			{
				await this.CloseOutputAutoTimeoutAsync(WebSocketCloseStatus.EndpointUnavailable, "Unable to send Pong response", ex).ConfigureAwait(false);
				throw;
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

			if (this._state == WebSocketState.CloseSent)
			{
				// this is a response to close handshake initiated by this instance
				this._state = WebSocketState.Closed;
				Events.Log.CloseHandshakeComplete(this._guid);
			}
			else if (this._state == WebSocketState.Open)
			{
				// this is in response to a close handshake initiated by the remote instance
				var closePayload = new ArraySegment<byte>(buffer.Array, buffer.Offset, frame.Count);
				this._state = WebSocketState.CloseReceived;
				Events.Log.CloseHandshakeRespond(this._guid, frame.CloseStatus, frame.CloseStatusDescription);

				using (var stream = this._recycledStreamFactory())
				{
					WebSocketFrameWriter.Write(WebSocketOpCode.ConnectionClose, closePayload, stream, true, this._isClient);
					Events.Log.SendingFrame(this._guid, WebSocketOpCode.ConnectionClose, true, closePayload.Count, false);
					await this.WriteStreamToNetworkAsync(stream, cancellationToken).ConfigureAwait(false);
				}
			}
			else
				Events.Log.CloseFrameReceivedInUnexpectedState(this._guid, this._state, frame.CloseStatus, frame.CloseStatusDescription);

			return new WebSocketReceiveResult(frame.Count, WebSocketMessageType.Close, frame.IsFinBitSet, frame.CloseStatus, frame.CloseStatusDescription);
		}

		ArraySegment<byte> GetBuffer(MemoryStream stream)
		{
			// Avoid calling ToArray on the MemoryStream because it allocates a new byte array on the heap
			// We avaoid this by attempting to access the internal memory stream buffer
			// This works with supported streams like the recyclable memory stream and writable memory streams
			if (!stream.TryGetBuffer(out ArraySegment<byte> buffer))
			{
				if (!this._tryGetBufferFailureLogged)
				{
					Events.Log.TryGetBufferNotSupported(this._guid, stream?.GetType()?.ToString());
					this._tryGetBufferFailureLogged = true;
				}

				// internal buffer not suppoted, fall back to ToArray()
				var array = stream.ToArray();
				buffer = new ArraySegment<byte>(array, 0, array.Length);
			}

			return new ArraySegment<byte>(buffer.Array, buffer.Offset, (int)stream.Position);
		}

		/// <summary>
		/// Puts data on the wire
		/// </summary>
		/// <param name="stream">The stream to read data from</param>
		async Task WriteStreamToNetworkAsync(MemoryStream stream, CancellationToken cancellationToken)
		{
			try
			{
				var buffer = this.GetBuffer(stream);
				await this._stream.WriteAsync(buffer.Array, buffer.Offset, buffer.Count, cancellationToken).ConfigureAwait(false);
				await this._stream.FlushAsync().ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				if (Logger.IsEnabled(LogLevel.Debug) && ex is OperationCanceledException)
					Logger.LogDebug("Cancel (WriteStreamToNetworkAsync)");
				throw ex;
			}
			finally
			{
				GC.Collect();
			}
		}

		/// <summary>
		/// Turns a spec websocket frame opcode into a WebSocketMessageType
		/// </summary>
		WebSocketOpCode GetOppCode(WebSocketMessageType messageType)
		{
			if (this._isContinuationFrame)
				return WebSocketOpCode.ContinuationFrame;

			switch (messageType)
			{
				case WebSocketMessageType.Binary:
					return WebSocketOpCode.BinaryFrame;

				case WebSocketMessageType.Text:
					return WebSocketOpCode.TextFrame;

				case WebSocketMessageType.Close:
					throw new NotSupportedException("Cannot use Send function to send a close frame. Use Close function.");

				default:
					throw new NotSupportedException($"MessageType {messageType} not supported");
			}
		}

		/// <summary>
		/// Automatic WebSocket close in response to some invalid data from the remote websocket host
		/// </summary>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="statusDescription">A description of why we are closing</param>
		/// <param name="ex">The exception (for logging)</param>
		async Task CloseOutputAutoTimeoutAsync(WebSocketCloseStatus closeStatus, string statusDescription, Exception ex)
		{
			var timeSpan = TimeSpan.FromSeconds(5);
			Events.Log.CloseOutputAutoTimeout(this._guid, closeStatus, statusDescription, ex.ToString());

			try
			{
				// we may not want to send sensitive information to the client / server
				if (this._includeExceptionInCloseResponse)
					statusDescription = statusDescription + "\r\n\r\n" + ex.ToString();

				using (var autoCancel = new CancellationTokenSource(timeSpan))
				{
					await this.CloseOutputAsync(closeStatus, statusDescription, autoCancel.Token).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException)
			{
				// do not throw an exception because that will mask the original exception
				Events.Log.CloseOutputAutoTimeoutCancelled(this._guid, (int)timeSpan.TotalSeconds, closeStatus, statusDescription, ex.ToString());
			}
			catch (Exception closeException)
			{
				// do not throw an exception because that will mask the original exception
				Events.Log.CloseOutputAutoTimeoutError(this._guid, closeException.ToString(), closeStatus, statusDescription, ex.ToString());
			}
		}
	}
}