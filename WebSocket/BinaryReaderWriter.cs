#region Related components
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Components.WebSockets.Implementation
{
	internal static class BinaryReaderWriter
    {
        public static async Task ReadExactlyAsync(int length, Stream stream, ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (length == 0)
				return;

			if (buffer.Count < length)
            {
                // TODO: it is not impossible to get rid of this, just a little tricky
                // if the supplied buffer is too small for the payload then we should only return the number of bytes in the buffer
                // this will have to propogate all the way up the chain
                throw new InternalBufferOverflowException($"Unable to read {length} bytes into buffer (offset: {buffer.Offset} size: {buffer.Count}). Use a larger read buffer");
            }

            var offset = 0;
            do
            {
				var read = await stream.ReadAsync(buffer.Array, buffer.Offset + offset, length - offset, cancellationToken).ConfigureAwait(false);
                if (read == 0)
					throw new EndOfStreamException($"Unexpected end of stream encountered whilst attempting to read {length:#,##0} bytes");
				offset += read;
            }
			while (offset < length);
        }

        public static async Task<ushort> ReadUShortExactlyAsync(Stream stream, bool isLittleEndian, ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await BinaryReaderWriter.ReadExactlyAsync(2, stream, buffer, cancellationToken).ConfigureAwait(false);

            if (!isLittleEndian)
				Array.Reverse(buffer.Array, buffer.Offset, 2);

			return BitConverter.ToUInt16(buffer.Array, buffer.Offset);
        }

        public static async Task<ulong> ReadULongExactlyAsync(Stream stream, bool isLittleEndian, ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await BinaryReaderWriter.ReadExactlyAsync(8, stream, buffer, cancellationToken).ConfigureAwait(false);

            if (!isLittleEndian)
				Array.Reverse(buffer.Array, buffer.Offset, 8);

			return BitConverter.ToUInt64(buffer.Array, buffer.Offset);
        }

        public static async Task<long> ReadLongExactlyAsync(Stream stream, bool isLittleEndian, ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await BinaryReaderWriter.ReadExactlyAsync(8, stream, buffer, cancellationToken).ConfigureAwait(false);

            if (!isLittleEndian)
				Array.Reverse(buffer.Array, buffer.Offset, 8);

			return BitConverter.ToInt64(buffer.Array, buffer.Offset);
        }

        public static void WriteInt(int value, Stream stream, bool isLittleEndian)
        {
			var buffer = value.ToBytes();
            if (BitConverter.IsLittleEndian && !isLittleEndian)
				Array.Reverse(buffer);

			stream.Write(buffer, 0, buffer.Length);
        }

        public static void WriteULong(ulong value, Stream stream, bool isLittleEndian)
        {
			var buffer = value.ToBytes();
			if (BitConverter.IsLittleEndian && ! isLittleEndian)
				Array.Reverse(buffer);

			stream.Write(buffer, 0, buffer.Length);
        }

        public static void WriteLong(long value, Stream stream, bool isLittleEndian)
        {
			var buffer = value.ToBytes();
			if (BitConverter.IsLittleEndian && !isLittleEndian)
				Array.Reverse(buffer);

			stream.Write(buffer, 0, buffer.Length);
        }

        public static void WriteUShort(ushort value, Stream stream, bool isLittleEndian)
        {
			var buffer = value.ToBytes();
			if (BitConverter.IsLittleEndian && !isLittleEndian)
				Array.Reverse(buffer);

			stream.Write(buffer, 0, buffer.Length);
        }
    }
}