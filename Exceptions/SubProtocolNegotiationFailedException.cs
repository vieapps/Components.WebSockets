using System;

namespace net.vieapps.Components.WebSockets.Exceptions
{
	[Serializable]
	public class SubProtocolNegotiationFailedException : Exception
	{
		public SubProtocolNegotiationFailedException() : base() { }

		public SubProtocolNegotiationFailedException(string message) : base(message) { }

		public SubProtocolNegotiationFailedException(string message, Exception innerException) : base(message, innerException) { }
	}
}