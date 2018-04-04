#region Related components
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using net.vieapps.Components.Utility;
using net.vieapps.Components.WebSockets.Exceptions;
#endregion

namespace net.vieapps.Components.WebSockets
{
	public class HttpHelper
	{
		const string _HTTP_GET_HEADER_REGEX = @"^GET(.*)HTTP\/1\.1";

		/// <summary>
		/// Calculates a random WebSocket key that can be used to initiate a WebSocket handshake
		/// </summary>
		/// <returns>A random websocket key</returns>
		public static string CalculateWebSocketKey()
		{
			// this is not used for cryptography so doing something simple like he code below is op
			var rand = new Random((int)DateTime.Now.Ticks);
			byte[] keyAsBytes = new byte[16];
			rand.NextBytes(keyAsBytes);
			return keyAsBytes.ToBase64();
		}

		/// <summary>
		/// Computes a WebSocket accept string from a given key
		/// </summary>
		/// <param name="secWebSocketKey">The web socket key to base the accept string on</param>
		/// <returns>A web socket accept string</returns>
		public static string ComputeSocketAcceptString(string secWebSocketKey)
		{
			// this is a guid as per the web socket spec
			const string webSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
			return (secWebSocketKey + webSocketGuid).GetHash("SHA1").ToBase64();
		}

		/// <summary>
		/// Reads an HTTP header as per the HTTP spec
		/// </summary>
		/// <param name="stream">The stream to read UTF8 text from</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns>The HTTP header</returns>
		public static async Task<string> ReadHttpHeaderAsync(Stream stream, CancellationToken cancellationToken = default(CancellationToken))
		{
			var length = 1024 * 16; // 16KB buffer more than enough for http header
			var buffer = new byte[length];
			var offset = 0;
			var read = 0;

			do
			{
				if (offset >= length)
					throw new EntityTooLargeException("Http header message too large to fit in buffer (16KB)");

				read = await stream.ReadAsync(buffer, offset, length - offset, cancellationToken).ConfigureAwait(false);
				offset += read;
				var header = Encoding.UTF8.GetString(buffer, 0, offset);

				// as per http specification, all headers should end this this
				if (header.Contains("\r\n\r\n"))
					return header;

			}
			while (read > 0);

			return string.Empty;
		}

		/// <summary>
		/// Decodes the header to detect is this is a web socket upgrade response
		/// </summary>
		/// <param name="header">The HTTP header</param>
		/// <returns>True if this is an http WebSocket upgrade response</returns>
		public static bool IsWebSocketUpgradeRequest(String header)
		{
			var getRegex = new Regex(_HTTP_GET_HEADER_REGEX, RegexOptions.IgnoreCase);
			var getRegexMatch = getRegex.Match(header);

			if (getRegexMatch.Success)
			{
				// check if this is a web socket upgrade request
				var webSocketUpgradeRegex = new Regex("Upgrade: websocket", RegexOptions.IgnoreCase);
				var webSocketUpgradeRegexMatch = webSocketUpgradeRegex.Match(header);
				return webSocketUpgradeRegexMatch.Success;
			}

			return false;
		}

		/// <summary>
		/// Gets the path from the HTTP header
		/// </summary>
		/// <param name="httpHeader">The HTTP header to read</param>
		/// <returns>The path</returns>
		public static string GetPathFromHeader(string httpHeader)
		{
			var getRegex = new Regex(_HTTP_GET_HEADER_REGEX, RegexOptions.IgnoreCase);
			var getRegexMatch = getRegex.Match(httpHeader);

			// extract the path attribute from the first line of the header
			if (getRegexMatch.Success)
				return getRegexMatch.Groups[1].Value.Trim();

			return null;
		}

		/// <summary>
		/// Reads the HTTP response code from the http response string
		/// </summary>
		/// <param name="response">The response string</param>
		/// <returns>the response code</returns>
		public static string ReadHttpResponseCode(string response)
		{
			var getRegex = new Regex(@"HTTP\/1\.1 (.*)", RegexOptions.IgnoreCase);
			var getRegexMatch = getRegex.Match(response);

			// extract the path attribute from the first line of the header
			if (getRegexMatch.Success)
				return getRegexMatch.Groups[1].Value.Trim();

			return null;
		}

		/// <summary>
		/// Writes an HTTP response string to the stream
		/// </summary>
		/// <param name="response">The response (without the new line characters)</param>
		/// <param name="stream">The stream to write to</param>
		/// <param name="cancellationToken">The cancellation token</param>
		public static async Task WriteHttpHeaderAsync(string response, Stream stream, CancellationToken cancellationToken = default(CancellationToken))
		{
			response = response.Trim() + "\r\n\r\n";
			var bytes = Encoding.UTF8.GetBytes(response);
			await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
		}
	}

	#region Logger
	public static class Logger
	{
		static ILoggerFactory LoggerFactory;

		/// <summary>
		/// Assigns a logger factory
		/// </summary>
		/// <param name="loggerFactory"></param>
		public static void AssignLoggerFactory(ILoggerFactory loggerFactory)
		{
			if (Logger.LoggerFactory == null && loggerFactory != null)
				Logger.LoggerFactory = loggerFactory;
		}

		/// <summary>
		/// Gets a logger factory
		/// </summary>
		/// <returns></returns>
		public static ILoggerFactory GetLoggerFactory()
		{
			return Logger.LoggerFactory ?? new NullLoggerFactory();
		}

		/// <summary>
		/// Creates a logger
		/// </summary>
		/// <param name="type"></param>
		/// <returns></returns>
		public static ILogger CreateLogger(Type type)
		{
			return (Logger.LoggerFactory ?? new NullLoggerFactory()).CreateLogger(type);
		}

		/// <summary>
		/// Creates a logger
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <returns></returns>
		public static ILogger CreateLogger<T>()
		{
			return Logger.CreateLogger(typeof(T));
		}
	}
	#endregion

	#region NullLogger
	public class NullLoggerFactory : ILoggerFactory
	{
		public void AddProvider(ILoggerProvider provider) { }

		public ILogger CreateLogger(string categoryName)
		{
			return NullLogger.Instance;
		}

		public void Dispose() { }
	}

	public class NullLogger : ILogger
	{
		internal static NullLogger Instance = new NullLogger();

		private NullLogger() { }

		public IDisposable BeginScope<TState>(TState state)
		{
			return null;
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return false;
		}

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) { }
	}
	#endregion

	#region QueuedStream (from Fleck)
	/// <summary>
	/// Wraps a stream and queues multiple write operations.
	/// Useful for wrapping SslStream as it does not support multiple simultaneous write operations.
	/// </summary>
	public class QueuedStream : Stream
	{
		readonly Stream _stream;
		readonly Queue<WriteData> _queue = new Queue<WriteData>();
		int _pendingWrite;
		bool _disposed;

		public QueuedStream(Stream stream)
		{
			this._stream = stream;
		}

		public override bool CanRead
		{
			get { return _stream.CanRead; }
		}

		public override bool CanSeek
		{
			get { return _stream.CanSeek; }
		}

		public override bool CanWrite
		{
			get { return _stream.CanWrite; }
		}

		public override long Length
		{
			get { return _stream.Length; }
		}

		public override long Position
		{
			get { return _stream.Position; }
			set { _stream.Position = value; }
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			return _stream.Read(buffer, offset, count);
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			return _stream.Seek(offset, origin);
		}

		public override void SetLength(long value)
		{
			_stream.SetLength(value);
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException("QueuedStream does not support synchronous write operations yet.");
		}

		public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
		{
			return _stream.BeginRead(buffer, offset, count, callback, state);
		}

		public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
		{
			lock (_queue)
			{
				var data = new WriteData(buffer, offset, count, callback, state);
				if (_pendingWrite > 0)
				{
					_queue.Enqueue(data);
					return data.AsyncResult;
				}
				return BeginWriteInternal(buffer, offset, count, callback, state, data);
			}
		}

		public override int EndRead(IAsyncResult asyncResult)
		{
			return _stream.EndRead(asyncResult);
		}

		public override void EndWrite(IAsyncResult asyncResult)
		{
			if (asyncResult is QueuedWriteResult)
			{
				var queuedResult = asyncResult as QueuedWriteResult;
				if (queuedResult.Exception != null) throw queuedResult.Exception;
				var ar = queuedResult.ActualResult;
				if (ar == null)
				{
					throw new NotSupportedException(
						"QueuedStream does not support synchronous write operations. Please wait for callback to be invoked before calling EndWrite.");
				}
				// EndWrite on actual stream should already be invoked.
			}
			else
			{
				throw new ArgumentException();
			}
		}

		public override void Flush()
		{
			_stream.Flush();
		}

		public override void Close()
		{
			_stream.Close();
		}

		protected override void Dispose(bool disposing)
		{
			if (!_disposed)
			{
				if (disposing)
				{
					_stream.Dispose();
				}
				_disposed = true;
			}
			base.Dispose(disposing);
		}

		IAsyncResult BeginWriteInternal(byte[] buffer, int offset, int count, AsyncCallback callback, object state, WriteData queued)
		{
			_pendingWrite++;
			var result = _stream.BeginWrite(buffer, offset, count, ar =>
			{
				// callback can be executed even before return value of BeginWriteInternal is set to this property
				queued.AsyncResult.ActualResult = ar;
				try
				{
					// so that we can call BeginWrite again
					_stream.EndWrite(ar);
				}
				catch (Exception exc)
				{
					queued.AsyncResult.Exception = exc;
				}

				// one down, another is good to go
				lock (_queue)
				{
					_pendingWrite--;
					while (_queue.Count > 0)
					{
						var data = _queue.Dequeue();
						try
						{
							data.AsyncResult.ActualResult = BeginWriteInternal(data.Buffer, data.Offset, data.Count, data.Callback, data.State, data);
							break;
						}
						catch (Exception exc)
						{
							_pendingWrite--;
							data.AsyncResult.Exception = exc;
							data.Callback(data.AsyncResult);
						}
					}
					callback(queued.AsyncResult);
				}
			}, state);

			// always return the wrapped async result.
			// this is especially important if the underlying stream completed the operation synchronously (hence "result.CompletedSynchronously" is true!)
			queued.AsyncResult.ActualResult = result;
			return queued.AsyncResult;
		}

		#region Nested type: WriteData
		class WriteData
		{
			public readonly byte[] Buffer;
			public readonly int Offset;
			public readonly int Count;
			public readonly AsyncCallback Callback;
			public readonly object State;
			public readonly QueuedWriteResult AsyncResult;

			public WriteData(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
			{
				Buffer = buffer;
				Offset = offset;
				Count = count;
				Callback = callback;
				State = state;
				AsyncResult = new QueuedWriteResult(state);
			}
		}
		#endregion

		#region Nested type: QueuedWriteResult
		class QueuedWriteResult : IAsyncResult
		{
			readonly object _state;

			public QueuedWriteResult(object state)
			{
				_state = state;
			}

			public Exception Exception { get; set; }

			public IAsyncResult ActualResult { get; set; }

			public object AsyncState
			{
				get { return _state; }
			}

			public WaitHandle AsyncWaitHandle
			{
				get { throw new NotSupportedException("Queued write operations do not support wait handle."); }
			}

			public bool CompletedSynchronously
			{
				get { return false; }
			}

			public bool IsCompleted
			{
				get { return ActualResult != null && ActualResult.IsCompleted; }
			}
		}
		#endregion

	}
	#endregion

}