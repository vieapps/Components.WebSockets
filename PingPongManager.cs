#region Related components
using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

using net.vieapps.Components.WebSockets.Internal;
#endregion

namespace net.vieapps.Components.WebSockets
{
    /// <summary>
    /// Ping Pong Manager used to facilitate ping pong WebSocket messages
    /// </summary>
    public class PingPongManager : IPingPongManager
    {
        readonly WebSocketImplementation _webSocket;
        readonly Guid _guid;
        readonly TimeSpan _keepAliveInterval;
        readonly Task _pingTask;
        readonly CancellationToken _cancellationToken;
        Stopwatch _stopwatch;
        long _pingSentTicks;

        /// <summary>
        /// Raised when a Pong frame is received
        /// </summary>
        public event EventHandler<PongEventArgs> Pong;

        /// <summary>
        /// Initialises a new instance of the PingPongManager to facilitate ping pong WebSocket messages.
        /// If you are manually creating an instance of this class then it is advisable to set keepAliveInterval to 
        /// TimeSpan.Zero when you create the WebSocket instance (using a factory) otherwise you may be automatically
        /// be sending duplicate Ping messages (see keepAliveInterval below)
        /// </summary>
        /// <param name="webSocket">The web socket used to listen to ping messages and send pong messages</param>
        /// <param name="keepAliveInterval">The time between automatically sending ping messages. 
        /// Set this to TimeSpan.Zero if you with to manually control sending ping messages.
        /// </param>
        /// <param name="cancellationToken">The token used to cancel a pending ping send AND the automatic sending of ping messages if keepAliveInterval is positive</param>
        public PingPongManager(Guid guid, WebSocket webSocket, TimeSpan keepAliveInterval, CancellationToken cancellationToken = default(CancellationToken))
        {
            var webSocketImpl = webSocket as WebSocketImplementation;
            this._webSocket = webSocketImpl ?? throw new InvalidCastException("Cannot cast WebSocket to an instance of WebSocketImplementation. Please use the web socket factories to create a web socket");
            this._guid = guid;
            this._keepAliveInterval = keepAliveInterval;
            this._cancellationToken = cancellationToken;
            webSocketImpl.Pong += this.WebSocketImpl_Pong;
            this._stopwatch = Stopwatch.StartNew();

			this._pingTask = keepAliveInterval == TimeSpan.Zero
				? Task.FromResult(0)
				: Task.Run(this.PingForeverAsync, cancellationToken);
        }

        /// <summary>
        /// Sends a ping frame
        /// </summary>
        /// <param name="payload">The payload (must be 125 bytes of less)</param>
        /// <param name="cancellationToken">The cancellation token</param>
        public async Task SendPingAsync(ArraySegment<byte> payload, CancellationToken cancellationToken = default(CancellationToken))
        {
            await this._webSocket.SendPingAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        protected virtual void OnPong(PongEventArgs args)
        {
            Pong?.Invoke(this, args);
        }

        async Task PingForeverAsync()
        {
            Events.Log.PingPongManagerStarted(this._guid, (int)this._keepAliveInterval.TotalSeconds);
            try
            {
                while (!this._cancellationToken.IsCancellationRequested)
                {
					if (!this._cancellationToken.IsCancellationRequested)
						await Task.Delay(this._keepAliveInterval, this._cancellationToken).ConfigureAwait(false);

                    if (this._webSocket.State != WebSocketState.Open)
						break;

					if (this._pingSentTicks != 0)
                    {
                        Events.Log.KeepAliveIntervalExpired(this._guid, (int)this._keepAliveInterval.TotalSeconds);
                        await this._webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, $"No Pong message received in response to a Ping after KeepAliveInterval {this._keepAliveInterval}", this._cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    if (!this._cancellationToken.IsCancellationRequested)
                    {
                        this._pingSentTicks = this._stopwatch.Elapsed.Ticks;
                        var buffer = new ArraySegment<byte>(BitConverter.GetBytes(this._pingSentTicks));
						await this.SendPingAsync(buffer, this._cancellationToken).ConfigureAwait(false);
					}
				}
            }
            catch (OperationCanceledException)
            {
                // normal, do nothing
            }
			Events.Log.PingPongManagerEnded(this._guid);
        }
        
        void WebSocketImpl_Pong(object sender, PongEventArgs e)
        {
            this._pingSentTicks = 0;
            this.OnPong(e);
        }       
    }

	// --------------------------------------------------

	/// <summary>
	/// Pong EventArgs
	/// </summary>
	public class PongEventArgs : EventArgs
	{
		/// <summary>
		/// The data extracted from a Pong WebSocket frame
		/// </summary>
		public ArraySegment<byte> Payload { get; private set; }

		/// <summary>
		/// Initialises a new instance of the PongEventArgs class
		/// </summary>
		/// <param name="payload">The pong payload must be 125 bytes or less (can be zero bytes)</param>
		public PongEventArgs(ArraySegment<byte> payload)
		{
			this.Payload = payload;
		}
	}
}
