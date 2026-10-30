---
date: 2026-10-02
Status: Current
tags:
  - d_text
cssclasses:
  - Aurora.css
Linker:
  - "[[Arctis Aurora]]"
System:
Class:
  - "[[Formula Popup]]"
Parent Class:
Interfaces:
Used by:
  - DocumentEditorControl
Type:
  - Internal
  - Sealed
Attributes:
Namespace: ArctisAurora.Core.UI
SourceFile: AuroraEngine/Core/UI/Tex/FormulaPopup.cs
VerifiedAgainst: 2026-10-02
---
## Description

Edits one formula's TeX source in a one-line field that opens in a popup just under the formula. Ctrl+M inserts an inline formula and Ctrl+Shift+M a display one; the note's right-click menu has the same two plus Edit formula. A click on a formula selects it the way a click selects a picture, and a double-click or Enter on the selected formula opens the popup.

The note itself is the preview. Every change in the field, including the field's own undo, rewrites the formula in place and the paragraph reflows around it, without touching the note's undo history. Only finishing the edit records anything, and it records a single step: "Insert formula" for a new one, "Edit formula" for an existing one whose source changed.

Enter or a click anywhere else commits. Esc puts the old source back, and so does the popup being closed from outside, such as by switching to another application. A new formula left empty, whether committed or cancelled, disappears without a trace in the history. A formula cannot be placed in a read-only note, a plain text note, a code block or a horizontal rule.

While the popup is open, Ctrl+V pastes into the field: if the clipboard still holds the cells last copied in a sheet, it inserts `\sheet{reference}` at the field's caret, so the note shows the cell's value at once; otherwise it pastes plainly. See [[Sheet Editor]].

## API summary

| Member | Kind | Summary |
| --- | --- | --- |
| `Open(DocumentEditorControl editor, DocumentControl document, DocumentAddress at, bool placed)` | static | Opens the field on the formula at `at`; `placed` marks one that was just inserted. |
| `PasteLink()` | static | Inserts a `\sheet{reference}` for the last copied sheet cells into the open popup's field; returns false when no popup is being edited or the clipboard is not the last sheet copy. |

## Methods

### `Open` *(static)*
	remember the formula's current source
	fill the field with it, all of it selected
	on every change in the field, write the field's text into the formula
	on Enter or a click away, finish
	on Esc, cancel
	open a popup holding the field under the formula's drawn box, cancelling if anything else closes it
	give the field the keyboard

### `PasteLink` *(static)*
	if no popup is open, return false
	if the clipboard text is the last copy made in a sheet
		build the reference of the copied cells
		paste \sheet{reference} into the field, which rewrites the formula like any other change
	otherwise
		paste the clipboard text plainly into the field
	return true

### Finish
	if already finished, stop
	close the popup
	if the formula was just inserted
		if its source is empty, take it out of the note
		otherwise record it as one inserted formula
	otherwise, if its source changed
		record one edit from the old source to the new
	after Enter, put the keyboard back on the note's caret

### Cancel
	if already finished, stop
	close the popup
	if the formula was just inserted, take it out of the note
	otherwise put the old source back
	put the keyboard back on the note's caret

## Related
- [[Math Layout]] — lays out what the field holds
- [[Math Parser]] — reads the source
