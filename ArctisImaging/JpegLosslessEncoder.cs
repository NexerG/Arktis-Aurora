using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Arctis.Imaging
{
    // Lossless JPEG (T.81 process 14, SOF3) written the way libjpeg-turbo writes it: 8-bit grey or RGB, one predictor,
    // no point transform or restarts, one optimal Huffman table shared by every component.
    internal static partial class JpegEncoder
    {
        // after the shared tables, per 8-bit difference at d + 510: its count, then (code << 5) | length with extra bits
        private const int CombinedAt = TablesLength, Widest = 510;

        private static ReadOnlySpan<byte> JfifMarker => new byte[] { 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 };
        private static ReadOnlySpan<byte> AdobeMarker => new byte[] { 0xFF, 0xEE, 0, 14, (byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e', 0, 100, 0, 0, 0, 0, 0 };

        internal static void EncodeLossless(ReadOnlySpan<byte> pixels, int width, int height, int pitch, PixelLayout layout, Stream output, in JpegLosslessSettings settings)
        {
            bool grey = layout == PixelLayout.R8 || layout == PixelLayout.RG8;
            int count = grey ? 1 : 3, step = ImageCodec.BytesPerPixel(layout), red = layout == PixelLayout.BGRA8 ? 2 : 0;
            Span<int> channels = stackalloc int[3] { red, 1, 2 - red };
            ReadOnlySpan<int> used = channels.Slice(0, count);
            int predictor = settings.Predictor, total = step * width * height;
            short[] differences = ArrayPool<short>.Shared.Rent(total);
            long[] freq = ArrayPool<long>.Shared.Rent(257);
            int[] tables = ArrayPool<int>.Shared.Rent(CombinedAt + 2 * Widest + 1);
            byte[] spec = ArrayPool<byte>.Shared.Rent(SpecLength);
            byte[] chunk = ArrayPool<byte>.Shared.Rent(1 << 16);
            try
            {
                switch (predictor)
                {
                    case 1: Differences<JpegDecoder.Predictor1>(pixels, width, height, pitch, step, differences); break;
                    case 2: Differences<JpegDecoder.Predictor2>(pixels, width, height, pitch, step, differences); break;
                    case 3: Differences<JpegDecoder.Predictor3>(pixels, width, height, pitch, step, differences); break;
                    case 4: Differences<JpegDecoder.Predictor4>(pixels, width, height, pitch, step, differences); break;
                    case 5: Differences<JpegDecoder.Predictor5>(pixels, width, height, pitch, step, differences); break;
                    case 6: Differences<JpegDecoder.Predictor6>(pixels, width, height, pitch, step, differences); break;
                    default: Differences<JpegDecoder.Predictor7>(pixels, width, height, pitch, step, differences); break;
                }
                Span<int> values = tables.AsSpan(CombinedAt, 2 * Widest + 1);
                values.Clear();
                for (int p = 0; p < total; p += step)
                    for (int i = 0; i < used.Length; i++) values[differences[p + used[i]] + Widest]++;
                Array.Clear(freq, 0, 257);
                for (int d = -Widest; d <= Widest; d++) freq[Length(d)] += values[Widest + d];
                Span<byte> huffman = spec.AsSpan(0, SpecLength);
                Optimal(freq.AsSpan(0, 257), tables, huffman);
                Codes(huffman, tables.AsSpan(CodeAt, 256), tables.AsSpan(SizeAt, 256));
                for (int d = -Widest; d <= Widest; d++)
                {
                    int s = Length(d), extra = (d < 0 ? d - 1 : d) & ((1 << s) - 1);
                    tables[CombinedAt + Widest + d] = (tables[CodeAt + s] << s | extra) << 5 | (tables[SizeAt + s] + s);
                }

                Writer w = new Writer(output, chunk);
                LosslessHeaders(ref w, width, height, count, predictor, huffman);
                ReadOnlySpan<int> combined = tables.AsSpan(CombinedAt, 2 * Widest + 1);
                for (int p = 0; p < total; p += step)
                    for (int i = 0; i < used.Length; i++)
                    {
                        int e = combined[differences[p + used[i]] + Widest];
                        w.Put(e >> 5, e & 31);
                    }
                w.Pad();
                w.Word(0xFFD9);
                w.Flush();
            }
            finally
            {
                ArrayPool<short>.Shared.Return(differences);
                ArrayPool<long>.Shared.Return(freq);
                ArrayPool<int>.Shared.Return(tables);
                ArrayPool<byte>.Shared.Return(spec);
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }

        // Every byte minus its prediction, in the source's own layout without the pitch; alpha bytes are computed and never read.
        private static void Differences<P>(ReadOnlySpan<byte> pixels, int width, int height, int pitch, int step, short[] differences) where P : struct, JpegDecoder.IPredictor
        {
            int rowBytes = width * step;
            ref byte first = ref MemoryMarshal.GetReference(pixels);
            ref short out0 = ref MemoryMarshal.GetArrayDataReference(differences);
            for (int x = 0; x < rowBytes; x++) Unsafe.Add(ref out0, x) = (short)(Unsafe.Add(ref first, x) - (x < step ? 128 : Unsafe.Add(ref first, x - step)));
            for (int y = 1; y < height; y++)
            {
                ref byte row = ref Unsafe.Add(ref first, y * pitch);
                ref byte up = ref Unsafe.Subtract(ref row, pitch);
                ref short d = ref Unsafe.Add(ref out0, y * rowBytes);
                for (int x = 0; x < step; x++) Unsafe.Add(ref d, x) = (short)(Unsafe.Add(ref row, x) - Unsafe.Add(ref up, x));
                PredictRow<P>(ref row, ref up, ref d, (nuint)step, (nuint)rowBytes);
            }
        }

        // One row past its first pixel: each byte minus P(left, above, above-left).
        private static void PredictRow<P>(ref byte row, ref byte up, ref short d, nuint step, nuint end) where P : struct, JpegDecoder.IPredictor
        {
            nuint x = step;
            if (Vector512.IsHardwareAccelerated)
                for (; x + 64 <= end; x += 64)
                {
                    (Vector512<ushort> s0, Vector512<ushort> s1) = Vector512.Widen(Vector512.LoadUnsafe(ref row, x));
                    (Vector512<ushort> a0, Vector512<ushort> a1) = Vector512.Widen(Vector512.LoadUnsafe(ref row, x - step));
                    (Vector512<ushort> b0, Vector512<ushort> b1) = Vector512.Widen(Vector512.LoadUnsafe(ref up, x));
                    (Vector512<ushort> c0, Vector512<ushort> c1) = Vector512.Widen(Vector512.LoadUnsafe(ref up, x - step));
                    (s0.AsInt16() - default(P).Predict(a0.AsInt16(), b0.AsInt16(), c0.AsInt16())).StoreUnsafe(ref d, x);
                    (s1.AsInt16() - default(P).Predict(a1.AsInt16(), b1.AsInt16(), c1.AsInt16())).StoreUnsafe(ref d, x + 32);
                }
            if (Vector256.IsHardwareAccelerated)
                for (; x + 32 <= end; x += 32)
                {
                    (Vector256<ushort> s0, Vector256<ushort> s1) = Vector256.Widen(Vector256.LoadUnsafe(ref row, x));
                    (Vector256<ushort> a0, Vector256<ushort> a1) = Vector256.Widen(Vector256.LoadUnsafe(ref row, x - step));
                    (Vector256<ushort> b0, Vector256<ushort> b1) = Vector256.Widen(Vector256.LoadUnsafe(ref up, x));
                    (Vector256<ushort> c0, Vector256<ushort> c1) = Vector256.Widen(Vector256.LoadUnsafe(ref up, x - step));
                    (s0.AsInt16() - default(P).Predict(a0.AsInt16(), b0.AsInt16(), c0.AsInt16())).StoreUnsafe(ref d, x);
                    (s1.AsInt16() - default(P).Predict(a1.AsInt16(), b1.AsInt16(), c1.AsInt16())).StoreUnsafe(ref d, x + 16);
                }
            if (Vector128.IsHardwareAccelerated)
                for (; x + 16 <= end; x += 16)
                {
                    (Vector128<ushort> s0, Vector128<ushort> s1) = Vector128.Widen(Vector128.LoadUnsafe(ref row, x));
                    (Vector128<ushort> a0, Vector128<ushort> a1) = Vector128.Widen(Vector128.LoadUnsafe(ref row, x - step));
                    (Vector128<ushort> b0, Vector128<ushort> b1) = Vector128.Widen(Vector128.LoadUnsafe(ref up, x));
                    (Vector128<ushort> c0, Vector128<ushort> c1) = Vector128.Widen(Vector128.LoadUnsafe(ref up, x - step));
                    (s0.AsInt16() - default(P).Predict(a0.AsInt16(), b0.AsInt16(), c0.AsInt16())).StoreUnsafe(ref d, x);
                    (s1.AsInt16() - default(P).Predict(a1.AsInt16(), b1.AsInt16(), c1.AsInt16())).StoreUnsafe(ref d, x + 8);
                }
            for (; x < end; x++)
                Unsafe.Add(ref d, x) = (short)(Unsafe.Add(ref row, x) - default(P).Predict(Unsafe.Add(ref row, x - step), Unsafe.Add(ref up, x), Unsafe.Add(ref up, x - step)));
        }

        private static void LosslessHeaders(ref Writer w, int width, int height, int count, int predictor, ReadOnlySpan<byte> spec)
        {
            w.Word(0xFFD8);
            ReadOnlySpan<byte> app = count == 1 ? JfifMarker : AdobeMarker;
            for (int i = 0; i < app.Length; i++) w.Byte(app[i]);
            ReadOnlySpan<byte> ids = count == 1 ? "\u0001"u8 : "RGB"u8;

            w.Word(0xFFC3);
            w.Word(8 + 3 * count);
            w.Byte(8);
            w.Word(height);
            w.Word(width);
            w.Byte(count);
            for (int i = 0; i < count; i++)
            {
                w.Byte(ids[i]);
                w.Byte(0x11);
                w.Byte(0);
            }

            int n = 0;
            for (int l = 0; l < 16; l++) n += spec[l];
            w.Word(0xFFC4);
            w.Word(19 + n);
            w.Byte(0);
            for (int i = 0; i < 16 + n; i++) w.Byte(spec[i]);

            w.Word(0xFFDA);
            w.Word(6 + 2 * count);
            w.Byte(count);
            for (int i = 0; i < count; i++)
            {
                w.Byte(ids[i]);
                w.Byte(0);
            }
            w.Byte(predictor);
            w.Byte(0);
            w.Byte(0);
        }
    }
}
