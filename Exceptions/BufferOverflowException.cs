using System;

namespace net.vieapps.Components.WebSockets.Exceptions
{
    [Serializable]
    public class BufferOverflowException : Exception
    {
        public BufferOverflowException() : base() { }

        public BufferOverflowException(string message) : base(message) { }

        public BufferOverflowException(string message, Exception inner) : base(message, inner) { }
    }
}