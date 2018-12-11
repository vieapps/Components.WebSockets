namespace net.vieapps.Components.WebSockets
{
	public enum WebSocketOpCode
	{
		Continuation = 0,
		Text = 1,
		Binary = 2,
		ConnectionClose = 8,
		Ping = 9,
		Pong = 10
	}
}