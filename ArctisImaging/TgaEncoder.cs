using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Arctis.Imaging
{
    // Truevision TGA: grey, colour-mapped and true-colour, plain or run-length encoded, from any corner.
    internal static class TgaEncoder
    {
        private const int Mapped = 1, TrueColour = 2, Grey = 3, RunLength = 8;
        private const int BlockBytes = 1 << 18;

        internal static void Encode(ReadOnlySpan<byte> pixels, int width, int height, int pitch, PixelLayout layout, Stream output, in TgaSettings settings)
        {
            TgaPixelFormat format = settings.FormatFor(layout);
            bool indexed = format == TgaPixelFormat.Indexed8 || format == TgaPixelFormat.IndexedAlpha8;
            int kind = indexed ? Mapped : format == TgaPixelFormat.Grey8 || format == TgaPixelFormat.GreyAlpha16 ? Grey : TrueColour;
            int depth = format switch
            {
                TgaPixelFormat.Grey8 or TgaPixelFormat.Indexed8 or TgaPixelFormat.IndexedAlpha8 => 8,
                TgaPixelFormat.Rgb24 => 24,
                TgaPixelFormat.Argb32 => 32,
                _ => 16
            };
            int alphaBits = format switch
            {
                TgaPixelFormat.GreyAlpha16 or TgaPixelFormat.IndexedAlpha8 or TgaPixelFormat.Argb32 => 8,
                TgaPixelFormat.Argb1555 => 1,
                _ => 0
            };
            int bpp = depth / 8;
            bool rightToLeft = settings.Origin == TgaOrigin.BottomRight || settings.Origin == TgaOrigin.TopRight;
            bool topDown = settings.Origin == TgaOrigin.TopLeft || settings.Origin == TgaOrigin.TopRight;

            Span<uint> palette = stackalloc uint[256];
            int colours = 0;
            byte[]? indices = indexed ? ArrayPool<byte>.Shared.Rent(checked(width * height)) : null;
            byte[] rgba = ArrayPool<byte>.Shared.Rent(width * 4);
            uint[] values = ArrayPool<uint>.Shared.Rent(width);
            byte[] line = ArrayPool<byte>.Shared.Rent(width * (bpp + 1) + 1);
            try
            {
                if (indexed) colours = BmpEncoder.BuildPalette(pixels, width, height, pitch, layout, format == TgaPixelFormat.IndexedAlpha8, palette, indices);
                int entryBytes = format == TgaPixelFormat.IndexedAlpha8 ? 4 : 3;

                Span<byte> header = stackalloc byte[18];
                header.Clear();
                header[1] = indexed ? (byte)1 : (byte)0;
                header[2] = (byte)(kind | (settings.Rle ? RunLength : 0));
                BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(5), (ushort)colours);
                header[7] = indexed ? (byte)(entryBytes * 8) : (byte)0;
                BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(12), (ushort)width);
                BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(14), (ushort)height);
                header[16] = (byte)depth;
                header[17] = (byte)(alphaBits | (rightToLeft ? 0x10 : 0) | (topDown ? 0x20 : 0));
                output.Write(header);
                if (indexed)
                {
                    Span<byte> map = stackalloc byte[1024];
                    for (int i = 0; i < colours; i++)
                    {
                        uint c = palette[i];
                        map[i * entryBytes] = (byte)(c >> 16);
                        map[i * entryBytes + 1] = (byte)(c >> 8);
                        map[i * entryBytes + 2] = (byte)c;
                        if (entryBytes == 4) map[i * entryBytes + 3] = (byte)(c >> 24);
                    }
                    output.Write(map.Slice(0, colours * entryBytes));
                }

                bool colour = layout == PixelLayout.RGBA8 || layout == PixelLayout.BGRA8;
                bool direct = !indexed && !settings.Rle && !rightToLeft && (format switch
                {
                    TgaPixelFormat.Argb32 or TgaPixelFormat.Rgb24 => colour,
                    TgaPixelFormat.Grey8 => colour || layout == PixelLayout.R8,
                    TgaPixelFormat.GreyAlpha16 => layout == PixelLayout.RG8,
                    _ => false
                });
                bool rleRow = settings.Rle && !indexed && !rightToLeft && (format == TgaPixelFormat.Argb32 || format == TgaPixelFormat.Rgb24 ? colour : format == TgaPixelFormat.Grey8 && layout == PixelLayout.R8);
                Span<uint> row = values.AsSpan(0, width);
                if (direct && !topDown) WriteBottomUp(pixels, width, height, pitch, layout, format, bpp, output);
                else for (int stored = 0; stored < height; stored++)
                {
                    int y = topDown ? stored : height - 1 - stored;
                    if (direct)
                    {
                        WriteDirect(pixels.Slice(y * pitch), line, width, layout, format);
                        output.Write(line, 0, width * bpp);
                        continue;
                    }
                    if (rleRow)
                    {
                        output.Write(line, 0, WriteRunLength(RleRow(pixels.Slice(y * pitch), row, layout), bpp, line));
                        continue;
                    }
                    if (!indexed) PixelRows.ToRgba(pixels.Slice(y * pitch), rgba, width, layout);
                    for (int x = 0; x < width; x++)
                    {
                        int at = rightToLeft ? width - 1 - x : x;
                        if (indices != null)
                        {
                            row[at] = indices[y * width + x];
                            continue;
                        }
                        int r = rgba[x * 4], g = rgba[x * 4 + 1], b = rgba[x * 4 + 2], a = rgba[x * 4 + 3];
                        row[at] = format switch
                        {
                            TgaPixelFormat.Grey8 => PixelRows.Luma(r, g, b),
                            TgaPixelFormat.GreyAlpha16 => PixelRows.Luma(r, g, b) | (uint)a << 8,
                            TgaPixelFormat.Rgb24 => (uint)(b | g << 8 | r << 16),
                            TgaPixelFormat.Argb32 => (uint)(b | g << 8 | r << 16 | a << 24),
                            _ => (uint)(Five(r) << 10 | Five(g) << 5 | Five(b) | (format == TgaPixelFormat.Argb1555 && a >= 128 ? 0x8000 : 0))
                        };
                    }
                    int length = settings.Rle ? WriteRunLength(row, bpp, line) : WriteRaw(row, bpp, line);
                    output.Write(line, 0, length);
                }

                if (!settings.OmitFooter)
                {
                    Span<byte> footer = stackalloc byte[26];
                    footer.Clear();
                    "TRUEVISION-XFILE."u8.CopyTo(footer.Slice(8));
                    output.Write(footer);
                }
            }
            finally
            {
                if (indices != null) ArrayPool<byte>.Shared.Return(indices);
                ArrayPool<byte>.Shared.Return(rgba);
                ArrayPool<uint>.Shared.Return(values);
                ArrayPool<byte>.Shared.Return(line);
            }
        }

        private static int Five(int c) => (c * 31 + 127) / 255;

        // Direct rows bottom-up: each block of source rows is read top to bottom into mirrored slots, then written at once.
        private static void WriteBottomUp(ReadOnlySpan<byte> pixels, int width, int height, int pitch, PixelLayout layout, TgaPixelFormat format, int bpp, Stream output)
        {
            int rowBytes = width * bpp, rows = Math.Max(1, BlockBytes / rowBytes);
            byte[] block = ArrayPool<byte>.Shared.Rent(rows * rowBytes);
            try
            {
                for (int end = height; end > 0; end -= rows)
                {
                    int n = Math.Min(rows, end);
                    for (int j = 0; j < n; j++)
                        WriteDirect(pixels.Slice((end - n + j) * pitch), block.AsSpan((n - 1 - j) * rowBytes, rowBytes), width, layout, format);
                    output.Write(block, 0, n * rowBytes);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(block);
            }
        }

        // One left-to-right raw row converted straight from the source.
        private static void WriteDirect(ReadOnlySpan<byte> src, Span<byte> line, int width, PixelLayout layout, TgaPixelFormat format)
        {
            switch (format)
            {
                case TgaPixelFormat.Argb32:
                    if (layout == PixelLayout.BGRA8) src.Slice(0, width * 4).CopyTo(line);
                    else ImageOps.CopySwizzled(src.Slice(0, width * 4), line);
                    break;
                case TgaPixelFormat.Rgb24:
                    PixelRows.ToBgr(src, line, width, layout);
                    break;
                case TgaPixelFormat.GreyAlpha16:
                    src.Slice(0, width * 2).CopyTo(line);
                    break;
                default:
                    if (layout == PixelLayout.R8)
                    {
                        src.Slice(0, width).CopyTo(line);
                        break;
                    }
                    int first = layout == PixelLayout.RGBA8 ? 0 : 2, last = 2 - first;
                    for (int x = 0; x < width; x++) line[x] = PixelRows.Luma(src[x * 4 + first], src[x * 4 + 1], src[x * 4 + last]);
                    break;
            }
        }

        // One row's pixels as the values RLE compares, read in place when the source is BGRA8.
        private static ReadOnlySpan<uint> RleRow(ReadOnlySpan<byte> src, Span<uint> values, PixelLayout layout)
        {
            if (layout == PixelLayout.BGRA8) return MemoryMarshal.Cast<byte, uint>(src.Slice(0, values.Length * 4));
            if (layout == PixelLayout.RGBA8) ImageOps.CopySwizzled(src.Slice(0, values.Length * 4), MemoryMarshal.AsBytes(values));
            else for (int x = 0; x < values.Length; x++) values[x] = src[x];
            return values;
        }

        private static int WriteRaw(ReadOnlySpan<uint> row, int bpp, Span<byte> line)
        {
            if (line.Length < row.Length * bpp + 1) throw new ArgumentException("line is shorter than the row");
            ref byte d = ref MemoryMarshal.GetReference(line);
            int at = 0;
            for (int x = 0; x < row.Length; x++) at = Put(row[x], bpp, ref d, at);
            return at;
        }

        // Run packets for 2 or more equal pixels, raw packets up to the next equal pair; at most 128 pixels a packet.
        // Only the low bpp bytes of a value are compared and written.
        private static int WriteRunLength(ReadOnlySpan<uint> row, int bpp, Span<byte> line)
        {
            int width = row.Length;
            if (line.Length < width * (bpp + 1) + 1) throw new ArgumentException("line is shorter than the worst-case packets");
            uint mask = bpp == 4 ? uint.MaxValue : (1u << bpp * 8) - 1;
            ref uint p = ref MemoryMarshal.GetReference(row);
            ref byte d = ref MemoryMarshal.GetReference(line);
            int at = 0, x = 0;
            while (x < width)
            {
                uint v = Unsafe.Add(ref p, x);
                int run = 1;
                while (x + run < width && run < 128 && ((Unsafe.Add(ref p, x + run) ^ v) & mask) == 0) run++;
                if (run >= 2)
                {
                    Unsafe.Add(ref d, at++) = (byte)(0x80 | (run - 1));
                    at = Put(v, bpp, ref d, at);
                    x += run;
                    continue;
                }
                int start = x++;
                while (x < width && x - start < 128 && !(x + 1 < width && ((Unsafe.Add(ref p, x) ^ Unsafe.Add(ref p, x + 1)) & mask) == 0)) x++;
                Unsafe.Add(ref d, at++) = (byte)(x - start - 1);
                if (bpp == 4)
                {
                    MemoryMarshal.AsBytes(row.Slice(start, x - start)).CopyTo(line.Slice(at));
                    at += (x - start) * 4;
                }
                else for (int i = start; i < x; i++) at = Put(Unsafe.Add(ref p, i), bpp, ref d, at);
            }
            return at;
        }

        // Stores the low bpp bytes of v; a 3-byte pixel is stored as 4, so line has a byte of slack past the end.
        private static int Put(uint v, int bpp, ref byte d, int at)
        {
            if (bpp >= 3) Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, at), v);
            else if (bpp == 2) Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, at), (ushort)v);
            else Unsafe.Add(ref d, at) = (byte)v;
            return at + bpp;
        }
    }
}
