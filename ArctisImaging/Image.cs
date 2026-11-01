using System;
using System.IO;

namespace Arctis.Imaging
{
    /// <summary>A decoded image in managed memory: tightly packed rows of one layout.</summary>
    public sealed class Image
    {
        /// <summary>Creates a cleared image.</summary>
        public Image(int width, int height, PixelLayout layout = PixelLayout.RGBA8)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException($"invalid image size {width}x{height}");
            Width = width;
            Height = height;
            Layout = layout;
            Pixels = new byte[checked(width * height * ImageCodec.BytesPerPixel(layout))];
        }

        /// <summary>Width in pixels.</summary>
        public int Width { get; }
        /// <summary>Height in pixels.</summary>
        public int Height { get; }
        /// <summary>Memory order of each pixel.</summary>
        public PixelLayout Layout { get; }
        /// <summary>The pixel rows, top to bottom, RowPitch bytes each.</summary>
        public byte[] Pixels { get; }
        /// <summary>Bytes per row.</summary>
        public int RowPitch => Width * ImageCodec.BytesPerPixel(Layout);

        /// <summary>One row of pixels.</summary>
        public Span<byte> Row(int y) => Pixels.AsSpan(y * RowPitch, RowPitch);

        /// <summary>Decodes an image from its file bytes.</summary>
        public static Image Load(ReadOnlySpan<byte> data, PixelLayout layout = PixelLayout.RGBA8, ImageLimits limits = default)
        {
            if (!ImageCodec.TryIdentify(data, out ImageHeader header)) throw new ImageFormatException("unrecognised image format");
            DecodeOptions options = new DecodeOptions(layout, 0, limits);
            options.Limits.Check(header.Width, header.Height);
            Image image = new Image(header.Width, header.Height, layout);
            ImageCodec.Decode(data, image.Pixels, options);
            return image;
        }

        /// <summary>Decodes an image file.</summary>
        public static Image Load(string path, PixelLayout layout = PixelLayout.RGBA8, ImageLimits limits = default) => Load(File.ReadAllBytes(path), layout, limits);

        /// <summary>Decodes an image from the rest of a stream.</summary>
        public static Image Load(Stream stream, PixelLayout layout = PixelLayout.RGBA8, ImageLimits limits = default)
        {
            if (stream is MemoryStream memory && memory.TryGetBuffer(out ArraySegment<byte> buffer))
                return Load(buffer.AsSpan((int)memory.Position), layout, limits);
            using MemoryStream copy = new MemoryStream();
            stream.CopyTo(copy);
            return Load(copy.GetBuffer().AsSpan(0, (int)copy.Length), layout, limits);
        }

        /// <summary>Decodes a device-independent bitmap that has no file header, as the clipboard holds it.</summary>
        public static Image LoadDib(ReadOnlySpan<byte> dib, PixelLayout layout = PixelLayout.RGBA8, ImageLimits limits = default)
        {
            if (!ImageCodec.TryIdentifyDib(dib, out ImageHeader header)) throw new ImageFormatException("unrecognised bitmap header");
            DecodeOptions options = new DecodeOptions(layout, 0, limits);
            options.Limits.Check(header.Width, header.Height);
            Image image = new Image(header.Width, header.Height, layout);
            ImageCodec.DecodeDib(dib, image.Pixels, options);
            return image;
        }

        /// <summary>Copies pixels into a new image.</summary>
        public static Image FromPixels(ReadOnlySpan<byte> pixels, int width, int height, int rowPitch, PixelLayout layout)
        {
            Image image = new Image(width, height, layout);
            ImageOps.Crop(pixels, rowPitch == 0 ? image.RowPitch : rowPitch, layout, 0, 0, width, height, image.Pixels, image.RowPitch);
            return image;
        }

        /// <summary>A new image holding a rectangle of this one.</summary>
        public Image Crop(int x, int y, int width, int height)
        {
            if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > Width || y + height > Height)
                throw new ArgumentOutOfRangeException(nameof(x), $"rectangle {x},{y} {width}x{height} is outside {Width}x{Height}");
            Image image = new Image(width, height, Layout);
            ImageOps.Crop(Pixels, RowPitch, Layout, x, y, width, height, image.Pixels, image.RowPitch);
            return image;
        }

        /// <summary>Writes the image as a PNG.</summary>
        public void SavePng(Stream output) => ImageCodec.EncodePng(Pixels, Width, Height, RowPitch, Layout, output);

        /// <summary>Writes the image as a PNG file.</summary>
        public void SavePng(string path)
        {
            using FileStream file = File.Create(path);
            SavePng(file);
        }
    }
}
