---
date: 2026-10-01
Status: Current
tags:
  - d_text
cssclasses:
  - Aurora.css
Linker:
  - "[[Arctis Aurora]]"
System:
Class:
  - "[[Math Layout]]"
Parent Class:
Interfaces:
Used by:
Type:
  - Public
  - Static
Attributes:
Namespace: ArctisAurora.Core.UI
SourceFile: AuroraEngine/Core/UI/Tex/MathLayout.cs
VerifiedAgainst: 2026-10-01
---
## Description

Places a parsed formula from [[Math Parser]] as glyphs and rules, the way TeX's math typesetting does, using the font's own constants from [[Math Constants]] and glyph sizes from the Cambria Math atlas ([[Atlas Meta Data]]). The result is a `MathBox`: its width, height above the baseline and depth below it, a list of glyphs and a list of filled rules. Everything is in em at font size 1 with y measured up from the baseline, so whoever draws it multiplies by the text's font size.

Inline formulas use text style and display formulas display style; scripts shrink to script and script-script style by the font's own percentages. Big operators grow in display style and take their limits above and below; in text style the limits move to the side. Tall delimiters grow by scaling until they are 2.4 times their natural height and are then built from the Unicode bracket pieces, and a tall square root switches from a scaled `√` to the `⎷` hook with a drawn stem.

A glyph asked for in a face that lacks it (Greek in Latin Modern Math's italic, which is LM Roman Italic) is set from the regular face instead, so it shows upright rather than not at all.

A note draws a `MathBox` through its paragraph's text control: each glyph from the run's own font when that font has math constants (`latin-modern-math` from a `.tex` preview), otherwise from the `math` font, at the run's size times the glyph's scale, each rule as a filled rectangle with its top on a whole pixel and at least one pixel thick and one pixel wide. A display formula takes the whole column as its width and is drawn centred in it, which is what puts it on a line of its own. A formula that could not be read draws its source in the palette's danger colour.

An environment (`MathArray`) is laid out as a grid in two passes: every cell is measured first, then each is placed by its column's width and alignment. Matrix, `cases` and `array` cells use text style, the aligned family uses display style and `smallmatrix` and `\substack` use script style. Every grid is centred on the math axis, and an aligned right-hand cell is laid out after an empty ordinary atom, as amsmath does, so a leading `=` keeps its relation space.

Layout takes the line width as an optional argument, 0 meaning none, and only a top-level `align`, `flalign` or `multline` uses it. Such a formula spreads over the width with amsmath's rules, and its box is marked `widthAware`, so the note's text control lays it out again whenever the width changes and keeps those boxes only for the current width. With no width the old spacing stays: `align` and `flalign` are spaced like `aligned` with 1 em between pairs, and `multline` has its first row flush left and its last row flush right within its widest row plus 1 em, middle rows centred.

A `\tag` becomes the box's `tag`: its text sits at its row's baseline with its right edge at x 0, and a display formula's height and depth include it. An inline formula's tag is placed after the formula, a quad apart. The note draws a formula centred but shifted left to keep a quad clear of its tag, and lets it overflow past that.

In a TeX document without unicode-math (`MathRules.Tfm`) the layout is given the text point size. Letters, digits and symbols then take Computer Modern's real widths, italic corrections, heights and depths from `TexMathMetrics`, at the design size LaTeX picks for that point size, and scripts are set at LaTeX's `\DeclareMathSizes` sizes instead of the font's percentages. Big operators, sized delimiters, radicals, tags and error text keep Latin Modern metrics.

Formulas are inserted and edited through [[Formula Popup]].

## API summary

| Member | Kind | Summary |
| --- | --- | --- |
| `Layout(MathNode root, bool display, AtlasMetaData atlas, MathConstants constants, float lineWidth = 0, float strut = 0, MathRules rules = MathRules.OpenType, float textPt = 0f)` | static | The formula as a `MathBox` in em; a `MathError` lays out its source as plain text with `error` set. A line width in em makes a top-level `align`, `flalign` or `multline` spread over it. A strut in em replaces the array row strut. With `MathRules.Tfm` scripts and limits follow TeX's Appendix G rule 18 using the constants file's `Sub1`, `Sub2`, `Sup1`, `Sup2`, `Sup3`, `SubDrop`, `SupDrop` and `BigOpSpacing5`, which a TeX document without unicode-math selects. |

## Fields & Properties

```C#
// MathBox
float width, height, depth;
List<MathGlyph> glyphs;   // char, face, x, y, scale
List<MathRule> rules;     // x, top y, width, height
bool error;
MathBox tag;              // a display formula's tag, right edge at x 0
```

## Methods

### `Layout` *(static)*
	if the root is a MathError
		lay out its source as regular text and flag it
	otherwise lay out the root in display or text style

### Laying out a list
	give every atom its class
		a binary operator with nothing usable before it becomes ordinary
		a binary operator right before a relation, closer or punctuation becomes ordinary
	for each atom
		add TeX's space between the previous class and this one, skipping the medium and thick ones in script styles
		place the atom on the baseline
		after a plain symbol, add its italic correction

### Laying out scripts
	if the nucleus is a big operator or operator name that takes limits, in display style
		centre the limits above and below the operator by the font's limit gaps
		in TFM mode the upper limit rises by the larger of big_op_spacing1 plus the superscript's depth and big_op_spacing3   // TeX rule 13a
		in TFM mode the operator's height and depth come from cmex10's box, centred on the axis   // glyph and width stay Latin Modern
	otherwise
		raise the superscript and lower the subscript by the font's shifts
		clash = the font's minimum gap − the gap between them
		if clash > 0
			in TFM mode   // TeX rule 18e
				lower the subscript by clash
				lift both by the font's superscript-bottom-max-with-subscript − (the superscript's shift − its depth), when that is positive
			in OpenType mode   // the MATH spec's order
				raise the superscript by the smaller of that same amount (not below 0) and clash
				lower the subscript by the rest
		put the superscript after the nucleus's italic correction
		add the font's space after the scripts

### Laying out a fraction
	numerator and denominator one style smaller, the denominator cramped
	push them apart until each clears the rule by the font's gap
	centre the rule on the math axis
	pad either side by the null delimiter space

### Laying out a square root
	if a `√` up to twice its size covers the body
		scale it and line its top up with the bar
	otherwise
		place the `⎷` hook at the bottom and draw the stem up to the bar
	draw the bar over the body
	place the degree on the sign's left, raised by the font's percentage

### Laying out `\left…\right`
	size the delimiters to cover the body around the axis, as TeX does
	for each delimiter
		natural size if it is tall enough
		scaled up to 2.4 times
		beyond that, top piece, bottom piece, a middle piece for braces, and overlapping extenders between them
		vertical bars become rules

### Laying out an array
	first pass
		lay out every cell in its style
		take each column's width and each row's height and depth, never under the strut of 0.84 above and 0.36 below, cases rows 1.2 times that
	rows in the aligned family
		baselines are 1.5 em apart, which is baselineskip plus `\jot`
		when the gap between two rows would fall under 0.3, use 0.4
	rows in a smallmatrix or `\substack`
		baselines 0.77 apart, lineskip 0.13 when the gap is too small
	add each row's `\\[len]` skip
	second pass
		place each cell in its column by its l, c or r alignment
		put the gap between columns: matrix 1, array 0.5 each side, cases 1, smallmatrix 5/18, aligned pairs by `pairGap`
		draw `|` rules and `\hline` rules 0.04 thick
	in a grid that fills the line width, set the margins and gaps from the width as in the next section
	centre the grid on the math axis
	place the delimiters around it as `\left…\right` does

### Spreading a grid over the line
	for an `align` with n pairs of columns
		the margins and the gaps between pairs are all (width - sum of the columns) / (n + 1)
	for a `flalign` with n pairs
		there are no margins and the gaps are (width - sum of the columns) / (n - 1)
		one pair falls back to the `align` rule
	in both the gaps are at least `\minalignsep`, 1 em
		when they would be less, the margins take (width - sum - (n - 1) * the gap) / 2, never negative
	for a `multline`
		the first row sits `\multlinegap`, 1 em, from the left and the last row 1 em from the right
		with a tag, the tag and `\multlinetaggap`, 1 em, take the last row's place
		the middle rows are centred
	when a tagged row ends within 1 em of its tag
		spread again over the width minus the widest tag and 1 em
	`alignat`, `aligned` and `gather` are not spread

### Laying out a tag, a frame and a stack
	a tag: lay out its text, put it at its row's baseline with the right edge at x 0
	a child's tag moves up with the child, keeping only its vertical offset
	a frame (`\boxed`): lay out the body, then draw a rule 0.04 thick at a gap of 0.3 around it
	a fraction without a rule (`\binom`): stack numerator and denominator by the font's stack constants
	scripts with `overUnder` always take their limits above and below

## Related
- [[Math Parser]] — produces the nodes
- [[Math Constants]] — the font's layout constants
- [[Atlas Meta Data]] — glyph sizes
