# Decision — font variants: one atlas per family, faces as a list keyed variant × style

**Date:** 2026-10-30
**Scope:** `ArctisAurora.Core.Filing` — `AtlasMetaData`, `Glyph`, `AuroraFont.GenerateGlyphAtlas`, `FontCatalog`, `InstalledFamily`; `ArctisAurora.Core.Filing.Serialization` — `AssetImporter`, `FontImport`, `FontVariantImport`, `FontImportStamp`; `ArctisAurora.Core.UI` — `TextMeasurer`, `TextRunControl`, `Run`, `StyleDelta`, `CaretStyle`, `DocumentToolbarControl`, `FontLibrary`, `DrawRecorder`, `PdfExport`, `MathLayout`, `TexLowering`

## What changed
- A font import bakes every face of a family into one atlas: the plain family's Regular/Bold/Italic/BoldItalic plus every other variant installed under the same typographic family (Calibri Light and Light Italic under Calibri).
- The variant is a per-run property (`Variant="Light"` in the note XML), chosen from a Variant dropdown in the document toolbar right after the font button.
- `AtlasMetaData`: removed `hasBold`, `hasItalic`, `hasBoldItalic`, `styleCount`, `StyleBlock`, `Effective(FontStyle)`. Added serialized `int[] faceKeys` (one per baked face in atlas block order, value = variant * 4 + (int)style, face 0 = plain regular), `char[] variantNames` (names joined by '\n', variant 0 = plain family, empty name), `GlyphMetrics[] faceMetrics` (faces after the first: face f glyph i at (f-1)*glyphCount+i).
- `AtlasMetaData` built on load: private `int[] slots` (variant*4+style -> face or -1), public `string[] variants`. New: `faceCount`, `int Effective(int variant, FontStyle style)`, `bool HasFace(int variant, FontStyle style)`, `int VariantOf(string? name)` (linear; unknown/empty = 0), `GlyphMetrics Metrics(Glyph glyph, int face)`. `CellIndex(charIndex, face)`, `Kern(char, char, face)`, `TableAdvance(char, face)` take a face index. `ReadKerning(facePaths, faceIndices)` reads every baked face. `AuroraFont.GenerateGlyphAtlas(faces, facePaths, faceIndices, faceKeys, variantNames, baseName, perGlyphSize, outputRoot)`.
- `Glyph`: removed `bold`, `italic`, `boldItalic`, `Metrics(FontStyle)`, `SetMetrics`; `regular` stays as the first face's metrics; new non-serialized `int index` (set in `BuildCharIndex`).
- `FontImport` gains `List<FontVariantImport> variants` (XSD element `Variant`). New sealed XSD type `FontVariantImport`: `Name`, `Source` (upright face), `Face` (.ttc index of the upright), `Bold`, `Italic`, `BoldItalic`; every face taken as declared, empty = the variant lacks it.
- `FontImportStamp`: `boldSource`/`italicSource`/`boldItalicSource` replaced by `faces` (key:file:index|… in block order). `<name>.import.axml` lists `<Face Key Source Index/>` children instead of the three `*Source` attributes.
- `AssetImporter`: `importerVersion` 6 -> 7 (every font and icon set re-bakes once). `ImportFontIfStale` gathers the plain faces then each variant's faces (a variant with no resolvable face is skipped and gets no name). `ImportFont(characters, baseName, string[] paths, int[] faceIndices, int[] faceKeys, string variantNames, glyphSize, outputRoot)` replaces the four-path signature. `ParseImportSet` reads `<Variant>` children. New private `AddFace`, `StampFaces`, `noSuffixes`.
- `InstalledFamily` gains `InstalledFamily[] variants` (on the head) and `InstalledFamily? head` (on a variant). `FontCatalog`: `Face` record gains `typographic` (name ID 16, else ID 1); `FamilyName(reader, table, nameId)`; `Group` puts name-ID-1 families sharing a typographic name under the family named as it (the head must itself have typographic == its own name; variants sorted by name); `Families` lists heads only; `Find(name)` searches heads, then variants by their own name-ID-1 name, and returns the exact family. Catalog cache `FontCatalog.catalog.axml` gains a `Typographic` attribute; entries without it are re-read once.
- `TextMeasurer.Run`: `face` is `int`; constructor gains optional `int variant = 0`. `MeasuredRun.face` is int. `TextRunControl`: `StyleSpan.variant` (string); `BuildRuns` passes `VariantOf(span.variant)`; private `WriteGlyph` takes the resolved `int face`; math glyphs pass `Effective(0, g.face)`.
- `Run` (BlockControl.cs) XSD attribute `Variant`; `NoteModel` and `TextRunData` (`SameStyle`, `Runs()`) carry it. `StyleDelta.variant` (ctor param, `Of`, `Apply`, `With`, `Changes`), `CaretStyle.variant`.
- `DocumentToolbarControl`: `variantCaption`, `variantButtonWidth` (90), `PlainVariant` ("Regular"), `shownVariant`, `OpenVariants`, `internal static VariantEntries(editor)`. The menu lists Regular + the caret font's variants; picking applies `StyleDelta(variant: …)`; Regular clears it.
- `FontLibrary`: `VariantsOf(string asset)`; `Import`/`ImportNow` import `family.head ?? family`; `ImportOf` fills variants with names from private `VariantName` ("Calibri Light" under "Calibri" -> "Light"); `Register` writes `<Variant>` children into `UserFonts.imports.axml`; `Imported()` ranks by `faceCount`.
- `DrawRecorder.Glyph` and `DrawItem.face` take `int face`; `PdfExport.Face(FontAsset, int atlasFace)` reads the stamp's n-th `<Face>`. `MathLayout`, `TexLowering.Face` / x-height use `atlas.Metrics(glyph, atlas.Effective(0, style))`.
- `TexLowering.Imported(name)`: installed family (`FontCatalog.Find`) first, fontspec `Path=` files only when not installed.
- Tests: new `Fonts.CatalogVariants`, `Fonts.ImportVariants` (`FontPickerTests`, in AuroraEngine `Fonts.tests.axml`), `TextInput.FontVariant` (Thorium `TextInputTests`, in `TextInput.tests.axml`). Updated: FontTests, LatinModernTests, FontPickerTests (faceCount), MathTests, TexTests (Kern/TableAdvance take 0), AtlasPerfTests (ImportFont arrays). Golden `Ribbon.Docs.Format` re-approved (only the new Variant button).

## Why these choices

**One atlas per family holds all variants (user's model).**
A variant is a property of the text, not another font. Rejected (my first proposal): each variant as its own font asset ("Calibri Light" listed separately in the font list) — the user said it did not make sense.

**Face selection is a face index into the atlas, not a `FontStyle`.**
Text keeps the four-value `FontStyle` for bold/italic; variant × style picks the face. Rejected: a full weight/width axis replacing bold/italic — it touches every `FontStyle` use, the note format and every golden. The user picked scope "Import + by name", then refined it to this.

**Fallback: the variant's own face for the style, else the plain family's face for that style, else the variant's regular, else regular.**
So Light + italic = Light Italic; Light + bold = Calibri Bold (Light has no bold).

**Variant names come from the font: name ID 1 with the typographic (ID 16) family name stripped.**
"Calibri Light" -> "Light". Rejected: a fixed weight list.

**Metrics for faces after the first live in one flat `faceMetrics` array on the atlas.**
Not an array per glyph (aurora-code rule 8: no nested collections). Face 0 stays on `Glyph.regular` because the importer, MTSDFGen and PdfType3Font use it as each face's working metrics.

**Variant names are stored in the `.agd` (`variantNames`).**
A run resolves its face from the atlas alone, with no registry — same reason `TextMeasurer.Run` resolves from `AtlasMetaData`. Rejected: reading names from the import stamp.

**The variant is stored by name in the note and resolved to an index at measure time (`VariantOf`, linear, < 16 variants), not per frame.**

**The stamp lists faces as `<Face Key Source Index/>` children** so the PDF export finds each face's source file by block index.

**F1 (user: a): fontspec names resolve to the installed family first, `Path=` files only when not installed.**
Partly reverses L11a (`Path=` files first). Rejected (b): keep the L11a order — the thesis would keep importing its bundled Calibri-nm.ttf from wherever the document sits (how the %TEMP% import happened, see [[font-import-from-scratch-copy]]). Consequence: on this machine the thesis uses Windows' calibri.ttf, a different file from the thesis's Calibri-nm.ttf (sizes differ by ~2.5 KB), so heading widths and kerns may differ slightly from the LuaLaTeX reference.

**F2 (user: a): group by name ID 16 only; fonts lacking ID 16 stay separate as today.**
Rejected (b): guessing the family from the ID-1 prefix (e.g. "Arial Black" -> Arial) — a guess, and [[font-picker]] already rejected filename/name guessing.

**F3 (user: a): a run keeps its variant name when the font changes.**
A font without that variant draws its plain face; switching back restores the variant. Rejected (b): clearing the variant on a font change.

**This extends [[font-picker]] and supersedes [[bold-italic-face]].**
Family grouping gains an outer name-ID-16 layer (name ID 1 stays the inner layer); bold-italic as the fourth fixed face becomes a face list.

## Verification
- Test-verified and golden-verified. Builds clean.
- Midpoint (atlas/importer/measure/draw switched to int face, no variants anywhere): `_Build/test.sh` Fonts 7 passed, Tex 114, TextInput 99, Math 18, LatinModern 17 — each with 1 failure, the pre-existing Boot ("failed to load the default sampler asset"); goldens pixel-identical after the re-bake.
- End: full Thorium `--test` 463 passed, 5 failed, 1 skipped. Failures: Boot, Sheet.PasteLinks, Sheet.NotePasteLinks, Sheet.MathLinks (all pre-existing) and golden Ribbon.Docs.Format, which differed only by the new Variant button (inspected, re-approved with `--test-approve`). New tests Fonts.CatalogVariants, Fonts.ImportVariants, TextInput.FontVariant pass (Fonts 9 passed + Perf.AtlasBake skipped; TextInput 100 passed).
- Shot-verified (text): the user's Calibri import removed, thesis exported headless (`Sablonas.tex --exec "Wait 6000; Focus; Tex.ExportPdf; Wait 8000; Quit"`, exit 0, no `[Vulkan]` lines): Calibri imported from C:\WINDOWS\Fonts as one 6-face atlas with a Light variant, PDF 35 pages, heading text (SANTRAUKA, SANTRUMPŲ…) present, no "left out" warnings.
- NOT GUI-verified: the Variant dropdown was never clicked in the app, and a Light run was never looked at on screen.
- Heading positions against the reference PDF were NOT re-measured (pypdfium2 not available in the session). Not perf-measured.

## Known gaps
- The re-bake wiped `latinmodern-math.math.axml`'s hand-set TFM values a third time (Sub1…BigOpSpacing5 written as 0; Tex.ClassicScripts failed); restored by hand to Sub1="0.15" Sub2="0.247217" Sup1="0.412892" Sup2="0.362892" Sup3="0.288889" SubDrop="0.05" SupDrop="0.386108" BigOpSpacing5="0.1". The WIP item for a durable home stays open.
- fontspec / `\fontseries` cannot choose a variant; `\setmainfont{Calibri Light}` now draws plain Calibri (finds the variant, imports its family, returns the family's asset).
- Variable fonts not handled; faces after index 0 in a .ttc are still skipped for styled faces.
- The variant is not carried into Markdown export.
- The old `Fonts/Calibri-nm` bake folder under %LOCALAPPDATA%\Arktis\Data\Fonts is left unused (not deleted).
- Committed atlases in AuroraEditor, Carbon and AuroraEngine data re-bake on their own Debug boot; until then their old `.agd` files do not load with the new layout (format changed, importerVersion 7).
- The dead `AtlasMetaData.Deserialize(name)` legacy reader was updated only enough to compile (discards the old bold/italic metrics).
- The toolbar shows the stored variant name even when the current font lacks it (it draws the plain face).

Related: [[font-picker]], [[bold-italic-face]], [[latex-editor]], [[font-import-from-scratch-copy]], [[asset-manifest-and-import]]
