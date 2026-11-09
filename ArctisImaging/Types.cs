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
        Ktx2,
        /// <summary>Arctis distance-field atlas: RGBA8 whose alpha is the median of R, G and B.</summary>
        Adf,
        /// <summary>Truevision TGA; it has no signature, so it is recognised only when named as the hint.</summary>
        Tga,
        /// <summary>Tagged Image File Format.</summary>
        Tiff,
        /// <summary>WebP, lossless or lossy.</summary>
        Webp,
        /// <summary>Windows icon or cursor; the largest, deepest entry is decoded.</summary>
        Ico,
        /// <summary>Netpbm PBM, PGM, PPM or PAM.</summary>
        Pnm,
        /// <summary>Quite OK Image format.</summary>
        Qoi,
        /// <summary>farbfeld: 16-bit RGBA, big-endian.</summary>
        Farbfeld,
        /// <summary>ZSoft PCX; it has no reliable signature, so it is recognised only when named as the hint.</summary>
        Pcx,
        /// <summary>X BitMap C source; recognised only when named as the hint.</summary>
        Xbm,
        /// <summary>X PixMap version 3 C source.</summary>
        Xpm,
        /// <summary>JPEG-LS (ITU-T T.87), lossless or near-lossless.</summary>
        JpegLs
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

    /// <summary>How much a JPEG's chroma planes are reduced against luminance.</summary>
    public enum ChromaSubsampling : byte
    {
        /// <summary>Half width, half height.</summary>
        S420,
        /// <summary>Half width, full height.</summary>
        S422,
        /// <summary>Full resolution.</summary>
        S444
    }

    /// <summary>How a JPEG is written; the default is libjpeg's: quality 75, 4:2:0, standard Huffman tables.</summary>
    public readonly struct JpegSettings
    {
        /// <summary>libjpeg's default quality.</summary>
        public const int DefaultQuality = 75;

        private readonly int quality;

        /// <summary>Creates settings; a zero quality takes the default.</summary>
        public JpegSettings(int quality, ChromaSubsampling subsampling = ChromaSubsampling.S420, bool optimize = false)
        {
            this.quality = quality;
            Subsampling = subsampling;
            Optimize = optimize;
        }

        /// <summary>libjpeg's quality scale (1-100).</summary>
        public int Quality => quality != 0 ? quality : DefaultQuality;
        /// <summary>Chroma resolution of a colour image; grey images ignore it.</summary>
        public ChromaSubsampling Subsampling { get; }
        /// <summary>Huffman tables built for the image in a second pass, rather than the standard ones.</summary>
        public bool Optimize { get; }

        internal void Check()
        {
            if (Quality < 1 || Quality > 100) throw new ArgumentException($"jpeg quality {Quality} is outside 1-100");
            if (Subsampling > ChromaSubsampling.S444) throw new ArgumentException($"unknown chroma subsampling {Subsampling}");
        }
    }

    /// <summary>How a lossless JPEG is written; the default is TurboJPEG's predictor 1.</summary>
    public readonly struct JpegLosslessSettings
    {
        private readonly int predictor;

        /// <summary>Creates settings; a zero predictor takes the default.</summary>
        public JpegLosslessSettings(int predictor) => this.predictor = predictor;

        /// <summary>The T.81 predictor (1-7) each sample's difference is taken from.</summary>
        public int Predictor => predictor != 0 ? predictor : 1;

        internal void Check()
        {
            if (Predictor < 1 || Predictor > 7) throw new ArgumentException($"lossless jpeg predictor {Predictor} is outside 1-7");
        }
    }

    /// <summary>How a BMP stores one pixel. Indexed formats take an exact palette of the image's colours, without alpha.</summary>
    public enum BmpPixelFormat : byte
    {
        /// <summary>Indexed8 with a grey palette for R8, Argb32 for every other layout.</summary>
        Auto,
        /// <summary>Two colours, 1 bit each.</summary>
        Indexed1,
        /// <summary>Up to 16 colours, 4 bits each.</summary>
        Indexed4,
        /// <summary>Up to 256 colours, a byte each.</summary>
        Indexed8,
        /// <summary>16 bits: 5 red, 5 green, 5 blue.</summary>
        Rgb555,
        /// <summary>16 bits: 5 red, 6 green, 5 blue.</summary>
        Rgb565,
        /// <summary>16 bits: 1 alpha, 5 red, 5 green, 5 blue.</summary>
        Argb1555,
        /// <summary>16 bits: 4 each of alpha, red, green, blue.</summary>
        Argb4444,
        /// <summary>24 bits: blue, green, red.</summary>
        Rgb24,
        /// <summary>32 bits: blue, green, red, an unused byte.</summary>
        Xrgb32,
        /// <summary>32 bits: blue, green, red, alpha.</summary>
        Argb32
    }

    /// <summary>The BMP header version.</summary>
    public enum BmpHeader : byte
    {
        /// <summary>V5 for an alpha format stored with bit fields, Info for everything else.</summary>
        Auto,
        /// <summary>OS/2 BITMAPCOREHEADER, 12 bytes: indexed or 24-bit, uncompressed, at most 65535 pixels a side.</summary>
        Core,
        /// <summary>BITMAPINFOHEADER, 40 bytes.</summary>
        Info,
        /// <summary>BITMAPV4HEADER, 108 bytes, with masks and an sRGB colour space.</summary>
        V4,
        /// <summary>BITMAPV5HEADER, 124 bytes.</summary>
        V5
    }

    /// <summary>The BMP compression field.</summary>
    public enum BmpCompression : byte
    {
        /// <summary>Bit fields for the 16-bit formats other than Rgb555 and for an alpha format in a V4/V5 header;
        /// AlphaBitFields for a 16-bit alpha format in an Info header; none otherwise.</summary>
        Auto,
        /// <summary>BI_RGB: plain rows.</summary>
        None,
        /// <summary>BI_RLE4 or BI_RLE8, for Indexed4 and Indexed8.</summary>
        Rle,
        /// <summary>BI_BITFIELDS: 16 or 32-bit pixels described by masks.</summary>
        BitFields,
        /// <summary>BI_ALPHABITFIELDS: bit fields with an alpha mask after an Info header.</summary>
        AlphaBitFields
    }

    /// <summary>How a BMP or DIB is written; the default is an 8-bit grey palette for R8 and a 32-bit V5 bitmap with
    /// alpha otherwise, rows bottom-up.</summary>
    public readonly struct BmpSettings
    {
        /// <summary>Creates settings; Auto fields are chosen from the pixel layout and each other.</summary>
        public BmpSettings(BmpPixelFormat format, BmpHeader header = BmpHeader.Auto, BmpCompression compression = BmpCompression.Auto, bool topDown = false)
        {
            Format = format;
            Header = header;
            Compression = compression;
            TopDown = topDown;
        }

        /// <summary>How one pixel is stored.</summary>
        public BmpPixelFormat Format { get; }
        /// <summary>The header version.</summary>
        public BmpHeader Header { get; }
        /// <summary>The compression field.</summary>
        public BmpCompression Compression { get; }
        /// <summary>Rows stored top to bottom, as a negative height; not with RLE or a Core header.</summary>
        public bool TopDown { get; }

        internal bool HasAlpha => Format == BmpPixelFormat.Argb1555 || Format == BmpPixelFormat.Argb4444 || Format == BmpPixelFormat.Argb32;
        internal bool IsIndexed => Format >= BmpPixelFormat.Indexed1 && Format <= BmpPixelFormat.Indexed8;
        internal bool IsSixteen => Format >= BmpPixelFormat.Rgb555 && Format <= BmpPixelFormat.Argb4444;

        // The settings with every Auto field decided.
        internal BmpSettings Resolve(PixelLayout layout)
        {
            BmpPixelFormat format = Format != BmpPixelFormat.Auto ? Format : layout == PixelLayout.R8 ? BmpPixelFormat.Indexed8 : BmpPixelFormat.Argb32;
            bool alpha = format == BmpPixelFormat.Argb1555 || format == BmpPixelFormat.Argb4444 || format == BmpPixelFormat.Argb32;
            BmpHeader header = Header;
            if (header == BmpHeader.Auto)
                header = alpha && Compression != BmpCompression.None && Compression != BmpCompression.AlphaBitFields ? BmpHeader.V5 : BmpHeader.Info;
            BmpCompression compression = Compression;
            if (compression == BmpCompression.Auto)
            {
                bool masksInHeader = header == BmpHeader.V4 || header == BmpHeader.V5;
                if (format == BmpPixelFormat.Rgb565) compression = BmpCompression.BitFields;
                else if (format == BmpPixelFormat.Argb1555 || format == BmpPixelFormat.Argb4444) compression = masksInHeader ? BmpCompression.BitFields : BmpCompression.AlphaBitFields;
                else if (format == BmpPixelFormat.Argb32 && masksInHeader) compression = BmpCompression.BitFields;
                else compression = BmpCompression.None;
            }
            return new BmpSettings(format, header, compression, TopDown);
        }

        // Called on resolved settings.
        internal void Check()
        {
            if (Format > BmpPixelFormat.Argb32) throw new ArgumentException($"unknown bmp pixel format {Format}");
            if (Header > BmpHeader.V5) throw new ArgumentException($"unknown bmp header {Header}");
            if (Compression > BmpCompression.AlphaBitFields) throw new ArgumentException($"unknown bmp compression {Compression}");
            bool masks = Compression == BmpCompression.BitFields || Compression == BmpCompression.AlphaBitFields;
            if (Header == BmpHeader.Core && (!IsIndexed && Format != BmpPixelFormat.Rgb24 || Compression != BmpCompression.None || TopDown))
                throw new ArgumentException($"a core header holds only uncompressed bottom-up indexed or 24-bit rows, not {Format} {Compression}{(TopDown ? " top-down" : "")}");
            if (Compression == BmpCompression.Rle && Format != BmpPixelFormat.Indexed4 && Format != BmpPixelFormat.Indexed8) throw new ArgumentException($"bmp RLE needs Indexed4 or Indexed8, not {Format}");
            if (Compression == BmpCompression.Rle && TopDown) throw new ArgumentException("bmp RLE rows cannot be top-down");
            if (masks && !IsSixteen && Format != BmpPixelFormat.Xrgb32 && Format != BmpPixelFormat.Argb32) throw new ArgumentException($"bmp bit fields need a 16 or 32-bit format, not {Format}");
            if (Compression == BmpCompression.AlphaBitFields && Header != BmpHeader.Info) throw new ArgumentException("bmp alpha bit fields follow an Info header only; V4 and V5 carry the alpha mask themselves");
            if (Compression == BmpCompression.None && (Format == BmpPixelFormat.Rgb565 || Format == BmpPixelFormat.Argb1555 || Format == BmpPixelFormat.Argb4444))
                throw new ArgumentException($"{Format} needs bit fields; uncompressed 16-bit is Rgb555");
            if (Compression == BmpCompression.BitFields && Header == BmpHeader.Info && HasAlpha) throw new ArgumentException($"{Format} with bit fields after an Info header has no alpha mask; use a V4/V5 header or AlphaBitFields");
        }
    }

    /// <summary>How a TGA stores one pixel. Indexed formats take an exact palette of the image's colours.</summary>
    public enum TgaPixelFormat : byte
    {
        /// <summary>Grey8 for R8, Argb32 for every other layout.</summary>
        Auto,
        /// <summary>8-bit grey.</summary>
        Grey8,
        /// <summary>8-bit grey and 8-bit alpha.</summary>
        GreyAlpha16,
        /// <summary>Up to 256 colours with a 24-bit colour map; alpha is dropped.</summary>
        Indexed8,
        /// <summary>Up to 256 colours with a 32-bit colour map that keeps alpha.</summary>
        IndexedAlpha8,
        /// <summary>16 bits: 5 each of red, green, blue.</summary>
        Rgb555,
        /// <summary>16 bits: 1 alpha, 5 each of red, green, blue.</summary>
        Argb1555,
        /// <summary>24 bits: blue, green, red.</summary>
        Rgb24,
        /// <summary>32 bits: blue, green, red, alpha.</summary>
        Argb32
    }

    /// <summary>The corner a TGA's first stored pixel sits in.</summary>
    public enum TgaOrigin : byte
    {
        /// <summary>Rows bottom-up, pixels left to right.</summary>
        BottomLeft,
        /// <summary>Rows bottom-up, pixels right to left.</summary>
        BottomRight,
        /// <summary>Rows top-down, pixels left to right.</summary>
        TopLeft,
        /// <summary>Rows top-down, pixels right to left.</summary>
        TopRight
    }

    /// <summary>How a TGA is written; the default is uncompressed grey for R8 and 32-bit otherwise, bottom-left first,
    /// with the TGA 2.0 footer.</summary>
    public readonly struct TgaSettings
    {
        /// <summary>Creates settings.</summary>
        public TgaSettings(TgaPixelFormat format, bool rle = false, TgaOrigin origin = TgaOrigin.BottomLeft, bool omitFooter = false)
        {
            Format = format;
            Rle = rle;
            Origin = origin;
            OmitFooter = omitFooter;
        }

        /// <summary>How one pixel is stored.</summary>
        public TgaPixelFormat Format { get; }
        /// <summary>Run-length packets, none crossing a row.</summary>
        public bool Rle { get; }
        /// <summary>The corner the first stored pixel sits in.</summary>
        public TgaOrigin Origin { get; }
        /// <summary>Writes a TGA 1.0 file, without the TRUEVISION-XFILE footer.</summary>
        public bool OmitFooter { get; }

        internal TgaPixelFormat FormatFor(PixelLayout layout) => Format != TgaPixelFormat.Auto ? Format : layout == PixelLayout.R8 ? TgaPixelFormat.Grey8 : TgaPixelFormat.Argb32;

        internal void Check()
        {
            if (Format > TgaPixelFormat.Argb32) throw new ArgumentException($"unknown tga pixel format {Format}");
            if (Origin > TgaOrigin.TopRight) throw new ArgumentException($"unknown tga origin {Origin}");
        }
    }

    /// <summary>The Netpbm format written.</summary>
    public enum PnmKind : byte
    {
        /// <summary>PGM for R8 and RG8, PPM for RGBA8 and BGRA8; alpha is dropped.</summary>
        Auto,
        /// <summary>PBM bitmap: luminance below 128 is black.</summary>
        Pbm,
        /// <summary>PGM greymap of luminance.</summary>
        Pgm,
        /// <summary>PPM pixmap of red, green and blue.</summary>
        Ppm,
        /// <summary>PAM (P7) with a tuple type.</summary>
        Pam
    }

    /// <summary>A PAM tuple type.</summary>
    public enum PamTupleType : byte
    {
        /// <summary>GRAYSCALE for R8, GRAYSCALE_ALPHA for RG8, RGB_ALPHA for RGBA8 and BGRA8.</summary>
        Auto,
        /// <summary>BLACKANDWHITE: maxval 1, where 1 is white.</summary>
        BlackAndWhite,
        /// <summary>BLACKANDWHITE_ALPHA: maxval 1.</summary>
        BlackAndWhiteAlpha,
        /// <summary>GRAYSCALE.</summary>
        Grayscale,
        /// <summary>GRAYSCALE_ALPHA.</summary>
        GrayscaleAlpha,
        /// <summary>RGB.</summary>
        Rgb,
        /// <summary>RGB_ALPHA.</summary>
        RgbAlpha
    }

    /// <summary>How a Netpbm file is written; the default is raw PGM or PPM at maxval 255.</summary>
    public readonly struct PnmSettings
    {
        private readonly int maxVal;

        /// <summary>Creates settings; a zero maxval takes 255, or 1 for PBM and the black-and-white tuple types.</summary>
        public PnmSettings(PnmKind kind, bool plain = false, int maxVal = 0, PamTupleType tupleType = PamTupleType.Auto)
        {
            Kind = kind;
            Plain = plain;
            this.maxVal = maxVal;
            TupleType = tupleType;
        }

        /// <summary>The format written.</summary>
        public PnmKind Kind { get; }
        /// <summary>ASCII samples (P1-P3) rather than binary (P4-P6); PAM has no plain form.</summary>
        public bool Plain { get; }
        /// <summary>The largest sample value (1-65535); above 255 samples take two bytes.</summary>
        public int MaxVal => maxVal != 0 ? maxVal : IsOneBit ? 1 : 255;
        /// <summary>The PAM tuple type; only PAM takes one.</summary>
        public PamTupleType TupleType { get; }

        internal bool IsOneBit => Kind == PnmKind.Pbm || TupleType == PamTupleType.BlackAndWhite || TupleType == PamTupleType.BlackAndWhiteAlpha;

        internal PnmKind KindFor(PixelLayout layout) => Kind != PnmKind.Auto ? Kind : layout == PixelLayout.R8 || layout == PixelLayout.RG8 ? PnmKind.Pgm : PnmKind.Ppm;

        internal PamTupleType TupleTypeFor(PixelLayout layout) => TupleType != PamTupleType.Auto ? TupleType
            : layout == PixelLayout.R8 ? PamTupleType.Grayscale : layout == PixelLayout.RG8 ? PamTupleType.GrayscaleAlpha : PamTupleType.RgbAlpha;

        internal void Check()
        {
            if (Kind > PnmKind.Pam) throw new ArgumentException($"unknown netpbm kind {Kind}");
            if (TupleType > PamTupleType.RgbAlpha) throw new ArgumentException($"unknown pam tuple type {TupleType}");
            if (Plain && Kind == PnmKind.Pam) throw new ArgumentException("PAM has no plain form");
            if (TupleType != PamTupleType.Auto && Kind != PnmKind.Pam) throw new ArgumentException($"a tuple type is for PAM only, not {Kind}");
            if (MaxVal < 1 || MaxVal > 65535) throw new ArgumentException($"netpbm maxval {MaxVal} is outside 1-65535");
            if (IsOneBit && MaxVal != 1) throw new ArgumentException($"{(Kind == PnmKind.Pbm ? "PBM" : TupleType.ToString())} has maxval 1, not {MaxVal}");
        }
    }

    /// <summary>The colour space a QOI header names; it changes no pixel data.</summary>
    public enum QoiColorspace : byte
    {
        /// <summary>sRGB with linear alpha.</summary>
        Srgb,
        /// <summary>Every channel linear.</summary>
        Linear
    }

    /// <summary>How a QOI is written; the default is 3 channels for R8 and 4 otherwise, sRGB.</summary>
    public readonly struct QoiSettings
    {
        private readonly int channels;

        /// <summary>Creates settings; zero channels are chosen from the layout.</summary>
        public QoiSettings(int channels, QoiColorspace colorspace = QoiColorspace.Srgb)
        {
            this.channels = channels;
            Colorspace = colorspace;
        }

        /// <summary>3 (alpha dropped) or 4; zero chooses from the layout.</summary>
        public int Channels => channels;
        /// <summary>The colour space the header names.</summary>
        public QoiColorspace Colorspace { get; }

        internal int ChannelsFor(PixelLayout layout) => channels != 0 ? channels : layout == PixelLayout.R8 ? 3 : 4;

        internal void Check()
        {
            if (channels != 0 && channels != 3 && channels != 4) throw new ArgumentException($"qoi channels {channels} is not 3 or 4");
            if (Colorspace > QoiColorspace.Linear) throw new ArgumentException($"unknown qoi colour space {Colorspace}");
        }
    }

    /// <summary>How an icon or cursor entry stores its image.</summary>
    public enum IconEncoding : byte
    {
        /// <summary>An RGBA8 PNG.</summary>
        Png,
        /// <summary>A DIB with an AND mask.</summary>
        Bmp
    }

    /// <summary>One image of an icon or cursor, at most 256 pixels a side.</summary>
    public readonly struct IconEntry
    {
        /// <summary>Creates an entry; a BMP entry's Auto format is Argb32, and the hotspot is for cursors only.</summary>
        public IconEntry(Image image, IconEncoding encoding = IconEncoding.Png, BmpPixelFormat bmpFormat = BmpPixelFormat.Auto, int hotspotX = 0, int hotspotY = 0)
        {
            Image = image;
            Encoding = encoding;
            BmpFormat = bmpFormat;
            HotspotX = hotspotX;
            HotspotY = hotspotY;
        }

        /// <summary>The entry's image.</summary>
        public Image Image { get; }
        /// <summary>PNG or BMP.</summary>
        public IconEncoding Encoding { get; }
        /// <summary>A BMP entry's pixel format: Indexed1/4/8, Rgb555, Rgb24, Xrgb32 or Argb32.</summary>
        public BmpPixelFormat BmpFormat { get; }
        /// <summary>A cursor's hotspot column.</summary>
        public int HotspotX { get; }
        /// <summary>A cursor's hotspot row.</summary>
        public int HotspotY { get; }
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
