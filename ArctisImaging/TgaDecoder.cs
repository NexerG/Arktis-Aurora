using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Arctis.Imaging
{
    // Truevision TGA: colour-mapped, true-colour and grey, plain or run-length encoded, either origin.
    internal static class TgaDecoder
    {
        private const int HeaderSize = 18;
        private const int Mapped = 1, TrueColour = 2, Grey = 3, RunLength = 8;

        private struct Info
        {
            public int idLength, mapType, imageType, mapFirst, mapLength, mapEntryBits, width, height, depth, alphaBits, pixelStart;
            public bool rightToLeft, topDown;
        }

        internal static bool TryReadHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (!TryReadInfo(data, out Info info)) return false;
            int kind = info.imageType & 7;
            int channels = kind == Grey ? (info.depth == 16 ? 2 : 1) : (info.alphaBits > 0 ? 4 : 3);
            header = new ImageHeader(ImageFormat.Tga, info.width, info.height, channels, 8, info.alphaBits > 0);
            return true;
        }

        // Decodes every row, flipping to top-down and left-to-right.
        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int pitch, PixelLayout layout)
        {
            if (!TryReadInfo(data, out Info info)) throw new ImageFormatException("TGA header is invalid");
            int bpp = (info.depth + 7) / 8;
            int rowBytes = info.width * bpp;
            bool runLength = (info.imageType & RunLength) != 0;
            if (!runLength && info.pixelStart + (long)rowBytes * info.height > data.Length) throw new ImageFormatException("TGA pixel data runs past the end");

            bool alpha = info.alphaBits > 0;
            uint[] palette = (info.imageType & 7) == Mapped ? ReadPalette(data, info, alpha) : Array.Empty<uint>();
            byte[] raw = ArrayPool<byte>.Shared.Rent(rowBytes);
            byte[] rgba = ArrayPool<byte>.Shared.Rent(info.width * 4);
            bool anyAlpha = false;
            try
            {
                int pos = info.pixelStart, left = 0, runAt = 0;
                bool run = false;
                for (int row = 0; row < info.height; row++)
                {
                    ReadOnlySpan<byte> src;
                    if (runLength)
                    {
                        ReadRunLengthRow(data, raw.AsSpan(0, rowBytes), bpp, ref pos, ref left, ref run, ref runAt);
                        src = raw;
                    }
                    else src = data.Slice(info.pixelStart + row * rowBytes, rowBytes);
                    ConvertRow(src, rgba.AsSpan(0, info.width * 4), info, palette, alpha, ref anyAlpha);
                    int y = info.topDown ? row : info.height - 1 - row;
                    PixelRows.FromRgba(rgba, destination.Slice(y * pitch), info.width, layout);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(raw);
                ArrayPool<byte>.Shared.Return(rgba);
                if (palette.Length > 0) ArrayPool<uint>.Shared.Return(palette);
            }

            // an alpha channel that is zero everywhere carries no alpha, not a transparent picture
            if (alpha && !anyAlpha)
                for (int y = 0; y < info.height; y++) PixelRows.MakeOpaque(destination.Slice(y * pitch), info.width, layout);
        }

        // Fills one row of stored pixels from packets, which may run on across rows.
        private static void ReadRunLengthRow(ReadOnlySpan<byte> data, Span<byte> raw, int bpp, ref int pos, ref int left, ref bool run, ref int runAt)
        {
            for (int at = 0; at < raw.Length; at += bpp)
            {
                if (left == 0)
                {
                    if (pos >= data.Length) throw new ImageFormatException("TGA run-length data ends early");
                    byte packet = data[pos++];
                    left = (packet & 0x7F) + 1;
                    run = (packet & 0x80) != 0;
                    if (run)
                    {
                        runAt = pos;
                        pos += bpp;
                    }
                }
                int from = run ? runAt : pos;
                if (from + bpp > data.Length) throw new ImageFormatException("TGA run-length data ends early");
                for (int b = 0; b < bpp; b++) raw[at + b] = data[from + b];
                if (!run) pos += bpp;
                left--;
            }
        }

        // One row of stored pixels as RGBA8, reversed when the file runs right to left.
        private static void ConvertRow(ReadOnlySpan<byte> src, Span<byte> rgba, in Info info, uint[] palette, bool alpha, ref bool anyAlpha)
        {
            int kind = info.imageType & 7;
            int bpp = (info.depth + 7) / 8;
            bool reverse = info.rightToLeft;
            for (int x = 0, o = 0; o < rgba.Length; x++, o += 4)
            {
                uint c;
                if (kind == Mapped)
                {
                    int index = (info.depth == 8 ? src[x] : BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(x * 2))) - info.mapFirst;
                    c = (uint)index < (uint)info.mapLength ? palette[index] : alpha ? 0 : 0xFF000000u;
                }
                else if (kind == Grey)
                {
                    if (info.depth == 8) c = src[x] * 0x010101u | 0xFF000000u;
                    else c = src[x * 2] * 0x010101u | (alpha ? (uint)src[x * 2 + 1] << 24 : 0xFF000000u);
                }
                else c = Colour(src.Slice(x * bpp), info.depth, alpha);
                if (alpha) anyAlpha |= c >> 24 != 0;
                BinaryPrimitives.WriteUInt32LittleEndian(rgba.Slice(reverse ? rgba.Length - 4 - o : o), c);
            }
        }

        private static uint[] ReadPalette(ReadOnlySpan<byte> data, in Info info, bool alpha)
        {
            int entryBytes = (info.mapEntryBits + 7) / 8;
            int start = HeaderSize + info.idLength;
            uint[] palette = ArrayPool<uint>.Shared.Rent(info.mapLength);
            for (int i = 0; i < info.mapLength; i++) palette[i] = Colour(data.Slice(start + i * entryBytes), info.mapEntryBits, alpha);
            return palette;
        }

        // A stored 15, 16, 24 or 32-bit colour as little-endian RGBA8.
        private static uint Colour(ReadOnlySpan<byte> p, int bits, bool alpha)
        {
            if (bits == 24) return p[2] | (uint)p[1] << 8 | (uint)p[0] << 16 | 0xFF000000u;
            if (bits == 32) return p[2] | (uint)p[1] << 8 | (uint)p[0] << 16 | (alpha ? (uint)p[3] << 24 : 0xFF000000u);
            int v = BinaryPrimitives.ReadUInt16LittleEndian(p);
            uint r = Widen5((v >> 10) & 31), g = Widen5((v >> 5) & 31), b = Widen5(v & 31);
            uint a = bits == 16 && alpha && (v & 0x8000) == 0 ? 0u : 255u;
            return r | g << 8 | b << 16 | a << 24;
        }

        private static uint Widen5(int c) => (uint)(c << 3 | c >> 2);

        private static bool TryReadInfo(ReadOnlySpan<byte> data, out Info info)
        {
            info = default;
            if (data.Length < HeaderSize) return false;
            info.idLength = data[0];
            info.mapType = data[1];
            info.imageType = data[2];
            info.mapFirst = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(3));
            info.mapLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(5));
            info.mapEntryBits = data[7];
            info.width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(12));
            info.height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(14));
            info.depth = data[16];
            byte descriptor = data[17];
            info.alphaBits = descriptor & 15;
            info.rightToLeft = (descriptor & 0x10) != 0;
            info.topDown = (descriptor & 0x20) != 0;

            if (info.mapType > 1 || (descriptor & 0xC0) != 0 || info.width == 0 || info.height == 0 || info.alphaBits > 8) return false;
            int kind = info.imageType & 7;
            if ((info.imageType & ~(7 | RunLength)) != 0) return false;
            bool validEntry = info.mapEntryBits == 15 || info.mapEntryBits == 16 || info.mapEntryBits == 24 || info.mapEntryBits == 32;
            if (kind == Mapped)
            {
                if (info.mapType != 1 || info.mapLength == 0 || !validEntry || (info.depth != 8 && info.depth != 16)) return false;
            }
            else if (kind == TrueColour)
            {
                if (info.depth != 15 && info.depth != 16 && info.depth != 24 && info.depth != 32) return false;
            }
            else if (kind == Grey)
            {
                if (info.depth != 8 && info.depth != 16) return false;
            }
            else return false;
            if (info.mapType == 1 && info.mapLength > 0 && !validEntry) return false;

            int mapBytes = info.mapType == 1 ? info.mapLength * ((info.mapEntryBits + 7) / 8) : 0;
            info.pixelStart = HeaderSize + info.idLength + mapBytes;
            return info.pixelStart <= data.Length;
        }
    }
}
