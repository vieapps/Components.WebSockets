# VIEApps.Components.WebSockets

A concrete implementation of the System.Net.WebSockets.WebSocket abstract class on .NET Standard 2.0

A WebSocket library that allows you to make WebSocket connections as a client or to respond to WebSocket requests as a server.
You can safely pass around a general purpose WebSocket instance throughout your codebase without tying yourself strongly to this library.
This is the same WebSocket abstract class used by .NET Standard 2.0 and it allows for asynchronous WebSocket communication for improved performance and scalability.

## NuGet
- Package ID: VIEApps.Components.WebSockets
- Details: https://www.nuget.org/packages/VIEApps.Components.WebSockets

## Walking on the ground

The class **net.vieapps.Components.WebSockets.Implementation.WebSocket** is an implementation of the System.Net.WebSockets.WebSocket abstract class,
that allows you send and receive messages in the same way for both client and server side.

### Receiving messages:
```csharp
async Task ReceiveAsync(Implementation.WebSocket websocket)
{
    var buffer = new ArraySegment<byte>(new byte[1024]);
    while (true)
    {
        WebSocketReceiveResult result = await websocket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
        switch (result.MessageType)
        {
            case WebSocketMessageType.Close:
                return;
            case WebSocketMessageType.Text:
            case WebSocketMessageType.Binary:
                var value = Encoding.UTF8.GetString(buffer, result.Count);
                Console.WriteLine(value);
                break;
        }
    }
}
```

### Sending messages:
```csharp
async Task SendAsync(Implementation.WebSocket websocket)
{
    var buffer = new ArraySegment<byte>(Encoding.UTF8.GetBytes("Hello World"));
    await websocket.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
} 
```

### Useful properties:
```csharp
public Guid ID { get; }
public bool IsClient { get; }
public DateTime Time { get; }
public string LocalEndPoint { get; }
public string RemoteEndPoint { get; }
```

## Fly on the sky with Event-liked driven

### Using the WebSocket class

This is a centralized element for working with both client and server mode.
This class has 4 action properties (event handlers) to take are all cases of work, you just need to assign your code to cover its.
```csharp
Action<WebSocket, Exception> OnError; // fire when got any error
Action<WebSocket> OnConnectionEstablished; // fire when a connection is established
Action<WebSocket> OnConnectionBroken; // fire when a connection is broken
Action<WebSocket, WebSocketReceiveResult, byte[]> OnMessageReceived; // fire when got a message (when a message is received)
```

And this class has some methods for working on both client and server role:
```csharp
void Connect(Uri uri, Action<Implementation.WebSocket> onSuccess, Action<Exception> onFailed);
void Connect(string location, Action<Implementation.WebSocket> onSuccess, Action<Exception> onFailed);
void StartListen(int port, X509Certificate2 certificate, Action onSuccess, Action<Exception> onFailed);
void StartListen(int port, Action onSuccess, Action<Exception> onFailed);
void StopListen(bool doCancel);
```

### WebSocket client

Use the **Connect** method to connect to a remote endpoint

### WebSocket server

Use the **StartListen** method to start the listener to listen incomming connection requests.

Use the **StopListen** method to stop the listener.

### WebSocket server with Secure WebSockets (wss://)

Enabling secure connections requires two things:
- Pointing certificate to an x509 certificate that containing a public and private key.
- Using the scheme **wss** instead of **ws** (or **https** instead of **http**) on all clients

```csharp
var websocket = new WebSocket();
websocket.Certificate = new X509Certificate2("my-certificate.pfx");
// websocket.Certificate = new X509Certificate2("my-certificate.pfx", "cert-password", X509KeyStorageFlags.UserKeySet);
websocket.Start(46429);
```

Want to have a free SSL certificate? Take a look at [Lets Encrypt](https://letsencrypt.org/).

Special: A very simple tool named [lets-encrypt-win-simple](https://github.com/PKISharp/win-acme) will help your IIS works with Lets Encrypt very well.

### Receiving and Sending messages:

Messages are received automatically via parallel tasks, and you only need to assign **OnMessageReceived** event for handling its.

Sending messages are the same with **Implementation.WebSocket** class with a little diffirent: you need a WebSocket connection (instance of *Implementation.WebSocket*) that specified by an identity.

### Connection management

Take a look at some methods named GetWebSocket, GetWebSocket, CloseWebSocket, ... to work with WebSocket connections.

## Others

### The important things

- 16K is default length of the buffer for receiving messages (its large enough for most case because we are usually use WebSocket to send/receive small data), and to change the length of the buffer to receive more large messages, use the static method **SetBufferLength** of the *WebSocket* class.
- If the incomming messages is continuous messages, the type always be "Binary", and the property named "EndOfMessage" is "true" in the last message - "false" in the previous messages (the second parameter of OnMessageReceived - type: WebSocketReceiveResult).
- Some portion of codes are reference from [NinjaSource WebSocket](https://github.com/ninjasource/Ninja.WebSockets)

### Logging

Can be any provider that supports extension of Microsoft.Extensions.Logging (via dependency injection).

Our prefers:
- [Microsoft.Extensions.Logging.Console](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Console): live logs
- [Serilog.Extensions.Logging.File](https://www.nuget.org/packages/Serilog.Extensions.Logging.File): for rollinng log files (by date) - high performance, and very simple to use

### Dependencies

- Microsoft.Extensions.Logging.Abstractions
- Microsoft.IO.RecyclableMemoryStream
- VIEApps.Components.Utility

### Namespaces

```csharp
using net.vieapps.Components.Utility;
using net.vieapps.Components.WebSockets;
```
