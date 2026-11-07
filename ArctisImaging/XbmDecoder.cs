using System;
using System.Buffers;

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

        internal static bool TryReadHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (!TryReadInfo(data, out Info info)) return false;
            header = new ImageHeader(ImageFormat.Xbm, info.width, info.height, 1, 1, false);
            return true;
        }

        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int pitch, PixelLayout layout)
        {
            if (!TryReadInfo(data, out Info info)) throw new ImageFormatException("XBM header is invalid");
            int width = info.width, unitBits = info.shorts ? 16 : 8, units = (width + unitBits - 1) / unitBits;
            byte[] rgba = ArrayPool<byte>.Shared.Rent(width * 4);
            try
            {
                int pos = info.arrayStart;
                for (int y = 0; y < info.height; y++)
                {
                    for (int u = 0; u < units; u++)
                    {
                        int value = ReadValue(data, ref pos);
                        for (int bit = 0; bit < unitBits; bit++)
                        {
                            int x = u * unitBits + bit;
                            if (x >= width) break;
                            byte grey = (value >> bit & 1) != 0 ? (byte)0 : (byte)255;
                            rgba[x * 4] = rgba[x * 4 + 1] = rgba[x * 4 + 2] = grey;
                            rgba[x * 4 + 3] = 255;
                        }
                    }
                    PixelRows.FromRgba(rgba, destination.Slice(y * pitch), width, layout);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rgba);
            }
        }

        // The _width and _height defines, then the array's element type and its opening brace.
        private static bool TryReadInfo(ReadOnlySpan<byte> data, out Info info)
        {
            info = default;
            int pos = 0;
            while (pos < data.Length)
            {
                int at = data.Slice(pos).IndexOf("#define"u8);
                if (at < 0) break;
                pos += at + 7;
                int nameStart = Skip(data, pos), nameEnd = nameStart;
                while (nameEnd < data.Length && !IsSpace(data[nameEnd])) nameEnd++;
                ReadOnlySpan<byte> name = data.Slice(nameStart, nameEnd - nameStart);
                pos = Skip(data, nameEnd);
                int n = 0, digits = 0;
                for (; pos < data.Length && data[pos] >= (byte)'0' && data[pos] <= (byte)'9' && digits < 9; pos++, digits++) n = n * 10 + data[pos] - '0';
                if (name.EndsWith("_width"u8)) info.width = n;
                else if (name.EndsWith("_height"u8)) info.height = n;
            }
            int brace = data.IndexOf((byte)'{');
            if (info.width <= 0 || info.height <= 0 || brace < 0) return false;
            int bitsName = data.Slice(0, brace).LastIndexOf("_bits"u8);
            if (bitsName < 0) return false;
            info.shorts = data.Slice(0, bitsName).LastIndexOf("short"u8) > data.Slice(0, bitsName).LastIndexOf("char"u8);
            info.arrayStart = brace + 1;
            return true;
        }

        // The next hex or decimal number of the array.
        private static int ReadValue(ReadOnlySpan<byte> data, ref int pos)
        {
            while (pos < data.Length && (IsSpace(data[pos]) || data[pos] == (byte)',')) pos++;
            bool hex = pos + 1 < data.Length && data[pos] == (byte)'0' && (data[pos + 1] | 0x20) == (byte)'x';
            if (hex) pos += 2;
            int value = 0, digits = 0;
            for (; pos < data.Length; pos++, digits++)
            {
                int c = data[pos], d = c >= '0' && c <= '9' ? c - '0' : hex && (c | 0x20) >= 'a' && (c | 0x20) <= 'f' ? (c | 0x20) - 'a' + 10 : -1;
                if (d < 0) break;
                value = (value * (hex ? 16 : 10) + d) & 0xFFFF;
            }
            if (digits == 0) throw new ImageFormatException("XBM array is truncated or holds a non-number");
            return value;
        }

        private static int Skip(ReadOnlySpan<byte> data, int pos)
        {
            while (pos < data.Length && IsSpace(data[pos])) pos++;
            return pos;
        }

        private static bool IsSpace(byte b) => b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r';
    }
}
