---
date: 2026-02-19
Status: Current
tags:
  - d_UI
  - d_text
  - d_Font
cssclasses:
Linker: "[[Text]]"
Class: AtlasMetaData
Parent Class:
Interfaces: "[[IDeserialize]]"
Type: Public
Attributes:
  - Serializable
---
## Description

The purpose of this class is to store relevant data of the font (characters, some [[Glyph]] data) in order to be able to use the glyph atlas.
## Input/Output

- public void `Deserialize(string name)` -> deserializes the meta data relevant for using the glyph atlas given the font name.

## Fields & Properties

```C#
public int glyphCount;
public char[] chars;
public Glyph[] glyphs;
public float pxRange;

// the baked faces in block order as variant * 4 + style, face 0 the plain regular; variant names joined by
// '\n', the plain family's empty name first; faces after the first keep glyph i's metrics at (f - 1) * glyphCount + i
public int[] faceKeys;
public char[] variantNames;
public GlyphMetrics[] faceMetrics;

// variant * 4 + style to face, -1 where the bake found none, and the variant names, built on load
[NonSerializable]
private int[] slots;
[NonSerializable]
public string[] variants;

// char to index into chars and glyphs, built on load
[NonSerializable]
private Dictionary<char, int> charIndex;
private static readonly CharHash charHash = new CharHash();

private sealed class CharHash : IEqualityComparer<char>
{
	public bool Equals(char a, char b) => a == b;
	public int GetHashCode(char c) => c;
}

// em advances of the chars below AdvanceTableSize, one row per face, built on load
public const int AdvanceTableSize = 256;
[NonSerializable]
private float[] advances;

public int faceCount => faceKeys.Length;
public int cellCount => glyphCount * faceCount;

// kern pairs baked at import, last in the class, all default empty
public uint[] kernKeys;      // a << 16 | b
public float[] kernValues;   // em
public int[] kernStarts;     // face f's pairs at kernStarts[f] .. kernStarts[f + 1]

// face << 32 | a << 16 | b to em, built on load
[NonSerializable]
private Dictionary<ulong, float> kerns;
```

## Faces and the atlas grid

The atlas holds one cell per (character, face), laid out as consecutive per-face blocks in a square grid of `ceil(sqrt(cellCount))` cells to a side — the plain family's faces first, then each variant's (Light, Black…) — and `faceKeys` names each block as `variant * 4 + style`. Everything above this class asks for a face index: `Effective` turns a variant and a style into one, and `Metrics`, `TableAdvance`, `Kern` and `CellIndex` take it, so adding a face is arithmetic here and nothing at all in the shader. The first face's metrics stay on each [[Glyph]]'s `regular`; the other faces' sit in the flat `faceMetrics` array. See `ClaudeMemory/Decisions/font-variants.md`.

## Advances without a lookup

Measuring text asks for one advance per character, and a large note asks a million times a frame when it rewraps. A dictionary probe, a [[Glyph]] read and a metrics copy per character were two thirds of an optimized measure, so `BuildCharIndex` also lays the advances of the first 256 characters out flat, one row per face, and `TableAdvance` is a single array read. A character the font lacks takes space's advance there, the same fallback the measurer applies above 256 through the dictionary. See `ClaudeMemory/Decisions/large-note-measure-cost.md`.

`Effective` is the honesty step. A run asks for a variant and a style, and the answer is the variant's own face for that style, else the plain family's face for that style, else the variant's regular, else regular — so Light with italic is Light Italic, Light with bold is the family's Bold, and a family with no italic file draws regular. The face is chosen once and used for the metrics and the cell together, so a run never measures against one face and draws in another. `VariantOf` turns a run's variant name into its index, 0 for a name the font lacks. See `ClaudeMemory/Decisions/font-variants.md`.

## Kerning
`ReadKerning` asks `FontKerning.Read` for each baked face's non-zero pairs among the atlas charset, reading the font's GPOS `kern` feature and falling back to the old `kern` table, and stores them in `kernKeys`, `kernValues` and `kernStarts` so they ride in the `.agd`. `BuildCharIndex` turns them into the `kerns` dictionary on load, a shared empty one when there are none. `Kern (first, second, face)` is one dictionary probe, in em. An atlas written before kerning loads with no pairs and kerns nothing until the font is re-imported; the importer's version stamp makes a Debug boot do that.

## Methods / Functions

### Public
#### Deserialize
get meta data file location from name
open binary file to read
	read glyph count
	foreach glyph
		read char
	foreach glyph
		read xMin/yMin/xMax/yMax/width/height/right-left-top side bearings

## Helpers

```C#
public int Effective(int variant, FontStyle style)
{
	the variant's own face for the style, if baked: return it
	the plain family's face for the style, if baked: return it
	the variant's regular, if baked: return it
	return 0
}

public bool HasFace(int variant, FontStyle style) => slots[variant * 4 + (int)style] >= 0;

public int VariantOf(string? name)
{
	empty name: return 0
	for each variant after the plain family
		names match, ignoring case: return its index
	return 0
}

public GlyphMetrics Metrics(Glyph glyph, int face) => face == 0 ? glyph.regular : faceMetrics[(face - 1) * glyphCount + glyph.index];

public int CellIndex(int charIndex, int face) => face * glyphCount + charIndex;

public void BuildCharIndex()
{
	charIndex = new Dictionary<char, int>(glyphCount, charHash);
	for each glyph i
		charIndex.TryAdd(chars[i], i)
		glyphs[i].index = i
	variants = variantNames split on '\n'
	slots = variants.Length * 4 entries of -1, then slots[faceKeys[f]] = f for each face f
	advances = new float[faceCount * AdvanceTableSize];
	for each char c below AdvanceTableSize
		glyph = GetGlyph(c), or GetGlyph(' ') when missing
		if no glyph: leave 0
		for each face f
			advances[f * AdvanceTableSize + c] = Metrics(glyph, f).advanceWidth
}

public float TableAdvance(char character, int face) => advances[face * AdvanceTableSize + character];

public Glyph GetGlyph(char character)
{
	if (charIndex.TryGetValue(character, out int index))
	{
		return glyphs[index];
	}
	return null;
}

public (Glyph, int) GetGlyphAndIndex(char character)
{
	if (charIndex.TryGetValue(character, out int index))
	{
		return (glyphs[index], index);
	}
	return (null, -1);
}

public int GetIndexOfChar(char character)
{
	if (charIndex.TryGetValue(character, out int index))
	{
		return index;
	}
	return -1;
}
```