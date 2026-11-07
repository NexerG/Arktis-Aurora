using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;

namespace Arctis.Imaging
{
    // Quite OK Image format, chunk for chunk what the reference qoi.h encoder writes.
    internal static class QoiEncoder
    {
        private const int OpDiff = 0x40, OpLuma = 0x80, OpRun = 0xC0, OpRgb = 0xFE, OpRgba = 0xFF;
        private const int BufferSize = 1 << 16;

        internal static void Encode(ReadOnlySpan<byte> pixels, int width, int height, int pitch, PixelLayout layout, Stream output, in QoiSettings settings)
        {
            int channels = settings.ChannelsFor(layout);
            Span<byte> header = stackalloc byte[14];
            header[0] = (byte)'q';
            header[1] = (byte)'o';
            header[2] = (byte)'i';
            header[3] = (byte)'f';
            BinaryPrimitives.WriteUInt32BigEndian(header.Slice(4), (uint)width);
            BinaryPrimitives.WriteUInt32BigEndian(header.Slice(8), (uint)height);
            header[12] = (byte)channels;
            header[13] = (byte)settings.Colorspace;
            output.Write(header);

            byte[] rgba = ArrayPool<byte>.Shared.Rent(width * 4);
            byte[] chunks = ArrayPool<byte>.Shared.Rent(BufferSize);
            Span<uint> index = stackalloc uint[64];
            index.Clear();
            try
            {
                uint opaque = channels == 3 ? 0xFF000000u : 0u;
                uint prev = 0xFF000000u;
                int run = 0, at = 0;
                for (int y = 0; y < height; y++)
                {
                    ReadOnlySpan<byte> row = pixels.Slice(y * pitch);
                    if (layout != PixelLayout.RGBA8)
                    {
                        PixelRows.ToRgba(row, rgba, width, layout);
                        row = rgba;
                    }
                    for (int x = 0; x < width; x++)
                    {
                        if (at > BufferSize - 8)
                        {
                            output.Write(chunks, 0, at);
                            at = 0;
                        }
                        uint px = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(x * 4)) | opaque;
                        if (px == prev)
                        {
                            if (++run == 62)
                            {
                                chunks[at++] = (byte)(OpRun | 61);
                                run = 0;
                            }
                            continue;
                        }
                        if (run > 0)
                        {
                            chunks[at++] = (byte)(OpRun | (run - 1));
                            run = 0;
                        }

                        int r = (byte)px, g = (byte)(px >> 8), b = (byte)(px >> 16), a = (byte)(px >> 24);
                        int slot = (r * 3 + g * 5 + b * 7 + a * 11) & 63;
                        if (index[slot] == px) chunks[at++] = (byte)slot;
                        else
                        {
                            index[slot] = px;
                            if (a == (byte)(prev >> 24))
                            {
                                int dr = (sbyte)(r - (byte)prev), dg = (sbyte)(g - (byte)(prev >> 8)), db = (sbyte)(b - (byte)(prev >> 16));
                                int drg = dr - dg, dbg = db - dg;
                                if (dr >= -2 && dr <= 1 && dg >= -2 && dg <= 1 && db >= -2 && db <= 1)
                                    chunks[at++] = (byte)(OpDiff | (dr + 2) << 4 | (dg + 2) << 2 | (db + 2));
                                else if (drg >= -8 && drg <= 7 && dg >= -32 && dg <= 31 && dbg >= -8 && dbg <= 7)
                                {
                                    chunks[at++] = (byte)(OpLuma | (dg + 32));
                                    chunks[at++] = (byte)((drg + 8) << 4 | (dbg + 8));
                                }
                                else
                                {
                                    chunks[at++] = OpRgb;
                                    chunks[at++] = (byte)r;
                                    chunks[at++] = (byte)g;
                                    chunks[at++] = (byte)b;
                                }
                            }
                            else
                            {
                                chunks[at++] = OpRgba;
                                BinaryPrimitives.WriteUInt32LittleEndian(chunks.AsSpan(at), px);
                                at += 4;
                            }
                        }
                        prev = px;
                    }
                }
                if (at > BufferSize - 9)
                {
                    output.Write(chunks, 0, at);
                    at = 0;
                }
                if (run > 0) chunks[at++] = (byte)(OpRun | (run - 1));
                for (int i = 0; i < 7; i++) chunks[at++] = 0;
                chunks[at++] = 1;
                output.Write(chunks, 0, at);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rgba);
                ArrayPool<byte>.Shared.Return(chunks);
            }
        }
    }
}
