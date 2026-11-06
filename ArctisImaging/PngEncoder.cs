using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Arctis.Imaging
{
    // Each row filtered by Sub or Up, whichever leaves the smaller sum of residuals.
    internal static unsafe class PngEncoder
    {
        internal static void Encode(ReadOnlySpan<byte> pixels, int width, int height, int pitch, PixelLayout layout, Stream output, in DeflateSettings deflate)
        {
            int bpp = ImageCodec.BytesPerPixel(layout);
            int rowBytes = width * bpp;
            byte colorType = layout switch
            {
                PixelLayout.R8 => 0,
                PixelLayout.RG8 => 4,
                _ => 6
            };

            Span<byte> signature = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(signature, 0x89504E470D0A1A0Aul);
            Span<byte> ihdr = stackalloc byte[13];
            ihdr.Clear();
            BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4), height);
            ihdr[8] = 8;
            ihdr[9] = colorType;
            output.Write(signature);
            WriteChunk(output, 0x49484452, ihdr);

            MemoryStream compressed = new MemoryStream();
            int filteredBytes = height * (rowBytes + 1);
            bool swizzle = layout == PixelLayout.BGRA8;
            byte[] buffers = ArrayPool<byte>.Shared.Rent(3 * rowBytes + 1 + filteredBytes);
            try
            {
                fixed (byte* source = pixels, work = buffers)
                {
                    // prev starts as the zero row; spare takes a swizzled row
                    byte* prev = work, spare = work + rowBytes, up = work + 2 * rowBytes, filtered = up + rowBytes + 1;
                    new Span<byte>(prev, rowBytes).Clear();
                    up[0] = 2;
                    for (int y = 0; y < height; y++)
                    {
                        byte* cur = source + (long)y * pitch;
                        if (swizzle)
                        {
                            new ReadOnlySpan<byte>(cur, rowBytes).CopyTo(new Span<byte>(spare, rowBytes));
                            ImageOps.SwizzleBgraToRgba(new Span<byte>(spare, rowBytes));
                            cur = spare;
                            spare = prev;
                        }

                        byte* sub = filtered + (long)y * (rowBytes + 1);
                        sub[0] = 1;
                        long subCost = 0, upCost = 0;
                        int i = 0;
                        for (; i < bpp; i++)
                        {
                            byte u = (byte)(cur[i] - prev[i]);
                            sub[i + 1] = cur[i];
                            up[i + 1] = u;
                            subCost += Residual(cur[i]);
                            upCost += Residual(u);
                        }
                        if (Ssse3.IsSupported)
                        {
                            Vector128<ulong> subSum = Vector128<ulong>.Zero, upSum = Vector128<ulong>.Zero;
                            for (; i + 16 <= rowBytes; i += 16)
                            {
                                Vector128<byte> c = Sse2.LoadVector128(cur + i);
                                Vector128<byte> s = c - Sse2.LoadVector128(cur + i - bpp), u = c - Sse2.LoadVector128(prev + i);
                                Sse2.Store(sub + 1 + i, s);
                                Sse2.Store(up + 1 + i, u);
                                subSum += Sse2.SumAbsoluteDifferences(Ssse3.Abs(s.AsSByte()), Vector128<byte>.Zero).AsUInt64();
                                upSum += Sse2.SumAbsoluteDifferences(Ssse3.Abs(u.AsSByte()), Vector128<byte>.Zero).AsUInt64();
                            }
                            subCost += (long)Vector128.Sum(subSum);
                            upCost += (long)Vector128.Sum(upSum);
                        }
                        for (; i < rowBytes; i++)
                        {
                            byte s = (byte)(cur[i] - cur[i - bpp]), u = (byte)(cur[i] - prev[i]);
                            sub[i + 1] = s;
                            up[i + 1] = u;
                            subCost += Residual(s);
                            upCost += Residual(u);
                        }
                        if (upCost < subCost) new ReadOnlySpan<byte>(up, rowBytes + 1).CopyTo(new Span<byte>(sub, rowBytes + 1));
                        prev = cur;
                    }
                }
                Deflater.Deflate(buffers.AsSpan(3 * rowBytes + 1, filteredBytes), compressed, deflate);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffers);
            }

            WriteChunk(output, 0x49444154, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
            WriteChunk(output, 0x49454E44, ReadOnlySpan<byte>.Empty);
        }

        private static int Residual(byte v) => (sbyte)v < 0 ? -(sbyte)v : v;

        private static void WriteChunk(Stream output, uint type, ReadOnlySpan<byte> body)
        {
            Span<byte> head = stackalloc byte[8];
            BinaryPrimitives.WriteInt32BigEndian(head, body.Length);
            BinaryPrimitives.WriteUInt32BigEndian(head.Slice(4), type);
            uint crc = Crc32.Update(Crc32.Update(0, head.Slice(4)), body);
            Span<byte> tail = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
            output.Write(head);
            output.Write(body);
            output.Write(tail);
        }
    }
}
