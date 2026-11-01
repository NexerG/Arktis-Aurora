using System;
using System.IO;
using Xunit;

namespace Arctis.Imaging.Tests
{
    public class PngTests
    {
        private static byte[] Pattern(int length, int seed)
        {
            byte[] data = new byte[length];
            uint s = (uint)seed * 2654435761u + 1;
            for (int i = 0; i < length; i++)
            {
                s = s * 1664525u + 1013904223u;
                data[i] = (byte)(s >> 24);
            }
            return data;
        }

        [Fact]
        public void Crc32MatchesTheCheckValue()
        {
            byte[] check = System.Text.Encoding.ASCII.GetBytes("123456789");
            Assert.Equal(0xCBF43926u, Crc32.Update(0, check));
            byte[] data = Pattern(1000, 7);
            Assert.Equal(PngBuilder.ReferenceCrc(data), Crc32.Update(0, data));
            Assert.Equal(PngBuilder.ReferenceCrc(data), Crc32.Update(Crc32.Update(0, data.AsSpan(0, 333)), data.AsSpan(333)));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void Rgba8DecodesUnderEveryFilter(byte filter)
        {
            byte[] pixels = Pattern(37 * 23 * 4, filter);
            byte[] png = PngBuilder.Build(37, 23, 6, 8, pixels, filter);
            Image image = Image.Load(png);
            Assert.Equal(37, image.Width);
            Assert.Equal(23, image.Height);
            Assert.Equal(pixels, image.Pixels);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        [InlineData(4)]
        public void RgbDecodesUnderFilters(byte filter)
        {
            byte[] rgb = Pattern(19 * 11 * 3, 3);
            Image image = Image.Load(PngBuilder.Build(19, 11, 2, 8, rgb, filter));
            for (int i = 0; i < 19 * 11; i++)
            {
                Assert.Equal(rgb[i * 3], image.Pixels[i * 4]);
                Assert.Equal(rgb[i * 3 + 1], image.Pixels[i * 4 + 1]);
                Assert.Equal(rgb[i * 3 + 2], image.Pixels[i * 4 + 2]);
                Assert.Equal(255, image.Pixels[i * 4 + 3]);
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        public void LowDepthGreyScalesToFullRange(byte depth)
        {
            int width = 13, height = 5;
            int rowBytes = (width * depth + 7) / 8;
            int max = (1 << depth) - 1;
            byte[] rows = new byte[rowBytes * height];
            int[] samples = new int[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int v = (x + y * 3) % (max + 1);
                    samples[y * width + x] = v;
                    int bit = x * depth;
                    rows[y * rowBytes + (bit >> 3)] |= (byte)(v << (8 - depth - (bit & 7)));
                }
            Image image = Image.Load(PngBuilder.Build(width, height, 0, depth, rows, 1));
            for (int i = 0; i < width * height; i++)
            {
                byte g = (byte)(samples[i] * 255 / max);
                Assert.Equal(g, image.Pixels[i * 4]);
                Assert.Equal(g, image.Pixels[i * 4 + 2]);
                Assert.Equal(255, image.Pixels[i * 4 + 3]);
            }
        }

        [Fact]
        public void SixteenBitRoundsToEightBit()
        {
            ushort[] values = { 0, 128, 255, 256, 32767, 32768, 65280, 65535 };
            byte[] rows = new byte[values.Length * 8];
            for (int i = 0; i < values.Length; i++)
                for (int c = 0; c < 4; c++)
                {
                    rows[i * 8 + c * 2] = (byte)(values[i] >> 8);
                    rows[i * 8 + c * 2 + 1] = (byte)values[i];
                }
            Image image = Image.Load(PngBuilder.Build(values.Length, 1, 6, 16, rows, 0));
            for (int i = 0; i < values.Length; i++)
                Assert.Equal((byte)Math.Round(values[i] / 257.0), image.Pixels[i * 4]);
        }

        [Fact]
        public void PaletteWithTransparencyExpands()
        {
            byte[] plte = { 255, 0, 0, 0, 255, 0, 0, 0, 255 };
            byte[] trns = { 0, 128 };
            byte[] rows = { 0, 1, 2, 2, 1, 0 };
            Image image = Image.Load(PngBuilder.Build(3, 2, 3, 8, rows, 0, plte, trns));
            Assert.Equal(new byte[] { 255, 0, 0, 0, 0, 255, 0, 128, 0, 0, 255, 255, 0, 0, 255, 255, 0, 255, 0, 128, 255, 0, 0, 0 }, image.Pixels);
        }

        [Fact]
        public void RgbColourKeyBecomesTransparent()
        {
            byte[] rows = { 10, 20, 30, 40, 50, 60 };
            byte[] trns = { 0, 10, 0, 20, 0, 30 };
            Image image = Image.Load(PngBuilder.Build(2, 1, 2, 8, rows, 0, null, trns));
            Assert.Equal(0, image.Pixels[3]);
            Assert.Equal(255, image.Pixels[7]);
        }

        [Fact]
        public void GreyAlphaDecodes()
        {
            byte[] rows = { 100, 7, 200, 250 };
            Image image = Image.Load(PngBuilder.Build(2, 1, 4, 8, rows, 2));
            Assert.Equal(new byte[] { 100, 100, 100, 7, 200, 200, 200, 250 }, image.Pixels);
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(3, 2)]
        [InlineData(9, 9)]
        [InlineData(33, 17)]
        public void InterlacedMatchesProgressive(int width, int height)
        {
            byte[] pixels = Pattern(width * height * 4, width);
            Image image = Image.Load(PngBuilder.Build(width, height, 6, 8, pixels, 4, null, null, true));
            Assert.Equal(pixels, image.Pixels);
        }

        [Theory]
        [InlineData(PixelLayout.RGBA8)]
        [InlineData(PixelLayout.BGRA8)]
        [InlineData(PixelLayout.R8)]
        [InlineData(PixelLayout.RG8)]
        public void EncoderRoundTripsEveryLayout(PixelLayout layout)
        {
            int width = 41, height = 29;
            byte[] pixels = Pattern(width * height * ImageCodec.BytesPerPixel(layout), 11);
            MemoryStream png = new MemoryStream();
            ImageCodec.EncodePng(pixels, width, height, 0, layout, png);
            Image image = Image.Load(png.ToArray(), layout);
            Assert.Equal(pixels, image.Pixels);
        }

        [Fact]
        public void DecodeHonoursRowPitch()
        {
            byte[] pixels = Pattern(5 * 3 * 4, 2);
            byte[] png = PngBuilder.Build(5, 3, 6, 8, pixels, 1);
            Assert.True(ImageCodec.TryIdentify(png, out ImageHeader header));
            DecodeOptions options = new DecodeOptions(PixelLayout.RGBA8, 32);
            Assert.Equal(32 * 2 + 20, ImageCodec.RequiredBytes(header, options));
            byte[] destination = new byte[ImageCodec.RequiredBytes(header, options)];
            Array.Fill(destination, (byte)0xAB);
            ImageCodec.Decode(png, destination, options);
            for (int y = 0; y < 3; y++)
            {
                Assert.Equal(pixels.AsSpan(y * 20, 20).ToArray(), destination.AsSpan(y * 32, 20).ToArray());
                if (y < 2) Assert.Equal(0xAB, destination[y * 32 + 20]);
            }
        }

        [Fact]
        public void BadChecksumIsRejected()
        {
            byte[] png = PngBuilder.Build(4, 4, 6, 8, Pattern(64, 1), 0);
            png[png.Length - 20] ^= 0xFF;
            Assert.Throws<ImageFormatException>(() => Image.Load(png));
        }

        [Fact]
        public void TruncatedFileIsRejected()
        {
            byte[] png = PngBuilder.Build(16, 16, 6, 8, Pattern(1024, 1), 0);
            for (int cut = 34; cut < png.Length - 12; cut += 7)
                Assert.Throws<ImageFormatException>(() => Image.Load(png.AsSpan(0, cut)));
        }

        [Fact]
        public void InvalidFilterIsRejected()
        {
            byte[] png = PngBuilder.Build(2, 2, 6, 8, Pattern(16, 1), 5);
            Assert.Throws<ImageFormatException>(() => Image.Load(png));
        }

        [Fact]
        public void OversizedHeaderIsRejectedBeforeAllocating()
        {
            byte[] png = PngBuilder.Build(1, 1, 6, 8, new byte[4], 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), 100000);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), 100000);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), PngBuilder.ReferenceCrc(png.AsSpan(12, 17)));
            Assert.Throws<ImageFormatException>(() => Image.Load(png));
            Assert.Throws<ImageFormatException>(() => Image.Load(png, PixelLayout.RGBA8, new ImageLimits(200000, 200000, 1000)));
        }

        [Fact]
        public void InvalidDepthIsRejected()
        {
            byte[] png = PngBuilder.Build(1, 1, 6, 8, new byte[4], 0);
            png[24] = 4;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), PngBuilder.ReferenceCrc(png.AsSpan(12, 17)));
            Assert.Throws<ImageFormatException>(() => Image.Load(png));
        }
    }
}
