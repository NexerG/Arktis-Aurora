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
        Gif,
        /// <summary>Khronos Texture 2.0.</summary>
        Ktx2
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

    /// <summary>DEFLATE match search effort, zlib's four tuning numbers. Zero fields take zlib level 6's values.</summary>
    public readonly struct DeflateSettings
    {
        /// <summary>zlib level 6's values.</summary>
        public const int DefaultGoodLength = 8, DefaultMaxLazy = 16, DefaultNiceLength = 128, DefaultMaxChain = 128;

        private readonly int goodLength;
        private readonly int maxLazy;
        private readonly int niceLength;
        private readonly int maxChain;

        /// <summary>Creates settings; a zero or negative value takes the default.</summary>
        public DeflateSettings(int goodLength, int maxLazy, int niceLength, int maxChain)
        {
            this.goodLength = goodLength;
            this.maxLazy = maxLazy;
            this.niceLength = niceLength;
            this.maxChain = maxChain;
        }

        /// <summary>A match at least this long searches the next position with a quarter of the chain (3-258).</summary>
        public int GoodLength => goodLength > 0 ? goodLength : DefaultGoodLength;
        /// <summary>A match at least this long is taken without looking one byte ahead for a longer one (3-258).</summary>
        public int MaxLazy => maxLazy > 0 ? maxLazy : DefaultMaxLazy;
        /// <summary>A match at least this long stops the search (3-258).</summary>
        public int NiceLength => niceLength > 0 ? niceLength : DefaultNiceLength;
        /// <summary>Earlier positions tried per search (at least 4).</summary>
        public int MaxChain => maxChain > 0 ? maxChain : DefaultMaxChain;

        internal void Check()
        {
            if (GoodLength < 3 || GoodLength > 258) throw new ArgumentException($"deflate good length {GoodLength} is outside 3-258");
            if (MaxLazy < 3 || MaxLazy > 258) throw new ArgumentException($"deflate max lazy {MaxLazy} is outside 3-258");
            if (NiceLength < 3 || NiceLength > 258) throw new ArgumentException($"deflate nice length {NiceLength} is outside 3-258");
            if (MaxChain < 4) throw new ArgumentException($"deflate max chain {MaxChain} is below 4");
        }
    }

    /// <summary>What a KTX2 file holds, read without decoding pixels.</summary>
    public readonly struct Ktx2Info
    {
        /// <summary>Creates the info.</summary>
        public Ktx2Info(PixelLayout layout, bool srgb, int width, int height, int levels)
        {
            Layout = layout;
            Srgb = srgb;
            Width = width;
            Height = height;
            Levels = levels;
        }

        /// <summary>The stored layout; R8 and RG8 read as grey and grey with alpha, as elsewhere.</summary>
        public PixelLayout Layout { get; }
        /// <summary>Whether the format is an _SRGB one rather than _UNORM.</summary>
        public bool Srgb { get; }
        /// <summary>Width of level 0 in pixels.</summary>
        public int Width { get; }
        /// <summary>Height of level 0 in pixels.</summary>
        public int Height { get; }
        /// <summary>Mip levels stored; level p is HalfSize applied p times to each side.</summary>
        public int Levels { get; }
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
