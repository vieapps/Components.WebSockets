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

		internal static bool Remove(WebSocketConnection connection, WebSocketCloseStatus closeStatus = WebSocketCloseStatus.EndpointUnavailable, string closeStatusDescription = "Service is unavailable")
		{
			if (connection != null && WebSocketConnectionManager.Connections.TryRemove(connection.ID, out WebSocketConnection instance))
			{
				instance.Dispose(closeStatus, closeStatusDescription);
				GC.Collect();
				return true;
			}
			return false;
		}

		internal static void Remove(List<WebSocketConnection> connections)
		{
			connections?.ForEach(connection => WebSocketConnectionManager.Remove(connection));
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
		public static Task SendAsync(Guid id, ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return WebSocketConnectionManager.Connections.TryGetValue(id, out WebSocketConnection connection)
				? connection.SendAsync(buffer, messageType, endOfMessage, cancellationToken)
				: Task.CompletedTask;
		}

		/// <summary>
		/// Sends the message to a WebSocket connection
		/// </summary>
		/// <param name="id">The identity of a WebSocket connection to send</param>
		/// <param name="message">The text message to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public static Task SendAsync(Guid id, string message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return WebSocketConnectionManager.Connections.TryGetValue(id, out WebSocketConnection connection)
				? connection.SendAsync(message, endOfMessage, cancellationToken)
				: Task.CompletedTask;
		}

		/// <summary>
		/// Sends the message to a WebSocket connection
		/// </summary>
		/// <param name="id">The identity of a WebSocket connection to send</param>
		/// <param name="message">The binary message to send</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public static Task SendAsync(Guid id, byte[] message, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return WebSocketConnectionManager.Connections.TryGetValue(id, out WebSocketConnection connection)
				? connection.SendAsync(message, endOfMessage, cancellationToken)
				: Task.CompletedTask;
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
			await connections.ForEachAsync((connection, token) => connection.SendAsync(buffer, messageType, endOfMessage, token), cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Sends the message to all WebSocket connections
		/// </summary>
		/// <param name="buffer">The buffer containing data to send</param>
		/// <param name="messageType">The message type. Can be Text or Binary</param>
		/// <param name="endOfMessage">true if this message is a standalone message (this is the norm), false if it is a multi-part message (and true for the last message)</param>
		/// <param name="cancellationToken">the cancellation token</param>
		public static Task SendAllAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken = default(CancellationToken))
		{
			return WebSocketConnectionManager.SendAsync(connection => true, buffer, messageType, endOfMessage, cancellationToken);
		}
	}
}