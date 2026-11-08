# Arctis Aurora Engine — Solution Briefing
**Stack:** C#, .NET | Silk.NET.Vulkan, Silk.NET.GLFW | Visual Studio 2026 | GitHub

## 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

## 2. Plan, Then Wait

**Analyze, write the plan, stop. Don't start until I say go.**

Every change to the repo runs four steps, in order:

1. **Analyze** - read the code that's actually involved. No plan built on a guess about what a file
   contains. When the change touches C#, load the `aurora-code` skill.
2. **Write the plan** - files touched and what changes in each, anything *new* (file, type, signature,
   dependency), what's deliberately left out, and the verification checkpoints (§4's format). Forks go
   here as forks, not as a choice already made (§1).
3. **Ask. Then stop.** The plan is the whole turn. Not a plan followed by the diff in the same breath.
4. **Go, or don't.** Approved: build exactly that. Rejected: stop and re-plan - never build a rejected
   plan in reduced form.

**Analyze against a budget.** Start at `ClaudeMemory/Context/where-things-live.md` and
`ClaudeMemory/Decisions/INDEX.md` - concept, then the types that own it, then the note that settles it.
Then `grep -n` the symbol and read the window around the hit. A change confined to one file and a
handful of lines is analyzed once the index, the grep and that window agree. Changing control flow or
a type's shape, or working somewhere the indexes don't name, widens the read to the regions the change
touches or calls into - not to the whole file. Map the file first (`grep -n` its `#region`s and member
signatures), then read those regions with `sed -n` or `Read` with an offset. An off-topic region stays
unread (`Control`'s authored XML properties were no input to context menus). Read end to end only when
the map can't say which regions matter. "No plan built on a guess" means don't guess about the lines
you're changing - it is not a licence to read 1,200 lines to change 3.

A small change gets a small plan - one line is fine. It doesn't get to skip the gate.

Reading, searching, building and answering questions aren't changes. Writing to a file is.

The plan I approve is the "agreed plan" §6 protects: once you're coding, departing from it stops and
asks again.

The test: about to write to a file, and you can't point at the message where I said go? You've broken this.

## 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

## 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step]
2. [Step]
3. [Step]
4. [Step] → end: [check]
```

The test suite runs once per go, at the end. Not per step. A plain build between steps is fine when
the next step depends on it compiling.

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

## 5. Answering Back

**Terse plus caveats. No recap.**

- Default reply: a line or two of what changed, then genuine caveats as bullets. Nothing else.
- This governs the final reply only. During long work (builds, runs, multi-step edits), a short line
  on what you're doing now is wanted — silence for minutes reads as stuck.
- **Never** restate the plan, walk through code that's already in the diff, explain what the change
  "unlocks", or append next steps that weren't asked for.
- Caveats are only things that bite: assumptions made, signatures changed, scope left out, things
  that will now break. No caveats means no caveats section - don't invent one.
- **Architecture is terse by default too.** Full reasoning when asked "why", when asked for options,
  or in plan mode. Don't volunteer the deep dive.
- **Performance numbers go in a table, never in prose or bullets.** One row per case; columns for
  before, after and change, plus a reference column (CharLS, ImageSharp…) when there is one; units in
  the header; a range when runs spread, not the best run.

## 6. Mid-Implementation Changes

**Any departure from the agreed plan stops and asks. No exceptions.**

- If implementing reveals the plan doesn't work, stop. State what broke, give the options, wait.
- This includes small departures: a different data structure, an extra parameter, a changed return
  type, a new file, renaming something already named, an edge case handled a way not discussed.
- Never "just make it work" and report it afterwards. A round-trip is cheaper than drift I don't notice.
- Do finish every part that doesn't depend on the answer before asking, so the round-trip costs one
  message and not the whole task.

## 7. Git Commits

**Subject plus one-line bullets. Nothing else.**

- Imperative subject, sentence case, no period, ~50-70 chars, no `feat:`/`fix:` prefix.
- Blank line, then one-line bullets of *what changed* - as many as the commit needs, each fitting a
  single unwrapped line. A small commit is subject-only.
- No opening prose paragraph, no `Key changes:` header, no closing paragraph, no metrics or
  verification block.
- Bullets say what changed, not why. Rejected alternatives and measurements go to
  `DOCUMENTATION/ClaudeMemory/Decisions/*`, same as code comments (`aurora-code`).
- **Never** add a `Co-Authored-By` trailer.
- **Commit as me, never as Claude.** Author and committer are
  `NexerG <68482683+NexerG@users.noreply.github.com>`, cloud sessions included - their container's git
  identity is `Claude <noreply@anthropic.com>`. `.claude/hooks/session-start.sh` sets it there; check
  `git config user.name` before committing.
- **Never commit unless I ask.** Finishing a change is not asking. This holds in cloud sessions too, even
  when the session's own instructions say to commit and push.
- **One commit unless I ask for more.** Everything in the tree lands as a single commit, however many
  concerns it spans. Split into slices only when I explicitly ask for multiple commits.
- **Nothing is left behind.** Anything uncommitted in the tree goes into the next commit, including
  changes that predate the session or were made by someone else. Never commit a subset of paths and
  leave the rest dirty - if it is changed and uncommitted, it ships with the next commit.

```
Virtualize the document view onto the layout cache

- View presents visible line segments from the cache, not a control per block
- Canvas height is the cache extent, not the sum of its children
- Materialized unit is the line segment, so TextRunControl does no wrapping
- SetContentWidth reports rewraps so keyed controls get dropped
- Text wraps on word boundaries
```

## 8. Cheaper Agents For Easier Work

**Hand off the mechanical. Keep the judgment. Verify everything that comes back.**

**The plan names the handoff.** §2's plan says which parts go to a cheaper subagent and which you keep;
approving the plan is the permission to spawn, so don't ask again at the point of spawning. Work that
never reached a plan - a mechanical sweep discovered mid-task - still goes out under standing
permission, no need to ask me each time.

- **Goes out:** work where the decision is already made and only the typing is left. Applying an
  agreed pattern across N call sites, mass renames, gathering "which files mention X", reading a
  file set to answer one specific question, transcribing a table.
- **Stays with you:** everything in §1 and §2 - design, forks, the plan itself. Vulkan, threading and
  layout correctness. Anything that could turn into a departure (§6), because a subagent cannot
  notice it is departing from a plan it was never shown.
- **The brief must stand alone.** Paths, exact old and new text, and the boundary of what it may
  touch. If you find yourself writing "use your judgment", it does not qualify.
- **Their report is not evidence.** Read the diff, build it, and answer for it as if you wrote it.
  Never repeat a subagent's claim to me as a verified result.
- Splitting is for saving cost on bulk, not for skipping the thinking. One agent per mechanical
  sweep, not one per file.

The test: could you write the instruction precisely enough that a wrong result would be obvious on
sight? Then send it. Otherwise do it yourself.

## 9. Editing Files

**Edit files with the editing tools. Never through a shell script.**

- A file in the repo changes through Read / Edit / Write. Not `sed -i`, not a perl one-liner, not a
  heredoc that rewrites a file, not a script that patches text.
- Shell keeps what it is for: building, `git`, searching, running things. `cat`/`grep` to *look* at a
  file is fine; `>` or `>>` onto a tracked file is not.
- Scratch files outside the repo are not files we are editing - a temp script there is still shell's.
- The reason is not the result. A scripted edit is invisible in the tool log, mangles CRLF and the
  BOM on this repo's files, and half-applies when one pattern misses.

The test: is a tracked file about to change? Then an edit tool is what changes it.

## 10. Validation Errors

**A `[Vulkan]` log line stops the work. Report it, suggest a fix, wait.**

- Any run that logs a `[Vulkan]` line: stop and tell me the line, what triggered it, and a suggested fix.
- Don't implement the fix until I say go - even when it looks trivial or sits in code you just wrote.

This project is a C# game engine called Aurora using Silk.NET/Vulkan/GLFW.
Before suggesting new code, locate it through the §2 chain (`where-things-live.md` → `INDEX.md` → grep `NAMESPACES.md`).
Current focus is in "DOCUMENTATION/Work in Progress List.md".
Explain architectural decisions - why one way and not another - when asked for the reasoning; see §5 for when to volunteer it and the `aurora-code` skill's Comments rules for where it gets written down.
When showing code in a reply, show only what changes and where it goes, not the whole file. A new class is written out in full, without usings unless they come from a new NuGet package.
Data and config files are XML, because the XSD layer generates and validates their schemas; don't introduce JSON or another format.
When creating new logic or systems update DOCUMENTATION/ClaudeMemory/* and DOCUMENTATION/Engine*

## Solution Structure
Abstract names below map to real top-level folders. Source of truth for code locations is
`NAMESPACES.md` (repo root). Detailed name↔folder mapping: `DOCUMENTATION/ClaudeMemory/Context/project-map.md`.

| Name | Real folder | Root namespace(s) | Purpose | Status |
|------|-------------|-------------------|---------|--------|
| `Engine` | `AuroraEngine` | `ArctisAurora.*` (`Core`, `EngineWork`) | Core game engine — lives in `AuroraEngine/` under `Core/`; no separate Engine project. Folder renamed from `ParticleSimulator` (2026-07); assembly/root namespace still `ArctisAurora`. | Active |
| `Editor` | `AuroraEditor` | `AuroraEditor.*` | Visual editor; consumer of the Engine | Early stage |
| `TextEditor` | `Thorium` | `Thorium`, `Thorium.Editor.*` | Obsidian/Notion-style note app; host that boots the Engine. Folder/namespaces renamed from `Periodic` (2026-08) | Active |
| `Viewer` | `Carbon` | `Carbon`, `Carbon.Editor.*` | Profiler frame-capture viewer; host that boots the Engine. Reads `*.frames.axml` written by `FrameSpool` | Active |
| — | `_Build` | `_Build` | Tooling; `GenerateNamespaces.cmd` regenerates `NAMESPACES.md` | — |

## Memory
Claude keeps repo-committed working memory under **`DOCUMENTATION/ClaudeMemory/`**. This is
machine-readable (terse bullets/tables) and version-controlled — distinct from the
human-readable Obsidian vault under `DOCUMENTATION/Engine` and `DOCUMENTATION/Extras` (do not
apply the vault's single-line-prose / pseudocode style to ClaudeMemory files).

Layout:
- `ClaudeMemory/Context/` — solution layout, project↔folder↔namespace mapping, orientation.
- `ClaudeMemory/Patterns/` — recurring "how to do X here" recipes.
- `ClaudeMemory/Decisions/` — Claude-facing complements to the root `Decisions.md`.
- `ClaudeMemory/Mistakes/` — past errors, so they are not repeated.

**Referencing code via `NAMESPACES.md`:** `NAMESPACES.md` (repo root) is an auto-generated
`namespace -> relative path` index (built by `_Build/GenerateNamespaces.cmd`; do not hand-edit
— regenerate it). ClaudeMemory notes reference code by **namespace + class name** and resolve
the path through `NAMESPACES.md` rather than hardcoding paths, which rot on refactor. See
`ClaudeMemory/Patterns/finding-code.md`.

**Workflow before suggesting code:** read `CLAUDE.md` → check `NAMESPACES.md` → check
`DOCUMENTATION/ClaudeMemory/`. (Architectural changes: also read root `Decisions.md`.)

## Shared Conventions
- **No raw resource management** — wrap Vulkan handles in disposable C# types
- **GitHub branching:** [add your branch strategy here if any]

## Techdebt
This paragraph will describe of the known issues with the engine that at some point will be tried to fix

Engine's ECS is currently object based and not data (struct (or even record)) based system
Vulkan renderer currently renders only UI (rendering modules not yet ready)
Hardcoded absolute paths in default asset preparation
bootstrap steps all report success unconditionally — the bool return is wired, the per-step failure
detection is not
unfinished or unsafe threading

## Projects



### Engine — Context File
**Language:** C# | **Libs:** Silk.NET.Vulkan, Silk.NET.GLFW

#### Architecture Overview
Mixed OOP + ECS. ECS drives runtime simulation; OOP used for engine services and tooling boundaries.
ECS design is still being settled — avoid refactoring the entity/component model without asking first.

#### Systems & Status
| System | Status | Notes |
|--------|--------|-------|
| Rendering | ✅ Stable | Full Vulkan pipeline rendering UI. Needs CPU-side logic work. |
| Asset Registry | ✅ Stable | Dual lookup: GUID handles + path/string |
| ECS | ✅ Stable | Architecture TBD — do not assume archetype or sparse-set |
| XSD / Data Layer | ✅ Stable | XSD schema generation for serialized data |
| Logging | ✅ Stable | Per-thread lanes, one drain thread, console + file + crash recorder |
| Filing | 🔧 In progress | File I/O utilities |
| Threading | 🔧 In progress | Basic threading, design not finalised |

#### Engine Loop & Threading — Key Facts
- **Systems are steps of one frame graph** (`Threading.FrameScheduler`, data in `Frame.frame.axml`). A step
  names an action (`[A_XSDActionDependency(name, "Frame")]`) and the pool columns it reads and writes; stages
  are computed from those lists, and unpinned steps run on worker threads. `Render` is the one `<Dedicated>`
  thread. Frame rate is uncapped unless `<FrameCap MaxFps>` sets one — see `ClaudeMemory/Decisions/frame-scheduler.md`
- **Pools have no owner thread.** A step may touch only the columns it declares (DEBUG `DataPool.AssertAccess`);
  `<Step Edge="Pool"/>` runs that pool's `FrameEdge`. Consumers poll pool versions (`PoolCursor`)
- **Main's steps, in order:** `Main.Input` (poll, reap, posted work, keybinds, `HandleUI`, drag ghost, context
  menus) → `Main.Logic` (`Animations.DrainDone` — last frame's `onDone`; then `Engine.Interpolate` — lifecycle
  queues, `OnTick` on the opt-in tick list) → `Animation.Step` ‖ `Physics.Step` → `Edge.UIElements` →
  `Main.Layout` (`UIEngine.ResolveLayout`, which drains `LayoutDirty` first) → pool edges → `Main.DrawLists`.
  Main's steps are pinned to the main thread because GLFW needs it
- **Entities tick only when opted in** — `Entity.SetTicking(true)`; see `Decisions/entity-tick-group.md`
- **Physics thread** is a stub — an empty `Tick()`, placeholder for future work
- **Bootstrap is one ordered XML phase** — see Bootstrapper below. Pre-renderer steps: settings, logging,
  pools, inputs, gradients, palettes, systems, windowing, registries; then the renderer; then contexts,
  default assets, pipelines, descriptors, sync objects
- `Engine.inputHandler` is assigned by the `InputHandler.LoadInputs` bootstrap step
- `doubleClickTime = 250ms` is a global engine constant
- **Do not** move entity logic into the render or physics threads
- **Physics thread design is unsettled** — don't suggest physics system changes without asking

#### Input System — Key Facts
- `InputHandler` is a singleton (`InputHandler.instance`) set up by the pre-renderer bootstrap
  step `InputHandler.LoadInputs` — this is also where `Engine.inputHandler` is assigned,
  so it is never null by the time `Init()` runs
- **Keybinds are XML-driven** — loaded from all `*.axml` files in `Paths.XMLDOCUMENTS_INPUTS`;
  actions are resolved by scanning `[A_XSDActionDependency]` methods across all assemblies
  via reflection and bound as `Action` delegates
- **Keybind groups** — XML files map to named groups (`keybindGroups`); swap active group
  with `SetActiveKeybindGroup(string)` e.g. per game mode or editor context
- **One ingress for every OS** — a platform backend posts `RawInputEvent`s (Key, Button,
  PointerMove, Scroll, ScrollContact, Text) to `InputHandler.Post`; `Drain()` runs first in
  `ActivateKeybinds()` and applies them in arrival order. `GlfwInput.Wire` is the desktop backend;
  `TouchpadInput` (Win32 HID) posts `ScrollContact`. No GLFW types inside `InputHandler`
- **The UI follows `InputHandler.primaryPointer`** (`PointerState`: id, kind, button masks, taps);
  mouse buttons also still reach the key tracker as `Keys`
- **One keybind for every platform** — maps never name a platform. On a Mac
  (`GestureMatcher.commandIsControl`) Cmd fills any Ctrl modifier or role, and rebind capture records
  Cmd as Ctrl. See `ClaudeMemory/Decisions/input-platform-layer.md`
- **`Keys.AnySymbol`** — special wildcard key; any character key press also fires keybinds
  registered to `AnySymbol`, used for text input listeners
- **`ICharacterInput`** interface — implement for widgets/controls that need raw char input
- **Held key repeat** — `repeatDelay = 0.35s` before repeat starts, `repeatRate = 0.01s`
  between repeats; tracked per-keybind via `repeatWatch` / `isRepeating`
- Mouse and keyboard share the `Keys` enum — mouse buttons are `MouseLeft`, `MouseRight` etc.
- **Do not** add new input processing outside `InputHandler` — all input flows through here

#### Bootstrapper — Key Facts
The XML rework has **landed**. There is no `BootstrapStage` enum and no `[A_BootstrapStage]`
attribute any more; both were replaced by declared phases in `Bootstrap.axml`.
- `Bootstrapper.Load(Paths.BOOTSTRAP)` reflects over all loaded assemblies once, collecting every
  static method tagged `[A_XSDActionDependency(name, "Bootstrap")]` into a name → method map
- `Bootstrap.axml` declares `<Phase Name="…">` elements holding ordered `<Step Action="…"/>`
  entries; the action string is the attribute's name, and `Bootstrapper.RunPhase(name)` invokes
  them **in the order the XML lists them**
- **Execution order is data, not reflection order** — to change what runs when, edit
  `Bootstrap.axml`; there is exactly one phase today, `"Bootstrap"`
- A step whose action name resolves to nothing is logged and skipped, not fatal
- Every bootstrap step returns `bool`. **A step returning `false` halts its phase** — nothing after
  it runs and `RunPhase` returns false. Steps that cannot meaningfully fail return `true`
- Comments in `Bootstrap.axml` mark the pre-renderer / post-renderer boundaries, which is what the
  old `PreGPUAPI` / `PostGPUAPI` stages encoded

#### Logging — Key Facts
`ArctisAurora.Core.Diagnostics`. A log call is a memory write and nothing else.
- **A channel per subsystem, held as a static readonly field** — `static readonly LogChannel Log =
  LogChannel.For("Renderer");` then `Log.Warn($"…")`. Never `Console.WriteLine` — there are none left
- **Levels:** `Hot Trace Debug Info Warn Error Fatal Off`. **Only `Hot` is `[Conditional("DEBUG")]`**
  and is gone from Release entirely; the rest stay runtime-gated so a shipped build can be asked for
  verbose output. `Warn` and above also print `@File.cs:line`
- **A disabled level costs a field load and a predicted branch.** The `[InterpolatedStringHandler]`
  constructor's `out bool` makes the compiler emit none of the `AppendFormatted` calls, so the
  interpolation holes are never evaluated. There is one handler type per level because
  `[InterpolatedStringHandlerArgument("")]` only carries the receiver
- **`Log.Every(ms)` / `Log.Once()`** gate on the call site, keyed by `[CallerFilePath]` +
  `[CallerLineNumber]`. Use them for anything inside the frame loop
- **Do NOT log per frame.** `LastTickMs` and `GpuEngineStats` already exist and go to the shaders —
  that is telemetry, not logging
- **One SPSC lane per thread** (`LogLane`). `Core.Diagnostics` must not reference `Core.Data` —
  `DataPool` and `DataManager` log, so the dependency only runs one way
- **The drain is a plain background thread, not a `ThreadedSystem`** — it owns no pools. Do not
  convert it
- **Self-starting**, because `XSDGenerator` logs before `Engine.Init`. `Logging.Configure` is step 2
  of `Bootstrap.axml` and replays what boot logged before it; `Logging.Flush` is the last `Commit`
  step of `Shutdown.axml`
- **File spills on watermark, `FlushMs`, or any `Error`**, and rotates at `MaxFileMB`. Config is
  `LoggingSettings` (`<Logging>`), so it cascades and can be overridden per user
- The channel floor is the **minimum across all sinks**, not the console's — the flight recorder
  captures below what anything prints
- See `ClaudeMemory/Decisions/engine-logging.md` and `Engine/Systems/LOGGING.md`

#### Shutdown — Key Facts
The bootstrap sequence run backwards, and deliberately the same shape — `Shutdown.cs`,
`Shutdown.axml`, `[A_XSDActionDependency(name, "Shutdown")]`, `Shutdown.RunPhase(name)`, `bool`
returns that halt a phase.
- **Two phases, and the difference matters.** `Request` may refuse: it is where anything that asks
  the user something lives. `Commit` is past the point of no return and runs against a tree that is
  still live, before any teardown
- **A step that must ask the user returns `false` and re-enters.** It opens its prompt, returns
  false to halt the attempt, and the prompt's callbacks call `Shutdown.Resume()`, which re-runs the
  sequence — one question per attempt, in order. Cancelling simply never resumes, which is what
  leaves the application running. `Shutdown.Request()` starts a *fresh* attempt and clears what a
  previous one remembered
- A `Commit` step returning false is logged and skips the rest of its phase, but the application
  still exits — otherwise one broken handler makes it unquittable
- `Window.Close` on `Engine.primary` calls `Shutdown.Request()`; any other window settles its own
  notes through `NoteActions.SettleWindow` and closes alone
- The OS close request (Alt+F4, taskbar, `WM_CLOSE`) takes the same `Window.Close` path, posted from
  a GLFW close callback; ghost and plain context-menu windows ignore it
- **Do not** put a prompt in a `Commit` step — nothing there can suspend or refuse

#### Rendering — Key Facts
The full Vulkan pipeline is working and rendering UI:
- Instance, device, swapchain, render passes, pipelines, shader loading — all present
- **Current pain point:** CPU-side widget logic — positioning, parenting, children, scaling
- **Set 0 is the renderer's, not a module's.** `Renderer` owns a global descriptor set holding
  `GpuEngineStats` (per-system tick times, total/wrapped time, frame index), one host-visible mapped
  buffer per swapchain image, built in `Renderer.PrepareDescriptors`. A module's own sets start at 1
- **Per-frame buffer writes have two owners:** `Renderer.Draw` writes the global ones after the
  timeline wait, then calls each module's `UpdateFrameData(imageIndex)` for its own. Do NOT put
  module-specific buffer updates back into `Draw`
- **Buffer memory is chosen by write frequency, not by buffer type.** Rewritten every frame →
  `AVulkanBufferHandler.CreateMappedBuffer`, which prefers `DEVICE_LOCAL|HOST_VISIBLE|HOST_COHERENT`
  and falls back to plain host-visible, with **one buffer per swapchain image**. Uploaded once →
  `CreateBuffer<T>`, which stages into `DEVICE_LOCAL`. Textures always stage — optimally-tiled images
  cannot live in host-visible memory on any platform. Nothing branches on ReBAR; the memory-type
  query is what absorbs PC/Android/Apple differences
- **Never read back through a mapped pointer** — the target may be write-combined, where one read
  costs orders of magnitude. Whole-struct `Unsafe.Write` and `WriteMappedRange` are forward-only and
  safe; `ptr->field = x`, `+=` or a read-to-compare are not.
  See `ClaudeMemory/Decisions/mapped-streaming-buffers.md`
- Module command buffers are recorded **only when dirty**, so anything that must change per frame has
  to change through memory a stable descriptor already points at — not through a re-record
- Shaders live in **four physical copies** (`AuroraEngine/`, `Thorium/`, `AuroraEditor/Shaders/`,
  `Carbon/Shaders/`), because they are loaded by a `"../../../Shaders/…"` path relative to the
  running app's own folder. Edit the `AuroraEngine` copy, compile with
  `glslc --target-env=vulkan1.3`, copy the `.spv` to all four — the UI `.spv` are SPIR-V 1.6 and must
  stay byte-identical across projects. `Carbon/` carries only the UI path's four
  (`UIEngine/UIEngine.*`, `Modules/Compositor/compositor.*`)
- Do NOT redesign the Vulkan pipeline; focus help on the scene/widget graph layer above it

#### Asset Registry — Key Facts
- `AssetRegistries` is a **type-indexed library of typed dictionaries** — each registry
  is a `Dictionary<TKey, TValue>` stored in two parallel lookups:
  - `library`: keyed by **value Type** (e.g. `typeof(AVulkanMesh)`)
  - `libraryByName`: keyed by **string name** (e.g. `"Meshes"`)
- Registry definitions are **XML-driven** — `Registry.axml` declares each dictionary's
  name, key type, and value type; parsed at bootstrap via `ParseXML()`
- Type resolution uses `AnyXMLType.typeMap` with fallback to `AnyXMLType.FindType()`
  for engine-specific types
- Retrieval API: `GetAsset<T>(name)`, `GetRegistryByValueType<K,V>()`,
  `GetRegistryByName<K,V>()`
- Bootstrap steps: pre-renderer `AssetRegistries.InstantiateRegistries` + `RegisterSerializableTypes`;
  post-renderer `PrepareDefaultAssets`, `PreloadAssets`, `PrepareAllAssets` (meshes, fonts, textures, styles)
- Assets derive from abstract `Asset` with `LoadAsset()` and `LoadDefault()`
- Serializable types are auto-discovered via `[Serializable]` attribute + reflection,
  stored as `Dictionary<uint, Type>` with hashed IDs
- **Do not** assume direct field-based asset access — always go through the registry API

#### ECS — Key Facts
- Architecture is not finalised — ask before assuming storage strategy
- Components and entities exist; query/iteration pattern TBD

#### XSD / Data Layer — Key Facts
- `XSDGenerator` reflects over all loaded assemblies at runtime and emits `.xsd` schema
  files to `Paths.XMLSCHEMAS` — fully automatic, no manual schema authoring
- **Three attribute types drive everything:**
  - `[A_XSDType(name, category)]` — marks a class/struct/enum/interface as an XSD type;
    supports `AllowedChildren`, `MinChildren`, `MaxChildren` for element constraints
  - `[A_XSDElementProperty(name, category)]` — marks a field or property as an XSD
    attribute (scalars) or element (collections/lists)
  - `[A_XSDActionDependency(name, category)]` — marks a static/instance method as a
    callable action reference in XML
- **Output files** generated per run:
  - `{Category}TypeSchema.xsd` — one per category, contains complex + enum types
  - `AllTypesSchema.xsd` — union of all known type names across all categories
  - `actionSchema.xsd` — all `[A_XSDActionDependency]` methods as string enumerations
- **Type resolution** uses two maps:
  - `MemberMap` — C# primitives → `xs:*` names (used when generating member types)
  - `typeMap` — reflection-built map of `[A_XSDType]`-annotated types → their XSD names
  - `AnyXMLType.typeMap` — inverse map used at XML parse time to resolve strings → `Type`
- **`AnyXMLType.FindType(string)`** resolves an XSD type name string back to a C# `Type`
  by scanning `[A_XSDType]` attributes across all assemblies — used by `AssetRegistries`
  during `Registry.axml` parsing
- **Do not** hand-edit generated `.xsd` files — they are overwritten on each run
- **Do not** add new primitive mappings to `MemberMap`/`AnyXMLType.typeMap` separately —
  they must be kept in sync manually (both maps exist, one for each direction)
  
### What Claude Should NOT Do
- Don't refactor the Vulkan pipeline internals
- Don't assume a specific ECS storage model
- Don't replace XSD with another serialization approach unprompted
- Don't introduce new NuGet dependencies without flagging it first

### Editor
Depends on: **Engine project**

#### Purpose
Visual editor for MyEngine. Built on top of the Engine — uses the same ECS, rendering, and asset systems.

#### Status
Early stage. Core editor shell is being set up.

#### Architecture Notes
- Editor is a consumer of the Engine, not a fork of it
- UI is rendered through Engine's Vulkan pipeline
- [Add: docking, panels, tool windows — describe once they exist]

#### What Claude Should Know
- Editor-specific code lives here; Engine internals are described in the Engine section above
- Don't duplicate engine logic in the editor — extend via engine APIs

### TextEditor — Context File
Obsidian/Notion-style note-taking and document app.

#### Status
Working note app: vaults, a rich-text document editor with undo, notes on disk as XML, Markdown or plain text.

**Still in development, with no users.** Nothing has shipped, so a change to Thorium's formats or baked assets
needs no upgrade or migration code: re-import or re-bake in the dev build instead (e.g. bump
`AssetImporter.importerVersion`).

#### Goals
- Rich text editing

#### Architecture Notes
- Renders through the Engine's Vulkan UI stack (`ArctisAurora.Core.UI`); no UI of its own