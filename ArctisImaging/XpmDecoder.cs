using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Arctis.Imaging
{
    // X PixMap 3: quoted C strings holding the values line, the colour table and the pixel rows.
    // Colours are hex or None; a named colour is not supported.
    internal static class XpmDecoder
    {
        private const int MaxCharsPerPixel = 8;

        private struct Info
        {
            public int width, height, colours, cpp, tableStart;
        }

        internal static bool TryReadHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (!TryReadInfo(data, out Info info)) return false;
            header = new ImageHeader(ImageFormat.Xpm, info.width, info.height, 4, 8, true);
            return true;
        }

        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int pitch, PixelLayout layout)
        {
            if (!TryReadInfo(data, out Info info)) throw new ImageFormatException("XPM header is invalid");
            int width = info.width, cpp = info.cpp;
            bool direct = cpp <= 2;
            uint[] table = direct ? ArrayPool<uint>.Shared.Rent(1 << (8 * cpp)) : Array.Empty<uint>();
            Dictionary<ulong, uint> keyed = direct ? null! : new Dictionary<ulong, uint>(info.colours);
            byte[] rgba = ArrayPool<byte>.Shared.Rent(width * 4);
            try
            {
                if (direct) table.AsSpan(0, 1 << (8 * cpp)).Clear();
                int pos = info.tableStart;
                for (int i = 0; i < info.colours; i++)
                {
                    ReadOnlySpan<byte> entry = NextString(data, ref pos);
                    if (entry.Length < cpp) throw new ImageFormatException("XPM colour entry is shorter than its key");
                    uint colour = ParseColour(entry.Slice(cpp));
                    if (direct) table[Key(entry, cpp)] = colour;
                    else keyed[(ulong)Key(entry, cpp) | (ulong)KeyHigh(entry, cpp) << 32] = colour;
                }
                for (int y = 0; y < info.height; y++)
                {
                    ReadOnlySpan<byte> row = NextString(data, ref pos);
                    if (row.Length < width * cpp) throw new ImageFormatException("XPM pixel row is short");
                    for (int x = 0; x < width; x++)
                    {
                        ReadOnlySpan<byte> k = row.Slice(x * cpp, cpp);
                        uint c;
                        if (direct) c = table[Key(k, cpp)];
                        else if (!keyed.TryGetValue((ulong)Key(k, cpp) | (ulong)KeyHigh(k, cpp) << 32, out c)) c = 0;
                        BinaryPrimitives.WriteUInt32LittleEndian(rgba.AsSpan(x * 4), c);
                    }
                    PixelRows.FromRgba(rgba, destination.Slice(y * pitch), width, layout);
                }
            }
            finally
            {
                if (direct) ArrayPool<uint>.Shared.Return(table);
                ArrayPool<byte>.Shared.Return(rgba);
            }
        }

        private static bool TryReadInfo(ReadOnlySpan<byte> data, out Info info)
        {
            info = default;
            int start = 0;
            while (start < data.Length && (data[start] == (byte)' ' || data[start] == (byte)'\t' || data[start] == (byte)'\r' || data[start] == (byte)'\n')) start++;
            if (!data.Slice(start).StartsWith("/* XPM */"u8)) return false;
            int pos = start + 9;
            ReadOnlySpan<byte> values;
            try
            {
                values = NextString(data, ref pos);
            }
            catch (ImageFormatException)
            {
                return false;
            }
            int p = 0;
            info.width = Number(values, ref p);
            info.height = Number(values, ref p);
            info.colours = Number(values, ref p);
            info.cpp = Number(values, ref p);
            info.tableStart = pos;
            return info.width > 0 && info.height > 0 && info.colours > 0 && info.cpp > 0 && info.cpp <= MaxCharsPerPixel;
        }

        // The contents of the next double-quoted string, skipping C comments between strings.
        private static ReadOnlySpan<byte> NextString(ReadOnlySpan<byte> data, scoped ref int pos)
        {
            while (pos < data.Length)
            {
                byte b = data[pos];
                if (b == (byte)'"')
                {
                    int end = data.Slice(pos + 1).IndexOf((byte)'"');
                    if (end < 0) break;
                    ReadOnlySpan<byte> s = data.Slice(pos + 1, end);
                    pos += end + 2;
                    return s;
                }
                if (b == (byte)'/' && pos + 1 < data.Length && data[pos + 1] == (byte)'*')
                {
                    int close = data.Slice(pos + 2).IndexOf("*/"u8);
                    if (close < 0) break;
                    pos += close + 4;
                    continue;
                }
                pos++;
            }
            throw new ImageFormatException("XPM string is missing or unterminated");
        }

        // The colour for the first of the c, g, g4, m keys present, in that order of preference.
        private static uint ParseColour(ReadOnlySpan<byte> spec)
        {
            ReadOnlySpan<byte> best = default;
            int bestRank = int.MaxValue;
            int p = 0;
            while (true)
            {
                ReadOnlySpan<byte> key = Token(spec, ref p);
                if (key.Length == 0) break;
                // a value runs to the next key, so a named colour may hold spaces
                int valueStart = p, valueEnd = p;
                while (true)
                {
                    int before = p;
                    ReadOnlySpan<byte> word = Token(spec, ref p);
                    if (word.Length == 0 || IsKey(word))
                    {
                        p = before;
                        break;
                    }
                    valueEnd = p;
                }
                int rank = key.SequenceEqual("c"u8) ? 0 : key.SequenceEqual("g"u8) ? 1 : key.SequenceEqual("g4"u8) ? 2 : key.SequenceEqual("m"u8) ? 3 : int.MaxValue;
                if (rank < bestRank)
                {
                    bestRank = rank;
                    best = spec.Slice(valueStart, valueEnd - valueStart).Trim((byte)' ');
                }
            }
            if (best.Length == 0) throw new ImageFormatException("XPM colour entry has no colour");
            if (best.Length == 4 && (best[0] | 0x20) == 'n' && (best[1] | 0x20) == 'o' && (best[2] | 0x20) == 'n' && (best[3] | 0x20) == 'e') return 0;
            if (best[0] != (byte)'#') throw new NotSupportedException($"XPM named colour \"{System.Text.Encoding.ASCII.GetString(best)}\" is not supported");
            int digits = best.Length - 1;
            if (digits % 3 != 0 || digits < 3 || digits > 12) throw new ImageFormatException("XPM hex colour has the wrong length");
            int per = digits / 3;
            uint rgba = 0xFF000000u;
            for (int ch = 0; ch < 3; ch++)
            {
                int v = 0;
                for (int d = 0; d < per; d++)
                {
                    int c = best[1 + ch * per + d] | 0x20, h = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : -1;
                    if (h < 0) throw new ImageFormatException("XPM hex colour holds a non-hex digit");
                    v = v << 4 | h;
                }
                int eight = per == 1 ? v * 17 : v >> (4 * per - 8);
                rgba |= (uint)eight << (8 * ch);
            }
            return rgba;
        }

        private static bool IsKey(ReadOnlySpan<byte> w) => w.SequenceEqual("c"u8) || w.SequenceEqual("g"u8) || w.SequenceEqual("g4"u8) || w.SequenceEqual("m"u8) || w.SequenceEqual("s"u8);

        private static ReadOnlySpan<byte> Token(ReadOnlySpan<byte> s, ref int p)
        {
            while (p < s.Length && (s[p] == (byte)' ' || s[p] == (byte)'\t')) p++;
            int start = p;
            while (p < s.Length && s[p] != (byte)' ' && s[p] != (byte)'\t') p++;
            return s.Slice(start, p - start);
        }

        private static int Number(ReadOnlySpan<byte> s, ref int p)
        {
            ReadOnlySpan<byte> t = Token(s, ref p);
            if (t.Length == 0 || t.Length > 9) return -1;
            int n = 0;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] < (byte)'0' || t[i] > (byte)'9') return -1;
                n = n * 10 + t[i] - '0';
            }
            return n;
        }

        private static uint Key(ReadOnlySpan<byte> k, int cpp)
        {
            uint v = 0;
            for (int i = 0; i < cpp && i < 4; i++) v |= (uint)k[i] << (8 * i);
            return v;
        }

        private static uint KeyHigh(ReadOnlySpan<byte> k, int cpp)
        {
            uint v = 0;
            for (int i = 4; i < cpp; i++) v |= (uint)k[i] << (8 * (i - 4));
            return v;
        }
    }
}
