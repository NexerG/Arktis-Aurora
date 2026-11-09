using System;
using System.Buffers.Binary;
using System.IO;
using Xunit;

namespace Arctis.Imaging.Tests
{
    public class TgaTests
    {
        // An 18-byte header, an optional colour map, then the pixel bytes as given.
        private static byte[] Tga(int imageType, int width, int height, int depth, int descriptor, byte[] pixels, byte[]? map = null, int mapFirst = 0, int mapEntryBits = 24)
        {
            int mapLength = map == null ? 0 : map.Length / ((mapEntryBits + 7) / 8);
            byte[] file = new byte[18 + (map?.Length ?? 0) + pixels.Length];
            file[1] = (byte)(map == null ? 0 : 1);
            file[2] = (byte)imageType;
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(3), (ushort)mapFirst);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(5), (ushort)mapLength);
            file[7] = (byte)(map == null ? 0 : mapEntryBits);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(12), (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(14), (ushort)height);
            file[16] = (byte)depth;
            file[17] = (byte)descriptor;
            map?.CopyTo(file, 18);
            pixels.CopyTo(file, 18 + (map?.Length ?? 0));
            return file;
        }

        private static Image Load(byte[] file, PixelLayout layout = PixelLayout.RGBA8) => Image.Load(file, layout, default, ImageFormat.Tga);

        private static void AssertPixel(Image image, int x, int y, int r, int g, int b, int a)
        {
            int i = (y * image.Width + x) * 4;
            Assert.Equal(new byte[] { (byte)r, (byte)g, (byte)b, (byte)a }, image.Pixels[i..(i + 4)]);
        }

        [Fact]
        public void IsRecognisedOnlyWithTheHint()
        {
            byte[] file = Tga(2, 1, 1, 24, 0, new byte[] { 1, 2, 3 });
            Assert.False(ImageCodec.TryIdentify(file, out _));
            Assert.True(ImageCodec.TryIdentify(file, out ImageHeader header, ImageFormat.Tga));
            Assert.Equal(ImageFormat.Tga, header.Format);
            Assert.Throws<ImageFormatException>(() => Image.Load(file));
        }

        [Fact]
        public void TrueColour24BottomUpFlips()
        {
            // rows stored bottom row first, BGR
            byte[] file = Tga(2, 2, 2, 24, 0, new byte[] { 3, 2, 1, 6, 5, 4, 30, 20, 10, 60, 50, 40 });
            Image image = Load(file);
            AssertPixel(image, 0, 0, 10, 20, 30, 255);
            AssertPixel(image, 1, 0, 40, 50, 60, 255);
            AssertPixel(image, 0, 1, 1, 2, 3, 255);
            AssertPixel(image, 1, 1, 4, 5, 6, 255);
        }

        [Fact]
        public void TrueColour32TopDownKeepsAlpha()
        {
            byte[] file = Tga(2, 2, 1, 32, 0x28, new byte[] { 3, 2, 1, 128, 6, 5, 4, 0 });
            Image image = Load(file);
            AssertPixel(image, 0, 0, 1, 2, 3, 128);
            AssertPixel(image, 1, 0, 4, 5, 6, 0);
        }

        [Fact]
        public void ZeroAlphaEverywhereIsOpaque()
        {
            byte[] file = Tga(2, 2, 1, 32, 0x28, new byte[] { 3, 2, 1, 0, 6, 5, 4, 0 });
            Image image = Load(file);
            AssertPixel(image, 0, 0, 1, 2, 3, 255);
            AssertPixel(image, 1, 0, 4, 5, 6, 255);
        }

        [Fact]
        public void AlphaBitsZeroIgnoresStoredAlpha()
        {
            byte[] file = Tga(2, 1, 1, 32, 0x20, new byte[] { 3, 2, 1, 7 });
            AssertPixel(Load(file), 0, 0, 1, 2, 3, 255);
        }

        [Fact]
        public void RightToLeftReverses()
        {
            byte[] file = Tga(2, 2, 1, 24, 0x30, new byte[] { 3, 2, 1, 6, 5, 4 });
            Image image = Load(file);
            AssertPixel(image, 0, 0, 4, 5, 6, 255);
            AssertPixel(image, 1, 0, 1, 2, 3, 255);
        }

        [Fact]
        public void Rgb555And1555()
        {
            // 0x7C00 red, 0x03E0 green, 0x801F opaque blue, 0x001F transparent blue
            byte[] pixels = { 0x00, 0x7C, 0xE0, 0x03, 0x1F, 0x80, 0x1F, 0x00 };
            Image noAlpha = Load(Tga(2, 4, 1, 16, 0x20, pixels));
            AssertPixel(noAlpha, 0, 0, 255, 0, 0, 255);
            AssertPixel(noAlpha, 1, 0, 0, 255, 0, 255);
            AssertPixel(noAlpha, 3, 0, 0, 0, 255, 255);
            Image withAlpha = Load(Tga(2, 4, 1, 16, 0x21, pixels));
            AssertPixel(withAlpha, 2, 0, 0, 0, 255, 255);
            AssertPixel(withAlpha, 3, 0, 0, 0, 255, 0);
        }

        [Fact]
        public void GreyAndGreyAlpha()
        {
            AssertPixel(Load(Tga(3, 1, 1, 8, 0x20, new byte[] { 77 })), 0, 0, 77, 77, 77, 255);
            AssertPixel(Load(Tga(3, 1, 1, 16, 0x28, new byte[] { 77, 99 })), 0, 0, 77, 77, 77, 99);
        }

        [Fact]
        public void ColourMappedHonoursFirstEntry()
        {
            // map entries 5 and 6 (first index 5), BGR
            byte[] map = { 30, 20, 10, 60, 50, 40 };
            byte[] file = Tga(1, 3, 1, 8, 0x20, new byte[] { 6, 5, 9 }, map, 5);
            Image image = Load(file);
            AssertPixel(image, 0, 0, 40, 50, 60, 255);
            AssertPixel(image, 1, 0, 10, 20, 30, 255);
            AssertPixel(image, 2, 0, 0, 0, 0, 255);
        }

        [Fact]
        public void RunLengthPacketsCrossRows()
        {
            // a raw packet of 1, a run of 2 across the end of row 0, then a raw packet of 1
            byte[] pixels = { 0x00, 3, 2, 1, 0x81, 6, 5, 4, 0x00, 9, 8, 7 };
            Image image = Load(Tga(10, 2, 2, 24, 0x20, pixels));
            AssertPixel(image, 0, 0, 1, 2, 3, 255);
            AssertPixel(image, 1, 0, 4, 5, 6, 255);
            AssertPixel(image, 0, 1, 4, 5, 6, 255);
            AssertPixel(image, 1, 1, 7, 8, 9, 255);
        }

        [Fact]
        public void RunLengthTruncatedThrows()
        {
            byte[] pixels = { 0x81, 3, 2, 1 };
            Assert.Throws<ImageFormatException>(() => Load(Tga(10, 2, 2, 24, 0x20, pixels)));
        }

        [Fact]
        public void UncompressedTruncatedThrows()
        {
            Assert.Throws<ImageFormatException>(() => Load(Tga(2, 2, 2, 24, 0x20, new byte[9])));
        }

        [Fact]
        public void DecodesToGreyLayout()
        {
            Image image = Load(Tga(3, 2, 1, 8, 0x20, new byte[] { 10, 200 }), PixelLayout.R8);
            Assert.Equal(new byte[] { 10, 200 }, image.Pixels);
        }

        private static readonly PixelLayout[] Layouts = { PixelLayout.RGBA8, PixelLayout.BGRA8, PixelLayout.R8, PixelLayout.RG8 };

        // Packs stored pixels into run-length packets that run on across rows.
        private static byte[] RunLength(byte[] pixels, int bpp)
        {
            MemoryStream output = new MemoryStream();
            int n = pixels.Length / bpp;
            for (int i = 0; i < n;)
            {
                int run = 1;
                while (i + run < n && run < 128 && pixels.AsSpan(i * bpp, bpp).SequenceEqual(pixels.AsSpan((i + run) * bpp, bpp))) run++;
                if (run >= 2)
                {
                    output.WriteByte((byte)(0x80 | (run - 1)));
                    output.Write(pixels, i * bpp, bpp);
                    i += run;
                    continue;
                }
                int start = i++;
                while (i < n && i - start < 128 && (i + 1 >= n || !pixels.AsSpan(i * bpp, bpp).SequenceEqual(pixels.AsSpan((i + 1) * bpp, bpp)))) i++;
                output.WriteByte((byte)(i - start - 1));
                output.Write(pixels, start * bpp, (i - start) * bpp);
            }
            return output.ToArray();
        }

        // Raw and RLE left-to-right files, in every layout and both row orders, decode as the per-pixel path decodes the
        // same pixels stored right to left; row 1 is one colour, so runs cross rows and pass 128 pixels.
        [Theory]
        [InlineData(2, 32, 8, false)]
        [InlineData(2, 32, 8, true)]
        [InlineData(2, 32, 0, false)]
        [InlineData(2, 24, 0, false)]
        [InlineData(2, 16, 0, false)]
        [InlineData(2, 15, 0, false)]
        [InlineData(3, 8, 0, false)]
        [InlineData(3, 16, 8, false)]
        [InlineData(3, 16, 8, true)]
        [InlineData(1, 8, 0, false)]
        [InlineData(1, 8, 8, false)]
        [InlineData(1, 8, 8, true)]
        public void DirectRowsMatchThePerPixelPath(int kind, int depth, int alphaBits, bool zeroAlpha)
        {
            int bpp = (depth + 7) / 8, entryBits = alphaBits > 0 ? 32 : 24, height = 3;
            byte[]? map = null;
            if (kind == 1)
            {
                map = new byte[10 * entryBits / 8];
                new Random(depth + alphaBits).NextBytes(map);
                if (zeroAlpha) for (int i = 3; i < map.Length; i += 4) map[i] = 0;
            }
            for (int width = 1; width <= 149; width += 4)
            {
                byte[] pixels = new byte[width * height * bpp];
                Random random = new Random(width);
                for (int i = 0; i < pixels.Length; i += bpp)
                {
                    if (i > 0 && (i / bpp / width == 1 || random.Next(2) == 0)) Array.Copy(pixels, i - bpp, pixels, i, bpp);
                    else for (int b = 0; b < bpp; b++) pixels[i + b] = (byte)random.Next(256);
                    if (kind == 1) pixels[i] %= 16;
                    if (zeroAlpha && kind != 1) pixels[i + bpp - 1] = 0;
                }
                byte[] reversed = new byte[pixels.Length];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++) Array.Copy(pixels, (y * width + x) * bpp, reversed, (y * width + width - 1 - x) * bpp, bpp);
                byte[] packed = RunLength(pixels, bpp), packedReversed = RunLength(reversed, bpp);
                for (int top = 0; top < 2; top++)
                    foreach (PixelLayout layout in Layouts)
                    {
                        int descriptor = alphaBits | top * 0x20;
                        byte[] expected = Load(Tga(kind, width, height, depth, descriptor | 0x10, reversed, map, 3, entryBits), layout).Pixels;
                        Assert.Equal(expected, Load(Tga(kind, width, height, depth, descriptor, pixels, map, 3, entryBits), layout).Pixels);
                        Assert.Equal(expected, Load(Tga(kind | 8, width, height, depth, descriptor, packed, map, 3, entryBits), layout).Pixels);
                        Assert.Equal(expected, Load(Tga(kind | 8, width, height, depth, descriptor | 0x10, packedReversed, map, 3, entryBits), layout).Pixels);
                    }
            }
        }

        private static byte[] Fixture(string file) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));

        private static byte[] Encode(Image image, in TgaSettings settings)
        {
            MemoryStream output = new MemoryStream();
            image.SaveTga(output, settings);
            return output.ToArray();
        }

        // n colours, each with its own alpha, in a pattern of short runs.
        private static Image Colours(int n, int width = 37, int height = 23)
        {
            Image image = new Image(width, height);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int c = (x / 2 + y * 3) % n, i = (y * width + x) * 4;
                    image.Pixels[i] = (byte)(c * 7);
                    image.Pixels[i + 1] = (byte)(c * 13);
                    image.Pixels[i + 2] = (byte)(c * 29);
                    image.Pixels[i + 3] = (byte)(c * 41);
                }
            return image;
        }

        private static byte Five(int v)
        {
            int q = (v * 31 + 127) / 255;
            return (byte)(q << 3 | q >> 2);
        }

        // Fixtures written by Pillow 13 from the PNG named first: 24-bit for RGB, 32-bit for RGBA, 8-bit grey for L.
        [Theory]
        [InlineData("EncodeSource", "EncodeSource", TgaPixelFormat.Rgb24, false)]
        [InlineData("QoiRgba", "EncodeRgba", TgaPixelFormat.Auto, false)]
        [InlineData("EncodeSource", "EncodeSourceRle", TgaPixelFormat.Rgb24, true)]
        [InlineData("QoiRgba", "EncodeRgbaRle", TgaPixelFormat.Auto, true)]
        public void EncodeMatchesPillow(string source, string name, TgaPixelFormat format, bool rle)
        {
            Assert.Equal(Fixture(name + ".tga"), Encode(Image.Load(Fixture(source + ".png")), new TgaSettings(format, rle)));
        }

        [Fact]
        public void GreyEncodeMatchesPillow()
        {
            Assert.Equal(Fixture("EncodeGrey.tga"), Encode(Image.Load(Fixture("EncodeGrey.png"), PixelLayout.R8), default));
        }

        [Theory]
        [InlineData(TgaPixelFormat.Grey8)]
        [InlineData(TgaPixelFormat.GreyAlpha16)]
        [InlineData(TgaPixelFormat.Indexed8)]
        [InlineData(TgaPixelFormat.IndexedAlpha8)]
        [InlineData(TgaPixelFormat.Rgb555)]
        [InlineData(TgaPixelFormat.Argb1555)]
        [InlineData(TgaPixelFormat.Rgb24)]
        [InlineData(TgaPixelFormat.Argb32)]
        public void EncodeRoundTripsEveryFormatOriginAndCompression(TgaPixelFormat format)
        {
            Image image = Colours(200);
            byte[] p = image.Pixels;
            byte[] expected = new byte[p.Length];
            for (int i = 0; i < p.Length; i += 4)
            {
                byte luma = (byte)((p[i] * 13933 + p[i + 1] * 46871 + p[i + 2] * 4732 + 32768) >> 16);
                bool grey = format == TgaPixelFormat.Grey8 || format == TgaPixelFormat.GreyAlpha16, five = format == TgaPixelFormat.Rgb555 || format == TgaPixelFormat.Argb1555;
                expected[i] = grey ? luma : five ? Five(p[i]) : p[i];
                expected[i + 1] = grey ? luma : five ? Five(p[i + 1]) : p[i + 1];
                expected[i + 2] = grey ? luma : five ? Five(p[i + 2]) : p[i + 2];
                expected[i + 3] = format switch
                {
                    TgaPixelFormat.GreyAlpha16 or TgaPixelFormat.IndexedAlpha8 or TgaPixelFormat.Argb32 => p[i + 3],
                    TgaPixelFormat.Argb1555 => p[i + 3] >= 128 ? (byte)255 : (byte)0,
                    _ => 255
                };
            }
            for (int origin = 0; origin < 4; origin++)
                for (int rle = 0; rle < 2; rle++)
                    Assert.Equal(expected, Load(Encode(image, new TgaSettings(format, rle == 1, (TgaOrigin)origin))).Pixels);
        }

        [Fact]
        public void EncodeRunsLongerThanAPacketRoundTrip()
        {
            Image image = new Image(300, 2);
            for (int i = 0; i < 300 * 4; i += 4) image.Pixels[i] = image.Pixels[i + 3] = 200;
            for (int i = 300 * 4; i < image.Pixels.Length; i++) image.Pixels[i] = (byte)(i * 7);
            Assert.Equal(image.Pixels, Load(Encode(image, new TgaSettings(TgaPixelFormat.Argb32, true))).Pixels);
        }

        [Fact]
        public void EncodeFooterCanBeOmitted()
        {
            Image image = Colours(5);
            byte[] with = Encode(image, default), without = Encode(image, new TgaSettings(TgaPixelFormat.Auto, omitFooter: true));
            Assert.Equal(with.Length - 26, without.Length);
            Assert.Equal(with[..without.Length], without);
            Assert.Equal(image.Pixels, Load(without).Pixels);
        }

        [Fact]
        public void EncodeIndexedOverflowAndOversizeThrow()
        {
            Assert.Throws<ArgumentException>(() => Encode(Image.Load(Fixture("EncodeSource.png")), new TgaSettings(TgaPixelFormat.Indexed8)));
            Assert.Throws<ArgumentException>(() => Encode(new Image(65536, 1, PixelLayout.R8), default));
        }
    }
}
