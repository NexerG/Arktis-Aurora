using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Arctis.Imaging
{
    // TIFF, first image only: 1-16-bit grey, palette, RGB and CMYK with optional alpha, chunky or planar, in strips or
    // tiles; uncompressed, PackBits, LZW, Deflate or JPEG, with or without the horizontal predictor.
    internal static class TiffDecoder
    {
        private const int NoCompression = 1, Lzw = 5, Jpeg = 7, Deflate = 8, OldDeflate = 32946, PackBits = 32773;
        private const int WhiteIsZero = 0, BlackIsZero = 1, Rgb = 2, Palette = 3, Cmyk = 5, YCbCr = 6;
        private const int LzwClear = 256, LzwEnd = 257, LzwFirst = 258, LzwMaxCodes = 4096;

        // where a tag's values sit: type 1, 3 or 4, and the position of the first value; type 0 when the tag is absent
        private struct Field
        {
            public int type, at;
            public uint count;
        }

        private struct Info
        {
            public bool bigEndian;
            public int width, height, samples, compression, photometric, planar, predictor, sampleFormat, fillOrder, inkSet;
            public int rowsPerStrip, tileWidth, tileHeight;
            public Field bits, offsets, counts, colorMap, extra, jpegTables;
        }

        internal static bool TryReadHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (!TryReadInfo(data, out Info info)) return false;
            bool alpha = info.samples > BaseSamples(info.photometric);
            int bits = info.bits.type == 0 ? 1 : (int)Value(data, info, info.bits, 0);
            header = new ImageHeader(ImageFormat.Tiff, info.width, info.height, info.photometric == Palette ? (alpha ? 4 : 3) : info.samples, bits, alpha);
            return true;
        }

        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int pitch, PixelLayout layout)
        {
            if (!TryReadInfo(data, out Info info)) throw new ImageFormatException("TIFF header is invalid");
            int bits = CheckSupported(data, info);
            int baseSamples = BaseSamples(info.photometric);
            if (info.samples < baseSamples) throw new ImageFormatException($"TIFF has {info.samples} samples per pixel; photometric {info.photometric} needs {baseSamples}");
            int alphaSample = -1;
            bool premultiplied = false;
            if (info.samples > baseSamples && info.extra.type != 0)
            {
                uint kind = Value(data, info, info.extra, 0);
                if (kind == 1 || kind == 2) alphaSample = baseSamples;
                premultiplied = kind == 1;
            }
            uint[]? palette = info.photometric == Palette ? ReadPalette(data, info, bits) : null;

            bool tiled = info.tileWidth > 0;
            int chunkWidth = tiled ? info.tileWidth : info.width;
            int chunkHeight = tiled ? info.tileHeight : Math.Min(info.rowsPerStrip, info.height);
            int across = tiled ? (info.width + chunkWidth - 1) / chunkWidth : 1;
            int down = (info.height + chunkHeight - 1) / chunkHeight;
            int chunks = across * down;
            int planes = info.planar == 2 ? info.samples : 1;
            int stored = chunks * planes;
            if (info.offsets.count < stored) throw new ImageFormatException($"TIFF lists {info.offsets.count} data offsets for {stored} {(tiled ? "tiles" : "strips")}");
            if (info.counts.type != 0 && info.counts.count < stored) throw new ImageFormatException($"TIFF lists {info.counts.count} data sizes for {stored} {(tiled ? "tiles" : "strips")}");
            if (info.counts.type == 0 && info.compression != NoCompression) throw new ImageFormatException("compressed TIFF has no data sizes");

            int unitSamples = info.samples / planes;
            int unitRowBytes = checked((chunkWidth * unitSamples * bits + 7) / 8);
            int planeBytes = checked(unitRowBytes * chunkHeight);
            int outBytes = ImageCodec.BytesPerPixel(layout);
            bool jpeg = info.compression == Jpeg;
            ReadOnlySpan<byte> jpegTables = info.jpegTables.type != 0 ? data.Slice(info.jpegTables.at, (int)info.jpegTables.count) : default;
            int largest = planeBytes;
            if (info.fillOrder == 2 && info.counts.type != 0)
                for (int i = 0; i < stored; i++) largest = (int)Math.Max(largest, Math.Min(Value(data, info, info.counts, i), (uint)data.Length));

            byte[] chunk = ArrayPool<byte>.Shared.Rent(jpeg ? checked(chunkWidth * 4 * chunkHeight) : checked(planeBytes * planes));
            byte[] widened = ArrayPool<byte>.Shared.Rent(chunkWidth * info.samples);
            byte[] rgba = ArrayPool<byte>.Shared.Rent(chunkWidth * 4);
            byte[] reversed = info.fillOrder == 2 ? ArrayPool<byte>.Shared.Rent(largest) : Array.Empty<byte>();
            int[] lzw = info.compression == Lzw ? ArrayPool<int>.Shared.Rent(LzwMaxCodes * 2) : Array.Empty<int>();
            try
            {
                for (int c = 0; c < chunks; c++)
                {
                    int x0 = c % across * chunkWidth, y0 = c / across * chunkHeight;
                    int rows = tiled ? chunkHeight : Math.Min(chunkHeight, info.height - y0);
                    int visibleWidth = Math.Min(chunkWidth, info.width - x0);
                    int visibleRows = Math.Min(rows, info.height - y0);
                    int expected = rows * unitRowBytes;

                    if (jpeg)
                    {
                        ReadOnlySpan<byte> src = Stored(data, info, c, expected, tiled, reversed);
                        if (!JpegDecoder.TryReadHeader(src, out ImageHeader frame) || frame.Width < visibleWidth || frame.Height < visibleRows || frame.Width > chunkWidth || frame.Height > chunkHeight)
                            throw new ImageFormatException($"TIFF JPEG {(tiled ? "tile" : "strip")} {c} does not match its {chunkWidth}x{rows} size");
                        JpegDecoder.DecodeAbbreviated(src, jpegTables, chunk, chunkWidth * 4, PixelLayout.RGBA8, info.photometric == YCbCr);
                        for (int r = 0; r < visibleRows; r++)
                            PixelRows.FromRgba(chunk.AsSpan(r * chunkWidth * 4), destination.Slice((y0 + r) * pitch + x0 * outBytes), visibleWidth, layout);
                        continue;
                    }

                    for (int p = 0; p < planes; p++)
                    {
                        ReadOnlySpan<byte> src = Stored(data, info, p * chunks + c, expected, tiled, reversed);
                        Span<byte> dst = chunk.AsSpan(p * planeBytes, expected);
                        int written = info.compression switch
                        {
                            NoCompression => CopyStored(src, dst),
                            PackBits => UnpackBits(src, dst),
                            Lzw => UnpackLzw(src, dst, lzw),
                            _ => Inflate(src, dst)
                        };
                        if (written < expected) throw new ImageFormatException($"TIFF {(tiled ? "tile" : "strip")} {p * chunks + c} holds {written} of {expected} bytes");
                    }

                    for (int r = 0; r < visibleRows; r++)
                    {
                        for (int p = 0; p < planes; p++)
                            if (info.predictor == 2) UndoPredictor(chunk.AsSpan(p * planeBytes + r * unitRowBytes, unitRowBytes), unitSamples, bits, info.bigEndian);
                        ReadOnlySpan<byte> row = chunk.AsSpan(r * unitRowBytes, unitRowBytes);
                        if (bits != 8 || planes > 1)
                        {
                            Widen(chunk, r * unitRowBytes, planeBytes, planes, info.samples, visibleWidth, bits, info.bigEndian, info.photometric != Palette, widened);
                            row = widened;
                        }
                        ConvertRow(row, rgba.AsSpan(0, visibleWidth * 4), info.samples, info.photometric, palette, alphaSample, premultiplied);
                        PixelRows.FromRgba(rgba, destination.Slice((y0 + r) * pitch + x0 * outBytes), visibleWidth, layout);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
                ArrayPool<byte>.Shared.Return(widened);
                ArrayPool<byte>.Shared.Return(rgba);
                if (reversed.Length > 0) ArrayPool<byte>.Shared.Return(reversed);
                if (lzw.Length > 0) ArrayPool<int>.Shared.Return(lzw);
            }
        }

        private static int BaseSamples(int photometric) => photometric == Rgb || photometric == YCbCr ? 3 : photometric == Cmyk ? 4 : 1;

        // Checks every field the decode relies on and returns the bits per sample.
        private static int CheckSupported(ReadOnlySpan<byte> data, in Info info)
        {
            int c = info.compression, p = info.photometric;
            if (c != NoCompression && c != Lzw && c != Deflate && c != OldDeflate && c != PackBits && c != Jpeg) throw new NotSupportedException($"TIFF compression {c} is not supported");
            if (p != WhiteIsZero && p != BlackIsZero && p != Rgb && p != Palette && p != Cmyk && !(p == YCbCr && c == Jpeg))
                throw new NotSupportedException($"TIFF photometric interpretation {p} is not supported{(p == YCbCr ? " without JPEG compression" : "")}");
            if (p == Cmyk && info.inkSet != 1) throw new NotSupportedException($"TIFF ink set {info.inkSet} is not supported");
            if (info.planar != 1 && info.planar != 2) throw new NotSupportedException($"TIFF planar configuration {info.planar} is not supported");
            if (info.sampleFormat != 1) throw new NotSupportedException($"TIFF sample format {info.sampleFormat} is not supported");
            if (info.fillOrder != 1 && info.fillOrder != 2) throw new NotSupportedException($"TIFF fill order {info.fillOrder} is not supported");
            if (info.predictor != 1 && info.predictor != 2) throw new NotSupportedException($"TIFF predictor {info.predictor} is not supported");
            int bits = info.bits.type == 0 ? 1 : (int)Value(data, info, info.bits, 0);
            for (int s = 1; s < info.samples && info.bits.type != 0; s++)
                if (Value(data, info, info.bits, info.bits.count == 1 ? 0 : s) != bits) throw new NotSupportedException("TIFF with mixed sample depths is not supported");
            if (bits != 1 && bits != 2 && bits != 4 && bits != 8 && bits != 16) throw new NotSupportedException($"{bits}-bit TIFF samples are not supported");
            if (bits == 16 && p == Palette) throw new NotSupportedException("16-bit palette TIFF is not supported");
            if (bits < 8 && p != Palette && p != WhiteIsZero && p != BlackIsZero) throw new NotSupportedException($"{bits}-bit samples with photometric {p} are not supported");
            if (bits < 8 && info.predictor == 2) throw new NotSupportedException($"TIFF predictor on {bits}-bit samples is not supported");
            if (c == Jpeg && (bits != 8 || info.planar != 1 || info.samples != BaseSamples(p) || p == Palette || p == Cmyk))
                throw new NotSupportedException("JPEG TIFF other than 8-bit chunky grey, RGB or YCbCr is not supported");
            if (info.offsets.type == 0) throw new ImageFormatException("TIFF has no image data offsets");
            if (info.tileWidth > 0 != info.tileHeight > 0) throw new ImageFormatException("TIFF tile size is incomplete");
            if (info.tileWidth > Math.Max(info.width, 4096) || info.tileHeight > Math.Max(info.height, 4096))
                throw new ImageFormatException($"TIFF tile {info.tileWidth}x{info.tileHeight} is far larger than the {info.width}x{info.height} image");
            return bits;
        }

        // The colour map: 2^bits reds, then greens, then blues, each 16 bits.
        private static uint[] ReadPalette(ReadOnlySpan<byte> data, in Info info, int bits)
        {
            int entries = 1 << bits;
            if (info.colorMap.type != 3 || info.colorMap.count < 3 * entries) throw new ImageFormatException($"palette TIFF has no {entries}-entry colour map");
            uint[] palette = new uint[256];
            for (int i = 0; i < entries; i++)
            {
                uint r = Value(data, info, info.colorMap, i) >> 8, g = Value(data, info, info.colorMap, entries + i) >> 8, b = Value(data, info, info.colorMap, 2 * entries + i) >> 8;
                palette[i] = r | g << 8 | b << 16 | 0xFF000000u;
            }
            return palette;
        }

        // The stored bytes of one strip or tile, bit-reversed into scratch under fill order 2.
        private static ReadOnlySpan<byte> Stored(ReadOnlySpan<byte> data, in Info info, int index, int expected, bool tiled, byte[] reversed)
        {
            long offset = Value(data, info, info.offsets, index);
            long size = info.counts.type == 0 ? expected : Value(data, info, info.counts, index);
            if (offset + size > data.Length) throw new ImageFormatException($"TIFF {(tiled ? "tile" : "strip")} {index} runs past the end");
            ReadOnlySpan<byte> src = data.Slice((int)offset, (int)size);
            if (info.fillOrder != 2) return src;
            Span<byte> flipped = reversed.AsSpan(0, src.Length);
            for (int i = 0; i < src.Length; i++) flipped[i] = (byte)((src[i] * 0x0202020202UL & 0x010884422010UL) % 1023);
            return flipped;
        }

        // Undoes horizontal differencing: each sample adds the same sample of the pixel to its left, in its own width.
        private static void UndoPredictor(Span<byte> row, int samples, int bits, bool bigEndian)
        {
            if (bits == 8)
            {
                for (int i = samples; i < row.Length; i++) row[i] += row[i - samples];
                return;
            }
            for (int i = samples * 2; i + 1 < row.Length; i += 2)
            {
                ushort v = (ushort)(U16(row, i, bigEndian) + U16(row, i - samples * 2, bigEndian));
                if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(row.Slice(i), v);
                else BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(i), v);
            }
        }

        // One row as 8-bit chunky samples: planes interleaved, 16-bit samples cut to their high byte,
        // smaller ones unpacked most significant first and scaled to 0-255 unless they are palette indices.
        private static void Widen(byte[] chunk, int rowAt, int planeBytes, int planes, int samples, int width, int bits, bool bigEndian, bool scale, Span<byte> output)
        {
            int unitSamples = samples / planes, count = width * unitSamples, max = (1 << bits) - 1, high = bigEndian ? 0 : 1;
            for (int p = 0; p < planes; p++)
            {
                ReadOnlySpan<byte> src = chunk.AsSpan(p * planeBytes + rowAt);
                for (int i = 0; i < count; i++)
                {
                    int v;
                    if (bits == 8) v = src[i];
                    else if (bits == 16) v = src[i * 2 + high];
                    else
                    {
                        int bit = i * bits;
                        v = (src[bit >> 3] >> (8 - bits - (bit & 7))) & max;
                        if (scale) v = v * 255 / max;
                    }
                    output[planes == 1 ? i : i * samples + p] = (byte)v;
                }
            }
        }

        // One row of stored samples as RGBA8 with straight alpha.
        private static void ConvertRow(ReadOnlySpan<byte> row, Span<byte> rgba, int samples, int photometric, uint[]? palette, int alphaSample, bool premultiplied)
        {
            for (int o = 0, at = 0; o < rgba.Length; o += 4, at += samples)
            {
                ReadOnlySpan<byte> s = row.Slice(at, samples);
                uint c;
                if (photometric == Rgb) c = s[0] | (uint)s[1] << 8 | (uint)s[2] << 16;
                else if (photometric == Palette) c = palette![s[0]] & 0xFFFFFFu;
                else if (photometric == Cmyk)
                {
                    int k = 255 - s[3];
                    c = Times(255 - s[0], k) | Times(255 - s[1], k) << 8 | Times(255 - s[2], k) << 16;
                }
                else
                {
                    uint g = photometric == WhiteIsZero ? 255u - s[0] : s[0];
                    c = g * 0x010101u;
                }
                uint a = alphaSample < 0 ? 255u : s[alphaSample];
                if (premultiplied && a != 255) c = Unpremultiply(c, a);
                BinaryPrimitives.WriteUInt32LittleEndian(rgba.Slice(o), c | a << 24);
            }
        }

        // a * b / 255, rounded
        private static uint Times(int a, int b)
        {
            int t = a * b + 128;
            return (uint)((t + (t >> 8)) >> 8);
        }

        private static uint Unpremultiply(uint c, uint a)
        {
            if (a == 0) return 0;
            uint r = Math.Min(255u, ((c & 0xFF) * 255 + a / 2) / a);
            uint g = Math.Min(255u, (((c >> 8) & 0xFF) * 255 + a / 2) / a);
            uint b = Math.Min(255u, (((c >> 16) & 0xFF) * 255 + a / 2) / a);
            return r | g << 8 | b << 16;
        }

        #region decompression
        private static int CopyStored(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            int n = Math.Min(src.Length, dst.Length);
            src.Slice(0, n).CopyTo(dst);
            return n;
        }

        private static int Inflate(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            Inflater.InflateZlib(src, dst);
            return dst.Length;
        }

        // Apple PackBits: a signed count byte, then that many literals plus one, or one byte repeated one minus it times.
        private static int UnpackBits(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            int i = 0, o = 0;
            while (o < dst.Length && i < src.Length)
            {
                int n = (sbyte)src[i++];
                if (n >= 0)
                {
                    int count = Math.Min(n + 1, dst.Length - o);
                    if (i + count > src.Length) throw new ImageFormatException("PackBits literal runs past the end");
                    src.Slice(i, count).CopyTo(dst.Slice(o));
                    i += n + 1;
                    o += count;
                }
                else if (n != -128)
                {
                    if (i >= src.Length) throw new ImageFormatException("PackBits run runs past the end");
                    int count = Math.Min(1 - n, dst.Length - o);
                    dst.Slice(o, count).Fill(src[i++]);
                    o += count;
                }
            }
            return o;
        }

        // TIFF LZW: codes most significant bit first, 9 to 12 bits, widening one code early.
        // table[code] packs prefix (12 bits), last byte (8) and first byte (8); table[MaxCodes + code] is the string length.
        private static int UnpackLzw(ReadOnlySpan<byte> src, Span<byte> dst, int[] table)
        {
            for (int code = 0; code < 256; code++)
            {
                table[code] = 0xFFF | code << 12 | code << 20;
                table[LzwMaxCodes + code] = 1;
            }
            int pos = 0, o = 0, width = 9, next = LzwFirst, previous = -1;
            uint buffer = 0;
            int buffered = 0;
            while (o < dst.Length)
            {
                while (buffered < width)
                {
                    if (pos >= src.Length) return o;
                    buffer = buffer << 8 | src[pos++];
                    buffered += 8;
                }
                int code = (int)(buffer >> (buffered - width)) & ((1 << width) - 1);
                buffered -= width;

                if (code == LzwEnd) break;
                if (code == LzwClear)
                {
                    width = 9;
                    next = LzwFirst;
                    previous = -1;
                    continue;
                }
                if (previous < 0)
                {
                    if (code >= 256) throw new ImageFormatException($"LZW code {code} follows a clear");
                    dst[o++] = (byte)code;
                    previous = code;
                    continue;
                }
                if (code > next || (code == next && next >= LzwMaxCodes)) throw new ImageFormatException($"LZW code {code} is past the table ({next})");

                if (next < LzwMaxCodes)
                {
                    int firstByte = code == next ? table[previous] >> 20 : table[code] >> 20;
                    table[next] = previous | firstByte << 12 | (table[previous] >> 20) << 20;
                    table[LzwMaxCodes + next] = table[LzwMaxCodes + previous] + 1;
                    next++;
                    if (next == (1 << width) - 1 && width < 12) width++;
                }

                int length = table[LzwMaxCodes + code];
                int walk = code;
                for (int k = length - 1; k >= 0; k--)
                {
                    int entry = table[walk];
                    if (o + k < dst.Length) dst[o + k] = (byte)(entry >> 12);
                    walk = entry & 0xFFF;
                }
                o = Math.Min(o + length, dst.Length);
                previous = code;
            }
            return o;
        }
        #endregion

        #region directory
        private static bool TryReadInfo(ReadOnlySpan<byte> data, out Info info)
        {
            info = default;
            if (data.Length < 8) return false;
            if (data[0] == (byte)'I' && data[1] == (byte)'I') info.bigEndian = false;
            else if (data[0] == (byte)'M' && data[1] == (byte)'M') info.bigEndian = true;
            else return false;
            if (U16(data, 2, info.bigEndian) != 42) return false;
            uint ifd = U32(data, 4, info.bigEndian);
            if (ifd < 8 || ifd + 2L > data.Length) return false;
            int entries = U16(data, (int)ifd, info.bigEndian);
            if (ifd + 2L + entries * 12L > data.Length) return false;

            info.samples = 1;
            info.compression = NoCompression;
            info.photometric = -1;
            info.planar = 1;
            info.predictor = 1;
            info.sampleFormat = 1;
            info.fillOrder = 1;
            info.inkSet = 1;
            info.rowsPerStrip = int.MaxValue;
            for (int e = 0; e < entries; e++)
            {
                int at = (int)ifd + 2 + e * 12;
                int tag = U16(data, at, info.bigEndian);
                if (!TryReadField(data, at, info.bigEndian, out Field field)) continue;
                uint value = field.count == 0 ? 0 : Value(data, info, field, 0);
                switch (tag)
                {
                    case 256: info.width = (int)Math.Min(value, int.MaxValue); break;
                    case 257: info.height = (int)Math.Min(value, int.MaxValue); break;
                    case 258: info.bits = field; break;
                    case 259: info.compression = (int)value; break;
                    case 262: info.photometric = (int)value; break;
                    case 266: info.fillOrder = (int)value; break;
                    case 273: info.offsets = field; break;
                    case 277: info.samples = (int)value; break;
                    case 278: info.rowsPerStrip = (int)Math.Min(value, int.MaxValue); break;
                    case 279: info.counts = field; break;
                    case 284: info.planar = (int)value; break;
                    case 317: info.predictor = (int)value; break;
                    case 320: info.colorMap = field; break;
                    case 322: info.tileWidth = (int)Math.Min(value, int.MaxValue); break;
                    case 323: info.tileHeight = (int)Math.Min(value, int.MaxValue); break;
                    case 324: info.offsets = field; break;
                    case 325: info.counts = field; break;
                    case 332: info.inkSet = (int)value; break;
                    case 338: info.extra = field; break;
                    case 339: info.sampleFormat = (int)value; break;
                    case 347: info.jpegTables = field; break;
                }
            }
            if (info.photometric < 0) info.photometric = info.samples >= 3 ? Rgb : BlackIsZero;
            return info.width > 0 && info.height > 0 && info.samples > 0 && info.rowsPerStrip > 0;
        }

        // Reads an entry of type BYTE, UNDEFINED, SHORT or LONG; false for any other type or values that run past the end.
        private static bool TryReadField(ReadOnlySpan<byte> data, int entry, bool bigEndian, out Field field)
        {
            field = default;
            int type = U16(data, entry + 2, bigEndian);
            int size = type == 1 || type == 7 ? 1 : type == 3 ? 2 : type == 4 ? 4 : 0;
            if (size == 0) return false;
            uint count = U32(data, entry + 4, bigEndian);
            long bytes = (long)count * size;
            long at = bytes <= 4 ? entry + 8 : U32(data, entry + 8, bigEndian);
            if (at + bytes > data.Length) return false;
            field = new Field { type = type, at = (int)at, count = count };
            return true;
        }

        private static uint Value(ReadOnlySpan<byte> data, in Info info, in Field field, int index)
        {
            if ((uint)index >= field.count) throw new ImageFormatException($"TIFF field holds {field.count} values, not {index + 1}");
            return field.type switch
            {
                1 or 7 => data[field.at + index],
                3 => U16(data, field.at + index * 2, info.bigEndian),
                _ => U32(data, field.at + index * 4, info.bigEndian)
            };
        }

        private static ushort U16(ReadOnlySpan<byte> data, int at, bool bigEndian) =>
            bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data.Slice(at)) : BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at));

        private static uint U32(ReadOnlySpan<byte> data, int at, bool bigEndian) =>
            bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data.Slice(at)) : BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at));
        #endregion
    }
}
