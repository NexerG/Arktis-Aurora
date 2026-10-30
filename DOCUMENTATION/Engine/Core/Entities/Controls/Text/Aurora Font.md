---
date: 2026-05-30
Status: Current
tags:
  - d_UI
  - d_text
  - d_Font
cssclasses:
  - Aurora.css
Linker:
  - "[[Arctis Aurora]]"
System:
Class:
  - "[[Aurora Font]]"
Parent Class:
Interfaces:
  - "[[IDeserialize]]"
Used by:
Type:
  - Public
Attributes:
  - Serializable
Namespace: ArctisAurora.Core.Filing
SourceFile: AuroraEngine/Core/Filing/AuroraFont.cs
VerifiedAgainst: 2026-05-30
---
## Description

Parses a TrueType (`.ttf`) or CFF (`.otf`, Type 2 charstrings) font and bakes an **MTSDF** glyph atlas. Two roles: (1) a serializable container for the font's table directory + character set (loaded from `.afm`), and (2) the static atlas generator that reads the `.ttf` tables, reconstructs glyph outlines, colors their edges, and renders a multi-channel-plus-true-distance atlas PNG along with the `.agd` metadata ([[Atlas Meta Data]]).

> Text rendering is an active WIP area. The pipeline now produces **MTSDF** (RGB edge channels + an alpha "true distance" channel), having moved on from plain MSDF.

## API summary

| Member | Kind | Summary |
| --- | --- | --- |
| `Deserialize(string path)` | public | Load `.afm` â†’ `FontMeta`, the `TableEntry[]` directory, and the char set. |
| `GenerateGlyphAtlas(AuroraFont fontData, string fontName, int perGlyphSize)` | static | Bake the atlas + `.agd` from a system `.ttf`. |

## Fields & Properties

```C#
[StructLayout(Sequential, Pack=1), @Serializable] public struct FontMeta   { uint version; ushort tableCount; }
[StructLayout(Sequential, Pack=1), @Serializable] public struct TableEntry { string name; uint checksum, offset, length; }
[StructLayout(Sequential, Pack=1), @Serializable] public struct TextData   { int characterCount; char[] characters; }

[@Serializable] public FontMeta fontMeta;
[@Serializable] public TableEntry[] tableEntries;
[@Serializable] public TextData textData;
```

## Methods

### `Deserialize` *(public)*
Reads the `FontMeta`, the `TableEntry` directory, and the character list from the `.afm` binary.

### `GenerateGlyphAtlas` *(static)*
Reads the `.ttf` from the **system fonts folder** (`Environment.SpecialFolder.Fonts`), then:
	`maxp` â†’ glyph count
	`head` â†’ `unitsPerEm` + `indexToLocFormat`
	`loca` (TrueType only) â†’ glyph offsets
	per character: `cmap` (`GetGlyphIndex`) â†’ `glyf` (`GetGlyphOutline`) or `CFF ` (`GetCffGlyph`) â†’ a [[Glyph]] of bezier contours
	`hhea`/`hmtx` â†’ `advanceWidth` + left side bearings
	derive `tsb`
	serialize the [[Atlas Meta Data]] â†’ `.agd`
	render each glyph cell via `GenerateMTSDF`
	save `{font}_atlas.png` (plus a debug `SDF_A.png`)

## Helpers

- `GetGlyphIndex` â€” `cmap` format-4 segmented lookup (char â†’ glyph index).
- `GetGlyphOutline` â€” parse `glyf` contours into beziers, `BuildEdges`, then assign MSDF **edge colors** (R/G/B) by corner detection so channels can be combined.
- `GenerateMTSDF` â€” per pixel: three per-channel signed distances + a true distance, packed into RGBA.
- `GetCffGlyph` — a font with a `CFF ` table: `CffOutlines.Read` reads the CharStrings, global and local subr INDEXes, `CffOutlines.Interpret` runs the Type 2 charstring into beziers (cubic controls flagged `isCubicControl`), `CffOutlines.Bounds` takes the exact ink box including cubic extrema, then `Normalise`.
- `Normalise` — shared by `GetGlyphOutline` and `GetCffGlyph`: unit-box normalisation, `BuildEdges`, `MTSDFGen.ColorEdges`.
- Empty glyph — a glyph with no contours takes `glyphHeight = lineHeight` and `glyphWidth = advanceWidth`; a glyph exactly one em tall or wide (LM Math ∑, √) is a normal glyph, not empty.
- Font source lookup — `AssetImporter.ResolveSystemFont` probes the machine font folder, the per-user font folder, then `<repo>/_Build/FontSources/` (gitignored). Fonts bake only in Debug; without the source the committed atlas is kept and a Warn is logged.
- `GetClosestDistanceOfChannel` Â· `ClosestTOnBezier` (coarse sample + Newton refinement) Â· `ComputeWindingNumber` (ray-cast via quadratic roots) Â· `SolveCubic` / `SolveQuadratic`.

## Font picker and runtime import

The note format bar (`DocumentToolbarControl`) has a font button. Its list holds the imported fonts by family name, each drawn in its own font, and below "More fonts" every installed system family not yet imported, its name drawn in its own face. Picking an unimported family bakes it at runtime behind an "Importing <family>, please wait" window, then registers and applies it. CJK is not supported; it needs on-demand glyph paging and is recorded as missing.

`FontCatalog` scans the OS font folders and groups faces into `InstalledFamily` records by OpenType name ID 1 (the legacy family), with bold and italic read from the OS/2 `fsSelection` bits. Name ID 1 holds at most Regular, Bold, Italic and Bold Italic, which is exactly the engine's four faces. The result is cached in `FontCatalog.catalog.axml` under the user data folder.

`FontPreviews` draws each family's name in its own face from one shared atlas. The glyphs of every name are remapped to private-use codepoints U+E000–U+F8FF, so the ordinary text path draws them. A family whose face lacks a character of its own name gets no preview and is listed in the default font. The previews bake on a background task after boot, and the list shows "Finding installed fonts…" until they are registered.

Runtime imports go to `Paths.USER_DATA` (`%LOCALAPPDATA%\Arktis\Data`), a per-user mount added last in every build, so it has the lowest priority. A font imported once is usable from every app, including shipped builds whose data is a read-only archive.

```
FontLibrary.Import(family, done)
	if importing then return
	if family is already imported then done(its asset), return
	FontImportWindow.Show(family)
	on a worker thread
		AssetImporter.ImportFontIfStale(font, charsets, USER_DATA/Fonts)
	post to main
		if the bake failed then FontImportWindow.Fail(error), return
		FontImportWindow.Hide()
		write UserFonts.imports.axml and UserFonts.assets.axml
		register the FontAsset and upload its texture
		done(asset)
```

Language packs are charsets joined by commas in the `<Fonts><LanguagePacks Packs="Latin"/>` setting. Runtime-imported fonts bake only the characters their face maps (`Covered`). Changing the setting re-bakes the user fonts, and the new atlases load on the next start.

## Faces and variants

A baked atlas holds a list of faces, not a fixed four. Each face has a key, `variant * 4 + (int)FontStyle`, stored in `faceKeys` in atlas block order; face 0 is the plain family's Regular. Variant 0 is the plain family (empty name); the other variants (Light, …) are named in `variantNames`, joined by newlines, so a run resolves its face from the atlas alone. Face 0's glyph metrics sit on `Glyph.regular`; the metrics of the later faces sit in one flat `faceMetrics` array, face f glyph i at (f-1)*glyphCount+i.

A font import bakes the plain family's Regular, Bold, Italic and Bold Italic plus every other variant installed under the same typographic family (OpenType name ID 16), so Calibri Light and Light Italic land in the Calibri atlas. A run carries a `Variant` name and a `FontStyle`; the face comes from the fallback rule.

```
AtlasMetaData.Effective(variant, style)
	if the variant has a face for the style then return it
	if the plain family has a face for that style then return it
	if the variant has a regular face then return it
	return the plain regular face
```

So Light + italic is Light Italic, and Light + bold is Calibri Bold because Light has no bold. `VariantOf(name)` turns the stored name into an index (linear search; unknown or empty is 0). A font without the run's variant draws its plain face.

The import stamp `<name>.import.axml` lists one `<Face Key Source Index/>` child per baked face in block order, which is how the PDF export finds each face's source file. The `FontImport` data lists variants as `<Variant Name Source Face Bold Italic BoldItalic/>` children. Decision: [[font-variants]].

## Related
- [[Glyph]] â€” the outline/metrics type produced here
- [[Atlas Meta Data]] â€” the serialized atlas metadata
- [[IDeserialize]] Â· [[Serializer]]
