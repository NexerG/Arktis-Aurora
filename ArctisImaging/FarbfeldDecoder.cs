using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Arctis.Imaging
{
    // farbfeld: magic, big-endian width and height, then RGBA16 big-endian rows; each sample keeps its high byte.
    internal static class FarbfeldDecoder
    {
        private const int HeaderSize = 16;

        internal static bool TryReadHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (data.Length < HeaderSize || !data.Slice(0, 8).SequenceEqual("farbfeld"u8)) return false;
            uint width = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8)), height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(12));
            if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue) return false;
            header = new ImageHeader(ImageFormat.Farbfeld, (int)width, (int)height, 4, 16, true);
            return true;
        }

        internal static void Decode(ReadOnlySpan<byte> data, Span<byte> destination, int pitch, PixelLayout layout)
        {
            if (!TryReadHeader(data, out ImageHeader header)) throw new ImageFormatException("farbfeld header is invalid");
            int width = header.Width, samples = width * 4;
            if (HeaderSize + (long)width * 8 * header.Height > data.Length) throw new ImageFormatException("farbfeld pixels run past the end");
            byte[] rgba = ArrayPool<byte>.Shared.Rent(width * 4);
            try
            {
                for (int y = 0; y < header.Height; y++)
                {
                    ReadOnlySpan<byte> row = data.Slice((int)(HeaderSize + (long)y * width * 8), width * 8);
                    for (int i = 0; i < samples; i++) rgba[i] = row[i * 2];
                    PixelRows.FromRgba(rgba, destination.Slice(y * pitch), width, layout);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rgba);
            }
        }
    }
}
