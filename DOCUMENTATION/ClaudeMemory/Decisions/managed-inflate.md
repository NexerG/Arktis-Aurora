# Decision — managed inflate: Arctis's own zlib/DEFLATE decoder for PNG

**Date:** 2026-11-01
**Scope:** `Arctis.Imaging` — `Inflater`, `PngDecoder`; tests `Arctis.Imaging.Tests.InflaterTests`

## What changed
- New `Arctis.Imaging.Inflater` (internal static). Entry `Inflate(ReadOnlySpan<byte> png, int firstIdat, Span<byte> output)`: fills `output` exactly or throws `ImageFormatException`.
	- Structs only: `Bits` (cursor over consecutive IDAT chunk bodies read in place, 64-bit bit buffer, steps into the next IDAT chunk in its slow `Refill`); `Tables` holding `LitlenTable` `[InlineArray(2342)]`, `DistTable` `[InlineArray(402)]`, `CodeTable` `[InlineArray(128)]`, as a stack local.
	- Private: `Codes` (hot loop), `Copy`, `Stored`, `ReadDynamic`, `BuildFixed`, `Build` (canonical Huffman; 11-bit litlen root, 8-bit distance root, 7-bit code-length root, sub-tables sized as zlib sizes them), `Leaf`, `Reverse`, ~~`Adler32` (Vector128)~~ (removed 2026-11-02, see "Adler-32 dropped").
- **Windowed (2026-11-02, supersedes the first landing's whole buffer).** `Inflater` is resumable: `internal struct State` (`s` Bits, `t` Tables, `block`, `final`, `done`, `storedLeft`, `adler`), `Begin(ref State, byte* png, int length, int firstIdat)`, `byte* Fill(ref State, byte* start, byte* o, byte* outEnd, bool last)`, `Finish(ref State)`; consts `MaxMatch = 258`, `History = 32768`. `Bits`, `Tables` and the table types are internal (nested in `State`). `Codes` stops when `o` reaches a limit; `Stored` copies what fits and carries `storedLeft`; `Adler32(uint adler, byte*, int)` is running. `Inflate(png, firstIdat, output)` stays as Begin, one Fill over the whole output, Finish.
- `PngDecoder.Decode(data, destination, pitch, layout, int fill)` (internal; the 4-arg overload forwards with `DefaultFill = 1 << 18`) rents a window of max(`History`, rowBytes + 1) + fill + `MaxMatch`, clamped to the image's total inflated size (small images: one window, no slides). A fill stops within `MaxMatch` of the window end so a match is never split; on the last window the fill runs to the stream's end so the "longer than the image" / "ends early" checks keep their meaning. The slide keeps max(32 KB history, the unfinished row).
- `PngDecoder`: private `struct Pass` (index, x0, y0, dx, dy, pw, ph, stride, j, direct) and `NextPass(ref Pass, passes, in Chunks)`; `DecodePass` became `DecodeRow(src, cur, prev, in Chunks, palette, in Pass, bpp, rgba, packed, destination, pitch, layout)`; the `zero` buffer is gone (prev is cleared at each pass start). `Unfilter(type, src, cur, prev, bpp)` and every kernel (`Sub4`, `Sub8`, `Average4`, `Average8`, `Paeth4`, `PaethWide`, `PaethScalar`, `AddUp`) read the filtered row from `src` and write `cur`; filter 0 copies. New `PassBytes`, new `Progressive` pass table `{0,0,1,1}`.
- Tests added 2026-11-02: `PngTests.SmallWindowsMatchOneWholeWindow` (301x233, RGBA8 / RGB8 / Grey8 / RGBA16 / RGBA8 Adam7 / Grey8 Adam7, filters 0-4, fills 1 / 7 / 4096 / rowBytes vs one whole window); `InflaterTests.CorruptStreamsThrowOnlyFormatErrorsAcrossSmallWindows` (2,000 byte-flip rounds, 399x100 Grey8, fill 7, only `ImageFormatException`, guard bytes untouched).
- Removed: `PngDecoder.IdatStream`, the `ZLibStream` on the decode path, the `InvalidDataException` / `EndOfStreamException` catches, block reads and the two-half `lines` buffer, `using System.IO` / `System.IO.Compression` in `PngDecoder`.
- Unchanged: `PngEncoder` and `ArctisAurora` `PdfDocument` still compress with `ZLibStream`.
- Tests `InflaterTests`: matches `ZLibStream` at NoCompression / Fastest / Optimal / SmallestSize on random, text-like and run/overlap data; zlib's empty stream; a hand-built fixed-Huffman block (cross-checked against `ZLibStream`); IDAT chunk sizes 1 / 2 / 7 / 4096 for compressed and stored; every truncation offset rejected; output 1 byte short or long rejected; ~~bad Adler rejected~~ (superseded 2026-11-02: `BadChecksumIsRejected` is now `BadChecksumIsIgnored`, a flipped trailer byte decodes to the original data); 2,000 seeded byte-flip rounds throw only `ImageFormatException` and never touch 64 guard bytes after the output.
- Behaviour changes, malformed files only: extra data after the image's last row is now rejected (was ignored); a bad zlib stream raises `ImageFormatException` directly (was `InvalidDataException` wrapped into it). .NET's `ZLibStream` writes 0 bytes for empty input, which is not a valid zlib stream and is rejected.

## Why these choices

**Reimplemented instead of "using zlib-ng more".**
.NET's `ZLibStream` already is zlib-ng. Calling it more directly means internal `CompressionNative_*` exports (undocumented, can change per release) or shipping a native zlib-ng (breaks Arctis's managed-only, per-platform binaries). Neither is faster.

**Windowed decoder, not one whole buffer (user chose 2026-11-02, reversing the 2026-11-01 choice).**
The first landing inflated into one pooled buffer of the raw image size (~23 MB for a 2432² atlas). Boot measurement is what reversed it: `AssetRegistries.PreloadAssets` allocated 165.3 MB with it. The window (256 KB fill, rented from the 512 KB ArrayPool bucket) cut that to 50.8 MB; the rest is not decode. Harness first-pass allocation 59.5 -> 0.7 MB.

**Unfilter out of place (variant B), not copy-then-in-place (variant A).**
The window's bytes must stay the filtered stream because later matches read them. A copied each row into scratch and kept the kernels; B changed every kernel to read `src` and write `cur`. Boot indistinguishable (A 584-656 ms, B 579-655 ms, 4 alternating runs each); harness warm pass B ~207 ms vs A ~208-230 ms. B kept: no extra copy per row.

**The remaining boot slowdown is tier-0 JIT, not memory; flags applied 2026-11-02, effect unmeasured.**
Inflate and the unfilter loops run once per image so they stay tier-0 for the whole boot. Rule 42a forbade AggressiveOptimization; the exception below was applied afterwards. Still open candidates: ReadyToRun-compile Arctis.Imaging, parallel or baked atlases; none done.

**Compressed bytes are read in place across IDAT chunks.**
Atlases have 3-47 chunks of up to 64 KB; no copy of the compressed bytes, and zlib-ng's own window copy is gone too.

**~~Adler-32 is verified (user chose).~~ Superseded 2026-11-02: see "Adler-32 dropped" below.**
CRC-32 per chunk is already checked by `Walk`; Adler caught a decoder bug.

**Codes are rejected when over-subscribed, or incomplete with more than one code, as zlib and libdeflate do.**
That keeps the table bounds at 2342 / 402 (libdeflate's ENOUGH values for these roots).

**Hot loop: one branchless 8-byte refill per symbol.**
At least 56 bits covers the longest match (48 bits). Matches are copied 8 bytes at a time when the distance is >= 8 and the output has room to overshoot; distance 1 is a fill.

**~~No JIT flags (rule 42a)~~ superseded 2026-11-02 by the AggressiveOptimization exception below; portable `Vector128` only.**

**AggressiveOptimization in Arctis.Imaging, an exception to rule 42a (user chose 2026-11-02).**
`[MethodImpl(MethodImplOptions.AggressiveOptimization)]` on `Inflater` `Fill`, `Codes`, `Copy`, `Stored`, `ReadDynamic`, `Build`, `Bits.Refill`; `PngDecoder` `Decode(…, int fill)`, `DecodeRow`, `Expand`, `Unfilter`, `Sub4`, `Sub8`, `Average4`, `Average8`, `Paeth4`, `PaethWide`, `PaethScalar`, `AddUp`; `Crc32.Update`; `PixelRows.FromRgba`; `ImageOps.CopySwizzled`.
These loops run once per image, so under tiering they stay tier-0 for the whole boot; the cold cost measured earlier on 2026-11-02 was ~120 ms of PreloadAssets, shrinking to ~20 ms with `DOTNET_TieredCompilation=0`.
Cost accepted: these methods never get the tier-1 PGO recompile.
Rejected: `<TieredCompilationQuickJitForLoops>false</…>` in each host csproj, the same PGO loss but app-wide and unmeasured elsewhere. ReadyToRun was also on the table (publish-only, dev runs unaffected); not done.
Effect on boot time is unmeasured (user said don't profile).

**Adler-32 dropped rather than folded into the row loop (user chose 2026-11-02).**
`PngDecoder.Walk` already verifies CRC-32 over every chunk including IDAT, so the Adler pass was a second checksum over all inflated bytes (126 MB per boot for the 11 font atlases), and the most Vector128-heavy loop, worst at tier-0. Folding it into the row loop would only improve cache use; the per-byte work stays. stb_image, wuffs and libspng skip it by default, and .NET's `DeflateStream` (ImageSharp's path) never sees it.
`Inflater.Adler32` and `State.adler` are removed; `Finish` still reads the 4 trailer bytes and keeps the overread ("ends early") check but does not compare them.
Behaviour change, malformed files only: a stream whose chunk CRCs pass but whose Adler is wrong now decodes instead of throwing "checksum does not match".

## Measurements
Same container, Release. Decodebench is a scratchpad Stopwatch bench: 32 decodes per row, median of 3 runs, ms per decode; ImageSharp median over 6 runs. zlib-ng's inflate alone on RGBA8 was 9.7-11 ms; Arctis's whole decode is now 7.6.

| row | before | after | ImageSharp | after / IS |
|---|---|---|---|---|
| Atlas (32 copies) | 32.75 | 24.71 | 31.18 | 0.79 |
| RGBA8 None/Sub/Up/Avg/Paeth | 11.2/11.9/11.5/14.0/16.7 | 7.6/8.5/7.6/10.3/12.8 | 12.4/12.5/12.2/14.3/16.0 | 0.61/0.68/0.63/0.72/0.80 |
| RGB8 None/Sub/Up/Avg/Paeth | 9.8/11.5/9.4/13.1/16.0 | 6.9/8.5/6.5/10.4/13.2 | 10.8/12.9/10.6/13.9/24.0 | 0.64/0.66/0.62/0.74/0.55 |
| Grey8 None/Sub/Up/Avg/Paeth | 3.9/6.2/4.4/6.9/9.9 | 2.9/5.1/3.3/5.6/8.3 | 6.7/9.0/7.6/9.6/12.3 | 0.44/0.57/0.43/0.58/0.67 |
| RGBA16 None/Sub/Up/Avg/Paeth | 22.0/21.4/21.9/23.0/26.5 | 13.0/13.8/12.5/15.0/17.1 | 28.2/33.1/28.3/36.5/61.0 | 0.46/0.42/0.44/0.41/0.28 |
| RGBA8 Adam7 | 22.3 | 16.5 | 21.0 | 0.79 |

Engine `Perf.ImageDecode` (Release+PROFILE, lavapipe, 3 runs each side, p50 medians, ms; ratio vs ImageSharp in brackets):
- Atlas 31.0 -> 27.9 (0.89)
- RGBA8 None 12.0 -> 7.5 (0.58)
- RGBA8 Paeth 17.5 -> 12.2 (0.76)
- RGBA8 Adam7 21.7 -> 16.5 (0.77)
- RGBA16 Paeth 26.2 -> 17.7 (0.27)
- Grey8 None 6.2 -> 3.1 (0.43)

## Verification
- **Test-verified**: ArctisImaging.Tests 72/72 (58 earlier + 14 `InflaterTests` cases).
- Byte-identical: 244 SHA-256 hashes (61 images x RGBA8 / BGRA8 / R8 / RG8) match the pre-change baseline.
- Perf: all 4 `Perf.ImageDecode` tests PASS in 3 runs, zero `[Vulkan]` lines. Builds clean for net8.0 and net10.0.
- Not checked (first landing): Windows, ARM, big-endian, engine Debug suite, boot Preload zones, palette / sub-byte PNG speed.
- **Test-verified** (windowed, 2026-11-02): ArctisImaging.Tests 79/79 (72 earlier + 7 new). Byte-identical: SHA-256 of 356 decodes (all 89 PNGs under Thorium's Data x RGBA8 / BGRA8 / R8 / RG8) identical across `ZLibStream`, whole-buffer and windowed. Engine solution builds clean Release+PROFILE; 16 Thorium `--profile` boots ran and exited 0.
- **Test-verified** (AggressiveOptimization, Adler-32 dropped, fused RGBA8 unfilter, 2026-11-02): ArctisImaging.Tests 79/79. Byte-identical: SHA-256 of 356 decodes (89 PNGs x RGBA8 / BGRA8 / R8 / RG8, so both the fused RGBA8 path and the unfused layouts) identical to the `ZLibStream`-era decoder. Engine solution builds clean Release+PROFILE. No timings taken. Not checked: boot or harness timing, Debug engine suite, `--test=Perf`, Linux, ARM, GUI.
- Not checked (windowed): Debug engine test suite, `--test=Perf` (`Perf.ImageDecode.*`), Linux, ARM, big-endian, palette / sub-byte PNG speed, GUI. Boot Preload zones and Windows are now checked.

## Two literals per lookup (2026-11-02, uncommitted) — removed 2026-11-04
**Superseded:** `Literal2` and `Pair` were dropped in the 2026-11-04 flag-bit update below; the notes here describe the removed code.
- `Inflater` entry kind `Literal2 = 6`: value holds `lit1 | lit2 << 8`, length is the combined code length (<= 11, the root bits).
- `private static void Pair(uint* table, int root)` (AggressiveOptimization) runs at the end of `Build` when `mode == LitlenMode`, for dynamic and fixed blocks. It walks the 2,048 root entries top down; where entry i is a literal of length len1 and `table[i >> len1]` is a literal of length <= root - len1, entry i becomes `Literal2`. Top down so the second code's entry is still unpaired when read.
- `Codes` has a `Literal2` branch ahead of `Literal`: needs 2 bytes of room or throws "PNG image data is longer than the image"; writes the two bytes separately (endian-neutral).
- Sub-table entries, lengths and distances are unchanged. No new tests: `MatchesZlibOnEveryKindOfData`, `DecodesAFixedHuffmanBlock`, the small-window test and both fuzz tests cover the path.
- **Kept without a measured gain, by the user's choice.** Boot `PreloadAssets` (5 runs alternating, swapping only Arctis.Imaging.dll): previous build 483-502 ms, two-literal 483-503 ms, 50.8 MB both. Scratch harness (13 boot images, Release, 4 runs each): first pass 231-242 vs 237-255 ms, warm passes 207-224 vs 211-229 ms (previous vs two-literal), so slightly slower if anything.
- Likely reason, not measured: a pair needs both codes to total <= 11 bits (about 5 bits each) and font-atlas literal codes are probably longer, so few pairs form, while every Huffman block pays an extra 2,048-entry pass in `Pair`. Pair formation was not counted.
- Not done: three or more literals per refill, a larger root table.

## Boot measurements (Windows, Release+PROFILE, `--profile`, zone `AssetRegistries.PreloadAssets`: 11 font atlases up to 2432² / 22.6 MB inflated each, plus 2 masks)

| decoder | time ms | allocated MB | runs |
|---|---|---|---|
| ImageSharp (61ac31d, last ImageSharp commit) | 618-631 | 36.3 | 5 alternating |
| `ZLibStream` (bd268a3) | 459-497 | 50.6 | 12 |
| `ZLibStream` (bd268a3), re-measured 2026-11-02 | 467-493 | 50.6 | 5 alternating |
| tree before two-literal (AggressiveOptimization, no Adler) | 491-511 | 50.8 | 5 alternating |
| two-literal pairing | 483-503 | 50.8 | 5 alternating (previous build 483-502 ms) |
| whole-buffer managed (0c3746d) | 560-627 | 165.3 | 4 |
| windowed (final) | 579-655 | 50.8 | 4, alternating with variant A |

Scratch harness, same 13 images decoded cold: first pass 213-234 ms with `ZLibStream`, 330-357 ms with the managed inflater (whole-buffer and windowed alike); 235-242 ms with `DOTNET_TieredCompilation=0`; warm passes ~198-220 ms for all versions. The 25% decode win measured in the Linux container did not reproduce on Windows: warm, the managed inflater is within a few percent of `ZLibStream` (198-210 vs 203-220 ms).

## Known gaps
- RGBA8 Paeth ~0.76-0.80 vs ImageSharp; the user's target is 0.5. Partly done 2026-11-04: shorter chain and 4-row wavefront landed (see [[png-decode-fast-paths]]); ratio vs ImageSharp not re-measured.
- Two-literal pairing landed (2026-11-02) but measured no gain, possibly a small loss on warm decode; the 2026-11-04 histogram explains it (0.02 M pairs formed). More literals per refill rejected on data: literals are 3.1% of output bytes.
- Two-literal verification: **Test-verified**, ArctisImaging.Tests 79/79; byte-identical SHA-256 of 356 decodes (89 PNGs x RGBA8 / BGRA8 / R8 / RG8); engine solution builds clean Release+PROFILE. Not checked: Debug engine suite, `--test=Perf`, Linux, ARM, big-endian.
- R8/RG8 output still copies through the `rgba` scratch row.
- `PngEncoder` / PDF still use `ZLibStream` for compression.
- Numbers in the Measurements section are from a noisy CPU-rendered Linux container.
- Boot PreloadAssets still ~100-150 ms slower than `ZLibStream` (tier-0 JIT) as measured before the AggressiveOptimization flags; the flags' effect is unmeasured.
- Perf suite not rerun after the windowed change.

## Update 2026-11-04 — Codes fast loop

### What changed
- `Arctis.Imaging.Inflater.Codes` — new fast loop at the top of the outer loop, entered while `o < fastLimit` (`fastLimit = outEnd - MaxMatch - 8`) and `p + 16 <= end`.
- It drops the per-literal `outEnd` checks, the `length > outEnd - o` check and `Copy`'s room check. It keeps `distance > o - start` and the invalid-code / invalid-distance throws.
- The next lit/len entry is looked up right after a literal (>= 41 bits remain) or after a second branchless refill following a match's length+distance decode.
- Matches with distance >= 8 are copied inline in 8-byte words; shorter ones call `Copy`.
- The existing loop body stays as the careful path (tail, last window, IDAT chunk boundaries).
- No data XML touched, no public API change.

### Why
**Fast loop plus careful loop, libdeflate's fastloop/slowloop structure.** `p + 16 <= end` keeps two branchless 8-byte refills per iteration inside the current IDAT chunk; `fastLimit` guarantees a literal, a <=258-byte match and the copy's 8-byte overshoot fit.

**Measured the symbol mix before choosing what to optimise.** Scratch counted copy of the inflater on the 12 atlases: literal bytes 3.1% of output, match bytes 96.9%; 4.12 M single literals, 0.02 M paired literals, 5.45 M matches, average match 23.7 B. Fast refills 9.59 M, slow 1.3 K.

| distance | share of matches | length | share of matches |
|---|---|---|---|
| 1 | 9.0% | 3-7 | 58.4% |
| 4 | 3.6% | 8-15 | 19.7% |
| 2-7 other | ~0.1% | 16-63 | 12.1% |
| 8-15 | 1.7% | 64+ | 9.9% |
| 16-63 | 2.9% | | |
| 64+ | 82.7% | | |

Rejected on this data: a second literal per refill (literals are 3% of bytes), and a short-distance (2-7) pattern copy (3.7% of matches). It also explains why two-literal pairing measured no gain.

**`AggressiveOptimization` on the new Paeth kernel follows the rule-42a exception** already used for Arctis.Imaging decode loops.

### Measurements
Inflate (whole-buffer `Inflater.Inflate`, 12 atlases, Linux container, 5 alternating processes): HEAD 163.9-183.3 ms, new 152.0-159.3 ms. Whole-decode table is in [[png-decode-fast-paths]]. The Ubuntu .NET `ZLibStream` reference measured 316-394 ms for inflate, but Ubuntu's build links stock zlib (confirmed by `ldd` 2026-11-04) rather than the zlib-ng of the Windows .NET bundles, so it is not a valid Windows reference.

### Verification
- **Test-verified**: ArctisImaging.Tests 80/80, Release, net10.0, Linux. Byte-identical SHA-256 across 476 decodes (119 PNGs x 4 layouts) between HEAD and new `Arctis.Imaging.dll`.
- Not checked: Windows, boot PreloadAssets (`--profile`), engine `Perf.ImageDecode`, Debug engine suite, ARM/big-endian.

### Known gaps
- ~~Inflate is still ~3x cycles/byte of libdeflate-class decoders per the earlier estimate~~ — superseded 2026-11-04: measured ~1.5x slower than libdeflate before the flag-bit update, ~1.25x after.
- Boot effect on Windows unmeasured.

## Update 2026-11-04 — flag-bit entries, wide copy, PCLMUL CRC

### What changed
- `Arctis.Imaging.Inflater` entry layout (uint) replaces the kind values in bits 8-15 (`Literal, Length, End, Sub, Invalid, Distance, Literal2` removed):
	- [0..4] bits to consume = code length + extra bits (litlen <= 20, dist <= 28)
	- [8..11] code length (on a sub pointer: sub-table bits)
	- [12] `SubF` sub pointer; [13] `EndF` end of block; [14] `Exc` exceptional (sub pointer, end, invalid)
	- [16..30] value (literal byte, base length, base distance, sub-table start); [31] `LitF` literal
	- Unfilled / invalid slots are `Exc` with 0 bits.
- `Codes` fast and careful loops: literal test is `(int)e < 0`; end / invalid behind one `Exc` test; `bits >>= (int)e` consumes code + extra in one shift (C# masks a ulong shift count to 6 bits, bit 5 of the entry is always 0). The extra-bit value is read from the word saved before the shift: `(saved & ((1ul << total) - 1)) >> codelen` (libdeflate's "saved bitbuf"). Local `saved` (fast loop) / `keep` (careful loop).
- Fast loop match copy: distance >= 16 -> two unconditional `Vector128` load/stores (32 bytes) then a 16-byte loop; 8-15 -> existing 8-byte loop; 1 -> inline `Vector128.Create(byte)` fill (local `run`); 2-7 -> `Copy` as before. `fastLimit` = `outEnd - MaxMatch - 32` (was `- 8`) for the copy overshoot.
- `Leaf`, `Build` (invalid fill, sub pointer `start << 16 | Exc | SubF | subBits << 8`) and `ReadDynamic` (code-length table invalid check `(e & Exc) != 0`, bits used `e & 31`) moved to the new layout. `Pair` and two-literal entries removed. New `using System.Runtime.Intrinsics`.
- No change to `Inflate`, `Begin`, `Fill`, `Finish` signatures or `State`. No data XML touched.
- `Arctis.Imaging.Crc32.Update(uint, ReadOnlySpan<byte>)` unchanged signature; when `Pclmulqdq.IsSupported` and length >= 64 it hands the largest multiple-of-16 prefix to new private `Fold(uint crc, ReadOnlySpan<byte> data)` (`AggressiveOptimization`, rule-42a Arctis.Imaging exception, like `Update`); slicing-by-8 handles the tail and is the fallback.
- `Fold`: 4 x 128-bit accumulators folded over 64-byte blocks with k1k2 (0x0154442bd4, 0x01c6e41596), reduced to one with k3k4 (0x01751997d0, 0x00ccaa009e), single 16-byte folds, 128 -> 64 bits with k5 (0x0163cd6124), Barrett reduction with poly (0x01db710641, 0x01f7011641). Constants and sequence are Intel's "Fast CRC computation using PCLMULQDQ" for the reflected polynomial, as in Chromium zlib's `crc32_sse42_simd_`. `MemoryMarshal.GetReference` + `Vector128.LoadUnsafe`, no unsafe code.
- Test `Arctis.Imaging.Tests.PngTests.Crc32MatchesReferenceAtEveryLength` (new): lengths 0-616 at start offsets 0-15, whole and split at length/3, vs `PngBuilder.ReferenceCrc`.

### Why
**Hardware counters were unavailable, so prototypes were the measurement.** The plan was `perf stat` branch-miss counters first; this cloud VM exposes no `cpu` PMU (`/sys/bus/event_source/devices` has only software / tracepoint / breakpoint / msr / power / uprobe), so scratch prototypes were timed against the real inflater in the same process instead.

**Flag-bit entries with one shift per code + extra (libdeflate's layout).** Per match the bit buffer took 4 serially dependent shifts (length code, length extra, distance code, distance extra); now 2. The kind dispatch was 4 sequential compares (`Literal2` first, though it formed in 0.2% of symbols); now one sign test for literal and one `Exc` test for the rare kinds.

**`Literal2` dropped (user choice, recommended).** Pairs formed 0.02 M of 9.6 M symbols (2026-11-04 histogram); the earlier landing measured no gain; the new layout has no room for a second literal byte (bits 16-30 value, bit 31 the literal flag), so keeping it needed a different packing plus a branch on the literal path. Not prototyped. Dropping it alone (pairing off, compares unchanged) was within noise.

**16-byte match copy and inline distance-1 fill.** 82.7% of matches have distance >= 64, average 23.7 B; two unconditional 16-byte stores cover ~90% of matches without a loop branch. Distance 1 is 9% of matches and previously left the fast loop for `Copy` -> `Span.Fill`. Distance-4 pattern copy still not done (3.6%).

**CRC: own x64 fold, not the `System.IO.Hashing` NuGet package (user choice, recommended).** Keeps Arctis.Imaging (a published package) at zero dependencies. `System.IO.Hashing.Crc32` was used in the scratch bench only, as the speed target (2.3-2.5 ms). x86's SSE4.2 `crc32` instruction is CRC-32C, a different polynomial, so unusable. ARM64 (`Crc32.Arm64`, same polynomial) left out: cannot be run in this container.

### Measurements
Scratch bench, Release, net10.0, Linux container, Xeon 2.8 GHz with AVX2 / BMI2 / PCLMUL / AVX-512, `taskset -c 2`, 12 Thorium boot atlases = 16.3 MB PNG -> 133.2 MB inflated, 20 warm passes per process, 5 processes; ms for all 12 as median / min; every variant byte-identical to libdeflate's output.

| variant | median range | min range |
|---|---|---|
| Arctis before | 164-175 | 152-155 |
| V1 = flag entries + one shift, no Literal2 | 142-154 | 129-133 |
| V2 = V1 + 16-byte copy + distance-1 fill (landed) | 128-137 | 123-125 |
| libdeflate 1.19 (P/Invoke) | 107-112 | 99-102 |

Reference decoders, same machine (same 12 zlib streams, median ms of 10 passes, 5 processes): libdeflate 106-113; zlib-ng 2.3.3 (PyPI `zlib-ng` wheel, Python `zlib_ng.decompress`) 236-268; stock zlib via Python 315-347; .NET `ZLibStream` 312-333. `ldd` confirms Ubuntu's .NET 10 `libSystem.IO.Compression.Native.so` links the system `libz.so.1`, i.e. stock zlib, not zlib-ng (the earlier "likely" is confirmed). Before this change Arctis inflate (~165) was ~1.5x faster than zlib-ng and ~1.5x slower than libdeflate; now ~1.25x slower than libdeflate. Python harness overhead is small: Python stock zlib matches .NET `ZLibStream`.

Final A/B (HEAD vs working-tree `Arctis.Imaging` sources, same bench, 5 alternating process pairs, 20 passes, median / min ms):

| stage | HEAD | new |
|---|---|---|
| whole decode RGBA8 (`ImageCodec.Decode`) | 319-344 / 290-303 | 255-277 / 242-258 |
| inflate (`Inflater.Inflate`) | 162-172 / 154-156 | 128-140 / 119-121 |
| CRC-32 over the 12 PNG files | 14.6-15.4 / 14.0-14.1 | 2.4-2.6 / 2.2-2.3 |

### Verification
- **Test-verified**: ArctisImaging.Tests 81/81 (80 existing + `Crc32MatchesReferenceAtEveryLength`), Release, net10.0, Linux; also 81/81 with `DOTNET_EnablePCLMULQDQ=0` (slicing fallback). Byte-identical: SHA-256 of all 119 repo PNGs (excluding bin/obj) x RGBA8 / BGRA8 / R8 / RG8 = 476 decodes identical between HEAD sources and the new sources, 0 errors. `ArctisImaging.csproj` builds clean for net8.0 and net10.0 (Release). aurora-codereview: scan hits all false positives; one comment-length finding on `Fold`'s descriptor, fixed.
- Not checked: Windows; boot `AssetRegistries.PreloadAssets` (`--profile`); engine `Perf.ImageDecode` tests; Debug engine suite; net8.0 at runtime (built only); ARM64 and big-endian; a CPU without PCLMUL other than the env-var switch; the non-accelerated `Vector128` path.

### Known gaps
- Windows / boot effect unmeasured. (Superseded 2026-11-06: Windows decode and boot numbers are in [[png-decode-fast-paths]].)
- ARM64 CRC stays on slicing-by-8.
- Distance 2-7 matches (except 1) still go through `Copy`; distance-4 pattern copy not done.
- Inflate still ~1.25x libdeflate on this data.
- Paeth unchanged; atlases still Paeth-baked.

## Update 2026-11-05 — InflateZlib, neutral messages

### What changed
- NEW `Arctis.Imaging.Inflater.InflateZlib(ReadOnlySpan<byte> zlib, Span<byte> output)` (internal): inflates a plain in-memory zlib stream of known output size. `Begin`'s header check moved into a private `ReadHeader` shared by both.
- Every `Inflater` error message is now format-neutral ("zlib stream ends early", "zlib stream has an invalid header", "zlib stream is longer than its output", "zlib stream has an invalid code", ...). "PNG has no image data" stays (it is about PNG chunks).
- Reason: KTX2 ZLIB levels are plain zlib streams, see [[ktx2-container]]; the compressor side is [[png-deflate]].

### Known gaps
- Messages changed text; no test asserted them.

- Wider tiers for `Crc32.Fold` (VPCLMULQDQ, net9+), `Deflater.Common` and `Deflater.Adler32`: see [[simd-width-tiers]]. The Inflater match copy stays 128-bit.

Related: [[png-decode-fast-paths]], [[png-deflate]], [[ktx2-container]], [[simd-width-tiers]]
