# Decision — a sheet is its own file kind, drawn visible-cells-only, with pages and layers in the format from day one

**Date:** 2026-10-02
**Scope:** `ArctisAurora.Core.UI` — `SheetDocument`, `SheetPage`, `SheetLayer`, `SheetCell`, `SheetXml`,
`SheetControl`, `SheetEditorControl`, `SheetCellEdit`, `SheetActions`, `IFileEditor`, `TabViewControl.FileEditorOf`,
`SheetFormula`, `SheetValue`, `SheetCalc`, `SheetCellId`, `SheetBook`, `SheetLinks`, `SheetBox`, `SheetBoxCell`,
`StyleSpan.sheetRef`, `Run.sheet`, `TextInputActions.PasteLink`, `SheetLinks.ExpandMath`/`RenameMath`/`HasMathLinks`,
`TextRunControl.MathBoxFor`, `FormulaPopup.PasteLink`;
`Thorium.Editor.CustomControls.VaultBrowserControl` (`BuildSheetTab`, `NewSheet`, `BaseName`, `Extension`, `FindSheet`),
`Thorium.Editor.VaultsWindow.Enter`;
S4/S5 (2026-10-04/05): `SheetPageStripControl`, `SheetLayersControl`, `SheetCsv`, `SheetPageEdit`, `SheetPageRenameEdit`, `SheetLayerEdit`, `SheetLayerShowEdit`, `SheetBook.PageRenamed`/`Restructured`, `SheetFormula.RenamePage`, `SheetLinks.PageRenamed`, `ContextMenus.Open` (`onClosed`), `FormulaPopup.Show`; Thorium `VaultBrowserControl` (`SheetFromCsv`, `Sheets.FromCsv`, `Sheets.ConvertCsv`), `VaultCsv.menu.axml`

**Status:** PARTIAL — S1, S2a, S2b1, S2b2, S2b3, S2c, S3, S4a, S4b, S5 and S5b of [sheets-plan](../Context/sheets-plan.md) landed; S2d is open.

## What changed
- A sheet is a `*.sheet.axml` in the vault: `<Sheet Name><Page Name><Column At Width/><Row At Height/><Layer Name Visible><Cell At="B3">text</Cell></Layer></Page></Sheet>`.
  Addresses are A1 (`Column At="B"`, `Row At="4"`, 1-based rows). Cells written row-major. No namespace, no XSD.
- Model: `SheetDocument` → `pages` → `SheetPage` (sparse `columnWidths`/`rowHeights`, `layers`) → `SheetLayer`
  (`Dictionary<long, SheetCell>`, key `row << 32 | column`, `visible`). Absent = empty. Unknown elements kept in
  `extra` at each level and written back.
- `SheetPage.Shown(r, c)` = topmost **visible** layer holding the cell. Edits land in the topmost layer.
- Band geometry: `ColumnLeft`/`RowTop` sum default + sparse deltas; `ColumnAt`/`RowAt` binary-search them.
- View: `SheetEditorControl : ScrollableControl` (Both axes, `Surface` ground) holding one `SheetControl` canvas.
  In an unfixed sheet (the type before S6; a fixed one measures to its stored size, see § Fixed sheets and insert) the canvas measures to used extent (+100 rows, +26 columns, and past the active cell) and in `ArrangeCore`
  reads the scroller's inner rect, then places pooled parts for the visible window only: grid lines and cell
  `LabelControl`s in a `Parts` container, a selection wash (`Accent`, alpha 0.18), four `Accent` outline bars on
  the active cell, the in-cell `TextBoxControl`, then headers (`Chrome` bands, `MutedInk` names) pinned to the
  viewport. Unused pooled parts arrange to `Hidden` (zero-size at the sheet's own corner, see S3); pools never shrink.
- Numbers (invariant `double.TryParse`) right-align when they fit; text is cut at the cell edge.
- Editing: typing over a cell (`Sheet.Write` on `AnySymbol`) opens the field empty; F2 / double-click keeps the
  text with the caret at the end. Enter / Tab commit and step (Shift reverses), Esc cancels (`Text.Cancel` →
  `TextBoxControl.Cancel`), blur commits without taking focus back. One `SheetCellEdit` per commit, paste, clear or cut.
- Clipboard: `IClipboardTarget` on the editor — copy writes the selection as TSV (`\t`, `\r\n`), paste writes TSV
  from the active cell and selects the block. A blank pasted field clears its cell.
- `IFileEditor` (`path`, `isDirty`, `Save`, `Repath`, `ViewState`, `RestoreView`) is what tab close, open-document
  lookup, session capture/restore, vault rename and `NoteActions.SaveEditedIn` (shutdown save, window settle) talk
  to. `TabViewControl.EditorOf` stays for note-only paths (naming prompt). `SheetEditorControl.Save` commits an
  open cell edit first, so quit, close and Ctrl+S keep text typed without Enter.
- Sheet `ViewState` reuses `SessionTab`: `caretBlock`/`caretOffset` = active row/column, `anchorBlock`/`anchorOffset`
  = anchor, `scrollX`, `topDelta` = scroll Y.
- Thorium: `BuildTab` routes `*.sheet.axml` to `BuildSheetTab`; `Sheets.New`/`Sheets.NewHere` in the File, Vault and
  VaultFolder menus; rename and duplicate keep `.sheet.axml` whole (`BaseName`, `Extension`).
- **S2a formulas (2026-10-03).** A cell starting `=` is a formula; the file keeps the raw text. `SheetFormula.Parse`
  (recursive descent) → nodes `Constant`, `Reference` (page name?, cell or range), `Negate`, `Binary`, `Call`
  (`SUM` at S2a; S7 adds comparisons and more functions, see § S7). `SheetValue` = Empty/Number/Text/Error; `FromRaw` keeps a typed number's text; `Display()` → `G15`.
- `SheetCalc`: `formulas`, `values`, `precedents`, `dependents` keyed by `SheetCellId(SheetPage, key)`.
  `Changed(page, cells)` relinks and runs Kahn over `Downstream`; leftovers get `#CYCLE!`. `PageNamed` is
  case-insensitive. `SheetControl.ArrangeGrid` shows `Display()` and right-aligns `Number`. `Copy` writes values.
- **S2b1 vault-wide graph (2026-10-03).** `SheetBook` (static) holds one `SheetDocument` per full path (`Get` loads
  and registers), `owners` (page → document), and the one `calc`. `SheetEditorControl.LoadPath` goes through
  `Get`, so tabs of one file share a document. `Register` (path-less documents, tests), `Unregister`, `Created`,
  `Deleted`, `Renamed(old, new, vaultSheets)`, `Clear` (vault switch, `VaultsWindow.Enter`).
- `SheetCalc` knows every registered document; `Add` queues one and `Settle` links queued documents, then runs
  Kahn — a file loaded while linking (a reference to an unloaded sheet) is queued, not linked re-entrantly.
- Reference syntax `[Budget]Expenses!B3`, `'[My budget]Sheet 1'!B3` (page required). `PageNamed(home, file, page)`
  resolves the file through `SheetBook.Resolve` → `findSheet`, which Thorium sets to `VaultBrowserControl.FindSheet`
  (first `*.sheet.axml` in the vault whose name or path ending matches, like `FindNote`). Missing → `#REF!`.
- `SheetFormula.RenamePrefix(raw, rename)` (was `RenameFile` until S5b) rewrites `[file]page` prefixes found while parsing, the callback taking `(file, page)` (spans collected by the
  parser), via `Prefix(file, page)`, which quotes a name holding anything past letters, digits, `_`, `.` (and `/`
  in a file). `SheetBook.Renamed` rewrites loaded documents (saving those with no open tab) and every other
  vault sheet on disk that holds a reference; a file without one is not rewritten.
- `SheetDocument.undo` — one history per file. `SheetCellEdit` holds the document and calls `SheetBook.Changed`,
  which raises `SheetBook.changed(document)`; every `SheetEditorControl` redraws, and the edited file's editors set
  `unsaved`. `changed(null)` = values may have moved (create/delete/rename), redraw only.
- **S2b2 Paste link (2026-10-03).** `Copy` keeps `copiedText` + `copiedFrom` (document, path, page, range); `Cut`
  drops them. `PasteLink` writes `=` + prefix + address per cell (none on the same page, `Page!` in the same file,
  `[File]Page!` across files; file = path's base name), one undo step, block selected; a clipboard that no longer
  matches pastes plainly. `Sheet.PasteLink` (Ctrl+V since 2026-10-30) falls back to `Text.PasteLink` outside a sheet (S2b3).
- **S2b3 sheet links in notes (2026-10-04).** `SheetLinks` (static, `ArctisAurora.Core.UI`): `Parse(reference, out file, out page,
  out top, out left, out bottom, out right)`, `Reference(path, page, top, left, bottom, right)`, `IsLink(target)`,
  `Layout(reference, in TextMeasurer.Run, LineMetrics, zoom) → SheetBox`, `Plain(reference)`, `Renamed(oldPath, newPath, notes)`,
  private `RewriteFile`. Same file: `SheetBox` (width, height, range, cells, rules) and `SheetBoxCell(text, rect, pen, baseline, error)` record struct.
- Reference `file#Page!A1` or `file#Page!A1:B2`; file part = sheet file name with extension (`Budget.sheet.axml`); split at the
  first `#` and the last `!`; range ends ordered.
- `StyleSpan.sheetRef` / `IsSheet`; `IsObject` now includes it; `AsText` clears it. `Run.sheet` → XML attribute
  `<Run Sheet="Budget.sheet.axml#Data!B1"/>`; `AppendRun` / `Runs()` carry it.
- `TextRunControl`: `_runSheet` list; the `BuildRuns` sheet branch measures a `SheetBox` through the object-box
  `TextMeasurer.Run` (math flag, no depth); `WriteSheet` draws it in `Emit`.
- `DocumentControl` region `sheet links`: `PasteLink(text)`, `RefreshSheetLinks()`, `RenameSheetLinks(Func<string,string?>)`;
  `PlainText` (copy) writes `SheetLinks.Plain` for sheet spans. `DocumentEditorControl`: subscribes to `SheetBook.changed`
  (ctor), unsubscribes in the new `OnDestroy`; `BookChanged` → `RefreshSheetLinks`; `PasteLink()` (step "Paste link");
  `RenameSheetLinks(rename)` passthrough. `SheetEditorControl.CopiedReference(clipboardText)` (internal static).
- **S2c note math reads sheet cells (2026-10-04).** `\sheet{Budget.sheet.axml#Data!B1}` in a formula's TeX typesets the cell's live value; substitution only, nothing is evaluated.
  `SheetLinks` region `math`: `HasMathLinks(source)`, `ExpandMath(source)`, private `MathValue(reference)`, `RenameMath(source, rename)`; regexes `xmlMath` (`<Run … Math="…">`) and `mathLink` (`\sheet{…}`).
  `RewriteFile` also rewrites `\sheet{…}` tokens (whole text in `.md`; inside `Math="…"` attributes in `.axml`, decoded then re-encoded). New private `Escaped(value)` = XAttribute serialization,
  now also used for the `Sheet` attribute (replaces `SecurityElement.Escape` from S2b3; keeps `&#xA;` for line breaks in a display formula's source).
- `TextRunControl.MathBoxFor`: a formula holding a link is parsed and cached under `ExpandMath(source)`; `MathParser` unchanged and pure. `DocumentControl.RefreshSheetLinks` also invalidates blocks
  with a formula holding a link; `RenameSheetLinks` also rewrites formula sources through `RenameMath`.
- `FormulaPopup`: static `open` (the popup being edited), `PasteLink()` inserts `\sheet{ref}` into its source box via `SheetEditorControl.CopiedReference`, through `TextBoxControl.Paste` (so the live preview updates).
  `TextInputActions.PasteLink` (`Text.PasteLink`) order is now `FormulaPopup.PasteLink()` → note editor `PasteLink()` → plain `Paste()`; keys unchanged (Ctrl+Shift+V → `Sheet.PasteLink` → `Text.PasteLink` outside a sheet).
- Test `Sheet.MathLinks` (`SheetTests.cs`, `Sheet.tests.axml`), golden `Sheet.MathLinks.Math.png`.
- `TextInputActions.PasteLink` = action `Text.PasteLink`; `SheetActions.PasteLink` falls back to it instead of `Paste()`.
  No keybind change: Ctrl+Shift+V stays on `Sheet.PasteLink`.
- `MarkdownFormat`: new `embed` regex; `ParseInline` reads `![[X.sheet.axml#…]]` as a Sheet run; `Compose` writes it back;
  `Flatten` style key `sheet|ref`.
- `SheetBook.Names`, `Renamed` and `BaseName` went private → internal (S5b changed their signatures and removed `Stem`). Thorium
  `VaultBrowserControl.RenameNote` calls `SheetLinks.Renamed(path, target, VaultNotes())` after `SheetBook.Renamed`; new
  private `VaultNotes()` (vault `.md` + `.axml`, `*.sheet.axml` excluded).
- Tests (`SheetTests`, `Sheet.tests.axml`): `Sheet.NoteFormats`, `Sheet.NoteLinks` (goldens `Sheet.NoteLinks.Links.png`,
  `Sheet.NoteLinks.Edited.png`), `Sheet.NotePasteLinks`, `Sheet.NoteRenameRewrites`. Fixture `FolderResolver` strips `.sheet.axml` like
  `FindSheet`; new fixtures `BudgetSheet`, `NoteBlocks`, `NoteContent`.

## Why these choices

**Its own file, not a block inside a note.**
A note table (`TableControl`) is a `GridListControl` whose cells are stacks of blocks — a control per cell, built
eagerly. That is right for a 5×10 table in prose and wrong for 10,000 rows. A sheet is a different document kind
in its own tab, which is also the shape the suite's workspace tabs (Notes / Sheets / Board) want.

**`.sheet.axml`, not `.sheet`.**
Matches the repo's `[name].[type].axml` convention. Cost: `Path.GetExtension` sees `.axml`, so anything that names
or re-extends a file goes through `VaultBrowserControl.BaseName`/`Extension`. `Accepts` needed nothing — `.axml` is
already accepted. `WriteName` works unchanged because the root carries `Name`.

**Only visible cells have controls, and they are reused.**
`Sheet.ScrollKeepsControls` measured 10,000 rows: the control count is under 1000 and identical before and after
scrolling to row 5000. Rejected: drawing glyphs straight from a custom `Emit` (no child controls at all) — less
overhead, but it duplicates `TextRunControl`'s glyph path; pooled labels reuse it as is.

**A rebind costs one extra layout pass, accepted.**
Setting a pooled label's `text` inside `ArrangeCore` calls `InvalidateLayout`, which marks every ancestor measure-dirty
and registers the root, so the frame after a scroll re-measures the chain once and settles (no text changes the
second time). Cheap — siblings are cached by offer — and avoided only by rebinding before layout, which the thumb
drag and `ScrollIntoView` paths do not offer a hook for.

**Keys are added as second binds, not by teaching `Text.*` about sheets.**
`GestureMatcher.Update` fires every bind on a trigger with the same modifier count; each `Sheet.*` action no-ops
unless a `SheetEditorControl` is at or above `UIEngine.activeControl`, exactly as `Text.*` no-ops without its editor.
Order does not matter: Enter fires `Text.NewBlock` (commits the field via `Box()`) and `Sheet.Enter` (commits if
still editing, then steps). Shift+Enter is read inside `Sheet.Enter` — a `Shift+Enter` bind would shadow
`Text.NewBlock` in notes. Shift+Tab needs its own bind (`Sheet.TabBack`) because `Text.Outdent` already shadows the
plain Tab binds when Shift is held.

**`IFileEditor.isDirty` is implemented explicitly.**
`Entity` already has a public `isDirty` (the ECS dirty flag). A public `isDirty` on an editor would hide it.
`SheetEditorControl` keeps its own flag as `unsaved`.

**Pages and layers are in the format before any UI shows them.**
Chosen (1c): a file is pages (bottom tabs, like a workbook) and each page is layers stacked over one grid. S1 shows
page 0 and edits its top layer, but reads and writes every page and layer, so S4 adds UI and never migrates files.

**The editor paints `Surface`.**
`DocumentEditorControl` is transparent because its pages draw their own paper. A sheet has no page, and on
the bare window ground it read as ink on black in the first golden.

### S2a — formulas (2026-10-03)

**Values live in `SheetCalc`, not on `SheetCell`.** (user, 2026-10-02; per document at S2a, vault-wide since S2b1)
What a cell shows comes from `Shown`, which crosses layers, so a value on one layer's cell is the wrong owner.
`SheetCell` stays exactly what the file holds. Only formula cells are cached; a typed cell reads `FromRaw` each
time, which keeps `1.50` as typed.

**The node is `(SheetPage, key)`, so it already names its file.** (user, 2026-10-03: S2a then S2b)
S2b moves the graph from one per document to one per vault; the node type and the Kahn pass stay. Cross-page
references were asked for together with cross-file ones and notes reading sheets; only the first is S2a.

**A reference reads the shown cell.** (user, 2026-10-02) A formula sees what the grid shows. A formula on a covered
or hidden layer is not evaluated. Per-layer reference syntax was rejected for now.

**Kahn order, not recursive evaluation.** A 10,000-cell chain would recurse 10,000 deep. Kahn is a loop, and its
leftover set is exactly the cells in or behind a cycle — no separate cycle walk. `Sheet.FormulaRecalc` runs the
10,000 chain.

**Excel's precedence.** (user, 2026-10-03) Unary minus above `^` (`-2^2` = 4), `^` left-associative (`2^3^2` = 64).
Spreadsheet users expect Excel, not maths.

**Errors are values that propagate, left operand first.** `#ERROR!` for an unparseable formula keeps the raw text
(Google Sheets' shape) rather than refusing the entry, which needs a prompt the grid does not have. `#NUM!` for a
non-finite result and `#REF!` past Excel's grid (16,384 × 1,048,576) were added during the build — the agreed list
did not cover overflow or a grid bound.

**Copy writes values; Ctrl+V pastes links first; Ctrl+Shift+V pastes values.** (user, 2026-10-30; until then Ctrl+V
pasted values and Ctrl+Shift+V linked, S2b) The OS clipboard wants values, and paste does not adjust relative
references, so a pasted formula would point at the old cells. Ctrl+V is `Sheet.PasteLink`: a reference or live link
whenever the clipboard is the last sheet copy, a plain paste otherwise — so copying inside one sheet now pastes `=A1`,
unlike Excel. Cut drops the copy record, so a cut always pastes values and never links to the cells it emptied.
Ctrl+Shift+V is `Text.Paste`, which is "paste plain" in most other apps. The menus' "Paste" follows Ctrl+V; the
sheet menu's "Paste values" is `Text.Paste`. `SheetActions.PasteLink` falls back to `Text.PasteLink` whenever the
sheet refuses (a cell being edited), else Ctrl+V would be dead in a cell editor; an empty clipboard returns false so
a copied picture reaches `Text.Paste`'s image branch.

### S2b1/S2b2 — across files, Paste link (2026-10-03)

**One loaded copy per file, shared by every tab.** Live cross-file reads need one truth per file; two copies of a
sheet open in two tabs would diverge. The cost — two tabs edit one history — is why undo moved onto the document
(user, 2026-10-03: one history per sheet file). `unsaved` stays per editor; every editor of the edited file sets it.

**Excel's `[file]Page!` syntax, page required; the file part resolves like a vault link.** (user, 2026-10-03) First
match wins on duplicate names, as `[[links]]` do; a folder path ending (`[Finance/Budget]`) disambiguates.

**Linking queues a loaded file instead of recursing.** Resolving `[Budget]` while linking `Summary` loads Budget,
whose own links may resolve back to Summary mid-link. `Settle` drains a queue and re-runs Kahn until nothing is
queued, so evaluation never sees a half-linked document.

**A rename rewrites references; a delete, or an outside rename, is `#REF!`.** (user, 2026-10-03) Rewriting by
re-parsing and saving is safe for sheets — `Sheet.XmlRoundTrip` proves the writer reproduces the file. Only files
that held a reference are written. A loaded sheet with no open tab is saved at once, since nothing else would.

**Thorium's own saves are the freshness boundary.** (user, 2026-10-03) A sheet is loaded once per vault session;
an edit made outside Thorium is seen on the next launch or vault switch. No file watcher.

**Paste link falls back to `Text.PasteLink` outside a sheet (a plain paste when no link applies), and the Ctrl+Shift+V bind precedes Ctrl+V's.** A Ctrl+Shift+V bind
shadows Ctrl+V only when it comes first in `InputMap.inputs.axml` (the matcher consumes triggers in file order, as
`Math.InsertDisplay` before `Math.Insert`). One action per chord: a second `Text.PasteLink` bind on the same chord
ran after `Sheet.PasteLink`'s own fallback and pasted every link twice.

**A test may not share an input action's name.** `Sheet.PasteLink` as a test name crashed boot: the keybind
loader bound the `Test`-category method by name. The test is `Sheet.PasteLinks`.

### S2b3 — sheet links in notes (2026-10-04)

**A sheet link is an object character, not a note-level block kind.** (user, 2026-10-04)
U+FFFC plus a `StyleSpan`, like pictures and formulas. Rejected: a block kind beside `TableControl`, which would touch
`Blocks()`, pagination, both writers (`.md` skips tables today) and structural undo. The object path gives undo,
selection, delete, copy and snapshots for free. `IsObject` covers it, so [[math-in-notes]]' rules (typing beside it, never merged) apply.

**A range is display-style; one cell is inline.**
A range's advance is the full column, so it sits on its own line (the display-math trick, F5′); drawn left-aligned,
cut at the column edge. One cell is inline, its box the value's text advance in the run's font and style, ascent from the line.

**A range draws values and 1 px grid rules only, no headers.** (user, 2026-10-04)
Rules are `PaletteRole.Line`. Rejected: column/row headers. Geometry is the sheet page's column widths and row heights ×
note `textZoom`; text in the run's font and size, vertically centred; numbers right-align when they fit (sheet inset 4 px),
as in the grid. Errors (`#REF!`, `#DIV/0!`, …) draw in `PaletteRole.Danger`, like a failed formula.

**Values are not cached on the span.**
`BuildRuns` lays the box out at measure (a `SheetBox` per run), so a remeasure re-reads `SheetBook.calc`. Refresh =
every open note editor invalidates the blocks holding a sheet span on `SheetBook.changed` (edit-time walk, not per frame).
Emit skips grid rows and rules outside the clip. A link to an unloaded sheet loads it through `SheetBook.Resolve` during
measure, as pictures load in `BuildRuns`.

**Paste link in a note only when the clipboard is the sheet's last copy.**
The clipboard text must equal the sheet's last copy and the source must have a path; otherwise (stale clipboard, `.txt`
note, read-only, code or rule block) it is a plain paste. One undo step ("Paste link", an `InsertRangeEdit`). Ctrl+V of a
sheet copy in a note stays plain text (tabs → spaces). `Text.PasteLink` walks to the nearest `DocumentEditorControl`
rather than adding `PasteLink` to `IClipboardTarget` — no interface change.

**Copying a sheet object out of a note writes its values.** (user, 2026-10-04)
Plain text is one value or TSV rows, not the `![[…]]` reference — "the OS clipboard gets values". Rejected: the reference
text. Our own in-process paste keeps the live span (fragment).

**Markdown: a sheet link only when the file part ends `.sheet.axml`.**
`![[Budget#…]]` without the extension stays text because it clashes with note-heading embeds. Literal typed embed text is
escaped by the writer's existing re-read check.

**A rename reaches notes by a token-level rewrite.** (user, 2026-10-04)
Re-saving a note does not reproduce its bytes, so only the link text changes (`.axml`: the `Sheet="…"` attribute on a
`<Run`, decoded and escaped; `.md`: `![[…]]`). BOM and line endings preserved; only files holding a matching link are
written. Open notes (found through `TabViewControl.FindOpenDocuments`): spans rewritten in memory too and the note's dirty
state left as is, so memory and disk agree whether the user saves or discards. Rejected: memory only + mark dirty.

### S2c — note math reads sheet cells (2026-10-04)

**Substitution only, not evaluation.** (user, 2026-10-04)
Formulas still only typeset. Rejected for S2c: a TeX-tree evaluator (digits, `+ − × · /`, `\frac`, `^`, `\sqrt`, parentheses) with a result after a trailing `=` — a slice of its own, recorded as possible S2d, not designed.

**Syntax `\sheet{…}` holding the same reference string as note links.** (user, 2026-10-04)
One format (`file#Page!A1`), and the rename rewrite reuses `SheetLinks`. Rejected: bare Excel `[Budget]Data!B1`, which clashes with TeX's bracket (optional-argument) syntax.

**Expansion happens before parsing, and the box cache is keyed by the expanded source.** (user, 2026-10-04)
A number becomes `{value}` (math digits), text becomes `\text{value}` with `{`, `}`, `\` stripped, a range becomes `\text{#VALUE!}`, a missing file/page/address `\text{#REF!}`.
Rejected for a range: a matrix, which needs math environments — `\begin` fails today. A changed value is a new cache key, so the cache never goes stale; `MathParser` stays a pure string → tree function.

**Paste link (Ctrl+V since 2026-10-30) in the formula popup inserts `\sheet{ref}` at the field caret when the clipboard is the sheet's last copy.** (user, 2026-10-04)
Otherwise the field pastes plainly.

### S3 — formatting and resize (2026-10-04)

**What landed.**
- `SheetFormat` (in SheetDocument.cs): `readonly record struct SheetFormat(bool bold, string? fill, string? number)`; `number` is a .NET custom numeric format string (null = General), `fill` a hex.
- `SheetPage.formats : Dictionary<long, SheetFormat>` keyed by `SheetDocument.Key`; `Format(row, column)`, `SetFormat(row, column, format)` (the default format removes the key).
- `SheetValue.Display(string? format)`: a Number through the format, anything else as `Display()`.
- `SheetXml`: `<Format At="B3" Bold="true" Fill="#C8E6A0" Number="#,##0.00"/>` under `<Page>`, after `<Row>`, row-major; older files load unchanged. `SheetXml.Fill` drops a `Fill` that is not `#RRGGBB` / `RRGGBB` (2026-10-06): `Control.HexToRGB` throws on anything else when the grid paints, so one bad attribute made the file unopenable — see `Mistakes/sheet-fill-is-hex.md`.
- `SheetEdits.cs`: `SheetFormatEdit` (list of row, column, before, after) and `SheetBandEdit` (column or row, index, size before/after, null = default); both raise `SheetBook.Changed(document, page, [])` so the tab goes unsaved and open notes redraw.
- `SheetEditorControl` region `formatting`: `ToggleBold()`, `SetFill(hex)`, `SetNumberFormat(number)`, private `Restyle(label, change)` (one undo step over `Range()`), internal `ResizeBand(column, index, before, after)`, `IsSelected(row, column)`.
- `SheetControl`: `fills` Parts first in draw order (pooled `fillParts`); bold through the cell label's run `style` (`FontStyle.Bold`); grid text through `Display(format.number)`; header edge resize — `EdgeAt(point, out column, out index)` (±4 px `grabWidth` around a column header's right edge or a row header's bottom edge), `OnPointerPress` starts it, `OnDrag` writes the size live (min `minimumBand` = 8 px), `OnDragStop` records it through `ResizeBand`; `OnPointerMove`/`OnPointerExit` set HResize/VResize/Arrow through `ShowCursor`; a right press on a cell outside the selection selects it.
- `SheetLinks.Layout` (inline value and range grid) uses the page's number format; `SheetLinks.Plain` and `ExpandMath` stay unformatted.
- `SheetActions` region `formatting`: `Sheet.Bold`, `Sheet.FillNone/Yellow/Green/Blue/Pink/Orange/Purple` (read `DocumentToolbarControl.highlightOptions`, now `internal`), `Sheet.FormatGeneral` (null), `Sheet.FormatNumber` (`#,##0.00`), `Sheet.FormatPercent` (`0.00%`), `Sheet.FormatCurrency` (`€#,##0.00`).
- Data: `InputMap.inputs.axml` Ctrl+B → `Sheet.Bold` beside `Text.Bold` (each checks its own editor); new `Menus/Sheet.menu.axml` (Cut, Copy, Paste, Paste link | Bold, Fill ▸, Number format ▸) registered as `sheet` in `ThoriumAssets.assets.axml`; Thorium `VaultBrowserControl.BuildSheetTab` sets `contextMenu = "sheet"`; `EngineFonts.imports.axml` Latin charset gains `€`.

**Formats live per page, shared by all layers, like band sizes.**
Rejected: per layer on `SheetCell` — it would break "an empty cell is absent", which `SheetLayer.Set`, `SheetPage.Used` and the formula graph rely on; a formatted empty cell would need a cell with no text. Consequences: a cell can be formatted while empty; Delete/Clear empties contents and keeps the format (Excel behaviour); cut/paste move values only, formats stay where they are.

**A number format is a .NET custom numeric format string, rendered with InvariantCulture.** (user, 2026-10-04)
Presets are data in actions, so adding one is an action plus a menu line. General and Scientific are one preset: General already shows G15, which switches to E-notation for very large or small numbers. Currency is euros.

**If a cell's value is a number, a number is copied.** (user, 2026-10-04)
Copy writes unformatted values (`12.50%` copies `0.125`, `€1,234.50` copies `1234.5`), so copy-then-paste inside a sheet still yields numbers. Same for `SheetLinks.Plain` (a note's plain-text copy). Note math (`\sheet{…}` substitution) stays unformatted because it must parse. Note link display (inline and range) shows the formatted text so the note matches the sheet.

**Bold toggle is Excel's.**
If the active cell is bold, unbold the whole selection, else bold it.

**Formats are applied from Ctrl+B and a grid context menu; fill swatches are the note highlight swatches (one list).**
Rejected for now: a sheet toolbar swapping in for `DocumentToolbar` — a new control and a `UI.ui.axml` change; deferred until workspace tabs reshape the chrome.

**Resize is recorded in `OnDragStop`, not `OnPointerRelease`.**
A release during a drag goes to `UIEngine.EndDrag`, never to the control's release handler. The drag writes the size live and one `SheetBandEdit` is pushed at the end.

**Spare pooled parts hide at the sheet's own corner (`Hidden`), not at `LayoutRect.Empty`.**
`SheetControl` arranges its `Parts` layers at the sheet's whole rect before arranging their pooled children. A spare child hidden with `LayoutRect.Empty` sits at absolute (0,0), and `LayoutRect.Union` does not skip zero-size rects, so when the sheet sat away from the origin the layer's cached `subtreeBounds` missed the child (DEBUG `VerifySubtreeCache` / skipped-layout check logged `subtreeBounds != recomputed`; the hit-test early-out could be wrong). Pre-existing since S1 (reproduced by scrolling a sheet at x=500 past its text with S1 code only); earlier tests had the sheet at x=0, where the origin is inside the rect. `Hidden` replaces `LayoutRect.Empty` for spare pooled lines, fills, labels and header names, so every child stays inside its layer's rect.
Rejected / not done: making `Union` skip empty rects engine-wide (touches every control). `selection` and `field` still arrange to `LayoutRect.Empty` — they are SheetControl's direct children, unioned after `ArrangeCore`, so consistent, just a looser bound. Companion fix 2026-10-05: a layer's bounds also came from the children's previous frame, wrong once a fixed page shrinks; `Parts.Settle` re-arranges each layer after its children (see the S6 follow-up).

**`€` was added to the Latin charset** because currency drew a blank glyph. Chosen over an `EUR` suffix or `$` (user). Thorium's arial, arialbd, Electrolize and consola atlases were re-baked.

### S4a — page tabs (2026-10-04)

**What landed.**
- `SheetEditorControl` base `ScrollableControl` → `StackPanelControl` (vertical, Stretch both axes): `public readonly ScrollableControl scroller` (nested private `Scroller` holds the `SheetControl`; the pending-view / scroll-active-cell-into-view `ArrangeCore` moved into it unchanged) above a private `strip` (`SheetPageStripControl`). Callers scroll through `editor.scroller`.
- `pageIndex`, `page` (shown `SheetPage`), private `Build(int)` (fresh grid on one page, selection A1, scroll 0; moves the active control to the new grid if the old grid or its field held it). `BookChanged` skips a destroyed editor (`Entity.destroyed`) and falls back to a neighbour page if the shown one was removed. `ViewState`/`RestoreView` carry the page index in `SessionTab.topBlock`.
- Region `pages`: `ShowPage(int)`, `AddPage()` ("Sheet N", first free N), `DeletePage(int)` (the last page stays), `RenamePage(int, string) → bool` (refuses empty, containing `!`, or another page's name ignoring case), `ExportPage(int) → string?`, private `FreePageName`, private `Record(label, IEditRecord)`.
- `SheetPageStripControl : StackPanelControl` (no XML): Chrome strip 26 px; private `PageTab : ButtonControl` per page (an `EditableLabelControl` caption; press shows the page, double-tap renames, `contextMenu = "sheet-page"`), a permanent "+" button, a star filler, a "Layers" button. `Sync()` rebuilds tabs only when pages or names changed, else relights. Its static ctor registers the code-built menu `sheet-page` through `ContextMenus.Register`: Rename, Delete, Export as CSV.
- `SheetEdits.cs`: `SheetPageEdit(document, index, page, added)` (add and delete), `SheetPageRenameEdit(document, page, before, after)`.
- Page rename rewrites vault-wide: `SheetBook.PageRenamed(document, oldName, newName)` → private `RewritePages` (files) and `RewriteFormulas` (shared with the file rename); `SheetFormula.RenamePage(raw, Func<string? file, string page, string?>)` (shares private `Rewrite` with `RenameFile`; the parser now also collects page-only prefixes `Page!`, `'My page'!`, prefix tuple file is `string?`); `SheetLinks.PageRenamed(sheetPath, oldName, newName, notes)` (private `RewriteNotes` shared with `Renamed`). New hooks `SheetBook.vaultSheets`/`vaultNotes` (`Func<IEnumerable<string>>?`, set by Thorium beside `findSheet`; `VaultBrowserControl.VaultSheets`/`VaultNotes` private → internal). `SheetBook.Restructured(document)` re-keys `owners`, runs `calc.RecalcAll`, raises `changed(document)`.

**The strip lives inside the editor, not drawn pinned inside `SheetControl` like the headers.**
`TabViewControl.FileEditorOf` needs `children[0] is IFileEditor` and `SheetActions.Editor()` walks up; both are kept. A pinned strip would have sat under the horizontal scroll thumb and made tabs pooled parts. Cost: the editor is a StackPanel holding a scroller + strip, and callers scroll through `editor.scroller`.

**A page rename rewrites every reference to the page, vault-wide, and undo re-runs the rewrite in reverse.** (user rule already set for file renames: "a rename inside Thorium rewrites references")
Same-file `Page!`, other sheets' `[File]Page!` (loaded or on disk), note links `file#Page!…` and note math `\sheet{file#Page!…}`. It is one undo record, so undoing rewrites other files and notes again. Rejected: same-file only, or `#REF!` everywhere.

**Page add, delete and rename are undo records.**
An older `SheetCellEdit` holds its `SheetPage` (and `SheetLayer`) and would otherwise write into a detached object. References resolve by name, so undoing a delete brings them back without bookkeeping.

**Structure changes recalc with `SheetCalc.RecalcAll`, not a targeted relink.**
They happen at edit rate, not per frame.

**Delete page with one left is a no-op, not a greyed menu row.**
Menu rows have no disabled state (user accepted).

**The page menu is code-built through `ContextMenus.Register`.**
A press-opened code menu would be closed by the right-release `OpenOn` that follows.

### S4b — layers panel (2026-10-04)

**What landed.**
- `SheetLayersControl : StackPanelControl` (no XML): `panelWidth`, `Sync()` (rebuilds rows on add/remove, else repaints). One row per layer, top first: a visibility toggle built from the `bullet-disc` / `bullet-circle` icons (filled / outline dot; no eye icon exists in the default set) and the name. The edited layer is lit and a click on a name picks it. "Add layer" (goes on top, becomes edited) and "Delete layer" (deletes the edited one; the last layer stays).
- Opened by the strip's "Layers" button as `ContextMenuContent` above itself, its height measured first because menus do not flip upward.
- `SheetEditorControl` region `layers`: `editLayer` (picked layer while on the page, else topmost), `EditLayer(SheetLayer)`, `ToggleLayer(SheetLayer)`, `AddLayer()`, `DeleteLayer()`, private `FreeLayerName`; private `pickedLayer` replaces the old `Layer => layers[^1]`. Edits land in the picked layer; the open cell field shows that layer's own text while the grid shows the topmost visible layer's.
- `SheetEdits.cs`: `SheetLayerEdit(document, page, index, layer, added)`, `SheetLayerShowEdit(document, page, layer)` (undo = redo = flip; the `page` parameter is unused). Show/hide recalcs through `SheetBook.Restructured`.

**The edited layer is per editor and not saved.**
It defaults to the topmost layer; a newly added layer becomes edited.

**Layer add, delete and show/hide are undo records**, for the same reason as pages (an older `SheetCellEdit` holds its `SheetLayer`).

**Delete layer with one left is a no-op**, like delete page.

### S5 — CSV in place (2026-10-05)

**What landed.**
- Static `SheetCsv`: `extension`, `IsCsv`, `Delimiter(text)` (the most used of `,` `;` tab in the first record outside quotes; comma on a tie or none), `Read(text, delimiter)` (RFC 4180, BOM stripped, a closing line break makes no empty record), `Write(rows, delimiter)` (quotes fields holding the delimiter, `"`, CR/LF or edge spaces; CRLF after every record), `Load(path)`, `Save(document, path)`, `Export(page, path)`.
- `SheetDocument.csvDelimiter`, `csvBom`; `Load`/`Save` route by extension to `SheetCsv` or `SheetXml`. A CSV document is one page named after the file, one layer. `LoadPath` hides the strip for a CSV.
- `SheetBook.Renamed(oldPath, newPath, vaultSheets)` re-keys a loaded CSV (S5 first had a separate `Moved`; S5b removed it).
- Thorium `VaultBrowserControl`: `Accepts` adds `.csv`; `DisplayName` keeps the `.csv` suffix; `RowContextMenu` → `vault-csv` for a CSV; `BuildTab` routes a CSV to `BuildSheetTab`; `FindNote`/`FirstNote` skip CSVs; `DeleteFile` drops a CSV from `SheetBook`; `RenameNote` strips a typed `.csv` suffix and calls `SheetBook.Renamed`.
- Actions `Sheets.FromCsv` ("Create a sheet from this") and `Sheets.ConvertCsv` ("Convert to sheet") over private `SheetFromCsv(file, replace)`. `Thorium/…/Menus/VaultCsv.menu.axml` (Create a sheet from this, Convert to sheet, Rename, Duplicate, Delete) registered as `vault-csv`.
- `SheetEditorControl.PasteLink`: S5 pasted plainly from a CSV source and `CopiedReference` returned null for one; S5b removed those guards (see S5b).
- Page tab right-click "Export as CSV" → `ExportPage`: `<Sheet> - <Page>.csv` beside the sheet.

**A CSV opens in place instead of being imported into a new `.sheet.axml`.** (user: "recreating a file and then interpreting it — might as well interpret it on the go")
Ctrl+S writes the CSV back where it is. Formats, column widths and extra layers are not saved for a CSV (a CSV can hold none of them); there is no page strip in a CSV tab.

**Save writes the raw cell text and keeps the delimiter and BOM it read.**
Formulas stay `=B2*2` (Excel also evaluates `=` fields in CSV) rather than values.

**References into a CSV were deferred to S5b, and landed there.**
Until then Paste link from a CSV pasted plainly (retracted by S5b).

**Export writes values as shown, numbers unformatted (the S3 copy rule), UTF-8 with BOM, comma, CRLF.**
Excel needs the BOM for UTF-8. Re-export overwrites the same file name.

**Two menu actions on a CSV row.** (user)
"Create a sheet from this" writes `<name>.sheet.axml` beside the CSV, keeps the CSV and opens the sheet. "Convert to sheet" does the same, then sends the CSV to the recycle bin and closes its tab; reversible, so no confirm prompt. Both use the loaded copy, so unsaved edits in an open CSV tab come along.

### S5b — references into a CSV (2026-10-05)

**What landed.**
- Reference forms: sheet formula `[data.csv]data!A1`; note link `data.csv#data!A1` (`![[data.csv#data!A1]]` in `.md`, `<Run Sheet=…>` in `.axml`); note math `\sheet{data.csv#data!A1}`. The page is the CSV's file base name.
- `SheetDocument.isCsv` (set by `SheetCsv.Load`). `SheetCalc.PageNamed` returns null for a file-part reference whose home page belongs to a CSV document, so a CSV reaches nothing outside itself (`#REF!`).
- `SheetFormula.RenamePrefix(raw, Func<string? file, string page, (string? file, string page)?>)` is the old private `Rewrite`, now public; `RenamePage` wraps it; `RenameFile` removed (no callers left).
- `SheetBook`: `Renamed(oldPath, newPath, vaultSheets)` handles CSV paths (the moved document gets `isCsv` from the new path; CSV→CSV renames its page; CSV documents are skipped as rewrite targets other than the moved one); `PageRenamed` skips CSV documents; `Names(file, path)` (was `(file, stem)`) handles CSV file parts; `Renamed(file, oldPath, newPath)` (was `(file, oldName, newName)`) keeps a folder prefix and whether an extension was written — a CSV target writes `.csv`, a sheet target writes `.sheet.axml` only if the old part had an extension; new `FileName(path)` (reference file part: sheet base name, CSV name with `.csv`); `BaseName` handles `.csv`; `Stem` and `Moved` removed.
- `SheetLinks.IsLink` accepts `.csv` file parts; `Renamed` rewrites the page part too for CSV→CSV; `PageRenamed` uses `Names(file, path)`.
- `SheetEditorControl.PasteLink`: file part via `SheetBook.FileName`; pastes plainly when the target document is a CSV and the copy came from another file; the S5 guards that blocked links out of a CSV (`PasteLink`, `CopiedReference`) are gone.
- Thorium `VaultBrowserControl`: `FindSheet` resolves a `.csv` file part to the first vault CSV whose path ends with it; `RenameNote` calls `SheetBook.Renamed` and `SheetLinks.Renamed` for CSVs too; `SheetFromCsv(replace: true)` calls both before `DeleteFile`. `VaultSheets()` still lists only `*.sheet.axml`.
- Test `Sheet.CsvLinks` (`Sheet.tests.axml`).

**A CSV can be referenced but references nothing outside itself.** (user, 2026-10-05: "CSVs should not be able to reference anything from outside, but they can be referenced.")
So other files' renames never rewrite a CSV (no re-serialising a CSV just to rewrite a dead reference), and Paste link into a CSV from another file pastes plain values.

**The file part keeps `.csv`; a sheet's file part may drop `.sheet.axml`.** Excel writes `[data.csv]data!A1`.
Rejected: dropping `.csv`, which would collide with a sheet of the same base name (`data.csv` and `data` resolve separately).

**A CSV→CSV rename rewrites the page part as well as the file part.**
`[data.csv]data!` → `[cost.csv]cost!`, and the CSV's own `data!B2` → `cost!B2`, because a CSV cannot store its page name and re-derives it from the file name on load. Rejected: a fixed page name like `Sheet1` (references would read `[data.csv]Sheet1!A1`, unlike Excel); rewriting the file part only, which breaks on the next load when the page is re-derived from the new file name.

**"Convert to sheet" retargets references; "Create a sheet from this" leaves them on the CSV.**
Convert replaces the file, so every reference moves to the new sheet before the CSV goes to the recycle bin (`[data.csv]data!` → `[data.sheet.axml]data!`, note `data.csv#…` → `data.sheet.axml#…`; page unchanged), and the converted document now reaches other files. Create-from keeps the CSV, so its references stay.

### Formula popup closed from outside (2026-10-04)

**`ContextMenus.Open` takes `onClosed`, run once when the top-level menu closes, whoever closes it; `FormulaPopup.Show` passes `Cancel`.**
`FormulaPopup.open` went stale when the menu holding the popup was closed by anything but the popup's own Finish/Cancel (focus loss through `ContextMenus.Tick`, another `ContextMenus.Open`). Destroyed controls are dropped by `UIEngine.Forget` without `onBlur`, so Ctrl+Shift+V then pasted into a destroyed box: `DataPool.GetRef` read row -1 → `IndexOutOfRangeException` in `Main.Input`. `onClosed` is private `_onClosed`, run at the start of `CloseFrom(0)`. See [[context-menus]].
Rejected: a liveness guard in `FormulaPopup.PasteLink` — it would stop the crash but leave an unrecorded preview. Cancel on an outside close discards the typed source; clicking elsewhere inside the app still commits through blur. The user accepted Cancel; committing instead is an open option.

### Fixed sheets and insert (2026-10-05)

**Fixed vs unfixed is a document type chosen at creation, not a per-page or later toggle (user).**
- `SheetDocument.fixedSize`, `SheetDocument.Blank(string? name, bool fixedSize = false)`. An unfixed sheet keeps today's code path untouched. A fixed page has `SheetPage.rows`/`columns` (only meaningful on a fixed document; `SheetPage.Blank` sets `SheetPage.defaultSize` = 25 x 25, the user's default). `SheetEditorControl.Select` clamps to the page; private `BookChanged` re-clamps anchor/active when a fixed page shrinks (undo of a grow).
- `SheetXml`: `<Sheet Fixed="true">`, `<Page Rows Columns>`, written only for fixed documents; on load page size = max(attribute or 25, `Used()`); no `Fixed` attribute = unfixed (all pre-existing files). CSV is always unfixed — no change in `SheetCsv`; convert-to-sheet stays unfixed.
- Thorium: `VaultBrowserControl` actions `Sheets.NewFixed`, `Sheets.NewFixedHere`; `Sheets.New`/`Sheets.NewHere` now create unfixed sheets; private `NewSheetAtRoot(bool)`, `NewSheetBeside(bool)`; `NewSheet(folder, fixedSize)`, `CreateSheet(folder, name, fixedSize, rows, columns)`. "New sheet" in `File.menu.axml`, `Vault.menu.axml`, `VaultFolder.menu.axml` is now a submenu "Fixed size" / "Unfixed".
- **Fixed size chosen at creation (user, 2026-10-21; reverses the earlier rejection of a size field in the name prompt).** "Fixed size" opens `NoteNameWindow.AskSheet(source, suggestion, rows, columns, Action<string,int,int>, onCancel)` — the name prompt plus a Rows / Columns row prefilled with `SheetPage.defaultSize`; `CreateSheet` sets `pages[0].rows/columns`. One prompt, not a second `SheetSizeWindow` (would duplicate the window boilerplate and add a dialog). The size row is built once and `Hide()`/`Show()`n per ask (layout skips hidden children), so plain `Ask` is unchanged. Confirm refuses a count outside 1..`SheetFormula.maxRows`/`maxColumns`, like an empty name. Unfixed sheets get no size prompt. No Tab between the boxes (`Sheet.Tab` only reaches `SheetGrowPopup`). Test `Sheet.FixedSizeChosen`.

**A fixed page grows by hand through "+" strips along the whole bottom and right edges (user).**
- Left press on a strip adds 1; Shift+left adds `SheetSettings.grow.step` (default 10); right press opens `SheetGrowPopup`. The strips span the whole edge.
- `SheetSettings : SettingCategory` (XSD "Sheets", Settings category) with `SheetGrowSetting` (XSD "SheetGrow", member `step`/`Step`, default 10), both in `SheetDocument.cs`; shows in the Settings window automatically, no settings XML file.
- `SheetControl`: fixed docs measure to `rows` x `columns` + a 20 px strip (`growWidth`); arrange loops stop at the page edge; grid lines end at the grid edge; two fixed parts `growRows`/`growColumns` with "+" labels (Chrome / MutedInk; restyled, see the 2026-10-05 follow-up below), cut to the viewport; `ArrangeGrow`, `GrowAt`. `growPresses` (2-bit history of left presses on a strip) makes `OnPointerTap` ignore a double tap that involved a strip press.
- `SheetEditorControl.Grow(rows, columns)` records `SheetSizeEdit(document, page, (rows, columns) before, after)`, label "Grow page"; `SheetPage.Extend(rows, columns)` only grows.
- `SheetGrowPopup` (internal): built like `FormulaPopup` (`ContextMenus.Open` + `ContextMenuContent`), two number boxes "Add horizontal" (columns) and "Add vertical" (rows), both starting at 0; the bottom strip opens it with "Add vertical" focused, the right strip with "Add horizontal". Enter = one `Grow`, Esc/outside click cancels, `SheetGrowPopup.Tab()` swaps boxes (`Sheet.Tab`/`Sheet.TabBack` try it first); non-numbers count as 0.

**Right press on a strip sets `stopsContextMenu` for that one release and opens the popup on the next tick.**
The context menu opens on right *release* in `UIEngine` and `ContextMenus.Open` always closes the open menu first, so a popup opened on press would be closed. Right press sets `growMenu` and `stopsContextMenu = true` (the walk yields no entries); `OnPointerRelease` posts (`Engine.Post`) opening the popup next tick and resets `stopsContextMenu`.

**Paste past the edge of a fixed page grows the page in the same undo step (user).**
`Paste` pushes a `SheetSizeEdit` and the `SheetCellEdit` in one undo scope. Rejected: truncating the paste.

**Up/left extension is insert-before-selection from the grid's right-click menu, in both sheet types (user).**
- The user chose up and left; insert at the selection generalises "prepend at row 1 / column A" at the same cost. Count = the selection's span (Excel behaviour).
- `Sheet.InsertRowsAbove`, `Sheet.InsertColumnsLeft` (entries in `Sheet.menu.axml`, own group after Paste link); `SheetEditorControl.Insert(bool column)`.
- `SheetPage.Shift(bool column, int at, int count)` moves keys at/after `at` in every layer's cells, `formats`, `columnWidths`/`rowHeights`, and adds count to the size; a negative count first drops the -count bands at `at`.
- `SheetInsertEdit(document, page, column, at, count)`: redo = `Shift(+count)` + `SheetBook.Shifted`, undo = `Shift(-count)` + `SheetBook.Shifted` with -count.

**Reference rewriting on insert reuses the page-rename machinery; the formula text stays the only source of truth.**
- `SheetBook.Shifted(document, page, column, at, count)` is shaped like `PageRenamed`: rewrites its own doc, other loaded non-CSV sheets (saved if no tab has them open), unloaded vault sheets via `vaultSheets`, notes via `SheetLinks.Shifted(sheetPath, page, column, at, count, notes)`, then `calc.RecalcAll`. Private `RewriteFormulas` gained an overload passing the formula's own `SheetPage`.
- `SheetLinks.Shifted` moves each endpoint of `file#Page!A1:B2` links (and `\sheet{…}` math) through the existing `RewriteNotes`.
- `SheetFormula.ShiftCells(raw, Func<string? file, string? page, bool> onPage, bool column, int at, int count)`; the private `Parser` gained an optional `addresses` span list (like `prefixes`), and private `Address(...)` takes the first word's start index (because `Peek` skips whitespace).
- Range endpoints shift independently: `SUM(A1:A10)` with rows inserted at row 5 grows (`A1:A12`-style) and an insert at row 1 moves the whole range.
- Rejected for now: storing backlinks on referenced cells. They go stale with edits outside Thorium, make typing in one file dirty another file (cross-file undo), cost one entry per cell of a range (`SUM(A1:Z100000)` already records 2.6M graph edges), and need notes→cell lists. The user intends to come back to this later with a **link cache file in the vault**. Superseded 2026-10-25 by [[link-graph]]: the cache is a link cache outside the vault, keyed by vault id.

**Verified:** builds clean. Test-verified: `Sheet.FixedSize` (25x25 blank, XML attributes written/omitted, old file opens unfixed, fixed page grows to hold its cells, select/Enter/Tab clamp, left click on bottom strip adds 1 row, Shift+click on right strip adds the set step, no cell opens after quick strip clicks, undo, paste past edge grows in one undo, grown size saved) and `Sheet.InsertShifts` (rows: own formulas incl. range, values, formats and row heights move, fixed size grows, loaded sheet refs incl. a file-qualified range, sheet on disk, note link range and `\sheet{}` math; undo restores all; columns: same-page and loaded-sheet refs; undo). Golden-verified: `Sheet.FixedSize.Page` (5x4 fixed page with both strips). Full Thorium `--test`: 151 passed, 9 failed — Boot (pre-existing sampler-asset error), `Sheet.Layers`, `Sheet.MathLinks` and six `TextInput.Math*` tests, which open `ContextMenus` popups and are the known OS-focus failure (gated under `--test` by the follow-up below). **Not verified:** the context-menu Insert entries through the real menu (only `Insert` called directly), GUI use of anything. The right-click popup and Tab between its boxes are covered by the follow-up below.

**Follow-up (2026-10-05): the strips follow the palette and sit off the grid, the grow popup has a test, a parts layer is re-arranged once its children are placed.**
- Strip look: `SheetControl.growGap` (4 px) separates the strips from the grid and from each other (`MeasureCore` on a fixed sheet adds `growWidth + growGap * 2`; `ArrangeGrow` insets both strips by `growGap` from the grid and pulls their ends in by `growGap`). `growRows`/`growColumns` get `cornerRole = CornerRole.Control` (`ControlRadius`) and `gradient = "sheet-grow"`, a radial in `Engine.gradients.axml`: stop `Role="Field" Pos="0"`, stop `Role="Accent" Alpha="0.6" Pos="1"`. The look is white-to-blue on the light palette and stays dim on the dark one. A gradient stop may name any surface role, `Ink` or `MutedInk`; Accent counts as a surface (`Palettes.RoleOffsets` accepts Ground..Danger). Rejected: fixed `#FFFFFF` → `#A9C4EC` (glares on the dark palette; the user asked for a palette solution) and a fixed corner radius (user chose the palette's).
- Test: `Sheet.GrowPopup` (`Thorium` `SheetTests`, listed in `Sheet.tests.axml`): right-click the right strip → "Add horizontal" focused; type 3, Tab → "Add vertical", type 2, Enter → the page grows 3 columns and 2 rows; one undo reverts both; right-click the bottom strip → "Add vertical" focused; Esc → nothing added, popup closed. The strips are not hit-testable controls, so `TestContext.Click(control, point, button = Keys.MouseLeft)` clicks a design-space point with any mouse button and fails the test if the point does not hit `control` or a descendant (same check as the point `Drag` overload).
- `ContextMenus.Tick` returns early under `TestRunner.active` (user approved; it was the open one-line option in Known gaps), so popup tests no longer depend on OS focus: `Sheet.Layers`, `Sheet.MathLinks` and the six `TextInput.Math*` tests pass with another window focused.
- Layer bounds: `SheetControl.ArrangeCore` arranges each `Parts` layer at the sheet's rect before it places that layer's pooled children (they need the layer's clip), so `LayoutEngine.ArrangeRow` computed the layer's `subtreeBounds` from the children's previous-frame bounds. Harmless while every child stays inside the layer rect, but a fixed page that shrinks (undo of a grow) leaves last frame's column line outside the new rect. Proven with a temporary probe: after the shrink the grid layer's cached bounds were 648 wide while its lines ended at 448 and its rect was 640, and DEBUG `VerifySubtreeCache` and the skipped-layout check logged every frame. Unfixed sheets never hit it because their canvas always extends past every line; pre-existing since S1. Fix: `Parts.Settle(LayoutRect)` sets `ArrangeFlags.ArrangeDirty` and arranges again, and `ArrangeCore` ends with `fills.Settle`, `grid.Settle`, `headers.Settle` after the pooled children are placed. Rejected: making `LayoutRect.Union` or the engine recompute bounds after `ArrangeCore` in general (touches every control). Companion to the `Hidden` rule under S3 above.
- **Verified:** builds clean. Test-verified: `Sheet.GrowPopup` passes with no errors logged; full Thorium `--test` 160 passed, 1 failed (Boot, pre-existing default-sampler asset error), 41 skipped. Golden-verified: `Sheet.FixedSize.Page` re-approved after reading the image (strips off the grid, rounded, radial white-to-blue on the light palette). **Not checked:** the dark palette look, GUI use.

### S7 — formula functions (2026-10-06)

**Comparisons return the numbers 1 and 0, not a boolean value kind.** (user, 2026-10-06)
Rejected: a TRUE/FALSE `SheetValueKind` like Excel and Google Sheets. It would touch `SheetValue`, `Display`, the XML and every consumer of value kinds. 1/0 composes in arithmetic: `=(A1>0)*(B1<10)` acts as AND and `(B2>0)+(B3>0)` counts. The cost is that a comparison shows 1/0 instead of TRUE/FALSE.

**Comparison is its own node `Compare`, not an extension of `Binary`.**
`Binary` carries a single `char` op and `<=`, `<>`, `>=` are two characters.

**Rounding goes through `decimal`.**
A double → decimal cast keeps the 15 significant digits a double displays, so ROUND(2.345,2) = 2.35 as in Excel; rounding the double directly gives 2.34 (2.345 * 100 = 234.49999…). Values with |x| ≥ 1e15 come back unrounded, to keep the decimal cast in range.

**No structured "Tables"** (Google-Sheets-style header row, typed columns, banding, filters, `Table[Column]` refs). (user chose a plain sheet; not designed)

What landed:
- Operators `=` `<>` `<` `>` `<=` `>=`, lowest precedence, below `+ -` as in Excel. Numbers compare numerically; text compares `OrdinalIgnoreCase` and sorts after numbers; Empty counts as 0 against a number; an error operand propagates.
- `SheetFormula.Call.Evaluate` is a switch over SUM / MIN / MAX / AVERAGE / ROUND / ROUNDUP / ROUNDDOWN / IF, names case-insensitive; anything else stays `#NAME?`.
- `Call.Aggregate` (sum, count, min, max over all arguments; a range skips text and blanks, as SUM always did) is shared by SUM/MIN/MAX/AVERAGE. MIN/MAX of no numbers = 0; AVERAGE of no numbers = `#DIV/0!`.
- `Call.Rounded`: ROUND = half away from zero, ROUNDUP = away from zero, ROUNDDOWN = toward zero. Digits optional (default 0), may be negative (ROUND(1234,-2) = 1200), clamped to ±15; a wrong argument count = `#VALUE!`.
- IF(test, then, [else]): the test must be a number (text = `#VALUE!`), non-zero is true; only the taken branch is evaluated, so an error in the other branch does not propagate; a missing else gives 0; an argument count outside 2–3 = `#VALUE!`. `References` still collects every branch so recalc order is right.
- Parser: a new `Comparison()` level above `Additive()`, used by `Formula()`, parenthesised expressions and function arguments; new `Operator()` reads a comparison operator.
- Tests: `Sheet.FormulaEval` gained cases for all of the above.
- User content, not engine code: `Company finances.sheet.axml` in the Thorium vault (pages Summary, Inputs, Staff, Costs, Pricing), a Lithuanian 2026 UAB cost/licence-pricing model built on these functions.
- **Verified:** test-verified — `_Build/test.sh Sheet`: 25 passed, 2 failed, both pre-existing and unrelated (Boot: the baseline sampler-asset error; `Sheet.FixedSize` golden, already on the WIP list). No `[Vulkan]` lines. The finance sheet was loaded through `SheetBook.Get` in a throwaway test (since removed): every page evaluated with no `#` errors and hand-checked values (gross €3,000 → employer cost €3,053.10, net €1,815; NPD at €2,500 → €86.97). **NOT GUI-verified:** typing comparison/IF/ROUND formulas in a running Thorium.

## Known gaps
- Fixed sheets and insert (S6): undo after another file wrote a reference into inserted rows — that file is not on this sheet's undo stack (same as page rename), so its reference is left pointing at whichever row slides into place (not turned into #REF!). Documented deliberately; the user will revisit with a link cache file in the vault (superseded 2026-10-25 by [[link-graph]]: a link cache outside the vault, keyed by vault id).
- No delete rows/columns, no changing a sheet's type after creation, no size field at creation. The pending Paste-link source (`copiedFrom`) is not shifted by an insert.
- Two quick clicks at the same spot on the bottom strip: the strip moves down a row after the first click, so the second lands on the new last row (selects it, does not grow). Use Shift+click or the popup for several.
- A click in the empty area past a fixed grid selects the clamped edge cell (`CellAt` still answers there). Header Chrome bands still span the whole viewport past a fixed grid's edge.
- `SheetPage.Shift` also adjusts `rows`/`columns` on unfixed documents (unused there).
- No freeze panes.
- Page/layers (S4): no page reorder; no per-page remembered selection (switching goes to A1/top); undo of a record on a page not shown applies without switching to it. No layer rename or reorder; no dimming of cells not on the edited layer; edits into a hidden layer are invisible. Page rename undo rewrites other files again (they are not on their own undo stacks). `SheetLayerShowEdit` has an unused `page` parameter.
- CSV (S5): formatting/width/layer changes mark a CSV tab unsaved but are silently dropped on save. Export overwrites without asking. A decimal comma in a `;` CSV is read as text, not a number. `SheetValue.Display()` returns null for Empty (Export maps it to "").
- CSV references (S5b): a note link copied out of a note to a CSV cell, and CSV references generally, are only test-verified. After a convert the file part reads `[cost.sheet.axml]cost!` instead of `[cost]cost!` (valid, just longer). Undo records made before a CSV rename hold the old reference (same as sheets). `FindSheet` for `.csv` enumerates the vault each call (like sheets).
- A formula popup closed by focus loss cancels (the typed source is dropped); a blur inside the app commits — inconsistent; committing in both is an open option.
- ~~`--test` depends on OS focus~~ — resolved 2026-10-05: `ContextMenus.Tick` returns early under `TestRunner.active`.
- Grow strips: the radial is an ellipse fitted to each strip, so a long strip shows a stretched highlight. Clipping a strip at the viewport edge cuts its rounded end square. The dark palette look was not checked.
- Formatting (S3): no italic, underline, text colour, alignment, borders or font size in cells; no dates or date formats, custom format entry, increase/decrease decimals.
- Resize (S3): no autofit on edge double-click, no resizing several selected columns at once, no Esc to cancel a drag; no whole-column/row selection from headers.
- Formats do not travel with copy/paste/cut.
- The open edit field does not show bold.
- Bold and fill do not show in notes' linked cells (number format does).
- A note's range grid updates on resize only when the drag ends, not live.
- `ShowCursor` caches the last shape it set; another control changing the cursor in between can leave it out of step.
- Carbon and AuroraEditor carry their own font atlases; they re-bake with `€` on their next run (the tree goes dirty then).
- Sheet links in notes: a range wider than the column is cut at the column edge; a range taller than a page does not split across pages.
- An empty linked cell has zero width inline (invisible, hard to select).
- Undo records made before a rename still hold the old reference; undoing past a rename brings back a `#REF!` link.
- A file name containing `#` or `]]`, or a page name containing `!`, breaks the reference format.
- Every open note walks its blocks on each sheet edit (edit-time cost, not per frame).
- No click action on a link (open the sheet).
- Note math (S2c): the static math layout cache gains one entry per distinct value a linked formula has shown, and nothing evicts (edit-rate growth, not per frame).
- Obsidian renders `\sheet{…}` as an unknown command. Plain-text copy of a formula writes its source with `\sheet{…}`, not the value.
- An error value inside a formula (`#REF!`, `#VALUE!`) draws in ink, not `Danger` — only an unparseable formula is red.
- No number formatting inside formulas; the value appears as the sheet displays it (G15, so a large number can read `1E+20`).
- In `.md`, a `\sheet{…}` naming the renamed sheet is rewritten wherever it appears in the file, including in plain text outside `$…$`.
- No evaluation of formulas (possible S2d).
- Renaming a sheet page rewrites note links and `\sheet{…}` (S4a); adding a page rewrites nothing.
- Non-note `.axml` files in the vault holding `<Run … Sheet="` would be rewritten too (unlikely).
- Paste link uses the source file's base name; with two sheets of that name in the vault the reference resolves to
  the first match, which may be the other one. Same-file other-page links (page tabs exist since S4a) are only test-verified
  (not GUI-verified).
- Edits made outside Thorium to a loaded sheet are not seen until relaunch or a vault switch; a loaded sheet is
  never unloaded during a vault session. `Created`/`Deleted`/`Renamed` relink every loaded sheet.
- `SheetControl.ArrangeCore` loops forever on an unbounded viewport (fixed for fixed sheets only, 2026-10-05: their arrange loops stop at the page edge; unfixed sheets still hang) — an editor laid out as its own root (never
  added to a window) hangs the frame. Found by a test; real tabs are always bounded. Hit again by `Sheet.CsvLinks`: a CSV editor never added to a window hung the run (a detached `SheetEditorControl` that is selected/copied); with both editors in the window the test passes. Not proven by instrumentation.
- After `Cut`, Paste link pastes plainly (the copy's source is dropped).
- Formulas: no `$A$1`, no string literals (`="text"`; text compares only against another cell), no AND/OR/NOT/COUNT (1/0 arithmetic stands in), no fill-down (a new row of formulas must be typed), typing `21%` stays text (`FromRaw` does not parse a percent sign; enter 0.21 with the Percent format), references are not adjusted on paste; since S4a a page
  rename rewrites them and a page add recalcs.
- A range is one edge per cell: `SUM(A1:Z100000)` records 2.6M edges.
- Showing or hiding a layer recalcs through `SheetBook.Restructured` (S4b).
- Errors draw in plain ink, left-aligned.
- Arrows inside an open cell move the caret (Excel's edit mode); there is no enter mode where arrows commit.
- Paste reads plain TSV — quoted fields with embedded tabs/newlines are split literally.
- Cut-off text shows a partial glyph at the cell edge (label clip), no ellipsis.
- Select-all (Ctrl+A) selects the used range, not the whole grid.
- Headers: dragging an edge resizes (S3), but plain header clicks do nothing; no autoscroll on drag-select past the edge.
- `File ▸ Save note` (`Text.Save`) does not reach a sheet; Ctrl+S does (`Sheet.Save`).
- **NOT GUI-verified** — test- and golden-verified only (`Sheet.*`, `Sheet.GridDraws.Grid.png`; formulas
  `Sheet.FormulaEval`, `FormulaRecalc`, `FormulaView`; across files `Sheet.CrossFile`, `RenameRewrites`, `PasteLinks`; notes `Sheet.NoteFormats`, `NoteLinks` (goldens
  `Sheet.NoteLinks.Links.png`, `Sheet.NoteLinks.Edited.png`), `NotePasteLinks`, `NoteRenameRewrites`). S2b3: `Sheet` suite 16/16, full `--test`
  136 passed, 41 skipped, 1 failed = Boot (pre-existing default-sampler error); no real session used Ctrl+Shift+V into a note or renamed a sheet from the vault browser.
  S2c: `Sheet.MathLinks` (golden `Sheet.MathLinks.Math.png`) checks the expansion of number, text, range (`#VALUE!`) and missing sheet (`#REF!`); a formula widens after a sheet edit while the note stays clean;
  Ctrl+M then Ctrl+Shift+V in the popup puts `\sheet{Budget.sheet.axml#Data!B1}` in the field and Enter commits it; a stale clipboard pastes plainly; a rename rewrites an open note's formula, an `.axml` note's `Math` attribute
  byte-for-byte otherwise (including `&#xA;` and `&lt;`), and an `.md` note's `$…$`. `Sheet` suite 17/17, full `--test` 137 passed, 41 skipped, 1 failed = Boot (pre-existing default-sampler error); no `[Vulkan]` lines.
  Golden looked at before approval ("x a = 20 y", "#REF! end"). Typing `\sheet{…}` and Ctrl+Shift+V in the popup were not tried in a real session.
  S3: test- and golden-verified. `--test=Sheet` 19 passed (suite 19/19), full `--test` 139 passed, 41 skipped, 1 failed = Boot (pre-existing default-sampler error); no `[Vulkan]` lines.
  `Sheet.Formats` checks the Format XML round trip; Ctrl+B bolds a selection, a second Ctrl+B unbolds and leaves no format; fill; Number `1,234.50`, Currency `€1,234.50`, Percent on a formula `24.69%`, text ignores a number format; Copy of a formatted number gives `1234.5`; `ExpandMath` gives `{1234.5}`; Delete keeps the format; undo/redo; unsaved; formats saved.
  Golden `Sheet.Formats.Grid.png` looked at before approval (bold, orange fill, `€1,234.50`, `24.69%` in the grid, the note's inline and range links formatted). `Sheet.Resize` checks a drag of B's right edge → 160 px, unsaved, undo removes the width, redo; row 1 drag deepens it and the note's linked range grows; a row dragged past its top clamps at 8 px; a press away from an edge still selects; sizes saved. Existing goldens still matched after the atlas re-bake.
  NOT GUI-verified: the right-click menu (Fill/Number format submenus, right-press selecting), resize cursor shapes on hover, Ctrl+B in a real session, resize feel.
  S4/S5: test- and golden-verified, NOT GUI-verified. Full `--test` (window focused) 142 passed, 1 failed = Boot (pre-existing default-sampler error), 41 skipped; no `[Vulkan]` lines; `--test=Sheet` 22 passed + Boot.
  `Sheet.Pages`: add/switch/rename/delete; rename rewrites same-file, another loaded sheet, a sheet on disk, an `.md` note link and `\sheet{}`; undo/redo of rename; delete → `#REF!`, undo restores; a second editor survives; view state keeps the page; save round trip; golden `Sheet.Pages.Strip.png`.
  `Sheet.Layers`: edits land in the picked layer; a covered cell keeps showing the top; hide changes the shown value and a dependent formula; undo/redo; delete; `Visible` round trip; clicks the Layers button and a layer name through the test runner; golden `Sheet.Layers.Panel.png`. `Sheet.GridDraws.Grid.png` and `Sheet.Formats.Grid.png` re-approved (strip at the bottom, thumbs moved, nothing else).
  `Sheet.Csv`: quoting, escaped quotes, line breaks, BOM, delimiter detection, write/read round trip, export name/BOM/values/overwrite, a CSV opens through `SessionLayout.tabFactory` as a sheet tab with no strip, a `;` CSV without BOM saved back in kind with formulas raw.
  Popup crash: reproduced with a focus-stealing window (instrumented log showed `Tick` closing the menu, then `PasteLink` on `destroyed=True`); no crash under the same thief with the fix. The new `Sheet.MathLinks` check (close the open popup from outside, then Ctrl+Shift+V) crashes 3/3 with `onClosed` removed and passes with it.
  NOT checked in a real session: clicking tabs, "+", rename by double-click, the page and Layers menus, the vault browser CSV row and its menu, "Create a sheet from this" and "Convert to sheet" (both only build; no test drives the vault browser), CSV rename/delete.
  S5b: test-verified, NOT GUI-verified. Full `--test` (window focused) 143 passed, 1 failed = Boot (pre-existing default-sampler error), 41 skipped; no `[Vulkan]` lines. `Sheet.CsvLinks`: sheet formulas read a CSV (including a CSV formula's result); `data.csv` vs `data.sheet.axml` resolve separately; a CSV's `[Budget]Data!A1` is `#REF!`; note link `Plain` and `\sheet{}` expansion read a CSV; Paste link from a CSV tab writes `=[data.csv]data!B2`; Paste link into a CSV from Budget pastes `10`; a CSV rename rewrites a loaded sheet (file+page), the CSV's own `data!B2`, a sheet on disk, a `.md` note's link and `\sheet{}`, and leaves `[data]Data!A1` alone; a convert-style rename (CSV → `cost.sheet.axml`) points sheet and note references at the sheet, the value still reads, and the converted document now reaches Budget.
  NOT checked: the vault browser path (rename a CSV row, Convert to sheet through the menu) and anything in a real session.

Related: [[document-tables]], [[session-restore]], [[vault-browser-and-shell]], [[document-undo]], [[text-clipboard]]
