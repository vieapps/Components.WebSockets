namespace net.vieapps.Components.WebSockets
{
	public enum WebSocketOpCode
	{
		/// <summary>
		/// Continuous message
		/// </summary>
		Continuation = 0,

		/// <summary>
		/// Text message
		/// </summary>
		Text = 1,

		/// <summary>
		/// Binary message
		/// </summary>
		Binary = 2,

		/// <summary>
		/// Closing message
		/// </summary>
		ConnectionClose = 8,

		/// <summary>
		/// Ping message
		/// </summary>
		Ping = 9,

		/// <summary>
		/// Pong message
		/// </summary>
		Pong = 10
	}
}