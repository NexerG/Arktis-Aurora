using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Generators;
using Image = Arctis.Imaging.Image;

namespace ArctisAurora.Core.Filing
{
    [@Serializable]
    public class IconAtlasMetaData
    {
        [@Serializable]
        public int iconCount;
        // newline separated, in atlas cell order; the serializer has no string[] support
        [@Serializable]
        public string names = string.Empty;
        [@Serializable]
        public Glyph[] icons = null!;
        [@Serializable]
        public float pxRange;
        [@Serializable]
        public int cellSize;

        // cell quad over the ink box
        public float cellScale => 1f / (1f - 2f * MTSDFGen.InkMargin(cellSize));

        [@NonSerializable]
        private string[] split = null!;

        public (Glyph, int) GetIconAndIndex(string name)
        {
            split ??= names.Split('\n');
            int index = Array.IndexOf(split, name);
            if (index < 0 || index >= icons.Length)
                return (null!, -1);
            return (icons[index], index);
        }
    }

    public static class IconSet
    {
        private static readonly Diagnostics.LogChannel Log = Diagnostics.LogChannel.For("Assets");

        // Cooks a folder of SVGs into one MTSDF atlas plus its .aid, the same square grid the font
        // atlas uses. An icon that cannot be read is skipped, not fatal.
        internal static void GenerateIconAtlas(string setName, IEnumerable<string> svgFiles, int perIconSize, string outputRoot)
        {
            List<string> names = new List<string>();
            List<Glyph> icons = new List<Glyph>();

            Diagnostics.Profiling.Zone.Start("Atlas.ReadFaces");
            foreach (string file in svgFiles.OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                if (!SvgPath.TryLoad(file, out Glyph icon, out string reason))
                {
                    Log.Warn($"icon '{Path.GetFileName(file)}': {reason} — skipped.");
                    continue;
                }
                MTSDFGen.ColorEdges(icon);
                names.Add(Path.GetFileNameWithoutExtension(file));
                icons.Add(icon);
            }
            Diagnostics.Profiling.Zone.End("Atlas.ReadFaces");

            IconAtlasMetaData meta = new IconAtlasMetaData();
            meta.iconCount = icons.Count;
            meta.names = string.Join('\n', names);
            meta.icons = icons.ToArray();
            meta.pxRange = MTSDFGen.PxRange;
            meta.cellSize = perIconSize;

            string dir = Path.Combine(outputRoot, setName);
            Directory.CreateDirectory(dir);
            Diagnostics.Profiling.Zone.Start("Atlas.WriteMeta");
            Serializer.SerializeAttributed(meta, Path.Combine(dir, $"{setName}.aid")); // aurora icon data
            Diagnostics.Profiling.Zone.End("Atlas.WriteMeta");

            int iconsPerAxis = Math.Max(1, (int)Math.Ceiling(MathF.Sqrt(icons.Count)));
            Image atlas = new Image(perIconSize * iconsPerAxis, perIconSize * iconsPerAxis);
            Diagnostics.Profiling.Zone.Start("Atlas.Cells");
            for (int i = 0; i < icons.Count; i++)
            {
                MTSDFGen.GenerateCell(icons[i], atlas, (i % iconsPerAxis) * perIconSize,
                    (i / iconsPerAxis) * perIconSize, perIconSize, MTSDFGen.PxRange);
                Diagnostics.Profiling.Zone.Increment("Cell");
            }
            Diagnostics.Profiling.Zone.End("Atlas.Cells");

            Diagnostics.Profiling.Zone.Start("Atlas.SavePng");
            atlas.SavePng(Path.Combine(dir, $"{setName}_atlas.png"));
            Diagnostics.Profiling.Zone.End("Atlas.SavePng");
            Log.Info($"icon set '{setName}': {icons.Count} icons at {perIconSize}px.");
        }
    }
}
