using System;
using System.Text;
using Xunit;

namespace Arctis.Imaging.Tests
{
    public class XpmTests
    {
        private static byte[] Xpm(string body) => Encoding.ASCII.GetBytes("/* XPM */\nstatic char *icon[] = {\n" + body + "};\n");

        [Fact]
        public void OneCharacterKeysWithHexAndNone()
        {
            byte[] file = Xpm("/* columns rows colors chars-per-pixel */\n\"3 2 3 1\",\n\"  c None\",\n\". c #FF8000\",\n\"X c #abc s accent\",\n/* pixels */\n\" .X\",\n\"X. \"\n");
            Assert.True(ImageCodec.TryIdentify(file, out ImageHeader header));
            Assert.Equal(ImageFormat.Xpm, header.Format);
            Assert.Equal(new byte[]
            {
                0, 0, 0, 0, 255, 128, 0, 255, 0xAA, 0xBB, 0xCC, 255,
                0xAA, 0xBB, 0xCC, 255, 255, 128, 0, 255, 0, 0, 0, 0
            }, Image.Load(file).Pixels);
        }

        [Fact]
        public void ColourKeyIsPreferredOverGreyAndMono()
        {
            byte[] file = Xpm("\"1 1 1 1\",\n\"a m #000000 g #808080 c #102030\",\n\"a\"\n");
            Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 255 }, Image.Load(file).Pixels);
        }

        [Fact]
        public void LongKeysAndWideHex()
        {
            byte[] file = Xpm("\"2 1 2 3\",\n\"ab1 c #123456789ABC\",\n\"ab2 c none\",\n\"ab2ab1\"\n");
            Assert.Equal(new byte[] { 0, 0, 0, 0, 0x12, 0x56, 0x9A, 255 }, Image.Load(file).Pixels);
        }

        [Fact]
        public void NamedColourIsNotSupported() =>
            Assert.Throws<NotSupportedException>(() => Image.Load(Xpm("\"1 1 1 1\",\n\"a c light grey\",\n\"a\"\n")));

        [Fact]
        public void ShortRowThrows() =>
            Assert.Throws<ImageFormatException>(() => Image.Load(Xpm("\"2 1 1 1\",\n\"a c #000000\",\n\"a\"\n")));
    }
}
