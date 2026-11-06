using System;
using System.IO;
using Xunit;

namespace Arctis.Imaging.Tests
{
    public class AdfTests
    {
        private static int Median(int a, int b, int c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

        // Distance-field-like ramps around circles, channels offset near their edges, alpha the median; or uniform noise.
        private static byte[] Field(int width, int height, bool noise, int seed)
        {
            byte[] rgba = new byte[width * height * 4];
            uint s = (uint)seed * 2654435761u + 7;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    for (int c = 0; c < 3; c++)
                    {
                        int value;
                        if (noise)
                        {
                            s = s * 1664525u + 1013904223u;
                            value = (int)(s >> 24);
                        }
                        else
                        {
                            float dx = x % 40 - 20 + c, dy = y % 40 - 20;
                            value = Math.Clamp((int)(128 + 32 * (12 - MathF.Sqrt(dx * dx + dy * dy))), 0, 255);
                        }
                        rgba[i + c] = (byte)value;
                    }
                    rgba[i + 3] = (byte)Median(rgba[i], rgba[i + 1], rgba[i + 2]);
                }
            return rgba;
        }

        private static byte[] Encode(byte[] rgba, int width, int height)
        {
            MemoryStream file = new MemoryStream();
            ImageCodec.EncodeAdf(rgba, width, height, 0, file);
            return file.ToArray();
        }

        private static byte[] Valid() => Encode(Field(300, 40, false, 1), 300, 40);

        public static TheoryData<int, int, bool> Shapes()
        {
            TheoryData<int, int, bool> shapes = new TheoryData<int, int, bool>();
            int[][] sizes = { new[] { 1, 1 }, new[] { 7, 5 }, new[] { 15, 33 }, new[] { 256, 256 }, new[] { 300, 270 }, new[] { 513, 17 } };
            foreach (int[] size in sizes)
            {
                shapes.Add(size[0], size[1], false);
                shapes.Add(size[0], size[1], true);
            }
            return shapes;
        }

        [Theory]
        [MemberData(nameof(Shapes))]
        public void RoundTripsWithinOne(int width, int height, bool noise)
        {
            byte[] rgba = Field(width, height, noise, width * 31 + height);
            byte[] file = Encode(rgba, width, height);
            byte[] decoded = new byte[rgba.Length];
            ImageHeader header = ImageCodec.Decode(file, decoded, new DecodeOptions(PixelLayout.RGBA8));
            Assert.Equal(ImageFormat.Adf, header.Format);
            for (int i = 0; i < rgba.Length; i += 4)
            {
                for (int c = 0; c < 3; c++)
                    Assert.True(Math.Abs(rgba[i + c] - decoded[i + c]) <= 1, $"texel {i / 4} channel {c}: {rgba[i + c]} became {decoded[i + c]}");
                Assert.Equal(Median(decoded[i], decoded[i + 1], decoded[i + 2]), decoded[i + 3]);
            }
        }

        [Fact]
        public void DecodeHonoursRowPitch()
        {
            byte[] rgba = Field(37, 11, false, 3);
            byte[] file = Encode(rgba, 37, 11);
            byte[] tight = new byte[rgba.Length];
            ImageCodec.Decode(file, tight, new DecodeOptions(PixelLayout.RGBA8));
            int pitch = 37 * 4 + 12;
            byte[] padded = new byte[pitch * 11];
            Array.Fill(padded, (byte)0xCD);
            ImageCodec.Decode(file, padded, new DecodeOptions(PixelLayout.RGBA8, pitch));
            for (int y = 0; y < 11; y++)
            {
                Assert.True(padded.AsSpan(y * pitch, 37 * 4).SequenceEqual(tight.AsSpan(y * 37 * 4, 37 * 4)));
                if (y < 10) Assert.All(padded.AsSpan(y * pitch + 37 * 4, 12).ToArray(), b => Assert.Equal(0xCD, b));
            }
        }

        [Fact]
        public void IdentifiesTheHeader()
        {
            Assert.True(ImageCodec.TryIdentify(Valid(), out ImageHeader header));
            Assert.Equal(ImageFormat.Adf, header.Format);
            Assert.Equal(300, header.Width);
            Assert.Equal(40, header.Height);
        }

        [Fact]
        public void AlphaThatIsNotTheMedianIsRejected()
        {
            byte[] rgba = Field(20, 20, false, 5);
            rgba[4 * 57 + 3] ^= 0x40;
            Assert.Throws<ArgumentException>(() => Encode(rgba, 20, 20));
        }

        [Fact]
        public void OnlyRgba8IsDecoded()
        {
            byte[] file = Valid();
            Assert.Throws<NotSupportedException>(() => ImageCodec.Decode(file, new byte[300 * 40 * 4], new DecodeOptions(PixelLayout.BGRA8)));
        }

        [Fact]
        public void EveryTruncationIsRejected()
        {
            byte[] file = Valid();
            byte[] pixels = new byte[300 * 40 * 4];
            for (int length = 0; length < file.Length; length++)
            {
                byte[] cut = file.AsSpan(0, length).ToArray();
                if (!ImageCodec.TryIdentify(cut, out _)) continue;
                Assert.Throws<ImageFormatException>(() => ImageCodec.Decode(cut, pixels, new DecodeOptions(PixelLayout.RGBA8)));
            }
        }

        [Fact]
        public void CorruptStreamsThrowOrDecode()
        {
            byte[] file = Valid();
            byte[] pixels = new byte[300 * 40 * 4];
            uint s = 12345;
            for (int round = 0; round < 500; round++)
            {
                byte[] bad = (byte[])file.Clone();
                for (int k = 0; k < 4; k++)
                {
                    s = s * 1664525u + 1013904223u;
                    int at = 12 + (int)(s % (uint)(bad.Length - 12));
                    bad[at] ^= (byte)(1 << (int)(s >> 29));
                }
                try
                {
                    ImageCodec.Decode(bad, pixels, new DecodeOptions(PixelLayout.RGBA8));
                }
                catch (ImageFormatException)
                {
                }
            }
        }
    }
}
