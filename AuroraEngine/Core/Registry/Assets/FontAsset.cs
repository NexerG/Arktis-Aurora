using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Generators;
using ArctisAurora.Core.Registry;
using ArctisAurora.EngineWork.Registry;
using Silk.NET.Vulkan;
using System.Xml.Linq;

namespace ArctisAurora.Core.Registry.Assets
{
    [A_XSDType("FontAsset", "AssetRegistry")]
    public class FontAsset : AbstractAsset
    {
        private static readonly Core.Diagnostics.LogChannel Log = Core.Diagnostics.LogChannel.For("Assets");

        public AtlasMetaData atlasMetaData = null!;
        public TextureAsset textureAsset = null!;

        // a math font's layout constants; null for a text font
        public MathConstants? mathConstants;

        // the cooked folder under Data/Fonts
        public string folder = string.Empty;

        // the name the font calls itself
        public string family = string.Empty;

        public FontAsset() { }

        public Glyph GetGlyph(char c) => atlasMetaData.GetGlyph(c);

        public override void Load(string name, string source)
        {
            string fontName = source.Substring(source.LastIndexOf('/') + 1);
            folder = fontName;

            atlasMetaData = new AtlasMetaData();
            Serializer.DeserializeAttributed(Paths.Font(fontName, fontName + ".agd"), ref atlasMetaData);
            atlasMetaData.BuildCharIndex();

            if (atlasMetaData.pxRange != MTSDFGen.PxRange)
                Log.Error($"font '{fontName}' was baked at pxRange {atlasMetaData.pxRange}, the shader expects {MTSDFGen.PxRange} — re-import it.");

            textureAsset = new TextureAsset();
            textureAsset.LoadFile(Paths.Font(fontName, fontName + "_atlas.adf"), Format.R8G8B8A8Unorm);

            string math = Paths.Font(fontName, fontName + ".math.axml");
            if (DataFiles.Exists(math)) mathConstants = MathConstants.Load(math);

            family = FamilyOf(fontName) ?? name;
        }

        // The stamp's family, else the installed source's own name.
        private static string? FamilyOf(string folder)
        {
            string stamp = Paths.Font(folder, folder + ".import.axml");
            if (!DataFiles.Exists(stamp)) return null;

            XElement root = DataFiles.LoadXml(stamp);
            if ((string?)root.Attribute("Family") is { Length: > 0 } family) return family;

            string? source = AssetImporter.ResolveSystemFont((string?)root.Attribute("Source") ?? string.Empty);
            return source == null ? null : FontCatalog.ReadFace(source, (int?)root.Attribute("Face") ?? 0)?.family;
        }

        /*public FontAsset LoadFont(string name)
        {
            if (AssetRegistries.fonts.ContainsKey(name))
            {
                return AssetRegistries.fonts[name];
            }
            atlasMetaData = new AtlasMetaData();
            atlasMetaData.Deserialize(name);

            string imagePath = Paths.FONTS + "/" + name + "/" + name + "_atlas.png";
            if (System.IO.File.Exists(imagePath))
            {
                image = Image.Load<Rgba32>(imagePath);
                AssetRegistries.fonts[name] = this;
                return this;
            }

            throw new Exception(name);
        }

        public static FontAsset LoadDefault()
        {
            FontAsset fontAsset = new FontAsset();

            fontAsset.atlasMetaData = new AtlasMetaData();
            fontAsset.atlasMetaData.Deserialize("arial");

            string imagePath = Paths.FONTS + "/arial/" + "arial_atlas.png";
            fontAsset.image = Image.Load<Rgba32>(imagePath);

            return fontAsset;
        }*/
    }
}
