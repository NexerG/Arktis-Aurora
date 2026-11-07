using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Arctis.Imaging
{
    internal static unsafe class PngDecoder
    {
        private const uint Ihdr = 0x49484452, Plte = 0x504C5445, Idat = 0x49444154, Iend = 0x49454E44, Trns = 0x74524E53;

        // Adam7 passes: x start, y start, x step, y step
        private static ReadOnlySpan<byte> Adam7 => new byte[] { 0, 0, 8, 8, 4, 0, 8, 8, 0, 4, 4, 8, 2, 0, 4, 4, 0, 2, 2, 4, 1, 0, 2, 2, 0, 1, 1, 2 };
        private static ReadOnlySpan<byte> Progressive => new byte[] { 0, 0, 1, 1 };
        internal const int DefaultFill = 1 << 18;

        // what the chunk walk collects
        private struct Chunks
        {
            public int width, height, bitDepth, colorType, channels;
            public bool interlaced;
            public int firstIdat;
            public bool hasKey;
            public ushort keyR, keyG, keyB;
        }

        // where the row walk stands: the pass's table offset and geometry, and its next row
        private struct Pass
        {
            public int index, x0, y0, dx, dy, pw, ph, stride, j;
            public bool direct;
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

        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int pitch, PixelLayout layout) => Decode(data, destination, pitch, layout, DefaultFill);

        // Inflates into a window of history plus fill bytes, writes out each row as it completes, then slides the
        // window down to the history and the unfinished row.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int pitch, PixelLayout layout, int fill)
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

            int window = (int)Math.Min(total, (long)Math.Max(Inflater.History, rowBytes + 1) + fill + Inflater.MaxMatch);
            byte[] raw = ArrayPool<byte>.Shared.Rent(window);
            byte[] cur = ArrayPool<byte>.Shared.Rent(rowBytes + 1);
            byte[] prev = ArrayPool<byte>.Shared.Rent(rowBytes + 1);
            byte[] rgba = ArrayPool<byte>.Shared.Rent(c.width * 4);
            byte[] packed = ArrayPool<byte>.Shared.Rent(c.width * 4);
            try
            {
                fixed (byte* png = data)
                fixed (byte* start = raw)
                {
                    Inflater.State st = default;
                    Inflater.Begin(ref st, png, data.Length, c.firstIdat);
                    Pass p = new Pass { index = -4 };
                    bool more = NextPass(ref p, passes, in c);
                    prev.AsSpan(0, rowBytes + 1).Clear();
                    bool grouped = layout == PixelLayout.RGBA8 && Vector128.IsHardwareAccelerated;
                    byte* o = start, row = start;
                    long left = total;
                    while (true)
                    {
                        bool last = left <= window - (o - start);
                        byte* outEnd = last ? o + left : start + window;
                        byte* end = Inflater.Fill(ref st, start, o, outEnd, last);
                        left -= end - o;
                        o = end;

                        // every row now whole in the window
                        while (more && o - row >= p.stride)
                        {
                            if (grouped && p.direct && p.ph - p.j >= 4 && o - row >= 4L * p.stride && row[0] == 4 && row[p.stride] == 4 && row[2 * p.stride] == 4 && row[3 * p.stride] == 4)
                            {
                                Paeth4Rows(new ReadOnlySpan<byte>(row, 4 * p.stride), p.stride, prev.AsSpan(0, p.stride - 1), destination.Slice((p.y0 + p.j * p.dy) * pitch), p.dy * pitch);
                                row += 4 * p.stride;
                                p.j += 4;
                            }
                            else
                            {
                                DecodeRow(new ReadOnlySpan<byte>(row, p.stride), cur, prev, in c, palette, in p, bpp, rgba, packed, destination, pitch, layout);
                                (cur, prev) = (prev, cur);
                                row += p.stride;
                                p.j++;
                            }
                            if (p.j < p.ph) continue;
                            more = NextPass(ref p, passes, in c);
                            if (more) prev.AsSpan(0, p.stride).Clear();
                        }

                        if (last)
                        {
                            if (o != outEnd) throw new ImageFormatException("PNG image data ends early");
                            break;
                        }
                        if (st.done) throw new ImageFormatException("PNG image data ends early");

                        long slide = Math.Min(row - start, o - start - Inflater.History);
                        if (slide <= 0) continue;
                        Buffer.MemoryCopy(start + slide, start, window, o - start - slide);
                        o -= slide;
                        row -= slide;
                    }
                    Inflater.Finish(ref st);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(raw);
                ArrayPool<byte>.Shared.Return(cur);
                ArrayPool<byte>.Shared.Return(prev);
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

        // Moves to the next pass that has rows; false once every pass is done.
        private static bool NextPass(ref Pass p, ReadOnlySpan<byte> passes, in Chunks c)
        {
            for (p.index += 4; p.index < passes.Length; p.index += 4)
            {
                p.x0 = passes[p.index];
                p.y0 = passes[p.index + 1];
                p.dx = passes[p.index + 2];
                p.dy = passes[p.index + 3];
                p.pw = (c.width - p.x0 + p.dx - 1) / p.dx;
                p.ph = (c.height - p.y0 + p.dy - 1) / p.dy;
                if (p.pw <= 0 || p.ph <= 0) continue;
                p.stride = (int)(((long)p.pw * c.channels * c.bitDepth + 7) / 8) + 1;
                p.direct = c.colorType == 6 && c.bitDepth == 8 && p.dx == 1;
                p.j = 0;
                return true;
            }
            return false;
        }

        // Unfilters row j of pass p, or of the whole image as the pass (0, 0, 1, 1), from its filter byte and bytes in
        // src into cur against the unfiltered row above it, and writes it out.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void DecodeRow(ReadOnlySpan<byte> src, Span<byte> cur, ReadOnlySpan<byte> prev, in Chunks c, uint[] palette, in Pass p, int bpp, byte[] rgba, byte[] packed, Span<byte> destination, int pitch, PixelLayout layout)
        {
            int outBpp = ImageCodec.BytesPerPixel(layout);
            bool wide = layout == PixelLayout.RGBA8 || layout == PixelLayout.BGRA8;
            bool fused = p.direct && layout == PixelLayout.RGBA8;
            Span<byte> line = cur.Slice(0, src.Length - 1);
            Span<byte> row = destination.Slice((p.y0 + p.j * p.dy) * pitch);
            Unfilter(src[0], src.Slice(1), line, prev.Slice(0, line.Length), fused ? row.Slice(p.x0 * 4, line.Length) : line, bpp);
            if (fused) return;
            if (p.direct) PixelRows.FromRgba(line, row.Slice(p.x0 * outBpp), p.pw, layout);
            else if (wide) Expand(line, row.Slice(p.x0 * 4), p.pw, p.dx, in c, palette, layout == PixelLayout.BGRA8);
            else
            {
                Expand(line, rgba, p.pw, 1, in c, palette, false);
                if (p.dx == 1) PixelRows.FromRgba(rgba, row.Slice(p.x0 * outBpp), p.pw, layout);
                else
                {
                    PixelRows.FromRgba(rgba, packed, p.pw, layout);
                    for (int i = 0; i < p.pw; i++)
                        packed.AsSpan(i * outBpp, outBpp).CopyTo(row.Slice((p.x0 + i * p.dx) * outBpp));
                }
            }
        }

        // Converts one unfiltered row of any colour type and depth to RGBA8 or BGRA8, pixel x landing at x * step * 4.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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
                            Vector128<byte> alpha = Vector128.Create(0xFF000000u).AsByte();
                            Vector128<byte> q0 = Vector128.Create((byte)0, 0, 0, 0xFF, 1, 1, 1, 0xFF, 2, 2, 2, 0xFF, 3, 3, 3, 0xFF);
                            Vector128<byte> q1 = Vector128.Create((byte)4, 4, 4, 0xFF, 5, 5, 5, 0xFF, 6, 6, 6, 0xFF, 7, 7, 7, 0xFF);
                            Vector128<byte> q2 = Vector128.Create((byte)8, 8, 8, 0xFF, 9, 9, 9, 0xFF, 10, 10, 10, 0xFF, 11, 11, 11, 0xFF);
                            Vector128<byte> q3 = Vector128.Create((byte)12, 12, 12, 0xFF, 13, 13, 13, 0xFF, 14, 14, 14, 0xFF, 15, 15, 15, 0xFF);

                            // 16 grey bytes copied into every 128-bit lane, each lane expanding its own four
                            if (step == 1 && Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
                            {
                                Vector512<byte> q = Vector512.Create(Vector256.Create(q0, q1), Vector256.Create(q2, q3));
                                Vector512<byte> opaque = Vector512.Create(Vector256.Create(alpha, alpha), Vector256.Create(alpha, alpha));
                                for (; x + 16 <= width; x += 16)
                                {
                                    Vector128<byte> l = Vector128.Load(src + x);
                                    Vector256<byte> g = Vector256.Create(l, l);
                                    (Avx512BW.Shuffle(Vector512.Create(g, g), q) | opaque).Store(dst + x * 4);
                                }
                            }
                            if (step == 1 && Vector256.IsHardwareAccelerated && Avx2.IsSupported)
                            {
                                Vector256<byte> qa = Vector256.Create(q0, q1), qb = Vector256.Create(q2, q3), opaque = Vector256.Create(alpha, alpha);
                                for (; x + 16 <= width; x += 16)
                                {
                                    Vector128<byte> l = Vector128.Load(src + x);
                                    Vector256<byte> g = Vector256.Create(l, l);
                                    byte* d = dst + x * 4;
                                    (Avx2.Shuffle(g, qa) | opaque).Store(d);
                                    (Avx2.Shuffle(g, qb) | opaque).Store(d + 32);
                                }
                            }
                            if (step == 1 && Vector128.IsHardwareAccelerated)
                            {
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
                            Vector128<byte> order = bgra
                                ? Vector128.Create((byte)2, 1, 0, 0xFF, 5, 4, 3, 0xFF, 8, 7, 6, 0xFF, 11, 10, 9, 0xFF)
                                : Vector128.Create((byte)0, 1, 2, 0xFF, 3, 4, 5, 0xFF, 6, 7, 8, 0xFF, 9, 10, 11, 0xFF);
                            Vector128<byte> alpha = Vector128.Create(0xFF000000u).AsByte();

                            // a dword permute gives each 128-bit lane its four pixels' 12 bytes, which it then expands
                            if (step == 1 && Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
                            {
                                Vector512<byte> lanes = Vector512.Create(Vector256.Create(order, order), Vector256.Create(order, order));
                                Vector512<byte> opaque = Vector512.Create(Vector256.Create(alpha, alpha), Vector256.Create(alpha, alpha));
                                Vector512<int> spread = Vector512.Create(0, 1, 2, 0, 3, 4, 5, 0, 6, 7, 8, 0, 9, 10, 11, 0);
                                for (; x + 22 <= width; x += 16)
                                    (Avx512BW.Shuffle(Avx512F.PermuteVar16x32(Vector512.Load(src + x * 3).AsInt32(), spread).AsByte(), lanes) | opaque).Store(dst + x * 4);
                            }
                            if (step == 1 && Vector256.IsHardwareAccelerated && Avx2.IsSupported)
                            {
                                Vector256<byte> lanes = Vector256.Create(order, order), opaque = Vector256.Create(alpha, alpha);
                                Vector256<int> spread = Vector256.Create(0, 1, 2, 0, 3, 4, 5, 0);
                                for (; x + 11 <= width; x += 8)
                                    (Avx2.Shuffle(Avx2.PermuteVar8x32(Vector256.Load(src + x * 3).AsInt32(), spread).AsByte(), lanes) | opaque).Store(dst + x * 4);
                            }
                            if (step == 1 && Vector128.IsHardwareAccelerated)
                            {
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
                            Vector128<byte> order = bgra
                                ? Vector128.Create((byte)5, 4, 3, 2, 1, 0, 7, 6, 13, 12, 11, 10, 9, 8, 15, 14)
                                : Vector128.Create((byte)1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14);
                            if (step == 1 && Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
                            {
                                Vector512<byte> lanes = Vector512.Create(Vector256.Create(order, order), Vector256.Create(order, order));
                                Vector512<uint> scale = Vector512.Create(255u), round = Vector512.Create(32895u);
                                for (; x + 8 <= width; x += 8)
                                {
                                    (Vector512<uint> lo, Vector512<uint> hi) = Vector512.Widen(Avx512BW.Shuffle(Vector512.Load(src + x * 8), lanes).AsUInt16());
                                    Vector512<ushort> narrow = Vector512.Narrow((lo * scale + round) >> 16, (hi * scale + round) >> 16);
                                    Vector512.Narrow(narrow, narrow).AsByte().GetLower().Store(dst + x * 4);
                                }
                            }
                            if (step == 1 && Vector256.IsHardwareAccelerated && Avx2.IsSupported)
                            {
                                Vector256<byte> lanes = Vector256.Create(order, order);
                                Vector256<uint> scale = Vector256.Create(255u), round = Vector256.Create(32895u);
                                for (; x + 4 <= width; x += 4)
                                {
                                    (Vector256<uint> lo, Vector256<uint> hi) = Vector256.Widen(Avx2.Shuffle(Vector256.Load(src + x * 8), lanes).AsUInt16());
                                    Vector256<ushort> narrow = Vector256.Narrow((lo * scale + round) >> 16, (hi * scale + round) >> 16);
                                    Vector256.Narrow(narrow, narrow).AsByte().GetLower().Store(dst + x * 4);
                                }
                            }
                            if (step == 1 && Vector128.IsHardwareAccelerated)
                            {
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
        // Unfilters the filtered row src into cur against prev, the unfiltered row above; at four bytes per pixel each
        // finished word also goes to dst, which is cur itself when the row is not stored straight out.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Unfilter(byte type, ReadOnlySpan<byte> src, Span<byte> cur, ReadOnlySpan<byte> prev, Span<byte> dst, int bpp)
        {
            int n = cur.Length;
            switch (type)
            {
                case 0:
                    src.CopyTo(cur);
                    if (dst != cur) src.CopyTo(dst);
                    break;
                case 1:
                    if (bpp == 4) Sub4(src, cur, dst);
                    else if (bpp == 8) Sub8(src, cur);
                    else
                        fixed (byte* s = src)
                        fixed (byte* p = cur)
                        {
                            for (int i = 0; i < bpp && i < n; i++) p[i] = s[i];
                            for (int i = bpp; i < n; i++) p[i] = (byte)(s[i] + p[i - bpp]);
                        }
                    break;
                case 2:
                    AddUp(src, cur, prev, dst);
                    break;
                case 3:
                    if (bpp == 4) Average4(src, cur, prev, dst);
                    else if (bpp == 8) Average8(src, cur, prev);
                    else
                        fixed (byte* s = src)
                        fixed (byte* p = cur)
                        fixed (byte* q = prev)
                        {
                            for (int i = 0; i < bpp && i < n; i++) p[i] = (byte)(s[i] + (q[i] >> 1));
                            for (int i = bpp; i < n; i++) p[i] = (byte)(s[i] + ((p[i - bpp] + q[i]) >> 1));
                        }
                    break;
                case 4:
                    if (bpp == 4 && Vector128.IsHardwareAccelerated) Paeth4(src, cur, prev, dst);
                    else if (bpp >= 3 && Vector128.IsHardwareAccelerated) PaethWide(src, cur, prev, bpp);
                    else
                    {
                        PaethScalar(src, cur, prev, bpp, 0);
                        if (dst != cur) cur.CopyTo(dst);
                    }
                    break;
                default:
                    throw new ImageFormatException($"PNG row filter {type} is invalid");
            }
        }

        // Sub at four bytes per pixel: a bytewise add of the left pixel, one 32-bit word at a time.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Sub4(ReadOnlySpan<byte> src, Span<byte> cur, Span<byte> dst)
        {
            fixed (byte* s = src)
            fixed (byte* p = cur)
            fixed (byte* d = dst)
            {
                uint a = 0;
                for (int i = 0; i + 4 <= cur.Length; i += 4)
                {
                    uint x = *(uint*)(s + i);
                    a = ((x & 0x7F7F7F7Fu) + (a & 0x7F7F7F7Fu)) ^ ((x ^ a) & 0x80808080u);
                    *(uint*)(p + i) = a;
                    *(uint*)(d + i) = a;
                }
            }
        }

        // Paeth at four bytes per pixel: a pixel's four channels widened to 16-bit lanes and predicted together.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Paeth4(ReadOnlySpan<byte> src, Span<byte> cur, ReadOnlySpan<byte> prev, Span<byte> dst)
        {
            fixed (byte* pSrc = src)
            fixed (byte* pCur = cur)
            fixed (byte* pPrev = prev)
            fixed (byte* pDst = dst)
            {
                Vector128<short> a = Vector128<short>.Zero, c = Vector128<short>.Zero;
                Vector128<short> low = Vector128.Create((short)0xFF);
                for (int i = 0; i + 4 <= cur.Length; i += 4)
                {
                    Vector128<short> b = Widen4(pPrev + i);
                    a = PaethStep(a, b, c, Widen4(pSrc + i), low);
                    uint word = Narrow4(a);
                    *(uint*)(pCur + i) = word;
                    *(uint*)(pDst + i) = word;
                    c = b;
                }
            }
        }

        // Paeth at four bytes per pixel on four rows at once; the last row overwrites prev.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Paeth4Rows(ReadOnlySpan<byte> src, int stride, Span<byte> prev, Span<byte> dst, int pitch)
        {
            fixed (byte* pSrc = src)
            fixed (byte* pPrev = prev)
            fixed (byte* pDst = dst)
            {
                byte* s0 = pSrc + 1, s1 = s0 + stride, s2 = s1 + stride, s3 = s2 + stride;
                byte* d0 = pDst, d1 = d0 + pitch, d2 = d1 + pitch, d3 = d2 + pitch;
                Vector128<short> a0 = Vector128<short>.Zero, a1 = a0, a2 = a0, a3 = a0, c0 = a0, c1 = a0, c2 = a0, c3 = a0;
                Vector128<short> low = Vector128.Create((short)0xFF);
                for (int i = 0; i + 4 <= prev.Length; i += 4)
                {
                    Vector128<short> b0 = Widen4(pPrev + i);
                    Vector128<short> n0 = PaethStep(a0, b0, c0, Widen4(s0 + i), low);
                    Vector128<short> n1 = PaethStep(a1, n0, c1, Widen4(s1 + i), low);
                    Vector128<short> n2 = PaethStep(a2, n1, c2, Widen4(s2 + i), low);
                    Vector128<short> n3 = PaethStep(a3, n2, c3, Widen4(s3 + i), low);
                    c0 = b0; c1 = n0; c2 = n1; c3 = n2;
                    a0 = n0; a1 = n1; a2 = n2; a3 = n3;
                    *(uint*)(d0 + i) = Narrow4(n0);
                    *(uint*)(d1 + i) = Narrow4(n1);
                    *(uint*)(d2 + i) = Narrow4(n2);
                    uint word = Narrow4(n3);
                    *(uint*)(d3 + i) = word;
                    *(uint*)(pPrev + i) = word;
                }
            }
        }

        // One pixel's prediction added to its filtered value, all four channels in 16-bit lanes.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<short> PaethStep(Vector128<short> a, Vector128<short> b, Vector128<short> c, Vector128<short> x, Vector128<short> low)
        {
            Vector128<short> bc = b - c, ac = a - c;
            Vector128<short> pa = Vector128.Abs(bc), pb = Vector128.Abs(ac), pc = Vector128.Abs(ac + bc);
            Vector128<short> useA = Vector128.LessThanOrEqual(pa, pb) & Vector128.LessThanOrEqual(pa, pc);
            Vector128<short> useB = Vector128.LessThanOrEqual(pb, pc);
            return (x + Vector128.ConditionalSelect(useA, a, Vector128.ConditionalSelect(useB, b, c))) & low;
        }

        private static Vector128<short> Widen4(byte* p) => Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(uint*)p).AsByte()).AsInt16();

        private static uint Narrow4(Vector128<short> v) => Vector128.Narrow(v.AsUInt16(), v.AsUInt16()).AsUInt32().ToScalar();

        // Sub at eight bytes per pixel: Sub4's bytewise add on one 64-bit word.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Sub8(ReadOnlySpan<byte> src, Span<byte> cur)
        {
            fixed (byte* s = src)
            fixed (byte* p = cur)
            {
                ulong a = 0;
                for (int i = 0; i + 8 <= cur.Length; i += 8)
                {
                    ulong x = *(ulong*)(s + i);
                    a = ((x & 0x7F7F7F7F7F7F7F7Ful) + (a & 0x7F7F7F7F7F7F7F7Ful)) ^ ((x ^ a) & 0x8080808080808080ul);
                    *(ulong*)(p + i) = a;
                }
            }
        }

        // Average at four bytes per pixel: per byte (a & b) + ((a ^ b) >> 1), then a bytewise add, one 32-bit word at a time.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Average4(ReadOnlySpan<byte> src, Span<byte> cur, ReadOnlySpan<byte> prev, Span<byte> dst)
        {
            fixed (byte* s = src)
            fixed (byte* p = cur)
            fixed (byte* q = prev)
            fixed (byte* d = dst)
            {
                uint a = 0;
                for (int i = 0; i + 4 <= cur.Length; i += 4)
                {
                    uint b = *(uint*)(q + i), x = *(uint*)(s + i);
                    uint m = (a & b) + (((a ^ b) & 0xFEFEFEFEu) >> 1);
                    a = ((x & 0x7F7F7F7Fu) + (m & 0x7F7F7F7Fu)) ^ ((x ^ m) & 0x80808080u);
                    *(uint*)(p + i) = a;
                    *(uint*)(d + i) = a;
                }
            }
        }

        // Average4 on one 64-bit word.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Average8(ReadOnlySpan<byte> src, Span<byte> cur, ReadOnlySpan<byte> prev)
        {
            fixed (byte* s = src)
            fixed (byte* p = cur)
            fixed (byte* q = prev)
            {
                ulong a = 0;
                for (int i = 0; i + 8 <= cur.Length; i += 8)
                {
                    ulong b = *(ulong*)(q + i), x = *(ulong*)(s + i);
                    ulong m = (a & b) + (((a ^ b) & 0xFEFEFEFEFEFEFEFEul) >> 1);
                    a = ((x & 0x7F7F7F7F7F7F7F7Ful) + (m & 0x7F7F7F7F7F7F7F7Ful)) ^ ((x ^ m) & 0x8080808080808080ul);
                    *(ulong*)(p + i) = a;
                }
            }
        }

        // Paeth at three to eight bytes per pixel: Paeth4 over eight lanes, lanes past the pixel unpredicted so they store back as read.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void PaethWide(ReadOnlySpan<byte> src, Span<byte> cur, ReadOnlySpan<byte> prev, int bpp)
        {
            int i = 0;
            fixed (byte* pSrc = src)
            fixed (byte* pCur = cur)
            fixed (byte* pPrev = prev)
            {
                Vector128<short> a = Vector128<short>.Zero, c = Vector128<short>.Zero;
                Vector128<short> low = Vector128.Create((short)0xFF);
                Vector128<short> pixel = Vector128.LessThan(Vector128.Create((short)0, 1, 2, 3, 4, 5, 6, 7), Vector128.Create((short)bpp));
                for (; i + 8 <= cur.Length; i += bpp)
                {
                    Vector128<short> b = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)(pPrev + i)).AsByte()).AsInt16();
                    Vector128<short> x = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)(pSrc + i)).AsByte()).AsInt16();
                    Vector128<short> pa = Vector128.Abs(b - c), pb = Vector128.Abs(a - c), pc = Vector128.Abs(a + b - c - c);
                    Vector128<short> useA = Vector128.LessThanOrEqual(pa, pb) & Vector128.LessThanOrEqual(pa, pc);
                    Vector128<short> useB = Vector128.LessThanOrEqual(pb, pc);
                    a = (x + (Vector128.ConditionalSelect(useA, a, Vector128.ConditionalSelect(useB, b, c)) & pixel)) & low;
                    *(ulong*)(pCur + i) = Vector128.Narrow(a.AsUInt16(), a.AsUInt16()).AsUInt64().ToScalar();
                    c = b;
                }
            }
            PaethScalar(src, cur, prev, bpp, i);
        }

        // Paeth one byte at a time from start.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void PaethScalar(ReadOnlySpan<byte> src, Span<byte> cur, ReadOnlySpan<byte> prev, int bpp, int start)
        {
            fixed (byte* s = src)
            fixed (byte* p = cur)
            fixed (byte* q = prev)
                for (int i = start; i < cur.Length; i++)
                {
                    int a = i >= bpp ? p[i - bpp] : 0, b = q[i], cc = i >= bpp ? q[i - bpp] : 0;
                    int pa = Math.Abs(b - cc), pb = Math.Abs(a - cc), pc = Math.Abs(a + b - 2 * cc);
                    p[i] = (byte)(s[i] + (pa <= pb && pa <= pc ? a : pb <= pc ? b : cc));
                }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void AddUp(ReadOnlySpan<byte> src, Span<byte> cur, ReadOnlySpan<byte> prev, Span<byte> dst)
        {
            int i = 0;
            if (Vector512.IsHardwareAccelerated)
                for (; i + 64 <= cur.Length; i += 64)
                {
                    Vector512<byte> v = Vector512.Create(src.Slice(i, 64)) + Vector512.Create(prev.Slice(i, 64));
                    v.CopyTo(cur.Slice(i));
                    v.CopyTo(dst.Slice(i));
                }
            if (Vector256.IsHardwareAccelerated)
                for (; i + 32 <= cur.Length; i += 32)
                {
                    Vector256<byte> v = Vector256.Create(src.Slice(i, 32)) + Vector256.Create(prev.Slice(i, 32));
                    v.CopyTo(cur.Slice(i));
                    v.CopyTo(dst.Slice(i));
                }
            if (Vector128.IsHardwareAccelerated)
                for (; i + 16 <= cur.Length; i += 16)
                {
                    Vector128<byte> v = Vector128.Create(src.Slice(i, 16)) + Vector128.Create(prev.Slice(i, 16));
                    v.CopyTo(cur.Slice(i));
                    v.CopyTo(dst.Slice(i));
                }
            for (; i < cur.Length; i++)
            {
                byte v = (byte)(src[i] + prev[i]);
                cur[i] = v;
                dst[i] = v;
            }
        }
        #endregion
    }
}
