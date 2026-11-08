using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Arctis.Imaging
{
    // Netpbm: PBM, PGM and PPM, plain (P1-P3) or raw (P4-P6), and PAM (P7); samples are scaled from maxval to 8 bits.
    internal static class PnmDecoder
    {
        private struct Info
        {
            public int kind, width, height, maxval, depth, rasterStart;
        }

        internal static bool TryReadHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (!TryReadInfo(data, out Info info)) return false;
            header = new ImageHeader(ImageFormat.Pnm, info.width, info.height, info.depth, info.maxval > 255 ? 16 : 8, info.depth == 2 || info.depth == 4);
            return true;
        }

        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int pitch, PixelLayout layout)
        {
            if (!TryReadInfo(data, out Info info)) throw new ImageFormatException("Netpbm header is invalid");
            int width = info.width, depth = info.depth, maxval = info.maxval;
            bool bits = info.kind == 1 || info.kind == 4, plain = info.kind <= 3, wide = maxval > 255;
            long rowBytes = bits ? (width + 7) / 8 : (long)width * depth * (wide ? 2 : 1);
            if (!plain && info.rasterStart + rowBytes * info.height > data.Length) throw new ImageFormatException("Netpbm raster runs past the end");
            if (!plain && !bits && maxval == 255 && DecodeDirect(data, in info, destination, pitch, layout)) return;

            byte[] rgba = ArrayPool<byte>.Shared.Rent(width * 4);
            Span<byte> scale = stackalloc byte[256];
            for (int v = 0; v < 256; v++) scale[v] = v > maxval ? (byte)255 : (byte)((v * 255 + maxval / 2) / Math.Max(maxval, 1));
            try
            {
                int pos = info.rasterStart;
                Span<int> sample = stackalloc int[4];
                for (int y = 0; y < info.height; y++)
                {
                    ReadOnlySpan<byte> row = plain ? default : data.Slice(info.rasterStart + (int)(y * rowBytes));
                    for (int x = 0; x < width; x++)
                    {
                        if (bits)
                        {
                            int bit = plain ? ReadBit(data, ref pos) : (row[x >> 3] >> (7 - (x & 7))) & 1;
                            byte grey = bit != 0 ? (byte)0 : (byte)255;
                            rgba[x * 4] = rgba[x * 4 + 1] = rgba[x * 4 + 2] = grey;
                            rgba[x * 4 + 3] = 255;
                            continue;
                        }
                        for (int s = 0; s < depth; s++)
                        {
                            int v = plain ? ReadNumber(data, ref pos) : wide ? BinaryPrimitives.ReadUInt16BigEndian(row.Slice((x * depth + s) * 2)) : row[x * depth + s];
                            sample[s] = v <= 255 ? scale[v] : v >= maxval ? 255 : (int)(((long)v * 255 + maxval / 2) / maxval);
                        }
                        if (depth <= 2)
                        {
                            rgba[x * 4] = rgba[x * 4 + 1] = rgba[x * 4 + 2] = (byte)sample[0];
                            rgba[x * 4 + 3] = depth == 2 ? (byte)sample[1] : (byte)255;
                        }
                        else
                        {
                            rgba[x * 4] = (byte)sample[0];
                            rgba[x * 4 + 1] = (byte)sample[1];
                            rgba[x * 4 + 2] = (byte)sample[2];
                            rgba[x * 4 + 3] = depth == 4 ? (byte)sample[3] : (byte)255;
                        }
                    }
                    PixelRows.FromRgba(rgba, destination.Slice(y * pitch), width, layout);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rgba);
            }
        }

        // Binary maxval-255 rows straight from the raster into the layout; false leaves the image to the general path.
        private static bool DecodeDirect(ReadOnlySpan<byte> data, in Info info, Span<byte> destination, int pitch, PixelLayout layout)
        {
            int width = info.width, depth = info.depth, rowBytes = width * depth;
            bool bgra = layout == PixelLayout.BGRA8, wide = bgra || layout == PixelLayout.RGBA8;
            if (depth == 2 || (depth >= 3 && !wide)) return false;
            for (int y = 0; y < info.height; y++)
            {
                ReadOnlySpan<byte> src = data.Slice(info.rasterStart + y * rowBytes);
                Span<byte> row = destination.Slice(y * pitch);
                if (depth == 3) PixelRows.FromRgb(src, row, width, bgra);
                else if (depth == 4 && bgra) ImageOps.CopySwizzled(src.Slice(0, rowBytes), row);
                else if (depth == 4) src.Slice(0, rowBytes).CopyTo(row);
                else if (wide) PixelRows.FromGrey(src, row, width);
                else if (layout == PixelLayout.R8) src.Slice(0, width).CopyTo(row);
                else
                    for (int x = 0; x < width; x++)
                    {
                        row[x * 2] = src[x];
                        row[x * 2 + 1] = 255;
                    }
            }
            return true;
        }

        private static bool TryReadInfo(ReadOnlySpan<byte> data, out Info info)
        {
            info = default;
            if (data.Length < 3 || data[0] != (byte)'P' || data[1] < (byte)'1' || data[1] > (byte)'7' || !IsSpace(data[2])) return false;
            info.kind = data[1] - '0';
            int pos = 2;
            if (info.kind == 7) return TryReadPam(data, ref info);

            info.width = ReadHeaderNumber(data, ref pos);
            info.height = ReadHeaderNumber(data, ref pos);
            bool bits = info.kind == 1 || info.kind == 4;
            info.maxval = bits ? 1 : ReadHeaderNumber(data, ref pos);
            info.depth = info.kind == 3 || info.kind == 6 ? 3 : 1;
            if (info.width <= 0 || info.height <= 0 || info.maxval <= 0 || info.maxval > 65535 || pos >= data.Length || !IsSpace(data[pos])) return false;
            info.rasterStart = pos + 1;
            return true;
        }

        // PAM header lines up to ENDHDR; a missing DEPTH or TUPLTYPE takes what the other implies.
        private static bool TryReadPam(ReadOnlySpan<byte> data, ref Info info)
        {
            int pos = 3;
            info.depth = 0;
            int tupleDepth = 0;
            while (pos < data.Length)
            {
                int end = data.Slice(pos).IndexOf((byte)'\n');
                if (end < 0) return false;
                ReadOnlySpan<byte> line = data.Slice(pos, end).TrimEnd((byte)'\r');
                pos += end + 1;
                if (line.Length == 0 || line[0] == (byte)'#') continue;
                if (line.SequenceEqual("ENDHDR"u8))
                {
                    if (info.depth == 0) info.depth = tupleDepth;
                    info.rasterStart = pos;
                    return info.width > 0 && info.height > 0 && info.maxval > 0 && info.maxval <= 65535 && info.depth >= 1 && info.depth <= 4;
                }
                int space = line.IndexOf((byte)' ');
                if (space < 0) return false;
                ReadOnlySpan<byte> key = line.Slice(0, space), value = line.Slice(space + 1).Trim((byte)' ');
                if (key.SequenceEqual("TUPLTYPE"u8))
                {
                    tupleDepth = value.SequenceEqual("BLACKANDWHITE"u8) || value.SequenceEqual("GRAYSCALE"u8) ? 1
                        : value.SequenceEqual("BLACKANDWHITE_ALPHA"u8) || value.SequenceEqual("GRAYSCALE_ALPHA"u8) ? 2
                        : value.SequenceEqual("RGB"u8) ? 3 : value.SequenceEqual("RGB_ALPHA"u8) ? 4 : 0;
                    continue;
                }
                int p = 0, n = ReadHeaderNumber(value, ref p);
                if (key.SequenceEqual("WIDTH"u8)) info.width = n;
                else if (key.SequenceEqual("HEIGHT"u8)) info.height = n;
                else if (key.SequenceEqual("DEPTH"u8)) info.depth = n;
                else if (key.SequenceEqual("MAXVAL"u8)) info.maxval = n;
            }
            return false;
        }

        // A decimal number after whitespace and comments; -1 when there is none.
        private static int ReadHeaderNumber(ReadOnlySpan<byte> data, ref int pos)
        {
            SkipSpace(data, ref pos);
            if (pos >= data.Length || data[pos] < (byte)'0' || data[pos] > (byte)'9') return -1;
            long n = 0;
            for (; pos < data.Length && data[pos] >= (byte)'0' && data[pos] <= (byte)'9'; pos++)
                if ((n = n * 10 + data[pos] - '0') > int.MaxValue) return -1;
            return (int)n;
        }

        private static int ReadNumber(ReadOnlySpan<byte> data, ref int pos)
        {
            int n = ReadHeaderNumber(data, ref pos);
            if (n < 0) throw new ImageFormatException("Netpbm raster is truncated or holds a non-number");
            return n;
        }

        // Plain PBM digits need no whitespace between them.
        private static int ReadBit(ReadOnlySpan<byte> data, ref int pos)
        {
            SkipSpace(data, ref pos);
            if (pos >= data.Length || (data[pos] != (byte)'0' && data[pos] != (byte)'1')) throw new ImageFormatException("PBM raster is truncated or holds a non-bit");
            return data[pos++] - '0';
        }

        private static void SkipSpace(ReadOnlySpan<byte> data, ref int pos)
        {
            while (pos < data.Length)
            {
                if (data[pos] == (byte)'#')
                    while (pos < data.Length && data[pos] != (byte)'\n') pos++;
                else if (IsSpace(data[pos])) pos++;
                else return;
            }
        }

        private static bool IsSpace(byte b) => b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r' || b == 0x0B || b == 0x0C;
    }
}
