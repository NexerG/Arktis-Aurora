using ArctisAurora.Core.Registry;

namespace ArctisAurora.Core.Imaging
{
    [A_XSDType("ImageLimits", "Settings")]
    public class ImageLimitsSetting : Setting
    {
        [A_XSDElementProperty("MaxWidth", "Settings")]
        public int maxWidth { get; set; } = 65536;

        [A_XSDElementProperty("MaxHeight", "Settings")]
        public int maxHeight { get; set; } = 65536;

        [A_XSDElementProperty("MaxMegapixels", "Settings")]
        public int maxMegapixels { get; set; } = 256;
    }

    [A_XSDType("Imaging", "Settings", AllowedChildren = typeof(Setting))]
    public class ImagingSettings : SettingCategory
    {
        public readonly ImageLimitsSetting limits = new ImageLimitsSetting();
    }
}
