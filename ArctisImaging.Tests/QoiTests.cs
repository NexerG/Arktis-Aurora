using System;
using System.IO;
using Xunit;

namespace Arctis.Imaging.Tests
{
    // Fixtures are QOI files written by Pillow 13 with Pillow's decode beside them.
    public class QoiTests
    {
        private static byte[] Fixture(string file) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));

        [Theory]
        [InlineData("QoiRgb")]
        [InlineData("QoiRgba")]
        [InlineData("QoiFlat")]
        public void MatchesPillow(string name)
        {
            byte[] data = Fixture(name + ".qoi");
            Assert.True(ImageCodec.TryIdentify(data, out ImageHeader header));
            Assert.Equal(ImageFormat.Qoi, header.Format);
            Assert.Equal(Image.Load(Fixture(name + ".png")).Pixels, Image.Load(data).Pixels);
        }

        [Fact]
        public void EveryChunkKindDecodes()
        {
            byte[] file =
            {
                (byte)'q', (byte)'o', (byte)'i', (byte)'f', 0, 0, 0, 6, 0, 0, 0, 1, 4, 0,
                0xFE, 10, 20, 30,           // RGB
                0x40 | 3 << 4 | 1 << 2 | 2, // DIFF +1 -1 0
                0x80 | 34, 0x88,            // LUMA, all three +2
                0xFF, 1, 2, 3, 4,           // RGBA
                0xC0 | 0,                   // RUN of 1
                (byte)((10 * 3 + 20 * 5 + 30 * 7 + 255 * 11) & 63), // INDEX of the first pixel
                0, 0, 0, 0, 0, 0, 0, 1
            };
            Image image = Image.Load(file);
            Assert.Equal(new byte[] { 10, 20, 30, 255, 11, 19, 30, 255, 13, 21, 32, 255, 1, 2, 3, 4, 1, 2, 3, 4, 10, 20, 30, 255 }, image.Pixels);
        }

        [Fact]
        public void TruncatedChunksThrow()
        {
            byte[] file = Fixture("QoiRgb.qoi");
            Assert.Throws<ImageFormatException>(() => Image.Load(file.AsSpan(0, 40).ToArray()));
        }

        private static byte[] Encode(Image image, in QoiSettings settings)
        {
            MemoryStream output = new MemoryStream();
            image.SaveQoi(output, settings);
            return output.ToArray();
        }

        // Fixtures written by Pillow 13 with colorspace="sRGB" from the PNG named first.
        [Theory]
        [InlineData("EncodeSource", "EncodeSource", 3)]
        [InlineData("QoiRgba", "EncodeRgba", 4)]
        public void EncodeMatchesPillow(string source, string name, int channels)
        {
            Assert.Equal(Fixture(name + ".qoi"), Encode(Image.Load(Fixture(source + ".png")), new QoiSettings(channels)));
        }

        [Theory]
        [InlineData(PixelLayout.RGBA8)]
        [InlineData(PixelLayout.BGRA8)]
        [InlineData(PixelLayout.R8)]
        [InlineData(PixelLayout.RG8)]
        public void EncodeRoundTripsEveryLayout(PixelLayout layout)
        {
            Image image = Image.Load(Fixture("QoiRgba.png"), layout);
            byte[] file = Encode(image, default);
            Assert.Equal(layout == PixelLayout.R8 ? 3 : 4, file[12]);
            Assert.Equal(image.Pixels, Image.Load(file, layout).Pixels);
        }

        [Fact]
        public void ThreeChannelsDropAlphaAndLinearOnlyChangesTheHeader()
        {
            Image image = Image.Load(Fixture("QoiRgba.png"));
            Image decoded = Image.Load(Encode(image, new QoiSettings(3)));
            for (int i = 0; i < decoded.Pixels.Length; i += 4)
            {
                Assert.Equal(image.Pixels[i..(i + 3)], decoded.Pixels[i..(i + 3)]);
                Assert.Equal(255, decoded.Pixels[i + 3]);
            }
            byte[] srgb = Encode(image, default), linear = Encode(image, new QoiSettings(0, QoiColorspace.Linear));
            Assert.Equal(1, linear[13]);
            linear[13] = 0;
            Assert.Equal(srgb, linear);
        }

        [Fact]
        public void LongRunsAndLargeOutputRoundTrip()
        {
            Image image = new Image(300, 300);
            Random random = new Random(7);
            random.NextBytes(image.Pixels.AsSpan(0, 150 * 300 * 4));
            Assert.Equal(image.Pixels, Image.Load(Encode(image, default)).Pixels);
        }

        [Fact]
        public void InvalidChannelsThrow()
        {
            Assert.Throws<ArgumentException>(() => Encode(new Image(1, 1), new QoiSettings(2)));
        }
    }
}
