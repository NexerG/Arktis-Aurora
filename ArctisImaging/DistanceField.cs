using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Arctis.Imaging
{
    // ADF: an RGBA8 distance-field atlas whose alpha is median(R,G,B). R, G and B are planes in 256² tiles, each texel
    // MED-predicted from left, up and up-left, the residual quantised to ±1, zero runs and literals under one canonical
    // Huffman code per plane. Layout: "ADF1", width, height, 3 x 537 code lengths, tile offsets, tile streams, 16 zero bytes.
    internal static unsafe class DistanceField
    {
        private const int Tile = 256, TileTexels = Tile * Tile, Near = 1, Step = 2 * Near + 1;
        private const int RunBase = 512, RunBuckets = 25, Alphabet = RunBase + RunBuckets, CodeBits = 12, RootSize = 1 << CodeBits;
        private const int HeaderEnd = 12, TablesEnd = HeaderEnd + 3 * Alphabet, Padding = 16, PlanePadding = 32;
        private const int RunFlag = 16;

        private static ReadOnlySpan<byte> Magic => "ADF1"u8;

        // Recognises the magic and reads the size; never throws.
        internal static bool TryReadHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (data.Length < HeaderEnd || !data.StartsWith(Magic)) return false;
            header = new ImageHeader(ImageFormat.Adf, (int)Math.Min(U32(data, 4), int.MaxValue), (int)Math.Min(U32(data, 8), int.MaxValue), 4, 8, true);
            return true;
        }

        #region encode
        internal static void Encode(ReadOnlySpan<byte> rgba, int width, int height, int rowPitch, Stream output)
        {
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<byte> row = rgba.Slice(y * rowPitch, width * 4);
                for (int i = 0; i < row.Length; i += 4)
                    if (row[i + 3] != Median(row[i], row[i + 1], row[i + 2]))
                        throw new ArgumentException($"alpha at {i / 4},{y} is not the median of R, G and B");
            }

            int tilesX = (width + Tile - 1) / Tile, tilesY = (height + Tile - 1) / Tile, tileCount = tilesX * tilesY;
            byte[] planes = ArrayPool<byte>.Shared.Rent(3 * TileTexels);
            uint[] symbols = ArrayPool<uint>.Shared.Rent(TileTexels);
            byte[] stream = ArrayPool<byte>.Shared.Rent(3 * TileTexels * CodeBits / 8 + 16);
            try
            {
                int[] counts = new int[3 * Alphabet];
                for (int t = 0; t < tileCount; t++)
                {
                    TileRect(t, tilesX, width, height, out int x0, out int y0, out int tw, out int th);
                    Split(rgba, rowPitch, x0, y0, tw, th, planes);
                    for (int p = 0; p < 3; p++)
                    {
                        int n = PlaneSymbols(planes, p * tw * th, tw, th, symbols);
                        for (int i = 0; i < n; i++) counts[p * Alphabet + (int)(symbols[i] & 1023)]++;
                    }
                }

                byte[] lengths = new byte[3 * Alphabet];
                uint[] codes = new uint[3 * Alphabet];
                for (int p = 0; p < 3; p++)
                {
                    BuildLengths(counts.AsSpan(p * Alphabet, Alphabet), lengths.AsSpan(p * Alphabet, Alphabet));
                    Canonical(lengths.AsSpan(p * Alphabet, Alphabet), codes.AsSpan(p * Alphabet, Alphabet));
                }

                using MemoryStream body = new MemoryStream(width * height / 2);
                uint[] offsets = new uint[tileCount + 1];
                for (int t = 0; t < tileCount; t++)
                {
                    TileRect(t, tilesX, width, height, out int x0, out int y0, out int tw, out int th);
                    Split(rgba, rowPitch, x0, y0, tw, th, planes);
                    offsets[t] = (uint)body.Length;
                    ulong acc = 0;
                    int bits = 0, at = 0;
                    for (int p = 0; p < 3; p++)
                    {
                        int n = PlaneSymbols(planes, p * tw * th, tw, th, symbols);
                        for (int i = 0; i < n; i++)
                        {
                            int symbol = (int)(symbols[i] & 1023), index = p * Alphabet + symbol;
                            Put(stream, ref at, ref acc, ref bits, codes[index], lengths[index]);
                            if (symbol > RunBase) Put(stream, ref at, ref acc, ref bits, symbols[i] >> 10, symbol - RunBase);
                        }
                    }
                    if (bits > 0) stream[at++] = (byte)(acc << (8 - bits));
                    body.Write(stream, 0, at);
                }
                offsets[tileCount] = (uint)body.Length;

                Span<byte> word = stackalloc byte[4];
                output.Write(Magic);
                BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)width);
                output.Write(word);
                BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)height);
                output.Write(word);
                output.Write(lengths);
                for (int t = 0; t <= tileCount; t++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(word, offsets[t]);
                    output.Write(word);
                }
                body.WriteTo(output);
                output.Write(stackalloc byte[Padding]);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(planes);
                ArrayPool<uint>.Shared.Return(symbols);
                ArrayPool<byte>.Shared.Return(stream);
            }
        }

        // Copies a tile's R, G and B into three tight planes.
        private static void Split(ReadOnlySpan<byte> rgba, int rowPitch, int x0, int y0, int tw, int th, byte[] planes)
        {
            int n = tw * th;
            for (int y = 0; y < th; y++)
            {
                ReadOnlySpan<byte> row = rgba.Slice((y0 + y) * rowPitch + x0 * 4, tw * 4);
                int o = y * tw;
                for (int x = 0; x < tw; x++)
                {
                    planes[o + x] = row[x * 4];
                    planes[n + o + x] = row[x * 4 + 1];
                    planes[2 * n + o + x] = row[x * 4 + 2];
                }
            }
        }

        // Quantises one plane in place, as the decoder will reconstruct it, and lists its symbols:
        // bits 0-9 the symbol, bits 10+ a run's extra bits.
        private static int PlaneSymbols(byte[] planes, int start, int tw, int th, uint[] symbols)
        {
            int count = 0, zeros = 0;
            for (int y = 0; y < th; y++)
            {
                int o = start + y * tw;
                for (int x = 0; x < tw; x++)
                {
                    int pred = y == 0 ? (x == 0 ? 0 : planes[o + x - 1]) : (x == 0 ? planes[o - tw] : Med(planes[o + x - 1], planes[o + x - tw], planes[o + x - tw - 1]));
                    int diff = planes[o + x] - pred;
                    int e = diff >= 0 ? (diff + Near) / Step : -((-diff + Near) / Step);
                    int v = pred + e * Step;
                    planes[o + x] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                    if (e == 0)
                    {
                        zeros++;
                        continue;
                    }
                    if (zeros > 0)
                    {
                        symbols[count++] = Run(zeros);
                        zeros = 0;
                    }
                    symbols[count++] = (uint)((e << 1) ^ (e >> 31));
                }
            }
            if (zeros > 0) symbols[count++] = Run(zeros);
            return count;
        }

        private static uint Run(int length)
        {
            int k = BitOperations.Log2((uint)length);
            return (uint)(RunBase + k) | (uint)(length - (1 << k)) << 10;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Put(byte[] stream, ref int at, ref ulong acc, ref int bits, uint value, int length)
        {
            acc = acc << length | value;
            bits += length;
            while (bits >= 8)
            {
                bits -= 8;
                stream[at++] = (byte)(acc >> bits);
            }
        }

        // Huffman code lengths capped at CodeBits; counts are halved until the tree fits.
        private static void BuildLengths(ReadOnlySpan<int> counts, Span<byte> lengths)
        {
            int[] weights = counts.ToArray();
            int[] parent = new int[2 * Alphabet];
            int[] leaf = new int[Alphabet];
            PriorityQueue<int, long> queue = new PriorityQueue<int, long>(Alphabet);
            while (true)
            {
                lengths.Clear();
                int used = 0, only = 0;
                for (int s = 0; s < Alphabet; s++)
                    if (weights[s] > 0)
                    {
                        used++;
                        only = s;
                    }
                if (used == 0) return;
                if (used == 1)
                {
                    lengths[only] = 1;
                    return;
                }

                int nodes = 0;
                queue.Clear();
                for (int s = 0; s < Alphabet; s++)
                    if (weights[s] > 0)
                    {
                        leaf[s] = nodes;
                        parent[nodes] = -1;
                        queue.Enqueue(nodes++, weights[s]);
                    }
                while (queue.Count > 1)
                {
                    queue.TryDequeue(out int a, out long wa);
                    queue.TryDequeue(out int b, out long wb);
                    parent[nodes] = -1;
                    parent[a] = nodes;
                    parent[b] = nodes;
                    queue.Enqueue(nodes++, wa + wb);
                }

                int longest = 0;
                for (int s = 0; s < Alphabet; s++)
                    if (weights[s] > 0)
                    {
                        int depth = 0;
                        for (int node = leaf[s]; parent[node] >= 0; node = parent[node]) depth++;
                        lengths[s] = (byte)depth;
                        if (depth > longest) longest = depth;
                    }
                if (longest <= CodeBits) return;
                for (int s = 0; s < Alphabet; s++)
                    if (weights[s] > 0) weights[s] = Math.Max(1, weights[s] >> 1);
            }
        }

        // Canonical codes, shortest first and by symbol within a length; read most significant bit first.
        private static void Canonical(ReadOnlySpan<byte> lengths, Span<uint> codes)
        {
            Span<int> perLength = stackalloc int[CodeBits + 1];
            Span<uint> next = stackalloc uint[CodeBits + 1];
            perLength.Clear();
            for (int s = 0; s < lengths.Length; s++)
                if (lengths[s] > 0) perLength[lengths[s]]++;
            uint code = 0;
            for (int b = 1; b <= CodeBits; b++)
            {
                code = (code + (uint)perLength[b - 1]) << 1;
                next[b] = code;
            }
            for (int s = 0; s < lengths.Length; s++)
                if (lengths[s] > 0) codes[s] = next[lengths[s]]++;
        }
        #endregion

        #region decode
        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int rowPitch, PixelLayout layout)
        {
            if (layout != PixelLayout.RGBA8) throw new NotSupportedException($"ADF decodes to RGBA8 only, not {layout}");
            int width = (int)Math.Min(U32(data, 4), int.MaxValue), height = (int)Math.Min(U32(data, 8), int.MaxValue);
            if (width == 0 || height == 0) throw new ImageFormatException($"ADF size {width}x{height} is empty");
            int tilesX = (width + Tile - 1) / Tile, tilesY = (height + Tile - 1) / Tile;
            long tileCount = (long)tilesX * tilesY, offsetsEnd = TablesEnd + (tileCount + 1) * 4;
            if (offsetsEnd + Padding > data.Length) throw new ImageFormatException("ADF tile index is truncated");

            int[] entries = ArrayPool<int>.Shared.Rent(3 * RootSize);
            byte[] planes = ArrayPool<byte>.Shared.Rent(3 * TileTexels + PlanePadding);
            try
            {
                for (int p = 0; p < 3; p++) BuildEntries(data.Slice(HeaderEnd + p * Alphabet, Alphabet), entries.AsSpan(p * RootSize, RootSize));
                long dataLength = data.Length - offsetsEnd - Padding;
                uint previous = 0;
                for (int t = 0; t <= tileCount; t++)
                {
                    uint offset = U32(data, TablesEnd + t * 4);
                    if (offset < previous || offset > dataLength) throw new ImageFormatException($"ADF tile {t} lies outside the file");
                    previous = offset;
                }

                fixed (byte* src = data) fixed (byte* dst = destination) fixed (byte* pb = planes) fixed (int* table = entries)
                {
                    byte* limit = src + data.Length - 8;
                    for (int t = 0; t < tileCount; t++)
                    {
                        TileRect(t, tilesX, width, height, out int x0, out int y0, out int tw, out int th);
                        byte* input = src + offsetsEnd + U32(data, TablesEnd + t * 4);
                        DecodeTile(input, limit, table, pb, tw, th);
                        Interleave(pb, tw, th, dst + (long)y0 * rowPitch + x0 * 4, rowPitch);
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(entries);
                ArrayPool<byte>.Shared.Return(planes);
            }
        }

        // Root table entries: bits 0-3 code length, bit 4 run, bits 8+ a run's bucket or a literal's residual times Step.
        private static void BuildEntries(ReadOnlySpan<byte> lengths, Span<int> entries)
        {
            Span<int> perLength = stackalloc int[CodeBits + 1];
            perLength.Clear();
            int kraft = 0;
            for (int s = 0; s < Alphabet; s++)
            {
                int length = lengths[s];
                if (length == 0) continue;
                if (length > CodeBits) throw new ImageFormatException($"ADF code length {length} exceeds {CodeBits}");
                perLength[length]++;
                kraft += RootSize >> length;
            }
            if (kraft > RootSize) throw new ImageFormatException("ADF code lengths are over-subscribed");

            Span<int> next = stackalloc int[CodeBits + 1];
            int code = 0;
            for (int b = 1; b <= CodeBits; b++)
            {
                code = (code + perLength[b - 1]) << 1;
                next[b] = code;
            }
            entries.Clear();
            for (int s = 0; s < Alphabet; s++)
            {
                int length = lengths[s];
                if (length == 0) continue;
                int entry = s >= RunBase ? length | RunFlag | (s - RunBase) << 8 : length | ((s >> 1) ^ -(s & 1)) * Step << 8;
                entries.Slice(next[length]++ << (CodeBits - length), 1 << (CodeBits - length)).Fill(entry);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void DecodeTile(byte* input, byte* limit, int* table, byte* pb, int tw, int th)
        {
            int n = tw * th;
            bool wide = tw >= 16;
            ulong buf = 0;
            int cnt = 0;
            for (int p = 0; p < 3; p++)
            {
                int* root = table + p * RootSize;
                byte* plane = pb + p * n;
                int run = 0;
                for (int y = 0; y < th; y++)
                {
                    byte* cur = plane + y * tw, up = cur - tw;
                    int x = 0, left = 0;
                    while (x < tw)
                    {
                        if (run == 0)
                        {
                            buf |= BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(input)) >> cnt;
                            input += (63 - cnt) >> 3;
                            cnt |= 56;
                            if (input > limit) Corrupt();
                            int entry = root[buf >> (64 - CodeBits)], length = entry & 15;
                            if (length == 0) Corrupt();
                            buf <<= length;
                            cnt -= length;
                            if ((entry & RunFlag) != 0)
                            {
                                int k = entry >> 8;
                                run = 1 << k;
                                if (k > 0)
                                {
                                    run += (int)(buf >> (64 - k));
                                    buf <<= k;
                                    cnt -= k;
                                }
                                continue;
                            }
                            int pred = y == 0 ? left : (x == 0 ? up[0] : Med(left, up[x], up[x - 1]));
                            int v = pred + (entry >> 8);
                            left = v < 0 ? 0 : v > 255 ? 255 : v;
                            cur[x] = (byte)left;
                            x++;
                            continue;
                        }

                        int end = x + Math.Min(run, tw - x);
                        run -= end - x;
                        if (y == 0)
                        {
                            for (; x < end; x++) cur[x] = (byte)left;
                            continue;
                        }
                        // MED with left == up-left is up, so after a texel equal to its up neighbour the run copies the row above.
                        while (x < end && x > 0 && left != up[x - 1])
                        {
                            left = Med(left, up[x], up[x - 1]);
                            cur[x] = (byte)left;
                            x++;
                        }
                        if (x < end)
                        {
                            if (wide)
                            {
                                byte* s = up + x, d = cur + x;
                                int rem = end - x;
                                do
                                {
                                    Vector128.Store(Vector128.Load(s), d);
                                    s += 16;
                                    d += 16;
                                    rem -= 16;
                                } while (rem > 0);
                            }
                            else
                                for (int j = x; j < end; j++) cur[j] = up[j];
                            x = end;
                            left = up[x - 1];
                        }
                    }
                }
                if (run != 0) Corrupt();
            }
        }

        // Writes RGBA with A = median(R,G,B).
        private static void Interleave(byte* pb, int tw, int th, byte* dst, int rowPitch)
        {
            int n = tw * th;
            for (int y = 0; y < th; y++)
            {
                byte* r = pb + y * tw, g = r + n, b = g + n, o = dst + (long)y * rowPitch;
                int x = 0;
                if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
                {
                    // the unpacks leave each 128-bit lane's pixels in four vectors; qword permutes put them in order
                    Vector512<long> first = Vector512.Create(0L, 1, 8, 9, 2, 3, 10, 11), second = Vector512.Create(4L, 5, 12, 13, 6, 7, 14, 15);
                    Vector512<long> low = Vector512.Create(0L, 1, 2, 3, 8, 9, 10, 11), high = Vector512.Create(4L, 5, 6, 7, 12, 13, 14, 15);
                    for (; x + 64 <= tw; x += 64)
                    {
                        Vector512<byte> vr = Vector512.Load(r + x), vg = Vector512.Load(g + x), vb = Vector512.Load(b + x);
                        Vector512<byte> m = Avx512BW.Max(Avx512BW.Min(vr, vg), Avx512BW.Min(Avx512BW.Max(vr, vg), vb));
                        Vector512<ushort> rg0 = Avx512BW.UnpackLow(vr, vg).AsUInt16(), rg1 = Avx512BW.UnpackHigh(vr, vg).AsUInt16();
                        Vector512<ushort> bm0 = Avx512BW.UnpackLow(vb, m).AsUInt16(), bm1 = Avx512BW.UnpackHigh(vb, m).AsUInt16();
                        Vector512<long> v0 = Avx512BW.UnpackLow(rg0, bm0).AsInt64(), v1 = Avx512BW.UnpackHigh(rg0, bm0).AsInt64();
                        Vector512<long> v2 = Avx512BW.UnpackLow(rg1, bm1).AsInt64(), v3 = Avx512BW.UnpackHigh(rg1, bm1).AsInt64();
                        Vector512<long> a = Avx512F.PermuteVar8x64x2(v0, first, v1), c = Avx512F.PermuteVar8x64x2(v2, first, v3);
                        Vector512<long> e = Avx512F.PermuteVar8x64x2(v0, second, v1), h = Avx512F.PermuteVar8x64x2(v2, second, v3);
                        byte* d = o + x * 4;
                        Avx512F.PermuteVar8x64x2(a, low, c).AsByte().Store(d);
                        Avx512F.PermuteVar8x64x2(a, high, c).AsByte().Store(d + 64);
                        Avx512F.PermuteVar8x64x2(e, low, h).AsByte().Store(d + 128);
                        Avx512F.PermuteVar8x64x2(e, high, h).AsByte().Store(d + 192);
                    }
                }
                if (Vector256.IsHardwareAccelerated && Avx2.IsSupported)
                    for (; x + 32 <= tw; x += 32)
                    {
                        Vector256<byte> vr = Vector256.Load(r + x), vg = Vector256.Load(g + x), vb = Vector256.Load(b + x);
                        Vector256<byte> m = Avx2.Max(Avx2.Min(vr, vg), Avx2.Min(Avx2.Max(vr, vg), vb));
                        Vector256<ushort> rg0 = Avx2.UnpackLow(vr, vg).AsUInt16(), rg1 = Avx2.UnpackHigh(vr, vg).AsUInt16();
                        Vector256<ushort> bm0 = Avx2.UnpackLow(vb, m).AsUInt16(), bm1 = Avx2.UnpackHigh(vb, m).AsUInt16();
                        Vector256<byte> v0 = Avx2.UnpackLow(rg0, bm0).AsByte(), v1 = Avx2.UnpackHigh(rg0, bm0).AsByte();
                        Vector256<byte> v2 = Avx2.UnpackLow(rg1, bm1).AsByte(), v3 = Avx2.UnpackHigh(rg1, bm1).AsByte();
                        byte* d = o + x * 4;
                        Avx2.Permute2x128(v0, v1, 0x20).Store(d);
                        Avx2.Permute2x128(v2, v3, 0x20).Store(d + 32);
                        Avx2.Permute2x128(v0, v1, 0x31).Store(d + 64);
                        Avx2.Permute2x128(v2, v3, 0x31).Store(d + 96);
                    }
                if (Sse2.IsSupported)
                    for (; x + 16 <= tw; x += 16)
                    {
                        Vector128<byte> vr = Sse2.LoadVector128(r + x), vg = Sse2.LoadVector128(g + x), vb = Sse2.LoadVector128(b + x);
                        Vector128<byte> m = Sse2.Max(Sse2.Min(vr, vg), Sse2.Min(Sse2.Max(vr, vg), vb));
                        Vector128<ushort> rg0 = Sse2.UnpackLow(vr, vg).AsUInt16(), rg1 = Sse2.UnpackHigh(vr, vg).AsUInt16();
                        Vector128<ushort> bm0 = Sse2.UnpackLow(vb, m).AsUInt16(), bm1 = Sse2.UnpackHigh(vb, m).AsUInt16();
                        byte* d = o + x * 4;
                        Sse2.Store(d, Sse2.UnpackLow(rg0, bm0).AsByte());
                        Sse2.Store(d + 16, Sse2.UnpackHigh(rg0, bm0).AsByte());
                        Sse2.Store(d + 32, Sse2.UnpackLow(rg1, bm1).AsByte());
                        Sse2.Store(d + 48, Sse2.UnpackHigh(rg1, bm1).AsByte());
                    }
                for (; x < tw; x++)
                {
                    byte* d = o + x * 4;
                    d[0] = r[x];
                    d[1] = g[x];
                    d[2] = b[x];
                    d[3] = (byte)Median(r[x], g[x], b[x]);
                }
            }
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Corrupt() => throw new ImageFormatException("ADF tile stream is corrupt");
        #endregion

        #region primitives
        private static void TileRect(int t, int tilesX, int width, int height, out int x0, out int y0, out int tw, out int th)
        {
            x0 = t % tilesX * Tile;
            y0 = t / tilesX * Tile;
            tw = Math.Min(Tile, width - x0);
            th = Math.Min(Tile, height - y0);
        }

        // MED: clamp(a + b - c, min(a, b), max(a, b)); min and max by sign mask, without branches.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Med(int a, int b, int c)
        {
            int d = a - b, s = d >> 31;
            int mn = b + (d & s), mx = a - (d & s);
            int g = a + b - c;
            int lo = g - mn;
            g = mn + (lo & ~(lo >> 31));
            int hi = g - mx;
            return mx + (hi & (hi >> 31));
        }

        private static int Median(int a, int b, int c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

        private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at));
        #endregion
    }
}
