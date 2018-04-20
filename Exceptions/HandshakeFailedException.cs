using System;

namespace net.vieapps.Components.WebSockets.Exceptions
{
    [Serializable]
    public class HandshakeFailedException : Exception
    {
        public HandshakeFailedException() : base() { }

        public HandshakeFailedException(string message) : base(message) { }

        public HandshakeFailedException(string message, Exception inner) : base(message, inner) { }
    }
}
