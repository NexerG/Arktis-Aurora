using System;
using System.Buffers.Binary;
using System.IO;

namespace Arctis.Imaging
{
    /// <summary>Reads headers, decodes into caller-owned memory and encodes PNG.</summary>
    public static class ImageCodec
    {
        /// <summary>Bytes one pixel takes in a layout.</summary>
        public static int BytesPerPixel(PixelLayout layout) => layout switch
        {
            PixelLayout.R8 => 1,
            PixelLayout.RG8 => 2,
            _ => 4
        };

        /// <summary>The destination row pitch the options give for a width.</summary>
        public static int RowPitchOf(int width, in DecodeOptions options) => options.RowPitch > 0 ? options.RowPitch : width * BytesPerPixel(options.Layout);

        /// <summary>Destination bytes a decode of this header needs: every row at the pitch, the last one tight.</summary>
        /// <exception cref="ImageFormatException">The header exceeds the options' limits.</exception>
        public static int RequiredBytes(in ImageHeader header, in DecodeOptions options)
        {
            options.Limits.Check(header.Width, header.Height);
            int tight = header.Width * BytesPerPixel(options.Layout);
            int pitch = RowPitchOf(header.Width, options);
            if (pitch < tight) throw new ArgumentException($"row pitch {pitch} is smaller than a row ({tight} bytes)");
            return checked(pitch * (header.Height - 1) + tight);
        }

        /// <summary>Recognises a file by its leading bytes and reads its header.</summary>
        public static bool TryIdentify(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            if (PngDecoder.TryReadHeader(data, out header)) return true;
            if (BmpDecoder.TryReadFileHeader(data, out header)) return true;
            if (TryReadJpegHeader(data, out header)) return true;
            if (TryReadGifHeader(data, out header)) return true;
            header = default;
            return false;
        }

        /// <summary>Reads the header of a device-independent bitmap that has no file header.</summary>
        public static bool TryIdentifyDib(ReadOnlySpan<byte> dib, out ImageHeader header) => BmpDecoder.TryReadDibHeader(dib, out header);

        /// <summary>Decodes an image into the destination in the options' layout and pitch.</summary>
        /// <exception cref="ImageFormatException">The file is malformed or exceeds the limits.</exception>
        /// <exception cref="NotSupportedException">The format is recognised but has no decoder.</exception>
        public static ImageHeader Decode(ReadOnlySpan<byte> source, Span<byte> destination, in DecodeOptions options)
        {
            if (!TryIdentify(source, out ImageHeader header)) throw new ImageFormatException("unrecognised image format");
            Prepare(header, destination, options);
            switch (header.Format)
            {
                case ImageFormat.Png: PngDecoder.Decode(source, destination, RowPitchOf(header.Width, options), options.Layout); break;
                case ImageFormat.Bmp: BmpDecoder.Decode(source.Slice(BmpDecoder.FileHeaderSize), BinaryPrimitives.ReadInt32LittleEndian(source.Slice(10)) - BmpDecoder.FileHeaderSize, destination, RowPitchOf(header.Width, options), options.Layout); break;
                default: throw new NotSupportedException($"{header.Format} decoding is not available");
            }
            return header;
        }

        /// <summary>Decodes a device-independent bitmap that has no file header.</summary>
        /// <exception cref="ImageFormatException">The bitmap is malformed or exceeds the limits.</exception>
        /// <exception cref="NotSupportedException">The bitmap uses a compression with no decoder.</exception>
        public static ImageHeader DecodeDib(ReadOnlySpan<byte> dib, Span<byte> destination, in DecodeOptions options)
        {
            if (!BmpDecoder.TryReadDibHeader(dib, out ImageHeader header)) throw new ImageFormatException("unrecognised bitmap header");
            Prepare(header, destination, options);
            BmpDecoder.Decode(dib, -1, destination, RowPitchOf(header.Width, options), options.Layout);
            return header;
        }

        /// <summary>Writes pixels as a PNG: R8 as grey, RG8 as grey with alpha, RGBA8 and BGRA8 as RGBA.</summary>
        public static void EncodePng(ReadOnlySpan<byte> pixels, int width, int height, int rowPitch, PixelLayout layout, Stream output)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException($"invalid image size {width}x{height}");
            int tight = width * BytesPerPixel(layout);
            if (rowPitch == 0) rowPitch = tight;
            if (rowPitch < tight || pixels.Length < (long)rowPitch * (height - 1) + tight) throw new ArgumentException("pixel span is smaller than the image");
            PngEncoder.Encode(pixels, width, height, rowPitch, layout, output);
        }

        private static void Prepare(in ImageHeader header, Span<byte> destination, in DecodeOptions options)
        {
            int needed = RequiredBytes(header, options);
            if (destination.Length < needed) throw new ArgumentException($"destination holds {destination.Length} bytes; the image needs {needed}");
        }

        #region header probes
        private static bool TryReadJpegHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return false;
            int i = 2;
            while (i + 4 <= data.Length)
            {
                if (data[i] != 0xFF) return false;
                byte marker = data[i + 1];
                if (marker == 0xFF)
                {
                    i++;
                    continue;
                }
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    i += 2;
                    continue;
                }
                if (marker == 0xD9 || marker == 0xDA) return false;
                int length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 2));
                if (length < 2) return false;
                bool isFrame = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (isFrame)
                {
                    if (i + 10 > data.Length) return false;
                    int precision = data[i + 4];
                    int height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 5));
                    int width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 7));
                    int components = data[i + 9];
                    header = new ImageHeader(ImageFormat.Jpeg, width, height, components, precision, false);
                    return true;
                }
                i += 2 + length;
            }
            return false;
        }

        private static bool TryReadGifHeader(ReadOnlySpan<byte> data, out ImageHeader header)
        {
            header = default;
            if (data.Length < 10 || data[0] != (byte)'G' || data[1] != (byte)'I' || data[2] != (byte)'F' || data[3] != (byte)'8'
                || (data[4] != (byte)'7' && data[4] != (byte)'9') || data[5] != (byte)'a')
                return false;
            int width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6));
            int height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8));
            header = new ImageHeader(ImageFormat.Gif, width, height, 4, 8, true);
            return true;
        }
        #endregion
    }
}
