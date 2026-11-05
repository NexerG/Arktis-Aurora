using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Arctis.Imaging
{
    /// <summary>zlib (RFC 1950) and DEFLATE (RFC 1951) compression; the match search follows zlib's deflate_slow.</summary>
    public static unsafe class Deflater
    {
        private const int MinMatch = 3, MaxMatch = 258, WSize = 32768, WMask = WSize - 1, TooFar = 4096;
        private const int MaxDist = WSize - (MaxMatch + MinMatch + 1), HashSize = 1 << 15, HashMask = HashSize - 1;
        private const int BlockSymbols = 16383, MaxStored = 65535, EndOfBlock = 256;
        private const int LitlenCodes = 286, DistCodes = 30, LengthCodes = 19;

        // work layout, in ints
        private const int LitFreq = 0, DistFreq = 288, ClFreq = 320, LitLens = 340, DistLens = 628, ClLens = 660;
        private const int LitCodes = 680, DistCodesAt = 968, ClCodes = 1000, Rle = 1020, Scratch = 1340, WorkSize = 1916;

        private static ReadOnlySpan<byte> CodeOrder => new byte[] { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

        /// <summary>Writes input as one zlib stream.</summary>
        /// <exception cref="ArgumentException">A setting is out of range.</exception>
        public static void Deflate(ReadOnlySpan<byte> input, Stream output, in DeflateSettings settings = default)
        {
            settings.Check();
            int[] chains = ArrayPool<int>.Shared.Rent(HashSize + WSize);
            ushort[] symbols = ArrayPool<ushort>.Shared.Rent(2 * BlockSymbols);
            int[] work = ArrayPool<int>.Shared.Rent(WorkSize);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
            try
            {
                fixed (byte* data = input)
                fixed (int* c = chains)
                fixed (ushort* s = symbols)
                fixed (int* w = work)
                {
                    Encoder e = new Encoder(data, input.Length, c, s, w, new BitWriter(buffer, output), settings);
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
            private readonly int n;
            private readonly int* head, prev;
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
                prev = chains + HashSize;
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
                new Span<int>(head, HashSize).Clear();
                new Span<int>(work, ClFreq).Clear();
            }

            // zlib's deflate_slow: a match is taken only if the next position has no longer one.
            public void Run()
            {
                int strstart = 0, matchLength = MinMatch - 1, matchStart = 0;
                bool matchAvailable = false;
                while (strstart < n)
                {
                    int hashHead = 0;
                    if (n - strstart >= MinMatch) hashHead = Insert(strstart);
                    int prevLength = matchLength, prevMatch = matchStart;
                    matchLength = MinMatch - 1;
                    if (hashHead != 0 && prevLength < maxLazy && strstart - hashHead <= MaxDist)
                    {
                        matchLength = LongestMatch(strstart, hashHead, prevLength, ref matchStart);
                        if (matchLength == MinMatch && strstart - matchStart > TooFar) matchLength = MinMatch - 1;
                    }

                    if (prevLength >= MinMatch && matchLength <= prevLength)
                    {
                        int maxInsert = n - MinMatch;
                        bool full = TallyMatch(strstart - 1 - prevMatch, prevLength);
                        for (int k = prevLength - 2; k > 0; k--)
                            if (++strstart <= maxInsert) Insert(strstart);
                        matchAvailable = false;
                        matchLength = MinMatch - 1;
                        strstart++;
                        if (full) WriteBlock(false);
                    }
                    else if (matchAvailable)
                    {
                        if (TallyLiteral(data[strstart - 1])) WriteBlock(false);
                        strstart++;
                    }
                    else
                    {
                        matchAvailable = true;
                        strstart++;
                    }
                }
                if (matchAvailable) TallyLiteral(data[strstart - 1]);
                WriteBlock(true);
            }

            private readonly int Insert(int pos)
            {
                byte* p = data + pos;
                int h = ((p[0] << 10) ^ (p[1] << 5) ^ p[2]) & HashMask;
                int match = head[h];
                prev[pos & WMask] = match;
                head[h] = pos;
                return match;
            }

            // zlib's longest_match: walks the hash chain for a match longer than prevLength.
            private readonly int LongestMatch(int strstart, int cur, int prevLength, ref int matchStart)
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
                byte scanEnd1 = scan[bestLen - 1], scanEnd = scan[bestLen];
                do
                {
                    byte* match = window + cur;
                    if (match[bestLen] != scanEnd || match[bestLen - 1] != scanEnd1 || match[0] != scan[0] || match[1] != scan[1]) continue;
                    int len = 2 + Common(scan + 2, match + 2, maxLen - 2);
                    if (len > bestLen)
                    {
                        matchStart = cur;
                        bestLen = len;
                        if (len >= nice) break;
                        scanEnd1 = scan[bestLen - 1];
                        scanEnd = scan[bestLen];
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
                work[LitFreq + 257 + LengthCode(length - MinMatch)]++;
                work[DistFreq + DistCode(distance - 1)]++;
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
                ushort* distances = symDist, values = symLen;
                int symbols = count;
                for (int i = 0; i < symbols; i++)
                {
                    int distance = distances[i], value = values[i];
                    if (distance == 0)
                    {
                        bits.Put((uint)lc[value], ll[value]);
                        continue;
                    }
                    int code = LengthCode(value), e = LengthExtra(code);
                    bits.Put((uint)lc[257 + code], ll[257 + code]);
                    if (e != 0) bits.Put((uint)(value & ((1 << e) - 1)), e);
                    int d = distance - 1, dcode = DistCode(d), de = DistExtra(dcode);
                    bits.Put((uint)dc[dcode], dl[dcode]);
                    if (de != 0) bits.Put((uint)(d & ((1 << de) - 1)), de);
                }
                bits.Put((uint)lc[EndOfBlock], ll[EndOfBlock]);
            }
        }

        // bits LSB first into a buffer drained to the stream
        private struct BitWriter
        {
            private readonly byte[] buffer;
            private readonly Stream stream;
            private int pos, fill;
            private ulong acc;

            public BitWriter(byte[] buffer, Stream stream)
            {
                this.buffer = buffer;
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
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)acc);
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
            for (; i + 8 <= max; i += 8)
            {
                ulong x = Unsafe.ReadUnaligned<ulong>(a + i) ^ Unsafe.ReadUnaligned<ulong>(b + i);
                if (x != 0) return i + (BitOperations.TrailingZeroCount(x) >> 3);
            }
            while (i < max && a[i] == b[i]) i++;
            return i;
        }

        // length - 3 to its code index 0-28 (symbol 257 + index)
        private static int LengthCode(int l)
        {
            if (l < 8) return l;
            if (l == 255) return 28;
            int top = BitOperations.Log2((uint)l);
            return 4 * (top - 1) + ((l >> (top - 2)) & 3);
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
