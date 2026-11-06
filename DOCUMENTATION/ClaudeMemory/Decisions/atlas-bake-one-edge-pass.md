# Decision — the MTSDF bake evaluates each edge once per texel and allocates nothing

**Date:** 2026-10-02
**Scope:** `ArctisAurora.Core.Generators` — `MTSDFGen.GenerateCell`, `ComputeWindingNumber`, `SolveCubic`,
`SolveQuadratic`; `ArctisAurora.Core.Filing` — `AuroraFont.GenerateGlyphAtlas`, `IconSet.GenerateIconAtlas`;
`ArctisAurora.Core.Filing.Serialization` — `AssetImporter.ImportFont`; `ArctisAurora.Tests` — `AtlasPerfTests`

## What changed
- `GenerateCell` walks the edges once per texel: one `ClosestTOnBezier` per coloured edge feeds every channel the
  edge carries; the true channel is `min(R, G, B)`; winding is computed once and negates all four
- `GetClosestDistanceOfChannel` deleted — it ran the whole edge list and the winding once per channel, 4× per texel
- `SolveCubic` / `SolveQuadratic` write into a caller's `Span<float>` and return the root count;
  `ComputeWindingNumber` holds one `stackalloc float[3]` for all edges
- Zones `Atlas.ReadFaces`, `Atlas.WriteMeta`, `Atlas.Cells` (with `#Cell`), `Atlas.SaveAdf` (was `Atlas.SavePng`) in the three bake entry points
- Perf test `Perf.AtlasBake` (suite `Fonts`): icon set `default`, arial Latin, cambria Math ×3 faces, one bake per
  frame, into `%TEMP%\AuroraAtlasBake` — never the tracked `Data/Fonts` / `Data/Icons`. Clears the folder at start,
  so the last run's output stays for byte comparison. Does no work in Debug or plain Release (`SKIP`)

## Why these choices

**Every edge has two of the three colours, so the four channel passes evaluated each edge three times.**
The true channel took the min over every coloured edge, which is exactly `min` of the three channel minima — the
single pass is the same float compares in the same order, and the bake is **byte-identical** (`.png`, `.agd`,
`.aid`, `.afm`, all three bakes, `cmp` against a pre-change Release bake).

**`ClosestTOnBezier` is ~94% of a cell; winding was ~4%.** `dotnet-trace` sampled-thread-time over the cambria bake,
before the change. The allocation fix is about memory, not time: 8.9 GB per cambria bake, all `float[]` root arrays.

Measured, Release+`PROFILE`, `--test=Fonts`, 3 runs each side, `Atlas.Cells` per bake:

| bake | cells | before ms (3 runs) | after ms (3 runs) | per cell before → after | alloc before → after |
|---|---|---|---|---|---|
| icons `default` | 23 | 506 / 509 / 506 | 180 / 182 / 180 | 22.0 → 7.8 ms | 77 MB → 0.1 KB |
| arial Latin | 114 | 4,796 / 4,805 / 4,796 | 1,554 / 1,548 / 1,557 | 42.1 → 13.6 ms | 730 MB → 0 KB |
| cambria Math ×3 | 879 | 57,848 / 57,677 / 57,653 | 30,350 / 17,944 / 17,864 | 65.7 → 20.4 ms | 8.9 GB → 7.3 KB |

Cambria's first after-run (30.4 s) is an outlier against two runs at 17.9 s and a before-spread under 0.4%; the
per-cell figure uses runs 2–3. `ReadFaces`, `SavePng`, `WriteMeta` together are under 1% of any bake.

**Not done, by choice for now:** parallel cells (cells are independent, 16 cores here); edge culling by control-polygon
bounds against the running minimum; fewer than 32 coarse samples in `ClosestTOnBezier` (changes output, needs goldens).

## Known gaps
- `Perf.AtlasBake` has no `<Budget>` — limits not set yet
- `--test` filters by suite, so every run of it bakes cambria (~20 s)
- Release runs of `--test=Fonts` report `FAIL Boot` from `failed to load the default sampler asset` — predates this
  change and is outside the bake

Related: [[engine-profiling]], [[asset-manifest-and-import]], [[atlas-is-unorm-not-srgb]]
