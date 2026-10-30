# Decision — font picker: installed fonts imported on demand, names drawn in their own face

**Date:** 2026-10-21
**Scope:** `ArctisAurora.Core.Filing` — `FontCatalog`, `InstalledFamily`, `FontPreviews`, `Paths` (`USER_DATA`, `RedirectUserData`), `VirtualFileSystem.Replace`; `ArctisAurora.Core.Filing.Serialization.AssetImporter`; `ArctisAurora.Core.UI` — `FontLibrary`, `FontImportWindow`, `FontSettings`, `LanguagePacksSetting`, `DocumentToolbarControl`, `StyleDelta`, `CaretStyle`, `BlockControl`

## What changed
- `FontCatalog` (`Scan`, `Families`, `Find`, `ReadFace`, internal `ReadTables`) lists installed families as `InstalledFamily` (name, regular, regularFace, bold, italic, boldItalic). Cache `<USER_DATA>/Fonts/FontCatalog.catalog.axml` (`<FontCatalog><Face Path Index Modified Family Bold Italic/>`).
- `FontPreviews` (`AssetName = "font-previews"`, `Bake(families, outputRoot)`, `TextFor(family)`) writes `font-previews.agd`, `font-previews_atlas.png`, `font-previews.map.axml` (Hash + `<Preview Family Text/>`).
- `FontLibrary`: bootstrap action `Fonts.StartCatalog` (after `AssetRegistries.PrepareAllAssets` in `Bootstrap.bootstrap.axml`), `LoadCatalog`, `RegisterPreviews`, `Imported`, `PreviewOf`, `FamilyOf`, `AssetFor`, `Import(family, done)`, `importing`, settings action `Fonts.Rebake`. Writes `<USER_DATA>/XML/Imports/UserFonts.imports.axml` and `<USER_DATA>/XML/Assets/UserFonts.assets.axml`.
- `FontImportWindow` (`Show`, `Fail`, `Hide`, `isOpen`) — a menu window like `PasswordWindow`.
- `Paths.USER_DATA` = `%LOCALAPPDATA%\Arktis\Data`, mounted last (lowest priority) in every build; `RedirectUserData(root)`; `VirtualFileSystem.Replace(mount, with)`; `TestRunner.Arm` redirects it to `<host>/TestUserData`, emptied each run.
- `AssetImporter`: `ImportFontIfStale(font, charsets, outputRoot)` internal, returns bool; `RunImports` bakes sets under USER_DATA into `UserFonts` (`<USER_DATA>/Fonts`), all others into `Paths.FONTS`; new `Charsets()`, private `ResolveChars` (comma list), `Covered`; `ResolveSystemFont` accepts an absolute path; stamps carry `Family`.
- `FontImport.covered` (`Covered`) and `Charset` accept comma-separated names. `FontAsset.family` = stamp Family, else installed source name table, else asset name. `AuroraFont.GetGlyphIndex` is internal.
- `StyleDelta.fontName` (ctor, Apply/With/Changes/Of; "" clears), `CaretStyle.fontName` (resolved through the block), `BlockControl.FontAt(span)`.
- `DocumentToolbarControl`: font caption button (`OpenFonts`, internal static `FontList(editor)`, `PickFont`, `FontRow`, `FontHeader`, nested `FontRowButton`).
- `EngineFonts.imports.axml` gains charsets `LatinExtended` (U+00A1–017F less soft hyphen), `Greek`, `Cyrillic` (U+0400–045F, Ґґ).
- `FontSettings` (`<Fonts>`) with `LanguagePacksSetting` (`<LanguagePacks Packs="Latin">`, OnChanged `Fonts.Rebake`).
- Tests in `FontPickerTests.cs`: `Fonts.CatalogGroups`, `Fonts.StyleDeltaFont`, `Fonts.LanguagePackCovered`, `Fonts.Previews`, `Fonts.ImportFamily` (golden `Fonts.ImportFamily.Label.png`), listed in `Fonts.tests.axml`. Golden `Ribbon.Docs.Format.png` re-approved (the new button shifts the bar).

## Why these choices

**Families are grouped by OpenType name ID 1, style from OS/2 fsSelection.**
(Extended 2026-10-30 by [[font-variants]]: name-ID-1 families sharing a typographic family name, ID 16, now sit as variants under the family named as it, e.g. Calibri Light under Calibri; ID 1 stays the inner layer. The sentence "exactly the engine's four faces" below no longer holds: a family is a list of faces.) Name ID 1 holds at most Regular/Bold/Italic/Bold Italic, so arial/arialbd/ariali/arialbi become "Arial" while Arial Black and Arial Narrow stay their own families. Style bits: bold 0x20, italic 0x01; `head.macStyle` fallback. Rejected: filename-suffix grouping (the importer's old probe) — it guesses and is wrong for many families; the user also rejected listing file names.

**A styled face is taken only from face 0 of its file.**
`ImportFont` reads bold/italic faces at index 0. A family with no regular promotes its bold, then italic, then bold italic.

**Names are drawn in their own font through one shared preview atlas.**
An ordinary `FontAsset` whose glyphs are each family's name characters taken from that family's own regular face, remapped to private-use codepoints U+E000–U+F8FF, so the existing text path draws them unchanged. Rejected: one small atlas per family — the bindless texture table is 256 slots (`TextureAsset.MaxTextures`) and Windows has ~220 families; bitmap rasterising the names — a second text path. A family whose face lacks a character of its own name (symbol fonts, cmap without 3/1) gets no preview and is listed in the default font. Measured: 2313 glyphs for 212 families at 32 px cells; catalog scan + preview bake 5.7 s (Debug, this machine, first time), cached afterwards by a hash of names+paths+mtimes; scan of 374 faces is milliseconds once cached.

**Previews bake on a background Task after boot.**
`Fonts.StartCatalog` registers them through `Engine.Post`; until ready the list shows "Finding installed fonts…" with families in the default font. Rejected: baking on first open under the wait window — several seconds of freeze. Skipped under `--test`.

**Runtime imports go to a per-user, lowest-priority mount in every build.**
`%LOCALAPPDATA%\Arktis\Data` gives "import once, use anywhere" across all apps and works in shipped builds whose Data is a read-only .arx. Rejected: writing into `AuroraEngine/Data/Fonts` — it exists only in Debug, so shipped apps could not import. User approved.

**Import bakes on a worker thread; registration is posted to main.**
Profiling tables are thread-static and MTSDFGen has no mutable statics; registration and texture upload run on main via `Engine.Post`. The wait window is not modal, a second pick while importing is ignored, the font is applied to the editor's selection at completion time. On failure the window shows the error with an OK button and nothing is registered.

**Language packs apply only to runtime-imported fonts, and a change takes effect next start.**
`<Fonts><LanguagePacks Packs="Latin"/>` is a comma list of charset names; user fonts bake with `Covered=true`, so characters the face does not map are dropped instead of baking .notdef. Changing it re-bakes the user fonts behind the wait window; new atlases load on next start because reloading a live `FontAsset` would leak its texture slot and leave text-measure caches stale. Engine/app-declared fonts stay one charset without a cover filter, to avoid re-baking every tracked atlas.

**CJK is not supported.**
Tens of thousands of glyphs do not fit one atlas; it needs on-demand glyph paging, a separate system. The user explicitly deferred it ("skip CJK for now ... note that CJK is missing").

**Picker rows are `FontRowButton`s acting on press, in a `ScrollableControl`.**
`takesActiveControl` is false (same reason as `ToolButton`: a button that leaves the active control alone gets no release). The scrollable (220×≤360) sits in a `ContextMenuContent` because `ContextMenuControl.Row` cannot live inside a scrollable (it casts its parent). The imported list dedupes by family, keeping the asset with most faces, tie to `default`; the preview asset is hidden.

**Font lists scanned:** Windows (machine + per-user), macOS (/System/Library/Fonts, /Library/Fonts, ~/Library/Fonts), Linux (/usr/share/fonts, /usr/local/share/fonts, ~/.local/share/fonts, ~/.fonts), recursively, plus `_Build/FontSources`.

## Verification
- Builds clean (Thorium Debug). Test-verified: `--test=Fonts` all pass. Golden-verified: `Fonts.ImportFamily.Label` (Georgia from a runtime-baked atlas). Full `--test`: 368 passed, Boot failed on the pre-existing default sampler error only. Shot-verified: a throwaway shot of the whole list showed every name in its own face.
- NOT GUI-verified: clicking the toolbar button, scrolling the list, the wait window over a real window, applying a font to a selection by mouse, the Settings → LanguagePacks re-bake. Not verified on Linux or macOS. Not perf-measured beyond the bake timing.

## Known gaps
- CJK missing (no language pack for Chinese/Japanese/Korean).
- Language pack change takes effect on next start; packs are free text in Settings, unknown names make the re-bake skip that font with a warning.
- A family's bold/italic inside a .ttc at a non-zero face index is ignored (regular-only).
- Undeclared faces of a user import are still suffix-probed by `ResolveFace`.
- Cambria Math's oversized line box makes its row sit low in the list.
- Long family names are not truncated in the 140 px toolbar caption.
- Out of scope: removing an imported font, weights beyond four faces, variable-font axes, missing-glyph font fallback.
- Markdown/plain-text notes cannot carry a run font (only .axml notes save `<Run FontName>`).

Related: [[document-format-bar]], [[bold-italic-face]], [[asset-manifest-and-import]], [[arx-archives]]
