using System;
using System.IO;
using Xunit;

namespace Arctis.Imaging.Tests
{
    // Fixtures are CharLS encodes through imagecodecs 2026.10.10: RGB is sample-interleaved, RGBA line-interleaved and
    // LsRgbIlv0 three single-component CharLS scans spliced into one frame. Each .png is CharLS's decode, above 8 bits
    // reduced to the high byte and below 8 scaled to 0-255.
    public class JpegLsTests
    {
        private static byte[] Fixture(string file) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));

        [Theory]
        [InlineData("LsGrey8")]
        [InlineData("LsRgb8")]
        [InlineData("LsRgba8")]
        [InlineData("LsRgbIlv0")]
        [InlineData("LsNear3Rgb")]
        [InlineData("LsNear2Grey")]
        [InlineData("LsGrey12")]
        [InlineData("LsGrey16")]
        [InlineData("LsGrey5")]
        [InlineData("LsFlat")]
        [InlineData("LsFlatGrey")]
        [InlineData("LsTiny")]
        [InlineData("LsNarrow")]
        public void MatchesCharLS(string name)
        {
            byte[] file = Fixture(name + ".jls");
            Assert.True(ImageCodec.TryIdentify(file, out ImageHeader header));
            Assert.Equal(ImageFormat.JpegLs, header.Format);
            Assert.Equal(Image.Load(Fixture(name + ".png")).Pixels, Image.Load(file).Pixels);
        }

        [Fact]
        public void MappingTableIsNotSupported()
        {
            byte[] file = Fixture("LsGrey8.jls");
            int sos = Find(file, 0xDA);
            file[sos + 6] = 1;
            Assert.Throws<NotSupportedException>(() => Image.Load(file));
        }

        [Fact]
        public void TruncatedScanThrows()
        {
            byte[] file = Fixture("LsGrey8.jls");
            Assert.Throws<ImageFormatException>(() => Image.Load(file.AsSpan(0, file.Length / 2).ToArray()));
        }

        private static int Find(byte[] file, byte marker)
        {
            for (int i = 0; i + 1 < file.Length; i++)
                if (file[i] == 0xFF && file[i + 1] == marker) return i;
            return -1;
        }
    }
}
