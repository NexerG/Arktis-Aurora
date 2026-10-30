---
date: 2026-08-31
Status: Current
tags:
  - d_UI
  - d_Entity
cssclasses:
  - Aurora.css
Linker:
  - "[[Entity]]"
System:
  - "[[SESSION]]"
Class:
  - "[[Workspace]]"
Parent Class:
  - "[[Panel]]"
Interfaces:
Used by:
  - "[[Thorium]]"
Type:
  - Public
Attributes:
  - A_XSDType("Workspace", "UI")
Namespace: ArctisAurora.Core.UI
SourceFile: AuroraEngine/Core/UI/Containers/Workspace/WorkspaceControl.cs
VerifiedAgainst: 2026-08-31
---
## Description

The place in a window where panes go, and the only thing [[SESSION]] needs in order to know where to put a restored arrangement.

It holds workspace pages and paints nothing — its mask is `invisible`, like every structural container — so it is invisible in a layout except as the slot the shown page fills. A page is a `WorkspacePageControl`: a name (`title`), a `kind` and one pane tree (`content`). Only the shown page is measured and arranged; the others stay alive and hidden, the way inactive tab pages do, so a note open in a hidden workspace keeps its undo history.

What the workspace adds beyond that is three names: `Default`, the UI document built into a page when the session has nothing recorded for this window, `Pane`, a document holding one empty pane that a restored arrangement splits to rebuild itself, and `FirstRun`, the space-separated list of workspace kinds a window starts with.

Declaring one is a single element. In [[Thorium]] the main window's whole editing area is one `<Workspace>` element with `Default="workspace"`, `Pane="tab-pane"` and `FirstRun="General Docs Sheets LaTeX Manager Calendar"` so a fresh window starts with every kind, and the two-pane arrangement lives in the `workspace` document.

## Pages and kinds

`WorkspaceKind` is General, Docs, Sheets, LaTeX, Manager or Calendar. The kind decides which files the vault browser lists while the page is shown: Docs lists notes that are not sheets, CSV, planners or `.tex`, Sheets lists sheets and CSV, LaTeX lists `.tex`, Manager and Calendar list planners, General lists everything. A planner opened while a Calendar page is shown starts in its Calendar view.

`WorkspacePageControl.Of(control)` returns the nearest page above a control, which is how a tab strip or the vault browser learns whether its view is on the shown page.

The page is code-only, with no XML element. It exists because `SplitView`'s `Split` and `Collapse` swap the single child of their host: one stable single-child host per workspace leaves those rules unchanged.

## The tab bar

`WorkspaceBarControl` is the strip of tabs in the title bar, one `WorkspaceButton` per page of the workspace in its own window. The shown tab paints a ground with the tab accent, the others are clear with muted ink. A press shows the page, a double-click renames it in place, a right-click opens the `workspace` context menu, and the "+" button next to the bar opens `workspace-add`.

A tab can be dragged: dropping it in its own bar reorders it, dropping it on another window's bar moves the workspace, panes and tabs included, into that window, and dropping it outside every app window tears it off into a new window built from the bar's `TearOffDocument`. A 2 px accent marker shows the gap while the tab is over a bar. The move happens on drop and not live, because the bar rebuilds on every `changed` and that would destroy the dragged button mid-drag. The primary window always keeps its last workspace; a secondary window that loses its last one closes.

The bar rebuilds when the workspace raises `changed`, posted to the main thread rather than run in place, because a rename commits from inside the caption a rebuild would destroy.

## Tab-row tools

Each pane's tab row carries the tools of its active editor at the right, mounted by `TabViewControl.SyncTools` from the editor's `tools`. A note gets bold, italic, underline, align left, bullets and Paste link, a sheet gets bold, the Format and Fill menus, Paste link and Sum, a LaTeX file gets Build, Live, the error count, Sync and zoom. The tools are `TabToolsControl` buttons that act on press and never take the active control, so the caret stays where it was.

A pane also has a "+" that makes a file of the workspace's kind: a note in Docs, a sheet in Sheets, a `.tex` file in LaTeX, a planner in Manager and Calendar, and in General a menu of kinds. A sheet has a 24 px formula bar with the cell name, an fx mark and the formula field.

## Ribbon and inspector

Docs and Sheets workspaces get a 30 px ribbon under the title bar and a 240 px inspector column to the right of the panes, behind a splitter that can be dragged. Docs has the categories Format, Insert and Page, Sheets has Format, Insert and Data, and a toggle at the right of the ribbon shows or hides the inspector. The chosen category and the toggle belong to the page (`ribbonCategory`, `inspectorShown`) and are saved in the session. In a workspace the ribbon covers, the tab-row tools give way to it; in General, in torn-off windows and for a sheet inside Docs they stay.

Both act on the active editor of the focused pane of the shown page. Docs shows Paragraph and Page in the inspector, Sheets shows Cell and Layers. The ribbon's buttons never take the active control, the same as the tab-row tools, so an entry first gives the note its caret or the sheet its grid. A few entries needed logic that did not exist before: numbered and task lists, Picture… which copies a chosen file into the note's `attachments` folder, Import CSV… which adds a page to the open sheet, and Convert CSV to sheet, which is a Thorium action.

### FocusedView
```
FocusedView(page)
	if TabViewControl.focused is on page
		return TabViewControl.focused
	if page.lastFocused is not null
		return page.lastFocused
	return the first pane of page
```

## API summary

| Member | Kind | Summary |
| --- | --- | --- |
| `defaultDocument` | `Default` attribute | UI document built into a page when the session has nothing for this window. |
| `paneDocument` | `Pane` attribute | UI document holding one empty pane, which a restored arrangement splits. |
| `firstRun` | `FirstRun` attribute | Kinds a window starts with when nothing was saved; General alone when left out. |
| `shown` | property | The page on screen. |
| `Pages` | property | The pages, in tab order. |
| `AddPage(title, kind)` | method | Adds a page; the first is shown, later ones hidden. |
| `Show(page)` | method | Hides the shown page and shows another. |
| `Rename(page, title)` | method | Renames a page. |
| `RemovePage(page)` | method | Removes a page; refuses the last one. |
| `Move(page, index)` | method | Reorders a page within the bar, or moves it in from another workspace at a gap index. |
| `changed` | static event | Raised with the workspace when its pages change. |
| `LoadDefault()` | method | Parses `Default` into a page of the first `FirstRun` kind and adds an empty pane to each further kind. |
| `LoadEmpty()` | method | One page per `FirstRun` kind, each with one empty pane. |
| `LoadPane()` | method | Parses `Pane` into the shown page, or into a new General page, and returns it for a restore to build on. |
| `LoadPane(page)` | method | The same, into a given page. |
| `In(control)` | static | The workspace somewhere under a control, or null for a window that declares none. |

## Methods

### AddPage
```
AddPage(title, kind)
	page = new WorkspacePage(title, kind)
	AddChild(page)
	if shown is null
		shown = page
		page.isShown = true
	else
		page.isShown = false
	raise changed
	return page
```

### Show
Remembering the focused pane is what makes switching back land where the reader was.

```
Show(page)
	old = shown
	old.lastFocused = the focused TabView under old
	old.isShown = false
	shown = page
	page.isShown = true
	if page.lastFocused is not null
		ActiveControl = page.lastFocused.editor
	else
		ActiveControl = the first pane of page
	raise changed
```

### Move
A page leaving another workspace is detached with RemoveChild, which does not tell that workspace, so the source is settled explicitly.

```
Move(page, index)
	if page is a child of this
		old = index of page
		if index > old -> index = index - 1
		place page at index among the children
		raise changed
		return
	source = the workspace page is in
	if source is on Engine.primary and has one page -> return
	oldIndex = index of page in source
	source.RemoveChild(page)
	AddChild(page)
	place page at index among the children
	Show(page)
	source.Released(page, oldIndex)
```

```
Released(page, oldIndex)
	if shown is page -> shown = null
	if no pages remain
		if the window is not the primary -> Engine.CloseWindow(the window)
		return
	if nothing is shown -> Show(the page now at oldIndex)
	raise changed
```

### RemovePage
```
RemovePage(page)
	if Pages has one entry -> return
	wasShown = page is shown
	neighbour = the page next to it
	detach page
	if wasShown -> Show(neighbour)
	raise changed
```

### LoadDefault
```
LoadDefault()
	if defaultDocument is empty -> return
	kinds = firstRun split on spaces, or General alone
	page = AddPage(name of kinds[0], kinds[0])
	page.AddChild(ParseXML(defaultDocument))
	for each further kind in kinds
		AddPage(name of kind, kind).AddChild(an empty pane from paneDocument)
```

### LoadEmpty
```
LoadEmpty()
	for each kind in firstRun, or General alone
		AddPage(name of kind, kind).AddChild(an empty pane from paneDocument)
```

### LoadPane
Returns the pane rather than just attaching it, because the caller is about to split it and needs the handle.

```
LoadPane()
	page = shown, or a new General page
	return LoadPane(page)

LoadPane(page)
	if paneDocument is empty -> return null
	parsed = ParseXML(paneDocument)
	if parsed is not a TabView -> return null
	page.AddChild(parsed)
	return parsed
```

### In
```
In(control)
	if control is null -> return null
	if control is a Workspace -> return control
	for each child of control
		found = In(child)
		if found is not null -> return found
	return null
```

One workspace per window is the assumption everywhere, and the first found in depth-first order is the one that is used.

## Why the seed is a document and not children

The panes an application authors are its first-run arrangement, and once a session exists they are not built at all. Leaving them inline as the workspace's XML children would mean parsing them on every boot and destroying them immediately — and those children carry `DocumentEditor Source=`, so that is two whole notes turned into control trees and thrown away, at one control per glyph. Naming a document instead means the seed is only ever read when it is used.

It also makes the arrangement's status legible. `Default="workspace"` says out loud that the document is a fallback, where a subtree that restore silently replaced would have made editing it look broken.

## Why `Pane` is separate from `Default`

A pane is not a bare `TabView`. It carries tab metrics, three colours, a `TearOffDocument` and a context menu, all authored, and a restored arrangement needs panes that have all of it. Building one in code would carry none of it.

Only one has to be parsed. [[SplitView]] copies the source pane's kind and every one of those attributes onto each pane a split creates, so a single seed grows into an arrangement of any depth with the chrome intact. For a window whose default *is* one empty pane — a torn-off window — `Default` and `Pane` simply name the same document.

## Who fills it, and when

Not the control. A workspace is filled by an explicit call right after its window's root has been assigned, from the three places that assign one: the host at startup, [[SESSION]] for each window it reopens, and `TabViewControl.TearOff` for a window a tab has just been dragged into.

It cannot fill itself during parsing, because the record it would need is per window and a tree is not attached to a window yet. It cannot do it from `OnStart` either — `Engine.Interpolate` drains that queue with a `foreach`, and building a document creates entities.

The session records one `SessionWorkspace` per page, with its title, kind and pane tree, and which one was shown. A record written before workspaces existed loads as one General page and is written back in the new shape.

Reset UI (`Session.Reset`, in Thorium's app menu) is the fourth caller. It settles unsaved notes, empties the primary's workspace, closes every other window holding one except stickies, and calls `LoadDefault` on the primary again.

## Known holes

Each page is a single-child host, which exposed an ordering defect in [[SplitView]]'s collapse: the survivor of a collapsing split was re-parented into the host while the split was still a child of it. Every other host was a `StackPanel`, which tolerates the transient second child. Collapse now detaches the split first, matching what its own `Split` had always done.

Nothing enforces one workspace per window; a document declaring two would have the second one ignored by everything that goes looking.

An empty pane shows nothing at all. Duplicating a page keeps its title.

## Related
- [[Thorium]] — hosts the ribbon, the inspector and the vault browser whose filter follows the shown page
- [[SESSION]] — what fills a workspace, and the record it is filled from
- [[SplitView]] — how a seed pane becomes an arrangement, and what it copies onto each pane
- [[Tab View]] — what a pane is
- [[UI Document]] — how `Default` and `Pane` resolve a name to a file
