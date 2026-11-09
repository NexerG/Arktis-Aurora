using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;

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

        /// <summary>Recognises a file by its leading bytes and reads its header; a format with no signature is tried
        /// only when it is the hint, such as one taken from the file's extension.</summary>
        public static bool TryIdentify(ReadOnlySpan<byte> data, out ImageHeader header, ImageFormat hint = ImageFormat.Unknown)
        {
            if (PngDecoder.TryReadHeader(data, out header)) return true;
            if (BmpDecoder.TryReadFileHeader(data, out header)) return true;
            if (Ktx2.TryReadHeader(data, out header)) return true;
            if (DistanceField.TryReadHeader(data, out header)) return true;
            if (JpegDecoder.TryReadHeader(data, out header)) return true;
            if (GifDecoder.TryReadHeader(data, out header)) return true;
            if (TiffDecoder.TryReadHeader(data, out header)) return true;
            if (WebpDecoder.TryReadHeader(data, out header)) return true;
            if (QoiDecoder.TryReadHeader(data, out header)) return true;
            if (FarbfeldDecoder.TryReadHeader(data, out header)) return true;
            if (XpmDecoder.TryReadHeader(data, out header)) return true;
            if (IcoDecoder.TryReadHeader(data, out header)) return true;
            if (PnmDecoder.TryReadHeader(data, out header)) return true;
            if (hint == ImageFormat.Tga && TgaDecoder.TryReadHeader(data, out header)) return true;
            if (hint == ImageFormat.Pcx && PcxDecoder.TryReadHeader(data, out header)) return true;
            if (hint == ImageFormat.Xbm && XbmDecoder.TryReadHeader(data, out header)) return true;
            header = default;
            return false;
        }

        /// <summary>Reads the header of a device-independent bitmap that has no file header.</summary>
        public static bool TryIdentifyDib(ReadOnlySpan<byte> dib, out ImageHeader header) => BmpDecoder.TryReadDibHeader(dib, out header);

        /// <summary>Decodes an image into the destination in the options' layout and pitch.</summary>
        /// <exception cref="ImageFormatException">The file is malformed or exceeds the limits.</exception>
        /// <exception cref="NotSupportedException">The format is recognised but has no decoder.</exception>
        public static ImageHeader Decode(ReadOnlySpan<byte> source, Span<byte> destination, in DecodeOptions options, ImageFormat hint = ImageFormat.Unknown)
        {
            if (!TryIdentify(source, out ImageHeader header, hint)) throw new ImageFormatException("unrecognised image format");
            Prepare(header, destination, options);
            switch (header.Format)
            {
                case ImageFormat.Png: PngDecoder.Decode(source, destination, RowPitchOf(header.Width, options), options.Layout); break;
                case ImageFormat.Bmp: BmpDecoder.Decode(source.Slice(BmpDecoder.FileHeaderSize), BinaryPrimitives.ReadInt32LittleEndian(source.Slice(10)) - BmpDecoder.FileHeaderSize, destination, RowPitchOf(header.Width, options), options.Layout); break;
                case ImageFormat.Ktx2:
                    Ktx2Info info = Ktx2.ReadInfo(source, out int scheme);
                    Ktx2.DecodeLevel(source, info, scheme, 0, destination, RowPitchOf(header.Width, options), options.Layout);
                    break;
                case ImageFormat.Adf: DistanceField.Decode(source, destination, RowPitchOf(header.Width, options), options.Layout); break;
                case ImageFormat.Tga: TgaDecoder.Decode(source, destination, RowPitchOf(header.Width, options), options.Layout); break;
                case ImageFormat.Tiff: TiffDecoder.Decode(source, destination, RowPitchOf(header.Width, options), options.Layout); break;
                case ImageFormat.Webp: WebpDecoder.Decode(source, destination, RowPitchOf(header.Width, options), options.Layout); break;
                case ImageFormat.Jpeg: JpegDecoder.Decode(source, destination, RowPitchOf(header.Width, options), options.Layout); break;
                case ImageFormat.Gif: GifDecoder.Decode(source, destination, RowPitchOf(header.Width, options), options.Layout); break;
                case ImageFormat.JpegLs: JpegLsDecoder.Decode(source, destination, RowPitchOf(header.Width, options), options.Layout); break;
                case ImageFormat.Ico: IcoDecoder.Decode(source, destination, RowPitchOf(header.Width, options), options.Layout); break;
                case ImageFormat.Pnm: PnmDecoder.Decode(source, destination, RowPitchOf(header.Width, options), options.Layout); break;
                case ImageFormat.Qoi: QoiDecoder.Decode(source, destination, RowPitchOf(header.Width, options), options.Layout); break;
                case ImageFormat.Farbfeld: FarbfeldDecoder.Decode(source, destination, RowPitchOf(header.Width, options), options.Layout); break;
                case ImageFormat.Pcx: PcxDecoder.Decode(source, destination, RowPitchOf(header.Width, options), options.Layout); break;
                case ImageFormat.Xbm: XbmDecoder.Decode(source, destination, RowPitchOf(header.Width, options), options.Layout); break;
                case ImageFormat.Xpm: XpmDecoder.Decode(source, destination, RowPitchOf(header.Width, options), options.Layout); break;
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
        public static void EncodePng(ReadOnlySpan<byte> pixels, int width, int height, int rowPitch, PixelLayout layout, Stream output, in DeflateSettings deflate = default)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException($"invalid image size {width}x{height}");
            int tight = width * BytesPerPixel(layout);
            if (rowPitch == 0) rowPitch = tight;
            if (rowPitch < tight || pixels.Length < (long)rowPitch * (height - 1) + tight) throw new ArgumentException("pixel span is smaller than the image");
            deflate.Check();
            PngEncoder.Encode(pixels, width, height, rowPitch, layout, output, deflate);
        }

        /// <summary>Writes pixels as a baseline JPEG: R8 and RG8 as grey, RGBA8 and BGRA8 as YCbCr; alpha is dropped.</summary>
        public static void EncodeJpeg(ReadOnlySpan<byte> pixels, int width, int height, int rowPitch, PixelLayout layout, Stream output, in JpegSettings settings = default)
        {
            if (width <= 0 || height <= 0 || width > 65535 || height > 65535) throw new ArgumentException($"invalid image size {width}x{height}");
            int tight = width * BytesPerPixel(layout);
            if (rowPitch == 0) rowPitch = tight;
            if (rowPitch < tight || pixels.Length < (long)rowPitch * (height - 1) + tight) throw new ArgumentException("pixel span is smaller than the image");
            settings.Check();
            JpegEncoder.Encode(pixels, width, height, rowPitch, layout, output, settings);
        }

        /// <summary>Writes pixels as a lossless JPEG: R8 and RG8 as grey, RGBA8 and BGRA8 as RGB; alpha is dropped.</summary>
        public static void EncodeLosslessJpeg(ReadOnlySpan<byte> pixels, int width, int height, int rowPitch, PixelLayout layout, Stream output, in JpegLosslessSettings settings = default)
        {
            if (width <= 0 || height <= 0 || width > 65535 || height > 65535) throw new ArgumentException($"invalid image size {width}x{height}");
            int tight = width * BytesPerPixel(layout);
            if (rowPitch == 0) rowPitch = tight;
            if (rowPitch < tight || pixels.Length < (long)rowPitch * (height - 1) + tight) throw new ArgumentException("pixel span is smaller than the image");
            settings.Check();
            JpegEncoder.EncodeLossless(pixels, width, height, rowPitch, layout, output, settings);
        }

        /// <summary>Writes an RGBA8 distance-field atlas as ADF: R, G and B within ±1, alpha rebuilt as their median on decode.</summary>
        /// <exception cref="ArgumentException">A pixel's alpha is not the median of its R, G and B.</exception>
        public static void EncodeAdf(ReadOnlySpan<byte> rgba, int width, int height, int rowPitch, Stream output)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException($"invalid image size {width}x{height}");
            if (rowPitch == 0) rowPitch = width * 4;
            if (rowPitch < width * 4 || rgba.Length < (long)rowPitch * (height - 1) + width * 4) throw new ArgumentException("pixel span is smaller than the image");
            DistanceField.Encode(rgba, width, height, rowPitch, output);
        }

        /// <summary>Reads what a KTX2 file holds; false when the data is not a KTX2 file.</summary>
        /// <exception cref="ImageFormatException">The file is malformed.</exception>
        /// <exception cref="NotSupportedException">The file uses a format, shape or supercompression with no decoder.</exception>
        public static bool TryReadKtx2(ReadOnlySpan<byte> data, out Ktx2Info info)
        {
            info = default;
            if (!Ktx2.IsKtx2(data)) return false;
            info = Ktx2.ReadInfo(data, out _);
            return true;
        }

        /// <summary>Decodes one mip level of a KTX2 file into the destination in the options' layout and pitch.</summary>
        /// <exception cref="ImageFormatException">The file is malformed or the level exceeds the limits.</exception>
        /// <exception cref="NotSupportedException">The file uses a format, shape or supercompression with no decoder.</exception>
        public static void DecodeKtx2Level(ReadOnlySpan<byte> source, int level, Span<byte> destination, in DecodeOptions options)
        {
            if (!Ktx2.IsKtx2(source)) throw new ImageFormatException("not a KTX2 file");
            Ktx2Info info = Ktx2.ReadInfo(source, out int scheme);
            if (level < 0 || level >= info.Levels) throw new ArgumentOutOfRangeException(nameof(level), $"level {level} of {info.Levels}");
            int width = Ktx2.LevelSize(info.Width, level), height = Ktx2.LevelSize(info.Height, level);
            Prepare(new ImageHeader(ImageFormat.Ktx2, width, height, BytesPerPixel(info.Layout), 8, info.Layout != PixelLayout.R8), destination, options);
            Ktx2.DecodeLevel(source, info, scheme, level, destination, RowPitchOf(width, options), options.Layout);
        }

        /// <summary>Writes mip levels as a KTX2 file: levels[0] full size, each next HalfSize of the one before, one layout.
        /// Compressed levels are ZLIB-supercompressed.</summary>
        public static void EncodeKtx2(ReadOnlySpan<Image> levels, bool srgb, bool compress, Stream output, in DeflateSettings deflate = default)
        {
            if (levels.Length == 0) throw new ArgumentException("no levels to write");
            Image top = levels[0];
            int most = BitOperations.Log2((uint)Math.Max(top.Width, top.Height)) + 1;
            if (levels.Length > most) throw new ArgumentException($"{levels.Length} levels is more than a {top.Width}x{top.Height} image has");
            for (int p = 1; p < levels.Length; p++)
            {
                Image level = levels[p], above = levels[p - 1];
                if (level.Layout != top.Layout) throw new ArgumentException($"level {p} is {level.Layout}, level 0 is {top.Layout}");
                if (level.Width != ImageOps.HalfSize(above.Width) || level.Height != ImageOps.HalfSize(above.Height))
                    throw new ArgumentException($"level {p} is {level.Width}x{level.Height}, not half of level {p - 1}");
            }
            deflate.Check();
            Ktx2.Encode(levels, srgb, compress, output, deflate);
        }

        /// <summary>Writes pixels as a BMP file in any header, pixel format and compression the settings name.</summary>
        /// <exception cref="ArgumentException">The settings contradict each other, or an indexed format has fewer palette
        /// entries than the image has colours.</exception>
        public static void EncodeBmp(ReadOnlySpan<byte> pixels, int width, int height, int rowPitch, PixelLayout layout, Stream output, in BmpSettings settings = default)
        {
            CheckPixels(pixels, width, height, ref rowPitch, layout);
            BmpSettings resolved = settings.Resolve(layout);
            resolved.Check();
            BmpEncoder.Encode(pixels, width, height, rowPitch, layout, output, resolved, true);
        }

        /// <summary>Writes pixels as a device-independent bitmap without a file header, as the clipboard holds it.</summary>
        /// <exception cref="ArgumentException">The settings contradict each other, or an indexed format has fewer palette
        /// entries than the image has colours.</exception>
        public static void EncodeDib(ReadOnlySpan<byte> pixels, int width, int height, int rowPitch, PixelLayout layout, Stream output, in BmpSettings settings = default)
        {
            CheckPixels(pixels, width, height, ref rowPitch, layout);
            BmpSettings resolved = settings.Resolve(layout);
            resolved.Check();
            BmpEncoder.Encode(pixels, width, height, rowPitch, layout, output, resolved, false);
        }

        /// <summary>Writes pixels as a TGA file in the pixel format, compression and origin the settings name.</summary>
        /// <exception cref="ArgumentException">An indexed format meets more than 256 colours.</exception>
        public static void EncodeTga(ReadOnlySpan<byte> pixels, int width, int height, int rowPitch, PixelLayout layout, Stream output, in TgaSettings settings = default)
        {
            CheckPixels(pixels, width, height, ref rowPitch, layout);
            if (width > 65535 || height > 65535) throw new ArgumentException($"a TGA holds at most 65535 pixels a side, not {width}x{height}");
            settings.Check();
            TgaEncoder.Encode(pixels, width, height, rowPitch, layout, output, settings);
        }

        /// <summary>Writes pixels as a Netpbm file: PBM, PGM, PPM or PAM, raw or plain, at any maxval.</summary>
        public static void EncodePnm(ReadOnlySpan<byte> pixels, int width, int height, int rowPitch, PixelLayout layout, Stream output, in PnmSettings settings = default)
        {
            CheckPixels(pixels, width, height, ref rowPitch, layout);
            settings.Check();
            PnmEncoder.Encode(pixels, width, height, rowPitch, layout, output, settings);
        }

        /// <summary>Writes pixels as a QOI file.</summary>
        public static void EncodeQoi(ReadOnlySpan<byte> pixels, int width, int height, int rowPitch, PixelLayout layout, Stream output, in QoiSettings settings = default)
        {
            CheckPixels(pixels, width, height, ref rowPitch, layout);
            settings.Check();
            QoiEncoder.Encode(pixels, width, height, rowPitch, layout, output, settings);
        }

        /// <summary>Writes entries as a Windows icon, in the order given; PNG entries take the deflate settings.</summary>
        public static void EncodeIco(ReadOnlySpan<IconEntry> entries, Stream output, in DeflateSettings deflate = default)
        {
            CheckEntries(entries, false);
            deflate.Check();
            IcoEncoder.Encode(entries, false, output, deflate);
        }

        /// <summary>Writes entries as a Windows cursor, each with its hotspot, in the order given.</summary>
        public static void EncodeCur(ReadOnlySpan<IconEntry> entries, Stream output, in DeflateSettings deflate = default)
        {
            CheckEntries(entries, true);
            deflate.Check();
            IcoEncoder.Encode(entries, true, output, deflate);
        }

        private static void CheckPixels(ReadOnlySpan<byte> pixels, int width, int height, ref int rowPitch, PixelLayout layout)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException($"invalid image size {width}x{height}");
            if (layout > PixelLayout.RG8) throw new ArgumentException($"unknown pixel layout {layout}");
            int tight = width * BytesPerPixel(layout);
            if (rowPitch == 0) rowPitch = tight;
            if (rowPitch < tight || pixels.Length < (long)rowPitch * (height - 1) + tight) throw new ArgumentException("pixel span is smaller than the image");
        }

        private static void CheckEntries(ReadOnlySpan<IconEntry> entries, bool cursor)
        {
            if (entries.Length == 0 || entries.Length > 65535) throw new ArgumentException($"an icon holds 1-65535 entries, not {entries.Length}");
            for (int i = 0; i < entries.Length; i++)
            {
                IconEntry e = entries[i];
                if (e.Image == null) throw new ArgumentException($"entry {i} has no image");
                if (e.Image.Width > 256 || e.Image.Height > 256) throw new ArgumentException($"entry {i} is {e.Image.Width}x{e.Image.Height}; an icon entry is at most 256x256");
                if (e.Encoding > IconEncoding.Bmp) throw new ArgumentException($"entry {i} has unknown encoding {e.Encoding}");
                BmpPixelFormat f = e.BmpFormat;
                if (f != BmpPixelFormat.Auto && (e.Encoding == IconEncoding.Png || f == BmpPixelFormat.Rgb565 || f == BmpPixelFormat.Argb1555 || f == BmpPixelFormat.Argb4444 || f > BmpPixelFormat.Argb32))
                    throw new ArgumentException($"entry {i}: {f} is not an icon BMP format; use Indexed1/4/8, Rgb555, Rgb24, Xrgb32 or Argb32 with BMP encoding");
                if (cursor ? e.HotspotX < 0 || e.HotspotY < 0 || e.HotspotX >= e.Image.Width || e.HotspotY >= e.Image.Height : e.HotspotX != 0 || e.HotspotY != 0)
                    throw new ArgumentException(cursor ? $"entry {i}'s hotspot {e.HotspotX},{e.HotspotY} is outside its {e.Image.Width}x{e.Image.Height} image" : $"entry {i} has a hotspot; only cursors take one");
            }
        }

        private static void Prepare(in ImageHeader header, Span<byte> destination, in DecodeOptions options)
        {
            int needed = RequiredBytes(header, options);
            if (destination.Length < needed) throw new ArgumentException($"destination holds {destination.Length} bytes; the image needs {needed}");
        }
    }
}
