# Decision — png deflate: Arctis's own zlib/DEFLATE compressor, no System.IO.Compression at runtime

**Date:** 2026-11-05
**Scope:** `Arctis.Imaging` — `Deflater`, `DeflateSettings`, `PngEncoder`, `ImageCodec`, `Image`; `ArctisAurora.Core.Pdf` — `PdfDocument`; tests `DeflaterTests`

## What changed
- NEW `Arctis.Imaging.Deflater` (public static): `Deflate(ReadOnlySpan<byte> input, Stream output, in DeflateSettings settings = default)` writes one zlib stream (header 0x78 0x9C, blocks, Adler-32).
	- Private `Encoder` struct: `Run`, `Insert`, `LongestMatch`, `TallyLiteral`, `TallyMatch`, `WriteBlock`, `WriteSymbols`. Private `BitWriter` struct.
	- Trees region: `BuildLengths`, `MinimumRedundancy`, `Canonical`, `RunLengths`. Primitives: `Common`, `LengthCode`, `DistCode`, `Adler32`, ...
- NEW `Arctis.Imaging.DeflateSettings` (public readonly struct, `Types.cs`): `GoodLength`, `MaxLazy`, `NiceLength`, `MaxChain`; constants `DefaultGoodLength = 8`, `DefaultMaxLazy = 16`, `DefaultNiceLength = 128`, `DefaultMaxChain = 128`.
	- A zero or negative field takes its default (same convention as `ImageLimits`).
	- Internal `Check()` throws `ArgumentException` for lengths outside 3-258 or chain below 4.
- CHANGED signatures (optional trailing parameter, existing callers unchanged): `ImageCodec.EncodePng(..., in DeflateSettings deflate = default)`, `Image.SavePng(Stream, in DeflateSettings = default)`, `Image.SavePng(string, in DeflateSettings = default)`.
- CHANGED `PngEncoder.Encode`: still Sub/Up per row; now filters every row into one pooled buffer and calls `Deflater.Deflate` (was `ZLibStream` at `CompressionLevel.Fastest`).
- CHANGED `PdfDocument.Deflate` -> `Deflater.Deflate` (was `ZLibStream` Optimal); `using System.IO.Compression` removed.
- No runtime code uses `System.IO.Compression` any more. The two test projects keep `ZLibStream` on purpose.
- NEW tests `DeflaterTests`: `RoundTripsThroughBothInflaters` (4 settings incl. zlib level 4- and 9-like), `ChecksumMatchesZlib`, `EmptyInputIsTheStandardEmptyStream`, `IncompressibleInputIsStored`, `OutOfRangeSettingsAreRejected`.
- No data XML read or added.

## Why these choices

**Own deflater, no `System.IO.Compression` at runtime (user, 2026-11-05).**
The user wanted the zlib dependency gone and only their own libraries used.
Tests keep `ZLibStream` as an independent reference (user's choice): testing `Deflater` only against `Inflater` would pass a bug both share.

**Settings are zlib's four tuning numbers, not levels (user: "like zlib has levels, let us just have parameters").**
zlib 1.3.1 `deflate.c` `configuration_table`: each level is (good, lazy, nice, chain) plus a loop; level 6 = 8, 16, 128, 128 with `deflate_slow`. Those are the defaults.
- chain = earlier positions tried per byte
- nice = stop searching at a match this long
- lazy (max_lazy) = do not look one byte ahead for a better match when the current one is already this long
- good = when the previous match is this long, search with a quarter of the chain
- Fixed, not settings (zlib leaves them across levels too): 15 hash bits (multiplicative over 3 bytes since 2026-11-06, see the update below; was zlib's shift of 5 per byte), blocks of 16 383 symbols (zlib memLevel 8), TOO_FAR 4096 (3-byte matches further back are dropped), MAX_DIST 32 506 (32 768 - 262 lookahead).
- MaxChain must be >= 4: zlib's chain walk with good >= prev would otherwise reach 0 and never stop (zlib's own stated requirement).
- No greedy `deflate_fast` loop, so zlib levels 1-3 cannot be reproduced; no named levels.

**Close to zlib, not byte-exact (user chose option a, 2026-11-05).**
The match search is a port of `deflate_slow` / `longest_match` (same hash, chain, lazy, good/nice, TOO_FAR, MAX_DIST, block length).
Each block is written dynamic / fixed / stored, whichever is smallest (zlib's `_tr_flush_block` byte comparison; stored only when the block's bytes <= 65 535).
Huffman lengths use Moffat-Katajainen in-place minimum redundancy plus a Kraft-sum length limit (15 bits, 7 for the code-length code), not zlib's `trees.c`, so output bytes differ from zlib's.
Code-length runs follow zlib's `scan_tree` / `send_tree` (litlen and distance trees encoded separately). At least two codes per tree, as zlib forces.
Option (b), a byte-exact port of zlib including `trees.c` so a test could assert identical bytes against classic zlib, was deferred: roughly double the code, zlib-licence "altered source" marking for the port, and every later speed-up would have to keep byte-identical output.

**.NET's `ZLibStream` is zlib-ng since .NET 9, not classic zlib.**
Its level 6 uses the same four numbers but `deflate_medium` and a 16-bit hash; `CompressionLevel.Optimal` = level 6, Fastest = 1 (`deflate_quick`).
Python's `zlib` in the Linux container links classic zlib 1.3, used as the true level-6 reference.

## Measurements
Linux cloud container, Release, .NET 10.0.114, second of two runs per encoder, scratch benchmark (not a test). Input = Thorium's 12 committed atlas PNGs decoded and re-filtered Sub/Up = 133 187 456 bytes of filtered rows.

| Encoder | Bytes | Time |
|---|---|---|
| `Deflater` default | 16 144 872 | 2 387 ms |
| classic zlib 1.3 level 6 (Python) | 16 144 894 | 2 226 ms |
| `ZLibStream` Optimal (zlib-ng 6) | 16 144 894 | 2 308 ms |
| `ZLibStream` Fastest | 18 657 453 | 941 ms |

- `Deflater` is within 22 bytes of zlib 6 in size and about 7 % slower than classic zlib.
- Re-encoding those 12 atlases with `PngEncoder` (Sub/Up + `Deflater` default) gives 16 145 556 bytes against 16 281 409 committed (ImageSharp, Paeth-adaptive).
- This supersedes the "PNG size grows 15.5 -> 22.5 MB" claim in [[png-decode-fast-paths]], which was measured with `ZLibStream` Fastest. The re-bake itself was NOT done (`AssetImporter.importerVersion` still 9).

## Verification
- **test-verified (unit)**: `dotnet test ArctisImaging.Tests -f net10.0` -> 124 passed, 0 failed (81 before; 43 new across this and [[ktx2-container]]). Round-trips go through both Arctis `Inflater` and .NET `ZLibStream`.
- In-engine suite (`_Build/test.sh`, Thorium net10.0, Linux Xvfb + lavapipe): 455 passed, 55 failed, 8 skipped on this change and on a clean HEAD worktree run alongside; identical failing-test lists and error kinds, 0 `[Vulkan]` lines. The 55 are the known lavapipe golden failures. `Tex.Pdf.Structure`, `Tex.Pdf.Text`, `Tex.Pdf.Bullets` pass (they inflate PDF streams now written by `Deflater`).
- Builds clean: ArctisImaging and Thorium net10.0 on Linux.
- NOT checked: Windows (solution build not run); PDF output opened in a viewer; boot time effect. NOT GUI-verified.

## Known gaps
- Byte-exact zlib port (option b) deferred.
- `Deflater` encode is ~7 % slower than classic zlib 6; optimised 2026-11-06 (update below), still ~1.55x .NET's zlib-ng on Windows.
- Atlases not re-baked; `importerVersion` still 9.

## Update 2026-11-06 — Windows measurements

Scratch console bench (not a test), Release, net10.0, Windows. Input = Thorium's 12 committed atlas PNGs decoded to RGBA8; `ImageCodec.EncodePng`, 6 passes per process, warm median ms for all 12; 3 alternating process pairs.

| encoder | warm median ms | bytes |
|---|---|---|
| old: ZLibStream Fastest (4810b00^) | 327-331 | 23 589 906 |
| new: Deflater default (HEAD 4810b00) | 1183-1224 | 16 145 556 |
| reference: 4810b00^ with CompressionLevel.Optimal | 730-737 | 16 445 630 |

- New is ~3.6x slower than the old encoder and 0.684x its size (per atlas 0.668-0.703); slightly smaller than the committed atlas files (16 281 409 bytes).
- At equal effort `Deflater` is ~1.6x slower than .NET's Windows zlib (Optimal) and 1.8% smaller. The Linux table above had them near-equal (2387 vs 2308 ms), so the speed gap is Windows-specific; it supersedes "about 7 % slower" for Windows.
- Output check: for all 12 atlases the old output, new output and committed PNG decode to byte-identical RGBA8; each new file's IDAT inflates cleanly with System.IO.Compression's `ZLibStream`.
- Not checked: ARM.

## Update 2026-11-06 — encode speed-ups

**What changed (`Deflater` only; no public signature changed):**
- `LongestMatch` pre-checks a candidate with two 16-bit compares (bytes 0-1 and bestLen-1..bestLen), zlib's UNALIGNED_OK form; was four byte compares. Output byte-identical.
- `Insert` hash: `(p0 | p1 << 8 | p2 << 16) * 2654435761 >> 17` (still 3 bytes, 15 bits); was zlib's `(p0 << 10 ^ p1 << 5 ^ p2) & mask`. Changes output. `HashMask` removed.
- `Adler32`: SSSE3 path (16 bytes a step, `SumAbsoluteDifferences` + `MultiplyAddAdjacent`, blocks of 5552); scalar loop kept for the tail and non-x64.
- Static tables `lengthCodeOf[256]`, `distCodeOf[512]` (zlib's `_length_code` / `_dist_code` shape) replace `LengthCode` / `DistCode` in `TallyMatch` and `WriteSymbols`; the functions now only build the tables.
- `WriteSymbols` writes code + extra bits in one `Put`; `BitWriter` writes through a pointer into the pinned buffer (was `buffer.AsSpan(pos)`).
- Second pass, same day: `Common` compares 32 bytes a step with `Vector256` before the 8-byte loop. Output byte-identical (16 142 144 B); 0.968 / 0.977 / 0.986 of the previous time, 3 processes x 6 passes, same bench.
- Tried and dropped (scratch copy, same bench): chain links as 16-bit distances (table 128 -> 64 KB) ~10 % slower, the subtract lands on the dependent-load chain; hoisting `data` / `head` / `prev` into locals in `Run` (the JIT reloads them per insert, aliasing through `int*`) no change, they hit L1; a local `BitWriter` copy in `WriteSymbols` no change; distance-1 run shortcut in `LongestMatch` at most -4 %; not inserting positions deep inside byte runs +5 % time, +1.4 % size.
- Third pass, same day (rule review of `Deflater` + `PngEncoder`):
	- `Run` hoists `data`, `n`, `maxLazy` and `maxInsert` into locals (rule 39); no measured speed change.
	- Hash bits follow the input: `Math.Clamp(Log2(n) + 1, 8, 15)`, field `hashShift`; only `1 << bits` head entries are cleared per call. Inputs >= 16 384 bytes keep 15 bits and their output; smaller inputs (PDF Type 3 glyph procedures, one stream per glyph) change output. 237-byte stream: 3.0-3.9 -> 2.3 us per call; the rest is the per-block tree build.
	- `PngEncoder.Encode` reads rows straight from the source (copies only BGRA8 rows, to swizzle), filters 16 bytes a step under SSSE3 (`Ssse3.Abs` + `SumAbsoluteDifferences` for the residual sums), scalar for the first pixel and the tail; `up[0]` set once. Same filter choice per row, output byte-identical (12 atlases 16 142 828 B; BGRA8 input gives the same bytes as RGBA8 for 12/12). PNG side of `EncodePng` (encode minus `Deflate` alone) ~180 -> ~13 ms; `EncodePng` ~975 -> ~810 ms, 3 processes.
	- Dropped (user, 2026-11-06): sizing the `compressed` `MemoryStream` up front. Rule 2 does not apply, the count is not computable; only a bound (~ input size) is, ~10x the real output, so growing by doubling is cheaper.
- Search profile after both passes: input 78.6 % zero bytes; 10.1 M searches, 235 M chain steps; 9 % of searches start on zero bytes and take 38 % of the steps; 84 % of steps come after the search's final best match; 1.03 M searches exhaust the chain and take 45 % of the steps. Chain capped at 4 still costs ~330 ms (per-byte fixed cost of the lazy loop); at 128 the walk adds ~420 ms.

**Why (user chose option b, 2026-11-06).** Before the change, an instrumented copy split the 133 MB atlas run into search 89 %, `WriteSymbols` 7 %, Adler-32 3 %, trees 1 %. Counts: 133 M inserts, 10.7 M `LongestMatch` calls, 264 M chain steps (~25 per call), 24 M `Common` calls averaging 23 bytes. The walk is a dependent load per step, so cost is steps x latency.
- (a) keep zlib's hash: byte-identical output, pre-check alone ~1035 -> ~940 ms.
- (b) chosen: a better-spread 3-byte hash has fewer collisions on the chain; ~5 % more and 0.02 % smaller output.
- (c) rejected: 4-byte hash (zlib-ng's choice), ~610 ms but +3.0 % size (16 627 121 B), larger than .NET zlib's output; effectively a different level.
- Not tried: 16-bit chains with a sliding window, `deflate_medium`, greedy loop. .NET's speed lead comes from zlib-ng's `deflate_medium` algorithm, not from micro-work.

**Measurements.** Scratch console bench (not a test), Windows, Release, net10.0, input = Thorium's 12 atlases decoded and filtered Sub/Up (133 187 456 bytes), `Deflater.Deflate` default settings, 3 processes x 6 passes; per-process median ms.

| encoder | per-process median ms | bytes |
|---|---|---|
| before (HEAD 4810b00 `Deflater`) | 1068 / 1044 / 1023 | 16 144 872 |
| after | 867 / 854 / 840 | 16 142 144 |
| .NET `ZLibStream` Optimal (zlib-ng 6) | ~550 | 16 433 998 |

- ~18 % faster; .NET zlib-ng is still ~1.55x faster at 1.8 % larger output.

**Verification.** test-verified (unit): `dotnet test ArctisImaging.Tests -f net10.0` 124 passed. Bench also deflates lengths 0-40 and +/-40 around 5552, 11104 and 1 MiB (284 lengths) and inflates them through `ZLibStream`, which checks Adler-32: all match. net8.0 builds clean. Not checked: ARM / scalar Adler path on hardware, in-engine suite, Linux.

Related: [[managed-inflate]], [[png-decode-fast-paths]], [[ktx2-container]]
