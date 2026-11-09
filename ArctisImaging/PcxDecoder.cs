using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Arctis.Imaging
{
    // ZSoft PCX: 1-bit with 1-4 planes, 2/4/8-bit indexed, 8-bit with 3 or 4 planes; plain or RLE scanlines.
    internal static class PcxDecoder
    {
        private const int HeaderSize = 128, TrailerSize = 769;

        private struct Info
        {
            public int bits, planes, bytesPerLine, width, height, encoding;
        }

        internal static bool TryReadHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (!TryReadInfo(data, out Info info)) return false;
            bool rgb = info.bits == 8 && info.planes >= 3;
            header = new ImageHeader(ImageFormat.Pcx, info.width, info.height, rgb ? info.planes : 3, 8, info.planes == 4 && info.bits == 8);
            return true;
        }

        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int pitch, PixelLayout layout)
        {
            if (!TryReadInfo(data, out Info info)) throw new ImageFormatException("PCX header is invalid");
            int width = info.width, bits = info.bits, planes = info.planes, bpl = info.bytesPerLine;
            int lineBytes = planes * bpl;
            bool rgb = bits == 8 && planes >= 3;

            Span<uint> palette = stackalloc uint[256];
            bool trailer = bits == 8 && planes == 1 && data.Length >= HeaderSize + TrailerSize && data[data.Length - TrailerSize] == 0x0C;
            for (int i = 0; i < 256; i++)
            {
                if (trailer)
                {
                    ReadOnlySpan<byte> e = data.Slice(data.Length - TrailerSize + 1 + i * 3);
                    palette[i] = e[0] | (uint)e[1] << 8 | (uint)e[2] << 16 | 0xFF000000u;
                }
                else if (bits == 8) palette[i] = (uint)i * 0x010101u | 0xFF000000u;
                else if (bits == 1 && planes == 1) palette[i] = i == 0 ? 0xFF000000u : 0xFFFFFFFFu;
                else if (i < 16)
                {
                    ReadOnlySpan<byte> e = data.Slice(16 + i * 3);
                    palette[i] = e[0] | (uint)e[1] << 8 | (uint)e[2] << 16 | 0xFF000000u;
                }
                else palette[i] = 0xFF000000u;
            }

            // 8-bit rows go straight into RGBA8/BGRA8 destinations and sub-byte rows into any; the rest through an RGBA8 row
            bool packed = bits < 8;
            bool direct = packed || bits == 8 && (layout == PixelLayout.RGBA8 || layout == PixelLayout.BGRA8), bgra = layout == PixelLayout.BGRA8;
            bool grey = bits == 8 && planes == 1;
            for (int i = 0; grey && i < 256; i++) grey = palette[i] == ((uint)i * 0x010101u | 0xFF000000u);
            if (direct && bgra && !rgb)
                for (int i = 0; i < 256; i++)
                {
                    uint c = palette[i];
                    palette[i] = c & 0xFF00FF00u | (c >> 16 & 0xFFu) | (c & 0xFFu) << 16;
                }

            // sub-byte palette as three 16-byte channel tables in destination byte order; R8/RG8 keep luma in the first
            Span<byte> tables = stackalloc byte[48];
            bool luma = layout == PixelLayout.R8 || layout == PixelLayout.RG8;
            for (int i = 0; packed && i < 16; i++)
            {
                uint c = palette[i];
                tables[i] = luma ? PixelRows.Luma((byte)c, (byte)(c >> 8), (byte)(c >> 16)) : (byte)c;
                tables[16 + i] = (byte)(c >> 8);
                tables[32 + i] = (byte)(c >> 16);
            }

            byte[] line = ArrayPool<byte>.Shared.Rent(lineBytes + 64);
            byte[]? rgba = direct ? null : ArrayPool<byte>.Shared.Rent(width * 4);
            byte[]? idx = packed ? ArrayPool<byte>.Shared.Rent(width + 256) : null;
            try
            {
                int pos = HeaderSize, end = trailer ? data.Length - TrailerSize : data.Length, runLeft = 0;
                byte runValue = 0;
                for (int y = 0; y < info.height; y++)
                {
                    ReadLine(data, ref pos, end, line, lineBytes, info.encoding == 1, ref runLeft, ref runValue);
                    Span<byte> row = destination.Slice(y * pitch);
                    if (direct)
                    {
                        if (rgb) InterleavePlanes(line, bpl, width, planes == 4, row, bgra);
                        else if (packed)
                        {
                            if (bits == 1) PixelRows.BitIndices(line, bpl, planes, idx, width);
                            else PixelRows.PackedIndices(line, bits, idx, width);
                            PixelRows.LookupIndices(idx, tables, width, row, layout);
                        }
                        else if (grey) PixelRows.FromGrey(line, row, width);
                        else PixelRows.LookupRow(line, palette, width, row);
                        continue;
                    }

                    for (int x = 0; x < width; x++)
                    {
                        uint c = rgb ? line[x] | (uint)line[bpl + x] << 8 | (uint)line[2 * bpl + x] << 16 | (planes == 4 ? (uint)line[3 * bpl + x] << 24 : 0xFF000000u) : palette[line[x]];
                        BinaryPrimitives.WriteUInt32LittleEndian(rgba.AsSpan(x * 4), c);
                    }
                    PixelRows.FromRgba(rgba, row, width, layout);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(line);
                if (rgba != null) ArrayPool<byte>.Shared.Return(rgba);
                if (idx != null) ArrayPool<byte>.Shared.Return(idx);
            }
        }

        // Fills the first n bytes of line with one scanline's plane lines; line has 64 bytes of slack after n.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ReadLine(ReadOnlySpan<byte> data, ref int pos, int end, Span<byte> line, int n, bool rle, ref int runLeft, ref byte runValue)
        {
            if (line.Length < n + 64) throw new ArgumentException("line has no slack after n bytes");
            int p = pos, i = 0;
            if (!rle)
            {
                if (end - p < n) throw new ImageFormatException("PCX scanlines run past the end");
                data.Slice(p, n).CopyTo(line);
                pos = p + n;
                return;
            }
            ref byte s = ref MemoryMarshal.GetReference(data);
            ref byte d = ref MemoryMarshal.GetReference(line);
            if (runLeft > 0)
            {
                i = Math.Min(runLeft, n);
                line.Slice(0, i).Fill(runValue);
                runLeft -= i;
            }

            // store a vector of input as literals, keep the bytes before its first code, then decode that code
            if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
            {
                Vector512<byte> code = Vector512.Create((byte)0xC0);
                for (int last = end - 65; i < n && p <= last;)
                {
                    Vector512<byte> v = Vector512.LoadUnsafe(ref s, (nuint)p);
                    v.StoreUnsafe(ref d, (nuint)i);
                    int k = Math.Min(BitOperations.TrailingZeroCount(Vector512.GreaterThanOrEqual(v, code).ExtractMostSignificantBits()), n - i);
                    i += k;
                    p += k;
                    if (k < 64 && i < n) StoreRun(ref s, ref d, ref p, ref i, n, ref runLeft, ref runValue);
                }
            }
            if (Vector256.IsHardwareAccelerated && Avx2.IsSupported)
            {
                Vector256<byte> code = Vector256.Create((byte)0xC0);
                for (int last = end - 33; i < n && p <= last;)
                {
                    Vector256<byte> v = Vector256.LoadUnsafe(ref s, (nuint)p);
                    v.StoreUnsafe(ref d, (nuint)i);
                    int k = Math.Min(BitOperations.TrailingZeroCount(Vector256.GreaterThanOrEqual(v, code).ExtractMostSignificantBits()), n - i);
                    i += k;
                    p += k;
                    if (k < 32 && i < n) StoreRun(ref s, ref d, ref p, ref i, n, ref runLeft, ref runValue);
                }
            }
            if (Vector128.IsHardwareAccelerated)
            {
                Vector128<byte> code = Vector128.Create((byte)0xC0);
                for (int last = end - 17; i < n && p <= last;)
                {
                    Vector128<byte> v = Vector128.LoadUnsafe(ref s, (nuint)p);
                    v.StoreUnsafe(ref d, (nuint)i);
                    int k = Math.Min(BitOperations.TrailingZeroCount(Vector128.GreaterThanOrEqual(v, code).ExtractMostSignificantBits() | 0x10000u), n - i);
                    i += k;
                    p += k;
                    if (k < 16 && i < n) StoreRun(ref s, ref d, ref p, ref i, n, ref runLeft, ref runValue);
                }
            }
            while (i < n)
            {
                if (p >= end) throw new ImageFormatException("PCX scanlines run past the end");
                byte b = Unsafe.Add(ref s, p++);
                if (b < 0xC0)
                {
                    Unsafe.Add(ref d, i++) = b;
                    continue;
                }
                if (p >= end) throw new ImageFormatException("PCX scanlines run past the end");
                byte v = Unsafe.Add(ref s, p++);
                int count = b & 0x3F, take = Math.Min(count, n - i);
                if (take == 1) Unsafe.Add(ref d, i) = v;
                else line.Slice(i, take).Fill(v);
                i += take;
                if (take < count)
                {
                    runLeft = count - take;
                    runValue = v;
                }
            }
            pos = p;
        }

        // Decodes the run code at p into line at i with whole-vector stores; a run past n carries to the next line.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StoreRun(ref byte s, ref byte d, ref int p, ref int i, int n, ref int runLeft, ref byte runValue)
        {
            int count = Unsafe.Add(ref s, p) & 0x3F;
            byte value = Unsafe.Add(ref s, p + 1);
            p += 2;
            Vector128<byte> fill = Vector128.Create(value);
            fill.StoreUnsafe(ref d, (nuint)i);
            if (count > 16)
            {
                fill.StoreUnsafe(ref d, (nuint)i + 16);
                fill.StoreUnsafe(ref d, (nuint)i + 32);
                fill.StoreUnsafe(ref d, (nuint)i + 48);
            }
            int take = Math.Min(count, n - i);
            i += take;
            if (take < count)
            {
                runLeft = count - take;
                runValue = value;
            }
        }

        // Interleaves the R, G, B (and A) plane lines of one scanline into RGBA8, or BGRA8 when bgra is set.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static void InterleavePlanes(ReadOnlySpan<byte> line, int bpl, int width, bool alpha, Span<byte> row, bool bgra)
        {
            if (bpl < width || line.Length < bpl * (alpha ? 4 : 3) || row.Length < width * 4) throw new ArgumentException("line or row is shorter than width pixels");
            ref byte first = ref MemoryMarshal.GetReference(line);
            ref byte r = ref bgra ? ref Unsafe.Add(ref first, 2 * bpl) : ref first;
            ref byte g = ref Unsafe.Add(ref first, bpl);
            ref byte b = ref bgra ? ref first : ref Unsafe.Add(ref first, 2 * bpl);
            ref byte a = ref alpha ? ref Unsafe.Add(ref first, 3 * bpl) : ref first;
            ref byte d = ref MemoryMarshal.GetReference(row);
            nuint w = (nuint)width, x = 0;

            // zero-extend bytes to (r | g << 8) and (b | a << 8) shorts, then those to (rg | ba << 16) pixels
            if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
            {
                Vector512<ushort> opaque = Vector512.Create((ushort)0xFF00);
                for (; x + 64 <= w; x += 64)
                {
                    Vector512<byte> vr = Vector512.LoadUnsafe(ref r, x), vg = Vector512.LoadUnsafe(ref g, x), vb = Vector512.LoadUnsafe(ref b, x);
                    Vector512<byte> va = alpha ? Vector512.LoadUnsafe(ref a, x) : default;
                    Vector512<ushort> rgL = Vector512.WidenLower(vr) | Vector512.WidenLower(vg) << 8, rgH = Vector512.WidenUpper(vr) | Vector512.WidenUpper(vg) << 8;
                    Vector512<ushort> baL = Vector512.WidenLower(vb) | (alpha ? Vector512.WidenLower(va) << 8 : opaque);
                    Vector512<ushort> baH = Vector512.WidenUpper(vb) | (alpha ? Vector512.WidenUpper(va) << 8 : opaque);
                    nuint di = x * 4;
                    (Vector512.WidenLower(rgL) | Vector512.WidenLower(baL) << 16).AsByte().StoreUnsafe(ref d, di);
                    (Vector512.WidenUpper(rgL) | Vector512.WidenUpper(baL) << 16).AsByte().StoreUnsafe(ref d, di + 64);
                    (Vector512.WidenLower(rgH) | Vector512.WidenLower(baH) << 16).AsByte().StoreUnsafe(ref d, di + 128);
                    (Vector512.WidenUpper(rgH) | Vector512.WidenUpper(baH) << 16).AsByte().StoreUnsafe(ref d, di + 192);
                }
            }
            if (Vector256.IsHardwareAccelerated && Avx2.IsSupported)
            {
                Vector256<ushort> opaque = Vector256.Create((ushort)0xFF00);
                for (; x + 32 <= w; x += 32)
                {
                    Vector256<byte> vr = Vector256.LoadUnsafe(ref r, x), vg = Vector256.LoadUnsafe(ref g, x), vb = Vector256.LoadUnsafe(ref b, x);
                    Vector256<byte> va = alpha ? Vector256.LoadUnsafe(ref a, x) : default;
                    Vector256<ushort> rgL = Vector256.WidenLower(vr) | Vector256.WidenLower(vg) << 8, rgH = Vector256.WidenUpper(vr) | Vector256.WidenUpper(vg) << 8;
                    Vector256<ushort> baL = Vector256.WidenLower(vb) | (alpha ? Vector256.WidenLower(va) << 8 : opaque);
                    Vector256<ushort> baH = Vector256.WidenUpper(vb) | (alpha ? Vector256.WidenUpper(va) << 8 : opaque);
                    nuint di = x * 4;
                    (Vector256.WidenLower(rgL) | Vector256.WidenLower(baL) << 16).AsByte().StoreUnsafe(ref d, di);
                    (Vector256.WidenUpper(rgL) | Vector256.WidenUpper(baL) << 16).AsByte().StoreUnsafe(ref d, di + 32);
                    (Vector256.WidenLower(rgH) | Vector256.WidenLower(baH) << 16).AsByte().StoreUnsafe(ref d, di + 64);
                    (Vector256.WidenUpper(rgH) | Vector256.WidenUpper(baH) << 16).AsByte().StoreUnsafe(ref d, di + 96);
                }
            }
            if (Vector128.IsHardwareAccelerated)
            {
                Vector128<ushort> opaque = Vector128.Create((ushort)0xFF00);
                for (; x + 16 <= w; x += 16)
                {
                    Vector128<byte> vr = Vector128.LoadUnsafe(ref r, x), vg = Vector128.LoadUnsafe(ref g, x), vb = Vector128.LoadUnsafe(ref b, x);
                    Vector128<byte> va = alpha ? Vector128.LoadUnsafe(ref a, x) : default;
                    Vector128<ushort> rgL = Vector128.WidenLower(vr) | Vector128.WidenLower(vg) << 8, rgH = Vector128.WidenUpper(vr) | Vector128.WidenUpper(vg) << 8;
                    Vector128<ushort> baL = Vector128.WidenLower(vb) | (alpha ? Vector128.WidenLower(va) << 8 : opaque);
                    Vector128<ushort> baH = Vector128.WidenUpper(vb) | (alpha ? Vector128.WidenUpper(va) << 8 : opaque);
                    nuint di = x * 4;
                    (Vector128.WidenLower(rgL) | Vector128.WidenLower(baL) << 16).AsByte().StoreUnsafe(ref d, di);
                    (Vector128.WidenUpper(rgL) | Vector128.WidenUpper(baL) << 16).AsByte().StoreUnsafe(ref d, di + 16);
                    (Vector128.WidenLower(rgH) | Vector128.WidenLower(baH) << 16).AsByte().StoreUnsafe(ref d, di + 32);
                    (Vector128.WidenUpper(rgH) | Vector128.WidenUpper(baH) << 16).AsByte().StoreUnsafe(ref d, di + 48);
                }
            }
            for (; x < w; x++)
            {
                Unsafe.Add(ref d, x * 4) = Unsafe.Add(ref r, x);
                Unsafe.Add(ref d, x * 4 + 1) = Unsafe.Add(ref g, x);
                Unsafe.Add(ref d, x * 4 + 2) = Unsafe.Add(ref b, x);
                Unsafe.Add(ref d, x * 4 + 3) = alpha ? Unsafe.Add(ref a, x) : (byte)255;
            }
        }

        private static bool TryReadInfo(ReadOnlySpan<byte> data, out Info info)
        {
            info = default;
            if (data.Length < HeaderSize || data[0] != 0x0A || data[1] > 5 || data[2] > 1) return false;
            info.encoding = data[2];
            info.bits = data[3];
            info.planes = data[65];
            info.bytesPerLine = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(66));
            int xmin = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4)), ymin = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6));
            int xmax = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8)), ymax = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(10));
            info.width = xmax - xmin + 1;
            info.height = ymax - ymin + 1;
            bool known = info.bits == 1 ? info.planes >= 1 && info.planes <= 4
                : info.bits == 2 || info.bits == 4 ? info.planes == 1
                : info.bits == 8 && (info.planes == 1 || info.planes == 3 || info.planes == 4);
            return known && info.width > 0 && info.height > 0 && info.bytesPerLine * 8L >= (long)info.width * info.bits;
        }
    }
}
