using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;

namespace net.vieapps.Components.WebSockets.Exceptions
{
    [Serializable]
    public class ServerListenerSocketException : Exception
    {
        public ServerListenerSocketException() : base()
        {            
        }

        public ServerListenerSocketException(string message) : base(message)
        {            
        }

        public ServerListenerSocketException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
