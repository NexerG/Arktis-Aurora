# Decision — a planner is one vault file with three views; the calendar is merged into it

**Date:** 2026-10-07
**Scope:** `ArctisAurora.Core.UI` — `PlannerDocument`, `PlannerCategory`, `PlannerTicket`, `TimeRange`, `PlannerXml`, `GanttChartControl`, `PlannerBoardControl`, `PlannerEditorControl`, `PlannerZoom`, `PlannerView`, `PlannerTicketPopup`, `PlannerActions`, `PlannerTicketEdit`, `PlannerTicketAddEdit`, `PlannerCategoryPopup`, `PlannerCategoryEdit`, `PlannerCategoryAddEdit`, `CalendarControl`, `CalendarSpan`, `PlannerAttachment`, `PlannerAttachmentKind`, `PlannerAttachmentKindMap`, `PlannerAttachmentKinds`; `ArctisAurora.Core.Users` — `User`; `Thorium.Editor.CustomControls.VaultBrowserControl` (`BuildPlannerTab`, `NewPlanner`, `CreatePlanner`, `BuildTab`, `Extension`, `BaseName`, `VaultNotes`); data `Engine.attachments.axml`; suite `Planner.tests.axml`

## What changed
- New vault file kind `*.planner.axml`: categories and the tickets filed under them. Phases F0 + P1 of [[planner-plan]] landed read-only; P2 (Gantt editing), P3 (board drag), P4 (categories, assignee chips), PC (calendar view) and C2 (attachments, links) landed 2026-10-07 — see the P2, P3, P4, PC and C2 sections below.
- `PlannerDocument` — `extension`, `defaultColor` (`#4C8BF5`), `name`, `categories`, `tickets`, `extra` (unknown elements kept), `undo` (used from P2), `IsPlanner`, `Load`, `Save`, `Blank(name)` (one category "General"), `CategoryOf` (missing category → first), `ColorOf` (ticket colour, else category colour), `TicketsIn(category)` (by `order`, then start).
- `PlannerCategory` (`id` Guid, `name`, `colorHex`); `PlannerTicket` (`id` Guid, `name`, `categoryId`, `colorHex?`, `time`, `creator`, `created`, `assignees`, `order` = position in its board column, `extra`).
- `TimeRange` — `readonly record struct (start, end, allDay, timeZone)`; wall-clock start, **exclusive** end; all-day runs midnight to midnight; `Days(first, last)`. `timeZone` is stored and round-tripped, never converted.
- `PlannerXml` (`Load`, `Save`, `Parse`, `ToXml`): `<Planner Name><Category Id Name Color/><Ticket Id Name Category Color Start End AllDay TimeZone Creator Created Order><Assignee User/></Ticket></Planner>`. All-day Start/End `yyyy-MM-dd`, timed `yyyy-MM-ddTHH:mm`, Created ISO "s". A ticket without readable Start/End is kept as an unknown element; no categories → "General" added; written order categories, tickets, unknown.
- `GanttChartControl` (built like `SheetControl`): `PlannerZoom { Hour, Day, Week, Month }` (day widths 768 / 32 / 10 / 3 px), `nameWidth` (see 2026-10-09 below), `bandHeight` 22, `headerHeight` 44, `rowHeight` 28; span = every ticket and today, padded 4 units (6 at Hour); rows = each category (shaded) then its tickets; only rows/units in the viewport get pooled `ChartParts`; name column pinned left, two-band date header pinned top, weeks start on the week-start setting (`DateFormats.WeekStart`; Monday until 2026-10-09), today line in Accent. Geometry: `Offset`, `X`, `TimeAt`, `RowTop`, `RowOf`, `BarRect`, internal `RowsChanged`.
- 2026-10-09: name column is resizable — drag ±3 px of its right edge (HResize cursor), clamped 80–600; width lives on `PlannerDocument.nameWidth` (default `defaultNameWidth` 200), written as `<Planner NameWidth>` only when not default. A resize marks the file unsaved but is not an undo step. The now line is Danger (matches the Calendar) and ticks: `OnTick` re-arranges when the minute turns, as `CalendarControl` does. Every Gantt line (grid, header, name edge, links, now line, selection ring) follows `SheetControl`'s rule: thickness `UIScaling.Hairline` (≥1 device px), position `UIScaling.Snap`.
- 2026-10-09: board columns have a width each — `PlannerCategory.width` (default `defaultWidth` 260), `<Category Width>` only when not default; drag ±3 px of a column's right edge, clamped 160–600, marks unsaved, no undo (same as `NameWidth`). Card name, dates and "by" lines wrap through a private `PlannerBoardControl.WrappingLabel` (`LabelControl` with `Wraps => true`), so cards grow downward with their text. The column heading label wraps too (`widthStar = 1` so the horizontal heading stack offers it the leftover width), and the initials chips sit in a `WrapPanelControl` — so nothing on the board widens a column past its set width.
- 2026-10-09: board column menu (`PlannerCategory.menu.axml`) has "Add ticket" → `Planner.AddTicketHere` → `PlannerEditorControl.AddTicketToPickedCategory`, opening `PlannerTicketPopup` at the press point with the new ticket filed in that column (`Open(..., category:)`). Board only; the Gantt menu is unchanged.
- 2026-10-09: board column menu also has "Delete category and tickets" → `Planner.DeleteCategoryWithTickets` → `ConfirmDeletePickedCategory` (`ConfirmWindow.Ask`) → `DeleteCategoryWithTickets`: each ticket's `PlannerTicketAddEdit` then the `PlannerCategoryAddEdit`, inside one `undo.Begin` step. Never the last category. Followers in other categories keep a dangling `follows` (ignored by `Find`; undo restores the leader). The popup's Delete still moves tickets to the first category.
- `PlannerBoardControl` (horizontal StackPanel): `columnWidth` 260, internal `Rebuild()`, static `Dates(TimeRange)`; a column per category (swatch + "Name  count"), cards with a colour strip on top, name, dates, "by <creator>".
- `PlannerEditorControl` (`IFileEditor`): `PlannerView { Gantt, Board, Calendar }`; toolbar Gantt | Board | Calendar left, Hour/Day/Week/Month right (Gantt only); `SetZoom` keeps the time at the viewport's left edge; `ViewState`/`RestoreView` put the view in `SessionTab.topBlock`, zoom in `caretBlock`, scroll in `scrollX`/`topDelta`. Private nested `PlannerScroller`.
- `ArctisAurora.Core.Users.User`: `name`, static `current` = `new User("Grexen")`, `Named(string)`, equality by name. Hard-coded on the user's instruction.
- Thorium: actions `Planners.New`, `Planners.NewHere`; `BuildTab` routes `*.planner.axml`; `Extension`/`BaseName` treat ".planner.axml" as one extension; `VaultNotes()` skips planners. "New planner" in `File.menu.axml`, `Vault.menu.axml`, `VaultFolder.menu.axml`.
- Tests: `Planner.XmlRoundTrip`, `Planner.GanttGeometry`, `Planner.BoardColumns`, `Planner.ViewState`.

## Why these choices

**One file, three views — the calendar is merged into the planner.**
A planner ticket is the event, and the calendar's Day/Week/Month view becomes a third planner view (phase PC). Rejected: a separate `*.calendar.axml` file kind plus an `ITimedItem` interface so a calendar view could sit over both — two file kinds and an abstraction for what the user wants as one document. `CalendarDocument`, `CalendarEvent` and `CalendarEditorControl` are dropped. Google/iCloud sync stays out and stays parked in [[calendar-plan]].

**The extension is `*.planner.axml`.**
The user's pick over `*.gantt.axml` and `*.board.axml`, because one file holds both views.

**Board columns are the categories.**
The user's pick; moving a card between columns will change its category (P3). Rejected: Trello-style status columns (To do / Doing / Done) with category as a colour label — one more field and its own column list.

**Times are date + time, not whole days.**
The planner is heading toward calendar use, so `TimeRange` carries wall-clock times and the Gantt has an Hour zoom. Drags (P2) will snap to the hour on Hour zoom, the day otherwise. End is exclusive, matching iCalendar's DTEND.

**The creator is a `User` class, hand-set to "Grexen".**
No accounts, no settings field. Stored in XML by name. Assignees are `User`s too, stored as `<Assignee User/>`; the assignee feature was a stub until P4, which shows initials chips on board cards.

**Ids are Guids.**
Category and ticket ids are Guids so a rename does not break a reference.

**The board is read-only in P1, not deferred.**
A read-only board is P3's display part pulled forward so the Gantt/Board switch has something to switch to.

**The board card's colour strip runs across the top.**
In a horizontal `StackPanelControl` a child with no preferred height fills the cross axis, so a side strip with no height measured unbounded and stretched the first card to the column's full height, hiding the rest. Seen in a shot, then fixed.

**No `PlannerBook`.**
Sheets have `SheetBook` to share one loaded copy across tabs, for formulas; not needed yet.

## Known gaps
- (P1 state) Nothing was editable; P2 made the Gantt editable. The board is still read-only (P3).
- (P1 state) No goldens: the today line and the span move with the clock. P2 added `PlannerEditorControl.clock` and the golden `Planner.GanttGolden`.
- `timeZone` is round-tripped only, never applied.
- The Gantt chart is narrower than the viewport when the span is short (the grid stops at the span end).
- **Verified:** test-verified (`bash _Build/test.sh Planner` → 4 passed; full run 225 passed, 3 failed — Boot on the baseline sampler error, `Sheet.FixedSize` already in the WIP list, `Perf.TypeLargeNote` on the baseline `[Layout] skipped layout … desired stale` errors; no `[Vulkan]` lines) and shot-verified (a throwaway golden, since deleted, showed the Gantt at Day and Week zoom and the board with 3 categories / 6 tickets).
- **Not GUI-verified:** "New planner" from the File / vault / folder menus, opening, renaming, duplicating and deleting a planner in the vault browser, tab restore across a restart, scrolling and the zoom buttons in a running Thorium. No test drives the vault browser.

## P2 — Gantt editing (landed 2026-10-07)

### What changed
- Gantt editing: click a bar to select it (outlined in Ink); drag the middle to move, an edge to resize; double-click edits; "+ Ticket" opens the ticket popup for a new ticket; Delete removes the selected ticket; Ctrl+Z / Ctrl+Y or Ctrl+Shift+Z undo / redo; Ctrl+S saves; right-click offers Edit ticket, Delete ticket and a "Move to category" submenu listing the planner's categories.
- Engine, `ContextMenus`: `OpenFrom(entries, on, point, width = 0, centered = false)` opens a menu from inside an open popup without closing it; `RegisterSource(name, build)` + `ContextMenuSubmenu.source` build a submenu's entries when it opens. Details in [[context-menus]] (section "Menus inside popups and submenus built on open"). `DropdownControl.OnPointerRelease` now calls `OpenFrom`.
- `PlannerDocument`: `changed` event + `Changed()`, `NextOrder(category)` (order that puts a ticket last in a category). `PlannerTicket`: `Clone()`, `CopyFrom(other)`.
- `PlannerEdits.cs`: `PlannerTicketEdit(document, ticket, before, after)` (copies before/after over the live ticket, raises `Changed`), `PlannerTicketAddEdit(document, ticket, index, add)` (insert at index / remove).
- `PlannerEditorControl`: `undo`, `selected`, `clock` (`Func<DateTime>`, default `DateTime.Now`) and `Now`, `Select`, `AddTicket`, `EditSelected`, `MoveSelected(category)`, `DeleteSelected`, `Apply(ticket?, draft)` (edit → one "Edit ticket" step; null ticket → "Add ticket", the draft becomes the ticket and is selected), internal `CommitTime(ticket, before, label)`, `Undo`, `Redo`, private `Record(label, edit)` (Begin scope, Redo, Push — same as `SheetEditorControl.Record`), private `DocumentChanged` (sets `unsaved`, drops a deleted selection, redraws chart/board), `OnDestroy` unsubscribes. Toolbar gains "+ Ticket".
- `GanttChartControl`: `contextMenu = "planner"`; static ctor registers source `"planner-categories"` (one button per category of the planner found by walking up from `ContextMenus.target`, action `MoveSelected`; empty when nothing is selected); selection outline (4 Ink parts, `outlineWidth` 2); public `TicketAt(point)`, private `GripAt` (edge zone = min(6 px, bar width / 3) inside, 6 px outside → resize; middle → move), `OnPointerPress` (left: select + `StartDrag` on a bar; right: select the row's ticket, return false so the menu opens), `OnDrag` (snaps the delta: hour on Hour zoom for a timed ticket, day otherwise; resize keeps at least one unit), `OnDragStop` (one "Move ticket" / "Resize ticket" step through `CommitTime`), `OnPointerMove`/`OnPointerExit` (HResize cursor on edges), `OnPointerTap` (double → edit). Today line and span use `editor.Now`.
- `PlannerTicketPopup.cs` (internal, built like `SheetGrowPopup`): Name, Category (`DropdownControl`, SubField), Start, End (`yyyy-MM-dd` for all-day, end shown as the last day; `yyyy-MM-dd HH:mm` timed; two dates → all-day, a time in either → timed; unreadable box keeps its value; end ≤ start → +1 day / +1 hour), Assignees (comma-separated → `User.Named`), Colour (`ColorPickerControl`), "Created by", an Apply button. Enter in any box or Apply = one step; Esc / outside click cancels. New ticket: "New ticket", today all day, `User.current`, created = `editor.Now`, last in the selected ticket's category (else the first). Changing category re-orders it last in the new one.
- `PlannerActions.cs`: `Planner.Undo`, `Planner.Redo`, `Planner.Delete`, `Planner.Edit`, `Planner.Save` ("Input" actions; no-op unless a planner holds the active control).
- Thorium data: `Planner.menu.axml` (Edit ticket → `Planner.Edit`, Delete ticket → `Planner.Delete`, `<ContextSubmenu Text="Move to category" Source="planner-categories"/>`), registered as `planner` in `ThoriumAssets.assets.axml`; `InputMap.inputs.axml`: Delete → `Planner.Delete`, Ctrl+Z → `Planner.Undo`, Ctrl+Y and Ctrl+Shift+Z → `Planner.Redo`, Ctrl+S → `Planner.Save` (second binds beside the `Sheet.*` / `Text.*` ones).
- Tests: Thorium `Planner.DragMoveUndo`, `Planner.DragResize`, `Planner.EditAddDelete`, `Planner.DeleteKeys`, `Planner.SaveKey`, `Planner.PopupCategory`, `Planner.MenuMove`, `Planner.GanttGolden` (golden `Planner.GanttGolden.Day.png`, clock pinned to 2026-10-06 12:00); engine Input suite `Input.DropdownInsidePopup`, `Input.SourceSubmenu`.

### Why
**Nested menus in the engine (user's pick).**
A `DropdownControl` inside a `ContextMenus` popup used to close the popup, because `ContextMenus.Open` closes whatever is open first. Rejected: category chips in the popup (works today, no engine change, but the user wanted the general fix); making the ticket editor a dialog window (a second hosting path for one popup).

**Submenus built on open (user's pick: "gets stuff on open … or registered at runtime then get'ed").**
`RegisterSource` is both: a builder registered at runtime, called each time the submenu opens. Rejected: a registered list the planner keeps updated — stale when two open planners have different categories. Rejected: the chart building its whole right-click menu in code — loses the XML menu.

**One undo step per gesture.**
A drag mutates the ticket live and records one `PlannerTicketEdit` at `OnDragStop` (before = clone at press), the same shape as `SheetBandEdit` recorded in `OnDragStop`. The popup applies all fields as one step.

**Snapping snaps the delta, not the edge.**
A timed ticket moved on Day zoom keeps its time of day; hour only on Hour zoom for timed tickets.

**Edit records copy whole tickets** (`Clone`/`CopyFrom`) rather than per-field records — a ticket is a dozen fields; one record type covers drag, popup and Move to category.

**`PlannerDocument.changed`** is the planner's equivalent of `SheetBook.changed`, per document because there is no `PlannerBook` (one editor per document).

**`clock` on the editor** pins "now" so the Gantt golden does not move with the date.

**Ctrl+S and the Apply button were added at the user's request.**
Apply because a press on the colour picker moves focus off the text boxes and Enter in the picker's hex box only commits the colour.

Category is picked by name in the popup: two categories with the same name resolve to the first.

### Known gaps
- No Tab between popup boxes (no `Planner.Tab` bind); the popup's caption labels are centred in their 90 px column.
- A ticket's own colour cannot be cleared back to the category colour once picked.
- Dragging a bar past the chart's span is cut off until release (span rebuilds on `Changed`).
- No vertical drag between categories on the Gantt (the board drags cards between categories since P3).
- `PlannerCategoryEdit` moved to P4 (nothing edits categories yet).
- (P2 state) Board view still read-only; P3 made it draggable.
- Other hosts' generated schemas (`UITypeSchema.xsd` etc. under AuroraEditor/Carbon) regenerate with the new `Source` attribute on their next run.
- **Verified:** test-verified (`bash _Build/test.sh Planner` → 12 passed; `bash _Build/test.sh Input` → 6 passed, Boot fails only on the baseline sampler error; full run 235 passed, 3 failed — Boot (baseline), `Sheet.FixedSize` (pre-existing), `Perf.TypeLargeNote` (baseline `[Layout] skipped layout` error kinds); no `[Vulkan]` lines). golden-verified: `Planner.GanttGolden` (Day zoom, selection outline, pinned today line), approved after reading the PNG. shot-verified: a throwaway shot (deleted) showed the ticket popup with its category list open over it, popup still open.
- **Not GUI-verified:** dragging bars with a real mouse (cursor shape, feel of snapping), the popup in a running Thorium (typing dates, colour picker, Apply), right-click menu and Move to category submenu, Ctrl+S / Ctrl+Z / Delete in a real session, and the Settings window / note-properties dropdowns after the `OpenFrom` switch (they should be unchanged).

## P3 — board drag (landed 2026-10-07)

### What changed
- The board is interactive. A card pressed and moved 4 px starts a drag (with the `DragGhost` preview, like a tab); while it is over the board the target column is outlined in Accent and a gap opens where the card will land; dropping files the ticket under that column's category at that place, renumbering the column, as one undo step.
- `PlannerBoardControl`: new public nested `TicketCard : StackPanelControl` (`board`, `ticket`; arms on a left press, claims the drag past `dragThreshold` 4 px with `StartDrag` + `DragGhost.Show`, `OnDragStop` → `DragGhost.Hide`) — the same claim-past-threshold shape as `TabStripButtonControl`. New private `columns` list (category, column panel, cards) built by `Rebuild`; public `CardsIn(category)`; private static `Fill(card, document, ticket)` replaces the old static `Card(...)` builder.
- `PlannerBoardControl` region `drop`: private `DropAt(dragged, point, out column, out index)` (the column whose x-range holds the point, else the nearest; index = the column's other cards whose centre is above the point; only a `TicketCard` of this board), `DraggingOverStart`/`DraggingOver` → `Mark`, `DraggingOverEnd` → `Unmark`, `FinishDrag` → `editor.MoveTicket(ticket, category, index)` (P3 posted it with `Engine.Post`; P4 removed that, the board rebuild is posted instead — see "P4").
- Marker: target column `edgeRole = Accent`, `edgeThickness` 1.5 px (`markWidth` 3 × 0.5); a `dropGap` 28 px top margin on the card the drop lands before, or bottom margin on the last card when it lands last; `markedColumn`/`markedCard`/`markedIndex`, reset by `Unmark` and `Rebuild`.
- `PlannerEditorControl.MoveTicket(ticket, category, index)` (new): builds the target column without the ticket, inserts it at `index` (clamped), and records one "Move ticket" step changing `order` (0..n) and `categoryId` of every ticket whose value differs. The source column is not renumbered (gaps in `order` are harmless).
- `PlannerTicketEdit` now holds a list of (ticket, before, after); new constructor `PlannerTicketEdit(document, List<(ticket, before, after)>)`; the single-ticket constructor delegates to it. Undo/Redo copy every entry, then raise `Changed` once.
- Tests (Thorium Planner suite): `Planner.BoardDragAcross`, `Planner.BoardDragWithin`, `Planner.BoardGolden` (goldens `Planner.BoardGolden.Board.png` and `Planner.BoardGolden.Marker.png` — the Marker shot drives `board.DraggingOver` directly).

### Why
**Engine drag-and-drop, not a self-contained drag in the board.**
Cards are drag claimants and the board is the drop target through `DraggingOver*`/`FinishDrag`, as tabs do; this gets `DragGhost` and the hit-test that skips the dragged subtree for free. Rejected: the board dragging a fake card itself (like `GanttChartControl` drags bars) — re-implements the preview and hit-testing.

**A gap marker, not a line.**
First tried an Accent edge on the card the drop lands before: invisible, because the card's colour strip child draws over its top edge (seen in the golden shot). There is no insert-at-index child API, so a marker child was not an option without reshaping the board. A margin gap on the neighbouring card opens the space where the card will land (Trello-like) and is stable under the pointer: the card the gap pushes down moves away from the pointer, so the index does not flip back.

**Renumber the target column in one record.**
Inserting between neighbours with integer `order` needs renumbering; a list-carrying `PlannerTicketEdit` keeps it one record and raises `Changed` once (per-ticket records would rebuild the board once per ticket). Rejected: fractional/spaced orders.

**Superseded in P4 — the drop was applied one tick later (`Engine.Post`) in `FinishDrag`.**
Applying it inside `FinishDrag` rebuilds the board, which destroys the dragged card while `UIEngine.EndDrag` is still running (it calls `OnDragStop` on it afterwards). That left stale layout state: a later, unrelated test (`Perf.Controls.Scrollable.Relayout`, run after the Planner suite in the full `--test` run) logged `[UIEngine] 'entity' subtreeBounds (0, 0, 0, 0) != recomputed …` ×4 and `[Layout] skipped layout left … subtreeBounds stale` ×4 and failed. Bisected: removing the two board-drag tests cleared it; removing `DragGhost.Show` did not; deferring the move did. The root cause in the engine (destroying the drag claimant inside `FinishDrag`) is not fixed. P4 widened the finding and moved the fix to the source: `FinishDrag` calls `MoveTicket` directly and the board rebuild itself is posted (`RebuildSoon`). See [[rebuild-inside-pointer-dispatch]].

`PlannerCategoryEdit` stays in P4.

### Known gaps
- Cards cannot be selected or opened from the board (no click-to-select, no double-click to edit; Delete / Edit act on the Gantt selection only).
- A drop outside the board does nothing (the card stays); no auto-scroll while dragging near the board's edge.
- The engine trap above is unguarded; P4 widened it to destroying controls inside any `UIEngine` pointer dispatch.
- **Verified:** test-verified (Planner suite 15 passed, Boot fails only on the baseline sampler error; full run 238 passed, 3 failed — Boot (baseline), `Sheet.FixedSize` (pre-existing), `Perf.TypeLargeNote` (baseline `[Layout] skipped layout … desired stale` kinds); no `[Vulkan]` lines). golden-verified: `Planner.BoardGolden` Board and Marker shots, approved after reading both PNGs (column outline + gap above the card).
- **Not GUI-verified:** a real mouse drag of a card (ghost preview, marker following the pointer, drop), dragging off the board or into another window, drag feel with many cards.

## P4 — categories and assignee chips (landed 2026-10-07)

### What changed
- Categories can be added, renamed, recoloured and deleted, each one undo step; board cards show assignee initials as chips. The board now rebuilds one tick after a change (posted), which replaced P3's per-call deferral of the drop and fixes the same layout-state bug on every path.
- `ArctisAurora.Core.Users.User.initials` (new property): first letters of the first two words, else the first two letters of a one-word name, upper-cased ("Grexen" → "GR", "Ada Lovelace" → "AL"; empty → "?").
- `PlannerEdits.cs`: new `PlannerCategoryEdit(document, category, (name, colorHex) before, (name, colorHex) after)`; new `PlannerCategoryAddEdit(document, category, index, add)` (insert at index / remove; raises `Changed`).
- `PlannerEditorControl`: `pickedCategory` + private `pickedFrom`/`pickedAt` (the category last pressed on and where); `PickCategory(category, from, point)`; `AddCategory()` (category popup under the new toolbar button "+ Category"); `EditPickedCategory()`; `ApplyCategory(category?, name, colorHex)` (null → "Add category", appended; else "Edit category" when changed); `DeleteCategory(category)` ("Delete category"; never the last one). `DocumentChanged` now calls `board?.RebuildSoon()`.
- `PlannerCategoryPopup.cs` (new, internal, like `PlannerTicketPopup`): Name box, `ColorPickerControl`, a button row (Delete — only when editing and more than one category — a spacer, Apply). Enter / Apply apply; empty name keeps the old one (or "New category"); Esc / outside click cancels.
- `GanttChartControl`: public `CategoryAt(point)`, private `RowAt(point)` (`TicketAt` now uses it); every press calls `editor.PickCategory(CategoryAt(point), this, point)`; a double click on a category row opens its popup (on a ticket row it still edits the ticket); `stopsContextMenu = true` (ancestors add nothing to its menu).
- `PlannerBoardControl`: `RebuildSoon()` (internal; one `Engine.Post`ed `Rebuild` per tick, skipped if the board was destroyed; flag `rebuildPending`); `Rebuild` is now private. Columns: `contextMenu = "planner-category"`, `stopsContextMenu = true`, press picks their category, double click opens its popup. `TicketCard`: press picks its category; returns handled only for a left press (so right-click reaches the column's menu); new `OnPointerTap` swallows taps so a double click on a card does not open the column's category. Cards show a row of initials chips (`chipHeight` 18, SubField, bold) when the ticket has assignees. `FinishDrag` calls `editor.MoveTicket` directly again (no `Engine.Post`).
- `PlannerActions`: `Planner.EditCategory`, `Planner.AddCategory`.
- Thorium data: `Planner.menu.axml` gains a line, "Edit category", "Add category"; new `PlannerCategory.menu.axml` (Edit category, Add category) registered as `planner-category` in `ThoriumAssets.assets.axml`.
- Tests: `Planner.CategoryEdits`, `Planner.CategoryPopup`, `Planner.AssigneeGolden` (golden `Planner.AssigneeGolden.Board.png`).

### Why
**Deleting a category leaves its tickets pointing at it.**
They show under the first category (`CategoryOf` fallback) while it is gone, and one undo brings the category back with its tickets in place; no per-ticket records. On save their `Category` attribute still names the deleted id, and on load they fall back to the first category. Rejected: reassigning every ticket to the first category — it needs a ticket record per ticket and loses where they were if the user saves before undoing; equal on save, worse on undo complexity. The last category cannot be deleted (a planner always has one).

**Entry points.**
Toolbar "+ Category"; double click a category row (Gantt) or a column (board); right-click "Edit category" / "Add category" (Gantt menu and the new board-column menu). "Edit category" acts on the category last pressed on — a right press counts — so the menu needs no target of its own.

**New categories start at `defaultColor`.**
The popup's picker is where a colour is chosen (no palette cycling).

**The layout-state bug, widened and fixed at the source.**
P3 found that rebuilding the board inside `FinishDrag` left stale layout state (a later `Perf.Controls.Scrollable.Relayout` logs `[UIEngine] 'entity' subtreeBounds (0, 0, 0, 0) != recomputed …` and `[Layout] skipped layout left … subtreeBounds stale`, only in the full `--test` run) and deferred the drop. P4 hit the identical errors from the category popup's Delete button: the delete rebuilt the board inside a *press* dispatch. Bisected again (removing `Planner.CategoryPopup` cleared it; deferring the delete cleared it). So the rule is not about `FinishDrag`: rebuilding (destroying controls) inside UIEngine's pointer dispatch — press, release, tap, drag end — corrupts layout state; doing it from posted work (`Engine.Post`) is safe. The fix moved to the source: `PlannerBoardControl.RebuildSoon` posts one rebuild per tick for every change (drop, popup Apply/Delete, undo, keys), and the two per-call deferrals were removed. Popup Apply on the board view (e.g. "+ Ticket" while the board shows) would have hit the same bug. The Gantt chart never destroys controls on a change (pooled parts), so it needs nothing. The engine root cause is still unguarded. See [[rebuild-inside-pointer-dispatch]].

**The category popup's button row has a fixed height.**
A horizontal `StackPanelControl` with a star spacer and no preferred height measured `float.MaxValue` tall (same trap as P1's card strip), which pushed the popup into its own OS window and, under `--test`, crashed with "Failed to create the context menu window". Seen via a temporary log in `ContextMenus.Host` (removed). See [[star-child-in-unbounded-stack]].

**`stopsContextMenu` on the chart and the board columns.**
So parent controls add nothing to the planner menus.

### Known gaps
- Category order cannot be changed (no reordering of columns / category rows).
- Duplicate category names: the ticket popup's dropdown resolves to the first.
- Deleted-category ticket ids stay in the file (harmless fallback, but never cleaned).
- The board shows a change one tick late (posted rebuild).
- **Verified:** test-verified — Planner suite 18 passed (Boot fails only on the baseline sampler error); full run 241 passed, 3 failed — Boot (baseline), `Sheet.FixedSize` (pre-existing), `Perf.TypeLargeNote` (baseline `[Layout] … desired stale`); no `[Vulkan]` lines. shot-verified: `Planner.AssigneeGolden`, approved in this change after reading the PNG (GR / AL chips on A, BO on C).
- **NOT GUI-verified:** the plan's "GUI check in a running Thorium" was not done — it would create a planner in the user's real vault and drive it with synthetic OS input (known traps in `aurora-verify`). Unchecked: "+ Category", the category popup (typing, picker, Delete), double clicks on rows/columns, right-click category menus, chips with many assignees, the deferred board rebuild's one-tick lag in a real session.

## PC — calendar view (landed 2026-10-07)

### What changed
- A third planner view, Calendar (Gantt | Board | Calendar): Day / Week time grids and a Month grid over the planner's tickets, overlap packing, a now line redrawn once a minute, create / move / resize with undo. Calendar C1 of [[calendar-plan]] is superseded by it.
- `CalendarSpan { Day, Week, Month }`; `CalendarControl : ContainerControl` (`ArctisAurora.Core.UI`). Consts: public `gutterWidth` 56, `headerHeight` 24, `laneHeight` 22, `hourHeight` 48; private `blockInset` 2, `labelHeight` 18, `cellHeader` 20, `chipHeight` 18, `nowWidth` 2, `dotSize` 8, `outlineWidth` 2, `grabHeight` 6, `attachmentAlpha` 0.4. `public static readonly TimeSpan snap` = 15 min.
- `CalendarControl` public members: `first` (`DateOnly`; Day = anchor, Week = the anchor's week start (`DateFormats.WeekStart`; was Monday until 2026-10-09), Month = the week start on or before the 1st, 6×7 cells), `TopHeight` (pinned header + all-day strip, at least 1 lane), `BodyTop`, `PointAt(DateTime)`, `TimeAt(Vector2)` (unsnapped), `DayAt(Vector2)`, `BlockRect(ticket)` (first shown block, nullable), `TicketAt(point)`, static `Pack(IReadOnlyList<(DateTime start, DateTime end)>) → (int column, int columns)[]`; internal `Changed()`. `PointAt`, `BodyTop` and `TopHeight` are public for the tests.
- Overrides: `OnTick` (invalidates arrange when the minute of `editor.Now` changes; `FrameScheduler.RequestFrameAt` for the next minute boundary; `SetTicking(true)` in the ctor), `OnPointerPress`, `OnDrag`, `OnDragStop`, `OnPointerMove`, `OnPointerExit`, `OnPointerTap`. `contextMenu = "planner"`, `stopsContextMenu = true`.
- Private nested `CalendarParts` (the layer; renamed from a `ChartParts` copy, see [[parallel-stack-name-collisions]]), `CalendarPool<T>` (pooled parts per layer; hides what an arrange did not take), `Piece` struct, `PieceKind { Timed, AllDay, Chip }`, `Grip { Move, Start, End }`. Pieces are built in Measure (all-day lanes packed, then per-day timed pieces packed), rects in Arrange.
- Day / Week: hour gutter pinned left, day-name header and all-day strip pinned top. The strip holds all-day tickets and timed tickets of 24 h or more, packed into lanes. A timed ticket crossing midnight is one piece per day. Timed blocks under the pinned header are not hit. Now line + dot in Danger across today's column.
- Month: weekday row, 6×7 cells, day number (Accent today, MutedInk outside the month), chips "HH:mm Name" (all-day / long tickets: name only), "+N more" when a cell is full.
- Selection outline in Ink. A press on a block selects it and starts a drag: Day / Week timed — middle moves (across days), top / bottom edge resizes (VResize cursor), 15 min snap, minimum one snap; all-day strip and Month chips move by whole days. Release records through `editor.CommitTime`.
- Double click: a block opens the edit popup; an empty grid slot opens the new-ticket popup, 1 h from the slot floored to 15 min; the strip or a Month cell makes a new all-day ticket. Right click opens the `planner` menu.
- `PlannerEditorControl`: `PlannerView` gains `Calendar`; `calendar`; `span` (default Week); private `anchorDay`; `anchor` (today when unset); `ShowSpan(CalendarSpan)`; `Step(int direction)` (±1 day / 7 days / 1 month); `GoToday()`; `NewAt(TimeRange, Control from, Vector2 point)`. Toolbar gains a "Calendar" view button, `<` `Today` `>` step buttons and Day / Week / Month span buttons (shown only on Calendar; the zoom buttons stay Gantt-only), private `ShowIf`.
- The calendar opens scrolled to 07:00, and Day / Week following Month reopen at 07:00: private `pendingTop` + `MorningTop()`, applied in `PlannerScroller.ArrangeCore`. `EditSelected` anchors the popup under `calendar.BlockRect` on the calendar. `DocumentChanged` calls `calendar?.Changed()`.
- `ViewState` / `RestoreView` also store the span in `SessionTab.anchorBlock` and the anchor day as `DateOnly.DayNumber` in `topOffset` (0 = today).
- `PlannerTicketPopup.Open(editor, from, point, ticket, TimeRange? time = null)` — signature change; a new ticket uses `time`, else today all day.
- Tests (Thorium Planner suite, 28 after C2): `Planner.CalendarPack`, `Planner.CalendarGeometry`, `Planner.CalendarDragUndo`, `Planner.CalendarCreate`, `Planner.CalendarMonth`, `Planner.CalendarGolden` (goldens `Planner.CalendarGolden.Week.png`, `.Month.png`); extended `Planner.ViewState` (calendar span + anchor).

### Why
**The calendar's width is the viewport; no horizontal scroll.**
`ScrollableControl` measures its content at the viewport size, so no scroller change was needed.

**Pooled parts, never destroyed on a change.**
Like the Gantt, so the rebuild-inside-pointer-dispatch trap ([[rebuild-inside-pointer-dispatch]]) does not apply to the calendar.

**Packing.**
Sort by start, then longest; clusters of transitively overlapping spans; the first free column; a block's width is the day column's width divided by its cluster's column count. The all-day lanes use the same function. Packing uses the time including attachments.

**The step buttons are ASCII `<` `>`.**
The plan said ‹ ›; glyph coverage of the UI font was not checked.

**Month chips move only.**
Resize is Day / Week only.

**Session state reuses unused `SessionTab` fields** (`anchorBlock`, `topOffset`) rather than adding fields.

### Known gaps
- No drag-to-create on empty time; no resize in Month or in the all-day strip; a timed ticket cannot be dragged into the all-day strip.
- A taller ticket popup that does not fit below the click opens in its own OS window, where tests' `Find` (the primary window) cannot see it.
- Test trap: two clicks in a test count as a double click only if earlier presses are more than 250 ms old (`InputHandler` tap window, real timestamps); earlier tests' drags pushed `tapCount` to 4, so `Planner.CalendarCreate` waits 300 ms of wall time first.
- `timeZone` is still not applied.
- Other hosts' generated schemas (AuroraEditor / Carbon `UITypeSchema.xsd`, `actionSchema.xsd`) regenerate on their next run with the new types and action.
- **Verified** (covers PC and C2): test-verified — `bash _Build/test.sh Planner` → 28 passed (Boot fails only on the baseline sampler error); full run 251 passed, 3 failed — Boot (baseline sampler), `Sheet.FixedSize` (pre-existing), `Perf.TypeLargeNote` (baseline `[Layout] … desired stale`); no `[Vulkan]` lines. golden-verified — `Planner.CalendarGolden` Week (packing Review / Pairing / Lunch, selection outline, now line + dot at Tue 12:00, Offsite in the strip) and Month (chips, muted out-of-month days, selection), `Planner.AttachmentGolden` Gantt and Week; all four approved after reading the PNGs.
- **NOT GUI-verified:** no check in a running Thorium — real-mouse drags / resizes on the calendar, the VResize cursor, the toolbar step / span buttons, the now line advancing with the real clock, the popup's Before / After / Follows rows by hand, session restore across a restart.

## C2 — attachments and links (landed 2026-10-07)

### What changed
- Tickets carry attachments (time before / after, kinds from XML: Travel, Prep, custom) and a single "follows" link (B follows A). Moving A or changing A's end shifts everything downstream of it in the same undo step.
- `PlannerAttachments.cs`: `[A_XSDType("AttachmentKind","UI")] PlannerAttachmentKind` (`name`, `colorHex` default #8E8E93, `minutes` default 30); `[A_XSDType("AttachmentKinds","UI")] PlannerAttachmentKindMap` (the schema's root type); `readonly record struct PlannerAttachment(string kind, bool before, TimeSpan duration)`; static `PlannerAttachmentKinds` — `all`, `Find(name)` (case-insensitive), bootstrap step `[A_XSDActionDependency("PlannerAttachmentKinds.Load","Bootstrap")] Load()` reading the engine `Engine.attachments.axml`, then the host's optional `Attachments.attachments.axml` (a later same-name kind replaces an earlier one).
- Data: `Engine.attachments.axml` — Travel (#8E8E93, 30 min), Prep (#B48EDB, 15 min). `Bootstrap.bootstrap.axml` runs `PlannerAttachmentKinds.Load` after `Effects.LoadEffects`. Thorium ships no host file yet.
- `PlannerTicket`: `attachments` (`List<PlannerAttachment>`), `follows` (`Guid?`), `OuterStart` / `OuterEnd` (time including attachments), `Attached(bool before)`; `Clone` / `CopyFrom` copy both. `PlannerDocument.Find(Guid)`, `Followers(ticket)` (direct), `Upstream(ticket, other)` (true when `other` follows `ticket` directly or through others; cycle-guarded).
- `PlannerXml`: Ticket attribute `Follows="guid"` (written after `Order`); child `<Attachment Kind Side="Before|After" Minutes/>` (written after Assignee, before unknown children). An attachment without a kind or positive minutes is kept as unknown.
- `PlannerEditorControl.Apply` and `CommitTime` record `WithFollowers(ticket, before, after)` (private): the edit plus every ticket downstream shifted by the change of the ticket's END, transitively, visited-set guarded — one `PlannerTicketEdit`.
- `PlannerTicketPopup`: rows "Before" / "After" (comma text "Travel 30m, Prep 1h"; `1h30` = 90 min; a bare number = minutes; no length = the kind's default; an unknown kind is dropped; internal `FormatAttachments`, `ParseAttachments`) and "Follows" (`DropdownControl` "(none)" + every ticket except this one and those downstream of it).
- `GanttChartControl`: attachments are blocks in the kind's colour at alpha 0.4 before / after the bar in its row (`attachmentParts`, bars layer); link elbows (`linkParts`, grid layer, MutedInk, `elbowWidth` 8: right from A's end, down / up to B's row, across to B's start) are drawn when either row is in view; the span uses `OuterStart` / `OuterEnd`. The calendar draws attachments at the same alpha, and packs with the outer time.
- Tests: `Planner.FollowersMove`, `Planner.FollowCycle`, `Planner.AttachmentText`, `Planner.AttachmentGolden` (goldens `Planner.AttachmentGolden.Gantt.png`, `.Week.png`); extended `Planner.XmlRoundTrip` (Follows, Attachment).

### Why
**An attachment is a translucent block in the kind's colour, not dashed.**
The user's pick (1a). The engine has no dashed drawing; a dashed outline from pooled segments would mean many more parts and is invisible at Week / Month Gantt zoom.

**"Follows" shifts followers by the leader's END change, applied on release.**
The user's pick (2a). A move changes the end, so followers shift; resizing only the start leaves them; popup edits that move the end shift them too. Rejected: pushing B only when it would start before A ends — a constraint solver, and gaps become unpredictable. Rejected: followers moving live during a drag — extra per-frame work; they jump on release instead.

**One leader per ticket, `Guid? follows`.**
The user's pick (3a): many may follow one; the popup excludes downstream tickets so the UI cannot make a cycle. Rejected: a list of leaders — it needs a list editor in the popup instead of a dropdown.

**The Gantt link elbows are drawn.**
The user's pick (4 in).

**Attachment kinds are engine data, like gradients and palettes.**
An engine file plus an optional host file, loaded by a bootstrap step (the agreed outline). Rejected: kinds stored per planner file.

**Attachments are edited as comma text,** the same stub style as assignees.

### Known gaps
- Found during verification: `Planner.AttachmentText` clicking the popup's Apply button (close + apply inside a pointer press dispatch, a P2 code path) made the later `Perf.Controls.Scrollable.Relayout` log `[UIEngine] 'entity' subtreeBounds (0, 0, 0, 0) != recomputed …` and `[Layout] skipped layout left … subtreeBounds stale` (×8) in the full run. Bisected by adding tests back one batch at a time to the full run; applying with Enter instead cleared it, and the test now uses Enter. The popup's Apply button is unchanged, so the unguarded engine trap is still reachable through it. See [[rebuild-inside-pointer-dispatch]].
- Attachments are not drawn on the board, in Month chips, or for all-day tickets in the calendar strip.
- Followers move only on release, not live.
- Duplicate ticket names: the Follows dropdown resolves to the first.
- A deleted leader leaves `follows` pointing at a missing id (ignored; undo restores).
- The Gantt link elbow's first segment sits under an after-attachment block (it starts at the leader's end).
- Verification: see the PC section.

## D1/D2 — descriptions and click to expand (landed 2026-10-09)

### What changed
- D1: `PlannerTicket.description` (string, default ""), copied in `CopyFrom`. `PlannerXml`: Ticket child `<Description>text</Description>`, written first among the ticket's children (before `Assignee`), only when non-empty; newlines kept. Format comment: `<Ticket …><Description/><Assignee User/><Attachment …/></Ticket>`.
- NEW `ArctisAurora.Core.UI.Text.RegexTextControl` (sealed, ContainerControl, `IContext`, `IClipboardTarget`, `ITextField`; no XML element): an editable field a regex cuts into segments, each a wrapping run, stacked vertically. Public `pattern` (default `\n`), `separator` (default "\n", what Shift+Enter inserts), `text`, `fontSize`, `isEditing`, `Segments`, `onCommit`/`onCancel`/`onBlur`/`onEdited`, `Focus`, `SelectAll`, `WriteChar`, `InsertSeparator`, `Backspace(word)`, `Delete(word)`, `MoveCaret`, `Commit`, `Cancel`, `Undo`, `Redo`, `Copy`/`Cut`/`Paste`, const `maxLines` 8. Private nested `FieldParts`, `FieldEdit`.
- NEW interface `ITextField` (in `TextInputActions.cs` beside `IClipboardTarget`): `isEditing`, `WriteChar`, `Backspace`, `Delete`, `MoveCaret`, `SelectAll`, `Commit`, `Cancel`, `Undo`, `Redo`. `TextBoxControl` implements it; `TextInputActions.Box()` returns `ITextField`; `Editor()` stops at any editing `ITextField`. `TextInputActions.NewBlock` (Enter): a focused `RegexTextControl` with Shift (Extend modifier) held → `InsertSeparator()`, else `Commit()` as before.
- `PlannerTicketPopup`: private `description` (`RegexTextControl`), "Description" row under Name, min height 3 rows (`descriptionRows`); `Finish` writes `draft.description = description.text.Trim()` in the same undo step.
- D2: `PlannerEditorControl.expanded` (public get), `Expand(PlannerTicket?)` (null, or a ticket with an empty description, folds away; redraws chart / board / calendar); `DocumentChanged` drops `expanded` when the ticket is deleted or its description emptied; `Load` resets it; `EditSelected` gains a board branch (popup under `board.CardOf(selected)`). `PlannerActions.Collapse` = action `Planner.Collapse`, bound to Escape (Press) in Thorium `InputMap.inputs.axml`, calls `Expand(null)`.
- `PlannerBoardControl`: public `CardOf(ticket)`; `Fill(card, document, ticket, bool expanded)` adds one wrapped label per description line under the name; `TicketCard.OnPointerRelease` (left, tapCount 1) selects + expands and returns handled; board `OnPointerRelease` (left, tapCount 1, off any card) folds away; `Rebuild` makes the board the active control when the active one was inside it. `WrappingLabel` moved from a private nested class to namespace level (`internal sealed class WrappingLabel : LabelControl`, same file).
- `GanttChartControl`: public `RowHeight(row)`; private `expandedRow`, `expandedExtra`, `expandedLines`, `wrappedWidth`, `rewrapPending`, `descriptionLines` (`WrappingLabel` pool in the `bars` layer), `RowIndex(y)`, `DescriptionWidth`, `MeasureDescription()`, `ArrangeDescription(view, firstRow, lastRow)`, `OnPointerRelease` (left, tapCount 1, not on the name edge → `editor.Expand(TicketAt(point))`). `RowTop` adds `expandedExtra` below the expanded row; `RowAt`, first / last visible row, row lines and the chart's desired height use the cumulative geometry.
- `CalendarControl`: new layer `cards` (above `strip`, below the selection outline), pools `cardParts`, `cardNames`, `cardTexts` (`WrappingLabel`), field `expandedRect`, private `ShownBlock(ticket, view)` (extracted from `ArrangeSelection`), `ArrangeExpanded(view)`, `OnPointerRelease` (left, tapCount 1 → `editor.Expand(TicketAt(point))`). `ArrangeSelection` outlines the card when the selected ticket is the expanded one; `ClipSlid` clips `cards` too.
- Tests: `Thorium.Tests.TextInputTests` — `RegexText.LinesAndCommit`, `RegexText.WrapMoves`, `RegexText.CommaPattern`; `Thorium.Tests.PlannerTests` — `Planner.Description`, `Planner.ExpandBoard`, `Planner.ExpandGantt`, `Planner.ExpandCalendar`, `Planner.XmlRoundTrip` extended with a two-line description, helper `TapClear()` (waits 300 ms of wall time so the next click is a single one). Goldens `Planner.ExpandGantt.Gantt.png`, `Planner.ExpandCalendar.Week.png`, `Planner.ExpandCalendar.Month.png`.

### Why

**Multi-line editing is a new control, not a change to `TextMeasurer` or `TextBoxControl`.**
`TextRunControl` wraps but has no hard line break (notes avoid it because each paragraph is a block). Rejected: teaching `TextMeasurer` to break on `\n` (touches the layout core that notes and Tex depend on — greedy, optimal and around-slots paths) and a multi-line mode on `TextBoxControl`. The user asked for the generic form: a field whose segments come from a regex, so the same control can later split e.g. assignees on `,\s*`. Matched text is not drawn; a caret offset inside a match snaps to the nearest segment edge; Backspace at a segment's start / Delete at its end remove the whole match; pasted line breaks become `separator`.

**Segments stack vertically only.**
Inline flow (chips) was left out — not asked for.

**Enter applies, Shift+Enter inserts a line break.**
The user chose Enter as the universal confirm key. Shift is read inside `Text.NewBlock` off the plain Enter bind (same pattern as `Sheet.Enter`), so there is no new keybind and notes are unaffected (their branch runs first). Rejected: Ctrl+Enter to apply; Ctrl+Enter for newline; no line breaks.

**Storage is a `<Description>` child element, not an attribute.**
XML normalises newlines inside attributes to spaces.

**Vertical caret moves and Home/End read the runs' last measured lines.**
If an edit in the same frame left them stale, the move is skipped rather than measuring outside the layout pass (measuring touches the `UIElements` pool, which `Main.Input` does not declare).

**Expand happens on pointer release, not on tap.**
`UIEngine` dispatches `Tap` only for tapCount ≥ 2; single clicks arrive as `Release`, and a drag past the click slop never releases onto the control — so dragging a bar, block or card never expands it. The second click of a double click (tapCount 2) does not change the expansion, so double-click-to-edit still works. Clicking the already-expanded ticket keeps it open (no toggle) for the same reason.

**The Gantt description is pinned to the viewport's body.**
It runs from the name column's edge to the viewport's right edge, like the name column is pinned, not wrapped "from the bar's left edge to the viewport's right edge" as first planned. Found in implementation: that width changes with horizontal scroll, so the row height would change and rows below jump while scrolling, and the height would need re-measuring from inside arrange (layout trap). Also rejected: bar-left with a fixed 360 px width (can run off-screen). A viewport width change (window resize) posts one `InvalidateLayout` (`rewrapPending`), so the height catches up one frame later.

**The calendar card grows downward over the blocks below and draws above them; hit-testing still follows each block's real time range.**
The card is not hit-testable, so drags and clicks on the covered block keep working. Card width = the block's width (narrow when packed into columns). Month: the chip grows down over the cells below.

**Display labels split the description on `\n`.**
One wrapping label per line, because labels don't break on newlines either.

**Pooled part order is draw order.**
The calendar card panel is taken from its pool before the text labels, else the labels are adopted first and draw under the card (found in the first golden).

**A nested class name collided again.**
A nested `Layer` in `RegexTextControl` crashed boot (`AssetRegistries.RegisterSerializableTypes` keys every `Entity` subclass by bare `Type.Name`; `CustomEntities.Layer` exists); renamed `FieldParts`. `WrappingLabel` was hoisted to namespace level so calendar and Gantt share it with the board. See [[parallel-stack-name-collisions]].

**The board rebuild stole focus back.**
Expanding rebuilds the board, destroying the clicked card that was the active control and leaving no parent chain for `PlannerActions.Editor()` (Esc / Delete did nothing). `Rebuild` now sets the board active first. See [[rebuild-inside-pointer-dispatch]].

### Known gaps
- Description is plain text; no rich text, no styling of matches.
- `RegexTextControl` segments stack vertically only; no inline (chip) flow; no `<RegexText>` XML element.
- No editing in the expanded view; edit through the popup.
- Calendar card width is the block width, so a packed narrow block gives a tall narrow card; a card disappears when its block's top scrolls under the pinned header (same rule as the selection outline).
- Gantt today line and grid lines draw across the description text.
- Gantt description re-wraps one frame late after a window resize.
- A vertical caret move in the same frame as an edit is skipped (stale lines).
- The taller popup (Description row adds ~78 px) opens in its own OS window more often when it doesn't fit below the click (pre-existing behaviour).
- Board has no double-click-to-edit on cards still (pre-existing WIP item); a selected card has no outline.
- **Verified:** test-verified and golden-verified. Full run 304 passed, 1 failed (Boot — the baseline default-sampler error), 1 skipped (`Perf.AtlasBake`, unoptimized JIT); no `[Vulkan]` lines. Planner suite 32 passed + Boot; TextInput suite 94 passed + Boot. Goldens `Planner.ExpandGantt.Gantt`, `Planner.ExpandCalendar.Week`, `Planner.ExpandCalendar.Month` approved after reading the PNGs (Gantt row grown with two pinned lines, Print row pushed down; Week card over the Review block's top with the selection outline; Month card over the cell). Synthetic input (t.Click / t.Key / t.Type) drove the popup, Shift+Enter, Enter, Esc, clicks.
- **NOT GUI-verified:** real mouse/keyboard in a running Thorium — typing into the description box, Shift+Enter, caret Up/Down feel, selection across lines, scrolling past 8 lines, clicking cards / bars / blocks, Esc, window resize re-wrap of the Gantt description.

## Calendar snap and week start (landed 2026-10-09; snap superseded 2026-10-10)

The snap described below (quiet period, `lastScroll`, `snapDelay`, `snapSeconds`, nearest-day rounding) was replaced on 2026-10-10: the calendar now holds the slide while scrolling and, on `OnPointerScrollEnd`, eases over 0.15 s (cubic ease-out) onto the next whole day (Day/Week) or week (Month) in the direction of travel. See [[scroll-gesture-end]]. The week-start part is unchanged.

### What changed
- `CalendarControl` (superseded, see above): `OnPointerScroll` recorded `lastScroll` and called `FrameScheduler.RequestFrameAt(lastScroll + snapDelay)`; `OnTick` eased `slide` to `MathF.Round(slide)` after the quiet period. Const `weekStartAlpha` 0.35 remains. New pool `weekStartParts` (grid layer, `PaletteRole.Line` at 0.35 alpha) tints the week-start day's column; its header name is bold.
- The week start is now `DateFormats.WeekStart` (a setting), not a hard-coded Monday: `CalendarControl.first` and `GanttChartControl.Floor` (Week unit). Month view: the week start is always column 0.
- Date and time display on the board, Gantt and calendar follows `DateTimeSettings` — see [[date-time-settings]].
- Tests: `Planner.CalendarSnap`, `Planner.WeekStart`; helper `PinDates` called from `Show`.

### Why
**(Superseded 2026-10-10 by [[scroll-gesture-end]]) Snap to the nearest whole day or week (`MathF.Round`).**
Rejected then: a direction-based snap, which needs velocity tracking for no asked-for gain. Quiet-period detection was time-based with an explicit `RequestFrameAt`. Both reversed: the snap is now direction-of-travel and starts on gesture end.

**The week-start highlight is a faint whole-column tint, not a stronger divider line.**
Alpha 0.5 looked too heavy in the golden, so 0.35.

### Known gaps
- Snap tests wait about 1 s of real time per step via `Engine.totalTime` loops.
- The existing CalendarGeometry slide check (half a day after 2 frames) can in principle be snapped back if 2 frames take over 0.15 s.
- **Verified:** test-verified and golden-verified. `_Build/test.sh` full run 306 passed, 1 failed (Boot — the baseline default-sampler error), 1 skipped; no `[Vulkan]` lines. Goldens re-approved after reading diffs (only the tint changed): `Planner.CalendarGolden.Week/Month`, `Planner.ExpandCalendar.Week/Month`, `Planner.AttachmentGolden.Week`.
- **Not checked:** real wheel/trackpad feel of the snap.

Related: [[date-time-settings]], [[planner-plan]], [[calendar-plan]], [[sheets]], [[context-menus]], [[note-naming-and-text-field]], [[parallel-stack-name-collisions]], [[rebuild-inside-pointer-dispatch]], [[star-child-in-unbounded-stack]], [[ui-engine-stack]]
