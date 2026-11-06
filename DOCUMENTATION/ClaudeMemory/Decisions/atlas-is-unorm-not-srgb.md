# Decision — a distance field is not a colour, so font atlases upload as UNORM

**Date:** 2026-08-19
**Status:** LANDED, compile-verified. **NOT GUI-verified.**
**Scope:** `ArctisAurora.Core.Registry.Assets` (`TextureAsset`, `FontAsset`),
`ArctisAurora.EngineWork.Rendering.Helpers` (`AVulkanBufferHandler`).

## The bug

`TextureAsset.LoadFile` hardcoded `Format.R8G8B8A8Srgb` for the image *and* the view, and
`FontAsset` loads the MSDF/MTSDF atlas through it. Vulkan's sRGB transfer function is applied to R,
G and B on sample and **not** to A. The atlas stores distances, not colours, so the three MSDF
channels were being non-linearly warped on every fetch while the true-distance channel was not.

Measured on `Thorium/Data/Fonts/arial/arial_atlas.png` (704x704), read through the same
`Image.Load<Rgba32>` path the engine uses, so these are the bytes that reach the GPU:

| | sRGB-decoded (before) | UNORM (after) |
|---|---|---|
| max abs(median(RGB) - A) | 0.287 | 0.000 |
| Thorium's `> 0.1` fallback fires, whole atlas | 53.7% | 0.0% |
| ...within the rasterized edge band | **100.0%** (100,540 px) | 0.0% |

`sd` crossed 0.5 at a stored value of **0.7354** instead of 0.5000 — glyph edges sat **0.94 px
inside** where they belong, per side, at the `pxRange = 4.0` the Thorium shader hardcodes.

## Why nobody saw it

`median(R,G,B) == A` **bit-for-bit at all 495,616 pixels** of this atlas. The smallest representable
difference is 1/255; the measured maximum is zero.

So Thorium's `abs((sd - 0.5) - (trueSD - 0.5)) > 0.1` fallback fired on 100% of edge pixels and
switched to the one channel sRGB does not touch — whose value is identical to what an uncorrupted
median would have returned. **The bug perfectly masked itself in the only project that runs.**

The engine and editor fragment shaders have no such fallback; they take the decoded median directly,
so their text renders ~0.94 px eroded per side. Latent rather than live: `AuroraEngine.Program.Main`
is empty and `AuroraEditor` has no entry point.

## Decisions

### 1. A format argument, not a flipped constant

`LoadFile(string path, Format format = Format.R8G8B8A8Srgb)`. Ordinary colour textures genuinely
want sRGB — flipping the constant would fix fonts by breaking every other texture. `FontAsset` is the
one caller passing `R8G8B8A8Unorm`, and the default keeps `TextureAsset.Load` (the registry path)
untouched.

### 2. The shaders were left alone

With UNORM the fallback simply stops firing and the shader reads the median instead of alpha. Since
those are the same bits, Thorium's output is unchanged and the engine/editor paths become correct.
Deleting the now-inert fallback was **not** done — it is the correct guard for a real MTSDF whose
alpha is an independent true distance, which is what the atlas *should* eventually carry.

### 3. `CreateTextureBuffer`'s staging buffer was 8x oversized

The `ref Image<Rgba32>` overload sized it `Width * Height * BitsPerPixel` — bits, not bytes. The
`string pathToImage` overload beside it already had the `/ 8` and was correct, which is what made the
discrepancy obvious. 704x704 RGBA staged 15.9 MB instead of 1.98 MB. Transient and not incorrect
(`CopyPixelDataTo` fills what it needs and `CopyBufferToImage` reads what it needs), so it was a
waste rather than a defect.

## Still open

- **The alpha channel carries no information.** A true-SDF alpha is the entire point of MTSDF; here
  it duplicates the median exactly, so a quarter of the atlas is wasted and the `> 0.1` guard can
  never fire on merit. That is a generator-side question, outside this repo.
  Since 2026-11-06 ADF ([[distance-field-format]]) relies on alpha == median and drops alpha from storage.
- Consequence for [[control-edge-and-outline]]: `OutlineWidth` strokes the median, and a genuine
  true-SDF alpha would give rounder joins at acute corners than the median's mitred extensions.
- `pxRange = 4.0` is hardcoded in the Thorium fragment shader and stated nowhere else —
  `AtlasMetaData` carries only `glyphCount`, `chars` and `glyphs`. A font generated at a different
  range would render wrong with no diagnostic.

Related: [[control-edge-and-outline]], [[glyphs-as-pool-data]], [[ui-clipping]]
