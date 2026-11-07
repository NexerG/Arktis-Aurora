using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;

namespace Arctis.Imaging
{
    // Windows icons and cursors: a directory, then each entry as an RGBA8 PNG or a DIB with an AND mask, in order.
    internal static class IcoEncoder
    {
        internal static void Encode(ReadOnlySpan<IconEntry> entries, bool cursor, Stream output, in DeflateSettings deflate)
        {
            int directorySize = 6 + entries.Length * 16;
            byte[] directory = new byte[directorySize];
            directory[2] = cursor ? (byte)2 : (byte)1;
            BinaryPrimitives.WriteUInt16LittleEndian(directory.AsSpan(4), (ushort)entries.Length);
            MemoryStream body = new MemoryStream();
            for (int i = 0; i < entries.Length; i++)
            {
                IconEntry entry = entries[i];
                Image image = entry.Image;
                long start = body.Length;
                int bits = 32, colours = 0;
                if (entry.Encoding == IconEncoding.Png) WritePng(image, body, deflate);
                else
                {
                    BmpPixelFormat format = entry.BmpFormat == BmpPixelFormat.Auto ? BmpPixelFormat.Argb32 : entry.BmpFormat;
                    bits = format switch
                    {
                        BmpPixelFormat.Indexed1 => 1,
                        BmpPixelFormat.Indexed4 => 4,
                        BmpPixelFormat.Indexed8 => 8,
                        BmpPixelFormat.Rgb555 => 16,
                        BmpPixelFormat.Rgb24 => 24,
                        _ => 32
                    };
                    if (bits < 8) colours = 1 << bits;
                    BmpEncoder.EncodeIconEntry(image, format, body);
                }

                Span<byte> e = directory.AsSpan(6 + i * 16, 16);
                e[0] = (byte)(image.Width == 256 ? 0 : image.Width);
                e[1] = (byte)(image.Height == 256 ? 0 : image.Height);
                e[2] = (byte)colours;
                BinaryPrimitives.WriteUInt16LittleEndian(e.Slice(4), cursor ? (ushort)entry.HotspotX : (ushort)1);
                BinaryPrimitives.WriteUInt16LittleEndian(e.Slice(6), cursor ? (ushort)entry.HotspotY : (ushort)bits);
                BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(8), (uint)(body.Length - start));
                BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(12), (uint)(directorySize + start));
            }
            output.Write(directory);
            output.Write(body.GetBuffer(), 0, (int)body.Length);
        }

        private static void WritePng(Image image, Stream output, in DeflateSettings deflate)
        {
            if (image.Layout == PixelLayout.RGBA8 || image.Layout == PixelLayout.BGRA8)
            {
                PngEncoder.Encode(image.Pixels, image.Width, image.Height, image.RowPitch, image.Layout, output, deflate);
                return;
            }
            int width = image.Width, height = image.Height;
            byte[] rgba = ArrayPool<byte>.Shared.Rent(width * height * 4);
            try
            {
                for (int y = 0; y < height; y++) PixelRows.ToRgba(image.Row(y), rgba.AsSpan(y * width * 4), width, image.Layout);
                PngEncoder.Encode(rgba, width, height, width * 4, PixelLayout.RGBA8, output, deflate);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rgba);
            }
        }
    }
}
