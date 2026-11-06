using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Generators;
using Image = Arctis.Imaging.Image;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using static ArctisAurora.Core.Filing.AuroraFont;

namespace ArctisAurora.Core.Filing
{
    // Every installed family's name in its own face, baked as one font atlas under private-use codepoints.
    public static class FontPreviews
    {
        private static readonly Diagnostics.LogChannel Log = Diagnostics.LogChannel.For("Assets");

        public const string AssetName = "font-previews";

        // bake shape; bump version to force a re-bake
        private const int cellSize = 32;
        private const int version = 2;
        private const int firstCode = 0xE000;
        private const int lastCode = 0xF8FF;

        private static Dictionary<string, string>? texts;

        // A family's name spelt in the preview atlas's codepoints; null when it has no preview.
        public static string? TextFor(string family) => Volatile.Read(ref texts)?.GetValueOrDefault(family);

        // Bakes into outputRoot/font-previews unless the stamp still matches the families.
        public static void Bake(IReadOnlyList<InstalledFamily> families, string outputRoot)
        {
            string dir = Path.Combine(outputRoot, AssetName);
            string mapPath = Path.Combine(dir, AssetName + ".map.axml");
            string agdPath = Path.Combine(dir, AssetName + ".agd");
            string atlasPath = Path.Combine(dir, AssetName + "_atlas.adf");
            string hash = Hash(families);

            if (File.Exists(mapPath) && File.Exists(agdPath) && File.Exists(atlasPath))
            {
                XElement stored = XElement.Load(mapPath);
                if ((string?)stored.Attribute("Hash") == hash)
                {
                    Volatile.Write(ref texts, stored.Elements("Preview")
                        .ToDictionary(e => (string)e.Attribute("Family")!, e => (string)e.Attribute("Text")!, StringComparer.OrdinalIgnoreCase));
                    return;
                }
            }

            List<char> chars = new List<char>();
            List<Glyph> glyphs = new List<Glyph>();
            Dictionary<string, string> spelt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int next = firstCode;
            foreach (InstalledFamily family in families)
            {
                char[] distinct = family.name.Distinct().ToArray();
                if (next + distinct.Length - 1 > lastCode) break;

                Glyph[]? faceGlyphs = ReadGlyphs(family, distinct);
                if (faceGlyphs == null) continue;

                Dictionary<char, char> code = new Dictionary<char, char>();
                for (int i = 0; i < distinct.Length; i++)
                {
                    code[distinct[i]] = (char)next;
                    chars.Add((char)next);
                    glyphs.Add(faceGlyphs[i]);
                    next++;
                }
                spelt[family.name] = new string(family.name.Select(c => code[c]).ToArray());
            }

            Log.Info($"font previews: baking {glyphs.Count} glyphs for {spelt.Count} families...");
            Directory.CreateDirectory(dir);
            WriteAtlas(chars.ToArray(), glyphs.ToArray(), agdPath, atlasPath);
            new XElement("FontPreviews", new XAttribute("Hash", hash),
                spelt.Select(p => new XElement("Preview", new XAttribute("Family", p.Key), new XAttribute("Text", p.Value)))).Save(mapPath);
            Volatile.Write(ref texts, spelt);
        }

        // The regular face's glyphs for chars; null when the face cannot draw one of them.
        private static Glyph[]? ReadGlyphs(InstalledFamily family, char[] chars)
        {
            try
            {
                AuroraFont font = new AuroraFont();
                using (BinaryReader reader = new BinaryReader(File.OpenRead(family.regular)))
                {
                    font.tableEntries = FontCatalog.ReadTables(reader, family.regularFace);
                    TableEntry cmap = font.tableEntries.First(t => t.name == "cmap");
                    if (chars.Any(c => GetGlyphIndex(c, reader, cmap) == 0)) return null;
                }
                font.textData = new TextData { characterCount = chars.Length, characters = chars };
                return ReadFaceGlyphs(font, family.regular);
            }
            catch (Exception e)
            {
                Log.Debug($"font previews: '{family.name}' skipped ({e.Message}).");
                return null;
            }
        }

        // Same cell layout as a family atlas, one face.
        private static void WriteAtlas(char[] chars, Glyph[] glyphs, string agdPath, string atlasPath)
        {
            AtlasMetaData meta = new AtlasMetaData
            {
                glyphCount = glyphs.Length,
                chars = chars,
                glyphs = glyphs,
                pxRange = MTSDFGen.PxRange,
                cellSize = cellSize
            };
            Serializer.SerializeAttributed(meta, agdPath);

            int perAxis = (int)Math.Ceiling(MathF.Sqrt(Math.Max(1, glyphs.Length)));
            Image image = new Image(cellSize * perAxis, cellSize * perAxis);
            for (int cell = 0; cell < glyphs.Length; cell++)
                MTSDFGen.GenerateCell(glyphs[cell], image, cell % perAxis * cellSize, cell / perAxis * cellSize, cellSize, MTSDFGen.PxRange);
            image.SaveAdf(atlasPath);
        }

        private static string Hash(IReadOnlyList<InstalledFamily> families)
        {
            StringBuilder key = new StringBuilder($"{version}|{cellSize}|{MTSDFGen.PxRange}");
            foreach (InstalledFamily family in families)
                key.Append('|').Append(family.name).Append('|').Append(family.regular).Append('|').Append(family.regularFace)
                   .Append('|').Append(File.Exists(family.regular) ? File.GetLastWriteTimeUtc(family.regular).Ticks : 0);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())));
        }
    }
}
