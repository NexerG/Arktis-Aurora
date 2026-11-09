using System;
using System.IO;
using Xunit;

namespace Arctis.Imaging.Tests
{
    // Fixtures are libjpeg-turbo 3.2 lossless encodes through imagecodecs of the Lj*.png sources beside them.
    public class JpegLosslessEncodeTests
    {
        private static byte[] Fixture(string file) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));

        private static byte[] Encode(Image image, in JpegLosslessSettings settings)
        {
            MemoryStream output = new MemoryStream();
            image.SaveLosslessJpeg(output, settings);
            return output.ToArray();
        }

        private static void AssertSameBytes(byte[] expected, byte[] actual)
        {
            int at = 0;
            int shorter = Math.Min(expected.Length, actual.Length);
            while (at < shorter && expected[at] == actual[at]) at++;
            Assert.True(at == expected.Length && at == actual.Length, $"lengths {expected.Length} / {actual.Length}, first difference at byte {at}");
        }

        [Theory]
        [InlineData("LjGreyP1", 1)]
        [InlineData("LjGreyP2", 2)]
        [InlineData("LjGreyP3", 3)]
        [InlineData("LjGreyP4", 4)]
        [InlineData("LjGreyP5", 5)]
        [InlineData("LjGreyP6", 6)]
        [InlineData("LjGreyP7", 7)]
        public void GreyMatchesLibjpegTurbo(string name, int predictor)
        {
            byte[] expected = Fixture(name + ".jpg");
            AssertSameBytes(expected, Encode(Image.Load(Fixture(name + ".png"), PixelLayout.R8), new JpegLosslessSettings(predictor)));
            AssertSameBytes(expected, Encode(Image.Load(Fixture(name + ".png"), PixelLayout.RG8), new JpegLosslessSettings(predictor)));
        }

        [Theory]
        [InlineData("LjRgb", 1)]
        [InlineData("LjRgbP6", 6)]
        public void RgbMatchesLibjpegTurbo(string name, int predictor) =>
            AssertSameBytes(Fixture(name + ".jpg"), Encode(Image.Load(Fixture(name + ".png")), new JpegLosslessSettings(predictor)));

        [Fact]
        public void DefaultIsPredictor1() => AssertSameBytes(Fixture("LjRgb.jpg"), Encode(Image.Load(Fixture("LjRgb.png")), default));

        [Fact]
        public void BgraWritesTheSameFile()
        {
            Image rgba = Image.Load(Fixture("EncodeLarge.png"));
            Image bgra = Image.Load(Fixture("EncodeLarge.png"), PixelLayout.BGRA8);
            AssertSameBytes(Encode(rgba, default), Encode(bgra, default));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        public void RoundTripsThroughTheDecoder(int predictor)
        {
            Image source = Image.Load(Fixture("EncodeLarge.png"));
            Image decoded = Image.Load(Encode(source, new JpegLosslessSettings(predictor)));
            Assert.Equal(source.Width, decoded.Width);
            Assert.Equal(source.Height, decoded.Height);
            for (int i = 0; i < source.Pixels.Length; i++)
                if ((i & 3) != 3) Assert.Equal(source.Pixels[i], decoded.Pixels[i]);

            Image grey = Image.Load(Fixture("EncodeLarge.png"), PixelLayout.R8);
            Assert.Equal(grey.Pixels, Image.Load(Encode(grey, new JpegLosslessSettings(predictor)), PixelLayout.R8).Pixels);
        }

        [Fact]
        public void PitchedRowsWriteTheSameFile()
        {
            Image image = Image.Load(Fixture("LjRgb.png"));
            int pitch = image.RowPitch + 12;
            byte[] pitched = new byte[pitch * image.Height];
            for (int y = 0; y < image.Height; y++) image.Row(y).CopyTo(pitched.AsSpan(y * pitch));
            MemoryStream output = new MemoryStream();
            ImageCodec.EncodeLosslessJpeg(pitched, image.Width, image.Height, pitch, PixelLayout.RGBA8, output);
            AssertSameBytes(Fixture("LjRgb.jpg"), output.ToArray());
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(8)]
        public void PredictorOutsideRangeThrows(int predictor) =>
            Assert.Throws<ArgumentException>(() => Encode(new Image(8, 8), new JpegLosslessSettings(predictor)));

        [Fact]
        public void ShortPixelSpanThrows() =>
            Assert.Throws<ArgumentException>(() => ImageCodec.EncodeLosslessJpeg(new byte[10], 8, 8, 0, PixelLayout.RGBA8, new MemoryStream()));
    }
}
