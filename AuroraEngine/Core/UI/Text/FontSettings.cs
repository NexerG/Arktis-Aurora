using ArctisAurora.Core.Registry;

namespace ArctisAurora.Core.UI.Text
{
    [A_XSDType("LanguagePacks", "Settings")]
    public class LanguagePacksSetting : Setting
    {
        [A_XSDElementProperty("Packs", "Settings", "Charsets an imported font is baked with, separated by commas.")]
        public string packs { get; set; } = "Latin";
    }

    [A_XSDType("Fonts", "Settings", AllowedChildren = typeof(Setting))]
    public class FontSettings : SettingCategory
    {
        public readonly LanguagePacksSetting languages = new LanguagePacksSetting { onChanged = "Fonts.Rebake" };
    }
}
