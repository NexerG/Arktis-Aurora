using ArctisAurora.Core.Registry;
using Arctis.Imaging;
using Image = Arctis.Imaging.Image;

namespace ArctisAurora.Core.Imaging
{
    // Image files for the engine: Arctis.Imaging under the configured limits, ImageSharp for what it cannot decode yet.
    public static class ImageFiles
    {
        // The limits from ImagingSettings; the library's defaults before settings load.
        public static ImageLimits Limits
        {
            get
            {
                if (!SettingsRegistry.Groups.TryGetValue(typeof(ImagingSettings), out ISettingsGroup? group)) return default;
                ImageLimitsSetting limits = ((ImagingSettings)group).limits;
                return new ImageLimits(limits.maxWidth, limits.maxHeight, (long)limits.maxMegapixels * 1_000_000);
            }
        }

        internal static bool CanDecode(ImageFormat format) => format == ImageFormat.Png || format == ImageFormat.Bmp || format == ImageFormat.Ktx2 || format == ImageFormat.Adf;

        // Decodes any image file the engine reads.
        public static Image Load(ReadOnlySpan<byte> data, PixelLayout layout = PixelLayout.RGBA8)
        {
            if (ImageCodec.TryIdentify(data, out ImageHeader header) && CanDecode(header.Format)) return Image.Load(data, layout, Limits);
            return LoadLegacy(data, layout);
        }

        public static Image Load(string path, PixelLayout layout = PixelLayout.RGBA8) => Load(File.ReadAllBytes(path), layout);

        // Width and height from the file's header, without decoding pixels.
        public static bool TryIdentify(ReadOnlySpan<byte> data, out int width, out int height)
        {
            if (ImageCodec.TryIdentify(data, out ImageHeader header))
            {
                width = header.Width;
                height = header.Height;
                return true;
            }
            try
            {
                SixLabors.ImageSharp.ImageInfo info = SixLabors.ImageSharp.Image.Identify(data);
                width = info.Width;
                height = info.Height;
                return true;
            }
            catch (Exception)
            {
                width = 0;
                height = 0;
                return false;
            }
        }

        private static Image LoadLegacy(ReadOnlySpan<byte> data, PixelLayout layout)
        {
            if (layout != PixelLayout.RGBA8 && layout != PixelLayout.BGRA8) throw new NotSupportedException($"{layout} is not available for this image format");
            using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> legacy = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(data);
            ImageLimits limits = Limits;
            if (legacy.Width > limits.MaxWidth || legacy.Height > limits.MaxHeight || (long)legacy.Width * legacy.Height > limits.MaxPixels)
                throw new ImageFormatException($"image size {legacy.Width}x{legacy.Height} exceeds the limits");
            Image image = new Image(legacy.Width, legacy.Height, layout);
            legacy.CopyPixelDataTo(image.Pixels);
            if (layout == PixelLayout.BGRA8) ImageOps.SwizzleBgraToRgba(image.Pixels);
            return image;
        }
    }
}
