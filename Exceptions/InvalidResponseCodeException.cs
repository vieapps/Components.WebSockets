using System;

namespace net.vieapps.Components.WebSockets.Exceptions
{
	[Serializable]
	public class InvalidResponseCodeException : Exception
	{
		public string ResponseCode { get; private set; }

		public string ResponseHeader { get; private set; }

		public string ResponseDetails { get; private set; }

		public InvalidResponseCodeException() : base() { }

		public InvalidResponseCodeException(string message) : base(message) { }

		public InvalidResponseCodeException(string responseCode, string responseDetails, string responseHeader) : base(responseCode)
		{
			this.ResponseCode = responseCode;
			this.ResponseDetails = responseDetails;
			this.ResponseHeader = responseHeader;
		}

		public InvalidResponseCodeException(string message, Exception inner) : base(message, inner) { }
	}
}
