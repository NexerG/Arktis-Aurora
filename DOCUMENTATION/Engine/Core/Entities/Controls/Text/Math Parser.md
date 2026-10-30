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
  - "[[Math Parser]]"
Parent Class:
Interfaces:
Used by:
  - "[[Math Layout]]"
Type:
  - Public
  - Static
Attributes:
Namespace: ArctisAurora.Core.UI
SourceFile: AuroraEngine/Core/UI/Tex/MathParser.cs
VerifiedAgainst: 2026-10-01
---
## Description

Turns the TeX of a math formula — what sits between `$…$` or `$$…$$` in a note — into a tree of math nodes that [[Math Layout]] can place. It reads TeX math mode only: scripts, fractions, roots, `\left…\right`, accents, `\text`, `\mathrm`, `\mathbf`, `\mathbb`, spacing commands, operator names, Greek letters and the symbols listed in `MathSymbols`.

It also reads the amsmath environments: `\begin{…}…\end{…}` for the matrix family, `cases`, `array`, the aligned family, `gathered`, `multline` and the top-level `align`, `gather`, `flalign`, `alignat`, `multline` and `equation` with their starred forms. Alongside them it reads `\tag`, `\substack`, `\binom`, `\dbinom`, `\tbinom`, `\overset`, `\underset`, `\boxed` and `\operatorname`. `\notag`, `\nonumber` and `\label{…}` are accepted and dropped. The parser never numbers an equation, so a note shows a number only when the formula says `\tag`; numbering a `.tex` document's equations is [[Tex Typesetter]]'s job.

It never throws. Anything it cannot read — an unknown command, an unbalanced brace, a double superscript — turns the whole formula into a single `MathError` that keeps the source, which layout then shows as plain text flagged as an error.

The parser knows nothing of sheets. A `\sheet{…}` cell reference in a formula is replaced by the cell's value before the source reaches it, so it only ever sees ordinary TeX. See [[Sheet Editor]].

> Notes hold, draw, save and copy formulas; they cannot yet be inserted or edited in the editor. See `ClaudeMemory/Context/math-plan.md`.

## API summary

| Member | Kind | Summary |
| --- | --- | --- |
| `Parse(string tex)` | static | The formula as a `MathList`, or a `MathError` when any part of it cannot be read. |

## Nodes

| Node | Holds |
| --- | --- |
| `MathList` | a `{…}` group or the whole formula: a list of nodes |
| `MathSymbol` | one character, its face, its `MathClass`, and whether it is a big operator that takes limits |
| `MathText` | `\text` content or an operator name such as `sin` or `lim`, with its class and whether it takes limits |
| `MathScripts` | a nucleus with a superscript, a subscript, or both; `overUnder` forces limits, for `\overset` and `\underset` |
| `MathFraction` | numerator, denominator, and the style `\dfrac` or `\tfrac` forces; `noRule` is `\binom` |
| `MathArray` | an environment: its `MathArrayKind` (`Matrix`, `Small`, `Cases`, `Array`, `Aligned`, `Gathered`, `Multline`), column letters l, c, r, the column boundaries holding `\|`, the gap between aligned pairs in em, the rows of cells, the extra space after each `\\[len]` in em, and the row boundaries with `\hline` |
| `MathTag` | the text of a `\tag`: "(x)", or "x" for `\tag*` |
| `MathFramed` | the body of a `\boxed` |
| `MathRadical` | the body and an optional degree |
| `MathDelimited` | `\left` and `\right` delimiters around a body; `\0` is the empty `.` delimiter |
| `MathAccent` | an accent character, or a bar, over a body |
| `MathSpace` | a fixed space in em |
| `MathError` | the source of a formula that could not be read |

## Methods

### `Parse` *(static)*
	parse a list until the end of the source
	if anything failed along the way
		return a MathError holding the source
	return the list

### Parsing a list
	repeat
		skip whitespace
		at the end: fine only for the whole formula
		`}` closes a group, `]` closes a root's degree, `\right` ends a `\left` body
		otherwise parse one atom with its scripts and add it

### Parsing an atom with scripts
	parse the atom; a bare `^` or `_` has an empty nucleus
	while the next character is `^` or `_`
		a second superscript or second subscript fails the formula
		parse its argument: a `{…}` group, one command, or one character
	wrap in MathScripts when any script was read

### Choosing a face
	inside `\mathbf`: bold
	outside `\mathrm`, `\mathbf` and `\mathbb`: Latin letters and lowercase Greek are italic
	everything else: regular

### Parsing an environment
	read the name and find it in the environments table, which gives the kind and its delimiters
	for an `array`, read the column spec: letters l, c, r and the `|` rules between them
	read rows until the matching `\end`
		a cell ends at `&`, a row at `\\`
		after `\\` read an optional `[len]` as extra space, in em, ex, pt, mm, cm or in; spaces before `[` are not skipped
		`\hline` marks a row boundary
		a trailing `\\` before the `\end` adds no row
	in the aligned family, cells pair up and the gap between pairs is 1 em, or 0 for alignat and alignedat
	wrap the rows in a MathArray, inside the delimiters the table names

## Related
- [[Math Layout]] — places the nodes
- [[Math Constants]] — the font constants layout reads
