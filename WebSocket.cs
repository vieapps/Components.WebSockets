#region Related components
using System;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Net.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using net.vieapps.Components.WebSockets.Exceptions;
using net.vieapps.Components.Utility;
#endregion

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("VIEApps.Components.XUnitTests")]

namespace net.vieapps.Components.WebSockets
{
	/// <summary>
	/// The centralized point for working with WebSocket
	/// </summary>
	public class WebSocket : IDisposable
	{

		#region Properties
		readonly ConcurrentDictionary<Guid, ManagedWebSocket> _websockets = new ConcurrentDictionary<Guid, ManagedWebSocket>();
		readonly ILogger _logger = null;
		readonly Func<MemoryStream> _recycledStreamFactory = null;
		readonly CancellationTokenSource _processingCTS = null;
		CancellationTokenSource _listeningCTS = null;
		TcpListener _tcpListener = null;
		bool _disposing = false, _disposed = false;

		/// <summary>
		/// Gets the listening port of the listener
		/// </summary>
		public int Port { get; private set; } = 46429;

		/// <summary>
		/// Gets or sets the SSL certificate for securing connections
		/// </summary>
		public X509Certificate2 Certificate { get; set; } = null;

		/// <summary>
		/// Gets or sets the SSL protocol for securing connections with SSL Certificate
		/// </summary>
		public SslProtocols SslProtocol { get; set; } = SslProtocols.Tls;

		/// <summary>
		/// Gets or sets the collection of supported sub-protocol
		/// </summary>
		public IEnumerable<string> SupportedSubProtocols { get; set; } = new string[0];

		/// <summary>
		/// Gets or sets keep-alive interval (seconds) for sending ping messages from server
		/// </summary>
		public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(60);

		/// <summary>
		/// Gets or sets a value that specifies whether the listener is disable the Nagle algorithm or not (default is true - means disable for better performance)
		/// </summary>
		/// <remarks>
		/// Set to true to send a message immediately with the least amount of latency (typical usage for chat)
		/// This will disable Nagle's algorithm which can cause high tcp latency for small packets sent infrequently
		/// However, if you are streaming large packets or sending large numbers of small packets frequently it is advisable to set NoDelay to false
		/// This way data will be bundled into larger packets for better throughput
		/// </remarks>
		public bool NoDelay { get; set; } = true;

		/// <summary>
		/// Gets or sets await interval (miliseconds) while receiving messages
		/// </summary>
		public int AwaitInterval { get; set; } = 0;
		#endregion

		#region Event Handlers
		/// <summary>
		/// Event to fire when got an error while processing
		/// </summary>
		public event Action<ManagedWebSocket, Exception> ErrorHandler;

		/// <summary>
		/// Gets or Sets the action to fire when got an error while processing
		/// </summary>
		public Action<ManagedWebSocket, Exception> OnError
		{
			set => this.ErrorHandler += value;
			get => this.ErrorHandler;
		}

		/// <summary>
		/// Event to fire when a connection is established
		/// </summary>
		public event Action<ManagedWebSocket> ConnectionEstablishedHandler;

		/// <summary>
		/// Gets or Sets the action to fire when a connection is established
		/// </summary>
		public Action<ManagedWebSocket> OnConnectionEstablished
		{
			set => this.ConnectionEstablishedHandler += value;
			get => this.ConnectionEstablishedHandler;
		}

		/// <summary>
		/// Event to fire when a connection is broken
		/// </summary>
		public event Action<ManagedWebSocket> ConnectionBrokenHandler;

		/// <summary>
		/// Gets or Sets the action to fire when a connection is broken
		/// </summary>
		public Action<ManagedWebSocket> OnConnectionBroken
		{
			set => this.ConnectionBrokenHandler += value;
			get => this.ConnectionBrokenHandler;
		}

		/// <summary>
		/// Event to fire when a message is received
		/// </summary>
		public event Action<ManagedWebSocket, WebSocketReceiveResult, byte[]> MessageReceivedHandler;

		/// <summary>
		/// Gets or Sets the action to fire when a message is received
		/// </summary>
		public Action<ManagedWebSocket, WebSocketReceiveResult, byte[]> OnMessageReceived
		{
			set => this.MessageReceivedHandler += value;
			get => this.MessageReceivedHandler;
		}
		#endregion

		/// <summary>
		/// Creates new an instance of the centralized <see cref="WebSocket">WebSocket</see>
		/// </summary>
		/// <param name="loggerFactory">The logger factory</param>
		/// <param name="cancellationToken">The cancellation token</param>
		public WebSocket(ILoggerFactory loggerFactory, CancellationToken cancellationToken) : this(loggerFactory, null, cancellationToken) { }

		/// <summary>
		/// Creates new an instance of the centralized <see cref="WebSocket">WebSocket</see>
		/// </summary>
		/// <param name="loggerFactory">The logger factory</param>
		/// <param name="recycledStreamFactory">Used to get a recyclable memory stream (this can be used with the Microsoft.IO.RecyclableMemoryStreamManager class)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		public WebSocket(ILoggerFactory loggerFactory = null, Func<MemoryStream> recycledStreamFactory = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			Logger.AssignLoggerFactory(loggerFactory);
			this._logger = Logger.CreateLogger<WebSocket>();
			this._recycledStreamFactory = recycledStreamFactory ?? WebSocketHelper.GetRecyclableMemoryStreamFactory();
			this._processingCTS = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		}

		/// <summary>
		/// Gets or sets the size (length) of the protocol buffer used to receive and parse frames, the default is 16kb, the minimum is 1kb (1024 bytes)
		/// </summary>
		public static int ReceiveBufferSize
		{
			get => WebSocketHelper.ReceiveBufferSize;
			set => WebSocketHelper.ReceiveBufferSize = value >= 1024 ? value : WebSocketHelper.ReceiveBufferSize;
		}

		/// <summary>
		/// Gets or sets the agent name of the protocol for working with related headers
		/// </summary>
		public static string AgentName
		{
			get => WebSocketHelper.AgentName;
			set => WebSocketHelper.AgentName = !string.IsNullOrWhiteSpace(value) ? value : WebSocketHelper.AgentName;
		}

		#region Listen for client requests as server
		/// <summary>
		/// Starts to listen for client requests as a WebSocket server
		/// </summary>
		/// <param name="port">The port for listening</param>
		/// <param name="certificate">The SSL Certificate to secure connections</param>
		/// <param name="onSuccess">Action to fire when start successful</param>
		/// <param name="onFailure">Action to fire when failed to start</param>
		public void StartListen(int port = 46429, X509Certificate2 certificate = null, Action onSuccess = null, Action<Exception> onFailure = null)
		{
			// check
			if (this._tcpListener != null)
			{
				try
				{
					onSuccess?.Invoke();
				}
				catch (Exception ex)
				{
					this._logger.Log(LogLevel.Information, LogLevel.Error, $"Error occurred while calling the handler => {ex.Message}", ex);
				}
				return;
			}

			// listen
			try
			{
				// open the listener
				this.Port = port > IPEndPoint.MinPort && port < IPEndPoint.MaxPort ? port : 46429;
				this.Certificate = certificate ?? this.Certificate;

				this._tcpListener = new TcpListener(IPAddress.Any, this.Port);
				this._tcpListener.Server.NoDelay = this.NoDelay;
				this._tcpListener.Server.SetKeepAliveInterval();
				this._tcpListener.Start(1024);

				var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
					? "Windows"
					: RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
						? "Linux"
						: "macOS";
				platform += $" ({RuntimeInformation.FrameworkDescription.Trim()}) - SSL: {this.Certificate != null}";
				if (this.Certificate != null)
					platform += $" ({this.Certificate.GetNameInfo(X509NameType.DnsName, false)} :: Issued by {this.Certificate.GetNameInfo(X509NameType.DnsName, true)})";

				this._logger.LogInformation($"The listener is started (listening port: {this.Port})\r\nPlatform: {platform}\r\nPowered by {WebSocketHelper.AgentName} {this.GetType().Assembly.GetVersion()}");
				try
				{
					onSuccess?.Invoke();
				}
				catch (Exception ex)
				{
					this._logger.Log(LogLevel.Information, LogLevel.Error, $"Error occurred while calling the handler => {ex.Message}", ex);
				}

				// listen for incoming connection requests
				this.Listen();
			}
			catch (SocketException ex)
			{
				var message = $"Error occurred while listening on port \"{this.Port}\". Make sure another application is not running and consuming this port.";
				this._logger.Log(LogLevel.Debug, LogLevel.Error, message, ex);
				try
				{
					onFailure?.Invoke(new ListenerSocketException(message, ex));
				}
				catch (Exception e)
				{
					this._logger.Log(LogLevel.Information, LogLevel.Error, $"Error occurred while calling the handler => {e.Message}", e);
				}
			}
			catch (Exception ex)
			{
				this._logger.Log(LogLevel.Debug, LogLevel.Error, $"Got an unexpected error while listening: {ex.Message}", ex);
				try
				{
					onFailure?.Invoke(ex);
				}
				catch (Exception e)
				{
					this._logger.Log(LogLevel.Information, LogLevel.Error, $"Error occurred while calling the handler => {e.Message}", e);
				}
			}
		}

		/// <summary>
		/// Starts to listen for client requests as a WebSocket server
		/// </summary>
		/// <param name="port">The port for listening</param>
		/// <param name="onSuccess">Action to fire when start successful</param>
		/// <param name="onFailure">Action to fire when failed to start</param>
		public void StartListen(int port, Action onSuccess, Action<Exception> onFailure)
			=> this.StartListen(port, null, onSuccess, onFailure);

		/// <summary>
		/// Starts to listen for client requests as a WebSocket server
		/// </summary>
		/// <param name="port">The port for listening</param>
		public void StartListen(int port)
			=> this.StartListen(port, null, null);

		/// <summary>
		/// Stops listen
		/// </summary>
		/// <param name="cancelPendings">true to cancel the pending connections</param>
		public void StopListen(bool cancelPendings = true)
		{
			// cancel all pending connections
			if (cancelPendings)
				this._listeningCTS?.Cancel();

			// dispose
			try
			{
				this._tcpListener?.Server?.Close();
				this._tcpListener?.Stop();
			}
			catch (Exception ex)
			{
				this._logger.Log(LogLevel.Debug, LogLevel.Error, $"Got an unexpected error when stop the listener: {ex.Message}", ex);
			}
			finally
			{
				this._tcpListener = null;
			}
		}

		Task Listen()
		{
			this._listeningCTS = CancellationTokenSource.CreateLinkedTokenSource(this._processingCTS.Token);
			return this.ListenAsync();
		}

		async Task ListenAsync()
		{
			try
			{
				while (!this._listeningCTS.IsCancellationRequested)
				{
					var tcpClient = await this._tcpListener.AcceptTcpClientAsync().WithCancellationToken(this._listeningCTS.Token).ConfigureAwait(false);
					tcpClient.Client.SetKeepAliveInterval();
					this.AcceptClient(tcpClient);
				}
			}
			catch (Exception ex)
			{
				this.StopListen(false);
				if (ex is OperationCanceledException || ex is TaskCanceledException || ex is ObjectDisposedException || ex is SocketException || ex is IOException)
					this._logger.LogInformation($"The listener is stopped {(this._logger.IsEnabled(LogLevel.Debug) ? $"({ex.GetType()})" : "")}");
				else
					this._logger.LogError($"The listener is stopped ({ex.Message})", ex);
			}
		}

		void AcceptClient(TcpClient tcpClient)
			=> Task.Run(() => this.AcceptClientAsync(tcpClient)).ConfigureAwait(false);

		async Task AcceptClientAsync(TcpClient tcpClient)
		{
			ManagedWebSocket websocket = null;
			try
			{
				var id = Guid.NewGuid();
				var endpoint = tcpClient.Client.RemoteEndPoint;

				// get stream
				Stream stream = null;
				if (this.Certificate != null)
					try
					{
						Events.Log.AttemptingToSecureConnection(id);
						this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"Attempting to secure the connection ({id} @ {endpoint})");

						stream = new SslStream(tcpClient.GetStream(), false);
						await (stream as SslStream).AuthenticateAsServerAsync(
							serverCertificate: this.Certificate,
							clientCertificateRequired: false,
							enabledSslProtocols: this.SslProtocol,
							checkCertificateRevocation: false
						).WithCancellationToken(this._listeningCTS.Token).ConfigureAwait(false);

						Events.Log.ConnectionSecured(id);
						this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"The connection successfully secured ({id} @ {endpoint})");
					}
					catch (OperationCanceledException)
					{
						return;
					}
					catch (Exception ex)
					{
						Events.Log.ServerSslCertificateError(id, ex.ToString());
						if (ex is AuthenticationException)
							throw ex;
						else
							throw new AuthenticationException($"Cannot secure the connection: {ex.Message}", ex);
					}
				else
				{
					Events.Log.ConnectionNotSecured(id);
					this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"Use insecured connection ({id} @ {endpoint})");
					stream = tcpClient.GetStream();
				}

				// parse request
				this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"The connection is opened, then parse the request ({id} @ {endpoint})");

				var header = await stream.ReadHeaderAsync(this._listeningCTS.Token).ConfigureAwait(false);
				this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"Handshake request ({id} @ {endpoint}) => \r\n{header.Trim()}");

				var isWebSocketRequest = false;
				var path = string.Empty;
				var match = new Regex(@"^GET(.*)HTTP\/1\.1", RegexOptions.IgnoreCase).Match(header);
				if (match.Success)
				{
					isWebSocketRequest = new Regex("Upgrade: WebSocket", RegexOptions.IgnoreCase).Match(header).Success;
					if (isWebSocketRequest)
						path = match.Groups[1].Value.Trim();
				}

				// verify request
				if (!isWebSocketRequest)
				{
					this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"The request contains no WebSocket upgrade request, then ignore ({id} @ {endpoint})");
					stream.Close();
					tcpClient.Close();
					return;
				}

				// accept the request
				var options = new WebSocketOptions
				{
					KeepAliveInterval = this.KeepAliveInterval
				};
				Events.Log.AcceptWebSocketStarted(id);
				this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"The request has requested an upgrade to WebSocket protocol, negotiating WebSocket handshake ({id} @ {endpoint})");

				try
				{
					// check the version (support version 13 and above)
					match = new Regex("Sec-WebSocket-Version: (.*)").Match(header);
					if (match.Success)
					{
						var secWebSocketVersion = match.Groups[1].Value.Trim().CastAs<int>();
						if (secWebSocketVersion < 13)
							throw new VersionNotSupportedException($"WebSocket Version {secWebSocketVersion} is not supported, must be 13 or above");
					}
					else
						throw new VersionNotSupportedException("Unable to find \"Sec-WebSocket-Version\" in the upgrade request");

					// get the request key
					match = new Regex("Sec-WebSocket-Key: (.*)").Match(header);
					var requestKey = match.Success
						? match.Groups[1].Value.Trim()
						: throw new KeyMissingException("Unable to find \"Sec-WebSocket-Key\" in the upgrade request");

					// negotiate subprotocol
					match = new Regex("Sec-WebSocket-Protocol: (.*)").Match(header);
					options.SubProtocol = match.Success
						? match.Groups[1].Value.Trim().Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).NegotiateSubProtocol(this.SupportedSubProtocols)
						: null;

					// handshake
					var handshake =
						$"HTTP/1.1 101 Switching Protocols\r\n" +
						$"Connection: Upgrade\r\n" +
						$"Upgrade: WebSocket\r\n" +
						$"Server: {WebSocketHelper.AgentName}\r\n" +
						$"Date: {DateTime.Now.ToHttpString()}\r\n" +
						$"Sec-WebSocket-Accept: {requestKey.ComputeAcceptKey()}\r\n";
					if (!string.IsNullOrWhiteSpace(options.SubProtocol))
						handshake += $"Sec-WebSocket-Protocol: {options.SubProtocol}\r\n";
					options.AdditionalHeaders?.ForEach(kvp => handshake += $"{kvp.Key}: {kvp.Value}\r\n");

					Events.Log.SendingHandshake(id, handshake);
					await stream.WriteHeaderAsync(handshake, this._listeningCTS.Token).ConfigureAwait(false);
					Events.Log.HandshakeSent(id, handshake);
					this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"Handshake response ({id} @ {endpoint}) => \r\n{handshake.Trim()}");
				}
				catch (VersionNotSupportedException ex)
				{
					Events.Log.WebSocketVersionNotSupported(id, ex.ToString());
					await stream.WriteHeaderAsync($"HTTP/1.1 426 Upgrade Required\r\nSec-WebSocket-Version: 13\r\nException: {ex.Message}", this._listeningCTS.Token).ConfigureAwait(false);
					throw ex;
				}
				catch (Exception ex)
				{
					Events.Log.BadRequest(id, ex.ToString());
					await stream.WriteHeaderAsync($"HTTP/1.1 400 Bad Request\r\nException: {ex.Message}", this._listeningCTS.Token).ConfigureAwait(false);
					throw ex;
				}

				Events.Log.ServerHandshakeSuccess(id);
				this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"WebSocket handshake response has been sent, the stream is ready ({id} @ {endpoint})");

				// update the connected WebSocket connection
				match = new Regex("Sec-WebSocket-Extensions: (.*)").Match(header);
				options.Extensions = match.Success
					? match.Groups[1].Value.Trim()
					: null;

				match = new Regex("Host: (.*)").Match(header);
				var host = match.Success
					? match.Groups[1].Value.Trim()
					: string.Empty;

				websocket = new WebSocketImplementation(id, false, this._recycledStreamFactory, stream, options, new Uri($"ws{(this.Certificate != null ? "s" : "")}://{host}{path}"), endpoint, tcpClient.Client.LocalEndPoint);

				match = new Regex("User-Agent: (.*)").Match(header);
				websocket.Extra["User-Agent"] = match.Success
					? match.Groups[1].Value.Trim()
					: string.Empty;

				match = new Regex("Referer: (.*)").Match(header);
				if (match.Success)
					websocket.Extra["Referer"] = match.Groups[1].Value.Trim();
				else
				{
					match = new Regex("Origin: (.*)").Match(header);
					websocket.Extra["Referer"] = match.Success
						? match.Groups[1].Value.Trim()
						: string.Empty;
				}

				// add into the collection
				await this.AddWebSocketAsync(websocket).ConfigureAwait(false);

				// callback
				try
				{
					this.ConnectionEstablishedHandler?.Invoke(websocket);
				}
				catch (Exception e)
				{
					this._logger.Log(LogLevel.Information, LogLevel.Error, $"Error occurred while calling the handler => {e.Message}", e);
				}

				// receive messages
				this.Receive(websocket);
			}
			catch (Exception ex)
			{
				if (ex is OperationCanceledException || ex is TaskCanceledException || ex is ObjectDisposedException || ex is SocketException || ex is IOException)
				{
					// normal, do nothing
				}
				else
				{
					this._logger.Log(LogLevel.Debug, LogLevel.Error, $"Error occurred while accepting an incoming connection request: {ex.Message}", ex);
					try
					{
						this.ErrorHandler?.Invoke(websocket, ex);
					}
					catch (Exception e)
					{
						this._logger.Log(LogLevel.Information, LogLevel.Error, $"Error occurred while calling the handler => {e.Message}", e);
					}
				}
			}
		}
		#endregion

		#region Connect to remote endpoints as client
		async Task ConnectAsync(Uri uri, WebSocketOptions options, Action<ManagedWebSocket> onSuccess = null, Action<Exception> onFailure = null)
		{
			this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"Attempting to connect ({uri})");
			try
			{
				// connect the TCP client
				var id = Guid.NewGuid();
				var tcpClient = new TcpClient
				{
					NoDelay = options.NoDelay
				};
				tcpClient.Client.SetKeepAliveInterval();

				if (IPAddress.TryParse(uri.Host, out IPAddress ipAddress))
				{
					Events.Log.ClientConnectingToIPAddress(id, ipAddress.ToString(), uri.Port);
					await tcpClient.ConnectAsync(address: ipAddress, port: uri.Port).WithCancellationToken(this._processingCTS.Token).ConfigureAwait(false);
				}
				else
				{
					Events.Log.ClientConnectingToHost(id, uri.Host, uri.Port);
					await tcpClient.ConnectAsync(host: uri.Host, port: uri.Port).WithCancellationToken(this._processingCTS.Token).ConfigureAwait(false);
				}

				var endpoint = tcpClient.Client.RemoteEndPoint;
				this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"The endpoint ({uri}) is connected ({id} @ {endpoint})");
				
				// Check if the caller set Host in headers - support SNI (server name indication)
				var sniHost = options.AdditionalHeaders?.ContainsKey("Host") == true ? options.AdditionalHeaders["Host"] : null;

				// get the connected stream
				Stream stream = null;
				if (uri.Scheme.IsEquals("wss") || uri.Scheme.IsEquals("https"))
					try
					{
						Events.Log.AttemptingToSecureConnection(id);
						this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"Attempting to secure the connection ({id} @ {endpoint})");

						stream = new SslStream(
							innerStream: tcpClient.GetStream(),
							leaveInnerStreamOpen: false,
							userCertificateValidationCallback: (sender, certificate, chain, sslPolicyErrors) => options.IgnoreCertificateErrors || RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || sslPolicyErrors == SslPolicyErrors.None,
							userCertificateSelectionCallback: (sender, host, certificates, certificate, issuers) => this.Certificate
						);
						await (stream as SslStream).AuthenticateAsClientAsync(targetHost: sniHost ?? uri.Host).WithCancellationToken(this._processingCTS.Token).ConfigureAwait(false);

						Events.Log.ConnectionSecured(id);
						this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"The connection successfully secured ({id} @ {endpoint})");
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch (Exception ex)
					{
						Events.Log.ClientSslCertificateError(id, ex.ToString());
						if (ex is AuthenticationException)
							throw ex;
						else
							throw new AuthenticationException($"Cannot secure the connection: {ex.Message}", ex);
					}
				else
				{
					Events.Log.ConnectionNotSecured(id);
					this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"Use insecured connection ({id} @ {endpoint})");
					stream = tcpClient.GetStream();
				}

				// send handshake
				this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"Negotiating WebSocket handshake ({id} @ {endpoint})");
				var requestAcceptKey = CryptoService.GenerateRandomKey(16).ToBase64();
				var handshake =
					$"GET {uri.PathAndQuery} HTTP/1.1\r\n" +
					$"Host: {sniHost ?? $"{uri.Host}:{uri.Port}"}\r\n" +
					$"Origin: {uri.Scheme.Replace("ws", "http")}://{uri.Host}{(uri.Port != 80 && uri.Port != 443 ? $":{uri.Port}" : "")}\r\n" +
					$"Connection: Upgrade\r\n" +
					$"Upgrade: WebSocket\r\n" +
					$"User-Agent: Mozilla/5.0 ({WebSocketHelper.AgentName}/{RuntimeInformation.FrameworkDescription.Trim()}/{(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Macintosh; Intel Mac OS X; " : "")}{RuntimeInformation.OSDescription.Trim()})\r\n" +
					$"Date: {DateTime.Now.ToHttpString()}\r\n" +
					$"Sec-WebSocket-Version: 13\r\n" +
					$"Sec-WebSocket-Key: {requestAcceptKey}\r\n";
				if (!string.IsNullOrWhiteSpace(options.SubProtocol))
					handshake += $"Sec-WebSocket-Protocol: {options.SubProtocol}\r\n";
				if (!string.IsNullOrWhiteSpace(options.Extensions))
					handshake += $"Sec-WebSocket-Extensions: {options.Extensions}\r\n";
				options.AdditionalHeaders?.ForEach(kvp => handshake += $"{kvp.Key}: {kvp.Value}\r\n");

				Events.Log.SendingHandshake(id, handshake);
				await stream.WriteHeaderAsync(handshake, this._processingCTS.Token).ConfigureAwait(false);
				Events.Log.HandshakeSent(id, handshake);
				this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"Handshake request ({id} @ {endpoint}) => \r\n{handshake.Trim()}");

				// read response
				Events.Log.ReadingResponse(id);
				var response = string.Empty;
				try
				{
					response = await stream.ReadHeaderAsync(this._processingCTS.Token).ConfigureAwait(false);
					this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"Handshake response ({id} @ {endpoint}) => \r\n{response.Trim()}");
				}
				catch (Exception ex)
				{
					Events.Log.ReadResponseError(id, ex.ToString());
					throw new HandshakeFailedException("Handshake unexpected failure", ex);
				}

				// get the response code
				var match = new Regex(@"HTTP\/1\.1 (.*)", RegexOptions.IgnoreCase).Match(response);
				var responseCode = match.Success
					? match.Groups[1].Value.Trim()
					: null;

				// throw if got invalid response code
				if (!"101 Switching Protocols".IsEquals(responseCode))
				{
					var lines = response.Split(new[] { "\r\n" }, StringSplitOptions.None);
					for (var index = 0; index < lines.Length; index++)
						if (string.IsNullOrWhiteSpace(lines[index])) // if there is more to the message than just the header
						{
							var builder = new StringBuilder();
							for (var idx = index + 1; idx < lines.Length - 1; idx++)
								builder.AppendLine(lines[idx]);
							throw new InvalidResponseCodeException(responseCode, builder.ToString(), response);
						}
				}

				// check the accepted key
				match = new Regex("Sec-WebSocket-Accept: (.*)").Match(response);
				var actualAcceptKey = match.Success
					? match.Groups[1].Value.Trim()
					: null;
				var expectedAcceptKey = requestAcceptKey.ComputeAcceptKey();
				if (!expectedAcceptKey.IsEquals(actualAcceptKey))
				{
					var warning = $"Handshake failed because the accept key from the server \"{actualAcceptKey}\" was not the expected \"{expectedAcceptKey}\"";
					Events.Log.HandshakeFailure(id, warning);
					throw new HandshakeFailedException(warning);
				}

				Events.Log.ClientHandshakeSuccess(id);
				this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"Handshake success ({id} @ {endpoint})");

				// get the accepted sub-protocol
				match = new Regex("Sec-WebSocket-Protocol: (.*)").Match(response);
				options.SubProtocol = match.Success
					? match.Groups[1].Value.Trim()
					: null;

				// update the connected WebSocket connection
				var websocket = new WebSocketImplementation(id, true, this._recycledStreamFactory, stream, options, uri, endpoint, tcpClient.Client.LocalEndPoint);
				await this.AddWebSocketAsync(websocket).ConfigureAwait(false);

				// callback
				try
				{
					this.ConnectionEstablishedHandler?.Invoke(websocket);
					onSuccess?.Invoke(websocket);
				}
				catch (Exception ex)
				{
					this._logger.Log(LogLevel.Information, LogLevel.Error, $"Error occurred while calling the handler => {ex.Message}", ex);
				}

				// receive messages
				this.Receive(websocket);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception ex)
			{
				this._logger.Log(LogLevel.Debug, LogLevel.Error, $"Could not connect ({uri}): {ex.Message}", ex);
				try
				{
					onFailure?.Invoke(ex);
				}
				catch (Exception e)
				{
					this._logger.Log(LogLevel.Information, LogLevel.Error, $"Error occurred while calling the handler => {e.Message}", e);
				}
			}
		}

		/// <summary>
		/// Connects to a remote endpoint as a WebSocket client
		/// </summary>
		/// <param name="uri">The address of the remote endpoint to connect to</param>
		/// <param name="options">The options</param>
		/// <param name="onSuccess">Action to fire when connect successful</param>
		/// <param name="onFailure">Action to fire when failed to connect</param>
		public void Connect(Uri uri, WebSocketOptions options, Action<ManagedWebSocket> onSuccess = null, Action<Exception> onFailure = null)
			=> Task.Run(() => this.ConnectAsync(uri, options ?? new WebSocketOptions(), onSuccess, onFailure)).ConfigureAwait(false);

		/// <summary>
		/// Connects to a remote endpoint as a WebSocket client
		/// </summary>
		/// <param name="uri">The address of the remote endpoint to connect to</param>
		/// <param name="subProtocol">The sub-protocol</param>
		/// <param name="onSuccess">Action to fire when connect successful</param>
		/// <param name="onFailure">Action to fire when failed to connect</param>
		public void Connect(Uri uri, string subProtocol = null, Action<ManagedWebSocket> onSuccess = null, Action<Exception> onFailure = null)
			=> this.Connect(uri, new WebSocketOptions { SubProtocol = subProtocol }, onSuccess, onFailure);

		/// <summary>
		/// Connects to a remote endpoint as a WebSocket client
		/// </summary>
		/// <param name="location">The address of the remote endpoint to connect to</param>
		/// <param name="subProtocol">The sub-protocol</param>
		/// <param name="onSuccess">Action to fire when connect successful</param>
		/// <param name="onFailure">Action to fire when failed to connect</param>
		public void Connect(string location, string subProtocol = null, Action<ManagedWebSocket> onSuccess = null, Action<Exception> onFailure = null)
			=> this.Connect(new Uri(location), subProtocol, onSuccess, onFailure);

		/// <summary>
		/// Connects to a remote endpoint as a WebSocket client
		/// </summary>
		/// <param name="location">The address of the remote endpoint to connect to</param>
		/// <param name="onSuccess">Action to fire when connect successful</param>
		/// <param name="onFailure">Action to fire when failed to connect</param>
		public void Connect(string location, Action<ManagedWebSocket> onSuccess, Action<Exception> onFailure)
			=> this.Connect(location, null, onSuccess, onFailure);
		#endregion

		#region Wrap a WebSocket connection of ASP.NET / ASP.NET Core
		/// <summary>
		/// Wraps a <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection of ASP.NET / ASP.NET Core and acts like a <see cref="WebSocket">WebSocket</see> server
		/// </summary>
		/// <param name="webSocket">The <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection of ASP.NET / ASP.NET Core</param>
		/// <param name="requestUri">The original request URI of the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection</param>
		/// <param name="remoteEndPoint">The remote endpoint of the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection</param>
		/// <param name="localEndPoint">The local endpoint of the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection</param>
		/// <param name="userAgent">The string that presents the user agent of the client that made this request to the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection</param>
		/// <param name="urlReferer">The string that presents the url referer of the client that made this request to the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection</param>
		/// <param name="headers">The string that presents the headers of the client that made this request to the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection</param>
		/// <param name="cookies">The string that presents the cookies of the client that made this request to the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection</param>
		/// <param name="onSuccess">The action to fire when the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection is wrap success</param>
		/// <returns>A task that run the receiving process when wrap successful or an exception when failed</returns>
		public Task WrapAsync(System.Net.WebSockets.WebSocket webSocket, Uri requestUri, EndPoint remoteEndPoint = null, EndPoint localEndPoint = null, string userAgent = null, string urlReferer = null, string headers = null, string cookies = null, Action<ManagedWebSocket> onSuccess = null)
		{
			try
			{
				// create
				var websocket = new WebSocketWrapper(webSocket, requestUri, remoteEndPoint, localEndPoint);

				if (!string.IsNullOrWhiteSpace(userAgent))
					websocket.Extra["User-Agent"] = userAgent;

				if (!string.IsNullOrWhiteSpace(urlReferer))
					websocket.Extra["Referer"] = urlReferer;

				if (!string.IsNullOrWhiteSpace(headers))
					websocket.Extra["Headers"] = headers;

				if (!string.IsNullOrWhiteSpace(cookies))
					websocket.Extra["Cookies"] = cookies;

				this.AddWebSocket(websocket);
				this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"Wrap a WebSocket connection [{webSocket.GetType()}] successful ({websocket.ID} @ {websocket.RemoteEndPoint}");

				// callback
				try
				{
					this.ConnectionEstablishedHandler?.Invoke(websocket);
					onSuccess?.Invoke(websocket);
				}
				catch (Exception ex)
				{
					this._logger.Log(LogLevel.Information, LogLevel.Error, $"Error occurred while calling the handler => {ex.Message}", ex);
				}

				// receive messages
				return this.ReceiveAsync(websocket);
			}
			catch (Exception ex)
			{
				this._logger.Log(LogLevel.Debug, LogLevel.Error, $"Unable to wrap a WebSocket connection [{webSocket.GetType()}]: {ex.Message}", ex);
				return Task.FromException(new WrapWebSocketFailedException($"Unable to wrap a WebSocket connection [{webSocket.GetType()}]", ex));
			}
		}

		/// <summary>
		/// Wraps a <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection of ASP.NET / ASP.NET Core and acts like a <see cref="WebSocket">WebSocket</see> server
		/// </summary>
		/// <param name="webSocket">The <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection of ASP.NET / ASP.NET Core</param>
		/// <param name="requestUri">The original request URI of the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection</param>
		/// <param name="remoteEndPoint">The remote endpoint of the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection</param>
		/// <param name="localEndPoint">The local endpoint of the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection</param>
		/// <param name="userAgent">The string that presents the user agent of the client that made this request to the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection</param>
		/// <param name="urlReferer">The string that presents the url referer of the client that made this request to the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection</param>
		/// <param name="onSuccess">The action to fire when the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection is wrap success</param>
		/// <returns>A task that run the receiving process when wrap successful or an exception when failed</returns>
		public Task WrapAsync(System.Net.WebSockets.WebSocket webSocket, Uri requestUri, EndPoint remoteEndPoint, EndPoint localEndPoint, string userAgent, string urlReferer, Action<ManagedWebSocket> onSuccess)
			=> this.WrapAsync(webSocket, requestUri, remoteEndPoint, localEndPoint, userAgent, urlReferer, null, null, onSuccess);

		/// <summary>
		/// Wraps a <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection of ASP.NET / ASP.NET Core and acts like a <see cref="WebSocket">WebSocket</see> server
		/// </summary>
		/// <param name="webSocket">The <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection of ASP.NET / ASP.NET Core</param>
		/// <param name="requestUri">The original request URI of the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection</param>
		/// <param name="remoteEndPoint">The remote endpoint of the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> connection</param>
		/// <returns>A task that run the receiving process when wrap successful or an exception when failed</returns>
		public Task WrapAsync(System.Net.WebSockets.WebSocket webSocket, Uri requestUri, EndPoint remoteEndPoint)
			=> this.WrapAsync(webSocket, requestUri, remoteEndPoint, null, null, null, null, null, null);
		#endregion

		#region Receive messages
		void Receive(ManagedWebSocket websocket)
			=> Task.Run(() => this.ReceiveAsync(websocket)).ConfigureAwait(false);

		async Task ReceiveAsync(ManagedWebSocket websocket)
		{
			var buffer = new ArraySegment<byte>(new byte[WebSocketHelper.ReceiveBufferSize]);
			while (!this._processingCTS.IsCancellationRequested)
			{
				// receive message from the WebSocket connection
				WebSocketReceiveResult result = null;
				try
				{
					result = await websocket.ReceiveAsync(buffer, this._processingCTS.Token).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					var closeStatus = WebSocketCloseStatus.InternalServerError;
					var closeStatusDescription = $"Got an unexpected error: {ex.Message}";
					if (ex is OperationCanceledException || ex is TaskCanceledException || ex is ObjectDisposedException || ex is WebSocketException || ex is SocketException || ex is IOException)
					{
						closeStatus = websocket.IsClient ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.EndpointUnavailable;
						closeStatusDescription = websocket.IsClient ? "Disconnected" : "Service is unavailable";
					}

					this.CloseWebSocket(websocket, closeStatus, closeStatusDescription);
					try
					{
						this.ConnectionBrokenHandler?.Invoke(websocket);
					}
					catch (Exception e)
					{
						this._logger.Log(LogLevel.Information, LogLevel.Error, $"Error occurred while calling the handler => {e.Message}", e);
					}

					if (ex is OperationCanceledException || ex is TaskCanceledException || ex is ObjectDisposedException || ex is WebSocketException || ex is SocketException || ex is IOException)
						this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"Stop receiving process when got an error: {ex.Message} ({ex.GetType().GetTypeName(true)})");
					else
					{
						this._logger.Log(LogLevel.Debug, LogLevel.Error, closeStatusDescription, ex);
						try
						{
							this.ErrorHandler?.Invoke(websocket, ex);
						}
						catch (Exception e)
						{
							this._logger.Log(LogLevel.Information, LogLevel.Error, $"Error occurred while calling the handler => {e.Message}", e);
						}
					}
					return;
				}

				// message to close
				if (result.MessageType == WebSocketMessageType.Close)
				{
					this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"The remote endpoint is initiated to close - Status: {result.CloseStatus} - Description: {result.CloseStatusDescription ?? "N/A"} ({websocket.ID} @ {websocket.RemoteEndPoint})");
					this.CloseWebSocket(websocket);
					try
					{
						this.ConnectionBrokenHandler?.Invoke(websocket);
					}
					catch (Exception ex)
					{
						this._logger.Log(LogLevel.Information, LogLevel.Error, $"Error occurred while calling the handler => {ex.Message}", ex);
					}
					return;
				}

				// exceed buffer size
				if (result.Count > WebSocketHelper.ReceiveBufferSize)
				{
					var message = $"WebSocket frame cannot exceed buffer size of {WebSocketHelper.ReceiveBufferSize:#,##0} bytes";
					this._logger.Log(LogLevel.Debug, LogLevel.Debug, $"Close the connection because {message} ({websocket.ID} @ {websocket.RemoteEndPoint})");
					await websocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, $"{message}, send multiple frames instead.", CancellationToken.None).ConfigureAwait(false);

					this.CloseWebSocket(websocket);
					try
					{
						this.ConnectionBrokenHandler?.Invoke(websocket);
						this.ErrorHandler?.Invoke(websocket, new BufferOverflowException(message));
					}
					catch (Exception ex)
					{
						this._logger.Log(LogLevel.Information, LogLevel.Error, $"Error occurred while calling the handler => {ex.Message}", ex);
					}
					return;
				}

				// got a message
				if (result.Count > 0)
				{
					this._logger.Log(LogLevel.Trace, LogLevel.Debug, $"A message was received - Type: {result.MessageType} - End of message: {result.EndOfMessage} - Length: {result.Count:#,##0} ({websocket.ID} @ {websocket.RemoteEndPoint})");
					try
					{
						this.MessageReceivedHandler?.Invoke(websocket, result, buffer.Take(result.Count).ToArray());
					}
					catch (Exception ex)
					{
						this._logger.Log(LogLevel.Information, LogLevel.Error, $"Error occurred while calling the handler => {ex.Message}", ex);
					}
				}

				// wait for next round
				if (this.AwaitInterval > 0)
					try
					{
						await Task.Delay(this.AwaitInterval, this._processingCTS.Token).ConfigureAwait(false);
					}
					catch
					{
						this.CloseWebSocket(websocket, websocket.IsClient ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.EndpointUnavailable, websocket.IsClient ? "Disconnected" : "Service is unavailable");
						try
						{
							this.ConnectionBrokenHandler?.Invoke(websocket);
						}
						catch (Exception ex)
						{
							this._logger.Log(LogLevel.Information, LogLevel.Error, $"Error occurred while calling the handler => {ex.Message}", ex);
						}
						return;
					}

				// prepare buffer for next round
				if (!buffer.Array.Length.Equals(WebSocketHelper.ReceiveBufferSize))
					buffer = new ArraySegment<byte>(new byte[WebSocketHelper.ReceiveBufferSize]);
			}
		}
		#endregion

		#region Send messages
		/// <summary>
		/// Sends the message to a <see cref="ManagedWebSocket">WebSocket</see> connection
		/// </summary>
		/// <param name="id">The identity of a <see cref="ManagedWebSocket">WebSocket</see> connection</param>
		/// <param name="buffer">The buffer containing message to send</param>
		/// <param name="messageType">The message type, can be Text or Binary</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public async Task SendAsync(Guid id, ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			ManagedWebSocket websocket = null;
			try
			{
				if (this._websockets.TryGetValue(id, out websocket))
					await websocket.SendAsync(buffer, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
				else
					throw new InformationNotFoundException($"WebSocket connection with identity \"{id}\" is not found");
			}
			catch (Exception ex)
			{
				try
				{
					this.ErrorHandler?.Invoke(websocket, ex);
				}
				catch (Exception e)
				{
					this._logger.Log(LogLevel.Information, LogLevel.Error, $"Error occurred while calling the handler => {e.Message}", e);
				}
			}
		}

		/// <summary>
		/// Sends the message to a <see cref="ManagedWebSocket">WebSocket</see> connection
		/// </summary>
		/// <param name="id">The identity of a <see cref="ManagedWebSocket">WebSocket</see> connection</param>
		/// <param name="message">The text message to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public async Task SendAsync(Guid id, string message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			ManagedWebSocket websocket = null;
			try
			{
				if (this._websockets.TryGetValue(id, out websocket))
					await websocket.SendAsync(message, endOfMessage, cancellationToken).ConfigureAwait(false);
				else
					throw new InformationNotFoundException($"WebSocket connection with identity \"{id}\" is not found");
			}
			catch (Exception ex)
			{
				try
				{
					this.ErrorHandler?.Invoke(websocket, ex);
				}
				catch (Exception e)
				{
					this._logger.Log(LogLevel.Information, LogLevel.Error, $"Error occurred while calling the handler => {e.Message}", e);
				}
			}
		}

		/// <summary>
		/// Sends the message to a <see cref="ManagedWebSocket">WebSocket</see> connection
		/// </summary>
		/// <param name="id">The identity of a <see cref="ManagedWebSocket">WebSocket</see> connection</param>
		/// <param name="message">The binary message to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public async Task SendAsync(Guid id, byte[] message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			ManagedWebSocket websocket = null;
			try
			{
				if (this._websockets.TryGetValue(id, out websocket))
					await websocket.SendAsync(message, endOfMessage, cancellationToken).ConfigureAwait(false);
				else
					throw new InformationNotFoundException($"WebSocket connection with identity \"{id}\" is not found");
			}
			catch (Exception ex)
			{
				try
				{
					this.ErrorHandler?.Invoke(websocket, ex);
				}
				catch (Exception e)
				{
					this._logger.Log(LogLevel.Information, LogLevel.Error, $"Error occurred while calling the handler => {e.Message}", e);
				}
			}
		}

		/// <summary>
		/// Sends the message to the <see cref="ManagedWebSocket">WebSocket</see> connections that matched with the predicate
		/// </summary>
		/// <param name="predicate">The predicate for selecting <see cref="ManagedWebSocket">WebSocket</see> connections</param>
		/// <param name="buffer">The buffer containing message to send</param>
		/// <param name="messageType">The message type. Can be Text or Binary</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public Task SendAsync(Func<ManagedWebSocket, bool> predicate, ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
			=> this.GetWebSockets(predicate).ForEachAsync(async (websocket, token) =>
			{
				try
				{
					await websocket.SendAsync(buffer.Clone(), messageType, endOfMessage, token).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					try
					{
						this.ErrorHandler?.Invoke(websocket, ex);
					}
					catch (Exception e)
					{
						this._logger.Log(LogLevel.Information, LogLevel.Error, $"Error occurred while calling the handler => {e.Message}", e);
					}
				}
			}, cancellationToken);

		/// <summary>
		/// Sends the message to the <see cref="ManagedWebSocket">WebSocket</see> connections that matched with the predicate
		/// </summary>
		/// <param name="predicate">The predicate for selecting <see cref="ManagedWebSocket">WebSocket</see> connections</param>
		/// <param name="message">The text message to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public Task SendAsync(Func<ManagedWebSocket, bool> predicate, string message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
			=> this.SendAsync(predicate, message.ToArraySegment(), WebSocketMessageType.Text, endOfMessage, cancellationToken);

		/// <summary>
		/// Sends the message to the <see cref="ManagedWebSocket">WebSocket</see> connections that matched with the predicate
		/// </summary>
		/// <param name="predicate">The predicate for selecting <see cref="ManagedWebSocket">WebSocket</see> connections</param>
		/// <param name="message">The binary message to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public Task SendAsync(Func<ManagedWebSocket, bool> predicate, byte[] message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
			=> this.SendAsync(predicate, message.ToArraySegment(), WebSocketMessageType.Binary, endOfMessage, cancellationToken);
		#endregion

		#region Connection management
		bool AddWebSocket(ManagedWebSocket websocket)
			=> websocket != null
				? this._websockets.TryAdd(websocket.ID, websocket)
				: false;

		async Task<bool> AddWebSocketAsync(ManagedWebSocket websocket)
		{
			if (!this.AddWebSocket(websocket))
			{
				if (websocket != null)
					await Task.Delay(UtilityService.GetRandomNumber(123, 456)).ConfigureAwait(false);
				return this.AddWebSocket(websocket);
			}
			return true;
		}

		/// <summary>
		/// Gets a <see cref="ManagedWebSocket">WebSocket</see> connection that specifed by identity
		/// </summary>
		/// <param name="id"></param>
		/// <returns></returns>
		public ManagedWebSocket GetWebSocket(Guid id)
			=> this._websockets.TryGetValue(id, out ManagedWebSocket websocket)
				? websocket
				: null;

		/// <summary>
		/// Gets the collection of <see cref="ManagedWebSocket">WebSocket</see> connections that matched with the predicate
		/// </summary>
		/// <param name="predicate">Predicate for selecting <see cref="ManagedWebSocket">WebSocket</see> connections, if no predicate is provied then return all</param>
		/// <returns></returns>
		public IEnumerable<ManagedWebSocket> GetWebSockets(Func<ManagedWebSocket, bool> predicate = null)
			=> predicate != null
				? this._websockets.Values.Where(websocket => predicate(websocket))
				: this._websockets.Values;

		/// <summary>
		/// Closes the <see cref="ManagedWebSocket">WebSocket</see> connection and remove from the centralized collections
		/// </summary>
		/// <param name="websocket">The <see cref="ManagedWebSocket">WebSocket</see> connection to close</param>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="closeStatusDescription">A description of why we are closing</param>
		/// <returns></returns>
		bool CloseWebsocket(ManagedWebSocket websocket, WebSocketCloseStatus closeStatus, string closeStatusDescription)
		{
			if (websocket.State == WebSocketState.Open)
				Task.Run(() => websocket.DisposeAsync(closeStatus, closeStatusDescription)).ConfigureAwait(false);
			else
				websocket.Close();
			return true;
		}

		/// <summary>
		/// Closes the <see cref="ManagedWebSocket">WebSocket</see> connection and remove from the centralized collections
		/// </summary>
		/// <param name="id">The identity of a <see cref="ManagedWebSocket">WebSocket</see> connection to close</param>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="closeStatusDescription">A description of why we are closing</param>
		/// <returns>true if closed and destroyed</returns>
		public bool CloseWebSocket(Guid id, WebSocketCloseStatus closeStatus = WebSocketCloseStatus.EndpointUnavailable, string closeStatusDescription = "Service is unavailable")
			=> this._websockets.TryRemove(id, out ManagedWebSocket websocket)
				? this.CloseWebsocket(websocket, closeStatus, closeStatusDescription)
				: false;

		/// <summary>
		/// Closes the <see cref="ManagedWebSocket">WebSocket</see> connection and remove from the centralized collections
		/// </summary>
		/// <param name="websocket">The <see cref="ManagedWebSocket">WebSocket</see> connection to close</param>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="closeStatusDescription">A description of why we are closing</param>
		/// <returns>true if closed and destroyed</returns>
		public bool CloseWebSocket(ManagedWebSocket websocket, WebSocketCloseStatus closeStatus = WebSocketCloseStatus.EndpointUnavailable, string closeStatusDescription = "Service is unavailable")
			=> websocket == null
				? false
				: this.CloseWebsocket(this._websockets.TryRemove(websocket.ID, out ManagedWebSocket webSocket) ? webSocket : websocket, closeStatus, closeStatusDescription);
		#endregion

		#region Dispose
		public void Dispose()
		{
			// check state
			if (this._disposing || this._disposed)
				return;

			// update state
			this._disposing = true;

			// stop listener
			this.StopListen();

			// close all WebSocket connections
			using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
			{
				Task.WaitAll(this._websockets.Values.Select(websocket => websocket.DisposeAsync(WebSocketCloseStatus.NormalClosure, "Disconnected", cts.Token)).ToArray(), TimeSpan.FromSeconds(5));
				this._websockets.Clear();
			}

			// cancel all pending operations
			this._listeningCTS?.Dispose();
			this._processingCTS.Cancel();
			this._processingCTS.Dispose();

			// update state
			this._disposed = true;
			this._disposing = false;
		}

		~WebSocket()
		{
			this.Dispose();
			GC.SuppressFinalize(this);
		}
		#endregion

	}

	// ------------------------------------------------------------------------

	/// <summary>
	/// An implementation or a wrapper of the <see cref="System.Net.WebSockets.WebSocket">WebSocket</see> abstract class with more useful information
	/// </summary>
	public abstract class ManagedWebSocket : System.Net.WebSockets.WebSocket
	{
		protected bool _disposing = false, _disposed = false;

		#region Properties
		/// <summary>
		/// Gets the identity of the <see cref="ManagedWebSocket">WebSocket</see> connection
		/// </summary>
		public Guid ID { get; internal set; }

		/// <summary>
		/// Gets the state that indicates the <see cref="ManagedWebSocket">WebSocket</see> connection is client mode or not (client mode means the <see cref="ManagedWebSocket">WebSocket</see> connection is connected to a remote endpoint)
		/// </summary>
		public bool IsClient { get; protected set; } = false;

		/// <summary>
		/// Gets the keep-alive interval (seconds) the <see cref="ManagedWebSocket">WebSocket</see> connection (for send ping message from server)
		/// </summary>
		public TimeSpan KeepAliveInterval { get; internal set; } = TimeSpan.Zero;

		/// <summary>
		/// Gets the time-stamp when the <see cref="ManagedWebSocket">WebSocket</see> connection is established
		/// </summary>
		public DateTime Timestamp { get; } = DateTime.Now;

		/// <summary>
		/// Gets the original Uniform Resource Identifier (URI) of the <see cref="ManagedWebSocket">WebSocket</see> connection
		/// </summary>
		public Uri RequestUri { get; internal set; }

		/// <summary>
		/// Gets the remote endpoint of the <see cref="ManagedWebSocket">WebSocket</see> connection
		/// </summary>
		public EndPoint RemoteEndPoint { get; internal set; }

		/// <summary>
		/// Gets the local endpoint of the <see cref="ManagedWebSocket">WebSocket</see> connection
		/// </summary>
		public EndPoint LocalEndPoint { get; internal set; }

		/// <summary>
		/// Gets the extra information of the <see cref="ManagedWebSocket">WebSocket</see> connection
		/// </summary>
		public Dictionary<string, object> Extra { get; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Gets the state to include the full exception (with stack trace) in the close response when an exception is encountered and the WebSocket connection is closed
		/// </summary>
		protected abstract bool IncludeExceptionInCloseResponse { get; }
		#endregion

		#region Methods
		/// <summary>
		/// Sends data over the <see cref="ManagedWebSocket">WebSocket</see> connection asynchronously
		/// </summary>
		/// <param name="data">The text data to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), if its a multi-part message then false (and true for the last)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public Task SendAsync(string data, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
			=> this.SendAsync((data ?? "").ToArraySegment(), WebSocketMessageType.Text, endOfMessage, cancellationToken);

		/// <summary>
		/// Sends data over the <see cref="ManagedWebSocket">WebSocket</see> connection asynchronously
		/// </summary>
		/// <param name="data">The binary data to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), if its a multi-part message then false (and true for the last)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public Task SendAsync(byte[] data, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
			=> this.SendAsync((data ?? new byte[0]).ToArraySegment(), WebSocketMessageType.Binary, endOfMessage, cancellationToken);

		/// <summary>
		/// Sends data over the <see cref="ManagedWebSocket">WebSocket</see> connection asynchronously
		/// </summary>
		/// <param name="data">The binary data to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), if its a multi-part message then false (and true for the last)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public Task SendAsync(ArraySegment<byte> data, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
			=> this.SendAsync(data, WebSocketMessageType.Binary, endOfMessage, cancellationToken);

		/// <summary>
		/// Closes the <see cref="ManagedWebSocket">WebSocket</see> connection automatically in response to some invalid data from the remote host
		/// </summary>
		/// <param name="closeStatus">The close status to use</param>
		/// <param name="closeStatusDescription">A description of why we are closing</param>
		/// <param name="exception">The exception (for logging)</param>
		/// <param name="onCancel">The action to fire when got operation canceled exception</param>
		/// <param name="onError">The action to fire when got error exception</param>
		/// <returns></returns>
		internal async Task CloseOutputTimeoutAsync(WebSocketCloseStatus closeStatus, string closeStatusDescription, Exception exception, Action onCancel = null, Action<Exception> onError = null)
		{
			var timespan = TimeSpan.FromSeconds(4);
			Events.Log.CloseOutputAutoTimeout(this.ID, closeStatus, closeStatusDescription, exception != null ? exception.ToString() : "N/A");

			try
			{
				using (var cts = new CancellationTokenSource(timespan))
				{
					await Task.WhenAll(
						this.CloseOutputAsync(closeStatus, (closeStatusDescription ?? "") + (this.IncludeExceptionInCloseResponse && exception != null ? "\r\n\r\n" + exception.ToString() : ""), CancellationToken.None),
						Task.Delay((int)timespan.TotalSeconds - 1, cts.Token)
					).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException)
			{
				Events.Log.CloseOutputAutoTimeoutCancelled(this.ID, (int)timespan.TotalSeconds, closeStatus, closeStatusDescription, exception != null ? exception.ToString() : "N/A");
				onCancel?.Invoke();
			}
			catch (Exception closeException)
			{
				Events.Log.CloseOutputAutoTimeoutError(this.ID, closeException.ToString(), closeStatus, closeStatusDescription, exception != null ? exception.ToString() : "N/A");
				Logger.Log<ManagedWebSocket>(LogLevel.Debug, LogLevel.Warning, $"Error occurred while closing a WebSocket connection by time-out: {closeException.Message} ({this.ID} @ {this.RemoteEndPoint})", closeException);
				onError?.Invoke(closeException);
			}
		}

		/// <summary>
		/// Cleans up unmanaged resources (will send a close frame if the connection is still open)
		/// </summary>
		public override void Dispose()
			=> this.DisposeAsync().Wait(4321);

		internal virtual async Task DisposeAsync(WebSocketCloseStatus closeStatus = WebSocketCloseStatus.EndpointUnavailable, string closeStatusDescription = "Service is unavailable", CancellationToken cancellationToken = default(CancellationToken), Action onDisposed = null)
		{
			if (!this._disposing && !this._disposed)
			{
				this._disposing = true;
				Events.Log.WebSocketDispose(this.ID, this.State);
				if (this.State == WebSocketState.Open)
					await this.CloseOutputTimeoutAsync(closeStatus, closeStatusDescription, null, () => Events.Log.WebSocketDisposeCloseTimeout(this.ID, this.State), ex => Events.Log.WebSocketDisposeError(this.ID, this.State, ex.ToString())).ConfigureAwait(false);
				try
				{
					onDisposed?.Invoke();
				}
				catch { }
				this._disposed = true;
				this._disposing = false;
			}
		}

		internal virtual void Close() { }

		~ManagedWebSocket()
		{
			this.Dispose();
			GC.SuppressFinalize(this);
		}
		#endregion

	}
}