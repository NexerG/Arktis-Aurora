using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Arctis.Imaging
{
    // Lossless JPEG (T.81 process 14, SOF3): Huffman-coded differences from one of seven predictors, 2-16 bits,
    // components unsubsampled; samples above 8 bits keep their high byte.
    internal static partial class JpegDecoder
    {
        private static void ReadLosslessFrame(ReadOnlySpan<byte> segment, ref Frame f)
        {
            if (f.seen) throw new ImageFormatException("JPEG has more than one frame");
            if (segment.Length < 6) throw new ImageFormatException("JPEG frame header is truncated");
            f.precision = segment[0];
            f.height = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(1));
            f.width = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(3));
            f.count = segment[5];
            if (f.precision < 2 || f.precision > 16) throw new ImageFormatException($"lossless JPEG precision {f.precision} is out of range");
            if (f.width == 0 || f.height == 0) throw new ImageFormatException("JPEG frame has no size");
            if (f.count != 1 && f.count != 3 && f.count != 4) throw new NotSupportedException($"{f.count}-component JPEG is not available");
            if (segment.Length < 6 + f.count * 3) throw new ImageFormatException("JPEG frame header is truncated");
            Span<Component> components = f.components;
            for (int i = 0; i < f.count; i++)
            {
                components[i].id = segment[6 + i * 3];
                int sampling = segment[7 + i * 3];
                if (sampling >> 4 != (segment[7] >> 4) || (sampling & 15) != (segment[7] & 15))
                    throw new NotSupportedException("subsampled lossless JPEG is not available");
            }
            int total = checked(f.count * f.width * f.height);
            f.samples = ArrayPool<ushort>.Shared.Rent(total);
            f.samples.AsSpan(0, total).Clear();
            f.lossless = true;
            f.seen = true;
        }

        // One scan: each sample is its prediction plus a Huffman-coded difference, modulo 2^16.
        private static int DecodeLossless(ReadOnlySpan<byte> data, int start, ref Frame f, ReadOnlySpan<int> scan, int predictor, int pt)
        {
            if (predictor < 1 || predictor > 7 || pt >= f.precision) throw new ImageFormatException("lossless JPEG scan has an invalid predictor or point transform");
            f.pointTransform = pt;
            Bits bits = new Bits(data, start, f.unstuffed);
            ref int tables = ref MemoryMarshal.GetArrayDataReference(f.tables);
            Span<Component> components = f.components;
            Span<int> planeAt = stackalloc int[4];
            Span<int> table = stackalloc int[4];
            int width = f.width, height = f.height, count = scan.Length;
            for (int i = 0; i < count; i++)
            {
                planeAt[i] = scan[i] * width * height;
                table[i] = components[scan[i]].dc;
                components[scan[i]].scanned = true;
            }
            ushort[] samples = f.samples;
            int initial = 1 << (f.precision - pt - 1);
            int interval = f.restartInterval, todo = interval, firstRow = 0, firstX = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (interval > 0)
                    {
                        if (todo == 0)
                        {
                            bits.Restart();
                            todo = interval;
                            firstRow = y;
                            firstX = x;
                        }
                        todo--;
                    }
                    for (int i = 0; i < count; i++)
                    {
                        int at = planeAt[i] + y * width + x;
                        int prediction;
                        if (y == firstRow) prediction = x == firstX ? initial : samples[at - 1];
                        else if (x == 0) prediction = samples[at - width];
                        else
                        {
                            int ra = samples[at - 1], rb = samples[at - width], rc = samples[at - width - 1];
                            prediction = predictor switch
                            {
                                1 => ra,
                                2 => rb,
                                3 => rc,
                                4 => ra + rb - rc,
                                5 => ra + ((rb - rc) >> 1),
                                6 => rb + ((ra - rc) >> 1),
                                _ => (ra + rb) >> 1
                            };
                        }
                        bits.Ensure();
                        int s = bits.Symbol(ref tables, table[i]);
                        if (s > 16) throw new ImageFormatException("lossless JPEG difference category is out of range");
                        int diff = s == 16 ? 32768 : bits.Receive(s);
                        samples[at] = (ushort)(prediction + diff);
                    }
                }
            }
            return NextMarker(data, bits.pos);
        }

        // Samples shifted back by the point transform, reduced to 8 bits, then through the baseline colour conversion.
        private static void OutputLossless(in Frame f,Span<byte> destination, int pitch, PixelLayout layout)
        {
            int width = f.width, height = f.height, count = f.count, precision = f.precision, pt = f.pointTransform;
            int max = (1 << precision) - 1, down = precision - 8;
            bool ycc = IsYcc(in f);
            ushort[] samples = f.samples;
            byte[] rows = ArrayPool<byte>.Shared.Rent(count * width);
            byte[] rgba = layout == PixelLayout.RGBA8 ? Array.Empty<byte>() : ArrayPool<byte>.Shared.Rent(width * 4);
            try
            {
                for (int y = 0; y < height; y++)
                {
                    for (int i = 0; i < count; i++)
                    {
                        int from = i * width * height + y * width;
                        Span<byte> row = rows.AsSpan(i * width, width);
                        for (int x = 0; x < width; x++)
                        {
                            int v = (samples[from + x] << pt) & max;
                            row[x] = down >= 0 ? (byte)(v >> down) : (byte)((v * 255 + max / 2) / max);
                        }
                    }
                    ReadOnlySpan<byte> c0 = rows.AsSpan(0, width);
                    ReadOnlySpan<byte> c1 = count >= 3 ? rows.AsSpan(width, width) : c0;
                    ReadOnlySpan<byte> c2 = count >= 3 ? rows.AsSpan(width * 2, width) : c0;
                    Span<byte> target = layout == PixelLayout.RGBA8 ? destination.Slice(y * pitch, width * 4) : rgba.AsSpan(0, width * 4);
                    if (count == 4) ConvertCmykRow(c0, c1, c2, rows.AsSpan(width * 3, width), target, width, f.adobeTransform == 2, f.adobeTransform >= 0);
                    else ConvertRow(c0, c1, c2, target, width, ycc);
                    if (layout != PixelLayout.RGBA8) PixelRows.FromRgba(target, destination.Slice(y * pitch), width, layout);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rows);
                if (rgba.Length > 0) ArrayPool<byte>.Shared.Return(rgba);
            }
        }
    }
}
