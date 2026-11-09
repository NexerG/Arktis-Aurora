using System;
using System.Buffers;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Arctis.Imaging
{
    // Baseline JPEG (T.81) written the way libjpeg-turbo writes it: its YCbCr formulas, downsampling and edge padding,
    // islow forward DCT, the Annex K quantisers scaled by quality, standard or two-pass optimal Huffman tables.
    internal static partial class JpegEncoder
    {
        private const int ConstBits = 13, Pass1Bits = 2;

        // one pooled run: quantisers (natural order) for tables 0-1, then code and length per symbol for
        // DC0, AC0, DC1, AC1, then the optimal-table scratch (code sizes, chains, counts per length)
        private const int QuantAt = 0, CodeAt = 2 * 64, SizeAt = CodeAt + 4 * 256, CodeSizeAt = SizeAt + 4 * 256;
        private const int OthersAt = CodeSizeAt + 257, BitsAt = OthersAt + 257, TablesLength = BitsAt + 257;

        // a Huffman spec per table: 16 counts per length, then up to 256 symbols
        private const int SpecLength = 16 + 256;

        private struct Component
        {
            public int h, v, width, height, blocksX, blocksY, planeStart, table;
        }

        [InlineArray(3)]
        private struct Components
        {
            private Component element;
        }

        private static ReadOnlySpan<byte> Natural => new byte[]
        {
            0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
        };

        internal static void Encode(ReadOnlySpan<byte> pixels, int width, int height, int pitch, PixelLayout layout, Stream output, in JpegSettings settings)
        {
            bool grey = layout == PixelLayout.R8 || layout == PixelLayout.RG8;
            int count = grey ? 1 : 3;
            int hmax = grey || settings.Subsampling == ChromaSubsampling.S444 ? 1 : 2;
            int vmax = grey || settings.Subsampling != ChromaSubsampling.S420 ? 1 : 2;
            int mcusX = (width + 8 * hmax - 1) / (8 * hmax), mcusY = (height + 8 * vmax - 1) / (8 * vmax);
            Components comps = default;
            int planeBytes = 0, blockCount = 0;
            for (int i = 0; i < count; i++)
            {
                ref Component c = ref comps[i];
                c.h = i == 0 ? hmax : 1;
                c.v = i == 0 ? vmax : 1;
                c.width = mcusX * c.h * 8;
                c.height = mcusY * c.v * 8;
                c.blocksX = (width * c.h + 8 * hmax - 1) / (8 * hmax);
                c.blocksY = (height * c.v + 8 * vmax - 1) / (8 * vmax);
                c.planeStart = planeBytes;
                c.table = i == 0 ? 0 : 1;
                planeBytes += c.width * c.height;
                blockCount += mcusX * mcusY * c.h * c.v;
            }

            int tableCount = grey ? 1 : 2;
            byte[] planes = ArrayPool<byte>.Shared.Rent(planeBytes + count * comps[0].width * vmax);
            short[] coefficients = ArrayPool<short>.Shared.Rent(blockCount * 64);
            int[] tables = ArrayPool<int>.Shared.Rent(TablesLength);
            byte[] specs = ArrayPool<byte>.Shared.Rent(4 * SpecLength);
            byte[] chunk = ArrayPool<byte>.Shared.Rent(1 << 16);
            long[]? freq = null;
            try
            {
                Sample(pixels, width, height, pitch, layout, ref comps, count, vmax, planes, planeBytes);
                Quantisers(tables, settings.Quality, tableCount);
                TransformAll(planes, coefficients, tables, ref comps, count, mcusX, mcusY);
                if (settings.Optimize)
                {
                    freq = ArrayPool<long>.Shared.Rent(4 * 257);
                    Array.Clear(freq, 0, 4 * 257);
                    Writer none = default;
                    Scan(coefficients, ref comps, count, mcusX * mcusY, tables, freq, ref none);
                    for (int s = 0; s < 2 * tableCount; s++) Optimal(freq.AsSpan(s * 257, 257), tables, specs.AsSpan(s * SpecLength, SpecLength));
                }
                else
                {
                    for (int s = 0; s < 2 * tableCount; s++) Standard(s, specs.AsSpan(s * SpecLength, SpecLength));
                }
                for (int s = 0; s < 2 * tableCount; s++) Codes(specs.AsSpan(s * SpecLength, SpecLength), tables.AsSpan(CodeAt + s * 256, 256), tables.AsSpan(SizeAt + s * 256, 256));

                Writer w = new Writer(output, chunk);
                Headers(ref w, width, height, ref comps, count, tableCount, tables, specs);
                Scan(coefficients, ref comps, count, mcusX * mcusY, tables, null, ref w);
                w.Pad();
                w.Word(0xFFD9);
                w.Flush();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(planes);
                ArrayPool<short>.Shared.Return(coefficients);
                ArrayPool<int>.Shared.Return(tables);
                ArrayPool<byte>.Shared.Return(specs);
                ArrayPool<byte>.Shared.Return(chunk);
                if (freq != null) ArrayPool<long>.Shared.Return(freq);
            }
        }

        #region sampling
        // Converts one row group (vmax rows, the last image row repeated past the bottom, the last column past the right)
        // at a time, downsamples it into the planes, then repeats each plane's last row down to its height.
        private static void Sample(ReadOnlySpan<byte> pixels, int width, int height, int pitch, PixelLayout layout, ref Components comps, int count, int vmax, byte[] planes, int scratchAt)
        {
            int bpp = ImageCodec.BytesPerPixel(layout);
            int fullWidth = comps[0].width;
            int groupBytes = fullWidth * vmax;
            int red = layout == PixelLayout.BGRA8 ? 2 : 0, blue = 2 - red;
            int groups = (height + vmax - 1) / vmax;
            for (int g = 0; g < groups; g++)
            {
                for (int r = 0; r < vmax; r++)
                {
                    ReadOnlySpan<byte> source = pixels.Slice(Math.Min(g * vmax + r, height - 1) * pitch);
                    int row = scratchAt + r * fullWidth;
                    for (int x = 0; x < fullWidth; x++)
                    {
                        int at = Math.Min(x, width - 1) * bpp;
                        if (count == 1)
                        {
                            planes[row + x] = source[at];
                            continue;
                        }
                        int R = source[at + red], G = source[at + 1], B = source[at + blue];
                        planes[row + x] = (byte)((19595 * R + 38470 * G + 7471 * B + 32768) >> 16);
                        planes[row + groupBytes + x] = (byte)((-11059 * R - 21709 * G + 32768 * B + 8421375) >> 16);
                        planes[row + 2 * groupBytes + x] = (byte)((32768 * R - 27439 * G - 5329 * B + 8421375) >> 16);
                    }
                }
                for (int i = 0; i < count; i++)
                {
                    ref Component c = ref comps[i];
                    Downsample(planes, scratchAt + i * groupBytes, fullWidth, c.planeStart + g * c.v * c.width, c.width, fullWidth / c.width, vmax / c.v, c.v);
                }
            }
            for (int i = 0; i < count; i++)
            {
                ref Component c = ref comps[i];
                Span<byte> plane = planes.AsSpan(c.planeStart, c.width * c.height);
                ReadOnlySpan<byte> last = plane.Slice((groups * c.v - 1) * c.width, c.width);
                for (int y = groups * c.v; y < c.height; y++) last.CopyTo(plane.Slice(y * c.width));
            }
        }

        // libjpeg's h2v1 (bias 0, 1, ...) and h2v2 (bias 1, 2, ...) averages, or a copy at full size.
        private static void Downsample(byte[] planes, int input, int inputWidth, int output, int outputWidth, int fx, int fy, int rows)
        {
            for (int r = 0; r < rows; r++)
            {
                int top = input + r * fy * inputWidth, below = top + (fy - 1) * inputWidth, target = output + r * outputWidth;
                if (fx == 1)
                {
                    planes.AsSpan(top, outputWidth).CopyTo(planes.AsSpan(target));
                    continue;
                }
                int bias = fy == 1 ? 0 : 1, flip = fy == 1 ? 1 : 3;
                for (int x = 0; x < outputWidth; x++)
                {
                    int sum = planes[top + 2 * x] + planes[top + 2 * x + 1];
                    if (fy == 2) sum += planes[below + 2 * x] + planes[below + 2 * x + 1];
                    planes[target + x] = (byte)((sum + bias) >> fy);
                    bias ^= flip;
                }
            }
        }
        #endregion

        #region transform
        private static void Quantisers(int[] tables, int quality, int tableCount)
        {
            int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
            ReadOnlySpan<byte> basic = BasicQuantisers;
            for (int i = 0; i < tableCount * 64; i++) tables[QuantAt + i] = Math.Clamp((basic[i] * scale + 50) / 100, 1, 255);
        }

        // Every block in MCU order, coefficients quantised and in zig-zag order. Blocks past a component's edge are
        // libjpeg's dummies: no AC, and the DC of the block before them.
        private static void TransformAll(byte[] planes, short[] coefficients, int[] tables, ref Components comps, int count, int mcusX, int mcusY)
        {
            Span<int> ws = stackalloc int[64];
            int k = 0;
            for (int my = 0; my < mcusY; my++)
                for (int mx = 0; mx < mcusX; mx++)
                    for (int i = 0; i < count; i++)
                    {
                        ref Component c = ref comps[i];
                        ReadOnlySpan<int> quant = tables.AsSpan(QuantAt + c.table * 64, 64);
                        for (int by = 0; by < c.v; by++)
                            for (int bx = 0; bx < c.h; bx++, k++)
                            {
                                Span<short> block = coefficients.AsSpan(k * 64, 64);
                                int blockX = mx * c.h + bx, blockY = my * c.v + by;
                                if (blockY >= c.blocksY || blockX >= c.blocksX)
                                {
                                    block.Clear();
                                    block[0] = coefficients[(k - 1) * 64];
                                    continue;
                                }
                                Fdct(planes.AsSpan(c.planeStart + blockY * 8 * c.width + blockX * 8), c.width, ws);
                                Quantise(ws, quant, block);
                            }
                    }
        }

        // libjpeg's jfdctint (islow): output scaled up by 8.
        private static void Fdct(ReadOnlySpan<byte> samples, int stride, Span<int> ws)
        {
            for (int row = 0; row < 8; row++)
            {
                ReadOnlySpan<byte> s = samples.Slice(row * stride, 8);
                Span<int> d = ws.Slice(row * 8, 8);
                int tmp0 = s[0] + s[7] - 256, tmp7 = s[0] - s[7];
                int tmp1 = s[1] + s[6] - 256, tmp6 = s[1] - s[6];
                int tmp2 = s[2] + s[5] - 256, tmp5 = s[2] - s[5];
                int tmp3 = s[3] + s[4] - 256, tmp4 = s[3] - s[4];
                int tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;
                d[0] = (tmp10 + tmp11) << Pass1Bits;
                d[4] = (tmp10 - tmp11) << Pass1Bits;
                int z1 = (tmp12 + tmp13) * 4433;
                d[2] = Descale(z1 + tmp13 * 6270, ConstBits - Pass1Bits);
                d[6] = Descale(z1 - tmp12 * 15137, ConstBits - Pass1Bits);
                Odd(tmp4, tmp5, tmp6, tmp7, out d[1], out d[3], out d[5], out d[7], ConstBits - Pass1Bits);
            }
            for (int col = 0; col < 8; col++)
            {
                int tmp0 = ws[col] + ws[56 + col], tmp7 = ws[col] - ws[56 + col];
                int tmp1 = ws[8 + col] + ws[48 + col], tmp6 = ws[8 + col] - ws[48 + col];
                int tmp2 = ws[16 + col] + ws[40 + col], tmp5 = ws[16 + col] - ws[40 + col];
                int tmp3 = ws[24 + col] + ws[32 + col], tmp4 = ws[24 + col] - ws[32 + col];
                int tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;
                ws[col] = Descale(tmp10 + tmp11, Pass1Bits);
                ws[32 + col] = Descale(tmp10 - tmp11, Pass1Bits);
                int z1 = (tmp12 + tmp13) * 4433;
                ws[16 + col] = Descale(z1 + tmp13 * 6270, ConstBits + Pass1Bits);
                ws[48 + col] = Descale(z1 - tmp12 * 15137, ConstBits + Pass1Bits);
                Odd(tmp4, tmp5, tmp6, tmp7, out ws[8 + col], out ws[24 + col], out ws[40 + col], out ws[56 + col], ConstBits + Pass1Bits);
            }
        }

        private static void Odd(int tmp4, int tmp5, int tmp6, int tmp7, out int d1, out int d3, out int d5, out int d7, int shift)
        {
            int z1 = tmp4 + tmp7, z2 = tmp5 + tmp6, z3 = tmp4 + tmp6, z4 = tmp5 + tmp7;
            int z5 = (z3 + z4) * 9633;
            tmp4 *= 2446;
            tmp5 *= 16819;
            tmp6 *= 25172;
            tmp7 *= 12299;
            z1 *= -7373;
            z2 *= -20995;
            z3 = z3 * -16069 + z5;
            z4 = z4 * -3196 + z5;
            d7 = Descale(tmp4 + z1 + z3, shift);
            d5 = Descale(tmp5 + z2 + z4, shift);
            d3 = Descale(tmp6 + z2 + z3, shift);
            d1 = Descale(tmp7 + z1 + z4, shift);
        }

        private static int Descale(int x, int n) => (x + (1 << (n - 1))) >> n;

        // Rounds |x| / 8q to nearest, half away from zero, into zig-zag order.
        private static void Quantise(ReadOnlySpan<int> ws, ReadOnlySpan<int> quant, Span<short> block)
        {
            ReadOnlySpan<byte> natural = Natural;
            for (int z = 0; z < 64; z++)
            {
                int n = natural[z], d = quant[n] << 3, x = ws[n];
                block[z] = (short)(x < 0 ? -((-x + (d >> 1)) / d) : (x + (d >> 1)) / d);
            }
        }
        #endregion

        #region entropy coding
        // Walks the blocks in order: counts symbols into freq when it is given, writes them otherwise.
        private static void Scan(short[] coefficients, ref Components comps, int count, int mcus, int[] tables, long[]? freq, ref Writer w)
        {
            Span<int> pred = stackalloc int[3];
            pred.Clear();
            int k = 0;
            for (int m = 0; m < mcus; m++)
                for (int i = 0; i < count; i++)
                {
                    ref Component c = ref comps[i];
                    int dc = 2 * c.table, ac = dc + 1;
                    for (int b = c.h * c.v; b > 0; b--, k++)
                    {
                        ReadOnlySpan<short> block = coefficients.AsSpan(k * 64, 64);
                        int diff = block[0] - pred[i];
                        pred[i] = block[0];
                        Symbol(dc, Length(diff), diff, tables, freq, ref w);
                        int run = 0;
                        for (int z = 1; z < 64; z++)
                        {
                            int v = block[z];
                            if (v == 0)
                            {
                                run++;
                                continue;
                            }
                            for (; run > 15; run -= 16) Symbol(ac, 0xF0, 0, tables, freq, ref w);
                            int length = Length(v);
                            Symbol(ac, (run << 4) | length, v, tables, freq, ref w);
                            run = 0;
                        }
                        if (run > 0) Symbol(ac, 0, 0, tables, freq, ref w);
                    }
                }
        }

        // A symbol's code, then the low (symbol & 15) bits of the value, one less when it is negative.
        private static void Symbol(int table, int symbol, int value, int[] tables, long[]? freq, ref Writer w)
        {
            if (freq != null)
            {
                freq[table * 257 + symbol]++;
                return;
            }
            w.Put(tables[CodeAt + table * 256 + symbol], tables[SizeAt + table * 256 + symbol]);
            int extra = symbol & 15;
            if (extra != 0) w.Put((value < 0 ? value - 1 : value) & ((1 << extra) - 1), extra);
        }

        private static int Length(int v) => v == 0 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)Math.Abs(v));

        private static void Standard(int s, Span<byte> spec)
        {
            ReadOnlySpan<byte> counts = s switch { 0 => DcLuminanceCounts, 1 => AcLuminanceCounts, 2 => DcChrominanceCounts, _ => AcChrominanceCounts };
            ReadOnlySpan<byte> values = (s & 1) == 0 ? DcValues : s == 1 ? AcLuminanceValues : AcChrominanceValues;
            counts.CopyTo(spec);
            values.CopyTo(spec.Slice(16));
        }

        // libjpeg's jpeg_gen_optimal_table (T.81 K.2), with a pseudo-symbol 256 so no code is all ones.
        private static void Optimal(Span<long> freq, int[] tables, Span<byte> spec)
        {
            Span<int> codeSize = tables.AsSpan(CodeSizeAt, 257), others = tables.AsSpan(OthersAt, 257), bits = tables.AsSpan(BitsAt, 257);
            codeSize.Clear();
            bits.Clear();
            others.Fill(-1);
            freq[256] = 1;
            while (true)
            {
                int c1 = -1, c2 = -1;
                long v = 1000000000L;
                for (int i = 0; i <= 256; i++)
                    if (freq[i] != 0 && freq[i] <= v)
                    {
                        v = freq[i];
                        c1 = i;
                    }
                v = 1000000000L;
                for (int i = 0; i <= 256; i++)
                    if (freq[i] != 0 && freq[i] <= v && i != c1)
                    {
                        v = freq[i];
                        c2 = i;
                    }
                if (c2 < 0) break;
                freq[c1] += freq[c2];
                freq[c2] = 0;
                codeSize[c1]++;
                while (others[c1] >= 0)
                {
                    c1 = others[c1];
                    codeSize[c1]++;
                }
                others[c1] = c2;
                codeSize[c2]++;
                while (others[c2] >= 0)
                {
                    c2 = others[c2];
                    codeSize[c2]++;
                }
            }

            int symbols = 0;
            for (int i = 0; i <= 256; i++)
            {
                if (codeSize[i] == 0) continue;
                bits[codeSize[i]]++;
                if (i < 256) symbols++;
            }
            // lengths past 16 move up the tree in pairs
            for (int i = 256; i > 16; i--)
                while (bits[i] > 0)
                {
                    int j = i - 2;
                    while (bits[j] == 0) j--;
                    bits[i] -= 2;
                    bits[i - 1]++;
                    bits[j + 1] += 2;
                    bits[j]--;
                }
            int longest = 16;
            while (bits[longest] == 0) longest--;
            bits[longest]--;
            for (int l = 1; l <= 16; l++) spec[l - 1] = (byte)bits[l];
            int p = 16;
            for (int l = 1; p < 16 + symbols; l++)
                for (int j = 0; j < 256; j++)
                    if (codeSize[j] == l) spec[p++] = (byte)j;
        }

        // Canonical codes from a spec's counts per length.
        private static void Codes(ReadOnlySpan<byte> spec, Span<int> code, Span<int> size)
        {
            int next = 0, p = 16;
            for (int l = 1; l <= 16; l++, next <<= 1)
                for (int n = spec[l - 1]; n > 0; n--, p++)
                {
                    code[spec[p]] = next++;
                    size[spec[p]] = l;
                }
        }

        private static void Headers(ref Writer w, int width, int height, ref Components comps, int count, int tableCount, int[] tables, byte[] specs)
        {
            w.Word(0xFFD8);
            ReadOnlySpan<byte> jfif = new byte[] { 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 };
            for (int i = 0; i < jfif.Length; i++) w.Byte(jfif[i]);

            ReadOnlySpan<byte> natural = Natural;
            for (int t = 0; t < tableCount; t++)
            {
                w.Word(0xFFDB);
                w.Word(67);
                w.Byte(t);
                for (int z = 0; z < 64; z++) w.Byte(tables[QuantAt + t * 64 + natural[z]]);
            }

            w.Word(0xFFC0);
            w.Word(8 + 3 * count);
            w.Byte(8);
            w.Word(height);
            w.Word(width);
            w.Byte(count);
            for (int i = 0; i < count; i++)
            {
                w.Byte(i + 1);
                w.Byte(comps[i].h << 4 | comps[i].v);
                w.Byte(comps[i].table);
            }

            for (int s = 0; s < 2 * tableCount; s++)
            {
                ReadOnlySpan<byte> spec = specs.AsSpan(s * SpecLength, SpecLength);
                int n = 0;
                for (int l = 0; l < 16; l++) n += spec[l];
                w.Word(0xFFC4);
                w.Word(19 + n);
                w.Byte((s & 1) << 4 | s >> 1);
                for (int i = 0; i < 16 + n; i++) w.Byte(spec[i]);
            }

            w.Word(0xFFDA);
            w.Word(6 + 2 * count);
            w.Byte(count);
            for (int i = 0; i < count; i++)
            {
                w.Byte(i + 1);
                w.Byte(comps[i].table << 4 | comps[i].table);
            }
            w.Byte(0);
            w.Byte(63);
            w.Byte(0);
        }

        // Bytes through a pooled chunk to the stream; entropy bits MSB first, a 0xFF followed by a stuffed 0x00.
        private ref struct Writer
        {
            private readonly Stream output;
            private readonly byte[] chunk;
            private int at, count;
            private ulong bits;

            public Writer(Stream output, byte[] chunk)
            {
                this.output = output;
                this.chunk = chunk;
            }

            public void Byte(int b)
            {
                chunk[at++] = (byte)b;
                if (at == chunk.Length) Flush();
            }

            public void Word(int w)
            {
                Byte(w >> 8);
                Byte(w);
            }

            public void Put(int code, int size)
            {
                bits = bits << size | (uint)code;
                count += size;
                while (count >= 8)
                {
                    count -= 8;
                    int b = (int)(bits >> count) & 0xFF;
                    Byte(b);
                    if (b == 0xFF) Byte(0);
                }
            }

            // Fills the last byte with one bits.
            public void Pad()
            {
                if ((count & 7) != 0) Put((1 << (8 - count)) - 1, 8 - count);
            }

            public void Flush()
            {
                output.Write(chunk, 0, at);
                at = 0;
            }
        }
        #endregion

        #region standard tables
        // T.81 Annex K: luminance then chrominance quantisers in natural order, and the four Huffman specs
        private static ReadOnlySpan<byte> BasicQuantisers => new byte[]
        {
            16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
            18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
            17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99
        };

        private static ReadOnlySpan<byte> DcLuminanceCounts => new byte[] { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
        private static ReadOnlySpan<byte> DcChrominanceCounts => new byte[] { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
        private static ReadOnlySpan<byte> DcValues => new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        private static ReadOnlySpan<byte> AcLuminanceCounts => new byte[] { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D };
        private static ReadOnlySpan<byte> AcChrominanceCounts => new byte[] { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };

        private static ReadOnlySpan<byte> AcLuminanceValues => new byte[]
        {
            0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
            0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
            0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
            0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
            0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
            0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
            0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
            0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
            0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
            0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
            0xF9, 0xFA
        };

        private static ReadOnlySpan<byte> AcChrominanceValues => new byte[]
        {
            0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
            0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
            0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
            0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
            0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
            0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
            0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
            0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
            0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
            0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
            0xF9, 0xFA
        };
        #endregion
    }
}
