using System;

namespace net.vieapps.Components.WebSockets.Exceptions
{
	[Serializable]
	public class KeyMissingException : Exception
	{
		public KeyMissingException() : base() { }

		public KeyMissingException(string message) : base(message) { }

		public KeyMissingException(string message, Exception inner) : base(message, inner) { }
	}
}
