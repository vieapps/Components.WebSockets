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
	internal class PingPongManager
	{
		readonly WebSocketImplementation _websocket;
		readonly CancellationToken _cancellationToken;
		readonly Action<ManagedWebSocket, byte[]> _onPong;
		readonly Func<ManagedWebSocket, byte[], byte[]> _getPongPayload;
		readonly Func<ManagedWebSocket, byte[]> _getPingPayload;
		long _pingTimestamp = 0;

		public PingPongManager(WebSocketImplementation websocket, WebSocketOptions options, CancellationToken cancellationToken)
		{
			this._websocket = websocket;
			this._cancellationToken = cancellationToken;
			this._getPongPayload = options.GetPongPayload;
			this._onPong = options.OnPong;
			if (this._websocket.KeepAliveInterval != TimeSpan.Zero)
			{
				this._getPingPayload = options.GetPingPayload;
				Task.Run(this.SendPingAsync).ConfigureAwait(false);
			}
		}

		public void OnPong(byte[] pong)
		{
			this._pingTimestamp = 0;
			this._onPong?.Invoke(this._websocket, pong);
		}

		public ValueTask SendPongAsync(byte[] ping)
			=> this._websocket.SendPongAsync((this._getPongPayload?.Invoke(this._websocket, ping) ?? ping).ToArraySegment(), this._cancellationToken);

		public async Task SendPingAsync()
		{
			Events.Log.PingPongManagerStarted(this._websocket.ID, this._websocket.KeepAliveInterval.TotalSeconds.CastAs<int>());
			try
			{
				while (!this._cancellationToken.IsCancellationRequested)
				{
					await Task.Delay(this._websocket.KeepAliveInterval, this._cancellationToken).ConfigureAwait(false);
					if (this._websocket.State != WebSocketState.Open)
						break;

					if (this._pingTimestamp != 0)
					{
						Events.Log.KeepAliveIntervalExpired(this._websocket.ID, (int)this._websocket.KeepAliveInterval.TotalSeconds);
						await this._websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, $"No PONG message received in response to a PING message after keep-alive interval ({this._websocket.KeepAliveInterval})", this._cancellationToken).ConfigureAwait(false);
						break;
					}

					this._pingTimestamp = DateTime.Now.ToUnixTimestamp();
					await this._websocket.SendPingAsync((this._getPingPayload?.Invoke(this._websocket) ?? this._pingTimestamp.ToBytes()).ToArraySegment(), this._cancellationToken).ConfigureAwait(false);
				}
			}
			catch { }
			Events.Log.PingPongManagerEnded(this._websocket.ID);
		}
	}
}