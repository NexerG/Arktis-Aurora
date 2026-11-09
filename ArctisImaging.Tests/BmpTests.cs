using System;
using System.Buffers.Binary;
using System.IO;
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

        // 61 wide so every SIMD tier and the scalar tail run; rows bottom-up and top-down, a padded pitch left untouched.
        [Theory]
        [InlineData("32")]
        [InlineData("24")]
        [InlineData("555")]
        [InlineData("565")]
        public void DirectRowsWriteEveryLayout(string kind)
        {
            const int width = 61, height = 3;
            int bitCount = kind == "32" ? 32 : kind == "24" ? 24 : 16;
            byte[] masks = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(masks, 0xF800);
            BinaryPrimitives.WriteUInt32LittleEndian(masks.AsSpan(4), 0x07E0);
            BinaryPrimitives.WriteUInt32LittleEndian(masks.AsSpan(8), 0x001F);
            byte[] rgba = new byte[width * height * 4];
            Func<int, int, byte[]> pixel = (x, y) =>
            {
                uint h = (uint)(x * 73856093 ^ y * 19349663) * 2654435761u;
                byte b = (byte)h, g = (byte)(h >> 8), r = (byte)(h >> 16), a = (byte)(h >> 24 | 1);
                int v = kind == "555" ? (int)(h & 0x7FFF) : (int)(h & 0xFFFF);
                byte[] source = kind == "32" ? new byte[] { b, g, r, a } : kind == "24" ? new byte[] { b, g, r } : new byte[] { (byte)v, (byte)(v >> 8) };
                if (kind == "555")
                {
                    r = Replicate((v >> 10) & 31, 5);
                    g = Replicate((v >> 5) & 31, 5);
                    b = Replicate(v & 31, 5);
                }
                else if (kind == "565")
                {
                    r = Replicate(v >> 11, 5);
                    g = Replicate((v >> 5) & 63, 6);
                    b = Replicate(v & 31, 5);
                }
                int at = (y * width + x) * 4;
                rgba[at] = r;
                rgba[at + 1] = g;
                rgba[at + 2] = b;
                rgba[at + 3] = kind == "32" ? a : (byte)255;
                return source;
            };
            foreach (bool topDown in new bool[] { false, true })
            {
                byte[] dib = Dib(width, topDown ? -height : height, bitCount, kind == "565" ? 3u : 0u, kind == "565" ? masks : Array.Empty<byte>(), pixel);
                foreach (PixelLayout layout in new PixelLayout[] { PixelLayout.RGBA8, PixelLayout.BGRA8, PixelLayout.R8, PixelLayout.RG8 })
                {
                    int size = layout == PixelLayout.R8 ? 1 : layout == PixelLayout.RG8 ? 2 : 4, pitch = width * size + 7;
                    byte[] pixels = new byte[pitch * height];
                    Array.Fill(pixels, (byte)0xA5);
                    ImageCodec.DecodeDib(dib, pixels, new DecodeOptions(layout, pitch));
                    byte[] expected = new byte[width * size];
                    for (int y = 0; y < height; y++)
                    {
                        PixelRows.FromRgba(rgba.AsSpan(y * width * 4), expected, width, layout);
                        Assert.Equal(expected, pixels.AsSpan(y * pitch, width * size).ToArray());
                        for (int i = width * size; i < pitch; i++) Assert.Equal(0xA5, pixels[y * pitch + i]);
                    }
                }
            }
        }

        private static byte Replicate(int c, int bits) => (byte)(bits == 5 ? c << 3 | c >> 2 : c << 2 | c >> 4);

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

        // A DIB header and palette followed by an RLE stream instead of rows.
        private static byte[] RleDib(int width, int height, int bitCount, byte[] palette, byte[] stream)
        {
            byte[] header = Dib(width, height, bitCount, bitCount == 8 ? 1u : 2u, palette, (x, y) => new byte[1]);
            byte[] dib = new byte[40 + palette.Length + stream.Length];
            Array.Copy(header, dib, 40 + palette.Length);
            stream.CopyTo(dib, 40 + palette.Length);
            return dib;
        }

        // entries 1-3 of a palette as BGR0, read back as RGBA
        private static byte[] RlePalette(int entries)
        {
            byte[] palette = new byte[entries * 4];
            for (int i = 1; i <= 3; i++)
            {
                palette[i * 4] = (byte)(i * 3 - 2);
                palette[i * 4 + 1] = (byte)(i * 3 - 1);
                palette[i * 4 + 2] = (byte)(i * 3);
            }
            return palette;
        }

        private static readonly byte[] none = { 0, 0, 0, 0 }, one = { 3, 2, 1, 255 }, two = { 6, 5, 4, 255 }, three = { 9, 8, 7, 255 };

        private static byte[] Row(params byte[][] pixels)
        {
            byte[] row = new byte[pixels.Length * 4];
            for (int i = 0; i < pixels.Length; i++) pixels[i].CopyTo(row, i * 4);
            return row;
        }

        [Fact]
        public void Rle8RunsLiteralsDeltaAndEnd()
        {
            // bottom row: a run of 2, a literal of 3 that overflows the width, end of line; a delta down a row and one right;
            // a run of 2, end of bitmap
            byte[] stream = { 2, 1, 0, 3, 2, 3, 2, 0, 0, 0, 0, 2, 1, 1, 2, 3, 0, 1 };
            Image image = Image.LoadDib(RleDib(4, 3, 8, RlePalette(256), stream));
            byte[] expected = new byte[4 * 3 * 4];
            Row(none, three, three, none).CopyTo(expected, 0);
            Row(one, one, two, three).CopyTo(expected, 32);
            Assert.Equal(expected, image.Pixels);
        }

        [Fact]
        public void Rle4AlternatesNibbles()
        {
            byte[] stream = { 3, 0x12, 0, 3, 0x21, 0x30, 0, 0, 0, 1 };
            Image image = Image.LoadDib(RleDib(5, 2, 4, RlePalette(16), stream));
            byte[] expected = new byte[5 * 2 * 4];
            Row(one, two, one, two, one).CopyTo(expected, 20);
            Assert.Equal(expected, image.Pixels);
        }

        [Fact]
        public void RleLiteralPastTheEndIsRejected()
        {
            byte[] stream = { 0, 5, 1, 2 };
            Assert.Throws<ImageFormatException>(() => Image.LoadDib(RleDib(5, 1, 8, RlePalette(256), stream)));
        }

        // BGR0 palette entries that differ in every channel; read back as RGBA, indices past the count as opaque black.
        private static byte[] IndexedPalette(int entries)
        {
            byte[] palette = new byte[entries * 4];
            for (int i = 0; i < entries; i++)
            {
                palette[i * 4] = (byte)(i * 13 + 7);
                palette[i * 4 + 1] = (byte)(i * 29 + 3);
                palette[i * 4 + 2] = (byte)(i * 53 + 11);
            }
            return palette;
        }

        private static void PutIndexed(byte[] rgba, int at, int index, byte[] palette)
        {
            bool known = index * 4 < palette.Length;
            rgba[at] = known ? palette[index * 4 + 2] : (byte)0;
            rgba[at + 1] = known ? palette[index * 4 + 1] : (byte)0;
            rgba[at + 2] = known ? palette[index * 4] : (byte)0;
            rgba[at + 3] = 255;
        }

        // Decodes into every layout with a padded pitch and checks each row against the RGBA8 reference.
        private static void AssertEveryLayout(byte[] dib, byte[] rgba, int width, int height)
        {
            foreach (PixelLayout layout in new PixelLayout[] { PixelLayout.RGBA8, PixelLayout.BGRA8, PixelLayout.R8, PixelLayout.RG8 })
            {
                int size = layout == PixelLayout.R8 ? 1 : layout == PixelLayout.RG8 ? 2 : 4, pitch = width * size + 7;
                byte[] pixels = new byte[pitch * height];
                Array.Fill(pixels, (byte)0xA5);
                ImageCodec.DecodeDib(dib, pixels, new DecodeOptions(layout, pitch));
                byte[] expected = new byte[width * size];
                for (int y = 0; y < height; y++)
                {
                    PixelRows.FromRgba(rgba.AsSpan(y * width * 4), expected, width, layout);
                    Assert.Equal(expected, pixels.AsSpan(y * pitch, width * size).ToArray());
                    for (int i = width * size; i < pitch; i++) Assert.Equal(0xA5, pixels[y * pitch + i]);
                }
            }
        }

        // 245 wide so every index and lookup tier and the scalar tail run; both row orders; short palettes leave indices
        // past their count opaque black.
        [Theory]
        [InlineData(1, 2)]
        [InlineData(4, 16)]
        [InlineData(4, 5)]
        [InlineData(8, 256)]
        [InlineData(8, 7)]
        public void IndexedRowsWriteEveryLayout(int bitCount, int entries)
        {
            const int width = 245, height = 3;
            byte[] palette = IndexedPalette(entries), rgba = new byte[width * height * 4];
            Func<int, int, byte[]> pixel = (x, y) =>
            {
                int index = (int)((uint)(x * 73856093 ^ y * 19349663) * 2654435761u >> 24) & ((1 << bitCount) - 1);
                PutIndexed(rgba, (y * width + x) * 4, index, palette);
                return new byte[] { (byte)index };
            };
            foreach (bool topDown in new bool[] { false, true })
            {
                byte[] dib = Dib(width, topDown ? -height : height, bitCount, 0, palette, pixel);
                BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(32), entries);
                AssertEveryLayout(dib, rgba, width, height);
            }
        }

        // A bit-field channel widened to 8 bits by repeating its bits, the BMP decoder's rule.
        private static byte Widen(uint v, uint mask)
        {
            if (mask == 0) return 0;
            int shift = System.Numerics.BitOperations.TrailingZeroCount(mask), bits = System.Numerics.BitOperations.PopCount(mask);
            uint c = (v & mask) >> shift;
            if (bits >= 8) return (byte)(c >> (bits - 8));
            uint wide = c << (8 - bits);
            for (int filled = bits; filled < 8; filled *= 2) wide |= wide >> filled;
            return (byte)wide;
        }

        // 245 wide so every tier and the scalar tail run; both row orders, all layouts, a padded pitch left untouched;
        // a 32-bit bitmap whose alpha is zero everywhere comes out opaque.
        [Theory]
        [InlineData("1555")]
        [InlineData("4444")]
        [InlineData("444x")]
        [InlineData("rgba")]
        [InlineData("abgr")]
        [InlineData("1010102")]
        [InlineData("scattered")]
        [InlineData("xrgb")]
        [InlineData("zero")]
        [InlineData("rgbazero")]
        public void BitFieldRowsWriteEveryLayout(string kind)
        {
            const int width = 245, height = 3;
            uint[] masks = kind switch
            {
                "1555" => new uint[] { 0x7C00, 0x03E0, 0x001F, 0x8000 },
                "4444" => new uint[] { 0x0F00, 0x00F0, 0x000F, 0xF000 },
                "444x" => new uint[] { 0x0F00, 0x00F0, 0x000F, 0 },
                "abgr" => new uint[] { 0xFF000000, 0x00FF0000, 0x0000FF00, 0x000000FF },
                "1010102" => new uint[] { 0x3FF00000, 0x000FFC00, 0x000003FF, 0xC0000000 },
                "scattered" => new uint[] { 0x00F00F00, 0x000000F5, 0x0A000000, 0xF0000000 },
                "xrgb" => new uint[] { 0x00FF0000, 0x0000FF00, 0x000000FF, 0 },
                "zero" => new uint[] { 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000 },
                _ => new uint[] { 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000 },
            };
            int bitCount = kind is "1555" or "4444" or "444x" ? 16 : 32;
            bool noAlpha = kind.EndsWith("zero");
            byte[] rgba = new byte[width * height * 4];
            Func<int, int, byte[]> pixel = (x, y) =>
            {
                uint v = (uint)(x * 73856093 ^ y * 19349663) * 2654435761u;
                if (bitCount == 16) v &= 0xFFFF;
                if (noAlpha) v &= ~masks[3];
                int at = (y * width + x) * 4;
                rgba[at] = Widen(v, masks[0]);
                rgba[at + 1] = Widen(v, masks[1]);
                rgba[at + 2] = Widen(v, masks[2]);
                rgba[at + 3] = masks[3] == 0 || noAlpha ? (byte)255 : Widen(v, masks[3]);
                byte[] bytes = new byte[bitCount / 8];
                for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(v >> (i * 8));
                return bytes;
            };
            foreach (bool topDown in new bool[] { false, true })
            {
                byte[] dib = kind == "zero"
                    ? Dib(width, topDown ? -height : height, 32, 0, Array.Empty<byte>(), pixel)
                    : Dib(width, topDown ? -height : height, bitCount, 3, Array.Empty<byte>(), pixel, 124, masks);
                AssertEveryLayout(dib, rgba, width, height);
            }
        }

        // Runs past the width, odd literals, a short end of line, a delta across rows and an early end: skipped pixels
        // come out transparent black in every layout.
        [Theory]
        [InlineData(4)]
        [InlineData(8)]
        public void RleWritesEveryLayout(int bitCount)
        {
            const int width = 245, height = 4;
            bool four = bitCount == 4;
            byte[] palette = IndexedPalette(four ? 16 : 256), rgba = new byte[width * height * 4];
            MemoryStream stream = new MemoryStream();
            int x = 0, row = 0;
            void Put(int index)
            {
                if (x < width && row < height) PutIndexed(rgba, ((height - 1 - row) * width + x) * 4, index, palette);
                x++;
            }
            void Run(int n, int a, int b)
            {
                stream.WriteByte((byte)n);
                stream.WriteByte((byte)(four ? a << 4 | b : a));
                for (int k = 0; k < n; k++) Put((k & 1) == 0 || !four ? a : b);
            }
            void Literal(params int[] indices)
            {
                stream.WriteByte(0);
                stream.WriteByte((byte)indices.Length);
                int bytes = four ? (indices.Length + 1) / 2 : indices.Length;
                for (int k = 0; k < bytes; k++)
                    stream.WriteByte((byte)(four ? indices[k * 2] << 4 | (k * 2 + 1 < indices.Length ? indices[k * 2 + 1] : 0) : indices[k]));
                if ((bytes & 1) != 0) stream.WriteByte(0);
                foreach (int index in indices) Put(index);
            }
            void EndOfLine()
            {
                stream.Write(new byte[] { 0, 0 });
                x = 0;
                row++;
            }

            Run(200, 3, 9);
            Literal(1, 14, 2, 7, 11, 5, 8);
            Run(255, 6, 12);
            EndOfLine();
            Run(10, 4, 4);
            Literal(5, 6, 7);
            stream.Write(new byte[] { 0, 2, 3, 0 });
            x += 3;
            Run(2, 8, 10);
            EndOfLine();
            Literal(13, 2, 9);
            stream.Write(new byte[] { 0, 2, 5, 1 });
            x += 5;
            row++;
            Run(4, 15, 1);
            stream.Write(new byte[] { 0, 1 });
            AssertEveryLayout(RleDib(width, height, bitCount, palette, stream.ToArray()), rgba, width, height);
        }

        private static byte[] Fixture(string file) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));

        private static byte[] Encode(Image image, in BmpSettings settings)
        {
            MemoryStream output = new MemoryStream();
            image.SaveBmp(output, settings);
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

        // An 8-bit channel through a field of the given width and back, widened by bit replication as the decoder does.
        private static byte Through(int v, int bits)
        {
            if (bits >= 8) return (byte)v;
            int q = (v * ((1 << bits) - 1) + 127) / 255, wide = q << (8 - bits);
            for (int filled = bits; filled < 8; filled *= 2) wide |= wide >> filled;
            return (byte)wide;
        }

        // Fixtures written by Pillow 13 from the PNG named first: 24-bit for RGB, 32-bit BI_RGB for RGBA, grey palette for L.
        [Fact]
        public void EncodeMatchesPillow()
        {
            Assert.Equal(Fixture("EncodeSource.bmp"), Encode(Image.Load(Fixture("EncodeSource.png")), new BmpSettings(BmpPixelFormat.Rgb24)));
            Assert.Equal(Fixture("EncodeRgba.bmp"), Encode(Image.Load(Fixture("QoiRgba.png")), new BmpSettings(BmpPixelFormat.Argb32, BmpHeader.Info)));
            Assert.Equal(Fixture("EncodeGrey.bmp"), Encode(Image.Load(Fixture("EncodeGrey.png"), PixelLayout.R8), default));
        }

        [Theory]
        [InlineData(BmpPixelFormat.Indexed1, BmpHeader.Info, BmpCompression.None, false)]
        [InlineData(BmpPixelFormat.Indexed1, BmpHeader.Core, BmpCompression.None, false)]
        [InlineData(BmpPixelFormat.Indexed4, BmpHeader.Info, BmpCompression.None, true)]
        [InlineData(BmpPixelFormat.Indexed4, BmpHeader.Info, BmpCompression.Rle, false)]
        [InlineData(BmpPixelFormat.Indexed8, BmpHeader.V5, BmpCompression.None, true)]
        [InlineData(BmpPixelFormat.Indexed8, BmpHeader.Auto, BmpCompression.Rle, false)]
        [InlineData(BmpPixelFormat.Indexed8, BmpHeader.Core, BmpCompression.Auto, false)]
        [InlineData(BmpPixelFormat.Rgb555, BmpHeader.Info, BmpCompression.None, true)]
        [InlineData(BmpPixelFormat.Rgb555, BmpHeader.V4, BmpCompression.BitFields, false)]
        [InlineData(BmpPixelFormat.Rgb565, BmpHeader.Info, BmpCompression.Auto, false)]
        [InlineData(BmpPixelFormat.Rgb565, BmpHeader.V5, BmpCompression.BitFields, true)]
        [InlineData(BmpPixelFormat.Argb1555, BmpHeader.Info, BmpCompression.Auto, false)]
        [InlineData(BmpPixelFormat.Argb1555, BmpHeader.V4, BmpCompression.BitFields, true)]
        [InlineData(BmpPixelFormat.Argb4444, BmpHeader.Auto, BmpCompression.Auto, true)]
        [InlineData(BmpPixelFormat.Argb4444, BmpHeader.Info, BmpCompression.AlphaBitFields, false)]
        [InlineData(BmpPixelFormat.Rgb24, BmpHeader.Core, BmpCompression.None, false)]
        [InlineData(BmpPixelFormat.Rgb24, BmpHeader.Info, BmpCompression.None, true)]
        [InlineData(BmpPixelFormat.Rgb24, BmpHeader.V5, BmpCompression.Auto, false)]
        [InlineData(BmpPixelFormat.Xrgb32, BmpHeader.Info, BmpCompression.None, false)]
        [InlineData(BmpPixelFormat.Xrgb32, BmpHeader.Info, BmpCompression.BitFields, true)]
        [InlineData(BmpPixelFormat.Argb32, BmpHeader.Info, BmpCompression.None, false)]
        [InlineData(BmpPixelFormat.Argb32, BmpHeader.Info, BmpCompression.AlphaBitFields, true)]
        [InlineData(BmpPixelFormat.Argb32, BmpHeader.V4, BmpCompression.BitFields, false)]
        [InlineData(BmpPixelFormat.Auto, BmpHeader.Auto, BmpCompression.Auto, true)]
        public void EncodeRoundTripsEveryForm(BmpPixelFormat format, BmpHeader header, BmpCompression compression, bool topDown)
        {
            Image image = Colours(format == BmpPixelFormat.Indexed1 ? 2 : format == BmpPixelFormat.Indexed4 ? 16 : 200);
            (int r, int g, int b, int a) bits = format switch
            {
                BmpPixelFormat.Rgb555 => (5, 5, 5, 0),
                BmpPixelFormat.Rgb565 => (5, 6, 5, 0),
                BmpPixelFormat.Argb1555 => (5, 5, 5, 1),
                BmpPixelFormat.Argb4444 => (4, 4, 4, 4),
                BmpPixelFormat.Argb32 or BmpPixelFormat.Auto => (8, 8, 8, 8),
                _ => (8, 8, 8, 0)
            };
            byte[] p = image.Pixels, expected = new byte[p.Length];
            for (int i = 0; i < p.Length; i += 4)
            {
                expected[i] = Through(p[i], bits.r);
                expected[i + 1] = Through(p[i + 1], bits.g);
                expected[i + 2] = Through(p[i + 2], bits.b);
                expected[i + 3] = bits.a == 0 ? (byte)255 : Through(p[i + 3], bits.a);
            }
            BmpSettings settings = new BmpSettings(format, header, compression, topDown);
            byte[] file = Encode(image, settings);
            Assert.Equal(expected, Image.Load(file).Pixels);
            Assert.Equal(topDown, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(22)) < 0);

            MemoryStream dib = new MemoryStream();
            ImageCodec.EncodeDib(image.Pixels, image.Width, image.Height, 0, PixelLayout.RGBA8, dib, settings);
            Assert.Equal(file[14..], dib.ToArray());
            Assert.Equal(expected, Image.LoadDib(dib.ToArray()).Pixels);
        }

        [Fact]
        public void EncodeDefaultIsV5WithAlphaAndGreyPaletteForR8()
        {
            byte[] file = Encode(Colours(9), default);
            Assert.Equal(124, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(14)));
            Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(14 + 16)));
            Assert.Equal(0xFF000000u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(14 + 52)));
            byte[] grey = Encode(Image.Load(Fixture("EncodeGrey.png"), PixelLayout.R8), default);
            Assert.Equal(8, BinaryPrimitives.ReadUInt16LittleEndian(grey.AsSpan(14 + 14)));
            Assert.Equal(256u, BinaryPrimitives.ReadUInt32LittleEndian(grey.AsSpan(14 + 32)));
        }

        [Fact]
        public void EncodeRleLongRunsAndLiteralsRoundTrip()
        {
            Image image = new Image(600, 3);
            for (int x = 0; x < 600; x++)
            {
                int i = x * 4;
                image.Pixels[i] = (byte)(x < 300 ? 10 : x % 7 * 30);
                image.Pixels[i + 600 * 4] = (byte)(x % 3 == 0 ? 50 : 90);
                image.Pixels[i + 1200 * 4] = (byte)(x / 2 % 11 * 20);
            }
            for (int i = 3; i < image.Pixels.Length; i += 4) image.Pixels[i] = 255;
            foreach (BmpPixelFormat format in new[] { BmpPixelFormat.Indexed4, BmpPixelFormat.Indexed8 })
                Assert.Equal(image.Pixels, Image.Load(Encode(image, new BmpSettings(format, compression: BmpCompression.Rle))).Pixels);
        }

        [Fact]
        public void EncodeContradictionsAndOverflowThrow()
        {
            Image image = Colours(3);
            Assert.Throws<ArgumentException>(() => Encode(image, new BmpSettings(BmpPixelFormat.Rgb24, compression: BmpCompression.Rle)));
            Assert.Throws<ArgumentException>(() => Encode(image, new BmpSettings(BmpPixelFormat.Indexed8, compression: BmpCompression.Rle, topDown: true)));
            Assert.Throws<ArgumentException>(() => Encode(image, new BmpSettings(BmpPixelFormat.Indexed8, BmpHeader.Core, topDown: true)));
            Assert.Throws<ArgumentException>(() => Encode(image, new BmpSettings(BmpPixelFormat.Argb32, BmpHeader.Core)));
            Assert.Throws<ArgumentException>(() => Encode(image, new BmpSettings(BmpPixelFormat.Argb32, BmpHeader.V5, BmpCompression.AlphaBitFields)));
            Assert.Throws<ArgumentException>(() => Encode(image, new BmpSettings(BmpPixelFormat.Argb1555, BmpHeader.Info, BmpCompression.None)));
            Assert.Throws<ArgumentException>(() => Encode(image, new BmpSettings(BmpPixelFormat.Argb32, BmpHeader.Info, BmpCompression.BitFields)));
            Assert.Throws<ArgumentException>(() => Encode(image, new BmpSettings(BmpPixelFormat.Rgb24, compression: BmpCompression.BitFields)));
            Assert.Throws<ArgumentException>(() => Encode(Image.Load(Fixture("EncodeSource.png")), new BmpSettings(BmpPixelFormat.Indexed8)));
            Assert.Throws<ArgumentException>(() => Encode(image, new BmpSettings(BmpPixelFormat.Indexed1)));
        }
    }
}
