using System;

namespace Arctis.Imaging
{
    /// <summary>Memory order of one decoded pixel. R8 and RG8 hold luminance, RG8 with alpha.</summary>
    public enum PixelLayout : byte
    {
        /// <summary>Four bytes: red, green, blue, alpha.</summary>
        RGBA8,
        /// <summary>Four bytes: blue, green, red, alpha.</summary>
        BGRA8,
        /// <summary>One byte of luminance.</summary>
        R8,
        /// <summary>Two bytes: luminance, alpha.</summary>
        RG8
    }

    /// <summary>The file format an image was read from.</summary>
    public enum ImageFormat : byte
    {
        /// <summary>Not recognised.</summary>
        Unknown,
        /// <summary>Portable Network Graphics.</summary>
        Png,
        /// <summary>Windows bitmap file, with its file header.</summary>
        Bmp,
        /// <summary>Windows device-independent bitmap without a file header, as the clipboard holds it.</summary>
        Dib,
        /// <summary>JPEG (JFIF / EXIF).</summary>
        Jpeg,
        /// <summary>Graphics Interchange Format.</summary>
        Gif
    }

    /// <summary>What a file says about its image, read without decoding pixels.</summary>
    public readonly struct ImageHeader
    {
        /// <summary>Creates a header.</summary>
        public ImageHeader(ImageFormat format, int width, int height, int channels, int bitDepth, bool hasAlpha)
        {
            Format = format;
            Width = width;
            Height = height;
            Channels = channels;
            BitDepth = bitDepth;
            HasAlpha = hasAlpha;
        }

        /// <summary>The file format.</summary>
        public ImageFormat Format { get; }
        /// <summary>Width in pixels.</summary>
        public int Width { get; }
        /// <summary>Height in pixels.</summary>
        public int Height { get; }
        /// <summary>Channels as stored; a palette counts as 3, or 4 with transparency.</summary>
        public int Channels { get; }
        /// <summary>Bits per channel as stored.</summary>
        public int BitDepth { get; }
        /// <summary>Whether the file can carry transparency.</summary>
        public bool HasAlpha { get; }
        /// <summary>Channels times bit depth.</summary>
        public int BitsPerPixel => Channels * BitDepth;
    }

    /// <summary>Caps checked against a header before anything is allocated. Zero fields take the defaults.</summary>
    public readonly struct ImageLimits
    {
        /// <summary>Default largest width or height.</summary>
        public const int DefaultMaxDimension = 1 << 16;
        /// <summary>Default largest pixel count.</summary>
        public const long DefaultMaxPixels = 1L << 28;

        private readonly int maxWidth;
        private readonly int maxHeight;
        private readonly long maxPixels;

        /// <summary>Creates limits; a zero or negative value takes the default.</summary>
        public ImageLimits(int maxWidth, int maxHeight, long maxPixels)
        {
            this.maxWidth = maxWidth;
            this.maxHeight = maxHeight;
            this.maxPixels = maxPixels;
        }

        /// <summary>Largest accepted width.</summary>
        public int MaxWidth => maxWidth > 0 ? maxWidth : DefaultMaxDimension;
        /// <summary>Largest accepted height.</summary>
        public int MaxHeight => maxHeight > 0 ? maxHeight : DefaultMaxDimension;
        /// <summary>Largest accepted width times height.</summary>
        public long MaxPixels => maxPixels > 0 ? maxPixels : DefaultMaxPixels;

        internal void Check(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ImageFormatException($"invalid image size {width}x{height}");
            if (width > MaxWidth || height > MaxHeight || (long)width * height > MaxPixels)
                throw new ImageFormatException($"image size {width}x{height} exceeds the limits {MaxWidth}x{MaxHeight}, {MaxPixels} pixels");
        }
    }

    /// <summary>How to decode: the layout to write, the destination row pitch and the size limits.</summary>
    public readonly struct DecodeOptions
    {
        /// <summary>Creates options. A row pitch of 0 means tightly packed rows.</summary>
        public DecodeOptions(PixelLayout layout, int rowPitch = 0, ImageLimits limits = default)
        {
            Layout = layout;
            RowPitch = rowPitch;
            Limits = limits;
        }

        /// <summary>The layout written to the destination.</summary>
        public PixelLayout Layout { get; }
        /// <summary>Bytes from one destination row to the next; 0 is width times bytes per pixel.</summary>
        public int RowPitch { get; }
        /// <summary>Caps checked before decoding.</summary>
        public ImageLimits Limits { get; }
    }

    /// <summary>Thrown when a file is malformed or exceeds its limits.</summary>
    public sealed class ImageFormatException : Exception
    {
        /// <summary>Creates the exception.</summary>
        public ImageFormatException(string message) : base(message) { }

        /// <summary>Creates the exception around the error that caused it.</summary>
        public ImageFormatException(string message, Exception inner) : base(message, inner) { }
    }
}
