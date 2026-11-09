using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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
            if (interval == 0)
            {
                ref ushort first = ref MemoryMarshal.GetArrayDataReference(samples);
                ReadOnlySpan<int> planes = planeAt.Slice(0, count), tablesOf = table.Slice(0, count);
                switch (predictor)
                {
                    case 1: DecodeScan<Predictor1>(ref bits, ref tables, ref first, planes, tablesOf, width, height, initial); break;
                    case 2: DecodeScan<Predictor2>(ref bits, ref tables, ref first, planes, tablesOf, width, height, initial); break;
                    case 3: DecodeScan<Predictor3>(ref bits, ref tables, ref first, planes, tablesOf, width, height, initial); break;
                    case 4: DecodeScan<Predictor4>(ref bits, ref tables, ref first, planes, tablesOf, width, height, initial); break;
                    case 5: DecodeScan<Predictor5>(ref bits, ref tables, ref first, planes, tablesOf, width, height, initial); break;
                    case 6: DecodeScan<Predictor6>(ref bits, ref tables, ref first, planes, tablesOf, width, height, initial); break;
                    default: DecodeScan<Predictor7>(ref bits, ref tables, ref first, planes, tablesOf, width, height, initial); break;
                }
                return NextMarker(data, bits.pos);
            }
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
                        samples[at] = (ushort)(prediction + Difference(ref bits, ref tables, table[i]));
                    }
                }
            }
            return NextMarker(data, bits.pos);
        }

        internal interface IPredictor
        {
            int Predict(int ra, int rb, int rc);
            Vector128<short> Predict(Vector128<short> ra, Vector128<short> rb, Vector128<short> rc);
            Vector256<short> Predict(Vector256<short> ra, Vector256<short> rb, Vector256<short> rc);
            Vector512<short> Predict(Vector512<short> ra, Vector512<short> rb, Vector512<short> rc);
        }

        // T.81 table H.1: ra is left, rb above, rc above-left
        internal readonly struct Predictor1 : IPredictor
        {
            public int Predict(int ra, int rb, int rc) => ra;
            public Vector128<short> Predict(Vector128<short> ra, Vector128<short> rb, Vector128<short> rc) => ra;
            public Vector256<short> Predict(Vector256<short> ra, Vector256<short> rb, Vector256<short> rc) => ra;
            public Vector512<short> Predict(Vector512<short> ra, Vector512<short> rb, Vector512<short> rc) => ra;
        }

        internal readonly struct Predictor2 : IPredictor
        {
            public int Predict(int ra, int rb, int rc) => rb;
            public Vector128<short> Predict(Vector128<short> ra, Vector128<short> rb, Vector128<short> rc) => rb;
            public Vector256<short> Predict(Vector256<short> ra, Vector256<short> rb, Vector256<short> rc) => rb;
            public Vector512<short> Predict(Vector512<short> ra, Vector512<short> rb, Vector512<short> rc) => rb;
        }

        internal readonly struct Predictor3 : IPredictor
        {
            public int Predict(int ra, int rb, int rc) => rc;
            public Vector128<short> Predict(Vector128<short> ra, Vector128<short> rb, Vector128<short> rc) => rc;
            public Vector256<short> Predict(Vector256<short> ra, Vector256<short> rb, Vector256<short> rc) => rc;
            public Vector512<short> Predict(Vector512<short> ra, Vector512<short> rb, Vector512<short> rc) => rc;
        }

        internal readonly struct Predictor4 : IPredictor
        {
            public int Predict(int ra, int rb, int rc) => ra + rb - rc;
            public Vector128<short> Predict(Vector128<short> ra, Vector128<short> rb, Vector128<short> rc) => ra + rb - rc;
            public Vector256<short> Predict(Vector256<short> ra, Vector256<short> rb, Vector256<short> rc) => ra + rb - rc;
            public Vector512<short> Predict(Vector512<short> ra, Vector512<short> rb, Vector512<short> rc) => ra + rb - rc;
        }

        internal readonly struct Predictor5 : IPredictor
        {
            public int Predict(int ra, int rb, int rc) => ra + ((rb - rc) >> 1);
            public Vector128<short> Predict(Vector128<short> ra, Vector128<short> rb, Vector128<short> rc) => ra + ((rb - rc) >> 1);
            public Vector256<short> Predict(Vector256<short> ra, Vector256<short> rb, Vector256<short> rc) => ra + ((rb - rc) >> 1);
            public Vector512<short> Predict(Vector512<short> ra, Vector512<short> rb, Vector512<short> rc) => ra + ((rb - rc) >> 1);
        }

        internal readonly struct Predictor6 : IPredictor
        {
            public int Predict(int ra, int rb, int rc) => rb + ((ra - rc) >> 1);
            public Vector128<short> Predict(Vector128<short> ra, Vector128<short> rb, Vector128<short> rc) => rb + ((ra - rc) >> 1);
            public Vector256<short> Predict(Vector256<short> ra, Vector256<short> rb, Vector256<short> rc) => rb + ((ra - rc) >> 1);
            public Vector512<short> Predict(Vector512<short> ra, Vector512<short> rb, Vector512<short> rc) => rb + ((ra - rc) >> 1);
        }

        internal readonly struct Predictor7 : IPredictor
        {
            public int Predict(int ra, int rb, int rc) => (ra + rb) >> 1;
            public Vector128<short> Predict(Vector128<short> ra, Vector128<short> rb, Vector128<short> rc) => (ra + rb) >> 1;
            public Vector256<short> Predict(Vector256<short> ra, Vector256<short> rb, Vector256<short> rc) => (ra + rb) >> 1;
            public Vector512<short> Predict(Vector512<short> ra, Vector512<short> rb, Vector512<short> rc) => (ra + rb) >> 1;
        }

        // A scan without restarts: the first row predicts from the left, the first column from above.
        private static void DecodeScan<P>(ref Bits bits, ref int tables, ref ushort first, scoped ReadOnlySpan<int> planeAt, scoped ReadOnlySpan<int> table, int width, int height, int initial) where P : struct, IPredictor
        {
            if (planeAt.Length == 1) DecodePlane<P>(ref bits, ref tables, ref Unsafe.Add(ref first, planeAt[0]), table[0], width, height, initial);
            else DecodeInterleaved<P>(ref bits, ref tables, ref first, planeAt, table, width, height, initial);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void DecodePlane<P>(ref Bits bits, ref int tables, ref ushort plane, int t, int width, int height, int initial) where P : struct, IPredictor
        {
            int ra = (ushort)(initial + Difference(ref bits, ref tables, t));
            plane = (ushort)ra;
            for (int x = 1; x < width; x++)
            {
                ra = (ushort)(ra + Difference(ref bits, ref tables, t));
                Unsafe.Add(ref plane, x) = (ushort)ra;
            }
            for (int y = 1; y < height; y++)
            {
                ref ushort row = ref Unsafe.Add(ref plane, y * width);
                ref ushort up = ref Unsafe.Subtract(ref row, width);
                int rc = up;
                ra = (ushort)(rc + Difference(ref bits, ref tables, t));
                row = (ushort)ra;
                for (int x = 1; x < width; x++)
                {
                    int rb = Unsafe.Add(ref up, x);
                    ra = (ushort)(default(P).Predict(ra, rb, rc) + Difference(ref bits, ref tables, t));
                    Unsafe.Add(ref row, x) = (ushort)ra;
                    rc = rb;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void DecodeInterleaved<P>(ref Bits bits, ref int tables, ref ushort first, scoped ReadOnlySpan<int> planeAt, scoped ReadOnlySpan<int> table, int width, int height, int initial) where P : struct, IPredictor
        {
            int count = planeAt.Length;
            for (int i = 0; i < count; i++) Unsafe.Add(ref first, planeAt[i]) = (ushort)(initial + Difference(ref bits, ref tables, table[i]));
            for (int x = 1; x < width; x++)
                for (int i = 0; i < count; i++)
                {
                    ref ushort s = ref Unsafe.Add(ref first, planeAt[i] + x);
                    s = (ushort)(Unsafe.Subtract(ref s, 1) + Difference(ref bits, ref tables, table[i]));
                }
            for (int y = 1; y < height; y++)
            {
                int rowAt = y * width;
                for (int i = 0; i < count; i++)
                {
                    ref ushort s = ref Unsafe.Add(ref first, planeAt[i] + rowAt);
                    s = (ushort)(Unsafe.Subtract(ref s, width) + Difference(ref bits, ref tables, table[i]));
                }
                for (int x = 1; x < width; x++)
                    for (int i = 0; i < count; i++)
                    {
                        ref ushort s = ref Unsafe.Add(ref first, planeAt[i] + rowAt + x);
                        int ra = Unsafe.Subtract(ref s, 1), rb = Unsafe.Subtract(ref s, width), rc = Unsafe.Subtract(ref s, width + 1);
                        s = (ushort)(default(P).Predict(ra, rb, rc) + Difference(ref bits, ref tables, table[i]));
                    }
            }
        }

        // one difference of table t: the whole-difference lookup, else the symbol and its bits
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Difference(ref Bits bits, ref int tables, int t)
        {
            bits.Ensure();
            int entry = Unsafe.Add(ref tables, DcWholeAt + (t << WholeBits) + bits.Peek(WholeBits));
            if (entry != 0)
            {
                bits.Skip(entry & 0xFF);
                return entry >> 8;
            }
            int s = bits.Symbol(ref tables, t);
            if (s > 16) throw new ImageFormatException("lossless JPEG difference category is out of range");
            return s == 16 ? 32768 : bits.Receive(s);
        }

        // Samples shifted back by the point transform, reduced to 8 bits, then through the baseline colour conversion.
        private static void OutputLossless(in Frame f,Span<byte> destination, int pitch, PixelLayout layout)
        {
            int width = f.width, height = f.height, count = f.count, precision = f.precision, pt = f.pointTransform;
            int max = (1 << precision) - 1, down = precision - 8;
            bool ycc = IsYcc(in f), direct = count == 1 && layout == PixelLayout.R8;
            ushort[] samples = f.samples;
            byte[] rows = direct ? Array.Empty<byte>() : ArrayPool<byte>.Shared.Rent(count * width);
            byte[] rgba = layout == PixelLayout.RGBA8 || direct ? Array.Empty<byte>() : ArrayPool<byte>.Shared.Rent(width * 4);
            try
            {
                for (int y = 0; y < height; y++)
                {
                    for (int i = 0; i < count; i++)
                    {
                        ReadOnlySpan<ushort> from = samples.AsSpan(i * width * height + y * width, width);
                        Span<byte> row = direct ? destination.Slice(y * pitch, width) : rows.AsSpan(i * width, width);
                        if (down >= 0) Reduce(from, row, pt, max, down);
                        else
                            for (int x = 0; x < width; x++)
                                row[x] = (byte)((((from[x] << pt) & max) * 255 + max / 2) / max);
                    }
                    if (direct) continue;
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
                if (rows.Length > 0) ArrayPool<byte>.Shared.Return(rows);
                if (rgba.Length > 0) ArrayPool<byte>.Shared.Return(rgba);
            }
        }

        // One row of ((v << pt) & max) >> down, narrowed to bytes; from and row are the same length.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Reduce(ReadOnlySpan<ushort> from, Span<byte> row, int pt, int max, int down)
        {
            ref ushort s = ref MemoryMarshal.GetReference(from);
            ref byte d = ref MemoryMarshal.GetReference(row);
            nuint w = (nuint)row.Length, x = 0;
            if (Vector512.IsHardwareAccelerated)
            {
                Vector512<ushort> mask = Vector512.Create((ushort)max);
                for (; x + 64 <= w; x += 64)
                {
                    Vector512<ushort> a = ((Vector512.LoadUnsafe(ref s, x) << pt) & mask) >> down;
                    Vector512<ushort> b = ((Vector512.LoadUnsafe(ref s, x + 32) << pt) & mask) >> down;
                    Vector512.Narrow(a, b).StoreUnsafe(ref d, x);
                }
            }
            if (Vector256.IsHardwareAccelerated)
            {
                Vector256<ushort> mask = Vector256.Create((ushort)max);
                for (; x + 32 <= w; x += 32)
                {
                    Vector256<ushort> a = ((Vector256.LoadUnsafe(ref s, x) << pt) & mask) >> down;
                    Vector256<ushort> b = ((Vector256.LoadUnsafe(ref s, x + 16) << pt) & mask) >> down;
                    Vector256.Narrow(a, b).StoreUnsafe(ref d, x);
                }
            }
            if (Vector128.IsHardwareAccelerated)
            {
                Vector128<ushort> mask = Vector128.Create((ushort)max);
                for (; x + 16 <= w; x += 16)
                {
                    Vector128<ushort> a = ((Vector128.LoadUnsafe(ref s, x) << pt) & mask) >> down;
                    Vector128<ushort> b = ((Vector128.LoadUnsafe(ref s, x + 8) << pt) & mask) >> down;
                    Vector128.Narrow(a, b).StoreUnsafe(ref d, x);
                }
            }
            for (; x < w; x++) Unsafe.Add(ref d, x) = (byte)(((Unsafe.Add(ref s, x) << pt) & max) >> down);
        }
    }
}
