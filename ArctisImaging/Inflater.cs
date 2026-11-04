using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Arctis.Imaging
{
    // zlib (RFC 1950) and DEFLATE (RFC 1951) decoding of a PNG's IDAT chunks, a window at a time.
    internal static unsafe class Inflater
    {
        internal const int MaxMatch = 258, History = 32768;
        private const int LitlenBits = 11, DistBits = 8, CodeBits = 7;
        private const uint SubF = 1u << 12, EndF = 1u << 13, Exc = 1u << 14, LitF = 1u << 31;
        private const int LitlenMode = 0, DistMode = 1, CodeMode = 2;
        private const int NeedHeader = 0, InStored = 1, InCodes = 2;

        private static ReadOnlySpan<ushort> LengthBase => new ushort[] { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
        private static ReadOnlySpan<byte> LengthExtra => new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        private static ReadOnlySpan<ushort> DistBase => new ushort[] { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
        private static ReadOnlySpan<byte> DistExtra => new byte[] { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
        private static ReadOnlySpan<byte> CodeOrder => new byte[] { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

        // decode tables; entry: LitF | value << 16 | flags | code length << 8 | bits used
        [InlineArray(2342)]
        internal struct LitlenTable { private uint e; }

        [InlineArray(402)]
        internal struct DistTable { private uint e; }

        [InlineArray(128)]
        internal struct CodeTable { private uint e; }

        internal struct Tables
        {
            public LitlenTable litlen;
            public DistTable dist;
            public CodeTable code;
        }

        // where a stream stands between fills: cursor, tables, block, stored bytes left
        internal struct State
        {
            public Bits s;
            public Tables t;
            public int block;
            public bool final, done;
            public uint storedLeft;
        }

        // the bit cursor over consecutive IDAT chunk bodies, read in place
        internal struct Bits
        {
            public byte* data;
            public int length;
            public byte* p, end;
            public ulong bits;
            public int count, overread;

            // Tops the buffer up to at least 56 bits a byte at a time, stepping into the next IDAT chunk.
            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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
                State st = default;
                Begin(ref st, data, png.Length, firstIdat);
                byte* outEnd = start + output.Length;
                if (Fill(ref st, start, start, outEnd, true) != outEnd) throw new ImageFormatException("PNG image data ends early");
                Finish(ref st);
            }
        }

        // Opens the zlib stream in the IDAT chunks starting at firstIdat; png stays pinned until Finish.
        internal static void Begin(ref State st, byte* png, int length, int firstIdat)
        {
            st.s = new Bits { data = png, length = length, end = png + firstIdat - 4 };
            if (!st.s.NextChunk()) throw new ImageFormatException("PNG has no image data");
            uint cmf = st.s.Take(8), flg = st.s.Take(8);
            if ((cmf & 15) != 8 || cmf >> 4 > 7 || (cmf << 8 | flg) % 31 != 0 || (flg & 0x20) != 0)
                throw new ImageFormatException("PNG image data has an invalid zlib header");
        }

        // Inflates from o toward outEnd: to within MaxMatch of it, or on the last window to the stream's end.
        // Matches reach back no further than start. Returns where the output stopped.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static byte* Fill(ref State st, byte* start, byte* o, byte* outEnd, bool last)
        {
            byte* limit = last ? outEnd + 1 : outEnd - MaxMatch;
            while (o < limit && !st.done)
            {
                if (st.block == NeedHeader)
                {
                    st.final = st.s.Take(1) != 0;
                    uint type = st.s.Take(2);
                    if (type == 0)
                    {
                        st.s.AlignToByte();
                        uint len = st.s.Take(16), nlen = st.s.Take(16);
                        if (len != (~nlen & 0xFFFF)) throw new ImageFormatException("PNG image data has an invalid stored block");
                        st.storedLeft = len;
                        st.block = InStored;
                    }
                    else
                    {
                        if (type == 1) BuildFixed(ref st.t);
                        else if (type == 2) ReadDynamic(ref st.s, ref st.t);
                        else throw new ImageFormatException("PNG image data has an invalid block type");
                        st.block = InCodes;
                    }
                }
                o = st.block == InStored ? Stored(ref st, o, outEnd, last) : Codes(ref st, o, start, limit, outEnd);
                if (st.block == NeedHeader && st.final) st.done = true;
            }
            return o;
        }

        // Checks the stream ended and its trailer is present; the trailer's Adler-32 is not compared.
        internal static void Finish(ref State st)
        {
            if (!st.done) throw new ImageFormatException("PNG image data ends early");
            ref Bits s = ref st.s;
            s.AlignToByte();
            s.Take(16);
            s.Take(16);
            if (s.overread * 8 > s.count) throw new ImageFormatException("PNG image data ends early");
        }

        #region blocks
        // Decodes a Huffman block until its end or until o reaches limit; a refill per symbol covers the longest match.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static byte* Codes(ref State st, byte* o, byte* start, byte* limit, byte* outEnd)
        {
            ref Bits s = ref st.s;
            fixed (uint* lit = &st.t.litlen[0])
            fixed (uint* dist = &st.t.dist[0])
            {
                ulong bits = s.bits;
                int count = s.count;
                byte* p = s.p, end = s.end;
                byte* fastLimit = outEnd - MaxMatch - 32;
                while (o < limit)
                {
                    // fast loop: no output checks, two refills of input in reach, the next entry looked up early
                    if (o < fastLimit && p + 16 <= end)
                    {
                        bits |= (BitConverter.IsLittleEndian ? *(ulong*)p : BinaryPrimitives.ReverseEndianness(*(ulong*)p)) << count;
                        p += (63 - count) >> 3;
                        count |= 56;
                        uint next = lit[bits & ((1u << LitlenBits) - 1)];
                        do
                        {
                            bits |= (BitConverter.IsLittleEndian ? *(ulong*)p : BinaryPrimitives.ReverseEndianness(*(ulong*)p)) << count;
                            p += (63 - count) >> 3;
                            count |= 56;
                            uint f = next;
                            if ((f & SubF) != 0) f = lit[(f >> 16) + (uint)((bits >> LitlenBits) & ((1u << (int)(f >> 8 & 15)) - 1))];
                            ulong saved = bits;
                            bits >>= (int)f;
                            count -= (int)(f & 31);
                            if ((int)f < 0)
                            {
                                *o++ = (byte)(f >> 16);
                                next = lit[bits & ((1u << LitlenBits) - 1)];
                                continue;
                            }
                            if ((f & Exc) != 0)
                            {
                                if ((f & EndF) == 0) throw new ImageFormatException("PNG image data has an invalid code");
                                st.block = NeedHeader;
                                break;
                            }
                            int flength = (int)(f >> 16) + (int)((saved & ((1ul << (int)(f & 31)) - 1)) >> (int)(f >> 8 & 15));

                            uint g = dist[bits & ((1u << DistBits) - 1)];
                            if ((g & SubF) != 0) g = dist[(g >> 16) + (uint)((bits >> DistBits) & ((1u << (int)(g >> 8 & 15)) - 1))];
                            saved = bits;
                            bits >>= (int)g;
                            count -= (int)(g & 31);
                            if ((g & Exc) != 0) throw new ImageFormatException("PNG image data has an invalid distance code");
                            int fdistance = (int)(g >> 16) + (int)((saved & ((1ul << (int)(g & 31)) - 1)) >> (int)(g >> 8 & 15));
                            if (fdistance > o - start) throw new ImageFormatException("PNG image data refers before its start");

                            bits |= (BitConverter.IsLittleEndian ? *(ulong*)p : BinaryPrimitives.ReverseEndianness(*(ulong*)p)) << count;
                            p += (63 - count) >> 3;
                            count |= 56;
                            next = lit[bits & ((1u << LitlenBits) - 1)];
                            byte* from = o - fdistance, to = o, stop = o + flength;
                            if (fdistance >= 16)
                            {
                                Vector128.Store(Vector128.Load(from), to);
                                Vector128.Store(Vector128.Load(from + 16), to + 16);
                                while (to + 32 < stop)
                                {
                                    to += 16;
                                    from += 16;
                                    Vector128.Store(Vector128.Load(from + 16), to + 16);
                                }
                            }
                            else if (fdistance >= 8)
                            {
                                do
                                {
                                    *(ulong*)to = *(ulong*)from;
                                    to += 8;
                                    from += 8;
                                }
                                while (to < stop);
                            }
                            else if (fdistance == 1)
                            {
                                Vector128<byte> run = Vector128.Create(*from);
                                do
                                {
                                    Vector128.Store(run, to);
                                    to += 16;
                                }
                                while (to < stop);
                            }
                            else Copy(o, fdistance, flength, outEnd);
                            o += flength;
                        }
                        while (o < fastLimit && p + 16 <= end);
                        if (st.block == NeedHeader) break;
                        continue;
                    }

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
                    if ((e & SubF) != 0) e = lit[(e >> 16) + (uint)((bits >> LitlenBits) & ((1u << (int)(e >> 8 & 15)) - 1))];
                    ulong keep = bits;
                    bits >>= (int)e;
                    count -= (int)(e & 31);
                    if ((int)e < 0)
                    {
                        if (o == outEnd) throw new ImageFormatException("PNG image data is longer than the image");
                        *o++ = (byte)(e >> 16);
                        continue;
                    }
                    if ((e & Exc) != 0)
                    {
                        if ((e & EndF) == 0) throw new ImageFormatException("PNG image data has an invalid code");
                        st.block = NeedHeader;
                        break;
                    }
                    int length = (int)(e >> 16) + (int)((keep & ((1ul << (int)(e & 31)) - 1)) >> (int)(e >> 8 & 15));

                    uint d = dist[bits & ((1u << DistBits) - 1)];
                    if ((d & SubF) != 0) d = dist[(d >> 16) + (uint)((bits >> DistBits) & ((1u << (int)(d >> 8 & 15)) - 1))];
                    keep = bits;
                    bits >>= (int)d;
                    count -= (int)(d & 31);
                    if ((d & Exc) != 0) throw new ImageFormatException("PNG image data has an invalid distance code");
                    int distance = (int)(d >> 16) + (int)((keep & ((1ul << (int)(d & 31)) - 1)) >> (int)(d >> 8 & 15));

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
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

        // Copies what of a stored block fits before outEnd; the rest waits for the next window.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static byte* Stored(ref State st, byte* o, byte* outEnd, bool last)
        {
            ref Bits s = ref st.s;
            uint len = st.storedLeft;
            if (last && len > outEnd - o) throw new ImageFormatException("PNG image data is longer than the image");
            while (len > 0 && s.count >= 8 && o < outEnd)
            {
                *o++ = (byte)s.bits;
                s.bits >>= 8;
                s.count -= 8;
                len--;
            }

            if (s.count == 0) s.bits = 0;
            while (len > 0 && o < outEnd)
            {
                if (s.p == s.end && !s.NextChunk()) throw new ImageFormatException("PNG image data ends early");
                int n = (int)Math.Min(Math.Min(len, (uint)(s.end - s.p)), outEnd - o);
                Buffer.MemoryCopy(s.p, o, n, n);
                s.p += n;
                o += n;
                len -= (uint)n;
            }
            st.storedLeft = len;
            if (len == 0) st.block = NeedHeader;
            return o;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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
                    int used = (int)(e & 31);
                    if ((e & Exc) != 0) throw new ImageFormatException("PNG image data has an invalid code length code");
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
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

            for (int i = 0; i < 1 << root; i++) table[i] = Exc;
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
                        for (int i = subStart; i < next; i++) table[i] = Exc;
                        table[reversed & ((1 << root) - 1)] = (uint)subStart << 16 | Exc | SubF | (uint)subBits << 8;
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
            if (mode == CodeMode) return (uint)symbol << 16 | l << 8 | l;
            if (mode == DistMode)
                return symbol < 30 ? (uint)DistBase[symbol] << 16 | l << 8 | (l + DistExtra[symbol]) : Exc | l;
            if (symbol < 256) return LitF | (uint)symbol << 16 | l << 8 | l;
            if (symbol == 256) return Exc | EndF | l << 8 | l;
            if (symbol < 286) return (uint)LengthBase[symbol - 257] << 16 | l << 8 | (l + LengthExtra[symbol - 257]);
            return Exc | l;
        }

        private static int Reverse(int code, int len)
        {
            int r = 0;
            for (int i = 0; i < len; i++, code >>= 1) r = r << 1 | (code & 1);
            return r;
        }
        #endregion
    }
}
