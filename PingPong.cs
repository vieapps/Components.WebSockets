#region Related components
using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Components.WebSockets
{
	/// <summary>
	/// Pong EventArgs
	/// </summary>
	internal class PongEventArgs : EventArgs
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

	// --------------------------------------------------

	/// <summary>
	/// Ping Pong Manager used to facilitate ping pong WebSocket messages
	/// </summary>
	internal interface IPingPongManager
	{
		/// <summary>
		/// Raised when a Pong frame is received
		/// </summary>
		event EventHandler<PongEventArgs> Pong;

		/// <summary>
		/// Sends a ping frame
		/// </summary>
		/// <param name="payload">The payload (must be 125 bytes of less)</param>
		/// <param name="cancellation">The cancellation token</param>
		Task SendPingAsync(ArraySegment<byte> payload, CancellationToken cancellation = default(CancellationToken));
	}

	// --------------------------------------------------

	/// <summary>
	/// Ping Pong Manager used to facilitate ping pong WebSocket messages
	/// </summary>
	internal class PingPongManager : IPingPongManager
	{
		readonly WebSocketImplementation _websocket;
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
		/// </summary>
		/// <param name="websocket">The WebSocket instance used to listen to ping messages and send pong messages</param>
		/// <param name="cancellationToken">The token used to cancel a pending ping send AND the automatic sending of ping messages if KeepAliveInterval is positive</param>
		public PingPongManager(WebSocketImplementation websocket, CancellationToken cancellationToken)
		{
			this._websocket = websocket;
			this._websocket.Pong += this.DoPong;
			this._cancellationToken = cancellationToken;
			this._stopwatch = Stopwatch.StartNew();
			this._pingTask = Task.Run(this.DoPingAsync);
		}

		/// <summary>
		/// Sends a ping frame
		/// </summary>
		/// <param name="payload">The payload (must be 125 bytes of less)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		public Task SendPingAsync(ArraySegment<byte> payload, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this._websocket.SendPingAsync(payload, cancellationToken);
		}

		async Task DoPingAsync()
		{
			Events.Log.PingPongManagerStarted(this._websocket.ID, (int)this._websocket.KeepAliveInterval.TotalSeconds);
			try
			{
				while (!this._cancellationToken.IsCancellationRequested)
				{
					await Task.Delay(this._websocket.KeepAliveInterval, this._cancellationToken).ConfigureAwait(false);
					if (this._websocket.State != WebSocketState.Open)
						break;

					if (this._pingSentTicks != 0)
					{
						Events.Log.KeepAliveIntervalExpired(this._websocket.ID, (int)this._websocket.KeepAliveInterval.TotalSeconds);
						await this._websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, $"No Pong message received in response to a Ping after KeepAliveInterval ({this._websocket.KeepAliveInterval})", this._cancellationToken).ConfigureAwait(false);
						break;
					}

					this._pingSentTicks = this._stopwatch.Elapsed.Ticks;
					await this.SendPingAsync(this._pingSentTicks.ToArraySegment(), this._cancellationToken).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException)
			{
				// normal, do nothing
			}
			Events.Log.PingPongManagerEnded(this._websocket.ID);
		}

		protected virtual void OnPong(PongEventArgs args)
		{
			this.Pong?.Invoke(this, args);
		}

		void DoPong(object sender, PongEventArgs arg)
		{
			this._pingSentTicks = 0;
			this.OnPong(arg);
		}
	}
}