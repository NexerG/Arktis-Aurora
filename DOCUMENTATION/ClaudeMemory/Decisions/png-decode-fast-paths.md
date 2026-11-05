# Decision — png decode fast paths: simd paeth, sub4, block inflate

**Date:** 2026-11-01
**Scope:** `Arctis.Imaging` — `PngDecoder`; `ArctisAurora.Core.Imaging` — `TextureLoader.UploadFile`

## What changed
- `PngDecoder.Sub4(Span<byte>)` and `PngDecoder.Paeth4(Span<byte>, ReadOnlySpan<byte>)`, called from `Unfilter` when bpp == 4. `Paeth4` only when `Vector128.IsHardwareAccelerated`.
- `DecodeRows` gains an `int block` parameter (rows per inflate read).
- `Decode` rents `lines` as `(block + 1) * (rowBytes + 1)`, with `block = clamp(64 KB / (rowBytes + 1), 1, height)`. Slot 0 holds the previous row; the last row is carried forward between blocks.
- ~~`[MethodImpl(MethodImplOptions.AggressiveOptimization)]` on `DecodeRows`, `Unfilter`, `Sub4`, `Paeth4`, `AddUp`.~~ **Superseded 2026-11-01**: removed (also from `NoteSegments.Patch`/`Match`) and banned by aurora-code rule 42a; see "Update 2026-11-01".
- ~~Interlaced path and bpp != 4 stay scalar, one row per read.~~ **Superseded 2026-11-01**: see "Update 2026-11-01 — all colour types, Adam7, fewer copies". `DecodeRows`/`DecodeInterlaced` no longer exist (now `DecodePass`).
- `TextureLoader.UploadFile`: permanent `Profiling.Zone` "Image.Decode" around `ImageCodec.Decode` into mapped staging. Only the fast path is zoned, not the CPU-copy path through `ImageFiles.Load`.
- Decoded output is byte-identical to before. No data XML changed.

## Why these choices

**Paeth undo is the cost, so it got SIMD.**
Thorium's 11 font atlases plus the default icon atlas are 15.5 MB of PNG decoding to 127 MB of RGBA8. ImageSharp baked them with Paeth on all 18,304 rows. Paeth has a serial dependency per pixel (the left neighbour must be finished first): ~130-200 ms scalar. Inflate alone was estimated at ~62 ms; superseded by the 2026-11-04 split below (inflate ~47% of decode in the Linux container). CRC is noise.

**The SIMD is across one pixel's 4 channels (16-bit lanes), not across pixels.**
The left-neighbour dependency forbids going across pixels. SIMD Paeth ~130 ms vs ~200 ms scalar on these atlases.

**Inflate in ~64 KB blocks of rows.**
~~Per-row `ReadExactly` on `ZLibStream` had measurable call overhead.~~ **Superseded 2026-11-01**: the inflate stage is now [[managed-inflate]]; block reads are gone.

**~~AggressiveOptimization on the hot loops.~~ Superseded 2026-11-01, see "Update 2026-11-01".**
They run once at boot, so tier-0 plus OSR cost ~90 ms of cold time. Cold-process bench: 311 -> 221 ms with that plus block inflate (Windows).

**Decoding straight into mapped staging is not the problem.**
Decoding into a PAGE_WRITECOMBINE buffer measured 135 vs 137 ms into a normal array. Rows are still unfiltered in pooled scratch and written forward once.

**Rejected for now: re-baking atlases with Arctis's own `PngEncoder`.**
It writes Sub/Up rows only (bump `AssetImporter.importerVersion` 9 -> 10). Decode would drop to ~100 ms warm / ~130 ms cold process, but PNG size grows 15.5 -> 22.5 MB (Fastest deflate), ~22 MB of binary churn lands in git, and the first boot re-bakes. The target was met without it. It stays the next lever, along with Optimal compression to win back size.
**Superseded 2026-11-05 (size claim only):** the 15.5 -> 22.5 MB figure was measured with `ZLibStream` Fastest. With `PngEncoder` now using `Deflater` at its default settings, a Sub/Up re-bake of Thorium's 12 atlases is 16.15 MB against 16.28 MB committed (Paeth, ImageSharp), slightly smaller, not larger. The re-bake itself was NOT done (`AssetImporter.importerVersion` still 9); the ~22 MB binary churn cost remains (every atlas PNG changes), but the size-growth argument no longer holds. See [[png-deflate]].

**Rejected: decoding atlases in parallel on workers.**
Single-core speed first (optimize-before-threading rule). Remains a later option: decode in parallel into staging, upload serially.

## Measurements
Release+PROFILE, Thorium `--profile=30`, Bootstrap thread, 3 runs each side, same machine, 2026-11-01. Before = the ImageSharp -> Arctis switch with the scalar decoder; after = this change.

| Metric | Before (ms) | After (ms) |
|---|---|---|
| `Preload.FontAsset` (temporary zone, 13 calls) | 885 / 681 / 692 | 435 / 433 / 432 |
| `AssetRegistries.PreloadAssets` | 1,035 / 722 / 733 | 478 / 467 / 466 |
| Atlas decode total (`Tex.Decode` temporary zone = `Image.Decode` now) | 648 / 544 / 546 | 306 / 304 / 303 |
| Bench warm decode of the 12 atlases | 270 | 210 |

- User target for `Preload.FontAsset` was < 600 ms, roughly what ImageSharp did.
- In-engine decode (~300 ms) is above the bench (~210 ms warm) because of cold-process effects: JIT of the rest and first touch of fresh staging pages.
- Earlier ImageSharp numbers: warm decode of the atlases ~205 ms, cold ~385 ms; PreloadAssets 612 ms of a ~1.1 s boot.
- Extends the existing memory that ImageSharp is being replaced by an in-house loader for license and security reasons.

## Update 2026-11-01 — AO banned, ImageSharp comparison, Linux perf setup

### What changed
- `[MethodImpl(MethodImplOptions.AggressiveOptimization)]` removed from `Arctis.Imaging.PngDecoder` (`DecodeRows`, `Unfilter`, `Sub4`, `Paeth4`, `AddUp`) and `ArctisAurora.Core.UI.Notes.NoteSegments` (`Patch`, `Match`), with their unused `using System.Runtime.CompilerServices`.
- aurora-code SKILL.md rule 42a (numbered 42a so 43-61 keep their numbers): no AggressiveOptimization.
- New `ArctisAurora.Tests.ImageDecodePerfTests`, actions `Perf.ImageDecode.Atlases.Arctis`, `.Atlases.ImageSharp`, `.Formats.Arctis`, `.Formats.ImageSharp`, registered in `Perf.tests.axml` (Timeout 60000 atlases / 180000 formats, NO `<Budget>`).
	- Atlases: the 11 Thorium font atlases (`Fonts/*/*_atlas.png`) plus `Icons/default/default_atlas.png`, cycled into 64 separate `byte[]` copies; one decode per frame into one reused RGBA8 buffer; zones `Image.Decode.Arctis.Atlas` / `Image.Decode.ImageSharp.Atlas`.
	- Formats: a 1024² crop of the arial atlas, encoded at setup by ImageSharp as RGBA8, RGB8, Grey8, RGBA16 x filters None/Sub/Up/Average/Paeth, plus RGBA8 Adam7, plus (ImageSharp only) JPEG Q90 and GIF; 64 decodes per row; zones `Image.Decode.<Arctis|ImageSharp>.Png.<Colour>.<Filter>`, `.Png.RGBA8.Adam7`, `.Jpeg.Q90`, `.Gif`.
	- ImageSharp side is `Image.Load<Rgba32>` + `CopyPixelDataTo` into the same buffer. No warm-up pass.
- Linux tooling: `_Build/PerfTests.sh` builds Thorium `-f net10.0` Release+PROFILE with `-p:EnableWindowsTargeting=true` and runs `--test=Perf` under `xvfb-run` with `VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json` (lavapipe); its Windows branch mirrors `PerfTests.cmd`. `.claude/skills/aurora-perf/profile.ps1` is cross-platform under pwsh (`$IsLinux` picks the `net10.0` bin and Linux build line, launches via xvfb-run + lavapipe; captures from `[Environment]::GetFolderPath('ApplicationData')`, `~/.config` on Linux). `summarize.ps1` unchanged. aurora-perf SKILL.md gained the Linux build line.
- Cloud-session rules: CLAUDE.md §7 two bullets (commit as `NexerG <68482683+NexerG@users.noreply.github.com>`; never commit unless asked). `.claude/agents/aurora-committer.md` commits with `-c user.name/-c user.email`. New `.claude/hooks/session-start.sh` + `.claude/settings.json` SessionStart hook sets that identity when `CLAUDE_CODE_REMOTE=true`.

### Why
**AggressiveOptimization is banned (user request: avoid it in general).**
It skips tiered compilation, so a method never gets the Dynamic PGO tier-1 recompile. Cold-start cost is to be cut with data and design instead. This supersedes the section above that credited it with ~90 ms of the 311 -> 221 ms Windows cold-process win.

**The comparison is an engine perf test, not a standalone bench (user choice).**
BenchmarkDotNet rejected: a new NuGet dependency. ImageSharp is already referenced by `AuroraEngine.csproj`.

**No `<Budget>` on the decode tests (user agreed).**
One atlas decode is 20-180 ms, over the 8 ms Max cap every budget carries; they measure, not gate.

**64 separate copies, so repeated files are not served from a warm cache.**

**`profile.ps1` made cross-platform instead of bash/Python twins** — one script for both OSes.

**`--test=` filters by suite name only,** so runs use `--test=Perf` and read the `Image.Decode.*` zones.

### Measurements
Release+PROFILE, Linux cloud container, lavapipe (CPU Vulkan) under xvfb, 3 runs with the attribute (AO) + 3 without, 2026-11-01. Per-decode zone ms, ranges across runs.

| row | Arctis+AO p50 | Arctis p50 | ImageSharp p50 (6 runs) | Arctis/IS median p50 |
|---|---|---|---|---|
| Atlas (64, mixed 512²-2432²) | 31.2-33.7 | 31.2-37.5 | 29.6-35.7 | 1.02 |
| Atlas total per 64 (ms) | 2994-3316 | 2974-3133 | 3190-3535 | — |
| Atlas max | 117-183 | 122-129 | 210-486 | — |
| Png.RGBA8.None/Sub/Up/Paeth | 11.3-16.7 | 11.3-19.7 | 11.6-16.6 | 0.92 / 0.99 / 0.93 / 1.07 |
| Png.RGBA8.Average | 16.1-16.6 | 20.7-25.9 | 13.9-14.3 | 1.56 |
| Png.RGBA8.Adam7 | 30.3-32.5 | 31.4-31.6 | 20.5-26.1 | 1.45 |
| Png.RGB8 None/Sub/Up/Avg/Paeth | — | 13.2-19.4 / 17.4-26.0 / 12.5-19.1 / 19.7-32.7 / 26.5-27.9 | 10.7-12.7 / 12.2-15.0 / 10.3-11.9 / 14.1-16.7 / 25.6-32.0 | 1.14 / 1.31 / 1.22 / 1.46 / 1.00 |
| Png.Grey8 None/Sub/Up/Avg/Paeth | — | 10.3-10.9 / 12.4-13.2 / 10.8-11.2 / 13.2-15.1 / 16.7-19.2 | 7.0-7.6 / 8.8-10.4 / 7.4-9.8 / 9.6-13.0 / 13.6-20.6 | 1.50 / 1.28 / 1.36 / 1.36 / 1.19 |
| Png.RGBA16 None/Sub/Up/Avg/Paeth | — | 33.2-33.8 / 44.4-45.7 / 33.9-36.0 / 51.3-62.0 / 69.8-78.1 | 27.4-35.7 / 32.1-36.0 / 28.2-32.6 / 35.7-44.2 / 62.1-85.0 | 1.18 / 1.37 / 1.21 / 1.42 / 0.98 |
| Jpeg.Q90 / Gif | unsupported | unsupported | 14.2-18.7 / 14.1-20.6 | — |

- On the real atlas set Arctis ≈ ImageSharp at p50 and ~5-10% less total. Its cold max is far lower because boot already JIT-warmed Arctis (atlases are preloaded through it) while ImageSharp's first decode pays its own JIT: p50 is the fair column, max is not.
- Off the RGBA8 fast paths (RGB8, Grey8, RGBA16, Adam7, the Average filter) Arctis is 1.2-1.56x slower.
- Removing AO left the atlas set unchanged and showed slower p50 on some scalar rows (RGBA8/RGB8 Average, Sub; RGBA16 Sub/Average), but the without-AO ranges are wide and the CPU is shared with lavapipe's render thread: an observation, not a result.

### Verification (update)
- **Test-verified**: ArctisImaging.Tests 50/50 on Linux (net10.0). Release+PROFILE Thorium builds clean; all 4 `Perf.ImageDecode` tests PASS in all 6 runs; zero `[Vulkan]` lines in all 6 runs. Hook script run once: git identity set.
- Other Perf tests fail their budgets here (52-55 failures): budgets are per machine and Render/Draw runs on lavapipe (CPU); not a regression signal.
- Not checked: Windows runs of the new tests and of the `profile.ps1` Windows branch; `profile.ps1` not executed on Linux (only `PerfTests.sh` was); boot-time cost of removing AO (`Preload`/`PreloadAssets` zones via `--profile`); Debug build/test suite not rerun.

### Known gaps (update)
- JPEG and GIF decode absent in Arctis (`ImageFiles` still falls back to ImageSharp).
- ~~No fast paths for RGB8 / Grey8 / 16-bit / interlaced / Average filter~~ — done, see "Update 2026-11-01 — all colour types, Adam7, fewer copies".
- AO-removal effect on boot unmeasured; numbers are from a noisy CPU-rendered container and should be repeated on Windows.
- The Linux container needed hand-installed packages (not in repo): dotnet-sdk-10.0 (Ubuntu; Microsoft's download host is blocked by the network policy), mesa-vulkan-drivers, vulkan-validationlayers (Release requests `VK_LAYER_KHRONOS_validation`; without it instance creation fails with ErrorLayerNotPresent), libminizip1t64 (libassimp.so needs libminizip.so.1, otherwise PreloadAssets crashes), pwsh via `dotnet tool install PowerShell`.
- The planner twin in `D:\Repositories\ThoriumNotes` is not reachable from the cloud container.

## Verification
- **Test-verified** for the decoder: ArctisImaging.Tests 50/50, including RGBA8 under every filter.
- SHA-256 of all 12 real atlases decoded by the old build and the new one match.
- Engine suite `_Build/test.sh`: 508 passed, 8 failed, 2 skipped, 0 new. Failures are Boot (pre-existing baseline error) and 7 LatinModern Tex goldens, identical px counts and deltas to a run from before this change.
- No `[Vulkan]` lines in the profile runs.
- Not checked: Carbon/AuroraEditor hosts, interlaced or non-RGBA PNG performance.

## Known gaps
- The 7 LatinModern golden failures and the Boot baseline error predate this change and are unexplained here.
- No perf test with a Budget locks in the `Image.Decode` cost.
- Atlases are still Paeth-baked by ImageSharp; the re-bake is an open option.
- ~~bpp != 4 (RGB8, grey, 16-bit) and interlaced images take the scalar path.~~ Superseded 2026-11-01, see the update below.

## Update 2026-11-01 — all colour types, Adam7, fewer copies

### What changed
All private, in `Arctis.Imaging.PngDecoder`; no public API change.
- ~~Block reads / two ping-pong halves below~~ **Superseded 2026-11-01**: `DecodePass` now unfilters in place in one whole raw buffer inflated by `Inflater`; see [[managed-inflate]].
- `DecodeRows` + `DecodeInterlaced` merged into `DecodePass(inflate, c, palette, lines, span, x0, y0, dx, dy, bpp, rgba, packed, destination, pitch, layout)`. Progressive = the single pass (0,0,1,1); interlaced = 7 Adam7 passes.
	- Each pass inflates up to `span` bytes of rows per `ReadExactly` (`span = block * (rowBytes + 1)`, block as before from 64 KB), alternating between two halves of `lines`. A block's first row reads its previous row from the other half (no carry-row copy). A zero row sits after the two halves.
	- `Decode` rents `lines` as `(2 * block + 1) * (rowBytes + 1)` (was `block + 1`).
- `Expand(raw, rgba, width, c, palette)` -> `Expand(raw, output, width, step, c, palette, bgra)`: writes RGBA8 or BGRA8 directly, pixel x at `x * step * 4`.
	- RGBA8/BGRA8 output writes straight into the destination row (Adam7: at the pass's final pixel positions via `step = dx`).
	- R8/RG8 output still goes Expand -> `rgba` scratch -> `PixelRows.FromRgba` (Adam7: -> `packed` -> per-pixel scatter).
	- Grey8 no-key, step 1: Vector128 shuffle widens 16 grey bytes to 64 output bytes. RGB8 no-key, step 1: shuffle 12 -> 16 bytes, OR alpha. RGBA16 step 1: 2 pixels per iteration, byte-swap shuffle, widen, `(v*255+32895)>>16` (same as `To8`), narrow. Other cases: pointer loops with depth/key tests outside the hot cases.
	- New helpers `Store(byte*, uint)` (little-endian store) and `Big16(byte*)`.
- Filters: new `Sub8` (64-bit SWAR), `Average4` / `Average8` (SWAR: per byte `(a & b) + ((a ^ b & 0xFE...) >> 1)`, then Sub4's bytewise add), `PaethWide` (Paeth4's 16-bit-lane SIMD on 8 lanes for bpp 3/6/8; lanes past the pixel get no prediction so they store back as read; scalar tail), `PaethScalar(cur, prev, bpp, start)`. Remaining scalar Sub/Average loops use pointers (no bounds checks). `Sub4`, `Paeth4`, `AddUp` unchanged.
- Test: `Arctis.Imaging.Tests.PngTests.EveryDepthDecodesUnderEveryFilter(colorType, depth)`: colour types 0/2/4/6 x depth 8/16 x filters 0-4 x progressive/Adam7 x RGBA8 and BGRA8 output, width 37 x height 11, expected RGBA8 computed in the test.

### Why
**Copies are minimised (user: "if there are copies - don't, use as less as possible").**
Each destination byte is written once and never read. The one remaining copy (unfiltered scratch row -> destination) stays because the destination may be write-combined mapped staging (`TextureLoader` decodes into it) and unfiltering needs the previous row; reading it back from the destination is the forbidden mapped readback. Copies per output byte: other colour types 2 -> 1; Adam7 3 -> 1 (RGBA8/BGRA8 output); per-block carry row 1 -> 0.

**Merging `DecodeRows` and `DecodeInterlaced` gives Adam7 passes the block reads without duplicating the ping-pong loop.**

**No JIT flags (rule 42a); all SIMD is portable `Vector128` behind `Vector128.IsHardwareAccelerated`, with scalar fallbacks.**

**Where the time went before** (scratch Stopwatch bench, quiet CPU, 1024² crop, median of 40): inflate equal to ImageSharp's; the gap was all after inflate: span-indexed per-byte filter loops (bounds checks), per-pixel Expand with per-pixel depth/key tests plus a second copy, and Adam7's per-pixel `Span.CopyTo` and one `ReadExactly` per row.

**Left out:** fusing R8/RG8 output into Expand (a third set of Expand loops, rejected), RGBA8 Paeth (`Paeth4` untouched; 1.02-1.08, within noise), inflate, palette and sub-byte depths (no perf row), JPEG/GIF.

### Measurements
Release+PROFILE, Linux container, lavapipe under xvfb, `Perf.ImageDecode` p50 ms per decode. Before = 3 runs after AO removal; after = 3 runs; ImageSharp pooled over 9 runs; ratio = median after / median ImageSharp.

| row | before | after | ImageSharp | ratio |
|---|---|---|---|---|
| Atlas (64) | 31.2-37.5 | 30.9-38.5 | 29.0-39.5 | 0.98 |
| Grey8 None/Sub/Up/Avg/Paeth | 10.3-19.2 | 4.5-10.6 | 6.5-20.6 | 0.87/0.65/0.58/0.71/0.72 |
| RGB8 None/Sub/Up/Avg/Paeth | 12.5-32.7 | 9.6-17.8 | 9.7-30.9 | 0.88/0.91/0.91/0.93/0.64 |
| RGBA16 None/Sub/Up/Avg/Paeth | 33.2-78.1 | 20.4-30.2 | 27.1-85.7 | 0.76/0.61/0.75/0.63/0.38 |
| RGBA8 None/Sub/Up/Avg | 11.3-25.9 | 11.8-15.7 | 11.6-16.3 | 0.93/0.99/0.97/0.99 |
| RGBA8 Adam7 | 31.4-31.6 | 21.6-26.5 | 20.6-26.9 | 0.99 |
| RGBA8 Paeth | 16.5-19.7 | 16.7-19.8 | 15.7-16.8 | 1.08 |

### Verification
- **Test-verified**: ArctisImaging.Tests 58/58 (50 old + 8 new theory cases).
- Byte-identical: SHA-256 of decoded output for 61 images (RGBA8/RGB8/Grey8/RGBA16/RGB16/GreyA8/GreyA16/Grey16 x 5 filters, every type Adam7, 12 atlases) x 4 layouts (RGBA8, BGRA8, R8, RG8) = 244 hashes identical before vs after.
- Perf: all 4 `Perf.ImageDecode` tests PASS in 3 runs, zero `[Vulkan]` lines.
- Not checked: Windows; big-endian; ARM; engine Debug suite; boot-time Preload zones.

### Known gaps
- RGBA8 Paeth still ~1.02-1.08x ImageSharp.
- R8/RG8 output still copies through the `rgba` scratch row (and `packed` for Adam7).
- JPEG/GIF decoders still absent.
- Numbers from a noisy CPU-rendered container; repeat on Windows.

## Update 2026-11-02 — RGBA8 unfilter stores straight to the destination

### What changed
- `PngDecoder.Unfilter(type, src, cur, prev, dst, bpp)` gains `dst`; `Sub4`, `Average4`, `Paeth4`, `AddUp` take `Span<byte> dst` and store every finished word to both `cur` and `dst`; the None filter copies to `dst` too; non-accelerated Paeth at 4 bpp copies `cur` to `dst` after `PaethScalar`.
- `DecodeRow` passes the destination row as `dst` when `p.direct && layout == RGBA8` (colour type 6, depth 8, non-interlaced pass, 4 bytes per pixel) and returns without `PixelRows.FromRgba`; otherwise it passes `cur` as `dst`, so the second store lands on the same address and there is no per-word branch.
- The decode loops here also carry `AggressiveOptimization`, see [[managed-inflate]].

### Why
**One pass per row instead of two.** The atlas path unfiltered into `cur`, then `FromRgba` copied `cur` to the destination. The destination is mapped staging memory: the extra stores are whole 32-bit / 128-bit words, ascending, never read back, the same pattern as the copy they replace.
Left out: BGRA8 fusing (swizzle on store) and the 8 bpp kernels, since no direct path uses 8 bpp.

### Verification
- **Test-verified**: ArctisImaging.Tests 79/79. Byte-identical: SHA-256 of 356 decodes (89 PNGs x RGBA8 / BGRA8 / R8 / RG8) identical to the `ZLibStream`-era decoder. No timings taken.

### Known gaps
- Effect on boot time unmeasured. BGRA8 not fused.

## Update 2026-11-04 — Paeth shorter chain and 4-row wavefront

### What changed
- `PngDecoder.Paeth4Rows(src, stride, prev, dst, pitch)` — new. Unfilters four rows at once from `src` (each row starts with its filter byte). `prev` is the unfiltered row above the group and is overwritten in place with the group's 4th row; `dst` is the first destination row, `pitch` the destination row step. Row k+1's upper and upper-left neighbours come from row k's registers, not memory.
- `PngDecoder.PaethStep(a, b, c, x, low)` — new, `AggressiveInlining`. Paeth predict + add on one pixel's 4 channels in 16-bit lanes; `pa = |b-c|` and `b-c` are computed off the left-neighbour dependency chain.
- `PngDecoder.Widen4(byte*)`, `PngDecoder.Narrow4(Vector128<short>)` — new helpers.
- `PngDecoder.Paeth4` — body now uses `PaethStep`/`Widen4`/`Narrow4` (shorter chain); signature unchanged.
- `PngDecoder.Decode` drain loop: new local `grouped = layout == RGBA8 && Vector128.IsHardwareAccelerated`. Takes `Paeth4Rows` when `grouped && p.direct && p.ph - p.j >= 4`, 4 rows whole in the window, and all 4 filter bytes == 4. Destination is `(p.y0 + p.j * p.dy) * pitch`, step `p.dy * pitch` (Adam7's last pass is `direct` too). Advances `p.j += 4` with no cur/prev swap. Otherwise the existing `DecodeRow` path.
- `Paeth4Rows` carries `AggressiveOptimization` under the rule-42a exception for Arctis.Imaging decode loops, see [[managed-inflate]].
- Tests: `PngBuilder.Build` gains optional `byte[]? rowFilters` (row y uses `rowFilters[y % length]`); new `PngTests.MixedFiltersMatchReference` (301x233 RGBA8, mixed filter pattern, fills 1 / 7 / 4096 / rowBytes / 1<<30, compared to the source pixels).
- No data XML touched, no public API change.

### Why
**Measure first: Paeth is latency-bound, not throughput-bound.** Scratch bench (Release, net10.0, Linux container, shared 4-core Xeon 2.8 GHz, 12 atlases = 16.3 MB PNG to 133.2 MB inflated, all 18,304 rows Paeth, all RGBA8): warm whole decode ~404-422 ms = CRC ~16 (4%) + inflate ~176-207 (47%) + Paeth ~196-215 (49%); walk/slide/bookkeeping ~0. Cold first pass only 30-60 ms above warm, so tier-0 JIT is no longer significant. Paeth ran ~6 ns/px (~17 cycles/px) against ~5 cycles of work if independent.

**Four rows in a wavefront beat two.** Variants in a scratch bench (same data, 3 runs, ms median per run, all byte-identical):

| variant | run 1 | run 2 | run 3 |
|---|---|---|---|
| HEAD `Paeth4` | 205.6 | 208.8 | 204.9 |
| shorter chain | 189.2 | 183.1 | 191.0 |
| 2-row wavefront | 129.1 | 128.1 | 124.2 |
| **4-row wavefront (chosen)** | 122.3 | 108.7 | 105.0 |
| packed lanes (row n pixel i + row n+1 pixel i-1 in one `Vector128`) | 321.8 | 307.9 | 308.7 |

4 rows beat 2 in every run. Packed lanes rejected: the shuffle it puts on the chain costs more than the second pixel gains.

**No third row buffer.** The user preferred cache-friendly computation over extra buffers. The wavefront hands rows over in registers; only the group's last row is stored to the single row buffer, overwriting `prev[i]` after row 0 has read it. Per row on the fused path ~1.25 loads + 1.25 stores (src + amortised prev; dst + amortised prev) against 2 + 2 before. The user's view: PNG is not cache-unfriendly in working set (window ~288 KB in L2, a 9.7 KB row in L1); it is serially dependent (Paeth left neighbour, Huffman bit position).

**Group path only for the fused RGBA8 output.** BGRA8, R8, RG8 and non-direct passes keep the single-row path (a group would need a buffer per row). A group needs 4 whole rows; otherwise single-row `Paeth4` runs (window edges, pass ends). There is no 2-row fallback kernel.

### Measurements
Scratch Stopwatch bench, Release, net10.0, Linux container (shared Xeon 2.8 GHz), 12 boot atlases, median of 10 warm passes per process, 5 processes alternating HEAD (e126c36) / new (inflate fast loop included, see [[managed-inflate]]):

| stage | HEAD (5 runs) | new (5 runs) |
|---|---|---|
| whole decode | 367.0-380.4 ms | 269.2-305.7 ms |
| inflate (whole-buffer `Inflater.Inflate`) | 163.9-183.3 ms | 152.0-159.3 ms |
| single-row Paeth unfilter (shorter chain only; the 4-row path is not in this stage) | 194.7-201.6 ms | 178.6-192.1 ms |
| CRC | 12.3-15.2 ms | 11.8-12.5 ms |

Cold first decode of all 12 (3 runs each, sequential): HEAD 427-521 ms, new 375-465 ms. The isolated stages overstate a little (they read input from DRAM, not the L2-hot window), so stages sum to more than the whole decode.

### Verification
- **Test-verified**: ArctisImaging.Tests 80/80 (79 existing + `MixedFiltersMatchReference`), Release, net10.0, Linux. A first-draft Adam7 bug (group rows placed at `p.j` instead of `y0 + j*dy`) was caught by 4 failing interlaced tests and fixed before this result.
- Byte-identical: SHA-256 of all 119 PNGs in the repo (excluding bin/obj) x RGBA8/BGRA8/R8/RG8 = 476 decodes, identical between the HEAD `Arctis.Imaging.dll` and the new one, 0 errors.
- aurora-codereview: scan 0 hits; one open question on rule 33 (`in` for `PaethStep`'s `Vector128` parameters), not applied.
- Not checked: Windows; boot PreloadAssets zone (`--profile`); engine `Perf.ImageDecode` tests; Debug engine suite; ARM/big-endian; the non-accelerated `Vector128` path (falls back to existing code); ratio vs ImageSharp.

### Known gaps
- Ratio vs ImageSharp not re-measured; the 0.5x target is still open.
- Boot effect on Windows unmeasured (`AssetRegistries.PreloadAssets`).
- Rule 33 question on `PaethStep`'s vector parameters open.
- BGRA8 / R8 / RG8 Paeth stays single-row.
- Atlases are still Paeth-baked; re-baking with Up/Sub remains an open lever.

Related: [[atlas-bake-one-edge-pass]], [[asset-manifest-and-import]], [[engine-profiling]]
