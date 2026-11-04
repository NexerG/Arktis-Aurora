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
        [InlineData(0, 8)]
        [InlineData(0, 16)]
        [InlineData(2, 8)]
        [InlineData(2, 16)]
        [InlineData(4, 8)]
        [InlineData(4, 16)]
        [InlineData(6, 8)]
        [InlineData(6, 16)]
        public void EveryDepthDecodesUnderEveryFilter(byte colorType, byte depth)
        {
            const int width = 37, height = 11;
            int channels = colorType switch { 2 => 3, 4 => 2, 6 => 4, _ => 1 };
            int sampleBytes = depth / 8;
            byte[] samples = Pattern(width * height * channels * sampleBytes, colorType + depth);

            byte[] expected = new byte[width * height * 4];
            Span<int> v = stackalloc int[4];
            for (int i = 0; i < width * height; i++)
            {
                for (int k = 0; k < channels; k++)
                {
                    int at = (i * channels + k) * sampleBytes;
                    int s = depth == 16 ? samples[at] << 8 | samples[at + 1] : samples[at];
                    v[k] = depth == 16 ? (s * 255 + 32895) >> 16 : s;
                }
                bool colour = colorType == 2 || colorType == 6;
                bool alpha = colorType == 4 || colorType == 6;
                expected[i * 4] = (byte)v[0];
                expected[i * 4 + 1] = (byte)(colour ? v[1] : v[0]);
                expected[i * 4 + 2] = (byte)(colour ? v[2] : v[0]);
                expected[i * 4 + 3] = (byte)(alpha ? v[channels - 1] : 255);
            }

            for (byte filter = 0; filter <= 4; filter++)
            {
                for (int interlace = 0; interlace < 2; interlace++)
                {
                    byte[] png = PngBuilder.Build(width, height, colorType, depth, samples, filter, interlaced: interlace == 1);
                    byte[] rgba = new byte[width * height * 4];
                    byte[] bgra = new byte[width * height * 4];
                    ImageCodec.Decode(png, rgba, new DecodeOptions(PixelLayout.RGBA8));
                    ImageCodec.Decode(png, bgra, new DecodeOptions(PixelLayout.BGRA8));
                    Assert.Equal(expected, rgba);
                    for (int i = 0; i < width * height; i++)
                    {
                        Assert.Equal(expected[i * 4 + 2], bgra[i * 4]);
                        Assert.Equal(expected[i * 4 + 1], bgra[i * 4 + 1]);
                        Assert.Equal(expected[i * 4], bgra[i * 4 + 2]);
                        Assert.Equal(expected[i * 4 + 3], bgra[i * 4 + 3]);
                    }
                }
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
        [InlineData(6, 8, false)]
        [InlineData(2, 8, false)]
        [InlineData(0, 8, false)]
        [InlineData(6, 16, false)]
        [InlineData(6, 8, true)]
        [InlineData(0, 8, true)]
        public void SmallWindowsMatchOneWholeWindow(byte colorType, byte depth, bool interlaced)
        {
            int width = 301, height = 233;
            int channels = colorType == 6 ? 4 : colorType == 2 ? 3 : 1;
            int rowBytes = width * channels * depth / 8;
            byte[] samples = Pattern(rowBytes * height, colorType + depth);
            for (int i = samples.Length / 2; i < samples.Length; i++) samples[i] = samples[i - 5003];
            int[] fills = { 1, 7, 4096, rowBytes };
            for (byte filter = 0; filter <= 4; filter++)
            {
                byte[] png = PngBuilder.Build(width, height, colorType, depth, samples, filter, null, null, interlaced);
                byte[] whole = new byte[width * height * 4];
                PngDecoder.Decode(png, whole, width * 4, PixelLayout.RGBA8, 1 << 30);
                for (int f = 0; f < fills.Length; f++)
                {
                    byte[] windowed = new byte[whole.Length];
                    PngDecoder.Decode(png, windowed, width * 4, PixelLayout.RGBA8, fills[f]);
                    Assert.Equal(whole, windowed);
                }
            }
        }

        [Fact]
        public void MixedFiltersMatchReference()
        {
            int width = 301, height = 233;
            byte[] pixels = Pattern(width * height * 4, width);
            for (int i = pixels.Length / 2; i < pixels.Length; i++) pixels[i] = pixels[i - 5003];
            byte[] rowFilters = { 4, 4, 4, 4, 4, 1, 4, 4, 2, 4, 4, 4, 4, 4, 4, 4, 4, 3, 0, 4, 4, 4 };
            byte[] png = PngBuilder.Build(width, height, 6, 8, pixels, 0, null, null, false, rowFilters);
            int[] fills = { 1, 7, 4096, width * 4, 1 << 30 };
            for (int f = 0; f < fills.Length; f++)
            {
                byte[] decoded = new byte[pixels.Length];
                PngDecoder.Decode(png, decoded, width * 4, PixelLayout.RGBA8, fills[f]);
                Assert.Equal(pixels, decoded);
            }
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
