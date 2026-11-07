using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Arctis.Imaging
{
    // ZSoft PCX: 1-bit with 1-4 planes, 2/4/8-bit indexed, 8-bit with 3 or 4 planes; plain or RLE scanlines.
    internal static class PcxDecoder
    {
        private const int HeaderSize = 128, TrailerSize = 769;

        private struct Info
        {
            public int bits, planes, bytesPerLine, width, height, encoding;
        }

        internal static bool TryReadHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (!TryReadInfo(data, out Info info)) return false;
            bool rgb = info.bits == 8 && info.planes >= 3;
            header = new ImageHeader(ImageFormat.Pcx, info.width, info.height, rgb ? info.planes : 3, 8, info.planes == 4 && info.bits == 8);
            return true;
        }

        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int pitch, PixelLayout layout)
        {
            if (!TryReadInfo(data, out Info info)) throw new ImageFormatException("PCX header is invalid");
            int width = info.width, bits = info.bits, planes = info.planes, bpl = info.bytesPerLine;
            int lineBytes = planes * bpl;
            bool rgb = bits == 8 && planes >= 3;

            Span<uint> palette = stackalloc uint[256];
            bool trailer = bits == 8 && planes == 1 && data.Length >= HeaderSize + TrailerSize && data[data.Length - TrailerSize] == 0x0C;
            for (int i = 0; i < 256; i++)
            {
                if (trailer)
                {
                    ReadOnlySpan<byte> e = data.Slice(data.Length - TrailerSize + 1 + i * 3);
                    palette[i] = e[0] | (uint)e[1] << 8 | (uint)e[2] << 16 | 0xFF000000u;
                }
                else if (bits == 8) palette[i] = (uint)i * 0x010101u | 0xFF000000u;
                else if (bits == 1 && planes == 1) palette[i] = i == 0 ? 0xFF000000u : 0xFFFFFFFFu;
                else if (i < 16)
                {
                    ReadOnlySpan<byte> e = data.Slice(16 + i * 3);
                    palette[i] = e[0] | (uint)e[1] << 8 | (uint)e[2] << 16 | 0xFF000000u;
                }
                else palette[i] = 0xFF000000u;
            }

            byte[] line = ArrayPool<byte>.Shared.Rent(lineBytes);
            byte[] rgba = ArrayPool<byte>.Shared.Rent(width * 4);
            try
            {
                int pos = HeaderSize, end = trailer ? data.Length - TrailerSize : data.Length, runLeft = 0;
                byte runValue = 0;
                for (int y = 0; y < info.height; y++)
                {
                    // runs may carry over from one scanline to the next
                    for (int i = 0; i < lineBytes; i++)
                    {
                        if (info.encoding == 0)
                        {
                            if (pos >= end) throw new ImageFormatException("PCX scanlines run past the end");
                            line[i] = data[pos++];
                            continue;
                        }
                        if (runLeft == 0)
                        {
                            if (pos >= end) throw new ImageFormatException("PCX scanlines run past the end");
                            byte b = data[pos++];
                            if (b >= 0xC0)
                            {
                                if (pos >= end) throw new ImageFormatException("PCX scanlines run past the end");
                                runLeft = b & 0x3F;
                                runValue = data[pos++];
                                if (runLeft == 0)
                                {
                                    i--;
                                    continue;
                                }
                            }
                            else
                            {
                                runLeft = 1;
                                runValue = b;
                            }
                        }
                        line[i] = runValue;
                        runLeft--;
                    }

                    for (int x = 0; x < width; x++)
                    {
                        uint c;
                        if (rgb) c = line[x] | (uint)line[bpl + x] << 8 | (uint)line[2 * bpl + x] << 16 | (planes == 4 ? (uint)line[3 * bpl + x] << 24 : 0xFF000000u);
                        else if (bits == 8) c = palette[line[x]];
                        else if (bits == 1)
                        {
                            int index = 0;
                            for (int p = 0; p < planes; p++) index |= ((line[p * bpl + (x >> 3)] >> (7 - (x & 7))) & 1) << p;
                            c = palette[index];
                        }
                        else
                        {
                            int bit = x * bits;
                            c = palette[(line[bit >> 3] >> (8 - bits - (bit & 7))) & ((1 << bits) - 1)];
                        }
                        BinaryPrimitives.WriteUInt32LittleEndian(rgba.AsSpan(x * 4), c);
                    }
                    PixelRows.FromRgba(rgba, destination.Slice(y * pitch), width, layout);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(line);
                ArrayPool<byte>.Shared.Return(rgba);
            }
        }

        private static bool TryReadInfo(ReadOnlySpan<byte> data, out Info info)
        {
            info = default;
            if (data.Length < HeaderSize || data[0] != 0x0A || data[1] > 5 || data[2] > 1) return false;
            info.encoding = data[2];
            info.bits = data[3];
            info.planes = data[65];
            info.bytesPerLine = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(66));
            int xmin = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4)), ymin = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6));
            int xmax = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8)), ymax = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(10));
            info.width = xmax - xmin + 1;
            info.height = ymax - ymin + 1;
            bool known = info.bits == 1 ? info.planes >= 1 && info.planes <= 4
                : info.bits == 2 || info.bits == 4 ? info.planes == 1
                : info.bits == 8 && (info.planes == 1 || info.planes == 3 || info.planes == 4);
            return known && info.width > 0 && info.height > 0 && info.bytesPerLine * 8L >= (long)info.width * info.bits;
        }
    }
}
