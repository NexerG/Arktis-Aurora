using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Arctis.Imaging
{
    // PNG's CRC-32 (ISO-HDLC polynomial), slicing by eight.
    internal static class Crc32
    {
        private static readonly uint[] table = Build();

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            uint[] t = table;
            crc = ~crc;
            int i = 0;
            for (; i + 8 <= data.Length; i += 8)
            {
                uint one = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i)) ^ crc;
                uint two = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 4));
                crc = t[7 * 256 + (one & 0xFF)] ^ t[6 * 256 + ((one >> 8) & 0xFF)] ^ t[5 * 256 + ((one >> 16) & 0xFF)] ^ t[4 * 256 + (one >> 24)]
                    ^ t[3 * 256 + (two & 0xFF)] ^ t[2 * 256 + ((two >> 8) & 0xFF)] ^ t[256 + ((two >> 16) & 0xFF)] ^ t[two >> 24];
            }
            for (; i < data.Length; i++)
                crc = t[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        private static uint[] Build()
        {
            uint[] t = new uint[8 * 256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            for (int s = 1; s < 8; s++)
                for (int n = 0; n < 256; n++)
                    t[s * 256 + n] = t[(s - 1) * 256 + n] >> 8 ^ t[t[(s - 1) * 256 + n] & 0xFF];
            return t;
        }
    }
}
