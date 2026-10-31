---
date: 2026-10-05
Status: Current
tags:
  - d_text
cssclasses:
  - Aurora.css
Linker:
  - "[[Arctis Aurora]]"
System:
Class:
  - "[[Tex Expander]]"
Parent Class:
Interfaces:
Used by:
Type:
  - Public
Attributes:
Namespace: ArctisAurora.Core.Tex
SourceFile: AuroraEngine/Core/Tex/TexExpander.cs
VerifiedAgainst: 2026-10-05
---
## Description

Reads the source of a TeX or LaTeX document and hands back the tokens a typesetter would see, after every macro, conditional and register operation has been carried out. It is the base the [[Tex Typesetter]] sits on; nothing in the user interface calls either yet, and a `.tex` file opens in the note editor as plain coloured source.

It follows TeX's own rules rather than a command table: categories (`\catcode`) are read live, groups save and restore meanings with TeX's global/local level rule, `\def` takes delimited parameters, and numbers and dimensions are scanned with the arithmetic of tex.web in scaled points. The LaTeX layer — `\newcommand`, `\newenvironment`, `\begin`/`\end`, `\newif`, `\newcounter` and friends — is built in natively rather than read from `latex.ltx`.

It never throws. Problems are collected in `errors` with a line and column and the run recovers the way TeX does: an undefined control sequence is skipped, a runaway argument stops at `\par` unless it is `\long`, and an unclosed `{`, group or conditional is reported at the end of the file. A run stops with an error after 1,000,000 expansion steps or an input-stack depth of 10,000.

Lengths in `em` and `ex` are read from the `quad` and `xHeight` callbacks, which the typesetter points at the font; with neither set they are fixed at 10pt and 4.30554pt (the cmr10 values).

> The wider route, the rejected tex.web port and the phases still to come are in `ClaudeMemory/Context/tex-plan.md`.

## API summary

| Member | Kind | Summary |
| --- | --- | --- |
| `TexExpander(string source, string prelude = "")` | constructor | Starts a run over the source, reading the prelude (TeX text) first. |
| `errors` | field | The `TexError` list (line, column, message) collected so far. |
| `quad`, `xHeight` | field | `Func<int>?` giving em and ex in scaled points; null means a fixed 10pt. |
| `passUndefined` | field | An undefined control sequence is returned as a token instead of reported. |
| `Next(out TexToken)` | method | The next unexpandable token; false at the end of the source. |
| `DefinePrimitive(name)` | method | Makes `Next` return the token under its own name. |
| `Save(Action restore)` | method | Runs the action when the current group closes. |
| `Insert(TexToken[])` | method | Pushes tokens back to be read next. |
| `InputSource(string, Action?)` | method | Opens a file's text as a lazily read input level above the pending tokens; the optional action runs when the file is used up. |
| `Expanded(TexToken[], TexToken owner)` | method | Returns the tokens fully expanded as `\edef` would. |
| `InsertSource(string)` | method | Tokenizes text with the current catcodes and pushes the tokens; they carry no source positions. |
| `CanReadRaw`, `ReadRaw(terminator)`, `ReadRawChar(out char)` | method | Reads source characters without tokenizing, for `\verb` and similar. |
| `Counter(name)`, `StepCounter(name)` | method | Reads and steps a LaTeX counter. |
| `IsDefined(name)` | method | Whether a macro of that name is defined. |
| `MacroBody(name)` | method | A macro's body when it takes no parameters and no optional argument, else null. |
| `RegisterMuGlue(name)` | method | A `\muskip` register's glue (natural, stretch, shrink in mu fixed point, 65536 = 1mu); `\newmuskip`, `\muskipdef` and `\muskip` make them, numbered from 256, and `ScanGlue(mu: true)` reads one, requiring the unit `mu`. |
| `Error`, `ScanInt`, `ScanDimen(bool mu = false)`, `ScanGlue`, `ScanKeyword`, `Argument`, `Optional`, `NameArgument`, `TakeStar` | method | The scanning helpers a typesetter's own commands use. |

## Tokens

| Type | Holds |
| --- | --- |
| `TexCatcode` | the 16 TeX categories, in `\catcode` order |
| `TexToken` | a control sequence (`name`) or a character with its category (`ch`, `cat`); a body parameter slot 1–9 (`param`); the `line` and `column` it came from |
| `TexError` | line, column and message |

What `Next` returns: characters, `{` and `}` (after their group is opened or closed), `\relax` and `\par`.

## Methods

### `Next`
	repeat
		read the next token from the reader or from pushed-back macro text
		at the end of the source
			report open groups and conditionals
			return false
		if it is a macro or an expandable primitive
			expand it, pushing its replacement back
		else if it is an assignment or a LaTeX definition command
			perform it: a meaning, register or catcode changes, or a group opens or closes
		otherwise
			return it

### Reading a line *(TexReader)*
	split the source on newlines and cut trailing spaces
	append the end-of-line character
	for each character
		read its category now, so a `\catcode` earlier on the line already applies
		a control word skips the spaces after it
		a `%` drops the rest of the line
		a blank line is a `\par`

### `\def` with delimited parameters
	read the control sequence being defined
	read the parameter text up to the opening `{`
	read the body to the matching `}`
	store a macro meaning in the current group level
	a `\global`, `\gdef` or `\xdef` meaning survives the group's restore

### `\newcommand`
	read the name, then optional `[n]` and `[default]`
	a star means the command is not long
	define a macro with n parameters
	if the first parameter has a default
		the first argument is optional, taken from `[...]`

### `\begin` and `\end`
	`\begin{name}` opens an environment group and runs the begin code
	`\end{name}` pushes `\end<name>`'s code followed by an internal sentinel control sequence
	the sentinel closes the group once the end code has run
	a `\end` that does not match the open environment is reported LaTeX-style

### `InputSource` and reading an input file
	`InputSource(text, atEnd)`
		push the current reader, the end hook, the line and column of the input and the input-stack depth (`floor`) onto `files`
		start a new reader over the text
	`Get`
		repeat
			if the input stack is deeper than the current file's `floor`
				take the next token from the pushed-back levels
			else if the current reader has a token
				return it with the line and column where the file was input
			else if a file is open
				pop it and restore the outer reader
				run its end hook
	a reader error inside the file is reported at the position of the input
	because a file is read lazily, a `\makeatletter` or `\verb` inside it acts on the text that follows

### `Expanded`
	`Expanded(tokens, owner)`
		push the tokens and a closing `}`
		return `ExpandedBody`   // the body as `\edef` reads it

## Not supported yet
`#{` parameters, `^^` notation, TeX's unbraced `\input file`, `\number`, `\romannumeral`, `\meaning`, `\aftergroup`, `\afterassignment`, `\refstepcounter`, `\outer` and mu units.

## Related
- [[Tex Typesetter]] — turns these tokens into a node list and lowers it to a `<Document>`
- [[Rich Text Document]] — opens a `.tex` file as source, one `Code` block per line
- [[Math Parser]] — the separate parser for math inside notes
