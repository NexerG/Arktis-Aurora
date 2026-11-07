using System;
using System.Buffers.Binary;
using System.IO;
using Xunit;

namespace Arctis.Imaging.Tests
{
    // Fixtures are libwebp lossless encodes (through Pillow) of the patterns below, recomputed here to compare.
    public class WebpTests
    {
        private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".webp"));

        private static uint Hash(int x, int y)
        {
            uint v = (uint)x * 73856093u ^ (uint)y * 19349663u;
            v ^= v >> 13;
            v *= 0x5BD1E995u;
            v ^= v >> 15;
            return v;
        }

        private static (byte r, byte g, byte b, byte a) Photo(int x, int y)
        {
            uint n = Hash(x, y);
            return ((byte)(x * 4 + (n & 15)), (byte)(y * 5 + x + (n >> 4 & 7)), (byte)(x * y / 8 + (n >> 8 & 3)), 255);
        }

        private static (byte r, byte g, byte b, byte a) Alpha(int x, int y)
        {
            (byte r, byte g, byte b, byte _) = Photo(x, y);
            return (r, g, b, (byte)(x * 6 + y * 2));
        }

        private static (byte r, byte g, byte b, byte a) Palette(int count, int x, int y)
        {
            int i = (int)((Hash(x / 3, y / 2) >> 3) % (uint)count);
            return ((byte)(i * 37), (byte)(i * 91 + 17), (byte)(i * 53 + 101), (byte)(i % 5 != 0 ? 255 : 128));
        }

        private static (byte r, byte g, byte b, byte a) Tiles(int x, int y)
        {
            uint t = Hash(x % 13, y % 7);
            return ((byte)t, (byte)(t >> 8), (byte)(t >> 16), 255);
        }

        private static (byte r, byte g, byte b, byte a) Expected(string name, int x, int y) => name switch
        {
            "Photo" or "PhotoFast" => Photo(x, y),
            "Alpha" => Alpha(x, y),
            "Palette2" => Palette(2, x, y),
            "Palette4" => Palette(4, x, y),
            "Palette16" => Palette(16, x, y),
            "Palette200" => Palette(200, x, y),
            _ => Tiles(x, y)
        };

        [Theory]
        [InlineData("Photo", 64, 48)]
        [InlineData("PhotoFast", 64, 48)]
        [InlineData("Alpha", 40, 40)]
        [InlineData("Palette2", 61, 37)]
        [InlineData("Palette4", 61, 37)]
        [InlineData("Palette16", 61, 37)]
        [InlineData("Palette200", 61, 37)]
        [InlineData("Tiles", 128, 64)]
        public void LosslessMatchesTheSource(string name, int width, int height)
        {
            byte[] file = Fixture(name);
            Assert.True(ImageCodec.TryIdentify(file, out ImageHeader header));
            Assert.Equal(ImageFormat.Webp, header.Format);
            Assert.Equal(width, header.Width);
            Assert.Equal(height, header.Height);

            Image image = Image.Load(file);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    (byte r, byte g, byte b, byte a) = Expected(name, x, y);
                    int i = (y * width + x) * 4;
                    Assert.True(image.Pixels[i] == r && image.Pixels[i + 1] == g && image.Pixels[i + 2] == b && image.Pixels[i + 3] == a,
                        $"{name} pixel {x},{y}: {image.Pixels[i]} {image.Pixels[i + 1]} {image.Pixels[i + 2]} {image.Pixels[i + 3]}, want {r} {g} {b} {a}");
                }
        }

        [Fact]
        public void DecodesToBgra()
        {
            byte[] file = Fixture("Alpha");
            Image rgba = Image.Load(file);
            Image bgra = Image.Load(file, PixelLayout.BGRA8);
            for (int i = 0; i < rgba.Pixels.Length; i += 4)
            {
                Assert.Equal(rgba.Pixels[i], bgra.Pixels[i + 2]);
                Assert.Equal(rgba.Pixels[i + 2], bgra.Pixels[i]);
                Assert.Equal(rgba.Pixels[i + 3], bgra.Pixels[i + 3]);
            }
        }

        [Fact]
        public void TruncatedThrows()
        {
            byte[] file = Fixture("Photo");
            byte[] cut = file[..(file.Length / 2)];
            BinaryPrimitives.WriteUInt32LittleEndian(cut.AsSpan(4), (uint)cut.Length - 8);
            BinaryPrimitives.WriteUInt32LittleEndian(cut.AsSpan(16), (uint)cut.Length - 20);
            Assert.Throws<ImageFormatException>(() => Image.Load(cut));
        }

        // A RIFF file holding one chunk.
        private static byte[] Riff(string fourcc, byte[] payload)
        {
            byte[] file = new byte[20 + payload.Length + (payload.Length & 1)];
            "RIFF"u8.CopyTo(file);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)file.Length - 8);
            "WEBP"u8.CopyTo(file.AsSpan(8));
            for (int i = 0; i < 4; i++) file[12 + i] = (byte)fourcc[i];
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), (uint)payload.Length);
            payload.CopyTo(file, 20);
            return file;
        }

        // Lossy fixtures are libwebp 1.6.0 encodes; each .png beside one is dwebp's decode of it, and for the animation
        // Pillow's first frame.
        [Theory]
        [InlineData("LossyQ75", 61, 37, false)]
        [InlineData("LossyQ5", 61, 37, false)]
        [InlineData("LossyQ100", 61, 37, false)]
        [InlineData("LossyM0", 61, 37, false)]
        [InlineData("LossyM6", 61, 37, false)]
        [InlineData("LossyOneSegment", 61, 37, false)]
        [InlineData("LossySimpleFilter", 61, 37, false)]
        [InlineData("LossyNoFilter", 61, 37, false)]
        [InlineData("LossySharp", 61, 37, false)]
        [InlineData("LossyLarge", 333, 211, false)]
        [InlineData("LossyFlat", 48, 32, false)]
        [InlineData("LossyTiny", 3, 3, false)]
        [InlineData("LossySingle", 1, 1, false)]
        [InlineData("LossyAlpha", 40, 40, true)]
        [InlineData("LossyAlphaRaw", 40, 40, true)]
        [InlineData("LossyAlphaHorizontal", 40, 40, true)]
        [InlineData("LossyAlphaVertical", 40, 40, true)]
        [InlineData("LossyAlphaGradient", 40, 40, true)]
        [InlineData("LossyAlphaQ50", 40, 40, true)]
        [InlineData("Animated", 64, 48, true)]
        public void LossyMatchesLibwebp(string name, int width, int height, bool alpha)
        {
            byte[] file = Fixture(name);
            Assert.True(ImageCodec.TryIdentify(file, out ImageHeader header));
            Assert.Equal(ImageFormat.Webp, header.Format);
            Assert.Equal(width, header.Width);
            Assert.Equal(height, header.Height);
            Assert.Equal(alpha, header.HasAlpha);

            Image image = Image.Load(file);
            Image expected = Image.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".png")));
            for (int i = 0; i < image.Pixels.Length; i++)
                if (image.Pixels[i] != expected.Pixels[i])
                    Assert.Fail($"{name} pixel {i / 4 % width},{i / 4 / width} channel {i & 3}: {image.Pixels[i]}, want {expected.Pixels[i]}");
        }

        [Theory]
        [InlineData("LossyAlpha")]
        [InlineData("Animated")]
        public void LossyDecodesToBgra(string name)
        {
            byte[] file = Fixture(name);
            Image rgba = Image.Load(file);
            Image bgra = Image.Load(file, PixelLayout.BGRA8);
            for (int i = 0; i < rgba.Pixels.Length; i += 4)
            {
                Assert.Equal(rgba.Pixels[i], bgra.Pixels[i + 2]);
                Assert.Equal(rgba.Pixels[i + 1], bgra.Pixels[i + 1]);
                Assert.Equal(rgba.Pixels[i + 2], bgra.Pixels[i]);
                Assert.Equal(rgba.Pixels[i + 3], bgra.Pixels[i + 3]);
            }
        }

        [Theory]
        [InlineData(0.3)]
        [InlineData(0.7)]
        public void TruncatedLossyThrows(double keep)
        {
            byte[] file = Fixture("LossyLarge");
            int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(16));
            byte[] cut = Riff("VP8 ", file[20..(20 + (int)(length * keep))]);
            Assert.Throws<ImageFormatException>(() => Image.Load(cut));
        }

        [Fact]
        public void LossyWithoutStartCodeThrows()
        {
            byte[] file = Fixture("LossyQ75");
            file[23] ^= 0xFF;
            Assert.Throws<ImageFormatException>(() => Image.Load(file));
        }
    }
}
