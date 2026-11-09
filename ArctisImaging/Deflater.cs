using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Arctis.Imaging
{
    /// <summary>zlib (RFC 1950) and DEFLATE (RFC 1951) compression; zlib's deflate_slow over 4-byte hash chains and a 3-byte slot.</summary>
    public static unsafe class Deflater
    {
        private const int MinMatch = 3, MaxMatch = 258, WSize = 32768, WMask = WSize - 1, TooFar = 4096;
        private const int MaxDist = WSize - (MaxMatch + MinMatch + 1), HashSize = 1 << 15;
        private const int BlockSymbols = 16383, MaxStored = 65535, EndOfBlock = 256, MinRunFill = 16;
        private const int LitlenCodes = 286, DistCodes = 30, LengthCodes = 19;

        // work layout, in ints
        private const int LitFreq = 0, DistFreq = 288, ClFreq = 320, LitLens = 340, DistLens = 628, ClLens = 660;
        private const int LitCodes = 680, DistCodesAt = 968, ClCodes = 1000, Rle = 1020, Scratch = 1340, SymTable = 1916, DistTable = 2428, WorkSize = 2460;

        // length - 3 to its code index; distance - 1 below 256, then 256 + ((distance - 1) >> 7), to its code
        private static readonly byte[] lengthCodeOf = LengthCodeTable(), distCodeOf = DistCodeTable();

        private static ReadOnlySpan<byte> CodeOrder => new byte[] { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

        /// <summary>Writes input as one zlib stream.</summary>
        /// <exception cref="ArgumentException">A setting is out of range.</exception>
        public static void Deflate(ReadOnlySpan<byte> input, Stream output, in DeflateSettings settings = default)
        {
            settings.Check();
            int[] chains = ArrayPool<int>.Shared.Rent(2 * HashSize + WSize);
            ushort[] symbols = ArrayPool<ushort>.Shared.Rent(2 * BlockSymbols);
            int[] work = ArrayPool<int>.Shared.Rent(WorkSize);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
            try
            {
                fixed (byte* data = input)
                fixed (int* c = chains)
                fixed (ushort* s = symbols)
                fixed (int* w = work)
                fixed (byte* b = buffer)
                {
                    Encoder e = new Encoder(data, input.Length, c, s, w, new BitWriter(buffer, b, output), settings);
                    e.bits.Put(0x9C78, 16);
                    e.Run();
                    e.bits.AlignToByte();
                    e.bits.Put(BinaryPrimitives.ReverseEndianness(Adler32(input)), 32);
                    e.bits.Drain();
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(chains);
                ArrayPool<ushort>.Shared.Return(symbols);
                ArrayPool<int>.Shared.Return(work);
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private struct Encoder
        {
            public BitWriter bits;

            // input and match search
            private readonly byte* data;
            private readonly int n, hashShift;
            private readonly int* head, head3, prev;
            private readonly int goodLength, maxLazy, niceLength, maxChain;

            // the block being gathered: symbols, frequencies, bytes covered
            private readonly ushort* symDist, symLen;
            private readonly int* work;
            private int count, blockStart, blockBytes;

            public Encoder(byte* data, int n, int* chains, ushort* symbols, int* work, in BitWriter bits, in DeflateSettings settings)
            {
                this.data = data;
                this.n = n;
                head = chains;
                head3 = chains + HashSize;
                prev = chains + 2 * HashSize;
                symDist = symbols;
                symLen = symbols + BlockSymbols;
                this.work = work;
                this.bits = bits;
                goodLength = settings.GoodLength;
                maxLazy = settings.MaxLazy;
                niceLength = settings.NiceLength;
                maxChain = settings.MaxChain;
                count = 0;
                blockStart = 0;
                blockBytes = 0;
                int hashBits = Math.Clamp(BitOperations.Log2((uint)n) + 1, 8, 15);
                hashShift = 32 - hashBits;
                new Span<int>(head, 1 << hashBits).Clear();
                new Span<int>(head3, 1 << hashBits).Clear();
                new Span<int>(work, ClFreq).Clear();
            }

            // zlib's deflate_slow: a match is taken only if the next position has no longer one.
            public void Run()
            {
                int strstart = 0, matchLength = MinMatch - 1, matchStart = 0;
                bool matchAvailable = false;
                byte* window = data;
                int length = n, lazy = maxLazy, maxInsert = n - MinMatch, longMatch = 16 * maxLazy;
                while (strstart < length)
                {
                    int hashHead = 0, near = 0;
                    if (strstart <= maxInsert) hashHead = Insert(strstart, out near);
                    int prevLength = matchLength, prevMatch = matchStart;
                    matchLength = MinMatch - 1;
                    if (prevLength < lazy && (hashHead != 0 && strstart - hashHead <= MaxDist || near != 0 && strstart - near <= TooFar))
                    {
                        matchLength = LongestMatch(strstart, hashHead, near, prevLength, ref matchStart);
                        if (matchLength == MinMatch && strstart - matchStart > TooFar) matchLength = MinMatch - 1;
                    }

                    if (prevLength >= MinMatch && matchLength <= prevLength)
                    {
                        int distance = strstart - 1 - prevMatch, last = strstart + prevLength - 2;
                        bool full = TallyMatch(distance, prevLength);
                        if (prevLength > longMatch) strstart = last - 1;
                        else if (distance == 1 && prevLength >= MinRunFill)
                        {
                            InsertRun(strstart + 1, last - 3);
                            strstart = last - 3;
                        }
                        int insertEnd = last < maxInsert ? last : maxInsert;
                        while (strstart < insertEnd) Insert(++strstart, out _);
                        strstart = last;
                        matchAvailable = false;
                        matchLength = MinMatch - 1;
                        strstart++;
                        if (full) WriteBlock(false);
                    }
                    else if (matchAvailable)
                    {
                        if (TallyLiteral(window[strstart - 1])) WriteBlock(false);
                        strstart++;
                    }
                    else
                    {
                        matchAvailable = true;
                        strstart++;
                    }
                }
                if (matchAvailable) TallyLiteral(window[strstart - 1]);
                WriteBlock(true);
            }

            // Links pos into its 4-byte chain and the 3-byte slot; near = the slot's previous position.
            private readonly int Insert(int pos, out int near)
            {
                uint v = Bytes(pos);
                int h = (int)(v * 2654435761u >> hashShift), h3 = (int)((v & 0xFFFFFF) * 2654435761u >> hashShift);
                int match = head[h];
                near = head3[h3];
                prev[pos & WMask] = match;
                head[h] = pos;
                head3[h3] = pos;
                return match;
            }

            // Inserts from..to inside a run of one byte value, whose bucket's head is from - 1: each links to the one before.
            private readonly void InsertRun(int from, int to)
            {
                uint v = Bytes(from);
                head[(int)(v * 2654435761u >> hashShift)] = to;
                head3[(int)((v & 0xFFFFFF) * 2654435761u >> hashShift)] = to;
                int at = from & WMask, count = to - from + 1, first = WSize - at < count ? WSize - at : count;
                Ascending(prev + at, from - 1, first);
                if (first < count) Ascending(prev, from - 1 + first, count - first);
            }

            // 4 bytes at pos; 3 at the last position
            private readonly uint Bytes(int pos)
            {
                byte* p = data + pos;
                return pos + 3 < n ? Unsafe.ReadUnaligned<uint>(p) : (uint)(p[0] | p[1] << 8 | p[2] << 16);
            }

            // zlib's longest_match over the 4-byte chain, after the 3-byte slot's candidate near.
            private readonly int LongestMatch(int strstart, int cur, int near, int prevLength, ref int matchStart)
            {
                int lookahead = n - strstart;
                int maxLen = lookahead < MaxMatch ? lookahead : MaxMatch;
                int bestLen = prevLength;
                if (bestLen >= maxLen) return bestLen;
                int chain = prevLength >= goodLength ? maxChain >> 2 : maxChain;
                int nice = niceLength < lookahead ? niceLength : lookahead;
                int limit = strstart > MaxDist ? strstart - MaxDist : 0;
                byte* window = data;
                int* links = prev;
                byte* scan = window + strstart;
                ushort scanStart = *(ushort*)scan, scanEnd = *(ushort*)(scan + bestLen - 1);
                if (bestLen < MinMatch && near != 0 && strstart - near <= TooFar)
                {
                    byte* m3 = window + near;
                    if (*(ushort*)m3 == scanStart && m3[2] == scan[2])
                    {
                        int len = 3 + Common(scan + 3, m3 + 3, maxLen - 3);
                        matchStart = near;
                        bestLen = len;
                        if (len >= nice) return bestLen;
                        scanEnd = *(ushort*)(scan + bestLen - 1);
                    }
                }
                if (cur == 0 || strstart - cur > MaxDist) return bestLen;
                do
                {
                    byte* match = window + cur;
                    if (*(ushort*)(match + bestLen - 1) != scanEnd || *(ushort*)match != scanStart) continue;
                    int len = 2 + Common(scan + 2, match + 2, maxLen - 2);
                    if (len > bestLen)
                    {
                        matchStart = cur;
                        bestLen = len;
                        if (len >= nice) break;
                        scanEnd = *(ushort*)(scan + bestLen - 1);
                    }
                } while ((cur = links[cur & WMask]) > limit && --chain != 0);
                return bestLen;
            }

            private bool TallyLiteral(byte c)
            {
                symDist[count] = 0;
                symLen[count] = c;
                work[LitFreq + c]++;
                count++;
                blockBytes++;
                return count == BlockSymbols;
            }

            private bool TallyMatch(int distance, int length)
            {
                symDist[count] = (ushort)distance;
                symLen[count] = (ushort)(length - MinMatch);
                int d = distance - 1;
                work[LitFreq + 257 + lengthCodeOf[length - MinMatch]]++;
                work[DistFreq + distCodeOf[d < 256 ? d : 256 + (d >> 7)]]++;
                count++;
                blockBytes += length;
                return count == BlockSymbols;
            }

            // Writes the gathered block as dynamic, fixed or stored, whichever is smallest, and starts the next.
            private void WriteBlock(bool last)
            {
                int* w = work;
                w[LitFreq + EndOfBlock] = 1;
                BuildLengths(w + LitFreq, LitlenCodes, w + LitLens, 15, w + Scratch);
                BuildLengths(w + DistFreq, DistCodes, w + DistLens, 15, w + Scratch);
                int hlit = LitlenCodes;
                while (hlit > 257 && w[LitLens + hlit - 1] == 0) hlit--;
                int hdist = DistCodes;
                while (hdist > 1 && w[DistLens + hdist - 1] == 0) hdist--;

                new Span<int>(w + ClFreq, LengthCodes).Clear();
                int runs = RunLengths(w + LitLens, hlit, w + Rle, 0, w + ClFreq);
                runs = RunLengths(w + DistLens, hdist, w + Rle, runs, w + ClFreq);
                BuildLengths(w + ClFreq, LengthCodes, w + ClLens, 7, w + Scratch);
                int hclen = LengthCodes;
                while (hclen > 4 && w[ClLens + CodeOrder[hclen - 1]] == 0) hclen--;

                long extra = 0, dynamic = 17 + 3L * hclen + 2L * w[ClFreq + 16] + 3L * w[ClFreq + 17] + 7L * w[ClFreq + 18], fixedBits = 3;
                for (int i = 0; i < LengthCodes; i++) dynamic += (long)w[ClFreq + i] * w[ClLens + i];
                for (int i = 0; i < LitlenCodes; i++)
                {
                    dynamic += (long)w[LitFreq + i] * w[LitLens + i];
                    fixedBits += (long)w[LitFreq + i] * FixedLength(i);
                }
                for (int i = 0; i < 29; i++) extra += (long)w[LitFreq + 257 + i] * LengthExtra(i);
                for (int i = 0; i < DistCodes; i++)
                {
                    dynamic += (long)w[DistFreq + i] * w[DistLens + i];
                    fixedBits += 5L * w[DistFreq + i];
                    extra += (long)w[DistFreq + i] * DistExtra(i);
                }
                long dynamicBytes = (dynamic + extra + 7) >> 3, fixedBytes = (fixedBits + extra + 7) >> 3;
                if (fixedBytes <= dynamicBytes) dynamicBytes = fixedBytes;

                uint final = last ? 1u : 0u;
                if (blockBytes <= MaxStored && blockBytes + 4 <= dynamicBytes)
                {
                    bits.Put(final, 3);
                    bits.AlignToByte();
                    bits.Put((uint)blockBytes | (uint)(~blockBytes & 0xFFFF) << 16, 32);
                    bits.PutBytes(new ReadOnlySpan<byte>(data + blockStart, blockBytes));
                }
                else if (fixedBytes == dynamicBytes)
                {
                    bits.Put(final | 2, 3);
                    for (int i = 0; i < 288; i++) w[LitLens + i] = FixedLength(i);
                    for (int i = 0; i < 32; i++) w[DistLens + i] = 5;
                    Canonical(w + LitLens, 288, w + LitCodes);
                    Canonical(w + DistLens, 32, w + DistCodesAt);
                    WriteSymbols();
                }
                else
                {
                    bits.Put(final | 4, 3);
                    bits.Put((uint)(hlit - 257), 5);
                    bits.Put((uint)(hdist - 1), 5);
                    bits.Put((uint)(hclen - 4), 4);
                    for (int i = 0; i < hclen; i++) bits.Put((uint)w[ClLens + CodeOrder[i]], 3);
                    Canonical(w + ClLens, LengthCodes, w + ClCodes);
                    for (int i = 0; i < runs; i++)
                    {
                        int run = w[Rle + i], sym = run & 31;
                        bits.Put((uint)w[ClCodes + sym], w[ClLens + sym]);
                        if (sym == 16) bits.Put((uint)(run >> 5), 2);
                        else if (sym == 17) bits.Put((uint)(run >> 5), 3);
                        else if (sym == 18) bits.Put((uint)(run >> 5), 7);
                    }
                    Canonical(w + LitLens, LitlenCodes, w + LitCodes);
                    Canonical(w + DistLens, DistCodes, w + DistCodesAt);
                    WriteSymbols();
                }

                count = 0;
                blockStart += blockBytes;
                blockBytes = 0;
                new Span<int>(w, ClFreq).Clear();
            }

            private void WriteSymbols()
            {
                int* lc = work + LitCodes, ll = work + LitLens, dc = work + DistCodesAt, dl = work + DistLens;
                uint* table = (uint*)(work + SymTable), dist = (uint*)(work + DistTable);
                fixed (byte* lengthCodes = lengthCodeOf, distanceCodes = distCodeOf)
                {
                    // literal c at c, length - 3 at 256 + value with its extra bits: code | bits << 24
                    for (int c = 0; c < 256; c++) table[c] = (uint)lc[c] | (uint)ll[c] << 24;
                    for (int v = 0; v < 256; v++)
                    {
                        int code = 257 + lengthCodes[v], e = LengthExtra(code - 257);
                        table[256 + v] = (uint)lc[code] | (uint)(v & ((1 << e) - 1)) << ll[code] | (uint)(ll[code] + e) << 24;
                    }
                    // distance code: code | code length << 16 | code + extra length << 24
                    for (int i = 0; i < DistCodes; i++) dist[i] = (uint)dc[i] | (uint)dl[i] << 16 | (uint)(dl[i] + DistExtra(i)) << 24;

                    // a literal takes the same path with a zero mask, so its distance part writes 0 bits
                    ushort* distances = symDist, values = symLen;
                    for (int i = 0, symbols = count; i < symbols; i++)
                    {
                        int distance = distances[i], m = -distance >> 31;
                        uint entry = table[values[i] + (256 & m)];
                        bits.Put(entry & 0xFFFFFF, (int)(entry >> 24));
                        int d = (distance - 1) & m;
                        uint de = dist[distanceCodes[d < 256 ? d : 256 + (d >> 7)]];
                        int shift = (int)(de >> 16) & 0xFF, total = (int)(de >> 24);
                        bits.Put(((de & 0xFFFF) | (uint)(d & ((1 << (total - shift)) - 1)) << shift) & (uint)m, total & m);
                    }
                }
                bits.Put((uint)lc[EndOfBlock], ll[EndOfBlock]);
            }
        }

        // bits LSB first into a buffer drained to the stream
        private struct BitWriter
        {
            private readonly byte[] buffer;
            private readonly byte* start;
            private readonly Stream stream;
            private int pos, fill;
            private ulong acc;

            public BitWriter(byte[] buffer, byte* start, Stream stream)
            {
                this.buffer = buffer;
                this.start = start;
                this.stream = stream;
                pos = 0;
                fill = 0;
                acc = 0;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Put(uint value, int length)
            {
                acc |= (ulong)value << fill;
                fill += length;
                if (fill < 32) return;
                if (pos + 4 > buffer.Length) Drain();
                *(uint*)(start + pos) = BitConverter.IsLittleEndian ? (uint)acc : BinaryPrimitives.ReverseEndianness((uint)acc);
                pos += 4;
                acc >>= 32;
                fill -= 32;
            }

            public void AlignToByte()
            {
                for (; fill > 0; fill -= 8)
                {
                    if (pos == buffer.Length) Drain();
                    buffer[pos++] = (byte)acc;
                    acc >>= 8;
                }
                fill = 0;
                acc = 0;
            }

            public void PutBytes(ReadOnlySpan<byte> bytes)
            {
                AlignToByte();
                Drain();
                stream.Write(bytes);
            }

            public void Drain()
            {
                stream.Write(buffer, 0, pos);
                pos = 0;
            }
        }

        #region trees
        // Huffman code lengths for freq[0..n), none longer than limit; at least two codes, as zlib forces.
        private static void BuildLengths(int* freq, int n, int* lens, int limit, int* scratch)
        {
            int* sorted = scratch, key = scratch + 288;
            int used = 0;
            for (int i = 0; i < n; i++)
            {
                lens[i] = 0;
                if (freq[i] != 0) sorted[used++] = freq[i] << 9 | i;
            }
            if (used < 2)
            {
                int a = used == 1 ? sorted[0] & 511 : 0;
                lens[a] = 1;
                lens[a == 0 ? 1 : 0] = 1;
                return;
            }

            new Span<int>(sorted, used).Sort();
            for (int i = 0; i < used; i++) key[i] = sorted[i] >> 9;
            MinimumRedundancy(key, used);

            Span<int> perLength = stackalloc int[33];
            perLength.Clear();
            for (int i = 0; i < used; i++) perLength[key[i]]++;
            for (int i = limit + 1; i <= 32; i++) perLength[limit] += perLength[i];
            uint total = 0;
            for (int i = limit; i > 0; i--) total += (uint)perLength[i] << (limit - i);
            for (; total != 1u << limit; total--)
            {
                perLength[limit]--;
                for (int i = limit - 1; i > 0; i--)
                {
                    if (perLength[i] == 0) continue;
                    perLength[i]--;
                    perLength[i + 1] += 2;
                    break;
                }
            }

            int j = used;
            for (int len = 1; len <= limit; len++)
                for (int k = perLength[len]; k > 0; k--) lens[sorted[--j] & 511] = len;
        }

        // Moffat and Katajainen's in-place minimum-redundancy lengths; a holds ascending weights, gets lengths.
        private static void MinimumRedundancy(int* a, int n)
        {
            a[0] += a[1];
            int root = 0, leaf = 2;
            for (int next = 1; next < n - 1; next++)
            {
                if (leaf >= n || a[root] < a[leaf])
                {
                    a[next] = a[root];
                    a[root++] = next;
                }
                else a[next] = a[leaf++];
                if (leaf >= n || (root < next && a[root] < a[leaf]))
                {
                    a[next] += a[root];
                    a[root++] = next;
                }
                else a[next] += a[leaf++];
            }

            a[n - 2] = 0;
            for (int next = n - 3; next >= 0; next--) a[next] = a[a[next]] + 1;
            int avbl = 1, depthUsed = 0, depth = 0, slot = n - 1;
            root = n - 2;
            while (avbl > 0)
            {
                while (root >= 0 && a[root] == depth)
                {
                    depthUsed++;
                    root--;
                }
                while (avbl > depthUsed)
                {
                    a[slot--] = depth;
                    avbl--;
                }
                avbl = 2 * depthUsed;
                depth++;
                depthUsed = 0;
            }
        }

        // Canonical codes, bit-reversed for LSB-first output.
        private static void Canonical(int* lens, int n, int* codes)
        {
            Span<int> perLength = stackalloc int[16];
            Span<int> next = stackalloc int[16];
            perLength.Clear();
            for (int i = 0; i < n; i++) perLength[lens[i]]++;
            perLength[0] = 0;
            int code = 0;
            for (int len = 1; len < 16; len++)
            {
                code = (code + perLength[len - 1]) << 1;
                next[len] = code;
            }
            for (int i = 0; i < n; i++)
            {
                int len = lens[i];
                if (len != 0) codes[i] = Reverse(next[len]++, len);
            }
        }

        // zlib's scan_tree / send_tree runs: 16 repeats the previous length 3-6 times, 17 and 18 repeat zero.
        private static int RunLengths(int* lens, int n, int* rle, int runs, int* clFreq)
        {
            int prevLen = -1, nextLen = lens[0], count = 0, maxCount = 7, minCount = 4;
            if (nextLen == 0)
            {
                maxCount = 138;
                minCount = 3;
            }
            for (int i = 0; i < n; i++)
            {
                int curLen = nextLen;
                nextLen = i + 1 < n ? lens[i + 1] : -1;
                if (++count < maxCount && curLen == nextLen) continue;
                if (count < minCount)
                {
                    clFreq[curLen] += count;
                    for (; count > 0; count--) rle[runs++] = curLen;
                }
                else if (curLen != 0)
                {
                    if (curLen != prevLen)
                    {
                        rle[runs++] = curLen;
                        clFreq[curLen]++;
                        count--;
                    }
                    rle[runs++] = 16 | (count - 3) << 5;
                    clFreq[16]++;
                }
                else if (count <= 10)
                {
                    rle[runs++] = 17 | (count - 3) << 5;
                    clFreq[17]++;
                }
                else
                {
                    rle[runs++] = 18 | (count - 11) << 5;
                    clFreq[18]++;
                }

                count = 0;
                prevLen = curLen;
                if (nextLen == 0)
                {
                    maxCount = 138;
                    minCount = 3;
                }
                else if (curLen == nextLen)
                {
                    maxCount = 6;
                    minCount = 3;
                }
                else
                {
                    maxCount = 7;
                    minCount = 4;
                }
            }
            return runs;
        }
        #endregion

        #region primitives
        private static int Common(byte* a, byte* b, int max)
        {
            int i = 0;
            if (Vector512.IsHardwareAccelerated)
                for (; i + 64 <= max; i += 64)
                {
                    ulong m = ~Vector512.Equals(Vector512.Load(a + i), Vector512.Load(b + i)).ExtractMostSignificantBits();
                    if (m != 0) return i + BitOperations.TrailingZeroCount(m);
                }
            if (Vector256.IsHardwareAccelerated)
                for (; i + 32 <= max; i += 32)
                {
                    uint m = ~Vector256.Equals(Vector256.Load(a + i), Vector256.Load(b + i)).ExtractMostSignificantBits();
                    if (m != 0) return i + BitOperations.TrailingZeroCount(m);
                }
            if (Vector128.IsHardwareAccelerated)
                for (; i + 16 <= max; i += 16)
                {
                    uint m = ~Vector128.Equals(Vector128.Load(a + i), Vector128.Load(b + i)).ExtractMostSignificantBits() & 0xFFFF;
                    if (m != 0) return i + BitOperations.TrailingZeroCount(m);
                }
            for (; i + 8 <= max; i += 8)
            {
                ulong x = Unsafe.ReadUnaligned<ulong>(a + i) ^ Unsafe.ReadUnaligned<ulong>(b + i);
                if (x != 0) return i + (BitOperations.TrailingZeroCount(x) >> 3);
            }
            while (i < max && a[i] == b[i]) i++;
            return i;
        }

        // dst[k] = value + k for k < count
        private static void Ascending(int* dst, int value, int count)
        {
            int k = 0;
            if (Vector512.IsHardwareAccelerated)
                for (Vector512<int> v = Vector512.Create(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15) + Vector512.Create(value); k + 16 <= count; k += 16, v += Vector512.Create(16))
                    v.Store(dst + k);
            if (Vector256.IsHardwareAccelerated)
                for (Vector256<int> v = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7) + Vector256.Create(value + k); k + 8 <= count; k += 8, v += Vector256.Create(8))
                    v.Store(dst + k);
            if (Vector128.IsHardwareAccelerated)
                for (Vector128<int> v = Vector128.Create(0, 1, 2, 3) + Vector128.Create(value + k); k + 4 <= count; k += 4, v += Vector128.Create(4))
                    v.Store(dst + k);
            for (; k < count; k++) dst[k] = value + k;
        }

        // length - 3 to its code index 0-28 (symbol 257 + index)
        private static int LengthCode(int l)
        {
            if (l < 8) return l;
            if (l == 255) return 28;
            int top = BitOperations.Log2((uint)l);
            return 4 * (top - 1) + ((l >> (top - 2)) & 3);
        }

        private static byte[] LengthCodeTable()
        {
            byte[] table = new byte[256];
            for (int i = 0; i < table.Length; i++) table[i] = (byte)LengthCode(i);
            return table;
        }

        private static byte[] DistCodeTable()
        {
            byte[] table = new byte[512];
            for (int i = 0; i < 256; i++)
            {
                table[i] = (byte)DistCode(i);
                table[256 + i] = (byte)DistCode(i << 7);
            }
            return table;
        }

        private static int LengthExtra(int code) => code < 8 || code == 28 ? 0 : (code >> 2) - 1;

        // distance - 1 to its code 0-29
        private static int DistCode(int d)
        {
            if (d < 4) return d;
            int top = BitOperations.Log2((uint)d);
            return 2 * top + ((d >> (top - 1)) & 1);
        }

        private static int DistExtra(int code) => code < 4 ? 0 : (code >> 1) - 1;

        private static int FixedLength(int symbol) => symbol < 144 ? 8 : symbol < 256 ? 9 : symbol < 280 ? 7 : 8;

        private static int Reverse(int code, int len)
        {
            int r = 0;
            for (int i = 0; i < len; i++)
            {
                r = r << 1 | (code & 1);
                code >>= 1;
            }
            return r;
        }

        private static uint Adler32(ReadOnlySpan<byte> data)
        {
            uint a = 1, b = 0;
            int i = 0;
            fixed (byte* p = data)
            {
                // 64, 32 or 16 bytes a step: vs1 sums the bytes, vs2 the weighted bytes, vs3 vs1 before each step
                if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
                {
                    Vector512<sbyte> weights = Vector512.Create(
                        Vector256.Create((sbyte)64, 63, 62, 61, 60, 59, 58, 57, 56, 55, 54, 53, 52, 51, 50, 49, 48, 47, 46, 45, 44, 43, 42, 41, 40, 39, 38, 37, 36, 35, 34, 33),
                        Vector256.Create((sbyte)32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1));
                    Vector512<short> ones = Vector512.Create((short)1);
                    while (data.Length - i >= 64)
                    {
                        int len = Math.Min(data.Length - i, 5552) & ~63;
                        Vector512<uint> vs1 = Vector512<uint>.Zero, vs2 = Vector512<uint>.Zero, vs3 = Vector512<uint>.Zero;
                        for (int end = i + len; i < end; i += 64)
                        {
                            Vector512<byte> v = Vector512.Load(p + i);
                            vs3 += vs1;
                            vs1 += Avx512BW.SumAbsoluteDifferences(v, Vector512<byte>.Zero).AsUInt32();
                            vs2 += Avx512BW.MultiplyAddAdjacent(Avx512BW.MultiplyAddAdjacent(v, weights), ones).AsUInt32();
                        }
                        vs2 += vs3 << 6;
                        b += a * (uint)len + Vector512.Sum(vs2);
                        a += Vector512.Sum(vs1);
                        a %= 65521;
                        b %= 65521;
                    }
                }
                if (Vector256.IsHardwareAccelerated && Avx2.IsSupported)
                {
                    Vector256<sbyte> weights = Vector256.Create((sbyte)32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1);
                    Vector256<short> ones = Vector256.Create((short)1);
                    while (data.Length - i >= 32)
                    {
                        int len = Math.Min(data.Length - i, 5552) & ~31;
                        Vector256<uint> vs1 = Vector256<uint>.Zero, vs2 = Vector256<uint>.Zero, vs3 = Vector256<uint>.Zero;
                        for (int end = i + len; i < end; i += 32)
                        {
                            Vector256<byte> v = Vector256.Load(p + i);
                            vs3 += vs1;
                            vs1 += Avx2.SumAbsoluteDifferences(v, Vector256<byte>.Zero).AsUInt32();
                            vs2 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(v, weights), ones).AsUInt32();
                        }
                        vs2 += vs3 << 5;
                        b += a * (uint)len + Vector256.Sum(vs2);
                        a += Vector256.Sum(vs1);
                        a %= 65521;
                        b %= 65521;
                    }
                }
                if (Ssse3.IsSupported)
                {
                    Vector128<sbyte> weights = Vector128.Create((sbyte)16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1);
                    Vector128<short> ones = Vector128.Create((short)1);
                    while (data.Length - i >= 16)
                    {
                        int len = Math.Min(data.Length - i, 5552) & ~15;
                        Vector128<uint> vs1 = Vector128<uint>.Zero, vs2 = Vector128<uint>.Zero, vs3 = Vector128<uint>.Zero;
                        for (int end = i + len; i < end; i += 16)
                        {
                            Vector128<byte> v = Sse2.LoadVector128(p + i);
                            vs3 += vs1;
                            vs1 += Sse2.SumAbsoluteDifferences(v, Vector128<byte>.Zero).AsUInt32();
                            vs2 += Sse2.MultiplyAddAdjacent(Ssse3.MultiplyAddAdjacent(v, weights), ones).AsUInt32();
                        }
                        vs2 += vs3 << 4;
                        b += a * (uint)len + Vector128.Sum(vs2);
                        a += Vector128.Sum(vs1);
                        a %= 65521;
                        b %= 65521;
                    }
                }
            }
            while (i < data.Length)
            {
                int end = Math.Min(i + 5552, data.Length);
                for (; i < end; i++)
                {
                    a += data[i];
                    b += a;
                }
                a %= 65521;
                b %= 65521;
            }
            return b << 16 | a;
        }
        #endregion
    }
}
