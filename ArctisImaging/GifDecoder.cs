using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Arctis.Imaging
{
    // GIF 87a / 89a: the first frame, placed on a transparent canvas the size of the logical screen.
    internal static class GifDecoder
    {
        private const int MaxCodes = 4096;
        private const uint MissingColour = 0xFF000000u;

        internal static bool TryReadHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (data.Length < 13 || data[0] != (byte)'G' || data[1] != (byte)'I' || data[2] != (byte)'F' || data[3] != (byte)'8'
                || (data[4] != (byte)'7' && data[4] != (byte)'9') || data[5] != (byte)'a')
                return false;
            int width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6));
            int height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8));
            header = new ImageHeader(ImageFormat.Gif, width, height, 4, 8, true);
            return true;
        }

        // Decodes the first image block; pixels outside it, and transparent ones, are transparent black.
        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int pitch, PixelLayout layout)
        {
            int width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6));
            int height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8));
            uint[] palette = ArrayPool<uint>.Shared.Rent(256);
            byte[] rgba = ArrayPool<byte>.Shared.Rent(width * 4);
            byte[] indices = Array.Empty<byte>();
            try
            {
                int pos = 13;
                byte screen = data[10];
                int globalCount = 0;
                if ((screen & 0x80) != 0)
                {
                    globalCount = 2 << (screen & 7);
                    if (pos + globalCount * 3 > data.Length) throw new ImageFormatException("GIF global colour table runs past the end");
                    pos += globalCount * 3;
                }

                // blocks up to the first image descriptor; a graphic control extension carries its transparency
                int transparent = -1;
                while (true)
                {
                    if (pos >= data.Length) throw new ImageFormatException("GIF has no image");
                    byte introducer = data[pos++];
                    if (introducer == 0x2C) break;
                    if (introducer != 0x21) throw new ImageFormatException($"GIF block 0x{introducer:X2} is not an image or an extension");
                    if (pos >= data.Length) throw new ImageFormatException("GIF extension ends early");
                    byte label = data[pos++];
                    if (label == 0xF9 && pos + 5 <= data.Length && data[pos] >= 4)
                        transparent = (data[pos + 1] & 1) != 0 ? data[pos + 4] : -1;
                    pos = SkipSubBlocks(data, pos);
                }

                if (pos + 9 > data.Length) throw new ImageFormatException("GIF image descriptor ends early");
                int left = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos));
                int top = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos + 2));
                int frameWidth = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos + 4));
                int frameHeight = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos + 6));
                byte frame = data[pos + 8];
                pos += 9;
                if ((frame & 0x80) != 0)
                {
                    int localCount = 2 << (frame & 7);
                    if (pos + localCount * 3 > data.Length) throw new ImageFormatException("GIF local colour table runs past the end");
                    ReadPalette(data.Slice(pos, localCount * 3), palette);
                    pos += localCount * 3;
                }
                else ReadPalette(data.Slice(13, globalCount * 3), palette);
                if (transparent >= 0) palette[transparent] = 0;

                int count = frameWidth * frameHeight;
                indices = ArrayPool<byte>.Shared.Rent(Math.Max(count, 1));
                int produced = count == 0 ? 0 : Decompress(data, pos, indices.AsSpan(0, count));

                // canvas rows; an interlaced frame stores its rows in four passes
                bool interlaced = (frame & 0x40) != 0;
                int pass1 = (frameHeight + 7) / 8, pass2 = (frameHeight + 3) / 8, pass3 = (frameHeight + 1) / 4;
                int x0 = Math.Min(left, width), x1 = Math.Min(left + frameWidth, width);
                Span<byte> row = rgba.AsSpan(0, width * 4);
                for (int y = 0; y < height; y++)
                {
                    row.Clear();
                    int fy = y - top;
                    if (fy >= 0 && fy < frameHeight)
                    {
                        int stored = !interlaced ? fy : (fy & 7) == 0 ? fy >> 3 : (fy & 7) == 4 ? pass1 + (fy >> 3)
                            : (fy & 3) == 2 ? pass1 + pass2 + (fy >> 2) : pass1 + pass2 + pass3 + (fy >> 1);
                        int start = stored * frameWidth - left;
                        int end = Math.Min(x1, produced - start);
                        for (int x = x0; x < end; x++) BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(x * 4), palette[indices[start + x]]);
                    }
                    PixelRows.FromRgba(row, destination.Slice(y * pitch), width, layout);
                }
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(palette);
                ArrayPool<byte>.Shared.Return(rgba);
                if (indices.Length > 0) ArrayPool<byte>.Shared.Return(indices);
            }
        }

        // Fills all 256 entries; an index past the table's end reads opaque black.
        private static void ReadPalette(ReadOnlySpan<byte> table, uint[] palette)
        {
            int entries = table.Length / 3;
            for (int i = 0; i < entries; i++) palette[i] = table[i * 3] | (uint)table[i * 3 + 1] << 8 | (uint)table[i * 3 + 2] << 16 | 0xFF000000u;
            palette.AsSpan(entries, 256 - entries).Fill(MissingColour);
        }

        private static int SkipSubBlocks(ReadOnlySpan<byte> data, int pos)
        {
            while (pos < data.Length)
            {
                int size = data[pos++];
                if (size == 0) return pos;
                pos += size;
            }
            throw new ImageFormatException("GIF sub-blocks run past the end");
        }

        // Variable-width LZW over the image's sub-blocks; returns how many indices it wrote, fewer if the data ends early.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static int Decompress(ReadOnlySpan<byte> data, int pos, Span<byte> output)
        {
            if (pos >= data.Length) throw new ImageFormatException("GIF image data ends early");
            int minimum = data[pos++];
            if (minimum < 1 || minimum > 11) throw new ImageFormatException($"GIF LZW minimum code size {minimum} is out of range");

            // the sub-blocks joined into one run
            int total = 0;
            for (int p = pos; p < data.Length && data[p] != 0; p += data[p] + 1) total += data[p];
            byte[] stream = ArrayPool<byte>.Shared.Rent(Math.Max(total, 1));
            int[] table = ArrayPool<int>.Shared.Rent(MaxCodes * 2);
            try
            {
                int length = 0;
                for (int p = pos; p < data.Length && data[p] != 0; p += data[p] + 1)
                {
                    int size = Math.Min(data[p], data.Length - p - 1);
                    data.Slice(p + 1, size).CopyTo(stream.AsSpan(length));
                    length += size;
                }

                // a code's entry: prefix | suffix << 16, and first byte | length << 8
                Span<int> links = table.AsSpan(0, MaxCodes);
                Span<int> spans = table.AsSpan(MaxCodes, MaxCodes);
                int clear = 1 << minimum, endCode = clear + 1;
                for (int c = 0; c < clear; c++)
                {
                    links[c] = c << 16;
                    spans[c] = c | 1 << 8;
                }

                int codeSize = minimum + 1, next = clear + 2, previous = -1, written = 0, read = 0, bitCount = 0;
                ulong bits = 0;
                while (written < output.Length)
                {
                    while (bitCount < codeSize && read < length)
                    {
                        bits |= (ulong)stream[read++] << bitCount;
                        bitCount += 8;
                    }
                    if (bitCount < codeSize) break;
                    int code = (int)(bits & ((1u << codeSize) - 1));
                    bits >>= codeSize;
                    bitCount -= codeSize;

                    if (code == clear)
                    {
                        codeSize = minimum + 1;
                        next = clear + 2;
                        previous = -1;
                        continue;
                    }
                    if (code == endCode) break;
                    if (previous < 0)
                    {
                        if (code >= clear) break;
                    }
                    else
                    {
                        if (code > next) break;
                        if (next < MaxCodes)
                        {
                            int suffix = (code < next ? spans[code] : spans[previous]) & 0xFF;
                            links[next] = previous | suffix << 16;
                            spans[next] = (spans[previous] & 0xFF) | ((spans[previous] >> 8) + 1) << 8;
                            next++;
                            if (next == 1 << codeSize && codeSize < 12) codeSize++;
                        }
                    }

                    // the code's string, written back to front from its last byte
                    int stringLength = spans[code] >> 8;
                    int end = written + stringLength;
                    int walk = code;
                    for (int i = end - 1; i >= written; i--)
                    {
                        int link = links[walk];
                        if (i < output.Length) output[i] = (byte)(link >> 16);
                        walk = link & 0xFFFF;
                    }
                    written = Math.Min(end, output.Length);
                    previous = code;
                }
                return written;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(stream);
                ArrayPool<int>.Shared.Return(table);
            }
        }
    }
}
