namespace net.vieapps.Components.WebSockets
{
	internal enum WebSocketOpCode
	{
		Continuation = 0,
		Text = 1,
		Binary = 2,
		ConnectionClose = 8,
		Ping = 9,
		Pong = 10
	}
}