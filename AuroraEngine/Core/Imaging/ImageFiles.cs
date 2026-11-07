using ArctisAurora.Core.Registry;
using Arctis.Imaging;
using Image = Arctis.Imaging.Image;

namespace ArctisAurora.Core.Imaging
{
    // Image files for the engine: Arctis.Imaging under the configured limits.
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

        // Decodes any image file the engine reads; the hint names a format with no signature, such as TGA.
        public static Image Load(ReadOnlySpan<byte> data, PixelLayout layout = PixelLayout.RGBA8, ImageFormat hint = ImageFormat.Unknown) => Image.Load(data, layout, Limits, hint);

        public static Image Load(string path, PixelLayout layout = PixelLayout.RGBA8) => Load(File.ReadAllBytes(path), layout, HintOf(path));

        // Width and height from the file's header, without decoding pixels.
        public static bool TryIdentify(string path, out int width, out int height)
        {
            bool known = ImageCodec.TryIdentify(File.ReadAllBytes(path), out ImageHeader header, HintOf(path));
            width = header.Width;
            height = header.Height;
            return known;
        }

        private static ImageFormat HintOf(string path) => path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) ? ImageFormat.Tga
            : path.EndsWith(".pcx", StringComparison.OrdinalIgnoreCase) ? ImageFormat.Pcx
            : path.EndsWith(".xbm", StringComparison.OrdinalIgnoreCase) ? ImageFormat.Xbm : ImageFormat.Unknown;
    }
}
