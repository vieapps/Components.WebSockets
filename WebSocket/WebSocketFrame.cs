using System;
using System.Net.WebSockets;

namespace net.vieapps.Components.WebSockets.Implementation
{
	public class WebSocketFrame
	{
		public bool IsFinBitSet { get; private set; }

		public WebSocketOpCode OpCode { get; private set; }

		public int Count { get; private set; }

		public WebSocketCloseStatus? CloseStatus { get; private set; }

		public string CloseStatusDescription { get; private set; }

		public WebSocketFrame(bool isFinBitSet, WebSocketOpCode webSocketOpCode, int count)
		{
			this.IsFinBitSet = isFinBitSet;
			this.OpCode = webSocketOpCode;
			this.Count = count;
		}

		public WebSocketFrame(bool isFinBitSet, WebSocketOpCode webSocketOpCode, int count, WebSocketCloseStatus closeStatus, string closeStatusDescription) : this(isFinBitSet, webSocketOpCode, count)
		{
			this.CloseStatus = closeStatus;
			this.CloseStatusDescription = closeStatusDescription;
		}

		public const int MaskKeyLength = 4;

		/// <summary>
		/// Mutate payload with the mask key
		/// This is a reversible process
		/// If you apply this to masked data it will be unmasked and visa versa
		/// </summary>
		/// <param name="maskKey">The 4 byte mask key</param>
		/// <param name="payload">The payload to mutate</param>
		public static void ToggleMask(ArraySegment<byte> maskKey, ArraySegment<byte> payload)
		{
			if (maskKey.Count != WebSocketFrame.MaskKeyLength)
				throw new Exception($"MaskKey key must be {WebSocketFrame.MaskKeyLength} bytes");

			var buffer = payload.Array;
			var maskKeyArray = maskKey.Array;

			// apply the mask key (this is a reversible process so no need to copy the payload)
			for (var index = payload.Offset; index < payload.Count; index++)
			{
				int payloadIndex = index - payload.Offset; // index should start at zero
				int maskKeyIndex = maskKey.Offset + (payloadIndex % WebSocketFrame.MaskKeyLength);
				buffer[index] = (Byte)(buffer[index] ^ maskKeyArray[maskKeyIndex]);
			}
		}
	}
}