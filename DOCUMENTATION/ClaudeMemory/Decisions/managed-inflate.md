# Decision — managed inflate: Arctis's own zlib/DEFLATE decoder for PNG

**Date:** 2026-11-01
**Scope:** `Arctis.Imaging` — `Inflater`, `PngDecoder`; tests `Arctis.Imaging.Tests.InflaterTests`

## What changed
- New `Arctis.Imaging.Inflater` (internal static). Entry `Inflate(ReadOnlySpan<byte> png, int firstIdat, Span<byte> output)`: fills `output` exactly or throws `ImageFormatException`.
	- Structs only: `Bits` (cursor over consecutive IDAT chunk bodies read in place, 64-bit bit buffer, steps into the next IDAT chunk in its slow `Refill`); `Tables` holding `LitlenTable` `[InlineArray(2342)]`, `DistTable` `[InlineArray(402)]`, `CodeTable` `[InlineArray(128)]`, as a stack local.
	- Private: `Codes` (hot loop), `Copy`, `Stored`, `ReadDynamic`, `BuildFixed`, `Build` (canonical Huffman; 11-bit litlen root, 8-bit distance root, 7-bit code-length root, sub-tables sized as zlib sizes them), `Leaf`, `Reverse`, `Adler32` (Vector128).
- `PngDecoder.Decode` rents one raw buffer of the sum over passes of height x (rowBytes + 1) and calls `Inflater.Inflate`.
- `PngDecoder.DecodePass(raw, zero, c, palette, x0, y0, dx, dy, bpp, rgba, packed, destination, pitch, layout)` returns the bytes its pass took and unfilters in place (prev = row above, or a zero row for a pass's first row). New `PassBytes`, new `Progressive` pass table `{0,0,1,1}`.
- Removed: `PngDecoder.IdatStream`, the `ZLibStream` on the decode path, the `InvalidDataException` / `EndOfStreamException` catches, block reads and the two-half `lines` buffer, `using System.IO` / `System.IO.Compression` in `PngDecoder`.
- Unchanged: `PngEncoder` and `ArctisAurora` `PdfDocument` still compress with `ZLibStream`.
- Tests `InflaterTests`: matches `ZLibStream` at NoCompression / Fastest / Optimal / SmallestSize on random, text-like and run/overlap data; zlib's empty stream; a hand-built fixed-Huffman block (cross-checked against `ZLibStream`); IDAT chunk sizes 1 / 2 / 7 / 4096 for compressed and stored; every truncation offset rejected; output 1 byte short or long rejected; bad Adler rejected; 2,000 seeded byte-flip rounds throw only `ImageFormatException` and never touch 64 guard bytes after the output.
- Behaviour changes, malformed files only: extra data after the image's last row is now rejected (was ignored); a bad zlib stream raises `ImageFormatException` directly (was `InvalidDataException` wrapped into it). .NET's `ZLibStream` writes 0 bytes for empty input, which is not a valid zlib stream and is rejected.

## Why these choices

**Reimplemented instead of "using zlib-ng more".**
.NET's `ZLibStream` already is zlib-ng. Calling it more directly means internal `CompressionNative_*` exports (undocumented, can change per release) or shipping a native zlib-ng (breaks Arctis's managed-only, per-platform binaries). Neither is faster.

**One whole buffer, not a windowed decoder (user chose).**
No 32 KB slide copies, no resumable state, simplest loop. Cost: one pooled buffer of the raw image size (4 MB for 1024² RGBA8, ~23 MB for a 2432² atlas). A raw size above `Array.MaxLength` is rejected with `ImageFormatException`.

**Compressed bytes are read in place across IDAT chunks.**
Atlases have 3-47 chunks of up to 64 KB; no copy of the compressed bytes, and zlib-ng's own window copy is gone too.

**Adler-32 is verified (user chose).**
CRC-32 per chunk is already checked by `Walk`; Adler catches a decoder bug.

**Codes are rejected when over-subscribed, or incomplete with more than one code, as zlib and libdeflate do.**
That keeps the table bounds at 2342 / 402 (libdeflate's ENOUGH values for these roots).

**Hot loop: one branchless 8-byte refill per symbol.**
At least 56 bits covers the longest match (48 bits). Matches are copied 8 bytes at a time when the distance is >= 8 and the output has room to overshoot; distance 1 is a fill.

**No JIT flags (rule 42a); portable `Vector128` only.**

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
- Not checked: Windows, ARM, big-endian, engine Debug suite, boot Preload zones, palette / sub-byte PNG speed.

## Known gaps
- RGBA8 Paeth ~0.76-0.80 vs ImageSharp; the user's target is 0.5. The Paeth plan (shorter dependency chain, 2-row wavefront, fused copy) is next and not done.
- Literal-heavy streams decode one symbol per refill (no multi-literal fast path yet).
- R8/RG8 output still copies through the `rgba` scratch row.
- `PngEncoder` / PDF still use `ZLibStream` for compression.
- Numbers from a noisy CPU-rendered Linux container.

Related: [[png-decode-fast-paths]]
