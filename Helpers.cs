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
		/// Reads an HTTP header as per the HTTP specification
		/// </summary>
		/// <param name="stream">The stream to read UTF8 text from</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns>The HTTP header</returns>
		public static async Task<string> ReadHttpHeaderAsync(Stream stream, CancellationToken cancellationToken = default(CancellationToken))
		{
			var length = 1024 * 16; // 16KB buffer more than enough for HTTP header
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
			var bytes = (response.Trim() + "\r\n\r\n").ToBytes();
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

}