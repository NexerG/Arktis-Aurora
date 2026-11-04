using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
            if (Pclmulqdq.IsSupported && data.Length >= 64)
            {
                i = data.Length & ~15;
                crc = Fold(crc, data.Slice(0, i));
            }
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

        // CRC of a multiple of 16 bytes, 64 or more, by carry-less folding.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static uint Fold(uint crc, ReadOnlySpan<byte> data)
        {
            ref byte b = ref MemoryMarshal.GetReference(data);
            nuint n = (nuint)data.Length, i = 64;
            Vector128<ulong> k1k2 = Vector128.Create(0x0154442bd4ul, 0x01c6e41596ul);
            Vector128<ulong> k3k4 = Vector128.Create(0x01751997d0ul, 0x00ccaa009eul);
            Vector128<ulong> k5k0 = Vector128.Create(0x0163cd6124ul, 0ul);
            Vector128<ulong> poly = Vector128.Create(0x01db710641ul, 0x01f7011641ul);
            Vector128<ulong> low32 = Vector128.Create(~0u, 0u, ~0u, 0u).AsUInt64();

            Vector128<ulong> x1 = Vector128.LoadUnsafe(ref b).AsUInt64() ^ Vector128.CreateScalar(crc).AsUInt64();
            Vector128<ulong> x2 = Vector128.LoadUnsafe(ref b, 16).AsUInt64();
            Vector128<ulong> x3 = Vector128.LoadUnsafe(ref b, 32).AsUInt64();
            Vector128<ulong> x4 = Vector128.LoadUnsafe(ref b, 48).AsUInt64();
            for (; i + 64 <= n; i += 64)
            {
                x1 = Pclmulqdq.CarrylessMultiply(x1, k1k2, 0x11) ^ Pclmulqdq.CarrylessMultiply(x1, k1k2, 0x00) ^ Vector128.LoadUnsafe(ref b, i).AsUInt64();
                x2 = Pclmulqdq.CarrylessMultiply(x2, k1k2, 0x11) ^ Pclmulqdq.CarrylessMultiply(x2, k1k2, 0x00) ^ Vector128.LoadUnsafe(ref b, i + 16).AsUInt64();
                x3 = Pclmulqdq.CarrylessMultiply(x3, k1k2, 0x11) ^ Pclmulqdq.CarrylessMultiply(x3, k1k2, 0x00) ^ Vector128.LoadUnsafe(ref b, i + 32).AsUInt64();
                x4 = Pclmulqdq.CarrylessMultiply(x4, k1k2, 0x11) ^ Pclmulqdq.CarrylessMultiply(x4, k1k2, 0x00) ^ Vector128.LoadUnsafe(ref b, i + 48).AsUInt64();
            }

            x1 = Pclmulqdq.CarrylessMultiply(x1, k3k4, 0x11) ^ Pclmulqdq.CarrylessMultiply(x1, k3k4, 0x00) ^ x2;
            x1 = Pclmulqdq.CarrylessMultiply(x1, k3k4, 0x11) ^ Pclmulqdq.CarrylessMultiply(x1, k3k4, 0x00) ^ x3;
            x1 = Pclmulqdq.CarrylessMultiply(x1, k3k4, 0x11) ^ Pclmulqdq.CarrylessMultiply(x1, k3k4, 0x00) ^ x4;
            for (; i < n; i += 16)
                x1 = Pclmulqdq.CarrylessMultiply(x1, k3k4, 0x11) ^ Pclmulqdq.CarrylessMultiply(x1, k3k4, 0x00) ^ Vector128.LoadUnsafe(ref b, i).AsUInt64();

            x1 = Pclmulqdq.CarrylessMultiply(x1, k3k4, 0x10) ^ Sse2.ShiftRightLogical128BitLane(x1, 8);
            x1 = Pclmulqdq.CarrylessMultiply(x1 & low32, k5k0, 0x00) ^ Sse2.ShiftRightLogical128BitLane(x1, 4);
            Vector128<ulong> q = Pclmulqdq.CarrylessMultiply(x1 & low32, poly, 0x10) & low32;
            x1 ^= Pclmulqdq.CarrylessMultiply(q, poly, 0x00);
            return x1.AsUInt32().GetElement(1);
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
