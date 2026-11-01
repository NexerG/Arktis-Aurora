using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace Arctis.Imaging.Tests
{
    public class InflaterTests
    {
        private static uint Next(ref uint s) => s = s * 1664525u + 1013904223u;

        private static byte[] Random(int length, int seed)
        {
            byte[] data = new byte[length];
            uint s = (uint)seed * 2654435761u + 1;
            for (int i = 0; i < length; i++) data[i] = (byte)(Next(ref s) >> 24);
            return data;
        }

        private static byte[] Text(int length, int seed)
        {
            string[] words = { "the", "atlas", "glyph", "render", "vulkan", "pixel", "row", "filter", "paeth", "of", "a", "and" };
            StringBuilder text = new StringBuilder(length + 16);
            uint s = (uint)seed + 7;
            while (text.Length < length) text.Append(words[Next(ref s) % (uint)words.Length]).Append(' ');
            return Encoding.ASCII.GetBytes(text.ToString(0, length));
        }

        // runs of one byte, short repeating periods (overlapping matches) and random stretches
        private static byte[] Runs(int length, int seed)
        {
            byte[] data = new byte[length];
            uint s = (uint)seed + 3;
            for (int i = 0; i < length;)
            {
                int kind = (int)(Next(ref s) % 3), n = Math.Min(length - i, 1 + (int)(Next(ref s) % 300));
                int period = 1 + (int)(Next(ref s) % 12);
                for (int k = 0; k < n; k++)
                    data[i + k] = kind == 0 ? (byte)period : kind == 1 ? (byte)((k % period) * 37) : (byte)(Next(ref s) >> 24);
                i += n;
            }
            return data;
        }

        private static byte[] Compress(byte[] data, CompressionLevel level)
        {
            MemoryStream z = new MemoryStream();
            using (ZLibStream deflate = new ZLibStream(z, level, true))
                deflate.Write(data);
            return z.ToArray();
        }

        // The zlib stream in IDAT chunks of at most size bytes, then IEND; checksums are left zero.
        private static byte[] Chunked(ReadOnlySpan<byte> zlib, int size)
        {
            MemoryStream png = new MemoryStream();
            Span<byte> header = stackalloc byte[8];
            for (int at = 0; at < zlib.Length || at == 0; at += size)
            {
                int n = Math.Min(size, zlib.Length - at);
                BinaryPrimitives.WriteInt32BigEndian(header, n);
                BinaryPrimitives.WriteUInt32BigEndian(header.Slice(4), 0x49444154);
                png.Write(header);
                png.Write(zlib.Slice(at, n));
                png.Write(new byte[4]);
                if (zlib.Length == 0) break;
            }
            png.Write(new byte[] { 0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0, 0, 0, 0 });
            return png.ToArray();
        }

        private static byte[] Inflate(byte[] png, int length)
        {
            byte[] output = new byte[length];
            Inflater.Inflate(png, 0, output);
            return output;
        }

        [Theory]
        [InlineData(CompressionLevel.NoCompression)]
        [InlineData(CompressionLevel.Fastest)]
        [InlineData(CompressionLevel.Optimal)]
        [InlineData(CompressionLevel.SmallestSize)]
        public void MatchesZlibOnEveryKindOfData(CompressionLevel level)
        {
            byte[][] inputs = { Random(70000, 1), Text(90000, 2), Runs(120000, 3), new byte[] { 42 } };
            foreach (byte[] data in inputs)
                Assert.Equal(data, Inflate(Chunked(Compress(data, level), 1 << 16), data.Length));
        }

        [Fact]
        public void DecodesTheEmptyStream()
        {
            byte[] zlib = { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 };
            Assert.Empty(Inflate(Chunked(zlib, 1 << 16), 0));
        }

        [Fact]
        public void DecodesAFixedHuffmanBlock()
        {
            // "abc", then a match of 6 at distance 3, then end of block: "abcabcabc"
            ulong bits = 0;
            int count = 0;
            void Put(uint value, int n)
            {
                bits |= (ulong)value << count;
                count += n;
            }
            void Code(uint code, int n)
            {
                uint r = 0;
                for (int i = 0; i < n; i++) r = r << 1 | (code >> i & 1);
                Put(r, n);
            }
            Put(1, 1);
            Put(1, 2);
            foreach (char ch in "abc") Code(0x30u + ch, 8);
            Code(260 - 256, 7);
            Code(2, 5);
            Code(0, 7);

            byte[] expected = Encoding.ASCII.GetBytes("abcabcabc");
            MemoryStream z = new MemoryStream();
            z.Write(new byte[] { 0x78, 0x01 });
            for (int i = 0; i < (count + 7) / 8; i++) z.WriteByte((byte)(bits >> (i * 8)));
            uint a = 1, b = 0;
            foreach (byte x in expected)
            {
                a = (a + x) % 65521;
                b = (b + a) % 65521;
            }
            Span<byte> adler = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(adler, b << 16 | a);
            z.Write(adler);
            byte[] zlib = z.ToArray();

            using (ZLibStream reference = new ZLibStream(new MemoryStream(zlib), CompressionMode.Decompress))
            {
                byte[] check = new byte[expected.Length];
                reference.ReadExactly(check);
                Assert.Equal(expected, check);
            }
            Assert.Equal(expected, Inflate(Chunked(zlib, 1 << 16), expected.Length));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(7)]
        [InlineData(4096)]
        public void ChunkBoundariesFallAnywhere(int size)
        {
            byte[] data = Runs(50000, 9);
            byte[] zlib = Compress(data, CompressionLevel.Optimal);
            Assert.Equal(data, Inflate(Chunked(zlib, size), data.Length));
            byte[] stored = Compress(data, CompressionLevel.NoCompression);
            Assert.Equal(data, Inflate(Chunked(stored, size), data.Length));
        }

        [Fact]
        public void EveryTruncationIsRejected()
        {
            byte[] data = Text(3000, 4);
            byte[] zlib = Compress(data, CompressionLevel.Optimal);
            for (int cut = 0; cut < zlib.Length; cut++)
            {
                byte[] png = Chunked(zlib.AsSpan(0, cut), 1 << 16);
                Assert.Throws<ImageFormatException>(() => Inflate(png, data.Length));
            }
        }

        [Fact]
        public void LengthMismatchIsRejected()
        {
            byte[] data = Runs(5000, 5);
            byte[] png = Chunked(Compress(data, CompressionLevel.Optimal), 1 << 16);
            Assert.Throws<ImageFormatException>(() => Inflate(png, data.Length - 1));
            Assert.Throws<ImageFormatException>(() => Inflate(png, data.Length + 1));
        }

        [Fact]
        public void BadChecksumIsRejected()
        {
            byte[] data = Text(5000, 6);
            byte[] zlib = Compress(data, CompressionLevel.Optimal);
            zlib[^1] ^= 1;
            Assert.Throws<ImageFormatException>(() => Inflate(Chunked(zlib, 1 << 16), data.Length));
        }

        [Fact]
        public void CorruptStreamsThrowOnlyFormatErrorsAndNeverWritePastTheOutput()
        {
            const int guard = 64;
            byte[][] sources = { Runs(4000, 7), Text(4000, 8) };
            uint s = 12345;
            for (int round = 0; round < 2000; round++)
            {
                byte[] data = sources[round & 1];
                byte[] zlib = Compress(data, round % 3 == 0 ? CompressionLevel.Fastest : CompressionLevel.Optimal);
                int flips = 1 + (int)(Next(ref s) % 3);
                for (int f = 0; f < flips; f++) zlib[2 + Next(ref s) % (uint)(zlib.Length - 2)] ^= (byte)(1 + Next(ref s) % 255);

                byte[] buffer = new byte[data.Length + guard];
                buffer.AsSpan(data.Length).Fill(0xA5);
                try
                {
                    Inflater.Inflate(Chunked(zlib, 1 << 16), 0, buffer.AsSpan(0, data.Length));
                }
                catch (ImageFormatException)
                {
                }
                for (int i = data.Length; i < buffer.Length; i++) Assert.Equal(0xA5, buffer[i]);
            }
        }
    }
}
