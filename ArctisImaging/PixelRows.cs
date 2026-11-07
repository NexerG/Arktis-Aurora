using System;
using System.Runtime.CompilerServices;

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
