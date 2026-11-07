using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Xunit;

namespace Arctis.Imaging.Tests
{
    public class GifTests
    {
        private sealed class Frame
        {
            public int left, top, width, height, transparent = -1, minimum;
            public byte[] indices = Array.Empty<byte>();
            public byte[]? local;
            public bool interlaced, deferredClear, extensions;
        }

        private static byte[] Palette(int count, int seed = 0)
        {
            byte[] table = new byte[count * 3];
            for (int i = 0; i < count; i++)
            {
                table[i * 3] = (byte)(i * 37 + seed);
                table[i * 3 + 1] = (byte)(i * 91 + 17);
                table[i * 3 + 2] = (byte)(i * 53 + 101);
            }
            return table;
        }

        private static uint Hash(int x, int y)
        {
            uint v = (uint)x * 73856093u ^ (uint)y * 19349663u;
            v ^= v >> 13;
            v *= 0x5BD1E995u;
            return v ^ v >> 15;
        }

        // Random indices over the left part of each row, long runs over the right part.
        private static byte[] Pattern(int width, int height, int colours)
        {
            byte[] indices = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    indices[y * width + x] = (byte)(x < width / 2 ? Hash(x, y) % (uint)colours : (uint)((y / 3) % colours));
            return indices;
        }

        private static byte[] Gif(int width, int height, byte[]? global, Frame frame)
        {
            List<byte> file = new List<byte>(1024);
            file.AddRange("GIF89a"u8.ToArray());
            AddUInt16(file, width);
            AddUInt16(file, height);
            file.Add((byte)(global == null ? 0 : 0x80 | SizeBits(global.Length / 3)));
            file.Add(0);
            file.Add(0);
            if (global != null) file.AddRange(global);
            if (frame.extensions)
            {
                file.AddRange(new byte[] { 0x21, 0xFE, 5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0 });
                file.AddRange(new byte[] { 0x21, 0xFF, 11 });
                file.AddRange("NETSCAPE2.0"u8.ToArray());
                file.AddRange(new byte[] { 3, 1, 0, 0, 0 });
            }
            if (frame.transparent >= 0) file.AddRange(new byte[] { 0x21, 0xF9, 4, 1, 0, 0, (byte)frame.transparent, 0 });

            file.Add(0x2C);
            AddUInt16(file, frame.left);
            AddUInt16(file, frame.top);
            AddUInt16(file, frame.width);
            AddUInt16(file, frame.height);
            file.Add((byte)((frame.local == null ? 0 : 0x80 | SizeBits(frame.local.Length / 3)) | (frame.interlaced ? 0x40 : 0)));
            if (frame.local != null) file.AddRange(frame.local);

            byte[] stored = frame.interlaced ? Interlace(frame.indices, frame.width, frame.height) : frame.indices;
            byte[] lzw = Lzw(stored, frame.minimum, frame.deferredClear);
            file.Add((byte)frame.minimum);
            for (int at = 0; at < lzw.Length; at += 255)
            {
                int size = Math.Min(255, lzw.Length - at);
                file.Add((byte)size);
                for (int i = 0; i < size; i++) file.Add(lzw[at + i]);
            }
            file.Add(0);
            file.Add(0x3B);
            return file.ToArray();
        }

        private static int SizeBits(int count)
        {
            int bits = 0;
            while (2 << bits < count) bits++;
            return bits;
        }

        private static void AddUInt16(List<byte> file, int value)
        {
            file.Add((byte)value);
            file.Add((byte)(value >> 8));
        }

        private static byte[] Interlace(byte[] indices, int width, int height)
        {
            byte[] stored = new byte[indices.Length];
            int row = 0;
            int[] starts = { 0, 4, 2, 1 }, steps = { 8, 8, 4, 2 };
            for (int pass = 0; pass < 4; pass++)
                for (int y = starts[pass]; y < height; y += steps[pass])
                    Array.Copy(indices, y * width, stored, row++ * width, width);
            return stored;
        }

        // A reference GIF LZW encoder; a full table either clears or, deferred, keeps coding without new entries.
        private static byte[] Lzw(byte[] indices, int minimum, bool deferredClear)
        {
            List<byte> output = new List<byte>(indices.Length);
            int clear = 1 << minimum, codeSize = minimum + 1, next = clear + 2, bitCount = 0;
            ulong bits = 0;
            Dictionary<int, int> codes = new Dictionary<int, int>(4096);

            void Emit(int code)
            {
                bits |= (ulong)code << bitCount;
                bitCount += codeSize;
                while (bitCount >= 8)
                {
                    output.Add((byte)bits);
                    bits >>= 8;
                    bitCount -= 8;
                }
            }

            void Grow()
            {
                next++;
                if (next > 1 << codeSize && codeSize < 12) codeSize++;
            }

            Emit(clear);
            int prefix = indices[0];
            for (int i = 1; i < indices.Length; i++)
            {
                int key = prefix << 8 | indices[i];
                if (codes.TryGetValue(key, out int found))
                {
                    prefix = found;
                    continue;
                }
                Emit(prefix);
                if (next < 4096)
                {
                    codes[key] = next;
                    Grow();
                }
                else if (!deferredClear)
                {
                    Emit(clear);
                    codes.Clear();
                    codeSize = minimum + 1;
                    next = clear + 2;
                }
                prefix = indices[i];
            }
            Emit(prefix);
            if (next < 4096) Grow();
            Emit(clear + 1);
            if (bitCount > 0) output.Add((byte)bits);
            return output.ToArray();
        }

        private static void AssertFrame(Image image, byte[] palette, Frame frame)
        {
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                {
                    int fx = x - frame.left, fy = y - frame.top;
                    byte[] expected = new byte[4];
                    if (fx >= 0 && fy >= 0 && fx < frame.width && fy < frame.height)
                    {
                        int index = frame.indices[fy * frame.width + fx];
                        if (index != frame.transparent)
                            expected = new byte[] { palette[index * 3], palette[index * 3 + 1], palette[index * 3 + 2], 255 };
                    }
                    int i = (y * image.Width + x) * 4;
                    Assert.True(expected.AsSpan().SequenceEqual(image.Pixels.AsSpan(i, 4)), $"pixel ({x}, {y})");
                }
        }

        [Fact]
        public void IsRecognisedBySignature()
        {
            Frame frame = new Frame { width = 3, height = 2, minimum = 2, indices = new byte[6] };
            Assert.True(ImageCodec.TryIdentify(Gif(7, 5, Palette(4), frame), out ImageHeader header));
            Assert.Equal(ImageFormat.Gif, header.Format);
            Assert.Equal(7, header.Width);
            Assert.Equal(5, header.Height);
        }

        [Theory]
        [InlineData(2, 2, 37, 23)]
        [InlineData(4, 2, 40, 9)]
        [InlineData(16, 4, 37, 23)]
        [InlineData(128, 7, 64, 33)]
        [InlineData(256, 8, 61, 40)]
        public void GlobalPaletteMatches(int colours, int minimum, int width, int height)
        {
            byte[] palette = Palette(colours);
            Frame frame = new Frame { width = width, height = height, minimum = minimum, indices = Pattern(width, height, colours) };
            AssertFrame(Image.Load(Gif(width, height, palette, frame)), palette, frame);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FullCodeTableClearsOrDefers(bool deferredClear)
        {
            byte[] palette = Palette(256);
            Frame frame = new Frame { width = 300, height = 200, minimum = 8, deferredClear = deferredClear, indices = Pattern(300, 200, 256) };
            AssertFrame(Image.Load(Gif(300, 200, palette, frame)), palette, frame);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(8)]
        [InlineData(19)]
        [InlineData(64)]
        public void InterlacedRowsLandInOrder(int height)
        {
            byte[] palette = Palette(16);
            Frame frame = new Frame { width = 13, height = height, minimum = 4, interlaced = true, indices = Pattern(13, height, 16) };
            AssertFrame(Image.Load(Gif(13, height, palette, frame)), palette, frame);
        }

        [Fact]
        public void TransparentIndexIsClear()
        {
            byte[] palette = Palette(16);
            Frame frame = new Frame { width = 20, height = 10, minimum = 4, transparent = 3, indices = Pattern(20, 10, 16) };
            AssertFrame(Image.Load(Gif(20, 10, palette, frame)), palette, frame);
        }

        [Fact]
        public void OffsetFrameSitsOnATransparentCanvas()
        {
            byte[] palette = Palette(8);
            Frame frame = new Frame { left = 3, top = 2, width = 4, height = 3, minimum = 3, indices = Pattern(4, 3, 8) };
            AssertFrame(Image.Load(Gif(10, 8, palette, frame)), palette, frame);
        }

        [Fact]
        public void FrameIsClippedToTheCanvas()
        {
            byte[] palette = Palette(8);
            Frame frame = new Frame { left = 6, top = 5, width = 7, height = 6, minimum = 3, indices = Pattern(7, 6, 8) };
            AssertFrame(Image.Load(Gif(10, 8, palette, frame)), palette, frame);
        }

        [Fact]
        public void LocalPaletteOverridesGlobal()
        {
            byte[] local = Palette(16, 5);
            Frame frame = new Frame { width = 12, height = 7, minimum = 4, local = local, extensions = true, indices = Pattern(12, 7, 16) };
            AssertFrame(Image.Load(Gif(12, 7, Palette(16), frame)), local, frame);
        }

        [Fact]
        public void TruncatedDataLeavesTheRestTransparent()
        {
            byte[] palette = Palette(16);
            Frame frame = new Frame { width = 40, height = 30, minimum = 4, indices = Pattern(40, 30, 16) };
            byte[] file = Gif(40, 30, palette, frame);
            int dataStart = 13 + palette.Length + 10 + 1;
            byte[] truncated = file.AsSpan(0, dataStart + 1 + 60).ToArray();
            truncated[dataStart] = 60;
            Image image = Image.Load(truncated);
            Assert.Equal(palette[frame.indices[0] * 3], image.Pixels[0]);
            Assert.Equal(new byte[4], image.Pixels[^4..]);
        }

        [Fact]
        public void GreyLayoutTakesLuma()
        {
            byte[] palette = Palette(4);
            Frame frame = new Frame { width = 5, height = 3, minimum = 2, indices = Pattern(5, 3, 4) };
            byte[] file = Gif(5, 3, palette, frame);
            Image grey = Image.Load(file, PixelLayout.R8);
            Image rgba = Image.Load(file);
            for (int i = 0; i < 15; i++)
                Assert.Equal(PixelRowsLuma(rgba.Pixels[i * 4], rgba.Pixels[i * 4 + 1], rgba.Pixels[i * 4 + 2]), grey.Pixels[i]);
        }

        private static byte PixelRowsLuma(int r, int g, int b) => (byte)((r * 13933 + g * 46871 + b * 4732 + 32768) >> 16);
    }
}
