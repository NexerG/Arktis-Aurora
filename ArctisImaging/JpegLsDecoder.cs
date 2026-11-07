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
            public bool seen;
            public Ids ids;
            public ushort[] samples;
        }

        // one scan's coding parameters and context state: A, B, C, N per regular context, then A, N, Nn per run context
        private struct Coder
        {
            public int near, maxval, range, qbpp, limit, t1, t2, t3, reset, runIndex;
            public int[] state;
        }

        private const int AAt = 0, BAt = Contexts, CAt = 2 * Contexts, NAt = 3 * Contexts, RunAAt = 4 * Contexts, RunNAt = RunAAt + RunContexts,
            RunNnAt = RunNAt + RunContexts, StateLength = RunNnAt + RunContexts;

        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int pitch, PixelLayout layout)
        {
            Frame f = new Frame();
            try
            {
                ReadSegments(data, ref f);
                if (!f.seen) throw new ImageFormatException("JPEG-LS has no frame");
                Output(in f, destination, pitch, layout);
            }
            finally
            {
                if (f.samples != null) ArrayPool<ushort>.Shared.Return(f.samples);
            }
        }

        #region segments
        private static void ReadSegments(ReadOnlySpan<byte> data, ref Frame f)
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
                        pos = ReadScan(data, pos + length, segment, ref f);
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

        private static int ReadScan(ReadOnlySpan<byte> data, int start, ReadOnlySpan<byte> segment, ref Frame f)
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
            s.state = ArrayPool<int>.Shared.Rent(StateLength);
            int stride = f.width + 2;
            int[] lines = ArrayPool<int>.Shared.Rent(count * 2 * stride);
            try
            {
                Initialise(ref s);
                lines.AsSpan(0, count * 2 * stride).Clear();
                Bits bits = new Bits(data, start);
                if (interleave == 2) DecodeSampleInterleaved(ref bits, ref s, in f,scan.Slice(0, count), lines, stride);
                else DecodeLineInterleaved(ref bits, ref s, in f,scan.Slice(0, count), lines, stride);
                return NextMarker(data, bits.pos);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(s.state);
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
            Span<int> state = s.state.AsSpan(0, StateLength);
            state.Slice(AAt, Contexts).Fill(a);
            state.Slice(BAt, 2 * Contexts).Clear();
            state.Slice(NAt, Contexts).Fill(1);
            state.Slice(RunAAt, RunContexts).Fill(a);
            state.Slice(RunNAt, RunContexts).Fill(1);
            state.Slice(RunNnAt, RunContexts).Clear();
            s.runIndex = 0;
        }
        #endregion

        #region scans
        // ILV 0 or 1: a line of each component in turn, each keeping its own run index.
        private static void DecodeLineInterleaved(ref Bits bits, ref Coder s, in Frame f, scoped ReadOnlySpan<int> scan, int[] lines, int stride)
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
                    DecodeLine(ref bits, ref s, prev, cur, width);
                    runIndices[i] = s.runIndex;
                    Store(cur, f.samples.AsSpan((scan[i] * height + y) * width, width));
                }
            }
        }

        // One component's line; prev and cur hold the samples at 1..width with an edge sample either side.
        private static void DecodeLine(ref Bits bits, ref Coder s, Span<int> prev, Span<int> cur, int width)
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
                    cur[x + 1] = Regular(ref bits, in s,qs, Predict(ra, rb, rc));
                    x++;
                    continue;
                }
                x += Run(ref bits, ref s, prev, cur, x, width);
                rb = prev[x];
                rd = prev[x + 1];
            }
        }

        // ILV 2: every component of a pixel in turn, sharing one run index; a run needs all of them flat.
        private static void DecodeSampleInterleaved(ref Bits bits, ref Coder s, in Frame f, scoped ReadOnlySpan<int> scan, int[] lines, int stride)
        {
            int width = f.width, height = f.height, count = scan.Length;
            Span<int> curAt = stackalloc int[4];
            Span<int> prevAt = stackalloc int[4];
            Span<int> qs = stackalloc int[4];
            for (int y = 0; y < height; y++)
            {
                for (int i = 0; i < count; i++)
                {
                    curAt[i] = (i * 2 + (y & 1)) * stride;
                    prevAt[i] = (i * 2 + ((y + 1) & 1)) * stride;
                    lines[curAt[i]] = lines[prevAt[i] + 1];
                    lines[prevAt[i] + width + 1] = lines[prevAt[i] + width];
                }
                int x = 0;
                while (x < width)
                {
                    bool flat = true;
                    for (int i = 0; i < count; i++)
                    {
                        int ra = lines[curAt[i] + x], rc = lines[prevAt[i] + x], rb = lines[prevAt[i] + x + 1], rd = lines[prevAt[i] + x + 2];
                        qs[i] = Context(in s,rd - rb, rb - rc, rc - ra);
                        flat &= qs[i] == 0;
                    }
                    if (!flat)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            int ra = lines[curAt[i] + x], rc = lines[prevAt[i] + x], rb = lines[prevAt[i] + x + 1];
                            lines[curAt[i] + x + 1] = Regular(ref bits, in s,qs[i], Predict(ra, rb, rc));
                        }
                        x++;
                        continue;
                    }
                    int length = RunLength(ref bits, ref s, width - x);
                    for (int i = 0; i < count; i++) lines.AsSpan(curAt[i] + x + 1, length).Fill(lines[curAt[i] + x]);
                    int end = x + length;
                    if (end < width)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            int ra = lines[curAt[i] + x], rb = lines[prevAt[i] + end + 1];
                            int err = InterruptionError(ref bits, in s,0);
                            lines[curAt[i] + end + 1] = Reconstruct(in s,rb, rb - ra < 0 ? -err : err);
                        }
                        s.runIndex = Math.Max(0, s.runIndex - 1);
                        end++;
                    }
                    x = end;
                }
                for (int i = 0; i < count; i++) Store(lines.AsSpan(curAt[i], stride), f.samples.AsSpan((scan[i] * height + y) * width, width));
            }
        }

        private static void Store(ReadOnlySpan<int> line, Span<ushort> plane)
        {
            for (int x = 0; x < plane.Length; x++) plane[x] = (ushort)line[x + 1];
        }
        #endregion

        #region modelling
        // The signed context of three local gradients; zero means run mode.
        private static int Context(in Coder s,int d1, int d2, int d3) => (Quantise(in s,d1) * 9 + Quantise(in s,d2)) * 9 + Quantise(in s,d3);

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
        private static int Predict(int ra, int rb, int rc)
        {
            if (ra < rb)
            {
                if (rc < ra) return rb;
                if (rc > rb) return ra;
            }
            else
            {
                if (rc < rb) return ra;
                if (rc > ra) return rb;
            }
            return ra + rb - rc;
        }

        // One regular-mode sample: bias-corrected prediction plus a Golomb-coded error, then the context update.
        private static int Regular(ref Bits bits, in Coder s,int qs, int prediction)
        {
            int sign = qs >> 31, q = (qs ^ sign) - sign;
            int[] state = s.state;
            int a = state[AAt + q], b = state[BAt + q], c = state[CAt + q], n = state[NAt + q];
            int k = 0;
            for (int t = n; t < a; t <<= 1) k++;
            int px = Math.Clamp(prediction + ((c ^ sign) - sign), 0, s.maxval);
            int mapped = DecodeValue(ref bits, k, s.limit, s.qbpp);
            int err = (mapped >> 1) ^ -(mapped & 1);
            if (k == 0 && s.near == 0) err ^= (2 * b + n - 1) >> 31;

            a += Math.Abs(err);
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
            state[AAt + q] = a;
            state[BAt + q] = b;
            state[CAt + q] = c;
            state[NAt + q] = n;
            return Reconstruct(in s,px, (err ^ sign) - sign);
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
            int[] state = s.state;
            int a = state[RunAAt + type], n = state[RunNAt + type], nn = state[RunNnAt + type];
            int temp = a + (n >> 1) * type, k = 0;
            for (int t = n; t < temp; t <<= 1) k++;
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
            state[RunAAt + type] = a;
            state[RunNAt + type] = n + 1;
            state[RunNnAt + type] = nn;
            return err;
        }

        // Prediction plus the dequantised error, brought back into 0..maxval modulo the range.
        private static int Reconstruct(in Coder s,int prediction, int err)
        {
            int step = 2 * s.near + 1, v = prediction + err * step;
            if (v < -s.near) v += s.range * step;
            else if (v > s.maxval + s.near) v -= s.range * step;
            return Math.Clamp(v, 0, s.maxval);
        }

        // A limited-length Golomb code: unary high bits, then k low bits, or qbpp escaped bits past the limit.
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
            int max = (1 << f.precision) - 1, down = f.precision - 8;
            ushort[] samples = f.samples;
            byte[] rgba = ArrayPool<byte>.Shared.Rent(width * 4);
            try
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int at = y * width + x;
                        byte s0 = Reduce(samples[at], max, down);
                        byte s1 = count >= 2 ? Reduce(samples[plane + at], max, down) : s0;
                        byte s2 = count >= 3 ? Reduce(samples[2 * plane + at], max, down) : s0;
                        byte s3 = count == 4 ? Reduce(samples[3 * plane + at], max, down) : count == 2 ? s1 : (byte)255;
                        rgba[x * 4] = s0;
                        rgba[x * 4 + 1] = count == 2 ? s0 : s1;
                        rgba[x * 4 + 2] = count == 2 ? s0 : s2;
                        rgba[x * 4 + 3] = s3;
                    }
                    PixelRows.FromRgba(rgba, destination.Slice(y * pitch), width, layout);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rgba);
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

            public int GetBits(int n)
            {
                if (n == 0) return 0;
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
