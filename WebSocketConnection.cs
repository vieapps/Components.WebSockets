#region Related components
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.IO;
using Microsoft.Extensions.Logging;

using net.vieapps.Components.Utility;
using net.vieapps.Components.WebSockets.Internal;
#endregion

namespace net.vieapps.Components.WebSockets
{
	/// <summary>
	/// The WebSocket Connection
	/// </summary>
	public class WebSocketConnection : IDisposable
	{
		internal const int BufferLength = 4 * 1024 * 1024;
		const int DefaultBlockSize = 16 * 1024;
		const int MaxBufferSize = 128 * 1024;

		/// <summary>
		/// Gets a factory to get recyclable memory stream with  RecyclableMemoryStreamManager class to limit LOH fragmentation and improve performance
		/// </summary>
		/// <returns></returns>
		public static Func<MemoryStream> GetRecyclableMemoryStreamFactory()
		{
			return new RecyclableMemoryStreamManager(WebSocketConnection.DefaultBlockSize, 4, WebSocketConnection.MaxBufferSize).GetStream;
		}

		public void Dispose()
		{
			this.WebSocket?.Dispose();
		}

		~WebSocketConnection()
		{
			this.Dispose();
			GC.SuppressFinalize(this);
		}

		/// <summary>
		/// Gets the WebSocket object of this connection
		/// </summary>
		public WebSocket WebSocket { get; internal set; }

		/// <summary>
		/// Gets the identity of this connection
		/// </summary>
		public Guid ID
		{
			get
			{
				return this.WebSocket != null
					? (this.WebSocket as WebSocketImplementation).ID
					: Guid.Empty;
			}
		}

		/// <summary>
		/// Gets the state that specified this is connection of WebSocket client
		/// </summary>
		public bool IsWebSocketClientConnection
		{
			get
			{
				return this.WebSocket != null
					? (this.WebSocket as WebSocketImplementation).IsClient
					: false;
			}
		}

		/// <summary>
		/// Gets the time when this connection is established
		/// </summary>
		public DateTime Time { get; internal set; } = DateTime.Now;

		/// <summary>
		/// Gets the remote endpoint of this connection
		/// </summary>
		public string EndPoint { get; internal set; }
	}

	// ----------------------------------------------------------

	/// <summary>
	/// The manager of all WebSocket connections
	/// </summary>
	public static class WebSocketConnectionManager
	{
		internal static ConcurrentDictionary<Guid, WebSocketConnection> Connections = new ConcurrentDictionary<Guid, WebSocketConnection>();

		internal static bool Add(WebSocketConnection connection)
		{
			return connection != null
				? WebSocketConnectionManager.Connections.TryAdd(connection.ID, connection)
				: false;
		}

		internal static bool Remove(WebSocketConnection connection)
		{
			if (connection != null && WebSocketConnectionManager.Connections.TryRemove(connection.ID, out WebSocketConnection instance))
			{
				instance.Dispose();
				return true;
			}
			return false;
		}

		/// <summary>
		/// Gets a connection that specifed by identity
		/// </summary>
		/// <param name="id"></param>
		/// <returns></returns>
		public static WebSocketConnection Get(Guid id)
		{
			return WebSocketConnectionManager.Connections.TryGetValue(id, out WebSocketConnection connection)
				? connection
				: null;
		}

		/// <summary>
		/// Gets the collection of connections that matched with the predicate
		/// </summary>
		/// <param name="predicate"></param>
		/// <returns></returns>
		public static IEnumerable<WebSocketConnection> Get(Func<WebSocketConnection, bool> predicate)
		{
			return WebSocketConnectionManager.Connections.Where(kvp => predicate != null ? predicate(kvp.Value) : false).Select(kvp => kvp.Value);
		}

		/// <summary>
		/// Gets the collection of all current connections
		/// </summary>
		/// <returns></returns>
		public static IEnumerable<WebSocketConnection> GetAll()
		{
			return WebSocketConnectionManager.Connections.Select(kvp => kvp.Value);
		}

		/// <summary>
		/// Sends the message to a WebSocket connection
		/// </summary>
		/// <param name="id">The identity of a WebSocket connection to send</param>
		/// <param name="buffer">The buffer containing data to send</param>
		/// <param name="messageType">The message type. Can be Text or Binary</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public static async Task SendAsync(Guid id, ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			var connection = WebSocketConnectionManager.Get(id);
			if (connection != null)
				await connection.WebSocket.SendAsync(buffer, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Sends the message to the WebSocket connections that matched with the predicate
		/// </summary>
		/// <param name="predicate">The predicate for selecting connections</param>
		/// <param name="buffer">The buffer containing data to send</param>
		/// <param name="messageType">The message type. Can be Text or Binary</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public static async Task SendAsync(Func<WebSocketConnection, bool> predicate, ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			var connections = WebSocketConnectionManager.Connections.Where(kvp => predicate != null ? predicate(kvp.Value) : false).Select(kvp => kvp.Value);
			await connections.ForEachAsync((connection, token) => connection.WebSocket.SendAsync(buffer, messageType, endOfMessage, token), cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Sends the message to all WebSocket connections
		/// </summary>
		/// <param name="buffer">The buffer containing data to send</param>
		/// <param name="messageType">The message type. Can be Text or Binary</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public static async Task SendAllAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			await WebSocketConnectionManager.SendAsync(connection => true, buffer, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
		}
	}
}