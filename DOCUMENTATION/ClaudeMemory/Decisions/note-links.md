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

## Known gaps
- Undoing a heading edit does not undo the link rewrites (leaving the heading again with the old text renames back only if the block survived).
- Folder moves do not rewrite path-qualified links; `[[x#^block]]` refs only open the note.
- `[text](note.md)` relative links write back as `[[note.md|text]]`.
- `Follow` is bound only in Thorium's input map.
- Test-only public members: `LinkSuggest.IsOpen/ShownCount/ShownAt/headingLoads`, `DocumentControl.FollowLink`.
- NOT GUI-verified: Ctrl+click, the hand cursor, the popup's look and position, scrolling to a heading after opening, the locked-note key prompt.

Related: [[note-file-formats]], [[link-graph]], [[document-tables]]
