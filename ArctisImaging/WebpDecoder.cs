using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Arctis.Imaging
{
    // WebP in its simple or extended RIFF container: lossless (VP8L), lossy (VP8) with or without an ALPH plane, and
    // the first frame of an animation on a transparent canvas.
    internal static class WebpDecoder
    {
        private const int Lossless = 0, Lossy = 1;
        private const int Predictor = 0, CrossColour = 1, SubtractGreen = 2, ColourIndexing = 3;
        private const int RootBits = 8, MaxCodeLength = 15;
        private const int LiteralCodes = 256, LengthCodes = 24, DistanceCodes = 40, CodeLengthCodes = 19;
        private const uint CacheMultiplier = 0x1E35A7BD;

        private static readonly byte[] CodeLengthOrder = { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

        // the 120 short distance codes as dy << 4 | (8 - dx)
        private static readonly byte[] CodeToPlane =
        {
            0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1a, 0x26, 0x2a, 0x38, 0x05, 0x37, 0x39, 0x15, 0x1b, 0x36, 0x3a,
            0x25, 0x2b, 0x48, 0x04, 0x47, 0x49, 0x14, 0x1c, 0x35, 0x3b, 0x46, 0x4a, 0x24, 0x2c, 0x58, 0x45, 0x4b, 0x34, 0x3c, 0x03,
            0x57, 0x59, 0x13, 0x1d, 0x56, 0x5a, 0x23, 0x2d, 0x44, 0x4c, 0x55, 0x5b, 0x33, 0x3d, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1e,
            0x66, 0x6a, 0x22, 0x2e, 0x54, 0x5c, 0x43, 0x4d, 0x65, 0x6b, 0x32, 0x3e, 0x78, 0x01, 0x77, 0x79, 0x53, 0x5d, 0x11, 0x1f,
            0x64, 0x6c, 0x42, 0x4e, 0x76, 0x7a, 0x21, 0x2f, 0x75, 0x7b, 0x31, 0x3f, 0x63, 0x6d, 0x52, 0x5e, 0x00, 0x74, 0x7c, 0x41,
            0x4f, 0x10, 0x20, 0x62, 0x6e, 0x30, 0x73, 0x7d, 0x51, 0x5f, 0x40, 0x72, 0x7e, 0x61, 0x6f, 0x50, 0x71, 0x7f, 0x60, 0x70
        };

        // the canvas, and where the image (or an animation's first frame) sits on it
        private struct Container
        {
            public int kind, width, height, payloadAt, payloadLength, alphaAt, alphaLength;
            public int frameX, frameY, frameWidth, frameHeight;
            public bool alpha;
        }

        private struct Transform
        {
            public int type, bits, xsize;
            public uint[]? data;
        }

        // LSB-first bit reader that reads zeros past the end and counts them
        private ref struct Bits
        {
            public ReadOnlySpan<byte> data;
            public int pos, count, overread;
            public ulong buffer;

            public void Refill()
            {
                while (count <= 56)
                {
                    if (pos < data.Length) buffer |= (ulong)data[pos++] << count;
                    else overread += 8;
                    count += 8;
                }
            }

            public uint Read(int n)
            {
                if (count < n) Refill();
                uint v = (uint)(buffer & ((1UL << n) - 1));
                buffer >>= n;
                count -= n;
                return v;
            }

            public void Skip(int n)
            {
                buffer >>= n;
                count -= n;
            }
        }

        internal static bool TryReadHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (!TryReadContainer(data, out Container c)) return false;
            header = new ImageHeader(ImageFormat.Webp, c.width, c.height, c.alpha ? 4 : 3, 8, c.alpha);
            return true;
        }

        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int pitch, PixelLayout layout)
        {
            if (!TryReadContainer(data, out Container c)) throw new ImageFormatException("WebP container is invalid");
            ReadOnlySpan<byte> stream = data.Slice(c.payloadAt, c.payloadLength);
            int fw = c.frameWidth, fh = c.frameHeight;
            bool whole = fw == c.width && fh == c.height;
            if (c.kind == Lossless && whole)
            {
                DecodeLossless(stream, fw, fh, destination, pitch, layout);
                return;
            }
            if (c.kind == Lossy && whole && layout == PixelLayout.RGBA8)
            {
                Vp8Decoder.Decode(stream, fw, fh, destination, pitch);
                if (c.alphaLength > 0) DecodeAlpha(data.Slice(c.alphaAt, c.alphaLength), fw, fh, destination, pitch);
                return;
            }

            byte[] frame = ArrayPool<byte>.Shared.Rent(fw * fh * 4);
            try
            {
                Span<byte> rgba = frame.AsSpan(0, fw * fh * 4);
                if (c.kind == Lossless) LosslessToRgba(stream, fw, fh, rgba);
                else
                {
                    Vp8Decoder.Decode(stream, fw, fh, rgba, fw * 4);
                    if (c.alphaLength > 0) DecodeAlpha(data.Slice(c.alphaAt, c.alphaLength), fw, fh, rgba, fw * 4);
                }
                if (whole)
                    for (int y = 0; y < fh; y++) PixelRows.FromRgba(rgba.Slice(y * fw * 4), destination.Slice(y * pitch), fw, layout);
                else Place(rgba, c, destination, pitch, layout);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frame);
            }
        }

        // Decodes a still lossless image straight into the destination rows.
        private static void DecodeLossless(ReadOnlySpan<byte> stream, int width, int height, Span<byte> destination, int pitch, PixelLayout layout)
        {
            Bits br = LosslessReader(stream, width, height);
            uint[] pixels = DecodeArgb(ref br, width, height);
            byte[] rgba = ArrayPool<byte>.Shared.Rent(width * 4);
            try
            {
                for (int y = 0; y < height; y++)
                {
                    ArgbToRgba(pixels.AsSpan(y * width, width), rgba);
                    PixelRows.FromRgba(rgba, destination.Slice(y * pitch), width, layout);
                }
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(pixels);
                ArrayPool<byte>.Shared.Return(rgba);
            }
        }

        // Decodes a lossless animation frame into tight RGBA8 rows.
        private static void LosslessToRgba(ReadOnlySpan<byte> stream, int width, int height, Span<byte> rgba)
        {
            Bits br = LosslessReader(stream, width, height);
            uint[] pixels = DecodeArgb(ref br, width, height);
            try
            {
                ArgbToRgba(pixels.AsSpan(0, width * height), rgba);
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(pixels);
            }
        }

        // Checks a VP8L header against the frame size and returns a reader past it.
        private static Bits LosslessReader(ReadOnlySpan<byte> stream, int width, int height)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(stream.Slice(1));
            int w = (int)(size & 0x3FFF) + 1, h = (int)(size >> 14 & 0x3FFF) + 1;
            if (w != width || h != height) throw new ImageFormatException($"WebP image is {w}x{h} inside a {width}x{height} frame");
            return new Bits { data = stream.Slice(5) };
        }

        private static void ArgbToRgba(ReadOnlySpan<uint> argb, Span<byte> rgba)
        {
            for (int x = 0; x < argb.Length; x++)
            {
                uint p = argb[x];
                rgba[x * 4] = (byte)(p >> 16);
                rgba[x * 4 + 1] = (byte)(p >> 8);
                rgba[x * 4 + 2] = (byte)p;
                rgba[x * 4 + 3] = (byte)(p >> 24);
            }
        }

        // Writes a frame onto a transparent canvas the size of the destination.
        private static void Place(ReadOnlySpan<byte> frame, in Container c, Span<byte> destination, int pitch, PixelLayout layout)
        {
            int rowBytes = c.width * 4, frameBytes = c.frameWidth * 4;
            byte[] row = ArrayPool<byte>.Shared.Rent(rowBytes);
            try
            {
                Span<byte> canvas = row.AsSpan(0, rowBytes);
                for (int y = 0; y < c.height; y++)
                {
                    canvas.Clear();
                    int fy = y - c.frameY;
                    if (fy >= 0 && fy < c.frameHeight) frame.Slice(fy * frameBytes, frameBytes).CopyTo(canvas.Slice(c.frameX * 4));
                    PixelRows.FromRgba(canvas, destination.Slice(y * pitch), c.width, layout);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(row);
            }
        }

        #region alpha
        // Decodes an ALPH chunk, raw or VP8L-coded and then unfiltered, into the alpha bytes of RGBA8 rows.
        private static void DecodeAlpha(ReadOnlySpan<byte> chunk, int width, int height, Span<byte> rgba, int pitch)
        {
            int method = chunk[0] & 3, filter = chunk[0] >> 2 & 3;
            if (method > 1 || (chunk[0] >> 4 & 3) > 1 || chunk[0] >> 6 != 0) throw new ImageFormatException($"WebP alpha header 0x{chunk[0]:X2} is invalid");
            int count = width * height;
            byte[] plane = ArrayPool<byte>.Shared.Rent(count);
            try
            {
                if (method == 0)
                {
                    if (chunk.Length - 1 < count) throw new ImageFormatException("WebP alpha plane is truncated");
                    chunk.Slice(1, count).CopyTo(plane);
                }
                else
                {
                    Bits br = new Bits { data = chunk.Slice(1) };
                    uint[] argb = DecodeArgb(ref br, width, height);
                    try
                    {
                        for (int i = 0; i < count; i++) plane[i] = (byte)(argb[i] >> 8);
                    }
                    finally
                    {
                        ArrayPool<uint>.Shared.Return(argb);
                    }
                }
                for (int y = 0; y < height; y++)
                {
                    Span<byte> row = plane.AsSpan(y * width, width);
                    Unfilter(filter, y > 0 ? plane.AsSpan((y - 1) * width, width) : default, row);
                    int at = y * pitch + 3;
                    for (int x = 0; x < row.Length; x++) rgba[at + x * 4] = row[x];
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(plane);
            }
        }

        // Undoes one row's horizontal, vertical or gradient prediction in place; above is the unfiltered row before it,
        // empty on the first row, where every filter predicts from the left.
        private static void Unfilter(int filter, ReadOnlySpan<byte> above, Span<byte> row)
        {
            if (filter == 0) return;
            if (above.IsEmpty || filter == 1)
            {
                int pred = above.IsEmpty ? 0 : above[0];
                for (int i = 0; i < row.Length; i++) pred = row[i] = (byte)(pred + row[i]);
                return;
            }
            if (filter == 2)
            {
                for (int i = 0; i < row.Length; i++) row[i] = (byte)(above[i] + row[i]);
                return;
            }
            int left = above[0], topLeft = above[0];
            for (int i = 0; i < row.Length; i++)
            {
                int top = above[i], g = left + top - topLeft;
                left = row[i] = (byte)(row[i] + (g < 0 ? 0 : g > 255 ? 255 : g));
                topLeft = top;
            }
        }
        #endregion

        // Decodes a VP8L image stream (transforms, then the entropy-coded image) into rented ARGB pixels the caller returns.
        private static uint[] DecodeArgb(ref Bits br, int width, int height)
        {
            Transform[] transforms = new Transform[4];
            int transformCount = 0, seen = 0, xsize = width;
            uint[] pixels = Array.Empty<uint>(), spare = Array.Empty<uint>();
            bool done = false;
            try
            {
                while (br.Read(1) == 1)
                {
                    int type = (int)br.Read(2);
                    if ((seen & 1 << type) != 0) throw new ImageFormatException($"WebP repeats transform {type}");
                    seen |= 1 << type;
                    Transform t = new Transform { type = type, xsize = xsize };
                    if (type == Predictor || type == CrossColour)
                    {
                        t.bits = (int)br.Read(3) + 2;
                        int blocks = DivRoundUp(xsize, t.bits) * DivRoundUp(height, t.bits);
                        t.data = ArrayPool<uint>.Shared.Rent(blocks);
                        DecodeEntropyImage(ref br, DivRoundUp(xsize, t.bits), DivRoundUp(height, t.bits), false, t.data.AsSpan(0, blocks));
                    }
                    else if (type == ColourIndexing)
                    {
                        int colours = (int)br.Read(8) + 1;
                        t.bits = colours > 16 ? 0 : colours > 4 ? 1 : colours > 2 ? 2 : 3;
                        t.data = ArrayPool<uint>.Shared.Rent(256);
                        Array.Clear(t.data, 0, 256);
                        DecodeEntropyImage(ref br, colours, 1, false, t.data.AsSpan(0, colours));
                        for (int i = 1; i < colours; i++) t.data[i] = AddPixels(t.data[i], t.data[i - 1]);
                        xsize = DivRoundUp(xsize, t.bits);
                    }
                    transforms[transformCount++] = t;
                }

                int total = width * height;
                pixels = ArrayPool<uint>.Shared.Rent(total);
                DecodeEntropyImage(ref br, xsize, height, true, pixels.AsSpan(0, xsize * height));
                if (br.overread > br.count) throw new ImageFormatException("WebP lossless stream ends early");

                for (int i = transformCount - 1; i >= 0; i--)
                {
                    ref Transform t = ref transforms[i];
                    switch (t.type)
                    {
                        case Predictor: UndoPredictor(pixels, t.xsize, height, t.bits, t.data!); break;
                        case CrossColour: UndoCrossColour(pixels, t.xsize, height, t.bits, t.data!); break;
                        case SubtractGreen: AddGreen(pixels.AsSpan(0, t.xsize * height)); break;
                        default:
                            if (spare.Length == 0) spare = ArrayPool<uint>.Shared.Rent(total);
                            ExpandIndices(pixels, spare, t.xsize, height, t.bits, t.data!);
                            (pixels, spare) = (spare, pixels);
                            break;
                    }
                }
                done = true;
                return pixels;
            }
            finally
            {
                for (int i = 0; i < transformCount; i++)
                    if (transforms[i].data is uint[] owned) ArrayPool<uint>.Shared.Return(owned);
                if (!done && pixels.Length > 0) ArrayPool<uint>.Shared.Return(pixels);
                if (spare.Length > 0) ArrayPool<uint>.Shared.Return(spare);
            }
        }

        #region entropy-coded images
        // Decodes xsize by ysize ARGB pixels: colour cache, then (main image only) meta prefix codes, prefix codes, LZ77 data.
        private static void DecodeEntropyImage(ref Bits br, int xsize, int ysize, bool main, Span<uint> pixels)
        {
            int cacheBits = 0;
            if (br.Read(1) == 1)
            {
                cacheBits = (int)br.Read(4);
                if (cacheBits < 1 || cacheBits > 11) throw new ImageFormatException($"WebP colour cache of {cacheBits} bits");
            }

            int prefixBits = 0, prefixXsize = 0, groups = 1;
            uint[] meta = Array.Empty<uint>();
            uint[] tables = Array.Empty<uint>(), cache = Array.Empty<uint>();
            int[] starts = Array.Empty<int>(), sorted = Array.Empty<int>();
            byte[] lengths = Array.Empty<byte>();
            try
            {
                if (main && br.Read(1) == 1)
                {
                    prefixBits = (int)br.Read(3) + 2;
                    prefixXsize = DivRoundUp(xsize, prefixBits);
                    int metaCount = prefixXsize * DivRoundUp(ysize, prefixBits);
                    meta = ArrayPool<uint>.Shared.Rent(metaCount);
                    DecodeEntropyImage(ref br, prefixXsize, DivRoundUp(ysize, prefixBits), false, meta.AsSpan(0, metaCount));
                    for (int i = 0; i < metaCount; i++)
                    {
                        meta[i] = meta[i] >> 8 & 0xFFFF;
                        if (meta[i] >= groups) groups = (int)meta[i] + 1;
                    }
                }

                int cacheSize = cacheBits > 0 ? 1 << cacheBits : 0;
                int greenCodes = LiteralCodes + LengthCodes + cacheSize;
                starts = ArrayPool<int>.Shared.Rent(groups * 5);
                lengths = ArrayPool<byte>.Shared.Rent(greenCodes);
                sorted = ArrayPool<int>.Shared.Rent(greenCodes);
                tables = ArrayPool<uint>.Shared.Rent(groups * 5 * (1 << RootBits));
                int used = 0;
                for (int g = 0; g < groups; g++)
                    for (int k = 0; k < 5; k++)
                    {
                        int alphabet = k == 0 ? greenCodes : k == 4 ? DistanceCodes : LiteralCodes;
                        ReadCodeLengths(ref br, alphabet, lengths, sorted);
                        int size = BuildTable(default, false, lengths.AsSpan(0, alphabet), sorted);
                        if (size == 0) throw new ImageFormatException("WebP prefix code is not a complete tree");
                        if (used + size > tables.Length)
                        {
                            uint[] grown = ArrayPool<uint>.Shared.Rent(Math.Max(tables.Length * 2, used + size));
                            tables.AsSpan(0, used).CopyTo(grown);
                            ArrayPool<uint>.Shared.Return(tables);
                            tables = grown;
                        }
                        BuildTable(tables.AsSpan(used, size), true, lengths.AsSpan(0, alphabet), sorted);
                        starts[g * 5 + k] = used;
                        used += size;
                    }

                if (cacheSize > 0)
                {
                    cache = ArrayPool<uint>.Shared.Rent(cacheSize);
                    Array.Clear(cache, 0, cacheSize);
                }
                DecodePixels(ref br, xsize, pixels, tables, starts, meta, prefixBits, prefixXsize, cache, cacheBits);
            }
            finally
            {
                if (meta.Length > 0) ArrayPool<uint>.Shared.Return(meta);
                if (tables.Length > 0) ArrayPool<uint>.Shared.Return(tables);
                if (cache.Length > 0) ArrayPool<uint>.Shared.Return(cache);
                if (starts.Length > 0) ArrayPool<int>.Shared.Return(starts);
                if (sorted.Length > 0) ArrayPool<int>.Shared.Return(sorted);
                if (lengths.Length > 0) ArrayPool<byte>.Shared.Return(lengths);
            }
        }

        // Literals, back-references and colour cache hits until every pixel is written.
        private static void DecodePixels(ref Bits br, int xsize, Span<uint> pixels, uint[] tables, int[] starts, uint[] meta, int prefixBits, int prefixXsize,
            uint[] cache, int cacheBits)
        {
            int total = pixels.Length, pos = 0, x = 0, y = 0;
            int mask = (1 << prefixBits) - 1;
            int cacheShift = 32 - cacheBits;
            bool hasMeta = meta.Length > 0, hasCache = cache.Length > 0, refresh = true;
            int group = 0;
            while (pos < total)
            {
                if (hasMeta && (refresh || (x & mask) == 0))
                {
                    group = (int)meta[(y >> prefixBits) * prefixXsize + (x >> prefixBits)] * 5;
                    refresh = false;
                }
                int green = ReadSymbol(ref br, tables.AsSpan(starts[group]));
                if (green < LiteralCodes)
                {
                    int red = ReadSymbol(ref br, tables.AsSpan(starts[group + 1]));
                    int blue = ReadSymbol(ref br, tables.AsSpan(starts[group + 2]));
                    int alpha = ReadSymbol(ref br, tables.AsSpan(starts[group + 3]));
                    uint argb = (uint)(alpha << 24 | red << 16 | green << 8 | blue);
                    pixels[pos++] = argb;
                    if (hasCache) cache[(argb * CacheMultiplier) >> cacheShift] = argb;
                    if (++x == xsize)
                    {
                        x = 0;
                        y++;
                    }
                }
                else if (green < LiteralCodes + LengthCodes)
                {
                    int length = PrefixValue(ref br, green - LiteralCodes);
                    int distanceCode = PrefixValue(ref br, ReadSymbol(ref br, tables.AsSpan(starts[group + 4])));
                    int distance = PlaneDistance(xsize, distanceCode);
                    if (distance > pos || length > total - pos) throw new ImageFormatException($"WebP back-reference of {length} at distance {distance} from pixel {pos}");
                    for (int i = 0; i < length; i++)
                    {
                        uint argb = pixels[pos - distance];
                        pixels[pos++] = argb;
                        if (hasCache) cache[(argb * CacheMultiplier) >> cacheShift] = argb;
                    }
                    x += length;
                    while (x >= xsize)
                    {
                        x -= xsize;
                        y++;
                    }
                    refresh = true;
                }
                else
                {
                    int index = green - LiteralCodes - LengthCodes;
                    if (index >= cache.Length) throw new ImageFormatException($"WebP colour cache index {index} without a cache that large");
                    uint argb = cache[index];
                    pixels[pos++] = argb;
                    cache[(argb * CacheMultiplier) >> cacheShift] = argb;
                    if (++x == xsize)
                    {
                        x = 0;
                        y++;
                    }
                }
            }
        }

        // A length or distance: the prefix symbol picks a range, extra bits pick within it.
        private static int PrefixValue(ref Bits br, int prefix)
        {
            if (prefix < 4) return prefix + 1;
            int extra = (prefix - 2) >> 1;
            int offset = (2 + (prefix & 1)) << extra;
            return offset + (int)br.Read(extra) + 1;
        }

        private static int PlaneDistance(int xsize, int code)
        {
            if (code > 120) return code - 120;
            int v = CodeToPlane[code - 1];
            int distance = (v >> 4) * xsize + 8 - (v & 15);
            return distance >= 1 ? distance : 1;
        }
        #endregion

        #region prefix codes
        // Reads one prefix code's lengths, simple (one or two symbols) or coded through the code-length code.
        private static void ReadCodeLengths(ref Bits br, int alphabet, byte[] lengths, int[] sorted)
        {
            Array.Clear(lengths, 0, alphabet);
            if (br.Read(1) == 1)
            {
                int symbols = (int)br.Read(1) + 1;
                int first = (int)br.Read(br.Read(1) == 1 ? 8 : 1);
                if (first >= alphabet) throw new ImageFormatException($"WebP prefix symbol {first} is outside an alphabet of {alphabet}");
                lengths[first] = 1;
                if (symbols == 2)
                {
                    int second = (int)br.Read(8);
                    if (second >= alphabet) throw new ImageFormatException($"WebP prefix symbol {second} is outside an alphabet of {alphabet}");
                    lengths[second] = 1;
                }
                return;
            }

            Span<byte> codeLengthLengths = stackalloc byte[CodeLengthCodes];
            codeLengthLengths.Clear();
            int count = (int)br.Read(4) + 4;
            for (int i = 0; i < count; i++) codeLengthLengths[CodeLengthOrder[i]] = (byte)br.Read(3);
            Span<uint> codeLengthTable = stackalloc uint[1 << RootBits];
            if (BuildTable(codeLengthTable, true, codeLengthLengths, sorted) == 0) throw new ImageFormatException("WebP code-length code is not a complete tree");

            int maxSymbol = alphabet;
            if (br.Read(1) == 1)
            {
                int lengthBits = 2 + 2 * (int)br.Read(3);
                maxSymbol = 2 + (int)br.Read(lengthBits);
                if (maxSymbol > alphabet) throw new ImageFormatException($"WebP code reads {maxSymbol} lengths for an alphabet of {alphabet}");
            }

            int previous = 8, symbol = 0;
            while (symbol < alphabet && maxSymbol-- > 0)
            {
                int code = ReadSymbol(ref br, codeLengthTable);
                if (code < 16)
                {
                    lengths[symbol++] = (byte)code;
                    if (code != 0) previous = code;
                    continue;
                }
                int repeat = code == 16 ? (int)br.Read(2) + 3 : code == 17 ? (int)br.Read(3) + 3 : (int)br.Read(7) + 11;
                if (symbol + repeat > alphabet) throw new ImageFormatException($"WebP code-length repeat runs past an alphabet of {alphabet}");
                lengths.AsSpan(symbol, repeat).Fill(code == 16 ? (byte)previous : (byte)0);
                symbol += repeat;
            }
        }

        // Builds the two-level lookup table for these code lengths; 0 when they are not a complete tree.
        private static int BuildTable(Span<uint> table, bool fill, ReadOnlySpan<byte> lengths, int[] sorted)
        {
            Span<int> count = stackalloc int[MaxCodeLength + 1];
            Span<int> offset = stackalloc int[MaxCodeLength + 2];
            count.Clear();
            for (int s = 0; s < lengths.Length; s++) count[lengths[s]]++;
            int symbols = lengths.Length - count[0];
            if (symbols == 0) return 0;
            offset[1] = 0;
            for (int len = 1; len <= MaxCodeLength; len++) offset[len + 1] = offset[len] + count[len];
            for (int s = 0; s < lengths.Length; s++)
                if (lengths[s] > 0) sorted[offset[lengths[s]]++] = s;

            int rootSize = 1 << RootBits;
            if (symbols == 1)
            {
                if (fill) table.Slice(0, rootSize).Fill((uint)sorted[0]);
                return rootSize;
            }

            int total = rootSize, key = 0, symbol = 0, nodes = 1, open = 1;
            for (int len = 1, step = 2; len <= RootBits; len++, step <<= 1)
            {
                open <<= 1;
                nodes += open;
                open -= count[len];
                if (open < 0) return 0;
                for (; count[len] > 0; count[len]--)
                {
                    if (fill) Replicate(table, key, step, rootSize, (uint)(len << 16 | sorted[symbol]));
                    symbol++;
                    key = NextKey(key, len);
                }
            }

            int low = -1, mask = rootSize - 1, sub = 0, subSize = rootSize;
            for (int len = RootBits + 1, step = 2; len <= MaxCodeLength; len++, step <<= 1)
            {
                open <<= 1;
                nodes += open;
                open -= count[len];
                if (open < 0) return 0;
                for (; count[len] > 0; count[len]--)
                {
                    if ((key & mask) != low)
                    {
                        sub += subSize;
                        int subBits = NextTableBits(count, len);
                        subSize = 1 << subBits;
                        total += subSize;
                        low = key & mask;
                        if (fill) table[low] = (uint)((subBits + RootBits) << 16 | (sub - low));
                    }
                    if (fill) Replicate(table.Slice(sub), key >> RootBits, step, subSize, (uint)((len - RootBits) << 16 | sorted[symbol]));
                    symbol++;
                    key = NextKey(key, len);
                }
            }
            return nodes == 2 * symbols - 1 ? total : 0;
        }

        // Writes value at key, key + step, ... below end.
        private static void Replicate(Span<uint> table, int key, int step, int end, uint value)
        {
            do
            {
                end -= step;
                table[key + end] = value;
            }
            while (end > 0);
        }

        // The next bit-reversed code of this length.
        private static int NextKey(int key, int len)
        {
            int step = 1 << (len - 1);
            while ((key & step) != 0) step >>= 1;
            return step != 0 ? (key & (step - 1)) + step : key;
        }

        // Bits of the second-level table that the codes from this length on need.
        private static int NextTableBits(ReadOnlySpan<int> count, int len)
        {
            int left = 1 << (len - RootBits);
            while (len < MaxCodeLength)
            {
                left -= count[len];
                if (left <= 0) break;
                len++;
                left <<= 1;
            }
            return len - RootBits;
        }

        private static int ReadSymbol(ref Bits br, scoped ReadOnlySpan<uint> table)
        {
            if (br.count < MaxCodeLength) br.Refill();
            uint v = (uint)br.buffer;
            int index = (int)(v & ((1 << RootBits) - 1));
            uint e = table[index];
            int n = (int)(e >> 16);
            if (n > RootBits)
            {
                br.Skip(RootBits);
                index += (int)(e & 0xFFFF) + (int)((v >> RootBits) & ((1u << (n - RootBits)) - 1));
                e = table[index];
                n = (int)(e >> 16);
            }
            br.Skip(n);
            return (int)(e & 0xFFFF);
        }
        #endregion

        #region inverse transforms
        private static void UndoPredictor(uint[] pixels, int xsize, int height, int bits, uint[] modes)
        {
            int blocksAcross = DivRoundUp(xsize, bits);
            Span<uint> first = pixels.AsSpan(0, xsize);
            first[0] = AddPixels(first[0], 0xFF000000u);
            for (int x = 1; x < first.Length; x++) first[x] = AddPixels(first[x], first[x - 1]);
            for (int y = 1; y < height; y++)
            {
                Span<uint> row = pixels.AsSpan(y * xsize, xsize);
                ReadOnlySpan<uint> up = pixels.AsSpan((y - 1) * xsize, xsize + 1);
                row[0] = AddPixels(row[0], up[0]);
                int modeRow = (y >> bits) * blocksAcross;
                for (int x = 1; x < row.Length; x++)
                {
                    int mode = (int)(modes[modeRow + (x >> bits)] >> 8 & 15);
                    uint l = row[x - 1], t = up[x], tl = up[x - 1], tr = up[x + 1];
                    uint prediction = mode switch
                    {
                        1 => l,
                        2 => t,
                        3 => tr,
                        4 => tl,
                        5 => Average2(Average2(l, tr), t),
                        6 => Average2(l, tl),
                        7 => Average2(l, t),
                        8 => Average2(tl, t),
                        9 => Average2(t, tr),
                        10 => Average2(Average2(l, tl), Average2(t, tr)),
                        11 => Select(t, l, tl),
                        12 => ClampAddSubtractFull(l, t, tl),
                        13 => ClampAddSubtractHalf(Average2(l, t), tl),
                        _ => 0xFF000000u
                    };
                    row[x] = AddPixels(row[x], prediction);
                }
            }
        }

        private static void UndoCrossColour(uint[] pixels, int xsize, int height, int bits, uint[] elements)
        {
            int blocksAcross = DivRoundUp(xsize, bits);
            for (int y = 0; y < height; y++)
            {
                Span<uint> row = pixels.AsSpan(y * xsize, xsize);
                int elementRow = (y >> bits) * blocksAcross;
                for (int x = 0; x < row.Length; x++)
                {
                    uint m = elements[elementRow + (x >> bits)];
                    int greenToRed = (sbyte)m, greenToBlue = (sbyte)(m >> 8), redToBlue = (sbyte)(m >> 16);
                    uint argb = row[x];
                    int green = (sbyte)(argb >> 8);
                    int red = (int)(argb >> 16 & 0xFF) + (greenToRed * green >> 5) & 0xFF;
                    int blue = (int)(argb & 0xFF) + (greenToBlue * green >> 5) + (redToBlue * (sbyte)red >> 5) & 0xFF;
                    row[x] = argb & 0xFF00FF00u | (uint)red << 16 | (uint)blue;
                }
            }
        }

        private static void AddGreen(Span<uint> pixels)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                uint argb = pixels[i];
                uint green = argb >> 8 & 0xFF;
                pixels[i] = argb & 0xFF00FF00u | (argb & 0x00FF00FFu) + (green << 16 | green) & 0x00FF00FFu;
            }
        }

        // Unpacks palette indices, 1 << bits to a packed pixel's green, into xsize-wide colours.
        private static void ExpandIndices(uint[] packed, uint[] output, int xsize, int height, int bits, uint[] palette)
        {
            int packedWidth = DivRoundUp(xsize, bits);
            int bitsPerIndex = 8 >> bits, perPixel = 1 << bits;
            uint indexMask = (1u << bitsPerIndex) - 1;
            for (int y = 0; y < height; y++)
            {
                int from = y * packedWidth;
                Span<uint> to = output.AsSpan(y * xsize, xsize);
                for (int x = 0; x < to.Length; x++)
                {
                    uint green = packed[from + (x >> bits)] >> 8 & 0xFF;
                    to[x] = palette[(green >> ((x & (perPixel - 1)) * bitsPerIndex)) & indexMask];
                }
            }
        }

        private static uint AddPixels(uint a, uint b) => (a & 0xFF00FF00u) + (b & 0xFF00FF00u) & 0xFF00FF00u | (a & 0x00FF00FFu) + (b & 0x00FF00FFu) & 0x00FF00FFu;

        private static uint Average2(uint a, uint b) => (((a ^ b) & 0xFEFEFEFEu) >> 1) + (a & b);

        // The top or left pixel, whichever is nearer the gradient estimate L + T - TL.
        private static uint Select(uint t, uint l, uint tl)
        {
            int towardTop = 0;
            for (int shift = 0; shift < 32; shift += 8)
            {
                int tc = (int)(t >> shift & 0xFF), lc = (int)(l >> shift & 0xFF), tlc = (int)(tl >> shift & 0xFF);
                towardTop += Math.Abs(lc - tlc) - Math.Abs(tc - tlc);
            }
            return towardTop <= 0 ? t : l;
        }

        private static uint ClampAddSubtractFull(uint a, uint b, uint c)
        {
            uint result = 0;
            for (int shift = 0; shift < 32; shift += 8)
                result |= (uint)Math.Clamp((int)(a >> shift & 0xFF) + (int)(b >> shift & 0xFF) - (int)(c >> shift & 0xFF), 0, 255) << shift;
            return result;
        }

        private static uint ClampAddSubtractHalf(uint a, uint b)
        {
            uint result = 0;
            for (int shift = 0; shift < 32; shift += 8)
            {
                int ac = (int)(a >> shift & 0xFF), bc = (int)(b >> shift & 0xFF);
                result |= (uint)Math.Clamp(ac + (ac - bc) / 2, 0, 255) << shift;
            }
            return result;
        }

        private static int DivRoundUp(int value, int bits) => (value + (1 << bits) - 1) >> bits;
        #endregion

        #region container
        // Finds the image chunk in a simple (VP8 / VP8L) or extended (VP8X) file; in an animation, the first frame's.
        private static bool TryReadContainer(ReadOnlySpan<byte> data, out Container c)
        {
            c = default;
            if (data.Length < 20 || !IsTag(data, 0, "RIFF") || !IsTag(data, 8, "WEBP")) return false;
            long end = Math.Min(data.Length, 8L + BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4)));
            int at = 12;
            bool extended = false, animated = false;
            while (at + 8 <= end)
            {
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at + 4));
                int payload = at + 8;
                if (payload + (long)size > end) return false;
                if (IsTag(data, at, "VP8X") && at == 12)
                {
                    if (size < 10) return false;
                    byte flags = data[payload];
                    c.width = c.frameWidth = (int)ReadUInt24(data, payload + 4) + 1;
                    c.height = c.frameHeight = (int)ReadUInt24(data, payload + 7) + 1;
                    c.alpha = (flags & 0x10) != 0;
                    animated = (flags & 0x02) != 0;
                    extended = true;
                }
                else if (animated && IsTag(data, at, "ANMF"))
                {
                    // the frame's own chunks follow its 16-byte header; the walk ends inside it
                    if (size < 16) return false;
                    c.frameX = (int)ReadUInt24(data, payload) * 2;
                    c.frameY = (int)ReadUInt24(data, payload + 3) * 2;
                    c.frameWidth = (int)ReadUInt24(data, payload + 6) + 1;
                    c.frameHeight = (int)ReadUInt24(data, payload + 9) + 1;
                    if (c.frameX + c.frameWidth > c.width || c.frameY + c.frameHeight > c.height) return false;
                    animated = false;
                    end = payload + size;
                    at = payload + 16;
                    continue;
                }
                else if (IsTag(data, at, "ALPH"))
                {
                    c.alphaAt = payload;
                    c.alphaLength = (int)size;
                }
                else if (IsTag(data, at, "VP8L"))
                {
                    if (size < 5 || data[payload] != 0x2F) return false;
                    uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(payload + 1));
                    if (bits >> 29 != 0) return false;
                    if (!extended)
                    {
                        c.width = c.frameWidth = (int)(bits & 0x3FFF) + 1;
                        c.height = c.frameHeight = (int)(bits >> 14 & 0x3FFF) + 1;
                        c.alpha = (bits >> 28 & 1) != 0;
                    }
                    c.kind = Lossless;
                    c.payloadAt = payload;
                    c.payloadLength = (int)size;
                    return true;
                }
                else if (IsTag(data, at, "VP8 "))
                {
                    if (size < 10 || data[payload + 3] != 0x9D || data[payload + 4] != 0x01 || data[payload + 5] != 0x2A) return false;
                    if (!extended)
                    {
                        c.width = c.frameWidth = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(payload + 6)) & 0x3FFF;
                        c.height = c.frameHeight = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(payload + 8)) & 0x3FFF;
                    }
                    c.alpha |= c.alphaLength > 0;
                    c.kind = Lossy;
                    c.payloadAt = payload;
                    c.payloadLength = (int)size;
                    return c.width > 0 && c.height > 0;
                }
                else if (!extended) return false;
                at = payload + (int)size + (int)(size & 1);
            }
            return false;
        }

        private static bool IsTag(ReadOnlySpan<byte> data, int at, string tag) =>
            data[at] == tag[0] && data[at + 1] == tag[1] && data[at + 2] == tag[2] && data[at + 3] == tag[3];

        private static uint ReadUInt24(ReadOnlySpan<byte> data, int at) => data[at] | (uint)data[at + 1] << 8 | (uint)data[at + 2] << 16;
        #endregion
    }
}
