---
date: 2026-10-02
Status: Current
tags:
  - d_UI
  - d_Entity
cssclasses:
  - Aurora.css
Linker:
  - "[[Arctis Aurora]]"
System:
Class:
  - "[[Sheet Editor]]"
Parent Class:
  - StackPanelControl
Interfaces:
  - IClipboardTarget
  - IFileEditor
Used by:
  - VaultBrowserControl
Type:
  - Public
Attributes:
Namespace: ArctisAurora.Core.UI
SourceFile: AuroraEngine/Core/UI/Sheets/SheetEditorControl.cs
VerifiedAgainst: 2026-10-05
---
## Description

One open sheet in a tab: a scroll viewport over a `SheetControl` grid with a strip of page tabs under it, the `SheetDocument` it shows, the file it came from and its undo history. A sheet is a `*.sheet.axml` file in the vault, opened from the browser like a note; a `.csv` file opens here too.

A sheet file holds pages, and each page holds layers stacked over the same grid. A cell shows the text of the topmost visible layer that has it, and edits go to the layer picked for editing, the topmost one until another is picked. The editor shows one page at a time, chosen from the strip of page tabs.

Only the cells inside the viewport have controls. The grid keeps a pool of labels and lines and rebinds them to whatever cells are in view, so a sheet of ten thousand rows costs the same number of controls as one of twenty.

A sheet is one of two types, chosen when it is created. An unfixed sheet is an open grid: it measures to the cells in use plus 100 rows and 26 columns, and past the active cell, so there is always empty room to type into. A fixed sheet has a stored size per page, 25 rows by 25 columns for a new sheet and for every page added to it, and the grid ends there. Both types are opened, edited, saved and referenced the same way.

## File format

```xml
<Sheet Name="Budget">
	<Page Name="Sheet 1">
		<Column At="B" Width="140"/>
		<Row At="4" Height="30"/>
			<Format At="B3" Bold="true" Fill="#C8E6A0" Number="#,##0.00"/>
		<Layer Name="Layer 1">
			<Cell At="A1">Item</Cell>
			<Cell At="B1">12.5</Cell>
		</Layer>
	</Page>
</Sheet>
```

Empty cells are not written. Cells are written row by row, and so are formats, after the rows and columns of the page. A file without `Format` elements loads unchanged. An element the reader does not know is kept and written back unchanged.

A fixed sheet writes `Fixed="true"` on the `Sheet` element and `Rows` and `Columns` on each `Page`; an unfixed sheet writes none of them, so every file from before fixed sheets opens as unfixed. When a fixed sheet is read, a page's size is the larger of its attribute (25 when missing) and the extent its cells use, so a cell is never outside its page. A CSV file is always unfixed.

## Formulas

A cell whose text starts with `=` is a formula. The file keeps the formula text; the grid shows its value, and F2 opens the formula again. A number typed into a cell keeps the text it was typed as, so `1.50` stays `1.50`; a computed number is written with up to fifteen significant digits, so `=0.1+0.2` shows `0.3`.

A formula takes numbers, `+ - * /`, `^`, unary minus and brackets, cell references like `B3`, ranges like `A1:B3`, references into another page of the same file like `Expenses!B3` or `'My page'!A1:A5`, comparisons and the functions listed below. Precedence follows Excel: unary minus binds tighter than `^`, so `=-2^2` is 4, and `^` groups left to right, so `=2^3^2` is 64. A reference reads the cell as shown, the topmost visible layer holding it. An empty cell counts as 0, and a range given to a function skips text and blanks.

The comparison operators are `=`, `<>`, `<`, `>`, `<=` and `>=`. They bind loosest, below `+` and `-`, as in Excel, and give the number 1 or 0 rather than TRUE or FALSE, so `=(A1>0)*(B1<10)` acts as AND and `=(B2>0)+(B3>0)` counts. Numbers compare numerically, text compares without regard to case and sorts after numbers, and an empty cell counts as 0 against a number. The functions, whose names are not case-sensitive, are `SUM`, `MIN`, `MAX`, `AVERAGE`, `ROUND`, `ROUNDUP`, `ROUNDDOWN` and `IF`. `MIN` and `MAX` of no numbers give 0 and `AVERAGE` of no numbers gives `#DIV/0!`. `ROUND` goes half away from zero, `ROUNDUP` away from zero and `ROUNDDOWN` toward zero; the digits argument is optional, defaults to 0 and may be negative, so `=ROUND(1234,-2)` is 1200 and `=ROUND(2.345,2)` is 2.35. `IF(test, then, else)` needs a number as the test, where non-zero is true, and evaluates only the branch it takes, so an error in the other branch does not show; a missing else gives 0. A wrong number of arguments gives `#VALUE!`. There are no string literals, so text can only be compared against another cell, and no `AND`, `OR`, `NOT` or `COUNT`.

Errors are values and pass through whatever reads them: `#DIV/0!` for a division by zero, `#VALUE!` for text in arithmetic or a range outside a function, `#NAME?` for an unknown function or word, `#REF!` for a page that does not exist or a cell past the grid (16,384 columns, 1,048,576 rows), `#NUM!` for a result too large to hold, `#CYCLE!` for a cell that ends up reading itself and everything that reads it, and `#ERROR!` for a formula that does not parse. A note's embedded sheet or `\sheet{}` math that points into a referenced vault that is not on this machine reads `#VAULT!`, while a formula into that vault reads `#REF!`.

A formula can read another sheet file in the vault: `[Budget]Expenses!B3`, or `'[My budget]Sheet 1'!B3` when a name has a space. The page is required. The file part is found the way a `[[link]]` is, by the sheet's name or the end of its path (`[Finance/Budget]`), first match winning. A sheet that is not open is loaded from disk to answer; a file or page that cannot be found reads `#REF!`. Renaming a sheet in the vault browser rewrites every reference to it, in open sheets and in sheets on disk; deleting it turns its readers to `#REF!`. A reference into another vault carries an `@Alias/` prefix (`[@Docs/Budget]Data!A1`): renaming that sheet or one of its pages, or inserting rows or columns in it, rewrites the reference in every open vault that references its vault, and Paste link across vaults writes the `@Alias/` prefix, adding the reference to the target vault when it isn't listed yet.

Copy writes the values, not the formulas. Ctrl+Shift+V pastes values. Ctrl+V pastes a link: one formula per copied cell that reads it, `=B3` from the same page, `=Page!B3` from another page and `=[File]Page!B3` from another file. If the clipboard has changed since the copy, or the copy was a cut, it pastes plainly.

Every loaded sheet is held once by `SheetBook`, so two tabs of the same file share one document and one undo history, and an edit in one sheet reaches every sheet that reads it while both are open. One `SheetCalc` covers all of them: the parsed formula of each formula cell, its value, and the cells each formula reads, recorded both ways so a change finds what reads it. A range adds one edge per cell it covers. Changes made to a sheet file outside Thorium are seen on the next launch or vault switch.

## Formatting and resizing

A cell can be bold, filled with a colour and shown through a number format. Ctrl+B toggles bold the way Excel does: if the active cell is bold the whole selection is unbolded, otherwise the whole selection is bolded. Right-clicking the grid opens a menu with Cut, Copy, Paste and Paste link, then Bold, Fill and Number format; a right press on a cell outside the selection selects it first. Fill offers the same swatches as note highlights, and Number format offers General, Number (`#,##0.00`), Percent (`0.00%`) and Currency (`€#,##0.00`). A change over the selection is one undo step.

A format belongs to the page and is shared by all of its layers, the way column widths and row heights are, so a cell can be formatted while it is empty. Delete and Clear empty a cell and keep its format, and cut and paste move values only, so formats stay where they are. A number format is a .NET custom numeric format string drawn with the invariant culture, and General shows up to fifteen significant digits, switching to E-notation for very large and very small numbers. Only a number goes through the format; text is shown as it is.

When a cell's value is a number, a number is copied: Copy writes the unformatted value, so `12.50%` copies as `0.125` and `€1,234.50` as `1234.5`, and a copy pasted back into a sheet gives numbers. A note's plain-text copy of a sheet link does the same, and `\sheet{…}` in note math stays unformatted because it has to parse.

Dragging the right edge of a column header or the bottom edge of a row header resizes that column or row. The grab zone is four pixels either side of the edge, and the cursor turns into a resize shape over it. The size follows the pointer while dragging, never below 8 pixels, and the drag is recorded as one undo step when it ends.

A sheet follows the document zoom: the grid, the headers and the text in them are laid out at that zoom, the same setting notes use, and Ctrl+wheel over the sheet changes it. Column widths and row heights are stored at 100%, so a resize drag at 200% moves the edge twice as far as it stores.

Not done yet: italic, underline, text colour, alignment, borders and font size in cells; dates, custom number formats and increase or decrease decimals; formats do not travel with copy and paste; the open edit field does not show bold; no autofit on a double-click of an edge, no resizing several selected columns at once and no Esc to cancel a drag.

## Pages and layers

The strip under the grid holds one tab per page, a "+" button that adds a page named "Sheet N" for the first free N, and a "Layers" button at the right end. Pressing a tab shows that page with the selection on A1 and the scroll at the top. Double-clicking a tab renames the page in place, and right-clicking it offers Rename, Delete and Export as CSV. The last page cannot be deleted. A page name cannot be empty, hold `!`, or match another page's name in any letter case. Adding, deleting and renaming a page are undo steps, and the shown page is kept by session restore.

Renaming a page rewrites every reference to it across the vault, because a rename inside Thorium rewrites references: `Page!` in the same sheet, `[File]Page!` in other sheets whether they are open or only on disk, note links such as `file#Page!B3`, and `\sheet{…}` in note math. Undoing the rename runs the same rewrite in reverse, so other files and notes are rewritten again.

The "Layers" button opens a panel above the strip with one row per layer, the top layer first. A dot at the left of a row is filled while the layer is visible and an outline while it is hidden, and clicking it toggles that. The layer being edited is lit, and clicking a layer's name picks it. "Add layer" puts a new layer on top and makes it the edited one, and "Delete layer" deletes the edited layer, except that the last layer stays. Adding, deleting and showing or hiding a layer are undo steps, and every formula is recalculated afterwards.

The grid shows the topmost visible layer's text, while an edit goes into the picked layer, and the open cell field shows that layer's own text. The picked layer belongs to the editor and is not saved. Not done yet: reordering pages, remembering the selection per page, renaming or reordering layers, dimming cells that are not on the edited layer, and a visible sign that an edit went into a hidden layer.

## Fixed sheets and growing

The "New" submenu of the vault menus lists Folder, Doc, Sheet and Planner, and "Sheet" opens a further submenu with "Fixed size" and "Unfixed". The type cannot be changed afterwards. "Fixed size" asks for the page size along with the name, in Rows and Columns boxes that start at 25; a count below 1 or past the grid limit keeps the prompt open.

On a fixed sheet the selection is clamped to the page, and the grid, its lines and its cells stop at the page edge. A strip 20 pixels wide runs along the whole bottom edge and the whole right edge, each showing a "+". The strips sit 4 pixels clear of the grid and of each other, with the palette's rounded corners, and fade from the palette's Field colour at the centre to its Accent colour at the edges, which reads white to blue on the light palette. Left-clicking a strip adds one row or column. Shift+left-click adds the number set under Sheets in the Settings window, 10 by default. Right-clicking a strip opens a small popup with an "Add horizontal" box (columns) and an "Add vertical" box (rows), both starting at 0. The bottom strip focuses "Add vertical" and the right strip "Add horizontal", Tab swaps them, Enter grows the page once by both amounts, and Esc or a click outside cancels. Anything that is not a number counts as 0. Each grow is one undo step, and undoing it shrinks the page again.

Pasting past the edge of a fixed page grows the page to fit, in the same undo step as the paste.

## Inserting rows and columns

Both sheet types can grow upwards and leftwards by inserting. The grid's right-click menu has "Insert rows above" and "Insert columns left", which insert as many rows or columns as the selection spans, before the selection, the way Excel does. The cells, formats and column widths or row heights at and after the insertion move over, and on a fixed sheet the page grows by the same amount.

Every reference to a moved cell is rewritten to follow it: formulas on the same page, formulas on other pages, formulas in other loaded sheets and in sheets only on disk, links to the cells in notes, and `\sheet{…}` in note math. The two ends of a range move independently, so a range that spans the insertion grows and a range below it moves whole. Undo moves everything back.

Not done yet: deleting rows and columns, changing a sheet's type after creation, a size field when creating a sheet. Undoing an insert does not touch a reference another file wrote into the inserted rows after the insert, because that file is not on this sheet's undo stack, so its reference keeps pointing at whichever row slides into place. Two quick clicks on the same spot of the bottom strip do not both grow, because the strip moves down a row after the first; Shift+click or the popup adds several.

## CSV files

A `.csv` file in the vault opens in a sheet tab as one page named after the file, with one layer and no page strip. Ctrl+S writes it back in place: the cell text as written, so a formula stays `=B2*2`, in the delimiter and BOM the file was read with. The delimiter is the most used of comma, semicolon and tab in the first record outside quotes, and a comma when there is no clear winner. Formats, column widths and extra layers cannot be held by a CSV, so they are not saved. A decimal comma in a semicolon file is read as text, not as a number.

Right-clicking a page tab and choosing Export as CSV writes `<Sheet> - <Page>.csv` beside the sheet: the values as shown, numbers unformatted, UTF-8 with a BOM, comma-separated, a CRLF after every record. Exporting again overwrites the earlier file without asking.

A CSV row in the vault browser has its own menu. "Create a sheet from this" writes `<name>.sheet.axml` beside the CSV, keeps the CSV and opens the sheet. "Convert to sheet" does the same and then sends the CSV to the recycle bin and closes its tab. Both start from the loaded copy, so unsaved edits in an open CSV tab come along. The menu also has Rename, Duplicate and Delete.

A CSV can be referenced but reaches nothing outside itself. A formula reads one as `[data.csv]data!A1`: the file part keeps `.csv`, so `data.csv` and a sheet named `data` are different files, and the page is the CSV's file name without the extension. A note links it as `data.csv#data!A1` (`![[data.csv#data!A1]]` in a Markdown note) and a note's formula reads it as `\sheet{data.csv#data!A1}`. Paste link from a CSV tab writes these references into a sheet or a note. Inside a CSV a reference with a file part reads `#REF!`, and Paste link into a CSV from another file pastes the plain values.

Renaming a CSV to another CSV rewrites every reference to it, the file part and the page part (`[data.csv]data!` becomes `[cost.csv]cost!`, and the CSV's own `data!B2` becomes `cost!B2`), because a CSV cannot store its page name and takes it from the file name on every load. "Convert to sheet" points every reference at the new sheet before the CSV goes to the recycle bin (`[data.csv]data!` becomes `[data.sheet.axml]data!`, a note's `data.csv#…` becomes `data.sheet.axml#…`, the page is unchanged), and the converted sheet can then reach other files. "Create a sheet from this" keeps the CSV, so references stay on the CSV.

## Sheet links in notes

A note can show sheet cells live. A link to one cell is drawn inline as that cell's current value, in the font and style of the text around it. A link to a range is drawn as a read-only grid on a line of its own, the way display math is, left-aligned and cut at the column edge. Either is a single object character in the note, like a picture or a formula, so it is selected, deleted, copied and undone like them.

A link is written `Budget.sheet.axml#Data!B1` for a cell and `Budget.sheet.axml#Data!A1:B2` for a range: the sheet's file name with its extension, then `#`, the page, `!` and the address. In a note's XML it is the `Sheet` attribute of a `Run` (`<Run Sheet="Budget.sheet.axml#Data!B1"/>`); in Markdown it is `![[Budget.sheet.axml#Data!B1]]`. Markdown reads that as a link only when the file part ends in `.sheet.axml`, so `![[Budget#…]]` stays a note-heading embed.

The grid of a range shows values and one-pixel rules, with no column or row headers. Its column widths and row heights are the page's, scaled by the note's zoom. Numbers show in the page's number format and right-align when they fit, errors such as `#REF!` or `#DIV/0!` draw in the danger colour, and a link to a sheet that does not exist reads `#REF!`. The link keeps no value of its own: the note asks the sheet each time it measures, and every open note re-measures its links when any sheet is edited, so the values follow the sheet while both are open.

Ctrl+V in a note pastes a link to the cells last copied in a sheet, as one undo step. It only does so while the clipboard still holds that copy and the sheet has a file; otherwise, and in plain-text notes, read-only notes, code blocks and rules, it pastes plainly. Ctrl+Shift+V of a sheet copy in a note stays plain text. Copying a link out of a note writes its current values, one value or tab-separated rows, so another program gets values rather than the reference; pasting it back into a note keeps the live link.

Renaming a sheet in the vault browser rewrites the links to it in the notes on disk and in the notes that are open. Only the link text in each file changes, and a note's unsaved state is left alone, so what is in memory and on disk agree whether the note is later saved or discarded. Undoing past a rename restores the old reference, which then reads `#REF!`.

A formula in a note can read a cell too: `\sheet{Budget.sheet.axml#Data!B1}` inside its TeX typesets the cell's current value, using the same reference text as a link. Before the formula is parsed, each `\sheet{…}` is replaced by the value: a number as math digits, text as `\text{…}` with braces and backslashes dropped, a range as `\text{#VALUE!}` and a missing file, page or cell as `\text{#REF!}`. The laid-out formula is remembered under the replaced text, so a changed cell is simply a new entry and the formula redraws after any sheet edit. This is substitution only: the formula is still typeset, never evaluated. In the formula popup, Ctrl+Shift+V puts `\sheet{…}` for the last copied cell at the caret of the source field, and renaming a sheet rewrites the references in formulas the way it rewrites links. Obsidian does not know `\sheet{…}` and shows it as an unknown command, and copying a formula as plain text writes its source rather than the value.

A linked cell shows its number format but not its bold or fill, and a linked range grid follows a column or row resize only when the drag ends. Not done yet: a range taller than a page does not split across pages, an empty linked cell is invisible inline, clicking a link does nothing, and a formula that reads cells is not evaluated. A file name holding `#` or `]]`, or a page name holding `!`, cannot be linked.

### Recalculating after a change
	for each changed cell
		forget its formula and the edges it had
		if its shown text is a formula, parse it and record the cells it reads
	collect the changed formula cells and everything that reads a changed cell, transitively
	for each collected cell, count how many of the cells it reads are also collected
	queue the cells whose count is zero
	while the queue has a cell
		evaluate it
		for each collected cell that reads it, lower the count, and queue it at zero
	every collected cell still waiting is in or behind a cycle, and shows #CYCLE!

## API summary

| Member | Kind | Summary |
| --- | --- | --- |
| `LoadPath(path)` | public | Loads a sheet file and shows its first page. |
| `Save()` | public | Commits an open edit, then writes the sheet back to its file. |
| `Select(row, column, extend)` | public | Moves the active cell; with extend, keeps the anchor so the selection grows. |
| `Move(rows, columns, extend)` | public | `Select` relative to the active cell. |
| `Enter(back)` / `Tab(back)` | public | Commits an open edit and steps down or right (up or left with back). |
| `BeginEdit(keep)` | public | Opens the field on the active cell, with its text (keep) or empty. |
| `TypeOver(input)` | public | Opens the field empty and types the queued characters into it. |
| `Clear()` | public | Empties the selected cells of the edited layer, as one undo step. |
| `Copy()` / `Cut()` / `Paste(text)` | public | The selection's values as tab-separated rows, out and in. |
| `PasteLink()` | public | Formulas reading the last copied cells, from the active cell; a plain paste if the clipboard changed. |
| `ViewState()` / `RestoreView(view)` | public | The active cell, anchor and scroll, for session restore. |
| `ToggleBold()` | public | Unbolds the selection if the active cell is bold, otherwise bolds it; one undo step. |
| `SetFill(hex)` | public | Fills the selected cells with a colour, or clears the fill with null; one undo step. |
| `SetNumberFormat(format)` | public | Sets the number format of the selected cells, null for General; one undo step. |
| `ResizeBand(column, index, before, after)` | internal | Records a column or row resize as one undo step, null meaning the default size. |
| `scroller` | public | The scroll viewport holding the grid; callers scroll through it. |
| `ShowPage(index)` | public | Shows a page, with the selection on A1 and the scroll at the top. |
| `AddPage()` | public | Adds a page named "Sheet N" for the first free N, as one undo step. |
| `DeletePage(index)` | public | Deletes a page as one undo step; the last page stays. |
| `RenamePage(index, name)` | public | Renames a page and rewrites its references vault-wide, as one undo step; false when the name is refused. |
| `ExportPage(index)` | public | Writes the page as `<Sheet> - <Page>.csv` beside the sheet and returns the path. |
| `EditLayer(layer)` | public | Picks the layer that edits go into. |
| `ToggleLayer(layer)` | public | Shows or hides a layer, as one undo step. |
| `AddLayer()` / `DeleteLayer()` | public | Adds a layer on top and edits it, or deletes the edited layer; the last layer stays. |
| `Grow(rows, columns)` | public | Adds that many rows and columns to a fixed page, as one undo step labelled "Grow page". |
| `Insert(column)` | public | Inserts rows above (or columns left of) the selection, as many as it spans, and shifts every reference vault-wide, as one undo step. |

## Methods

### BeginEdit
	if already editing, stop
	remember the active cell as the cell being edited
	fill the field with that cell's text, or nothing
	give the field the keyboard and select its text
	if keeping the text, put the caret at its end
	lay the grid out again and scroll the cell into view

### Finishing an edit
	if not editing, stop
	stop editing
	write the field's text into the edited cell as one undo step
	after Enter or Tab, give the keyboard back to the grid
	after a click elsewhere, leave the keyboard where the click put it

### Paste
	if editing, let the field take it
	split the text into rows, and each row at its tabs
	drop a trailing empty row
	write every value from the active cell down and right, as one undo step
	select the block that was written

### Paste link in a note
	if a formula popup is open
		if the clipboard text is the last copy made in a sheet
			insert \sheet{reference} into the popup's source field at its caret
		otherwise
			paste plainly into the field
		stop
	find the note editor holding the keyboard
	if the clipboard text is not the last copy made in a sheet, or that sheet has no file
		paste plainly
	otherwise, if the note is plain text or read-only, or the caret is in a code block or a rule
		paste plainly
	otherwise
		build the reference of the copied cells
		insert it at the caret as a sheet link object, as one undo step named "Paste link"

### Changing a format
	remember the format of every cell in the selection
	for each selected cell
		for bold, the opposite of the active cell's bold
		for fill or number format, the chosen value
		a format equal to the default removes the cell's entry
	write the new formats as one undo step
	tell the sheet book the page changed, so the tab goes unsaved and open notes redraw

### Resizing a column or row
	on a press, if the point is within four pixels of a column header's right edge or a row header's bottom edge
		remember that band and its size, and start the drag
	while dragging
		write the pointer's size into the band, no smaller than 8 pixels
		lay the grid out again
	when the drag stops
		record the size before and after as one undo step

### Renaming a page
	if the name is empty, holds !, or matches another page's name ignoring case, refuse
	rename the page
	for each sheet in the vault, loaded or on disk
		rewrite every reference that names the old page of this file
	for each note in the vault, open or on disk
		rewrite every link and \sheet{…} that names the old page of this file
	recalculate every formula
	all of the above is one undo step; undoing it runs the same rewrite with the names swapped

### Exporting a page
	for each row of the page up to its used extent
		take each cell's value as shown, numbers unformatted, empty as nothing
	write the rows as comma-separated records, quoting where needed, a CRLF after each
	write them as UTF-8 with a BOM to "<Sheet> - <Page>.csv" beside the sheet, replacing an earlier export

### Growing a fixed page
	if the document is not fixed, or both amounts are 0, do nothing
	commit an open edit
	read the page's size
	extend the page by the amounts, which only ever grows it
	record the size before and after as one undo step, labelled "Grow page"

### Inserting rows or columns
	count is the number of rows or columns the selection spans
	at is the selection's first row or column
	shift the page
		for each layer
			move every cell at or after at over by count
		move every format, and every column width or row height, at or after at over by count
		add count to the page's size
	for each formula on this sheet
		move the endpoints of every reference to this page at or after at over by count
	for each other sheet, loaded or on disk
		do the same for references that name this sheet's file and page
		save a loaded sheet no tab has open
	for each note in the vault, open or on disk
		move the endpoints of every link and \sheet{…} that names this page
	recalculate every formula
	all of the above is one undo step; undoing it shifts by minus count, first dropping the count bands at at, and rewrites the references back

### Arrange
	lay out the viewport and the grid
	if a restored view is waiting
		scroll to its offsets
	otherwise, if the active cell moved
		scroll it into view, keeping it clear of the pinned headers
	if the scroll changed, lay out the viewport once more

## Keys

`Sheet.*` actions sit on the same keys as the `Text.*` ones and do nothing unless a sheet has the keyboard. Arrows move (Shift extends), Enter and Tab commit and step (Shift reverses), F2 edits, Delete and Backspace clear, typing replaces the cell, Ctrl+Z / Ctrl+Y or Ctrl+Shift+Z undo and redo, Ctrl+S saves, Ctrl+A selects the used range, Ctrl+B toggles bold, Ctrl+Shift+V pastes a link. Esc and the arrows inside an open cell belong to the field. `Sheet.PasteLink` is the one exception to doing nothing without a sheet: outside one it runs Paste link in the note (a plain paste when no link applies), because its Ctrl+Shift+V bind sits ahead of Ctrl+V and would otherwise take that key from notes. "Insert rows above" and "Insert columns left" are entries of the grid's right-click menu (`Sheet.InsertRowsAbove`, `Sheet.InsertColumnsLeft`). While the grow popup is open, Tab and Shift+Tab swap its two boxes.

## Encryption
A sheet can be encrypted with a password; while locked, formulas and links into it show `#LOCKED!`, and a rename that must rewrite references prompts for its password. See [[ENCRYPTION]].

## Related
- [[Text Box]] — the field a cell is edited in
- [[Label]] — the text of each visible cell
