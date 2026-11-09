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
- `Deflater` encode is ~7 % slower than classic zlib 6; optimised 2026-11-06 (update below), still ~1.1x .NET's zlib-ng on Windows on atlas-like data only (2026-11-08, after the 4-byte chain update); on UI-screenshot goldens it is faster than zlib-ng since the 2026-11-08 update.
- Atlas-style chain-walk gap to zlib-ng (~1.1x) remains; libdeflate-style 4-byte chains + one-slot 3-byte table landed 2026-11-08 (last update); `deflate_medium` not done. Goldens output still 1.4 % larger than zlib-ng.
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

## Update 2026-11-08 — inserts inside matches

**What changed (`Arctis.Imaging.Deflater`, all private; no public signature changed):**
- New const `MinRunFill = 16`.
- New `Encoder.InsertRun(from, to)`: inserts from..to inside a one-byte run; sets `head[Hash(from)] = to` and `prev[i] = i - 1`, split where the window wraps at `WMask`.
- New `Encoder.Hash(pos)`: 3-byte multiplicative hash from one unaligned 32-bit read masked to 24 bits; byte form for the last position (`pos + 3 >= n`). `Insert` calls it.
- New primitive `Ascending(int* dst, int value, int count)`: dst[k] = value + k; Vector512 / Vector256 / Vector128 tiers plus scalar tail (index vectors spelled out, `VectorN<T>.Indices` is .NET 9+ and the library also targets net8.0).
- `Encoder.Run` match branch: computes the insert end once (`min(last, maxInsert)`) instead of testing per byte; local `longMatch = 16 * maxLazy`. Length > longMatch inserts only `last`; else distance 1 and length >= `MinRunFill` goes through `InsertRun` for s+2..s+L-3, then the last two positions are inserted normally.

**Why.**
- Corpus changed: the 12 font/icon atlases are ADF now (aab68a7), so PNG encoding today is mostly test goldens/actuals/diffs (`TestRunner`), `DocumentEditorControl` image saves, KTX2 zlib and PDF streams. Primary corpus = Thorium's 62 golden PNGs (143 139 058 filtered bytes); the atlases (extracted from `aab68a7^`, 133 187 456 bytes) stay as the large-image regression check. User agreed.
- Diagnosis: on goldens `Deflater` was 156-174 ms vs zlib-ng 62-66 ms; at MaxChain 4 it was still 124 ms, so the cost was the per-byte insert of long flat runs, not the chain walk.
- Run fill is output-identical: inside a distance-1 match every position s+2..s+L-3 hashes to the same bucket whose head is s+1 (inserted just before), so zlib's per-byte loop produces prev[i] = i - 1 and head = s+L-3 exactly. Only distance 1 is detected (implies equal bytes for free); other periods would need hash-collision handling.
- Step 2 (user asked to try it): zlib-ng's `insert_match` rule, skip interior inserts for matches > 16 x max_insert_length (= MaxLazy; 257-258 at defaults). Changes output; it is the only output-changing part. The dropped attempt in the 2026-11-06 update ("not inserting positions deep inside byte runs", +5 % time, +1.4 % size) was a different rule; this one measured faster on both corpora. Landed; the user confirmed keeping it.
- Not done: libdeflate-style 4-byte chains + one-slot 3-byte table (step 3), `deflate_medium`.

**Measurements.** Windows, AMD Ryzen 9 9900X, Release net10.0, scratch console bench (not a test), 3 processes x 6 passes, passes 3-5 range; HEAD = a copy of the pre-change `Deflater` in the same process.

| corpus | HEAD ms | step 1 ms | step 1+2 ms | zlib-ng Optimal ms |
|---|---|---|---|---|
| 62 goldens | 157-173 | 58-60 | 46-52 | 63-69 |
| 12 atlases | 827-871 | 780-830 | 759-784 | 547-587 |

| corpus | HEAD bytes | step 1 bytes | step 1+2 bytes | zlib-ng bytes |
|---|---|---|---|---|
| 62 goldens | 1 329 774 | 1 329 774 | 1 329 096 | 1 311 129 |
| 12 atlases | 16 142 144 | 16 142 144 | 16 158 570 | 16 433 998 |

- Goldens ~3.3x faster than HEAD, ~1.3x faster than zlib-ng, still 1.4 % larger. Atlases ~8 % faster; zlib-ng still ~1.4x faster there (chain walk dominates, untouched). Step 2 costs +0.10 % size on atlases, -0.05 % on goldens.

**Verification.** test-verified (unit): `dotnet test ArctisImaging.Tests -f net10.0` 459 passed. Bench: step 1 alone byte-identical to HEAD on 12/12 atlases and 62/62 goldens; with step 2 every output inflates back through `ZLibStream`; 284 lengths (0-40, +/-40 around 5552, 11104, 1 MiB) inflate and checksum through `ZLibStream`; same checks pass with DOTNET_EnableAVX512F=0, DOTNET_EnableAVX2=0 and DOTNET_EnableHWIntrinsic=0. ArctisImaging builds clean for net8.0 and net10.0 (Release). Not checked: ARM, Linux, in-engine suite (`_Build/test.sh`), PDF output in a viewer, small-input (PDF glyph stream) timing.

## Update 2026-11-08 — symbol writing, chain hopping dropped

**What changed (`Arctis.Imaging.Deflater`, all private; no public signature changed):**
- New work-layout consts `SymTable = 1916` (512 uints) and `DistTable = 2428` (32 uints); `WorkSize` 1916 -> 2460.
- `Encoder.WriteSymbols` is branch-free. Per block it builds `table[512]` (literal c at c; length-3 value v at 256 + v, extra bits pre-merged; entry = code | bit count << 24) and `dist[30]` (code | code length << 16 | code + extra length << 24).
- Per symbol: mask `m = -distance >> 31`; `table[value + (256 & m)]` is written in one `Put`; the distance part is written with value and length ANDed with m (a literal writes 0 bits). Still two `BitWriter.Put` calls per symbol.
- `LongestMatch` unchanged (attempt B below reverted). Output byte-identical to the previous (step 1+2) output.

**Profile that drove it** (Windows, Ryzen 9 9900X, Release net10.0, scratch bench with Stopwatch timers around WriteBlock / WriteSymbols / Adler32, one pass, after step 1+2):

| corpus | total ms | search + tally ms | trees + header ms | symbols ms | Adler ms | total at MaxChain 4 ms |
|---|---|---|---|---|---|---|
| 62 goldens | 47 | 39 | 1 | 6 | 1.6 | 31 |
| 12 atlases | 802 | 722 | 8 | 70 | 2.5 | 354 |

- Counts per pass: goldens 0.92 M searches, 14.6 M chain steps, 6.4 M single inserts, 8.1 M run-filled positions; atlases 10.1 M searches, 231 M chain steps (22.9/search), 76 M single inserts, 17 M run-filled, 24.1 M `Common` calls, 4.1 M literals, 5.4 M matches.
- Goldens are 98.9 % zero bytes after filtering, atlases 78.6 %. Chain walk ~16 ms of goldens, ~450 ms of atlases (default vs MaxChain 4).

**SIMD map.** `Common`, `Adler32`, `Ascending` and the `PngEncoder` filters all have 512/256/128 tiers. Not vectorisable in a useful way: the chain walk (each step's address is the previous load), bit writing (each symbol's position depends on all earlier bits), inserts (scatter where neighbours share a bucket; an 8-wide hash compute would save little since the cost is the head load/store).

**A (kept): branch-free symbol writing.** Hypothesis: literal/match branch mispredicts (~7 ns/symbol). Symbol phase on atlases 70 -> 55 ms (one pass), so mispredicts were only part of it; still ~5.8 ns/symbol. Remaining suspect is `bits` living in the Encoder struct (acc/fill/pos round-trip memory per `Put`) - not measured; the 2026-11-06 update already recorded "a local BitWriter copy in WriteSymbols no change".

| corpus | before ms | after A ms | bytes (both) |
|---|---|---|---|
| 12 atlases | 736-751 | 725-742 | 16 158 570 |
| 62 goldens | 46-51 | 47-54 | 1 329 096 |

3 processes x 6 passes, passes 3-5; "before" = step 1+2 code copied into the same process. Atlases ~1.5-2 % faster; goldens unchanged within noise.

**B (tried, reverted): zlib-ng's LONGEST_MATCH_SLOW chain hopping.** After a better match of length L, follow the hash chain of the offset 0..L-3 whose next entry is furthest back, plus the head of the hash at L-4; candidate = chain entry - offset, limit shifted by offset; any offset whose chain ends ends the search. One process, pass 5:

| corpus | before ms | B ms | before bytes | B bytes |
|---|---|---|---|---|
| 12 atlases | 742 | 851 | 16 158 570 | 16 142 635 |
| 62 goldens | 51 | 66 | 1 329 096 | 1 328 126 |

Slower on both (the per-improvement scan of up to L prev entries costs more than the steps it saves; zlib-ng only uses it at level 9), so reverted under the agreed rule (keep only if both corpora faster and atlases grow <= 0.1 %).

**Hazard for any future offset-chain attempt.** `prev` comes from an `ArrayPool` buffer that is never cleared, and step 2 (and the end of input) leaves positions uninserted. A hop reads `prev[matchStart + i]` for such positions; inside the first 32 KB of a call that is garbage from an earlier `Deflate` call and can point forward, i.e. a candidate >= strstart (distance <= 0, `Common` reading past the input). The plain chain walk is safe because it only follows entries of inserted positions and stops at `limit`. Any offset-chain technique needs `prev[0..min(n, WSize))` cleared per call and must not treat a hole as a chain end.

**Verification.** test-verified (unit): `dotnet test ArctisImaging.Tests -f net10.0` 459 passed. Bench: A byte-identical to the previous (step 1+2) output on 12/12 atlases and 62/62 goldens, also under DOTNET_EnableAVX512F=0, DOTNET_EnableAVX2=0, DOTNET_EnableHWIntrinsic=0; 284 lengths inflate and checksum through `ZLibStream`. ArctisImaging builds clean (Release, net8.0 + net10.0). Not checked: ARM, Linux, in-engine suite, PDF output in a viewer.

**Known gaps.** Symbol writing still ~5.8 ns/symbol on atlases, cause not measured. Atlas chain-walk gap to zlib-ng unchanged (~1.4x). Not done: libdeflate 4+3-byte hash (landed in the update below; gap now ~1.1x), `deflate_medium`, a register-held bit accumulator in `WriteSymbols`.

## Update 2026-11-08 — 4-byte chains and a 3-byte slot

Supersedes the "15 hash bits (multiplicative over 3 bytes ...)" detail above for the chains (the slot is still 3-byte). The 2026-11-06 option (c) rejection still stands for a plain 4-byte hash.

**What changed (`Arctis.Imaging.Deflater`, all private; no public signature changed; output changes):**
- `Deflate`: `chains` rented as `2 * HashSize + WSize` (was `HashSize + WSize`), laid out `head`, `head3`, `prev`.
- New field `Encoder.head3` (int*): the 3-byte slot, one entry per bucket holding the latest position; cleared `1 << hashBits` entries per call, as `head`.
- `Encoder.Insert(int pos)` -> `Insert(int pos, out int near)`: one 32-bit load, 4-byte chain hash `v * 2654435761 >> hashShift`, slot hash `(v & 0xFFFFFF) * 2654435761 >> hashShift`; returns the chain head, `near` = the slot's previous position; both loads happen before the three stores (prev, head, head3). Inserts inside matches pass `out _`.
- Removed `Encoder.Hash(int pos)`; new `Encoder.Bytes(int pos)`: 4 bytes at pos, 3 at the last position (`pos + 3 >= n`), shared by `Insert` and `InsertRun`.
- `Encoder.InsertRun` fills to `last - 3` (was `last - 2`) because a position now needs 4 bytes inside the run; also sets `head3`. Still output-identical to a per-byte insert loop under the new hash.
- `Encoder.Run`: a search runs when `prevLength < lazy` and either the chain head is within `MaxDist` or the slot's position is within `TooFar`.
- `Encoder.LongestMatch(strstart, cur, near, prevLength, ref matchStart)`: with no match of >= 3 yet, checks `near` first (3 bytes, then `Common` to extend); then walks the 4-byte chain only if its head is valid. Class summary: "zlib's deflate_slow over 4-byte hash chains and a 3-byte slot".

**Why: the profile above put ~60 % of atlas time in the chain walk at ~2 ns per step, latency-bound on `prev` (128 KB, in L2), so only fewer steps help.** A 4-byte chain shares 4 bytes, so far fewer candidates per walk; the 3-byte slot recovers 3-byte matches, which TOO_FAR (4096) already limits to nearby ones.

Measured in scratch first (both round-trip through `ZLibStream`), Release net10.0, 3 processes, passes 4-5:

| corpus | before ms | plain 4-byte ms | 4-byte + 3-byte slot ms | before bytes | plain 4-byte bytes | 4+3 bytes |
|---|---|---|---|---|---|---|
| 12 atlases | 738-784 | 539-554 | 601-627 | 16 158 570 | 16 642 400 | 16 157 249 |
| 62 goldens | 46-47 | 43-48 | 47-49 | 1 329 096 | 1 329 591 | 1 329 395 |

Plain 4-byte stays rejected (+3.0 % on atlases, larger than zlib-ng). The user chose 4+3 knowing the goldens trade.

**Insert order: both loads before the stores.** On zero bytes both hashes are 0, so `head[0]` and `head3[0]` are exactly 128 KB apart; storing `head3` before loading `head` made the load wait on the store (4K aliasing). Goldens 49 -> 47-48 ms in scratch, output byte-identical. Tried, no help: moving `head3` by 64 B (16 ints). (This departed from the agreed plan with the user's approval.)

Final measurement of the repo build (3 processes x 6 passes, passes 3-5; "before" = the step 1+2 Deflater copy in the same process, i.e. before branch-free symbol writing, worth ~1.5-2 % on atlases; zlib-ng = .NET `ZLibStream` Optimal):

| corpus | before ms | now ms | zlib-ng ms | before bytes | now bytes | zlib-ng bytes |
|---|---|---|---|---|---|---|
| 12 atlases | 764-837 | 607-645 | 543-601 | 16 158 570 | 16 157 249 | 16 433 998 |
| 62 goldens | 47-48 | 48-49 | 63-68 | 1 329 096 | 1 329 395 | 1 311 129 |

Atlases now ~1.1x zlib-ng (was ~1.4x) at 1.7 % smaller output; goldens still ~1.3x faster than zlib-ng.

**Verification.** test-verified (unit): `dotnet test ArctisImaging.Tests -f net10.0` 459 passed. Bench: the repo build's sizes equal the scratch variant's exactly on both corpora; every output inflates back to its input through `ZLibStream`; 284 lengths inflate and checksum through `ZLibStream`; also under DOTNET_EnableAVX512F=0, DOTNET_EnableAVX2=0 (before the reorder) and DOTNET_EnableHWIntrinsic=0. ArctisImaging builds clean for net8.0 and net10.0 (Release). Not checked: ARM, Linux, in-engine suite, PDF output in a viewer, small-input timing.

**Known gaps.** Goldens ~1-2 ms slower than before; their remaining chain walk is in zero runs (every zero position has the same 4-byte hash, so it does not shorten those chains); a zero-run shortcut in the walk is unmeasured, not done. Atlases still ~1.1x zlib-ng.

Related: [[managed-inflate]], [[png-decode-fast-paths]], [[ktx2-container]]
