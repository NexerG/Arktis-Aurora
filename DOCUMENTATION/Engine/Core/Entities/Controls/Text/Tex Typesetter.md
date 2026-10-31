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
  - "[[Tex Typesetter]]"
Parent Class:
Interfaces:
Used by:
Type:
  - Public
Attributes:
Namespace: ArctisAurora.Core.Tex
SourceFile: AuroraEngine/Core/Tex/TexTypesetter.cs
VerifiedAgainst: 2026-10-05
---
## Description

Reads the tokens a [[Tex Expander]] hands back and builds what TeX's stomach builds: a node list. Horizontal material is characters, glue, kerns, penalties, math and boxes; vertical material is paragraphs, vertical glue and rules. `TexLowering.Compile` then turns that list into a `<Document>` element that `DocumentXml.Parse` reads, so a LaTeX source becomes an ordinary paged note set in Latin Modern, with formulas in LM Math. The split view, `TexEditorControl`, calls it on a debounced recompile and shows what it collects in `errors`.

LaTeX commands that are plain macros are not C# at all. They live as TeX text in `TexFormat.Prelude`, which is read before every source, so `\textbf`, `\emph`, `\textcolor` and `\maketitle` keep LaTeX's own structure; sectioning, lists, sizes and font switches are C# primitives. Anything the math parser also knows (`\,`, `\quad`, `\ldots`, `\{`) is a C# primitive instead, because inside math the typesetter writes tokens back as source with expansion on, and a macro would expand to `\kern…` and break the formula. Text symbols, accents and spacing are therefore C# tables.

The amsmath environments are numbered here, not in [[Math Parser]]. `equation` and the `align` family take a number per row, `\label` inside one points at that number and `\eqref` resolves to it in parentheses. The typesetter does it by rewriting the source: each numbered row gets a `\tag{n}` before the formula reaches the parser. `\DeclareMathOperator` is a prelude macro over `\operatorname`.

It never throws. Problems are collected in `errors` and the run recovers the way LaTeX does: an `\end` that matches nothing is reported, a `$` that never closes is "Missing $ inserted", an undefined environment is an error.

> The route, the forks taken and the phases still to come are in `ClaudeMemory/Context/tex-plan.md`.

## API summary

| Member | Kind | Summary |
| --- | --- | --- |
| `TexTypesetter(string source, ITexFontMetrics? metrics, string? folder = null)` | constructor | Starts a run; no metrics means a fixed 10pt em and ex, and a text width of its length × size/2 (`FixedMetrics`). `folder` is where pictures and `.bib` files are looked for. |
| `Run()` | method | Typesets the whole source, then `TypesetBibliography()` and `Resolve()`. |
| `vlist` | field | The vertical list: paragraphs, vertical glue, penalties and rules. |
| `footnotes` | field | The footnotes, one `TexFootnote` (id and vlist) per `\footnote`; they replace the old endnotes and are lowered to `<Footnote>` groups. |
| `footnoteSkips` | field | `\skip\footins` in points, 9, 10 and 10.8 for a 10, 11 and 12pt class. |
| `pageStyle`, `pageStyles` | fields | The document's page style name and the named styles, each a `TexPageStyle`. |
| `errors` | field | The `TexError` list. |
| `hyphenation` | field | Word to break points, filled by `\hyphenation{…}`; document exceptions beat the pattern file's. |
| `parIndent` | field | The paragraph indent in points, 15, 17 or 18 for a 10, 11 or 12pt class. |
| `displaySkips` | field | The display skips in points, 10, 11 and 12 for a 10, 11 and 12pt class. |
| `lineSpread` | field | The `\baselinestretch` in force when the first body paragraph ended; lowering sets the document's line height to 1.2 times it. |
| `unicodeMath` | property | True when unicode-math or mathspec is loaded; lowering then leaves `DocumentLayout.MathRules` at OpenType, otherwise writes `MathRules="Tfm"` so math scripts and limits follow TeX's classic rules. |
| `documentClass`, `sizeOption`, `paper` | fields | What `\documentclass[…]{…}` and `geometry` asked for. |
| `marginTop`, `marginBottom`, `marginLeft`, `marginRight` | fields | Margins in mm; null means the class default. |
| `normalSize`, `expander` | fields | The base size and the expander underneath. |
| `labels`, `citations`, `bibLabels` | fields | The reference tables `Resolve()` fills each `TexRefNode` from. |
| `paperWidth`, `paperHeight` | fields | The paper size in mm. |
| `textWidth`, `textHeight`, `textWidths`, `TextHeight` | fields | The text area in scaled points; `textWidths` and `TextHeight` are static. |

## Nodes

| Type | Holds |
| --- | --- |
| `TexChar` | a character with its `TexFont` and `TexStyle` |
| `TexGlueNode` | a `TexGlue` (width, stretch, shrink, orders); `interword` marks a space between words |
| `TexKern` | a fixed gap |
| `TexPenalty` | a break cost; `Forced` is -10000 |
| `TexMathNode` | the formula's source text and whether it is display |
| `TexHBox` | a horizontal box of nodes |
| `TexParagraph` | a horizontal list and its `TexParStyle` |
| `TexVGlue`, `TexVPenalty` | vertical glue and penalties; a `TexVGlue` with `merge` set is `\addvspace` glue, which takes the larger of itself and the pending glue instead of adding |
| `TexRuleNode` | a horizontal rule |
| `TexRefNode` | a `\ref` or `\cite`: keys, whether it is a cite, its style and source position, and a mutable `text` that starts as "??" |
| `TexImage` | an `\includegraphics`: path, width and height in scaled points, scale, angle, keep-aspect |
| `TexBibliographyMark` | where `\bibliography` stood, so the bibliography can be spliced in there |
| `TexColumn` | a tabular column: alignment and width in scaled points, 0 meaning natural |
| `TexTableCell` | one tabular cell: its vertical list and its column span |
| `TexTable` | a tabular: columns, rows, its rules, the `\|` count at each column boundary, its alignment and its source line |
| `TexTableRule` | one horizontal rule at a row boundary: its kind (`TexRuleKind`: Plain, Heavy, Light or Cmid), the columns it spans, whether each end is trimmed, and a width in scaled points, 0 meaning the kind's own |

`TexParStyle` carries the paragraph's kind (`TexParKind`: Text, Heading, Code, Quote), heading level, alignment (`TexAlign`), list kind (`TexListKind`), list depth, depth within its list kind, item number and whether it starts an item, and `indent`, which marks an indented paragraph. `TexFont` is family (`TexFamily`: Roman, Sans, Mono), bold, italic and size in scaled points; `TexStyle` is font, colour and underline.

## Methods

### Typesetting a paragraph
	read the next token from the expander
	a character
		set it in the current font, colour and underline
		a ligature in Roman or Sans merges with the characters that follow, braces break it, mono has none
	a space
		append interword glue   // a source space carries the tracked space factor; control space, `~`, labels and footnote marks stay 1000
	a control sequence from a table
		append the text symbol, or compose the accent with its base
	`$` or `\(` or `\[`
		collect the formula as source text with undefined commands passed through
		append a `TexMathNode`
	`\par` or a blank line
		take the alignment now, as TeX does
		close the paragraph and append it to `vlist` with vertical glue

### Math collection
	turn on `passUndefined` so user macros expand and `\frac`, `\alpha` reach the math parser
	for each token until the closing delimiter
		write the token back as source, a control word gets a trailing space
		a `\par`, an `\end…` or an internal `@end…` sentinel ends the formula with "Missing $ inserted"

### Math environments
	the names matrix, pmatrix, bmatrix, Bmatrix, vmatrix, Vmatrix, smallmatrix, cases, array, aligned, alignedat, gathered and split are primitives
		inside math, `Append` writes them back as `\begin{x}` and `\end{x}` source for the parser
		outside math, they report "Missing $ inserted"
	`Ends` lets an `\end` of one of them through, so it does not close the formula being collected
	equation, align, gather, flalign, alignat and multline, starred or not, are collected as display math with `MathUntil`

### Numbering an equation environment
	split the collected body into rows at top-level `\\`, outside braces and nested `\begin…\end`
	if the environment is equation or multline
		it takes one number, multline's on the last row
	for each row
		skip it when it is empty or holds `\notag` or `\nonumber`
		take the next equation number, `n` in an article and `chapter.n` in a report or book
		put `\tag{n}` in the row
		map its `\label` key to n, or to the tag's own text when the row already has a `\tag`
	write the body back wrapped in `\begin{env}…\end{env}`, except for equation

### Grouping and state
	font, colour, underline, alignment, list frame, quote and hbox are saved through the expander's save stack with `Save(Action restore)`
	so `{\bf x}` and an environment restore as TeX does
	a heading or footnote suspends the outer state on an internal frame stack
	the sentinels `\@endhead` and `\@endnote` close it
	`\@runin` and `\@label` skip the space that follows

### Ligatures and accents
	`` `` `` becomes “, `''` becomes ”, ` becomes ‘, ' becomes ’
	`--` becomes –, `---` becomes —, `!`` and `?`` become ¡ and ¿
	ff, fi, fl, ffi and ffl become the single characters U+FB00 to U+FB04   // only when the family has no fontspec name or its name starts with "Latin Modern" (`JoinsF`); a fontspec font gets only the text ligatures: the dashes, the quotes, ¡ and ¿
	an accent composes with its base through Unicode NFC (base plus combining mark)
	`\i` and `\j` as accent bases become i and j
	an accent over nothing gives the spacing accent
	a form with no precomposed character keeps the base letter and logs a warning

### Space factor and the breaking registers
	`SfCode (c)`
		`.`, `?` and `!` give 3000
		`:` gives 2000
		`;` gives 1500
		`,` gives 1250 under `\nonfrenchspacing`, else 1000
		`)`, `]`, `'`, `’` and `”` give 0
		a Unicode capital gives 999
		anything else gives 1000
	`AddChar (c)`
		`code` = `SfCode (c)`
		if `code` is 0
			leave `spaceFactor` as it is
		else if `code` > 1000 and `spaceFactor` < 1000
			`spaceFactor` = 1000
		else
			`spaceFactor` = `code`
		the factor is reset to 1000 when the last node is not the character that set it
	`\@` sets the factor to 1000
	`\frenchspacing` is `FrenchSpacing(true)`: save the outer `frenchSpacing` through `expander.Save`, then set it, so every space takes factor 1000 until the group ends
	`\nonfrenchspacing` is `FrenchSpacing(false)`, group-local the same way
	`Interword (factor)`
		append a `TexGlueNode` with `spaceFactor` = `factor`   // only a source space passes the tracked factor
	`tolerance`, `pretolerance` and `emergencyStretch` read the registers in pt, for lowering to write

Lowering also writes `AdvanceGrid` = 1000 on the document's `DocumentLayout`, so the measurer truncates every glyph width and kern to 1/1000 em as LuaLaTeX does. `TexLowering.Advance`, and so `TextWidth` (used by `\settowidth` and leaders), applies the same grid.

### Hyphenation commands
	`\-` adds a U+00AD character, and the prelude no longer defines it as empty
	for each word in `\hyphenation{…}`
		`TexHyphenator.Exception` splits "as-so-ciate" into the word and its break points
		store them in `hyphenation`

### Sections
	article numbers down to subsubsection, report and book to subsection and add chapters
	section is bold at `\Large`, subsection at `\large`, subsubsection at the normal size
	a chapter is a "Chapter N" Text block at `\huge` and its title as a Heading1 at `\Huge`
	`\paragraph` and `\subparagraph` are bold run-in headings
	`\chapter` in an article is an error and is set as a section
	the number is the text of `\the<name>` expanded   // so LTPlius's `\renewcommand\thesection{\arabic{section}.}` gives "1." and "1.1."
	report and book define `\thesection` as `\thechapter.\arabic{section}`
	the first time a sectioning command is dispatched
		insert `\ifcsname <name>break\endcsname\<name>break\fi`, then the command again   // a defined `\sectionbreak` runs before every `\section`

### Vertical glue and indent
	`VSkip (glue, merge)` appends vertical glue with its natural width and its shrink   // the shrink reaches `ShrinkBefore` in the lowering; `\topsep`, `\itemsep`, `\parsep`, `\parskip`, `\partopsep`, display skips and default section skips carry `minus`; float separations, enumitem keys and titlesec spacing stay rigid
		`\vskip`, `\vspace` and `\bigskip` add
		structural skips (sections, lists, floats, verbatim, quote and center) set `merge`, so they take the larger of themselves and the pending glue like `\addvspace`
	a heading is preceded and followed by a skip, `ex` being the normal font's
		chapter 50pt above and 40pt below
		section 3.5ex above and 2.3ex below
		deeper levels 3.25ex above and 1.5ex below
		a run-in heading is not indented
	for a list
		the opening skip is `TopSkip` plus the outer `\parskip` (`OuterParskip`: the enclosing list's `\parsep`, else the register); the end skip has none
		list indents are `\leftmargini`..`iv` in em of `\normalsize` (2.5, 2.2, 1.87, 1.7)
		`ListSkip` gives `\topsep` above and below, `\itemsep + \parsep` between items, `\parsep` between paragraphs of one item
		the skip is full at depth 1, half at depth 2 and a quarter deeper
		`\topsep` is 8, 9, 10pt and `\itemsep` 4, 4.5, 5pt for 10, 11, 12pt
	a float sets no glue of its own; `\floatsep` and `\intextsep` are 12, 12, 14pt for 10, 11, 12pt (`floatSeps`) and `\textfloatsep` is 20pt (`TextFloatSep`), written to `<Page>` by lowering
	`EndEnvironment` sets `afterEnvironment`
	`\maketitle` in the prelude puts `\vskip2em` above, 1.5em and 1em between title, author and date, and 1.5em below
	a paragraph is marked indented, `parIndent` wide, unless
		`\noindent` was seen, which only counts in vertical mode (`noIndent`)
		it follows a heading, until text (`afterHeading`)
		it follows a list or trivlist environment or a float, until a blank line (`afterEnvironment`; `\par` in vertical mode clears it)
		it is inside a list or quote
		it is not justified: centred, flush or ragged
		it is a `\bibitem` entry

### Floats and tabulars
	`BeginFloat` keeps the `[...]` option through `Placement`
		the letters `htbpH!` are kept and others dropped
		an empty option becomes `tbp`, a lone `h` becomes `ht`
	the body is redirected into `TexFloat.vlist` by pushing a frame, `openFloat` marks it
		the float sits in the vertical list, or in the hlist when it is met mid-paragraph
		the last thing it does is insert `\@parboxrestore` into the token stream, inside the float's group
			the prelude's `\@arrayparboxrestore` resets `\parindent`, `\parskip`, `\leftskip`, `\rightskip` to 0pt and `\parfillskip` to 0pt plus 1fil
			LaTeX also resets `\lineskip`, `\baselineskip` and others, which the format does not model
	`EndFloat` closes `figure`, `table` and the starred forms
		after a mid-paragraph float the space that follows is eaten when one preceded it (`\@esphack`)
	a float inside a box, footnote, caption, heading, table cell or another float reports "LaTeX Error: Not in outer par mode" and stays inline
	where the float lands on a page is `Paginate`'s decision, not the typesetter's
	`\caption` sets a centred paragraph built from the language's `\fnum@<kind>` and the caption settings (see Languages and captions), numbered chapter.n in a report or book
	`Caption`
		if the caption follows float content
			add a VSkip of the `abovecaptionskip` register   // 10pt from the prelude
		mark the caption paragraph `strut`   // not for algorithm floats
	`EndCaption`
		if the caption came first
			add the `abovecaptionskip` skip after it
	`SingleSpaced`   // setspace single-spaces footnotes and floats
		true in a footnote or a float, except the body of an `[H]` float
		a caption stays single
	`\includegraphics` looks for the file under the source's folder and each `\graphicspath` entry
		with no extension, try .png, .jpg, .jpeg
		a PDF, EPS or PS is reported as unsupported
		a missing file is reported as "File `x' not found"
	a tabular ends the paragraph it appears in
	`BeginTabular`
		if the tabular opens a paragraph in vertical mode, outside tables and boxes
			`TexTable.indent` = `\parindent`   // TeX indents it and lets it overflow right
			`TexTable.opens` = true
				add the space `StartParagraph` adds   // list `\parsep`, else `\parskip` when not in a list, no keepSkip and not at an environment start
				consume noIndent, afterHeading and afterEnvironment
				clear environmentStart
	for each cell
		start from the tabular's starting style, a cell is not an expander group
		`&` ends the cell and the spaces after it are skipped
		`Dispatch` starts a cell with `StartCell` when a tabular is open with no cell and the next token is a non-space character or a known command that is not in `rowStart`
			`rowStart` holds hline, cline, toprule, midrule, bottomrule, cmidrule, multicolumn and endtabular
			an unknown command does not start a cell
			so the `{\bfseries` of `{\textbf{Aa}} & …` runs inside the first cell, and the style does not leak out of the table
		`StartParagraph` and `BeginTabular` still start a cell lazily as a backstop
	`\multicolumn` gives the cell its span
		a `|` in a `\multicolumn` spec is ignored
	`|` in the column spec is counted into the tabular's `vrules`, one count per column boundary
	`\hline`, `\cline{a-b}`, `\toprule`, `\midrule`, `\bottomrule` and `\cmidrule` leave a `TexTableRule`
		`\cmidrule` reads its optional width `[w]` and its `(l)`, `(r)` trims
		the rule goes at the row boundary under the last row when no row is open, otherwise above the open row
	the tabular's alignment is taken when the paragraph holding it ends, as TeX does for text
		`\centering`, `center` and `flushright` give a centred or right table
		a paragraph that ended justified gives a left table

### Pages
	`\newpage` and `\clearpage` (`PageBreak`) leave a `TexPageBreak`, with `clear` set for `\clearpage`
		`\cleardoublepage` is `\clearpage` and `\pagebreak` is `\newpage`, in the prelude
	`\pagestyle` and `\thispagestyle` (`SetPageStyle`) name a style from `pageStyles`
		`DefaultPageStyles` gives empty, plain, headings and myheadings
		the document starts in plain for an article or report, headings for a book
		`\maketitle` does `\thispagestyle{plain}`
	`\markboth` and `\markright` (`Mark`) are kept on the next paragraph as `markLeft` and `markRight`
		`\markright` keeps the left half
	a heading sets marks (`HeadingMarks`), a starred heading none
		headings style: an article `\section` is `\markright{UPPER(num␣␣title)}`, a report or book `\chapter` is `\markright{CHAPTER n. TITLE}`
		fancyhdr: an article `\section` is `\markboth{UPPER(num␣␣title)}{}` and a `\subsection` `\markright{num␣␣title}`
		fancyhdr: a report or book `\chapter` is `\markboth{CHAPTER n. TITLE}{}` and a `\section` `\markright{UPPER(num. title)}`
	`\thepage`, `\leftmark` and `\rightmark` (`Field`) write `PageField`, `LeftMarkField` or `RightMarkField` (U+E000–E002), the field characters a slot's text carries
		outside a running-head slot they print "??"
	`\fancyhf`, `\fancyhead`, `\fancyfoot`, `\lhead`, `\chead`, `\rhead`, `\lfoot`, `\cfoot`, `\rfoot` (`FancySlots`, `SetSlot`) fill a `TexPageStyle`'s slots by place name
	`\fancypagestyle` (`FancyPageStyle`) defines a named style
	`\headrulewidth` is 0.4pt and `\footrulewidth` 0pt, global to the document
	`\pageref` leaves a `TexRefNode` with `page` set, and `\label` leaves a `TexLabelMark` where it stands
		the number is filled after layout, see `TexEditorControl.ResolvePages` below

### Footnotes
	`\footnote` sets its text in `\footnotesize` into a `TexFootnote`
		the id is the footnote's sequence number, not the counter, so `\setcounter{footnote}` cannot make two share an id
	the mark in the text is a math node with `note` set, which lowering writes as a run with `Note="id"`

### Preamble and packages
Everything before `\begin{document}` is the preamble. `\documentclass` points the vertical target at a private list that is thrown away, so text written there is dropped and `LaTeX Error: Missing \begin{document}` is reported once. A fragment with no `\documentclass` still typesets. `\documentclass` also sets `\parindent` to the class's 15, 17 or 18pt, and `\hyphenpenalty` of 10000 or more turns hyphenation off for the lowering.

The length and parameter commands are expander primitives. `\newlength` takes the next skip register from 256, `\setlength` and `\addtolength` push an assignment and an `\advance`, and `\dimexpr` evaluates sums, products by integers and parentheses of lengths. `\parindent` is a dimen as in TeX, not a skip. `\pagetotal` always reads 0pt and says so, which `\pdfpageheight` uses as a signal.

#### `\pdfpageheight`
	if the value contains `\pagetotal`
		`fitHeight` = the rest of the value   // the last page is cut to its content plus this
	else
		the value is the paper height

#### `Section` with titlesec
	start from normalfont at the normal size, then the `\titleformat` format tokens
	an empty label shows no number
	`\the<level>` in a label is replaced by the full number
	the before-code is followed by the title in braces   // a before-code ending in `\MakeUppercase` takes the whole title
	`\titlespacing`'s left argument becomes the heading's indent
	run `@endhead`, then the after-code
	`@endheadafter` adds the after-skip and keeps the next paragraph unindented

#### `BeginList (kind, name, token)` with enumitem
	settings = the settings for "", then for `kind`, then for `name`   // from `\setlist` and `\newlist`
	apply the options in the square brackets over them
	`Item`, `StartParagraph` and `EndEnvironment` use the label, `leftmargin`, `itemsep`, `parsep` and `topsep` they hold
	a label's commands pick a marker: `\rule` and `\blacksquare` Square, `\textbullet` Disc, `\circ` Circle, `\arabic` Decimal, and so on   // anything else keeps the default marker
	inside the environment's group, set `\topsep`, `\itemsep` and `\parsep` from those settings or the class default   // as `\@listi` does at every list's begin
	`Item` adds the `\itemsep` and `\parsep` registers
	the skip at the end of the list stays the value computed at the begin

A global `\setlength` of `\topsep`, `\itemsep` or `\parsep` is overwritten at the next list's begin, as in LaTeX, and inside a list only `\itemsep` and `\parsep` take effect, because the end skip was computed at the begin. `\partopsep` is a register but is not added.

`\setlist` with a number reads as every level. Only seven enumitem keys are read; the others are logged. Times packages (`mathptmx`, `times`, `tgtermes`, `newtxtext`) set `times`, and lowering then sets the text in TeX Gyre Termes.

#### Line spacing with setspace
	`\setstretch{x}` and `\linespread{x}` call `Stretch(x)`, which sets `lineStretch`   // `\linespread` needs no `\selectfont`
	`\onehalfspacing` calls `Stretch` with the entry of `oneHalfStretches` for the class size (1.25, 1.213, 1.241 at 10, 11, 12pt)
	`\doublespacing` calls `Stretch` with the entry of `doubleStretches` (1.667, 1.618, 1.655)
	`\singlespacing` calls `Stretch(1)`
	`EndParagraph`
		if this is the first body paragraph of the main list in the document
			`bodyStretch` = `lineStretch`
	a stretch changed after that is logged once and ignored
	`lineSpread` = `bodyStretch`, and lowering sets the document's `LineHeight` to 1.2 × `lineSpread`
	`displaySkipStretch` = `lineSpread` when setspace is loaded, else null   // setspace's `\displayskipstretch`
		lowering multiplies the four display skips by it and drops their shrink
		`Float()` divides it back out, except for a placement containing `H`

The document's line height is the body `\baselineskip` over `\normalsize`, stretched by `lineSpread`; lowering writes it as the layout's `LineHeight`. A paragraph whose pitch differs gets its own `<Block LineHeight>`: floats and footnotes are single-spaced when setspace is loaded, and `\fontsize`'s second argument is read. A paragraph's pitch is the `\baselineskip` in force at paragraph end (TeX's rule), so a `\fontsize` inside a group that closes before `\par` uses the outer skip. `\newfontface\cs{…}` defines `\cs` to set the face and then run `\selectfont`, so `\fontsize{a}{b}\cs` takes the pending size.
#### Baseline spacing and vertical skips
Array, matrix and cases rows in a formula are spaced by a strut the typesetter computes per formula: `ArrayStrut()` is `\arraystretch` (expanded and parsed, default 1 from the prelude) times the `\baselineskip` at the formula (stretched by `\baselinestretch`, single in floats and footnotes under setspace) over the font size, set in `Place` when the source contains `\begin{` and written on the run as `Strut` (em). The math layouter scales its row struts by strut / 1.2, so a formula with no strut, and every note, is unchanged. A TeX line's pitch is its `\baselineskip`, not a multiple of its font size. The typesetter keeps `TexStyle.baselineSkip` (sp, 0 = the size command's `baselineSkipPoints` entry for 10/11/12pt) and stamps each paragraph with it, stretched by `lineSpread`. Lowering turns a pitch that differs from the document's into `<Block LineHeight>`.
	`BaselineSkip(style)`
		if `style.baselineSkip` is set
			return it
		return `baselineSkipPoints` entry for the style's size command and the class size
	`EndParagraph`
		`paragraph.baselineSkip` = `BaselineSkip` of the style in force now   // TeX reads \baselineskip at \par
	`CenterCr(skip)`   // \\ in center, flushleft, flushright, \centering, \raggedright, \raggedleft
		end the paragraph
		`\addvspace{-\parskip}`
		`\vskip skip`
	`\\` before a blank line in justified text
		lowering adds an empty last line   // TeX's underfull \hbox
	`TopSkip(top, vertical)`
		if the list or center/flush environment starts in vertical mode
			`top` += `partopSeps` entry for the class size   // \partopsep, above and below
	merged skip in lowering (`Spacing.last` is `\lastskip`)   // \@xaddvskip
		if `last` is 0: add the skip
		else if `last` < skip: raise `last` to the skip
		else if the skip is negative and `last` is not: add the skip
		a plain \vskip adds and becomes `last`, so a \vskip 0pt resets it
	`\centerline`
		a centred paragraph that keeps the outer `\baselineskip` (`\@keepskip`) and adds no `\parskip`
	a trivlist environment's first paragraph has its `\parskip` taken back
	a display heading adds `\parskip` after its before-skip
Display skips: a display that starts a paragraph (a blank line before it) gets `\parskip` and one empty line above and `\belowdisplayshortskip` below; a display in mid-paragraph gets `\abovedisplayskip` and `\belowdisplayskip`; the skip below applies even when the display ends its paragraph. A caption measures its natural width with `Width(hlist)`: it is centred when it fits `\linewidth` (caption's `singlelinecheck`), else justified.

#### Fonts with fontspec
`fontspec` is a known package. Its commands record or select a font by the name the source gives, and lowering decides what that name draws in (see Font resolution below). The file keys (`Path=`, `Extension=`, `UprightFont=`, `BoldFont=`, `ItalicFont=`, `BoldItalicFont=`, with `*` meaning the name) are recorded in `TexTypesetter.fontFiles` as absolute paths under the source's folder, so a font that ships beside the document is used; the other options (`Ligatures=TeX`) are read and ignored.
	`\setmainfont`, `\setsansfont` and `\setmonofont` store the name `FontName` reads in `mainFont`, `sansFont` and `monoFont`
	`\fontspec{name}` sets the `face` of the current `TexFont` to the name
	`\newfontface\cs{name}` and `\newfontfamily\cs{name}` both call `NewFontFace`
		`\cs` sets `face` to the name
		then runs `selectfont`   // so a pending `\fontsize` still applies
	`FontName`
		read `[options]{name}[options]`
		return the name and drop the options
	`\rmfamily`, `\sffamily`, `\ttfamily`, `\normalfont` and the Mono switches (`\verb`, verbatim and code paragraphs, `\url`) clear `face`
	a face overrides the family and keeps series, shape and size
	`TexFont` equality includes `face`, so runs split at a face change

#### Languages and captions
`polyglossia`, `caption` and `indentfirst` are known packages, and `babel` takes its main language from the last option that has a `\captions<name>`. A language decides three things: the caption names, the hyphenation patterns of a paragraph, and whether the first paragraph after a heading is indented. The language is per paragraph: a `TexParagraph` records the one it started in, so `\foreignlanguage` and a `\selectlanguage` in mid-paragraph do not change how the words they cover hyphenate.
	`\setmainlanguage` and `\setdefaultlanguage` store `mainLanguage`
	`\setotherlanguage[opts]{x}` is read, `\setotherlanguages` is read and ignored
	`\SetLanguageKeys{x}{indentfirst=…}` sets the language's entry in `indentFirsts`   // Lithuanian is true
	`SelectLanguage (name, captions)`
		set `language` and `indentFirst`
		when captions, insert `\captions<name>`
		`expander.Save` restores both when the group ends
	`\selectlanguage`, `\foreignlanguage[opts]{x}{text}` and the `otherlanguage` environments call `SelectLanguage`   // `\foreignlanguage` sets its text in a group and does not apply the language
	`\begin{document}`
		when a package set `mainLanguage`, `SelectLanguage (mainLanguage, true)`   // otherwise a preamble `\renewcommand\figurename` in a plain document would be overwritten
	`StartParagraph`
		indent unless the paragraph follows a heading and `indentFirst` is false
		record `language` on the new `TexParagraph`
	`\captionsetup[kind]{keys}` (`CaptionSetup`)
		for each of the keys name, labelsep, labelfont, font, textfont, justification, singlelinecheck
			store a `\caption@key` macro, or `\caption@key@kind` for one kind   // so a `\captionsetup` inside a float ends with its group
		other keys are read and ignored
	`Caption`
		pick the caption's alignment from `\caption@justification`   // centering is Center, raggedright Left, raggedleft Right, anything else Justify
		when `singlelinecheck` is false, off or no, start in that alignment, otherwise centred   // an overflowing caption takes the alignment (`captionJustify`) in `EndCaption`
		insert `{font {lfont [\let\<kind>name\caption@name@<kind>] \fnum@<kind>{n}} sep {tfont text}} \@endcaption`
	`\captionsenglish` defines `\fnum@figure` as "Figure~n"
	`\captionslithuanian` defines `\fnum@figure` as "n~pav."
	`\nobreakspace` and `~`
		`StartParagraph`
		`TexPenalty (10000)`
		interword glue   // TeX's tie, not the U+00A0 glyph

#### Table of contents and lists
The lists are not formatted in C#. `TexFormat.Prelude` holds ports of latex.ltx's `\contentsline`, `\numberline` and `\@dottedtocline`, article's `\l@section` to `\l@subparagraph` and `\l@figure`, `\l@table`; `ChapterContents` adds `\l@chapter` for report and book. The `tocloft` and `tocbibind` packages are built in: `ReadCode` reads their code like a local package, with `@` a letter until it ends. Entries are this compile's headings, typeset at the `\@starttoc` mark after the run, as the bibliography is.
	new primitives: `\leaders`, `\addcontentsline`, `\addtocontents`, `\@starttoc`, `\@endcontents`, `\@afterindenttrue`
	`\leaders` sets `TexGlueNode.leader`
	`Box` honours `\hbox to`   // `TexHBox.width`, -1 is natural
	`\@afterindenttrue` clears afterHeading   // an entry keeps its indent box after a heading
	`EndParagraph` records `\leftskip`, `\rightskip`, `\parfillskip` and `\interlinepenalty` in `TexParStyle` and `TexParagraph.interlinePenalty`   // 5000 or more keeps the paragraph on one page
	`AddChar` sets `straightQuote` on the `”` that an ASCII `"` becomes
	`StartParagraph` records `\parindent`
	`\tableofcontents`, `\listoffigures`, `\listoftables`
		set the title as `\section*` of `\contentsname`, `\listfigurename` or `\listtablename`   // tocloft's [titles] mode
		`\@starttoc{toc|lof|lot}` leaves a `TexContentsMark (extension)`
	`Section (level, [short], title)`
		typeset the heading
		`headingContents` = `\addcontentsline{toc}{level}{\protect\numberline{n}title}`   // `[short]` replaces the title
		`InsertHeadingContents` writes it at `@endhead`, or `@runin` for a run-in heading
		label the heading `@toc<n>`
	`Caption`
		write the lof or lot entry the same way, with `[short]`
	`BeginBibliography`
		when `bibInContents`, add a toc entry   // set per list in `TypesetBibliography`: `\printbibliography[heading=bibintoc]`, or tocbibind unless `notbib` when biblatex is not loaded
	`\addcontentsline{ext}{level}{text}` and `\addtocontents{ext}{text}` (`AddContentsLine`, `AddContents`)
		`Protected (text)`   // `\protect` becomes `\noexpand`, then `Expanded`, as \protected@write does
		append it to `contentsTokens` and record the range in `contentsEntries`
	`TypesetContents`   // after `TypesetBibliography`, before `Resolve`
		for each `TexContentsMark`
			wrap its entries in `\begingroup\@starttocsetup … \par\endgroup`   // tocloft sets `\parskip\cftparskip`, 0pt, there
			typeset them and splice the list in at the mark
		a bibliography entry goes where its mark stood   // `bibContentsAt`, `contentsAt`
	an entry's page is `\pageref{@toc<n>}`   // filled by `ResolvePages`, so two passes are the preview reload
	`\l@<level>` runs `\@dottedtocline`   // tocloft: `\@cftentry`
		`\leftskip` = the level's indent, `\rightskip` = `\@tocrmarg`, `\parfillskip` = `-\rightskip`
		`\hskip -\leftskip` starts the entry
		the page number sits in a right-aligned box of `\@pnumwidth`
	`\cftparskip`, `\cft<L>indent`, `\cft<L>numwidth` and the `\cft<L>font`, `pagefont`, `dotsep`, `leader` hooks exist per level   // sec, subsec, subsubsec, para, subpara, fig, tab

#### Bibliography and biblatex
`biblatex` and `silence` are known packages. Without biblatex the bibliography is the BibTeX one: `TexBibliography.Format` writes a `thebibliography` source in the `\bibliographystyle`. With biblatex loaded, `TexBiblatex.Format` writes that source instead: the drivers of the standard styles are ported to C# in the document's language (English and Lithuanian string tables transcribed from biblatex's lbx files), for article, book, inproceedings, thesis, online and misc, which every other entry type uses. Both paths end in the same hanging list: a label column whose width is measured, with the entries set against `\leftskip`. Customizations that are data are honoured (strings, name delimiters, name aliases, `\labelalphaothers`); hooks that are code (`\DeclareFieldFormat`, `\renewbibmacro`, drivers, `\defbibheading`) are read, ignored and logged.
	`Packages (biblatex, options)`
		`BiblatexOptions (options)`   // `TexBiblatex.Option` per key: style, sorting, maxbibnames, minbibnames, maxalphanames, minalphanames, giveninits, abbreviate, sortcites, eprint, doi, url, isbn, date, urldate, autolang
		the commands below take effect once `biblatex` is set
	`\addbibresource[opts]{file}`
		append the file to `bibFiles`
	`\DefineBibliographyStrings{lang}{key = text, …}`
		store the overrides in the settings' `strings`
	`\DeclareDelimFormat[context]{name}{code}`
		when no context is given, keep the code as source in `delims`   // `Source` puts a space after control words
	`\DeclareNameAlias{default|author|editor}{given-family|family-given|family-given/given-family}`
		store it as the settings' `nameFormat`
	`\parencite` and `\autocite` are `\cite`
	`\url` inside the bibliography, with biblatex loaded (`bibUrls`)
		the same atom loop as the plain `\url` below, with the big breaks `:` `-`
		set the style's `urlWidth`, `urlStretch` and `urlShrink` from `RegisterMuGlue("biburlbigskip")` (0mu plus 3mu) through `MuToSp`, `urlBigBreakPenalty` to `Counter("biburlbigbreakpenalty")` (100) and `urlBreakPenalty` to `Counter("biburlbreakpenalty")` (200)   // `\biburlsetup`; the plain `\url` reads the document's penalties instead
	`Packages (url, hyperref)`
		when `\UrlBreakPenalty` is undefined, insert `\mathchardef\UrlBreakPenalty=\binoppenalty \mathchardef\UrlBigBreakPenalty=\relpenalty`   // url.sty's own lines: the penalties are captured at load, so a later `\binoppenalty` has no effect on URLs
			when `\Urlmuskip` is undefined, insert `\newmuskip\Urlmuskip \Urlmuskip=0mu`   // url.sty's
	`\printbibliography[opts]` (`PrintBibliography`)
		read `heading=bibintoc` and `title=`
		`PlaceBibliography (title, inContents)` places a `TexBibliographyMark` and adds it to `bibMarks`   // one per call, each with its own title, language and contents flag
	`\hyperref[label]{text}`
		set the text plainly, no underline   // the four-argument URL form is not handled
	`\ref*` and `\pageref*`
		`Reference` takes the optional star
	`TypesetBibliography`, under biblatex
		`TexBiblatex.Format (cited, settings, language, title)`
			`Sorted`   // alphabetic: label with a, b, c after equal labels; numeric: numbers; compared with the language's CompareInfo
			for each entry
				`Entry` writes the driver's units   // the `Units` tracker adds the punctuation and capitalizes at a sentence start
		read the source with `@` a letter (`ReadCode`)
		the heading is the language's `references` (article) or `bibliography` (report, book) string, set by `\def\refname` or `\def\bibname`   // `title=` overrides it, `abbreviate` picks the short form
	`BeginBibliography`
		pass `{widest}` to `\@bibsetup`
		set `\parskip` to the list's parsep
		under biblatex `FrenchSpacing(true)`   // `\bibsetup`'s `\frenchspacing`, restored at the group end so the text after the bibliography keeps sentence spacing
	`\@bibsetup{widest}`
		`\settowidth\labelwidth{\@biblabel{widest}}`
		`\leftmargin` = `\labelwidth` + `\labelsep`
		`\leftskip\leftmargin`, `\parindent0pt`
	`BibItem`
		add topsep before the first entry and itemsep between entries
		record `bibOrder`
		insert `\@bibitem{label}`   // `\hskip-\leftskip` then an `\hb@xt@\labelwidth` box holding the label, then `\labelsep`; lowering folds the negative skip into a negative block `Indent`
	`\settowidth`   // `SetToWidth`
		typeset the text in the current font
		`Width(hlist)` sums the characters by run through `ITexFontMetrics.Width`, glue, kerns, rules, boxes and reference text
	`Resolve` fills a citation with `CiteText`
		biblatex alphabetic joins the keys with "; ", numeric and BibTeX with ", "
		`sortcites` orders the keys by their place in the bibliography
		a label past maxalphanames shows a plain "+"
	`\url`
		un-escape `\%`, `\#`, `\_`, `\&`
		read the sets from the document   // `UrlCharacters (macro, fallback)` reads `\UrlBreaks` and `\UrlBigBreaks` on the plain path and `\UrlNoBreaks` on both, falling back to url.sty's constants; the bibliography keeps `\biburlsetup`'s break sets
		`AppendDoList (tokens, text, depth)`
			for each token of the list
				`\do\x` or `\do x` appends the character
				a parameterless macro the list calls is read the same way   // `MacroBody`, depth up to 8
		classify each character with `AtomOf (c, breaks, bigBreaks, noBreaks)`   // Open for `UrlNoBreaks` `([{<`, Rel for the big breaks `:` (`:-` under `[hyphens]`), Bin for `UrlBreaks` `.@\/!_|;>]),?&'+=#`, else Ord; a later list wins
		for each character
			a Bin becomes Ord when it is first, follows a Bin, Rel or Open (after conversion), or precedes a Rel   // `://` breaks after `:` or the second `/`, never between the slashes
			insert U+E005 after a remaining Bin
			insert U+E006 after a Rel that is not followed by a Rel
			nothing after the last character
		set the style's `urlBreakPenalty` to `RegisterValue("UrlBreakPenalty") ?? RegisterValue("binoppenalty")`
		set `urlBigBreakPenalty` to `RegisterValue("UrlBigBreakPenalty") ?? RegisterValue("relpenalty")`
		on the bibliography path, set `urlNum…`, `urlUc…` and `urlLc…` penalty, width, stretch and shrink from `UrlLetters(counter, skip)`   // `biburlnumpenalty` with `biburlnumskip`, and the `uc` and `lc` pairs; a class with penalty 0 or less is all zero
		on the bibliography path, `UrlLetterMarker` appends U+E007 after a digit, U+E008 after A–Z and U+E009 after a–z when that class's penalty is above 0   // never after the URL's last character
		set `urlWidth`, `urlStretch` and `urlShrink` from `RegisterMuGlue("Urlmuskip")` through `MuToSp`   // mu × font size / (18 × 65536); an infinite order is set as finite, with one `Log.Once().Info`

#### Theorems, algorithms and the @ catcode
`amsthm` and `algorithm` are known packages. `amsthm` is read from `TexFormat.Amsthm` as working macros: a theorem head is bold "Name N" with an optional "(note)" and a full stop, the body font comes from the theorem style (plain is italic, definition and remark are upright), `\newtheoremstyle` keeps only its body-font argument, `\swapnumbers` does nothing, `\newtheorem*` is unnumbered and `proof` sets an italic "Proof." and ends with `\qed`, which is `\nobreak\hfill$\square$`. The `algorithm` package makes a float of kind "algorithm" through the same float path as figures and tables, ruled by default, with plain and boxed as the other styles (boxed draws no frame).

Every package or class read saves the catcode of `@`, makes it a letter and puts the saved value back at the end, as `\RequirePackage` does. `\input` leaves it alone.

	`Packages (amsthm)`
		`ReadCode (TexFormat.Amsthm)`
	`Packages (algorithm, options)`
		`Algorithm (options)`
			`algorithmStyle` is plain, ruled or boxed   // ruled by default
			`section` or `chapter` numbers the counter within that level, `\thealgorithm` = `\the<within>.\arabic{algorithm}`
			register `algorithm` and `algorithm*` as float kind "algorithm"
			read `TexFormat.Algorithm`
			if ruled
				`\caption@lfont@algorithm` = `\bfseries`
				`\caption@sep@algorithm` = `\space`
	`FloatStyle (vlist)`
		if ruled
			rule, 2pt, caption, 2pt, rule, 2pt, body, 2pt, rule   // the caption moves to the top wherever it was written
		else
			move the caption last
	`ReadCode (code, atEnd)`
		save `expander.atCatcode`
		set `expander.atCatcode` to Letter
		read the code
		run `atEnd`
		restore the saved catcode

#### Math in text mode
A math-only command, `^` or `_` met in text mode recovers the way TeX does. The error "Missing $ inserted" is reported, the token is pushed back and inline math starts in front of it. That math runs to the next `$`, to a `}` closing a group opened outside the math ("Extra }, or forgotten $"), or to `\par` or the end of an environment. Text set as math loses its spaces and any letter the math font lacks. The expander knows which names are math-only through `mathOnly`, which `TexLowering.Compile` sets to the keys of `MathSymbols.commands`, `bigOps`, `operatorNames`, `accents` and `delimiters`, because `Core.Tex` does not reference `Core.UI`.

	`MathShift`
		track the brace depth at the start of the math
		on a `}` that closes a group opened outside the math
			report "Extra }, or forgotten $"
			end the inline math
			dispatch the `}`
	`^` or `_` in text
		report "Missing $ inserted"
		push the token back
		start inline math
	`TexExpander` meets a name in `mathOnly` that is undefined outside math
		report "Missing $ inserted"
		push the name back
		return a `$` (MathShift) token in front of it

### Run, the end of the document
	typeset the body up to `\end{document}`
		`\ref` and `\cite` leave a `TexRefNode` carrying their style
		`\bibliography` leaves a `TexBibliographyMark`
	`TypesetBibliography`
		parse the `.bib` files with `TexBibliography.Parse`
		format the cited entries in the `\bibliographystyle` with `TexBibliography.Format` into thebibliography source   // under biblatex `TexBiblatex.Format`, see Bibliography and biblatex
		`InsertSource` pushes that source into the expander
		typeset it into a list and splice the list in at the mark
	`TypesetContents`   // the entries of the contents and the lists, see Table of contents and lists
	`Resolve`
		for each `TexRefNode`, fill its text
		a `\ref` inside a math node is rewritten to `\text{…}` in the math source

## Lowering

### `TexLowering.Compile`
	typeset the source with `TexAtlasMetrics` so em and ex come from the Latin Modern atlas, and hand it the source's folder for pictures and `.bib` files
	lower the vertical list to a `<Document>` element
	return it with the collected errors

### `TexLowering.Lower`
	`Hyphenate` first, over `vlist`, each footnote's `vlist` and each table cell, top level only so nothing inside an `\mbox` or `\hbox` is touched
		for each paragraph
			pick `TexHyphenator.For (language)` for the paragraph's `language`   // a language with no pattern file gets no hyphenation
			for each word, a run of letters in one style that is not monospaced
				capitals are lowered, and the ligatures U+FB00 to U+FB04 are taken apart for lookup
				leave the word as written when it already holds a `\-`
				leave the word as written when the node before or after it is a `TexChar` with `straightQuote`   // LuaTeX does not hyphenate next to an ASCII `"`; one instance in the thesis
				`TexHyphenator.Points` gives the break points: document exceptions, then the file's exceptions, then the patterns, with the minimums of the pattern file (2 and 3 for English, 2 and 2 for Lithuanian)
				insert a U+00AD character node at each point, none inside a ligature
	`Advance` gives U+00AD no width
	for each paragraph in `vlist`
		start one Block, and split it at every forced break (`\\`)
		for each stretch of one `TexStyle`
			write a Run
			write Run attributes only where they differ from the block's TextStyle: FontName, FontSize with FontSizeAuthored
			Bold, Italic, Underline and ColorHex are written as set
		interword glue becomes a space, or a no-break space inside an hbox
		`\mskip` and `\mkern` in text are set like `\hskip` and `\kern`, silently, with mu converted by `MuToSp` (1mu = font size / 18)   // `ScanGlue(true)` / `ScanDimen(true)`; a pending `\leaders` box is taken as by `\hskip`
		`\quad`, `\hspace` and `\kern` become a spacer run of their exact width, `<Run Text=" " Space="px"/>`, which draws nothing and never breaks a line
		penalties are dropped
		a `Spacing` holds the pending skip, the paragraph indent and the display skips
			vertical glue (`LowerVertical`) adds to the pending skip, or takes the larger of the two when it merges
			`Place` writes the pending skip as the next block's or table's `SpaceBefore` and clears it
			the paragraph indent is written as the block's `Indent`
			`Displays` cuts a paragraph at each display formula
				the text before is one block
				the display is its own block, with `\abovedisplayskip` above it
				the continuation is a block with `\belowdisplayskip` above it, and is never indented
		`\hrule` becomes a Rule block
		a `TexTable` becomes a `<Table>` with `Borders="false"`, `Padding` of `\tabcolsep` across and 0 down, and `Align`
			each column's `<Column Width>` is its natural width: Latin Modern glyph advances, the `MathLayout` width for math, the picture size for images
			add `\tabcolsep` of 6pt each side, minimum 28 px; a `p{w}` column uses w
			a merged cell wider than its columns widens the last of them
			a spanning cell is a `<Cell ColumnSpan>`
			a column's `|` become `RuleLeft` and `RuleRight`, and a rule becomes `RuleAbove` or `RuleBelow` with `TrimAbove` or `TrimBelow` on each cell it reaches
				two plain rules at one place become a double rule
			a tabular nested in a cell is flattened to one paragraph per row
		a `TexImage` becomes a Run with Image, Width, Height and Rotation
		a `TexRefNode` becomes its text in its style
	list items become `List="Bullet"` with `Level` as the total depth minus one
		the Marker follows the depth within that kind: enumerate Decimal, LowerAlpha, LowerRoman, UpperAlpha; itemize Disc, Circle, Square, SquareOutline
		an enumerate item carries `Start`, its number
		a description item is a plain Text block with its bold label inline
		a paragraph inside an item after the first is a plain Text block
	write the layout: LineHeight 1.2, BlockSpacing 0, which is LaTeX's `\parskip`, ListIndent 1.875 times the base size, OptimalBreaks true so Aurora breaks the lines Knuth-Plass
	TextStyles Text and Quote use `latin-modern`, Code uses `latin-modern-mono`, Heading levels take the size of the first heading of that level
	1pt is 96/72.27 pixels
	margins: article `\textwidth` is 345, 360 or 390pt for 10, 11 and 12pt, centred on the paper; vertical is (paper height − 550pt)/2
	the roman face is TeX Gyre Termes when the typesetter loaded a Times package, else Latin Modern; fontspec names can replace the roman, sans and mono faces (see Font resolution below); ex follows the roman
	horizontal glue of fil order or higher becomes a Run `Text=" " Fill="true"`, never trimmed
	a paragraph piece of only rule boxes and glue becomes a `<Block StylingType="Rule" Weight>`; a rule box anywhere else draws nothing
	list blocks take `Marker` and `ListIndent` from the paragraph's style
	the page is `<Page Size="Custom" Width Height>` when the source set a paper, and carries `FitHeight` when set
	a negative space above a block is written as a negative `SpaceBefore`   // `\vspace{-7pt}`

### Font resolution in `TexLowering`
A font name from fontspec becomes a font asset here. Termes and Carlito are bundled only in Thorium; in Carbon and AuroraEditor the names fall back to Latin Modern.
	`Lower` clears `named`, the per-compile cache of name to asset
	`Faces(typesetter)` sets `roman`, `sans` and `mono`
		a Times package gives Termes as the roman face, else Latin Modern
		`mainFont`, `sansFont` and `monoFont` replace them when `Named` resolves them
		a null typesetter gives the defaults
	`FontFor(font)`
		if `font.face` resolves through `Named`, return it
		else if the family is Mono, return `mono`
		else if the family is Sans, return `sans`, or `roman` when there is no sans font
		else return `roman`
	`Named(name)`
		if `named` has the name, return it
		if `aliases` has the name and the alias asset is registered in this host, use it   // exact bundled names only: TeX Gyre Termes → `tex-gyre-termes`; Carlito → `carlito`; Latin Modern Roman → `latin-modern`; Latin Modern Mono → `latin-modern-mono`
		else if a font asset of that name is registered, use it
		else if `FontLibrary.AssetFor(name)` finds a family imported through the font picker, an earlier compile or a bundled family, use it
		else if `Imported(name)` returns an asset, use it
		else if `standIns` has the name and its asset is registered, use it   // Times New Roman, Times, Nimbus Roman → `tex-gyre-termes`; Calibri → `carlito`
		else log the name once (Info, channel Tex; `missing` keeps it from repeating), keep the default face and return null
	`Imported(name)`
		if `TestRunner.active`, return null   // tests keep the stand-ins
		if the Path= files of this typesetter (`files`) name the font and the regular file exists
			build an `InstalledFamily` from those files   // family name from `FontCatalog.ReadFace`, only faces that exist
		else
			`FontLibrary.EnsureCatalog()`
			`FontCatalog.Find(name)` gives the installed family, or null
		`FontLibrary.ImportNow(family, "Latex")` bakes it on the calling thread and returns the asset name   // the compile blocks for the bake, about 6 to 8 s per family, once; the family then appears in the font picker
	a font imported later through the picker is used on the next compile, because `named` is per compile and `missing` is not
	the PDF export finds a bold or italic face that is not shipped or installed beside the regular face's source file, when that source is a full path
	the running-head `Slot`, `Advance`, `TextRun`, the Code style, Code blocks and `TexAtlasMetrics.XHeight` all go through `Faces` and `FontFor`

### Pages, footnotes and `PageRefs` in `TexLowering`
	`Layout` writes `<Page Style FootnoteSkip>` and its `<PageStyle>` and `<Slot>` children
	a `TexPageBreak` becomes `PageBreak` on the next block, through `Spacing.pageBreak` in `Place`
	a paragraph's `pageStyle`, `markLeft` and `markRight` are written on its first block
	`Footnotes` writes a `<Footnote Id>` group right after the paragraph, or the table, that holds the mark
		the mark's math run gets `Note`
	`PageRefs(root)` returns a `TexPageRefs`
		`refs` are the `\pageref` runs
		`labels` map each label key to its block index and offset
		both are found through the XElement annotations `PageRefNote` and `LabelSite`
	`TexEditorControl.ResolvePages` runs after layout
		for each label, read its page through `PageAt`
		if a `\pageref` number changed, reload the preview, at most twice
		when a number is filled into its run, `pageRefs.Fit (run, number)` re-sizes the spacers of the number's fixed box   // `TexPageRefs.fits`

### Contents lines in `TexLowering`
	`Skips` writes the block's `LeftIndent`, `RightIndent`, `RightIndentLast` and `Indent` from the paragraph's `TexParStyle`
	`TakeLeading` for a `\leftskip` paragraph
		its leading glue, kerns, penalties and empty boxes become the block's `Indent`   // `\hskip -\leftskip` hangs the entry; no negative-width spacer runs
	`FixedBox` for an `\hbox to`
		share the slack at its fil glue as spacers, or put it after the content when there is no fil glue
	a fill run gets `Leader`, `LeaderWidth` and `LeaderInset` from `LeaderCell`
		`LeaderCell` = the leader text, its inset and its cell width   // math kerns in mu, `\hbox` text in the text font
	`MathParser` reads `\mkern` and `\mskip` in mu (plus and minus read and dropped) and `\hbox{…}` as `\text`

### `TexAtlasMetrics`
	em is the font size
	ex is the 'x' glyph height from the face's `atlasMetaData`
	when the asset is missing, ex is 0.430554 of the em, the cmr10 ratio
	`Width (text, font)` is `TexLowering.TextWidth`   // the label column of the bibliography is measured with it

## PDF export
`File → Export PDF` in a `.tex` split view writes `<name>.pdf` beside the source, overwriting an earlier export and asking nothing. The PDF is vector: text is real selectable text in Type 3 fonts drawn from the font's own glyph outlines, pictures are the original files, and rules are vector rectangles. Colours come from the engine's `print` palette, so a dark theme does not give a dark PDF; "print" therefore also appears in the theme list.

### `PdfExport.Export(tree, target, title)`
	parse the preview's current `<Document>` tree into a fresh `RichTextDocument`
	build a detached `DocumentControl` that is in no window, at zoom 4, with the `print` palette
	measure and arrange it
	set `UIEngine.recorder`
	run the normal `UIEngine.Collect` walk
		the four draw points record instead of appending quads
			`TextRunControl.WriteGlyph` records font, face, char, size, pen, baseline, colour and clip
			`TextRunControl.WriteRect` and `TextRunControl.WriteImage` record rules and pictures
			`Control.Emit` records panels as rects
		a spacer run (`\quad`, `\hspace`) records a space glyph so copied text keeps the gap
	for each recorded item
		put it on the page sheet that contains it (`DocumentControl.PageRects`)
		convert it to page-local points, px × 72/96 / 4, y flipped
	for each page
		write one content stream, with a clip per clip change
		move glyphs in the bottom margin to the end of the stream so the page number copies last
	for each (font asset, style) used
		find the font file beside the atlas, then in the system font folders
		write a `PdfType3Font` holding only the used glyphs, at most 256 codes, with a ToUnicode map (ligatures U+FB00–FB06 map back to their letters)
	pictures: a JPEG is passed through as DCTDecode, anything else is decoded to RGB with a soft mask when translucent
	`PdfDocument.Save` writes the file

### `TexEditorControl.ExportPdf`
	if an edit is pending, recompile first
	if `\pageref` numbers are still being filled, mark the export waiting and return
	`OnTick` writes the PDF once they settle

Not exported: clickable `\href` links, bookmarks, floating pictures, gradients and effects (flat colour). Math copies as a jumble of symbols, as pdfLaTeX output does.

List bullets are exported. While recording, an icon control records its name and artwork box, and the writer draws the six default bullet shapes (disc, circle, square, outlined square, triangle, outlined triangle) as filled paths from a copy of their SVG geometry, with the outlines even-odd. Any other icon is skipped. If a bullet SVG changes, the writer's copy has to follow.

## Not supported yet
`\suppressfloats`, `placeins` and `\FloatBarrier`, `afterpage`, `tabularx`, `tabular*`, `longtable`, `\multirow`, natbib, the biblatex commands `\textcite`, `\footcite` and `\fullcite`, biber-only features, stretch inside a `\url` (`\Urlmuskip`), `\autoref`, `\nameref`, subfigure, clickable references, `\arrayrulecolor`, `\parindent` on left-aligned tables, `\numberwithin`, `subequations`, `\intertext`, `\shoveleft`, `\shoveright`, `\hdotsfor`, mathtools, `\setbox`, `\wd`, `\lastskip`, `\appendix`, clickable contents entries, tocloft's `\cftsetindents`, `\cftpagenumbersoff` and `\newlistof`, and tocloft's non-`[titles]` title formatting. There is no kerning. Vertical glue has no stretch or shrink (`\addvspace` merges against the last skip, `Spacing.last`; mid-page `\lineskip` is not modelled), a heading is not kept with the next paragraph, an over-wide tabular fits the text width instead of sticking into the margin, infinite glue vanishes except horizontal fil glue, which lowering turns into a fill run, `\tolerance`, `\pretolerance`, `\emergencystretch` and `\exhyphenpenalty` are registers that nothing reads, a `\rule` inside running text draws nothing, `\raisebox` ignores its raise, titlesec shapes other than hang and block are set as hang, `\titlespacing`'s right argument is ignored, `\roman`, `\alph`, `\Roman` and `\Alph` are undefined inside `\the<counter>`, `\labelsep` and `\leftmargin` are not registers, setspace's `spacing`, `onehalfspace` and `doublespace` environments are missing, `\sectionbreak` runs whether or not titlesec is loaded, bibliography entries have no `\itemsep` between them, a quotation's `\listparindent` is not modelled, a `\cline` under part of a merged cell rules the whole cell, booktabs gaps do not interrupt vertical rules, a spacer never breaks a line, and display formulas inside table cells get no display skips. Hyphenation is US English only, so accented words are never hyphenated; an explicit hyphen in a word ("well-known") is not a break opportunity; `\lefthyphenmin`, `\righthyphenmin`, `\uchyph` and `\hyphenpenalty` are not read. The preview breaks a paragraph Knuth-Plass only where a break set exists at tolerance 200; the rest stay greedy. Small caps are set upright and Sans is set in Roman unless `\setsansfont` names a font. fontspec's file keys are honoured and its other options are ignored, a font name that resolves to nothing keeps the default face and is only logged, and `indentfirst` is not supported. The em dash and the en dash do not draw at 13 px in a note. A `\def` inside a tabular cell leaks into later cells. `\hbox to` content wider than its box overflows (TeX would shrink `\hss`; spacers never go negative). A `\label` in vertical mode with no paragraph before it (right after a mark or a table) is dropped, so a contents entry pointing there keeps "??" as its page. A starred titlesec heading loses the `\hspace` of the class's before-code (7 pt left of the thesis). Two-sided page styles (`[LE,RO]` reads only the odd entry, `\cleardoublepage` is `\clearpage`), `\pagenumbering`, `\footnotemark`, `\footnotetext`, footnotes split across pages, `\footnotesep` and `\renewcommand` of `\sectionmark` are not supported; a mid-document `\pagestyle` sets the whole document's style, and a `\label` inside display math or a table cell has no page site.

## Related
- [[Tex Expander]] — the tokens this reads
- [[Math Layout]] — draws the formulas; a math run's font picks its math face
- [[Rich Text Document]] — the `<Document>` this lowers to
