# Decision — a note link is a run attribute, followed with Ctrl+click and rewritten on rename

**Date:** 2026-11-01
**Scope:** `ArctisAurora.Core.UI.Text` — `StyleSpan`, `TextRunData`, `TextRunControl`, `TextInputActions`; `ArctisAurora.Core.UI.Document` — `Run`, `DocumentControl`, `DocumentEditorControl`, `BlockControl`, `RichTextDocument`; `ArctisAurora.Core.UI.Notes` — `MarkdownFormat`, `NoteBlock`, `LinkIndex`, `LinkSuggest`, `NoteLinks`, `NoteHeadings`; `ArctisAurora.Core.UI.Sheets.SheetLinks`; `Thorium.Editor.CustomControls.VaultBrowserControl`

Stage 2 of the docs-port plan ([[note-file-formats]] gains tables in stage 1) — see `Context/docs-port-plan.md`.

## What changed
- `StyleSpan.link` (target as written) → `Run.link` → XML `<Run Text Link>`. `TextRunControl.RunStyle.link`: a link span without `colorHex` is drawn in `Palettes.Surface(palette, PaletteRole.Accent)` and underlined.
- `NoteBlock.AppendRun` copies link; `TextRunData.Runs()` and `SameStyle` carry it; `InsertText` puts text typed at either edge of a link span outside it (`IsAtSpanEdge`); new `LinkOf`, `InsertLink`, `SpanOf`.
- Markdown: `MarkdownFormat` regexes `wikiLink`, `webLink`; `LinkAt`, `ParseInline` read `[[t]]`, `[[t|alias]]`, `[text](url)` into link runs (not after `!`, so `![[note]]` stays text); `Compose` writes them via `LinkMarkdown`; public `IsWebAddress`; `Flatten` style key includes the link.
- `LinkIndex.FromXml`: a run's `Link` adds a `LinkKind.Link` edge (web addresses and same-note `#…` add none).
- Follow: `InputModifier.Follow`, bound to LeftControl/RightControl in Thorium's `InputMap.inputs.axml` only. `BlockControl.LinkAt`, hand cursor in `OnPointerMove`/`OnPointerExit`. `DocumentControl.GlyphPressed` follows when Follow is held; public `FollowLink(string)` calls `NotePropertiesControl.openLink` with `[[target]]` or the URL. `VaultBrowserControl.OpenLink` handles `#heading` (scrolls via private `ShowHeading`) and an empty name = same note.
- Typed `]]`: `TypeChar` calls `TypeLinkClose` (DocumentControlLists.cs) → `ReplaceWithLink`, one `BlockStateEdit`.
- Autocomplete `LinkSuggest`: host hook `linkNames` (wired in `Thorium.Thorium`); `Typed`, `CaretMoved`, `Move`, `Accept`, `Dismiss`. Up to 8 rows, prefix matches then contains, case-insensitive; a fixed set of `ButtonControl`s in a `ContextMenuContent`, highlight via role SubField. After `#` it lists the target's headings (`NoteHeadings.Of(path)` — live session doc if open, else the file; encrypted → none). `TextInputActions`: `Text.CaretUp/CaretDown` move the highlight, `Text.NewBlock`/`Text.Indent` accept, `Text.Cancel` dismisses — each only while a list is open.
- Rename: `NoteLinks` (host hook `vaultNotes`): `Renamed(oldPath,newPath,notes)`, `HeadingRenamed(notePath,old,new)`. `VaultBrowserControl.RenameNote` calls `Renamed` for non-sheet files; `DocumentControl.LeftHeading()` (fields `headingBlock`/`headingText`, hooked in `SetCaret` on block change, `Blur`, `DocumentEditorControl.ReleaseSession`) calls `HeadingRenamed`. `RichTextDocument.RenameNoteLinks(Func<string,string?>)`, `NoteSessions.Find(path)`.
- `SheetLinks.RewriteFile` now internal, takes `(path, prompt, Func<string,bool,string?> rewrite)`; byte/BOM handling split from the new private `RenamedText`. Sheet behaviour unchanged.

## Why these choices

**A link is a span attribute, not `[[…]]` text.**
It is clickable, indexable and renameable. `[[a#h]]` shows its target verbatim as `a#h` so it round-trips exactly; rejected Obsidian's `a > h` display.

**Follow is Ctrl+click through a named modifier role, not plain click (Word-style).**
Plain click would make placing a caret inside a link impossible.

**Typing at either edge of a link stays outside it; inside extends it.**

**Autocomplete reads a target's headings once, when `#` is typed, and filters in memory afterwards.**
Keys go through the existing text actions because ContextMenus have no keyboard navigation, so the caret keeps focus.

**A heading rename fires when the caret leaves the heading (or on blur / session close), not on save.**
Thorium autosaves (`NoteFileSync`), so a save-time diff would rewrite the vault for each partial word. Skipped when another heading still has the old text, or the heading was deleted. Links in the note itself are rewritten directly on its open document, so it works without a tab. Forks chosen: F8b (heading completion), F9c (heading-text renames).

**Renames reuse the sheet rewrite machinery.**
`SheetLinks.RewriteFile` is byte/BOM-preserving and prompts for the key of an encrypted file. Locked notes prompt on both file and heading renames, like sheets (user chose "prompt like sheets" over silently skipping).

## Table of contents (docs-port stage 3, 2026-11-01)

Fork F2a implemented: a note holds a live `<Toc/>` (optional `Depth="1..6"`), one clickable row per heading, indented by level, rebuilt as headings change. Pressing a row puts the caret at the start of that heading and scrolls to it.

**What changed**
- `Notes.NoteToc : NoteNode` (in NoteModel.cs), field `depth` (default 6 = all levels). `Notes.TocControl : StackPanelControl` (own file): `note`, `Rebuild(List<NoteBlock>)` (false when the heading list is unchanged), `Entries`, `Show(heading)`; `contextMenu = "toc"`, `stopsContextMenu = true`.
- `DocumentXml.Parse` reads `Toc` (Depth clamped 1..6); `WriteNode` writes `<Toc/>`, or `<Toc Depth="n"/>` when depth < 6.
- `RichTextDocument.FlatCount`: `NoteToc => 0`, like `NoteSealed`, so caret, selection and addresses never see it. `NoteSegments`: `"Toc"` joins the `Block`/`Table` single-entry case.
- `DocumentControl` (DocumentControlTables.cs `ShowNodes`, `NodeOf`) builds and recognises `TocControl`. New private `RefreshTocs(NoteChange)` (DocumentControlEditing.cs), called from `OnNoteChanged` after the switch, on Text/Kind/Inserted/Removed/Table/Nodes; a single-block Text change returns early unless that block is a heading.
- `DocumentEditorControl`: view build branch for `NoteToc`; `InsertToc()`, `DeleteToc(NoteToc)`, private `PutNodes(label, index, before, after)` (one `NodesEdit` undo step, header unchanged). `PdfExport.Record`: `NoteToc → TocControl` branch.
- `TextInputActions`: `Toc.Insert`, `Toc.Delete` (walks up from `ContextMenus.target` to the `TocControl` and its `DocumentEditorControl`).
- Data: `Toc.menu.axml` (one entry, "Delete table of contents"), registered in `ThoriumAssets.assets.axml` as ContextMenuAsset `toc`; `Note.menu.axml` gains "Insert table of contents" (`When="outside-table"`).
- Tests (TextInputTests.cs): `TextInput.TocXmlRoundTrip`, `TocRebuilds`, `TocFollow`, `TocInsertUndo`, `TocInsertRefusedInMarkdown`.

**A ToC is a note-level node with its own view (the `NoteSealed`/`SealedControl` pattern), not text.**
Rejected: generating `BlockControl`s with real `<Run Link>` runs. Selectable and in document fonts, but a press goes through `DocumentControl.GlyphPressed` → `PressAt`, which would put the caret in a block the model does not hold, needing guards across the caret code.

**Rows follow through `DocumentEditorControl.ShowHeading`, the path `[[#heading]]` links use.** No link text is stored.

**Depth is an optional attribute; absent = all six levels.** User chose 2b over listing all headings only. All headings are listed, including any above the ToC.

**Refused in `.md`/`.txt` notes and source view, as tables are.** The Markdown writer skips unknown elements and under F1a only `.axml` holds ToCs.

**Delete is a right-click menu named in XML (`toc`), with `stopsContextMenu` so note entries do not follow.**
Rejected: a menu built in code and opened on right press, because `UIEngine` always calls `ContextMenus.OpenOn(hovering)` on right release, which closes any open menu. Rejected: letting the Note menu's entries follow (user chose the own menu).

**Rebuild cost:** typing in a non-heading paragraph does no ToC work; otherwise one walk of the flat blocks per ToC, rows rebuilt only when the (level, text) list changed.

**Verified:** test-verified — builds clean; `--test=TextInput` 113 passed, 1 failed (Boot, pre-existing); full `--test` 513 passed, 2 failed (Boot pre-existing; TocInsertUndo failed on the unregistered menu, passes after the fix). NOT GUI-verified: clicking a row by mouse, the right-click menu's look, the ToC in a PDF export.

## Known gaps
- ToC: no ribbon button; rows are not links in the exported PDF; a ToC is dropped when an .axml note is converted to .md; duplicate heading texts go to the first heading (`ShowHeading`).
- Undoing a heading edit does not undo the link rewrites (leaving the heading again with the old text renames back only if the block survived).
- Folder moves do not rewrite path-qualified links; `[[x#^block]]` refs only open the note.
- `[text](note.md)` relative links write back as `[[note.md|text]]`.
- `Follow` is bound only in Thorium's input map.
- Test-only public members: `LinkSuggest.IsOpen/ShownCount/ShownAt/headingLoads`, `DocumentControl.FollowLink`.
- NOT GUI-verified: Ctrl+click, the hand cursor, the popup's look and position, scrolling to a heading after opening, the locked-note key prompt.

Related: [[note-file-formats]], [[link-graph]], [[document-tables]]
