# Components.WebSockets

A concrete implementation of the System.Net.WebSockets.WebSocket abstract class on .NET Standard 2.0

A WebSocket library that allows you to make WebSocket connections as a client or to respond to WebSocket requests as a server.
You can safely pass around a general purpose WebSocket instance throughout your codebase without tying yourself strongly to this library.
This is the same WebSocket abstract class used by .NET Core 2.0 and it allows for asynchronous WebSocket communication for improved performance and scalability.

## NuGet
- Package ID: VIEApps.Components.Utility
- Details: https://www.nuget.org/packages/VIEApps.Components.WebSockets/

## Walking on the ground

As a client, use the WebSocketClientFactory

```csharp
var factory = new WebSocketClientFactory();
var webSocket = await factory.ConnectAsync(new Uri("ws://localhost:56789/")).ConfigureAwait(false);
```

As a server, use the WebSocketServerFactory

```csharp
var stream = tcpClient.GetStream();
var factory = new WebSocketServerFactory();
var context = await factory.ReadHttpHeaderFromStreamAsync(stream).ConfigureAwait(false);

if (context.IsWebSocketRequest)
{
    var webSocket = await factory.AcceptWebSocketAsync(context).ConfigureAwait(false);
}
```
## Using the WebSocket class

Client and Server send and receive data in the same way.

### Receiving data:

Receive data in an infinite loop until we receive a close frame from the server
```csharp
async Task ReceiveAsync(WebSocket webSocket)
{
    var buffer = new ArraySegment<byte>(new byte[1024]);
    while (true)
    {
        WebSocketReceiveResult result = await webSocket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
        switch (result.MessageType)
        {
            case WebSocketMessageType.Close:
                return;
            case WebSocketMessageType.Text:
            case WebSocketMessageType.Binary:
                var value = Encoding.UTF8.GetString(buffer.Array, 0, result.Count);
                Console.WriteLine(value);
                break;
        }
    }
}
```

### Sending data:
```csharp
async Task SendAsync(WebSocket webSocket)
{
    var array = Encoding.UTF8.GetBytes("Hello World");
    var buffer = new ArraySegment<byte>(array);
    await webSocket.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
} 
```

### Simple client request / response:
The best approach to communicating using a web socket is to send and receive data on different worker threads as shown below. 

```csharp
public async Task Run()
{
    var factory = new WebSocketClientFactory();
    var uri = new Uri("ws://localhost:56789/notifications");
    using (var webSocket = await factory.ConnectAsync(uri).ConfigureAwait(false))
    {
        // receive loop
        var readTask = ReceiveAsync(webSocket);

        // send a message
        await SendAsync(webSocket).ConfigureAwait(false);

        // initiate the close handshake
        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);

        // wait for server to respond with a close frame
        await readTask.ConfigureAwait(false);
    }
}
```

## Fly on the sky with Event-liked driven

### WebSocketClient

As a client, use the WebSocketClient

Use constructor with URI of the end-point want to connect to

Use Start method to start the client with 6 action parameters:

- onStartSuccess: Fired when the client is started successfully
- onStarFailed: Fired when the client is failed to start
- onError: Fired when the client got an error exception while processing/receiving
- onConnectionEstablished: Fired when the connection is established
- onConnectionBroken: Fired when the connection is broken
- onMessageReceived: Fired when the client got a message

```csharp
var wsClient = new WebSocketClient("ws://localhost:8899/");
wsClient.Start(
    () => Console.WriteLine("The client is stared"),
    (ex) => Console.WriteLine($"Cannot start the client: {ex.Message}"),
    (ex) => Console.WriteLine($"Client got an error: {ex.Message}"),
    (conn) => Console.WriteLine($"Client got an open connection: {conn.ID} - {conn.EndPoint}"),
    (conn) => Console.WriteLine($"Client got a broken connection: {conn.ID} - {conn.EndPoint}"),
    (conn, result, buffer) => Console.WriteLine($"Client got a message: {(result.MessageType == WebSocketMessageType.Text ? buffer.GetString(result.Count) : "BIN")}")
);

```

Or if you don't like these function parameters, just assign event handlers by your code

```csharp
var wsClient = new WebSocketClient("ws://localhost:56789/")
{
    OnStartSuccess = () =>
    {
        Console.WriteLine("The client is stared");
    },
    OnStartFailed = (ex) =>
    {
        Console.WriteLine($"Cannot start the client: {ex.Message}");
    },
    OnError = (ex) =>
    {
        Console.WriteLine($"Client got an error: {ex.Message}");
    },
    OnConnectionEstablished = (conn) =>
    {
        Console.WriteLine($"Client got an open connection: {conn.ID}");
    },
    OnConnectionBroken = (conn) =>
    {
        Console.WriteLine($"Client got a broken connection: {conn.ID}");
    },
    OnMessageReceived = (conn, result, buffer) =>
    {
        Console.WriteLine($"Client got a message: {(result.MessageType == WebSocketMessageType.Text ? buffer.GetString(result.Count) : "BIN")}");
    }
};
wsClient.Start();

```
### WebSocketServer

As a server, use the WebSocketServer

Use constructor with port for listing all incomming requests

Use Start method to start the server with 6 action parameters:

- onStartSuccess: Fired when the server is started successfully
- onStarFailed: Fired when the server is failed to start
- onError: Fired when the server got an error exception while processing/receiving
- onConnectionEstablished: Fired when the connection is established
- onConnectionBroken: Fired when the connection is broken
- onMessageReceived: Fired when the server got a message

```csharp
var wsServer = new WebSocketServer(56789);
wsServer.Start(
    () => Console.WriteLine("The server is stared"),
    (ex) => Console.WriteLine($"Cannot start the server: {ex.Message}"),
    (ex) => Console.WriteLine($"Server got an error: {ex.Message}"),
    (conn) => Console.WriteLine($"Server got an open connection: {conn.ID} - {conn.EndPoint}"),
    (conn) => Console.WriteLine($"Server got a broken connection: {conn.ID} - {conn.EndPoint}"),
    (conn, result, buffer) => Console.WriteLine($"Server got a message: {(result.MessageType == WebSocketMessageType.Text ? buffer.GetString(result.Count) : "BIN")}")
);

```

Or if you don't like these function parameters, just assign event handlers by your code

```csharp
var wsServer = new WebSocketServer(56789)
{
    OnStartSuccess = () =>
    {
        Console.WriteLine("The server is stared");
    },
    OnStartFailed = (ex) =>
    {
        Console.WriteLine($"Cannot start the server: {ex.Message}");
    },
    OnError = (ex) =>
    {
        Console.WriteLine($"Server got an error: {ex.Message}");
    },
    OnConnectionEstablished = (conn) =>
    {
        Console.WriteLine($"Server got an open connection: {conn.ID}");
    },
    OnConnectionBroken = (conn) =>
    {
        Console.WriteLine($"Server got a broken connection: {conn.ID}");
    },
    OnMessageReceived = (conn, result, buffer) =>
    {
        Console.WriteLine($"Server got a message: {(result.MessageType == WebSocketMessageType.Text ? buffer.GetString(result.Count) : "BIN")}");
    }
};
wsServer.Start();
```

And if you want to see all current connections of the server, then take a look at property "Connections" of the server.

### WebSocketServer with Secure WebSockets (wss://)

Enabling secure connections requires two things:
- Pointing certificate to an x509 certificate containing a public and private key.
- Using the scheme 'wss://' instead of 'ws://' (or 'https://' instead of 'http://') on all clients

```csharp
var wsServer = new WebSocketServer(56789);
wsServer.Certificate = new X509Certificate2("my-certificate.pfx"); // wsServer.Certificate = new X509Certificate2("my-certificate.pfx", "cert-password", X509KeyStorageFlags.UserKeySet);
wsServer.Start();
```

### WebSocketConnectionManager

And take a look at static class WebSocketConnectionManager to play aroud with connections, that is centralized management of all current connections

## Namespaces
```csharp
using net.vieapps.Components.Utility;
using net.vieapps.Components.WebSockets;
```
