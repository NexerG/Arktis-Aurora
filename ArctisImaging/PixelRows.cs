using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Arctis.Imaging
{
    // Row conversion from RGBA8 into each output layout; destination rows are written, never read.
    internal static class PixelRows
    {
        // Rec. 709 weights in 16.16 fixed point; they sum to 65536, so grey in is grey out.
        private const int LumaR = 13933, LumaG = 46871, LumaB = 4732;

        internal static byte Luma(int r, int g, int b) => (byte)((r * LumaR + g * LumaG + b * LumaB + 32768) >> 16);

        // Writes width RGBA8 pixels into a row of the given layout.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void FromRgba(ReadOnlySpan<byte> rgba, Span<byte> row, int width, PixelLayout layout)
        {
            switch (layout)
            {
                case PixelLayout.RGBA8:
                    rgba.Slice(0, width * 4).CopyTo(row);
                    break;
                case PixelLayout.BGRA8:
                    ImageOps.CopySwizzled(rgba.Slice(0, width * 4), row);
                    break;
                case PixelLayout.R8:
                    for (int x = 0; x < width; x++)
                        row[x] = Luma(rgba[x * 4], rgba[x * 4 + 1], rgba[x * 4 + 2]);
                    break;
                case PixelLayout.RG8:
                    for (int x = 0; x < width; x++)
                    {
                        row[x * 2] = Luma(rgba[x * 4], rgba[x * 4 + 1], rgba[x * 4 + 2]);
                        row[x * 2 + 1] = rgba[x * 4 + 3];
                    }
                    break;
            }
        }

        // Writes width pixels of a row in the given layout as RGBA8.
        internal static void ToRgba(ReadOnlySpan<byte> row, Span<byte> rgba, int width, PixelLayout layout)
        {
            switch (layout)
            {
                case PixelLayout.RGBA8:
                    row.Slice(0, width * 4).CopyTo(rgba);
                    break;
                case PixelLayout.BGRA8:
                    ImageOps.CopySwizzled(row.Slice(0, width * 4), rgba);
                    break;
                case PixelLayout.R8:
                    for (int x = 0; x < width; x++)
                    {
                        byte v = row[x];
                        rgba[x * 4] = v;
                        rgba[x * 4 + 1] = v;
                        rgba[x * 4 + 2] = v;
                        rgba[x * 4 + 3] = 255;
                    }
                    break;
                case PixelLayout.RG8:
                    for (int x = 0; x < width; x++)
                    {
                        byte v = row[x * 2];
                        rgba[x * 4] = v;
                        rgba[x * 4 + 1] = v;
                        rgba[x * 4 + 2] = v;
                        rgba[x * 4 + 3] = row[x * 2 + 1];
                    }
                    break;
            }
        }

        // Writes width RGBA8 or BGRA8 pixels as 24-bit B,G,R.
        internal static void ToBgr(ReadOnlySpan<byte> row, Span<byte> dst, int width, PixelLayout layout) => Pack24(row, dst, width, layout == PixelLayout.RGBA8);

        // Writes width RGBA8 or BGRA8 pixels as 24-bit R,G,B.
        internal static void ToRgb(ReadOnlySpan<byte> row, Span<byte> dst, int width, PixelLayout layout) => Pack24(row, dst, width, layout == PixelLayout.BGRA8);

        // Drops every fourth byte, reversing the first three when swap is set; vector stores stay inside dst.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Pack24(ReadOnlySpan<byte> row, Span<byte> dst, int width, bool swap)
        {
            if (row.Length < width * 4 || dst.Length < width * 3) throw new ArgumentException("row or destination is shorter than width pixels");
            ref byte s = ref MemoryMarshal.GetReference(row);
            ref byte d = ref MemoryMarshal.GetReference(dst);
            nuint w = (nuint)width, room = (nuint)dst.Length, x = 0;
            Vector128<byte> order = swap
                ? Vector128.Create((byte)2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, 0x80, 0x80, 0x80, 0x80)
                : Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80);

            // each 128-bit lane packs its 12 bytes in place, then a dword permute closes the gaps
            if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
            {
                Vector512<byte> lanes = Vector512.Create(Vector256.Create(order, order), Vector256.Create(order, order));
                Vector512<int> gather = Vector512.Create(0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 3, 7, 11, 15);
                for (; x + 16 <= w && x * 3 + 64 <= room; x += 16)
                    Avx512F.PermuteVar16x32(Avx512BW.Shuffle(Vector512.LoadUnsafe(ref s, x * 4), lanes).AsInt32(), gather).AsByte().StoreUnsafe(ref d, x * 3);
            }
            if (Vector256.IsHardwareAccelerated && Avx2.IsSupported)
            {
                Vector256<byte> lanes = Vector256.Create(order, order);
                Vector256<int> gather = Vector256.Create(0, 1, 2, 4, 5, 6, 3, 7);
                for (; x + 8 <= w && x * 3 + 32 <= room; x += 8)
                    Avx2.PermuteVar8x32(Avx2.Shuffle(Vector256.LoadUnsafe(ref s, x * 4), lanes).AsInt32(), gather).AsByte().StoreUnsafe(ref d, x * 3);
            }
            if (Vector128.IsHardwareAccelerated)
            {
                for (; x + 16 <= w && x * 3 + 52 <= room; x += 16)
                {
                    nuint si = x * 4, di = x * 3;
                    Vector128<byte> a = Vector128.LoadUnsafe(ref s, si), b = Vector128.LoadUnsafe(ref s, si + 16);
                    Vector128<byte> c = Vector128.LoadUnsafe(ref s, si + 32), e = Vector128.LoadUnsafe(ref s, si + 48);
                    Vector128.Shuffle(a, order).StoreUnsafe(ref d, di);
                    Vector128.Shuffle(b, order).StoreUnsafe(ref d, di + 12);
                    Vector128.Shuffle(c, order).StoreUnsafe(ref d, di + 24);
                    Vector128.Shuffle(e, order).StoreUnsafe(ref d, di + 36);
                }
                for (; x + 4 <= w && x * 3 + 16 <= room; x += 4)
                    Vector128.Shuffle(Vector128.LoadUnsafe(ref s, x * 4), order).StoreUnsafe(ref d, x * 3);
            }
            nuint first = swap ? 2u : 0u, last = swap ? 0u : 2u;
            for (; x < w; x++)
            {
                Unsafe.Add(ref d, x * 3) = Unsafe.Add(ref s, x * 4 + first);
                Unsafe.Add(ref d, x * 3 + 1) = Unsafe.Add(ref s, x * 4 + 1);
                Unsafe.Add(ref d, x * 3 + 2) = Unsafe.Add(ref s, x * 4 + last);
            }
        }

        // Expands width grey bytes into opaque RGBA8, which is also BGRA8.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void FromGrey(ReadOnlySpan<byte> grey, Span<byte> row, int width)
        {
            if (grey.Length < width || row.Length < width * 4) throw new ArgumentException("grey or row is shorter than width pixels");
            ref byte s = ref MemoryMarshal.GetReference(grey);
            ref byte d = ref MemoryMarshal.GetReference(row);
            nuint w = (nuint)width, x = 0;
            Vector128<byte> alpha = Vector128.Create(0xFF000000u).AsByte();
            Vector128<byte> q0 = Vector128.Create((byte)0, 0, 0, 0xFF, 1, 1, 1, 0xFF, 2, 2, 2, 0xFF, 3, 3, 3, 0xFF);
            Vector128<byte> q1 = Vector128.Create((byte)4, 4, 4, 0xFF, 5, 5, 5, 0xFF, 6, 6, 6, 0xFF, 7, 7, 7, 0xFF);
            Vector128<byte> q2 = Vector128.Create((byte)8, 8, 8, 0xFF, 9, 9, 9, 0xFF, 10, 10, 10, 0xFF, 11, 11, 11, 0xFF);
            Vector128<byte> q3 = Vector128.Create((byte)12, 12, 12, 0xFF, 13, 13, 13, 0xFF, 14, 14, 14, 0xFF, 15, 15, 15, 0xFF);

            // 16 grey bytes copied into every 128-bit lane, each lane expanding its own four
            if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
            {
                Vector512<byte> q = Vector512.Create(Vector256.Create(q0, q1), Vector256.Create(q2, q3));
                Vector512<byte> opaque = Vector512.Create(Vector256.Create(alpha, alpha), Vector256.Create(alpha, alpha));
                for (; x + 16 <= w; x += 16)
                {
                    Vector128<byte> l = Vector128.LoadUnsafe(ref s, x);
                    Vector256<byte> g = Vector256.Create(l, l);
                    (Avx512BW.Shuffle(Vector512.Create(g, g), q) | opaque).StoreUnsafe(ref d, x * 4);
                }
            }
            if (Vector256.IsHardwareAccelerated && Avx2.IsSupported)
            {
                Vector256<byte> qa = Vector256.Create(q0, q1), qb = Vector256.Create(q2, q3), opaque = Vector256.Create(alpha, alpha);
                for (; x + 16 <= w; x += 16)
                {
                    Vector128<byte> l = Vector128.LoadUnsafe(ref s, x);
                    Vector256<byte> g = Vector256.Create(l, l);
                    (Avx2.Shuffle(g, qa) | opaque).StoreUnsafe(ref d, x * 4);
                    (Avx2.Shuffle(g, qb) | opaque).StoreUnsafe(ref d, x * 4 + 32);
                }
            }
            if (Vector128.IsHardwareAccelerated)
            {
                for (; x + 16 <= w; x += 16)
                {
                    Vector128<byte> g = Vector128.LoadUnsafe(ref s, x);
                    nuint di = x * 4;
                    (Vector128.Shuffle(g, q0) | alpha).StoreUnsafe(ref d, di);
                    (Vector128.Shuffle(g, q1) | alpha).StoreUnsafe(ref d, di + 16);
                    (Vector128.Shuffle(g, q2) | alpha).StoreUnsafe(ref d, di + 32);
                    (Vector128.Shuffle(g, q3) | alpha).StoreUnsafe(ref d, di + 48);
                }
            }
            for (; x < w; x++)
            {
                byte v = Unsafe.Add(ref s, x);
                Unsafe.Add(ref d, x * 4) = v;
                Unsafe.Add(ref d, x * 4 + 1) = v;
                Unsafe.Add(ref d, x * 4 + 2) = v;
                Unsafe.Add(ref d, x * 4 + 3) = 255;
            }
        }

        // Expands width 24-bit R,G,B pixels into opaque RGBA8, or BGRA8 when bgra is set; vector loads stay inside rgb.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void FromRgb(ReadOnlySpan<byte> rgb, Span<byte> row, int width, bool bgra)
        {
            if (rgb.Length < width * 3 || row.Length < width * 4) throw new ArgumentException("rgb or row is shorter than width pixels");
            ref byte s = ref MemoryMarshal.GetReference(rgb);
            ref byte d = ref MemoryMarshal.GetReference(row);
            nuint w = (nuint)width, room = (nuint)rgb.Length, x = 0;
            Vector128<byte> order = bgra
                ? Vector128.Create((byte)2, 1, 0, 0xFF, 5, 4, 3, 0xFF, 8, 7, 6, 0xFF, 11, 10, 9, 0xFF)
                : Vector128.Create((byte)0, 1, 2, 0xFF, 3, 4, 5, 0xFF, 6, 7, 8, 0xFF, 9, 10, 11, 0xFF);
            Vector128<byte> alpha = Vector128.Create(0xFF000000u).AsByte();

            // a dword permute gives each 128-bit lane its four pixels' 12 bytes, which it then expands
            if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
            {
                Vector512<byte> lanes = Vector512.Create(Vector256.Create(order, order), Vector256.Create(order, order));
                Vector512<byte> opaque = Vector512.Create(Vector256.Create(alpha, alpha), Vector256.Create(alpha, alpha));
                Vector512<int> spread = Vector512.Create(0, 1, 2, 0, 3, 4, 5, 0, 6, 7, 8, 0, 9, 10, 11, 0);
                for (; x + 16 <= w && x * 3 + 64 <= room; x += 16)
                    (Avx512BW.Shuffle(Avx512F.PermuteVar16x32(Vector512.LoadUnsafe(ref s, x * 3).AsInt32(), spread).AsByte(), lanes) | opaque).StoreUnsafe(ref d, x * 4);
            }
            if (Vector256.IsHardwareAccelerated && Avx2.IsSupported)
            {
                Vector256<byte> lanes = Vector256.Create(order, order), opaque = Vector256.Create(alpha, alpha);
                Vector256<int> spread = Vector256.Create(0, 1, 2, 0, 3, 4, 5, 0);
                for (; x + 8 <= w && x * 3 + 32 <= room; x += 8)
                    (Avx2.Shuffle(Avx2.PermuteVar8x32(Vector256.LoadUnsafe(ref s, x * 3).AsInt32(), spread).AsByte(), lanes) | opaque).StoreUnsafe(ref d, x * 4);
            }
            if (Vector128.IsHardwareAccelerated)
            {
                for (; x + 4 <= w && x * 3 + 16 <= room; x += 4)
                    (Vector128.Shuffle(Vector128.LoadUnsafe(ref s, x * 3), order) | alpha).StoreUnsafe(ref d, x * 4);
            }
            nuint first = bgra ? 2u : 0u, last = bgra ? 0u : 2u;
            for (; x < w; x++)
            {
                Unsafe.Add(ref d, x * 4) = Unsafe.Add(ref s, x * 3 + first);
                Unsafe.Add(ref d, x * 4 + 1) = Unsafe.Add(ref s, x * 3 + 1);
                Unsafe.Add(ref d, x * 4 + 2) = Unsafe.Add(ref s, x * 3 + last);
                Unsafe.Add(ref d, x * 4 + 3) = 255;
            }
        }

        // Sets alpha to opaque on every pixel of a decoded row.
        internal static void MakeOpaque(Span<byte> row, int width, PixelLayout layout)
        {
            switch (layout)
            {
                case PixelLayout.RGBA8:
                case PixelLayout.BGRA8:
                    for (int x = 0; x < width; x++) row[x * 4 + 3] = 255;
                    break;
                case PixelLayout.RG8:
                    for (int x = 0; x < width; x++) row[x * 2 + 1] = 255;
                    break;
            }
        }
    }
}
