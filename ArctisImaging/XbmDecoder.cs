using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Arctis.Imaging
{
    // X BitMap C source, X11 (char) or X10 (short) arrays, bits least significant first; a set bit is black.
    internal static class XbmDecoder
    {
        private struct Info
        {
            public int width, height, arrayStart;
            public bool shorts;
        }

        // character classes: 0-15 a digit's value, Separator, NotDigit
        private const byte Separator = 0x40, NotDigit = 0xFF;
        private static readonly byte[] charClass = BuildCharClass();

        internal static bool TryReadHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (!TryReadInfo(data, out Info info)) return false;
            header = new ImageHeader(ImageFormat.Xbm, info.width, info.height, 1, 1, false);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int pitch, PixelLayout layout)
        {
            if (!TryReadInfo(data, out Info info)) throw new ImageFormatException("XBM header is invalid");
            int width = info.width, unitBytes = info.shorts ? 2 : 1, rowBytes = info.shorts ? (width + 15) / 16 * 2 : (width + 7) / 8;
            byte[] bits = ArrayPool<byte>.Shared.Rent(rowBytes);
            try
            {
                int pos = info.arrayStart;
                ref byte d0 = ref MemoryMarshal.GetReference(destination);
                for (int y = 0; y < info.height; y++)
                {
                    for (int i = 0; i < rowBytes; i += unitBytes)
                    {
                        int value = ReadValue(data, ref pos);
                        bits[i] = (byte)value;
                        if (unitBytes == 2) bits[i + 1] = (byte)(value >> 8);
                    }
                    ExpandRow(bits, width, ref Unsafe.Add(ref d0, y * pitch), layout);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bits);
            }
        }

        // Writes width pixels from a row's bits, eight per source byte; each SIMD tier hands its remainder down.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static void ExpandRow(byte[] bits, int width, ref byte d, PixelLayout layout)
        {
            int whole = width >> 3, at = 0;
            bool wide = Vector512.IsHardwareAccelerated && Avx512BW.IsSupported, mid = Vector256.IsHardwareAccelerated && Avx2.IsSupported;
            // a lane whose mask bit is clear in its source byte compares equal to zero: white; a zero mask lane is always 0xFF
            switch (layout)
            {
                case PixelLayout.R8:
                {
                    Vector128<byte> mask = Vector128.Create((byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);
                    Vector128<byte> p0 = Vector128.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1), p1 = p0 + Vector128.Create((byte)2);
                    if (wide)
                    {
                        Vector256<byte> m2 = Vector256.Create(mask, mask);
                        Vector512<byte> m4 = Vector512.Create(m2, m2), pick = Vector512.Create(Vector256.Create(p0, p1), Vector256.Create(p0 + Vector128.Create((byte)4), p0 + Vector128.Create((byte)6)));
                        for (; at + 8 <= whole; at += 8)
                            Vector512.Equals(Avx512BW.Shuffle(Vector512.Create(Unsafe.ReadUnaligned<ulong>(ref bits[at])).AsByte(), pick) & m4, Vector512<byte>.Zero).StoreUnsafe(ref d, (nuint)(at * 8));
                    }
                    if (mid)
                    {
                        Vector256<byte> m2 = Vector256.Create(mask, mask), pick = Vector256.Create(p0, p1);
                        for (; at + 4 <= whole; at += 4)
                            Vector256.Equals(Avx2.Shuffle(Vector256.Create(Unsafe.ReadUnaligned<uint>(ref bits[at])).AsByte(), pick) & m2, Vector256<byte>.Zero).StoreUnsafe(ref d, (nuint)(at * 8));
                    }
                    for (; at + 2 <= whole; at += 2)
                        Vector128.Equals(Vector128.Shuffle(Vector128.Create(Unsafe.ReadUnaligned<ushort>(ref bits[at])).AsByte(), p0) & mask, Vector128<byte>.Zero).StoreUnsafe(ref d, (nuint)(at * 8));
                    break;
                }
                case PixelLayout.RG8:
                {
                    Vector128<byte> mask = Vector128.Create((byte)1, 0, 2, 0, 4, 0, 8, 0, 16, 0, 32, 0, 64, 0, 128, 0), one = Vector128.Create((byte)1);
                    if (wide)
                    {
                        Vector256<byte> m2 = Vector256.Create(mask, mask);
                        Vector512<byte> m4 = Vector512.Create(m2, m2), pick = Vector512.Create(Vector256.Create(Vector128<byte>.Zero, one), Vector256.Create(one + one, one + one + one));
                        for (; at + 4 <= whole; at += 4)
                            Vector512.Equals(Avx512BW.Shuffle(Vector512.Create(Unsafe.ReadUnaligned<uint>(ref bits[at])).AsByte(), pick) & m4, Vector512<byte>.Zero).StoreUnsafe(ref d, (nuint)(at * 16));
                    }
                    if (mid)
                    {
                        Vector256<byte> m2 = Vector256.Create(mask, mask), pick = Vector256.Create(Vector128<byte>.Zero, one);
                        for (; at + 2 <= whole; at += 2)
                            Vector256.Equals(Avx2.Shuffle(Vector256.Create(Unsafe.ReadUnaligned<ushort>(ref bits[at])).AsByte(), pick) & m2, Vector256<byte>.Zero).StoreUnsafe(ref d, (nuint)(at * 16));
                    }
                    for (; at < whole; at++)
                        Vector128.Equals(Vector128.Create(bits[at]) & mask, Vector128<byte>.Zero).StoreUnsafe(ref d, (nuint)(at * 16));
                    break;
                }
                default:
                {
                    Vector256<byte> mask = Vector256.Create((byte)1, 1, 1, 0, 2, 2, 2, 0, 4, 4, 4, 0, 8, 8, 8, 0, 16, 16, 16, 0, 32, 32, 32, 0, 64, 64, 64, 0, 128, 128, 128, 0);
                    if (wide)
                    {
                        Vector512<byte> mask2 = Vector512.Create(mask, mask), pick = Vector512.Create(Vector256<byte>.Zero, Vector256<byte>.One);
                        for (; at + 1 < whole; at += 2)
                        {
                            Vector512<byte> pair = Vector512.Create(Unsafe.ReadUnaligned<ushort>(ref bits[at])).AsByte();
                            Vector512.Equals(Avx512BW.Shuffle(pair, pick) & mask2, Vector512<byte>.Zero).StoreUnsafe(ref d, (nuint)(at * 32));
                        }
                    }
                    if (mid)
                    {
                        for (; at < whole; at++)
                            Vector256.Equals(Vector256.Create(bits[at]) & mask, Vector256<byte>.Zero).StoreUnsafe(ref d, (nuint)(at * 32));
                    }
                    else
                    {
                        Vector128<byte> low = mask.GetLower(), high = mask.GetUpper();
                        for (; at < whole; at++)
                        {
                            Vector128<byte> v = Vector128.Create(bits[at]);
                            Vector128.Equals(v & low, Vector128<byte>.Zero).StoreUnsafe(ref d, (nuint)(at * 32));
                            Vector128.Equals(v & high, Vector128<byte>.Zero).StoreUnsafe(ref d, (nuint)(at * 32 + 16));
                        }
                    }
                    break;
                }
            }
            int bpp = ImageCodec.BytesPerPixel(layout);
            for (int x = at * 8; x < width; x += 8) WriteTail(bits[x >> 3], Math.Min(8, width - x), ref Unsafe.Add(ref d, x * bpp), layout);
        }

        // Up to eight pixels from one source byte, scalar.
        private static void WriteTail(int value, int count, ref byte d, PixelLayout layout)
        {
            for (int x = 0; x < count; x++)
            {
                byte grey = (value >> x & 1) != 0 ? (byte)0 : (byte)255;
                switch (layout)
                {
                    case PixelLayout.R8:
                        Unsafe.Add(ref d, x) = grey;
                        break;
                    case PixelLayout.RG8:
                        Unsafe.Add(ref d, x * 2) = grey;
                        Unsafe.Add(ref d, x * 2 + 1) = 255;
                        break;
                    default:
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, x * 4), 0xFF000000u | grey * 0x010101u);
                        break;
                }
            }
        }

        // The _width and _height defines before the array's opening brace, then the array's element type.
        private static bool TryReadInfo(ReadOnlySpan<byte> data, out Info info)
        {
            info = default;
            int brace = data.IndexOf((byte)'{');
            if (brace < 0) return false;
            ReadOnlySpan<byte> head = data.Slice(0, brace);
            int pos = 0;
            while (pos < head.Length)
            {
                int at = head.Slice(pos).IndexOf("#define"u8);
                if (at < 0) break;
                pos += at + 7;
                int nameStart = Skip(head, pos), nameEnd = nameStart;
                while (nameEnd < head.Length && !IsSpace(head[nameEnd])) nameEnd++;
                ReadOnlySpan<byte> name = head.Slice(nameStart, nameEnd - nameStart);
                pos = Skip(head, nameEnd);
                int n = 0, digits = 0;
                for (; pos < head.Length && head[pos] >= (byte)'0' && head[pos] <= (byte)'9' && digits < 9; pos++, digits++) n = n * 10 + head[pos] - '0';
                if (name.EndsWith("_width"u8)) info.width = n;
                else if (name.EndsWith("_height"u8)) info.height = n;
            }
            if (info.width <= 0 || info.height <= 0) return false;
            int bitsName = head.LastIndexOf("_bits"u8);
            if (bitsName < 0) return false;
            info.shorts = head.Slice(0, bitsName).LastIndexOf("short"u8) > head.Slice(0, bitsName).LastIndexOf("char"u8);
            info.arrayStart = brace + 1;
            return true;
        }

        // The next hex or decimal number of the array.
        private static int ReadValue(ReadOnlySpan<byte> data, ref int pos)
        {
            byte[] classOf = charClass;
            while (pos < data.Length && classOf[data[pos]] == Separator) pos++;
            bool hex = pos + 1 < data.Length && data[pos] == (byte)'0' && (data[pos + 1] | 0x20) == (byte)'x';
            if (hex) pos += 2;
            int radix = hex ? 16 : 10, value = 0, start = pos;
            for (; pos < data.Length; pos++)
            {
                int d = classOf[data[pos]];
                if (d >= radix) break;
                value = (value * radix + d) & 0xFFFF;
            }
            if (pos == start) throw new ImageFormatException("XBM array is truncated or holds a non-number");
            return value;
        }

        private static int Skip(ReadOnlySpan<byte> data, int pos)
        {
            while (pos < data.Length && IsSpace(data[pos])) pos++;
            return pos;
        }

        private static bool IsSpace(byte b) => b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r';

        #region Tables
        private static byte[] BuildCharClass()
        {
            byte[] table = new byte[256];
            table.AsSpan().Fill(NotDigit);
            for (int c = '0'; c <= '9'; c++) table[c] = (byte)(c - '0');
            for (int c = 'a'; c <= 'f'; c++) table[c] = table[c - 32] = (byte)(c - 'a' + 10);
            table[' '] = table['\t'] = table['\n'] = table['\r'] = table[','] = Separator;
            return table;
        }
        #endregion
    }
}
