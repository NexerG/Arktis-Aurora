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
        internal const int LumaR = 13933, LumaG = 46871, LumaB = 4732;

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

        // 32-bit B,G,R,A pixels into any layout, alpha forced to 255 when opaque is set.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void Row32(ReadOnlySpan<byte> src, Span<byte> dst, int width, PixelLayout layout, bool opaque)
        {
            switch (layout)
            {
                case PixelLayout.RGBA8:
                    ImageOps.CopySwizzled(src.Slice(0, width * 4), dst);
                    if (opaque) ForceOpaque(dst, width);
                    break;
                case PixelLayout.BGRA8:
                    src.Slice(0, width * 4).CopyTo(dst);
                    if (opaque) ForceOpaque(dst, width);
                    break;
                case PixelLayout.R8:
                    for (int x = 0; x < width; x++) dst[x] = Luma(src[x * 4 + 2], src[x * 4 + 1], src[x * 4]);
                    break;
                case PixelLayout.RG8:
                    for (int x = 0; x < width; x++)
                    {
                        dst[x * 2] = Luma(src[x * 4 + 2], src[x * 4 + 1], src[x * 4]);
                        dst[x * 2 + 1] = opaque ? (byte)255 : src[x * 4 + 3];
                    }
                    break;
            }
        }

        // Sets the alpha byte of width 32-bit pixels to 255.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void ForceOpaque(Span<byte> row, int width)
        {
            if (row.Length < width * 4) throw new ArgumentException("row is shorter than width pixels");
            ref uint p = ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(row));
            nuint x = 0, w = (nuint)width;
            if (Vector512.IsHardwareAccelerated)
                for (Vector512<uint> a = Vector512.Create(0xFF000000u); x + 16 <= w; x += 16) (Vector512.LoadUnsafe(ref p, x) | a).StoreUnsafe(ref p, x);
            if (Vector256.IsHardwareAccelerated)
                for (Vector256<uint> a = Vector256.Create(0xFF000000u); x + 8 <= w; x += 8) (Vector256.LoadUnsafe(ref p, x) | a).StoreUnsafe(ref p, x);
            if (Vector128.IsHardwareAccelerated)
                for (Vector128<uint> a = Vector128.Create(0xFF000000u); x + 4 <= w; x += 4) (Vector128.LoadUnsafe(ref p, x) | a).StoreUnsafe(ref p, x);
            for (; x < w; x++) Unsafe.Add(ref p, x) |= 0xFF000000u;
        }

        // 24-bit B,G,R pixels into any layout.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void Row24(ReadOnlySpan<byte> src, Span<byte> dst, int width, PixelLayout layout)
        {
            switch (layout)
            {
                case PixelLayout.RGBA8:
                case PixelLayout.BGRA8:
                    FromRgb(src, dst, width, layout == PixelLayout.RGBA8);
                    break;
                case PixelLayout.R8:
                    for (int x = 0; x < width; x++) dst[x] = Luma(src[x * 3 + 2], src[x * 3 + 1], src[x * 3]);
                    break;
                case PixelLayout.RG8:
                    for (int x = 0; x < width; x++)
                    {
                        dst[x * 2] = Luma(src[x * 3 + 2], src[x * 3 + 1], src[x * 3]);
                        dst[x * 2 + 1] = 255;
                    }
                    break;
            }
        }

        // 555 or 565 pixels widened to 8 bits a channel by bit replication.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void Row16(ReadOnlySpan<byte> src, Span<byte> dst, int width, PixelLayout layout, bool wideGreen)
        {
            int size = layout == PixelLayout.R8 ? 1 : layout == PixelLayout.RG8 ? 2 : 4;
            if (src.Length < width * 2 || dst.Length < width * size) throw new ArgumentException("src or dst is shorter than width pixels");
            int rShift = wideGreen ? 11 : 10, gMask = wideGreen ? 63 : 31, gUp = wideGreen ? 2 : 3, gDown = wideGreen ? 4 : 2;
            ref byte s = ref MemoryMarshal.GetReference(src);
            ref byte d = ref MemoryMarshal.GetReference(dst);
            if (size == 1)
            {
                for (int i = 0; i < width; i++) Unsafe.Add(ref d, i) = Grey16(Unsafe.Add(ref s, i * 2) | Unsafe.Add(ref s, i * 2 + 1) << 8, rShift, gMask, gUp, gDown);
                return;
            }
            if (size == 2)
            {
                for (int i = 0; i < width; i++)
                {
                    Unsafe.Add(ref d, i * 2) = Grey16(Unsafe.Add(ref s, i * 2) | Unsafe.Add(ref s, i * 2 + 1) << 8, rShift, gMask, gUp, gDown);
                    Unsafe.Add(ref d, i * 2 + 1) = 255;
                }
                return;
            }

            // byte 0 of a pixel is red for RGBA8 and blue for BGRA8, byte 2 the other; channels are widened in 16-bit
            // lanes, then byte 0 and green pair with byte 2 and opaque alpha into 32-bit pixels
            int firstShift = layout == PixelLayout.RGBA8 ? rShift : 0, thirdShift = rShift - firstShift;
            ref uint p = ref Unsafe.As<byte, uint>(ref d);
            int x = 0;
            if (Vector512.IsHardwareAccelerated)
            {
                Vector512<ushort> five = Vector512.Create((ushort)31), green = Vector512.Create((ushort)gMask), opaque = Vector512.Create((ushort)0xFF00);
                for (; x + 32 <= width; x += 32)
                {
                    Vector512<ushort> v = Vector512.LoadUnsafe(ref s, (nuint)x * 2).AsUInt16();
                    Vector512<ushort> f = (v >> firstShift) & five, t = (v >> thirdShift) & five, g = (v >> 5) & green;
                    (Vector512<uint> l0, Vector512<uint> l1) = Vector512.Widen(f << 3 | f >> 2 | (g << gUp | g >> gDown) << 8);
                    (Vector512<uint> h0, Vector512<uint> h1) = Vector512.Widen(t << 3 | t >> 2 | opaque);
                    (l0 | h0 << 16).StoreUnsafe(ref p, (nuint)x);
                    (l1 | h1 << 16).StoreUnsafe(ref p, (nuint)x + 16);
                }
            }
            if (Vector256.IsHardwareAccelerated)
            {
                Vector256<ushort> five = Vector256.Create((ushort)31), green = Vector256.Create((ushort)gMask), opaque = Vector256.Create((ushort)0xFF00);
                for (; x + 16 <= width; x += 16)
                {
                    Vector256<ushort> v = Vector256.LoadUnsafe(ref s, (nuint)x * 2).AsUInt16();
                    Vector256<ushort> f = (v >> firstShift) & five, t = (v >> thirdShift) & five, g = (v >> 5) & green;
                    (Vector256<uint> l0, Vector256<uint> l1) = Vector256.Widen(f << 3 | f >> 2 | (g << gUp | g >> gDown) << 8);
                    (Vector256<uint> h0, Vector256<uint> h1) = Vector256.Widen(t << 3 | t >> 2 | opaque);
                    (l0 | h0 << 16).StoreUnsafe(ref p, (nuint)x);
                    (l1 | h1 << 16).StoreUnsafe(ref p, (nuint)x + 8);
                }
            }
            if (Vector128.IsHardwareAccelerated)
            {
                Vector128<ushort> five = Vector128.Create((ushort)31), green = Vector128.Create((ushort)gMask), opaque = Vector128.Create((ushort)0xFF00);
                for (; x + 8 <= width; x += 8)
                {
                    Vector128<ushort> v = Vector128.LoadUnsafe(ref s, (nuint)x * 2).AsUInt16();
                    Vector128<ushort> f = (v >> firstShift) & five, t = (v >> thirdShift) & five, g = (v >> 5) & green;
                    (Vector128<uint> l0, Vector128<uint> l1) = Vector128.Widen(f << 3 | f >> 2 | (g << gUp | g >> gDown) << 8);
                    (Vector128<uint> h0, Vector128<uint> h1) = Vector128.Widen(t << 3 | t >> 2 | opaque);
                    (l0 | h0 << 16).StoreUnsafe(ref p, (nuint)x);
                    (l1 | h1 << 16).StoreUnsafe(ref p, (nuint)x + 4);
                }
            }
            for (; x < width; x++)
            {
                int v = Unsafe.Add(ref s, x * 2) | Unsafe.Add(ref s, x * 2 + 1) << 8;
                int f = (v >> firstShift) & 31, t = (v >> thirdShift) & 31, g = (v >> 5) & gMask;
                Unsafe.Add(ref p, x) = (uint)(f << 3 | f >> 2 | (g << gUp | g >> gDown) << 8 | (t << 3 | t >> 2) << 16) | 0xFF000000u;
            }
        }

        private static byte Grey16(int v, int rShift, int gMask, int gUp, int gDown)
        {
            int r5 = (v >> rShift) & 31, g = (v >> 5) & gMask, b5 = v & 31;
            return Luma(r5 << 3 | r5 >> 2, g << gUp | g >> gDown, b5 << 3 | b5 >> 2);
        }

        // True when any 32-bit pixel of the block has a bit of aMask set.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static bool HasAnyAlpha(ReadOnlySpan<byte> pixels, uint aMask)
        {
            ref uint p = ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(pixels));
            nuint i = 0, n = (nuint)(pixels.Length / 4);
            if (Vector512.IsHardwareAccelerated)
                for (Vector512<uint> m = Vector512.Create(aMask); i + 64 <= n; i += 64)
                    if (((Vector512.LoadUnsafe(ref p, i) | Vector512.LoadUnsafe(ref p, i + 16) | Vector512.LoadUnsafe(ref p, i + 32) | Vector512.LoadUnsafe(ref p, i + 48)) & m) != Vector512<uint>.Zero) return true;
            if (Vector256.IsHardwareAccelerated)
                for (Vector256<uint> m = Vector256.Create(aMask); i + 32 <= n; i += 32)
                    if (((Vector256.LoadUnsafe(ref p, i) | Vector256.LoadUnsafe(ref p, i + 8) | Vector256.LoadUnsafe(ref p, i + 16) | Vector256.LoadUnsafe(ref p, i + 24)) & m) != Vector256<uint>.Zero) return true;
            if (Vector128.IsHardwareAccelerated)
                for (Vector128<uint> m = Vector128.Create(aMask); i + 16 <= n; i += 16)
                    if (((Vector128.LoadUnsafe(ref p, i) | Vector128.LoadUnsafe(ref p, i + 4) | Vector128.LoadUnsafe(ref p, i + 8) | Vector128.LoadUnsafe(ref p, i + 12)) & m) != Vector128<uint>.Zero) return true;
            for (; i < n; i++)
                if ((Unsafe.Add(ref p, i) & aMask) != 0) return true;
            return false;
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

        // Writes width palette entries, one per index byte, as 32-bit pixels.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void LookupRow(ReadOnlySpan<byte> line, ReadOnlySpan<uint> palette, int width, Span<byte> row)
        {
            if (line.Length < width || palette.Length < 256 || row.Length < width * 4) throw new ArgumentException("line or row is shorter than width pixels");
            ref byte s = ref MemoryMarshal.GetReference(line);
            ref uint p = ref MemoryMarshal.GetReference(palette);
            ref byte d = ref MemoryMarshal.GetReference(row);
            for (nuint x = 0; x < (nuint)width; x++)
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, x * 4), Unsafe.Add(ref p, Unsafe.Add(ref s, x)));
        }

        // Writes one palette index per pixel from 1-bit plane lines, high bit first, in whole vectors past width.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        internal static void BitIndices(ReadOnlySpan<byte> line, int bpl, int planes, Span<byte> idx, int width)
        {
            if (line.Length < planes * bpl + 64 || idx.Length < width + 64) throw new ArgumentException("line or idx has no slack after width pixels");
            ref byte s = ref MemoryMarshal.GetReference(line);
            ref byte d = ref MemoryMarshal.GetReference(idx);
            nuint w = (nuint)width, stride = (nuint)bpl;
            Vector128<byte> bit = Vector128.Create((byte)128, 64, 32, 16, 8, 4, 2, 1, 128, 64, 32, 16, 8, 4, 2, 1);
            Vector128<byte> p0 = Vector128.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1), two = Vector128.Create((byte)2);

            // each byte goes to its eight lanes; planes from the highest down double the index and add a set bit
            if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
            {
                Vector256<byte> b2 = Vector256.Create(bit, bit);
                Vector512<byte> b4 = Vector512.Create(b2, b2), one = Vector512.Create((byte)1);
                Vector512<byte> pick = Vector512.Create(Vector256.Create(p0, p0 + two), Vector256.Create(p0 + two + two, p0 + two + two + two));
                for (nuint x = 0; x < w; x += 64)
                {
                    Vector512<byte> v = Vector512<byte>.Zero;
                    for (int p = planes - 1; p >= 0; p--)
                        v = v + v | Vector512.Equals(Avx512BW.Shuffle(Vector512.Create(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, (nuint)p * stride + x / 8))).AsByte(), pick) & b4, b4) & one;
                    v.StoreUnsafe(ref d, x);
                }
                return;
            }
            if (Vector256.IsHardwareAccelerated && Avx2.IsSupported)
            {
                Vector256<byte> b2 = Vector256.Create(bit, bit), one = Vector256.Create((byte)1), pick = Vector256.Create(p0, p0 + two);
                for (nuint x = 0; x < w; x += 32)
                {
                    Vector256<byte> v = Vector256<byte>.Zero;
                    for (int p = planes - 1; p >= 0; p--)
                        v = v + v | Vector256.Equals(Avx2.Shuffle(Vector256.Create(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref s, (nuint)p * stride + x / 8))).AsByte(), pick) & b2, b2) & one;
                    v.StoreUnsafe(ref d, x);
                }
                return;
            }
            Vector128<byte> one1 = Vector128.Create((byte)1);
            for (nuint x = 0; x < w; x += 16)
            {
                Vector128<byte> v = Vector128<byte>.Zero;
                for (int p = planes - 1; p >= 0; p--)
                    v = v + v | Vector128.Equals(Vector128.Shuffle(Vector128.Create(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref s, (nuint)p * stride + x / 8))).AsByte(), p0) & bit, bit) & one1;
                v.StoreUnsafe(ref d, x);
            }
        }

        // Writes one palette index per pixel from 2- or 4-bit packed bytes, high bits first, in whole vectors past width.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        internal static void PackedIndices(ReadOnlySpan<byte> line, int bits, Span<byte> idx, int width)
        {
            if (line.Length < (width * bits + 7) / 8 + 64 || idx.Length < width + 256) throw new ArgumentException("line or idx has no slack after width pixels");
            ref byte s = ref MemoryMarshal.GetReference(line);
            ref byte d = ref MemoryMarshal.GetReference(idx);
            nuint w = (nuint)width, b = (nuint)bits, step = bits == 4 ? 2u : 4u;

            // a byte widened to (x >> 4) | (x & 15) << 8 holds its nibbles in pixel order; 2-bit splits each nibble again
            if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
            {
                for (nuint x = 0; x < w; x += 64 * step)
                {
                    Vector512<byte> v = Vector512.LoadUnsafe(ref s, x * b / 8);
                    Vector512<byte> lo = Split(Vector512.WidenLower(v), 4, 15), hi = Split(Vector512.WidenUpper(v), 4, 15);
                    if (bits == 4)
                    {
                        lo.StoreUnsafe(ref d, x);
                        hi.StoreUnsafe(ref d, x + 64);
                        continue;
                    }
                    Split(Vector512.WidenLower(lo), 2, 3).StoreUnsafe(ref d, x);
                    Split(Vector512.WidenUpper(lo), 2, 3).StoreUnsafe(ref d, x + 64);
                    Split(Vector512.WidenLower(hi), 2, 3).StoreUnsafe(ref d, x + 128);
                    Split(Vector512.WidenUpper(hi), 2, 3).StoreUnsafe(ref d, x + 192);
                }
                return;
            }
            if (Vector256.IsHardwareAccelerated && Avx2.IsSupported)
            {
                for (nuint x = 0; x < w; x += 32 * step)
                {
                    Vector256<byte> v = Vector256.LoadUnsafe(ref s, x * b / 8);
                    Vector256<byte> lo = Split(Vector256.WidenLower(v), 4, 15), hi = Split(Vector256.WidenUpper(v), 4, 15);
                    if (bits == 4)
                    {
                        lo.StoreUnsafe(ref d, x);
                        hi.StoreUnsafe(ref d, x + 32);
                        continue;
                    }
                    Split(Vector256.WidenLower(lo), 2, 3).StoreUnsafe(ref d, x);
                    Split(Vector256.WidenUpper(lo), 2, 3).StoreUnsafe(ref d, x + 32);
                    Split(Vector256.WidenLower(hi), 2, 3).StoreUnsafe(ref d, x + 64);
                    Split(Vector256.WidenUpper(hi), 2, 3).StoreUnsafe(ref d, x + 96);
                }
                return;
            }
            for (nuint x = 0; x < w; x += 16 * step)
            {
                Vector128<byte> v = Vector128.LoadUnsafe(ref s, x * b / 8);
                Vector128<byte> lo = Split(Vector128.WidenLower(v), 4, 15), hi = Split(Vector128.WidenUpper(v), 4, 15);
                if (bits == 4)
                {
                    lo.StoreUnsafe(ref d, x);
                    hi.StoreUnsafe(ref d, x + 16);
                    continue;
                }
                Split(Vector128.WidenLower(lo), 2, 3).StoreUnsafe(ref d, x);
                Split(Vector128.WidenUpper(lo), 2, 3).StoreUnsafe(ref d, x + 16);
                Split(Vector128.WidenLower(hi), 2, 3).StoreUnsafe(ref d, x + 32);
                Split(Vector128.WidenUpper(hi), 2, 3).StoreUnsafe(ref d, x + 48);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<byte> Split(Vector512<ushort> u, int shift, ushort mask) => (u >> shift | (u & Vector512.Create(mask)) << 8).AsByte();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<byte> Split(Vector256<ushort> u, int shift, ushort mask) => (u >> shift | (u & Vector256.Create(mask)) << 8).AsByte();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Split(Vector128<ushort> u, int shift, ushort mask) => (u >> shift | (u & Vector128.Create(mask)) << 8).AsByte();

        // Writes width pixels in the given layout, looking each index (0-15) up in the three 16-byte channel tables.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        internal static void LookupIndices(ReadOnlySpan<byte> idx, ReadOnlySpan<byte> tables, int width, Span<byte> row, PixelLayout layout)
        {
            int size = layout == PixelLayout.R8 ? 1 : layout == PixelLayout.RG8 ? 2 : 4;
            if (idx.Length < width || tables.Length < 48 || row.Length < width * size) throw new ArgumentException("idx, tables or row is shorter than width pixels");
            ref byte s = ref MemoryMarshal.GetReference(idx);
            ref byte t = ref MemoryMarshal.GetReference(tables);
            ref byte d = ref MemoryMarshal.GetReference(row);
            Vector128<byte> t0 = Vector128.LoadUnsafe(ref t), t1 = Vector128.LoadUnsafe(ref t, 16), t2 = Vector128.LoadUnsafe(ref t, 32);
            nuint w = (nuint)width, x = 0;

            // shuffle each channel out of its table, then widen (c0 | c1 << 8) and (c2 | 0xFF00) into pixels
            if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
            {
                Vector256<byte> h0 = Vector256.Create(t0, t0), h1 = Vector256.Create(t1, t1), h2 = Vector256.Create(t2, t2);
                Vector512<byte> a0 = Vector512.Create(h0, h0), a1 = Vector512.Create(h1, h1), a2 = Vector512.Create(h2, h2);
                Vector512<ushort> opaque = Vector512.Create((ushort)0xFF00);
                for (; x + 64 <= w; x += 64)
                {
                    Vector512<byte> i = Vector512.LoadUnsafe(ref s, x), c0 = Avx512BW.Shuffle(a0, i);
                    if (size == 1)
                    {
                        c0.StoreUnsafe(ref d, x);
                        continue;
                    }
                    Vector512<ushort> lo = Vector512.WidenLower(c0), hi = Vector512.WidenUpper(c0);
                    if (size == 2)
                    {
                        (lo | opaque).AsByte().StoreUnsafe(ref d, x * 2);
                        (hi | opaque).AsByte().StoreUnsafe(ref d, x * 2 + 64);
                        continue;
                    }
                    Vector512<byte> c1 = Avx512BW.Shuffle(a1, i), c2 = Avx512BW.Shuffle(a2, i);
                    lo |= Vector512.WidenLower(c1) << 8;
                    hi |= Vector512.WidenUpper(c1) << 8;
                    Vector512<ushort> baL = Vector512.WidenLower(c2) | opaque, baH = Vector512.WidenUpper(c2) | opaque;
                    nuint di = x * 4;
                    (Vector512.WidenLower(lo) | Vector512.WidenLower(baL) << 16).AsByte().StoreUnsafe(ref d, di);
                    (Vector512.WidenUpper(lo) | Vector512.WidenUpper(baL) << 16).AsByte().StoreUnsafe(ref d, di + 64);
                    (Vector512.WidenLower(hi) | Vector512.WidenLower(baH) << 16).AsByte().StoreUnsafe(ref d, di + 128);
                    (Vector512.WidenUpper(hi) | Vector512.WidenUpper(baH) << 16).AsByte().StoreUnsafe(ref d, di + 192);
                }
            }
            if (Vector256.IsHardwareAccelerated && Avx2.IsSupported)
            {
                Vector256<byte> a0 = Vector256.Create(t0, t0), a1 = Vector256.Create(t1, t1), a2 = Vector256.Create(t2, t2);
                Vector256<ushort> opaque = Vector256.Create((ushort)0xFF00);
                for (; x + 32 <= w; x += 32)
                {
                    Vector256<byte> i = Vector256.LoadUnsafe(ref s, x), c0 = Avx2.Shuffle(a0, i);
                    if (size == 1)
                    {
                        c0.StoreUnsafe(ref d, x);
                        continue;
                    }
                    Vector256<ushort> lo = Vector256.WidenLower(c0), hi = Vector256.WidenUpper(c0);
                    if (size == 2)
                    {
                        (lo | opaque).AsByte().StoreUnsafe(ref d, x * 2);
                        (hi | opaque).AsByte().StoreUnsafe(ref d, x * 2 + 32);
                        continue;
                    }
                    Vector256<byte> c1 = Avx2.Shuffle(a1, i), c2 = Avx2.Shuffle(a2, i);
                    lo |= Vector256.WidenLower(c1) << 8;
                    hi |= Vector256.WidenUpper(c1) << 8;
                    Vector256<ushort> baL = Vector256.WidenLower(c2) | opaque, baH = Vector256.WidenUpper(c2) | opaque;
                    nuint di = x * 4;
                    (Vector256.WidenLower(lo) | Vector256.WidenLower(baL) << 16).AsByte().StoreUnsafe(ref d, di);
                    (Vector256.WidenUpper(lo) | Vector256.WidenUpper(baL) << 16).AsByte().StoreUnsafe(ref d, di + 32);
                    (Vector256.WidenLower(hi) | Vector256.WidenLower(baH) << 16).AsByte().StoreUnsafe(ref d, di + 64);
                    (Vector256.WidenUpper(hi) | Vector256.WidenUpper(baH) << 16).AsByte().StoreUnsafe(ref d, di + 96);
                }
            }
            if (Vector128.IsHardwareAccelerated)
            {
                Vector128<ushort> opaque = Vector128.Create((ushort)0xFF00);
                for (; x + 16 <= w; x += 16)
                {
                    Vector128<byte> i = Vector128.LoadUnsafe(ref s, x), c0 = Vector128.Shuffle(t0, i);
                    if (size == 1)
                    {
                        c0.StoreUnsafe(ref d, x);
                        continue;
                    }
                    Vector128<ushort> lo = Vector128.WidenLower(c0), hi = Vector128.WidenUpper(c0);
                    if (size == 2)
                    {
                        (lo | opaque).AsByte().StoreUnsafe(ref d, x * 2);
                        (hi | opaque).AsByte().StoreUnsafe(ref d, x * 2 + 16);
                        continue;
                    }
                    Vector128<byte> c1 = Vector128.Shuffle(t1, i), c2 = Vector128.Shuffle(t2, i);
                    lo |= Vector128.WidenLower(c1) << 8;
                    hi |= Vector128.WidenUpper(c1) << 8;
                    Vector128<ushort> baL = Vector128.WidenLower(c2) | opaque, baH = Vector128.WidenUpper(c2) | opaque;
                    nuint di = x * 4;
                    (Vector128.WidenLower(lo) | Vector128.WidenLower(baL) << 16).AsByte().StoreUnsafe(ref d, di);
                    (Vector128.WidenUpper(lo) | Vector128.WidenUpper(baL) << 16).AsByte().StoreUnsafe(ref d, di + 16);
                    (Vector128.WidenLower(hi) | Vector128.WidenLower(baH) << 16).AsByte().StoreUnsafe(ref d, di + 32);
                    (Vector128.WidenUpper(hi) | Vector128.WidenUpper(baH) << 16).AsByte().StoreUnsafe(ref d, di + 48);
                }
            }
            for (; x < w; x++)
            {
                nuint i = Unsafe.Add(ref s, x);
                if (size == 1) Unsafe.Add(ref d, x) = Unsafe.Add(ref t, i);
                else if (size == 2) Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, x * 2), (ushort)(Unsafe.Add(ref t, i) | 0xFF00));
                else Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, x * 4), Unsafe.Add(ref t, i) | (uint)Unsafe.Add(ref t, i + 16) << 8 | (uint)Unsafe.Add(ref t, i + 32) << 16 | 0xFF000000u);
            }
        }
    }
}
