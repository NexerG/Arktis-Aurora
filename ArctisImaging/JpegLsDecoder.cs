using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Arctis.Imaging
{
    // JPEG-LS (T.87): LOCO-I context modelling with limited-length Golomb codes and run mode, lossless or near-lossless,
    // 2-16 bits, 1-4 components in any interleave; decoded the way CharLS decodes it. Samples above 8 bits keep their
    // high byte; components are grey, grey + alpha, RGB or RGBA.
    internal static class JpegLsDecoder
    {
        private const int Contexts = 365, RunContexts = 2, BasicT1 = 3, BasicT2 = 7, BasicT3 = 21, DefaultReset = 64;

        // run-length order of each run index
        private static ReadOnlySpan<byte> J => new byte[] { 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

        [InlineArray(4)]
        private struct Ids
        {
            private int element;
        }

        private struct Frame
        {
            public int width, height, count, precision, maxval, t1, t2, t3, reset;
            public bool seen, written;
            public Ids ids;
            public ushort[] samples;
        }

        private struct ContextState
        {
            public int a, b, c, n;
        }

        private struct RunState
        {
            public int a, n, nn;
        }

        // sample width of a scan: above 8 bits masks the bias update; up to 8, ILV 2 reads short codes by table
        private interface IWidth
        {
            static abstract bool IsWide { get; }
            static abstract bool UsesTable { get; }
        }

        private struct Narrow : IWidth
        {
            public static bool IsWide => false;
            public static bool UsesTable => false;
        }

        private struct NarrowTable : IWidth
        {
            public static bool IsWide => false;
            public static bool UsesTable => true;
        }

        private struct Wide : IWidth
        {
            public static bool IsWide => true;
            public static bool UsesTable => false;
        }

        // (mapped error << 4) | length of each Golomb code up to 8 bits, at k * 256 + the next 8 bits; 0 if longer
        private static readonly ushort[] Golomb = BuildGolomb();

        // one scan's coding parameters, context state, and the gradient quantisation table indexed by d + maxval
        private struct Coder
        {
            public int near, maxval, range, qbpp, limit, t1, t2, t3, reset, runIndex;
            public bool wraps;
            public ContextState[] contexts;
            public RunState[] runs;
            public sbyte[] quantise;
        }

        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int pitch, PixelLayout layout)
        {
            Frame f = new Frame();
            try
            {
                ReadSegments(data, ref f, destination, pitch, layout);
                if (!f.seen) throw new ImageFormatException("JPEG-LS has no frame");
                if (!f.written) Output(in f, destination, pitch, layout);
            }
            finally
            {
                if (f.samples != null) ArrayPool<ushort>.Shared.Return(f.samples);
            }
        }

        #region segments
        private static void ReadSegments(ReadOnlySpan<byte> data, ref Frame f, Span<byte> destination, int pitch, PixelLayout layout)
        {
            int pos = 2;
            while (pos + 4 <= data.Length)
            {
                byte marker = data[pos + 1];
                if (data[pos] != 0xFF || marker == 0xFF || marker < 0x80)
                {
                    pos++;
                    continue;
                }
                pos += 2;
                if (marker == 0xD9) break;
                if ((marker >= 0xD0 && marker <= 0xD8) || marker == 0x01) continue;
                int length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos));
                if (length < 2 || pos + length > data.Length) throw new ImageFormatException("JPEG-LS segment runs past the end");
                ReadOnlySpan<byte> segment = data.Slice(pos + 2, length - 2);
                switch (marker)
                {
                    case 0xF7: ReadFrame(segment, ref f); break;
                    case 0xF8: ReadPresets(segment, ref f); break;
                    case 0xDD:
                        if (segment.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(segment) != 0) throw new NotSupportedException("JPEG-LS with restart intervals is not available");
                        break;
                    case 0xE8:
                        if (segment.Length >= 4 && segment[0] == 'm' && segment[1] == 'r' && segment[2] == 'f' && segment[3] == 'x')
                            throw new NotSupportedException("JPEG-LS with an HP colour transform is not available");
                        break;
                    case 0xDA:
                        pos = ReadScan(data, pos + length, segment, ref f, destination, pitch, layout);
                        continue;
                    default:
                        if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                            throw new ImageFormatException("JPEG-LS file holds a T.81 frame");
                        break;
                }
                pos += length;
            }
        }

        private static void ReadFrame(ReadOnlySpan<byte> segment, ref Frame f)
        {
            if (f.seen) throw new ImageFormatException("JPEG-LS has more than one frame");
            if (segment.Length < 6) throw new ImageFormatException("JPEG-LS frame header is truncated");
            f.precision = segment[0];
            f.height = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(1));
            f.width = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(3));
            f.count = segment[5];
            if (f.precision < 2 || f.precision > 16) throw new ImageFormatException($"JPEG-LS precision {f.precision} is out of range");
            if (f.width == 0 || f.height == 0) throw new NotSupportedException("JPEG-LS with the size given later or in an LSE segment is not available");
            if (f.count < 1 || f.count > 4) throw new NotSupportedException($"{f.count}-component JPEG-LS is not available");
            if (segment.Length < 6 + f.count * 3) throw new ImageFormatException("JPEG-LS frame header is truncated");
            Span<int> ids = f.ids;
            for (int i = 0; i < f.count; i++)
            {
                ids[i] = segment[6 + i * 3];
                if (segment[7 + i * 3] != segment[7]) throw new NotSupportedException("subsampled JPEG-LS is not available");
            }
            int total = checked(f.count * f.width * f.height);
            f.samples = ArrayPool<ushort>.Shared.Rent(total);
            f.samples.AsSpan(0, total).Clear();
            f.seen = true;
        }

        // LSE type 1: preset MAXVAL, T1-T3 and RESET; zero keeps the default
        private static void ReadPresets(ReadOnlySpan<byte> segment, ref Frame f)
        {
            if (segment.Length < 1) throw new ImageFormatException("JPEG-LS preset segment is truncated");
            if (segment[0] != 1) throw new NotSupportedException($"JPEG-LS preset segment type {segment[0]} (mapping tables or oversize dimensions) is not available");
            if (segment.Length < 11) throw new ImageFormatException("JPEG-LS preset parameters are truncated");
            f.maxval = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(1));
            f.t1 = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(3));
            f.t2 = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(5));
            f.t3 = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(7));
            f.reset = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(9));
        }

        private static int ReadScan(ReadOnlySpan<byte> data, int start, ReadOnlySpan<byte> segment, ref Frame f, Span<byte> destination, int pitch,
            PixelLayout layout)
        {
            if (!f.seen) throw new ImageFormatException("JPEG-LS scan comes before its frame");
            int count = segment.Length > 0 ? segment[0] : 0;
            if (count < 1 || count > f.count || segment.Length < 4 + count * 2) throw new ImageFormatException("JPEG-LS scan header is malformed");
            Span<int> scan = stackalloc int[4];
            Span<int> ids = f.ids;
            for (int i = 0; i < count; i++)
            {
                int id = segment[1 + i * 2], index = -1;
                for (int c = 0; c < f.count; c++)
                    if (ids[c] == id) index = c;
                if (index < 0) throw new ImageFormatException("JPEG-LS scan names an unknown component");
                if (segment[2 + i * 2] != 0) throw new NotSupportedException("JPEG-LS with mapping tables is not available");
                scan[i] = index;
            }
            int near = segment[1 + count * 2], interleave = segment[2 + count * 2], pt = segment[3 + count * 2] & 15;
            if (interleave > 2 || (interleave == 0 && count != 1)) throw new ImageFormatException("JPEG-LS scan interleave is malformed");
            if (pt != 0) throw new NotSupportedException("JPEG-LS with a point transform is not available");

            Coder s = CreateCoder(in f, near);
            s.contexts = ArrayPool<ContextState>.Shared.Rent(Contexts);
            s.runs = ArrayPool<RunState>.Shared.Rent(RunContexts);
            s.quantise = ArrayPool<sbyte>.Shared.Rent(2 * s.maxval + 1);
            int stride = f.width + 2;
            int[] lines = ArrayPool<int>.Shared.Rent(count * 2 * stride);
            try
            {
                Initialise(ref s);
                lines.AsSpan(0, count * 2 * stride).Clear();
                Bits bits = new Bits(data, start);
                bool wide = s.maxval > 255;
                if (interleave == 2 && count == 3)
                {
                    bool direct = layout == PixelLayout.RGBA8 && f.count == 3 && s.maxval == 255 && f.precision == 8 && scan[0] == 0 && scan[1] == 1
                        && scan[2] == 2;
                    if (wide) DecodeTriplets<Wide>(ref bits, ref s, in f, scan.Slice(0, count), lines, stride, destination, pitch, false);
                    else DecodeTriplets<NarrowTable>(ref bits, ref s, in f, scan.Slice(0, count), lines, stride, destination, pitch, direct);
                    f.written = direct;
                }
                else if (interleave == 2)
                {
                    if (wide) DecodeSampleInterleaved<Wide>(ref bits, ref s, in f, scan.Slice(0, count), lines, stride);
                    else DecodeSampleInterleaved<NarrowTable>(ref bits, ref s, in f, scan.Slice(0, count), lines, stride);
                }
                else if (wide) DecodeLineInterleaved<Wide>(ref bits, ref s, in f, scan.Slice(0, count), lines, stride);
                else DecodeLineInterleaved<Narrow>(ref bits, ref s, in f, scan.Slice(0, count), lines, stride);
                return NextMarker(data, bits.pos);
            }
            finally
            {
                ArrayPool<ContextState>.Shared.Return(s.contexts);
                ArrayPool<RunState>.Shared.Return(s.runs);
                ArrayPool<sbyte>.Shared.Return(s.quantise);
                ArrayPool<int>.Shared.Return(lines);
            }
        }
        #endregion

        #region coding parameters
        // Thresholds from the preset segment or the T.87 defaults, and the derived range, qbpp and limit.
        private static Coder CreateCoder(in Frame f, int near)
        {
            int maxval = f.maxval > 0 ? f.maxval : (1 << f.precision) - 1;
            if (near > Math.Min(255, maxval / 2)) throw new ImageFormatException("JPEG-LS NEAR is out of range");
            int t1, t2, t3;
            if (maxval >= 128)
            {
                int factor = (Math.Min(maxval, 4095) + 128) >> 8;
                t1 = Clamp(factor * (BasicT1 - 2) + 2 + 3 * near, near + 1, maxval);
                t2 = Clamp(factor * (BasicT2 - 3) + 3 + 5 * near, t1, maxval);
                t3 = Clamp(factor * (BasicT3 - 4) + 4 + 7 * near, t2, maxval);
            }
            else
            {
                int factor = 256 / (maxval + 1);
                t1 = Clamp(Math.Max(2, BasicT1 / factor + 3 * near), near + 1, maxval);
                t2 = Clamp(Math.Max(3, BasicT2 / factor + 5 * near), t1, maxval);
                t3 = Clamp(Math.Max(4, BasicT3 / factor + 7 * near), t2, maxval);
            }
            Coder s = new Coder
            {
                near = near,
                maxval = maxval,
                t1 = f.t1 > 0 ? f.t1 : t1,
                t2 = f.t2 > 0 ? f.t2 : t2,
                t3 = f.t3 > 0 ? f.t3 : t3,
                reset = f.reset > 0 ? f.reset : DefaultReset,
                range = (maxval + 2 * near) / (2 * near + 1) + 1
            };
            s.qbpp = BitsToHold(s.range - 1);
            s.wraps = near == 0 && (maxval & (maxval + 1)) == 0;
            int bpp = Math.Max(2, BitsToHold(maxval));
            s.limit = 2 * (bpp + Math.Max(8, bpp));
            return s;
        }

        private static int Clamp(int i, int j, int maxval) => i > maxval || i < j ? j : i;

        // ceil(log2(v + 1)): the bits needed to hold v
        private static int BitsToHold(int v) => v <= 0 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)v);

        private static void Initialise(ref Coder s)
        {
            int a = Math.Max(2, (s.range + 32) / 64);
            s.contexts.AsSpan(0, Contexts).Fill(new ContextState { a = a, n = 1 });
            s.runs.AsSpan(0, RunContexts).Fill(new RunState { a = a, n = 1 });
            sbyte[] quantise = s.quantise;
            for (int d = -s.maxval; d <= s.maxval; d++) quantise[d + s.maxval] = (sbyte)Quantise(in s, d);
            s.runIndex = 0;
        }

        private static ushort[] BuildGolomb()
        {
            ushort[] table = new ushort[8 * 256];
            for (int k = 0; k < 8; k++)
                for (int high = 0; high + 1 + k <= 8; high++)
                {
                    int length = high + 1 + k, spare = 8 - length;
                    for (int low = 0; low < 1 << k; low++)
                    {
                        int code = ((1 << k) | low) << spare;
                        ushort entry = (ushort)((((high << k) | low) << 4) | length);
                        table.AsSpan((k << 8) | code, 1 << spare).Fill(entry);
                    }
                }
            return table;
        }
        #endregion

        #region scans
        // ILV 0 or 1: a line of each component in turn, each keeping its own run index.
        private static void DecodeLineInterleaved<T>(ref Bits bits, ref Coder s, in Frame f, scoped ReadOnlySpan<int> scan, int[] lines, int stride)
            where T : struct, IWidth
        {
            int width = f.width, height = f.height;
            Span<int> runIndices = stackalloc int[4];
            runIndices.Clear();
            for (int y = 0; y < height; y++)
            {
                for (int i = 0; i < scan.Length; i++)
                {
                    Span<int> cur = lines.AsSpan((i * 2 + (y & 1)) * stride, stride), prev = lines.AsSpan((i * 2 + ((y + 1) & 1)) * stride, stride);
                    s.runIndex = runIndices[i];
                    DecodeLine<T>(ref bits, ref s, prev, cur, width);
                    runIndices[i] = s.runIndex;
                    Store(cur, f.samples.AsSpan((scan[i] * height + y) * width, width));
                }
            }
        }

        // One component's line; prev and cur hold the samples at 1..width with an edge sample either side.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void DecodeLine<T>(ref Bits bits, ref Coder s, Span<int> prev, Span<int> cur, int width) where T : struct, IWidth
        {
            cur[0] = prev[1];
            prev[width + 1] = prev[width];
            int x = 0, rb = prev[0], rd = prev[1];
            while (x < width)
            {
                int ra = cur[x], rc = rb;
                rb = rd;
                rd = prev[x + 2];
                int qs = Context(in s,rd - rb, rb - rc, rc - ra);
                if (qs != 0)
                {
                    cur[x + 1] = Regular<T>(ref bits, in s,qs, Predict(ra, rb, rc));
                    x++;
                    continue;
                }
                x += Run(ref bits, ref s, prev, cur, x, width);
                rb = prev[x];
                rd = prev[x + 1];
            }
        }

        // ILV 2: every component of a pixel in turn, sharing one run index; a run needs all of them flat. A line holds
        // the pixels interleaved, component i of position p at p * count + i.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void DecodeSampleInterleaved<T>(ref Bits bits, ref Coder s, in Frame f, scoped ReadOnlySpan<int> scan, int[] lines, int stride)
            where T : struct, IWidth
        {
            int width = f.width, height = f.height, count = scan.Length, row = stride * count;
            Span<int> qs = stackalloc int[4];
            for (int y = 0; y < height; y++)
            {
                Span<int> cur = lines.AsSpan((y & 1) * row, row), prev = lines.AsSpan(((y + 1) & 1) * row, row);
                for (int i = 0; i < count; i++)
                {
                    cur[i] = prev[count + i];
                    prev[(width + 1) * count + i] = prev[width * count + i];
                }
                int x = 0;
                while (x < width)
                {
                    int at = x * count;
                    bool flat = true;
                    for (int i = 0; i < count; i++)
                    {
                        int ra = cur[at + i], rc = prev[at + i], rb = prev[at + count + i], rd = prev[at + 2 * count + i];
                        int q = Context(in s, rd - rb, rb - rc, rc - ra);
                        qs[i] = q;
                        flat &= q == 0;
                    }
                    if (!flat)
                    {
                        if (T.UsesTable) bits.Ensure(32);
                        for (int i = 0; i < count; i++)
                            cur[at + count + i] = Regular<T>(ref bits, in s, qs[i], Predict(cur[at + i], prev[at + count + i], prev[at + i]));
                        x++;
                        continue;
                    }
                    int length = RunLength(ref bits, ref s, width - x);
                    for (int p = at + count; p < at + (length + 1) * count; p++) cur[p] = cur[p - count];
                    int end = x + length;
                    if (end < width)
                    {
                        int to = (end + 1) * count;
                        for (int i = 0; i < count; i++)
                        {
                            int ra = cur[at + i], rb = prev[to + i];
                            int err = InterruptionError(ref bits, in s, 0);
                            cur[to + i] = Reconstruct(in s, rb, rb - ra < 0 ? -err : err);
                        }
                        s.runIndex = Math.Max(0, s.runIndex - 1);
                        end++;
                    }
                    x = end;
                }
                for (int i = 0; i < count; i++) StoreInterleaved(cur, i, count, f.samples.AsSpan((scan[i] * height + y) * width, width));
            }
        }

        // ILV 2 with three components, unrolled.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void DecodeTriplets<T>(ref Bits bits, ref Coder s, in Frame f, scoped ReadOnlySpan<int> scan, int[] lines, int stride,
            Span<byte> destination, int pitch, bool direct) where T : struct, IWidth
        {
            int width = f.width, height = f.height, row = stride * 3, m = s.maxval;
            sbyte[] quantise = s.quantise;
            for (int y = 0; y < height; y++)
            {
                Span<int> cur = lines.AsSpan((y & 1) * row, row), prev = lines.AsSpan(((y + 1) & 1) * row, row);
                for (int i = 0; i < 3; i++)
                {
                    cur[i] = prev[3 + i];
                    prev[(width + 1) * 3 + i] = prev[width * 3 + i];
                }
                int x = 0;
                // Q(Rb - Rc) per component, carried from the previous pixel's Q(Rd - Rb)
                int g0 = quantise[prev[3] - prev[0] + m], g1 = quantise[prev[4] - prev[1] + m], g2 = quantise[prev[5] - prev[2] + m];
                while (x < width)
                {
                    int at = x * 3;
                    int ra0 = cur[at], ra1 = cur[at + 1], ra2 = cur[at + 2];
                    int rc0 = prev[at], rc1 = prev[at + 1], rc2 = prev[at + 2];
                    int rb0 = prev[at + 3], rb1 = prev[at + 4], rb2 = prev[at + 5];
                    int rd0 = prev[at + 6], rd1 = prev[at + 7], rd2 = prev[at + 8];
                    int q0, q1, q2;
                    if (T.IsWide)
                    {
                        q0 = Context(in s, rd0 - rb0, rb0 - rc0, rc0 - ra0);
                        q1 = Context(in s, rd1 - rb1, rb1 - rc1, rc1 - ra1);
                        q2 = Context(in s, rd2 - rb2, rb2 - rc2, rc2 - ra2);
                    }
                    else
                    {
                        int h0 = quantise[rd0 - rb0 + m], h1 = quantise[rd1 - rb1 + m], h2 = quantise[rd2 - rb2 + m];
                        q0 = h0 * 81 + g0 * 9 + quantise[rc0 - ra0 + m];
                        q1 = h1 * 81 + g1 * 9 + quantise[rc1 - ra1 + m];
                        q2 = h2 * 81 + g2 * 9 + quantise[rc2 - ra2 + m];
                        g0 = h0;
                        g1 = h1;
                        g2 = h2;
                    }
                    if ((q0 | q1 | q2) != 0)
                    {
                        if (T.UsesTable) bits.Ensure(24);
                        cur[at + 3] = Regular<T>(ref bits, in s, q0, Predict(ra0, rb0, rc0));
                        cur[at + 4] = Regular<T>(ref bits, in s, q1, Predict(ra1, rb1, rc1));
                        cur[at + 5] = Regular<T>(ref bits, in s, q2, Predict(ra2, rb2, rc2));
                        x++;
                        continue;
                    }
                    int length = RunLength(ref bits, ref s, width - x);
                    for (int p = at + 3; p < at + (length + 1) * 3; p++) cur[p] = cur[p - 3];
                    int end = x + length;
                    if (end < width)
                    {
                        int to = (end + 1) * 3;
                        for (int i = 0; i < 3; i++)
                        {
                            int ra = cur[at + i], rb = prev[to + i];
                            int err = InterruptionError(ref bits, in s, 0);
                            cur[to + i] = Reconstruct(in s, rb, rb - ra < 0 ? -err : err);
                        }
                        s.runIndex = Math.Max(0, s.runIndex - 1);
                        end++;
                    }
                    x = end;
                    if (!T.IsWide && x < width)
                    {
                        int from = x * 3;
                        g0 = quantise[prev[from + 3] - prev[from] + m];
                        g1 = quantise[prev[from + 4] - prev[from + 1] + m];
                        g2 = quantise[prev[from + 5] - prev[from + 2] + m];
                    }
                }
                if (direct)
                {
                    Span<byte> target = destination.Slice(y * pitch, width * 4);
                    for (int p = 0, from = 3; p < target.Length; p += 4, from += 3)
                    {
                        target[p] = (byte)cur[from];
                        target[p + 1] = (byte)cur[from + 1];
                        target[p + 2] = (byte)cur[from + 2];
                        target[p + 3] = 255;
                    }
                }
                else for (int i = 0; i < 3; i++) StoreInterleaved(cur, i, 3, f.samples.AsSpan((scan[i] * height + y) * width, width));
            }
        }

        private static void Store(ReadOnlySpan<int> line, Span<ushort> plane)
        {
            for (int x = 0; x < plane.Length; x++) plane[x] = (ushort)line[x + 1];
        }

        private static void StoreInterleaved(ReadOnlySpan<int> line, int component, int count, Span<ushort> plane)
        {
            for (int x = 0, at = count + component; x < plane.Length; x++, at += count) plane[x] = (ushort)line[at];
        }
        #endregion

        #region modelling
        // The signed context of three local gradients; zero means run mode.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Context(in Coder s, int d1, int d2, int d3)
        {
            sbyte[] q = s.quantise;
            int m = s.maxval;
            return (q[d1 + m] * 9 + q[d2 + m]) * 9 + q[d3 + m];
        }

        private static int Quantise(in Coder s,int d)
        {
            if (d <= -s.t3) return -4;
            if (d <= -s.t2) return -3;
            if (d <= -s.t1) return -2;
            if (d < -s.near) return -1;
            if (d <= s.near) return 0;
            if (d < s.t1) return 1;
            if (d < s.t2) return 2;
            if (d < s.t3) return 3;
            return 4;
        }

        // Median edge detector.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Predict(int ra, int rb, int rc)
        {
            int d = ra - rb, md = d & (d >> 31);
            int min = rb + md, max = ra - md, p = ra + rb - rc;
            int below = (rc - max) >> 31;
            p = (p & below) | (min & ~below);
            int above = (min - rc) >> 31;
            return (p & above) | (max & ~above);
        }

        // One regular-mode sample: bias-corrected prediction plus a Golomb-coded error, then the context update.
        private static int Regular<T>(ref Bits bits, in Coder s,int qs, int prediction) where T : struct, IWidth
        {
            if (T.UsesTable) return RegularTable(ref bits, in s, qs, prediction);
            int sign = qs >> 31;
            ref ContextState context = ref s.contexts[(qs ^ sign) - sign];
            int a = context.a, b = context.b, c = context.c, n = context.n;
            int k = GolombK(n, a);
            int px = Math.Clamp(prediction + ((c ^ sign) - sign), 0, s.maxval);
            int mapped = DecodeValue(ref bits, k, s.limit, s.qbpp);
            int err = (mapped >> 1) ^ -(mapped & 1);
            if (k == 0 && s.near == 0) err ^= (2 * b + n - 1) >> 31;

            a += (err ^ (err >> 31)) - (err >> 31);
            b += err * (2 * s.near + 1);
            if (n == s.reset)
            {
                a >>= 1;
                b >>= 1;
                n >>= 1;
            }
            n++;
            if (T.IsWide)
            {
                int lo = (b + n - 1) >> 31, hi = -b >> 31;
                b += (n & lo) - (n & hi);
                c += lo - hi;
                if (b < 1 - n) b = 1 - n;
                if (b > 0) b = 0;
                if (c < -128) c = -128;
                if (c > 127) c = 127;
            }
            else if (b + n <= 0)
            {
                b += n;
                if (b <= -n) b = -n + 1;
                if (c > -128) c--;
            }
            else if (b > 0)
            {
                b -= n;
                if (b > 0) b = 0;
                if (c < 127) c++;
            }
            context.a = a;
            context.b = b;
            context.c = c;
            context.n = n;
            return s.wraps ? (px + ((err ^ sign) - sign)) & s.maxval : Reconstruct(in s,px, (err ^ sign) - sign);
        }

        // Regular for a code in the Golomb table, call-free; needs 8 buffered bits, misses go to RegularMiss.
        private static int RegularTable(ref Bits bits, in Coder s, int qs, int prediction)
        {
            int sign = qs >> 31;
            ref ContextState context = ref s.contexts[(qs ^ sign) - sign];
            int a = context.a, n = context.n;
            int k = GolombK(n, a);
            int entry = k >= 8 ? 0 : Golomb[(k << 8) | bits.Top8()];
            if (entry == 0) return RegularMiss(ref bits, in s, qs, prediction);
            bits.Skip(entry & 15);
            int b = context.b, c = context.c;
            int px = Math.Clamp(prediction + ((c ^ sign) - sign), 0, s.maxval);
            int mapped = entry >> 4;
            int err = (mapped >> 1) ^ -(mapped & 1);
            if (k == 0 && s.near == 0) err ^= (2 * b + n - 1) >> 31;

            a += (err ^ (err >> 31)) - (err >> 31);
            b += err * (2 * s.near + 1);
            if (n == s.reset)
            {
                a >>= 1;
                b >>= 1;
                n >>= 1;
            }
            n++;
            if (b + n <= 0)
            {
                b += n;
                if (b <= -n) b = -n + 1;
                if (c > -128) c--;
            }
            else if (b > 0)
            {
                b -= n;
                if (b > 0) b = 0;
                if (c < 127) c++;
            }
            context.a = a;
            context.b = b;
            context.c = c;
            context.n = n;
            return s.wraps ? (px + ((err ^ sign) - sign)) & s.maxval : Reconstruct(in s, px, (err ^ sign) - sign);
        }

        // A code longer than the table: the full path, then the 16 bits the next RegularTable calls rely on.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RegularMiss(ref Bits bits, in Coder s, int qs, int prediction)
        {
            int v = Regular<Narrow>(ref bits, in s, qs, prediction);
            bits.Ensure(16);
            return v;
        }

        // The least k with n << k >= a.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GolombK(int n, int a)
        {
            if (n >= a) return 0;
            int k = BitOperations.LeadingZeroCount((uint)n) - BitOperations.LeadingZeroCount((uint)a);
            return (n << k) < a ? k + 1 : k;
        }

        // A run of Ra from start, then the interrupting sample unless the run reached the end of the line.
        private static int Run(ref Bits bits, ref Coder s, Span<int> prev, Span<int> cur, int start, int width)
        {
            int ra = cur[start];
            int length = RunLength(ref bits, ref s, width - start);
            cur.Slice(start + 1, length).Fill(ra);
            int end = start + length;
            if (end == width) return length;
            int rb = prev[end + 1];
            if (Math.Abs(ra - rb) <= s.near) cur[end + 1] = Reconstruct(in s,ra, InterruptionError(ref bits, in s,1));
            else
            {
                int err = InterruptionError(ref bits, in s,0);
                cur[end + 1] = Reconstruct(in s,rb, rb - ra < 0 ? -err : err);
            }
            s.runIndex = Math.Max(0, s.runIndex - 1);
            return length + 1;
        }

        private static int RunLength(ref Bits bits, ref Coder s, int remaining)
        {
            ReadOnlySpan<byte> j = J;
            int index = 0;
            while (bits.GetBits(1) == 1)
            {
                int full = 1 << j[s.runIndex], count = Math.Min(full, remaining - index);
                index += count;
                if (count == full) s.runIndex = Math.Min(31, s.runIndex + 1);
                if (index == remaining) break;
            }
            if (index != remaining && j[s.runIndex] > 0) index += bits.GetBits(j[s.runIndex]);
            if (index > remaining) throw new ImageFormatException("JPEG-LS run runs past the end of the line");
            return index;
        }

        // The error of a run-interruption sample of the given type, then its run context update.
        private static int InterruptionError(ref Bits bits, in Coder s,int type)
        {
            ref RunState run = ref s.runs[type];
            int a = run.a, n = run.n, nn = run.nn;
            int k = GolombK(n, a + (n >> 1) * type);
            int mapped = DecodeValue(ref bits, k, s.limit - J[s.runIndex] - 1, s.qbpp);
            int v = mapped + type, odd = v & 1, magnitude = (v + odd) >> 1;
            int err = (k != 0 || 2 * nn >= n) == (odd != 0) ? -magnitude : magnitude;
            if (err < 0) nn++;
            a += (mapped + 1 - type) >> 1;
            if (n == s.reset)
            {
                a >>= 1;
                n >>= 1;
                nn >>= 1;
            }
            run.a = a;
            run.n = n + 1;
            run.nn = nn;
            return err;
        }

        // Prediction plus the dequantised error, brought back into 0..maxval modulo the range.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Reconstruct(in Coder s,int prediction, int err)
        {
            int step = 2 * s.near + 1, v = prediction + err * step;
            if (v < -s.near) v += s.range * step;
            else if (v > s.maxval + s.near) v -= s.range * step;
            return Math.Clamp(v, 0, s.maxval);
        }

        // A limited-length Golomb code: unary high bits, then k low bits, or qbpp escaped bits past the limit.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int DecodeValue(ref Bits bits, int k, int limit, int qbpp)
        {
            int high = bits.ReadZeros(limit);
            if (high >= limit - (qbpp + 1)) return bits.GetBits(qbpp) + 1;
            return k == 0 ? high : (high << k) + bits.GetBits(k);
        }
        #endregion

        #region output
        // 8-bit samples into the destination: grey, grey + alpha, RGB or RGBA by component count.
        private static void Output(in Frame f,Span<byte> destination, int pitch, PixelLayout layout)
        {
            int width = f.width, height = f.height, count = f.count, plane = width * height;
            int max = (1 << f.precision) - 1, down = f.precision - 8, reduceLength = Math.Max(max, f.maxval) + 1;
            ushort[] samples = f.samples;
            byte[] rgba = ArrayPool<byte>.Shared.Rent(width * 4);
            byte[] reduce = ArrayPool<byte>.Shared.Rent(reduceLength);
            try
            {
                for (int v = 0; v < reduceLength; v++) reduce[v] = Reduce(v, max, down);
                if (count == 1 && layout == PixelLayout.R8)
                {
                    for (int y = 0; y < height; y++)
                    {
                        ReadOnlySpan<ushort> line = samples.AsSpan(y * width, width);
                        Span<byte> row = destination.Slice(y * pitch, width);
                        for (int x = 0; x < line.Length; x++) row[x] = reduce[line[x]];
                    }
                    return;
                }
                for (int y = 0; y < height; y++)
                {
                    Span<byte> target = layout == PixelLayout.RGBA8 ? destination.Slice(y * pitch, width * 4) : rgba;
                    for (int x = 0; x < width; x++)
                    {
                        int at = y * width + x;
                        byte s0 = reduce[samples[at]];
                        byte s1 = count >= 2 ? reduce[samples[plane + at]] : s0;
                        byte s2 = count >= 3 ? reduce[samples[2 * plane + at]] : s0;
                        byte s3 = count == 4 ? reduce[samples[3 * plane + at]] : count == 2 ? s1 : (byte)255;
                        target[x * 4] = s0;
                        target[x * 4 + 1] = count == 2 ? s0 : s1;
                        target[x * 4 + 2] = count == 2 ? s0 : s2;
                        target[x * 4 + 3] = s3;
                    }
                    if (layout != PixelLayout.RGBA8) PixelRows.FromRgba(rgba, destination.Slice(y * pitch), width, layout);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rgba);
                ArrayPool<byte>.Shared.Return(reduce);
            }
        }

        private static byte Reduce(int v, int max, int down) => down >= 0 ? (byte)(Math.Min(v, max) >> down) : (byte)((Math.Min(v, max) * 255 + max / 2) / max);

        // The next marker at or after pos: 0xFF followed by a byte with its top bit set.
        private static int NextMarker(ReadOnlySpan<byte> data, int pos)
        {
            for (; pos + 1 < data.Length; pos++)
                if (data[pos] == 0xFF && data[pos + 1] >= 0x80) return pos;
            return data.Length;
        }
        #endregion

        // MSB-first bits; a byte after 0xFF holds 7 bits under a stuffed zero, and 0xFF before a byte >= 0x80 is a
        // marker, at which the reader feeds zeros.
        private ref struct Bits
        {
            private readonly ReadOnlySpan<byte> data;
            public int pos;
            private ulong buffer;
            private int count;
            private bool afterFF, ended;

            public Bits(ReadOnlySpan<byte> data, int pos)
            {
                this.data = data;
                this.pos = pos;
                buffer = 0;
                count = 0;
                afterFF = false;
                ended = false;
            }

            private void Fill()
            {
                if (!afterFF && !ended && pos + 8 <= data.Length)
                {
                    ulong v = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(pos)), inverse = ~v;
                    if (((inverse - 0x0101010101010101UL) & ~inverse & 0x8080808080808080UL) == 0)
                    {
                        int take = (63 - count) >> 3;
                        buffer |= (v & (~0UL << (64 - 8 * take))) >> count;
                        count += 8 * take;
                        pos += take;
                        return;
                    }
                }
                while (count <= 56)
                {
                    if (ended || pos >= data.Length || (data[pos] == 0xFF && (pos + 1 >= data.Length || data[pos + 1] >= 0x80)))
                    {
                        ended = true;
                        count = 64;
                        return;
                    }
                    int b = data[pos++];
                    if (afterFF)
                    {
                        buffer |= (ulong)b << (57 - count);
                        count += 7;
                    }
                    else
                    {
                        buffer |= (ulong)b << (56 - count);
                        count += 8;
                    }
                    afterFF = b == 0xFF;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Top8() => (int)(buffer >> 56);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Ensure(int n)
            {
                if (count < n) Fill();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Skip(int n)
            {
                buffer <<= n;
                count -= n;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int GetBits(int n)
            {
                if (count < n) Fill();
                int v = (int)(buffer >> (64 - n));
                buffer <<= n;
                count -= n;
                return v;
            }

            // zero bits up to the next one bit, which is consumed; more than limit zeros is a broken stream
            public int ReadZeros(int limit)
            {
                int zeros = 0;
                while (true)
                {
                    if (count < 32) Fill();
                    int lz = buffer == 0 ? 64 : BitOperations.LeadingZeroCount(buffer);
                    if (lz < count)
                    {
                        buffer <<= lz + 1;
                        count -= lz + 1;
                        return zeros + lz;
                    }
                    zeros += count;
                    buffer = 0;
                    count = 0;
                    if (zeros > limit) throw new ImageFormatException("JPEG-LS entropy data is truncated or holds an overlong code");
                }
            }
        }
    }
}
