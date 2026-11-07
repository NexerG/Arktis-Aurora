using System;
using System.Buffers.Binary;

namespace Arctis.Imaging
{
    // Windows icons and cursors: a directory of PNG or DIB entries, of which the largest and deepest is decoded.
    internal static class IcoDecoder
    {
        private const int DirectorySize = 6, EntrySize = 16;

        internal static bool TryReadHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (!TryPickEntry(data, out ReadOnlySpan<byte> entry)) return false;
            if (PngDecoder.TryReadHeader(entry, out ImageHeader png))
            {
                header = new ImageHeader(ImageFormat.Ico, png.Width, png.Height, png.Channels, png.BitDepth, png.HasAlpha);
                return true;
            }
            if (!BmpDecoder.TryReadDibHeader(entry, out ImageHeader dib) || dib.Height < 2) return false;
            header = new ImageHeader(ImageFormat.Ico, dib.Width, dib.Height / 2, 4, 8, true);
            return true;
        }

        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int pitch, PixelLayout layout)
        {
            if (!TryPickEntry(data, out ReadOnlySpan<byte> entry)) throw new ImageFormatException("icon directory is invalid");
            if (PngDecoder.TryReadHeader(entry, out _)) PngDecoder.Decode(entry, destination, pitch, layout);
            else BmpDecoder.DecodeIcon(entry, destination, pitch, layout);
        }

        // The entry with the largest area, then the most bits per pixel; every entry must lie inside the file.
        private static bool TryPickEntry(ReadOnlySpan<byte> data, out ReadOnlySpan<byte> entry)
        {
            entry = default;
            if (data.Length < DirectorySize + EntrySize || data[0] != 0 || data[1] != 0 || (data[2] != 1 && data[2] != 2) || data[3] != 0) return false;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4));
            if (count == 0 || DirectorySize + (long)count * EntrySize > data.Length) return false;
            long bestArea = -1;
            int bestBits = -1, bestAt = 0, bestLength = 0;
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> e = data.Slice(DirectorySize + i * EntrySize, EntrySize);
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(8)), at = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(12));
                if (length == 0 || at < DirectorySize + (uint)count * EntrySize || at + (ulong)length > (ulong)data.Length) return false;
                // size and depth from the entry's own header
                ReadOnlySpan<byte> image = data.Slice((int)at, (int)length);
                long area;
                int bits;
                if (PngDecoder.TryReadHeader(image, out ImageHeader png))
                {
                    area = (long)png.Width * png.Height;
                    bits = png.BitsPerPixel;
                }
                else if (BmpDecoder.TryReadDibHeader(image, out ImageHeader dib))
                {
                    area = (long)dib.Width * (dib.Height / 2);
                    bits = image.Length >= 16 ? BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(14)) : 0;
                }
                else return false;
                if (area < bestArea || (area == bestArea && bits <= bestBits)) continue;
                bestArea = area;
                bestBits = bits;
                bestAt = (int)at;
                bestLength = (int)length;
            }
            entry = data.Slice(bestAt, bestLength);
            return true;
        }
    }
}
