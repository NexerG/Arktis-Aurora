# LaTeX editor (native) — agreed route, L1–L6, L7a–L7g, L8, L9, L10a, L10b, L10c, L10d, L10e, L10f, L10g, L11a, L11b, L11c, L11c2, L11d, L11e, L11f, L11g, L11h and L12a landed

**Agreed:** 2026-10-05 (user: native route, "as many packages as possible", L3 node list on the recommendation).
**Built:** L1, which merged the file/source editor with the TeX expander (user). L2 (CFF + Latin Modern) landed 2026-10-05. L3 (typesetter + lowering) landed 2026-10-05. L4 (split view) landed 2026-10-06. L5 (floats, `tabular`, `\label`/`\ref`, BibTeX, note-table column spans) landed 2026-10-06. L6 (amsmath natively, shared with notes math M5) landed 2026-10-06. L7 was split into slices L7a–L7g (user-approved 2026-10-06) and L7a, L7b, L7c, L7d, L7e, L7f and L7g landed 2026-10-06. L8 (in-house PDF export) landed 2026-10-07. L9 (a real-world resume `.tex`) landed 2026-10-18. L10a (read the template), L10b (layout values), L10c (fonts), L10d (Lithuanian) and L10e (table of contents) landed 2026-10-28; L10f (biblatex) landed 2026-10-29, which completes L10.
**Decisions:** [../Decisions/latex-editor.md](../Decisions/latex-editor.md).
**Checklist form:** the LaTeX item in `DOCUMENTATION/Work in Progress List.md`.

Each phase lands and is verified before the next starts; each one re-runs CLAUDE.md §2 (analyze, plan, ask).

## Forks taken
| # | Question | Taken | Rejected |
|---|---|---|---|
| Route | how TeX is run | native: Aurora's typesetting, C# primitives for document commands and package environments | full port of tex.web (TRIP-verified, XeTeX-compatible, real `latex.ltx` and `.sty`) — costs T3–T5, ~25k lines of WEB, before anything shows |
| L-F1 | dependencies, packages | no NuGet, no external tools; `\usepackage` accepted, unknown packages logged and ignored; "as many as possible" starting amsmath, amssymb, graphicx, xcolor, hyperref (links), geometry, booktabs, enumitem, fancyhdr (L9 adds titlesec, ulem, mathptmx / times / tgtermes / newtxtext) | — |
| L-F2 | macro level | level 2: real tokenizer + expander (catcodes, `\def` with parameter text, `\expandafter`, `\csname`, conditionals, registers, groups) | level 1: command table + template `\newcommand` — breaks on `\def\foo#1.#2`, `\expandafter`, `\newif`, `\makeatletter` |
| L3 | interpreter output | TeX-like node list (chars, glue, kerns, penalties, boxes), lowered to `<Document>` | emitting straight to `<Document>` — closes `\hbox`/`\setbox`/`\wd`, `\vskip`, `\lastskip`/`\unskip`/`\lastbox` |
| L-F3 | editor shape | Overleaf split view: source + read-only paged preview | — |
| L-F4 | preview | the `<Document>` tree; Aurora's layout gains glue gaps, Knuth-Plass mode, hyphenation, footnotes, running heads, float placement (L7) | — |
| L-F5 | fonts | add CFF (Type 2 charstrings) support; the atlas baker already takes cubic `Bezier` contours | — |
| L-F6 | footnotes | endnotes in v1, real footnotes in L7 (landed as L7e, 2026-10-06; the endnote section is gone) | — |
| L1 | source editor | `.tex` reuses `DocumentEditorControl`, one Code block per line | a new editor control |
| L2-A | where the `.otf` sources live | gitignored `_Build/FontSources/`, probed after the system font folders | copying into the per-user Windows font folder; an absolute path in the import XML; committing the `.otf` files (SUPERSEDED by L8-F2: the LM `.otf` files are now committed beside their atlases) |
| L2-B | which hosts import Latin Modern | Thorium only (`ThoriumFonts.imports.axml`) | engine imports — would bake and commit three copies (Thorium, Carbon, AuroraEditor) |
| L2-C | charset | `Latex`, 348 chars incl. ligatures, accents, quotes, dashes | the existing `Latin` charset |
| L2-D | faces | Roman 10 ×4, Mono 10, LM Math with Roman italic/bold | optical sizes 5–17 and Sans left out |
| L2-E | ink box | exact cubic extrema | control-point hull (looser) |
| L3-F1 | where LaTeX commands that are plain macros live | a TeX prelude, a C# string in `TexFormat`, run before the source | every command as a C# primitive (more code, loses LaTeX's own structure); a data file `Data/Tex/aurora.ltx` (path plumbing into three hosts, not XML) |
| L3-F2 | font of a formula | a math run's `FontName` picks the math face, absent = `math` | keep Cambria (formulas would not match the body) |
| L3-F3 | package scope | core + geometry (paper/margin keys), xcolor (`\textcolor`, `\color`, named colours with tints, HTML/rgb/RGB/gray), hyperref `\href`/`\url` as underlined/mono text only | core only |
| L3-F4 | footnote marker | a math run `{}^{n}` (`<Run>` has no superscript); notes become endnotes under a "Notes" Heading1 (superseded by L7e: real footnotes, the section is gone; the marker is still a math run) | `[n]` text |
| L3-F5 | default paper | letterpaper as LaTeX; a4paper by class option or geometry | A4 (Aurora's default) |
| L3-A | Greek in LM Math italic | `MathLayout.Present` uses Regular when the requested face's ink box is empty and Regular's is not; Greek upright | LM Math's italic Greek at U+1D6FC+ (outside the 16-bit char atlas — large change); reverting L3-F2 |
| L3-B | the line before a display formula is justified across the width | resolved 2026-10-06 by L7a-F3: the general fix in `TextRunControl.Justify` (notes get it too) | splitting the paragraph at display math in lowering |
| L5-F1 | when `\ref` / `\cite` resolve | after the run, in one pass: they leave nodes carrying their style, the cited `.bib` entries are formatted into thebibliography source and typeset at the `\bibliography` mark, then `Resolve()` fills every node's text (user rejected both first options, then took this) | two passes like LaTeX's `.aux` (doubles the cost of every 0.5 s recompile); placeholder nodes resolved in lowering (the bibliography sees only earlier citations); carrying the aux across recompiles, latexmk-style (refs lag a compile and depend on history) |
| L5-F2 | table borders from LaTeX | a `Borders` flag on `<Table>`: full Aurora grid if the spec has `\|` or the body has `\hline`/`\cline`/booktabs rules, else none | always the grid (wrong for borderless tables); exact per-edge rules (`\|` per column, rule per row, booktabs weights) — a large `TableControl` change, L7-sized — **superseded by L7g-F1**: LaTeX tables always `Borders="false"` with exact rules |
| L5-F3 | tabular column widths | natural width measured in lowering (Latin Modern glyph advances, `MathLayout` width, picture size) + `\tabcolsep` 6pt each side, min 24 px; `p{w}` uses w; a merged cell wider than its columns widens the last | splitting the text width evenly |
| L5-F4 | `\multicolumn` in note tables | real column spans (user, scope iii): rows stored short, cells found by grid position, structural edits span-aware, "Merge cell right" / "Split cell" | merge by selection range; display-only spans with structural edits refused. Row spans (`\multirow`) not done |
| L5-F5 | equation numbers | stayed with L6 (resolved 2026-10-06): `\label` inside math was stripped and `\eqref` was "(??)" until L6 numbered equations | — |
| L5-F6 | BibTeX styles | plain, unsrt, abbrv, alpha; the 14 standard entry types following plain.bst's blocks | plain only |
| L6-F1 | environments that depend on the line width | layout does not know the width: `align`/`flalign` spaced like `aligned` with `\minalignsep` 1 em between pairs; `multline` first row flush left, last flush right within its widest row + 1 em `\multlinegap`, middle rows centred | passing the column width into `MathLayout.Layout` and the box cache key — exact amsmath spacing, but re-lays out on every width change and grows the cache per width — **revisited in L7g-F4/F5**: top-level `align`/`flalign`/`multline` are width-aware |
| L6-F2 | a tag that collides with its formula | the formula shifts left to keep 1 em clear of the tag, overflowing past that (`MathLeft`) | LaTeX's tag on its own line — needs the width (goes with F1's rejected option) |
| L6-F3 | how math environments reach `MathParser` from the typesetter | the names are expander primitives; `Append` writes them back as `\begin{…}`/`\end{…}` source | a flag on the expander so `\begin`/`\end` pass through as tokens while math is collected — touches the expander's group bookkeeping |
| L6-F4 | scope | environments plus the small ams commands: `\binom`/`\dbinom`/`\tbinom`, `\operatorname(*)`, `\DeclareMathOperator` (prelude macro), `\overset`/`\underset`, `\boxed` | environments only |
| L7a-F1 | where Knuth-Plass runs | LaTeX documents only, through the `DocumentLayout` attribute `OptimalBreaks` that lowering writes (user) | exposing it as a note setting now |
| L7a-F2 | justify shrink | justify shrinks spaces down to a third, as TeX (user) | stretch-only Knuth-Plass, which never sets a line tighter than natural |
| L7a-F3 | the line before a display formula (L3-B) | the general fix in `TextRunControl.Justify`: a line before a display-math line is treated as a last line, so notes get it too (user) | a LaTeX-only fix in lowering |
| L7a-F4 | no feasible break set at tolerance 200 | fall back to greedy for that block (user, asked again after implementation showed the cost) | a TeX-style second pass at tolerance 10000; a single pass at 10000 (accepts a very loose line TeX's first pass would reject, slower) |
| L7b-F1 | where soft hyphens are inserted | a pre-pass in `TexLowering.Lower` inserting U+00AD character nodes into paragraph lists, top level only so `\mbox`/`\hbox` are never hyphenated (agreed in the L7 split) | hyphenating inside Aurora's measurer (would hyphenate notes and needs a language per note); in the typesetter |
| L7b-F2 | hyphenation pattern source | TeX Live's hyph-en-us from CTAN converted to XML (user picked A), so lines break where LaTeX breaks them | B: our own patterns from a C# PATGEN port over the public-domain Moby Hyphenation List (~1.5 MB download, a larger slice, breaks some words differently from LaTeX); C: a rule-based hyphenator with no download (wrong breaks TeX would not make) |
| L7c-F1 | block model for indent and space above | `Indent`/`SpaceBefore` are a full note feature: carried through Enter (the tail keeps both), Backspace/merge (`TakeKind`), undo snapshots and paste, like `alignment`; no UI sets them (user) | honoured in layout and XML only, lost on edits |
| L7c-F2 | display skips | lowering splits a paragraph at each display formula into text / display / text blocks; `\abovedisplayskip` above the display, `\belowdisplayskip` above the unindented continuation; short display skips not used (user) | padding the display line's box inside one block (caret and selection on that line would grow) |
| L7c-F3 | exact horizontal gaps | a spacer run for `\quad`/`\hspace`/`\kern` (user) | keeping the NBSP approximation |
| L7c-F3b | what a spacer run is | a fixed-width NBSP whose span sets its advance, `<Run Text=" " Space="px"/>` (user, after implementation started); `.md`/`.txt` just get an NBSP, caret and selection work unchanged; cost: a spacer is never a line break (TeX can break at `\quad`/`\hspace`) | a U+FFFC object character as first proposed (breakable, but needs object handling in caret, typing and the writers) |
| L7-F1 | where page breaks, page styles, marks, footnotes and floats live | a full note feature carried through edits (user, 2026-10-06, against the recommendation). Edit rules (user, asked mid-implementation): Enter keeps a page break, `\thispagestyle` and marks with the head only; footnote/float membership goes to the tail too; `Snapshot`/`Restore`/`TakeKind` copy all; an orphan footnote sits at the foot of the last page (or one more if it does not fit), first duplicate anchor wins, no cascade delete; plain-text paste joins the caret block's footnote/float but never copies page break, page style or marks; no UI creates any | layout + XML only; the literal L7c rule "tail keeps everything" (two page breaks); deleting a footnote with its anchor in one undo step (range delete and undo would learn groups); a paste that carries nothing |
| L7-F2 | running-head slot content | one style per slot, plain text with `{page}`/`{leftmark}`/`{rightmark}` | runs or mixed styles in a slot |
| L7-F3 | `\pageref` | resolved by `TexEditorControl` after layout through `PageAt`; reloads the preview when a number changed, at most twice; a one-frame "??" per recompile that has `\pageref` | a page-field run filled inside `Paginate` (width changes mid-layout, re-measure inside layout); seeding from the previous compile's page map (lag and history, the reason L5-F1 rejected latexmk-style) |
| L7-F4 | the incremental "settled" `Paginate` shortcut | off whenever the document has a footnote/float block (`inserts`): a full pass | teaching the shortcut to compare page reservations and an empty float queue |
| L7-F5 | where footnotes sit | at the foot of the page's text area, as the slice said; since L7f above any bottom floats (L7f-F4) | directly under the text (what LaTeX does under `\raggedbottom`; footmisc `[bottom]` is the LaTeX fix) |
| L7-F6 | float parameters (built in L7f) | LaTeX-default constants | reading `\topfraction` etc. from the source into `<Page>` |
| L7-F7 | slice order | d, e, f built back to back under one approval; the user then stopped after e ("finish e, don't go to f") | land, verify and re-plan each slice separately |
| L8-F1 | PDF text | Type 3 fonts drawn from the glyph outlines, ToUnicode map (user) | embedding whole font programs (FontFile2/3, Identity-H: hinted and most compatible, but whole fonts per PDF, LM Math 733 KB, `cambria.ttc` split to one face); plain paths (no text selection). Cost: unhinted, small text can look heavier, strict tools handle Type 3 worse |
| L8-F2 | Latin Modern outlines on a user's machine | ship the LM `.otf` files (~1.25 MB, GUST licence) beside their atlases; reverses L2's "never committed" and supersedes L2-A (user) | writing outlines into a new committed file per font at import (needs a re-import, grows the data about as much) |
| L8-F3 | how the PDF learns what is drawn | a recorder checked at the four draw points, one null check per glyph (user) | redirecting `UIQuads` into a capture pool and decoding rows back (inverse mapping, writes a pool outside its step); a separate walker copying `Emit`'s pen logic |
| L8-F4 | layout scale | a detached copy at 4× zoom scaled by ¼ into points: rules keep LaTeX weights, the 12 px detail cull rarely bites (user) | 1× (PDF identical to the 100 % preview, rules ≥0.75 pt) |
| L8-F5 | colours | a new engine `print` palette for the export copy; "print" shows in the theme list and palette picker (accepted by the user) | the on-screen palette (a dark theme gives a dark PDF) |
| L8-F6 | scope | `.tex` only in the UI; engine code works for any `DocumentControl` (user) | every paged note (gradients and effects would export flat) |
| L8-F7 | target | `<name>.pdf` beside the `.tex`, overwritten, no dialog, as the sheet CSV `ExportPage` (user) | a save dialog (none exists) |
| L7f-F1 | a `t` float met mid-page | the current page's top if it fits, and `Paginate` rewinds: reserves the top, re-lays the page's text from the first flow block that reaches that page, rolls back footnotes reserved from that page on, drops and re-registers picture floats of the rewound blocks; floats already decided are skipped on the re-run, a "here" float is re-placed inline; at most 2 rewinds per page (user, recommended) | never the current page's top, always defer to the next page: simpler, but differs from LaTeX whenever a `[t]` float appears mid-page |
| L7f-F2 | a float inside a paragraph | the paragraph stays whole; its `<Float>` is emitted after it, as footnotes are, so the reference point is the paragraph's end, not its line (user, recommended). Cost: a float written mid-paragraph in a long paragraph is placed relative to where the paragraph ends; the golden fixture needed blank lines around its floats | (a) keep the pre-L7f split of the paragraph at the float; (c) an anchor run in the text for an exact reference line, which needs a new `Run` attribute |
| L7f-F3 | float separations | `<Page FloatSep TextFloatSep InTextSep>` in mm, written by lowering like `FootnoteSkip`, because the 12pt class differs (14 pt) (user, recommended) | fixed 10pt constants in code |
| L7f-F4 | footnotes and bottom floats | LaTeX2e's default order: footnotes above bottom floats (`\@makecol` puts `\footins` in the column, `\@combinefloats` appends `\@botlist` under it) (user, recommended) | footmisc `[bottom]` order |
| L7g-F1 | where table rules live (R-F1) | on the cells and columns: `<Cell RuleAbove RuleBelow TrimAbove TrimBelow>`, `<Column RuleLeft RuleRight>`, so row/column edits carry them (user, recommended). Cost: a `\cline` under part of a merged cell rules the whole cell | a `<Rule From To Kind Trim>` per row boundary — LaTeX's shape, but column insert/delete must shift indices |
| L7g-F2 | edits and rules (R-F2) | a new row/column has none, delete drops them, merge keeps the left cell's, split copies to both halves (trims: Left to first, Right to last) (user, recommended) | dropping all rules on any structural edit |
| L7g-F3 | cell padding (R-F3) | `<Table Padding="h v">`: `\tabcolsep` across and 0 down for LaTeX tables, 6 px for notes (user, recommended) | 6 px everywhere plus booktabs gaps (wrong table heights) |
| L7g-F4 | width-aware math (M-F1) | laid out again at the width, per-control cache for the current width only; the shared `mathBoxes` cache stays width-free (user, recommended) | (b) record each cell's glyph range and shift — `MathBox.Place` flattens the tree; (c) width in the shared key — cache grows per width |
| L7g-F5 | tags in width-aware math (M-F2) | amsmath's two stages, picked by the user mid-implementation: spread over the full width, and only if a tagged row ends within 1 em of its tag, spread again over width − (widest tag + 1 em) | (a) always reserve tag + 1 em (off-centre vs LaTeX; the approved option, put a tagged align 8 px left in `Tex.Amsmath`); ignoring tags |
| L7g-F6 | one-letter table columns (asked mid-implementation) | `TexLowering.MinColumn` 24 to 28 px (12 + 2·`\tabcolsep`), because `UIEngine.Collect` skips children under `detailCullSize` 12 px (user). Cost: about 5 px wider than LaTeX | changing the engine cull to both dimensions (UI-wide perf heuristic); a 6 px across inset (text about 2 px off LaTeX in every column) |

Cost accepted with the native route: support is bounded by what is reimplemented; a package not rewritten (e.g. `mathtools` patching amsmath internals) does not work.

## Phases

### L1 — file, source editor, expander — DONE 2026-10-05
- `TexSourceFormat`, `.tex` as `FileKind.Tex` in `FileTypes`, byte-identical save (`sourceNewline`, `sourceBom`), pageless, `Mode.Latex` colouring
- `ArctisAurora.Core.Tex`: `TexExpander`, `TexReader`, `TexToken`, `TexCatcode`, `TexError` — expansion only, nothing in the UI calls it
- Test- and golden-verified; **NOT GUI-verified**

### L2 — CFF + Latin Modern — DONE 2026-10-05
- `CffOutlines` (CFF reader + Type 2 charstring interpreter) feeds `AuroraFont.GetCffGlyph`; forks L2-A to L2-E above
- Latin Modern (Roman 10 ×4, Mono 10, LM Math) ships as a baked Thorium atlas; the `.otf` files sit in gitignored `_Build/FontSources/` — "never committed or needed on users' machines" (user, 2026-10-05) is SUPERSEDED by L8-F2: the `.otf` files are now also committed beside the atlases in Thorium's `Data/Fonts`
- Also fixed (user approved): the bake's empty-glyph check broke any glyph exactly 1 em tall or wide (LM Math ∑, √); "empty" now means no contours
- Test- and golden-verified (`Fonts.CffCharstring`, `LatinModern.Baked`, `LatinModern.Draws`); **NOT GUI-verified**
- Details: [[latex-editor]]

### L3 — interpreter → node list → `<Document>` — DONE 2026-10-05
- `TexTypesetter` (expander tokens → node list), `TexFormat.Prelude` (LaTeX commands as TeX macros), `TexLowering.Compile(source, out errors)` → `<Document>`; nothing in the UI calls it until L4
- The expander's `em`/`ex` now come from the font (`quad`/`xHeight`, `TexAtlasMetrics`); `\stepcounter` and `\newcounter{x}[within]` resets added
- Forks L3-F1 to L3-F5 above; Fork A (L3-A) and Fork B (L3-B) came after the first golden
- Test- and golden-verified (`Tex` suite 23/23, `Tex.Preview`); **NOT GUI-verified**
- Details: [[latex-editor]]

### L4 — split view — DONE 2026-10-06
- `UI.TexEditorControl`: source beside the preview, debounced 0.5 s recompile, error list with click-to-error, double-click in the preview jumps to the source line (commits 57316c9, 770f9bb)
- Test- and golden-verified (`Tex.Lowering.Lines`, `Tex.SplitView.Recompile`, `Tex.SplitView.Errors`, `Tex.SplitView.ClickToSource`, golden `Tex.SplitView.Errors.Split`); **NOT GUI-verified**
- Details: [[latex-editor]]

### L5 — floats, `tabular`, `\label`/`\ref`, `.bib` — DONE 2026-10-06
- Figure/table floats with captions, `\includegraphics`, `tabular` (booktabs rules, `\multicolumn`), `\label`/`\ref` in one pass, BibTeX (`TexBibliography`; plain/unsrt/abbrv/alpha, `\cite`, `\nocite`); forks L5-F1 to L5-F6 above
- Note tables gained real column spans (`<Cell ColumnSpan>`), a `Borders` flag, span-aware row/column edits and "Merge cell right" / "Split cell" — [[document-tables]]
- Test-verified (`--test=Tex` 30/30, `TextInput.TableColumnSpan`) and golden-verified (`Tex.Floats`); **NOT GUI-verified**
- Details: [[latex-editor]]

### L6 — amsmath natively — DONE 2026-10-06
- `MathParser` reads environments (matrix family, `cases`, `array`, aligned family, `gathered`, `multline`, top-level align/gather/flalign/alignat/multline/equation and starred forms), `\tag`, `\substack`, `\binom`, `\overset`/`\underset`, `\boxed`, `\operatorname(*)`; `MathLayout.Grid` lays arrays out in two passes; shared with notes math M5 (notes do not auto-number)
- `TexTypesetter` numbers `equation` and the align family by injecting `\tag{n}` per row (`Number`, `Rows`), `\label` points at the number, `\eqref` resolves; `\DeclareMathOperator` is a prelude macro; forks L6-F1 to L6-F4 above; L5-F5 resolved
- Math rules draw at least 1 px wide (user, after the first golden)
- Test- and golden-verified (`--test=Math` 17/17, `--test=Tex` 32/32, goldens `Tex.Amsmath`, `TextInput.MathArrays`); **NOT GUI-verified**
- Details: [[latex-editor]], [[math-in-notes]]

### L7 — Aurora layout adaptations — sliced L7a–L7g (user-approved 2026-10-06)
- **L7a — DONE 2026-10-06:** Knuth-Plass line breaking (`TextMeasurer.BreakOptimal`, `DocumentLayout.optimalBreaks`), justify shrink, the line before a display formula no longer stretched (L3-B); forks L7a-F1 to L7a-F4 above. Test- and golden-verified (`--test=Tex` 34/34, `--test=Math` 17/17, goldens `Tex.Preview` and `Tex.Amsmath` re-approved); **NOT GUI-verified**. Before L7b most paragraphs failed tolerance 200 and stayed greedy
- **L7b — DONE 2026-10-06:** Aurora's U+00AD soft hyphen (break opportunity, "-" at line end via `TextLine.hyphen`, dropped on copy); `TexLowering.Hyphenate` inserts them from `TexHyphenator` (Liang patterns from `en-us.hyphenation.axml`), plus `\-` and `\hyphenation`; forks L7b-F1, L7b-F2 above. Test- and golden-verified (`--test=Tex` 37/37, new golden `Tex.Hyphenation`); **NOT GUI-verified**
- **L7c — DONE 2026-10-06:** per-block first-line indent and space above in Aurora notes (`<Block Indent SpaceBefore>`, `<Table SpaceBefore>`, spacer run `<Run Space>`); the typesetter emits TeX's vertical glue and `\parindent`, lowering sums it into `SpaceBefore`, splits paragraphs at display math, sets `BlockSpacing` 0 and writes exact spacer runs; forks L7c-F1 to L7c-F3b above. Test- and golden-verified (`--test=Tex` 39/39, five goldens re-approved); **NOT GUI-verified**, not perf-measured; dead `TexLowering.Spaces` left in place
- **L7d — DONE 2026-10-06:** real page breaks (`\newpage`, `\clearpage`, `\cleardoublepage`, `\pagebreak`), running heads and feet from page styles (`\pagestyle`, `\thispagestyle`, empty/plain/headings/myheadings, fancyhdr), marks (`\markboth`, `\markright`, heading marks) and `\pageref` resolved from the laid-out preview; all note-model features (`<Block PageBreak PageStyle MarkLeft MarkRight>`, `<Table PageBreak>`, `<Page Style>` with `<PageStyle>`/`<Slot>` children); forks L7-F1 to L7-F4 above. Test-verified (`--test=Tex` 44, 5 new for L7d+L7e; `--test=TextInput` 90) and golden-verified (`Tex.Pages.Break`); **NOT GUI-verified**; NOT perf-measured
- **L7e — DONE 2026-10-06:** real footnotes: a `<Footnote Id>` group of blocks whose anchor is a run with `Note="id"`; `Paginate` reserves room at the foot of the page the anchor's line lands on and stacks the footnotes there under a short rule; LaTeX's endnote "Notes" section is gone; forks L7-F1, L7-F4, L7-F5 above. Test-verified (as L7d) and golden-verified (`Tex.FootnotePage.Foot`; `Tex.Preview` re-approved); **NOT GUI-verified**; NOT perf-measured
- **L7f — DONE 2026-10-06:** float placement by LaTeX's `[htbp!H]` rules with LaTeX-default parameters: here, top or foot of the current page, held back to a later page, or a page of floats; `\clearpage` (`PageBreak.Clear`) and the document end flush every waiting float as pages of floats; any `<Float Kind Placement>` group in a note is laid out this way. New `Tex.TexFloat`, `TexTypesetter.BeginFloat`/`EndFloat`/`Placement`, `TexLowering.Float`, `PageLayout.floatSep`/`textFloatSep`/`inTextSep`, `UI.PageFloats`, `PageSpace` top reservation / float pages / `Rollback`, `DocumentControl.MeasureFloats`/`ReserveOrphanNotes`; forks L7f-F1 to L7f-F4 above, L7-F6 built. Builds clean. Test-verified (`--test=Tex` 46/46 in Thorium incl. new `Tex.Lowering.Float` and `Tex.FloatPlacement`; `--test=TextInput` 90/90; `--test=Math` 17/17 in Carbon; `--test=LatinModern` 12/12) and golden-verified (`Tex.Floats.Page` re-approved; `Tex.FloatPage.Top` and `Tex.FloatPage.FloatPage` new, looked at); **NOT GUI-verified**; NOT perf-measured. Open: header-on-page-1 handling and the nested-float fallback await user confirmation; Carbon `Tex.Lowering` failure not investigated; `\suppressfloats`, `placeins`/`\FloatBarrier`, `afterpage` unsupported; centred float tables landed in L7g
- **L7g — DONE 2026-10-06:** exact table rules at LaTeX's weights (`|`, `||`, `\hline`, `\hline\hline`, `\cline{a-b}`, booktabs `\toprule`/`\midrule`/`\bottomrule`/`\cmidrule` with widths and trims) as a note feature (`<Column RuleLeft RuleRight>`, `<Cell RuleAbove RuleBelow TrimAbove TrimBelow>`, `<Table Padding>`); a tabular takes the alignment of its paragraph (`<Table Align>`); top-level `align`/`flalign`/`multline` spread over the line width with amsmath's rules, in notes math too (revisits L6-F1). New `Tex.TexRuleKind`, `Tex.TexTableRule`, `TexTable.rules`/`vrules`/`align` (`ruled` removed), `TexTypesetter.unaligned`/`Rule`/`Columns`/`OptionalWidth`, `UI.TableControl` (`alignment`, `cellRules`, `leftRules`/`rightRules`, `cellPadding`, `ApplyInsets`, `TableRule`, `RuleTrim`, `CellRules`), `UI.MathParser` `MathFill`/`MathArray.fill`, `UI.MathLayout.Layout(…, lineWidth)`/`MathBox.widthAware`, `UI.TextRunControl.FittedBox`; forks L7g-F1 to L7g-F6 above, L5-F2 superseded, L6-F1 revisited. Builds clean. Test-verified (`--test=Tex` 46/46 in Thorium; `--test=TextInput` 91/91 incl. new `TextInput.TableRules`; `--test=Math` 18/18 incl. new `Math.FillWidth`) and golden-verified (new `Tex.Booktabs`, `Tex.Floats.Page` re-approved, both looked at; `Tex.Amsmath` unchanged); full `--test` 219 passed, 4 failed (Boot, `Sheet.FixedSize`, `Perf.TypeLargeNote` pre-existing; `Perf.Controls.Scrollable.Relayout` unexplained, not reproduced in two reruns); **NOT GUI-verified**; NOT perf-measured. Open: `|` in a `\multicolumn` spec ignored; booktabs gaps do not interrupt vertical rules; no `\parindent` on left tables; no `\arrayrulecolor`; no UI for table alignment or rules; a tag never moves to its own line
- Kerning (GPOS pairs; needs a re-bake of committed atlases) is in no slice; open whether it becomes L7h or "left out"

### L8 — in-house PDF export — DONE 2026-10-07
- File → "Export PDF" (`Tex.ExportPdf`) writes `<name>.pdf` beside the `.tex`: vector, real selectable text in Type 3 fonts, pictures embedded, no NuGet; new namespace `ArctisAurora.Core.Pdf` (`PdfDocument`, `PdfType3Font`), `UI.PdfExport`, `UI.DrawRecorder`; forks L8-F1 to L8-F7 above
- A recorder (`UIEngine.recorder`) at the four draw points; a detached `DocumentControl` at zoom 4 with the new `print` palette; LM `.otf` sources now committed beside their atlases (supersedes L2-A)
- Test-verified (`--test=LatinModern` 15/15 incl. `Tex.Pdf.Structure`, `Tex.Pdf.Text`; `--test=Tex` 46/46, `--test=TextInput` 91/91, `--test=Math` 18/18; full `--test` 221 passed, 4 failed as in L7g); a richer export looked at in Chrome's PDF viewer and read by xpdf `pdftotext`; **NOT GUI-verified**; not perf-measured
- Left out: clickable `\href`, bookmarks, PDF/A, encryption, hinting, gradients and effects, wrapped pictures, opening the file after export, a keybind, export for ordinary notes
- Details: [[latex-editor]]

### L9 — a real-world resume `.tex` — DONE 2026-10-18
- Typesets and exports to PDF close to pdfLaTeX: geometry with a custom paper, mathptmx, enumitem `\newlist`/`\setlist`, titlesec `\titleformat`/`\titlespacing`, `\hfill` dates, `\setlength`/`\dimexpr`, `\hyphenpenalty=10000`, `\pdfpageheight=\dimexpr\pagetotal+…`; forks L9-F1 to L9-F5 in `latex-editor`
- Preamble text dropped with `Missing \begin{document}`; `\hfill` is a note-level fill run (`<Run Fill>`); per-item `<Block ListIndent>`; `<Block StylingType="Rule" Weight>`; `<Page FitHeight>`; TeX Gyre Termes for Times packages; bullet icons in the PDF; shrink applied to every Knuth-Plass line set over its room
- Console `Focus` command added for headless export (`dev-console`)
- Test- and golden-verified (full Thorium `--test` 363 passed, 1 failed — pre-existing Boot; golden `Tex.Resume.Page`); the user's resume exported headless and rendered; **NOT GUI-verified**
- Left out: honouring `\tolerance`/`\pretolerance`/`\emergencystretch`/`\exhyphenpenalty`, inline `\rule` boxes, `\raisebox` raise, titlesec shapes beyond hang/block, enumitem keys beyond seven, clickable `\href`
- Details: [[latex-editor]]

### L10 — a real-world thesis `.tex` — L10a, L10b, L10c, L10d, L10e DONE 2026-10-28; L10f and L10g DONE 2026-10-29
- Goal: the user's VU MIF bachelor thesis (`VUMIFTemplateClass.cls` + `LTPlius.sty` + `titulinis.tex`) typeset and exported close to its LuaLaTeX PDF (ground truth, 35 A4 pages; the export is 27 after L10a, 36 after L10b, 32 after L10c, still 32 after L10d, 29 after L10e (stand-in title page, no Photos/.bib), 35 after L10f, the same as the ground truth)
- Stages:

| Stage | Content | Status |
|---|---|---|
| L10a | read the template: `\input`/`\include`, local `.cls`/`.sty` read through the expander, `\@startsection`, title page, A4 at 12pt, class margins, placeholders for later-stage commands | DONE 2026-10-28 |
| L10b | layout values: setspace, list lengths (`\itemsep`/`\topsep` stray "0pt 0pt"), "1." numbering, uppercase section titles via titlesec's last-command-takes-the-title, `\sectionbreak`, `\fontsize` baselineskip (not done: F1 a keeps one document-wide line height) | DONE 2026-10-28 |
| L10c | fonts: fontspec (`\setmainfont`, `\setsansfont`, `\setmonofont`, `\fontspec`, `\newfontface`, `\newfontfamily`) Times New Roman → bundled Termes, Calibri → bundled Carlito (L10c-F1 A); a tabular row's first cell starts as in TeX | DONE 2026-10-28 |
| L10d | Lithuanian: polyglossia captions ("1 pav.", "lentelė"), polyglossia/babel language commands, hyphenation patterns hyph-lt from hyph-utf8 (MIT; an outside download, user-approved; `TexHyphenator.For`), `\captionsetup`, indentfirst, ragged paragraphs do not hyphenate, `~` as TeX's tie and U+00A0 as interword glue | DONE 2026-10-28 |
| L10e | table of contents and lists of figures / tables: ported latex.ltx / tocloft / tocbibind macros over `\hbox to`, `\leaders`, `\leftskip`/`\rightskip`/`\parfillskip`; block indents; leaders on fill runs; page numbers by `\pageref` (two passes = the preview reload) | DONE 2026-10-28 |
| L10f | biblatex: alphabetic labels like [tmp23], Lithuanian strings, C# drivers, a hanging bibliography list with a measured label column, URL breaks | DONE 2026-10-29 |
| L10g | visual match to the reference: per-block line height, fixed-depth TeX baseline model, fractional font sizes (preview = export pagination), widow/club control, foot skip to baseline, `\partopsep`, `\@centercr`, display skips, caption singlelinecheck, MATH display-operator sizes | DONE 2026-10-29 |

(L10 continues as the L11 group below.)

- Forks taken in L10a (L10-F1 to L10-F3 and the reader stack): `latex-editor`
- Forks taken in L10b (L10b-F1, L10b-F2): `latex-editor`
- Forks taken in L10c (L10c-F1 to L10c-F5): `latex-editor`
- Forks taken in L10d (L10d-F1 to L10d-F7): `latex-editor`
- Forks taken in L10e (L10e-F1 to L10e-F8): `latex-editor`
- Forks taken in L10f (L10f-F1 to L10f-F6): `latex-editor`
- Forks taken in L10g (L10g-F1 to L10g-F6, D1, D2): `latex-editor`
- Details: [[latex-editor]]

### L11 — thesis match from measured causes — L11a, L11b, L11c DONE 2026-10-29; L11d, L11e, L11f, L11g, L11h DONE 2026-10-30
- Goal: close the remaining drift of the thesis export against its LuaLaTeX PDF; each stage is re-planned after measuring. Forks L11-F1 (a), L11-F2 (a) user-picked 2026-10-29

| Stage | Content | Status |
|---|---|---|
| L11a | real installed fonts: Times New Roman body (now bundled TeX Gyre Termes), real Calibri headings via fontspec `Path=` (now Carlito) | DONE 2026-10-29 (fontspec Path= files, else installed family baked on first use, before the stand-ins; since 2026-10-30 installed family first, Path= only when not installed, see font-variants; line breaks still differed; the cause was not interword glue, see L11f) |
| L11b | page and list rules: line fits while its baseline is above the text bottom (`overhang`), list top = \topsep + outer \parskip, \leftmargini..iv list indents, `KeepWithNext` for headings and the empty line before a paragraph-starting display | DONE 2026-10-29 |
| L11c | display vertical model: interline glue max(\baselineskip − prevdepth − height, \lineskip), real line depth (revisits L10g D2); bmatrix rows 14.4 pt apart instead of 17.9 | DONE 2026-10-29 (interline glue from real ink height/depth via `TextMeasurer.Interline`, `DocumentLayout.LineSkip`; array strut = \arraystretch × \baselineskip per formula, `Run.Strut`; below-display gap open, see L11c2) |
| L11c2 | classic TeX math rules when no unicode-math: Appendix G rule 18 with cmsy10/cmex10 TFM dimensions (`MathRules.Tfm`) | DONE 2026-10-29 (~1.2 pt below displays still open) |
| L11f | line breaking as TeX: float font sizes in the document model (exact TeX points), space factor (`SfCode`, marker spaces U+E001–E004), TeX's breaking passes (`DocumentLayout` Tolerance/Pretolerance/EmergencyStretch), f-ligatures only for TFM/Latin Modern fonts, `\@`, `\frenchspacing` | DONE 2026-10-30 (forks L11f-F1..F3 in `latex-editor`; open: Calibri GSUB ligatures) |
| L11g | glyph widths in thousandths: TeX documents truncate every glyph width to 1/1000 em (`DocumentLayout.AdvanceGrid`, `TextMeasurer.Grid`) | DONE 2026-10-30 (retracts the L11f "not a layout cause" claim; thesis pages 13, 14 match line for line) |
| L11h | kerning from the font (GPOS `kern`, old `kern` table) as a global feature, baked into the `.agd`; one pen step `TextMeasurer.AdvanceAt` for every walk | DONE 2026-10-30 (forks F1..F3, D1..D3, Q1..Q3 in `latex-editor`; open: thesis p19 paragraph 7 vs 8 lines) |
| L11d | floats and tables: caption skips ~4–5 pt short, table rows shorter, float on a different page (p29), over-wide tabular into the margin (p6) | DONE 2026-10-30 (display skips ×`\displayskipstretch`, caption strut + `\abovecaptionskip`, here-float spacing, `[H]` body spacing, tabular indent, empty column width, `tsb` fix; figures exact, display gaps 0.0 pt; open: table/caption gaps 0.6–1.8 pt, p9 break drift; see `latex-editor` L11d) |
| L11e | page builder: vertical glue shrink on overfull pages (reference items 22.1–22.7 pt, natural 22.9), `\Urlmuskip` in bibliography URLs | DONE 2026-10-30 (forks L11e-F1..F4 in `latex-editor`; thesis 35 pages = reference, pages 11–14 break as the reference, p11 list items 22.7–22.8 pt apart vs 22.7; bibliography line counts match except [Nex25b] 4 vs 3; open: pages 17–18 shrink where the reference is natural) |

- Measured causes behind L11b: page 2 contents line at baseline 788.0 vs text bottom 790.96 pushed to page 3 (+18 pt); every list 3.0 pt short at the top (\parskip 3 pt); item text 7.4 pt left of the reference; heading "2.5.5 WebGL API" alone at the bottom of page 13
- Forks taken in L11 (L11-F1, L11-F2): `latex-editor`
- Details: [[latex-editor]]

### L12a — compiler errors: preamble, packages, @, text-mode math — DONE 2026-10-30
- A stage after L11, not part of the L11 thesis-match drift work. Thesis compile errors 33 → 6 (the 6 are real errors in the source)
- Content: amsthm, the algorithm float, `\appendix`, babel `\addto`, `\descriptionlabel`, registers `\hbadness \hfuzz \vfuzz \widowpenalty` and `page`, text-mode math recovery ("Missing $ inserted"; a math command alone as math since F1's reversal), `@` catcode saved/restored around every package read, □ in the Math charset
- Forks L12a-F1..F5, D1, D2: `latex-editor`. Supersedes the L10a "`@` restored by counting nested files" gap

## Left out overall
- TikZ/PGF, beamer, biber-only features (source maps, crossref and xdata inheritance; biblatex itself is done, L10f), catcode-level arbitrary TeX, `\output`, multi-column

Related: [[latex-editor]], [[math-in-notes]]
