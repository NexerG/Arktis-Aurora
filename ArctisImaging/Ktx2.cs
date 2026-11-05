using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace Arctis.Imaging
{
    // KTX 2.0: 2D, one layer and face, 8-bit R / RG / RGBA / BGRA as UNORM or SRGB, plain or ZLIB-supercompressed.
    internal static class Ktx2
    {
        private const int IndexEnd = 80, LevelEntry = 24;
        internal const int SchemeNone = 0, SchemeZlib = 3;
        private const uint R8Unorm = 9, R8Srgb = 15, RG8Unorm = 16, RG8Srgb = 22, RGBA8Unorm = 37, RGBA8Srgb = 43, BGRA8Unorm = 44, BGRA8Srgb = 50;

        private static ReadOnlySpan<byte> Identifier => new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };
        private static ReadOnlySpan<byte> Writer => "KTXwriter\0Arctis.Imaging\0"u8;

        // Recognises the identifier and reads level 0's size; never throws.
        internal static bool TryReadHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (data.Length < IndexEnd || !IsKtx2(data)) return false;
            int channels = 0;
            if (TryLayout(U32(data, 12), out PixelLayout layout, out _)) channels = ImageCodec.BytesPerPixel(layout);
            header = new ImageHeader(ImageFormat.Ktx2, (int)Math.Min(U32(data, 20), int.MaxValue), (int)Math.Min(U32(data, 24), int.MaxValue), channels, channels == 0 ? 0 : 8, channels > 1);
            return true;
        }

        // Validates everything a decode relies on: format, shape, scheme and every level's extent.
        internal static Ktx2Info ReadInfo(ReadOnlySpan<byte> data, out int scheme)
        {
            if (data.Length < IndexEnd) throw new ImageFormatException("KTX2 file is shorter than its header");
            uint format = U32(data, 12);
            if (!TryLayout(format, out PixelLayout layout, out bool srgb)) throw new NotSupportedException($"KTX2 vkFormat {format} is not supported");
            if (U32(data, 16) != 1) throw new ImageFormatException($"KTX2 typeSize {U32(data, 16)} is not 1 for an 8-bit format");
            uint width = U32(data, 20), height = U32(data, 24);
            if (width == 0) throw new ImageFormatException("KTX2 width is 0");
            if (height == 0) throw new NotSupportedException("1D KTX2 textures are not supported");
            if (U32(data, 28) != 0) throw new NotSupportedException("3D KTX2 textures are not supported");
            if (U32(data, 32) != 0) throw new NotSupportedException("KTX2 array textures are not supported");
            if (U32(data, 36) != 1) throw new NotSupportedException($"KTX2 face count {U32(data, 36)} is not supported");
            if (width > int.MaxValue || height > int.MaxValue) throw new ImageFormatException($"KTX2 size {width}x{height} is too large");
            uint levels = Math.Max(1u, U32(data, 40));
            if (levels > BitOperations.Log2(Math.Max(width, height)) + 1) throw new ImageFormatException($"KTX2 has {levels} levels, more than {width}x{height} allows");
            uint s = U32(data, 44);
            if (s != SchemeNone && s != SchemeZlib) throw new NotSupportedException($"KTX2 supercompression scheme {s} is not supported");
            if (IndexEnd + (long)levels * LevelEntry > data.Length) throw new ImageFormatException("KTX2 level index is truncated");

            int bpp = ImageCodec.BytesPerPixel(layout);
            for (int p = 0; p < levels; p++)
            {
                ReadLevel(data, p, out ulong offset, out ulong length, out ulong uncompressed);
                ulong expected = (ulong)LevelSize((int)width, p) * (ulong)LevelSize((int)height, p) * (ulong)bpp;
                if (uncompressed != expected) throw new ImageFormatException($"KTX2 level {p} is {uncompressed} bytes, not {expected}");
                if (s == SchemeNone && length != expected) throw new ImageFormatException($"KTX2 level {p} stores {length} bytes, not {expected}");
                if (offset > (ulong)data.Length || length > (ulong)data.Length - offset) throw new ImageFormatException($"KTX2 level {p} lies outside the file");
            }
            scheme = (int)s;
            return new Ktx2Info(layout, srgb, (int)width, (int)height, (int)levels);
        }

        // Decodes one level, already validated by ReadInfo, into rows of the given pitch and layout.
        internal static void DecodeLevel(ReadOnlySpan<byte> data, in Ktx2Info info, int scheme, int level, Span<byte> destination, int pitch, PixelLayout layout)
        {
            ReadLevel(data, level, out ulong offset, out ulong length, out _);
            ReadOnlySpan<byte> stored = data.Slice((int)offset, (int)length);
            int width = LevelSize(info.Width, level), height = LevelSize(info.Height, level);
            int rowBytes = width * ImageCodec.BytesPerPixel(info.Layout), size = rowBytes * height;
            if (info.Layout == layout && pitch == rowBytes)
            {
                if (scheme == SchemeNone) stored.CopyTo(destination);
                else Inflater.InflateZlib(stored, destination.Slice(0, size));
                return;
            }

            int inflated = scheme == SchemeNone ? 0 : size;
            byte[] scratch = ArrayPool<byte>.Shared.Rent(inflated + width * 4);
            try
            {
                ReadOnlySpan<byte> pixels = stored;
                if (scheme != SchemeNone)
                {
                    Inflater.InflateZlib(stored, scratch.AsSpan(0, size));
                    pixels = scratch.AsSpan(0, size);
                }
                Span<byte> rgba = scratch.AsSpan(inflated, width * 4);
                int outBytes = width * ImageCodec.BytesPerPixel(layout);
                for (int y = 0; y < height; y++)
                {
                    ReadOnlySpan<byte> row = pixels.Slice(y * rowBytes, rowBytes);
                    Span<byte> target = destination.Slice(y * pitch, outBytes);
                    if (info.Layout == layout)
                    {
                        row.CopyTo(target);
                        continue;
                    }
                    ToRgba(row, info.Layout, rgba, width);
                    PixelRows.FromRgba(rgba, target, width, layout);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }

        // Writes levels, already checked by the caller, smallest first after the header, DFD and KTXwriter.
        internal static void Encode(ReadOnlySpan<Image> levels, bool srgb, bool compress, Stream output, in DeflateSettings deflate)
        {
            Image top = levels[0];
            int count = levels.Length, bpp = ImageCodec.BytesPerPixel(top.Layout);
            int samples = top.Layout == PixelLayout.R8 ? 1 : top.Layout == PixelLayout.RG8 ? 2 : 4;
            int dfdOffset = IndexEnd + count * LevelEntry, dfdLength = 28 + 16 * samples;
            int kvdOffset = dfdOffset + dfdLength, kvdLength = (4 + Writer.Length + 3) & ~3;
            int dataStart = kvdOffset + kvdLength, align = compress ? 1 : 4;

            Span<long> offsets = stackalloc long[32];
            Span<long> lengths = stackalloc long[32];
            MemoryStream payload = new MemoryStream();
            for (int p = count - 1; p >= 0; p--)
            {
                while (payload.Length % align != 0) payload.WriteByte(0);
                offsets[p] = dataStart + payload.Length;
                Image level = levels[p];
                ReadOnlySpan<byte> pixels = level.Pixels.AsSpan(0, level.Width * level.Height * bpp);
                if (compress) Deflater.Deflate(pixels, payload, deflate);
                else payload.Write(pixels);
                lengths[p] = dataStart + payload.Length - offsets[p];
            }

            byte[] head = ArrayPool<byte>.Shared.Rent(dataStart);
            try
            {
                Span<byte> h = head.AsSpan(0, dataStart);
                h.Clear();
                Identifier.CopyTo(h);
                Put(h, 12, VkFormat(top.Layout, srgb));
                Put(h, 16, 1);
                Put(h, 20, (uint)top.Width);
                Put(h, 24, (uint)top.Height);
                Put(h, 36, 1);
                Put(h, 40, (uint)count);
                Put(h, 44, compress ? (uint)SchemeZlib : SchemeNone);
                Put(h, 48, (uint)dfdOffset);
                Put(h, 52, (uint)dfdLength);
                Put(h, 56, (uint)kvdOffset);
                Put(h, 60, (uint)kvdLength);
                for (int p = 0; p < count; p++)
                {
                    Span<byte> entry = h.Slice(IndexEnd + p * LevelEntry);
                    BinaryPrimitives.WriteUInt64LittleEndian(entry, (ulong)offsets[p]);
                    BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(8), (ulong)lengths[p]);
                    BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(16), (ulong)(levels[p].Width * levels[p].Height * bpp));
                }
                WriteDfd(h.Slice(dfdOffset, dfdLength), top.Layout, srgb, bpp, samples);
                Put(h, kvdOffset, (uint)Writer.Length);
                Writer.CopyTo(h.Slice(kvdOffset + 4));
                output.Write(h);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(head);
            }
            output.Write(payload.GetBuffer(), 0, (int)payload.Length);
        }

        // One basic descriptor block: RGBSDA, BT.709 primaries, straight alpha, one 8-bit sample per channel.
        private static void WriteDfd(Span<byte> dfd, PixelLayout layout, bool srgb, int bpp, int samples)
        {
            Put(dfd, 0, (uint)dfd.Length);
            Put(dfd, 8, 2u | (uint)(24 + 16 * samples) << 16);
            Put(dfd, 12, 1u | 1u << 8 | (srgb ? 2u : 1u) << 16);
            Put(dfd, 20, (uint)bpp);
            for (int k = 0; k < samples; k++)
            {
                uint channel = layout == PixelLayout.BGRA8 ? (k == 0 ? 2u : k == 2 ? 0u : (uint)k) : (uint)k;
                if (k == 3) channel = srgb ? 15u | 0x10 : 15u;
                Span<byte> sample = dfd.Slice(28 + 16 * k);
                Put(sample, 0, (uint)(8 * k) | 7u << 16 | channel << 24);
                Put(sample, 12, 255);
            }
        }

        private static void ToRgba(ReadOnlySpan<byte> row, PixelLayout layout, Span<byte> rgba, int width)
        {
            switch (layout)
            {
                case PixelLayout.RGBA8:
                    row.CopyTo(rgba);
                    break;
                case PixelLayout.BGRA8:
                    ImageOps.CopySwizzled(row, rgba);
                    break;
                case PixelLayout.R8:
                    for (int x = 0; x < width; x++)
                    {
                        byte g = row[x];
                        rgba[x * 4] = g;
                        rgba[x * 4 + 1] = g;
                        rgba[x * 4 + 2] = g;
                        rgba[x * 4 + 3] = 255;
                    }
                    break;
                case PixelLayout.RG8:
                    for (int x = 0; x < width; x++)
                    {
                        byte g = row[x * 2];
                        rgba[x * 4] = g;
                        rgba[x * 4 + 1] = g;
                        rgba[x * 4 + 2] = g;
                        rgba[x * 4 + 3] = row[x * 2 + 1];
                    }
                    break;
            }
        }

        private static bool TryLayout(uint format, out PixelLayout layout, out bool srgb)
        {
            srgb = format == R8Srgb || format == RG8Srgb || format == RGBA8Srgb || format == BGRA8Srgb;
            switch (format)
            {
                case R8Unorm: case R8Srgb: layout = PixelLayout.R8; return true;
                case RG8Unorm: case RG8Srgb: layout = PixelLayout.RG8; return true;
                case RGBA8Unorm: case RGBA8Srgb: layout = PixelLayout.RGBA8; return true;
                case BGRA8Unorm: case BGRA8Srgb: layout = PixelLayout.BGRA8; return true;
                default: layout = default; return false;
            }
        }

        private static uint VkFormat(PixelLayout layout, bool srgb) => layout switch
        {
            PixelLayout.R8 => srgb ? R8Srgb : R8Unorm,
            PixelLayout.RG8 => srgb ? RG8Srgb : RG8Unorm,
            PixelLayout.BGRA8 => srgb ? BGRA8Srgb : BGRA8Unorm,
            _ => srgb ? RGBA8Srgb : RGBA8Unorm
        };

        private static void ReadLevel(ReadOnlySpan<byte> data, int level, out ulong offset, out ulong length, out ulong uncompressed)
        {
            ReadOnlySpan<byte> entry = data.Slice(IndexEnd + level * LevelEntry, LevelEntry);
            offset = BinaryPrimitives.ReadUInt64LittleEndian(entry);
            length = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(8));
            uncompressed = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(16));
        }

        internal static bool IsKtx2(ReadOnlySpan<byte> data) => data.Length >= 12 && data.Slice(0, 12).SequenceEqual(Identifier);

        internal static int LevelSize(int size, int level) => Math.Max(1, size >> level);

        private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at));

        private static void Put(Span<byte> data, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(at), value);
    }
}
