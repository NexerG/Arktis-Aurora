# Decision — assets are declared in manifests, cooked by importers, resolved through the VFS

**Date:** 2026-08-07
**Status:** LANDED (2026-08-07), except lazy loading. Import runs at boot and skips unchanged bakes;
the asset manifest drives every file-backed asset; `AbstractAsset.Load` is the only loader method;
misses fall back to `default`. Only decision 3's **lazy** half is unbuilt — `RequestLoad` exists as
the seam and currently just reports.
**Scope:** `ArctisAurora.EngineWork.Registry.AssetRegistries`;
`ArctisAurora.Core.Registry.Assets.*`; `ArctisAurora.Core.Filing.Serialization`
(`AssetImporter`, `MeshImporter`, `Paths`, `VirtualFileSystem`).

## Three lifecycles, usually collapsed into two

```
SOURCE     C:\Windows\Fonts\arialbd.ttf, model.fbx      lives anywhere, never shipped
   | IMPORT       rare, offline, the only thing that WRITES into Data/
COOKED     Data/Fonts/arialbd/{.afm,.agd,_atlas.png}    VFS-visible
   | MANIFEST     (type, name) -> cooked source
   | VFS          which mount actually has it
   | LOAD         bytes -> asset -> GPU
REGISTRY   AssetRegistries dictionaries
```

The invariant that keeps import and load from fusing: **the loader only ever reads from `Data/`
through the VFS; the importer is the only thing that writes there.** A shipped build has no
importer path, so import is allowed to be slow and unoptimised.

This is [[asset-pipeline-bake]]'s layer 1→2 split arriving early, for fonts rather than meshes.

## What already existed before this design

| Piece | Where | State |
|-------|-------|-------|
| Mount/priority resolution | `VirtualFileSystem`, `DirectoryMount`, `ArxMount` | works; the pak drop-in landed 2026-10-16 as `ArxMount` ([[arx-archives]]) |
| Typed registries | `AssetRegistries` + `Registry.axml` | works |
| Declarative boot order | `Bootstrap.axml` | works; `PrepareDefaultAssets`/`PrepareAllAssets` are steps |
| Reflection XML→object | `DocumentXml.ApplyAttributes` | works, but `private` and document-scoped |
| **Font importer** | `AssetImporter.ImportFont` + `AuroraFont.GenerateGlyphAtlas` | **complete**: system `.ttf` → `.afm` + `.agd` + `_atlas.png` |
| **Mesh importer** | `MeshImporter.ImportFBX` | returns an Assimp `Scene`; **no cooked output** |

Import was therefore never missing — it existed twice, unnamed, with no shared contract and no
output convention. The design below names it rather than inventing it.

## Decisions

### 1. The manifest is the missing layer — **LANDED**

A directory of manifest files, `Data/XML/Assets/`, **all unioned**. Not one file: `EnumerateAll`
dedupes by *filename*, so a single `Assets.axml` in an app would silently replace the engine's whole
set instead of adding to it. Free filenames + entry-level union means an app adds assets by dropping
a file, and *overrides* one by declaring the same `(Type, Name)` pair — **first mount wins per
entry**, matching the `DocumentStyles.axml` cascade at finer granularity.

```xml
<AssetManifest>
  <Asset Type="FontAsset"    Name="default"   Source="Fonts/arial"/>
  <Asset Type="FontAsset"    Name="arial-b"   Source="Fonts/arialbd"/>
  <Asset Type="SamplerAsset" Name="ui" MagFilter="Linear" MinFilter="Linear"/>
</AssetManifest>
```

`Type` resolves via `AnyXMLType.FindType`, same as `Registry.axml`. Entries are keyed on
`(Type, Name)` and deduped in mount order, so an app overriding one engine asset does not replace
the set. Landed as `Data/XML/Assets/EngineAssets.axml`.

**Not built: inline per-type config.** An entry carries `Type`/`Name`/`Source` only. The design's
reflection-applied extra attributes exist to let `SamplerAsset` move into the manifest, and samplers
were left on `XML/Documents/Samplers/*.axml` — so the feature had no consumer. `SamplerAsset.Load`
throws `NotImplementedException` and `LoadAll` stays a plain method; its duplicated attribute chain
survives, minus the dead `LoadAsset` copy.

### 2. One loader method replaces three — **LANDED**

| Now | Problem |
|-----|---------|
| `LoadAsset(AbstractAsset asset, string name, string path)` | `asset` is **by value**; `asset = d[name]` is a dead write in both `FontAsset` and `TextureAsset`. Caller gets nothing. |
| `LoadDefault()` | hardcodes `"arial"` / `"defaultMask.png"` |
| `LoadAll(string path)` | `SamplerAsset` ignores the `path` and hardcodes the VFS dir |

Replaced by `Load(string name, string source)`. `LoadDefault` becomes "the manifest declares an
entry named `default`"; `LoadAll` becomes "the manifest has N entries". Registration moved out of
`Load` entirely — the preload loop adds to the registry via `IDictionary`, so the named
`FontAsset(string)`/`TextureAsset(string)` constructors that registered as a side effect are gone.

**`AVulkanMesh` is now an `AbstractAsset`** (user, 2026-08-07). Its `Load` runs Assimp on the FBX;
no cooked format was invented — `.amesh` is still deferred. This is what killed
`C:\Users\gmgyt\Desktop\VienetinisPlaneRetry.fbx`, now `Data/Meshes/uiquad.fbx`.

**Code-generated defaults stay in code** (user, 2026-08-07). `AVulkanMesh "default"` (a hand-written
pyramid), `ControlStyle "default"` (white tint) and the `SamplerAsset "default"` registration have no
file behind them, so `PrepareDefaultAssets` survives holding exactly those three. A `Source`-less
manifest entry would declare nothing — it would only relocate a call.

`TextureAsset` splits into `Load(name, source)` (VFS-resolved, registry-named) and an internal
`LoadFile(path)` that uploads and claims a bindless slot without taking a name. `FontAsset` uses the
second for its atlas, which is what removes the old `new TextureAsset("uidefault")` collision.

### 3. Preload all now; lazy is scaffolded, not built (user, 2026-08-07) — **preload LANDED**

Everything in the manifest loads eagerly at `PrepareAllAssets`. The **manifest index survives**
preload rather than being discarded, and `GetAsset<T>` grows one branch:

```
hit  -> return it
miss -> RequestLoad(type, name)   // today: warn, do nothing
        return d["default"]
```

`RequestLoad` is the seam. Filling it in later is one method body — no call site moves, because
`GetAsset` is already the only door.

**Why lazy is not built now:** `TextureAsset` submits on the transfer queue and `SamplerAsset`
calls `vk.CreateSampler`. That is safe today *only* because all loading happens at bootstrap before
the render thread exists. A real lazy load from `Interpolate()` would submit concurrently with the
render thread recording — queue submission is not thread-safe. Doing it properly means enqueueing
and swapping in at `FrameEdge`, which is [[engine-resource-manager]]'s `CommandLane` shape and drags
in handle indirection. Deferred with the resource manager.

### 4. A miss returns `default`, warned once (user, 2026-08-07) — **LANDED**

`GetAsset<T>` throws today. It will fall back instead. **Warn once per missed key** (a
`HashSet<string>` of already-reported keys) — silent fallback makes a typo'd font name render in
arial forever and look like a layout bug.

Note this is the *same branch* as decision 3's scaffold, which is why the scaffold is nearly free.

### 5. Import is declared, not called — **LANDED**

The charset and glyph size were import parameters hardcoded at a commented-out call site. They are
now XML, in `Data/XML/Imports/*.axml`, unioned across mounts (charsets first, so any set can
reference a charset another file declared):

```xml
<ImportSet>
  <Charset Name="Latin" Chars=" !&quot;#$%&amp;'()*+,-./0123456789…ĄČĘĖĮŠŲŪŽąčęėįšųūž"/>
  <FontImport Source="arial.ttf"   Charset="Latin" GlyphSize="64"/>
  <FontImport Source="arialbd.ttf" Charset="Latin" GlyphSize="64"/>
</ImportSet>
```

Shape deltas from the original sketch, all deliberate:

| Sketched | Landed | Reason |
|----------|--------|--------|
| `<Import Type="FontImporter">` | `<FontImport>` | mesh import does not exist; a `Type` with one legal value is dead weight |
| `Charset` as its own file | `<Charset>` inline in the `ImportSet` | still a named reference, one fewer directory; language packs union more files |
| `Name` per entry | none — folder is the ttf basename | mapping `arialbd` → a registry key is the *asset* manifest's job (decision 1) |

`Charset` as a named reference is the roadmap's "language packs" item falling out for free.

**Trigger: boot-time, gated on `Engine.isDebug`** (user, 2026-08-07), as a `<Step>` in
`Bootstrap.axml` ahead of `Renderer.InitRenderer`. Release builds never import. The stall was the
argument against it and it turned out small — **113 glyphs at 64px is ~13 s per face**, both
faces ~27 s, once ever.

**Output convention:** folder per asset under a type root — `Data/<TypeRoot>/<name>/`. Fonts
already did this; codified. `ImportFont` and `GenerateGlyphAtlas` now take an `outputRoot` and
create the directory — neither did, so any font whose folder did not already exist threw.

### 6. Meshes: FBX-at-load now, `.amesh` much later (user, 2026-08-07)

There is no cooked mesh format — `ImportFBX` hands an Assimp `Scene` straight to `LoadCustomMesh`.
So for now "mesh import" means *the FBX lives in `Data/Meshes/` and the manifest points at it*;
load parses it every boot. `AVulkanMesh` gets a real `.amesh` cooked form **later — explicitly not
now**; that is [[asset-pipeline-bake]]'s job and Phase D.

What this buys immediately: the hardcoded `C:\Users\gmgyt\Desktop\VienetinisPlaneRetry.fbx` in
`AssetRegistries.PrepareDefaultAssets` becomes a manifest entry, without inventing a format.

### 7. Reads resolve through the VFS; writes target the primary mount — **LANDED**

Fonts bypassed the VFS entirely: every read built a path off `Paths.FONTS` directly, while samplers
resolved through mounts. Consequence — an app could not add or override a font in its own `Data/`,
and `arial` is duplicated byte-for-byte across `AuroraEngine/`, `Thorium/` and `AuroraEditor/`
`Data/Fonts/`.

`Paths.Font(name, file)` now resolves `Fonts/<name>/<file>` across mounts, mirroring
`Paths.Doc`/`Paths.SamplerDoc`. Converted read sites: `AtlasMetaData.Deserialize`,
`FontAsset.LoadAsset`, `FontAsset.LoadDefault` (×2).

**Writes deliberately still use `Paths.FONTS`** (`AuroraFont.GenerateGlyphAtlas` ×3,
`AssetImporter.ImportFont`): import writes to the **primary mount**, i.e. the running app's own
`Data/`. Engine defaults stay pre-cooked and committed, so an app importing a font never dirties
`AuroraEngine/Data`.

`Thorium/Data/Fonts/arial` and `AuroraEditor/Data/Fonts/arial` were deleted (they were byte
identical to the engine's), along with their now-dangling `<Content Include>` entries. Both apps
were already debug-only — neither copies its own `Data/XML` to output — so nothing regressed.

**Known consequence, accepted for now (user, 2026-08-07):** an ImportSet declared in the *engine's*
Data still bakes into whichever app is running, so `EngineFonts.axml` recreates a per-app copy of
every font it declares. The alternative — outputs landing in the Data root of the ImportSet that
declared them — was considered and **deferred**; it is the fix when the duplication starts to hurt.

### 8. A bake is skipped only when nothing it depends on changed — **LANDED**

"Output missing" was the original trigger rule; it is not enough, because the charset and glyph size
change far more often than the `.ttf` does (adding EU languages is on the roadmap). Each cooked font
carries `Data/Fonts/<name>/<name>.import.axml`:

| Field | Why it invalidates |
|-------|--------------------|
| `Source` | the entry now points at a different face |
| `SourceHash` | SHA256 of every `.ttf` in the family — an installed face was replaced |
| `<Face Key Source Index/>` children | every baked face in block order (key = variant × 4 + style), by filename; replaced `BoldSource` / `ItalicSource` / `BoldItalicSource` in [[font-variants]] |
| `Charset` | resolved character string, stored verbatim, not hashed — debuggable |
| `GlyphSize` | atlas cell size changed |
| `ImporterVersion` | a `const` in `AssetImporter`; bump to re-cook everything. **7** since [[font-variants]] |

All of them must match, **and** the `.agd` and `_atlas.png` must exist — a stamp alone never authorises
a skip. Granularity is per font: changing one entry re-bakes that family and leaves the others.

**A change to the `.agd` field layout is an `ImporterVersion` bump, always.** The stamp describes the
*sources*, so it cannot notice that the reader's expectations moved — a family whose faces did not change
would keep a stamp that matches and an atlas that `Serializer.DeserializeAttributed` then misreads
positionally.

`.ttf` bytes are hashed rather than timestamped because a re-bake is expensive enough that a false
positive matters, and mtime moves for reasons content does not.

**The stamp is deleted before the bake, not just written after it.** Found by killing the process
mid-bake: the old stamp survived and would have validated half-written output on the next boot.

This is [[asset-pipeline-bake]]'s `hash(source) + importerVersion` cook key, arriving early and
scoped to fonts.

## Build order

1. `Paths.Font` + VFS-resolved font reads — **done**.
2. `XmlReflection` — `DocumentXml.ApplyAttributes` and the member-classification helpers hoisted to
   `Core/Filing/Serialization`. `DocumentXml` delegates; the import parser is the second consumer —
   **done**.
3. Import manifest + boot-time trigger + stamp — **done**.
4. Asset manifest parse + `Load(name, source)` + preload; `PrepareDefaultAssets` reduced to the three
   code-generated entries — **done**. Verified end to end: a `<Run FontName="arial-b">` renders in
   Arial Bold beside regular text in the same paragraph, so import → manifest → registry →
   `TextControl.fontName` → measurer and view agreeing all hold.
5. `GetAsset` miss branch: warn-once + default fallback + `RequestLoad` seam — **done**.
6. *(later)* `RequestLoad` for real, at `FrameEdge`, with the resource manager.
7. *(much later)* `.amesh` cook.

## Bugs — fixed

- **`AssetImporter.ImportFont` passed the literal `"arial.ttf"` to `GenerateGlyphAtlas`** instead of
  `fontName`, so importing `arialbd.ttf` wrote `arialbd.afm` and then baked *arial's* atlas over
  `arial.agd`/`arial_atlas.png`. This was the actual blocker on a second font.
- Neither `ImportFont` nor `GenerateGlyphAtlas` created its output directory — arial's already
  existed, so a genuinely new font threw.
- `GenerateGlyphAtlas` wrote a `SDF_A.png` debug dump into every font folder on every bake. Stale
  example generation; removed.

- `FontAsset.LoadAsset` NRE'd on a null `textureAsset` (only ever assigned in `LoadDefault`), and
  `LoadDefault` registered the arial atlas as `new TextureAsset("uidefault")`. Both methods are gone;
  `FontAsset.Load` builds its own unnamed `TextureAsset` via `LoadFile`.
- The two `.agd` readers are no longer both reachable. `FontAsset.Load` uses
  `Serializer.DeserializeAttributed` — the one that was actually exercised. `AtlasMetaData.
  Deserialize` (hand-rolled `BinaryReader`, only ever called from the NRE'ing `LoadAsset`) is now
  unreferenced by asset loading. Left in place, untested, a deletion candidate.

## Bugs — still open

- `GenerateGlyphAtlas` reads `hhea` ascender/descender and discards them, so
  `TextMeasurer.GetLineMetrics` derives the line box as max ink across the glyph set instead.
  Storing them changes the `.agd` format and needs every atlas re-baked — now cheap to force, since
  bumping `importerVersion` re-cooks everything.

## Still open

- ~~**`XmlReflection.ScalarMembers` still filters `IsControlChrome`**~~ — resolved 2026-08-07. The
  filter is deleted; the writer skips scalars equal to their default instead. See
  [[xml-save-skips-defaults]].

## Rejected

- **One `Assets.axml` per mount.** `EnumerateAll` dedupes by filename, so an app's copy replaces
  rather than extends. Entry-level union instead.
- **Manifest entry pointing at a separate per-asset config file.** Samplers would gain an
  indirection they do not need; reflection-applied inline attributes cover both shapes with one
  format.
- **Handles/assetIds now.** [[engine-resource-manager]] requires entities hold ids rather than GPU
  offsets, but nothing today holds an offset — `AssetRegistries` indirection already is the
  handle. Adding an id layer before the resource manager is speculative.
- **Hot reload, refcounting/unload, cook cache, pak.** The VFS is already the seam for the last two.

## Amendment 2026-10-16 — the pak drop-in landed

- `PakMount` was built as `ArxMount : IDataMount`; no call site above `VirtualFileSystem` changed.
- Data reads go through `DataFiles` (`Exists`, `Open`, `LoadXml`), which take a disk path or an archive-entry path. Import-only, vault, note and results reads stay on the disk APIs.
- Release with an `.arx` beside the exe mounts the loose `Data/` folder first, then each archive. See [[arx-archives]].

Related: [[asset-pipeline-bake]], [[engine-resource-manager]], [[glyphs-as-pool-data]], [[arx-archives]]
