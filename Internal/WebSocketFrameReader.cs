#region Related components
using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#endregion

namespace net.vieapps.Components.WebSockets.Internal
{
    /// <summary>
    /// Reads a WebSocket frame
    /// see http://tools.ietf.org/html/rfc6455 for specification
    /// </summary>
    internal static class WebSocketFrameReader
    {
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
            uint len = await WebSocketFrameReader.ReadLengthAsync(byte2, smallBuffer, fromStream, cancellationToken).ConfigureAwait(false);
            int count = (int)len;

            // use the masking key to decode the data if needed
            if (isMaskBitSet)
            {
                var maskKey = new ArraySegment<byte>(smallBuffer.Array, 0, WebSocketFrameCommon.MaskKeyLength);
                await BinaryReaderWriter.ReadExactlyAsync(maskKey.Count, fromStream, maskKey, cancellationToken).ConfigureAwait(false);
                await BinaryReaderWriter.ReadExactlyAsync(count, fromStream, intoBuffer, cancellationToken).ConfigureAwait(false);
				var payloadToMask = new ArraySegment<byte>(intoBuffer.Array, intoBuffer.Offset, count);
                WebSocketFrameCommon.ToggleMask(maskKey, payloadToMask);
            }
            else
				await BinaryReaderWriter.ReadExactlyAsync(count, fromStream, intoBuffer, cancellationToken).ConfigureAwait(false);

			return opCode == WebSocketOpCode.ConnectionClose
				?  WebSocketFrameReader.DecodeCloseFrame(isFinBitSet, opCode, count, intoBuffer)
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
        private static async Task<uint> ReadLengthAsync(byte byte2, ArraySegment<byte> smallBuffer, Stream fromStream, CancellationToken cancellationToken = default(CancellationToken))
        {
            byte payloadLengthFlag = 0x7F;
            var length = (uint) (byte2 & payloadLengthFlag);

            // read a short length or a long length depending on the value of len
            if (length == 126)
				length = await BinaryReaderWriter.ReadUShortExactlyAsync(fromStream, false, smallBuffer, cancellationToken).ConfigureAwait(false);

			else if (length == 127)
            {
                length = (uint)await BinaryReaderWriter.ReadULongExactlyAsync(fromStream, false, smallBuffer, cancellationToken).ConfigureAwait(false);
                const uint maxLen = 2147483648; // 2GB - not part of the spec but just a precaution. Send large volumes of data in smaller frames.

                // protect ourselves against bad data
                if (length > maxLen || length < 0)
					throw new ArgumentOutOfRangeException($"Payload length out of range. Min 0 max 2GB. Actual {length:#,##0} bytes.");
			}

			return length;
        }
    }
}
