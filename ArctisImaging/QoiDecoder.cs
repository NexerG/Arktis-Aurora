using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Arctis.Imaging
{
    // Quite OK Image format: run, index, diff, luma and literal chunks into RGBA8.
    internal static class QoiDecoder
    {
        private const int HeaderSize = 14, EndSize = 8;
        private const int OpIndex = 0x00, OpDiff = 0x40, OpLuma = 0x80, OpRun = 0xC0, OpRgb = 0xFE, OpRgba = 0xFF;

        internal static bool TryReadHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (data.Length < HeaderSize + EndSize || data[0] != (byte)'q' || data[1] != (byte)'o' || data[2] != (byte)'i' || data[3] != (byte)'f') return false;
            uint width = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4)), height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8));
            int channels = data[12];
            if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue || (channels != 3 && channels != 4) || data[13] > 1) return false;
            header = new ImageHeader(ImageFormat.Qoi, (int)width, (int)height, channels, 8, channels == 4);
            return true;
        }

        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int pitch, PixelLayout layout)
        {
            if (!TryReadHeader(data, out ImageHeader header)) throw new ImageFormatException("QOI header is invalid");
            int width = header.Width;
            byte[] rgba = ArrayPool<byte>.Shared.Rent(width * 4);
            Span<uint> index = stackalloc uint[64];
            index.Clear();
            try
            {
                int pos = HeaderSize, end = data.Length - EndSize, run = 0;
                byte r = 0, g = 0, b = 0, a = 255;
                for (int y = 0; y < header.Height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (run > 0) run--;
                        else
                        {
                            if (pos >= end) throw new ImageFormatException("QOI chunks run past the end");
                            int op = data[pos++];
                            if (op == OpRgb)
                            {
                                if (pos + 3 > end) throw new ImageFormatException("QOI chunks run past the end");
                                r = data[pos];
                                g = data[pos + 1];
                                b = data[pos + 2];
                                pos += 3;
                            }
                            else if (op == OpRgba)
                            {
                                if (pos + 4 > end) throw new ImageFormatException("QOI chunks run past the end");
                                r = data[pos];
                                g = data[pos + 1];
                                b = data[pos + 2];
                                a = data[pos + 3];
                                pos += 4;
                            }
                            else switch (op & 0xC0)
                            {
                                case OpIndex:
                                    uint c = index[op];
                                    r = (byte)c;
                                    g = (byte)(c >> 8);
                                    b = (byte)(c >> 16);
                                    a = (byte)(c >> 24);
                                    break;
                                case OpDiff:
                                    r += (byte)(((op >> 4) & 3) - 2);
                                    g += (byte)(((op >> 2) & 3) - 2);
                                    b += (byte)((op & 3) - 2);
                                    break;
                                case OpLuma:
                                    if (pos >= end) throw new ImageFormatException("QOI chunks run past the end");
                                    int dg = (op & 0x3F) - 32, rb = data[pos++];
                                    r += (byte)(dg - 8 + (rb >> 4));
                                    g += (byte)dg;
                                    b += (byte)(dg - 8 + (rb & 15));
                                    break;
                                default:
                                    run = op & 0x3F;
                                    break;
                            }
                            index[(r * 3 + g * 5 + b * 7 + a * 11) & 63] = r | (uint)g << 8 | (uint)b << 16 | (uint)a << 24;
                        }
                        rgba[x * 4] = r;
                        rgba[x * 4 + 1] = g;
                        rgba[x * 4 + 2] = b;
                        rgba[x * 4 + 3] = a;
                    }
                    PixelRows.FromRgba(rgba, destination.Slice(y * pitch), width, layout);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rgba);
            }
        }
    }
}
