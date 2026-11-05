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
- Fixed, not settings (zlib leaves them across levels too): 15 hash bits with a shift of 5 per byte, blocks of 16 383 symbols (zlib memLevel 8), TOO_FAR 4096 (3-byte matches further back are dropped), MAX_DIST 32 506 (32 768 - 262 lookahead).
- MaxChain must be >= 4: zlib's chain walk with good >= prev would otherwise reach 0 and never stop (zlib's own stated requirement).
- No greedy `deflate_fast` loop, so zlib levels 1-3 cannot be reproduced; no named levels.

**Close to zlib, not byte-exact (user chose option a, 2026-11-05).**
The match search is a port of `deflate_slow` / `longest_match` (same hash, chain, lazy, good/nice, TOO_FAR, MAX_DIST, block length).
Each block is written dynamic / fixed / stored, whichever is smallest (zlib's `_tr_flush_block` byte comparison; stored only when the block's bytes <= 65 535).
Huffman lengths use Moffat-Katajainen in-place minimum redundancy plus a Kraft-sum length limit (15 bits, 7 for the code-length code), not zlib's `trees.c`, so output bytes differ from zlib's.
Code-length runs follow zlib's `scan_tree` / `send_tree` (litlen and distance trees encoded separately). At least two codes per tree, as zlib forces.
Option (b), a byte-exact port of zlib including `trees.c` so a test could assert identical bytes against classic zlib, was deferred: roughly double the code, zlib-licence "altered source" marking for the port, and every later speed-up would have to keep byte-identical output.
The user said (b) may be needed later; it is an open WIP item.

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
- `Deflater` encode is ~7 % slower than classic zlib 6; not optimised.
- Atlases not re-baked; `importerVersion` still 9.

Related: [[managed-inflate]], [[png-decode-fast-paths]], [[ktx2-container]]
