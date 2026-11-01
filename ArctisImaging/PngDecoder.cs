using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Arctis.Imaging
{
    internal static unsafe class PngDecoder
    {
        private const uint Ihdr = 0x49484452, Plte = 0x504C5445, Idat = 0x49444154, Iend = 0x49454E44, Trns = 0x74524E53;

        // Adam7 passes: x start, y start, x step, y step
        private static ReadOnlySpan<byte> Adam7 => new byte[] { 0, 0, 8, 8, 4, 0, 8, 8, 0, 4, 4, 8, 2, 0, 4, 4, 0, 2, 2, 4, 1, 0, 2, 2, 0, 1, 1, 2 };

        // what the chunk walk collects
        private struct Chunks
        {
            public int width, height, bitDepth, colorType, channels;
            public bool interlaced;
            public int firstIdat;
            public bool hasKey;
            public ushort keyR, keyG, keyB;
        }

        internal static bool TryReadHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (data.Length < 33 || BinaryPrimitives.ReadUInt64BigEndian(data) != 0x89504E470D0A1A0Aul) return false;
            if (BinaryPrimitives.ReadUInt32BigEndian(data.Slice(12)) != Ihdr) return false;
            int width = BinaryPrimitives.ReadInt32BigEndian(data.Slice(16));
            int height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(20));
            int bitDepth = data[24], colorType = data[25];
            bool palette = colorType == 3;
            header = new ImageHeader(ImageFormat.Png, width, height, palette ? 3 : ChannelsOf(colorType), palette ? 8 : bitDepth, colorType == 4 || colorType == 6);
            return true;
        }

        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int pitch, PixelLayout layout)
        {
            Chunks c = default;
            uint[] palette = new uint[256];
            Walk(data, ref c, palette);

            int rowBytes = (int)(((long)c.width * c.channels * c.bitDepth + 7) / 8);
            int bpp = Math.Max(1, c.channels * c.bitDepth / 8);
            int block = Math.Clamp(64 * 1024 / (rowBytes + 1), 1, c.height);
            byte[] lines = ArrayPool<byte>.Shared.Rent((block + 1) * (rowBytes + 1));
            byte[] rgba = ArrayPool<byte>.Shared.Rent(c.width * 4);
            byte[] packed = ArrayPool<byte>.Shared.Rent(c.width * 4);
            try
            {
                fixed (byte* p = data)
                {
                    using IdatStream idat = new IdatStream(p, data.Length, c.firstIdat);
                    using ZLibStream inflate = new ZLibStream(idat, CompressionMode.Decompress);
                    if (c.interlaced) DecodeInterlaced(inflate, in c, palette, lines, rowBytes, bpp, rgba, packed, destination, pitch, layout);
                    else DecodeRows(inflate, in c, palette, lines, rowBytes, block, bpp, rgba, destination, pitch, layout);
                }
            }
            catch (InvalidDataException e)
            {
                throw new ImageFormatException("PNG image data is corrupt", e);
            }
            catch (EndOfStreamException e)
            {
                throw new ImageFormatException("PNG image data ends early", e);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(lines);
                ArrayPool<byte>.Shared.Return(rgba);
                ArrayPool<byte>.Shared.Return(packed);
            }
        }

        #region chunks
        private static void Walk(ReadOnlySpan<byte> data, ref Chunks c, uint[] palette)
        {
            c.width = BinaryPrimitives.ReadInt32BigEndian(data.Slice(16));
            c.height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(20));
            c.bitDepth = data[24];
            c.colorType = data[25];
            c.interlaced = data[28] == 1;
            if (BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8)) != 13 || data[26] != 0 || data[27] != 0 || data[28] > 1)
                throw new ImageFormatException("PNG header is invalid");
            if (!IsValidDepth(c.colorType, c.bitDepth))
                throw new ImageFormatException($"PNG colour type {c.colorType} with bit depth {c.bitDepth} is invalid");
            c.channels = ChannelsOf(c.colorType);
            c.firstIdat = -1;

            for (int i = 0; i < 256; i++) palette[i] = 0xFF000000u;
            int paletteCount = 0;
            int offset = 8;
            bool idatEnded = false;
            while (offset + 12 <= data.Length)
            {
                uint length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset));
                if (length > int.MaxValue || offset + 12 + (long)length > data.Length) throw new ImageFormatException("PNG chunk runs past the end of the file");
                uint type = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 4));
                ReadOnlySpan<byte> body = data.Slice(offset + 8, (int)length);
                bool critical = (type & 0x20000000) == 0;
                uint stored = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 8 + (int)length));
                if (Crc32.Update(0, data.Slice(offset + 4, (int)length + 4)) != stored)
                {
                    if (critical) throw new ImageFormatException("PNG chunk checksum does not match");
                    offset += 12 + (int)length;
                    continue;
                }

                if (type == Idat)
                {
                    if (idatEnded) throw new ImageFormatException("PNG image data chunks are not consecutive");
                    if (c.firstIdat < 0) c.firstIdat = offset;
                }
                else if (c.firstIdat >= 0) idatEnded = true;

                if (type == Iend) break;
                if (type == Plte)
                {
                    if (length % 3 != 0 || length > 768) throw new ImageFormatException("PNG palette is invalid");
                    paletteCount = (int)length / 3;
                    for (int i = 0; i < paletteCount; i++)
                        palette[i] = body[i * 3] | (uint)body[i * 3 + 1] << 8 | (uint)body[i * 3 + 2] << 16 | 0xFF000000u;
                }
                else if (type == Trns) ReadTransparency(body, ref c, palette);
                else if (critical && type != Ihdr && type != Idat) throw new ImageFormatException("PNG has an unknown critical chunk");
                offset += 12 + (int)length;
            }
            if (c.firstIdat < 0) throw new ImageFormatException("PNG has no image data");
            if (c.colorType == 3 && paletteCount == 0) throw new ImageFormatException("PNG palette is missing");
        }

        private static void ReadTransparency(ReadOnlySpan<byte> body, ref Chunks c, uint[] palette)
        {
            if (c.colorType == 3)
            {
                int count = Math.Min(body.Length, 256);
                for (int i = 0; i < count; i++) palette[i] = (palette[i] & 0x00FFFFFFu) | (uint)body[i] << 24;
            }
            else if (c.colorType == 0 && body.Length >= 2)
            {
                c.hasKey = true;
                c.keyR = BinaryPrimitives.ReadUInt16BigEndian(body);
            }
            else if (c.colorType == 2 && body.Length >= 6)
            {
                c.hasKey = true;
                c.keyR = BinaryPrimitives.ReadUInt16BigEndian(body);
                c.keyG = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(2));
                c.keyB = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(4));
            }
        }

        private static int ChannelsOf(int colorType) => colorType switch
        {
            2 => 3,
            4 => 2,
            6 => 4,
            _ => 1
        };

        private static bool IsValidDepth(int colorType, int depth) => colorType switch
        {
            0 => depth == 1 || depth == 2 || depth == 4 || depth == 8 || depth == 16,
            3 => depth == 1 || depth == 2 || depth == 4 || depth == 8,
            2 or 4 or 6 => depth == 8 || depth == 16,
            _ => false
        };
        #endregion

        #region rows
        // Inflates block rows per read into lines after the previous row, then carries the last row to the front.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void DecodeRows(ZLibStream inflate, in Chunks c, uint[] palette, byte[] lines, int rowBytes, int block, int bpp, byte[] rgba, Span<byte> destination, int pitch, PixelLayout layout)
        {
            int stride = rowBytes + 1;
            Span<byte> all = lines.AsSpan(0, (block + 1) * stride);
            all.Slice(0, stride).Clear();
            bool direct = c.colorType == 6 && c.bitDepth == 8;
            for (int y = 0; y < c.height; y += block)
            {
                int count = Math.Min(block, c.height - y);
                inflate.ReadExactly(all.Slice(stride, count * stride));
                for (int k = 0; k < count; k++)
                {
                    Span<byte> prev = all.Slice(k * stride, stride);
                    Span<byte> cur = all.Slice((k + 1) * stride, stride);
                    Unfilter(cur[0], cur.Slice(1), prev.Slice(1), bpp);
                    Span<byte> row = destination.Slice((y + k) * pitch);
                    if (direct) PixelRows.FromRgba(cur.Slice(1), row, c.width, layout);
                    else
                    {
                        Expand(cur.Slice(1), rgba, c.width, in c, palette);
                        PixelRows.FromRgba(rgba, row, c.width, layout);
                    }
                }
                all.Slice(count * stride, stride).CopyTo(all);
            }
        }

        private static void DecodeInterlaced(ZLibStream inflate, in Chunks c, uint[] palette, byte[] lines, int rowBytes, int bpp, byte[] rgba, byte[] packed, Span<byte> destination, int pitch, PixelLayout layout)
        {
            int outBpp = ImageCodec.BytesPerPixel(layout);
            ReadOnlySpan<byte> passes = Adam7;
            for (int pass = 0; pass < 7; pass++)
            {
                int x0 = passes[pass * 4], y0 = passes[pass * 4 + 1], dx = passes[pass * 4 + 2], dy = passes[pass * 4 + 3];
                int pw = (c.width - x0 + dx - 1) / dx, ph = (c.height - y0 + dy - 1) / dy;
                if (pw <= 0 || ph <= 0) continue;
                int passBytes = (int)(((long)pw * c.channels * c.bitDepth + 7) / 8);
                Span<byte> prev = lines.AsSpan(0, passBytes + 1);
                Span<byte> cur = lines.AsSpan(rowBytes + 1, passBytes + 1);
                prev.Clear();
                for (int j = 0; j < ph; j++)
                {
                    inflate.ReadExactly(cur);
                    Unfilter(cur[0], cur.Slice(1), prev.Slice(1), bpp);
                    Expand(cur.Slice(1), rgba, pw, in c, palette);
                    PixelRows.FromRgba(rgba, packed, pw, layout);
                    Span<byte> row = destination.Slice((y0 + j * dy) * pitch);
                    for (int i = 0; i < pw; i++)
                        packed.AsSpan(i * outBpp, outBpp).CopyTo(row.Slice((x0 + i * dx) * outBpp));
                    Span<byte> swap = prev;
                    prev = cur;
                    cur = swap;
                }
            }
        }

        // Converts one unfiltered row of any colour type and depth to RGBA8.
        private static void Expand(ReadOnlySpan<byte> raw, Span<byte> rgba, int width, in Chunks c, uint[] palette)
        {
            int depth = c.bitDepth;
            switch (c.colorType)
            {
                case 0:
                    for (int x = 0; x < width; x++)
                    {
                        int sample = depth == 16 ? BinaryPrimitives.ReadUInt16BigEndian(raw.Slice(x * 2)) : depth == 8 ? raw[x] : SubByte(raw, x, depth);
                        byte g = depth == 16 ? To8(sample) : (byte)(sample * (255 / ((1 << depth) - 1)));
                        rgba[x * 4] = g;
                        rgba[x * 4 + 1] = g;
                        rgba[x * 4 + 2] = g;
                        rgba[x * 4 + 3] = c.hasKey && sample == c.keyR ? (byte)0 : (byte)255;
                    }
                    break;
                case 2:
                    if (depth == 8)
                        for (int x = 0; x < width; x++)
                        {
                            byte r = raw[x * 3], g = raw[x * 3 + 1], b = raw[x * 3 + 2];
                            rgba[x * 4] = r;
                            rgba[x * 4 + 1] = g;
                            rgba[x * 4 + 2] = b;
                            rgba[x * 4 + 3] = c.hasKey && r == c.keyR && g == c.keyG && b == c.keyB ? (byte)0 : (byte)255;
                        }
                    else
                        for (int x = 0; x < width; x++)
                        {
                            ushort r = BinaryPrimitives.ReadUInt16BigEndian(raw.Slice(x * 6));
                            ushort g = BinaryPrimitives.ReadUInt16BigEndian(raw.Slice(x * 6 + 2));
                            ushort b = BinaryPrimitives.ReadUInt16BigEndian(raw.Slice(x * 6 + 4));
                            rgba[x * 4] = To8(r);
                            rgba[x * 4 + 1] = To8(g);
                            rgba[x * 4 + 2] = To8(b);
                            rgba[x * 4 + 3] = c.hasKey && r == c.keyR && g == c.keyG && b == c.keyB ? (byte)0 : (byte)255;
                        }
                    break;
                case 3:
                    for (int x = 0; x < width; x++)
                    {
                        uint entry = palette[depth == 8 ? raw[x] : SubByte(raw, x, depth)];
                        BinaryPrimitives.WriteUInt32LittleEndian(rgba.Slice(x * 4), entry);
                    }
                    break;
                case 4:
                    for (int x = 0; x < width; x++)
                    {
                        byte g = depth == 8 ? raw[x * 2] : To8(BinaryPrimitives.ReadUInt16BigEndian(raw.Slice(x * 4)));
                        byte a = depth == 8 ? raw[x * 2 + 1] : To8(BinaryPrimitives.ReadUInt16BigEndian(raw.Slice(x * 4 + 2)));
                        rgba[x * 4] = g;
                        rgba[x * 4 + 1] = g;
                        rgba[x * 4 + 2] = g;
                        rgba[x * 4 + 3] = a;
                    }
                    break;
                case 6:
                    if (depth == 8) raw.Slice(0, width * 4).CopyTo(rgba);
                    else
                        for (int i = 0; i < width * 4; i++)
                            rgba[i] = To8(BinaryPrimitives.ReadUInt16BigEndian(raw.Slice(i * 2)));
                    break;
            }
        }

        private static int SubByte(ReadOnlySpan<byte> raw, int x, int depth)
        {
            int bit = x * depth;
            return (raw[bit >> 3] >> (8 - depth - (bit & 7))) & ((1 << depth) - 1);
        }

        private static byte To8(int v) => (byte)((v * 255 + 32895) >> 16);
        #endregion

        #region filters
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Unfilter(byte type, Span<byte> cur, ReadOnlySpan<byte> prev, int bpp)
        {
            int n = cur.Length;
            switch (type)
            {
                case 0:
                    break;
                case 1:
                    if (bpp == 4)
                    {
                        Sub4(cur);
                        break;
                    }
                    for (int i = bpp; i < n; i++) cur[i] += cur[i - bpp];
                    break;
                case 2:
                    AddUp(cur, prev);
                    break;
                case 3:
                    for (int i = 0; i < bpp && i < n; i++) cur[i] += (byte)(prev[i] >> 1);
                    for (int i = bpp; i < n; i++) cur[i] += (byte)((cur[i - bpp] + prev[i]) >> 1);
                    break;
                case 4:
                    if (bpp == 4 && Vector128.IsHardwareAccelerated)
                    {
                        Paeth4(cur, prev);
                        break;
                    }
                    for (int i = 0; i < bpp && i < n; i++) cur[i] += prev[i];
                    for (int i = bpp; i < n; i++)
                    {
                        int a = cur[i - bpp], b = prev[i], cc = prev[i - bpp];
                        int pa = Math.Abs(b - cc), pb = Math.Abs(a - cc), pc = Math.Abs(a + b - 2 * cc);
                        cur[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : cc);
                    }
                    break;
                default:
                    throw new ImageFormatException($"PNG row filter {type} is invalid");
            }
        }

        // Sub at four bytes per pixel: a bytewise add of the left pixel, one 32-bit word at a time.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Sub4(Span<byte> cur)
        {
            fixed (byte* p = cur)
            {
                uint a = 0;
                for (int i = 0; i + 4 <= cur.Length; i += 4)
                {
                    uint x = *(uint*)(p + i);
                    a = ((x & 0x7F7F7F7Fu) + (a & 0x7F7F7F7Fu)) ^ ((x ^ a) & 0x80808080u);
                    *(uint*)(p + i) = a;
                }
            }
        }

        // Paeth at four bytes per pixel: a pixel's four channels widened to 16-bit lanes and predicted together.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Paeth4(Span<byte> cur, ReadOnlySpan<byte> prev)
        {
            fixed (byte* pCur = cur)
            fixed (byte* pPrev = prev)
            {
                Vector128<short> a = Vector128<short>.Zero, c = Vector128<short>.Zero;
                Vector128<short> low = Vector128.Create((short)0xFF);
                for (int i = 0; i + 4 <= cur.Length; i += 4)
                {
                    Vector128<short> b = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(uint*)(pPrev + i)).AsByte()).AsInt16();
                    Vector128<short> x = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(uint*)(pCur + i)).AsByte()).AsInt16();
                    Vector128<short> pa = Vector128.Abs(b - c), pb = Vector128.Abs(a - c), pc = Vector128.Abs(a + b - c - c);
                    Vector128<short> useA = Vector128.LessThanOrEqual(pa, pb) & Vector128.LessThanOrEqual(pa, pc);
                    Vector128<short> useB = Vector128.LessThanOrEqual(pb, pc);
                    a = (x + Vector128.ConditionalSelect(useA, a, Vector128.ConditionalSelect(useB, b, c))) & low;
                    *(uint*)(pCur + i) = Vector128.Narrow(a.AsUInt16(), a.AsUInt16()).AsUInt32().ToScalar();
                    c = b;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void AddUp(Span<byte> cur, ReadOnlySpan<byte> prev)
        {
            int i = 0;
            if (Vector128.IsHardwareAccelerated)
                for (; i + 16 <= cur.Length; i += 16)
                    (Vector128.Create((ReadOnlySpan<byte>)cur.Slice(i, 16)) + Vector128.Create(prev.Slice(i, 16))).CopyTo(cur.Slice(i));
            for (; i < cur.Length; i++) cur[i] += prev[i];
        }
        #endregion

        // The concatenated payload of consecutive IDAT chunks, read in place.
        private sealed class IdatStream : Stream
        {
            private readonly byte* data;
            private readonly int length;
            private int position;
            private int chunkEnd;

            internal IdatStream(byte* data, int length, int firstChunk)
            {
                this.data = data;
                this.length = length;
                Enter(firstChunk);
            }

            private bool Enter(int chunk)
            {
                if (chunk + 8 > length || BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(data + chunk + 4, 4)) != Idat) return false;
                int size = BinaryPrimitives.ReadInt32BigEndian(new ReadOnlySpan<byte>(data + chunk, 4));
                if (size < 0 || chunk + 8 + (long)size > length) return false;
                position = chunk + 8;
                chunkEnd = position + size;
                return true;
            }

            public override int Read(Span<byte> buffer)
            {
                while (position == chunkEnd)
                    if (!Enter(chunkEnd + 4)) return 0;
                int n = Math.Min(buffer.Length, chunkEnd - position);
                new ReadOnlySpan<byte>(data + position, n).CopyTo(buffer);
                position += n;
                return n;
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
