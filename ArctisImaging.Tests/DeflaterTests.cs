using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace Arctis.Imaging.Tests
{
    public class DeflaterTests
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

        // runs of one byte, short repeating periods and random stretches
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

        // random bytes repeated with the given period, so every match lies exactly that far back
        private static byte[] Periodic(int period, int length)
        {
            byte[] unit = Random(period, period);
            byte[] data = new byte[length];
            for (int i = 0; i < length; i++) data[i] = unit[i % period];
            return data;
        }

        // every input end to end; input k is inputs[starts[k]..starts[k + 1]]
        private static readonly int[] starts = { 0, 0, 1, 3, 70003, 270003, 570003, 670003, 770003, 870003, 875003 };
        private static readonly byte[] inputs = Concatenate();

        private static byte[] Concatenate()
        {
            byte[] all = new byte[starts[starts.Length - 1]];
            all[0] = 42;
            all[1] = 1;
            all[2] = 2;
            Random(70000, 1).CopyTo(all, starts[3]);
            Text(200000, 2).CopyTo(all, starts[4]);
            Runs(300000, 3).CopyTo(all, starts[5]);
            Periodic(32506, 100000).CopyTo(all, starts[7]);
            Periodic(32768, 100000).CopyTo(all, starts[8]);
            Periodic(3, 5000).CopyTo(all, starts[9]);
            return all;
        }

        private static byte[] Input(int k) => inputs.AsSpan(starts[k], starts[k + 1] - starts[k]).ToArray();

        private static byte[] Deflate(byte[] data, in DeflateSettings settings)
        {
            MemoryStream z = new MemoryStream();
            Deflater.Deflate(data, z, settings);
            return z.ToArray();
        }

        private static byte[] InflateWithZlib(byte[] zlib)
        {
            MemoryStream output = new MemoryStream();
            using (ZLibStream reference = new ZLibStream(new MemoryStream(zlib), CompressionMode.Decompress))
                reference.CopyTo(output);
            return output.ToArray();
        }

        public static TheoryData<int, int, int, int> Settings => new TheoryData<int, int, int, int>
        {
            { 0, 0, 0, 0 },
            { 4, 4, 16, 16 },
            { 32, 258, 258, 4096 },
            { 3, 3, 3, 4 }
        };

        [Theory]
        [MemberData(nameof(Settings))]
        public void RoundTripsThroughBothInflaters(int good, int lazy, int nice, int chain)
        {
            DeflateSettings settings = new DeflateSettings(good, lazy, nice, chain);
            for (int k = 0; k < starts.Length - 1; k++)
            {
                byte[] data = Input(k);
                byte[] zlib = Deflate(data, settings);
                Assert.Equal(data, InflateWithZlib(zlib));
                byte[] ours = new byte[data.Length];
                Inflater.InflateZlib(zlib, ours);
                Assert.Equal(data, ours);
            }
        }

        [Fact]
        public void ChecksumMatchesZlib()
        {
            for (int k = 1; k < starts.Length - 1; k++)
            {
                byte[] data = Input(k);
                MemoryStream reference = new MemoryStream();
                using (ZLibStream zlib = new ZLibStream(reference, CompressionLevel.Optimal, true))
                    zlib.Write(data);
                byte[] expected = reference.ToArray(), actual = Deflate(data, default);
                Assert.Equal(expected.AsSpan(expected.Length - 4).ToArray(), actual.AsSpan(actual.Length - 4).ToArray());
            }
        }

        [Fact]
        public void EmptyInputIsTheStandardEmptyStream()
        {
            Assert.Equal(new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 }, Deflate(Array.Empty<byte>(), default));
        }

        [Fact]
        public void IncompressibleInputIsStored()
        {
            byte[] data = Random(10000, 9);
            Assert.Equal(data.Length + 2 + 5 + 4, Deflate(data, default).Length);
        }

        [Theory]
        [InlineData(2, 0, 0, 0)]
        [InlineData(0, 259, 0, 0)]
        [InlineData(0, 0, 300, 0)]
        [InlineData(0, 0, 0, 3)]
        public void OutOfRangeSettingsAreRejected(int good, int lazy, int nice, int chain)
        {
            Assert.Throws<ArgumentException>(() => Deflate(new byte[] { 1 }, new DeflateSettings(good, lazy, nice, chain)));
        }
    }
}
