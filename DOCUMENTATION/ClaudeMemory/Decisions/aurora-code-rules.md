# Decision — the `aurora-code` skill is the single authority on how C# is written

**Date:** 2026-10-28
**Scope:** `.claude/skills/aurora-code/SKILL.md`, `.claude/skills/aurora-code/collections.md`, `.claude/skills/aurora-code/pools.md`, `.claude/skills/aurora-code/types.md`, `.claude/skills/aurora-code/loops.md`, `CLAUDE.md`

## What changed
- New project skill `aurora-code`: 61 numbered hard rules; the reference files give right/wrong pairs for the ones a single line doesn't show. Covers performance first, allocation, collections, data pools, LINQ, types, loops, naming, formatting and comments. `SKILL.md` holds the rules; `collections.md`, `pools.md`, `types.md`, `loops.md` are reference files.
- `CLAUDE.md` loses "Simplicity First" (old §3), the §4 bullet "Match existing style, even if you'd do it differently", "Comments In Code" (old §6), and the Shared Conventions "Style" and "Naming" lines. The sections after them renumber (4→3, 5→4, 7→5, 8→6, 9→7, 10→8, 11→9, 12→10). §2's Analyze step tells Claude to load the skill when a change touches C#.
- No engine code changed. The rules name existing symbols only: `DataPool` (`GetSpan<T>`, `GetRef<T>`, `Write<T>`, `Append`, `Append(int)`, `Truncate`, `Rewind`, `Allocate`, `Free`, `MarkContentDirty`, `MarkRangeDirty`, `MarkOrderDirty`, `DenseOf`, `Alive`, `Backing<T>`, `CopyTo`, `CopyRange`, `OwnerAt`, `FrameEdge`, `AssertAccess`), `PoolCursor` (`TryConsumeContent`, `TryConsumeStructural`), `ColumnKey<T>`, `DataHandle`, `DataManager.Get`, `Jobs.For`, `XmlReflection` (`IsListMember`, `ChildListFields`, `ScalarMembers`), `AVulkanBufferHandler.WriteMappedRange`, `LogChannel` (`Log.Once()`).
- Data XML the rules refer to: `Pools.pools.axml`, `Frame.frame.axml`.

## Why these choices

**Scope is Aurora only and C# only.**
GLSL keeps the `shader-pipeline` skill, XML data files keep CLAUDE.md's XML rule, build scripts are out. Rejected: covering KotD/Unity too — Mono/IL2CPP flip several rules (e.g. jagged vs `[,]` arrays), so the rules would split per runtime.

**Whatever in CLAUDE.md clashed with the skill was removed from CLAUDE.md** (user decision).
Clashes found:
- Simplicity First's "minimum code / 200 lines could be 50" argued against writing explicit loops instead of LINQ.
- §4's "match existing style" argued for LINQ inside LINQ-heavy files.
- Comments and Naming are code-writing rules that would drift as two copies.

Rejected: a precedence line only inside the skill — CLAUDE.md is in context every session, a skill only when invoked, so the old sections would argue the other way whenever the skill is not loaded. Rejected: keeping §3/§6 as pointer headers to avoid renumbering — the user chose delete and renumber.

**Performance is the priority; when it pulls against brevity or readability, performance wins.**
"Scope of a change" opens with that, then least code within the rules. The 3.3 ms frame (300 fps), allocation-free frames, and "a slow system gets faster, not spread across threads; threaded code also runs on one core" are carried in from the user's standing rules.
2026-11-04: strengthened by the user to "multithreading is not an optimisation" — threading is never suggested or introduced to make something faster, not even as a later lever; rule 28's `Jobs.For` split now needs the user to ask, not "the single-thread loop can't get faster".

**No LINQ in any new code — runtime, boot, tooling, importers, tests** (user decision).
Evidence already in [[engine-testing]]: `GridListControl` allocated ~14 objects per measure + arrange (LINQ, `Select().ToArray()`) — 820 KB; `GanttChartControl.BuildRows` ~2.9 ms and 3.5 MB per rebuild at 2,000 tickets with a LINQ sort. Rejected: banning it only in code the frame reaches — that judgement call has already been wrong (`GridListControl` is layout code).
- About 490 LINQ-shaped calls across 100 files in the engine's `Core` code (regex count, 2026-10-28) stay until a change rewrites the statement.
- `ImplicitUsings` imports `System.Linq` everywhere, so the compiler never flags new LINQ. `<Using Remove="System.Linq" />` would force an explicit `using` in ~100 files — not done.

**`List<T>` only when the final count can't be known before filling, always with a capacity** ("for now" — user).
Exception: child collections of an `[A_XSDType]` must be `List<T>`. `XmlReflection.IsListMember` / `ChildListFields` recognise only `List<>`; a `T[]` member falls through to `ScalarMembers` and is written as an attribute.

**Formatting is the codebase's current majority style**, counted across the engine's `Core` code on 2026-10-28:

| Choice | Count | Against |
|---|---|---|
| Block namespaces | 303 files | file-scoped 0 |
| `using` above the namespace | all | inside 0 |
| Indentation | spaces | tabs in 1 file |
| Braces | Allman | — |
| Explicitly typed locals | 5,341 | `var` 126 |
| `out Type name` | 574 | `out var` 18 |
| `= new Type(` | 1,777 | target-typed `new(` 140 |
| `== null` / `!= null` | 1,312 | `is null` 11 |
| One-expression `=>` members | 989 | — |
| `#region` | 81 files | — |
| Lines over 120 columns | 2,190 | so no line limit |
| Private fields | plain camelCase 785 | `_camelCase` 434 (75 files, e.g. `DataPool`) |
| Public fields | camelCase 1,411 | PascalCase 3 |
| Constants | PascalCase 65 | UPPER_SNAKE 17 (Win32 header constants, 4 files) |
| `sealed class` | 215 declarations already | — |

Private fields: majority picked, no underscore; files already using `_` stay as they are.

**Naming rules come from the user's analysis of KotD `TriangulationByEdges.cs`.**
- A method's name form says its kind: predicates are questions, queries name what they return, actions are verbs.
- Name length tracks closeness to a formula: textbook letters in math, long names in control flow.
- One metaphor per algorithm, shared by names and comments.
- A prefix names the index a collection takes.

The example was adapted from `_layeredHulls[layer][submesh][point]` to flat arrays (`layerFirstRing[layer]`, `ringStarts[ring]`) because the skill bans nested collections and underscore fields.

**The data pool rules restate existing decisions; nothing new is decided there.**
Sources: [[ecs-rework-data-pools]], [[cross-system-change-notification]], [[layout-refs-across-children]], [[pool-shrink]], [[frame-scheduler]]. Layout's row `ref` held across child calls is named as the one accepted exception to "hold a row ref only where nothing can grow the pool".

**Rule 42a has one exception: Arctis.Imaging decode loops (user, 2026-11-02).**
They run once per image, so under tiering they stay tier-0 for the whole boot. Evidence: ~120 ms of boot `PreloadAssets` cold, ~20 ms with `DOTNET_TieredCompilation=0` (see [[managed-inflate]]). Cost accepted: no tier-1 PGO recompile for those methods. Effect of the applied flags is unmeasured. Rule text in the skill now ends "Exception: Arctis.Imaging decode loops, which run once per image (see managed-inflate)."

**Error handling is deferred** (user). "No exceptions for control flow" was dropped with it.

**Four rules were set by measurement.**
Setup: scratch console, Release, .NET 10.0.401, AMD Ryzen 9 9900X, Windows x64, `Stopwatch`, median of 9 after 100 warm-up calls and a 1 s pause for tier-1. Two runs, figures from run 2, run 1 in parentheses where it differed by more than 5%.

Rule 33 — struct parameters: `in` to read, `ref` to write, every size. Non-inlined call, ns/call:

| Size | By value | `in` |
|---|---|---|
| 8 B | 1.48 | 1.51 |
| 16 B | 1.88 | 1.48 |
| 32 B | 1.92 (2.18) | 1.49 (1.72) |
| 64 B | 5.41 (6.04) | 1.68 (2.24) |

Inlined 32 B: 0.75 vs 0.74. Rejected: "by value up to 16 B" — 16 B by value was ~27% slower at the median in both runs. Windows x64 passes only structs of 8 B or less in a register and copies anything larger. `in` was never slower.

Rule 4 — scratch buffers: `stackalloc` up to 1 KB, `ArrayPool` above, never `new`. ns per call:

| Case | Size | `stackalloc` | `ArrayPool` | `new` |
|---|---|---|---|---|
| Touch (write 2 bytes) | 256 B | 1.76 | 5.72 | 5.58 |
| Touch | 1 KB | 4.70 | 5.71 | 13.12 |
| Touch | 4 KB | 18.03 | 5.70 | 45.81 |
| Fill (write every byte) | 256 B | 2.27 | 6.81 | 7.06 |
| Fill | 1 KB | 7.89 | 8.69 | 19.77 |
| Fill | 4 KB | 27.76 | 25.25 | 68.86 |

The crossover is between 1 KB and 4 KB because `stackalloc` zeroes (AuroraEngine has no `SkipLocalsInit`).

Rule 9 — grids: flat `T[w*h]`, never `T[,]` or `T[][]`. 1024×1024 `int`, ns/cell:

| Case | Sum | Four-neighbour stencil |
|---|---|---|
| Flat `y*w+x` | 0.249 | 0.771 |
| Flat row span | 0.213 | — |
| Flat linear | 0.226 | — |
| Jagged, row hoisted | 0.237 | 0.769 |
| Jagged `a[y][x]` | 0.387 | — |
| `[,]` | 0.471 | 1.438 |

`[,]` is ~1.9× slower. Jagged matches flat only with the row hoisted and costs H+1 allocations, so the jagged ban rests on allocation count, not speed.

Rule 37 — turn arithmetic: `(d + 1) & 3`. ns/iteration over 200M: `& 3` 0.387; `% 4` on `int` 1.109 (2.9×); `% 4u` 0.372.

## Verification
- Not built — the change has no code.
- Checked: the skill appears in the session's skill list once written (loads by name). Every API name in the reference examples greps to the engine's `Core` code (`DataPool` members, `PoolCursor.TryConsumeContent`, `ArrangeData.arranged`, `AVulkanBufferHandler.WriteMappedRange`, `A_XSDElementPropertyAttribute(string, string)`, `Log.Once().Warn`, `Jobs.For(int, int, IJobFor)`). No `[open]` or `[benchmark]` markers remain.
- Not checked: the examples were not compiled; the benchmarks are microbenchmarks in a scratch console, not Aurora frames.

## Known gaps
- Nothing enforces the skill: no analyzer flags LINQ, `var` or underscore fields in new code.
- Existing code breaks many rules (~490 LINQ calls, 434 `_` fields, rationale comments such as `DataPool`'s) and is left as is.
- The error-handling section is not written.
- Benchmarks ran on one machine (Zen 5, Windows x64). The 8 B register limit is the Windows x64 ABI; on Linux (System V) 16 B structs travel in two registers — `in` stays never-slower there, but the 16 B gap may vanish.
- `Changelog.md` keeps an old "CLAUDE.md §10" reference unrenumbered, as history.

Related: [[engine-testing]], [[ecs-rework-data-pools]], [[cross-system-change-notification]], [[layout-refs-across-children]], [[pool-shrink]], [[frame-scheduler]], [[mapped-streaming-buffers]]
