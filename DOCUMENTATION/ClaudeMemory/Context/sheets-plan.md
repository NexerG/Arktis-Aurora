# Sheets plan — spreadsheet editing in Thorium

**Status:** S1 landed 2026-10-02; S2a, S2b1, S2b2 2026-10-03. S2b3, S2c, S3, S4a and S4b 2026-10-04; S5, S5b and S6 2026-10-05. S2d is open (not designed);
nothing else is planned.
Decision record: [sheets](../Decisions/sheets.md). Part of the Thorium productivity suite (WIP list, Phase A):
notes, sheets and a todo board under Blender-style workspace tabs, plus a simplified mode with familiar layouts.

## Settled by the user (2026-10-02)
- A sheet is its own file, `*.sheet.axml`, opened in a tab like a note.
- "Layers" means both: pages (bottom tabs, one file = a workbook) and layers stacked over one page's grid.
- Tabs talk to editors through `IFileEditor`.
- Order after S1: formulas first.

## Settled by the user (2026-10-03)
- Reach: a cell can be read from other pages of its file, from other sheet files, by notes, and by note math.
- Ctrl+V pastes references, a plain paste when none apply; Ctrl+Shift+V pastes values (user, 2026-10-30; the
  reverse until then). OS clipboard gets values.
- A sheet copy pasted into a note: one cell → an inline live value; a range → a note table of live values.
- Excel precedence (`-2^2` = 4).
- Sliced S2a (one file) then S2b (the rest).
- S2b answers: notes match the sheet's keys (Ctrl+V live, Ctrl+Shift+V plain); `.md` writes
  `![[Budget.sheet.axml#Expenses!B3]]`; a rename inside Thorium rewrites references, a delete or outside rename is
  `#REF!`; freshness is Thorium's own saves (no watcher, no mtime checks).
- S2b forks: a range in a note is one read-only live grid block (implemented in S2b3 as a display object span, not a block kind), same in `.axml` and `.md` (replaces "a note table of
  live values" above); S2b split into S2b1/S2b2/S2b3; `[file]Page!` syntax, page required; undo on the document.

## Settled by the user (2026-10-04)
- S2c is substitution only: `\sheet{…}` holds the note-link reference string; evaluating the formula is a possible S2d.
- S3: formats live per page, shared by all layers (not per layer on the cell).
- S3: formats are applied from Ctrl+B and a grid context menu; a sheet toolbar comes later, with the workspace tabs.
- S3: numbers copy as numbers (unformatted), in sheets and in a note's plain-text copy.
- S3: General and Scientific are one preset; currency is euros.
- S3: note links show the number format, not bold or fill.

## Settled by the user (2026-10-04)
- S4: the page strip lives inside the editor (the editor is a StackPanel holding a scroller and the strip), not pinned inside the grid.
- S4: a page rename rewrites every reference vault-wide (sheets loaded or on disk, note links, note math); undo re-runs the rewrite in reverse.
- S4: page and layer add, delete and show/hide are undo records; structure changes recalc with `SheetCalc.RecalcAll`.
- S4: the edited layer is per editor and not saved; it defaults to the topmost, and a newly added layer becomes edited.
- S4: delete page or layer with one left is a no-op, not a greyed row.
- Formula popup closed from outside cancels (typed source dropped); committing instead is an open option.

## Settled by the user (2026-10-05)
- S5: a CSV opens in place in a sheet tab rather than being imported; Ctrl+S saves it raw with its own delimiter and BOM; no page strip; formats, widths and extra layers are not saved.
- S5: Export as CSV writes values as shown, numbers unformatted, UTF-8 with BOM, comma, CRLF, overwriting `<Sheet> - <Page>.csv` beside the sheet.
- S5: a CSV row in the vault browser offers "Create a sheet from this" (CSV kept) and "Convert to sheet" (CSV to the recycle bin, tab closed).
- S5b (references into a CSV) was deferred, then landed 2026-10-05 (below).

## Settled by the user (2026-10-05, S5b)
- A CSV can be referenced but references nothing outside itself ("CSVs should not be able to reference anything from outside, but they can be referenced"): a file-part reference inside a CSV is `#REF!`, Paste link into a CSV from another file pastes plain values, other files' renames never rewrite a CSV.
- The file part keeps `.csv` (`[data.csv]data!A1`, as Excel writes it), so `data.csv` and a sheet `data` resolve separately; the page is the CSV's file base name.
- Renaming a CSV to another CSV rewrites the page part as well as the file part (a CSV cannot store its page name).
- "Convert to sheet" retargets every reference to the new sheet before the CSV is recycled; "Create a sheet from this" leaves references on the CSV.

## Slices
| # | Slice | State |
|---|---|---|
| S1 | grid, selection, in-cell editing, undo, TSV clipboard, `.sheet.axml`, any-editor tabs | landed |
| S2a | formulas in one file — arithmetic, `SUM`, cell/range/page references, Kahn recalc, errors as values | landed |
| S2b1 | vault-wide graph (`SheetBook`), `[Budget]Expenses!B3` (unopened files loaded from disk), rename rewrites | landed |
| S2b2 | Paste link in sheets (Ctrl+Shift+V) | landed |
| S2b3 | notes show cells: inline live value (`StyleSpan` object like math), range = read-only live grid block, `<Run Sheet>` / `![[…]]`, `Text.PasteLink`, notes refresh on `SheetBook.changed`, rename reaches notes | landed |
| S2c | note math reads sheet cells — `\sheet{file#Page!A1}` in a formula is replaced by the cell's value before parsing (substitution only, no evaluation); Ctrl+Shift+V in the formula popup; rename rewrites formulas | landed 2026-10-04 |
| S2d | evaluate note formulas (result after a trailing `=`) — not designed | — |
| S3 | formatting (bold, fill, number formats), column/row resize by dragging header edges | landed 2026-10-04 |
| S4a | page tabs along the bottom — switch, add, rename, delete (undoable); a page rename rewrites references vault-wide | landed 2026-10-04 |
| S4b | layers panel — show/hide, pick the edited layer, add, delete | landed 2026-10-04 |
| S5 | CSV: a `.csv` opens in place as one page (saved back raw, own delimiter and BOM), Create a sheet from this / Convert to sheet in the vault menu, Export as CSV per page | landed 2026-10-05 |
| S5b | references into a CSV — `[data.csv]data!A1` in sheets, note links and `\sheet{…}`; Paste link from a CSV tab; a CSV references nothing outside itself; CSV rename and convert rewrite references | landed 2026-10-05 |
| S6 | fixed-size sheets (type chosen at creation, 25 x 25 pages, "+" grow strips and grow popup, paste past the edge grows) and insert rows above / columns left with vault-wide reference shifting | landed 2026-10-05 |
| S7 | formula functions — comparisons `= <> < > <= >=` (result the number 1/0; user settled, no boolean kind), MIN, MAX, AVERAGE, ROUND, ROUNDUP, ROUNDDOWN, IF beside SUM; a plain sheet, no structured Tables (user settled) | landed 2026-10-06 |

## Owed for S2b3 — resolved 2026-10-04
- Done: the paste-link key (Ctrl+V since 2026-10-30) is `Sheet.PasteLink`, which falls back to `Text.PasteLink` outside a sheet — `Text.PasteLink`
  took over that fallback.
- Rewriting `.md`/`.axml` notes on a sheet rename: re-saving a note does not reproduce its bytes, so this needs a
  token-level rewrite — done (`SheetLinks.Renamed`).

## Carried gaps from S1
See [sheets](../Decisions/sheets.md) § Known gaps.
