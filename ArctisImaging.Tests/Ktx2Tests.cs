using System;
using System.Buffers.Binary;
using System.IO;
using Xunit;

namespace Arctis.Imaging.Tests
{
    public class Ktx2Tests
    {
        private static Image Filled(int width, int height, PixelLayout layout, int seed)
        {
            Image image = new Image(width, height, layout);
            uint s = (uint)seed * 2654435761u + 1;
            for (int i = 0; i < image.Pixels.Length; i++)
            {
                s = s * 1664525u + 1013904223u;
                image.Pixels[i] = i % 7 == 0 ? (byte)(s >> 24) : (byte)(i / 5 * 3);
            }
            return image;
        }

        private static Image[] Chain(int width, int height, PixelLayout layout, bool full)
        {
            int count = 1;
            if (full)
                for (int w = width, h = height; w > 1 || h > 1; w = ImageOps.HalfSize(w), h = ImageOps.HalfSize(h)) count++;
            Image[] levels = new Image[count];
            for (int p = 0; p < count; p++)
            {
                levels[p] = Filled(width, height, layout, p + 1);
                width = ImageOps.HalfSize(width);
                height = ImageOps.HalfSize(height);
            }
            return levels;
        }

        private static byte[] Encode(Image[] levels, bool srgb, bool compress)
        {
            MemoryStream file = new MemoryStream();
            ImageCodec.EncodeKtx2(levels, srgb, compress, file);
            return file.ToArray();
        }

        private static byte[] Valid() => Encode(Chain(13, 6, PixelLayout.RGBA8, true), false, false);

        public static TheoryData<PixelLayout, bool, bool> Shapes()
        {
            TheoryData<PixelLayout, bool, bool> shapes = new TheoryData<PixelLayout, bool, bool>();
            PixelLayout[] layouts = { PixelLayout.R8, PixelLayout.RG8, PixelLayout.RGBA8, PixelLayout.BGRA8 };
            foreach (PixelLayout layout in layouts)
                for (int c = 0; c < 2; c++)
                    for (int f = 0; f < 2; f++) shapes.Add(layout, c == 1, f == 1);
            return shapes;
        }

        [Theory]
        [MemberData(nameof(Shapes))]
        public void RoundTripsEveryLayout(PixelLayout layout, bool compress, bool fullChain)
        {
            Image[] levels = Chain(37, 11, layout, fullChain);
            byte[] file = Encode(levels, layout == PixelLayout.RGBA8, compress);

            Assert.True(ImageCodec.TryReadKtx2(file, out Ktx2Info info));
            Assert.Equal(layout, info.Layout);
            Assert.Equal(layout == PixelLayout.RGBA8, info.Srgb);
            Assert.Equal(37, info.Width);
            Assert.Equal(11, info.Height);
            Assert.Equal(levels.Length, info.Levels);
            for (int p = 0; p < levels.Length; p++)
            {
                byte[] decoded = new byte[levels[p].Pixels.Length];
                ImageCodec.DecodeKtx2Level(file, p, decoded, new DecodeOptions(layout));
                Assert.Equal(levels[p].Pixels, decoded);
            }
            Assert.Equal(levels[0].Pixels, Image.Load(file, layout).Pixels);
        }

        [Fact]
        public void DecodeConvertsLayouts()
        {
            Image grey = Filled(5, 3, PixelLayout.R8, 4);
            Image rgba = Image.Load(Encode(new[] { grey }, false, true), PixelLayout.RGBA8);
            for (int i = 0; i < grey.Pixels.Length; i++)
            {
                byte g = grey.Pixels[i];
                Assert.Equal(new byte[] { g, g, g, 255 }, rgba.Pixels.AsSpan(i * 4, 4).ToArray());
            }

            Image colour = Filled(4, 4, PixelLayout.RGBA8, 5);
            byte[] bgra = new byte[colour.Pixels.Length];
            ImageCodec.DecodeKtx2Level(Encode(new[] { colour }, false, false), 0, bgra, new DecodeOptions(PixelLayout.BGRA8));
            ImageOps.SwizzleBgraToRgba(bgra);
            Assert.Equal(colour.Pixels, bgra);
        }

        [Fact]
        public void DecodeHonoursRowPitch()
        {
            Image image = Filled(3, 2, PixelLayout.RGBA8, 6);
            byte[] destination = new byte[16 + 12];
            Array.Fill(destination, (byte)0xAB);
            ImageCodec.DecodeKtx2Level(Encode(new[] { image }, false, true), 0, destination, new DecodeOptions(PixelLayout.RGBA8, 16));
            Assert.Equal(image.Pixels.AsSpan(0, 12).ToArray(), destination.AsSpan(0, 12).ToArray());
            Assert.Equal(0xAB, destination[12]);
            Assert.Equal(image.Pixels.AsSpan(12, 12).ToArray(), destination.AsSpan(16, 12).ToArray());
        }

        // A 2x1 R8G8B8A8_UNORM file assembled field by field from the KTX 2.0 specification.
        [Fact]
        public void WriterMatchesAFileBuiltFromTheSpecification()
        {
            byte[] expected = new byte[80 + 24 + 92 + 32 + 8];
            Span<byte> f = expected;
            new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(f);
            uint[] header = { 37, 1, 2, 1, 0, 0, 1, 1, 0, 104, 92, 196, 32 };
            for (int i = 0; i < header.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(f.Slice(12 + 4 * i), header[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(f.Slice(80), 228);
            BinaryPrimitives.WriteUInt64LittleEndian(f.Slice(88), 8);
            BinaryPrimitives.WriteUInt64LittleEndian(f.Slice(96), 8);

            Span<byte> dfd = f.Slice(104);
            BinaryPrimitives.WriteUInt32LittleEndian(dfd, 92);
            BinaryPrimitives.WriteUInt32LittleEndian(dfd.Slice(8), 2 | 88 << 16);
            dfd[12] = 1;
            dfd[13] = 1;
            dfd[14] = 1;
            dfd[20] = 4;
            byte[] channels = { 0, 1, 2, 15 };
            for (int k = 0; k < 4; k++)
            {
                Span<byte> sample = dfd.Slice(28 + 16 * k);
                sample[0] = (byte)(8 * k);
                sample[2] = 7;
                sample[3] = channels[k];
                sample[12] = 255;
            }

            Span<byte> kvd = f.Slice(196);
            BinaryPrimitives.WriteUInt32LittleEndian(kvd, 25);
            "KTXwriter\0Arctis.Imaging\0"u8.CopyTo(kvd.Slice(4));

            byte[] pixels = { 10, 20, 30, 40, 50, 60, 70, 80 };
            pixels.CopyTo(f.Slice(228));
            Image image = new Image(2, 1);
            pixels.CopyTo(image.Pixels, 0);
            Assert.Equal(expected, Encode(new[] { image }, false, false));
        }

        [Fact]
        public void OtherDataIsNotKtx2()
        {
            Assert.False(ImageCodec.TryReadKtx2(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, out _));
            byte[] file = Valid();
            file[1] = 0;
            Assert.False(ImageCodec.TryIdentify(file, out _));
        }

        [Fact]
        public void EveryTruncationIsRejected()
        {
            byte[] file = Valid();
            for (int length = 12; length < file.Length; length++)
            {
                byte[] cut = file.AsSpan(0, length).ToArray();
                Assert.Throws<ImageFormatException>(() => ImageCodec.TryReadKtx2(cut, out _));
            }
        }

        [Theory]
        [InlineData(12, 131u)]
        [InlineData(28, 4u)]
        [InlineData(32, 2u)]
        [InlineData(36, 6u)]
        [InlineData(44, 2u)]
        [InlineData(44, 1u)]
        public void UnsupportedFilesThrowNotSupported(int field, uint value)
        {
            byte[] file = Valid();
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(field), value);
            Assert.Throws<NotSupportedException>(() => ImageCodec.TryReadKtx2(file, out _));
            Assert.Throws<NotSupportedException>(() => Image.Load(file));
        }

        [Theory]
        [InlineData(16, 4u)]
        [InlineData(20, 0u)]
        [InlineData(40, 9u)]
        public void MalformedHeadersAreRejected(int field, uint value)
        {
            byte[] file = Valid();
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(field), value);
            Assert.Throws<ImageFormatException>(() => ImageCodec.TryReadKtx2(file, out _));
        }

        [Fact]
        public void LevelsOutsideTheFileAreRejected()
        {
            byte[] file = Valid();
            BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(80), (ulong)file.Length);
            Assert.Throws<ImageFormatException>(() => ImageCodec.TryReadKtx2(file, out _));
        }

        [Fact]
        public void LevelsMustHalve()
        {
            Image[] levels = { new Image(8, 8), new Image(3, 4) };
            Assert.Throws<ArgumentException>(() => Encode(levels, false, false));
        }
    }
}
