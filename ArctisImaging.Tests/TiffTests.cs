using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Xunit;

namespace Arctis.Imaging.Tests
{
    public class TiffTests
    {
        private struct Tag
        {
            public int tag, type;
            public uint[] values;

            public Tag(int tag, int type, params uint[] values)
            {
                this.tag = tag;
                this.type = type;
                this.values = values;
            }
        }

        // A one-strip file.
        private static byte[] Tiff(bool bigEndian, byte[] strip, params Tag[] tags) => Tiff(bigEndian, strip, new[] { 0, strip.Length }, false, tags);

        // Header, chunk data, the first IFD, then values too large to sit in their entries; chunk k is data[starts[k]..starts[k + 1]].
        private static byte[] Tiff(bool bigEndian, byte[] data, int[] starts, bool tiled, params Tag[] tags)
        {
            List<Tag> all = new List<Tag>(tags.Length + 2);
            all.AddRange(tags);
            int chunks = starts.Length - 1;
            uint[] offsets = new uint[chunks], counts = new uint[chunks];
            for (int i = 0; i < chunks; i++)
            {
                offsets[i] = 8 + (uint)starts[i];
                counts[i] = (uint)(starts[i + 1] - starts[i]);
            }
            uint at = 8 + (uint)data.Length;
            all.Add(new Tag(tiled ? 324 : 273, 4, offsets));
            all.Add(new Tag(tiled ? 325 : 279, 4, counts));
            all.Sort((a, b) => a.tag.CompareTo(b.tag));

            uint ifd = (at + 1) & ~1u;
            uint extra = ifd + 2 + (uint)all.Count * 12 + 4;
            MemoryStream file = new MemoryStream();
            byte[] scratch = new byte[4];
            void U16(long position, int v)
            {
                file.Position = position;
                if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(scratch, (ushort)v);
                else BinaryPrimitives.WriteUInt16LittleEndian(scratch, (ushort)v);
                file.Write(scratch, 0, 2);
            }
            void U32(long position, uint v)
            {
                file.Position = position;
                if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(scratch, v);
                else BinaryPrimitives.WriteUInt32LittleEndian(scratch, v);
                file.Write(scratch, 0, 4);
            }

            file.Write(bigEndian ? new byte[] { (byte)'M', (byte)'M' } : new byte[] { (byte)'I', (byte)'I' });
            U16(2, 42);
            U32(4, ifd);
            file.Position = 8;
            file.Write(data);
            U16(ifd, all.Count);
            for (int e = 0; e < all.Count; e++)
            {
                long entry = ifd + 2 + e * 12;
                Tag t = all[e];
                int size = t.type == 3 ? 2 : 4;
                U16(entry, t.tag);
                U16(entry + 2, t.type);
                U32(entry + 4, (uint)t.values.Length);
                long valueAt = entry + 8;
                if (t.values.Length * size > 4)
                {
                    U32(entry + 8, extra);
                    valueAt = extra;
                    extra += (uint)(t.values.Length * size);
                }
                for (int v = 0; v < t.values.Length; v++)
                    if (size == 2) U16(valueAt + v * 2, (int)t.values[v]);
                    else U32(valueAt + v * 4, t.values[v]);
            }
            U32(ifd + 2 + all.Count * 12, 0);
            return file.ToArray();
        }

        private static Tag[] Basic(int width, int height, int samples, int photometric, int compression, params Tag[] more)
        {
            uint[] bits = new uint[samples];
            for (int s = 0; s < samples; s++) bits[s] = 8;
            Tag[] tags = new Tag[6 + more.Length];
            tags[0] = new Tag(256, 4, (uint)width);
            tags[1] = new Tag(257, 4, (uint)height);
            tags[2] = new Tag(258, 3, bits);
            tags[3] = new Tag(259, 3, (uint)compression);
            tags[4] = new Tag(262, 3, (uint)photometric);
            tags[5] = new Tag(277, 3, (uint)samples);
            more.CopyTo(tags, 6);
            return tags;
        }

        // Deterministic RGB samples for a width x height image.
        private static byte[] Samples(int width, int height, int samples)
        {
            byte[] data = new byte[width * height * samples];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 37 + i / 7);
            return data;
        }

        private static void AssertRgb(Image image, byte[] samples)
        {
            for (int p = 0; p < image.Width * image.Height; p++)
            {
                Assert.Equal(samples[p * 3], image.Pixels[p * 4]);
                Assert.Equal(samples[p * 3 + 1], image.Pixels[p * 4 + 1]);
                Assert.Equal(samples[p * 3 + 2], image.Pixels[p * 4 + 2]);
                Assert.Equal(255, image.Pixels[p * 4 + 3]);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void UncompressedRgbEitherByteOrder(bool bigEndian)
        {
            byte[] samples = Samples(5, 3, 3);
            byte[] file = Tiff(bigEndian, samples, Basic(5, 3, 3, 2, 1));
            Assert.True(ImageCodec.TryIdentify(file, out ImageHeader header));
            Assert.Equal(ImageFormat.Tiff, header.Format);
            Assert.Equal(5, header.Width);
            AssertRgb(Image.Load(file), samples);
        }

        [Fact]
        public void SeveralStrips()
        {
            byte[] samples = Samples(4, 5, 3);
            byte[] file = Tiff(false, samples, new[] { 0, 24, 48, samples.Length }, false, Basic(4, 5, 3, 2, 1, new Tag(278, 3, 2)));
            AssertRgb(Image.Load(file), samples);
        }

        [Fact]
        public void TilesClipAtTheEdges()
        {
            int width = 5, height = 3, tileWidth = 4, tileHeight = 2;
            byte[] samples = Samples(width, height, 3);
            int tileBytes = tileWidth * tileHeight * 3;
            byte[] tiles = new byte[4 * tileBytes];
            int[] starts = { 0, tileBytes, 2 * tileBytes, 3 * tileBytes, 4 * tileBytes };
            for (int t = 0; t < 4; t++)
            {
                int x0 = t % 2 * tileWidth, y0 = t / 2 * tileHeight;
                for (int y = 0; y < tileHeight; y++)
                    for (int x = 0; x < tileWidth; x++)
                        if (x0 + x < width && y0 + y < height)
                            Array.Copy(samples, ((y0 + y) * width + x0 + x) * 3, tiles, starts[t] + (y * tileWidth + x) * 3, 3);
            }
            byte[] file = Tiff(false, tiles, starts, true, Basic(width, height, 3, 2, 1, new Tag(322, 3, (uint)tileWidth), new Tag(323, 3, (uint)tileHeight)));
            AssertRgb(Image.Load(file), samples);
        }

        [Fact]
        public void PackBits()
        {
            // a literal of 2, a run of 4, a no-op, a literal of 3 = 9 grey samples
            byte[] packed = { 1, 10, 20, 0xFD, 30, 0x80, 2, 40, 50, 60 };
            byte[] file = Tiff(false, packed, Basic(3, 3, 1, 1, 32773));
            Image image = Image.Load(file, PixelLayout.R8);
            Assert.Equal(new byte[] { 10, 20, 30, 30, 30, 30, 40, 50, 60 }, image.Pixels);
        }

        [Fact]
        public void LzwWidensPastNineBits()
        {
            byte[] samples = Samples(40, 30, 3);
            byte[] file = Tiff(false, Lzw(samples), Basic(40, 30, 3, 2, 5));
            AssertRgb(Image.Load(file), samples);
        }

        [Theory]
        [InlineData(8)]
        [InlineData(32946)]
        public void Deflate(int compression)
        {
            byte[] samples = Samples(9, 7, 3);
            MemoryStream zlib = new MemoryStream();
            using (ZLibStream stream = new ZLibStream(zlib, CompressionLevel.Optimal, true)) stream.Write(samples);
            byte[] file = Tiff(false, zlib.ToArray(), Basic(9, 7, 3, 2, compression));
            AssertRgb(Image.Load(file), samples);
        }

        [Fact]
        public void HorizontalPredictor()
        {
            int width = 6, height = 2;
            byte[] samples = Samples(width, height, 3);
            byte[] differenced = (byte[])samples.Clone();
            for (int y = 0; y < height; y++)
                for (int i = width * 3 - 1; i >= 3; i--) differenced[y * width * 3 + i] -= samples[y * width * 3 + i - 3];
            byte[] file = Tiff(false, Lzw(differenced), Basic(width, height, 3, 2, 5, new Tag(317, 3, 2)));
            AssertRgb(Image.Load(file), samples);
        }

        [Fact]
        public void PaletteAndWhiteIsZero()
        {
            uint[] map = new uint[768];
            map[3] = 0xFF00;
            map[256 + 3] = 0x8000;
            map[512 + 3] = 0x0100;
            byte[] palette = Tiff(false, new byte[] { 3, 0 }, Basic(2, 1, 1, 3, 1, new Tag(320, 3, map)));
            Image image = Image.Load(palette);
            Assert.Equal(new byte[] { 255, 128, 1, 255, 0, 0, 0, 255 }, image.Pixels);

            byte[] grey = Tiff(false, new byte[] { 0, 200 }, Basic(2, 1, 1, 0, 1));
            Assert.Equal(new byte[] { 255, 55 }, Image.Load(grey, PixelLayout.R8).Pixels);
        }

        [Fact]
        public void AlphaStraightAndPremultiplied()
        {
            byte[] stored = { 10, 20, 30, 51, 0, 0, 0, 0, 7, 8, 9, 255 };
            byte[] straight = Tiff(false, stored, Basic(3, 1, 4, 2, 1, new Tag(338, 3, 2)));
            Assert.Equal(stored, Image.Load(straight).Pixels);

            byte[] premultiplied = Tiff(false, stored, Basic(3, 1, 4, 2, 1, new Tag(338, 3, 1)));
            Assert.Equal(new byte[] { 50, 100, 150, 51, 0, 0, 0, 0, 7, 8, 9, 255 }, Image.Load(premultiplied).Pixels);
        }

        // Basic, with every sample at the given depth.
        private static Tag[] Deep(int bits, int width, int height, int samples, int photometric, int compression, params Tag[] more)
        {
            Tag[] tags = Basic(width, height, samples, photometric, compression, more);
            uint[] depths = new uint[samples];
            for (int s = 0; s < samples; s++) depths[s] = (uint)bits;
            tags[2] = new Tag(258, 3, depths);
            return tags;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SixteenBitKeepsTheHighByte(bool bigEndian)
        {
            byte[] high = Samples(3, 2, 3);
            byte[] stored = new byte[high.Length * 2];
            for (int i = 0; i < high.Length; i++)
            {
                stored[i * 2 + (bigEndian ? 0 : 1)] = high[i];
                stored[i * 2 + (bigEndian ? 1 : 0)] = (byte)(i * 11);
            }
            AssertRgb(Image.Load(Tiff(bigEndian, stored, Deep(16, 3, 2, 3, 2, 1))), high);
        }

        [Fact]
        public void HorizontalPredictorOnSixteenBit()
        {
            int width = 4;
            ushort[] values = new ushort[width * 3];
            for (int i = 0; i < values.Length; i++) values[i] = (ushort)(i * 7919 + 300);
            byte[] differenced = new byte[values.Length * 2];
            for (int i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(differenced.AsSpan(i * 2), (ushort)(i < 3 ? values[i] : values[i] - values[i - 3]));
            byte[] expected = new byte[values.Length];
            for (int i = 0; i < values.Length; i++) expected[i] = (byte)(values[i] >> 8);
            AssertRgb(Image.Load(Tiff(false, differenced, Deep(16, width, 1, 3, 2, 1, new Tag(317, 3, 2)))), expected);
        }

        [Fact]
        public void SmallSamplesUnpackAndScale()
        {
            // 1-bit black-is-zero, ten pixels a row, rows padded to a byte
            byte[] bilevel = Tiff(false, new byte[] { 0b1010_0000, 0b0100_0000, 0b0000_0000, 0b1100_0000 }, Deep(1, 10, 2, 1, 1, 1));
            Assert.Equal(new byte[] { 255, 0, 255, 0, 0, 0, 0, 0, 0, 255, 0, 0, 0, 0, 0, 0, 0, 0, 255, 255 }, Image.Load(bilevel, PixelLayout.R8).Pixels);

            byte[] twoBit = Tiff(false, new byte[] { 0b00_01_10_11 }, Deep(2, 4, 1, 1, 1, 1));
            Assert.Equal(new byte[] { 0, 85, 170, 255 }, Image.Load(twoBit, PixelLayout.R8).Pixels);

            uint[] map = new uint[3 * 16];
            map[5] = 0xFF00;
            map[16 + 5] = 0x8000;
            map[32 + 5] = 0x0100;
            byte[] palette = Tiff(false, new byte[] { 0x50, 0x50 }, Deep(4, 3, 1, 1, 3, 1, new Tag(320, 3, map)));
            Assert.Equal(new byte[] { 255, 128, 1, 255, 0, 0, 0, 255, 255, 128, 1, 255 }, Image.Load(palette).Pixels);
        }

        [Fact]
        public void CmykInvertsTheInks()
        {
            byte[] stored = { 0, 0, 0, 0, 255, 0, 0, 0, 0, 0, 0, 255, 100, 50, 25, 128 };
            byte[] file = Tiff(false, stored, Basic(4, 1, 4, 5, 1));
            Assert.Equal(new byte[] { 255, 255, 255, 255, 0, 255, 255, 255, 0, 0, 0, 255, 77, 102, 115, 255 }, Image.Load(file).Pixels);
        }

        [Fact]
        public void PlanarRgbInterleavesThePlanes()
        {
            int width = 3, height = 2;
            byte[] samples = Samples(width, height, 3);
            byte[] planes = new byte[samples.Length];
            for (int p = 0; p < 3; p++)
                for (int i = 0; i < width * height; i++) planes[p * width * height + i] = samples[i * 3 + p];
            int[] starts = { 0, 3, 6, 9, 12, 15, 18 };
            byte[] file = Tiff(false, planes, starts, false, Basic(width, height, 3, 2, 1, new Tag(278, 3, 1), new Tag(284, 3, 2)));
            AssertRgb(Image.Load(file), samples);
        }

        [Fact]
        public void FillOrderTwoReversesTheBits()
        {
            byte[] file = Tiff(false, new byte[] { 0b0000_1101 }, Deep(1, 8, 1, 1, 1, 1, new Tag(266, 3, 2)));
            Assert.Equal(new byte[] { 255, 0, 255, 255, 0, 0, 0, 0 }, Image.Load(file, PixelLayout.R8).Pixels);
        }

        // libtiff 4 through Pillow 13: three 8-row strips of JPEG with shared JPEGTables, photometric RGB and YCbCr;
        // each .png beside a .tif is Pillow's decode of it.
        [Theory]
        [InlineData("JpegRgb")]
        [InlineData("JpegYcc")]
        public void JpegStripsMatchPillow(string name)
        {
            Image expected = Image.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".png")));
            Image actual = Image.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".tif")));
            Assert.Equal(expected.Width, actual.Width);
            Assert.Equal(expected.Height, actual.Height);
            int worst = 0;
            for (int i = 0; i < expected.Pixels.Length; i++) worst = Math.Max(worst, Math.Abs(expected.Pixels[i] - actual.Pixels[i]));
            Assert.True(worst <= 1, $"off by {worst}");
        }

        [Fact]
        public void UnsupportedVariantsThrow()
        {
            byte[] fax = Tiff(false, new byte[3], Deep(1, 1, 1, 1, 0, 4));
            Assert.True(ImageCodec.TryIdentify(fax, out _));
            Assert.Throws<NotSupportedException>(() => Image.Load(fax));

            Assert.Throws<NotSupportedException>(() => Image.Load(Tiff(false, new byte[3], Basic(1, 1, 3, 6, 1))));
            Assert.Throws<NotSupportedException>(() => Image.Load(Tiff(false, new byte[3], Basic(1, 1, 3, 2, 6))));
            Assert.Throws<NotSupportedException>(() => Image.Load(Tiff(false, new byte[12], Deep(32, 1, 1, 3, 2, 1, new Tag(339, 3, 3)))));
        }

        [Fact]
        public void ShortStripThrows()
        {
            byte[] file = Tiff(false, new byte[10], Basic(2, 2, 3, 2, 1));
            Assert.Throws<ImageFormatException>(() => Image.Load(file));
        }

        // TIFF LZW: a clear first, MSB-first codes, one bit wider once the table reaches the next power of two.
        private static byte[] Lzw(byte[] data)
        {
            Dictionary<int, int> table = new Dictionary<int, int>();
            List<byte> output = new List<byte>(data.Length);
            ulong buffer = 0;
            int buffered = 0, width = 9, next = 258;
            void Emit(int code)
            {
                buffer = buffer << width | (uint)code;
                buffered += width;
                while (buffered >= 8)
                {
                    output.Add((byte)(buffer >> (buffered - 8)));
                    buffered -= 8;
                }
            }

            Emit(256);
            int current = data[0];
            for (int i = 1; i < data.Length; i++)
            {
                int key = current << 8 | data[i];
                if (table.TryGetValue(key, out int code))
                {
                    current = code;
                    continue;
                }
                Emit(current);
                table[key] = next++;
                if (next == 1 << width) width++;
                current = data[i];
            }
            Emit(current);
            if (++next == 1 << width) width++;
            Emit(257);
            if (buffered > 0) output.Add((byte)(buffer << (8 - buffered)));
            return output.ToArray();
        }
    }
}
