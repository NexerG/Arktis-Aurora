using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Generators;
using ArctisAurora.Core.Registry;
using Silk.NET.Vulkan;

namespace ArctisAurora.Core.Registry.Assets
{
    [A_XSDType("IconSetAsset", "AssetRegistry")]
    public class IconSetAsset : AbstractAsset
    {
        private static readonly Core.Diagnostics.LogChannel Log = Core.Diagnostics.LogChannel.For("Assets");

        public IconAtlasMetaData metaData = null!;
        public TextureAsset textureAsset = null!;

        public IconSetAsset() { }

        public override void Load(string name, string source)
        {
            string setName = source.Substring(source.LastIndexOf('/') + 1);

            metaData = new IconAtlasMetaData();
            Serializer.DeserializeAttributed(Paths.Icon(setName, setName + ".aid"), ref metaData);

            if (metaData.pxRange != MTSDFGen.PxRange)
                Log.Error($"icon set '{setName}' was baked at pxRange {metaData.pxRange}, the shader expects {MTSDFGen.PxRange} — re-import it.");

            textureAsset = new TextureAsset();
            textureAsset.LoadFile(Paths.Icon(setName, setName + "_atlas.adf"), Format.R8G8B8A8Unorm);
        }
    }
}
