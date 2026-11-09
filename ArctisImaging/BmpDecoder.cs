using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Arctis.Imaging
{
    // Windows bitmaps: core, info and V4/V5 headers; 1/4/8-bit palettes, RLE4/RLE8, 16/24/32-bit, plain or bit fields.
    internal static class BmpDecoder
    {
        internal const int FileHeaderSize = 14;
        private const uint Rgb = 0, Rle8 = 1, Rle4 = 2, BitFields = 3, AlphaBitFields = 6;

        private struct Info
        {
            public int headerSize, width, height, bitCount, paletteCount, masksAfterHeader;
            public bool topDown;
            public uint compression, rMask, gMask, bMask, aMask;
        }

        internal static bool TryReadFileHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (data.Length < FileHeaderSize + 12 || data[0] != (byte)'B' || data[1] != (byte)'M') return false;
            if (!TryReadDibHeader(data.Slice(FileHeaderSize), out ImageHeader dib)) return false;
            header = new ImageHeader(ImageFormat.Bmp, dib.Width, dib.Height, dib.Channels, dib.BitDepth, dib.HasAlpha);
            return true;
        }

        internal static bool TryReadDibHeader(ReadOnlySpan<byte> dib, out ImageHeader header)
        {
            header = default;
            if (!TryReadInfo(dib, out Info info)) return false;
            int channels = info.bitCount == 32 ? 4 : 3;
            int depth = info.bitCount == 16 ? 5 : 8;
            header = new ImageHeader(ImageFormat.Dib, info.width, info.height, channels, depth, info.bitCount == 32);
            return true;
        }

        // Decodes pixel rows; pixelOffset is from the start of the DIB header, or -1 to follow the palette.
        internal static void Decode(ReadOnlySpan<byte> dib, int pixelOffset, Span<byte> destination, int pitch, PixelLayout layout) =>
            Decode(dib, pixelOffset, destination, pitch, layout, false);

        // An icon entry's DIB: its header holds twice the height, and a 1-bit AND mask follows the colour rows.
        internal static void DecodeIcon(ReadOnlySpan<byte> dib, Span<byte> destination, int pitch, PixelLayout layout) =>
            Decode(dib, -1, destination, pitch, layout, true);

        private static void Decode(ReadOnlySpan<byte> dib, int pixelOffset, Span<byte> destination, int pitch, PixelLayout layout, bool icon)
        {
            if (!TryReadInfo(dib, out Info info)) throw new ImageFormatException("bitmap header is invalid");
            if (icon)
            {
                info.height /= 2;
                info.topDown = false;
            }
            bool rle = info.compression == Rle8 || info.compression == Rle4;
            if (info.compression != Rgb && info.compression != BitFields && info.compression != AlphaBitFields && !rle)
                throw new NotSupportedException($"bitmap compression {info.compression} is not supported");
            if ((info.compression == BitFields || info.compression == AlphaBitFields) && info.bitCount != 16 && info.bitCount != 32)
                throw new ImageFormatException("bitmap bit fields need 16 or 32 bits per pixel");
            if (rle && info.bitCount != (info.compression == Rle8 ? 8 : 4))
                throw new ImageFormatException($"bitmap RLE{(info.compression == Rle8 ? 8 : 4)} needs {(info.compression == Rle8 ? 8 : 4)} bits per pixel");

            int entrySize = info.headerSize == 12 ? 3 : 4;
            int paletteStart = info.headerSize + info.masksAfterHeader;
            if (pixelOffset < 0) pixelOffset = paletteStart + info.paletteCount * entrySize;
            long stride = ((long)info.width * info.bitCount + 31) / 32 * 4;
            long rowBytes = ((long)info.width * info.bitCount + 7) / 8;
            if (pixelOffset < paletteStart || (rle ? pixelOffset > dib.Length : pixelOffset + stride * (info.height - 1) + rowBytes > dib.Length))
                throw new ImageFormatException("bitmap pixel data runs past the end");
            if (paletteStart + (long)info.paletteCount * entrySize > dib.Length) throw new ImageFormatException("bitmap palette runs past the end");

            // palette entries as destination pixels; entries past the count are opaque black
            Span<uint> palette = stackalloc uint[256];
            for (int i = 0; info.bitCount <= 8 && i < 256; i++)
            {
                int r = 0, g = 0, b = 0;
                if (i < info.paletteCount)
                {
                    ReadOnlySpan<byte> e = dib.Slice(paletteStart + i * entrySize);
                    r = e[2];
                    g = e[1];
                    b = e[0];
                }
                palette[i] = layout switch
                {
                    PixelLayout.BGRA8 => (uint)(b | g << 8 | r << 16) | 0xFF000000u,
                    PixelLayout.R8 => PixelRows.Luma(r, g, b),
                    PixelLayout.RG8 => PixelRows.Luma(r, g, b) | 0xFF00u,
                    _ => (uint)(r | g << 8 | b << 16) | 0xFF000000u,
                };
            }
            if (rle)
            {
                DecodeRle(dib.Slice(pixelOffset), info, palette, destination, pitch, layout);
                return;
            }

            ReadOnlySpan<byte> pixels = dib.Slice(pixelOffset);

            // a 32-bit bitmap whose alpha is all zero has no alpha, not a transparent picture, so it decodes opaque
            bool anyAlpha = info.bitCount == 32 && info.aMask != 0 && layout != PixelLayout.R8 && PixelRows.HasAnyAlpha(pixels.Slice(0, (int)stride * info.height), info.aMask);
            if (info.bitCount == 32 && !anyAlpha) info.aMask = 0;
            if (!DecodeDirect(pixels, info, (int)stride, destination, pitch, layout) && !DecodeIndexed(pixels, info, (int)stride, palette, destination, pitch, layout))
                DecodeRows(pixels, info, (int)stride, destination, pitch, layout);

            // an icon's AND mask gives the alpha unless its 32-bit colour rows carry alpha of their own
            long maskStart = pixelOffset + stride * info.height, maskStride = ((long)info.width + 31) / 32 * 4;
            if (icon && !anyAlpha && maskStart + maskStride * info.height <= dib.Length)
                for (int row = 0; row < info.height; row++)
                    ApplyMask(dib.Slice((int)(maskStart + row * maskStride)), destination.Slice((info.height - 1 - row) * pitch), info.width, layout, info.aMask == 0);
        }

        // Rows of 32-bit BGRA or BGRX, 24-bit BGR and 16-bit 555/565 straight into the destination; false for any other form.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static bool DecodeDirect(ReadOnlySpan<byte> pixels, in Info info, int stride, Span<byte> destination, int pitch, PixelLayout layout)
        {
            int width = info.width, height = info.height;
            switch (info.bitCount)
            {
                case 32:
                    if (info.rMask != 0x00FF0000 || info.gMask != 0x0000FF00 || info.bMask != 0x000000FF || info.aMask != 0xFF000000 && info.aMask != 0) return false;
                    break;
                case 24:
                    break;
                case 16:
                    if (info.aMask != 0 || info.bMask != 0x1F || !(info.rMask == 0x7C00 && info.gMask == 0x03E0 || info.rMask == 0xF800 && info.gMask == 0x07E0)) return false;
                    break;
                default:
                    return false;
            }
            bool wideGreen = info.gMask == 0x07E0;
            for (int row = 0; row < height; row++)
            {
                ReadOnlySpan<byte> src = pixels.Slice(row * stride);
                Span<byte> dst = destination.Slice((info.topDown ? row : height - 1 - row) * pitch);
                if (info.bitCount == 32) PixelRows.Row32(src, dst, width, layout, info.aMask == 0);
                else if (info.bitCount == 24) PixelRows.Row24(src, dst, width, layout);
                else PixelRows.Row16(src, dst, width, layout, wideGreen);
            }
            return true;
        }

        // Rows of 1/4/8-bit palette indices straight into the destination; false for any other depth.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static bool DecodeIndexed(ReadOnlySpan<byte> pixels, in Info info, int stride, ReadOnlySpan<uint> palette, Span<byte> destination, int pitch, PixelLayout layout)
        {
            int bits = info.bitCount, width = info.width, height = info.height, size = ImageCodec.BytesPerPixel(layout);
            if (bits > 8) return false;
            int rowBytes = (width * bits + 7) >> 3;

            // palette entries 0-15 as three channel tables in destination byte order
            Span<byte> tables = stackalloc byte[48];
            for (int i = 0; i < 16; i++)
            {
                tables[i] = (byte)palette[i];
                tables[16 + i] = (byte)(palette[i] >> 8);
                tables[32 + i] = (byte)(palette[i] >> 16);
            }
            byte[]? line = bits < 8 ? ArrayPool<byte>.Shared.Rent(rowBytes + 64) : null;
            byte[]? idx = bits < 8 ? ArrayPool<byte>.Shared.Rent(width + 256) : null;
            try
            {
                for (int row = 0; row < height; row++)
                {
                    ReadOnlySpan<byte> src = pixels.Slice(row * stride, rowBytes);
                    Span<byte> dst = destination.Slice((info.topDown ? row : height - 1 - row) * pitch);
                    if (bits == 8)
                    {
                        Lookup(src, palette, width, dst, size);
                        continue;
                    }
                    src.CopyTo(line);
                    if (bits == 1) PixelRows.BitIndices(line, rowBytes, 1, idx, width);
                    else PixelRows.PackedIndices(line, 4, idx, width);
                    PixelRows.LookupIndices(idx, tables, width, dst, layout);
                }
            }
            finally
            {
                if (line != null) ArrayPool<byte>.Shared.Return(line);
                if (idx != null) ArrayPool<byte>.Shared.Return(idx);
            }
            return true;
        }

        // Bit-field rows of 16 or 32 bits straight into the destination, for the masks the direct rows do not take.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void DecodeRows(ReadOnlySpan<byte> pixels, in Info info, int stride, Span<byte> destination, int pitch, PixelLayout layout)
        {
            bool bgra = layout == PixelLayout.BGRA8, wide = info.bitCount == 32;
            Field r = new Field(info.rMask), g = new Field(info.gMask), b = new Field(info.bMask), a = new Field(info.aMask);
            Field first = bgra ? b : r, third = bgra ? r : b;
            int size = ImageCodec.BytesPerPixel(layout), rowBytes = info.width * (info.bitCount / 8);
            for (int row = 0; row < info.height; row++)
                RowFields(pixels.Slice(row * stride, rowBytes), destination.Slice((info.topDown ? row : info.height - 1 - row) * pitch), info.width, wide, first, g, third, a, size);
        }

        // Writes width bit-field pixels of 16 or 32 bits as pixels of size bytes; first and third are the channels
        // that land in bytes 0 and 2, red and blue for luma.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void RowFields(ReadOnlySpan<byte> src, Span<byte> dst, int width, bool wide, in Field first, in Field g, in Field third, in Field a, int size)
        {
            if (src.Length < width * (wide ? 4 : 2) || dst.Length < width * size) throw new ArgumentException("src or dst is shorter than width pixels");
            ref byte s = ref MemoryMarshal.GetReference(src);
            ref byte d = ref MemoryMarshal.GetReference(dst);
            nuint x = 0, w = (nuint)width;

            // each tier loads its pixels as 32-bit lanes, 16-bit pixels widened, then extracts and packs two vectors
            if (Vector512.IsHardwareAccelerated)
                for (; x + 32 <= w; x += 32)
                {
                    Vector512<uint> v0, v1;
                    if (wide)
                    {
                        v0 = Vector512.LoadUnsafe(ref Unsafe.As<byte, uint>(ref s), x);
                        v1 = Vector512.LoadUnsafe(ref Unsafe.As<byte, uint>(ref s), x + 16);
                    }
                    else (v0, v1) = Vector512.Widen(Vector512.LoadUnsafe(ref Unsafe.As<byte, ushort>(ref s), x));
                    Emit(v0, ref d, x, first, g, third, a, size);
                    Emit(v1, ref d, x + 16, first, g, third, a, size);
                }
            if (Vector256.IsHardwareAccelerated)
                for (; x + 16 <= w; x += 16)
                {
                    Vector256<uint> v0, v1;
                    if (wide)
                    {
                        v0 = Vector256.LoadUnsafe(ref Unsafe.As<byte, uint>(ref s), x);
                        v1 = Vector256.LoadUnsafe(ref Unsafe.As<byte, uint>(ref s), x + 8);
                    }
                    else (v0, v1) = Vector256.Widen(Vector256.LoadUnsafe(ref Unsafe.As<byte, ushort>(ref s), x));
                    Emit(v0, ref d, x, first, g, third, a, size);
                    Emit(v1, ref d, x + 8, first, g, third, a, size);
                }
            if (Vector128.IsHardwareAccelerated)
                for (; x + 8 <= w; x += 8)
                {
                    Vector128<uint> v0, v1;
                    if (wide)
                    {
                        v0 = Vector128.LoadUnsafe(ref Unsafe.As<byte, uint>(ref s), x);
                        v1 = Vector128.LoadUnsafe(ref Unsafe.As<byte, uint>(ref s), x + 4);
                    }
                    else (v0, v1) = Vector128.Widen(Vector128.LoadUnsafe(ref Unsafe.As<byte, ushort>(ref s), x));
                    Emit(v0, ref d, x, first, g, third, a, size);
                    Emit(v1, ref d, x + 4, first, g, third, a, size);
                }
            for (; x < w; x++)
            {
                uint v = wide ? Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref s, x * 4)) : Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref s, x * 2));
                byte c0 = first.Extract(v), c1 = g.Extract(v), c2 = third.Extract(v), alpha = a.mask == 0 ? (byte)255 : a.Extract(v);
                if (size == 4) Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, x * 4), c0 | (uint)c1 << 8 | (uint)c2 << 16 | (uint)alpha << 24);
                else if (size == 2) Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, x * 2), (ushort)(PixelRows.Luma(c0, c1, c2) | alpha << 8));
                else Unsafe.Add(ref d, x) = PixelRows.Luma(c0, c1, c2);
            }
        }

        // One vector of pixels from 32-bit lanes into the destination at pixel x.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Emit(Vector512<uint> v, ref byte d, nuint x, in Field first, in Field g, in Field third, in Field a, int size)
        {
            Vector512<uint> c0 = first.Extract(v), c1 = g.Extract(v), c2 = third.Extract(v);
            if (size == 1)
            {
                Vector512<ushort> grey = Vector512.Narrow(Luma(c0, c1, c2), Luma(c0, c1, c2));
                Vector512.Narrow(grey, grey).GetLower().GetLower().StoreUnsafe(ref d, x);
                return;
            }
            Vector512<uint> alpha = a.mask == 0 ? Vector512.Create(255u) : a.Extract(v);
            if (size == 4)
            {
                (c0 | c1 << 8 | c2 << 16 | alpha << 24).StoreUnsafe(ref Unsafe.As<byte, uint>(ref d), x);
                return;
            }
            Vector512<uint> pair = Luma(c0, c1, c2) | alpha << 8;
            Vector512.Narrow(pair, pair).GetLower().StoreUnsafe(ref Unsafe.As<byte, ushort>(ref d), x);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Emit(Vector256<uint> v, ref byte d, nuint x, in Field first, in Field g, in Field third, in Field a, int size)
        {
            Vector256<uint> c0 = first.Extract(v), c1 = g.Extract(v), c2 = third.Extract(v);
            if (size == 1)
            {
                Vector256<ushort> grey = Vector256.Narrow(Luma(c0, c1, c2), Luma(c0, c1, c2));
                Vector256.Narrow(grey, grey).GetLower().GetLower().StoreUnsafe(ref d, x);
                return;
            }
            Vector256<uint> alpha = a.mask == 0 ? Vector256.Create(255u) : a.Extract(v);
            if (size == 4)
            {
                (c0 | c1 << 8 | c2 << 16 | alpha << 24).StoreUnsafe(ref Unsafe.As<byte, uint>(ref d), x);
                return;
            }
            Vector256<uint> pair = Luma(c0, c1, c2) | alpha << 8;
            Vector256.Narrow(pair, pair).GetLower().StoreUnsafe(ref Unsafe.As<byte, ushort>(ref d), x);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Emit(Vector128<uint> v, ref byte d, nuint x, in Field first, in Field g, in Field third, in Field a, int size)
        {
            Vector128<uint> c0 = first.Extract(v), c1 = g.Extract(v), c2 = third.Extract(v);
            if (size == 1)
            {
                Vector128<ushort> grey = Vector128.Narrow(Luma(c0, c1, c2), Luma(c0, c1, c2));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, x), Vector128.Narrow(grey, grey).AsUInt32().ToScalar());
                return;
            }
            Vector128<uint> alpha = a.mask == 0 ? Vector128.Create(255u) : a.Extract(v);
            if (size == 4)
            {
                (c0 | c1 << 8 | c2 << 16 | alpha << 24).StoreUnsafe(ref Unsafe.As<byte, uint>(ref d), x);
                return;
            }
            Vector128<uint> pair = Luma(c0, c1, c2) | alpha << 8;
            Vector128.Narrow(pair, pair).GetLower().StoreUnsafe(ref Unsafe.As<byte, ushort>(ref d), x);
        }

        // Rec. 709 luma of 8-bit red, green and blue lanes, as PixelRows.Luma.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<uint> Luma(Vector512<uint> r, Vector512<uint> g, Vector512<uint> b) => (r * (uint)PixelRows.LumaR + g * (uint)PixelRows.LumaG + b * (uint)PixelRows.LumaB + Vector512.Create(32768u)) >> 16;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<uint> Luma(Vector256<uint> r, Vector256<uint> g, Vector256<uint> b) => (r * (uint)PixelRows.LumaR + g * (uint)PixelRows.LumaG + b * (uint)PixelRows.LumaB + Vector256.Create(32768u)) >> 16;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<uint> Luma(Vector128<uint> r, Vector128<uint> g, Vector128<uint> b) => (r * (uint)PixelRows.LumaR + g * (uint)PixelRows.LumaG + b * (uint)PixelRows.LumaB + Vector128.Create(32768u)) >> 16;

        // RLE8 or RLE4 runs, literal runs and escapes straight into the destination; pixels a delta or end of line skips
        // are cleared to transparent black, and a stream cut short decodes what it holds.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void DecodeRle(ReadOnlySpan<byte> src, in Info info, ReadOnlySpan<uint> palette, Span<byte> destination, int pitch, PixelLayout layout)
        {
            int width = info.width, size = ImageCodec.BytesPerPixel(layout);
            if (destination.Length < (long)(info.height - 1) * pitch + width * size) throw new ArgumentException("destination is shorter than the bitmap");
            bool four = info.compression == Rle4;
            Span<byte> nibbles = stackalloc byte[256];
            long filled = 0;
            int i = 0, x = 0, row = 0;
            while (i + 1 < src.Length && row < info.height)
            {
                int n = src[i], v = src[i + 1];
                i += 2;

                // short runs and literals inside the row write a fixed 32 or 16 pixels; those past the count are
                // overwritten by the next write or cleared as a skipped gap
                if (x + 32 <= width && (n > 0 ? n <= 32 : v >= 3 && v <= 16 && i + 16 <= src.Length))
                {
                    long at = (long)row * width + x;
                    if (at > filled) ClearRange(destination, filled, at, info, pitch, size);
                    ref byte d = ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), (info.topDown ? row : info.height - 1 - row) * pitch + x * size);
                    if (n > 0)
                    {
                        StoreRepeated(ref d, Pattern(palette[four ? v >> 4 : v], palette[four ? v & 15 : v], size), (nuint)(32 * size));
                        filled = at + n;
                        x += n;
                        continue;
                    }
                    ShortLiteral(ref Unsafe.Add(ref MemoryMarshal.GetReference(src), i), palette, ref d, four, size);
                    filled = at + v;
                    x += v;
                    i += ((four ? (v + 1) / 2 : v) + 1) & ~1;
                    continue;
                }
                if (n > 0)
                {
                    if (x < width)
                    {
                        int count = Math.Min(n, width - x);
                        FillRun(Reach(destination, ref filled, x, row, count, info, pitch, size), count, palette[four ? v >> 4 : v], palette[four ? v & 15 : v], size);
                    }
                    x += n;
                }
                else if (v == 0)
                {
                    x = 0;
                    row++;
                }
                else if (v == 1) break;
                else if (v == 2)
                {
                    if (i + 1 >= src.Length) break;
                    x += src[i];
                    row += src[i + 1];
                    i += 2;
                }
                else
                {
                    int bytes = four ? (v + 1) / 2 : v;
                    if (i + bytes > src.Length) throw new ImageFormatException("bitmap RLE literal runs past the end");
                    if (x < width)
                    {
                        int count = Math.Min(v, width - x);
                        Span<byte> dst = Reach(destination, ref filled, x, row, count, info, pitch, size);
                        if (!four) Lookup(src.Slice(i, count), palette, count, dst, size);
                        else
                        {
                            for (int k = 0; k < count; k++) nibbles[k] = (byte)((k & 1) == 0 ? src[i + (k >> 1)] >> 4 : src[i + (k >> 1)] & 15);
                            Lookup(nibbles, palette, count, dst, size);
                        }
                    }
                    x += v;
                    i += (bytes + 1) & ~1;
                }
            }
            ClearRange(destination, filled, (long)width * info.height, info, pitch, size);
        }

        // The destination's count pixels at stream pixel (x, row), after clearing every pixel the stream skipped to get there.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Span<byte> Reach(Span<byte> destination, ref long filled, int x, int row, int count, in Info info, int pitch, int size)
        {
            long at = (long)row * info.width + x;
            if (at > filled) ClearRange(destination, filled, at, info, pitch, size);
            filled = at + count;
            return destination.Slice((info.topDown ? row : info.height - 1 - row) * pitch + x * size, count * size);
        }

        // Zeroes stream pixels from raster position from up to to.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ClearRange(Span<byte> destination, long from, long to, in Info info, int pitch, int size)
        {
            while (from < to)
            {
                int row = (int)(from / info.width), x = (int)(from % info.width), count = (int)Math.Min(info.width - x, to - from);
                destination.Slice((info.topDown ? row : info.height - 1 - row) * pitch + x * size, count * size).Clear();
                from += count;
            }
        }

        // Writes count pixels alternating c0 and c1, starting with c0.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void FillRun(Span<byte> dst, int count, uint c0, uint c1, int size)
        {
            int pairs = count >> 1;
            if (size == 4) MemoryMarshal.Cast<byte, ulong>(dst.Slice(0, pairs * 8)).Fill(c0 | (ulong)c1 << 32);
            else if (size == 2) MemoryMarshal.Cast<byte, uint>(dst.Slice(0, pairs * 4)).Fill((c0 & 0xFFFF) | c1 << 16);
            else MemoryMarshal.Cast<byte, ushort>(dst.Slice(0, pairs * 2)).Fill((ushort)((c0 & 0xFF) | (c1 & 0xFF) << 8));
            if ((count & 1) == 0) return;
            if (size == 4) BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(pairs * 8), c0);
            else if (size == 2) BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(pairs * 4), (ushort)c0);
            else dst[pairs * 2] = (byte)c0;
        }

        // Two pixels c0, c1 of size bytes repeated across 8 bytes.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Pattern(uint c0, uint c1, int size) => size == 4 ? c0 | (ulong)c1 << 32
            : size == 2 ? ((c0 & 0xFFFF) | c1 << 16) * 0x0000000100000001ul : ((c0 & 0xFF) | (c1 & 0xFF) << 8) * 0x0001000100010001ul;

        // Writes bytes (a multiple of 32) of a repeating 8-byte pattern.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StoreRepeated(ref byte d, ulong pattern, nuint bytes)
        {
            if (Vector512.IsHardwareAccelerated && bytes >= 64)
            {
                Vector512<byte> wide = Vector512.Create(pattern).AsByte();
                for (nuint o = 0; o < bytes; o += 64) wide.StoreUnsafe(ref d, o);
                return;
            }
            if (Vector256.IsHardwareAccelerated)
            {
                Vector256<byte> wide = Vector256.Create(pattern).AsByte();
                for (nuint o = 0; o < bytes; o += 32) wide.StoreUnsafe(ref d, o);
                return;
            }
            Vector128<byte> narrow = Vector128.Create(pattern).AsByte();
            for (nuint o = 0; o < bytes; o += 16) narrow.StoreUnsafe(ref d, o);
        }

        // Writes 16 pixels from 16 index bytes (RLE8) or 8 bytes of index pairs (RLE4) at s.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ShortLiteral(ref byte s, ReadOnlySpan<uint> palette, ref byte d, bool four, int size)
        {
            ref uint p = ref MemoryMarshal.GetReference(palette);
            if (four)
            {
                for (nuint k = 0; k < 8; k++)
                {
                    int b = Unsafe.Add(ref s, k);
                    uint c0 = Unsafe.Add(ref p, b >> 4), c1 = Unsafe.Add(ref p, b & 15);
                    if (size == 4) Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, k * 8), c0 | (ulong)c1 << 32);
                    else if (size == 2) Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, k * 4), (c0 & 0xFFFF) | c1 << 16);
                    else Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, k * 2), (ushort)((c0 & 0xFF) | (c1 & 0xFF) << 8));
                }
                return;
            }
            for (nuint k = 0; k < 16; k++)
            {
                uint c = Unsafe.Add(ref p, Unsafe.Add(ref s, k));
                if (size == 4) Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, k * 4), c);
                else if (size == 2) Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, k * 2), (ushort)c);
                else Unsafe.Add(ref d, k) = (byte)c;
            }
        }

        // Writes count palette entries, one per index byte, as pixels of size bytes.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Lookup(ReadOnlySpan<byte> indices, ReadOnlySpan<uint> palette, int count, Span<byte> row, int size)
        {
            if (size == 4)
            {
                PixelRows.LookupRow(indices, palette, count, row);
                return;
            }
            if (indices.Length < count || palette.Length < 256 || row.Length < count * size) throw new ArgumentException("indices or row is shorter than count pixels");
            ref byte s = ref MemoryMarshal.GetReference(indices);
            ref uint p = ref MemoryMarshal.GetReference(palette);
            ref byte d = ref MemoryMarshal.GetReference(row);
            if (size == 2)
                for (nuint x = 0; x < (nuint)count; x++) Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, x * 2), (ushort)Unsafe.Add(ref p, Unsafe.Add(ref s, x)));
            else
                for (nuint x = 0; x < (nuint)count; x++) Unsafe.Add(ref d, x) = (byte)Unsafe.Add(ref p, Unsafe.Add(ref s, x));
        }

        // Alpha from one mask row: a set bit is transparent; rows already opaque skip the mask's zero bytes.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ApplyMask(ReadOnlySpan<byte> mask, Span<byte> row, int width, PixelLayout layout, bool opaque)
        {
            int at = layout == PixelLayout.RG8 ? 1 : 3, size = layout == PixelLayout.RG8 ? 2 : 4;
            if (layout == PixelLayout.R8) return;
            if (opaque)
            {
                int bytes = (width + 7) >> 3;
                for (int i = 0; i < bytes; i++)
                {
                    int m = mask[i];
                    if (m == 0) continue;
                    for (int x = i * 8, end = Math.Min(x + 8, width); x < end; x++)
                        row[x * size + at] = (byte)(((m >> (7 - (x & 7))) & 1) - 1);
                }
                return;
            }
            for (int x = 0; x < width; x++)
                row[x * size + at] = (mask[x >> 3] & (0x80 >> (x & 7))) != 0 ? (byte)0 : (byte)255;
        }

        private static bool TryReadInfo(ReadOnlySpan<byte> dib, out Info info)
        {
            info = default;
            if (dib.Length < 12) return false;
            int headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
            info.headerSize = headerSize;
            if (headerSize == 12)
            {
                info.width = BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(4));
                info.height = BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(6));
                info.bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(10));
                if (info.bitCount <= 8) info.paletteCount = 1 << info.bitCount;
            }
            else if (headerSize >= 40 && headerSize <= dib.Length)
            {
                info.width = BinaryPrimitives.ReadInt32LittleEndian(dib.Slice(4));
                int height = BinaryPrimitives.ReadInt32LittleEndian(dib.Slice(8));
                if (height == int.MinValue) return false;
                info.topDown = height < 0;
                info.height = Math.Abs(height);
                info.bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(14));
                info.compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(16));
                uint used = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(32));
                if (info.bitCount <= 8) info.paletteCount = used != 0 ? (int)Math.Min(used, 256u) : 1 << info.bitCount;

                ReadOnlySpan<byte> masks = default;
                if (headerSize >= 52) masks = dib.Slice(40, headerSize >= 56 ? 16 : 12);
                else if (info.compression == BitFields || info.compression == AlphaBitFields)
                {
                    info.masksAfterHeader = info.compression == AlphaBitFields ? 16 : 12;
                    if (dib.Length < 40 + info.masksAfterHeader) return false;
                    masks = dib.Slice(40, info.masksAfterHeader);
                }
                if (info.compression != Rgb && masks.Length >= 12)
                {
                    info.rMask = BinaryPrimitives.ReadUInt32LittleEndian(masks);
                    info.gMask = BinaryPrimitives.ReadUInt32LittleEndian(masks.Slice(4));
                    info.bMask = BinaryPrimitives.ReadUInt32LittleEndian(masks.Slice(8));
                    if (masks.Length >= 16) info.aMask = BinaryPrimitives.ReadUInt32LittleEndian(masks.Slice(12));
                }
            }
            else return false;

            if (info.bitCount != 1 && info.bitCount != 4 && info.bitCount != 8 && info.bitCount != 16 && info.bitCount != 24 && info.bitCount != 32) return false;
            if (info.compression == Rgb && info.bitCount == 16)
            {
                info.rMask = 0x7C00;
                info.gMask = 0x03E0;
                info.bMask = 0x001F;
            }
            else if (info.compression == Rgb && info.bitCount == 32)
            {
                info.rMask = 0x00FF0000;
                info.gMask = 0x0000FF00;
                info.bMask = 0x000000FF;
                info.aMask = 0xFF000000;
            }
            return true;
        }

        // One colour channel of a bit-field pixel, widened to 8 bits by bit replication.
        private readonly struct Field
        {
            public readonly uint mask;
            private readonly int shift;
            private readonly int bits;

            // Extract as lane shifts: ((v >> shift) & low) << up >> down, then OR-ed with itself shifted by s1, s2, s3
            private readonly uint low;
            private readonly int up, down, s1, s2, s3;

            public Field(uint mask)
            {
                this.mask = mask;
                shift = mask == 0 ? 0 : BitOperations.TrailingZeroCount(mask);
                bits = BitOperations.PopCount(mask);
                low = mask >> shift;
                bool narrow = bits > 0 && bits < 8;
                up = narrow ? 8 - bits : 0;
                down = bits > 8 ? bits - 8 : 0;
                s1 = narrow ? bits : 0;
                s2 = narrow && bits * 2 < 8 ? bits * 2 : 0;
                s3 = narrow && bits * 4 < 8 ? bits * 4 : 0;
            }

            public byte Extract(uint v)
            {
                if (mask == 0) return 0;
                uint c = (v & mask) >> shift;
                if (bits >= 8) return (byte)(c >> (bits - 8));
                uint wide = c << (8 - bits);
                for (int filled = bits; filled < 8; filled *= 2) wide |= wide >> filled;
                return (byte)wide;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector512<uint> Extract(Vector512<uint> v)
            {
                Vector512<uint> w = ((v >> shift) & Vector512.Create(low)) << up >> down;
                w |= w >> s1;
                w |= w >> s2;
                w |= w >> s3;
                return w & Vector512.Create(0xFFu);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector256<uint> Extract(Vector256<uint> v)
            {
                Vector256<uint> w = ((v >> shift) & Vector256.Create(low)) << up >> down;
                w |= w >> s1;
                w |= w >> s2;
                w |= w >> s3;
                return w & Vector256.Create(0xFFu);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector128<uint> Extract(Vector128<uint> v)
            {
                Vector128<uint> w = ((v >> shift) & Vector128.Create(low)) << up >> down;
                w |= w >> s1;
                w |= w >> s2;
                w |= w >> s3;
                return w & Vector128.Create(0xFFu);
            }
        }
    }
}
