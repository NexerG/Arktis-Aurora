using System;
using System.IO;
using Xunit;

namespace Arctis.Imaging.Tests
{
    // Fixtures are libjpeg-turbo 3.2 lossless encodes through imagecodecs 2026.10.10; each .png is libjpeg-turbo's
    // decode, above 8 bits reduced to the high byte.
    public class JpegLosslessTests
    {
        private static byte[] Fixture(string file) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));

        [Theory]
        [InlineData("LjGreyP1")]
        [InlineData("LjGreyP2")]
        [InlineData("LjGreyP3")]
        [InlineData("LjGreyP4")]
        [InlineData("LjGreyP5")]
        [InlineData("LjGreyP6")]
        [InlineData("LjGreyP7")]
        [InlineData("LjRgb")]
        [InlineData("LjRgbP6")]
        [InlineData("LjGrey12")]
        [InlineData("LjGrey16")]
        public void MatchesLibjpegTurbo(string name)
        {
            byte[] file = Fixture(name + ".jpg");
            Assert.True(ImageCodec.TryIdentify(file, out ImageHeader header));
            Assert.Equal(ImageFormat.Jpeg, header.Format);
            Assert.Equal(Image.Load(Fixture(name + ".png")).Pixels, Image.Load(file).Pixels);
        }

        [Fact]
        public void TruncatedScanStillDecodes()
        {
            byte[] file = Fixture("LjGreyP1.jpg");
            Image image = Image.Load(file.AsSpan(0, file.Length / 2).ToArray());
            Assert.Equal(Image.Load(Fixture("LjGreyP1.png")).Pixels[0..16], image.Pixels[0..16]);
        }
    }
}
