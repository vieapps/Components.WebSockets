# Components.WebSockets

A concrete implementation of the .NET Standard 2.0 System.Net.WebSockets.WebSocket abstract class

A WebSocket library that allows you to make WebSocket connections as a client or to respond to WebSocket requests as a server.
You can safely pass around a general purpose WebSocket instance throughout your codebase without tying yourself strongly to this library.
This is the same WebSocket abstract class used by .NET Core 2.0 and it allows for asynchronous Websocket communication for improved performance and scalability.

## Getting started (from the ground)

As a client, use the WebSocketClientFactory

```csharp
var factory = new WebSocketClientFactory();
var webSocket = await factory.ConnectAsync(new Uri("wss://example.com")).ConfigureAwait(false);
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

Client and Server send and receive data the same way.

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
    var uri = new Uri("ws://localhost:88909/notifications");
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

## Fly on the sky with Event drivent

As a client, use the WebSocketClient

Use constructor with URI of the end-point want to connect to

Use Start method to start the client with 6 action parameters:

- Action onSuccess: Fired when the client is started successfully
- Action<Exception> onFailed: Fired when the client is failed to start
- Action<WebSocketConnection, WebSocketReceiveResult, ArraySegment<byte>> onMessageReceived: Fired when got a message that sent from a server
- Action<WebSocketConnection> onConnectionEstablished: Fired when the connection is established
- Action<WebSocketConnection> onConnectionBroken: Fired when the connection is broken
- Action<WebSocketConnection, Exception> onConnectionError: Fired when the connection got an error

```csharp
var wsClient = new WebSocketClient("ws://24.4.77.3:8899/");
wsClient.Start(
    () => Console.WriteLine("The client is stared"),
    (ex) => Console.WriteLine($"Cannot start the client: {ex.Message}"),
    (conn, result, buffer) => Console.WriteLine($"Client got a message: {(result.MessageType == WebSocketMessageType.Text ? buffer.GetString(result.Count) : "BIN")}"),
    (conn) => Console.WriteLine($"Client got an open connection: {conn.IsWebSocketClientConnection}"),
    (conn) => Console.WriteLine($"Client got a broken connection: {conn.IsWebSocketClientConnection}"),
    (conn, ex) => Console.WriteLine($"Client got an error of a connection: {conn.IsWebSocketClientConnection}")
);

```

As a server, use the WebSocketServer

Use constructor with port for listing all incomming request

Use Start method to start the server with 6 action parameters:

- Action onSuccess: Fired when the server is started successfully
- Action<Exception> onFailed: Fired when the server is failed to start
- Action<WebSocketConnection, WebSocketReceiveResult, ArraySegment<byte>> onMessageReceived: Fired when got a message that sent from a client
- Action<WebSocketConnection> onConnectionEstablished: Fired when the connection is established
- Action<WebSocketConnection> onConnectionBroken: Fired when the connection is broken
- Action<WebSocketConnection, Exception> onConnectionError: Fired when the connection got an error

```csharp
var wsServer = new WebSocketServer(8899);
wsServer.Start(
    () => Console.WriteLine("The server is stared"),
    (ex) => Console.WriteLine($"Cannot start the server: {ex.Message}"),
    (conn, result, buffer) => Console.WriteLine($"Server got a message: {(result.MessageType == WebSocketMessageType.Text ? buffer.GetString(result.Count) : "BIN")}"),
    (conn) => Console.WriteLine($"Server got an open connection: {conn.IsWebSocketClientConnection}"),
    (conn) => Console.WriteLine($"Server got a broken connection: {conn.IsWebSocketClientConnection}"),
    (conn, ex) => Console.WriteLine($"Server got an error of a connection: {conn.IsWebSocketClientConnection}")
);

```

And have a look at static class WebSocketConnectionManager to play aroud with connections

## License

This project is licensed under the MIT License - see the LICENSE.md file for details
