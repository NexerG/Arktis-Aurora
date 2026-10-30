using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Registry.Assets;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.EngineWork.Registry;
using System.Xml.Linq;

namespace ArctisAurora.Tests
{
    internal static class FontPickerTests
    {
        private const int importTicks = 36000;

        [A_XSDActionDependency("Fonts.CatalogGroups", "Test")]
        private static IEnumerator<int> CatalogGroups(TestContext t)
        {
            FontCatalog.Scan();
            InstalledFamily? arial = FontCatalog.Find("Arial");
            if (arial == null)
            {
                t.skipped = "Arial is not installed";
                yield break;
            }

            t.Check(Named(arial.regular, "arial.ttf") && Named(arial.bold, "arialbd.ttf") && Named(arial.italic, "ariali.ttf")
                && Named(arial.boldItalic, "arialbi.ttf"), "Arial's four files group into one family");

            InstalledFamily? black = FontCatalog.Find("Arial Black");
            if (black != null) t.Check(!ReferenceEquals(black, arial) && Named(black.regular, "ariblk.ttf"), "Arial Black stays its own family");

            InstalledFamily? math = FontCatalog.Find("Cambria Math");
            InstalledFamily? cambria = FontCatalog.Find("Cambria");
            if (math != null && cambria != null)
                t.Check(Named(math.regular, "cambria.ttc") && math.regularFace == 1 && cambria.regularFace == 0,
                    "the second face of cambria.ttc is found as Cambria Math");
        }

        [A_XSDActionDependency("Fonts.CatalogVariants", "Test")]
        private static IEnumerator<int> CatalogVariants(TestContext t)
        {
            FontCatalog.Scan();
            InstalledFamily? calibri = FontCatalog.Find("Calibri");
            InstalledFamily? light = FontCatalog.Find("Calibri Light");
            if (calibri == null || light == null)
            {
                t.skipped = "Calibri or Calibri Light is not installed";
                yield break;
            }

            t.Check(ReferenceEquals(light.head, calibri) && Array.IndexOf(calibri.variants, light) >= 0, "Calibri Light is a variant of Calibri");
            t.Check(Named(light.regular, "calibril.ttf") && Named(light.italic, "calibrili.ttf"), "the variant keeps its own upright and italic");

            bool listed = false;
            foreach (InstalledFamily family in FontCatalog.Families!)
                if (ReferenceEquals(family, light)) listed = true;
            t.Check(!listed, "the picker lists Calibri once, not its variants");
        }

        [A_XSDActionDependency("Fonts.ImportVariants", "Test")]
        private static IEnumerator<int> ImportVariants(TestContext t)
        {
            FontCatalog.Scan();
            InstalledFamily? calibri = FontCatalog.Find("Calibri");
            InstalledFamily? light = FontCatalog.Find("Calibri Light");
            if (calibri == null || light == null)
            {
                t.skipped = "Calibri or Calibri Light is not installed";
                yield break;
            }

            string root = Path.Combine(Paths.USER_DATA, "VariantTest");
            FontImport import = new FontImport
            {
                source = calibri.regular,
                bold = calibri.bold ?? string.Empty,
                italic = calibri.italic ?? string.Empty,
                boldItalic = calibri.boldItalic ?? string.Empty,
                charset = "Base",
                glyphSize = 32,
                covered = true
            };
            import.variants.Add(new FontVariantImport { name = "Light", source = light.regular, italic = light.italic ?? string.Empty });
            t.Check(AssetImporter.ImportFontIfStale(import, new Dictionary<string, string> { ["Base"] = "AB" }, root), "the import bakes");

            string baseName = Path.GetFileNameWithoutExtension(calibri.regular);
            AtlasMetaData atlas = new AtlasMetaData();
            Serializer.DeserializeAttributed(Path.Combine(root, baseName, baseName + ".agd"), ref atlas);
            atlas.BuildCharIndex();
            int variant = atlas.VariantOf("Light");
            t.Check(atlas.faceCount == 6 && variant == 1, $"one atlas holds Calibri's four faces and Light's two: {atlas.faceCount} faces, Light is {variant}");
            t.Check(atlas.HasFace(variant, FontStyle.Italic) && atlas.Effective(variant, FontStyle.Italic) != atlas.Effective(0, FontStyle.Italic),
                "Light Italic is a face of its own");
            t.Check(atlas.Effective(variant, FontStyle.Bold) == atlas.Effective(0, FontStyle.Bold), "Light has no bold, so bold draws Calibri Bold");

            Glyph a = atlas.GetGlyph('A');
            t.Check(atlas.Metrics(a, atlas.Effective(variant, FontStyle.Regular)).advanceWidth != a.regular.advanceWidth, "Light's A is not Calibri's");
            int stamped = 0;
            foreach (XElement _ in XElement.Load(Path.Combine(root, baseName, baseName + ".import.axml")).Elements("Face")) stamped++;
            t.Check(stamped == 6, $"the stamp lists every face: {stamped}");
        }

        [A_XSDActionDependency("Fonts.StyleDeltaFont", "Test")]
        private static IEnumerator<int> StyleDeltaFont(TestContext t)
        {
            StyleSpan span = new StyleSpan { count = 3 };
            StyleDelta georgia = new StyleDelta(fontName: "georgia");

            t.Check(georgia.Changes(span), "a font the span lacks is a change");
            georgia.Apply(ref span);
            t.Check(span.fontName == "georgia" && !georgia.Changes(span), "applying sets the span's font");
            t.Check(StyleDelta.Of(span).fontName == "georgia", "a span's look carries its font");
            t.Check(new StyleDelta(bold: true).With(georgia).fontName == "georgia", "a laid-over font wins");

            new StyleDelta(fontName: string.Empty).Apply(ref span);
            t.Check(span.fontName == null, "an empty font clears the span's own");
            yield break;
        }

        [A_XSDActionDependency("Fonts.LanguagePackCovered", "Test")]
        private static IEnumerator<int> LanguagePackCovered(TestContext t)
        {
            string? arial = AssetImporter.ResolveSystemFont("arial.ttf");
            if (arial == null)
            {
                t.skipped = "Arial is not installed";
                yield break;
            }

            string root = Path.Combine(Paths.USER_DATA, "PackTest");
            Dictionary<string, string> charsets = new Dictionary<string, string> { ["Base"] = "AB", ["Greek"] = "αβ", ["Thai"] = "กข" };
            FontImport import = new FontImport { source = arial, charset = "Base, Greek,Thai", glyphSize = 32, covered = true };
            t.Check(AssetImporter.ImportFontIfStale(import, charsets, root), "the import bakes");

            AtlasMetaData atlas = new AtlasMetaData();
            Serializer.DeserializeAttributed(Path.Combine(root, "arial", "arial.agd"), ref atlas);
            string chars = new string(atlas.chars);
            t.Check(chars == "ABαβ", $"the packs join in order and only the characters Arial maps are baked (got '{chars}')");
        }

        [A_XSDActionDependency("Fonts.Previews", "Test")]
        private static IEnumerator<int> Previews(TestContext t)
        {
            FontCatalog.Scan();
            InstalledFamily? arial = FontCatalog.Find("Arial");
            InstalledFamily? georgia = FontCatalog.Find("Georgia");
            if (arial == null || georgia == null)
            {
                t.skipped = "Arial or Georgia is not installed";
                yield break;
            }

            string root = Path.Combine(Paths.USER_DATA, "PreviewTest");
            FontPreviews.Bake(new[] { arial, georgia }, root);
            string? arialText = FontPreviews.TextFor("Arial");
            string? georgiaText = FontPreviews.TextFor("Georgia");
            t.Check(arialText?.Length == 5 && georgiaText?.Length == 7, "each family's name is spelt one codepoint per character");
            t.Check(arialText != null && georgiaText != null && (arialText + georgiaText).All(c => c >= '' && c <= '')
                && !arialText.Intersect(georgiaText).Any(), "names spell in private-use codepoints no two families share");
            if (arialText == null || georgiaText == null) yield break;

            AtlasMetaData previews = new AtlasMetaData();
            Serializer.DeserializeAttributed(Path.Combine(root, FontPreviews.AssetName, FontPreviews.AssetName + ".agd"), ref previews);
            previews.BuildCharIndex();
            AtlasMetaData arialAtlas = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))["default"].atlasMetaData;

            float previewA = previews.GetGlyph(arialText[0]).regular.advanceWidth;
            float previewG = previews.GetGlyph(georgiaText[0]).regular.advanceWidth;
            t.Check(previewA == arialAtlas.GetGlyph('A').regular.advanceWidth, "Arial's preview A is Arial's own A");
            t.Check(previewG != arialAtlas.GetGlyph('G').regular.advanceWidth, "Georgia's preview G is not Arial's");

            FontPreviews.Bake(new[] { arial, georgia }, root);
            t.Check(FontPreviews.TextFor("Arial") == arialText, "an unchanged catalog reloads the same spelling");
        }

        [A_XSDActionDependency("Fonts.ImportFamily", "Test")]
        private static IEnumerator<int> ImportFamily(TestContext t)
        {
            FontCatalog.Scan();
            InstalledFamily? georgia = FontCatalog.Find("Georgia");
            if (georgia == null)
            {
                t.skipped = "Georgia is not installed";
                yield break;
            }

            string? asset = null;
            FontLibrary.Import(georgia, a => asset = a);
            t.Check(FontImportWindow.isOpen && FontLibrary.importing, "the wait window is up while the font bakes");
            for (int i = 0; i < importTicks && asset == null; i++) yield return 1;

            t.Check(asset != null && !FontImportWindow.isOpen, "the import finishes and the window closes");
            if (asset == null) yield break;

            Dictionary<string, FontAsset> fonts = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset));
            t.Check(fonts.TryGetValue(asset, out FontAsset? font) && font.family == "Georgia" && font.atlasMetaData.faceCount == 4,
                "Georgia is registered with its four faces");
            t.Check(FontLibrary.AssetFor("Georgia") == asset, "the picker now lists Georgia as imported");

            string imports = File.ReadAllText(Path.Combine(Paths.USER_DATA, "XML", "Imports", "UserFonts.imports.axml"));
            string assets = File.ReadAllText(Path.Combine(Paths.USER_DATA, "XML", "Assets", "UserFonts.assets.axml"));
            t.Check(imports.Contains(georgia.regular) && assets.Contains($"Name=\"{asset}\""), "the import is recorded for the next start");

            string? again = null;
            FontLibrary.Import(georgia, a => again = a);
            t.Check(again == asset && !FontImportWindow.isOpen, "importing it again hands back the same asset at once");

            LabelControl label = new LabelControl { text = "Georgia Quick", fontName = asset, fontSize = 24 };
            t.Show(label);
            yield return 2;
            yield return t.Golden("Label", label);
        }

        private static bool Named(string? path, string file) =>
            path != null && string.Equals(Path.GetFileName(path), file, StringComparison.OrdinalIgnoreCase);
    }
}
