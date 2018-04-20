using System;

namespace net.vieapps.Components.WebSockets.Exceptions
{
    [Serializable]
    public class ListenerSocketException : Exception
    {
        public ListenerSocketException() : base() { }

        public ListenerSocketException(string message) : base(message) { }

        public ListenerSocketException(string message, Exception inner) : base(message, inner) { }
    }
}