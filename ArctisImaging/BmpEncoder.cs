using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace Arctis.Imaging
{
    // Windows bitmaps from resolved settings: every header, palette, bit-field and RLE form BmpDecoder reads.
    internal static class BmpEncoder
    {
        private const uint Rgb = 0, Rle8 = 1, Rle4 = 2, BitFields = 3, AlphaBitFields = 6;
        private const int PixelsPerMetre = 3780, BlockBytes = 1 << 18;
        private const uint SrgbSpace = 0x73524742, ImagesIntent = 4;

        internal static void Encode(ReadOnlySpan<byte> pixels, int width, int height, int pitch, PixelLayout layout, Stream output, in BmpSettings settings, bool fileHeader) =>
            Write(pixels, width, height, pitch, layout, output, settings, fileHeader, false);

        // An icon entry: Info header with twice the height, then the colour rows and a 1-bit AND mask, both bottom-up.
        internal static void EncodeIconEntry(Image image, BmpPixelFormat format, Stream output) =>
            Write(image.Pixels, image.Width, image.Height, image.RowPitch, image.Layout, output, new BmpSettings(format, BmpHeader.Info, BmpCompression.None), false, true);

        private static void Write(ReadOnlySpan<byte> pixels, int width, int height, int pitch, PixelLayout layout, Stream output, in BmpSettings settings, bool fileHeader, bool icon)
        {
            BmpPixelFormat format = settings.Format;
            BmpHeader version = settings.Header;
            if (version == BmpHeader.Core && (width > 65535 || height > 65535)) throw new ArgumentException($"a core header holds at most 65535 pixels a side, not {width}x{height}");
            int bits = format switch
            {
                BmpPixelFormat.Indexed1 => 1,
                BmpPixelFormat.Indexed4 => 4,
                BmpPixelFormat.Indexed8 => 8,
                BmpPixelFormat.Rgb24 => 24,
                BmpPixelFormat.Xrgb32 or BmpPixelFormat.Argb32 => 32,
                _ => 16
            };
            Masks(format, out uint rMask, out uint gMask, out uint bMask, out uint aMask);
            bool rle = settings.Compression == BmpCompression.Rle;
            uint compression = settings.Compression switch
            {
                BmpCompression.Rle => bits == 8 ? Rle8 : Rle4,
                BmpCompression.BitFields => BitFields,
                BmpCompression.AlphaBitFields => AlphaBitFields,
                _ => Rgb
            };
            int headerSize = version switch
            {
                BmpHeader.Core => 12,
                BmpHeader.V4 => 108,
                BmpHeader.V5 => 124,
                _ => 40
            };
            int masksAfter = version != BmpHeader.Info ? 0 : compression == BitFields ? 12 : compression == AlphaBitFields ? 16 : 0;
            long stride = ((long)width * bits + 31) / 32 * 4, maskStride = icon ? ((long)width + 31) / 32 * 4 : 0;

            Span<uint> palette = stackalloc uint[256];
            int colours = 0;
            byte[]? indices = settings.IsIndexed ? ArrayPool<byte>.Shared.Rent(checked(width * height)) : null;
            byte[]? encoded = null;
            byte[] rgba = ArrayPool<byte>.Shared.Rent(width * 4);
            byte[] line = ArrayPool<byte>.Shared.Rent((int)Math.Max(stride, maskStride));
            byte[]? mask = icon ? ArrayPool<byte>.Shared.Rent(checked((int)maskStride * height)) : null;
            try
            {
                if (indices != null)
                {
                    if (layout == PixelLayout.R8 && bits == 8)
                    {
                        for (int i = 0; i < 256; i++) palette[i] = (uint)i * 0x010101u | 0xFF000000u;
                        colours = 256;
                        for (int y = 0; y < height; y++) pixels.Slice(y * pitch, width).CopyTo(indices.AsSpan(y * width));
                    }
                    else colours = BuildPalette(pixels, width, height, pitch, layout, false, palette.Slice(0, 1 << bits), indices);
                    if (version == BmpHeader.Core)
                    {
                        palette.Slice(colours, (1 << bits) - colours).Clear();
                        colours = 1 << bits;
                    }
                }

                long dataLength;
                if (rle)
                {
                    encoded = ArrayPool<byte>.Shared.Rent(checked(height * (2 * width + 2) + 2));
                    dataLength = EncodeRle(indices, width, height, bits == 4, encoded);
                }
                else dataLength = (stride + maskStride) * height;
                int entryBytes = version == BmpHeader.Core ? 3 : 4;
                long offset = headerSize + masksAfter + (long)colours * entryBytes;
                if ((fileHeader ? 14 : 0) + offset + dataLength > uint.MaxValue) throw new ArgumentException($"a {width}x{height} bitmap is larger than 4 GB");

                Span<byte> head = stackalloc byte[14 + 124 + 16];
                head.Clear();
                int at = 0;
                if (fileHeader)
                {
                    head[0] = (byte)'B';
                    head[1] = (byte)'M';
                    BinaryPrimitives.WriteUInt32LittleEndian(head.Slice(2), (uint)(14 + offset + dataLength));
                    BinaryPrimitives.WriteUInt32LittleEndian(head.Slice(10), (uint)(14 + offset));
                    at = 14;
                }
                Span<byte> h = head.Slice(at);
                BinaryPrimitives.WriteInt32LittleEndian(h, headerSize);
                if (version == BmpHeader.Core)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(4), (ushort)width);
                    BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(6), (ushort)height);
                    BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(8), 1);
                    BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(10), (ushort)bits);
                }
                else
                {
                    BinaryPrimitives.WriteInt32LittleEndian(h.Slice(4), width);
                    BinaryPrimitives.WriteInt32LittleEndian(h.Slice(8), icon ? height * 2 : settings.TopDown ? -height : height);
                    BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(12), 1);
                    BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(14), (ushort)bits);
                    BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(16), compression);
                    BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(20), (uint)dataLength);
                    BinaryPrimitives.WriteInt32LittleEndian(h.Slice(24), PixelsPerMetre);
                    BinaryPrimitives.WriteInt32LittleEndian(h.Slice(28), PixelsPerMetre);
                    BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(32), (uint)colours);
                    BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(36), (uint)colours);
                    if (version != BmpHeader.Info || masksAfter > 0)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(40), rMask);
                        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(44), gMask);
                        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(48), bMask);
                        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(52), aMask);
                    }
                    if (version != BmpHeader.Info) BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(56), SrgbSpace);
                    if (version == BmpHeader.V5) BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(108), ImagesIntent);
                }
                output.Write(head.Slice(0, at + headerSize + masksAfter));

                if (colours > 0)
                {
                    Span<byte> entries = stackalloc byte[1024];
                    for (int i = 0; i < colours; i++)
                    {
                        uint c = palette[i];
                        entries[i * entryBytes] = (byte)(c >> 16);
                        entries[i * entryBytes + 1] = (byte)(c >> 8);
                        entries[i * entryBytes + 2] = (byte)c;
                        if (entryBytes == 4) entries[i * entryBytes + 3] = 0;
                    }
                    output.Write(entries.Slice(0, colours * entryBytes));
                }

                if (rle)
                {
                    output.Write(encoded!, 0, (int)dataLength);
                    return;
                }
                if (mask != null) Array.Clear(mask, 0, (int)maskStride * height);
                Channel r = new Channel(rMask), g = new Channel(gMask), b = new Channel(bMask), a = new Channel(aMask);
                bool direct = indices == null && !icon && (bits == 24 || bits == 32) && (layout == PixelLayout.RGBA8 || layout == PixelLayout.BGRA8);
                if (direct && !settings.TopDown) WriteBottomUp(pixels, width, height, pitch, layout, bits, aMask == 0, (int)stride, output);
                else for (int row = 0; row < height; row++)
                {
                    int y = settings.TopDown ? row : height - 1 - row;
                    Span<byte> dst = line.AsSpan(0, (int)stride);
                    if (direct)
                    {
                        WriteDirect(pixels.Slice(y * pitch), dst, width, layout, bits, aMask == 0);
                        output.Write(dst);
                        continue;
                    }
                    dst.Clear();
                    if (indices != null)
                    {
                        ReadOnlySpan<byte> index = indices.AsSpan(y * width, width);
                        if (bits == 8) index.CopyTo(dst);
                        else
                            for (int x = 0; x < width; x++)
                                dst[x * bits >> 3] |= (byte)(index[x] << (8 - bits - (x * bits & 7)));
                    }
                    if (indices == null || icon) PixelRows.ToRgba(pixels.Slice(y * pitch), rgba, width, layout);
                    if (indices == null)
                    {
                        switch (bits)
                        {
                            case 24:
                                for (int x = 0; x < width; x++)
                                {
                                    dst[x * 3] = rgba[x * 4 + 2];
                                    dst[x * 3 + 1] = rgba[x * 4 + 1];
                                    dst[x * 3 + 2] = rgba[x * 4];
                                }
                                break;
                            case 32:
                                ImageOps.CopySwizzled(rgba.AsSpan(0, width * 4), dst);
                                if (aMask == 0)
                                    for (int x = 0; x < width; x++) dst[x * 4 + 3] = 0;
                                break;
                            default:
                                for (int x = 0; x < width; x++)
                                {
                                    uint v = r.Pack(rgba[x * 4]) | g.Pack(rgba[x * 4 + 1]) | b.Pack(rgba[x * 4 + 2]) | a.Pack(rgba[x * 4 + 3]);
                                    BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(x * 2), (ushort)v);
                                }
                                break;
                        }
                    }
                    if (mask != null)
                    {
                        Span<byte> bitsRow = mask.AsSpan(row * (int)maskStride, (int)maskStride);
                        for (int x = 0; x < width; x++)
                            if (rgba[x * 4 + 3] == 0) bitsRow[x >> 3] |= (byte)(0x80 >> (x & 7));
                    }
                    output.Write(dst);
                }
                if (mask != null) output.Write(mask, 0, (int)maskStride * height);
            }
            finally
            {
                if (indices != null) ArrayPool<byte>.Shared.Return(indices);
                if (encoded != null) ArrayPool<byte>.Shared.Return(encoded);
                if (mask != null) ArrayPool<byte>.Shared.Return(mask);
                ArrayPool<byte>.Shared.Return(rgba);
                ArrayPool<byte>.Shared.Return(line);
            }
        }

        // Direct rows bottom-up: each block of source rows is read top to bottom into mirrored slots, then written at once.
        private static void WriteBottomUp(ReadOnlySpan<byte> pixels, int width, int height, int pitch, PixelLayout layout, int bits, bool noAlpha, int stride, Stream output)
        {
            int rows = Math.Max(1, BlockBytes / stride);
            byte[] block = ArrayPool<byte>.Shared.Rent(rows * stride);
            try
            {
                for (int end = height; end > 0; end -= rows)
                {
                    int n = Math.Min(rows, end);
                    for (int j = 0; j < n; j++)
                        WriteDirect(pixels.Slice((end - n + j) * pitch), block.AsSpan((n - 1 - j) * stride, stride), width, layout, bits, noAlpha);
                    output.Write(block, 0, n * stride);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(block);
            }
        }

        // One 24 or 32-bit row converted straight from an RGBA8 or BGRA8 source, padding cleared.
        private static void WriteDirect(ReadOnlySpan<byte> src, Span<byte> dst, int width, PixelLayout layout, int bits, bool noAlpha)
        {
            if (bits == 24)
            {
                PixelRows.ToBgr(src, dst, width, layout);
                dst.Slice(width * 3).Clear();
                return;
            }
            if (layout == PixelLayout.BGRA8) src.Slice(0, width * 4).CopyTo(dst);
            else ImageOps.CopySwizzled(src.Slice(0, width * 4), dst);
            if (noAlpha)
                for (int x = 0; x < width; x++) dst[x * 4 + 3] = 0;
        }

        // An exact palette of the image's colours in first-seen order, and each pixel's index; alpha is forced opaque
        // unless kept. Throws when the image has more colours than the palette holds.
        internal static int BuildPalette(ReadOnlySpan<byte> pixels, int width, int height, int pitch, PixelLayout layout, bool keepAlpha, Span<uint> palette, Span<byte> indices)
        {
            Span<short> table = stackalloc short[512];
            table.Clear();
            uint opaque = keepAlpha ? 0u : 0xFF000000u;
            byte[] rgba = ArrayPool<byte>.Shared.Rent(width * 4);
            try
            {
                int count = 0, lastIndex = -1;
                uint last = 0;
                for (int y = 0; y < height; y++)
                {
                    ReadOnlySpan<byte> row = pixels.Slice(y * pitch);
                    if (layout != PixelLayout.RGBA8)
                    {
                        PixelRows.ToRgba(row, rgba, width, layout);
                        row = rgba;
                    }
                    Span<byte> rowIndices = indices.Slice(y * width, width);
                    for (int x = 0; x < width; x++)
                    {
                        uint c = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(x * 4)) | opaque;
                        if (c != last || lastIndex < 0)
                        {
                            int slot = (int)((c * 0x9E3779B1u) >> 23);
                            while (true)
                            {
                                int entry = table[slot];
                                if (entry == 0)
                                {
                                    if (count == palette.Length) throw new ArgumentException($"the image has more than {palette.Length} colours, the most an exact palette of this format holds");
                                    palette[count] = c;
                                    table[slot] = (short)++count;
                                    lastIndex = count - 1;
                                    break;
                                }
                                if (palette[entry - 1] == c)
                                {
                                    lastIndex = entry - 1;
                                    break;
                                }
                                slot = (slot + 1) & 511;
                            }
                            last = c;
                        }
                        rowIndices[x] = (byte)lastIndex;
                    }
                }
                return count;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rgba);
            }
        }

        // RLE8 or RLE4 rows bottom-up: runs of 2 or more, literals of 3 or more, single pixels as runs of 1; an end of
        // line after every row and an end of bitmap.
        private static int EncodeRle(ReadOnlySpan<byte> indices, int width, int height, bool four, Span<byte> dst)
        {
            int at = 0;
            for (int row = 0; row < height; row++)
            {
                ReadOnlySpan<byte> index = indices.Slice((height - 1 - row) * width, width);
                int x = 0;
                while (x < width)
                {
                    int run = RunAt(index, x, 255);
                    if (run >= 2)
                    {
                        dst[at++] = (byte)run;
                        dst[at++] = four ? (byte)(index[x] << 4 | index[x]) : index[x];
                        x += run;
                        continue;
                    }
                    int start = x;
                    while (x < width && x - start < 255 && RunAt(index, x, 3) < 3) x++;
                    int n = x - start;
                    if (n < 3)
                    {
                        for (int i = start; i < x; i++)
                        {
                            dst[at++] = 1;
                            dst[at++] = four ? (byte)(index[i] << 4) : index[i];
                        }
                        continue;
                    }
                    dst[at++] = 0;
                    dst[at++] = (byte)n;
                    int bytes = four ? (n + 1) / 2 : n;
                    if (four)
                    {
                        for (int i = 0; i < bytes; i++)
                            dst[at + i] = (byte)(index[start + i * 2] << 4 | (i * 2 + 1 < n ? index[start + i * 2 + 1] : 0));
                    }
                    else index.Slice(start, n).CopyTo(dst.Slice(at));
                    at += bytes;
                    if ((bytes & 1) != 0) dst[at++] = 0;
                }
                dst[at++] = 0;
                dst[at++] = 0;
            }
            dst[at++] = 0;
            dst[at++] = 1;
            return at;
        }

        // Pixels equal to index[x] from x on, at most cap.
        private static int RunAt(ReadOnlySpan<byte> index, int x, int cap)
        {
            int n = 1;
            while (x + n < index.Length && n < cap && index[x + n] == index[x]) n++;
            return n;
        }

        private static void Masks(BmpPixelFormat format, out uint r, out uint g, out uint b, out uint a)
        {
            a = 0;
            switch (format)
            {
                case BmpPixelFormat.Rgb555: r = 0x7C00; g = 0x03E0; b = 0x001F; break;
                case BmpPixelFormat.Rgb565: r = 0xF800; g = 0x07E0; b = 0x001F; break;
                case BmpPixelFormat.Argb1555: r = 0x7C00; g = 0x03E0; b = 0x001F; a = 0x8000; break;
                case BmpPixelFormat.Argb4444: r = 0x0F00; g = 0x00F0; b = 0x000F; a = 0xF000; break;
                case BmpPixelFormat.Xrgb32: r = 0x00FF0000; g = 0x0000FF00; b = 0x000000FF; break;
                case BmpPixelFormat.Argb32: r = 0x00FF0000; g = 0x0000FF00; b = 0x000000FF; a = 0xFF000000; break;
                default: r = g = b = 0; break;
            }
        }

        // One 8-bit channel rounded to a bit field.
        private readonly struct Channel
        {
            private readonly int shift;
            private readonly int max;

            public Channel(uint mask)
            {
                shift = mask == 0 ? 0 : BitOperations.TrailingZeroCount(mask);
                max = (int)(mask >> shift);
            }

            public uint Pack(byte c) => (uint)((c * max + 127) / 255) << shift;
        }
    }
}
