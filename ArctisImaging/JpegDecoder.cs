using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Arctis.Imaging
{
    // JPEG (T.81) baseline, extended and progressive Huffman at 8 bits, grey, three-component or CMYK, decoded the way
    // libjpeg-turbo decodes it: islow IDCT, fancy upsampling, its fixed-point YCbCr formulas.
    internal static partial class JpegDecoder
    {
        private const int ConstBits = 13, Pass1Bits = 2, FastBits = 9, WholeBits = 11;
        private const int DcFirst = 1, DcRefine = 2, AcFirst = 3, AcRefine = 4;
        private const int Full = 0, Replicate = 1, FancyH2V1 = 2, FancyH2V2 = 3, FancyH1V2 = 4;

        // natural index of each zig-zag position, padded so a corrupt run past 63 lands on 63
        private static ReadOnlySpan<byte> Natural => new byte[]
        {
            0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
            63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63
        };

        // one pooled run: 9-bit symbol lookups (length << 8 | symbol) for DC tables 0-3 and AC tables 4-7, whole-coefficient
        // lookups for the AC tables (value << 16 | run << 8 | length), libjpeg's maxcode and valoffset, symbols, quantisers
        private const int SymbolFastAt = 0, AcFastAt = 8 << FastBits, MaxCodeAt = AcFastAt + (4 << WholeBits), OffsetAt = MaxCodeAt + 8 * 18;
        private const int ValuesAt = OffsetAt + 8 * 17, QuantAt = ValuesAt + 8 * 256, TablesLength = QuantAt + 4 * 64;

        // a sequential scan of at least this many blocks and bits per block refills unconditionally
        private const int DenseScanBlocks = 4096, DenseScanBits = 15;

        private struct Component
        {
            public int id, h, v, quant, dc, ac, pred, width, height, blocksX, blocksY, planeStart, coefStart, upsample;
            public bool scanned;
        }

        [InlineArray(4)]
        private struct Components
        {
            private Component element;
        }

        private struct Frame
        {
            public int width, height, hmax, vmax, mcusX, mcusY, count, restartInterval, adobeTransform, ycc, planeBytes, coefCount;
            public bool progressive, jfif, seen;
            public Components components;
            public int[] tables;
            public byte[] planes;
            public short[] coefficients;
            public ulong[] nonzero;
            public byte[] unstuffed;

            // lossless (SOF3): one sample plane per component
            public bool lossless;
            public int precision, pointTransform;
            public ushort[] samples;
        }

        internal static bool TryReadHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return false;
            int i = 2;
            while (i + 4 <= data.Length)
            {
                if (data[i] != 0xFF) return false;
                byte marker = data[i + 1];
                if (marker == 0xFF)
                {
                    i++;
                    continue;
                }
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    i += 2;
                    continue;
                }
                if (marker == 0xD9 || marker == 0xDA) return false;
                int length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 2));
                if (length < 2) return false;
                bool isFrame = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (isFrame || marker == 0xF7)
                {
                    if (i + 10 > data.Length) return false;
                    int precision = data[i + 4];
                    int height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 5));
                    int width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 7));
                    int components = data[i + 9];
                    header = isFrame ? new ImageHeader(ImageFormat.Jpeg, width, height, components, precision, false)
                        : new ImageHeader(ImageFormat.JpegLs, width, height, components, precision, components == 2 || components == 4);
                    return true;
                }
                i += 2 + length;
            }
            return false;
        }

        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int pitch, PixelLayout layout) => Decode(data, default, destination, pitch, layout, -1);

        // A TIFF strip or tile: tables from its JPEGTables first, colour space from the TIFF rather than the markers.
        internal static void DecodeAbbreviated(ReadOnlySpan<byte> data, ReadOnlySpan<byte> tables, Span<byte> destination, int pitch, PixelLayout layout, bool ycc) =>
            Decode(data, tables, destination, pitch, layout, ycc ? 1 : 0);

        // Reads the segments in order, decoding each scan as it comes; a file cut short decodes what it holds.
        private static void Decode(ReadOnlySpan<byte> data, ReadOnlySpan<byte> tables, Span<byte> destination, int pitch, PixelLayout layout, int ycc)
        {
            Frame f = new Frame
            {
                adobeTransform = -1, ycc = ycc, tables = ArrayPool<int>.Shared.Rent(TablesLength),
                unstuffed = ArrayPool<byte>.Shared.Rent(Math.Max(data.Length, tables.Length) + 8)
            };
            try
            {
                f.tables.AsSpan(0, TablesLength).Clear();
                f.tables.AsSpan(MaxCodeAt, 8 * 18).Fill(-1);
                if (tables.Length > 0) ReadSegments(tables, ref f);
                ReadSegments(data, ref f);
                if (!f.seen) throw new ImageFormatException("JPEG has no frame");
                if (f.progressive) TransformAll(ref f);
                if (f.lossless) OutputLossless(in f, destination, pitch, layout);
                else Output(ref f, destination, pitch, layout);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(f.tables);
                ArrayPool<byte>.Shared.Return(f.unstuffed);
                if (f.samples != null) ArrayPool<ushort>.Shared.Return(f.samples);
                if (f.planes != null) ArrayPool<byte>.Shared.Return(f.planes);
                if (f.coefficients != null) ArrayPool<short>.Shared.Return(f.coefficients);
                if (f.nonzero != null) ArrayPool<ulong>.Shared.Return(f.nonzero);
            }
        }

        #region segments
        private static void ReadSegments(ReadOnlySpan<byte> data, ref Frame f)
        {
            int pos = 2;
            while (pos + 4 <= data.Length)
            {
                byte marker = data[pos + 1];
                if (data[pos] != 0xFF || marker == 0xFF || marker == 0)
                {
                    pos++;
                    continue;
                }
                pos += 2;
                if (marker == 0xD9) break;
                if ((marker >= 0xD0 && marker <= 0xD8) || marker == 0x01) continue;
                int length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos));
                if (length < 2 || pos + length > data.Length) throw new ImageFormatException("JPEG segment runs past the end");
                ReadOnlySpan<byte> segment = data.Slice(pos + 2, length - 2);
                switch (marker)
                {
                    case 0xC0:
                    case 0xC1:
                    case 0xC2:
                        ReadFrame(segment, ref f, marker == 0xC2);
                        break;
                    case 0xC3: ReadLosslessFrame(segment, ref f); break;
                    case 0xC4: ReadHuffman(segment, f.tables); break;
                    case 0xDB: ReadQuant(segment, f.tables); break;
                    case 0xDD:
                        if (segment.Length < 2) throw new ImageFormatException("JPEG restart interval is truncated");
                        f.restartInterval = BinaryPrimitives.ReadUInt16BigEndian(segment);
                        break;
                    case 0xE0:
                        f.jfif |= segment.Length >= 5 && segment[0] == 'J' && segment[1] == 'F' && segment[2] == 'I' && segment[3] == 'F' && segment[4] == 0;
                        break;
                    case 0xEE:
                        if (segment.Length >= 12 && segment[0] == 'A' && segment[1] == 'd' && segment[2] == 'o' && segment[3] == 'b' && segment[4] == 'e')
                            f.adobeTransform = segment[11];
                        break;
                    case 0xDA:
                        pos = ReadScan(data, pos + length, segment, ref f);
                        continue;
                    default:
                        if (marker >= 0xC3 && marker <= 0xCF && marker != 0xC8 && marker != 0xCC)
                            throw new NotSupportedException($"JPEG frame type 0x{marker:X2} (lossless, hierarchical or arithmetic) is not available");
                        break;
                }
                pos += length;
            }
        }

        private static void ReadFrame(ReadOnlySpan<byte> segment, ref Frame f, bool progressive)
        {
            if (f.seen) throw new ImageFormatException("JPEG has more than one frame");
            if (segment.Length < 6) throw new ImageFormatException("JPEG frame header is truncated");
            if (segment[0] != 8) throw new NotSupportedException($"{segment[0]}-bit JPEG is not available");
            f.height = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(1));
            f.width = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(3));
            f.count = segment[5];
            if (f.width == 0 || f.height == 0) throw new ImageFormatException("JPEG frame has no size");
            if (f.count != 1 && f.count != 3 && f.count != 4) throw new NotSupportedException($"{f.count}-component JPEG is not available");
            if (segment.Length < 6 + f.count * 3) throw new ImageFormatException("JPEG frame header is truncated");

            Span<Component> components = f.components;
            f.hmax = 1;
            f.vmax = 1;
            for (int i = 0; i < f.count; i++)
            {
                ref Component c = ref components[i];
                c.id = segment[6 + i * 3];
                c.h = segment[7 + i * 3] >> 4;
                c.v = segment[7 + i * 3] & 15;
                c.quant = segment[8 + i * 3];
                if (c.h < 1 || c.h > 4 || c.v < 1 || c.v > 4 || c.quant > 3) throw new ImageFormatException("JPEG component sampling or table is out of range");
                f.hmax = Math.Max(f.hmax, c.h);
                f.vmax = Math.Max(f.vmax, c.v);
            }
            f.mcusX = (f.width + 8 * f.hmax - 1) / (8 * f.hmax);
            f.mcusY = (f.height + 8 * f.vmax - 1) / (8 * f.vmax);

            int planeBytes = 0, coefCount = 0;
            for (int i = 0; i < f.count; i++)
            {
                ref Component c = ref components[i];
                if (f.hmax % c.h != 0 || f.vmax % c.v != 0) throw new NotSupportedException("JPEG with non-integral sampling ratios is not available");
                c.width = (f.width * c.h + f.hmax - 1) / f.hmax;
                c.height = (f.height * c.v + f.vmax - 1) / f.vmax;
                c.blocksX = f.mcusX * c.h;
                c.blocksY = f.mcusY * c.v;
                c.planeStart = planeBytes;
                c.coefStart = coefCount;
                planeBytes += c.blocksX * c.blocksY * 64;
                coefCount += c.blocksX * c.blocksY * 64;
                int hRatio = f.hmax / c.h, vRatio = f.vmax / c.v;
                c.upsample = hRatio == 1 && vRatio == 1 ? Full
                    : hRatio == 2 && vRatio == 1 && c.width > 2 ? FancyH2V1
                    : hRatio == 2 && vRatio == 2 && c.width > 2 ? FancyH2V2
                    : hRatio == 1 && vRatio == 2 ? FancyH1V2 : Replicate;
            }

            f.planes = ArrayPool<byte>.Shared.Rent(planeBytes);
            f.planeBytes = planeBytes;
            if (progressive)
            {
                f.coefficients = ArrayPool<short>.Shared.Rent(coefCount);
                f.coefficients.AsSpan(0, coefCount).Clear();
                f.coefCount = coefCount;
                f.nonzero = ArrayPool<ulong>.Shared.Rent(coefCount >> 6);
                f.nonzero.AsSpan(0, coefCount >> 6).Clear();
            }
            f.progressive = progressive;
            f.seen = true;
        }

        private static void ReadQuant(ReadOnlySpan<byte> segment, int[] tables)
        {
            int i = 0;
            while (i < segment.Length)
            {
                int precision = segment[i] >> 4, table = segment[i] & 15;
                int size = precision == 0 ? 64 : 128;
                if (table > 3 || precision > 1 || i + 1 + size > segment.Length) throw new ImageFormatException("JPEG quantisation table is malformed");
                ReadOnlySpan<byte> values = segment.Slice(i + 1, size);
                for (int k = 0; k < 64; k++)
                    tables[QuantAt + table * 64 + Natural[k]] = precision == 0 ? values[k] : BinaryPrimitives.ReadUInt16BigEndian(values.Slice(k * 2));
                i += 1 + size;
            }
        }

        private static void ReadHuffman(ReadOnlySpan<byte> segment, int[] tables)
        {
            int i = 0;
            while (i < segment.Length)
            {
                int tableClass = segment[i] >> 4, id = segment[i] & 15;
                if (tableClass > 1 || id > 3 || i + 17 > segment.Length) throw new ImageFormatException("JPEG Huffman table is malformed");
                ReadOnlySpan<byte> counts = segment.Slice(i + 1, 16);
                int total = 0;
                for (int l = 0; l < 16; l++) total += counts[l];
                if (total > 256 || i + 17 + total > segment.Length) throw new ImageFormatException("JPEG Huffman table is malformed");
                BuildTable(tables, tableClass * 4 + id, counts, segment.Slice(i + 17, total));
                i += 17 + total;
            }
        }

        private static void BuildTable(int[] tables, int t, ReadOnlySpan<byte> counts, ReadOnlySpan<byte> symbols)
        {
            Span<int> fast = tables.AsSpan(SymbolFastAt + (t << FastBits), 1 << FastBits);
            fast.Clear();

            // an AC code whose value bits also fit in the whole-coefficient lookup decodes in one step
            Span<int> whole = t < 4 ? default : tables.AsSpan(AcFastAt + ((t - 4) << WholeBits), 1 << WholeBits);
            whole.Clear();
            int code = 0, p = 0;
            for (int l = 1; l <= 16; l++)
            {
                int n = counts[l - 1];
                if (code + n > 1 << l) throw new ImageFormatException("JPEG Huffman code lengths overflow");
                tables[OffsetAt + t * 17 + l] = p - code;
                for (int i = 0; i < n; i++, p++, code++)
                {
                    int symbol = symbols[p];
                    if (l <= FastBits) fast.Slice(code << (FastBits - l), 1 << (FastBits - l)).Fill(l << 8 | symbol);
                    int size = symbol & 15;
                    if (whole.Length == 0 || size == 0 || l + size > WholeBits) continue;
                    int free = WholeBits - l - size;
                    for (int bits = 0; bits < 1 << size; bits++)
                    {
                        int value = bits < 1 << (size - 1) ? bits + (-1 << size) + 1 : bits;
                        whole.Slice(code << (WholeBits - l) | bits << free, 1 << free).Fill(value << 16 | (symbol >> 4) << 8 | (l + size));
                    }
                }
                tables[MaxCodeAt + t * 18 + l] = n > 0 ? code - 1 : -1;
                code <<= 1;
            }
            for (int i = 0; i < symbols.Length; i++) tables[ValuesAt + t * 256 + i] = symbols[i];
        }

        // Reads a scan header and decodes the entropy data after it; returns where the next marker starts.
        private static int ReadScan(ReadOnlySpan<byte> data, int start, ReadOnlySpan<byte> segment, ref Frame f)
        {
            if (!f.seen) throw new ImageFormatException("JPEG scan comes before its frame");
            int count = segment.Length > 0 ? segment[0] : 0;
            if (count < 1 || count > f.count || segment.Length < 4 + count * 2) throw new ImageFormatException("JPEG scan header is malformed");
            Span<int> scan = stackalloc int[4];
            Span<Component> components = f.components;
            for (int i = 0; i < count; i++)
            {
                int id = segment[1 + i * 2], tables = segment[2 + i * 2];
                int index = -1;
                for (int c = 0; c < f.count; c++)
                    if (components[c].id == id) index = c;
                if (index < 0 || tables >> 4 > 3 || (tables & 15) > 3) throw new ImageFormatException("JPEG scan names an unknown component or table");
                components[index].dc = tables >> 4;
                components[index].ac = tables & 15;
                components[index].scanned = true;
                scan[i] = index;
            }
            int ss = segment[1 + count * 2], se = segment[2 + count * 2], ah = segment[3 + count * 2] >> 4, al = segment[3 + count * 2] & 15;
            if (f.lossless) return DecodeLossless(data, start, ref f, scan.Slice(0, count), ss, al);
            if (!f.progressive)
            {
                ss = 0;
                se = 63;
            }
            else if (ss == 0 ? se != 0 : se < ss || se > 63 || count != 1) throw new ImageFormatException("JPEG progressive scan is malformed");
            if (!f.progressive) return DecodeSequential(data, start, ref f, scan.Slice(0, count));
            return DecodeProgressive(data, start, ref f, scan.Slice(0, count), ss, se, ah, al);
        }
        #endregion

        #region entropy decoding
        // A sequential scan: each block is decoded, transformed and written to its plane at once.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static int DecodeSequential(ReadOnlySpan<byte> data, int start, ref Frame f, ReadOnlySpan<int> scan)
        {
            Bits bits = new Bits(data, start, f.unstuffed);
            Span<short> block = stackalloc short[64];
            Span<int> workspace = stackalloc int[64];
            block.Clear();
            Span<Component> components = f.components;
            ref int tables = ref MemoryMarshal.GetArrayDataReference(f.tables);
            byte[] planes = f.planes;
            for (int i = 0; i < scan.Length; i++) components[scan[i]].pred = 0;

            bool single = scan.Length == 1;
            int mcusX = single ? (components[scan[0]].width + 7) >> 3 : f.mcusX;
            int mcusY = single ? (components[scan[0]].height + 7) >> 3 : f.mcusY;
            int interval = f.restartInterval, todo = interval;

            // a large scan dense enough over all its bytes refills on every Ensure
            int perMcu = 1;
            if (!single)
            {
                perMcu = 0;
                for (int i = 0; i < scan.Length; i++) perMcu += components[scan[i]].h * components[scan[i]].v;
            }
            long blocks = (long)mcusX * mcusY * perMcu;
            if (blocks >= DenseScanBlocks)
            {
                long bytes = interval > 0 ? ScanEnd(data, start) - start : bits.Length;
                bits.always = bytes * 8 >= DenseScanBits * blocks;
            }
            for (int my = 0; my < mcusY; my++)
            {
                for (int mx = 0; mx < mcusX; mx++)
                {
                    if (interval > 0)
                    {
                        if (todo == 0)
                        {
                            bits.Restart();
                            for (int i = 0; i < scan.Length; i++) components[scan[i]].pred = 0;
                            todo = interval;
                        }
                        todo--;
                    }
                    for (int i = 0; i < scan.Length; i++)
                    {
                        ref Component c = ref components[scan[i]];
                        int across = single ? 1 : c.h, down = single ? 1 : c.v;
                        for (int v = 0; v < down; v++)
                            for (int h = 0; h < across; h++)
                                SequentialBlock(ref bits, ref tables, ref c, planes, mx * across + h, my * down + v, block, workspace);
                    }
                }
            }
            return NextMarker(data, bits.pos);
        }

        // One block: DC, then AC through the whole-coefficient lookup or symbol by symbol; a block with no AC is a fill.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SequentialBlock(ref Bits bits, ref int tables, ref Component c, byte[] planes, int bx, int by, scoped Span<short> block, scoped Span<int> workspace)
        {
            ref short coef = ref MemoryMarshal.GetReference(block);
            ref byte natural = ref MemoryMarshal.GetReference(Natural);
            bits.Ensure();
            int t = bits.Symbol(ref tables, c.dc);
            if (t > 16) throw new ImageFormatException("JPEG DC difference is out of range");
            c.pred += bits.Receive(t);
            coef = (short)c.pred;

            ref int whole = ref Unsafe.Add(ref tables, AcFastAt + (c.ac << WholeBits));
            int acTable = 4 + c.ac;
            int stride = c.blocksX * 8;
            Span<byte> output = planes.AsSpan(c.planeStart + by * 8 * stride + bx * 8);
            ReadOnlySpan<int> quant = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref tables, QuantAt + c.quant * 64), 64);

            // an end of block straight after the DC
            bits.Ensure();
            int first = Unsafe.Add(ref tables, SymbolFastAt + (acTable << FastBits) + bits.Peek(FastBits));
            if (first != 0 && (first & 0xFF) == 0)
            {
                bits.Skip(first >> 8);
                FillBlock(output, stride, DcOnly(c.pred, quant[0]));
                coef = 0;
                return;
            }

            for (int k = 1; k < 64; k++)
            {
                bits.Ensure();
                int fast = Unsafe.Add(ref whole, bits.Peek(WholeBits));
                if (fast != 0)
                {
                    k += (fast >> 8) & 15;
                    bits.Skip(fast & 0xFF);
                    Unsafe.Add(ref coef, Unsafe.Add(ref natural, k)) = (short)(fast >> 16);
                    continue;
                }
                int rs = bits.Symbol(ref tables, acTable);
                int r = rs >> 4, s = rs & 15;
                if (s == 0)
                {
                    if (r != 15) break;
                    k += 15;
                    continue;
                }
                k += r;
                Unsafe.Add(ref coef, Unsafe.Add(ref natural, k)) = (short)bits.Receive(s);
            }
            Transform(block, quant, output, stride, workspace);
            block.Clear();
        }

        // A progressive scan: coefficients accumulate in the store and are transformed once every scan is in.
        private static int DecodeProgressive(ReadOnlySpan<byte> data, int start, ref Frame f, ReadOnlySpan<int> scan, int ss, int se, int ah, int al)
        {
            if (ss == 0) return ah == 0 ? ProgressiveScan(data, start, ref f, scan, ss, se, al, DcFirst) : ProgressiveScan(data, start, ref f, scan, ss, se, al, DcRefine);
            return ah == 0 ? ProgressiveScan(data, start, ref f, scan, ss, se, al, AcFirst) : ProgressiveScan(data, start, ref f, scan, ss, se, al, AcRefine);
        }

        // Inlined once per mode, so each copy carries only its own block decoder.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ProgressiveScan(ReadOnlySpan<byte> data, int start, ref Frame f, ReadOnlySpan<int> scan, int ss, int se, int al, int mode)
        {
            Bits bits = new Bits(data, start, f.unstuffed);
            Span<Component> components = f.components;
            ref int tables = ref MemoryMarshal.GetArrayDataReference(f.tables);
            short[] coefficients = f.coefficients;
            ulong[] nonzero = f.nonzero;
            for (int i = 0; i < scan.Length; i++) components[scan[i]].pred = 0;

            // one component is coded block by block over its own size; several go MCU by MCU
            bool single = scan.Length == 1;
            int mcusX = single ? (components[scan[0]].width + 7) >> 3 : f.mcusX;
            int mcusY = single ? (components[scan[0]].height + 7) >> 3 : f.mcusY;
            int interval = f.restartInterval, todo = interval, eobrun = 0;
            for (int my = 0; my < mcusY; my++)
            {
                for (int mx = 0; mx < mcusX; mx++)
                {
                    if (interval > 0)
                    {
                        if (todo == 0)
                        {
                            bits.Restart();
                            for (int i = 0; i < scan.Length; i++) components[scan[i]].pred = 0;
                            eobrun = 0;
                            todo = interval;
                        }
                        todo--;
                    }
                    for (int i = 0; i < scan.Length; i++)
                    {
                        ref Component c = ref components[scan[i]];
                        if (single)
                        {
                            ProgressiveBlock(ref bits, ref tables, ref c, coefficients, nonzero, mx, my, mode, ss, se, al, ref eobrun);
                            continue;
                        }
                        for (int v = 0; v < c.v; v++)
                            for (int h = 0; h < c.h; h++)
                                ProgressiveBlock(ref bits, ref tables, ref c, coefficients, nonzero, mx * c.h + h, my * c.v + v, mode, ss, se, al, ref eobrun);
                    }
                }
            }
            return NextMarker(data, bits.pos);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ProgressiveBlock(ref Bits bits, ref int tables, ref Component c, short[] coefficients, ulong[] nonzero, int bx, int by, int mode, int ss, int se, int al,
            ref int eobrun)
        {
            int index = (c.coefStart >> 6) + by * c.blocksX + bx;
            Span<short> block = coefficients.AsSpan(index << 6, 64);
            ref ulong mask = ref nonzero[index];
            switch (mode)
            {
                case DcFirst:
                    bits.Ensure();
                    int dc = bits.Symbol(ref tables, c.dc);
                    if (dc > 16) throw new ImageFormatException("JPEG DC difference is out of range");
                    c.pred += bits.Receive(dc);
                    block[0] = (short)(c.pred << al);
                    break;
                case DcRefine:
                    if (bits.GetBits(1) != 0) block[0] |= (short)(1 << al);
                    break;
                case AcFirst:
                    if (eobrun > 0)
                    {
                        eobrun--;
                        break;
                    }
                    ref int whole = ref Unsafe.Add(ref tables, AcFastAt + (c.ac << WholeBits));
                    for (int k = ss; k <= se; k++)
                    {
                        bits.Ensure();
                        int fast = Unsafe.Add(ref whole, bits.Peek(WholeBits));
                        if (fast != 0)
                        {
                            k += (fast >> 8) & 15;
                            bits.Skip(fast & 0xFF);
                            block[Natural[k]] = (short)((fast >> 16) << al);
                            mask |= 1UL << Math.Min(k, 63);
                            continue;
                        }
                        int rs = bits.Symbol(ref tables, 4 + c.ac);
                        int r = rs >> 4, s = rs & 15;
                        if (s != 0)
                        {
                            k += r;
                            block[Natural[k]] = (short)(bits.Receive(s) << al);
                            mask |= 1UL << Math.Min(k, 63);
                        }
                        else if (r == 15) k += 15;
                        else
                        {
                            eobrun = (1 << r) + (r != 0 ? bits.GetBits(r) : 0) - 1;
                            break;
                        }
                    }
                    break;
                case AcRefine:
                    RefineAc(ref bits, ref tables, 4 + c.ac, block, ref mask, ss, se, al, ref eobrun);
                    break;
            }
        }

        // libjpeg's decode_mcu_AC_refine: correction bits for nonzero coefficients, new ones of magnitude 1 << al.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RefineAc(ref Bits bits, ref int tables, int table, Span<short> block, ref ulong mask, int ss, int se, int al, ref int eobrun)
        {
            int p1 = 1 << al, m1 = -1 << al;
            int k = ss;
            if (eobrun == 0)
            {
                ulong nonzero = mask;
                while (k <= se)
                {
                    bits.Ensure();
                    int rs = bits.Symbol(ref tables, table);
                    int r = rs >> 4, s = rs & 15;
                    if (s != 0) s = bits.GetBits(1) != 0 ? p1 : m1;
                    else if (r != 15)
                    {
                        eobrun = (1 << r) + (r != 0 ? bits.GetBits(r) : 0);
                        break;
                    }

                    // the zero-history position r further on, correcting the nonzero ones passed on the way
                    ulong zeros = ~nonzero & Band(k, se);
                    for (; r > 0 && zeros != 0; r--) zeros &= zeros - 1;
                    int target = zeros != 0 ? BitOperations.TrailingZeroCount(zeros) : se + 1;
                    if (target > k) Correct(ref bits, block, nonzero & Band(k, target - 1), p1, m1);
                    k = target;
                    if (s != 0)
                    {
                        block[Natural[k]] = (short)s;
                        nonzero |= 1UL << Math.Min(k, 63);
                    }
                    k++;
                }
                mask = nonzero;
            }
            if (eobrun > 0)
            {
                if (k <= se) Correct(ref bits, block, mask & Band(k, se), p1, m1);
                eobrun--;
            }
        }

        // Correction bits for the pending coefficients in zig-zag order, up to sixteen per read.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Correct(ref Bits bits, Span<short> block, ulong pending, int p1, int m1)
        {
            while (pending != 0)
            {
                int n = Math.Min(BitOperations.PopCount(pending), 16);
                int v = bits.GetBits(n) << (32 - n);
                for (int i = 0; i < n; i++, v <<= 1)
                {
                    ref short coef = ref block[Natural[BitOperations.TrailingZeroCount(pending)]];
                    pending &= pending - 1;
                    if (v < 0 && (coef & p1) == 0) coef += (short)(coef >= 0 ? p1 : m1);
                }
            }
        }

        // zig-zag positions from through to, inclusive
        private static ulong Band(int from, int to) => (ulong.MaxValue << from) & (ulong.MaxValue >> (63 - to));

        // Where the scan starting at pos ends: the first marker that is not stuffing, fill or RSTn, found by vector search.
        private static int ScanEnd(ReadOnlySpan<byte> data, int pos)
        {
            while (true)
            {
                int ff = data.Slice(pos).IndexOf((byte)0xFF);
                if (ff < 0) return data.Length;
                pos += ff;
                if (pos + 1 >= data.Length) return data.Length;
                byte marker = data[pos + 1];
                if (marker != 0 && marker != 0xFF && (marker < 0xD0 || marker > 0xD7)) return pos;
                pos++;
            }
        }

        private static int NextMarker(ReadOnlySpan<byte> data, int pos)
        {
            for (; pos + 1 < data.Length; pos++)
            {
                if (data[pos] != 0xFF) continue;
                byte marker = data[pos + 1];
                if (marker != 0 && marker != 0xFF && (marker < 0xD0 || marker > 0xD7)) return pos;
            }
            return data.Length;
        }

        // MSB-first entropy bits over one unstuffed segment; past its end, at a marker or the end of the file, it feeds zeros.
        private ref struct Bits
        {
            private readonly ReadOnlySpan<byte> data;
            private readonly Span<byte> clean;
            public int pos;
            private int at, end;
            private ulong buffer;
            private int count;

            // refill on every Ensure rather than only below 32 bits
            public bool always;

            public Bits(ReadOnlySpan<byte> data, int pos, byte[] clean)
            {
                this.data = data;
                this.clean = clean;
                buffer = 0;
                count = 0;
                at = 0;
                always = false;
                end = Unstuff(data, pos, clean, out this.pos);
            }

            public readonly int Length => end;

            // tops the buffer up to at least 56 bits without a branch, unless 32 are already there and always is off
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Ensure()
            {
                if (!always && count >= 32) return;
                buffer |= BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(clean), at))) >> count;
                at = Math.Min(at + ((63 - count) >> 3), end);
                count |= 56;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public readonly int Peek(int n) => (int)(buffer >> (64 - n));

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Skip(int n)
            {
                buffer <<= n;
                count -= n;
            }

            // Copies the bytes from `from` up to the next marker, 0xFF 00 as 0xFF, then eight zeros; `marker` is where it stopped.
            private static int Unstuff(ReadOnlySpan<byte> data, int from, Span<byte> clean, out int marker)
            {
                int n = 0, p = from;
                while (p < data.Length)
                {
                    int ff = data.Slice(p).IndexOf((byte)0xFF);
                    int run = ff < 0 ? data.Length - p : ff;
                    data.Slice(p, run).CopyTo(clean.Slice(n));
                    n += run;
                    p += run;
                    if (ff < 0 || p + 1 < data.Length && data[p + 1] != 0) break;
                    clean[n++] = 0xFF;
                    p += 2;
                }
                marker = Math.Min(p, data.Length);
                clean.Slice(n, 8).Clear();
                return n;
            }

            // one Huffman symbol of table t; needs 16 bits buffered, which Ensure gives
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Symbol(ref int tables, int t)
            {
                int entry = Unsafe.Add(ref tables, SymbolFastAt + (t << FastBits) + Peek(FastBits));
                if (entry == 0) entry = LongSymbol(buffer, ref tables, t);
                Skip(entry >> 8);
                return entry & 0xFF;
            }

            // a code longer than the lookup, as length << 8 | symbol
            [MethodImpl(MethodImplOptions.NoInlining)]
            private static int LongSymbol(ulong buffer, ref int tables, int t)
            {
                int l = FastBits + 1;
                int code = (int)(buffer >> (64 - l));
                while (code > Unsafe.Add(ref tables, MaxCodeAt + t * 18 + l))
                {
                    if (++l > 16) throw new ImageFormatException("JPEG entropy data holds an invalid Huffman code");
                    code = (int)(buffer >> (64 - l));
                }
                return l << 8 | Unsafe.Add(ref tables, ValuesAt + t * 256 + code + Unsafe.Add(ref tables, OffsetAt + t * 17 + l));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int GetBits(int n)
            {
                if (count < n) Ensure();
                int v = Peek(n);
                Skip(n);
                return v;
            }

            // s bits as a signed magnitude; zero bits read nothing
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Receive(int s)
            {
                if (s == 0) return 0;
                int v = GetBits(s);
                return v + ((v >> (s - 1)) - 1 & (-1 << s) + 1);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Restart() => this = Restarted(this);

            // drops what is buffered, steps over the next RSTn, stopping at any other marker, and unstuffs what follows
            [MethodImpl(MethodImplOptions.NoInlining)]
            private static Bits Restarted(Bits b)
            {
                b.buffer = 0;
                b.count = 0;
                b.at = 0;
                int p = b.pos;
                for (; p + 1 < b.data.Length; p++)
                {
                    if (b.data[p] != 0xFF) continue;
                    byte marker = b.data[p + 1];
                    if (marker >= 0xD0 && marker <= 0xD7)
                    {
                        p += 2;
                        break;
                    }
                    if (marker != 0 && marker != 0xFF) break;
                }
                b.end = Unstuff(b.data, p, b.clean, out b.pos);
                return b;
            }
        }
        #endregion

        #region reconstruction
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void TransformAll(ref Frame f)
        {
            Span<int> workspace = stackalloc int[64];
            for (int i = 0; i < f.count; i++)
            {
                ref Component c = ref f.components[i];
                ReadOnlySpan<int> quant = f.tables.AsSpan(QuantAt + c.quant * 64, 64);
                int stride = c.blocksX * 8, blocksX = (c.width + 7) >> 3, blocksY = (c.height + 7) >> 3;
                for (int by = 0; by < blocksY; by++)
                {
                    for (int bx = 0; bx < blocksX; bx++)
                    {
                        ReadOnlySpan<short> block = f.coefficients.AsSpan(c.coefStart + (by * c.blocksX + bx) * 64, 64);
                        Span<byte> output = f.planes.AsSpan(c.planeStart + by * 8 * stride + bx * 8);
                        if (HasAc(block)) Transform(block, quant, output, stride, workspace);
                        else FillBlock(output, stride, DcOnly(block[0], quant[0]));
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HasAc(ReadOnlySpan<short> block)
        {
            ref short b = ref MemoryMarshal.GetReference(block);
            Vector128<short> any = Vector128.LoadUnsafe(ref b) & Vector128.Create((short)0, -1, -1, -1, -1, -1, -1, -1);
            for (int at = 8; at < 64; at += 8) any |= Vector128.LoadUnsafe(ref b, (nuint)at);
            return any != Vector128<short>.Zero;
        }

        // What islow gives a block whose only coefficient is the DC: one value, (dc x q + 4) >> 3 around 128.
        private static byte DcOnly(int dc, int q) => Clamp(((dc * q + 4) >> 3) + 128);

        private static void FillBlock(Span<byte> output, int stride, byte value)
        {
            ulong row = value * 0x0101010101010101UL;
            ref byte o = ref MemoryMarshal.GetReference(output);
            for (int y = 0; y < 8; y++) Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, y * stride), row);
        }

        // One block through the widest IDCT the machine runs; all three give libjpeg's islow result exactly.
        private static void Transform(ReadOnlySpan<short> input, ReadOnlySpan<int> q, Span<byte> output, int stride, Span<int> ws)
        {
            if (Avx2.IsSupported) IdctAvx2(input, q, output, stride);
            else if (Sse2.IsSupported || AdvSimd.Arm64.IsSupported) IdctVector(input, q, output, stride);
            else Idct(input, q, output, stride, ws);
        }

        #region vector IDCT
        // islow with each row as one Vector256: column pass, transpose, row pass, transpose back.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void IdctAvx2(ReadOnlySpan<short> input, ReadOnlySpan<int> q, Span<byte> output, int stride)
        {
            ref short coef = ref MemoryMarshal.GetReference(input);
            ref int quant = ref MemoryMarshal.GetReference(q);
            Vector256<int> r0 = Row256(ref coef, ref quant, 0), r1 = Row256(ref coef, ref quant, 8), r2 = Row256(ref coef, ref quant, 16), r3 = Row256(ref coef, ref quant, 24);
            Vector256<int> r4 = Row256(ref coef, ref quant, 32), r5 = Row256(ref coef, ref quant, 40), r6 = Row256(ref coef, ref quant, 48), r7 = Row256(ref coef, ref quant, 56);
            Butterfly(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, ConstBits - Pass1Bits);
            Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            Butterfly(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, ConstBits + Pass1Bits + 3);
            Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            ref byte o = ref MemoryMarshal.GetReference(output);
            StoreRows(ref o, stride, 0, r0.GetLower(), r0.GetUpper(), r1.GetLower(), r1.GetUpper());
            StoreRows(ref o, stride, 2, r2.GetLower(), r2.GetUpper(), r3.GetLower(), r3.GetUpper());
            StoreRows(ref o, stride, 4, r4.GetLower(), r4.GetUpper(), r5.GetLower(), r5.GetUpper());
            StoreRows(ref o, stride, 6, r6.GetLower(), r6.GetUpper(), r7.GetLower(), r7.GetUpper());
        }

        // islow with each row as two Vector128 halves; the transposes swap 4x4 quadrants.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void IdctVector(ReadOnlySpan<short> input, ReadOnlySpan<int> q, Span<byte> output, int stride)
        {
            ref short coef = ref MemoryMarshal.GetReference(input);
            ref int quant = ref MemoryMarshal.GetReference(q);
            Row128(ref coef, ref quant, 0, out Vector128<int> l0, out Vector128<int> h0);
            Row128(ref coef, ref quant, 8, out Vector128<int> l1, out Vector128<int> h1);
            Row128(ref coef, ref quant, 16, out Vector128<int> l2, out Vector128<int> h2);
            Row128(ref coef, ref quant, 24, out Vector128<int> l3, out Vector128<int> h3);
            Row128(ref coef, ref quant, 32, out Vector128<int> l4, out Vector128<int> h4);
            Row128(ref coef, ref quant, 40, out Vector128<int> l5, out Vector128<int> h5);
            Row128(ref coef, ref quant, 48, out Vector128<int> l6, out Vector128<int> h6);
            Row128(ref coef, ref quant, 56, out Vector128<int> l7, out Vector128<int> h7);
            Butterfly(ref l0, ref l1, ref l2, ref l3, ref l4, ref l5, ref l6, ref l7, ConstBits - Pass1Bits);
            Butterfly(ref h0, ref h1, ref h2, ref h3, ref h4, ref h5, ref h6, ref h7, ConstBits - Pass1Bits);

            // lanes become rows: columns 0-3 of rows 0-3 and 4-7, then columns 4-7
            Transpose(ref l0, ref l1, ref l2, ref l3);
            Transpose(ref l4, ref l5, ref l6, ref l7);
            Transpose(ref h0, ref h1, ref h2, ref h3);
            Transpose(ref h4, ref h5, ref h6, ref h7);
            Butterfly(ref l0, ref l1, ref l2, ref l3, ref h0, ref h1, ref h2, ref h3, ConstBits + Pass1Bits + 3);
            Butterfly(ref l4, ref l5, ref l6, ref l7, ref h4, ref h5, ref h6, ref h7, ConstBits + Pass1Bits + 3);
            Transpose(ref l0, ref l1, ref l2, ref l3);
            Transpose(ref h0, ref h1, ref h2, ref h3);
            Transpose(ref l4, ref l5, ref l6, ref l7);
            Transpose(ref h4, ref h5, ref h6, ref h7);

            ref byte o = ref MemoryMarshal.GetReference(output);
            StoreRows(ref o, stride, 0, l0, h0, l1, h1);
            StoreRows(ref o, stride, 2, l2, h2, l3, h3);
            StoreRows(ref o, stride, 4, l4, h4, l5, h5);
            StoreRows(ref o, stride, 6, l6, h6, l7, h7);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<int> Row256(ref short coef, ref int quant, int at)
            => Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref coef, (nuint)at)) * Vector256.LoadUnsafe(ref quant, (nuint)at);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Row128(ref short coef, ref int quant, int at, out Vector128<int> low, out Vector128<int> high)
        {
            (Vector128<int> l, Vector128<int> h) = Vector128.Widen(Vector128.LoadUnsafe(ref coef, (nuint)at));
            low = l * Vector128.LoadUnsafe(ref quant, (nuint)at);
            high = h * Vector128.LoadUnsafe(ref quant, (nuint)at + 4);
        }

        // One islow pass over eight inputs, in place: the even part from 0, 2, 4, 6, the odd part from 1, 3, 5, 7.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Butterfly(ref Vector128<int> v0, ref Vector128<int> v1, ref Vector128<int> v2, ref Vector128<int> v3,
            ref Vector128<int> v4, ref Vector128<int> v5, ref Vector128<int> v6, ref Vector128<int> v7, int shift)
        {
            Vector128<int> z1 = (v2 + v6) * Vector128.Create(4433);
            Vector128<int> tmp2 = z1 + v6 * Vector128.Create(-15137);
            Vector128<int> tmp3 = z1 + v2 * Vector128.Create(6270);
            Vector128<int> tmp0 = Vector128.ShiftLeft(v0 + v4, ConstBits);
            Vector128<int> tmp1 = Vector128.ShiftLeft(v0 - v4, ConstBits);
            Vector128<int> tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;

            Vector128<int> o0 = v7, o1 = v5, o2 = v3, o3 = v1;
            z1 = o0 + o3;
            Vector128<int> z2 = o1 + o2, z3 = o0 + o2, z4 = o1 + o3;
            Vector128<int> z5 = (z3 + z4) * Vector128.Create(9633);
            o0 *= Vector128.Create(2446);
            o1 *= Vector128.Create(16819);
            o2 *= Vector128.Create(25172);
            o3 *= Vector128.Create(12299);
            z1 *= Vector128.Create(-7373);
            z2 *= Vector128.Create(-20995);
            z3 = z3 * Vector128.Create(-16069) + z5;
            z4 = z4 * Vector128.Create(-3196) + z5;
            o0 += z1 + z3;
            o1 += z2 + z4;
            o2 += z2 + z3;
            o3 += z1 + z4;

            Vector128<int> round = Vector128.Create(1 << (shift - 1));
            tmp10 += round;
            tmp11 += round;
            tmp12 += round;
            tmp13 += round;
            v0 = Vector128.ShiftRightArithmetic(tmp10 + o3, shift);
            v7 = Vector128.ShiftRightArithmetic(tmp10 - o3, shift);
            v1 = Vector128.ShiftRightArithmetic(tmp11 + o2, shift);
            v6 = Vector128.ShiftRightArithmetic(tmp11 - o2, shift);
            v2 = Vector128.ShiftRightArithmetic(tmp12 + o1, shift);
            v5 = Vector128.ShiftRightArithmetic(tmp12 - o1, shift);
            v3 = Vector128.ShiftRightArithmetic(tmp13 + o0, shift);
            v4 = Vector128.ShiftRightArithmetic(tmp13 - o0, shift);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Butterfly(ref Vector256<int> v0, ref Vector256<int> v1, ref Vector256<int> v2, ref Vector256<int> v3,
            ref Vector256<int> v4, ref Vector256<int> v5, ref Vector256<int> v6, ref Vector256<int> v7, int shift)
        {
            Vector256<int> z1 = (v2 + v6) * Vector256.Create(4433);
            Vector256<int> tmp2 = z1 + v6 * Vector256.Create(-15137);
            Vector256<int> tmp3 = z1 + v2 * Vector256.Create(6270);
            Vector256<int> tmp0 = Vector256.ShiftLeft(v0 + v4, ConstBits);
            Vector256<int> tmp1 = Vector256.ShiftLeft(v0 - v4, ConstBits);
            Vector256<int> tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;

            Vector256<int> o0 = v7, o1 = v5, o2 = v3, o3 = v1;
            z1 = o0 + o3;
            Vector256<int> z2 = o1 + o2, z3 = o0 + o2, z4 = o1 + o3;
            Vector256<int> z5 = (z3 + z4) * Vector256.Create(9633);
            o0 *= Vector256.Create(2446);
            o1 *= Vector256.Create(16819);
            o2 *= Vector256.Create(25172);
            o3 *= Vector256.Create(12299);
            z1 *= Vector256.Create(-7373);
            z2 *= Vector256.Create(-20995);
            z3 = z3 * Vector256.Create(-16069) + z5;
            z4 = z4 * Vector256.Create(-3196) + z5;
            o0 += z1 + z3;
            o1 += z2 + z4;
            o2 += z2 + z3;
            o3 += z1 + z4;

            Vector256<int> round = Vector256.Create(1 << (shift - 1));
            tmp10 += round;
            tmp11 += round;
            tmp12 += round;
            tmp13 += round;
            v0 = Vector256.ShiftRightArithmetic(tmp10 + o3, shift);
            v7 = Vector256.ShiftRightArithmetic(tmp10 - o3, shift);
            v1 = Vector256.ShiftRightArithmetic(tmp11 + o2, shift);
            v6 = Vector256.ShiftRightArithmetic(tmp11 - o2, shift);
            v2 = Vector256.ShiftRightArithmetic(tmp12 + o1, shift);
            v5 = Vector256.ShiftRightArithmetic(tmp12 - o1, shift);
            v3 = Vector256.ShiftRightArithmetic(tmp13 + o0, shift);
            v4 = Vector256.ShiftRightArithmetic(tmp13 - o0, shift);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Transpose(ref Vector256<int> r0, ref Vector256<int> r1, ref Vector256<int> r2, ref Vector256<int> r3,
            ref Vector256<int> r4, ref Vector256<int> r5, ref Vector256<int> r6, ref Vector256<int> r7)
        {
            Vector256<int> t0 = Avx2.UnpackLow(r0, r1), t1 = Avx2.UnpackHigh(r0, r1), t2 = Avx2.UnpackLow(r2, r3), t3 = Avx2.UnpackHigh(r2, r3);
            Vector256<int> t4 = Avx2.UnpackLow(r4, r5), t5 = Avx2.UnpackHigh(r4, r5), t6 = Avx2.UnpackLow(r6, r7), t7 = Avx2.UnpackHigh(r6, r7);
            Vector256<int> u0 = Avx2.UnpackLow(t0.AsInt64(), t2.AsInt64()).AsInt32(), u1 = Avx2.UnpackHigh(t0.AsInt64(), t2.AsInt64()).AsInt32();
            Vector256<int> u2 = Avx2.UnpackLow(t1.AsInt64(), t3.AsInt64()).AsInt32(), u3 = Avx2.UnpackHigh(t1.AsInt64(), t3.AsInt64()).AsInt32();
            Vector256<int> u4 = Avx2.UnpackLow(t4.AsInt64(), t6.AsInt64()).AsInt32(), u5 = Avx2.UnpackHigh(t4.AsInt64(), t6.AsInt64()).AsInt32();
            Vector256<int> u6 = Avx2.UnpackLow(t5.AsInt64(), t7.AsInt64()).AsInt32(), u7 = Avx2.UnpackHigh(t5.AsInt64(), t7.AsInt64()).AsInt32();
            r0 = Avx2.Permute2x128(u0, u4, 0x20);
            r1 = Avx2.Permute2x128(u1, u5, 0x20);
            r2 = Avx2.Permute2x128(u2, u6, 0x20);
            r3 = Avx2.Permute2x128(u3, u7, 0x20);
            r4 = Avx2.Permute2x128(u0, u4, 0x31);
            r5 = Avx2.Permute2x128(u1, u5, 0x31);
            r6 = Avx2.Permute2x128(u2, u6, 0x31);
            r7 = Avx2.Permute2x128(u3, u7, 0x31);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Transpose(ref Vector128<int> r0, ref Vector128<int> r1, ref Vector128<int> r2, ref Vector128<int> r3)
        {
            Vector128<int> t0 = ZipLow(r0, r1), t1 = ZipLow(r2, r3), t2 = ZipHigh(r0, r1), t3 = ZipHigh(r2, r3);
            r0 = ZipLow(t0.AsInt64(), t1.AsInt64()).AsInt32();
            r1 = ZipHigh(t0.AsInt64(), t1.AsInt64()).AsInt32();
            r2 = ZipLow(t2.AsInt64(), t3.AsInt64()).AsInt32();
            r3 = ZipHigh(t2.AsInt64(), t3.AsInt64()).AsInt32();
        }

        // Two rows of eight samples, centred on 128, clamped and narrowed to bytes.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StoreRows(ref byte output, int stride, int row, Vector128<int> l0, Vector128<int> h0, Vector128<int> l1, Vector128<int> h1)
        {
            Vector128<int> centre = Vector128.Create(128), low = Vector128<int>.Zero, high = Vector128.Create(255);
            l0 = Vector128.Min(Vector128.Max(l0 + centre, low), high);
            h0 = Vector128.Min(Vector128.Max(h0 + centre, low), high);
            l1 = Vector128.Min(Vector128.Max(l1 + centre, low), high);
            h1 = Vector128.Min(Vector128.Max(h1 + centre, low), high);
            Vector128<ulong> both = Vector128.Narrow(Vector128.Narrow(l0, h0), Vector128.Narrow(l1, h1)).AsByte().AsUInt64();
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, row * stride), both.ToScalar());
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, (row + 1) * stride), both.GetElement(1));
        }

        // SSE2 unpack or NEON zip: interleaves the low or high halves of two vectors.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<T> ZipLow<T>(Vector128<T> a, Vector128<T> b) where T : struct
        {
            if (Sse2.IsSupported)
            {
                if (typeof(T) == typeof(int)) return Sse2.UnpackLow(a.AsInt32(), b.AsInt32()).As<int, T>();
                if (typeof(T) == typeof(long)) return Sse2.UnpackLow(a.AsInt64(), b.AsInt64()).As<long, T>();
                return Sse2.UnpackLow(a.AsInt16(), b.AsInt16()).As<short, T>();
            }
            if (typeof(T) == typeof(int)) return AdvSimd.Arm64.ZipLow(a.AsInt32(), b.AsInt32()).As<int, T>();
            if (typeof(T) == typeof(long)) return AdvSimd.Arm64.ZipLow(a.AsInt64(), b.AsInt64()).As<long, T>();
            return AdvSimd.Arm64.ZipLow(a.AsInt16(), b.AsInt16()).As<short, T>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<T> ZipHigh<T>(Vector128<T> a, Vector128<T> b) where T : struct
        {
            if (Sse2.IsSupported)
            {
                if (typeof(T) == typeof(int)) return Sse2.UnpackHigh(a.AsInt32(), b.AsInt32()).As<int, T>();
                if (typeof(T) == typeof(long)) return Sse2.UnpackHigh(a.AsInt64(), b.AsInt64()).As<long, T>();
                return Sse2.UnpackHigh(a.AsInt16(), b.AsInt16()).As<short, T>();
            }
            if (typeof(T) == typeof(int)) return AdvSimd.Arm64.ZipHigh(a.AsInt32(), b.AsInt32()).As<int, T>();
            if (typeof(T) == typeof(long)) return AdvSimd.Arm64.ZipHigh(a.AsInt64(), b.AsInt64()).As<long, T>();
            return AdvSimd.Arm64.ZipHigh(a.AsInt16(), b.AsInt16()).As<short, T>();
        }
        #endregion

        // libjpeg's jpeg_idct_islow: dequantise, columns into the workspace, then rows into samples.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Idct(ReadOnlySpan<short> input, ReadOnlySpan<int> q, Span<byte> output, int stride, Span<int> ws)
        {
            const int Shift1 = ConstBits - Pass1Bits, Round1 = 1 << (Shift1 - 1);
            const int Shift2 = ConstBits + Pass1Bits + 3, Round2 = 1 << (Shift2 - 1);
            for (int col = 0; col < 8; col++)
            {
                if (((ushort)input[8 + col] | (ushort)input[16 + col] | (ushort)input[24 + col] | (ushort)input[32 + col] | (ushort)input[40 + col]
                    | (ushort)input[48 + col] | (ushort)input[56 + col]) == 0)
                {
                    int dc = input[col] * q[col] << Pass1Bits;
                    for (int r = 0; r < 64; r += 8) ws[r + col] = dc;
                    continue;
                }
                int z2 = input[16 + col] * q[16 + col], z3 = input[48 + col] * q[48 + col];
                int z1 = (z2 + z3) * 4433;
                int tmp2 = z1 + z3 * -15137;
                int tmp3 = z1 + z2 * 6270;
                z2 = input[col] * q[col];
                z3 = input[32 + col] * q[32 + col];
                int tmp0 = (z2 + z3) << ConstBits;
                int tmp1 = (z2 - z3) << ConstBits;
                int tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;

                tmp0 = input[56 + col] * q[56 + col];
                tmp1 = input[40 + col] * q[40 + col];
                tmp2 = input[24 + col] * q[24 + col];
                tmp3 = input[8 + col] * q[8 + col];
                z1 = tmp0 + tmp3;
                z2 = tmp1 + tmp2;
                z3 = tmp0 + tmp2;
                int z4 = tmp1 + tmp3;
                int z5 = (z3 + z4) * 9633;
                tmp0 *= 2446;
                tmp1 *= 16819;
                tmp2 *= 25172;
                tmp3 *= 12299;
                z1 *= -7373;
                z2 *= -20995;
                z3 = z3 * -16069 + z5;
                z4 = z4 * -3196 + z5;
                tmp0 += z1 + z3;
                tmp1 += z2 + z4;
                tmp2 += z2 + z3;
                tmp3 += z1 + z4;

                ws[col] = (tmp10 + tmp3 + Round1) >> Shift1;
                ws[56 + col] = (tmp10 - tmp3 + Round1) >> Shift1;
                ws[8 + col] = (tmp11 + tmp2 + Round1) >> Shift1;
                ws[48 + col] = (tmp11 - tmp2 + Round1) >> Shift1;
                ws[16 + col] = (tmp12 + tmp1 + Round1) >> Shift1;
                ws[40 + col] = (tmp12 - tmp1 + Round1) >> Shift1;
                ws[24 + col] = (tmp13 + tmp0 + Round1) >> Shift1;
                ws[32 + col] = (tmp13 - tmp0 + Round1) >> Shift1;
            }

            for (int row = 0; row < 8; row++)
            {
                ReadOnlySpan<int> w = ws.Slice(row * 8, 8);
                Span<byte> o = output.Slice(row * stride, 8);
                if ((w[1] | w[2] | w[3] | w[4] | w[5] | w[6] | w[7]) == 0)
                {
                    o.Fill(Clamp(((w[0] + (1 << (Pass1Bits + 2))) >> (Pass1Bits + 3)) + 128));
                    continue;
                }
                int z2 = w[2], z3 = w[6];
                int z1 = (z2 + z3) * 4433;
                int tmp2 = z1 + z3 * -15137;
                int tmp3 = z1 + z2 * 6270;
                int tmp0 = (w[0] + w[4]) << ConstBits;
                int tmp1 = (w[0] - w[4]) << ConstBits;
                int tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;

                tmp0 = w[7];
                tmp1 = w[5];
                tmp2 = w[3];
                tmp3 = w[1];
                z1 = tmp0 + tmp3;
                z2 = tmp1 + tmp2;
                z3 = tmp0 + tmp2;
                int z4 = tmp1 + tmp3;
                int z5 = (z3 + z4) * 9633;
                tmp0 *= 2446;
                tmp1 *= 16819;
                tmp2 *= 25172;
                tmp3 *= 12299;
                z1 *= -7373;
                z2 *= -20995;
                z3 = z3 * -16069 + z5;
                z4 = z4 * -3196 + z5;
                tmp0 += z1 + z3;
                tmp1 += z2 + z4;
                tmp2 += z2 + z3;
                tmp3 += z1 + z4;

                o[0] = Clamp(((tmp10 + tmp3 + Round2) >> Shift2) + 128);
                o[7] = Clamp(((tmp10 - tmp3 + Round2) >> Shift2) + 128);
                o[1] = Clamp(((tmp11 + tmp2 + Round2) >> Shift2) + 128);
                o[6] = Clamp(((tmp11 - tmp2 + Round2) >> Shift2) + 128);
                o[2] = Clamp(((tmp12 + tmp1 + Round2) >> Shift2) + 128);
                o[5] = Clamp(((tmp12 - tmp1 + Round2) >> Shift2) + 128);
                o[3] = Clamp(((tmp13 + tmp0 + Round2) >> Shift2) + 128);
                o[4] = Clamp(((tmp13 - tmp0 + Round2) >> Shift2) + 128);
            }
        }

        // Upsamples each component to full size and converts the row to RGBA8, straight into RGBA8 destinations.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Output(ref Frame f, Span<byte> destination, int pitch, PixelLayout layout)
        {
            int width = f.width, count = f.count;
            int rowWidth = f.mcusX * 8 * f.hmax;
            bool ycc = IsYcc(in f);
            for (int i = 0; i < count; i++)
            {
                ref Component c = ref f.components[i];
                if (!c.scanned && !f.progressive) f.planes.AsSpan(c.planeStart, c.blocksX * c.blocksY * 64).Clear();
            }
            byte[] rows = ArrayPool<byte>.Shared.Rent(count * rowWidth);
            ushort[] sums = ArrayPool<ushort>.Shared.Rent(rowWidth + 2);
            byte[] rgba = layout == PixelLayout.RGBA8 ? Array.Empty<byte>() : ArrayPool<byte>.Shared.Rent(width * 4);
            try
            {
                for (int y = 0; y < f.height; y++)
                {
                    ReadOnlySpan<byte> c0 = Row(ref f, 0, y, rows.AsSpan(0, rowWidth), sums);
                    ReadOnlySpan<byte> c1 = count >= 3 ? Row(ref f, 1, y, rows.AsSpan(rowWidth, rowWidth), sums) : c0;
                    ReadOnlySpan<byte> c2 = count >= 3 ? Row(ref f, 2, y, rows.AsSpan(rowWidth * 2, rowWidth), sums) : c0;
                    Span<byte> target = layout == PixelLayout.RGBA8 ? destination.Slice(y * pitch, width * 4) : rgba.AsSpan(0, width * 4);
                    if (count == 4) ConvertCmykRow(c0, c1, c2, Row(ref f, 3, y, rows.AsSpan(rowWidth * 3, rowWidth), sums), target, width, f.adobeTransform == 2, f.adobeTransform >= 0);
                    else ConvertRow(c0, c1, c2, target, width, ycc);
                    if (layout != PixelLayout.RGBA8) PixelRows.FromRgba(target, destination.Slice(y * pitch), width, layout);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rows);
                ArrayPool<ushort>.Shared.Return(sums);
                if (rgba.Length > 0) ArrayPool<byte>.Shared.Return(rgba);
            }
        }

        // Three components are YCbCr unless the caller, an Adobe marker or R/G/B component ids without JFIF say RGB.
        private static bool IsYcc(in Frame f) => f.ycc >= 0 ? f.count == 3 && f.ycc == 1 : f.count == 3 && !(f.adobeTransform >= 0 ? f.adobeTransform == 0
            : !f.jfif && f.components[0].id == 'R' && f.components[1].id == 'G' && f.components[2].id == 'B');

        // One row of RGBA8 from three sample rows, YCbCr through libjpeg's fixed-point formulas or copied as they are.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ConvertRow(ReadOnlySpan<byte> c0, ReadOnlySpan<byte> c1, ReadOnlySpan<byte> c2, Span<byte> target, int width, bool ycc)
        {
            ref byte p0 = ref MemoryMarshal.GetReference(c0), p1 = ref MemoryMarshal.GetReference(c1), p2 = ref MemoryMarshal.GetReference(c2);
            ref byte t = ref MemoryMarshal.GetReference(target);
            int x = 0;
            if (Avx2.IsSupported)
            {
                // 16 pixels in 16-bit lanes, libjpeg's constants split into pmaddwd pairs
                if (ycc)
                {
                    Vector256<short> bias = Vector256.Create((short)128), two = Vector256.Create((short)2), alpha = Vector256.Create((short)255);
                    Vector256<short> kr = Pair(26345, 16384), kg = Pair(-22554, 18734), kb = Pair(-14942, 16384);
                    Vector256<int> half = Vector256.Create(32768);
                    for (; x + 16 <= width; x += 16)
                    {
                        Vector256<short> luma = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref p0, (nuint)x));
                        Vector256<short> cb = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref p1, (nuint)x)) - bias;
                        Vector256<short> cr = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref p2, (nuint)x)) - bias;
                        Vector256<short> r = luma + cr + Product(cr, two, kr, Vector256<int>.Zero);
                        Vector256<short> g = luma - cr + Product(cb, cr, kg, half);
                        Vector256<short> b = luma + cb + cb + Product(cb, two, kb, Vector256<int>.Zero);
                        Vector256<byte> rb = Avx2.PackUnsignedSaturate(r, b), ga = Avx2.PackUnsignedSaturate(g, alpha);
                        Vector256<short> rg = Avx2.UnpackLow(rb, ga).AsInt16(), ba = Avx2.UnpackHigh(rb, ga).AsInt16();
                        Vector256<short> low = Avx2.UnpackLow(rg, ba), high = Avx2.UnpackHigh(rg, ba);
                        Avx2.Permute2x128(low, high, 0x20).AsByte().StoreUnsafe(ref t, (nuint)(x * 4));
                        Avx2.Permute2x128(low, high, 0x31).AsByte().StoreUnsafe(ref t, (nuint)(x * 4 + 32));
                    }
                }
                for (; x + 8 <= width; x += 8)
                {
                    Vector256<int> r = Avx2.ConvertToVector256Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p0, x))).AsByte());
                    Vector256<int> g = Avx2.ConvertToVector256Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p1, x))).AsByte());
                    Vector256<int> b = Avx2.ConvertToVector256Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p2, x))).AsByte());
                    if (ycc)
                    {
                        Vector256<int> half = Vector256.Create(32768), low = Vector256<int>.Zero, high = Vector256.Create(255);
                        Vector256<int> cb = g - Vector256.Create(128), cr = b - Vector256.Create(128), luma = r;
                        r = Vector256.Min(Vector256.Max(luma + Vector256.ShiftRightArithmetic(cr * Vector256.Create(91881) + half, 16), low), high);
                        g = Vector256.Min(Vector256.Max(luma + Vector256.ShiftRightArithmetic(cb * Vector256.Create(-22554) - cr * Vector256.Create(46802) + half, 16), low), high);
                        b = Vector256.Min(Vector256.Max(luma + Vector256.ShiftRightArithmetic(cb * Vector256.Create(116130) + half, 16), low), high);
                    }
                    Vector256<int> pixels = r | Vector256.ShiftLeft(g, 8) | Vector256.ShiftLeft(b, 16) | Vector256.Create(unchecked((int)0xFF000000));
                    pixels.AsByte().StoreUnsafe(ref t, (nuint)(x * 4));
                }
            }
            else if (Sse2.IsSupported || AdvSimd.Arm64.IsSupported)
            {
                for (; x + 16 <= width; x += 16)
                {
                    (Vector128<ushort> a0, Vector128<ushort> a1) = Vector128.Widen(Vector128.LoadUnsafe(ref p0, (nuint)x));
                    (Vector128<ushort> b0, Vector128<ushort> b1) = Vector128.Widen(Vector128.LoadUnsafe(ref p1, (nuint)x));
                    (Vector128<ushort> d0, Vector128<ushort> d1) = Vector128.Widen(Vector128.LoadUnsafe(ref p2, (nuint)x));
                    ref byte o = ref Unsafe.Add(ref t, x * 4);
                    StorePixels(ref o, 0, a0, b0, d0, ycc, false);
                    StorePixels(ref o, 16, a0, b0, d0, ycc, true);
                    StorePixels(ref o, 32, a1, b1, d1, ycc, false);
                    StorePixels(ref o, 48, a1, b1, d1, ycc, true);
                }
            }
            for (; x < width; x++)
            {
                int luma = Unsafe.Add(ref p0, x), cb = Unsafe.Add(ref p1, x), cr = Unsafe.Add(ref p2, x);
                ref byte o = ref Unsafe.Add(ref t, x * 4);
                if (ycc)
                {
                    cb -= 128;
                    cr -= 128;
                    o = Clamp(luma + ((cr * 91881 + 32768) >> 16));
                    Unsafe.Add(ref o, 1) = Clamp(luma + ((cb * -22554 - cr * 46802 + 32768) >> 16));
                    Unsafe.Add(ref o, 2) = Clamp(luma + ((cb * 116130 + 32768) >> 16));
                }
                else
                {
                    o = (byte)luma;
                    Unsafe.Add(ref o, 1) = (byte)cb;
                    Unsafe.Add(ref o, 2) = (byte)cr;
                }
                Unsafe.Add(ref o, 3) = 255;
            }
        }

        // (a, b) pairs times (k0, k1), plus add, shifted down 16 and narrowed back in pixel order.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<short> Product(Vector256<short> a, Vector256<short> b, Vector256<short> k, Vector256<int> add)
        {
            Vector256<int> low = Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(a, b), k) + add;
            Vector256<int> high = Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(a, b), k) + add;
            return Avx2.PackSignedSaturate(Vector256.ShiftRightArithmetic(low, 16), Vector256.ShiftRightArithmetic(high, 16));
        }

        private static Vector256<short> Pair(short k0, short k1) => Vector256.Create((ushort)k0 | k1 << 16).AsInt16();

        // One row of RGBA8 from CMYK, YCCK turned to CMYK first; an Adobe file stores every ink inverted.
        private static void ConvertCmykRow(ReadOnlySpan<byte> c0, ReadOnlySpan<byte> c1, ReadOnlySpan<byte> c2, ReadOnlySpan<byte> c3, Span<byte> target, int width, bool ycck, bool inverted)
        {
            for (int x = 0; x < width; x++)
            {
                int c = c0[x], m = c1[x], y = c2[x], k = c3[x];
                if (ycck)
                {
                    int luma = c, cb = m - 128, cr = y - 128;
                    c = 255 - Clamp(luma + ((cr * 91881 + 32768) >> 16));
                    m = 255 - Clamp(luma + ((cb * -22554 - cr * 46802 + 32768) >> 16));
                    y = 255 - Clamp(luma + ((cb * 116130 + 32768) >> 16));
                }
                if (!inverted)
                {
                    c = 255 - c;
                    m = 255 - m;
                    y = 255 - y;
                    k = 255 - k;
                }
                Span<byte> o = target.Slice(x * 4, 4);
                o[0] = Times(c, k);
                o[1] = Times(m, k);
                o[2] = Times(y, k);
                o[3] = 255;
            }
        }

        // a * b / 255, rounded
        private static byte Times(int a, int b)
        {
            int t = a * b + 128;
            return (byte)((t + (t >> 8)) >> 8);
        }

        // Four pixels, one per int lane, from the low or high half of eight widened samples per component.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StorePixels(ref byte target, int offset, Vector128<ushort> a, Vector128<ushort> b, Vector128<ushort> d, bool ycc, bool upper)
        {
            Vector128<int> r = (upper ? Vector128.WidenUpper(a) : Vector128.WidenLower(a)).AsInt32();
            Vector128<int> g = (upper ? Vector128.WidenUpper(b) : Vector128.WidenLower(b)).AsInt32();
            Vector128<int> bl = (upper ? Vector128.WidenUpper(d) : Vector128.WidenLower(d)).AsInt32();
            if (ycc)
            {
                Vector128<int> half = Vector128.Create(32768), low = Vector128<int>.Zero, high = Vector128.Create(255);
                Vector128<int> cb = g - Vector128.Create(128), cr = bl - Vector128.Create(128);
                Vector128<int> luma = r;
                r = Vector128.Min(Vector128.Max(luma + Vector128.ShiftRightArithmetic(cr * Vector128.Create(91881) + half, 16), low), high);
                g = Vector128.Min(Vector128.Max(luma + Vector128.ShiftRightArithmetic(cb * Vector128.Create(-22554) - cr * Vector128.Create(46802) + half, 16), low), high);
                bl = Vector128.Min(Vector128.Max(luma + Vector128.ShiftRightArithmetic(cb * Vector128.Create(116130) + half, 16), low), high);
            }
            Vector128<int> pixels = r | Vector128.ShiftLeft(g, 8) | Vector128.ShiftLeft(bl, 16) | Vector128.Create(unchecked((int)0xFF000000));
            pixels.AsByte().StoreUnsafe(ref target, (nuint)offset);
        }

        // One full-resolution row of a component, upsampled into scratch unless it is already full size.
        private static ReadOnlySpan<byte> Row(ref Frame f, int index, int y, Span<byte> scratch, ushort[] sums)
        {
            ref Component c = ref f.components[index];
            int stride = c.blocksX * 8;
            ReadOnlySpan<byte> plane = f.planes.AsSpan(c.planeStart, stride * c.blocksY * 8);
            int width = c.width;
            switch (c.upsample)
            {
                case Full:
                    return plane.Slice(y * stride, stride);
                case FancyH2V1:
                    ColumnSums(plane.Slice(y * stride, width), default, sums, width);
                    Interleave(sums, scratch, width, 1, 2, 2);
                    return scratch;
                case FancyH2V2:
                {
                    int cy = y >> 1;
                    int other = (y & 1) == 0 ? Math.Max(cy - 1, 0) : Math.Min(cy + 1, c.height - 1);
                    ColumnSums(plane.Slice(cy * stride, width), plane.Slice(other * stride, width), sums, width);
                    Interleave(sums, scratch, width, 8, 7, 4);
                    return scratch;
                }
                case FancyH1V2:
                {
                    int cy = y >> 1;
                    bool upper = (y & 1) == 0;
                    int other = upper ? Math.Max(cy - 1, 0) : Math.Min(cy + 1, c.height - 1);
                    int bias = upper ? 1 : 2;
                    ReadOnlySpan<byte> near = plane.Slice(cy * stride, width), far = plane.Slice(other * stride, width);
                    for (int x = 0; x < width; x++) scratch[x] = (byte)((near[x] * 3 + far[x] + bias) >> 2);
                    return scratch;
                }
                default:
                {
                    int hRatio = f.hmax / c.h;
                    ReadOnlySpan<byte> input = plane.Slice(y / (f.vmax / c.v) * stride, width);
                    for (int x = 0; x < width; x++) scratch.Slice(x * hRatio, hRatio).Fill(input[x]);
                    return scratch;
                }
            }
        }

        // Sums at 1..width with the edge sums repeated at 0 and width + 1: the row itself, or 3 x near + far for h2v2.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ColumnSums(ReadOnlySpan<byte> near, ReadOnlySpan<byte> far, ushort[] sums, int width)
        {
            ref byte n = ref MemoryMarshal.GetReference(near);
            ref ushort s = ref MemoryMarshal.GetArrayDataReference(sums);
            bool vertical = far.Length > 0;
            ref byte fr = ref vertical ? ref MemoryMarshal.GetReference(far) : ref n;
            int x = 0;
            if (Avx2.IsSupported)
            {
                for (; x + 16 <= width; x += 16)
                {
                    Vector256<short> sum = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref n, (nuint)x));
                    if (vertical) sum = sum * Vector256.Create((short)3) + Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref fr, (nuint)x));
                    sum.AsUInt16().StoreUnsafe(ref s, (nuint)x + 1);
                }
            }
            else if (Sse2.IsSupported || AdvSimd.Arm64.IsSupported)
            {
                for (; x + 16 <= width; x += 16)
                {
                    (Vector128<ushort> n0, Vector128<ushort> n1) = Vector128.Widen(Vector128.LoadUnsafe(ref n, (nuint)x));
                    if (vertical)
                    {
                        (Vector128<ushort> f0, Vector128<ushort> f1) = Vector128.Widen(Vector128.LoadUnsafe(ref fr, (nuint)x));
                        n0 = n0 * Vector128.Create((ushort)3) + f0;
                        n1 = n1 * Vector128.Create((ushort)3) + f1;
                    }
                    n0.StoreUnsafe(ref s, (nuint)x + 1);
                    n1.StoreUnsafe(ref s, (nuint)x + 9);
                }
            }
            for (; x < width; x++)
                Unsafe.Add(ref s, x + 1) = (ushort)(vertical ? Unsafe.Add(ref n, x) * 3 + Unsafe.Add(ref fr, x) : Unsafe.Add(ref n, x));
            sums[0] = sums[1];
            sums[width + 1] = sums[width];
        }

        // libjpeg's fancy upsampling over the sums: output 2x leans on x - 1, 2x + 1 on x + 1.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Interleave(ushort[] sums, Span<byte> output, int width, int evenBias, int oddBias, int shift)
        {
            ref ushort s = ref MemoryMarshal.GetArrayDataReference(sums);
            ref byte o = ref MemoryMarshal.GetReference(output);
            int x = 0;
            if (Avx2.IsSupported)
            {
                Vector256<short> three = Vector256.Create((short)3), even = Vector256.Create((short)evenBias), odd = Vector256.Create((short)oddBias);
                for (; x + 16 <= width; x += 16)
                {
                    Vector256<short> left = Vector256.LoadUnsafe(ref s, (nuint)x).AsInt16();
                    Vector256<short> centre = Vector256.LoadUnsafe(ref s, (nuint)x + 1).AsInt16() * three;
                    Vector256<short> right = Vector256.LoadUnsafe(ref s, (nuint)x + 2).AsInt16();
                    Vector256<short> e = Vector256.ShiftRightLogical(centre + left + even, shift);
                    Vector256<short> d = Vector256.ShiftRightLogical(centre + right + odd, shift);
                    Avx2.PackUnsignedSaturate(Avx2.UnpackLow(e, d), Avx2.UnpackHigh(e, d)).StoreUnsafe(ref o, (nuint)(x * 2));
                }
            }
            if (Sse2.IsSupported || AdvSimd.Arm64.IsSupported)
            {
                Vector128<short> three = Vector128.Create((short)3), even = Vector128.Create((short)evenBias), odd = Vector128.Create((short)oddBias);
                for (; x + 8 <= width; x += 8)
                {
                    Vector128<short> left = Vector128.LoadUnsafe(ref s, (nuint)x).AsInt16();
                    Vector128<short> centre = Vector128.LoadUnsafe(ref s, (nuint)x + 1).AsInt16() * three;
                    Vector128<short> right = Vector128.LoadUnsafe(ref s, (nuint)x + 2).AsInt16();
                    Vector128<short> e = Vector128.ShiftRightLogical(centre + left + even, shift);
                    Vector128<short> d = Vector128.ShiftRightLogical(centre + right + odd, shift);
                    Vector128.Narrow(ZipLow(e, d), ZipHigh(e, d)).AsByte().StoreUnsafe(ref o, (nuint)(x * 2));
                }
            }
            for (; x < width; x++)
            {
                int centre = Unsafe.Add(ref s, x + 1) * 3;
                Unsafe.Add(ref o, x * 2) = (byte)((centre + Unsafe.Add(ref s, x) + evenBias) >> shift);
                Unsafe.Add(ref o, x * 2 + 1) = (byte)((centre + Unsafe.Add(ref s, x + 2) + oddBias) >> shift);
            }
        }

        private static byte Clamp(int v) => (uint)v <= 255 ? (byte)v : v < 0 ? (byte)0 : (byte)255;
        #endregion
    }
}
