using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Arctis.Imaging.Tests
{
    // Writes PNGs independently of the library's encoder: any colour type and depth, a chosen filter, Adam7.
    internal static class PngBuilder
    {
        // rows: unfiltered packed rows, rowBytes each
        // rowFilters, when given, replaces filter: row y takes rowFilters[y % rowFilters.Length]
        internal static byte[] Build(int width, int height, byte colorType, byte depth, byte[] rows, byte filter, byte[]? plte = null, byte[]? trns = null, bool interlaced = false, byte[]? rowFilters = null)
        {
            int channels = colorType switch { 2 => 3, 4 => 2, 6 => 4, _ => 1 };
            int bitsPerPixel = channels * depth;
            int bpp = Math.Max(1, bitsPerPixel / 8);

            MemoryStream raw = new MemoryStream();
            if (!interlaced)
            {
                int rowBytes = (width * bitsPerPixel + 7) / 8;
                WriteFiltered(raw, rows, rowBytes, height, bpp, filter, rowFilters);
            }
            else
            {
                // only whole-byte pixels are interlaced here
                int[] passes = { 0, 0, 8, 8, 4, 0, 8, 8, 0, 4, 4, 8, 2, 0, 4, 4, 0, 2, 2, 4, 1, 0, 2, 2, 0, 1, 1, 2 };
                int rowBytes = width * bpp;
                for (int p = 0; p < 7; p++)
                {
                    int x0 = passes[p * 4], y0 = passes[p * 4 + 1], dx = passes[p * 4 + 2], dy = passes[p * 4 + 3];
                    int pw = (width - x0 + dx - 1) / dx, ph = (height - y0 + dy - 1) / dy;
                    if (pw <= 0 || ph <= 0) continue;
                    byte[] pass = new byte[pw * bpp * ph];
                    for (int j = 0; j < ph; j++)
                        for (int i = 0; i < pw; i++)
                            Array.Copy(rows, (y0 + j * dy) * rowBytes + (x0 + i * dx) * bpp, pass, (j * pw + i) * bpp, bpp);
                    WriteFiltered(raw, pass, pw * bpp, ph, bpp, filter, rowFilters);
                }
            }

            MemoryStream png = new MemoryStream();
            png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            byte[] ihdr = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
            ihdr[8] = depth;
            ihdr[9] = colorType;
            ihdr[12] = interlaced ? (byte)1 : (byte)0;
            Chunk(png, "IHDR", ihdr);
            if (plte != null) Chunk(png, "PLTE", plte);
            if (trns != null) Chunk(png, "tRNS", trns);

            MemoryStream z = new MemoryStream();
            using (ZLibStream deflate = new ZLibStream(z, CompressionLevel.Optimal, true))
                deflate.Write(raw.ToArray());
            byte[] data = z.ToArray();
            // split the image data across two chunks
            int half = data.Length / 2;
            Chunk(png, "IDAT", data.AsSpan(0, half).ToArray());
            Chunk(png, "IDAT", data.AsSpan(half).ToArray());
            Chunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        internal static void Chunk(Stream s, string type, byte[] body)
        {
            byte[] head = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(head, body.Length);
            Encoding.ASCII.GetBytes(type, head.AsSpan(4));
            s.Write(head);
            s.Write(body);
            byte[] crcInput = new byte[4 + body.Length];
            head.AsSpan(4).CopyTo(crcInput);
            body.CopyTo(crcInput, 4);
            byte[] crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, ReferenceCrc(crcInput));
            s.Write(crc);
        }

        // bit-at-a-time CRC, independent of the library's sliced one
        internal static uint ReferenceCrc(ReadOnlySpan<byte> data)
        {
            uint c = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                c ^= b;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            return ~c;
        }

        private static void WriteFiltered(Stream raw, byte[] rows, int rowBytes, int height, int bpp, byte rowFilter, byte[]? rowFilters)
        {
            byte[] prev = new byte[rowBytes];
            for (int y = 0; y < height; y++)
            {
                byte filter = rowFilters != null ? rowFilters[y % rowFilters.Length] : rowFilter;
                raw.WriteByte(filter);
                for (int i = 0; i < rowBytes; i++)
                {
                    int x = rows[y * rowBytes + i];
                    int a = i >= bpp ? rows[y * rowBytes + i - bpp] : 0;
                    int b = prev[i];
                    int c = i >= bpp ? prev[i - bpp] : 0;
                    int predicted = filter switch
                    {
                        1 => a,
                        2 => b,
                        3 => (a + b) >> 1,
                        4 => Paeth(a, b, c),
                        _ => 0
                    };
                    raw.WriteByte((byte)(x - predicted));
                }
                Array.Copy(rows, y * rowBytes, prev, 0, rowBytes);
            }
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            return pb <= pc ? b : c;
        }
    }
}
