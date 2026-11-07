using System;
using System.IO;
using System.Text;
using Xunit;

namespace Arctis.Imaging.Tests
{
    // XbmBits.xbm is written by Pillow 13; its .png is Pillow's decode inverted, since Pillow reads a set bit as white.
    public class XbmTests
    {
        private static byte[] Fixture(string file) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));

        private static Image Load(string text) => Image.Load(Encoding.ASCII.GetBytes(text), PixelLayout.RGBA8, default, ImageFormat.Xbm);

        [Fact]
        public void MatchesPillowInverted() =>
            Assert.Equal(Image.Load(Fixture("XbmBits.png")).Pixels, Image.Load(Fixture("XbmBits.xbm"), PixelLayout.RGBA8, default, ImageFormat.Xbm).Pixels);

        [Fact]
        public void X11BitsAreLeastSignificantFirstAndSetIsBlack()
        {
            Image image = Load("#define a_width 10\n#define a_height 1\n#define a_x_hot 0\nstatic unsigned char a_bits[] = {\n   0x05, 0x02 };\n");
            Assert.Equal(10, image.Width);
            byte[] expected = new byte[40];
            for (int x = 0; x < 10; x++)
            {
                byte grey = x == 0 || x == 2 || x == 9 ? (byte)0 : (byte)255;
                expected[x * 4] = expected[x * 4 + 1] = expected[x * 4 + 2] = grey;
                expected[x * 4 + 3] = 255;
            }
            Assert.Equal(expected, image.Pixels);
        }

        [Fact]
        public void X10ShortsPadRowsTo16Bits()
        {
            Image image = Load("#define b_width 3\n#define b_height 2\nstatic short b_bits[] = { 0x0004, 0x0001 };\n");
            byte[] red = new byte[6];
            for (int i = 0; i < 6; i++) red[i] = image.Pixels[i * 4];
            Assert.Equal(new byte[] { 255, 255, 0, 0, 255, 255 }, red);
        }

        [Fact]
        public void IsRecognisedOnlyWithTheHint()
        {
            byte[] file = Fixture("XbmBits.xbm");
            Assert.False(ImageCodec.TryIdentify(file, out _));
            Assert.True(ImageCodec.TryIdentify(file, out ImageHeader header, ImageFormat.Xbm));
            Assert.Equal(ImageFormat.Xbm, header.Format);
        }

        [Fact]
        public void ShortArrayThrows() =>
            Assert.Throws<ImageFormatException>(() => Load("#define c_width 8\n#define c_height 2\nstatic char c_bits[] = { 0xFF };"));
    }
}
