using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Arctis.Imaging
{
    // Fastest deflate; each row filtered by Sub or Up, whichever leaves the smaller sum of residuals.
    internal static class PngEncoder
    {
        internal static void Encode(ReadOnlySpan<byte> pixels, int width, int height, int pitch, PixelLayout layout, Stream output)
        {
            int bpp = ImageCodec.BytesPerPixel(layout);
            int rowBytes = width * bpp;
            byte colorType = layout switch
            {
                PixelLayout.R8 => 0,
                PixelLayout.RG8 => 4,
                _ => 6
            };

            Span<byte> signature = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(signature, 0x89504E470D0A1A0Aul);
            Span<byte> ihdr = stackalloc byte[13];
            ihdr.Clear();
            BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4), height);
            ihdr[8] = 8;
            ihdr[9] = colorType;
            output.Write(signature);
            WriteChunk(output, 0x49484452, ihdr);

            MemoryStream compressed = new MemoryStream();
            byte[] buffers = ArrayPool<byte>.Shared.Rent(4 * (rowBytes + 1));
            try
            {
                Span<byte> prev = buffers.AsSpan(0, rowBytes);
                Span<byte> cur = buffers.AsSpan(rowBytes, rowBytes);
                Span<byte> sub = buffers.AsSpan(2 * rowBytes, rowBytes + 1);
                Span<byte> up = buffers.AsSpan(3 * rowBytes + 1, rowBytes + 1);
                prev.Clear();
                using (ZLibStream deflate = new ZLibStream(compressed, CompressionLevel.Fastest, true))
                {
                    for (int y = 0; y < height; y++)
                    {
                        pixels.Slice(y * pitch, rowBytes).CopyTo(cur);
                        if (layout == PixelLayout.BGRA8) ImageOps.SwizzleBgraToRgba(cur);

                        sub[0] = 1;
                        up[0] = 2;
                        long subCost = 0, upCost = 0;
                        for (int i = 0; i < rowBytes; i++)
                        {
                            byte s = (byte)(cur[i] - (i >= bpp ? cur[i - bpp] : 0));
                            byte u = (byte)(cur[i] - prev[i]);
                            sub[i + 1] = s;
                            up[i + 1] = u;
                            subCost += (sbyte)s < 0 ? -(sbyte)s : s;
                            upCost += (sbyte)u < 0 ? -(sbyte)u : u;
                        }
                        deflate.Write(subCost <= upCost ? sub : up);

                        Span<byte> swap = prev;
                        prev = cur;
                        cur = swap;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffers);
            }

            WriteChunk(output, 0x49444154, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
            WriteChunk(output, 0x49454E44, ReadOnlySpan<byte>.Empty);
        }

        private static void WriteChunk(Stream output, uint type, ReadOnlySpan<byte> body)
        {
            Span<byte> head = stackalloc byte[8];
            BinaryPrimitives.WriteInt32BigEndian(head, body.Length);
            BinaryPrimitives.WriteUInt32BigEndian(head.Slice(4), type);
            uint crc = Crc32.Update(Crc32.Update(0, head.Slice(4)), body);
            Span<byte> tail = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
            output.Write(head);
            output.Write(body);
            output.Write(tail);
        }
    }
}
