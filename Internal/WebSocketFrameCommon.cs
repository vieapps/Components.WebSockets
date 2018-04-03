using System;

namespace net.vieapps.Components.WebSockets.Internal
{
    internal static class WebSocketFrameCommon
    {
        public const int MaskKeyLength = 4;

        /// <summary>
        /// Mutate payload with the mask key
        /// This is a reversible process
        /// If you apply this to masked data it will be unmasked and visa versa
        /// </summary>
        /// <param name="maskKey">The 4 byte mask key</param>
        /// <param name="payload">The payload to mutate</param>
        public static void ToggleMask(ArraySegment<byte> maskKey, ArraySegment<byte> payload)
        {
            if (maskKey.Count != MaskKeyLength)
            {
                throw new Exception($"MaskKey key must be {WebSocketFrameCommon.MaskKeyLength} bytes");
            }

            var buffer = payload.Array;
			var maskKeyArray = maskKey.Array;

            // apply the mask key (this is a reversible process so no need to copy the payload)
            for (var index = payload.Offset; index < payload.Count; index++)
            {
                int payloadIndex = index - payload.Offset; // index should start at zero
                int maskKeyIndex = maskKey.Offset + (payloadIndex % MaskKeyLength);
                buffer[index] = (Byte)(buffer[index] ^ maskKeyArray[maskKeyIndex]);
            }
        }
    }
}
