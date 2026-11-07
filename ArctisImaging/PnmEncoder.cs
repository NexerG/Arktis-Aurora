using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Arctis.Imaging
{
    // Netpbm: PBM, PGM and PPM, raw or plain, and PAM; samples are scaled from 8 bits to maxval.
    internal static class PnmEncoder
    {
        private const int PlainLine = 70;

        internal static void Encode(ReadOnlySpan<byte> pixels, int width, int height, int pitch, PixelLayout layout, Stream output, in PnmSettings settings)
        {
            PnmKind kind = settings.KindFor(layout);
            PamTupleType tuple = kind == PnmKind.Pam ? settings.TupleTypeFor(layout) : PamTupleType.Auto;
            int maxval = settings.MaxVal;
            bool plain = settings.Plain, wide = maxval > 255;
            // samples per pixel, and whether they are luminance or carry alpha
            int depth = kind switch
            {
                PnmKind.Pbm or PnmKind.Pgm => 1,
                PnmKind.Ppm => 3,
                _ => tuple switch
                {
                    PamTupleType.BlackAndWhite or PamTupleType.Grayscale => 1,
                    PamTupleType.BlackAndWhiteAlpha or PamTupleType.GrayscaleAlpha => 2,
                    PamTupleType.Rgb => 3,
                    _ => 4
                }
            };
            bool grey = depth <= 2;
            WriteHeader(output, kind, tuple, plain, width, height, depth, maxval);

            Span<ushort> scale = stackalloc ushort[256];
            for (int v = 0; v < 256; v++) scale[v] = (ushort)((v * maxval + 127) / 255);
            int sampleBytes = wide ? 2 : 1;
            int rowBytes = kind == PnmKind.Pbm && !plain ? (width + 7) / 8
                : plain ? width * depth * 6 + width * depth * 6 / (PlainLine - 6) + 2
                : width * depth * sampleBytes;
            byte[] rgba = ArrayPool<byte>.Shared.Rent(width * 4);
            byte[] samples = ArrayPool<byte>.Shared.Rent(width * 4 * 2);
            byte[] line = ArrayPool<byte>.Shared.Rent(rowBytes);
            bool colour = layout == PixelLayout.RGBA8 || layout == PixelLayout.BGRA8;
            bool direct = !plain && maxval == 255 && (kind switch
            {
                PnmKind.Ppm => colour,
                PnmKind.Pgm => true,
                PnmKind.Pam => tuple == PamTupleType.RgbAlpha && layout == PixelLayout.RGBA8,
                _ => false
            });
            try
            {
                for (int y = 0; y < height; y++)
                {
                    if (direct)
                    {
                        WriteDirect(pixels.Slice(y * pitch), line, width, layout, kind);
                        output.Write(line, 0, rowBytes);
                        continue;
                    }
                    PixelRows.ToRgba(pixels.Slice(y * pitch), rgba, width, layout);
                    Span<ushort> row = MemoryMarshal.Cast<byte, ushort>(samples.AsSpan(0, width * depth * 2));
                    for (int x = 0; x < width; x++)
                    {
                        int r = rgba[x * 4], g = rgba[x * 4 + 1], b = rgba[x * 4 + 2], a = rgba[x * 4 + 3];
                        if (grey)
                        {
                            int luma = layout == PixelLayout.R8 || layout == PixelLayout.RG8 ? r : PixelRows.Luma(r, g, b);
                            if (kind == PnmKind.Pbm) row[x] = luma < 128 ? (ushort)1 : (ushort)0;
                            else
                            {
                                row[x * depth] = scale[luma];
                                if (depth == 2) row[x * 2 + 1] = scale[a];
                            }
                        }
                        else
                        {
                            row[x * depth] = scale[r];
                            row[x * depth + 1] = scale[g];
                            row[x * depth + 2] = scale[b];
                            if (depth == 4) row[x * 4 + 3] = scale[a];
                        }
                    }

                    int at = 0;
                    if (plain) at = WritePlain(row, line, kind == PnmKind.Pbm);
                    else if (kind == PnmKind.Pbm)
                    {
                        Array.Clear(line, 0, rowBytes);
                        for (int x = 0; x < width; x++)
                            if (row[x] != 0) line[x >> 3] |= (byte)(0x80 >> (x & 7));
                        at = rowBytes;
                    }
                    else if (wide)
                    {
                        for (int i = 0; i < row.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(line.AsSpan(i * 2), row[i]);
                        at = rowBytes;
                    }
                    else
                    {
                        for (int i = 0; i < row.Length; i++) line[i] = (byte)row[i];
                        at = rowBytes;
                    }
                    output.Write(line, 0, at);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rgba);
                ArrayPool<byte>.Shared.Return(samples);
                ArrayPool<byte>.Shared.Return(line);
            }
        }

        // One raw maxval-255 row converted straight from the source.
        private static void WriteDirect(ReadOnlySpan<byte> src, Span<byte> line, int width, PixelLayout layout, PnmKind kind)
        {
            switch (kind)
            {
                case PnmKind.Ppm:
                    PixelRows.ToRgb(src, line, width, layout);
                    break;
                case PnmKind.Pam:
                    src.Slice(0, width * 4).CopyTo(line);
                    break;
                default:
                    if (layout == PixelLayout.R8) src.Slice(0, width).CopyTo(line);
                    else if (layout == PixelLayout.RG8)
                        for (int x = 0; x < width; x++) line[x] = src[x * 2];
                    else
                    {
                        int first = layout == PixelLayout.RGBA8 ? 0 : 2, last = 2 - first;
                        for (int x = 0; x < width; x++) line[x] = PixelRows.Luma(src[x * 4 + first], src[x * 4 + 1], src[x * 4 + last]);
                    }
                    break;
            }
        }

        private static void WriteHeader(Stream output, PnmKind kind, PamTupleType tuple, bool plain, int width, int height, int depth, int maxval)
        {
            StringBuilder text = new StringBuilder(96);
            if (kind == PnmKind.Pam)
            {
                string type = tuple switch
                {
                    PamTupleType.BlackAndWhite => "BLACKANDWHITE",
                    PamTupleType.BlackAndWhiteAlpha => "BLACKANDWHITE_ALPHA",
                    PamTupleType.Grayscale => "GRAYSCALE",
                    PamTupleType.GrayscaleAlpha => "GRAYSCALE_ALPHA",
                    PamTupleType.Rgb => "RGB",
                    _ => "RGB_ALPHA"
                };
                text.Append("P7\nWIDTH ").Append(width).Append("\nHEIGHT ").Append(height).Append("\nDEPTH ").Append(depth)
                    .Append("\nMAXVAL ").Append(maxval).Append("\nTUPLTYPE ").Append(type).Append("\nENDHDR\n");
            }
            else
            {
                int magic = kind == PnmKind.Pbm ? 1 : kind == PnmKind.Pgm ? 2 : 3;
                text.Append('P').Append(plain ? magic : magic + 3).Append('\n').Append(width).Append(' ').Append(height).Append('\n');
                if (kind != PnmKind.Pbm) text.Append(maxval).Append('\n');
            }
            output.Write(Encoding.ASCII.GetBytes(text.ToString()));
        }

        // One row of samples as ASCII, wrapped before 70 columns: bits without separators, numbers with spaces.
        private static int WritePlain(ReadOnlySpan<ushort> row, byte[] line, bool bits)
        {
            int at = 0, column = 0;
            for (int i = 0; i < row.Length; i++)
            {
                if (bits)
                {
                    if (column == PlainLine)
                    {
                        line[at++] = (byte)'\n';
                        column = 0;
                    }
                    line[at++] = row[i] != 0 ? (byte)'1' : (byte)'0';
                    column++;
                    continue;
                }
                int digits = row[i] < 10 ? 1 : row[i] < 100 ? 2 : row[i] < 1000 ? 3 : row[i] < 10000 ? 4 : 5;
                if (column > 0 && column + 1 + digits > PlainLine)
                {
                    line[at++] = (byte)'\n';
                    column = 0;
                }
                else if (column > 0)
                {
                    line[at++] = (byte)' ';
                    column++;
                }
                Utf8Formatter.TryFormat(row[i], line.AsSpan(at), out int written);
                at += written;
                column += written;
            }
            line[at++] = (byte)'\n';
            return at;
        }
    }
}
