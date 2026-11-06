using ArctisAurora.Core.Registry;
using ArctisAurora.EngineWork;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using static ArctisAurora.Core.Filing.AuroraFont;
using AuroraFont = ArctisAurora.Core.Filing.AuroraFont;

namespace ArctisAurora.Core.Filing.Serialization
{
    public unsafe static class AssetImporter
    {
        private static readonly Diagnostics.LogChannel Log = Diagnostics.LogChannel.For("Assets");

        // Bump to invalidate every stamp and force a re-bake.
        private const int importerVersion = 10;

        [A_XSDActionDependency("AssetImporter.RunImports", "Bootstrap")]
        public static bool RunImports()
        {
            if (!Engine.isDebug) return true;

            List<ImportSet> sets = new List<ImportSet>();
            List<string> fontRoots = new List<string>();
            foreach (string file in VirtualFileSystem.EnumerateAll("XML/Imports", "*.imports.axml"))
            {
                sets.Add(ParseImportSet(file));
                fontRoots.Add(file.StartsWith(Paths.USER_DATA, StringComparison.OrdinalIgnoreCase) ? UserFonts : Paths.FONTS);
            }

            Dictionary<string, string> charsets = Charsets(sets);
            for (int i = 0; i < sets.Count; i++)
                foreach (FontImport font in sets[i].fonts)
                    ImportFontIfStale(font, charsets, fontRoots[i]);

            foreach (ImportSet set in sets)
                foreach (IconImport icons in set.icons)
                    ImportIconsIfStale(icons);

            return true;
        }

        // where fonts imported at runtime are baked
        internal static string UserFonts => Path.Combine(Paths.USER_DATA, "Fonts");

        // Every declared charset across every mounted import set.
        internal static Dictionary<string, string> Charsets() =>
            Charsets(VirtualFileSystem.EnumerateAll("XML/Imports", "*.imports.axml").Select(ParseImportSet));

        private static Dictionary<string, string> Charsets(IEnumerable<ImportSet> sets)
        {
            Dictionary<string, string> charsets = new Dictionary<string, string>();
            foreach (ImportSet set in sets)
                foreach (Charset charset in set.charsets)
                    charsets[charset.name] = charset.chars;
            return charsets;
        }

        // A comma-separated list of charset names as one string, in order, each character once; null when one is unknown.
        private static string ResolveChars(string names, Dictionary<string, string> charsets)
        {
            StringBuilder chars = new StringBuilder();
            HashSet<char> seen = new HashSet<char>();
            foreach (string name in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!charsets.TryGetValue(name, out string set)) return null;
                foreach (char c in set)
                    if (seen.Add(c)) chars.Append(c);
            }
            return chars.ToString();
        }

        // The characters a face's cmap maps.
        private static string Covered(string chars, string path, int face)
        {
            using BinaryReader reader = new BinaryReader(File.OpenRead(path));
            AuroraFont.TableEntry cmap = FontCatalog.ReadTables(reader, face).First(t => t.name == "cmap");
            return new string(chars.Where(c => GetGlyphIndex(c, reader, cmap) != 0).ToArray());
        }

        private static ImportSet ParseImportSet(string path)
        {
            ImportSet set = new ImportSet();
            XElement root = XElement.Load(path);
            XNamespace ns = root.GetDefaultNamespace();

            foreach (XElement element in root.Elements(ns + "Charset"))
            {
                Charset charset = new Charset();
                XmlReflection.ApplyAttributes(element, charset);
                set.charsets.Add(charset);
            }
            foreach (XElement element in root.Elements(ns + "FontImport"))
            {
                FontImport font = new FontImport();
                XmlReflection.ApplyAttributes(element, font);
                foreach (XElement child in element.Elements(ns + "Variant"))
                {
                    FontVariantImport variant = new FontVariantImport();
                    XmlReflection.ApplyAttributes(child, variant);
                    font.variants.Add(variant);
                }
                set.fonts.Add(font);
            }
            foreach (XElement element in root.Elements(ns + "IconImport"))
            {
                IconImport icons = new IconImport();
                XmlReflection.ApplyAttributes(element, icons);
                set.icons.Add(icons);
            }
            return set;
        }

        // An absolute path as given; else machine-wide fonts, the per-user font folder, then the repo's _Build/FontSources.
        internal static string ResolveSystemFont(string fileName)
        {
            if (Path.IsPathRooted(fileName)) return File.Exists(fileName) ? fileName : null;

            string machine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), fileName);
            if (File.Exists(machine)) return machine;

            string user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Fonts", fileName);
            if (File.Exists(user)) return user;

            string downloaded = Path.GetFullPath(Path.Combine(Paths.DATA, "..", "..", "_Build", "FontSources", fileName));
            if (File.Exists(downloaded)) return downloaded;

            return null;
        }

        // Suffixes tried when a face is not declared, in preference order.
        private static readonly string[] boldSuffixes = { "bd", "b", "-Bold", "Bold" };
        private static readonly string[] italicSuffixes = { "i", "-Italic", "Italic" };
        private static readonly string[] boldItalicSuffixes = { "bi", "z", "-BoldItalic", "BoldItalic" };

        // A declared face is taken as named and never second-guessed; an undeclared one is probed.
        private static string ResolveFace(FontImport font, string declared, string[] suffixes)
        {
            if (!string.IsNullOrEmpty(declared))
            {
                string path = ResolveSystemFont(declared);
                if (path == null) Log.Warn($"font import '{font.source}': declared face '{declared}' is not installed, skipped.");
                return path;
            }

            string stem = Path.GetFileNameWithoutExtension(font.source);
            string extension = Path.GetExtension(font.source);
            if (stem.EndsWith("-Regular", StringComparison.OrdinalIgnoreCase)) stem = stem[..^8];
            else if (stem.EndsWith("Regular", StringComparison.OrdinalIgnoreCase)) stem = stem[..^7];

            foreach (string suffix in suffixes)
            {
                string path = ResolveSystemFont(stem + suffix + extension);
                if (path != null) return path;
            }
            return null;
        }

        // Bakes into outputRoot unless its stamp still matches; false when the font was skipped.
        internal static bool ImportFontIfStale(FontImport font, Dictionary<string, string> charsets, string outputRoot)
        {
            string sourcePath = ResolveSystemFont(font.source);
            if (sourcePath == null)
            {
                Log.Warn($"font import '{font.source}': no such font installed, skipped.");
                return false;
            }
            string chars = ResolveChars(font.charset, charsets);
            if (chars == null)
            {
                Log.Warn($"font import '{font.source}': unknown charset '{font.charset}', skipped.");
                return false;
            }
            if (font.covered) chars = Covered(chars, sourcePath, font.face);

            string boldPath = ResolveFace(font, font.bold, boldSuffixes);
            string italicPath = ResolveFace(font, font.italic, italicSuffixes);
            string boldItalicPath = ResolveFace(font, font.boldItalic, boldItalicSuffixes);

            // every face in block order: the plain family's, then each variant's that has one
            int capacity = 4 + 4 * font.variants.Count;
            List<string> paths = new List<string>(capacity);
            List<int> indices = new List<int>(capacity);
            List<int> keys = new List<int>(capacity);
            AddFace(paths, indices, keys, sourcePath, font.face, 0);
            AddFace(paths, indices, keys, boldPath, 0, 1);
            AddFace(paths, indices, keys, italicPath, 0, 2);
            AddFace(paths, indices, keys, boldItalicPath, 0, 3);

            StringBuilder variantNames = new StringBuilder();
            int variant = 0;
            foreach (FontVariantImport declared in font.variants)
            {
                int before = paths.Count;
                int key = (variant + 1) * 4;
                AddFace(paths, indices, keys, ResolveFace(font, declared.source, noSuffixes), declared.face, key);
                AddFace(paths, indices, keys, ResolveFace(font, declared.bold, noSuffixes), 0, key + 1);
                AddFace(paths, indices, keys, ResolveFace(font, declared.italic, noSuffixes), 0, key + 2);
                AddFace(paths, indices, keys, ResolveFace(font, declared.boldItalic, noSuffixes), 0, key + 3);
                if (paths.Count == before) continue;
                variant++;
                variantNames.Append('\n').Append(declared.name);
            }

            StringBuilder faces = new StringBuilder();
            for (int f = 0; f < paths.Count; f++)
                faces.Append(keys[f]).Append(':').Append(Path.GetFileName(paths[f])).Append(':').Append(indices[f]).Append('|');

            string baseName = Path.GetFileNameWithoutExtension(font.source);
            FontImportStamp wanted = new FontImportStamp()
            {
                source = font.source,
                face = font.face,
                sourceHash = HashFiles(paths.ToArray()),
                faces = faces.ToString(),
                charset = chars,
                glyphSize = font.glyphSize,
                importerVersion = importerVersion
            };

            XElement found = ReadStamp(Paths.Font, baseName, baseName + ".agd", baseName + "_atlas.adf");
            if (found != null && wanted.Matches(new FontImportStamp()
            {
                source = (string)found.Attribute("Source") ?? string.Empty,
                face = (int?)found.Attribute("Face") ?? 0,
                sourceHash = (string)found.Attribute("SourceHash") ?? string.Empty,
                faces = StampFaces(found),
                charset = (string)found.Attribute("Charset") ?? string.Empty,
                glyphSize = (int?)found.Attribute("GlyphSize") ?? 0,
                importerVersion = (int?)found.Attribute("ImporterVersion") ?? 0
            })) return true;

            Log.Info($"font import '{font.source}': baking {chars.Length} glyphs at {font.glyphSize}px across {paths.Count} face(s)...");
            ClearStamp(outputRoot, baseName);
            ImportFont(chars, baseName, paths.ToArray(), indices.ToArray(), keys.ToArray(), variantNames.ToString(), font.glyphSize, outputRoot);
            MathConstants math = MathConstants.Read(sourcePath, font.face);
            math?.Save(Path.Combine(outputRoot, baseName, baseName + ".math.axml"));
            XElement stamp = new XElement("FontImportStamp",
                new XAttribute("Family", FontCatalog.ReadFace(sourcePath, font.face)?.family ?? baseName),
                new XAttribute("Source", wanted.source),
                new XAttribute("Face", wanted.face),
                new XAttribute("SourceHash", wanted.sourceHash),
                new XAttribute("Charset", wanted.charset),
                new XAttribute("GlyphSize", wanted.glyphSize),
                new XAttribute("ImporterVersion", wanted.importerVersion));
            for (int f = 0; f < paths.Count; f++)
                stamp.Add(new XElement("Face", new XAttribute("Key", keys[f]), new XAttribute("Source", Path.GetFileName(paths[f])),
                    new XAttribute("Index", indices[f])));
            WriteStamp(outputRoot, baseName, stamp);
            return true;
        }

        private static readonly string[] noSuffixes = Array.Empty<string>();

        private static void AddFace(List<string> paths, List<int> indices, List<int> keys, string path, int index, int key)
        {
            if (path == null) return;
            paths.Add(path);
            indices.Add(index);
            keys.Add(key);
        }

        // A stamp's faces in the form FontImportStamp.faces compares.
        private static string StampFaces(XElement stamp)
        {
            StringBuilder faces = new StringBuilder();
            foreach (XElement face in stamp.Elements("Face"))
                faces.Append((string)face.Attribute("Key")).Append(':').Append((string)face.Attribute("Source")).Append(':')
                    .Append((string)face.Attribute("Index")).Append('|');
            return faces.ToString();
        }

        private static void ImportIconsIfStale(IconImport icons)
        {
            string[] files = VirtualFileSystem.EnumerateAll(icons.source, "*.svg")
                .OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
            if (files.Length == 0)
            {
                Log.Warn($"icon import '{icons.name}': no .svg under '{icons.source}', skipped.");
                return;
            }

            IconImportStamp wanted = new IconImportStamp()
            {
                source = icons.source,
                sourceHash = HashFiles(files),
                iconSize = icons.iconSize,
                importerVersion = importerVersion
            };

            XElement found = ReadStamp(Paths.Icon, icons.name, icons.name + ".aid", icons.name + "_atlas.adf");
            if (found != null && wanted.Matches(new IconImportStamp()
            {
                source = (string)found.Attribute("Source") ?? string.Empty,
                sourceHash = (string)found.Attribute("SourceHash") ?? string.Empty,
                iconSize = (int?)found.Attribute("IconSize") ?? 0,
                importerVersion = (int?)found.Attribute("ImporterVersion") ?? 0
            })) return;

            Log.Info($"icon import '{icons.name}': baking {files.Length} icons at {icons.iconSize}px...");
            ClearStamp(Paths.ICONS, icons.name);
            IconSet.GenerateIconAtlas(icons.name, files, icons.iconSize, Paths.ICONS);
            WriteStamp(Paths.ICONS, icons.name, new XElement("IconImportStamp",
                new XAttribute("Source", wanted.source),
                new XAttribute("SourceHash", wanted.sourceHash),
                new XAttribute("IconSize", wanted.iconSize),
                new XAttribute("ImporterVersion", wanted.importerVersion)));
        }

        // Names and contents both, so renaming an icon invalidates the bake the same as editing one.
        private static string HashFiles(string[] files)
        {
            using MemoryStream digest = new MemoryStream();
            foreach (string file in files)
            {
                byte[] name = System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(file));
                digest.Write(name, 0, name.Length);
                digest.Write(SHA256.HashData(Array.FindAll(File.ReadAllBytes(file), b => b != (byte)'\r')));
            }
            return Convert.ToHexString(SHA256.HashData(digest.ToArray()));
        }

        // Drops the stamp ahead of a bake.
        private static void ClearStamp(string root, string name)
        {
            string path = Path.Combine(root, name, name + ".import.axml");
            if (File.Exists(path)) File.Delete(path);
        }

        // A stamp only counts when the files it describes are actually present.
        private static XElement ReadStamp(Func<string, string, string> resolve, string name, params string[] outputs)
        {
            foreach (string output in outputs)
                if (!File.Exists(resolve(name, output))) return null;

            string stampPath = resolve(name, name + ".import.axml");
            return File.Exists(stampPath) ? XElement.Load(stampPath) : null;
        }

        private static void WriteStamp(string root, string name, XElement stamp)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            stamp.Save(Path.Combine(dir, name + ".import.axml"));
        }

        // Bakes the faces into one atlas; faceKeys and variantNames as GenerateGlyphAtlas takes them, the plain regular first.
        public static void ImportFont(string characters, string baseName, string[] paths, int[] faceIndices, int[] faceKeys,
            string variantNames, int glyphSize, string outputRoot)
        {
            Directory.CreateDirectory(Path.Combine(outputRoot, baseName));

            AuroraFont[] faces = new AuroraFont[paths.Length];
            Diagnostics.Profiling.Zone.Start("Atlas.ReadFaces");
            for (int i = 0; i < paths.Length; i++)
                faces[i] = ReadFace(characters, paths[i], faceIndices[i], baseName, outputRoot);
            Diagnostics.Profiling.Zone.End("Atlas.ReadFaces");

            AuroraFont.GenerateGlyphAtlas(faces, paths, faceIndices, faceKeys, variantNames, baseName, glyphSize, outputRoot);
        }

        // Reads one face's table directory and round-trips it through its own .afm.
        private static AuroraFont ReadFace(string characters, string facePath, int face, string baseName, string outputRoot)
        {
            var fs = new FileStream(facePath, FileMode.Open, FileAccess.Read);
            var reader = new BinaryReader(fs);
            fs.Position = FaceOffset(reader, face);

            AuroraFont font = new AuroraFont();

            font.fontMeta = new FontMeta
            {
                version = ReadUInt32BE(reader),  // Big-endian
                tableCount = ReadUInt16BE(reader)
            };

            // Skip other fields (searchRange, entrySelector, rangeShift)
            fs.Position += 6;
            font.tableEntries = new TableEntry[font.fontMeta.tableCount];
            for (int i = 0; i < font.fontMeta.tableCount; i++)
            {
                font.tableEntries[i] = new TableEntry
                {
                    name = new string(reader.ReadChars(4)),
                    checksum = ReadUInt32BE(reader),
                    offset = ReadUInt32BE(reader),
                    length = ReadUInt32BE(reader)
                };
            }

            font.textData = new TextData
            {
                characterCount = characters.Length,
                characters = characters.ToCharArray()
            };

            //font.headTableInfo = new HeadTableInfo();
            //reader.BaseStream.Position = font.tableEntries.First(t => t.name == "head").offset + 50;
            //font.headTableInfo.indexToLocFormat = ReadUInt16BE(reader); // 0 = uint16, 1 = uint32

            string path = Path.Combine(outputRoot, baseName, Path.GetFileNameWithoutExtension(facePath) + ".afm");

            Serializer.SerializeAttributed(font, path);
            reader.Dispose();
            reader.Close();
            fs.Dispose();
            fs.Close();

            AuroraFont f = new AuroraFont();
            Serializer.DeserializeAttributed(path, ref f);
            //f.Deserialize(path);
            return f;
        }

        // Start of a face's table directory; a .ttc lists one per face after its header.
        internal static uint FaceOffset(BinaryReader reader, int face)
        {
            reader.BaseStream.Position = 0;
            if (new string(reader.ReadChars(4)) != "ttcf") return 0;
            reader.BaseStream.Position = 12 + 4 * face;
            return ReadUInt32BE(reader);
        }

        internal static short ReadInt16BE(BinaryReader reader) =>
            BitConverter.ToInt16(reader.ReadBytes(2).Reverse().ToArray(), 0);

        internal static ushort ReadUInt16BE(BinaryReader reader) =>
            BitConverter.ToUInt16(reader.ReadBytes(2).Reverse().ToArray(), 0);

        internal static uint ReadUInt32BE(BinaryReader reader) =>
            BitConverter.ToUInt32(reader.ReadBytes(4).Reverse().ToArray(), 0);

    }
}
