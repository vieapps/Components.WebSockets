using System;

namespace net.vieapps.Components.WebSockets.Exceptions
{
    [Serializable]
    public class VersionNotSupportedException : Exception
    {
        public VersionNotSupportedException() : base() { }

        public VersionNotSupportedException(string message) : base(message) { }

        public VersionNotSupportedException(string message, Exception inner) : base(message, inner) { }
    }
}
