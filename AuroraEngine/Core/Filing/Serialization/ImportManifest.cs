using ArctisAurora.Core.Registry;

namespace ArctisAurora.Core.Filing.Serialization
{
    [A_XSDType("Charset", "AssetRegistry")]
    public class Charset
    {
        [A_XSDElementProperty("Name", "AssetRegistry")]
        public string name { get; set; } = string.Empty;

        [A_XSDElementProperty("Chars", "AssetRegistry")]
        public string chars { get; set; } = string.Empty;
    }

    [A_XSDType("FontImport", "AssetRegistry")]
    public class FontImport
    {
        [A_XSDElementProperty("Source", "AssetRegistry")]
        public string source { get; set; } = string.Empty;

        // index into a .ttc collection; 0 for a single-face file
        [A_XSDElementProperty("Face", "AssetRegistry")]
        public int face { get; set; }

        // taken as named, unchecked; left empty the importer probes the usual suffixes
        [A_XSDElementProperty("Bold", "AssetRegistry")]
        public string bold { get; set; } = string.Empty;

        [A_XSDElementProperty("Italic", "AssetRegistry")]
        public string italic { get; set; } = string.Empty;

        [A_XSDElementProperty("BoldItalic", "AssetRegistry")]
        public string boldItalic { get; set; } = string.Empty;

        // one charset name, or several separated by commas
        [A_XSDElementProperty("Charset", "AssetRegistry")]
        public string charset { get; set; } = string.Empty;

        [A_XSDElementProperty("GlyphSize", "AssetRegistry")]
        public int glyphSize { get; set; } = 64;

        // bakes only the charset's characters the face maps
        [A_XSDElementProperty("Covered", "AssetRegistry")]
        public bool covered { get; set; }

        // the family's other variants, baked into the same atlas
        [A_XSDElementProperty("Variant", "AssetRegistry")]
        public List<FontVariantImport> variants { get; set; } = new List<FontVariantImport>();
    }

    // One other variant of a family (Light, Black…) and its faces, each taken as declared; an empty face is one it lacks.
    [A_XSDType("FontVariantImport", "AssetRegistry")]
    public sealed class FontVariantImport
    {
        [A_XSDElementProperty("Name", "AssetRegistry")]
        public string name { get; set; } = string.Empty;

        [A_XSDElementProperty("Source", "AssetRegistry")]
        public string source { get; set; } = string.Empty;

        // index into a .ttc collection for the upright face
        [A_XSDElementProperty("Face", "AssetRegistry")]
        public int face { get; set; }

        [A_XSDElementProperty("Bold", "AssetRegistry")]
        public string bold { get; set; } = string.Empty;

        [A_XSDElementProperty("Italic", "AssetRegistry")]
        public string italic { get; set; } = string.Empty;

        [A_XSDElementProperty("BoldItalic", "AssetRegistry")]
        public string boldItalic { get; set; } = string.Empty;
    }

    [A_XSDType("IconImport", "AssetRegistry")]
    public class IconImport
    {
        // folder of .svg files, relative to a Data root
        [A_XSDElementProperty("Source", "AssetRegistry")]
        public string source { get; set; } = string.Empty;

        [A_XSDElementProperty("Name", "AssetRegistry")]
        public string name { get; set; } = string.Empty;

        [A_XSDElementProperty("IconSize", "AssetRegistry")]
        public int iconSize { get; set; } = 64;
    }

    [A_XSDType("ImportSet", "AssetRegistry")]
    public class ImportSet
    {
        [A_XSDElementProperty("Charset", "AssetRegistry")]
        public List<Charset> charsets { get; set; } = new List<Charset>();

        [A_XSDElementProperty("FontImport", "AssetRegistry")]
        public List<FontImport> fonts { get; set; } = new List<FontImport>();

        [A_XSDElementProperty("IconImport", "AssetRegistry")]
        public List<IconImport> icons { get; set; } = new List<IconImport>();
    }

    // Written beside a font's cooked output; a bake is skipped when it still matches the declaration.
    public class FontImportStamp
    {
        public string source = string.Empty;
        public int face;
        public string sourceHash = string.Empty;

        // every baked face as key:file:index, in block order
        public string faces = string.Empty;
        public string charset = string.Empty;
        public int glyphSize;
        public int importerVersion;

        public bool Matches(FontImportStamp other) =>
            source == other.source
            && face == other.face
            && sourceHash == other.sourceHash
            && faces == other.faces
            && charset == other.charset
            && glyphSize == other.glyphSize
            && importerVersion == other.importerVersion;
    }

    // Same contract for an icon set; sourceHash covers every .svg in the folder, names included.
    public class IconImportStamp
    {
        public string source = string.Empty;
        public string sourceHash = string.Empty;
        public int iconSize;
        public int importerVersion;

        public bool Matches(IconImportStamp other) =>
            source == other.source
            && sourceHash == other.sourceHash
            && iconSize == other.iconSize
            && importerVersion == other.importerVersion;
    }
}
