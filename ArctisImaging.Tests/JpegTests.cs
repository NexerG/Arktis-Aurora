using System;
using System.IO;
using Xunit;

namespace Arctis.Imaging.Tests
{
    // Fixtures are libjpeg-turbo 3.2 encodes through Pillow 13; each .png beside a .jpg is Pillow's decode of it.
    public class JpegTests
    {
        private static byte[] Fixture(string file) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));

        private static int Find(byte[] file, byte marker)
        {
            for (int i = 2; i + 1 < file.Length; i++)
                if (file[i] == 0xFF && file[i + 1] == marker) return i;
            return -1;
        }

        private static void AssertNear(Image expected, Image actual, int rows)
        {
            Assert.Equal(expected.Width, actual.Width);
            Assert.Equal(expected.Height, actual.Height);
            int worst = 0, at = 0;
            for (int i = 0; i < expected.Width * rows * 4; i++)
            {
                int d = Math.Abs(expected.Pixels[i] - actual.Pixels[i]);
                if (d <= worst) continue;
                worst = d;
                at = i;
            }
            Assert.True(worst <= 1, $"off by {worst} at pixel ({at / 4 % expected.Width}, {at / 4 / expected.Width}) channel {at % 4}");
        }

        [Theory]
        [InlineData("Baseline444", 61, 37)]
        [InlineData("Baseline422", 61, 37)]
        [InlineData("Baseline420", 61, 37)]
        [InlineData("Quality100", 40, 24)]
        [InlineData("Quality5", 40, 24)]
        [InlineData("Optimized", 64, 48)]
        [InlineData("Restart420", 61, 37)]
        [InlineData("Progressive444", 61, 37)]
        [InlineData("Progressive420", 61, 37)]
        [InlineData("ProgressiveRestart", 61, 37)]
        [InlineData("Tiny420", 3, 3)]
        [InlineData("Single", 1, 1)]
        [InlineData("Large420", 333, 211)]
        [InlineData("LargeProgressive", 333, 211)]
        [InlineData("Grey", 45, 29)]
        [InlineData("GreyProgressive", 45, 29)]
        public void MatchesLibjpegTurbo(string name, int width, int height)
        {
            byte[] file = Fixture(name + ".jpg");
            Assert.True(ImageCodec.TryIdentify(file, out ImageHeader header));
            Assert.Equal(ImageFormat.Jpeg, header.Format);
            Assert.Equal(width, header.Width);
            Assert.Equal(height, header.Height);
            Image expected = Image.Load(Fixture(name + ".png"));
            AssertNear(expected, Image.Load(file), height);
        }

        [Fact]
        public void GreyIntoR8IsTheLuma()
        {
            Image grey = Image.Load(Fixture("Grey.jpg"), PixelLayout.R8);
            Image rgba = Image.Load(Fixture("Grey.jpg"));
            for (int i = 0; i < grey.Pixels.Length; i++) Assert.Equal(rgba.Pixels[i * 4], grey.Pixels[i]);
        }

        [Fact]
        public void TruncatedFileDecodesWhatItHolds()
        {
            byte[] file = Fixture("Large420.jpg");
            Image image = Image.Load(file.AsSpan(0, file.Length / 2));
            AssertNear(Image.Load(Fixture("Large420.png")), image, 16);
        }

        // Cmyk is Pillow's Adobe CMYK; Ycck (Adobe transform 2) and CmykPlain (no Adobe marker) are hand-built flat 8x8 files.
        // The 83x11 Wide files (Pillow CMYK, libjpeg-turbo YCCK) reach every SIMD tier and the scalar tail.
        [Theory]
        [InlineData("Cmyk")]
        [InlineData("Ycck")]
        [InlineData("CmykWide")]
        [InlineData("YcckWide")]
        public void FourComponentsMatchPillow(string name)
        {
            Image expected = Image.Load(Fixture(name + ".png"));
            AssertNear(expected, Image.Load(Fixture(name + ".jpg")), expected.Height);
        }

        // Pillow's Adobe RGB (transform 0), 83x11 like the four-component Wide files
        [Fact]
        public void RgbMatchesPillow()
        {
            Image expected = Image.Load(Fixture("RgbWide.png"));
            AssertNear(expected, Image.Load(Fixture("RgbWide.jpg")), expected.Height);
        }

        // stored CMYK 40, 200, 90, 30 read as plain inks, where Pillow would invert them
        [Fact]
        public void CmykWithoutAdobeMarkerIsNotInverted()
        {
            Image image = Image.Load(Fixture("CmykPlain.jpg"));
            for (int p = 0; p < image.Width * image.Height; p++)
                Assert.Equal(new byte[] { 190, 49, 146, 255 }, image.Pixels.AsSpan(p * 4, 4).ToArray());
        }

        [Fact]
        public void ArithmeticCodingIsNotSupported()
        {
            byte[] file = Fixture("Progressive420.jpg");
            file[Find(file, 0xC2) + 1] = 0xCA;
            Assert.Throws<NotSupportedException>(() => Image.Load(file));
        }

        [Fact]
        public void TwelveBitIsNotSupported()
        {
            byte[] file = Fixture("Baseline420.jpg");
            file[Find(file, 0xC0) + 4] = 12;
            Assert.Throws<NotSupportedException>(() => Image.Load(file));
        }
    }
}
