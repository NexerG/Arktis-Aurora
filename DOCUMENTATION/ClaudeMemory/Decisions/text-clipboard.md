# Decision — the clipboard is plain OS text plus our own formatted copy, reached through a control interface

**Date:** 2026-09-29
**Status:** LANDED. Test-verified (`TextInput.PasteRoundTrip`, `PastePlainLines`, `UndoReselects`,
`FieldClipboardAndUndo`, `WordMoves`). **Real OS clipboard NOT GUI-verified** — under `--test` the
clipboard stays in process, so Notepad ↔ Thorium has not been exercised.
**Scope:** `ArctisAurora.Core.Filing` — `ClipboardText`; `ArctisAurora.Core.UI` — `IClipboardTarget`,
`TextInputActions` (`Copy`/`Cut`/`Paste`, `WordEdge`, `CharClass`), `DocumentControl`,
`DocumentEditorControl`, `TextBoxControl`, `DeleteRangeEdit`, `InsertRangeEdit`; `ArctisAurora.EngineWork` —
`InputModifier.Word`

## What changed
- `ClipboardText.Get`/`Set` — GLFW's clipboard string against `Engine.primary`; under `TestRunner.active` a
  static string instead
- `IClipboardTarget { Copy; Cut; Paste(string) }`. `Text.Copy`/`Text.Cut`/`Text.Paste` walk up from
  `UIEngine.activeControl` to the first target that returns true. Implemented by `DocumentEditorControl` and
  `TextBoxControl` (false when not editing, so the walk passes it)
- `DocumentControl.CopySelection` writes plain text (blocks joined by `Environment.NewLine`) and keeps
  `copiedText` + `copiedFragment` statically. `PasteText` uses the fragment when the clipboard text still equals
  `copiedText`, else `FragmentFromText`
- Paste is `Insert` → `InsertRangeEdit` (undo `DeleteBetween`, redo `InsertBetween` = `InsertFragment` +
  caret to end). `InsertFragment` now has a forward caller
- `ForDestination`: the last block of a multi-block fragment takes the destination block's styling/list/check
- Plain text: `\r\n`/`\r` → paragraph breaks, tab → space, other control chars dropped; every line takes the
  caret's `StyleAt` span and the caret block's styling/list. A field flattens breaks and tabs to spaces
- `DeleteRangeEdit` records `anchor` + `caret`; undo re-selects. `DeleteSelection(restoreSelection: false)` —
  used by Backspace/Delete's one-character selection — records the anchor as both, so undo puts only the caret
  back where it was (fixes the caret landing before a restored Backspace character)
- `TextBoxControl` has an `UndoStack` of `FieldEdit` (text/anchor/cursor before and after); cleared on
  `Focus`/`Commit`/`Cancel`. `Text.Undo`/`Redo` fall back to the field
- Word moves: `InputModifier.Word` turns Left/Right into `CaretMove.WordLeft/WordRight`, Home/End into
  `DocumentStart/DocumentEnd`, Backspace/Delete into word deletes. Boundaries are `TextInputActions.WordEdge`
  over the `CharClass` rule `SelectWord` already used (moved out of `DocumentControl`). A word move at a block
  edge steps into the neighbour
- Thorium binds `Word` to both Ctrls, Ctrl+C/X/V, and Cut/Copy/Paste in the edit menus. The Ctrl+Backspace →
  `ExitApplication` bind was **removed** (user, 2026-09-29) — it shadowed plain Backspace

## Why these choices

**An interface, not another branch in `TextInputActions`.** Undo resolves "editor, else field" by type; the
clipboard was asked for engine-wide, and a walk to an interface lets file rows or the console output opt in
without the actions learning their type.

**Plain text on the OS clipboard, formatting kept in process.** A Win32 custom format would carry formatting
between Thorium instances but is Windows-only; plain-only loses bold on a copy within one note. Matching the
clipboard text against our last copy gets formatting everywhere inside one process — tabs, splits, torn-off
windows — for no platform code. The cost: identical text copied from another app pastes with our formatting.

**GLFW, not `ClipboardImage`'s Win32 calls.** GLFW is already loaded and cross-platform for strings; pictures
stay Win32 because GLFW has no image clipboard.

**`ForDestination` rather than changing `InsertFragment`.** `InsertFragment` is `DeleteRange`'s inverse and
must keep rebuilding the tail block with its own state; a copied fragment's last block has no paragraph end, so
Word's rule (it takes the destination's) is applied to a copy of the fragment before insert.

**Copy flattens across a table edge; cut refuses.** `BlockSnapshot` has no table shape, so cells copy as
paragraphs. Cut follows `DeleteSelection`'s `OneContainer` refusal.

## Known gaps
- Real OS clipboard untested (above). Ctrl+C/X/V name `LeftControl` but take either Ctrl since
  [[named-input-modifiers]] decision 6
- Ctrl+V is `Sheet.PasteLink`, whose fallback `Text.PasteLink` pastes a live sheet link into a note, else a plain
  paste; Ctrl+Shift+V is `Text.Paste` — see [[sheets]] S2b3
- A large paste is bounded by the glyph ceiling (~50k `GlyphControl`s); nothing refuses it
- `Text.Paste` is text-only; `note-images-plan` stage 3 adds the picture branch ahead of it
- Only Thorium binds text keys; Carbon/AuroraEditor get the actions but no keys
- An armed style (`pending`) is not spent by a paste

Related: [[document-undo]], [[document-selection]], [[named-input-modifiers]], [[text-drag-and-drop]], [[note-images]]
