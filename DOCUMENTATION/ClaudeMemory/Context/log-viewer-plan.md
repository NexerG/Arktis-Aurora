# Log viewer in Thorium — agreed plan, not started

**Agreed:** 2026-08-22. **Nothing built.** Deferred deliberately; the logger it reads from landed the
same day.
**What it reads:** `../Decisions/engine-logging.md` (the logger) and `../../Engine/Systems/LOGGING.md`.
**Checklist form:** the log-viewer item in `DOCUMENTATION/Work in Progress List.md`.

This file exists so the work can be picked up cold. It carries the decisions already taken, the facts
that were expensive to establish, and enough per-slice detail to build from without redesigning.

## Goal and scope

Show engine log output inside Thorium, coloured by level, with columns that line up.

**Two surfaces, one control** (user, 2026-08-22 — "both"). The live panel and the file viewer are the
same `LogViewControl` with two feeds. Do not build two controls.

| Feed | Source |
|---|---|
| Live | a new `MemorySink` ring, updated as the engine runs |
| File | the **tail** of `engine.log` or a rotated `engine.N.log` |

## Standing decisions

Settled. Do not re-litigate without asking.

| Decision | Why |
|---|---|
| **Cap at ~500 entries, drop the oldest** (user, 2026-08-22) | Stays clear of the glyph ceiling below. The cap *is* the mitigation — there is no virtualization to fall back on |
| **File feed is tail-only** | A direct consequence of the cap. Opening a 16 MB log shows its end, which is what you want after a crash, and is what a recorder dump already is. Paging further back is out |
| **One control, two feeds** | The record shape is identical; only the source differs |
| **`LogViewControl` lives in the engine**, not Thorium | `Core/UI/`, so the Editor can use it later. Thorium only declares it in XML |
| **Indent means continuation lines only** | A stack trace indents under its header. Free, and covers the crash case |
| **No scope-based nesting in this pass** | Real phase→step folding needs the *logger* to emit depth (`Log.Scope`), which changes the record shape. Its own decision — inferring nesting in the viewer from the `running: X` text is guesswork that breaks on a reworded message |
| **`MemorySink` is a new structure, not a reuse of `FlightRecorder`** | The recorder stores formatted bytes and drops the level, so nothing downstream can colour by it |
| Colours come from **XML attributes**, not hardcoded | Same as every other control here |

## Facts that were expensive to establish

- **There is no virtualization, and one ClaudeMemory note says otherwise.**
  `Decisions/glyphs-as-pool-data.md` (2026-07-31) says "the document virtualizes to viewport ± 1".
  **That is stale.** `Context/thorium-editor-architecture.md` supersedes it on 2026-08-07: *"No
  virtualization: every character is a `GlyphControl`, always."* Verify against
  `thorium-editor-architecture.md`, not the July note.

- **The 50,000-descriptor cap is NOT the blocker any more.** It was, when the sampler array was
  indexed by `gl_InstanceIndex`. Since 2026-07-31 it is a 256-entry texture table indexed by
  `ControlData.textureIndex`, and every glyph in a font shares one slot. Do not cite it.

- **The real cost is the pool row.** One character = one `GlyphControl` = a full `VulkanControl`
  entity + a `ControlData` row in the pooled SSBO + a mat4. At ~100 chars a line:

  | Log size | Glyph entities | Verdict |
  |---|---|---|
  | 60-line boot | ~6,000 | fine, a page of a note |
  | 500 lines (the cap) | ~50,000 | ≈ a 17-page note. Should be fine; largest tree Thorium will build |
  | 5,000 lines | ~500,000 | hundreds of MB, O(n) Measure/Arrange |
  | a 16 MB `engine.log` | millions | not happening |

- **The escape hatch, if the cap proves too tight**, is already designed in
  `Decisions/text-layout-one-measurer.md:36` — `TextMeasurer` works from the string and font metrics
  alone, so a run can hold `text` + a measured `BlockLayout` with **no** glyph children and call
  `SyncGlyphs()` when it scrolls into view. That is the engine change sequenced after Thorium v1 and
  the profiler. **The capped panel does not need rewriting when it lands.**

- **Per-glyph colour already works and is load-bearing.** `TextControl.controlColorHex` propagates to
  each `GlyphControl`; `glyphs-as-pool-data.md` keeps per-glyph tint deliberately, because per-letter
  colour is a required Thorium feature. Colouring a level tag costs nothing new.

- **Fonts are baked from a declared manifest** — `AuroraEngine/Data/XML/Imports/EngineFonts.imports.axml`.
  Adding one is a single `<FontImport>` line; `AssetImporter.RunImports` bakes anything stale at boot,
  Debug only. **Asset name = filename minus extension** (`arial`, `arialbd`, `Electrolize-Regular`).

- **With fixed-width column labels, the grid aligns without monospace.** Monospace matters inside the
  message column and for raw file lines, not for the columns themselves. Do not oversell it.

- **The row-list pattern to copy is `FileBrowserControl : ScrollableControl`** — a rows `StackPanel`,
  `rowHeight`, `indent`, and `AddRow` building `LabelControl`s with their own `controlColorHex`.
  `LogViewControl` is a sibling of it, not a subclass; `FileBrowserControl` is filesystem-specific.

- **`Entity.OnTick()` is virtual** and runs from `EntityRegistry.ProcessTicks()` inside
  `Engine.Interpolate()`, only for entities that called `SetTicking(true)` ([[entity-tick-group]]). A control
  can poll there once it opts in. `ContextMenus.Tick()` is the
  precedent for a per-frame UI hook.

- **A custom Thorium control is:** subclass a container, add `[A_XSDType("Name", "UI")]`, declare it
  in `Thorium/Data/XML/Documents/UI/UI.ui.axml`. `VaultBrowserControl` is the worked example.

- **Dark mode:** a container without the `invisible` mask asset paints opaque and becomes an
  accidental white background. `DocumentControl` does
  `maskAsset = AssetRegistries.GetAsset<TextureAsset>("invisible")`. Copy that.

## Slices

### 1. Monospace font

`AuroraEngine/Data/XML/Imports/EngineFonts.imports.axml`, one line — engine-wide so the Editor gets it too:

```xml
<FontImport Source="consola.ttf" Charset="Latin" GlyphSize="64"/>
```

→ verify: `Data/Fonts/consola/` appears after a Debug boot; `GetAsset<FontAsset>("consola")` resolves.

### 2. `MemorySink`

New `Core/Diagnostics/Sinks/MemorySink.cs`. A ring of structured entries:

```
LogEntry { time, level, channel, origin, text, file, line }
```

Exposes a snapshot and a `long version` bumped on append, so the UI polls one number instead of
locking.

**One internal signature change:** `LogSpool.Publish(LogLevel, ReadOnlySpan<byte>)` becomes
`Publish(in LogRecord, ReadOnlySpan<byte> message, ReadOnlySpan<byte> line)`. The sink wants the
parts, the console and file want the assembled line, and `DrainOnce` already holds both.

→ verify: dumping the sink after boot lists the same ~61 entries the file has, with levels intact.

### 3. Settings

Fourth `Setting` on `LoggingSettings`, feeding the floor calc the same way the other three do:

```xml
<LogMemory Enabled="true" MinLevel="Debug" Capacity="500"/>
```

→ verify: `Capacity="50"` keeps 50 and drops the oldest.

### 4. `LogFileReader`

New `Core/Diagnostics/LogFileReader.cs`. Seeks to the end, reads backwards in blocks until it has N
lines, parses into `LogEntry[]`.

The format is fixed and machine-written, so this is column splitting, not a grammar. Lines that do not
match — stack-trace continuations, recorder banners — attach to the previous entry as continuation
text. The format is:

```
20:04:59.953 INFO  Render:1423 [Renderer] rebuilding swapchain — resize
20:04:59.505 ERROR t2 [Assets] failed to load the default sampler  @SamplerAsset.cs:66
```

→ verify: reading the crash-test log recovers the Fatal entry with its stack attached as continuation,
not as four orphan rows.

### 5. `LogViewControl`

New `Core/UI/Controls/LogViewControl.cs`, `[A_XSDType("LogView", "UI")]`,
`: ScrollableControl`.

- one horizontal row per entry, one `LabelControl` per column with its own `controlColorHex`
- columns time / level / origin / channel / message, fixed widths from XML attributes
- `OnTick()` compares the sink's version, rebuilds only on change
- follow-tail: pinned to the bottom stays pinned, scrolling up releases it
- continuation lines indent under their header
- the `invisible` mask, per dark mode above

→ verify: boot Thorium, the panel matches the console; the sampler `ERROR` row is red; scrolling up
stops auto-follow; a forced crash shows the stack indented under its Fatal row.

### 6. Thorium wiring

`Thorium/Data/XML/Documents/UI/UI.ui.axml` — `<TabItem Header="Log"><LogView .../></TabItem>` in the
right-hand `EditableTabs`. Plus a `Log.OpenFile` action for the file feed.

→ verify: the tab renders, tears off into its own window like the others, survives a rebuild.

## Deliberately out of scope

- **Virtualization.** The cap is the whole mitigation. This does not touch the glyph ceiling and does
  not make the 100-page note work.
- **Scope-based nesting / folding.** Needs `Log.Scope` on the logger first.
- **Level and channel filter widgets, and search.** The data supports all three; no UI.
- **Editor wiring.** The control lives in the engine so it can, but Thorium is the only host here.

## Open questions

- Where the panel goes: a tab in the right-hand `EditableTabs` is the cheapest, but a bottom dock
  across the full width is the conventional place for a console. Not decided.
- Whether `Log.OpenFile` gets a file picker or just opens the current `engine.log`.

Related: [[engine-logging]], [[thorium-editor-architecture]], [[text-layout-one-measurer]],
[[glyphs-as-pool-data]], [[ui-data-control-split]], [[vault-browser-and-shell]]
