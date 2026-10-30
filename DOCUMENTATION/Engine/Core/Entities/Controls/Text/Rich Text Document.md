---
Status: Current
tags:
  - Engine
  - d_UI
  - d_Data
  - d_XML
  - d_Filing
Class:
  - "[[RichTextDocument]]"
Type:
  - Public
---
## Description
The note document model for the Thorium (Obsidian-style) editor. It is the source of truth for a note. On disk a note is `.axml`, `.md` or `.txt`; whichever it is, it is read into the engine's `<Document>` XML tree in memory and built from that — see [[#File formats]].

The model / view split it was designed around did not ship at first: blocks and runs were themselves `VulkanControl`s, so the model *was* the control tree and editing mutated it directly. That was the P0 decision — blocks are controls so the engine's UI layout lays a document out for free — and everything downstream follows from it, including why [[#Edit session — `DocumentEditSession`]] is not a working copy and why the whole UI is scheduled to split into data and visualization once Thorium and the profiler are up. The note model has since split the data out (N1, 2026-10-07): `RichTextDocument.blocks` is a `NoteNode[]` and the controls are views over it; since N2 (2026-10-08) every edit goes through `RichTextDocument`, which raises a `NoteChange` the controls only react to; since N3 (2026-10-08) one note can be open in several views at once, all sharing one edit session. See [[#Model shape]] and `ClaudeMemory/Decisions/note-model.md`.

## Model shape
A document is a flat list of blocks; a block of flowing text holds a list of inline runs.

- `RichTextDocument` — `NoteNode[] blocks`. `[A_XSDType("Document")]`, category `UI`. The entries are plain model objects, not controls; the editor builds a control per entry as its view.
- `RichTextDocument.Name` — the note's own name, and the only identity it has that survives the file being renamed. It is authored or absent and is **never derived**: a note's first heading is not its name, and nothing writes the file name into it, so an unnamed note stays detectably unnamed however many times it is saved. Display falls back to the file name at the point of use — a browser row shows the file, a tab caption shows the name. A note is named when it is created, by the vault's F5 / New prompt; nothing asks on Ctrl+S or on close, and a note with no `Name` keeps showing its file name. See `ClaudeMemory/Decisions/note-naming-and-text-field.md` and `ClaudeMemory/Decisions/note-live-sync.md`.
- `Block` (abstract, no `[A_XSDType]`) inherits [[TextBlockControl]] (a `PanelControl` derivative that flows runs) → `ContentBlock` (`[A_XSDType("Block")]`), the one concrete block, whose runs are its children. A heading is not a class of its own: a block carries a `StylingType` (`Text`, `Heading1`-`Heading6`, `Comment`, `Code`, `Quote`) and the styles file says how big that is, so adding a level is data rather than a new type. All category `UI`.
- `TextRun` (`[A_XSDType("Run")]`) → inherits [[TextInputControl]]: `Text`, `Bold`, `Italic`, `Strikethrough`, `FontName`, `FontSize` all come from the control, so the run adds no fields of its own (there is no `Inline` base anymore). Colour is the ordinary `ColorHex` every control has — a run that sets it repoints the colour onto each of its glyph children, since the run itself draws an invisible mask and only the glyphs reach the screen.
- `Block` carries no `[A_XSDType]` so it is never emitted as an element — it is an `AllowedChildren` target the [[XSDGenerator]] scans for concrete blocks; content blocks use `typeof(TextInputControl)` as their inline `AllowedChildren` (expands to the one `[A_XSDType]` subtype, `Run`).
- `Clone()` on the document / blocks / inlines is a deep copy. It was written for the working copy the editor was going to edit before a save; that is not what the edit session does, so its only callers now are `DocumentLayout.Clone` and whatever wants an independent copy of a note.
- A run's string and its spans live in `TextRunData`, a plain class the run control holds as `data`; the block's text and span operations forward to it.
- The data lives in plain model types, and the controls are views over them (N1 of the note model, 2026-10-07). `NoteNode` is the base of every entry in `RichTextDocument.blocks` and holds what blocks and tables share: `spaceBefore`, `pageBreak` and `insert`.
- `NoteBlock` is a paragraph: its `run` (a `TextRunData`) plus `stylingType`, `alignment`, `firstIndent`, `pageStyle`, `markLeft`, `markRight`, `language`, `codeWrap`, the list state, and the `AppendRun`, `SplitAt`, `Snapshot`, `SliceSnapshot`, `Restore` and `TakeKind` operations. `NoteTable` holds the column widths, the left and right column rules, the rows of `NoteCell`s and the table's borders, alignment and padding; a `NoteCell` holds its span, its rules and its `NoteBlock[]`.
- `BlockControl` and `TableControl` each hold a reference to their model object and read and write it directly, so there is no copy to keep in step. Loading a note (`RichTextDocument.Load`, `DocumentXml.Parse`) builds the model and no controls; the editor builds the views from it. Every edit goes through the model: `RichTextDocument` carries the edit functions and raises a `NoteChange` after each one, and `DocumentControl` only reacts — it adds or removes `BlockControl`s, re-lays out, rebuilds tables and puts the caret where the change says. Undo records hold the model, not a view.
- Arrays are used wherever the size does not change on every keystroke; a run's `spans` stay a `List`. A block insert or remove reallocates the array once.

Planned (not yet in code): `TextRun` gains `FontSize` (0 = inherit block default), `Underline` and a highlight color — mixed fonts/sizes word-by-word are just adjacent runs, with `StyleEquals` merging same-styled neighbours on edit. New blocks arrive via the same free round-trip: `CodeBlock` (Language attribute, monospace, no wrap; syntax coloring is computed at view time and never persisted). Tables landed in a different shape — see [[#Tables]].

## File formats
A note file is text in one of three formats, and none of them builds the editor directly. `MarkdownFormat` and `PlainTextFormat` only turn text into the `<Document>`/`<Block>`/`<Run>` tree and back; `DocumentXml.Parse` is the one thing that turns a tree into blocks, whichever file it came from. An `.axml` note is that tree already, so it goes straight to `DocumentXml`. Any `.axml` file can also be opened as coloured XML source beside its normal view — see [[Xml Editor]].

The file name becomes the note's `Name` for `.md` and `.txt`, so those notes are never unnamed. New notes from the vault browser and new stickies take the format the `NewNotes` setting names (`<Thorium><NewNotes Format="Axml"/>`, also `Markdown` or `PlainText`), `.axml` by default because it is the only format that holds everything a note can carry; a duplicate or a rename keeps the extension it had. Colour, gradient, font and size have nowhere to go in Markdown and are not written — the palette colours a Markdown note.

#### Load (path)
`FileJournal.Replay` (`path`)
switch on the extension of `path`
	`.md` → `DocumentXml.Parse` (`MarkdownFormat.Read` (file text, file name))
	`.txt` → `DocumentXml.Parse` (`PlainTextFormat.Read` (file text, file name))
	otherwise → `DocumentXml.Load` (`path`)

#### Save (path)
switch on the extension of `path`
	`.md` → write `MarkdownFormat.Write` (`DocumentXml.ToXml` (this))
	`.txt` → write `PlainTextFormat.Write` (`DocumentXml.ToXml` (this))
	otherwise → `DocumentXml.Save` (this, `path`)

### Markdown
One source line is one block. `#` to `######` are headings, `> ` is a quote, lines inside a fence are `Code` blocks, `- ` is a bullet and `- [ ] ` / `- [x] ` a task. `1.` and `1)` start numbered items, and `a.`, `A.`, `i.` and `I.` (or with `)`) start lettered and roman ones, the way Pandoc's fancy lists write them; a capital followed by a period needs two spaces after it, so a sentence like `A. Smith wrote` stays a sentence. Inside a line `**bold**`, `*italic*` or `_italic_`, `~~struck~~` and `` `code` `` become runs. A frontmatter block at the top is read as note properties — see [[#Properties]]. Everything else — links, tables, HTML — stays literal text and is written back as it was.

A fence opens on three or more backticks or tildes and closes only on a line of the same character that is at least as long, so a code block can hold a line of three backticks if its fence has four. When a note is written, each fence is made one longer than the longest line of backticks inside it. A fence that never closes runs to the end of the file.

#### Lettered Marker (token, item above)
if the token mixes capitals and small letters
	return none
if the item above at this level is lettered and `token` is the letter after its own
	return lettered
if `token` is a proper roman numeral, and it is longer than one letter, or is `i`/`I`, or the item above is roman
	return roman
if `token` is one letter
	return lettered
return none

A plain paragraph that would read back as one of these items is written with its dot or bracket escaped, `a\. like this`.

#### Write Inline (runs)
`plain` = the runs with their markers and no escapes
if reading `plain` back gives the same text and styles
	return `plain`
return the runs with `\` before every literal `` \ ` * _ ~ ``

#### List Level (indent)
`width` = indent width, a tab counting 4
drop open item widths wider than `width`
if the last open width equals `width`
	return its depth
push `width`
return the new depth

A first save normalizes a few things: `_i_` becomes `*i*`, `[X]` becomes `[x]`, list indents become one tab per level, a fence loses its language, `Comment` blocks become plain lines and line endings become LF.

## Properties
A note carries a palette of its own, the time it was created and the time it was last saved with changes. An `.axml` note keeps them as `Palette`, `Created` and `Modified` attributes on `<Document>`. A Markdown note keeps them in its frontmatter: a YAML block between `---` lines or a TOML block between `+++` lines at the very top of the file.

The frontmatter block is kept whole, exactly as written, so tags, aliases, comments and key order survive a save. Only the keys the note owns are read and written — `Palette`, `Created`, `Modified`, `ReadOnly`, `LineHeight`, `BlockSpacing`, `ListIndent`, `ListMarkers` and the page keys `PageMode`, `PageSize`, `Landscape`, `PageWidth`, `PageHeight`, `MarginTop`, `MarginBottom`, `MarginLeft`, `MarginRight`. `PageWidth` and `PageHeight` are only written when `PageSize` is `Custom`. The space between pages belongs to the app, not to a note, so `PageGap` is just one of your own keys now. They become the same attributes an `.axml` note has on `<Document>`, `<DocumentLayout>` and `<Page>`, so a Markdown note gets the per-note layout and page an `.axml` note has. Key names match case-insensitively and an existing line keeps its casing.

`Created` is filled from the file's creation time on disk when a note has none, and `Modified` is stamped whenever an edited note is saved, so every saved Markdown note ends up with a frontmatter block. Dates are ISO 8601 with the local offset. Turning off `StampModified` in `DocumentSettings` stops a save writing `Modified` at all — the key is taken out — and the panel shows the file's own last-write time instead.

`ListMarkers` lists the marker each list level takes by default, `[Decimal, LowerAlpha, LowerRoman]`, and is the same thing as the `<ListLevel>` entries an `.axml` note carries. `ReadOnly: true` makes the editor refuse every change to the text — typing, deleting, pasting, dropping, styling, undo and moving pictures — while the properties themselves stay editable, so the lock can be taken off again.

The properties are shown at the top of the first page in an [[Expander]]: the dates, a palette dropdown, the layout values, the list markers, a Read only checkbox and, for Markdown, every other frontmatter key. A value of `true` or `false` shows as a checkbox. A `[[note]]` link or a web address shows as a field with an Open button, which opens the note in a tab or the page in the browser. A list written as `- item` lines is edited as comma-separated text and written back as `- item` lines. A nested value stays read-only. Each of your own keys has a × that removes it, and the last row adds a new one by name. Picking a palette repaints that note only; a palette name that does not exist logs a warning and the note shows the app's palette.

#### Read Properties (block)
for each owned key
	`value` = the key's one-line value in `block`, or nothing
	if `value`
		set the matching attribute on `<Document>`, `<DocumentLayout>` or `<Page>`, creating the element
for each marker name in `ListMarkers`
	if it is a known marker
		add a `<ListLevel Marker>` to `<DocumentLayout>`

#### Merge Properties (block, tree)
for each owned key
	`value` = the matching attribute in `tree`, or nothing
	if `block` has the key
		if its value equals `value` → leave the line
		else if `value` → replace the key's line(s) with one line
		else → remove the key's line(s)
	else if `value`
		insert a line before the closing delimiter, or before the first TOML table
if there was no block and a value was written, the block is a new YAML one

## Lists
A list item is block state, not a block type: `listKind` (`None`, `Bullet`, `Task`, `SortedTask`), `listLevel`, `listMarker` and `isChecked` on `BlockControl`, written as `List`, `Level`, `Marker` and `Checked` on `<Block>`. `ApplyLayout` indents the text by `(level + 1) × ListIndent` and keeps one marker child in that indent. A split copies the kind, level and marker to the new block and leaves it unchecked, so Enter continues a list in the same marker. `SortedTask` is a task list that reorders itself like a phone checklist: ticking an item moves it to the top of the ticked items, unticking moves it to the bottom of the unticked ones, and deeper list levels under an item move with it. The move is animated: every list item whose content changed place slides from where its content was drawn to its new spot over a quarter of a second (ease-out), the ticked item and the items it passes alike, so nothing jumps. The slide is a `reveal` progress on the block that offsets its arranged position, and an item travelling to or from off screen stays drawn until it arrives. Undo and redo of a sorted move still jump. The `SortedTasks` setting (`Checked="Bottom"` or `"Top"`, under `<UI>`) picks which end the ticked items gather at; with `Top` everything mirrors, and a moved item still lands where the ticked and unticked items meet. While the caret is in a sorted checklist the inspector's Paragraph section shows a "Checked" row (Bottom or Top) under the List switch; picking one writes the same setting and re-sorts the caret's list, nested sorted sub-lists included, as one undo step ("Sort checklist"), while changing the setting in the Settings window re-sorts nothing. Turning paragraphs into a sorted checklist sorts them at once, in the same undo step, and the tick and the move are one undo step. Markdown has no syntax for it, so an `.md` note saves it as a plain task list and reopens it as `Task`. An item can carry its own indent: `listIndent` on the block, written as `ListIndent` in pixels on `<Block>`, replaces the layout's `ListIndent` for that item. A split, a slice, a plain-text paste and a move to another destination copy it, and LaTeX lowering writes it from a list's `leftmargin`.

### Markers
Bullets and numbers are one system. A `Bullet` item shows a `ListMarker`: one of six shapes (filled or empty circle, triangle and square) or its number written as `1.`, `A.`, `a.`, `i.` or `I.`. A shape is an `IconControl` from the default icon set, centred in the indent; a number is a `LabelControl` at the block's own font size, right-aligned against the text. Tasks keep their checkbox.

An item that names no marker takes its level's default from the document layout: `<ListLevel Marker="…"/>` entries inside `DocumentLayout`, one per level and cycling past the last. The editor-wide default in `DocumentSettings.settings.axml` is a single filled circle, and like every layout value a note can carry its own.

A marker is chosen per list, not per item. Right-click in the note, then **List marker**, gives every item at the caret's level of the caret's list that marker, as one undo step; a nested level below it and any other list keep theirs.

Numbers are not stored, with one exception: an item can carry a start, `Start` on `<Block>`, which restarts its list at that number. `RenumberLists` walks the blocks at the next measure after any change to the list structure and hands each item its marker and its number.

#### Renumber Lists
for each block in reading order
	if it sits in a different container than the block before, start every count afresh
	if it is not a list item, clear every count
	if it is a task, clear the counts at its level and deeper, and continue
	drop the counts deeper than its level
	`marker` = its own marker, or its level's default
	`number` = its start if it has one, else the count at its level + 1 when that count was for the same marker, otherwise 1
	show `marker` with `number`

A marker change at a level starts a new count, so a `1.` typed right under a bullet list reads `1.`, not the bullet list's next number.

A Markdown list that begins at `3.` opens numbered from 3: the reader keeps a list's first number when it is not 1 and ignores every number after it, the way CommonMark does, so `1. 1. 1.` still reads as 1, 2, 3. The start stays on the item it was written on — Enter after it does not copy it, and turning the item back into a paragraph clears it.

Right-click, **List marker**, **Continue numbering** numbers the caret's list on from the nearest list above it with the same marker at the same level, as one undo step. It writes that number as the list's start once, so the two lists do not stay linked when the one above grows.

#### Continue Numbering
find the first item at the caret's level of the caret's list
if its marker is not a number, stop
for each block above it, nearest first, inside the same container
	if it is an item at the same level with the same marker
		give the first item a start one past that item's number
		stop

Every list change is one `BlockStateEdit`, the block snapshotted before and after, so undo and redo restore it whole.

#### Type Char (c) — the list part
if `c` is a space and the block is not code
	if the block is not a list and its text so far is `- `
		remove `- ` and make it a list item with the default marker
	if the block is not a list and its text so far is digits then `. ` or `) `
		remove it and make it a list item numbered `1.`
	if the block is a bullet and its text so far is `[ ] ` or `[x] `
		remove it and make it a task, checked for `[x] `

#### Split Block — on an empty item
if the caret's block is an empty list item
	if its level is above 0, outdent it one level
	otherwise clear its kind, level, marker and tick
	either way, do not split

#### Backspace — at an item's start
if nothing is selected, the caret is at offset 0 and the block is a list item
	clear its kind, level and tick instead of deleting

## Markdown as you type
Markdown typed into a note becomes formatting the moment it is complete, the way Obsidian's live preview does it, and the markers disappear. A prefix at a block's start converts on its space, a closing inline marker converts on the character that closes it, and a whole-line marker converts on Enter. None of it happens inside a code block, on a rule, or in a `.txt` note, which has no way to save the result. Each conversion is part of the keystroke's own undo step, so one Ctrl+Z gives the typed markers back.

#### Type Char (c) — the Markdown part
if `c` is a space and the block's text so far is `#` to `######` then a space
	remove it and make the block a heading of that level
if `c` is a space and the block's text so far is `> `
	remove it and make the block a quote
if `c` closes `**`, `*`, `~~` or `` ` `` and an opener of the same marker sits earlier in the block
	the opener may not be followed by a space and the closer may not follow one
	a single `*` may not touch another `*`
	remove both markers
	make what was between them bold, italic, struck through or inline code
	arm the style off again — inline code included — so what is typed next is plain

#### Split Block — a Markdown line
if the block's whole text is ```` ``` ```` followed by an optional language name
	clear the text and make the block a code block in that language
	do not split
if the block's whole text is three or more of one of `-`, `*` or `_`
	clear the text and make the block a rule
	start a paragraph after the rule and put the caret in it

## Code blocks
A code block is a run of consecutive blocks styled Code, one block per line, which is the shape `.md` notes already read fenced code into. Each block carries the fence's language name, if one was given, and a split hands it on, so Enter inside code continues the same block. The style scheme names the font: `<TextStyle Type="Code" FontName="consola"/>` in `DocumentSettings.settings.axml`, Consolas being baked from the installed system font like Arial is. A Code span inside ordinary text takes the same font. Code is inset ten pixels on both sides and drawn on the palette's sub-field colour, a band the width of the column behind every line; consecutive code blocks are stacked with no block spacing between them, so the run reads as one box and needs no grouping when it crosses a page.

Code does not wrap. A line wider than the column runs on past the right margin, its sub-field band with it, and the note grows wide enough to hold it, so the editor's own sideways scroll reaches the end of the line. A code block in an `.axml` note can ask to wrap with `Wrap="true"`; `.md` and `.txt` have nowhere to say so, so their code never wraps.

Code is coloured as it is shown and never saved that way. Each run of code lines with one language is read top to bottom by `SyntaxTokenizer`, which marks every character plain, keyword, string, number or comment and hands what a line leaves open — a `/*` comment, an XML comment or tag, a Python triple quote — on to the next line. C#, GLSL, XML and Python have their own rules; any other language, or none, gets C-like rules with a few common keywords. A run is read again whenever one of its lines changes, so opening a comment on the first line recolours the lines under it. The colours come from the palette's `Keyword`, `String`, `Number` and `Comment`; a palette that leaves them out shows the first three in its accent and comments in muted text. A span with its own colour or gradient keeps it.

#### Highlight Code ()
for each run of consecutive code lines that share a language
	if any line of it changed
		`state` = nothing open
		for each line
			`state` = tokenize the line from `state`, one token per character

#### Tokenize (line, language, state) — C-like
while characters are left
	if a comment or triple quote is open, mark up to its close and close it, or mark to the end
	else if `//` (or `#` in Python), mark the rest as comment
	else if `/*` (or `"""` / `'''` in Python), open it
	else if a quote, mark up to the matching quote, stepping over escapes
	else if a digit, mark the number
	else if a word, mark it keyword when the language lists it
return what is still open

Tab inside code types a real tab character rather than nesting a list. With several code lines selected, Tab puts a tab at the start of each and Shift+Tab takes one off each, as one undo step.

A tab, anywhere in a note, moves the pen to the next tab stop. Stops sit every four space widths of the tab's own font, counted from the start of the line, so a tab after two letters and a tab at the line start both end at the first stop, and a tab that starts exactly on a stop goes on to the next one. Measuring, drawing, caret placement, clicking and alignment all work the advance out from where the tab starts.

#### Tab Advance (run, pen in line)
`stop` = 4 × the run's space advance
return `stop` − (pen in line mod `stop`)

#### Shift Code Indent (delta)
if the caret is not in code
	return not handled
if `delta` > 0 and nothing is selected
	type a tab at the caret
	return handled
for each code line from the anchor's block to the caret's block
	if `delta` > 0
		put a tab at the start
	else if the line starts with a tab
		take it off
	move the anchor and the caret with it if they are on this line
select from the anchor to the caret again
return handled

#### Split Block — the last line of code
if the block is an empty code line and the next block is not code
	turn it back into a plain paragraph
	do not split

## Rules
A horizontal rule is a block with the `Rule` styling and no text. It measures as one empty line and draws a single line across the column in the palette's line colour instead of glyphs. The caret can stand on it like on an empty paragraph, but nothing can be written into it. A rule may carry a thickness, `ruleWeight`, written as `Weight` in pixels on `<Block StylingType="Rule">`. It is then drawn that thick and stands with its bottom on the baseline of the block's text line, as a TeX rule box does inside a paragraph; without `Weight` the rule is the 1px line centred on the line. It is copied like `listIndent`, and LaTeX lowering writes it for a paragraph made only of rule boxes.

#### Type Char, Paste, Drop, Split Block — on a rule
split the rule at its start
make the new block after it a plain paragraph and put the caret there
carry on with the character, the paste, the dropped text or nothing

#### Delete Range (from, to) — when the head is a rule
merge as usual
give the merged block the tail's kind
	so Backspace at the start of the paragraph after a rule deletes the rule, not the paragraph
undo merges back and then puts the rule's kind back on the head

The styling menu's **Horizontal line** turns an empty caret block into a rule, or adds one after the caret's block when it holds text. In Markdown a rule is written `---`, except as a note's first line, where it is written `***` so it cannot be read back as frontmatter; `---`, `***` and `___` all read as a rule.

## Paragraph keybinds
Ctrl+1 to Ctrl+6 make the caret's paragraphs a heading of that level, Ctrl+7 a quote and Ctrl+9 a code block; pressing the same key on a paragraph that already has that style turns it back into text, and Ctrl+Shift+0 always does. Ctrl+8 steps the caret's paragraphs through text, bullets, numbers, tasks, sorted tasks and back to text, one undo step per press; Ctrl+Shift+8, Ctrl+Shift+7 and Ctrl+Shift+9 toggle bullets, numbers and tasks directly. Ctrl+Enter ticks or unticks the task under the caret and does nothing on any other paragraph, which also means it no longer splits one. Ctrl+Shift+X is strikethrough, Ctrl+Shift+E toggles inline code over the selection or for what is typed next the way Ctrl+B does bold (`CaretStyle.code` carries whether the caret is in code), Ctrl+Shift+R inserts a horizontal line, Ctrl+] and Ctrl+[ indent and outdent a list like Tab and Shift+Tab, Ctrl+V pastes a sheet link when the clipboard holds one, Ctrl+Shift+V always pastes plainly, and Ctrl+Alt+M opens the selected formula. The keys live in the host's `InputMap.inputs.axml`; the actions are `Text.Heading1`…`Text.Heading6`, `Text.Quote`, `Text.CodeBlock`, `Text.Paragraph`, `Text.CycleList`, `Text.ToggleCheck`, `Text.InlineCode` and `Text.Rule` in `TextInputActions`.

#### Cycle List ()
```
block = caret block of the focused editor
if block is null → return
switch block's list kind
    None                    → toggle bullets
    Bullet, shape marker    → toggle numbers
    Bullet, numbered marker → toggle tasks
    Task                    → toggle sorted tasks
    SortedTask              → toggle sorted tasks (back to text)
```

## Alignment
A block is aligned left, centred, right or justified. Alignment does not change how a paragraph breaks into lines; once the lines are measured, each one is slid across the width it is allowed to fill. A line beside a floating picture is only allowed the width the picture leaves free, and the measurer records that width on the line as `room`. Trailing spaces hang past the edge, so a right-aligned line ends at the margin on its last letter. Every place that turns a line into positions — drawing, the caret, presses, the selection and picture boxes — already adds the line's left offset, so none of them changed.

Justified text spreads every line except a paragraph's last across its room by widening the spaces inside it. The extra width is recorded on the line as `spaceExtra`, given to each space before the line's last letter, and every place that walks a line's characters adds it after such a space. A line with a tab in it is left as it is, because the tab stops would move under the stretch.

#### Align ()
if the block is justified
	for each line except the last
		`visible` = the width up to the line's last letter
		`spaces` = the spaces before that letter
		if the line holds a tab, has no such spaces, or already fills its room, skip it
		`spaceExtra` = (`room` − `visible`) ÷ `spaces`
		grow the line's width by (`room` − `visible`)
	stop
`factor` = 0 for left, ½ for centred, 1 for right
if `factor` is 0, stop
for each line
	`room` = the line's room, or the wrap width when it has none
	if the run does not wrap, skip it
	`hang` = the width of the spaces and tabs at the line's end
	add (`room` − (the line's width − `hang`)) × `factor` to the line's left

Ctrl+L, Ctrl+E, Ctrl+R and Ctrl+J, or the four alignment buttons beside the styling menu, align every block the selection touches, or the caret's block, as one undo step. Alignment is written as `Align` on `<Block>` and only `.axml` notes have it: Markdown has no paragraph alignment, and wrapping a paragraph in HTML would stop Markdown readers formatting what is inside it.

A run can be a fill, `<Run Fill="true">` (`StyleSpan.fill`): it has no width of its own, is a break point, and takes the slack of the line it sits on the way TeX's `\hfill` does. LaTeX lowering writes one for horizontal glue of fil order or higher, which is how a date lands at the right margin. Typing beside a fill goes into a text span, never into the fill. Knuth-Plass counts a short line that holds a fill, and does not break at it, as badness 0.

#### Fill ()
for each line
	if the line holds a fill run
		give the line's slack to its fills
		skip justifying and aligning this line
	the draw loop advances by the fill's share and records a space glyph, which the PDF export reads as a space

Knuth-Plass chooses breaks that assume a space may shrink by a third. Every line it set over its room is shrunk when drawn, whatever its alignment, through `Spread (line, stretch)`; before, only justified lines that were not a paragraph's last were, and the others ran past the clip.

#### Spread (line, stretch)
if Knuth-Plass set the line over its room
	shrink the line's spaces to fit   // centred, left, right, a paragraph's last line and a line before a display included

#### Shift List Level (delta)
for each list item the selection touches, in order
	`deepest` = the item above's level + 1, or 0 when the item above is not a list item
	`level` = delta > 0 ? min (level + 1, `deepest`) : level − 1
	skip if `level` < 0, or if indenting would not go deeper
	set the level and re-apply layout
record one `BlockStateEdit` if anything changed

Tab and Shift+Tab are `Text.Indent` and `Text.Outdent` in Thorium's input map.

## Encryption

A note can be encrypted as a whole file with a password, or have runs of blocks sealed under their own password; a locked seal is a single `NoteSealed` node shown as an [ENCRYPTED] button and stored as a `<Sealed>` segment. See [[ENCRYPTION]].

## Persistence — `DocumentXml`
Load and save are attribute-driven reflection (the same pattern as [[Vulkan Control]] `ParseXML`), so new blocks / inlines / run styles round-trip automatically once they carry the attributes. NOTE: the binary [[Serializer]] is unrelated — notes are XML, never routed through `Serializer`.

`RichTextDocument` implements [[XSDGenerator|IXMLParser]]`<RichTextDocument>`; the string argument is a file path (resolved by the vault), not a `Paths.Doc` name.

#### Load (path)
parse `XDocument` from `path`
return [[#Parse Element]] (`root`) as `RichTextDocument`

#### Parse Element (element)
`type` = [[AnyXMLType]]`.FindType`(`element` local name)
`node` = create instance of `type`
[[#Apply Attributes]] (`element`, `node`)
for each `child element` in `element`
	[[#Attach Child]] (`node`, [[#Parse Element]] (`child element`))
return `node`

#### Apply Attributes (element, node)
for each `member` of `node` with `[A_XSDElementProperty]`
	`attribute` = `element` attribute matching `member` name (case-insensitive)
	if `attribute` exists
		set `member` = convert `attribute` value to member type (`TypeDescriptor`)

#### Attach Child (parent, child)
`list` = `parent` `List<>` field whose element type is assignable from `child` type
add `child` to `list`

#### Save (document, path)
`root` = [[#Write Element]] (`document`, `document` layout)
create directory of `path`
write `XDocument` (`root`) to `path`

#### Write Element (node, layout)
`element` = xml element named `node` `[A_XSDType]` name
for each `member` of `node` with `[A_XSDElementProperty]`
	if `member` value equals the value on a fresh instance, skip
	if [[#Is Resolved]] (`node`, `member`, `layout`), skip
	set `element` attribute (`member` name) = [[#Format]] (`member` value)
for each complex `member` of `node`
	`child` = [[#Write Element]] (`member` value, `layout`)
	if `child` has attributes or elements, add `child` to `element`
for each `List<>` field on `node`
	for each `child` in field
		add [[#Write Element]] (`child`, `layout`) to `element`
return `element`

#### Format (value)
if `value` is a bool, return "true" or "false"
return `value` as invariant string

#### Is Resolved (node, member, layout)
if `node` is a `TextRun` and `member` is `fontSize`
	`type` = `run` styling type, or the owning block's when it inherits
	return `run` font size equals `layout` font size for `type`
if `node` is a `VulkanControl` and `member` is `controlColorHex`
	return `controlColor` differs from its default and `controlColorHex` equals its hex
return false

A saved value has to be one somebody wrote. Two mechanisms keep computed values out of a note: the ordinary one is the fresh-instance default (see [[xml-save-skips-defaults]]), and the second is `Is Resolved`, for values a *setter* computed, which no default check can catch because the computed value is not the default one.

There are two. `ApplyLayout` writes the styling scheme's size into every run's `fontSize` the moment a note is loaded — a Heading1 run holds 34 where a fresh run holds 16 — so without the check the first save stamps `FontSize` onto every run and pins the note to whatever the scheme said that day. And `controlColor`'s setter resolves into `controlColorHex`, so a run that named a colour holds both, and saving both pins the note to today's meaning of "gray".

The colour case has a trap the font size case does not: the hex may only be skipped when the *enum* is itself being written. `ControlColor`'s default is `red`, so a control that never named a colour has the enum omitted by the default check — dropping the hex as well would lose an authored `ColorHex` entirely on the next load. A run that deliberately authored the size or the hex the scheme already resolves to loses its explicit attribute, which is the acceptable half of the trade: it reloads to the same value either way, and a second save is byte-identical to the first.

`Format` exists for one reason: `xs:boolean` has no `True`. `Convert.ToString` spells a bool the C# way, so an unfixed save writes `Bold="True"` and the note fails the schema it names in its own `schemaLocation`. It still loads — the reader converts case-insensitively — which is exactly why this went unnoticed until a save was reachable from the UI.

## View — `DocumentEditorControl`
The editable view over a document. It is a [[#^scrollable|ScrollableControl]] over a `DocumentControl`, which stacks one `BlockControl` or `TableControl` per model entry, built by `LoadDocument` from the `NoteNode`s; each holds a reference to its model object and reads and writes it directly, so there is no second copy. `ApplyLayout` resolves each run's styling type into a font size through the scheme before they are stacked, and the caret is a child of the `DocumentControl` so it scrolls with the text.

This presentation does not scale past a few pages, since every character is a full control. The replacement was going to be L2's virtualized view over a layout cache; that was **built and reverted** on 2026-08-07, because virtualizing the view removed a second parallel set of glyphs and left the first one — parsing a note builds every glyph before any view is consulted, so the model alone is already past the descriptor array's 50,000 slots on a 400-block note. There is now one layout path for all text and the control tree is the hit-test again.

What replaces it is engine-wide rather than document-local: the UI splits into **data and visualization**, most of the UI becoming data and controls becoming the thing that draws it, **after** Thorium's first version and the test/profiling platform are up. See `DOCUMENTATION/ClaudeMemory/Decisions/ui-data-control-split.md` and [[Document Layout Engine#Status]].

- `[A_XSDType("DocumentEditor", "UI")]` so it can be placed in `UI.ui.axml`; a `Source` attribute names an engine-XML note to load (resolved via `Paths.Doc` when relative, used as-is when rooted).
- `LoadDocument(RichTextDocument)` — entry point used by the vault later; `LoadPath(name)` — load by file.
- The control tree lives in the engine's `Controls` render group. **Rebuild-on-edit (P3+) must remove stale run/glyph controls from that group** — the deferred-cleanup TODO already noted in [[INPUT|TextControl]]; build-once (P2) is unaffected.

#### Load Document (document)
`stack` = vertical [[StackPanel]] (invisible mask, stretch)
for each `block` in `document`
	`stack` add [[#Build Block]] (`block`)
set scrollable content = `stack`

#### Build Block (block)
if `block` is not a `ContentBlock` return null
`size` = heading ? size-for-level : paragraph size
`text block` = [[TextBlockControl]] (stretch)
for each `run` in `block`
	`run control` = [[INPUT|TextInputControl]] (size, run style flags)
	`run control` text = `run` text   // builds glyphs at the set size
	`text block` add `run control`
return `text block`

## Edit session — `DocumentEditSession`
An open note is `{ document, path }` and a `Save()` that writes the one to the other. It is deliberately **not** a working copy, even though the description above and the original plan both say it is: the L1/L2 rework left the model *being* the control tree, so a second copy would have to be a second control tree — one `GlyphControl` per character, twice — and the note about the glyph ceiling already says that number is being leaned on. A revert is therefore a reload from disk rather than a discarded clone. When per-note undo arrives it will be an edit log over the live tree, not a shadow copy of it.

`DocumentEditorControl.LoadPath` gets the session from `NoteSessions.Open(path)`, which hands every view of one path the same session — one model, one undo stack, one dirty flag, one save — and counts the views; the editor releases it on `LoadPath` and when it is destroyed, and the last release drops it. `Save()` on the editor forwards to it.

A change made in one view moves the caret, the selection anchor and the text-drag press in every other view by address mapping (`NoteChange.Map`); an insert exactly at another view's caret leaves that caret in front of the insert. Only the session's `lead` view — the one whose editor last became the active control — obeys the caret an edit names. "Split right / down" in the tab menu on a note or `.tex` tab opens a second view of the same note in the new pane. `LoadDocument` on its own leaves the editor sessionless and unsavable, which is what a preview of a document that came from nowhere should do.

## Live sync with the file — `NoteFileSync`
An open `.axml` note and its file on disk stay in step both ways, as groundwork for network replication. Each session owns one `NoteFileSync`, created by `NoteSessions.Open` and disposed by `NoteSessions.Close`; disposing flushes. Ctrl+S still saves at once.

In-app edits autosave almost immediately: the first edit saves on the next tick, then at most once every 250 ms, with a final save after the last edit. A save writes only the changed top-level segments of the file, in place. The segments are the header, the `<DocumentLayout>`, each `<Block>` and `<Table>`, and each `<Footnote>` / `<Float>` group. Each node carries an internal `dirty` flag, set from `RichTextDocument.Raise` — the one exit every model edit passes — so a run never tells its block. The header and the layout are compared with the file at write time instead of being flagged, and a column drag marks its table in `TableControl.EndResize`.

A segment has slack: trailing spaces after its element. An edit that fits is overwritten in place; one that outgrows its segment shifts the rest of the file once, from the first hole that changed size, so that is meant to be rare. A grown segment is given slack of `max(64 B, 25%)`, and slack left over beyond `max(256 B, 50%)` is trimmed by shifting left. The first write of a note has no map of the file and writes the whole thing. A failed write — the folder deleted, the file locked — is caught, logs one warning and leaves the note dirty for the next try.

Crash safety comes from a journal rather than a temp file and an atomic replace, because replacing the file rewrites all of it. `FileJournal` is held open for the session, one journal file per note under `Paths.JOURNAL`. A commit writes a record — a magic number, a SHA-256 of its body, and the body (the path, the new length and each offset with its bytes) — and forces it to disk once, applies the writes to the note and forces that, then zeroes the record's magic without forcing. There is no commit marker: `RichTextDocument.Load` replays a leftover record before it reads, deletes a zeroed one, and warns about one that is cut off or fails its checksum, applying nothing. Replay is idempotent, because the offsets are absolute and the length is final, which is why zeroing needs no flush. Disposing the journal deletes its file.

The write runs off the main thread. The main thread does the matching and serialises the dirty elements into a `SegmentPatch` (the writes, the new length, the new segment map and the new file bytes), keeps the file's bytes in memory so a partial save never opens the note to read it, and hands the patch to `FileWriteQueue`, one background thread that applies patches in order. The autosave timer returns without waiting. Ctrl+S, closing a tab or window, disposing the session, `Notes.SaveEdited`, an overwrite and a vault rename wait for the queue, so "saved" still means on disk and a failure still surfaces. A background failure drops the map and the file bytes, marks the note unsaved again and warns once. Shutdown waits for the queue through the `Files.Drain` step. The note itself is not held open between saves, so a program that writes it with a shared read lock still gets in.

A write by another program reaches the open note through a `FileSystemWatcher`. When the note has no unsaved edits the new file is parsed and matched against the open nodes from both ends by their XML, widened to whole footnote / float groups, and only the middle is swapped in through `ReplaceNodes`. That raises `NoteChangeKind.Nodes`, `DocumentControl.ShowNodes` reconciles the views, and the whole reload is one undo step named "Reloaded from disk", carrying the header with it. A write that does not parse as a note is ignored. When the note does have unsaved edits the app wins: they are written back over the outside write, reusing the segments that still match.

#### Edited ()
if a save is already pending → nothing
if 250 ms have passed since the last save → save on the next tick
otherwise → save when the 250 ms are up
a save finds the note clean → nothing to write

#### Write (wait)
if the note is not `.axml` → `document.Save` (`path`), the whole file
otherwise
	if the file's length or write time differs from what was last written → drop `map` and `file`
	`patch` = `NoteSegments.Prepare` (`document`, `path`, `map`, `file`)
	`FileWriteQueue.Enqueue` (`journal`, `patch`, `Done`)
	if `wait` → `FileWriteQueue.Drain`, then `Landed`, rethrowing the first failure
an autosave (`Flush`) passes `wait` = false and returns at once

#### Done (error)
this runs on the writer thread
if there was no error → remember the file's length and write time
queue the outcome, and post `Landed` to the main thread

#### Landed ()
if a write failed → drop `map` and `file`, mark the session unsaved, log one warning
otherwise → forget the warning
if nothing is in flight → `Settled`

#### Prepare (document, path, map, file) — `NoteSegments`
if there is no `map` or no `file` → build the whole file
keep each old segment that is unchanged: the header and layout by their bytes, an entry when none of its nodes is dirty and it is still in order
every run of segments between two kept ones is a hole, written anew
	each hole keeps its old room when the text fits and less than `max(256 B, 50%)` is left over
	otherwise its last segment gets `max(64 B, 25%)` of slack
holes before the first one that changed size → overwritten in place
from that one on → the rest of the file rewritten once
clear the dirty flags
return a `SegmentPatch` (writes, new length, new `map`, new `file`)
`NoteSegments.Write` is the synchronous form → `File.ReadAllBytes`, `Prepare`, `FileJournal.Commit`
	in DEBUG → read the file back, map it again, throw on any mismatch

#### Commit (writes, length) — `FileJournal`
write the record and `Flush(true)`
apply the writes to the note, set its length and `Flush(true)`
zero the record's magic and `Flush(false)`

#### Reload ()
if the file is the one this last wrote → nothing
if the note has unsaved edits
	`NoteSegments.Rebase` maps the outside file, keeping the lead and tail entries the note would write the same
	save, so the note is written over it
otherwise
	if the file is not a note → warn once, drop `map`, leave the note alone
	match the open nodes against the new ones from the lead and from the tail, widened to whole footnote / float groups
	`ReplaceNodes` (first, removed, added) with the header, as one undo step

## Caret movement
The caret is `(run, cursorPosition)` — the offset lives on the run, and `DocumentControl` remembers which run holds it. Movement splits in two by what the move actually asks:

- **Left / right** walk runs in document order. A run boundary *inside a block* is one caret slot and not two, because the end of one run and the start of the next resolve to the same point — the runs share a visual line through the `firstLineOffset` / `lastLineEndX` handshake. So a step across it lands past the duplicate. A block boundary is two slots, because the runs are on different lines.
- **Up / down, line start / end, page up / down** are all one primitive: resolve a point. `CaretAtPoint` scores every *line of every run* — not every run — because a visual line spans runs, so the run holding the line's start is routinely not the run the caret is in. A line closer in y always wins and x only breaks ties within a band, which is what makes line start and line end land on the right run rather than on the current one.

Line start and line end are the **visual** line's, not the paragraph's, which is the only reason the point primitive is needed at all. Page up and down move by one viewport height, which is why they live on the editor rather than on `DocumentControl` — the scroll viewport is the editor's.

### Word by word
Holding the `Word` [[INPUT#Named modifiers|named modifier]] (Ctrl in Thorium) turns Left and Right into word moves, Home and End into the start and end of the note, and Backspace and Delete into word deletes. The keybinds do not change — `TextInputActions.Move` asks whether the role is held and swaps the move, the same way `Extend` already turns a move into a selection. A word uses the three character classes of [[#Word and line]], so the rule for "what is a word" lives in one place (`TextInputActions.WordEdge`) and double-click, the arrows and the single-line field all agree.

#### Word Edge (text, offset, direction)
if moving right
	if the character ahead is not a space, skip every character of its class
	skip every space
else
	skip every space behind
	skip every character of the class behind
return where the walk stopped

At a block's edge a word move steps into the neighbouring block exactly as a character move does. A word delete is a word move with extend, then the ordinary range delete — the same "extend, then delete" trick plain Backspace uses — so it needs no deletion code of its own.

Every move *requests* a scroll to the caret rather than performing one, and a move into a different run repoints `UICollisionHandling.activeControl`. That last part is not cosmetic: `Text.Write` drains characters into whatever the collision handler last made active, so a caret that arrowed into a new run without repointing it would type into the run that was clicked.

## Scrolling to the caret
Every path that moves the caret — arrows, Backspace and Delete, Enter, typing, undo and redo — sets a flag and invalidates arrange. The scroll itself happens at the end of `DocumentEditorControl.Arrange`, which is the first moment the caret's run holds a rect for the layout it now lives in. Doing it at the call site cannot work: a block created this tick has no arranged rect at all, and `ScrollIntoView` reads a zero rect as "above the viewport" and throws the note to the top, which is why Enter shipped without a scroll for a week.

The rule the override lives under is that it must never exit with `isArrangeDirty` still set. `InvalidateArrange` walks up the tree and registers nothing the moment it meets a control already marked dirty — the control it started on included — and from inside `Arrange` every ancestor still is, since a container clears its own flag only after its children return. Leave the flag set and the editor is dirty forever: every later invalidate from it or from any run beneath it is silently dropped, so the wheel moves the offset with nothing redrawing and the arrow keys move the caret's offset with the caret never being repositioned.

```
Arrange(finalRect):
    base.Arrange(finalRect)
    if no scroll was requested: return
    clear the request
    if the caret has no point: return
    remember the offset, ScrollIntoView the caret's rect
    if the offset moved: base.Arrange(finalRect) again
    otherwise:           clear isArrangeDirty, which ScrollIntoView set for nothing
```

`ScrollIntoView` invalidates whether or not it actually scrolled, which is what the second branch is for; it does nothing to the offset when the target is already inside the viewport, so the extra pass is paid only on frames where the view genuinely moves. Clicks and drags are left out of all this — a click lands where the user is already looking, and a drag has its own overshoot scroll.

## Caret blink and focus
The caret blinks itself. `CaretControl` overrides `OnTick` and keeps itself on the engine's tick list while it is focused — `SetTicking(true)` when it is built and in `Focus`, `SetTicking(false)` in `Blur` — so the engine calls it on the main thread, which is also the pool's owner thread, only while it can blink. It accumulates `Engine.deltaTime` into a phase and reads it modulo the cycle, so a long tick cannot slide the wave off the clock. The alpha is `1` or `0` at 0.53s each way and is written only when it actually flips, which is twice a second for an idle caret. It is the first control in the engine to animate on the tick, and it deliberately does not introduce a tween system. `TextBoxControl` shares the class, so the rename field and the note-name prompt blink too.

The phase restarts from solid on every caret placement, because a caret that vanishes mid-keystroke reads as input lag. Two call sites cover every path: `SetCaret`, which every click, arrow, delete, undo and block split funnels through, and `TypeChar`, which typing reaches instead — `WriteChar` bumps `cursorPosition` on its own and never touches `SetCaret`.

Focus is **pushed**, not polled. `TextRun` forwards `OnContextAdded` / `OnContextRemoved` for `ActiveControl` to `DocumentControl.RegainFocus` / `LoseFocus`, the same way `FieldLine` raises `TextBoxControl.onBlur`. The run is always the control that hears it, because `SetCaret` repoints `UICollisionHandling.activeControl` at the caret's run on every call — a click past the text makes the block active for one instant and `SetCaret` overwrites it a moment later. That same direct write is why arrowing between runs raises nothing at all: it bypasses `Context.Set`, so no context is lost and the caret cannot blink out mid-move.

`LoseFocus` walks up from the **new** active control — `SetActiveControl` assigns the field before raising the removal — and keeps the caret if it meets the `DocumentEditorControl`. The scope is the editor and not the `DocumentControl`, because `ScrollableControl` parents its thumb beside the content rather than inside it, so scoping to the content would make dragging the scrollbar take the caret with it. Hiding is `alpha = 0` rather than the collapsed rect `TextBoxControl` uses, which keeps the whole mechanism inside `CaretControl` and runs no arrange pass when focus moves.

An unfocused editor still shows its selection, and the app losing OS focus does nothing — no GLFW focus callback is registered anywhere yet.

## Selection
A selection is two caret slots — an **anchor** where the press or the shift-extend started, and a **focus** where the caret is now. The focus is not stored separately: `caretRun` and `cursorPosition` already are it, so the only new state is the anchor, and `anchor == focus` is both "nothing is selected" and the plain-caret behaviour that existed before.

Slots are **normalized on write**, which is what makes that equality mean anything. The end of a run and offset 0 of the next run inside one block are the same point on screen — the block hands the next run the x the previous one ended at — so the two would compare as different while sitting on the same pixel, giving a phantom selection at every run boundary. `Normalize` walks a slot forward past any run end that has a following run in the same block, so one point is one pair. Rightward caret movement gets that rule for free; leftward has to do it itself, since normalizing only ever moves forwards.

Ordering the two ends needs reading order, and a run does not know where it sits in the document, so `OrderedRuns` is walked and the ends compared as `(run index, offset)`. This is what lets a drag run backwards.

Extending rather than collapsing is a boolean carried through the moves that already existed — `MoveCaret(move, extend)` and `SetCaret(run, offset, extend)` — and the boolean comes from the `Extend` [[INPUT#Named modifiers|named modifier]], not from a key the engine names. Shift is only what `InputMap.inputs.axml` happens to bind it to.

### Word and line
Clicking the same spot twice selects the word, three times the visual line. Both arrive as [[Vulkan Control]]'s multi-click, which reports the tap count from the release, so neither needs a gesture of its own — the editor switches on the count and everything below it is selection code that already existed.

A word is the maximal run of one **character class** around the caret, where a class is whitespace, word (letters, digits and `_`) or symbol. Three classes rather than two, so clicking punctuation selects the punctuation and not the identifier beside it. The walk crosses runs inside the block through `AdjacentRun`, because a bolded half-word is two runs and one word; it stops at the block edge, since a paragraph boundary is a boundary in every other operation too.

Which class is taken matters at the edges. `OffsetAt` returns the nearest slot, so clicking the right half of a word's last letter puts the caret *after* the word, where the character ahead is a space — taking the character ahead on its own would select the space and never the word that was clicked. So the rule is that **either side being a word wins**, and only when neither is does the character ahead decide.

The line needs no geometry at all: it is `MoveCaret(LineStart)` followed by `MoveCaret(LineEnd, extend)`, which is Home and then Shift+End. Those already resolve the *visual* line rather than the paragraph, so a wrapped block selects the row that was clicked and not all of it.

#### Select Word
```
`focus` = normalized caret slot
`right` = class of the character after `focus`, or none
`left` = class of the character before `focus`, or none
`target` = either is a word ? word : `right` ?? `left`; nothing on both sides returns
walk `start` back from `focus` while the character behind it is `target`
walk `end` forward from `focus` while the character ahead of it is `target`
anchor = normalized `start`, then place the caret at `end` extending
```

#### Highlight (run, from, to)
for each `line` of `run` layout
	clip [`from`, `to`) against the `line`'s character span, skip if empty
	`left` = clip starts the line ? the line's own left : `run` [[#Caret At]] (clip start) x
	`right` = clip ends the line ? the line's left + its width : `run` [[#Caret At]] (clip end) x
	arrange the next box at (`left`, `line` top) sized (`right` - `left`, `line` height)

One box per **visual line**, not one per selection, since a wrapped range is several rectangles. The x span comes from `CaretAt` — the same function that places the caret — so a highlight cannot drift from the caret drawn inside it. The one place it cannot be used is a wrapped line's final slot, which belongs to the line *below* by the caret-affinity rule, so the line's own width is the right edge there instead.

The boxes are `SelectionControl`s, a `PanelControl` exactly as `CaretControl` is. Two properties of them are load-bearing rather than incidental: they are **reused and arranged to nothing** when unneeded, because creating and destroying them as the mouse moves costs a pool allocation and a full paint-order permute every tick; and they are **inserted at the head of the child list**, because paint order is the tree's DFS order — which is why the caret, added last, draws over the text, and why a highlight added last would cover the letters instead of sitting behind them.

Drag runs on the engine's drag lifecycle: the editor calls `StartDrag()` from its click handler, `ResolveDrag` then arrives every tick until the button comes up, the focus follows the mouse through `CaretAtPoint`, and a mouse past the viewport edge scrolls by the overshoot. Reading `InputHandler.mousePos` rather than the position handed to `ResolveDrag` is the same one-frame-lag workaround the click path carries.

## Editing
There is one deletion primitive — a range between two caret slots — and Backspace, Delete, typing over a selection and Enter's leading collapse all go through it. Backspace and Delete with nothing selected build the range they need by extending the caret one move and deleting the result, so "the character before the caret" is never spelled out a second time: the rules about which side of a run boundary a slot lives on, and about a block boundary being two slots where a run boundary is one, stay in [[#Caret movement]] where they were already written.

The block merge falls out of that rather than being handled. Backspace at the start of a block extends the caret to the previous block's end, which is a range of zero characters spanning two blocks — and a range spanning two blocks collapses into the first by definition. Delete at the end of a block is the same range approached from the other side.

#### Delete Range (from, to)
if `from` run is `to` run
	remove [`from` offset, `to` offset) from the run's text
	place the caret at (`from` run, `from` offset) and return
`from` run text = its text before `from` offset
`to` run text = its text from `to` offset
destroy every run strictly between them in reading order
if the two runs are in different blocks, [[#Merge Into Head]] (`from` block, `to` block)
if `to` run is now empty, destroy it
place the caret at (`from` run, `from` offset)

#### Merge Into Head (head, tail)
move every surviving run of `tail` into `head`, in order
destroy every block from the one after `head` through `tail`
`head` apply layout

An emptied *tail* run is destroyed and an emptied *head* run is not, which looks arbitrary and is not. The caret has to land somewhere and a run carries the style the next typed character takes, so keeping the head run keeps the formatting at the caret the way every editor does; handing the caret to a neighbour would silently change it. The head run surviving is also what guarantees a block never ends up with zero runs, which is the case that would leave `Normalize`, `OrderedRuns` and `RunAt` all answering for nothing. The tail run has no such claim, and leaving it behind grows the saved note by a `<Run />` that reads back as real.

The merged block keeps the **head** block's styling type — deleting from a heading into the paragraph below leaves a heading. `ApplyLayout` re-runs on it afterwards, or the runs that moved across would keep the size the scheme resolved for the block they came from.

#### Split Block
delete the selection, if any
`tail` = new content block with the caret block's styling type
`carried` = clone of the caret's run, holding its text from the caret on
caret run text = its text before the caret
`tail` add `carried`, then every run after the caret's run in its block
if `carried` is empty and other runs moved in, destroy `carried`
the model inserts `tail` after the caret's block and raises `Inserted`
the view adds a control for `tail` and applies layout to it
place the caret at (`tail` first run, 0)

Enter's new block takes the old block's styling type rather than resetting to body text: splitting a heading mid-word has to give two headings, and "Enter at the *end* of a heading gives a paragraph" is a second rule keyed on caret position that can be added to `Split Block` later if it is wanted. A split used to be the one caret-moving operation that could not scroll, because the new block has no arranged rect until the next layout pass and a zero rect reads as "above the viewport", which threw the note to the top. That is why the scroll is now deferred rather than immediate — see [[#Scrolling to the caret]].

Blocks live in the model — `RichTextDocument.blocks`, which is what a save is written from — and the `DocumentControl`'s children are a view of them that layout and hit-testing walk. A structural edit changes only the model, which raises a `NoteChange`, and the view adds or removes its `BlockControl`s in answer; the view never edits the block list itself, so a second view of the same note can follow the same changes.

## Undo puts the selection back
Undoing a range delete re-inserts the text and then selects it again, with the caret on the end it was on before — a cut, a Delete over a selection or typing over one all come back highlighted. The delete record carries the anchor and caret it was made with. Backspace and Delete with nothing selected also delete through a one-character selection, but that selection was never the user's, so they record the anchor (where the caret really was) as both ends and undo puts back only the caret, on the correct side of the restored character.

## Clipboard
Ctrl+C, Ctrl+X and Ctrl+V are the `Text.Copy`, `Text.Cut` and `Text.Paste` actions. None of them knows about notes: each walks up from the active control to the first one implementing `IClipboardTarget` and asks it. The note editor and the single-line field are the two that do today, and a field that is not being edited says no so the walk carries on past it. The OS clipboard is read and written as plain text through GLFW (`ClipboardText`); a test run keeps it inside the process so it never overwrites the user's.

A copy puts plain text on the clipboard, one line per block, and also keeps the copied range as a document fragment, formatting and all, inside the engine. A paste compares the clipboard's text with that last copy: if they match, the fragment is pasted, so bold, colours and headings survive anywhere in the same app — another tab, a split, a torn-off window. Anything else is plain text, and each line becomes a paragraph in the style and block kind at the caret.

#### Paste Text (text)
delete the selection; if it cannot be deleted, stop
`fragment` = `text` is our last copy ? the copied fragment : paragraphs from `text`
if `fragment` has several blocks, its last block takes the caret block's kind
record an insert from the caret to where `fragment` ends
insert `fragment` at the caret
place the caret at the end of it

The last-block rule is Word's: the last pasted paragraph has no paragraph end of its own, so it joins the paragraph it lands in and takes that paragraph's heading or list setting. Undo of a paste deletes exactly the inserted range and redo re-inserts it — the insert is the delete's mirror image, and the two share `InsertFragment`. A selection that crosses a table's edge copies (cells come out as paragraphs) but does not cut, since it cannot be deleted either.

The single-line field has its own small history: each change records the field's text, selection and caret before and after, and pasted line breaks become spaces. It starts empty each time the field is focused, committed or cancelled.

## Dragging text
Pressing inside the selection picks it up instead of moving the caret. While it is dragged, whichever note the pointer is over shows a second caret where the text would land and scrolls when the pointer is past its edge. Releasing inside the same note moves the text as one undo step and leaves it selected; releasing in another note inserts it there and deletes it from the source, each note recording its own half. Holding the `Copy` named modifier (Ctrl) at the release copies instead of moving. Releasing inside the selection itself, or clicking it without dragging, just places the caret.

#### Finish Drag (dragged, point)
`source` = the note the drag came from; stop if it is not a text drag
`slot` = the caret slot under `point`
if `source` is this note
	under one step: move or copy the selection to `slot`
	if `slot` was inside the selection, place the caret there
else
	under this note's step: insert the source's selection at `slot`, selected
	unless copying, under the source's step: delete its selection
take the focus

What the engine drags is the source note's drop marker, not the editor. A drag hides the dragged control from its own hit test so it does not find itself under the pointer; dragging the editor would hide the whole note, and the text could never be dropped back into it. The marker has no children, so hiding it hides nothing else, and the same drop code then serves the same note, another tab and another window.

## Styling what has not been typed yet
A style picked with **nothing selected** is armed rather than discarded: bold, italic, a size from the format bar's px field or a colour are held on the `DocumentControl` as a nullable `StyleDelta` and spent on the next character typed. It is one field, `pending`, and it survives only where it was set — every caret move funnels through the three-argument `SetCaret`, which clears it, so clicking or arrowing away drops the arm the way every editor does. The one-argument overload does *not* clear, and that asymmetry is what lets the px field take the active control to be typed into and hand it back through `FocusCaret` without disarming the size it just set.

Arming merges into whatever is already armed, so bold then italic types both; and an arm that agrees with the run the caret sits in is dropped, which is what makes a second Ctrl+B disarm rather than pin the run's own style as a change.

Spending it needs no machinery of its own. `TypeChar` writes the character into the run the caret is already in, exactly as it did before, and then restyles that one character through the ordinary range primitive — so the split, the merge and both undo records are the ones a selection restyle already makes, and they join the same `Typing` step. Undo reverses within a step, so the style record unwinds the partition first and the text record removes the character second, leaving the caret where the character was. The cost is one split/apply/merge over one block, paid once: the second character lands in a run that already carries the style.

What the format bar reflects is the *resolved* style — a `CaretStyle` of the caret's run with the arm laid over it — rather than the run's own, so B lights the moment it is pressed and not only once something has been typed. The alternative shapes were both worse: a detached `TextRun` holding the style would have been an `Entity`, allocating a pool slot and ticking for as long as it was armed, and inserting an empty styled run into the block at arm time collides with `Normalize` walking past zero-length runs, with `MergeRuns` folding it away, and with an unspent arm leaving a stray `<Run />` in the saved note. See `DOCUMENTATION/ClaudeMemory/Decisions/armed-style-at-the-caret.md`.

### Keeping the style through Enter and Backspace
An arm lasts through typing, Enter, Backspace and Delete, and is dropped by anything that moves the caret somewhere else: an arrow or Home/End key, a click, a double or triple click, select-all, undo and redo, and a paste or drop. Edits move the caret too, which is why the arm is cleared at those navigation entry points rather than in `SetCaret`.

Enter keeps the style because a split that leaves either half empty gives that half the style at the split point, not the block's default — so Enter at the end of bold text starts a bold paragraph. Deleting keeps it because the range delete arms the style of the first character it removed whenever that differs from what the caret would now type; a style picked outright still wins. Backspacing out a whole bold word therefore keeps typing bold, and backspacing the plain letter in front of bold text keeps typing plain.

## Decorations and colour
A span carries `underline`, `strikethrough` and `highlightHex` beside its colour. None of them is a control: the text run writes them as flat rectangles into the same draw list its glyphs go into. For each segment of a line, the highlight goes in first, behind the glyphs; the underline and the strike line go in after them, in the text's own colour or gradient, sized as fractions of the font size.

#### Emit — one segment
if the segment is highlighted
	if a selection covers part of it, write the highlight on either side of the selected characters
	otherwise write it across the whole segment, line top to line bottom
write the segment's glyphs
if underlined, write a bar just below the baseline, as wide as the glyphs advanced
if struck, write a bar through the middle of the lower-case letters

The selection boxes sit behind the text, so a highlight drawn over them would hide the selection. Instead `ArrangeSelection` tells each selected block which characters are selected, and the highlight leaves those out.

Text colour and highlight each have a dropdown on the format bar: presets, and under them a [[Color Picker Control]]. Ctrl+U toggles underline. In a Markdown note these save the way Obsidian reads them — `<span style="color:#…">`, `==…==` for the default highlight, `<mark style="background:#…">` for any other, `<u>` for underline — and read back from the same forms. See `DOCUMENTATION/ClaudeMemory/Decisions/text-decorations-and-colour.md`.

## Tables
A table is a `NoteTable` sitting in the note's block list beside ordinary `NoteBlock`s, so `RichTextDocument.blocks` holds either kind; the `TableControl` is its view, built from it by `TableControl(NoteTable)`, and the column widths, rules, spans and padding live on the `NoteTable`, which the control reads and writes directly. `DocumentXml` normalises a table as it reads it: a short row is filled, spans are clamped to the columns and every empty cell gets one empty block. The `TableControl` inherits `GridListControl`: the columns are Fixed widths in design pixels scaled by the document zoom, the rows are Auto, and each cell is a vertical `StackPanelControl` of ordinary `BlockControl`s. A cell is a small stack of paragraphs rather than one block with hard line breaks, so Enter in a cell is the same split every paragraph already uses, and undo, joins and styling need nothing new.

Columns may add up to less or more than the page is wide. Every table sits in its own horizontal-only `ScrollableControl`, so a table wider than the page scrolls sideways on its own while the note keeps its width; moving the caret into a hidden column scrolls the table to it. A vertical wheel never scrolls a sideways viewport — it passes through to the note — and a horizontal wheel over the table scrolls the table.

The caret and every edit address a block by its position in `DocumentControl.Blocks()`, and that list now walks into tables: the note's blocks and every cell's blocks, row by row, left to right. Nothing about an address had to change — arrow keys, up and down by position, styling a range and every undo record already worked over that list. Tab and Shift+Tab step to the start of the next or previous cell, and Tab in the last cell adds a row and moves into it. Inside a list item, Tab at the item's very start nests it instead, the way Word does; anywhere else in the item it still steps cells.

Lists work inside a cell: `- `, `1. ` and `[ ] ` typed at a cell line's start make a list there, the checkbox finds its editor however deep it sits, and an item only nests under an item of the same cell. Headings and quotes can be typed in a cell too; code fences and rules cannot.

A delete is refused when its range is not all inside one container — the note itself, or one cell. That covers Backspace at a cell's start, Delete at its end, and a selection dragged across a table; the caret stays where it was.

A table splits across pages between rows. A row that would cross a page break is pushed to the next page, and the push is stored as the gap after the row above it, so the grid's own arrangement places it. A row taller than a page is not split and runs across the break.

#### Blocks ()
for each child of the document
	if it is a block, add it
	else if it is a table's viewport, add every cell's blocks in reading order

#### Paginate (top, bands) — a table
`y` = `top`
for each row
	if it is not the first row
		`pushed` = `bands` push (`y`, row height)
		gap after the previous row = `pushed` − `y`
		`y` = `pushed`
	`y` += row height
gap after the last row = 0
return `y` − `top`

#### Arrange Borders ()
for each cell
	`box` = the cell's rect with its inset added back
	line along the top of `box`, and down its left side
	if the cell is in the last column, line down its right side
	if no row follows directly below, line along the bottom of `box`

On disk a table is `<Table>` holding `<Column Width>` elements and `<Row>`s of `<Cell>`s, each cell holding `<Block>`s written exactly as the note's own. Tables live only in `.axml` notes; the Markdown and plain-text writers skip them. A `<Cell ColumnSpan="n">` spans n columns and `<Table Borders="false">` draws no borders; both are absent when the span is 1 and borders are on.
A cell may span several columns. Rows are stored short, with no placeholder cells behind a spanning cell, and the cell at a grid position is found by its spans, so inserting a column inside a span widens it and deleting one inside a span shrinks it. The Table submenu's Merge cell right makes the caret's cell absorb the next cell in its row, appending its blocks and dropping an empty side; it is refused on a row's last cell. Split cell turns a merged cell back into single cells with the content in the first, and is refused on a single cell. Each is one undo step. Row spans are not supported.
A table can also carry rules, padding and an alignment, which a LaTeX preview uses to draw a tabular as LaTeX does. `<Table Align>` sets centred or right alignment, and the table is then narrower than the page and moved across it. `<Table Padding="across down">` replaces the 6 px cell inset. `<Column RuleLeft RuleRight>` draws a plain or double rule down a column's edge, and `<Cell RuleAbove RuleBelow>` draws a rule across a cell's top or bottom, named by kind (Plain, Double, Heavy, Light or Cmid) and optionally a width in pixels, so `Heavy` or `Heavy 3`. `<Cell TrimAbove TrimBelow>` pulls a rule's ends in (Left, Right or Both), which is how a `\cmidrule(lr)` is drawn. Rules are drawn in the ink colour, at least one pixel thick, scaled with the zoom; rules that touch are drawn as one line so no seam shows, and vertical rules stop at a page gap. A new row or column has no rules, deleting one drops its rules, merge keeps the left cell's, and split copies them to both halves, the Left trim to the first piece and the Right trim to the last. Nothing in the editor creates these rules or sets the alignment yet.

### Editing a table
Right-click ▸ Insert table puts a three-by-three table after the caret's paragraph, its columns splitting the text width evenly. A `.md` or `.txt` note refuses it, because it could not save it, and so does a caret already inside a table. When the caret's paragraph is the last thing in the note it is split at its end first, so there is always a paragraph after a table to type into. The Table submenu inserts a row above or below the caret's, a column left or right of it, and deletes the row, the column or the whole table; deleting the last row or column deletes the table.

Every one of those is one undo step holding a `TableEdit`: the table's XML before and after, and where the caret was. A command does not edit the grid in place. It writes the table out as XML, changes the copy, and builds a fresh table from it, so the forward edit, undo and redo all take the same road the file loader does, and the caret is put back in the same cell, line and offset afterwards.

Each column's right edge is a grip. Dragging it widens or narrows that column alone, live, and letting go records one undo step.

#### Change Table (change)
if the caret is not in a table, do nothing
remember the caret's row, column, line in the cell and offset
`before` = the table as XML
`edited` = a copy of `before`, changed by `change`, which says which cell the caret lands in
replace the table with one built from `edited`
put the caret in that cell, at its old line and offset when it is the same cell
record `before`, the rebuilt table as XML, and both caret positions

#### Insert Table ()
if the note is `.md` or `.txt`, or the caret is in a table, refuse
if the caret's paragraph is the last thing in the note, split it at its end
build a 3 × 3 table of empty cells, every column a third of the text width
put it after the caret's paragraph and the caret in its first cell

#### Resize (pointer x) — a column grip
`width` = the width at the grab + (pointer x − x at the grab) ÷ zoom
`width` = at least 24, rounded to a whole pixel
set the column to `width` and lay the table out again

See `ClaudeMemory/Decisions/document-tables.md`.

## Pictures
A picture in a note is one character, U+FFFC, whose style span names a picture file — Word's model, where every picture is anchored to a place in the text. Because it is a character, the caret steps over it, a selection covers it, Delete removes it and undo puts it back without any of that code knowing pictures exist. The span holds the file path (absolute in memory, relative to the note on disk) and an optional width and height; with no size the picture shows at its own size, shrunk to the column, and with one side set the other follows its aspect.

Inline, a picture is as wide as it is drawn and stands on the baseline, so a tall one makes its line taller. A line may break on either side of it, and unlike a trailing space it never hangs past the margin: a picture that does not fit moves to the next line and leaves the word in front of it behind. It is drawn as one image quad in place of a glyph, with no highlight or decoration.

A picture span never merges with its neighbours and never grows. Typing next to one lands in the text span beside it, or in a new text span in the picture's style when there is none; the style a caret takes next to a picture is that style with the picture taken off.

Ctrl+V pastes text when the clipboard holds text, and a picture only when it holds no text. A copy that is text or a table always offers text, and a table has nothing that pastes it yet, so it lands as its text. A screenshot, a copied picture or an image file in Explorer offers none. The picture is saved as a PNG in an `attachments` folder beside the note, named after the note and the time, and goes in at the caret as one undo step. A plain-text note refuses it.

#### Paste Image (image)
if the note is plain text, refuse
save `image` as `attachments/<note> <time>.png` beside the note
delete the selection; if it cannot be deleted, stop
`span` = the caret's style with the picture file set and a count of one
record an insert of U+FFFC with `span` at the caret
insert it

#### Insert Text (offset, text) — beside a picture
`span` = the span the offset belongs to
if `span` is a picture
	if the offset is before it, `span` = the text span in front, or a new empty one in the picture's style
	else `span` = the text span after it, or a new empty one in the picture's style
grow `span` by the text's length

On disk an `.axml` note writes a picture as `<Run Image="attachments/…png" Width Height/>`, with no `Text`. Markdown writes Obsidian's `![|W](path)`, with spaces as `%20`, and reads `![alt|W](path)` and `![alt|WxH](path)`. Plain text drops pictures. See `ClaudeMemory/Decisions/note-images.md`.

### Wrapping text around a picture
Right-click a picture and choose Wrap text: In line with text, Square, Tight, Top and bottom, Behind text or In front of text. Any choice but the first makes the picture float. Its character stays in its paragraph, so the picture still belongs to that paragraph, moves with it and is deleted with it, but the character takes no room in the line. The picture is placed off the paragraph instead, by an offset from the column's left edge and the paragraph's top. Switching a picture to floating keeps it where it stood in the line, and switching it back clears the offset. Each change is one undo step. Pictures in table cells stay in line.

Square keeps text out of the picture's box plus an 8-pixel gap; Tight keeps it out of the picture's opaque outline instead, so text follows a round or cut-out picture's edge, and a picture with no transparency wraps exactly as Square does — the outline is read off each pixel row when the picture is loaded, since its pixels are freed once they are on the GPU; Top and bottom keeps text off the whole width beside it; Behind and In front leave the text alone and only decide whether the picture is drawn under it or over it. Where a picture leaves room on both sides, a line takes the wider side only. A line that would be squeezed under 48 pixels moves down past the picture instead.

A line's width now depends on where in the document it lands, so a paragraph a floating picture reaches is laid out and placed on the pages in the same pass, line by line from the top. Every other paragraph is measured once and placed afterwards exactly as before, and a note with no floating pictures runs the old code throughout. A floating picture is only ever at or below its paragraph's top, which is what lets the pass go top to bottom once.

#### Place (y, height) — a line in a paragraph a picture reaches
`top` = the paragraph's top + `y`
repeat
	`top` = moved to the next page if a line this tall would cross the page's bottom
	`gaps` = the paragraph's column, from its indent to its right edge
	for each wrapping picture overlapping `top` to `top` + `height`
		cut its box plus the gap out of `gaps` — or everything, for Top and bottom
	if no picture overlapped, or the widest gap is wide enough
		return `top` and the widest gap
	`top` = the nearest bottom of the pictures that overlapped

Markdown writes a floating picture as `<img src width height data-wrap data-x data-y>`, which Obsidian shows as a picture in place; the wrap and offset ride along in the data attributes.

### Moving a floating picture
Press on a floating picture to select it, then drag it to move it; the cursor shows a four-way arrow over one. The picture follows the pointer and the text reflows around it as it goes. It cannot leave the paper, margins included. On release the picture is handed to the paragraph it now sits beside — the last one whose top is at or above the picture's top — and its offset is measured again from that paragraph, so a picture is never above its own paragraph. That is what lets the layout pass run top to bottom once: a paragraph only ever needs to know about pictures belonging to it and the paragraphs above. While dragging, a picture held above its old paragraph does not push the text above aside yet; that happens on release. Moving within the same paragraph, or into another, is one undo step either way.

#### End Move (picture)
`top` = the picture's top in the document
`target` = the last paragraph outside a table whose top is at or above `top`, or the first paragraph
if `target` is the picture's own paragraph
	record the offset change
else
	put the picture back as it was when the drag began
	delete its character, recorded
	insert it at the start of `target` with its offset from `target`'s top, recorded
	select it

### Resizing
A click on a picture selects it: the selection becomes exactly its one character. Whenever the selection is exactly one picture — by a click, by Shift+arrows, by undo — the picture gets a thin frame and eight square handles, so there is no separate "selected picture" to keep in step with everything else. Delete, copy and dragging it to move it work on it as they would on any one-character selection, and a click on the already-selected picture leaves it selected.

A corner handle keeps the picture's shape, scaling by whichever direction the pointer moved further; a side handle stretches one direction only; Shift with a corner resizes freely. An inline picture's left and top edges are fixed by the text around it, so dragging the left or top handle outward grows it to the right or downward. A picture is never narrower or shorter than 8 pixels and never wider than its column. The size is stored without the document zoom, in whole pixels; a picture that follows its own shape and is resized by a corner keeps storing the width alone, so its Markdown stays `![|W]`. A whole drag is one undo step, and undo leaves the picture selected.

#### Resize Picture (handle, pointer, free)
`dx` = how far the pointer moved across since the press, counted outward from the handle's side, or 0 for a top or bottom handle
`dy` = the same downward, or 0 for a left or right handle
if `handle` is a corner and not `free`
	`scale` = (width + `dx`) ÷ width if `dx` moved further, else (height + `dy`) ÷ height
	clamp `scale` so neither side drops under 8 px and the width stays inside the column
	new size = the size at the press × `scale`
else
	new width = width + `dx`, between 8 px and the column
	new height = height + `dy`, at least 8 px
store the new size ÷ zoom, rounded; a corner on a picture with no stored height stores the width only

### Rotating
A selected picture also gets a thin ring around it, a little outside its corners. Press on the ring and drag around the picture to turn it; the cursor is a crosshair over the ring. The turn follows the angle the pointer has swept about the picture's centre since the press, in whole degrees, and Shift steps it by 15. The whole drag is one undo step. The angle is stored in clockwise degrees — `Rotation` on the run in an `.axml` note, `data-rotate` on an `<img>` in Markdown — but everything the engine does with it, from drawing to hit-testing to wrapping, works on a quaternion made from it.

The frame and handles turn with the picture, and resizing works along the picture's own sides: dragging a turned picture's handle stretches it along that side, and the opposite handle stays where it is on the page. Changing how text wraps keeps the turn.

An inline picture that is turned takes its turned bounding box in the line, so it never overlaps the text beside it and a picture turned on its side makes its line as tall as it is long. Because Markdown's `![|W]` has no room for an angle, a turned inline picture is written as `<img src width height data-rotate>` with no wrap.

A floating picture pushes text away from its turned bounding box. A Square picture has a collision option, set from the right-click Collision menu: Bounding box, the default, or Picture shape, which follows the tilted rectangle itself so lines can come closer beside its corners. Tight always follows the picture's own opaque outline, turned with it, and Top and bottom still clears the full width over the box's height. A floating picture is never allowed above its paragraph's top, and that now means its turned box: a turn that lifts the box above the paragraph hands the picture to the paragraph above, the same way dropping a moved picture does.

#### Rotate Picture (pointer, snap)
`from` = the direction from the picture's centre to where the ring was pressed
`to` = the direction from the centre to the pointer
`turn` = the quaternion that turns `from` onto `to`
`q` = the stored turn's quaternion followed by `turn`
`degrees` = twice the angle of `q` about its axis, rounded — to 15 if `snap`
store `degrees`, kept between 0 and 359

#### Outline (picture, line top, line bottom) — Picture shape and Tight
for each part of the picture — the whole rectangle for Picture shape, each opaque pixel row for Tight
	turn the part's four corners about the picture's centre
	take the corners that lie between the line's top and bottom
	take where each of the part's edges crosses the line's top or bottom
	widen the cut to every x taken

## Status
- P0 (model types) and P1 (XML persistence) complete; round-trip verified (in-code build + reload of code-built and hand-authored XML are byte/structurally equal).
- P3 complete: click→caret, character input, arrow / Home / End / PageUp / PageDown navigation, and Ctrl+S through `DocumentEditSession`. Save verified against the sample note — no run gains a `FontSize`. Navigation itself is compile-verified and pending GUI verification.
- P2 (`DocumentEditorControl`) implemented; built into `Thorium/Data/XML/Documents/UI/UI.ui.axml` via `<DocumentEditor Source="SampleNote.axml"/>`.
- P4 steps 1 and 2 complete: selection renders and is GUI-verified apart from drag auto-scroll, which the sample note is too short to exercise; deletion over a range, Backspace, Delete and Enter are bound and boot-verified but **not** GUI-verified. Step 3, Ctrl+B/I run split/merge, is next — `Bold` and `Italic` are still read by nothing.
- Undo and select-all do not exist, which deletion is the first feature to make matter: a mis-aimed delete is recoverable only by reloading the note.
- P5 complete: `Thorium` is a two-pane shell, a `VaultBrowser` listing a vault folder beside the editor, and `LoadPath` has a real caller at last. The vault is a settings path; the browser, being app rather than engine, lives in `Thorium` and is described in `DOCUMENTATION/ClaudeMemory/Decisions/vault-browser-and-shell.md`. Switching notes saves the one being left, since nothing tracks dirtiness and nothing can undo.
- Bullet and task lists with nesting landed 2026-09-17, with `.md` and `.txt` notes — builds and boots, NOT GUI-verified. Numbered lists landed with list markers; wiki-links: not yet — added as the editor grows. Markdown as you type, code blocks (font, ground, language; no syntax colouring, still wrapped), rules and left/centre/right alignment landed 2026-10-01 — see [[#Markdown as you type]]; tables landed 2026-09-29 with no insert UI — see [[#Tables]]. L2 is dropped, L3 (paged mode) is unaffected — see [[Document Layout Engine#Status]]. Revised phase order: `DOCUMENTATION/ClaudeMemory/Context/thorium-editor-architecture.md`.
