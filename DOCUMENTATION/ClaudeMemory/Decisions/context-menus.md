# Decision — a context menu is a document a control names, and it is hosted wherever it fits

**Date:** 2026-09-11
**Scope:** `ArctisAurora.Core.UI` — `ContextMenus`, `ContextMenuControl` (+ nested `Row`), `ContextMenu`,
`ContextMenuEntry` / `ContextMenuButton` / `ContextMenuLine` / `ContextMenuSubmenu`, `Control` (`contextMenu`,
`stopsContextMenu`, `ParseMenu`, `RecursiveParse`), `UIEngine` (`SolvePress`, `SolveRelease`), `ButtonControl`;
`ArctisAurora.Core.Registry.Assets.ContextMenuAsset`; `Core.UI.WindowActions.Acting`; `Engine.MainTick`.
2026-09-12 (landing 6b3): `MenuButtonControl`, `DropdownControl`, `TabViewControl.tabContextMenu`,
`Core.UI.TabActions`, `ViewActions`, `UIActions.Invoking`
2026-10-07 (planner P2): `ContextMenus.OpenFrom`, `RegisterSource`, `ContextMenuSubmenu.source`, `DropdownControl`
2026-10-23: `ContextMenus.RegisterCondition`, `ContextMenuEntry.when`, `DocumentEditorControl` (static ctor)

Designed fresh from the user's spec (2026-09-11) — the old stack's `ContextMenus` was deliberately not read or
ported. It stays for the old stack until 6d deletes it.

## What changed
- **A menu is its own document** — `*.menu.axml`, root `<ContextMenu>`, children `<ContextButton Text Action>`,
  `<ContextLine/>`, `<ContextSubmenu Text>` (nesting entries). Registered as `ContextMenuAsset` in a new
  `contextMenus` registry dictionary (`Registry.registry.axml`), listed in a host's `*.assets.axml`.
- **A control names one menu** — `ContextMenu="name"` (string) and `StopsContextMenu="true"` on `Control`.
- **Code builds a `ContextMenu` and `ContextMenus.Register(name, menu)`s it**; `Get(name)` checks registered
  menus first, then parses the document on first use and caches it. An unknown name logs a `Warn` and offers nothing.
- `Control.ParseMenu(path)` parses a menu document; `RecursiveParse` now takes an `object` owner and recurses into
  non-control elements, which is what lets a submenu carry entries. Existing non-control elements (grid
  definitions) are self-closing, so nothing else changed.
- **Right-button release** (`UIEngine.SolveRelease`) calls `ContextMenus.OpenOn(hovering, point)`: `Collect`
  walks up from the hovered control appending each named menu's entries, a line between groups, stopping after
  the first control with `stopsContextMenu`. Nothing collected → no menu.
- **Hosting** (`Host`): the panel is measured at the pointer in the origin window's design space. Fits entirely →
  appended as the **last child of the origin `WindowRoot`**. Out by a pixel → `Engine.OpenMenuWindow` with its own
  `WindowRoot`, placed at the pointer's screen position.
- **Submenus** open on row hover, beside the row, top-aligned with it; hovering another row of the parent closes
  them. Each is hosted by the same rule, always tested against the origin window.
- **Closing**: a clicked button row runs its `action`, then everything closes; any press not on a panel closes and
  passes through (`SolvePress` → `DismissUnlessInside`); `ContextMenus.Tick` (from `Engine.MainTick`) closes
  when no application window has focus (not under `--test`).
- `ContextMenus.target` is the control the menu was opened on, set before an action runs and cleared on close.
  `WindowActions.Acting` reads it first.
- Thorium's `TitleBar` names `title-bar` (`Menus/TitleBar.menu.axml`: Minimize, Maximize, line, Close) and stops.
- **Found and fixed on the way (user, 2026-09-11):** `DataPool.Allocate` now clears the new row (see
  [[ecs-rework-data-pools]]); `ButtonControl` runs `onRelease` for the left button only, so a right-click on a
  button opens its menu instead of firing it — a menu `Row` still fires on either button through its own override;
  the `Latin` charset gained `›` (U+203A) for the submenu arrow, re-baking Thorium's and Carbon's fonts.

## Why these choices

**Menus are separate documents, not child elements of the control (user, 2026-09-11).**
"UI builds the UI" — a `*.ui.axml` describes the tree, and a menu written inside a control's element mixes a second
concern into it. Rejected: entries authored inline, which the parser could already route into a `List<>` field on
the control. The cost of the choice is one indirection (a name) and one registry dictionary.

**A control holds a name, not a menu object.**
A `ContextMenu`-typed member with a `TypeConverter` from a name was the alternative. The XSD generator treats any
`[A_XSDType]`-tagged class member as a nested element, not an attribute, so the schema would reject
`ContextMenu="x"`. A string is an `xs:string` attribute, and code reaches the same lookup through `Register`.

**Entries are data; the panel is built on open.**
A menu authored as controls would be attached, measured and drawn wherever it was parsed. As data it is parsed once,
shared by every control that names it, and turned into controls only while open. Merging several controls' groups is
a list append rather than reparenting subtrees.

**In-window hosting is the last child of the root — no overlay layer.**
The draw list is DFS pre-order and the hit-test walks children last to first, so the last child of the root is
drawn on top and hit first. That is exactly "drawn last" with zero new engine surface. The panel overrides `Arrange`
to sit at its own `position`, ignoring the box `WindowRoot` offers. Cost: `WindowRoot.AddChild`/`RemoveChild`
invalidate the root, so opening or closing an in-window panel re-lays out the whole window — the cost of a resize.
When documents land on the new stack this may want a separate overlay list collected after the root.

**Each windowed panel gets a uniquely named window.**
`Engine.Publish` keys windows by name. Reusing a name while the previous window is `closeRequested` but not yet
reaped overwrites its entry, and `ReapClosedWindows` then never finds it — an orphaned OS window.

**Every panel is positioned and fit-tested in the origin window's design space.**
A windowed panel keeps its logical `position` there and arranges at 0,0 in its own window; a submenu's position is
its parent's plus the row offset, so the rule is one formula whether the parent is in-window or not. A submenu of a
windowed menu can therefore land back inside the origin window.

**Opens on right-button release, whatever the release handlers returned.**
`ButtonControl` consumes every release, so "consumed suppresses the menu" would mean no button could offer one.

**Focus loss is a per-tick question, not the focus callback.**
When a submenu window takes focus, its parent's window loses it; at callback time nothing says where focus went.
`Tick` asks GLFW whether *any* application window is focused, so focus moving between menu windows keeps the chain
open and focus leaving the application closes it.

**`target` is a plain static, not an `[A_ActiveContext]`.**
Nothing keybinds against it; actions read it. Without it `WindowActions.Acting` resolved the window of `hovering` —
the menu's own window when the menu is windowed.

## Menu bar, tab and view menus — 2026-09-12, landing 6b3

- `MenuButtonControl` `<MenuButton>`: a left press opens only the menu it names (`Get(contextMenu)` →
  `Open`) at its bottom-left edge; `takesActiveControl => false`. Its caption is an authored `<Label>` child.
- Thorium's `thorium` / `file` / `edit` and the engine's `view` / `tab` are `*.menu.axml`. The engine's are registered
  in `EngineAssets.assets.axml`, which every host's file system mounts below its own.
- `TabViewControl.tabContextMenu` (`TabContextMenu`, default `tab`) names each strip button's menu, and every
  strip button sets `stopsContextMenu`. `SplitViewControl.NewPane` carries `tabContextMenu` and `contextMenu`
  onto a split-off pane.
- `TabActions`' Close / CloseOthers / CloseRight / SplitRight / SplitDown take no argument: the
  `TabStripButtonControl` at or above `target`, else the old `ContextMenus.invoker`. `ViewActions` looks for a
  view above `target`, `UIEngine.activeControl`, `UIEngine.hovering` in turn. `UIActions.Invoking` resolves the new
  stack's window first. Each keeps the old stack's path as a fallback until 6d.
- `DropdownControl` builds `ContextMenuButton(text, action)` entries in code and opens them under itself, at least as wide as itself:
  `ContextMenus.Open`'s `width` becomes the top panel's `preferredWidth`, which `StackPanelControl.Measure` takes as a
  floor, so a caption wider than the dropdown widens the menu instead of overflowing it. `centered` sets the top panel's
  `ContextMenuControl.centered`, which `Row.Arrange` reads to center the caption; the dropdown passes it, nothing else
  does. Submenus take neither.

**The tab actions lost their parameter because the new format binds zero-argument `Action`s only.**
`ResolveAttributes` makes an `Action` delegate by name, which throws on a `(VulkanControl)` method. The old binder
already runs a zero-argument action inside `InvokeFor` with `invoker` set to the control it used to pass, so the old
tab menu behaves the same. Rejected: a same-named zero-argument overload — `TaggedActions` resolves names with
`FirstOrDefault`, so which of the two binds would be reflection order.

**`ViewActions` tries each candidate rather than the first non-null.**
View ▸ Split from the title bar has the menu button as `target`, with no view above it; stopping there did nothing.
Falling through to the active control is the reason the menu button leaves it alone.

**A menu button drops its own menu, not the right-click walk (user).**
`Collect` from a title-bar button would append the title bar's `title-bar` group to every menu.

**Tab buttons stop the walk (user).**
Without it a tab's menu carried the view's group after a line, so Split right and Split down appeared twice —
once for the tab under the pointer and once for the view's active tab.

**`EnabledWhen` is not on the new format (user: skip).**
`Tab.HasSiblings` / `HasRight` / `CanTearOff` stay old-only and every new row is enabled. `SplitOff` already refuses
a view's only tab and the close variants find nothing to close, so greying would only be cosmetic.

## `Open` takes `onClosed` — 2026-10-04

- `ContextMenus.Open(…, Action? onClosed = null)`; private `_onClosed` is run once at the start of `CloseFrom(0)` — when the top-level menu closes, whoever closes it (a clicked row, a press outside, focus loss through `Tick`, another `Open`).
- First user: `FormulaPopup.Show` passes `Cancel`.

**A menu hosting live state needs a close callback because destroyed controls get no `onBlur`.**
`UIEngine.Forget` drops destroyed controls without running `onBlur`, so a popup whose menu was closed from outside is never told. `FormulaPopup.open` stayed set, and Ctrl+Shift+V pasted into a destroyed box (`DataPool.GetRef` read row -1 → `IndexOutOfRangeException` in `Main.Input`). Rejected: a liveness guard in the popup's paste — it stops the crash but leaves an unrecorded preview. See [[sheets]].

## Menus inside popups and submenus built on open — 2026-10-07 (planner P2)

- `ContextMenus.OpenFrom(entries, on, point, width = 0, centered = false)`: when `on` is inside an open panel at depth d, closes panels deeper than d and hosts the new panel at d+1 (position converted into the origin window's design space as `owner.position + (point - owner.arrangedRect.xy)`), opener = null; otherwise identical to `Open`.
- `ContextMenus.Clicked`: a button row runs its action, then closes from `NestedFrom(panel)` — the shallowest open panel at depth ≥ 1 whose `opener` is null (a panel opened by `OpenFrom`), else 0 (the whole menu, as before). Picking from a list opened inside a popup closes only that list.
- `ContextMenus.DismissUnlessInside`: a press inside panel d closes panel d+1 when that one was opened by `OpenFrom` (opener null); a press outside every panel closes the menu as before.
- `ContextMenus.RegisterSource(string name, Func<List<ContextMenuEntry>> build)` + private `EntriesOf(ContextMenuSubmenu)`; `Entered` opens a submenu with the built entries when the submenu has a source. Unknown source → one `Log.Once().Warn` per call site, submenu does not open. Private `_sources` dictionary.
- `ContextMenuSubmenu.source` — `[A_XSDElementProperty("Source", "UI", …)]`, authored `<ContextSubmenu Text="…" Source="name"/>`; the reflective XML parser and the XSD generator pick it up with no other change; `UITypeSchema.xsd` regenerated.
- `DropdownControl.OnPointerRelease` calls `OpenFrom` instead of `Open`; the Settings window and note-properties dropdowns are not inside a menu, so unchanged.
- First users: the planner's ticket popup (category dropdown) and its "Move to category" submenu (`Source="planner-categories"`). See [[planner]].

**A dropdown inside a popup needs the engine to nest menus (user's pick).**
`ContextMenus.Open` closes whatever is open first, so a `DropdownControl` in a popup closed the popup. Rejected: category chips in the popup (works, no engine change, but the user wanted the general fix); a dialog window for the ticket editor (a second hosting path for one popup).

**A panel opened by `OpenFrom` is told apart by `opener == null` at depth ≥ 1.**
Submenu panels always have an opener row, so no new field on `ContextMenuControl`.

**Submenus build on open; `RegisterSource` is a builder registered at runtime and called each time the submenu opens (user: "gets stuff on open … or registered at runtime then get'ed").**
Rejected: a registered list the planner keeps updated — stale when two open planners have different categories. Rejected: the chart building its whole right-click menu in code — loses the XML menu.

**NOT GUI-verified:** the Settings window and note-properties dropdowns after the `OpenFrom` switch (they should be unchanged). Test-verified: engine Input suite `Input.DropdownInsidePopup`, `Input.SourceSubmenu` (`bash _Build/test.sh Input` → 6 passed).

## Entries shown only while a condition holds — 2026-10-23

- `ContextMenuEntry.when` — `[A_XSDElementProperty("When", "UI", …)]` on the abstract base, so any entry takes `When="name"`; `UITypeSchema.xsd` regenerated.
- `ContextMenus.RegisterCondition(string name, Func<Control, bool> shown)` + private `_conditions` and `Shown(entry, on)`. `Collect` filters each menu's entries against the control the menu opens on (a menu left empty adds no group line); `EntriesOf` filters a submenu's authored entries against `target`. Unknown condition → one `Log.Once().Warn` per call site, entry shows.
- `DocumentEditorControl`'s static ctor registers four, each on the caret of the nearest editor at or above the control: `table-cell` (caret block's parent is a cell of a `TableControl`), `outside-table` (caret block is a direct child of the `DocumentControl` — where `InsertTable` works), `picture-selected` (`SelectedPicture`), `formula-selected` (`SelectedMath`).
- Thorium `Note.menu.axml`: Edit formula `formula-selected`, Insert table `outside-table`, Table submenu `table-cell`, Wrap text and Collision `picture-selected`.
- A right-click press already moves the caret (`TextRunControl.OnPointerPress` passes every button; `PicturePressed` selects a picture or formula on any button), so the caret is where the click landed when the menu collects.

**Hide, not grey out (user: a note with no table should not offer cell commands).**
Different from the skipped `EnabledWhen`: those rows were harmless no-ops; these are whole groups that do not apply to what was clicked. Rejected: a `Source` builder for the Table submenu — the "Table" row would still show.

**Separators are not collapsed.** No combination of the note menu's conditions leaves two lines together or a line at an end; a menu where one could needs collapsing added to `Collect`.

**NOT GUI-verified:** a real right-click in Thorium. Test-verified: `TextInput.NoteMenuFollowsCaret` (`bash _Build/test.sh TextInput` → 99 passed; the one failure is the baseline Boot sampler error).

## Entries closing without acting — a hidden window kept a stale `isInWindow` — 2026-10-23

Scope: `ArctisAurora.Core.UI.UIEngine.Poll(RenderWindow)`, `RenderWindow.isInWindow`, `RenderWindow.hidden`.

- Symptom (first seen on Thorium's title-bar "Thorium" and File menus): a click on a menu entry only closed the menu.
- `UIEngine.Poll`: new local `bool inWindow = window.isInWindow && !window.hidden;` replaces `window.isInWindow` in the "pointer left this window" early-out (clears `hovering` if it is in this root, returns) and in the `SolveHover` guard.
- A hidden window that owns the current drag (`ownsDrag`) still runs its press/release/drag handling, without hover — the same as an un-hovered window owning a drag.
- Tests (Thorium, `Thorium.Tests.TitleBarTests`, suite file `TitleBar.tests.axml`):
	- `TitleBar.ThoriumMenu` loads the real `main` UI document, clicks File then "Vaults" (vaults window opens, menu closes), then Thorium then "Settings". Waits 40 ticks after opening a menu: the `menu-open` clip slides the panel in over 0.16 s and a point computed mid-slide misses the row.
	- `TitleBar.HiddenWindowIgnored` hides a second window (`Engine.OpenMenuWindow("titlebar-hidden", …)`, empty `WindowRoot`) through `Background.HideWindow`, opens File, `t.MoveTo(row)`, sets `hidden.isInWindow = true` and presses with `t.Key(Keys.MouseLeft)` (a press without a pointer move, because `TestContext.Point` rewrites every window's `isInWindow` on each move or click). Failed before the fix, passes after.

**Cause (proven): `Poll` runs for every window each frame, and a hidden window still counted as one the pointer was in.**
Each window with `isInWindow` true hit-tests the global button press against its own tree. `isInWindow` is set only by GLFW cursor enter/leave callbacks and `AGlfwWindow.SeedIsInWindow`; `glfwHideWindow` sends no leave, so the flag stays true. Thorium hides its primary `main` window whenever the vaults live in editor windows (`VaultWindows` restore: `Background.HideWindow(Engine.primary)`), so a pointer over that rect at launch left `main` with `isInWindow = true`. `Engine.windows` polls `main` before the editor window: its hit test (pointer outside its tree) gives `hovering = null`, `SolvePress` calls `ContextMenus.DismissUnlessInside(null)` and closes the open menu, and the editor window then sees the press land on the note under the vanished menu. The menu opens fine because nothing is open yet when `main` sees the press.

**Evidence.** Live Debug Thorium, real pointer (`SetCursorPos` + `mouse_event`), cursor parked at a screen point during boot, then Thorium then Vaults: cursor inside hidden `main`'s rect (364,120, 1280x720) at boot, the menu opens and Vaults does not (2 of 2, plus 2 earlier identical failures with Settings); cursor outside, Vaults opens (2 of 2). After the fix, inside, Vaults opens (2 of 2). The first theory ("closing with the Settings window open breaks it") was the mouse position: closing left the cursor inside that rect for the next launch.

**Why tests never saw it.** `TestContext.Point` sets `isInWindow` for every window (true only for the target) on each move or click, and `AGlfwWindow.SeedIsInWindow` forces false under `TestRunner.active`, so no test could carry a stale flag.

**Fix: skip hidden windows in `Poll` (user choice).**
Rejected: clearing `isInWindow` in `AGlfwWindow.Hide()` — covers every hide path, including ones that call `os.Hide()` without setting `RenderWindow.hidden`, but leaves the flag wrong by construction elsewhere; the user chose the `Poll` guard. Cost of the chosen fix: a window hidden with `os.Hide()` alone (not through `Background.Hide`/`HideWindow`, which set `hidden`) still keeps a stale flag. Not done: routing each press only to the window GLFW reported it in (the deeper design; every window still sees the global button state).

**Verified:** test-verified and GUI-verified (live real-pointer trials above). Full suite `bash _Build/test.sh` → 384 passed, only the known baseline Boot error, no `[Vulkan]` lines; `TitleBar.HiddenWindowIgnored` failed before the change and passes after. The user settings file was unchanged across all live runs (hash-compared to a copy taken first).

Gaps: a window hidden by `os.Hide()` without `RenderWindow.hidden` keeps the stale flag (none known in Thorium's hide paths); `isInWindow` itself stays true on a hidden window, only `Poll` ignores it; `ContextMenus.DismissUnlessInside(null)` still closes menus for a press any polling window resolves to nothing, which is intended for a real press outside every tree. Related: [[synthetic-click-into-foreground-app]].

## A drop button's second press closes its menu — 2026-10-23

Scope: `ArctisAurora.Core.UI.Menus.ContextMenus`, `TabToolsControl`, `DocumentToolbarControl`, `MenuButtonControl`, `DropdownControl`.

- `ContextMenus._closedOn` (private static `Control?`): `DismissUnlessInside` clears it on every press and, when that press closes the menu, records `target` (the control the menu was opened on) before `Close()`.
- `ContextMenus.ClosedOn(Control on)` (public static bool): true once when the current press just closed a menu opened on `on`, then clears.
- Openers that start with `if (ContextMenus.ClosedOn(owner/this)) return;`: `TabToolsControl.Drop` (now a block body), `DocumentToolbarControl.Drop` (private, block body), `MenuButtonControl.OnPointerPress`, `DropdownControl.OnPointerRelease`.
- Covers: inspector/ribbon dropdowns (page Size, Style, Format), document toolbar dropdowns, menu bar buttons, Settings-window dropdowns.
- Test: `Input.DropToggles` (`AuroraEngine/Tests/InputTests.cs`, listed in `Input.tests.axml`): open, second press closes and stays closed, third opens.

**The mechanism is needed because the button only ever sees a closed menu.**
`UIEngine.SolvePress` runs `ContextMenus.DismissUnlessInside` before dispatching the press, so by the time the button handles it the menu is already gone and the press reopens it.

**Opt-in per opener, not built into `ContextMenus.Open`.**
Rejected: suppressing any `Open` on the control the press just closed a menu for. Popups open on shared controls (`FormulaPopup` opens on the whole `DocumentEditorControl`), so a press on a second formula would close the first popup and then be refused.

**`_closedOn` lives from one press to the next.**
Cleared at the start of each press, so it covers buttons that open on press and `DropdownControl`, which opens on release.

**Not applied:**
- Right-click menus (`OpenOn`): a second right-click should reopen at the new spot.
- The formula, planner ticket, planner category and sheet-grow popups, the sheet layers button (`SheetPageStripControl.OpenLayers`), the vault browser's New button.
- A dropdown inside an open popup: that press closes a nested list through the `ContextMenuControl` branch, not the menu.

**Test-verified:** `Input.DropToggles`; Input suite 7 passed, only the known baseline Boot failure. The toggle is tested only through `TabToolsControl.Drop`, not the toolbar, menu bar or Settings dropdown paths. **NOT GUI-verified** in a live window.

## Vault "New" submenu — 2026-10-24

- Scope: `Thorium.Editor.CustomControls.VaultBrowserControl` (actions), `Thorium/Data/XML/Documents/Menus/*.menu.axml`.
- Every vault-browser context menu and the title-bar File menu replace the separate "New note / New sheet / New planner" entries with one **New** submenu: Folder, Doc, Sheet ▸ (Fixed size, Unfixed), Planner.
- Root menus (`Vault.menu.axml`, `File.menu.axml`) use the root actions: `Folders.New`, `Notes.New`, `Sheets.NewFixed` / `Sheets.New`, `Planners.New`.
- Row menus use the `…Here` actions (inside the right-clicked folder, or beside the right-clicked file): `VaultFolder`, `VaultNote`, `VaultNoteXml`, `VaultCsv` (gained New, had none), `VaultAsNote`, `VaultAsSheet`, `VaultAsPlanner`. `Folders.NewHere` falls back to the root without a row.
- `NewFolder(string)` asks `NoteNameWindow.Ask` (default "New folder"); `CreateFolder(folder, name)` picks "name", "name 2", … free against both directories and files, calls `Directory.CreateDirectory`, then `Expand(folder)` + `Rebuild()`. It opens nothing.
- `PaneNew.menu.axml`, `Thorium.menu.axml` and the old-stack `ContextMenus.menus.axml` are unchanged.

**Sheet stays a submenu with Fixed size / Unfixed, a third level.**
A single "Sheet" leaf would drop fixed-size creation from the menu.

**No icons in menu rows.**
`ContextButton` and `ContextSubmenu` have no Icon attribute and adding one is an engine change not taken now.

**Verified:** shot-verified via a throwaway test (deleted): right-click a folder → New → Sheet opened the nested submenu with Folder / Doc / Sheet / Planner and Fixed size / Unfixed. **NOT GUI-verified:** clicking Folder end to end (name dialog and folder creation), the File title-bar menu by eye. Row icons of the same change: [[file-browser-tree]].

## Known gaps
- No icons in context-menu rows (`ContextButton` / `ContextSubmenu` carry no Icon attribute).
- No flipping or clamping to the monitor — a windowed menu near a screen edge goes off-screen.
- A windowed panel renders 1:1 even when the origin window autoscales; only its position is converted.
- No Escape, no keyboard navigation, no hover delay for a diagonal move into a submenu.
- A bad `Action` name in a menu document throws at the first open, not at boot — the document is parsed lazily.
- Each window opened after boot logs the pre-existing `DemoteToHelperInvocation` validation error once more.
- The Editor's and the engine's own font copies were already stale (importer v1 and v3) and were not re-baked.
- A second press closes the menu only for the openers listed under "A drop button's second press closes its menu"; the others (right-click, popups on shared controls, sheet layers, vault browser New) still dismiss and reopen.
- `Tick` closes menus when no application window has focus, and returns early under `TestRunner.active` (2026-10-05), so `--test` popup tests (`Sheet.MathLinks`, `Sheet.Layers`, `TextInput.Math*`) do not depend on OS focus.
- New note, Vaults, Settings, Save note, Undo and Redo run old-stack actions, which draw nothing since `UIModule` left
  the module list. They come alive at 6c and 6c2; Exit works.

## Verified
GUI-verified in Thorium 2026-09-11, synthetic input + topmost capture:
- in-window at the pointer over the title bar, row hover tint, Maximize from a row maximizes the main window and
  restores it on the second use, the panel is removed after the action;
- walk and stop: a title-bar button's own menu, an automatic line, then the title bar's group, and none of the root's;
- a press outside closes the menu;
- near the bottom-right corner the menu is its own 112×100 window at the pointer, past the main window's edge; its
  submenu and a third level open as further windows beside their rows while the parent stays open; Maximize from
  the third level maximizes the **main** window and all three menu windows are reaped;
- Alt+Tab away closes a windowed menu (no press involved, so `Tick` is the only path that can have closed it).
- after the three fixes: closing one menu and opening another, and cycling a submenu three times, render cleanly
  where they had wrapped a character per line; `›` draws flush right; right-clicking Close opens the title-bar menu
  (windowed, at the edge) and the application keeps running; a left click on Close still quits.
- Boot error set unchanged (18); no exceptions across four Thorium runs.

Landing 6b3, GUI-verified in Thorium 2026-09-12, synthetic input + topmost capture:
- Thorium, File, Edit and View each drop their own entries directly under the button; pressing another swaps menus;
- a right click on a tab opens the `tab` group; Tab ▸ Close closed the tab it was opened on (its view's only tab —
  the emptied view collapsed).
**NOT GUI-verified:** the tab buttons' stop (added after that run, which still showed the view's group); every split
path — Tab ▸ Split, View ▸ Split, the Ctrl+\ keybinds — and Close on an inactive tab, all of which need two tabs
in one view; `Dropdown`'s list; `UIActions.Invoking`, which has no new-stack caller yet.

Related: [[ui-engine-stack]], [[ui-draw-list]], [[ui-document-registry]], [[context-menu-hosting]] and
[[context-menu-invoker]] (the old stack's)
