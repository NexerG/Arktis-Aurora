# Decision — bold-italic is a fourth baked face, not a synthesised one

**SUPERSEDED 2026-10-30 by [[font-variants]]:** faces are now a list keyed variant*4+style, selected by an int face index; the fixed four faces (`Glyph.bold/italic/boldItalic`, `AtlasMetaData.StyleBlock`, `hasBold…`) no longer exist.

**Date:** 2026-09-04
**Scope:** `ArctisAurora.Core.UISystem` — `FontStyle`, `Glyph`, `AtlasMetaData`, `AuroraFont.GenerateGlyphAtlas`;
`ArctisAurora.Core.Filing.Serialization` — `AssetImporter`, `FontImport`, `FontImportStamp`;
`ArctisAurora.Core.UISystem.Controls.Text.Editing.TextInputControl`;
`ArctisAurora.Core.UISystem.Controls.Text.Document.TextMeasurer`

## What changed

- `FontStyle` gains `BoldItalic`, appended so the existing three keep their ordinals.
- `Glyph` carries a fourth `GlyphMetrics` set, `boldItalic`, and a new `SetMetrics(FontStyle, GlyphMetrics)` —
  the inverse of `Metrics`, which is what the atlas fold loop needs.
- `AtlasMetaData` gains `hasBoldItalic`; `styleCount`, `Effective`, `StyleBlock` extend to four blocks.
- `GenerateGlyphAtlas` takes `FontStyle[] faceStyles` in place of `bool hasBold, bool hasItalic`.
- `AssetImporter` probes `boldItalicSuffixes = { "bi", "z", "-BoldItalic", "BoldItalic" }`, hashes the fourth
  face into `SourceHash`, and stamps `BoldItalicSource`. `FontImport` gains a declared `BoldItalic` attribute.
- `importerVersion` 3 → 4.
- Both places that derived a style from the two booleans — `TextInputControl.glyphStyle` and
  `TextMeasurer.MeasureBlock(ContentBlock, …)` — now yield `BoldItalic` when both are set. The old
  "bold wins over italic" comment is gone with the behaviour it described.

## Why these choices

**Nothing above the `.agd` needed touching, because the atlas was already N-face generic.**
`CellIndex`/`cellCount` compute the UV grid from `styleCount`, so a fourth block is arithmetic, not a format
the shader knows about. No shader, no descriptor, no `GlyphControl` change. The three things that capped it at
three faces were the enum, `Glyph`'s three metric fields, and two hardcoded ternaries.

**A missing bold-italic face falls back to `Regular`, not to `Bold`.** (user, 2026-09-04)
The alternative — degrade to bold, else italic, else regular — preserves *something* of the author's intent and
keeps the pre-change behaviour for single-face families. Rejected: falling back to `Bold` silently claims a
weight the family does not have for this style, and `Effective` already reads as "a style the family has no
face for draws as regular". Adding a two-level cascade under that sentence would make one style behave
unlike the other two for no stated reason.

**Face order is data carried alongside the faces, not recomputed from flags.**
The old fold loop inferred a face's style from its index (`hasBold && f == 1`), which is a two-case trick that
does not extend — with four faces the index-to-style map has six shapes. `ImportFont` already walks the paths
in block order, so it now builds a parallel `FontStyle[]` and `GenerateGlyphAtlas` derives the `has*` flags
from that. One array replaces what would have been a fourth `bool` parameter beside a fourth path parameter.

**`importerVersion` had to be bumped, and not for hygiene.**
`Serializer.DeserializeAttributed` is positional reflection over fields. A fourth `GlyphMetrics` on `Glyph` and
a third `bool` on `AtlasMetaData` change the `.agd` layout, so every previously baked atlas would be misread —
the stamp's own fields would not have caught it for a family with no new face (Electrolize's `SourceHash` and
`BoldItalicSource` are both unchanged).

**`z` is in the probe list because Windows uses two conventions.**
`arialbi`, `timesbi`, `courbi` take `bi`; `calibriz`, `verdanaz`, `segoeuiz` take `z`. Probing is `File.Exists`
against the system font folders, so a suffix that matches nothing costs nothing.

## Known gaps

- **Only Thorium's `Data/` was re-baked.** `AuroraEngine/`, `Carbon/` and `AuroraEditor/` still hold
  `ImporterVersion="3"` three-face atlases and re-bake on their next debug launch, which is the existing
  per-app import behaviour ([[asset-manifest-and-import]] §7). A *release* build against those committed
  atlases would misread the `.agd`, since release never imports — no such build exists today.
- `AtlasMetaData.Deserialize`, the hand-rolled `BinaryReader` path, was left untouched and now reads three
  metric sets and two bools. It has been unreferenced and misaligned with the writer since `FontAsset.Load`
  moved to `Serializer.DeserializeAttributed`; extending dead code was not worth it. Still a deletion
  candidate.
- The bake cost is now ~13 s × 4 faces for arial, ~80 s, once per app. Debug-only, gated on `Engine.isDebug`.
- Four faces per family is again a hardcoded ceiling — the enum, `Glyph`'s fields and `ImportFont`'s path list
  all name them. A family with condensed or multi-weight faces needs the metrics to become an indexed array,
  not a fifth field.

Related: [[asset-manifest-and-import]], [[text-styling-types]], [[document-format-bar]], [[atlas-is-unorm-not-srgb]]
