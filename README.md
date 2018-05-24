# VIEApps.Components.WebSockets

A concrete implementation of the *System.Net.WebSockets.WebSocket* abstract class on .NET Standard 2.0,
that allows you to make WebSocket connections as a client or to respond to WebSocket requests as a server
(or wrap existing WebSocket connections of ASP.NET / ASP.NET Core).

## NuGet
- Package ID: **VIEApps.Components.WebSockets**
- Details: https://www.nuget.org/packages/VIEApps.Components.WebSockets

## Walking on the ground

The class **ManagedWebSocket** is an implementation or a wrapper of the *System.Net.WebSockets.WebSocket* abstract class,
that allows you send and receive messages in the same way for both side of client and server role.

### Receiving messages:
```csharp
async Task ReceiveAsync(ManagedWebSocket websocket)
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
async Task SendAsync(ManagedWebSocket websocket)
{
	var buffer = new ArraySegment<byte>(Encoding.UTF8.GetBytes("Hello World"));
	await websocket.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
} 
```

### Useful properties:

```csharp
// the identity of the connection
public Guid ID { get; }

// true if the connection was made when connect to a remote endpoint (mean client role)
public bool IsClient { get; }

// original requesting URI of the connection
public Uri RequestUri { get; }

// the time when the connection is established
public DateTime Timestamp { get; }

// the remote endpoint
public EndPoint RemoteEndPoint { get; }

// the local endpoint
public EndPoint LocalEndPoint { get; }
```

## Fly on the sky with Event-liked driven

### Using the WebSocket class

This is a centralized element for working with both side of client and server role.
This class has 04 action properties (event handlers) to take care of all working cases, you just need to assign your code to cover its.

```csharp
// fire when got any error
Action<ManagedWebSocket, Exception> OnError;

// fire when a connection is established
Action<ManagedWebSocket> OnConnectionEstablished;

// fire when a connection is broken
Action<ManagedWebSocket> OnConnectionBroken;

// fire when a message is received
Action<ManagedWebSocket, WebSocketReceiveResult, byte[]> OnMessageReceived;
```

And this class has some methods for working on both side of client and server role:
```csharp
void Connect(Uri uri, string subProtocol, Action<ManagedWebSocket> onSuccess, Action<Exception> onFailed);
void StartListen(int port, X509Certificate2 certificate, Action onSuccess, Action<Exception> onFailed);
void StopListen();
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
var websocket = new WebSocket
{
	Certificate = new X509Certificate2("my-certificate.pfx")
	// Certificate = new X509Certificate2("my-certificate.pfx", "cert-password", X509KeyStorageFlags.UserKeySet)
};
websocket.StartListen(46429);
```

Want to have a free SSL certificate? Take a look at [Let's Encrypt](https://letsencrypt.org/).

Special: A simple tool named [lets-encrypt-win-simple](https://github.com/PKISharp/win-acme) will help your IIS works with Let's Encrypt very well.

### Wrap an existing WebSocket connection of ASP.NET / ASP.NET Core

When integrate this component with your app that hosted by ASP.NET / ASP.NET Core, you might want to use the WebSocket connections of ASP.NET / ASP.NET Core directly,
then the method **WrapAsync** is here to help. This method will return a task that run a process for receiving messages from this WebSocket connection.

```csharp
Task WrapAsync(System.Net.WebSockets.WebSocket webSocket, Uri requestUri, EndPoint remoteEndPoint, EndPoint localEndPoint);
```

And might be you need an extension method to wrap an existing WebSocket connection, then take a look at some lines of code below:

**ASP.NET**

```csharp
public static Task WrapAsync(this net.vieapps.Components.WebSockets.WebSocket websocket, AspNetWebSocketContext context)
{
	var serviceProvider = (IServiceProvider)HttpContext.Current;
	var httpWorker = serviceProvider?.GetService<HttpWorkerRequest>();
	var remoteAddress = httpWorker == null ? context.UserHostAddress : httpWorker.GetRemoteAddress();
	var remotePort = httpWorker == null ? 0 : httpWorker.GetRemotePort();
	var remoteEndpoint = IPAddress.TryParse(remoteAddress, out IPAddress ipAddress)
		? new IPEndPoint(ipAddress, remotePort > 0 ? remotePort : context.RequestUri.Port) as EndPoint
		: new DnsEndPoint(context.UserHostName, remotePort > 0 ? remotePort : context.RequestUri.Port) as EndPoint;
	var localAddress = httpWorker == null ? context.RequestUri.Host : httpWorker.GetLocalAddress();
	var localPort = httpWorker == null ? 0 : httpWorker.GetLocalPort();
	var localEndpoint = IPAddress.TryParse(localAddress, out ipAddress)
		? new IPEndPoint(ipAddress, localPort > 0 ? localPort : context.RequestUri.Port) as EndPoint
		: new DnsEndPoint(context.RequestUri.Host, localPort > 0 ? localPort : context.RequestUri.Port) as EndPoint;
	return websocket.WrapAsync(context.WebSocket, context.RequestUri, remoteEndpoint, localEndpoint);
}
```

**ASP.NET Core**

```csharp
public static async Task WrapAsync(this net.vieapps.Components.WebSockets.WebSocket websocket, HttpContext context)
{
	if (context.WebSockets.IsWebSocketRequest)
	{
		var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
		var requestUri = new Uri($"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}{context.Request.PathBase}{context.Request.QueryString}");
		var remoteEndPoint = new IPEndPoint(context.Connection.RemoteIpAddress, context.Connection.RemotePort);
		var localEndPoint = new IPEndPoint(context.Connection.LocalIpAddress, context.Connection.LocalPort);
		await websocket.WrapAsync(webSocket, requestUri, remoteEndPoint, localEndPoint).ConfigureAwait(false);
	}
}
```

While working with ASP.NET Core, we think that you need a middle-ware to handle all request of WebSocket connections, just look like this:

```csharp
public class WebSocketMiddleware
{
	readonly RequestDelegate _next;
	net.vieapps.Components.WebSockets.WebSocket _websocket;

	public WebSocketMiddleware(RequestDelegate next, ILoggerFactory loggerFactory)
	{
		var logger = loggerFactory.CreateLogger<WebSocketMiddleware>();
		this._websocket = new net.vieapps.Components.WebSockets.WebSocket(loggerFactory)
		{
			OnError = (websocket, exception) =>
			{
				logger.LogError(exception, $"Got an error: {websocket?.ID} @ {websocket?.RemoteEndPoint} => {exception.Message}");
			},
			OnConnectionEstablished = (websocket) =>
			{
				logger.LogDebug($"Connection is established: {websocket.ID} @ {websocket.RemoteEndPoint}");
			},
			OnConnectionBroken = (websocket) =>
			{
				logger.LogDebug($"Connection is broken: {websocket.ID} @ {websocket.RemoteEndPoint}");
			},
			OnMessageReceived = (websocket, result, data) =>
			{
				var message = result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text ? data.GetString() : "(binary message)";
				logger.LogDebug($"Got a message: {websocket.ID} @ {websocket.RemoteEndPoint} => {message}");
			}
		};
		this._next = next;
	}

	public async Task Invoke(HttpContext context)
	{
		await this._websocket.WrapAsync(context).ConfigureAwait(false);
		await this._next.Invoke(context).ConfigureAwait(false);
	}
}
```

And remember to tell APS.NET Core uses your middleware (at **Configure** method of *Startup.cs*)

```csharp
app.UseWebSockets();
app.UseMiddleware<WebSocketMiddleware>();
```

### Receiving and Sending messages:

Messages are received automatically via parallel tasks, and you only need to assign **OnMessageReceived** event for handling its.

Sending messages are the same as **ManagedWebSocket**, with a little different: the first argument - you need to specify a WebSocket connection (by an identity) for sending your messages.

```csharp
Task SendAsync(Guid id, ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken);
Task SendAsync(Guid id, string message, bool endOfMessage, CancellationToken cancellationToken);
Task SendAsync(Guid id, byte[] message, bool endOfMessage, CancellationToken cancellationToken);
Task SendAsync(Func<ManagedWebSocket, bool> predicate, ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken);
Task SendAsync(Func<ManagedWebSocket, bool> predicate, string message, bool endOfMessage, CancellationToken cancellationToken);
Task SendAsync(Func<ManagedWebSocket, bool> predicate, byte[] message, bool endOfMessage, CancellationToken cancellationToken);
```

### Connection management

Take a look at some methods *GetWebSocket...* to work with all connections.

```csharp
ManagedWebSocket GetWebSocket(Guid id);
IEnumerable<ManagedWebSocket> GetWebSockets(Func<ManagedWebSocket, bool> predicate);
bool CloseWebSocket(Guid id, WebSocketCloseStatus closeStatus, string closeStatusDescription);
bool CloseWebSocket(ManagedWebSocket websocket, WebSocketCloseStatus closeStatus, string closeStatusDescription);
```

## Others

### The important things

- 16K is default size of the protocol buffer for receiving messages (its large enough for most case because we are usually use WebSocket to send/receive small data). If you want to change (to receive large messages), then set a new value for the static property named **ReceiveBufferSize** of the *WebSocket* class.
- Some portion of codes are reference from [NinjaSource WebSocket](https://github.com/ninjasource/Ninja.WebSockets).

### Logging

Can be any provider that supports extension of Microsoft.Extensions.Logging (via dependency injection).

Our prefers:
- [Microsoft.Extensions.Logging.Console](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Console): live logs
- [Serilog.Extensions.Logging.File](https://www.nuget.org/packages/Serilog.Extensions.Logging.File): rolling log files (by date) - high performance, and very simple to use

### Dependencies

- [Microsoft.IO.RecyclableMemoryStream](https://www.nuget.org/packages/Microsoft.IO.RecyclableMemoryStream)
- [VIEApps.Components.Utility](https://www.nuget.org/packages/VIEApps.Components.Utility)

### Namespaces

```csharp
using net.vieapps.Components.Utility;
using net.vieapps.Components.WebSockets;
```

## A very simple stress test

### Environment

- 01 server with Windows 2012 R2 x64 on Intel Xeon E3-1220 v3 3.1GHz - 8GB RAM
- 05 clients with Windows 10 x64 and Ubuntu Linux 16.04 x64

### The scenario
- Clients (05 stations) made 20,000 concurrent connections to the server, all connections are secured (use Let's Encrypt SSL certificate)
- Clients send 02 messages per second to server (means server receives 40,000 messages/second) - size of 01 message: 1024 bytes (1K)
- Server sends 01 message to all connections (20,000 messages) each 10 minutes - size of 01 message: 1024 bytes (1K)

### The results
- Server is still alive after 01 week (60 * 24 * 7 = 10,080 minutes)
- No dropped connection
- No hang
- Used memory: 1.3 GB - 1.7 GB
- CPU usages: 3% - 15% while receiving messages, 18% - 35% while sending messages

## Performance Tuning

While working directly with this component, performance is not your problem, but when you wrap WebSockets connections of ASP.NET
or ASP.NET Corre (with IIS Integration), may be you reach max 5,000 concurrent connections (because IIS allow 5,000 CCU by default).

ASP.NET and IIS scale very well, but you'll need to change a few settings to set up your server for lots of concurrent connections,
as opposed to lots of requests per second.

### IIS Configuration

#### Max concurrent requests per application

Increase the number of concurrent requests IIS will serve at once:

Open an administrator command prompt at ***%windir%\System32\inetsrv\ ***
Run the command below to update the **appConcurrentRequestLimit** attribute to a suitable number (5000 is the default in IIS7+)

Example:

**appcmd.exe set config /section:system.webserver/serverRuntime /appConcurrentRequestLimit:100000**

### ASP.NET Configuration

#### Maximum Concurrent Requests Per CPU

By default ASP.NET 4.0 sets the maximum concurrent connections to 5000 per CPU. If you need more concurrent connections then you need to increase the maxConcurrentRequestsPerCPU setting.

Open ***%windir%\Microsoft.NET\Framework\v4.0.30319\aspnet.config*** (**Framework64** for 64 bit processes)

Copy from the sample below (ensure case is correct!)

Example:

```xml
<?xml version="1.0" encoding="UTF-8" ?>
<configuration>
    <runtime>
        <legacyUnhandledExceptionPolicy enabled="false" />
        <legacyImpersonationPolicy enabled="true"/>
        <alwaysFlowImpersonationPolicy enabled="false"/>
        <SymbolReadingPolicy enabled="1" />
        <shadowCopyVerifyByTimestamp enabled="true"/>
    </runtime>
    <startup useLegacyV2RuntimeActivationPolicy="true" />
    <system.web>
        <applicationPool maxConcurrentRequestsPerCPU="20000" />
    </system.web>
</configuration>
```

#### Request Queue Limit

When the total amount of connections exceed the **maxConcurrentRequestsPerCPU** setting (i.e. maxConcurrentRequestsPerCPU * number of logical processors),
ASP.NET will start throttling requests using a queue. To control the size of the queue, you can tweak the **requestQueueLimit**.

- Open ***%windir%\Microsoft.NET\Framework\v4.0.30319\Config\machine.config*** (**Framework64** for 64 bit processes)
- Locate the processModel element
- Set the **autoConfig** attribute to false and the **requestQueueLimit** attribute to a suitable number

Example:

```xml
<processModel autoConfig="false" requestQueueLimit="250000" />
```

#### Performance Counters

The following performance counters may be useful to watch while conducting concurrency testing and adjusting the settings detailed above:

Memory
- .NET CLR Memory# bytes in all Heaps (for w3wp)

ASP.NET
- ASP.NET\Requests Current
- ASP.NET\Queued
- ASP.NET\Rejected

CPU
- Processor Information\Processor Time

TCP/IP
- TCPv6\Connections Established
- TCPv4\Connections Established

Web Service
- Web Service\Current Connections
- Web Service\Maximum Connections

Threading
- .NET CLR LocksAndThreads\ # of current logical Threads
- .NET CLR LocksAndThreads\ # of current physical Threads
