using System;
using System.Buffers.Binary;
using System.IO;
using Xunit;

namespace Arctis.Imaging.Tests
{
    // Fixtures are PCX files written by Pillow 13 with Pillow's decode beside them; EGA planar files are built here.
    public class PcxTests
    {
        private static byte[] Fixture(string file) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));

        private static Image Load(byte[] file) => Image.Load(file, PixelLayout.RGBA8, default, ImageFormat.Pcx);

        [Theory]
        [InlineData("PcxRgb")]
        [InlineData("PcxGrey")]
        [InlineData("PcxPalette")]
        [InlineData("PcxBits")]
        public void MatchesPillow(string name) => Assert.Equal(Image.Load(Fixture(name + ".png")).Pixels, Load(Fixture(name + ".pcx")).Pixels);

        [Theory]
        [InlineData("PcxRgb")]
        [InlineData("PcxGrey")]
        [InlineData("PcxPalette")]
        [InlineData("PcxBits")]
        public void MatchesPillowInBgra(string name)
            => Assert.Equal(Image.Load(Fixture(name + ".png"), PixelLayout.BGRA8).Pixels, Image.Load(Fixture(name + ".pcx"), PixelLayout.BGRA8, default, ImageFormat.Pcx).Pixels);

        [Theory]
        [InlineData(3, false, PixelLayout.RGBA8)]
        [InlineData(3, false, PixelLayout.BGRA8)]
        [InlineData(4, false, PixelLayout.RGBA8)]
        [InlineData(4, false, PixelLayout.BGRA8)]
        [InlineData(1, false, PixelLayout.RGBA8)]
        [InlineData(1, false, PixelLayout.BGRA8)]
        [InlineData(1, true, PixelLayout.BGRA8)]
        public void OddWidthRowsDecodeExactly(int planes, bool greyRamp, PixelLayout layout)
        {
            // 117 = 64 + 32 + 16 + 5 pixels, so every vector width and the scalar tail run; plain bytes, no RLE
            const int W = 117, H = 3, Bpl = 118;
            int raster = Bpl * planes * H;
            byte[] palette = new byte[768];
            for (int i = 0; i < palette.Length; i++) palette[i] = greyRamp ? (byte)(i / 3) : (byte)(i * 5 + 1);
            byte[] file = new byte[128 + raster + (planes == 1 ? 769 : 0)];
            file[0] = 0x0A;
            file[1] = 5;
            file[3] = 8;
            file[65] = (byte)planes;
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8), W - 1);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(10), H - 1);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(66), Bpl);
            for (int i = 128; i < 128 + raster; i++) file[i] = (byte)(i * 7 + 3);
            if (planes == 1)
            {
                file[128 + raster] = 0x0C;
                palette.CopyTo(file, 128 + raster + 1);
            }

            byte[] expected = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    for (int k = 0; k < 4; k++)
                    {
                        int line = 128 + y * Bpl * planes;
                        byte v = k == 3 && planes < 4 ? (byte)255 : planes == 1 ? palette[file[line + x] * 3 + k] : file[line + k * Bpl + x];
                        expected[(y * W + x) * 4 + (layout == PixelLayout.BGRA8 && k < 3 ? 2 - k : k)] = v;
                    }
            Assert.Equal(expected, Image.Load(file, layout, default, ImageFormat.Pcx).Pixels);
        }

        [Theory]
        [InlineData(1, 1, PixelLayout.RGBA8, false)]
        [InlineData(1, 1, PixelLayout.BGRA8, false)]
        [InlineData(1, 1, PixelLayout.R8, false)]
        [InlineData(1, 1, PixelLayout.RG8, false)]
        [InlineData(1, 2, PixelLayout.RGBA8, false)]
        [InlineData(1, 2, PixelLayout.BGRA8, false)]
        [InlineData(1, 2, PixelLayout.R8, false)]
        [InlineData(1, 2, PixelLayout.RG8, false)]
        [InlineData(1, 3, PixelLayout.RGBA8, false)]
        [InlineData(1, 3, PixelLayout.BGRA8, false)]
        [InlineData(1, 3, PixelLayout.R8, false)]
        [InlineData(1, 3, PixelLayout.RG8, false)]
        [InlineData(1, 4, PixelLayout.RGBA8, false)]
        [InlineData(1, 4, PixelLayout.BGRA8, false)]
        [InlineData(1, 4, PixelLayout.R8, false)]
        [InlineData(1, 4, PixelLayout.RG8, false)]
        [InlineData(2, 1, PixelLayout.RGBA8, false)]
        [InlineData(2, 1, PixelLayout.BGRA8, false)]
        [InlineData(2, 1, PixelLayout.R8, false)]
        [InlineData(2, 1, PixelLayout.RG8, false)]
        [InlineData(4, 1, PixelLayout.RGBA8, false)]
        [InlineData(4, 1, PixelLayout.BGRA8, false)]
        [InlineData(4, 1, PixelLayout.R8, false)]
        [InlineData(4, 1, PixelLayout.RG8, false)]
        [InlineData(1, 1, PixelLayout.BGRA8, true)]
        [InlineData(1, 4, PixelLayout.BGRA8, true)]
        [InlineData(2, 1, PixelLayout.BGRA8, true)]
        [InlineData(4, 1, PixelLayout.BGRA8, true)]
        public void SubByteRowsDecodeExactly(int bits, int planes, PixelLayout layout, bool rle)
        {
            // 245 = 3 * 64 + 32 + 16 + 5 pixels, so every lookup width and the scalar tail run; RLE runs of 50 cross scanlines
            const int W = 245, H = 3;
            int bpl = (W * bits + 15) / 16 * 2, lineBytes = bpl * planes;
            byte[] raster = new byte[lineBytes * H];
            for (int i = 0; i < raster.Length; i++) raster[i] = rle ? (byte)(i / 50 * 37 + 5) : (byte)(i * 7 + 3);
            byte[] body = raster;
            if (rle)
            {
                body = new byte[raster.Length * 2];
                int n = 0;
                for (int i = 0; i < raster.Length;)
                {
                    int run = 1;
                    while (i + run < raster.Length && run < 63 && raster[i + run] == raster[i]) run++;
                    body[n++] = (byte)(0xC0 | run);
                    body[n++] = raster[i];
                    i += run;
                }
                Array.Resize(ref body, n);
            }

            byte[] file = new byte[128 + body.Length];
            file[0] = 0x0A;
            file[1] = 5;
            file[2] = (byte)(rle ? 1 : 0);
            file[3] = (byte)bits;
            for (int i = 0; i < 48; i++) file[16 + i] = (byte)(i * 13 + 7);
            file[65] = (byte)planes;
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8), W - 1);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(10), H - 1);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(66), (ushort)bpl);
            body.CopyTo(file, 128);

            int size = layout == PixelLayout.R8 ? 1 : layout == PixelLayout.RG8 ? 2 : 4;
            byte[] expected = new byte[W * H * size];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int line = y * lineBytes, index = 0;
                    if (bits == 1)
                        for (int p = 0; p < planes; p++) index |= (raster[line + p * bpl + x / 8] >> (7 - x % 8) & 1) << p;
                    else index = raster[line + x * bits / 8] >> (8 - bits - x * bits % 8) & ((1 << bits) - 1);
                    bool mono = bits == 1 && planes == 1;
                    byte r = mono ? (byte)(index * 255) : file[16 + index * 3], g = mono ? r : file[17 + index * 3], b = mono ? r : file[18 + index * 3];
                    int at = (y * W + x) * size;
                    if (size < 4)
                    {
                        expected[at] = PixelRows.Luma(r, g, b);
                        if (size == 2) expected[at + 1] = 255;
                        continue;
                    }
                    expected[at] = layout == PixelLayout.BGRA8 ? b : r;
                    expected[at + 1] = g;
                    expected[at + 2] = layout == PixelLayout.BGRA8 ? r : b;
                    expected[at + 3] = 255;
                }
            Assert.Equal(expected, Image.Load(file, layout, default, ImageFormat.Pcx).Pixels);
        }

        [Fact]
        public void IsRecognisedOnlyWithTheHint()
        {
            byte[] file = Fixture("PcxRgb.pcx");
            Assert.False(ImageCodec.TryIdentify(file, out _));
            Assert.True(ImageCodec.TryIdentify(file, out ImageHeader header, ImageFormat.Pcx));
            Assert.Equal(ImageFormat.Pcx, header.Format);
        }

        [Fact]
        public void FourPlaneEgaUsesTheHeaderPalette()
        {
            // 2x1, one byte per plane line; pixel 0 is index 0b0101, pixel 1 index 0b1010; plain bytes, no RLE
            byte[] file = new byte[128 + 4];
            file[0] = 0x0A;
            file[1] = 2;
            file[2] = 0;
            file[3] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8), 1);
            file[16 + 5 * 3] = 50;
            file[16 + 5 * 3 + 1] = 60;
            file[16 + 5 * 3 + 2] = 70;
            file[16 + 10 * 3 + 2] = 200;
            file[65] = 4;
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(66), 1);
            file[128] = 0x80;
            file[129] = 0x40;
            file[130] = 0x80;
            file[131] = 0x40;
            Assert.Equal(new byte[] { 50, 60, 70, 255, 0, 0, 200, 255 }, Load(file).Pixels);
        }

        [Fact]
        public void TruncatedScanlinesThrow()
        {
            byte[] file = Fixture("PcxRgb.pcx");
            Assert.Throws<ImageFormatException>(() => Load(file.AsSpan(0, 200).ToArray()));
        }
    }
}
