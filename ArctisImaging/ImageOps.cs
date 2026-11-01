using System;
using System.Buffers;
using System.Runtime.Intrinsics;

namespace Arctis.Imaging
{
    /// <summary>Pixel operations from one span to another; nothing is allocated per pixel.</summary>
    public static class ImageOps
    {
        /// <summary>The size of the next mip level: half, never below 1.</summary>
        public static int HalfSize(int size) => Math.Max(1, size / 2);

        /// <summary>Copies a rectangle out of an image of any layout.</summary>
        public static void Crop(ReadOnlySpan<byte> source, int sourcePitch, PixelLayout layout, int x, int y, int width, int height, Span<byte> destination, int destinationPitch)
        {
            int bpp = ImageCodec.BytesPerPixel(layout);
            int rowBytes = width * bpp;
            for (int row = 0; row < height; row++)
                source.Slice((y + row) * sourcePitch + x * bpp, rowBytes).CopyTo(destination.Slice(row * destinationPitch, rowBytes));
        }

        /// <summary>Swaps the first and third byte of every four, turning BGRA into RGBA and back.</summary>
        public static void SwizzleBgraToRgba(Span<byte> pixels) => CopySwizzled(pixels, pixels);

        // Swizzled copy that writes the destination without reading it; the two may be the same span.
        internal static void CopySwizzled(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            int i = 0;
            if (Vector128.IsHardwareAccelerated)
            {
                Vector128<byte> order = Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
                for (; i + 16 <= source.Length; i += 16)
                    Vector128.Shuffle(Vector128.Create(source.Slice(i, 16)), order).CopyTo(destination.Slice(i));
            }
            for (; i + 4 <= source.Length; i += 4)
            {
                byte r = source[i], b = source[i + 2];
                destination[i] = b;
                destination[i + 1] = source[i + 1];
                destination[i + 2] = r;
                destination[i + 3] = source[i + 3];
            }
        }

        /// <summary>One RGBA8 mip step: each output pixel is the box average of its 2x2 source block (3 on an odd
        /// last row or column), in linear light with premultiplied alpha. The destination is HalfSize of each side.</summary>
        public static void HalveLinear(ReadOnlySpan<byte> source, int width, int height, int sourcePitch, Span<byte> destination, int destinationPitch)
        {
            float[] lin = SrgbTable.toLinear;
            int w2 = HalfSize(width), h2 = HalfSize(height);
            for (int y = 0; y < h2; y++)
            {
                int y0 = Math.Min(y * 2, height - 1);
                int y1 = y == h2 - 1 ? height : Math.Min(y0 + 2, height);
                Span<byte> outRow = destination.Slice(y * destinationPitch);
                for (int x = 0; x < w2; x++)
                {
                    int x0 = Math.Min(x * 2, width - 1);
                    int x1 = x == w2 - 1 ? width : Math.Min(x0 + 2, width);
                    float r = 0, g = 0, b = 0, a = 0;
                    for (int sy = y0; sy < y1; sy++)
                    {
                        ReadOnlySpan<byte> row = source.Slice(sy * sourcePitch);
                        for (int sx = x0; sx < x1; sx++)
                        {
                            float alpha = row[sx * 4 + 3] * (1f / 255f);
                            r += lin[row[sx * 4]] * alpha;
                            g += lin[row[sx * 4 + 1]] * alpha;
                            b += lin[row[sx * 4 + 2]] * alpha;
                            a += alpha;
                        }
                    }
                    int n = (y1 - y0) * (x1 - x0);
                    Span<byte> px = outRow.Slice(x * 4, 4);
                    if (a <= 0f)
                    {
                        px.Clear();
                        continue;
                    }
                    px[0] = SrgbTable.FromLinear(r / a);
                    px[1] = SrgbTable.FromLinear(g / a);
                    px[2] = SrgbTable.FromLinear(b / a);
                    px[3] = (byte)Math.Min(255, (int)(a / n * 255f + 0.5f));
                }
            }
        }

        /// <summary>Resizes RGBA8 by area averaging, with premultiplied alpha. Meant for shrinking.</summary>
        public static void ResizeArea(ReadOnlySpan<byte> source, int width, int height, int sourcePitch, Span<byte> destination, int newWidth, int newHeight, int destinationPitch)
        {
            if (newWidth <= 0 || newHeight <= 0) throw new ArgumentException($"invalid size {newWidth}x{newHeight}");
            float[] acc = ArrayPool<float>.Shared.Rent(width * 4);
            try
            {
                float sx = (float)width / newWidth, sy = (float)height / newHeight;
                for (int y = 0; y < newHeight; y++)
                {
                    // vertical: premultiplied sum of the source rows this output row covers
                    Array.Clear(acc, 0, width * 4);
                    float top = y * sy, bottom = top + sy;
                    for (int j = (int)top; j < height && j < bottom; j++)
                    {
                        float weight = Math.Min(bottom, j + 1) - Math.Max(top, j);
                        ReadOnlySpan<byte> row = source.Slice(j * sourcePitch, width * 4);
                        for (int i = 0; i < width; i++)
                        {
                            float alpha = row[i * 4 + 3] * weight;
                            acc[i * 4] += row[i * 4] * alpha;
                            acc[i * 4 + 1] += row[i * 4 + 1] * alpha;
                            acc[i * 4 + 2] += row[i * 4 + 2] * alpha;
                            acc[i * 4 + 3] += alpha;
                        }
                    }

                    // horizontal
                    Span<byte> outRow = destination.Slice(y * destinationPitch);
                    for (int x = 0; x < newWidth; x++)
                    {
                        float left = x * sx, right = left + sx;
                        float r = 0, g = 0, b = 0, a = 0;
                        for (int i = (int)left; i < width && i < right; i++)
                        {
                            float weight = Math.Min(right, i + 1) - Math.Max(left, i);
                            r += acc[i * 4] * weight;
                            g += acc[i * 4 + 1] * weight;
                            b += acc[i * 4 + 2] * weight;
                            a += acc[i * 4 + 3] * weight;
                        }
                        Span<byte> px = outRow.Slice(x * 4, 4);
                        if (a <= 0f)
                        {
                            px.Clear();
                            continue;
                        }
                        px[0] = ClampByte(r / a);
                        px[1] = ClampByte(g / a);
                        px[2] = ClampByte(b / a);
                        px[3] = ClampByte(a / (sx * sy));
                    }
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(acc);
            }
        }

        private static byte ClampByte(float v) => v <= 0f ? (byte)0 : v >= 255f ? (byte)255 : (byte)(v + 0.5f);
    }
}
