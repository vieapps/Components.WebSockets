#region Related components
using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using net.vieapps.Components.Utility;
using net.vieapps.Components.WebSockets.Exceptions;
#endregion

namespace net.vieapps.Components.WebSockets.Implementation
{
	internal static class FrameReaderWriter
    {
		/// <summary>
		/// This is used for data masking so that web proxies don't cache the data
		/// Therefore, there are no cryptographic concerns
		/// </summary>
		static readonly Random Random;

		static FrameReaderWriter()
		{
			FrameReaderWriter.Random = new Random((int)DateTime.Now.Ticks);
		}

		/// <summary>
		/// Read a WebSocket frame from the stream
		/// </summary>
		/// <param name="fromStream">The stream to read from</param>
		/// <param name="intoBuffer">The buffer to read into</param>
		/// <param name="cancellationToken">the cancellation token</param>
		/// <returns>A websocket frame</returns>
		public static async Task<WebSocketFrame> ReadAsync(Stream fromStream, ArraySegment<byte> intoBuffer, CancellationToken cancellationToken)
        {
            // allocate a small buffer to read small chunks of data from the stream
            var smallBuffer = new ArraySegment<byte>(new byte[8]);

            await BinaryReaderWriter.ReadExactlyAsync(2, fromStream, smallBuffer, cancellationToken).ConfigureAwait(false);
            byte byte1 = smallBuffer.Array[0];
            byte byte2 = smallBuffer.Array[1];

            // process first byte
            byte finBitFlag = 0x80;
            byte opCodeFlag = 0x0F;
            bool isFinBitSet = (byte1 & finBitFlag) == finBitFlag;
            var opCode = (WebSocketOpCode) (byte1 & opCodeFlag);

            // read and process second byte
            byte maskFlag = 0x80;
            bool isMaskBitSet = (byte2 & maskFlag) == maskFlag;
            uint length = await FrameReaderWriter.ReadLengthAsync(byte2, smallBuffer, fromStream, cancellationToken).ConfigureAwait(false);
            int count = (int)length;

            // use the masking key to decode the data if needed
            if (isMaskBitSet)
            {
                var maskKey = new ArraySegment<byte>(smallBuffer.Array, 0, WebSocketFrame.MaskKeyLength);
                await BinaryReaderWriter.ReadExactlyAsync(maskKey.Count, fromStream, maskKey, cancellationToken).ConfigureAwait(false);
                await BinaryReaderWriter.ReadExactlyAsync(count, fromStream, intoBuffer, cancellationToken).ConfigureAwait(false);
				var payloadToMask = new ArraySegment<byte>(intoBuffer.Array, intoBuffer.Offset, count);
				WebSocketFrame.ToggleMask(maskKey, payloadToMask);
            }
            else
				await BinaryReaderWriter.ReadExactlyAsync(count, fromStream, intoBuffer, cancellationToken).ConfigureAwait(false);

			return opCode == WebSocketOpCode.ConnectionClose
				?  FrameReaderWriter.DecodeCloseFrame(isFinBitSet, opCode, count, intoBuffer)
				: new WebSocketFrame(isFinBitSet, opCode, count); // note that by this point the payload will be populated
        }

        /// <summary>
        /// Extracts close status and close description information from the web socket frame
        /// </summary>
        private static WebSocketFrame DecodeCloseFrame(bool isFinBitSet, WebSocketOpCode opCode, int count, ArraySegment<byte> buffer)
        {
            WebSocketCloseStatus closeStatus;
            string closeStatusDescription;

            if (count >= 2)
            {
                Array.Reverse(buffer.Array, buffer.Offset, 2); // network byte order
                var closeStatusCode = (int)BitConverter.ToUInt16(buffer.Array, buffer.Offset);
				closeStatus = Enum.IsDefined(typeof(WebSocketCloseStatus), closeStatusCode)
					?  (WebSocketCloseStatus)closeStatusCode
					: WebSocketCloseStatus.Empty;

                int offset = buffer.Offset + 2;
                int descCount = count - 2;

				closeStatusDescription = descCount > 0
					? Encoding.UTF8.GetString(buffer.Array, offset, descCount)
					: null;
			}
            else
            {
                closeStatus = WebSocketCloseStatus.Empty;
                closeStatusDescription = null;
            }

            return new WebSocketFrame(isFinBitSet, opCode, count, closeStatus, closeStatusDescription);
        }

        /// <summary>
        /// Reads the length of the payload according to the contents of byte2
        /// </summary>
        static async Task<uint> ReadLengthAsync(byte byte2, ArraySegment<byte> smallBuffer, Stream fromStream, CancellationToken cancellationToken = default(CancellationToken))
        {
            byte payloadLengthFlag = 0x7F;
            var length = (uint) (byte2 & payloadLengthFlag);

            // read a short length or a long length depending on the value of len
            if (length == 126)
				length = await BinaryReaderWriter.ReadUShortExactlyAsync(fromStream, false, smallBuffer, cancellationToken).ConfigureAwait(false);

			else if (length == 127)
            {
                length = (uint)await BinaryReaderWriter.ReadULongExactlyAsync(fromStream, false, smallBuffer, cancellationToken).ConfigureAwait(false);
                const uint maxLength = 2147483648; // 2GB - not part of the spec but just a precaution. Send large volumes of data in smaller frames.

                // protect ourselves against bad data
                if (length > maxLength || length < 0)
					throw new ArgumentOutOfRangeException($"Payload length out of range. Min 0 max 2GB. Actual {length:#,##0} bytes.");
			}

			return length;
        }

		/// <summary>
		/// No async await stuff here because we are dealing with a memory stream
		/// </summary>
		/// <param name="opCode">The web socket opcode</param>
		/// <param name="fromPayload">Array segment to get payload data from</param>
		/// <param name="toStream">Stream to write to</param>
		/// <param name="isLastFrame">True is this is the last frame in this message (usually true)</param>
		public static void Write(WebSocketOpCode opCode, ArraySegment<byte> fromPayload, MemoryStream toStream, bool isLastFrame, bool isClient)
		{
			var memoryStream = toStream;
			var finBitSetAsByte = isLastFrame ? (byte)0x80 : (byte)0x00;
			var byte1 = (byte)(finBitSetAsByte | (byte)opCode);
			memoryStream.WriteByte(byte1);

			// NB, set the mask flag if we are constructing a client frame
			var maskBitSetAsByte = isClient ? (byte)0x80 : (byte)0x00;

			// depending on the size of the length we want to write it as a byte, ushort or ulong
			if (fromPayload.Count < 126)
			{
				var byte2 = (byte)(maskBitSetAsByte | (byte)fromPayload.Count);
				memoryStream.WriteByte(byte2);
			}
			else if (fromPayload.Count <= ushort.MaxValue)
			{
				var byte2 = (byte)(maskBitSetAsByte | 126);
				memoryStream.WriteByte(byte2);
				BinaryReaderWriter.WriteUShort((ushort)fromPayload.Count, memoryStream, false);
			}
			else
			{
				var byte2 = (byte)(maskBitSetAsByte | 127);
				memoryStream.WriteByte(byte2);
				BinaryReaderWriter.WriteULong((ulong)fromPayload.Count, memoryStream, false);
			}

			// if we are creating a client frame then we MUST mack the payload as per the spec
			if (isClient)
			{
				var maskKey = new byte[WebSocketFrame.MaskKeyLength];
				FrameReaderWriter.Random.NextBytes(maskKey);
				memoryStream.Write(maskKey, 0, maskKey.Length);

				// mask the payload
				var maskKeyArraySegment = new ArraySegment<byte>(maskKey, 0, maskKey.Length);
				WebSocketFrame.ToggleMask(maskKeyArraySegment, fromPayload);
			}

			memoryStream.Write(fromPayload.Array, fromPayload.Offset, fromPayload.Count);
		}
	}
}
