using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;

namespace Arctis.Imaging
{
    // Truevision TGA: grey, colour-mapped and true-colour, plain or run-length encoded, from any corner.
    internal static class TgaEncoder
    {
        private const int Mapped = 1, TrueColour = 2, Grey = 3, RunLength = 8;

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
            byte[] line = ArrayPool<byte>.Shared.Rent(width * (bpp + 1));
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

                Span<uint> row = values.AsSpan(0, width);
                for (int stored = 0; stored < height; stored++)
                {
                    int y = topDown ? stored : height - 1 - stored;
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

        private static int WriteRaw(ReadOnlySpan<uint> row, int bpp, Span<byte> line)
        {
            int at = 0;
            for (int x = 0; x < row.Length; x++) at = Put(row[x], bpp, line, at);
            return at;
        }

        // Run packets for 2 or more equal pixels, raw packets up to the next equal pair; at most 128 pixels a packet.
        private static int WriteRunLength(ReadOnlySpan<uint> row, int bpp, Span<byte> line)
        {
            int at = 0, x = 0, width = row.Length;
            while (x < width)
            {
                int run = 1;
                while (x + run < width && run < 128 && row[x + run] == row[x]) run++;
                if (run >= 2)
                {
                    line[at++] = (byte)(0x80 | (run - 1));
                    at = Put(row[x], bpp, line, at);
                    x += run;
                    continue;
                }
                int start = x++;
                while (x < width && x - start < 128 && !(x + 1 < width && row[x] == row[x + 1])) x++;
                line[at++] = (byte)(x - start - 1);
                for (int i = start; i < x; i++) at = Put(row[i], bpp, line, at);
            }
            return at;
        }

        private static int Put(uint v, int bpp, Span<byte> line, int at)
        {
            for (int k = 0; k < bpp; k++) line[at + k] = (byte)(v >> (k * 8));
            return at + bpp;
        }
    }
}
