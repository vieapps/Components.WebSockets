#region Related components
using System;
using System.Net.Security;
using System.Net.WebSockets;
using System.Diagnostics.Tracing;
#endregion

namespace net.vieapps.Components.WebSockets
{
	/// <summary>
	/// Use the Guid to locate this EventSource in PerfView using the Additional Providers box (without wildcard characters)
	/// </summary>
	[EventSource(Name = "WebSockets", Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")]
	internal sealed class Events : EventSource
	{
		public static Events Log = new Events();

		[Event(1, Level = EventLevel.Informational)]
		public void ClientConnectingToIPAddress(Guid guid, string ipAddress, int port)
		{
			if (this.IsEnabled())
				this.WriteEvent(1, guid, ipAddress, port);
		}

		[Event(2, Level = EventLevel.Informational)]
		public void ClientConnectingToHost(Guid guid, string host, int port)
		{
			if (this.IsEnabled())
				this.WriteEvent(2, guid, host, port);
		}

		[Event(3, Level = EventLevel.Informational)]
		public void AttemptingToSecureConnection(Guid guid)
		{
			if (this.IsEnabled())
				this.WriteEvent(3, guid);
		}

		[Event(4, Level = EventLevel.Informational)]
		public void ConnectionSecured(Guid guid)
		{
			if (this.IsEnabled())
				this.WriteEvent(4, guid);
		}

		[Event(5, Level = EventLevel.Informational)]
		public void ConnectionNotSecured(Guid guid)
		{
			if (this.IsEnabled())
				this.WriteEvent(5, guid);
		}

		[Event(6, Level = EventLevel.Error)]
		public void ClientSslCertificateError(Guid guid, string exception)
		{
			if (this.IsEnabled())
				this.WriteEvent(6, guid, exception);
		}

		[Event(7, Level = EventLevel.Informational)]
		public void HandshakeSent(Guid guid, string httpHeader)
		{
			if (this.IsEnabled())
				this.WriteEvent(7, guid, httpHeader ?? string.Empty);
		}

		[Event(8, Level = EventLevel.Informational)]
		public void ReadingResponse(Guid guid)
		{
			if (this.IsEnabled())
				this.WriteEvent(8, guid);
		}

		[Event(9, Level = EventLevel.Error)]
		public void ReadResponseError(Guid guid, string exception)
		{
			if (this.IsEnabled())
				this.WriteEvent(9, guid, exception ?? string.Empty);
		}

		[Event(10, Level = EventLevel.Warning)]
		public void InvalidResponseCode(Guid guid, string response)
		{
			if (this.IsEnabled())
				this.WriteEvent(10, guid, response ?? string.Empty);
		}

		[Event(11, Level = EventLevel.Error)]
		public void HandshakeFailure(Guid guid, string message)
		{
			if (this.IsEnabled())
				this.WriteEvent(11, guid, message ?? string.Empty);
		}

		[Event(12, Level = EventLevel.Informational)]
		public void ClientHandshakeSuccess(Guid guid)
		{
			if (this.IsEnabled())
				this.WriteEvent(12, guid);
		}

		[Event(13, Level = EventLevel.Informational)]
		public void ServerHandshakeSuccess(Guid guid)
		{
			if (this.IsEnabled())
				this.WriteEvent(13, guid);
		}

		[Event(14, Level = EventLevel.Informational)]
		public void AcceptWebSocketStarted(Guid guid)
		{
			if (this.IsEnabled())
				this.WriteEvent(14, guid);
		}

		[Event(15, Level = EventLevel.Informational)]
		public void SendingHandshake(Guid guid, string response)
		{
			if (this.IsEnabled())
				this.WriteEvent(15, guid, response ?? string.Empty);
		}

		[Event(16, Level = EventLevel.Error)]
		public void WebSocketVersionNotSupported(Guid guid, string exception)
		{
			if (this.IsEnabled())
				this.WriteEvent(16, guid, exception ?? string.Empty);
		}

		[Event(17, Level = EventLevel.Error)]
		public void BadRequest(Guid guid, string exception)
		{
			if (this.IsEnabled())
				this.WriteEvent(17, guid, exception ?? string.Empty);
		}

		[Event(18, Level = EventLevel.Informational)]
		public void KeepAliveIntervalZero(Guid guid)
		{
			if (this.IsEnabled())
				this.WriteEvent(18, guid);
		}

		[Event(19, Level = EventLevel.Informational)]
		public void PingPongManagerStarted(Guid guid, int keepAliveIntervalSeconds)
		{
			if (this.IsEnabled())
				this.WriteEvent(19, guid, keepAliveIntervalSeconds);
		}

		[Event(20, Level = EventLevel.Informational)]
		public void PingPongManagerEnded(Guid guid)
		{
			if (this.IsEnabled())
				this.WriteEvent(20, guid);
		}

		[Event(21, Level = EventLevel.Warning)]
		public void KeepAliveIntervalExpired(Guid guid, int keepAliveIntervalSeconds)
		{
			if (this.IsEnabled())
				this.WriteEvent(21, guid, keepAliveIntervalSeconds);
		}

		[Event(22, Level = EventLevel.Warning)]
		public void CloseOutputAutoTimeout(Guid guid, WebSocketCloseStatus closeStatus, string statusDescription, string exception)
		{
			if (this.IsEnabled())
				this.WriteEvent(22, guid, closeStatus, statusDescription ?? string.Empty, exception ?? string.Empty);
		}

		[Event(23, Level = EventLevel.Error)]
		public void CloseOutputAutoTimeoutCancelled(Guid guid, int timeoutSeconds, WebSocketCloseStatus closeStatus, string statusDescription, string exception)
		{
			if (this.IsEnabled())
				this.WriteEvent(23, guid, timeoutSeconds, closeStatus, statusDescription ?? string.Empty, exception ?? string.Empty);
		}

		[Event(24, Level = EventLevel.Error)]
		public void CloseOutputAutoTimeoutError(Guid guid, string closeException, WebSocketCloseStatus closeStatus, string statusDescription, string exception)
		{
			if (this.IsEnabled())
				this.WriteEvent(24, guid, closeException ?? string.Empty, closeStatus, statusDescription ?? string.Empty, exception ?? string.Empty);
		}

		[Event(25, Level = EventLevel.Error)]
		public void ServerSslCertificateError(Guid guid, string exception)
		{
			if (this.IsEnabled())
				this.WriteEvent(25, guid, exception);
		}

		[Event(26, Level = EventLevel.Verbose)]
		public void SendingFrame(Guid guid, WebSocketOpCode webSocketOpCode, bool isFinBitSet, int numBytes, bool isPayloadCompressed)
		{
			if (this.IsEnabled(EventLevel.Verbose, EventKeywords.None))
				this.WriteEvent(26, guid, webSocketOpCode, isFinBitSet, numBytes, isPayloadCompressed);
		}

		[Event(27, Level = EventLevel.Verbose)]
		public void ReceivedFrame(Guid guid, WebSocketOpCode webSocketOpCode, bool isFinBitSet, int numBytes)
		{
			if (this.IsEnabled(EventLevel.Verbose, EventKeywords.None))
				this.WriteEvent(27, guid, webSocketOpCode, isFinBitSet, numBytes);
		}

		[Event(28, Level = EventLevel.Informational)]
		public void CloseOutputNoHandshake(Guid guid, WebSocketCloseStatus? closeStatus, string statusDescription)
		{
			if (this.IsEnabled())
				this.WriteEvent(28, guid, $"{closeStatus}", statusDescription ?? string.Empty);
		}

		[Event(29, Level = EventLevel.Informational)]
		public void CloseHandshakeStarted(Guid guid, WebSocketCloseStatus? closeStatus, string statusDescription)
		{
			if (this.IsEnabled())
				this.WriteEvent(29, guid, $"{closeStatus}", statusDescription ?? string.Empty);
		}

		[Event(30, Level = EventLevel.Informational)]
		public void CloseHandshakeRespond(Guid guid, WebSocketCloseStatus? closeStatus, string statusDescription)
		{
			if (this.IsEnabled())
				this.WriteEvent(30, guid, $"{closeStatus}", statusDescription ?? string.Empty);
		}

		[Event(31, Level = EventLevel.Informational)]
		public void CloseHandshakeComplete(Guid guid)
		{
			if (this.IsEnabled())
				this.WriteEvent(31, guid);
		}

		[Event(32, Level = EventLevel.Warning)]
		public void CloseFrameReceivedInUnexpectedState(Guid guid, WebSocketState webSocketState, WebSocketCloseStatus? closeStatus, string statusDescription)
		{
			if (this.IsEnabled())
				this.WriteEvent(32, guid, webSocketState, $"{closeStatus}", statusDescription ?? string.Empty);
		}

		[Event(33, Level = EventLevel.Informational)]
		public void WebSocketDispose(Guid guid, WebSocketState webSocketState)
		{
			if (this.IsEnabled())
				this.WriteEvent(33, guid, webSocketState);
		}

		[Event(34, Level = EventLevel.Warning)]
		public void WebSocketDisposeCloseTimeout(Guid guid, WebSocketState webSocketState)
		{
			if (this.IsEnabled())
				this.WriteEvent(34, guid, webSocketState);
		}

		[Event(35, Level = EventLevel.Error)]
		public void WebSocketDisposeError(Guid guid, WebSocketState webSocketState, string exception)
		{
			if (this.IsEnabled())
				this.WriteEvent(35, guid, webSocketState, exception ?? string.Empty);
		}

		[Event(36, Level = EventLevel.Warning)]
		public void InvalidStateBeforeClose(Guid guid, WebSocketState webSocketState)
		{
			if (this.IsEnabled())
				this.WriteEvent(36, guid, webSocketState);
		}

		[Event(37, Level = EventLevel.Warning)]
		public void InvalidStateBeforeCloseOutput(Guid guid, WebSocketState webSocketState)
		{
			if (this.IsEnabled())
				this.WriteEvent(37, guid, webSocketState);
		}

		[Event(38, Level = EventLevel.Warning)]
		public void PendingOperations(Guid guid)
		{
			if (this.IsEnabled())
				this.WriteEvent(38, guid);
		}
	}
}