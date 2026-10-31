# Decision — heading folds are a flag on the model, hidden blocks are skipped rather than zero-height

**Date:** 2026-11-01
**Scope:** `ArctisAurora.Core.UI.Notes` — `NoteBlock`; `ArctisAurora.Core.UI.Document` — `DocumentControl` (region `folds`), `DocumentControlEditing`, `DocumentControlPages`, `DocumentXml`, `DocumentEditorControl`; `ArctisAurora.Core.UI.Containers.Workspace` — `SessionTab`; `ArctisAurora.Tests` — `NoteTests`

## What changed
- Every top-level heading with content under it has a chevron in the left page margin (down open, right folded); a press folds or opens its section. Works in .axml and .md notes.
- A section is everything after the heading up to the next heading of the same or higher level (tables, ToCs and sealed blocks included). Folded blocks are out of the flow, the draw and hit-test.
- `NoteBlock.folded` (bool): not copied by `SplitAt`, `SliceSnapshot`/`Restore`, `TakeKind` or `BlockSnapshot`.
- `DocumentXml` reads/writes `<Block Folded="true">`, written only on headings.
- `DocumentControl` region `folds`:
  - `ToggleFold(BlockControl heading)` (public; folding moves a caret inside the section onto the heading's end)
  - `FoldsChanged()` (internal; sets `foldsDirty` + InvalidateLayout)
  - private `ApplyFolds()` (from `MeasureCore` when `foldsDirty`, like `listsDirty`/`RenumberLists`; opens any fold holding the caret, then `Control.Hide()`/`Show()` per top-level node, clears `folded` on a block no longer a heading, assigns chevrons)
  - `ShowChevron`, `ArrangeChevrons(textX)` (from `ArrangeCore`), `UnfoldAround(BlockControl)`, `SplitAfterFold()`, `SectionEnd(int)`
  - `FoldEdited()` (marks the session dirty only for an .axml note, `FileKind.Note`)
  - internal `IsFoldedAway(Control)` (walks parents for `hidden`), `TopIndex(Control)`, static `HeadingLevel(Entity)` (0 for non-headings and inserts)
  - fields `chevrons`, `chevronSize` 14, `chevronGap` 6 (design px x zoom), `foldsDirty`
  - nested public sealed `FoldChevron : IconControl` (press -> `ToggleFold`)
- Hidden nodes skipped in `MeasureCore` block loop, `ArrangeCore`, `DocumentControlPages.Paginate` (before anything, so `blockControls`/`blockTops` exclude them), `CaretAtPoint`, `AdjacentBlock` (steps over), `ArrangeSelection` (no highlight).
- `SetCaret` on a folded-away block opens its folds (covers ToC clicks / `ShowHeading`, links, undo).
- `CollectChildren` collects chevrons only when `UIEngine.recorder == null` (kept out of PDF export).
- `OnNoteChanged` calls `FoldsChanged()` on Kind/Inserted/Removed/Table/Nodes changes.
- `SplitBlock` on a folded heading calls `SplitAfterFold()` first.
- `SessionTab.folded` (string, XML `Folded`): comma-separated flat block indices of a Markdown note's folded headings; `DocumentEditorControl.ViewState`/`RestoreView` write/read it only for `FileKind.Markdown`.
- Test: `NoteTests.HeadingFolds` (`Notes.HeadingFolds`) in `Notes.tests.axml`.

## Why these choices

**Fold state lives on the model and in the .axml file; a .md note keeps it in the tab session.**
Markdown cannot hold it. Rejected: session-only for both formats (user chose in-file for .axml). Toggling is not undoable; in .axml it marks the note dirty (autosaves).

**A fold is semantic: a flag on the heading, the section derived from heading levels.**
Not a stored block range (rejected, different model). Consequence: anything after the section that is not a same-or-higher heading belongs to the fold.

**The caret never sits in hidden text.**
Any caret placement into a fold opens the folds above it.

**Chevrons are always visible on every heading with a section.**
User choice; rejected hover-only like Obsidian.

**Enter at the end of a folded heading inserts an empty heading of the same level after the section.**
`SplitBlockAt` at the section's last block's end, then `SetBlockList` restyles it (same shape as `InsertRule`, two undo steps); caret there, fold stays closed. Rejected: a plain-text line after the section (it would belong to the fold and be hidden, immediately reopening it); unfold-and-split. Enter mid-heading, or when the section ends in a table/ToC/sealed block, opens the fold and splits normally. Empty section: plain split.

**Hidden blocks use `Control.Hide()` and are skipped by pagination, not given zero height.**
`blockTops`/`blockControls` then simply exclude them, and `Hide()` already takes the subtree out of the draw and the hit-test. Rejected: zero height in `Paginate`, which keeps the indices aligned but still measures folded blocks and leaves the draw and hit-test to exclude them separately.

**Only top-level headings fold; headings in table cells do not.**
LaTeX preview headings get chevrons too (user chose to keep them). PDF export is unaffected: it only exports TeX-lowered trees (never `Folded`), and chevrons are not collected while recording.

## Known gaps
- No fold keybind, no fold-all, no animation.
- Headings in table cells do not fold.
- .md folds sit on the shared note model, so two tabs of the same .md show the same folds.
- Footnote/float inserts anchored inside a folded section are not hidden.
- Test-verified (`_Build/test.sh Notes`, `Notes.HeadingFolds` passes); suite 9 passed, 1 failed, the failure being the Boot check on a pre-existing `[Assets] failed to load the default sampler asset` error. **NOT GUI-verified**: chevron placement/look and click; .md session restore not exercised by a test.

Related: [[note-links]], [[note-model]]
