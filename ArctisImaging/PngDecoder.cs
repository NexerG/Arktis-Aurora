using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.Intrinsics;

namespace Arctis.Imaging
{
    internal static unsafe class PngDecoder
    {
        private const uint Ihdr = 0x49484452, Plte = 0x504C5445, Idat = 0x49444154, Iend = 0x49454E44, Trns = 0x74524E53;

        // Adam7 passes: x start, y start, x step, y step
        private static ReadOnlySpan<byte> Adam7 => new byte[] { 0, 0, 8, 8, 4, 0, 8, 8, 0, 4, 4, 8, 2, 0, 4, 4, 0, 2, 2, 4, 1, 0, 2, 2, 0, 1, 1, 2 };
        private static ReadOnlySpan<byte> Progressive => new byte[] { 0, 0, 1, 1 };

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
            ReadOnlySpan<byte> passes = c.interlaced ? Adam7 : Progressive;
            long total = 0;
            for (int pass = 0; pass < passes.Length; pass += 4)
                total += PassBytes(in c, passes[pass], passes[pass + 1], passes[pass + 2], passes[pass + 3]);
            if (total > Array.MaxLength) throw new ImageFormatException("PNG image is too large to decode");

            byte[] raw = ArrayPool<byte>.Shared.Rent((int)total);
            byte[] zero = ArrayPool<byte>.Shared.Rent(rowBytes + 1);
            byte[] rgba = ArrayPool<byte>.Shared.Rent(c.width * 4);
            byte[] packed = ArrayPool<byte>.Shared.Rent(c.width * 4);
            try
            {
                Inflater.Inflate(data, c.firstIdat, raw.AsSpan(0, (int)total));
                zero.AsSpan(0, rowBytes + 1).Clear();
                int at = 0;
                for (int pass = 0; pass < passes.Length; pass += 4)
                    at += DecodePass(raw.AsSpan(at), zero, in c, palette, passes[pass], passes[pass + 1], passes[pass + 2], passes[pass + 3], bpp, rgba, packed, destination, pitch, layout);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(raw);
                ArrayPool<byte>.Shared.Return(zero);
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
        // Inflated bytes of one pass: its rows, each with a filter byte.
        private static long PassBytes(in Chunks c, int x0, int y0, int dx, int dy)
        {
            long pw = (c.width - x0 + dx - 1) / dx, ph = (c.height - y0 + dy - 1) / dy;
            return pw <= 0 || ph <= 0 ? 0 : ph * ((pw * c.channels * c.bitDepth + 7) / 8 + 1);
        }

        // Unfilters one pass, or the whole image as the pass (0, 0, 1, 1), in place in the inflated rows and writes
        // it out; a row's previous row is the one above it. Returns the bytes the pass took.
        private static int DecodePass(Span<byte> raw, ReadOnlySpan<byte> zero, in Chunks c, uint[] palette, int x0, int y0, int dx, int dy, int bpp, byte[] rgba, byte[] packed, Span<byte> destination, int pitch, PixelLayout layout)
        {
            int pw = (c.width - x0 + dx - 1) / dx, ph = (c.height - y0 + dy - 1) / dy;
            if (pw <= 0 || ph <= 0) return 0;
            int stride = (int)(((long)pw * c.channels * c.bitDepth + 7) / 8) + 1;
            int outBpp = ImageCodec.BytesPerPixel(layout);
            bool direct = c.colorType == 6 && c.bitDepth == 8 && dx == 1;
            bool wide = layout == PixelLayout.RGBA8 || layout == PixelLayout.BGRA8;
            for (int j = 0; j < ph; j++)
            {
                Span<byte> cur = raw.Slice(j * stride, stride);
                ReadOnlySpan<byte> prev = j > 0 ? raw.Slice((j - 1) * stride, stride) : zero.Slice(0, stride);
                Unfilter(cur[0], cur.Slice(1), prev.Slice(1), bpp);
                Span<byte> row = destination.Slice((y0 + j * dy) * pitch);
                if (direct) PixelRows.FromRgba(cur.Slice(1), row.Slice(x0 * outBpp), pw, layout);
                else if (wide) Expand(cur.Slice(1), row.Slice(x0 * 4), pw, dx, in c, palette, layout == PixelLayout.BGRA8);
                else
                {
                    Expand(cur.Slice(1), rgba, pw, 1, in c, palette, false);
                    if (dx == 1) PixelRows.FromRgba(rgba, row.Slice(x0 * outBpp), pw, layout);
                    else
                    {
                        PixelRows.FromRgba(rgba, packed, pw, layout);
                        for (int i = 0; i < pw; i++)
                            packed.AsSpan(i * outBpp, outBpp).CopyTo(row.Slice((x0 + i * dx) * outBpp));
                    }
                }
            }
            return ph * stride;
        }

        // Converts one unfiltered row of any colour type and depth to RGBA8 or BGRA8, pixel x landing at x * step * 4.
        private static void Expand(ReadOnlySpan<byte> raw, Span<byte> output, int width, int step, in Chunks c, uint[] palette, bool bgra)
        {
            int depth = c.bitDepth, stride = step * 4, rs = bgra ? 16 : 0, bs = bgra ? 0 : 16;
            bool key = c.hasKey;
            fixed (byte* src = raw)
            fixed (byte* dst = output)
            {
                byte* o = dst;
                switch (c.colorType)
                {
                    case 0:
                        if (depth == 8 && !key)
                        {
                            int x = 0;
                            if (step == 1 && Vector128.IsHardwareAccelerated)
                            {
                                Vector128<byte> alpha = Vector128.Create(0xFF000000u).AsByte();
                                Vector128<byte> q0 = Vector128.Create((byte)0, 0, 0, 0xFF, 1, 1, 1, 0xFF, 2, 2, 2, 0xFF, 3, 3, 3, 0xFF);
                                Vector128<byte> q1 = Vector128.Create((byte)4, 4, 4, 0xFF, 5, 5, 5, 0xFF, 6, 6, 6, 0xFF, 7, 7, 7, 0xFF);
                                Vector128<byte> q2 = Vector128.Create((byte)8, 8, 8, 0xFF, 9, 9, 9, 0xFF, 10, 10, 10, 0xFF, 11, 11, 11, 0xFF);
                                Vector128<byte> q3 = Vector128.Create((byte)12, 12, 12, 0xFF, 13, 13, 13, 0xFF, 14, 14, 14, 0xFF, 15, 15, 15, 0xFF);
                                for (; x + 16 <= width; x += 16)
                                {
                                    Vector128<byte> g = Vector128.Load(src + x);
                                    byte* d = dst + x * 4;
                                    (Vector128.Shuffle(g, q0) | alpha).Store(d);
                                    (Vector128.Shuffle(g, q1) | alpha).Store(d + 16);
                                    (Vector128.Shuffle(g, q2) | alpha).Store(d + 32);
                                    (Vector128.Shuffle(g, q3) | alpha).Store(d + 48);
                                }
                            }
                            for (; x < width; x++)
                                Store(dst + x * stride, src[x] * 0x010101u | 0xFF000000u);
                        }
                        else
                            for (int x = 0; x < width; x++, o += stride)
                            {
                                int sample = depth == 16 ? Big16(src + x * 2) : depth == 8 ? src[x] : SubByte(raw, x, depth);
                                uint g = depth == 16 ? To8(sample) : (uint)(sample * (255 / ((1 << depth) - 1)));
                                Store(o, g * 0x010101u | (key && sample == c.keyR ? 0u : 0xFF000000u));
                            }
                        break;
                    case 2:
                        if (depth == 8 && !key)
                        {
                            int x = 0;
                            if (step == 1 && Vector128.IsHardwareAccelerated)
                            {
                                Vector128<byte> order = bgra
                                    ? Vector128.Create((byte)2, 1, 0, 0xFF, 5, 4, 3, 0xFF, 8, 7, 6, 0xFF, 11, 10, 9, 0xFF)
                                    : Vector128.Create((byte)0, 1, 2, 0xFF, 3, 4, 5, 0xFF, 6, 7, 8, 0xFF, 9, 10, 11, 0xFF);
                                Vector128<byte> alpha = Vector128.Create(0xFF000000u).AsByte();
                                for (; x + 6 <= width; x += 4)
                                    (Vector128.Shuffle(Vector128.Load(src + x * 3), order) | alpha).Store(dst + x * 4);
                            }
                            for (; x < width; x++)
                                Store(dst + x * stride, (uint)(src[x * 3] << rs | src[x * 3 + 1] << 8 | src[x * 3 + 2] << bs) | 0xFF000000u);
                        }
                        else
                            for (int x = 0; x < width; x++, o += stride)
                            {
                                int r = depth == 8 ? src[x * 3] : Big16(src + x * 6);
                                int g = depth == 8 ? src[x * 3 + 1] : Big16(src + x * 6 + 2);
                                int b = depth == 8 ? src[x * 3 + 2] : Big16(src + x * 6 + 4);
                                uint a = key && r == c.keyR && g == c.keyG && b == c.keyB ? 0u : 0xFF000000u;
                                if (depth == 16)
                                {
                                    r = To8(r);
                                    g = To8(g);
                                    b = To8(b);
                                }
                                Store(o, (uint)(r << rs | g << 8 | b << bs) | a);
                            }
                        break;
                    case 3:
                        for (int x = 0; x < width; x++, o += stride)
                        {
                            uint entry = palette[depth == 8 ? src[x] : SubByte(raw, x, depth)];
                            Store(o, bgra ? (entry & 0xFF00FF00u) | (entry >> 16 & 0xFFu) | (entry & 0xFFu) << 16 : entry);
                        }
                        break;
                    case 4:
                        for (int x = 0; x < width; x++, o += stride)
                        {
                            uint g = depth == 8 ? src[x * 2] : To8(Big16(src + x * 4));
                            uint a = depth == 8 ? src[x * 2 + 1] : To8(Big16(src + x * 4 + 2));
                            Store(o, g * 0x010101u | a << 24);
                        }
                        break;
                    case 6:
                        if (depth == 8)
                        {
                            if (step == 1) PixelRows.FromRgba(raw, output, width, bgra ? PixelLayout.BGRA8 : PixelLayout.RGBA8);
                            else
                                for (int x = 0; x < width; x++, o += stride)
                                {
                                    byte* s = src + x * 4;
                                    Store(o, (uint)(s[0] << rs | s[1] << 8 | s[2] << bs | s[3] << 24));
                                }
                        }
                        else
                        {
                            int x = 0;
                            if (step == 1 && Vector128.IsHardwareAccelerated)
                            {
                                Vector128<byte> order = bgra
                                    ? Vector128.Create((byte)5, 4, 3, 2, 1, 0, 7, 6, 13, 12, 11, 10, 9, 8, 15, 14)
                                    : Vector128.Create((byte)1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14);
                                Vector128<uint> scale = Vector128.Create(255u), round = Vector128.Create(32895u);
                                for (; x + 2 <= width; x += 2)
                                {
                                    (Vector128<uint> lo, Vector128<uint> hi) = Vector128.Widen(Vector128.Shuffle(Vector128.Load(src + x * 8), order).AsUInt16());
                                    Vector128<ushort> narrow = Vector128.Narrow((lo * scale + round) >> 16, (hi * scale + round) >> 16);
                                    *(ulong*)(dst + x * 4) = Vector128.Narrow(narrow, narrow).AsUInt64().ToScalar();
                                }
                            }
                            for (; x < width; x++)
                            {
                                byte* s = src + x * 8;
                                Store(dst + x * stride, (uint)(To8(Big16(s)) << rs | To8(Big16(s + 2)) << 8 | To8(Big16(s + 4)) << bs | To8(Big16(s + 6)) << 24));
                            }
                        }
                        break;
                }
            }
        }

        private static void Store(byte* p, uint rgba) => *(uint*)p = BitConverter.IsLittleEndian ? rgba : BinaryPrimitives.ReverseEndianness(rgba);

        private static int Big16(byte* p) => p[0] << 8 | p[1];

        private static int SubByte(ReadOnlySpan<byte> raw, int x, int depth)
        {
            int bit = x * depth;
            return (raw[bit >> 3] >> (8 - depth - (bit & 7))) & ((1 << depth) - 1);
        }

        private static byte To8(int v) => (byte)((v * 255 + 32895) >> 16);
        #endregion

        #region filters
        private static void Unfilter(byte type, Span<byte> cur, ReadOnlySpan<byte> prev, int bpp)
        {
            int n = cur.Length;
            switch (type)
            {
                case 0:
                    break;
                case 1:
                    if (bpp == 4) Sub4(cur);
                    else if (bpp == 8) Sub8(cur);
                    else
                        fixed (byte* p = cur)
                            for (int i = bpp; i < n; i++) p[i] += p[i - bpp];
                    break;
                case 2:
                    AddUp(cur, prev);
                    break;
                case 3:
                    if (bpp == 4) Average4(cur, prev);
                    else if (bpp == 8) Average8(cur, prev);
                    else
                        fixed (byte* p = cur)
                        fixed (byte* q = prev)
                        {
                            for (int i = 0; i < bpp && i < n; i++) p[i] += (byte)(q[i] >> 1);
                            for (int i = bpp; i < n; i++) p[i] += (byte)((p[i - bpp] + q[i]) >> 1);
                        }
                    break;
                case 4:
                    if (bpp == 4 && Vector128.IsHardwareAccelerated) Paeth4(cur, prev);
                    else if (bpp >= 3 && Vector128.IsHardwareAccelerated) PaethWide(cur, prev, bpp);
                    else PaethScalar(cur, prev, bpp, 0);
                    break;
                default:
                    throw new ImageFormatException($"PNG row filter {type} is invalid");
            }
        }

        // Sub at four bytes per pixel: a bytewise add of the left pixel, one 32-bit word at a time.
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

        // Sub at eight bytes per pixel: Sub4's bytewise add on one 64-bit word.
        private static void Sub8(Span<byte> cur)
        {
            fixed (byte* p = cur)
            {
                ulong a = 0;
                for (int i = 0; i + 8 <= cur.Length; i += 8)
                {
                    ulong x = *(ulong*)(p + i);
                    a = ((x & 0x7F7F7F7F7F7F7F7Ful) + (a & 0x7F7F7F7F7F7F7F7Ful)) ^ ((x ^ a) & 0x8080808080808080ul);
                    *(ulong*)(p + i) = a;
                }
            }
        }

        // Average at four bytes per pixel: per byte (a & b) + ((a ^ b) >> 1), then a bytewise add, one 32-bit word at a time.
        private static void Average4(Span<byte> cur, ReadOnlySpan<byte> prev)
        {
            fixed (byte* p = cur)
            fixed (byte* q = prev)
            {
                uint a = 0;
                for (int i = 0; i + 4 <= cur.Length; i += 4)
                {
                    uint b = *(uint*)(q + i), x = *(uint*)(p + i);
                    uint m = (a & b) + (((a ^ b) & 0xFEFEFEFEu) >> 1);
                    a = ((x & 0x7F7F7F7Fu) + (m & 0x7F7F7F7Fu)) ^ ((x ^ m) & 0x80808080u);
                    *(uint*)(p + i) = a;
                }
            }
        }

        // Average4 on one 64-bit word.
        private static void Average8(Span<byte> cur, ReadOnlySpan<byte> prev)
        {
            fixed (byte* p = cur)
            fixed (byte* q = prev)
            {
                ulong a = 0;
                for (int i = 0; i + 8 <= cur.Length; i += 8)
                {
                    ulong b = *(ulong*)(q + i), x = *(ulong*)(p + i);
                    ulong m = (a & b) + (((a ^ b) & 0xFEFEFEFEFEFEFEFEul) >> 1);
                    a = ((x & 0x7F7F7F7F7F7F7F7Ful) + (m & 0x7F7F7F7F7F7F7F7Ful)) ^ ((x ^ m) & 0x8080808080808080ul);
                    *(ulong*)(p + i) = a;
                }
            }
        }

        // Paeth at three to eight bytes per pixel: Paeth4 over eight lanes, lanes past the pixel unpredicted so they store back as read.
        private static void PaethWide(Span<byte> cur, ReadOnlySpan<byte> prev, int bpp)
        {
            int i = 0;
            fixed (byte* pCur = cur)
            fixed (byte* pPrev = prev)
            {
                Vector128<short> a = Vector128<short>.Zero, c = Vector128<short>.Zero;
                Vector128<short> low = Vector128.Create((short)0xFF);
                Vector128<short> pixel = Vector128.LessThan(Vector128.Create((short)0, 1, 2, 3, 4, 5, 6, 7), Vector128.Create((short)bpp));
                for (; i + 8 <= cur.Length; i += bpp)
                {
                    Vector128<short> b = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)(pPrev + i)).AsByte()).AsInt16();
                    Vector128<short> x = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)(pCur + i)).AsByte()).AsInt16();
                    Vector128<short> pa = Vector128.Abs(b - c), pb = Vector128.Abs(a - c), pc = Vector128.Abs(a + b - c - c);
                    Vector128<short> useA = Vector128.LessThanOrEqual(pa, pb) & Vector128.LessThanOrEqual(pa, pc);
                    Vector128<short> useB = Vector128.LessThanOrEqual(pb, pc);
                    a = (x + (Vector128.ConditionalSelect(useA, a, Vector128.ConditionalSelect(useB, b, c)) & pixel)) & low;
                    *(ulong*)(pCur + i) = Vector128.Narrow(a.AsUInt16(), a.AsUInt16()).AsUInt64().ToScalar();
                    c = b;
                }
            }
            PaethScalar(cur, prev, bpp, i);
        }

        // Paeth one byte at a time from start.
        private static void PaethScalar(Span<byte> cur, ReadOnlySpan<byte> prev, int bpp, int start)
        {
            fixed (byte* p = cur)
            fixed (byte* q = prev)
                for (int i = start; i < cur.Length; i++)
                {
                    int a = i >= bpp ? p[i - bpp] : 0, b = q[i], cc = i >= bpp ? q[i - bpp] : 0;
                    int pa = Math.Abs(b - cc), pb = Math.Abs(a - cc), pc = Math.Abs(a + b - 2 * cc);
                    p[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : cc);
                }
        }

        private static void AddUp(Span<byte> cur, ReadOnlySpan<byte> prev)
        {
            int i = 0;
            if (Vector128.IsHardwareAccelerated)
                for (; i + 16 <= cur.Length; i += 16)
                    (Vector128.Create((ReadOnlySpan<byte>)cur.Slice(i, 16)) + Vector128.Create(prev.Slice(i, 16))).CopyTo(cur.Slice(i));
            for (; i < cur.Length; i++) cur[i] += prev[i];
        }
        #endregion
    }
}
