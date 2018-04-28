using System;

namespace net.vieapps.Components.WebSockets.Implementation
{
	/// <summary>
	/// WebSocket OpCode
	/// </summary>
	internal enum WebSocketOpCode
	{
		ContinuationFrame = 0,
		TextFrame = 1,
		BinaryFrame = 2,
		ConnectionClose = 8,
		Ping = 9,
		Pong = 10
	}
}