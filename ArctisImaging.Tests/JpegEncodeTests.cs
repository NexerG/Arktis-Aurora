using System;
using System.IO;
using Xunit;

namespace Arctis.Imaging.Tests
{
    // Fixtures are libjpeg-turbo 3.2 encodes through Pillow 13 of the Encode*.png sources beside them.
    public class JpegEncodeTests
    {
        private static byte[] Fixture(string file) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));

        private static byte[] Encode(Image image, in JpegSettings settings)
        {
            MemoryStream output = new MemoryStream();
            image.SaveJpeg(output, settings);
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
        [InlineData("EncodeSource", "Encode420Q75", 75, ChromaSubsampling.S420, false)]
        [InlineData("EncodeSource", "Encode422Q75", 75, ChromaSubsampling.S422, false)]
        [InlineData("EncodeSource", "Encode444Q75", 75, ChromaSubsampling.S444, false)]
        [InlineData("EncodeSource", "Encode420Q90", 90, ChromaSubsampling.S420, false)]
        [InlineData("EncodeSource", "Encode420Q100", 100, ChromaSubsampling.S420, false)]
        [InlineData("EncodeSource", "Encode420Q5", 5, ChromaSubsampling.S420, false)]
        [InlineData("EncodeSource", "Encode420Optimized", 75, ChromaSubsampling.S420, true)]
        [InlineData("EncodeSource", "Encode444Optimized", 90, ChromaSubsampling.S444, true)]
        [InlineData("EncodeEven", "EncodeEven420", 75, ChromaSubsampling.S420, false)]
        [InlineData("EncodeEven", "EncodeEven422", 75, ChromaSubsampling.S422, false)]
        [InlineData("EncodeLarge", "EncodeLarge420", 75, ChromaSubsampling.S420, false)]
        [InlineData("EncodeLarge", "EncodeLargeOptimized", 85, ChromaSubsampling.S420, true)]
        [InlineData("EncodeTiny", "EncodeTiny420", 75, ChromaSubsampling.S420, false)]
        [InlineData("EncodeSingle", "EncodeSingle420", 75, ChromaSubsampling.S420, false)]
        public void MatchesLibjpegTurbo(string source, string name, int quality, ChromaSubsampling subsampling, bool optimize)
        {
            Image image = Image.Load(Fixture(source + ".png"));
            AssertSameBytes(Fixture(name + ".jpg"), Encode(image, new JpegSettings(quality, subsampling, optimize)));
        }

        [Theory]
        [InlineData("EncodeGrey", false)]
        [InlineData("EncodeGreyOptimized", true)]
        public void GreyMatchesLibjpegTurbo(string name, bool optimize)
        {
            Image grey = Image.Load(Fixture("EncodeGrey.png"), PixelLayout.R8);
            AssertSameBytes(Fixture(name + ".jpg"), Encode(grey, new JpegSettings(75, optimize: optimize)));
            Image withAlpha = Image.Load(Fixture("EncodeGrey.png"), PixelLayout.RG8);
            AssertSameBytes(Fixture(name + ".jpg"), Encode(withAlpha, new JpegSettings(75, optimize: optimize)));
        }

        [Fact]
        public void DefaultIsQuality75At420() => AssertSameBytes(Fixture("Encode420Q75.jpg"), Encode(Image.Load(Fixture("EncodeSource.png")), default));

        [Fact]
        public void BgraWritesTheSameFile()
        {
            Image rgba = Image.Load(Fixture("EncodeLarge.png"));
            Image bgra = Image.Load(Fixture("EncodeLarge.png"), PixelLayout.BGRA8);
            AssertSameBytes(Encode(rgba, default), Encode(bgra, default));
        }

        [Fact]
        public void RoundTripsThroughTheDecoder()
        {
            Image source = Image.Load(Fixture("EncodeLarge.png"));
            Image decoded = Image.Load(Encode(source, new JpegSettings(95, ChromaSubsampling.S444)));
            Assert.Equal(source.Width, decoded.Width);
            Assert.Equal(source.Height, decoded.Height);
            long error = 0;
            for (int i = 0; i < source.Pixels.Length; i++) error += Math.Abs(source.Pixels[i] - decoded.Pixels[i]);
            Assert.True(error < source.Pixels.Length * 4L, $"mean error {(double)error / source.Pixels.Length:F2}");
        }

        [Fact]
        public void PitchedRowsWriteTheSameFile()
        {
            Image image = Image.Load(Fixture("EncodeSource.png"));
            int pitch = image.RowPitch + 12;
            byte[] pitched = new byte[pitch * image.Height];
            for (int y = 0; y < image.Height; y++) image.Row(y).CopyTo(pitched.AsSpan(y * pitch));
            MemoryStream output = new MemoryStream();
            ImageCodec.EncodeJpeg(pitched, image.Width, image.Height, pitch, PixelLayout.RGBA8, output);
            AssertSameBytes(Fixture("Encode420Q75.jpg"), output.ToArray());
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(101)]
        public void QualityOutsideRangeThrows(int quality) =>
            Assert.Throws<ArgumentException>(() => Encode(new Image(8, 8), new JpegSettings(quality)));

        [Fact]
        public void ShortPixelSpanThrows() =>
            Assert.Throws<ArgumentException>(() => ImageCodec.EncodeJpeg(new byte[10], 8, 8, 0, PixelLayout.RGBA8, new MemoryStream()));
    }
}
