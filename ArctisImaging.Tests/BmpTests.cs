using System;
using System.Buffers.Binary;
using Xunit;

namespace Arctis.Imaging.Tests
{
    public class BmpTests
    {
        // A DIB with a BITMAPINFOHEADER (or larger), optional masks/palette, then rows padded to 4 bytes.
        private static byte[] Dib(int width, int height, int bitCount, uint compression, byte[] extra, Func<int, int, byte[]> pixel, int headerSize = 40, uint[]? headerMasks = null)
        {
            int stride = (width * bitCount + 31) / 32 * 4;
            byte[] dib = new byte[headerSize + extra.Length + stride * Math.Abs(height)];
            BinaryPrimitives.WriteInt32LittleEndian(dib, headerSize);
            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), width);
            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), height);
            BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), (ushort)bitCount);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16), compression);
            if (headerMasks != null)
                for (int i = 0; i < headerMasks.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(40 + i * 4), headerMasks[i]);
            extra.CopyTo(dib, headerSize);
            int rows = Math.Abs(height);
            for (int r = 0; r < rows; r++)
            {
                int y = height < 0 ? r : rows - 1 - r;
                int at = headerSize + extra.Length + r * stride;
                for (int x = 0; x < width; x++)
                {
                    byte[] p = pixel(x, y);
                    if (bitCount < 8)
                    {
                        int bit = x * bitCount;
                        dib[at + (bit >> 3)] |= (byte)(p[0] << (8 - bitCount - (bit & 7)));
                    }
                    else p.CopyTo(dib, at + x * (bitCount / 8));
                }
            }
            return dib;
        }

        private static byte[] WithFileHeader(byte[] dib, int pixelOffsetInDib)
        {
            byte[] file = new byte[14 + dib.Length];
            file[0] = (byte)'B';
            file[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), 14 + pixelOffsetInDib);
            dib.CopyTo(file, 14);
            return file;
        }

        [Fact]
        public void Bottom24BitFileDecodes()
        {
            byte[] dib = Dib(3, 2, 24, 0, Array.Empty<byte>(), (x, y) => new byte[] { (byte)(x * 10), (byte)(y * 20), 200 });
            Image image = Image.Load(WithFileHeader(dib, 40));
            Assert.Equal(3, image.Width);
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 3; x++)
                {
                    int i = (y * 3 + x) * 4;
                    Assert.Equal(200, image.Pixels[i]);
                    Assert.Equal(y * 20, image.Pixels[i + 1]);
                    Assert.Equal(x * 10, image.Pixels[i + 2]);
                    Assert.Equal(255, image.Pixels[i + 3]);
                }
        }

        [Fact]
        public void ZeroAlpha32BitIsOpaque()
        {
            byte[] dib = Dib(2, 2, 32, 0, Array.Empty<byte>(), (x, y) => new byte[] { 1, 2, 3, 0 });
            Image image = Image.LoadDib(dib);
            for (int i = 0; i < 4; i++) Assert.Equal(255, image.Pixels[i * 4 + 3]);
            Assert.Equal(3, image.Pixels[0]);
        }

        [Fact]
        public void V5TopDownKeepsAlpha()
        {
            uint[] masks = { 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000 };
            byte[] dib = Dib(2, -2, 32, 3, Array.Empty<byte>(), (x, y) => new byte[] { 10, 20, 30, (byte)(x * 100 + y) }, 124, masks);
            Image image = Image.LoadDib(dib);
            Assert.Equal(new byte[] { 30, 20, 10, 0, 30, 20, 10, 100, 30, 20, 10, 1, 30, 20, 10, 101 }, image.Pixels);
        }

        [Fact]
        public void BitFields565ScaleToEightBits()
        {
            byte[] masks = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(masks, 0xF800);
            BinaryPrimitives.WriteUInt32LittleEndian(masks.AsSpan(4), 0x07E0);
            BinaryPrimitives.WriteUInt32LittleEndian(masks.AsSpan(8), 0x001F);
            byte[] dib = Dib(2, 1, 16, 3, masks, (x, y) => x == 0 ? new byte[] { 0xFF, 0xFF } : new byte[] { 0x1F, 0x00 });
            Image image = Image.LoadDib(dib);
            Assert.Equal(new byte[] { 255, 255, 255, 255, 0, 0, 255, 255 }, image.Pixels);
        }

        [Fact]
        public void EightBitPaletteDecodes()
        {
            byte[] palette = new byte[256 * 4];
            palette[4] = 50;
            palette[5] = 60;
            palette[6] = 70;
            byte[] dib = Dib(3, 1, 8, 0, palette, (x, y) => new byte[] { (byte)(x == 1 ? 1 : 0) });
            Image image = Image.LoadDib(dib);
            Assert.Equal(new byte[] { 0, 0, 0, 255, 70, 60, 50, 255, 0, 0, 0, 255 }, image.Pixels);
        }

        [Fact]
        public void OneBitPaletteDecodes()
        {
            byte[] palette = { 0, 0, 0, 0, 255, 255, 255, 0 };
            byte[] dib = Dib(9, 1, 1, 0, palette, (x, y) => new byte[] { (byte)(x & 1) });
            Image image = Image.LoadDib(dib);
            for (int x = 0; x < 9; x++) Assert.Equal((x & 1) == 1 ? 255 : 0, image.Pixels[x * 4]);
        }

        [Fact]
        public void TruncatedBitmapIsRejected()
        {
            byte[] dib = Dib(8, 8, 24, 0, Array.Empty<byte>(), (x, y) => new byte[3]);
            Assert.Throws<ImageFormatException>(() => Image.LoadDib(dib.AsSpan(0, dib.Length - 10)));
        }

        [Fact]
        public void RunLengthIsNotSupported()
        {
            byte[] dib = Dib(2, 2, 8, 1, new byte[1024], (x, y) => new byte[1]);
            Assert.Throws<NotSupportedException>(() => Image.LoadDib(dib));
        }
    }
}
