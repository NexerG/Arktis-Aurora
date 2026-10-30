---
date: 2026-05-30
Status: Current
tags:
  - d_text
  - d_Font
  - d_Serialization
cssclasses:
  - Aurora.css
Linker:
  - "[[Aurora Font]]"
Class:
  - "[[Glyph]]"
Parent Class:
Interfaces:
Used by:
  - "[[Aurora Font]]"
  - "[[Atlas Meta Data]]"
Type:
  - Public
Attributes:
  - Serializable
Namespace: ArctisAurora.Core.Filing
SourceFile: AuroraEngine/Core/Filing/Glyph.cs
VerifiedAgainst: 2026-09-04
---
## Description

Per-glyph outline + metrics. Holds the raw quadratic-bezier `contours` parsed from the font's `glyf` table and the derived `edgeContours` used by MTSDF generation, plus the layout metrics serialized into the atlas data (`.agd`).

## Fields & Properties

The metrics live in a `GlyphMetrics` struct, and every face measures its own advances and ink boxes for the same character. A glyph carries only the first face's set, `regular`; an atlas keeps every other face's in [[Atlas Meta Data]]'s flat `faceMetrics` array, reached through `AtlasMetaData.Metrics(glyph, face)` with the glyph's `index`. During the bake each face is read into its own glyph array and `regular` is that face's working set.

```C#
[@Serializable]
public struct GlyphMetrics
{
	// ink box, font units
	public short xMin, yMin, xMax, yMax;

	// cell and horizontal metrics, in em
	public float glyphWidth;
	public float glyphHeight;
	public float advanceWidth;
	public float leftSideOffset;
	public float tsb;
}

// the first face's metrics; an atlas keeps the other faces' in AtlasMetaData.faceMetrics
public GlyphMetrics regular;

// position in the atlas's glyphs, set on load
[@NonSerializable] public int index;

[@NonSerializable] public List<List<Edge>> edgeContours = new();  // built for MTSDF
[NonSerializable]  public List<Bezier> contours = new();          // raw outline
```

## Methods

### `BuildEdges` *(public)*
Converts the raw `contours` (on-/off-curve bezier points) into `edgeContours` of quadratic `Edge`s. Consecutive off-curve points get an *implied* on-curve midpoint inserted between them â€” standard TrueType outline reconstruction.

### `SetParams` *(public)*
Stores the glyph bounds and computes the normalized `glyphWidth`/`glyphHeight` from `unitsPerEm`.

## Related
- [[Aurora Font]] â€” parses outlines into glyphs and bakes the MTSDF atlas
- [[Atlas Meta Data]] â€” stores the serialized glyphs
