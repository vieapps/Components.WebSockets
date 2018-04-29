using System;

namespace net.vieapps.Components.WebSockets.Exceptions
{
	[Serializable]
	public class WrapWebSocketFailedException : Exception
	{
		public WrapWebSocketFailedException() : base() { }

		public WrapWebSocketFailedException(string message) : base(message) { }

		public WrapWebSocketFailedException(string message, Exception innerException) : base(message, innerException) { }
	}
}