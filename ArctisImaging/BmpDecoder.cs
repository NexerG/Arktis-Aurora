using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace Arctis.Imaging
{
    // Windows bitmaps: core, info and V4/V5 headers; 1/4/8-bit palettes, 16/24/32-bit, plain or bit fields.
    internal static class BmpDecoder
    {
        internal const int FileHeaderSize = 14;
        private const uint Rgb = 0, BitFields = 3, AlphaBitFields = 6;

        private struct Info
        {
            public int headerSize, width, height, bitCount, paletteCount, masksAfterHeader;
            public bool topDown;
            public uint compression, rMask, gMask, bMask, aMask;
        }

        internal static bool TryReadFileHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (data.Length < FileHeaderSize + 12 || data[0] != (byte)'B' || data[1] != (byte)'M') return false;
            if (!TryReadDibHeader(data.Slice(FileHeaderSize), out ImageHeader dib)) return false;
            header = new ImageHeader(ImageFormat.Bmp, dib.Width, dib.Height, dib.Channels, dib.BitDepth, dib.HasAlpha);
            return true;
        }

        internal static bool TryReadDibHeader(ReadOnlySpan<byte> dib, out ImageHeader header)
        {
            header = default;
            if (!TryReadInfo(dib, out Info info)) return false;
            int channels = info.bitCount == 32 ? 4 : 3;
            int depth = info.bitCount == 16 ? 5 : 8;
            header = new ImageHeader(ImageFormat.Dib, info.width, info.height, channels, depth, info.bitCount == 32);
            return true;
        }

        // Decodes pixel rows; pixelOffset is from the start of the DIB header, or -1 to follow the palette.
        internal static void Decode(ReadOnlySpan<byte> dib, int pixelOffset, Span<byte> destination, int pitch, PixelLayout layout)
        {
            if (!TryReadInfo(dib, out Info info)) throw new ImageFormatException("bitmap header is invalid");
            if (info.compression != Rgb && info.compression != BitFields && info.compression != AlphaBitFields)
                throw new NotSupportedException($"bitmap compression {info.compression} is not supported");
            if (info.compression != Rgb && info.bitCount != 16 && info.bitCount != 32)
                throw new ImageFormatException("bitmap bit fields need 16 or 32 bits per pixel");

            int entrySize = info.headerSize == 12 ? 3 : 4;
            int paletteStart = info.headerSize + info.masksAfterHeader;
            if (pixelOffset < 0) pixelOffset = paletteStart + info.paletteCount * entrySize;
            long stride = ((long)info.width * info.bitCount + 31) / 32 * 4;
            long rowBytes = ((long)info.width * info.bitCount + 7) / 8;
            if (pixelOffset < paletteStart || pixelOffset + stride * (info.height - 1) + rowBytes > dib.Length)
                throw new ImageFormatException("bitmap pixel data runs past the end");
            if (paletteStart + (long)info.paletteCount * entrySize > dib.Length) throw new ImageFormatException("bitmap palette runs past the end");

            uint[] palette = new uint[256];
            for (int i = 0; i < 256; i++) palette[i] = 0xFF000000u;
            for (int i = 0; i < info.paletteCount && i < 256; i++)
            {
                ReadOnlySpan<byte> e = dib.Slice(paletteStart + i * entrySize);
                palette[i] = e[2] | (uint)e[1] << 8 | (uint)e[0] << 16 | 0xFF000000u;
            }

            Field r = new Field(info.rMask), g = new Field(info.gMask), b = new Field(info.bMask), a = new Field(info.aMask);
            byte[] rgba = ArrayPool<byte>.Shared.Rent(info.width * 4);
            bool anyAlpha = false;
            try
            {
                for (int row = 0; row < info.height; row++)
                {
                    ReadOnlySpan<byte> src = dib.Slice(pixelOffset + (int)(row * stride));
                    int y = info.topDown ? row : info.height - 1 - row;
                    switch (info.bitCount)
                    {
                        case 1:
                        case 4:
                        case 8:
                            for (int x = 0; x < info.width; x++)
                            {
                                int bit = x * info.bitCount;
                                int index = (src[bit >> 3] >> (8 - info.bitCount - (bit & 7))) & ((1 << info.bitCount) - 1);
                                BinaryPrimitives.WriteUInt32LittleEndian(rgba.AsSpan(x * 4), palette[index]);
                            }
                            break;
                        case 24:
                            for (int x = 0; x < info.width; x++)
                            {
                                rgba[x * 4] = src[x * 3 + 2];
                                rgba[x * 4 + 1] = src[x * 3 + 1];
                                rgba[x * 4 + 2] = src[x * 3];
                                rgba[x * 4 + 3] = 255;
                            }
                            break;
                        default:
                            int bytes = info.bitCount / 8;
                            for (int x = 0; x < info.width; x++)
                            {
                                uint v = bytes == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(x * 4)) : BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(x * 2));
                                rgba[x * 4] = r.Extract(v);
                                rgba[x * 4 + 1] = g.Extract(v);
                                rgba[x * 4 + 2] = b.Extract(v);
                                byte alpha = a.mask == 0 ? (byte)255 : a.Extract(v);
                                anyAlpha |= alpha != 0;
                                rgba[x * 4 + 3] = alpha;
                            }
                            break;
                    }
                    PixelRows.FromRgba(rgba, destination.Slice(y * pitch), info.width, layout);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rgba);
            }

            // a 32-bit bitmap whose alpha is all zero has no alpha, not a transparent picture
            if (info.bitCount == 32 && !anyAlpha)
                for (int y = 0; y < info.height; y++) PixelRows.MakeOpaque(destination.Slice(y * pitch), info.width, layout);
        }

        private static bool TryReadInfo(ReadOnlySpan<byte> dib, out Info info)
        {
            info = default;
            if (dib.Length < 12) return false;
            int headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
            info.headerSize = headerSize;
            if (headerSize == 12)
            {
                info.width = BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(4));
                info.height = BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(6));
                info.bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(10));
            }
            else if (headerSize >= 40 && headerSize <= dib.Length)
            {
                info.width = BinaryPrimitives.ReadInt32LittleEndian(dib.Slice(4));
                int height = BinaryPrimitives.ReadInt32LittleEndian(dib.Slice(8));
                if (height == int.MinValue) return false;
                info.topDown = height < 0;
                info.height = Math.Abs(height);
                info.bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(14));
                info.compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(16));
                uint used = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(32));
                if (info.bitCount <= 8) info.paletteCount = used != 0 ? (int)Math.Min(used, 256u) : 1 << info.bitCount;

                ReadOnlySpan<byte> masks = default;
                if (headerSize >= 52) masks = dib.Slice(40, headerSize >= 56 ? 16 : 12);
                else if (info.compression == BitFields || info.compression == AlphaBitFields)
                {
                    info.masksAfterHeader = info.compression == AlphaBitFields ? 16 : 12;
                    if (dib.Length < 40 + info.masksAfterHeader) return false;
                    masks = dib.Slice(40, info.masksAfterHeader);
                }
                if (info.compression != Rgb && masks.Length >= 12)
                {
                    info.rMask = BinaryPrimitives.ReadUInt32LittleEndian(masks);
                    info.gMask = BinaryPrimitives.ReadUInt32LittleEndian(masks.Slice(4));
                    info.bMask = BinaryPrimitives.ReadUInt32LittleEndian(masks.Slice(8));
                    if (masks.Length >= 16) info.aMask = BinaryPrimitives.ReadUInt32LittleEndian(masks.Slice(12));
                }
            }
            else return false;

            if (info.bitCount != 1 && info.bitCount != 4 && info.bitCount != 8 && info.bitCount != 16 && info.bitCount != 24 && info.bitCount != 32) return false;
            if (info.compression == Rgb && info.bitCount == 16)
            {
                info.rMask = 0x7C00;
                info.gMask = 0x03E0;
                info.bMask = 0x001F;
            }
            else if (info.compression == Rgb && info.bitCount == 32)
            {
                info.rMask = 0x00FF0000;
                info.gMask = 0x0000FF00;
                info.bMask = 0x000000FF;
                info.aMask = 0xFF000000;
            }
            return true;
        }

        // One colour channel of a bit-field pixel, widened to 8 bits by bit replication.
        private readonly struct Field
        {
            public readonly uint mask;
            private readonly int shift;
            private readonly int bits;

            public Field(uint mask)
            {
                this.mask = mask;
                shift = mask == 0 ? 0 : BitOperations.TrailingZeroCount(mask);
                bits = BitOperations.PopCount(mask);
            }

            public byte Extract(uint v)
            {
                if (mask == 0) return 0;
                uint c = (v & mask) >> shift;
                if (bits >= 8) return (byte)(c >> (bits - 8));
                uint wide = c << (8 - bits);
                for (int filled = bits; filled < 8; filled *= 2) wide |= wide >> filled;
                return (byte)wide;
            }
        }
    }
}
