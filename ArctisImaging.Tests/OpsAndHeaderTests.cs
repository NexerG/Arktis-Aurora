using System;
using Xunit;

namespace Arctis.Imaging.Tests
{
    public class OpsAndHeaderTests
    {
        [Fact]
        public void HalveKeepsAUniformColour()
        {
            byte[] src = new byte[4 * 4 * 4];
            for (int i = 0; i < 16; i++)
            {
                src[i * 4] = 37;
                src[i * 4 + 1] = 140;
                src[i * 4 + 2] = 251;
                src[i * 4 + 3] = 255;
            }
            byte[] dst = new byte[2 * 2 * 4];
            ImageOps.HalveLinear(src, 4, 4, 16, dst, 8);
            for (int i = 0; i < 4; i++)
                Assert.Equal(new byte[] { 37, 140, 251, 255 }, dst.AsSpan(i * 4, 4).ToArray());
        }

        [Fact]
        public void HalveAveragesInLinearLight()
        {
            byte[] src = { 0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 0, 255, 255, 255, 255, 255 };
            byte[] dst = new byte[4];
            ImageOps.HalveLinear(src, 2, 2, 8, dst, 4);
            Assert.Equal(188, dst[0]);
            Assert.Equal(255, dst[3]);
        }

        [Fact]
        public void HalveIgnoresTransparentColour()
        {
            byte[] src = { 255, 0, 0, 255, 0, 255, 0, 0, 255, 0, 0, 255, 0, 255, 0, 0 };
            byte[] dst = new byte[4];
            ImageOps.HalveLinear(src, 2, 2, 8, dst, 4);
            Assert.Equal(new byte[] { 255, 0, 0, 128 }, dst);
        }

        [Fact]
        public void HalveFoldsAnOddEdgeIntoTheLastPixel()
        {
            byte[] src = new byte[3 * 3 * 4];
            for (int i = 0; i < 9; i++) src[i * 4 + 3] = (byte)(i == 8 ? 255 : 0);
            byte[] dst = new byte[4];
            ImageOps.HalveLinear(src, 3, 3, 12, dst, 4);
            Assert.Equal((byte)Math.Round(255 / 9.0), dst[3]);
        }

        [Fact]
        public void ResizeAreaAveragesBlocks()
        {
            byte[] src = new byte[4 * 2 * 4];
            for (int i = 0; i < 8; i++)
            {
                src[i * 4] = (byte)((i % 4) < 2 ? 0 : 200);
                src[i * 4 + 3] = 255;
            }
            byte[] dst = new byte[2 * 1 * 4];
            ImageOps.ResizeArea(src, 4, 2, 16, dst, 2, 1, 8);
            Assert.Equal(0, dst[0]);
            Assert.Equal(200, dst[4]);
            Assert.Equal(255, dst[3]);
        }

        [Fact]
        public void SwizzleSwapsRedAndBlue()
        {
            byte[] px = new byte[4 * 7];
            for (int i = 0; i < px.Length; i++) px[i] = (byte)i;
            ImageOps.SwizzleBgraToRgba(px);
            for (int p = 0; p < 7; p++)
                Assert.Equal(new byte[] { (byte)(p * 4 + 2), (byte)(p * 4 + 1), (byte)(p * 4), (byte)(p * 4 + 3) }, px.AsSpan(p * 4, 4).ToArray());
        }

        [Fact]
        public void CropCopiesTheRectangle()
        {
            Image image = new Image(4, 3, PixelLayout.R8);
            for (int i = 0; i < 12; i++) image.Pixels[i] = (byte)i;
            Image crop = image.Crop(1, 1, 2, 2);
            Assert.Equal(new byte[] { 5, 6, 9, 10 }, crop.Pixels);
        }

        [Fact]
        public void JpegHeaderIsIdentified()
        {
            byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC2, 0x00, 0x11, 0x08, 0x01, 0x2C, 0x00, 0xC8, 0x03, 0, 0, 0 };
            Assert.True(ImageCodec.TryIdentify(jpeg, out ImageHeader header));
            Assert.Equal(ImageFormat.Jpeg, header.Format);
            Assert.Equal(200, header.Width);
            Assert.Equal(300, header.Height);
            Assert.Equal(24, header.BitsPerPixel);
            Assert.Throws<ImageFormatException>(() => ImageCodec.Decode(jpeg, new byte[200 * 300 * 4], default));
        }

        [Fact]
        public void GifHeaderIsIdentified()
        {
            byte[] gif = { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 10, 0, 20, 0, 0, 0, 0 };
            Assert.True(ImageCodec.TryIdentify(gif, out ImageHeader header));
            Assert.Equal(ImageFormat.Gif, header.Format);
            Assert.Equal(10, header.Width);
            Assert.Equal(20, header.Height);
        }

        [Fact]
        public void GarbageIsNotIdentified()
        {
            Assert.False(ImageCodec.TryIdentify(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34 }, out _));
            Assert.Throws<ImageFormatException>(() => Image.Load(new byte[3]));
        }

        [Fact]
        public void SmallDestinationIsRejected()
        {
            byte[] png = PngBuilder.Build(4, 4, 6, 8, new byte[64], 0);
            Assert.Throws<ArgumentException>(() => ImageCodec.Decode(png, new byte[63], default));
        }
    }
}
