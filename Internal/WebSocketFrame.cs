using System.Net.WebSockets;

namespace net.vieapps.Components.WebSockets.Internal
{
    internal class WebSocketFrame
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
    }
}