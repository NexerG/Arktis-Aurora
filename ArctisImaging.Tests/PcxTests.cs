using System;
using System.Buffers.Binary;
using System.IO;
using Xunit;

namespace Arctis.Imaging.Tests
{
    // Fixtures are PCX files written by Pillow 13 with Pillow's decode beside them; EGA planar files are built here.
    public class PcxTests
    {
        private static byte[] Fixture(string file) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));

        private static Image Load(byte[] file) => Image.Load(file, PixelLayout.RGBA8, default, ImageFormat.Pcx);

        [Theory]
        [InlineData("PcxRgb")]
        [InlineData("PcxGrey")]
        [InlineData("PcxPalette")]
        [InlineData("PcxBits")]
        public void MatchesPillow(string name) => Assert.Equal(Image.Load(Fixture(name + ".png")).Pixels, Load(Fixture(name + ".pcx")).Pixels);

        [Fact]
        public void IsRecognisedOnlyWithTheHint()
        {
            byte[] file = Fixture("PcxRgb.pcx");
            Assert.False(ImageCodec.TryIdentify(file, out _));
            Assert.True(ImageCodec.TryIdentify(file, out ImageHeader header, ImageFormat.Pcx));
            Assert.Equal(ImageFormat.Pcx, header.Format);
        }

        [Fact]
        public void FourPlaneEgaUsesTheHeaderPalette()
        {
            // 2x1, one byte per plane line; pixel 0 is index 0b0101, pixel 1 index 0b1010; plain bytes, no RLE
            byte[] file = new byte[128 + 4];
            file[0] = 0x0A;
            file[1] = 2;
            file[2] = 0;
            file[3] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8), 1);
            file[16 + 5 * 3] = 50;
            file[16 + 5 * 3 + 1] = 60;
            file[16 + 5 * 3 + 2] = 70;
            file[16 + 10 * 3 + 2] = 200;
            file[65] = 4;
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(66), 1);
            file[128] = 0x80;
            file[129] = 0x40;
            file[130] = 0x80;
            file[131] = 0x40;
            Assert.Equal(new byte[] { 50, 60, 70, 255, 0, 0, 200, 255 }, Load(file).Pixels);
        }

        [Fact]
        public void TruncatedScanlinesThrow()
        {
            byte[] file = Fixture("PcxRgb.pcx");
            Assert.Throws<ImageFormatException>(() => Load(file.AsSpan(0, 200).ToArray()));
        }
    }
}
