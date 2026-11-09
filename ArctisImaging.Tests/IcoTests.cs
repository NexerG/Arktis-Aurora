using System;
using System.Buffers.Binary;
using System.IO;
using Xunit;

namespace Arctis.Imaging.Tests
{
    // Fixtures are Pillow 13 icons with 16x16 and 32x32 entries, PNG or BMP; each .png beside one is Pillow's decode.
    public class IcoTests
    {
        private static byte[] Fixture(string file) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));

        // An icon directory over consecutive entries of images, entry k being lengths[k] bytes, placed after it in order.
        private static byte[] Icon(int type, byte[] images, params int[] lengths)
        {
            int start = 6 + lengths.Length * 16, at = start;
            byte[] file = new byte[start + images.Length];
            file[2] = (byte)type;
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), (ushort)lengths.Length);
            for (int i = 0; i < lengths.Length; i++)
            {
                Span<byte> e = file.AsSpan(6 + i * 16, 16);
                BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(8), (uint)lengths[i]);
                BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(12), (uint)at);
                at += lengths[i];
            }
            images.CopyTo(file, start);
            return file;
        }

        // A DIB as an icon stores it: doubled height, colour rows then 1-bit AND rows, both bottom-up and 4-byte padded.
        private static byte[] IconDib(int width, int height, int bitCount, uint[] palette, Func<int, int, uint> colour, Func<int, int, bool> masked)
        {
            int stride = (width * bitCount + 31) / 32 * 4, maskStride = (width + 31) / 32 * 4;
            byte[] dib = new byte[40 + palette.Length * 4 + (stride + maskStride) * height];
            BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), width);
            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), height * 2);
            BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), (ushort)bitCount);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(32), (uint)palette.Length);
            for (int i = 0; i < palette.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(40 + i * 4), palette[i]);
            int pixels = 40 + palette.Length * 4, mask = pixels + stride * height;
            for (int r = 0; r < height; r++)
            {
                int y = height - 1 - r;
                for (int x = 0; x < width; x++)
                {
                    uint c = colour(x, y);
                    if (bitCount == 32) BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(pixels + r * stride + x * 4), c);
                    else if (bitCount == 24)
                    {
                        int at = pixels + r * stride + x * 3;
                        dib[at] = (byte)c;
                        dib[at + 1] = (byte)(c >> 8);
                        dib[at + 2] = (byte)(c >> 16);
                    }
                    else dib[pixels + r * stride + (x * bitCount >> 3)] |= (byte)(c << (8 - bitCount - (x * bitCount & 7)));
                    if (masked(x, y)) dib[mask + r * maskStride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
            return dib;
        }

        [Theory]
        [InlineData("IcoPng")]
        [InlineData("IcoBmp")]
        public void LargestEntryMatchesPillow(string name)
        {
            byte[] file = Fixture(name + ".ico");
            Assert.True(ImageCodec.TryIdentify(file, out ImageHeader header));
            Assert.Equal(ImageFormat.Ico, header.Format);
            Assert.Equal(32, header.Width);
            Image image = Image.Load(file);
            Assert.Equal(Image.Load(Fixture(name + ".png")).Pixels, image.Pixels);
        }

        [Fact]
        public void CursorDecodesLikeAnIcon()
        {
            byte[] file = Fixture("IcoPng.ico");
            file[2] = 2;
            Assert.Equal(Image.Load(Fixture("IcoPng.png")).Pixels, Image.Load(file).Pixels);
        }

        [Fact]
        public void FourBitEntryTakesAlphaFromTheMask()
        {
            uint[] palette = { 0x00000000, 0x00FF0000, 0x0000FF00, 0x000000FF };
            byte[] dib = IconDib(3, 2, 4, palette, (x, y) => (uint)(x + y) & 3, (x, y) => x == 1);
            Image image = Image.Load(Icon(1, dib, dib.Length));
            Assert.Equal(3, image.Width);
            Assert.Equal(2, image.Height);
            Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 0, 0, 0, 0, 255, 0, 255, 255, 0, 0, 255, 0, 255, 0, 0, 0, 0, 255, 255 }, image.Pixels);
        }

        [Fact]
        public void ThirtyTwoBitEntryKeepsItsOwnAlpha()
        {
            byte[] dib = IconDib(2, 1, 32, Array.Empty<uint>(), (x, y) => x == 0 ? 0x80102030u : 0x00405060u, (x, y) => true);
            Image image = Image.Load(Icon(1, dib, dib.Length));
            Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x80, 0x40, 0x50, 0x60, 0 }, image.Pixels);
        }

        [Fact]
        public void ThirtyTwoBitEntryWithNoAlphaUsesTheMask()
        {
            byte[] dib = IconDib(2, 1, 32, Array.Empty<uint>(), (x, y) => 0x00102030u, (x, y) => x == 1);
            Image image = Image.Load(Icon(1, dib, dib.Length));
            Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 255, 0x10, 0x20, 0x30, 0 }, image.Pixels);
        }

        // 17 wide: the first mask byte is clear, the second alternates, the third has one bit.
        [Theory]
        [InlineData(PixelLayout.RGBA8)]
        [InlineData(PixelLayout.RG8)]
        public void TwentyFourBitEntryTakesAlphaFromTheMask(PixelLayout layout)
        {
            Func<int, int, bool> masked = (x, y) => x >= 8 && (x & 1) == y || x == 16;
            Func<int, int, uint> colour = (x, y) => (uint)(x * 0x0A0B0C + y * 0x010203);
            byte[] dib = IconDib(17, 2, 24, Array.Empty<uint>(), colour, masked);
            byte[] file = Icon(1, dib, dib.Length);
            Assert.True(ImageCodec.TryIdentify(file, out ImageHeader header));
            DecodeOptions options = new DecodeOptions(layout);
            byte[] pixels = new byte[ImageCodec.RequiredBytes(header, options)];
            ImageCodec.Decode(file, pixels, options);
            int size = layout == PixelLayout.RG8 ? 2 : 4;
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 17; x++)
                {
                    int at = (y * 17 + x) * size;
                    uint c = colour(x, y);
                    Assert.Equal(masked(x, y) ? 0 : 255, pixels[at + size - 1]);
                    if (layout == PixelLayout.RGBA8) Assert.Equal(new byte[] { (byte)(c >> 16), (byte)(c >> 8), (byte)c }, pixels.AsSpan(at, 3).ToArray());
                }
        }

        [Fact]
        public void PicksTheLargestThenDeepestEntry()
        {
            byte[] small = IconDib(1, 1, 32, Array.Empty<uint>(), (x, y) => 0xFF0000FFu, (x, y) => false);
            byte[] shallow = IconDib(2, 2, 4, new uint[] { 0x00FF0000 }, (x, y) => 0, (x, y) => false);
            byte[] deep = IconDib(2, 2, 32, Array.Empty<uint>(), (x, y) => 0xFF00FF00u, (x, y) => false);
            byte[] images = new byte[small.Length * 2 + shallow.Length + deep.Length];
            small.CopyTo(images, 0);
            shallow.CopyTo(images, small.Length);
            deep.CopyTo(images, small.Length + shallow.Length);
            small.CopyTo(images, small.Length + shallow.Length + deep.Length);
            Image image = Image.Load(Icon(1, images, small.Length, shallow.Length, deep.Length, small.Length));
            Assert.Equal(2, image.Width);
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, image.Pixels[0..4]);
        }

        [Fact]
        public void EntryOutsideTheFileIsNotRecognised()
        {
            byte[] dib = IconDib(1, 1, 32, Array.Empty<uint>(), (x, y) => 0, (x, y) => false);
            byte[] file = Icon(1, dib, dib.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(6 + 8), 1000);
            Assert.False(ImageCodec.TryIdentify(file, out _));
        }

        private static byte[] EncodeIco(params IconEntry[] entries)
        {
            MemoryStream output = new MemoryStream();
            ImageCodec.EncodeIco(entries, output);
            return output.ToArray();
        }

        // A square of colours whose alpha is 0 or 255, as an AND mask can hold it.
        private static Image Square(int size, PixelLayout layout = PixelLayout.RGBA8)
        {
            Image rgba = new Image(size, size);
            for (int i = 0; i < rgba.Pixels.Length; i += 4)
            {
                int c = i / 4 % 13;
                rgba.Pixels[i] = (byte)(c * 19);
                rgba.Pixels[i + 1] = (byte)(c * 7);
                rgba.Pixels[i + 2] = (byte)(255 - c * 11);
                rgba.Pixels[i + 3] = c == 4 ? (byte)0 : (byte)255;
            }
            if (layout == PixelLayout.RGBA8) return rgba;
            MemoryStream png = new MemoryStream();
            rgba.SavePng(png);
            return Image.Load(png.ToArray(), layout);
        }

        // The entry of a one-entry icon, from its directory.
        private static byte[] OnlyEntry(byte[] file) =>
            file[(int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(18))..(int)(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(18)) + BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(14)))];

        [Theory]
        [InlineData(IconEncoding.Png, BmpPixelFormat.Auto)]
        [InlineData(IconEncoding.Bmp, BmpPixelFormat.Auto)]
        [InlineData(IconEncoding.Bmp, BmpPixelFormat.Indexed4)]
        [InlineData(IconEncoding.Bmp, BmpPixelFormat.Indexed8)]
        [InlineData(IconEncoding.Bmp, BmpPixelFormat.Rgb24)]
        [InlineData(IconEncoding.Bmp, BmpPixelFormat.Xrgb32)]
        [InlineData(IconEncoding.Bmp, BmpPixelFormat.Argb32)]
        public void EncodeEntryRoundTrips(IconEncoding encoding, BmpPixelFormat format)
        {
            Image image = Square(19);
            byte[] file = EncodeIco(new IconEntry(image, encoding, format));
            Assert.Equal(image.Pixels, Image.Load(file).Pixels);
            Assert.Equal(19, file[6]);
            int bits = format == BmpPixelFormat.Indexed4 ? 4 : format == BmpPixelFormat.Indexed8 ? 8 : format == BmpPixelFormat.Rgb24 ? 24 : 32;
            Assert.Equal(bits, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6 + 6)));
            Assert.Equal(bits == 4 ? 16 : 0, file[6 + 2]);
        }

        [Fact]
        public void EncodeIndexedOneBitEntryRoundTrips()
        {
            Image image = new Image(9, 9);
            for (int i = 0; i < image.Pixels.Length; i += 4)
            {
                byte v = i / 4 % 3 == 0 ? (byte)255 : (byte)0;
                image.Pixels[i] = image.Pixels[i + 1] = image.Pixels[i + 2] = v;
                image.Pixels[i + 3] = i / 4 % 5 == 0 ? (byte)0 : (byte)255;
            }
            Assert.Equal(image.Pixels, Image.Load(EncodeIco(new IconEntry(image, IconEncoding.Bmp, BmpPixelFormat.Indexed1))).Pixels);
        }

        [Theory]
        [InlineData(PixelLayout.R8)]
        [InlineData(PixelLayout.RG8)]
        [InlineData(PixelLayout.BGRA8)]
        public void EncodePngEntriesAreRgba(PixelLayout layout)
        {
            Image image = Square(8, layout);
            byte[] entry = OnlyEntry(EncodeIco(new IconEntry(image)));
            Assert.Equal(6, entry[25]);
            Assert.Equal(image.Pixels, Image.Load(entry, layout).Pixels);
        }

        [Fact]
        public void EncodeKeepsEveryEntryInOrder()
        {
            Image small = Square(16), middle = Square(32), large = Square(256);
            byte[] file = EncodeIco(new IconEntry(small, IconEncoding.Bmp, BmpPixelFormat.Indexed8), new IconEntry(large), new IconEntry(middle, IconEncoding.Bmp));
            Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4)));
            Assert.Equal(new byte[] { 16, 0, 32 }, new[] { file[6], file[22], file[38] });
            Assert.Equal(large.Pixels, Image.Load(file).Pixels);
            for (int k = 0; k < 3; k++)
            {
                int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(6 + k * 16 + 8)), at = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(6 + k * 16 + 12));
                Assert.Equal(k == 0 ? 6 + 3 * 16 : (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(6 + (k - 1) * 16 + 12)) + (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(6 + (k - 1) * 16 + 8)), at);
                if (k == 2) Assert.Equal(file.Length, at + length);
            }
        }

        [Fact]
        public void EncodeCursorWritesHotspots()
        {
            MemoryStream output = new MemoryStream();
            ImageCodec.EncodeCur(new[] { new IconEntry(Square(32), hotspotX: 5, hotspotY: 31) }, output);
            byte[] file = output.ToArray();
            Assert.Equal(2, file[2]);
            Assert.Equal(5, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6 + 4)));
            Assert.Equal(31, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6 + 6)));
            Assert.Equal(Square(32).Pixels, Image.Load(file).Pixels);
        }

        [Fact]
        public void EncodeBadEntriesThrow()
        {
            Image image = Square(4);
            Assert.Throws<ArgumentException>(() => EncodeIco());
            Assert.Throws<ArgumentException>(() => EncodeIco(new IconEntry(new Image(257, 1))));
            Assert.Throws<ArgumentException>(() => EncodeIco(new IconEntry(image, IconEncoding.Png, BmpPixelFormat.Rgb24)));
            Assert.Throws<ArgumentException>(() => EncodeIco(new IconEntry(image, IconEncoding.Bmp, BmpPixelFormat.Rgb565)));
            Assert.Throws<ArgumentException>(() => EncodeIco(new IconEntry(image, hotspotX: 1)));
            Assert.Throws<ArgumentException>(() => ImageCodec.EncodeCur(new[] { new IconEntry(image, hotspotX: 4) }, new MemoryStream()));
        }
    }
}
