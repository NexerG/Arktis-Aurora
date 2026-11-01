using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Arctis.Imaging
{
    // zlib (RFC 1950) and DEFLATE (RFC 1951) decoding of a PNG's IDAT chunks into one whole buffer.
    internal static unsafe class Inflater
    {
        private const int LitlenBits = 11, DistBits = 8, CodeBits = 7;
        private const uint Literal = 0, Length = 1, End = 2, Sub = 3, Invalid = 4, Distance = 5;
        private const int LitlenMode = 0, DistMode = 1, CodeMode = 2;

        private static ReadOnlySpan<ushort> LengthBase => new ushort[] { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
        private static ReadOnlySpan<byte> LengthExtra => new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        private static ReadOnlySpan<ushort> DistBase => new ushort[] { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
        private static ReadOnlySpan<byte> DistExtra => new byte[] { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
        private static ReadOnlySpan<byte> CodeOrder => new byte[] { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

        // decode tables; an entry is value << 16 | kind << 8 | extra bits << 4 | code length
        [InlineArray(2342)]
        private struct LitlenTable { private uint e; }

        [InlineArray(402)]
        private struct DistTable { private uint e; }

        [InlineArray(128)]
        private struct CodeTable { private uint e; }

        private struct Tables
        {
            public LitlenTable litlen;
            public DistTable dist;
            public CodeTable code;
        }

        // the bit cursor over consecutive IDAT chunk bodies, read in place
        private struct Bits
        {
            public byte* data;
            public int length;
            public byte* p, end;
            public ulong bits;
            public int count, overread;

            // Tops the buffer up to at least 56 bits a byte at a time, stepping into the next IDAT chunk.
            public void Refill()
            {
                while (count <= 55)
                {
                    ulong b;
                    if (p < end) b = *p++;
                    else if (NextChunk()) continue;
                    else
                    {
                        if (++overread > 8) throw new ImageFormatException("PNG image data ends early");
                        b = 0;
                    }
                    bits |= b << count;
                    count += 8;
                }
            }

            public bool NextChunk()
            {
                long header = end - data + 4;
                if (header + 8 > length || BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(data + header + 4, 4)) != 0x49444154) return false;
                int size = BinaryPrimitives.ReadInt32BigEndian(new ReadOnlySpan<byte>(data + header, 4));
                if (size < 0 || header + 8 + (long)size > length) return false;
                p = data + header + 8;
                end = p + size;
                return true;
            }

            public uint Take(int n)
            {
                if (count < n) Refill();
                uint v = (uint)(bits & ((1ul << n) - 1));
                bits >>= n;
                count -= n;
                return v;
            }

            public void AlignToByte()
            {
                int drop = count & 7;
                bits >>= drop;
                count -= drop;
            }
        }

        // Inflates the zlib stream in the IDAT chunks starting at firstIdat; fills output exactly or throws.
        internal static void Inflate(ReadOnlySpan<byte> png, int firstIdat, Span<byte> output)
        {
            fixed (byte* data = png)
            fixed (byte* start = output)
            {
                Bits s = new Bits { data = data, length = png.Length, end = data + firstIdat - 4 };
                if (!s.NextChunk()) throw new ImageFormatException("PNG has no image data");
                uint cmf = s.Take(8), flg = s.Take(8);
                if ((cmf & 15) != 8 || cmf >> 4 > 7 || (cmf << 8 | flg) % 31 != 0 || (flg & 0x20) != 0)
                    throw new ImageFormatException("PNG image data has an invalid zlib header");

                Tables t = default;
                byte* o = start, outEnd = start + output.Length;
                uint final;
                do
                {
                    final = s.Take(1);
                    uint type = s.Take(2);
                    if (type == 0) o = Stored(ref s, o, outEnd);
                    else
                    {
                        if (type == 1) BuildFixed(ref t);
                        else if (type == 2) ReadDynamic(ref s, ref t);
                        else throw new ImageFormatException("PNG image data has an invalid block type");
                        o = Codes(ref s, ref t, o, start, outEnd);
                    }
                }
                while (final == 0);
                if (o != outEnd) throw new ImageFormatException("PNG image data ends early");

                s.AlignToByte();
                uint stored = s.Take(8) << 24 | s.Take(8) << 16 | s.Take(8) << 8 | s.Take(8);
                if (s.overread * 8 > s.count) throw new ImageFormatException("PNG image data ends early");
                if (Adler32(start, output.Length) != stored) throw new ImageFormatException("PNG image data checksum does not match");
            }
        }

        #region blocks
        // Decodes one Huffman-coded block; one refill per symbol covers the longest match (48 bits).
        private static byte* Codes(ref Bits s, ref Tables t, byte* o, byte* start, byte* outEnd)
        {
            fixed (uint* lit = &t.litlen[0])
            fixed (uint* dist = &t.dist[0])
            {
                ulong bits = s.bits;
                int count = s.count;
                byte* p = s.p, end = s.end;
                while (true)
                {
                    if (p + 8 <= end)
                    {
                        ulong word = *(ulong*)p;
                        bits |= (BitConverter.IsLittleEndian ? word : BinaryPrimitives.ReverseEndianness(word)) << count;
                        p += (63 - count) >> 3;
                        count |= 56;
                    }
                    else
                    {
                        s.bits = bits;
                        s.count = count;
                        s.p = p;
                        s.Refill();
                        bits = s.bits;
                        count = s.count;
                        p = s.p;
                        end = s.end;
                    }

                    uint e = lit[bits & ((1u << LitlenBits) - 1)];
                    if ((e >> 8 & 0xFF) == Sub) e = lit[(e >> 16) + (uint)((bits >> LitlenBits) & ((1u << (int)(e >> 4 & 15)) - 1))];
                    int used = (int)(e & 15);
                    bits >>= used;
                    count -= used;
                    uint kind = e >> 8 & 0xFF;
                    if (kind == Literal)
                    {
                        if (o == outEnd) throw new ImageFormatException("PNG image data is longer than the image");
                        *o++ = (byte)(e >> 16);
                        continue;
                    }
                    if (kind == End) break;
                    if (kind != Length) throw new ImageFormatException("PNG image data has an invalid code");

                    int extra = (int)(e >> 4 & 15);
                    int length = (int)(e >> 16) + (int)(bits & ((1ul << extra) - 1));
                    bits >>= extra;
                    count -= extra;

                    uint d = dist[bits & ((1u << DistBits) - 1)];
                    if ((d >> 8 & 0xFF) == Sub) d = dist[(d >> 16) + (uint)((bits >> DistBits) & ((1u << (int)(d >> 4 & 15)) - 1))];
                    used = (int)(d & 15);
                    bits >>= used;
                    count -= used;
                    if ((d >> 8 & 0xFF) != Distance) throw new ImageFormatException("PNG image data has an invalid distance code");
                    extra = (int)(d >> 4 & 15);
                    int distance = (int)(d >> 16) + (int)(bits & ((1ul << extra) - 1));
                    bits >>= extra;
                    count -= extra;

                    if (distance > o - start) throw new ImageFormatException("PNG image data refers before its start");
                    if (length > outEnd - o) throw new ImageFormatException("PNG image data is longer than the image");
                    Copy(o, distance, length, outEnd);
                    o += length;
                }
                s.bits = bits;
                s.count = count;
                s.p = p;
                s.end = end;
            }
            return o;
        }

        // Copies a match: eight bytes at a time when the source is at least eight back and there is room to overshoot.
        private static void Copy(byte* o, int distance, int length, byte* outEnd)
        {
            byte* src = o - distance;
            if (distance >= 8 && o + length + 8 <= outEnd)
            {
                byte* stop = o + length;
                do
                {
                    *(ulong*)o = *(ulong*)src;
                    o += 8;
                    src += 8;
                }
                while (o < stop);
            }
            else if (distance == 1) new Span<byte>(o, length).Fill(*src);
            else
                for (int i = 0; i < length; i++) o[i] = src[i];
        }

        private static byte* Stored(ref Bits s, byte* o, byte* outEnd)
        {
            s.AlignToByte();
            uint len = s.Take(16), nlen = s.Take(16);
            if (len != (~nlen & 0xFFFF)) throw new ImageFormatException("PNG image data has an invalid stored block");
            if (len > outEnd - o) throw new ImageFormatException("PNG image data is longer than the image");
            while (len > 0 && s.count >= 8)
            {
                *o++ = (byte)s.bits;
                s.bits >>= 8;
                s.count -= 8;
                len--;
            }
            if (len == 0) return o;

            s.bits = 0;
            while (len > 0)
            {
                if (s.p == s.end && !s.NextChunk()) throw new ImageFormatException("PNG image data ends early");
                int n = (int)Math.Min(len, (uint)(s.end - s.p));
                Buffer.MemoryCopy(s.p, o, n, n);
                s.p += n;
                o += n;
                len -= (uint)n;
            }
            return o;
        }

        private static void ReadDynamic(ref Bits s, ref Tables t)
        {
            int hlit = (int)s.Take(5) + 257, hdist = (int)s.Take(5) + 1, hclen = (int)s.Take(4) + 4;
            if (hlit > 286 || hdist > 30) throw new ImageFormatException("PNG image data has an invalid block header");

            Span<byte> lens = stackalloc byte[320];
            lens.Clear();
            ReadOnlySpan<byte> order = CodeOrder;
            for (int i = 0; i < hclen; i++) lens[order[i]] = (byte)s.Take(3);

            int total = hlit + hdist;
            fixed (uint* code = &t.code[0])
            {
                Build(lens.Slice(0, 19), code, 128, CodeBits, CodeMode);
                lens.Slice(0, 19).Clear();
                for (int i = 0; i < total;)
                {
                    if (s.count < 14) s.Refill();
                    uint e = code[s.bits & ((1u << CodeBits) - 1)];
                    int used = (int)(e & 15);
                    if ((e >> 8 & 0xFF) == Invalid) throw new ImageFormatException("PNG image data has an invalid code length code");
                    s.bits >>= used;
                    s.count -= used;
                    int symbol = (int)(e >> 16);
                    if (symbol < 16)
                    {
                        lens[i++] = (byte)symbol;
                        continue;
                    }
                    int repeat;
                    byte value = 0;
                    if (symbol == 16)
                    {
                        if (i == 0) throw new ImageFormatException("PNG image data repeats a code length before the first");
                        value = lens[i - 1];
                        repeat = 3 + (int)s.Take(2);
                    }
                    else if (symbol == 17) repeat = 3 + (int)s.Take(3);
                    else repeat = 11 + (int)s.Take(7);
                    if (i + repeat > total) throw new ImageFormatException("PNG image data has too many code lengths");
                    lens.Slice(i, repeat).Fill(value);
                    i += repeat;
                }
            }
            if (lens[256] == 0) throw new ImageFormatException("PNG image data has no end-of-block code");
            fixed (uint* lit = &t.litlen[0])
            fixed (uint* dist = &t.dist[0])
            {
                Build(lens.Slice(0, hlit), lit, 2342, LitlenBits, LitlenMode);
                Build(lens.Slice(hlit, hdist), dist, 402, DistBits, DistMode);
            }
        }

        private static void BuildFixed(ref Tables t)
        {
            Span<byte> lens = stackalloc byte[288];
            lens.Slice(0, 144).Fill(8);
            lens.Slice(144, 112).Fill(9);
            lens.Slice(256, 24).Fill(7);
            lens.Slice(280, 8).Fill(8);
            fixed (uint* lit = &t.litlen[0])
            fixed (uint* dist = &t.dist[0])
            {
                Build(lens, lit, 2342, LitlenBits, LitlenMode);
                lens.Slice(0, 32).Fill(5);
                Build(lens.Slice(0, 32), dist, 402, DistBits, DistMode);
            }
        }
        #endregion

        #region tables
        // Builds a canonical Huffman decode table: a primary table of root bits, sub-tables sized as zlib sizes them.
        private static void Build(ReadOnlySpan<byte> lens, uint* table, int capacity, int root, int mode)
        {
            Span<int> remaining = stackalloc int[16];
            remaining.Clear();
            for (int i = 0; i < lens.Length; i++) remaining[lens[i]]++;
            remaining[0] = 0;

            int codes = 0, left = 1;
            for (int len = 1; len <= 15; len++)
            {
                left = (left << 1) - remaining[len];
                if (left < 0) throw new ImageFormatException("PNG image data has an over-subscribed code");
                codes += remaining[len];
            }
            if (left > 0 && codes > 1) throw new ImageFormatException("PNG image data has an incomplete code");

            for (int i = 0; i < 1 << root; i++) table[i] = Invalid << 8;
            if (codes == 0) return;

            Span<int> offset = stackalloc int[16];
            offset[1] = 0;
            for (int len = 1; len < 15; len++) offset[len + 1] = offset[len] + remaining[len];
            Span<ushort> sorted = stackalloc ushort[288];
            for (int i = 0; i < lens.Length; i++)
                if (lens[i] != 0) sorted[offset[lens[i]]++] = (ushort)i;

            int code = 0, current = 0, next = 1 << root, subStart = 0, subBits = 0;
            int prefix = -1;
            for (int k = 0; k < codes; k++)
            {
                int symbol = sorted[k], len = lens[symbol];
                if (len > current)
                {
                    code <<= len - current;
                    current = len;
                }
                uint leaf = Leaf(mode, symbol, len);
                int reversed = Reverse(code, len);
                if (len <= root)
                    for (int i = reversed; i < 1 << root; i += 1 << len) table[i] = leaf;
                else
                {
                    int top = code >> (len - root);
                    if (top != prefix)
                    {
                        prefix = top;
                        subBits = len - root;
                        int room = 1 << subBits;
                        while (subBits + root < 15)
                        {
                            room -= remaining[subBits + root];
                            if (room <= 0) break;
                            subBits++;
                            room <<= 1;
                        }
                        subStart = next;
                        next += 1 << subBits;
                        if (next > capacity) throw new ImageFormatException("PNG image data has an oversized code");
                        for (int i = subStart; i < next; i++) table[i] = Invalid << 8;
                        table[reversed & ((1 << root) - 1)] = (uint)subStart << 16 | Sub << 8 | (uint)subBits << 4 | (uint)root;
                    }
                    for (int i = reversed >> root; i < 1 << subBits; i += 1 << (len - root)) table[subStart + i] = leaf;
                }
                remaining[len]--;
                code++;
            }
        }

        private static uint Leaf(int mode, int symbol, int len)
        {
            uint l = (uint)len;
            if (mode == CodeMode) return (uint)symbol << 16 | Literal << 8 | l;
            if (mode == DistMode)
                return symbol < 30 ? (uint)DistBase[symbol] << 16 | Distance << 8 | (uint)DistExtra[symbol] << 4 | l : Invalid << 8 | l;
            if (symbol < 256) return (uint)symbol << 16 | Literal << 8 | l;
            if (symbol == 256) return End << 8 | l;
            if (symbol < 286) return (uint)LengthBase[symbol - 257] << 16 | Length << 8 | (uint)LengthExtra[symbol - 257] << 4 | l;
            return Invalid << 8 | l;
        }

        private static int Reverse(int code, int len)
        {
            int r = 0;
            for (int i = 0; i < len; i++, code >>= 1) r = r << 1 | (code & 1);
            return r;
        }
        #endregion

        // Adler-32 in runs of up to 5552 bytes, sixteen bytes per step: byte sums, sums of sums, and position-weighted sums.
        private static uint Adler32(byte* p, int n)
        {
            ulong a = 1, b = 0;
            while (n > 0)
            {
                int run = Math.Min(n, 5552);
                int i = 0;
                if (Vector128.IsHardwareAccelerated && run >= 16)
                {
                    Vector128<uint> s1 = Vector128<uint>.Zero, s1s = Vector128<uint>.Zero, s2 = Vector128<uint>.Zero;
                    Vector128<ushort> wLow = Vector128.Create((ushort)16, 15, 14, 13, 12, 11, 10, 9);
                    Vector128<ushort> wHigh = Vector128.Create((ushort)8, 7, 6, 5, 4, 3, 2, 1);
                    int blocks = run >> 4;
                    for (int k = 0; k < blocks; k++, i += 16)
                    {
                        s1s += s1;
                        (Vector128<ushort> lo, Vector128<ushort> hi) = Vector128.Widen(Vector128.Load(p + i));
                        (Vector128<uint> l0, Vector128<uint> l1) = Vector128.Widen(lo + hi);
                        s1 += l0 + l1;
                        (Vector128<uint> w0, Vector128<uint> w1) = Vector128.Widen(lo * wLow + hi * wHigh);
                        s2 += w0 + w1;
                    }
                    b += 16ul * (ulong)blocks * a + 16ul * Vector128.Sum(Vector128.WidenLower(s1s)) + 16ul * Vector128.Sum(Vector128.WidenUpper(s1s)) + Vector128.Sum(s2);
                    a += Vector128.Sum(s1);
                }
                for (; i < run; i++)
                {
                    a += p[i];
                    b += a;
                }
                a %= 65521;
                b %= 65521;
                p += run;
                n -= run;
            }
            return (uint)(b << 16 | a);
        }
    }
}
