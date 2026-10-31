# Decision — a note is any of .axml, .md, .txt, read into an in-memory <Document> tree

**Date:** 2026-09-17
**Status:** LANDED. Solution builds clean. `Thorium` boots, and `Text.Indent`/`Text.Outdent` resolve at
`InputHandler.LoadInputs`. The Markdown and plain-text readers and writers were checked in a scratch
harness that compiles `NoteFormats.cs` alone: a 41-line fixture round-trips byte-identical, and
editor-shaped trees survive tree → md → tree. **NOT GUI-verified** — no `.md` note has been opened,
edited or saved in Thorium, no checkbox clicked, no Tab pressed.
**Scope:** `ArctisAurora.Core.UI` — `RichTextDocument`, `DocumentXml`, new `MarkdownFormat` and
`PlainTextFormat` (`NoteFormats.cs`), `BlockControl`, `ListKind`, `BlockSnapshot`, `BlockStateEdit`,
`DocumentControl`, `DocumentEditorControl`, `DocumentLayout`, `TextInputActions`;
`Thorium.Editor.CustomControls.VaultBrowserControl`; Thorium `InputMap.inputs.axml`

Supersedes the "Note format: engine XML (not markdown, not JSON)" line in
[../Context/thorium-editor-architecture.md](../Context/thorium-editor-architecture.md).

## What changed
- `RichTextDocument.Load(path)` / `Save(path)` switch on the file kind from `FileTypes` ([[axml-file-types]]), not the extension. `.md` and `.txt` go through
  `MarkdownFormat` / `PlainTextFormat` to an `XElement`; `.axml` stays `DocumentXml.Load` / `Save`.
  `RichTextDocument.Opens(path)` says which files open (`extensions` was removed). `ParseXML` was renamed `Load`.
- `DocumentXml` split: `Parse(XElement)` builds the blocks, `ToXml(document)` builds the tree. `Save`
  is `ToXml` + the `schemaLocation` stamp + write, and stays `.axml`-only.
- The formats never touch controls: `Read(text, name) → XElement`, `Write(XElement) → string`. The
  file name becomes `Name`, so a `.md`/`.txt` note never asks to be named.
- Vault: `Accepts` asks `FileTypes`; new notes and stickies take `ThoriumSettings.newNotes.Extension`
  (`NewNotesSetting`, `<Thorium><NewNotes Format="Axml|Markdown|PlainText"/>`, default `Axml` since
  2026-10-23 — was `.md`, which lost what Markdown cannot hold, e.g. `SortedTask`; the XML workspace's + stays `.axml`); duplicate and rename keep the source
  extension (`FreePath(folder, name, extension)`); `WriteName` only touches `.axml`.
- Lists: `BlockControl` gained `listKind` (`None`/`Bullet`/`Task`/`SortedTask`), `listLevel`, `isChecked`, carried
  by `SplitAt` (kind + level, unchecked), `SliceSnapshot`/`Restore`, and the `Block` attributes
  `List`/`Level`/`Checked`. `ApplyLayout` sets `padding.left = (level + 1) × DocumentLayout.listIndent`
  (24) and syncs a marker child: an Ink 6×6 dot, or a `CheckBoxControl` whose `onChanged` calls
  `DocumentEditorControl.SetChecked`.
- Editing: `BlockStateEdit` (before/after snapshots through `RestoreBlocks`) records every list change.
  Enter on an empty item and Backspace at an item's start clear the list (`ClearListAtCaret`);
  typing `- ` at a block start makes a bullet and `[ ] `/`[x] ` at a bullet's start makes a task
  (`TypeMarkdownPrefix`, same undo step as the keystroke); `Text.Indent`/`Text.Outdent` on Tab/Shift+Tab
  (`ShiftListLevel`), never deeper than one past the item above.

## Markdown subset

| Source | Tree |
|---|---|
| `#`…`######` + space | `StylingType=Heading1..6` |
| `> ` | `Quote` |
| fenced lines | `Code` blocks, the info string as `Language` (since 2026-10-01); a language change closes and reopens the fence |
| `---` / `***` / `___` alone on a line (since 2026-10-01) | `StylingType=Rule`, no text; written `---`, or `***` as line 0 |
| `- ` / `- [ ] ` / `- [x] ` (indent → level) | `List=Bullet/Task`, `Level`, `Checked` |
| `**b**` `*i*` `_i_` `~~s~~` `` `c` `` | `Bold` / `Italic` / `Strikethrough` runs, `StylingType=Code` run |
| `---`/`+++` block at line 0 (since 2026-09-27) | `Frontmatter` attribute + owned keys — see [[note-properties]] |
| `1.` / `a.` / `i.` numbered, lettered and roman lists | `List` with marker — see [[list-markers]] |
| pipe tables (header row, `---` separator with `:` alignment) (since 2026-11-01) | `<Table>`; see "Pipe tables" below |
| `[[t]]`, `[[t\|alias]]`, `[text](url)` (since 2026-11-01; not after `!`) | link runs, `Link` attribute — see [[note-links]] |
| everything else (HTML, `![[note]]`) | literal text |

## Pipe tables

- Reading: `MarkdownFormat.Read` is an indexed loop so `ReadTable` can look ahead for the separator row (`tableSeparator`). Cells via `TableCells`, alignment via `TableAlignments` (separator colons → each cell block's `Align`). `<br>` (`cellBreak`) splits a cell into blocks; `\|` is unescaped before the inline parse, as GFM does.
- Writing: `TableLines`; row 0 is the header, alignment is written from row 0's blocks; a multi-block cell is joined by `<br>`. `escapable` gained `|`; `BlockLine` escapes a line starting with `|`.
- Column widths split an A4 page's text width evenly (`tableWidth` = (210 − 2×25.4 mm) × `PageLayout.PxPerMm`, via `TextWidth(PageLayout)`). Rejected: `DocumentXml`'s 150 px default (long prose columns wrap heavily); a fit-to-content estimate.
- The header row stays plain text. Rejected: bold header runs — the writer would strip deliberate bold.
- Column spans do not survive Markdown (written as the cell plus empty cells).

## Why these choices

**Formats produce the XML tree, not blocks (user: fork A).**
`DocumentXml.Parse` stays the only code that builds `BlockControl`s, so a format is text ↔ `XElement`
and testable without the UI. Rejected: formats building `RichTextDocument` directly — one step
cheaper per open, and a second place constructing controls.

**A switch on the extension, not an `IDocumentFormat` interface (user asked why the interface).**
Three fixed formats with no outside registrant; a fourth is one more `case`. The interface and a
registry array were written into the first plan and dropped before any code.

**One block per source line (user).**
A paragraph-per-block model would join soft breaks and rewrite the file on first save. Blank lines
become empty blocks.

**Colour, gradient, font and size are not written to `.md` (user).**
Markdown is read as simple Markdown and the palette colours it. Rejected: inline `<span style>`.

**A custom parser, not Markdig (user).**
No new NuGet dependency, and Markdig cannot write Markdown back from this model anyway.

**The writer writes plain, and escapes only when plain does not read back the same.**
`WriteInline` composes the line unescaped, re-parses it, and falls back to escaping `` \ ` * _ ~ `` only
on a mismatch. Escaping unconditionally would rewrite `snake_case` and `2 * 3` in every file on its
first save. A line whose text would read as structure (`# `, `- `, `> `, a fence) gets one `\` before
its first non-blank.

**Nesting is relative to the items above.**
Indent width (tab = 4) against a stack of open item widths gives the level, so 2- and 4-space files both
nest. Written back as one tab per level.

**The checkbox is a real `CheckBoxControl` child of the block, not a glyph.**
The atlas has no ballot box, and a child is hit-tested before the block, so a press on it never reaches
`GlyphPressed`. `TextRunControl.Measure` ignores padding, so `BlockControl` overrides `Measure` to wrap
inside the indent rather than changing padding semantics for every label.

**List changes are snapshot records.** A tick, a nesting change and a cleared marker are block state
with no text involved; before/after snapshots through the existing `RestoreBlocks` are the whole
inverse, the same argument `StyleRangeEdit` makes.

## Known gaps
- Normalized on first save: `_i_` → `*i*`, `[X]` → `[x]`, space indents → tabs, fence info strings
  dropped, `Comment` blocks written as text, line endings LF.
- A line that needs any escape gets all of them.
- Adjacent marker runs can be ambiguous (`*a***b**`); the writer's re-read check does not help there.
- A level jump with no parent (level 2 under level 0) reads back as level 0.
- ~~Numbered lists are text~~ — `N.`/`N)` read as `Decimal` items and numeric markers write `N. ` since
  2026-09-29 ([[list-markers]]); colour, highlight and underline write as Obsidian HTML/`==`
  ([[text-decorations-and-colour]]). Per-role palette colours for headings/code/quotes do not exist.
- A double-click on a checkbox bubbles a 2-tap to `DocumentControl` and selects a word.
- Only Thorium's `InputMap.inputs.axml` binds Tab — it is the only host with `Text.*` binds.

Related: [[xml-editor]], [[document-undo]], [[text-styling-types]], [[xml-save-skips-defaults]], [[vault-browser-and-shell]], [[ui-palettes]], [[note-properties]]
