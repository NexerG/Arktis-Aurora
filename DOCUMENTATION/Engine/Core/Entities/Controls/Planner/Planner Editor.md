---
date: 2026-10-07
Status: Current
tags:
  - d_UI
  - d_Entity
cssclasses:
  - Aurora.css
Linker:
  - "[[Arctis Aurora]]"
System:
Class:
  - "[[Planner Editor]]"
Parent Class:
  - StackPanelControl
Interfaces:
  - IFileEditor
Used by:
  - VaultBrowserControl
Type:
  - Public
Attributes:
Namespace: ArctisAurora.Core.UI
SourceFile: AuroraEngine/Core/UI/Planner/PlannerEditorControl.cs
VerifiedAgainst: 2026-10-07
---
## Description

One open planner in a tab: a toolbar over either a Gantt chart, a board or a calendar of the same tickets, the `PlannerDocument` they show, and the file it came from. A planner is a `*.planner.axml` file in the vault, opened from the browser like a note or a sheet, and made with "New planner" in the File, vault and folder menus.

A planner holds categories and the tickets filed under them. A ticket has a name, a category, an optional colour of its own, a time range, a creator and a list of assignees. A ticket without a colour takes its category's colour, and a ticket whose category is missing is shown under the first category.

The Gantt view has a row per category, shaded, with its tickets under it, and a bar per ticket across a date header. It zooms between Hour, Day, Week and Month, and draws a line at the current time. The board view has a column per category and a card per ticket. Both are edited as described under Editing; cards on the board are dragged between and within columns. Categories can be added, renamed, recoloured and deleted, and board cards show the initials of their assignees as chips.

The calendar view lays the same tickets on a time grid, a Day or a Week at a time, or on a Month grid, and is edited by dragging blocks. A ticket can also carry attachments, time reserved before or after it such as travel or preparation, and can follow another ticket so that it moves with it.

Only the rows and date units inside the viewport have controls. The chart keeps a pool of parts and rebinds them to whatever is in view, the way the sheet grid does, with the name column pinned to the left and the date header pinned to the top.

A ticket's time is a `TimeRange`: a wall-clock start and an exclusive end, either all-day (midnight to midnight) or timed. A time zone name can be stored with it and is written back unchanged, but no conversion is made.

The creator of a ticket is a `User`, from `ArctisAurora.Core.Users`. There are no accounts: the current user is set by hand to "Grexen". A user is stored by name, and two users with the same name are equal.

## File format

```xml
<Planner Name="Launch">
	<Category Id="…" Name="Design" Color="#4C8BF5"/>
	<Ticket Id="…" Name="Mockups" Category="…" Start="2026-10-07" End="2026-10-10" AllDay="true" Creator="Grexen" Created="2026-10-07T09:30:00" Order="0" Follows="…">
		<Description>Two lines
of text</Description>
		<Assignee User="Grexen"/>
		<Attachment Kind="Travel" Side="Before" Minutes="30"/>
	</Ticket>
</Planner>
```

All-day times are written `yyyy-MM-dd`, timed ones `yyyy-MM-ddTHH:mm`. A planner with no category gets one called "General" when it is read. A ticket without a readable start and end is kept as an unknown element and written back unchanged, as is any other element the reader does not know. Categories are written first, then tickets, then the unknown elements. A ticket's `Follows` attribute names the ticket it follows and is written after `Order`. An `Attachment` element holds a kind, a side (`Before` or `After`) and a number of minutes, and is written after the assignees; one without a kind or without positive minutes is kept as an unknown element.

A ticket's description is plain text kept in a `<Description>` child, written first among the ticket's children and only when it is not empty. It is a child element rather than an attribute because XML turns newlines inside an attribute into spaces, and the description keeps its line breaks.

## Editing

Click a bar on the Gantt chart to select it; the selected bar is outlined in Ink, and its whole row, name column included, is washed in Accent at 15% alpha. Drag the middle of a bar to move it, or drag near an edge to resize it. The edge zone is the smaller of 6 pixels and a third of the bar inside the bar, and 6 pixels outside it; over an edge the cursor becomes a horizontal resize. A drag snaps by the delta rather than by the edge: to the hour on Hour zoom for a timed ticket, to the day otherwise, so a timed ticket moved on Day zoom keeps its time of day. A resize keeps at least one unit. A whole drag is one undo step, "Move ticket" or "Resize ticket". Double-click a bar to edit it.

"+ Ticket" in the toolbar opens the ticket popup for a new ticket. The new ticket is called "New ticket", lasts today as an all-day ticket, is created by the current user, and goes last in the selected ticket's category, or in the first category when nothing is selected. Delete removes the selected ticket. Ctrl+Z undoes, Ctrl+Y or Ctrl+Shift+Z redoes, and Ctrl+S saves.

The ticket popup has a name box, a category dropdown, start and end boxes, an assignees box, a colour picker, the creator ("Created by") and an Apply button. Dates are `yyyy-MM-dd` for an all-day ticket, with the end shown as the last day, and `yyyy-MM-dd HH:mm` for a timed one; two dates make the ticket all-day, and a time in either box makes it timed. A box that cannot be read keeps its value, and an end at or before the start becomes one day or one hour after it. Assignees are comma-separated names. Enter in any box or Apply applies all fields as one undo step; Esc or a click outside cancels. Changing the category puts the ticket last in the new one. The category list opens over the popup without closing it. There is no Tab between the boxes yet.

Right-click a bar for the planner menu: Edit ticket, Delete ticket and a Move to category submenu that lists the planner's categories when it opens, so it is never stale. The right click selects the ticket in the row first.

Press a card on the board and move it 4 pixels to start dragging it; a preview of the card follows the pointer, as a dragged tab does. While the pointer is over the board the column it would drop into is outlined in Accent, and a gap opens where the card will land: above the card the drop lands before, or below the last card when it lands last. The column is the one whose horizontal range holds the pointer, otherwise the nearest, and the place is after the column's other cards whose centre is above the pointer. Dropping files the ticket under that column's category at that place and renumbers the column's `order`, as one undo step, "Move ticket". The source column is not renumbered, so its `order` may keep a gap. Dropping outside the board does nothing. Cards cannot be selected or opened from the board yet. The board is rebuilt one tick after any change, not during the click or drag that caused it, so a change shows on the board one tick late.

The ticket popup has a Description row under the name, at least three rows tall. It is a `RegexTextControl`: an editable field that a regex cuts into segments, each shown as its own wrapping run, stacked one under the other. For the description the pattern is a line break, so each line is a segment. Enter applies the popup as before and Shift+Enter inserts a line break; a pasted line break becomes a line break in the field, and the field's `maxLines` is 8. The description is plain text, trimmed when it is applied, and is part of the same undo step as the rest of the edit.

A single click on a ticket in any view expands it to show its description. On the board the card grows; on the Gantt chart the row grows and pushes the rows below it down; in the calendar the block (Day, Week) or the chip (Month) grows downward over what is below it, drawn above those blocks but not hit-tested, so a click or drag there still reaches the block underneath. One ticket is expanded at a time, and a ticket with no description does not expand. The expansion happens when the mouse button is released, so dragging a bar, block or card never expands it, and the second click of a double click leaves the expansion as it is so that double-click-to-edit still works. Clicking the expanded ticket again keeps it open. Escape, or a click on nothing, folds it away. The Gantt description runs from the name column's edge to the viewport's right edge, so scrolling sideways does not change the row heights; it re-wraps one frame after the window is resized. The expanded view is read-only, so a description is edited through the popup.

The editor reads "now" from a clock, which is the system time unless a test pins it; the today line and the chart span follow it.

## Categories

"+ Category" in the toolbar opens the category popup for a new category, which starts with the planner's default colour and goes last. Double-click a category row on the Gantt chart or a column on the board to edit that category. Right-click either and choose "Edit category" or "Add category"; Edit acts on the category last pressed on, a right press included, so the menu needs no target of its own. Each change is one undo step: "Add category", "Edit category" or "Delete category".

The popup has a name box, a colour picker and an Apply button, and a Delete button when a category is being edited and the planner has more than one. Enter or Apply applies. An empty name keeps the old name, or becomes "New category" for a new category. Esc or a click outside cancels.

Deleting a category leaves its tickets pointing at it. They are shown under the first category while it is gone, and one undo brings the category back with its tickets in place. The saved file still names the deleted category on those tickets, and on load they fall back to the first category. The last category cannot be deleted. The order of categories cannot be changed yet, and two categories with the same name resolve to the first in the ticket popup's dropdown.

## Calendar

The toolbar's "Calendar" button shows the calendar. Its Day, Week and Month buttons choose the span, and `<`, `Today` and `>` step the shown period by a day, a week or a month; the zoom buttons belong to the Gantt chart only. Week is the default span. The calendar opens scrolled to 07:00, and Day or Week reopens at 07:00 after Month.

Day and Week show a time grid. The hours are pinned to the left, and the day names and an all-day strip are pinned to the top. The strip holds all-day tickets and timed tickets of 24 hours or more, packed into lanes. A timed ticket that crosses midnight is drawn as one block per day. A line with a dot marks the current time across today's column, and it moves once a minute so an idle window stays idle. A Week starts on the anchor's week-start day, which is a setting (Monday, Sunday and so on, or "System" for the OS culture), and that day's column is tinted faintly with its name in bold. While scrolling sideways the slide stays wherever it is left, however slowly it moves. When the scroll gesture ends (the last finger leaves a precision touchpad and its momentum fades, or a mouse wheel is quiet for 0.15 seconds) the slide eases over 0.15 seconds onto the next whole day (Day and Week) or whole week (Month) in the direction it was travelling. The clock, the hour labels and the date order follow the date and time settings.

Month shows a grid of 6 weeks, starting on the week-start day on or before the 1st. Each cell has its day number, in Accent for today and muted outside the month, and a chip per ticket, "HH:mm Name" or just the name for an all-day or long ticket. A full cell ends with "+N more".

Tickets that overlap in time are packed side by side: they are sorted by start, then longest first, split into clusters of tickets that overlap directly or through others, and each is given the first column that is free. The all-day strip packs its lanes the same way. Packing uses a ticket's time including its attachments.

Click a block to select it; it is outlined in Ink. On Day and Week, drag the middle of a timed block to move it, also to another day, or drag its top or bottom edge to resize it; the cursor becomes a vertical resize over an edge. A drag snaps to 15 minutes and a resize keeps at least one snap. A block in the all-day strip or a Month chip moves by whole days and cannot be resized. Releasing records the change as one undo step.

Double-click a block to edit it. Double-click an empty slot of the time grid for a new ticket of one hour, starting at the slot floored to 15 minutes. Double-click the strip or a Month cell for a new all-day ticket. Right-click opens the planner menu. The calendar is as wide as the viewport and does not scroll sideways.

## Attachments and links

An attachment is time reserved before or after a ticket, of a kind. The kinds are data: the engine ships Travel (30 minutes by default) and Prep (15 minutes), each with a colour, and a host may add its own in an optional file; a kind with the same name as an earlier one replaces it. On the Gantt chart an attachment is a translucent block in the kind's colour beside the bar, in the same row. On the calendar the Day and Week views draw them the same way. The board, Month chips and the all-day strip do not show them yet. A ticket's outer time is its time including its attachments, and the Gantt span and the calendar packing use it.

The ticket popup has a "Before" and an "After" row. An attachment is written as a kind and a length, separated by commas: "Travel 30m, Prep 1h". `1h30` means 90 minutes, a bare number means minutes, no length takes the kind's default, and an unknown kind is dropped.

A ticket can follow one other ticket, chosen in the popup's "Follows" dropdown, which lists every ticket except the ticket itself and those downstream of it, so a cycle cannot be made. Several tickets may follow the same one. When a ticket's end changes, every ticket downstream of it moves by the same amount, in the same undo step. Moving a ticket changes its end; resizing only its start does not; an edit in the popup that moves the end does. Followers jump when the drag is released, not while it is dragged. The Gantt chart draws a link from the leader's end, down or up to the follower's row, and across to its start.

## Views and zoom

The zoom sets how wide a day is: 768 pixels at Hour, 32 at Day, 10 at Week and 3 at Month. The chart spans every ticket and today, padded by 4 units, or 6 at Hour. The date header has two bands: the upper band shows days, months or years, and the lower band shows the zoom unit. Weeks start on the week-start day from the date and time settings. Changing the zoom keeps the time at the left edge of the viewport where it was.

Separately from that time zoom, all three views follow the document zoom setting that notes and sheets use: sizes, spacing and text in the Gantt chart, the board and the calendar are laid out at that zoom, and Ctrl+wheel over the planner changes it. The toolbar and the popups stay at the app zoom. The Gantt name column and the board column widths are stored at 100%, so a resize drag at 200% stores half the pointer distance.

A board card has a colour strip across its top, then the name, the dates and "by" the creator. When the ticket has assignees, a row of chips follows, one per assignee, showing their initials: the first letters of the first two words of the name, or the first two letters of a one-word name, in capitals, so "Ada Lovelace" is "AL" and "Grexen" is "GR"; an empty name shows "?". A column's heading is its swatch, the category name and the ticket count. The tickets of a column are in `order`, then by start.

The view, zoom and scroll are kept with the session, so a restored tab opens where it was left. On the calendar the span and the anchor day are kept too, with today meaning today when the tab is restored.

## API summary

| Member | Kind | Summary |
| --- | --- | --- |
| `LoadPath(path)` | public | Loads a planner file and shows it. |
| `Save()` | public | Writes the planner back to its file. |
| `ShowView(view)` | public | Switches between the Gantt chart, the board and the calendar. |
| `ShowSpan(span)` | public | Sets the calendar's span: Day, Week or Month. |
| `Step(direction)` | public | Moves the calendar's anchor by a day, a week or a month, forward or back. |
| `GoToday()` | public | Sets the calendar's anchor to today. |
| `NewAt(time, from, point)` | public | Opens the ticket popup for a new ticket over a time range. |
| `SetZoom(zoom)` | public | Sets the Gantt zoom, keeping the time at the viewport's left edge. |
| `ViewState()` / `RestoreView(view)` | public | The view, zoom and scroll, for session restore. |
| `Repath(path)` | public | Points the tab at a renamed file. |
| `Select(ticket)` | public | Selects a ticket and outlines its bar. |
| `AddTicket()` | public | Opens the ticket popup for a new ticket. |
| `EditSelected()` | public | Opens the ticket popup for the selected ticket, under its bar, block or card. |
| `expanded` | property | The ticket whose description is shown, or nothing. |
| `Expand(ticket)` | public | Shows a ticket's description in every view; nothing, or a ticket with no description, folds it away. |
| `PlannerBoardControl.CardOf(ticket)` | public | The card of a ticket on the board. |
| `GanttChartControl.RowHeight(row)` | public | The height of a row, taller for the expanded ticket. |
| `MoveSelected(category)` | public | Moves the selected ticket to the end of a category, as one undo step. |
| `MoveTicket(ticket, category, index)` | public | Files a ticket under a category at an index, renumbering that column's `order`, as one undo step. |
| `AddCategory()` | public | Opens the category popup for a new category. |
| `EditPickedCategory()` | public | Opens the category popup for the category last pressed on. |
| `ApplyCategory(category, name, colorHex)` | public | Applies the popup: an added category when it is null, otherwise an edit when the name or colour changed, as one undo step. |
| `DeleteCategory(category)` | public | Removes a category as one undo step; never the last one. Its tickets keep pointing at it. |
| `PickCategory(category, from, point)` | public | Remembers the category last pressed on and where. |
| `GanttChartControl.CategoryAt(point)` | public | The category whose row holds the point. |
| `PlannerBoardControl.RebuildSoon()` | internal | Posts one rebuild of the board for the next tick. |
| `PlannerBoardControl.CardsIn(category)` | public | The cards of a category's column, top to bottom. |
| `CalendarControl.Pack(spans)` | public | Gives each span a column and the column count of its cluster. |
| `CalendarControl.TicketAt(point)` | public | The ticket whose block holds the point. |
| `CalendarControl.BlockRect(ticket)` | public | The rectangle of the ticket's first shown block, or nothing. |
| `PlannerDocument.Followers(ticket)` | public | The tickets that follow a ticket directly. |
| `PlannerDocument.Upstream(ticket, other)` | public | True when another ticket follows the ticket, directly or through others. |
| `PlannerAttachmentKinds.Find(name)` | public | The attachment kind of that name, case-insensitive. |
| `DeleteSelected()` | public | Removes the selected ticket, as one undo step. |
| `Apply(ticket, draft)` | public | Applies the popup's draft as one undo step: an edit of the ticket, or an added ticket when it is null. |
| `Undo()` / `Redo()` | public | Steps through the planner's undo history. |
| `PlannerDocument.NextOrder(category)` | public | The order that puts a ticket last in a category. |
| `PlannerDocument.Blank(name)` | public | A planner with one category, "General". |
| `PlannerDocument.CategoryOf(ticket)` | public | The ticket's category, or the first when it is missing. |
| `PlannerDocument.ColorOf(ticket)` | public | The ticket's colour, else its category's. |
| `PlannerDocument.TicketsIn(category)` | public | A category's tickets by order, then start. |
| `TimeRange.Days(first, last)` | public | An all-day range from the first day to the last, end exclusive. |
| `User.Named(name)` | public | The current user for its own name, otherwise a new user of that name. |
| `User.initials` | public | The first letters of the first two words of the name, or the first two letters of a one-word name, in capitals; "?" when empty. |

## Methods

### SetZoom
	remember the time at the viewport's left edge
	set the zoom on the chart
	lay the chart out again
	scroll so that the remembered time is at the left edge

### Reading a planner
	read the Planner element's name
	for each Category element
		read its id, name and colour
	if there is no category
		add a category called General
	for each Ticket element
		if it has no readable start and end
			keep the element as unknown
		otherwise
			read its fields and its Assignee elements
	keep every other element as unknown

### CategoryOf
	find the category the ticket names
	if there is none
		return the first category

### Dragging a bar
	on press over a bar
		select the ticket
		remember a clone of it as before
		start the drag, as a move in the middle or a resize at an edge
	on drag
		snap the pointer delta to the hour on Hour zoom for a timed ticket, otherwise to the day
		change the ticket's start and end live
		for a resize keep at least one unit
	on drag stop
		record one Move ticket or Resize ticket step with before and after

### Dragging a card
	on a left press over a card
		arm the card
	on pointer move past 4 pixels while armed
		start the drag
		show the drag ghost
	while the pointer is over the board
		find the column whose horizontal range holds the pointer, otherwise the nearest
		count the column's other cards whose centre is above the pointer, as the index
		outline the column in Accent
		open a gap above the card at the index, or below the last card
	when the pointer leaves the board
		clear the outline and the gap
	on drop
		clear the outline and the gap
		call MoveTicket
	on drag stop
		hide the drag ghost

### MoveTicket
	build the target column's tickets without the dragged one
	insert the ticket at the index, clamped to the column
	for each ticket in the column
		set its order to its position
	set the dragged ticket's category to the column's
	for each ticket whose order or category differs from before
		keep the ticket with its before and after
	record one Move ticket step holding all of them
	the document raises Changed once

### Apply
	if the ticket is null
		the draft becomes the ticket
		record an Add ticket step and select it
	otherwise
		record one Edit ticket step that copies the draft over the ticket
	the document raises Changed
	the chart and the board are drawn again

### Expand
	if the ticket is null or its description is empty
		expanded becomes nothing
	otherwise
		expanded becomes the ticket
	the chart, the board and the calendar are drawn again

A change to the document drops `expanded` when its ticket is deleted or its description is emptied, and loading a planner resets it.

### RebuildSoon
	if a rebuild is already pending
		return
	mark a rebuild as pending
	post a rebuild for the next tick
	when the posted rebuild runs
		clear the pending mark
		if the board was destroyed
			return
		rebuild the columns and cards

### ApplyCategory
	if the category is null
		add a category with the name and colour, last
		record one Add category step
	otherwise if the name or colour differs from before
		record one Edit category step with before and after
	the document raises Changed
	the board rebuild is posted

### DeleteCategory
	if the category is the only one
		return
	record one Delete category step that removes the category at its index
	leave the tickets as they are
	the document raises Changed
	the board rebuild is posted

### Packing
	sort the spans by start, longest first when the starts are equal
	split them into clusters of spans that overlap, directly or through others
	for each cluster
		for each span in order
			give it the first column in which no earlier span of the cluster is still running
		count the columns the cluster used
	each span takes its column and its cluster's column count

### Dragging in the calendar
	on press over a block
		select the ticket
		remember a clone of it as before
		start the drag, as a move in the middle, or a resize at the top or bottom edge of a timed block
	on drag
		for a timed block, snap the pointer to 15 minutes
		for a block in the strip or a Month chip, snap to whole days
		for a resize keep at least one snap
	on drag stop
		call CommitTime with before and after

### Following
	take the change of the ticket's end
	record the edit of the ticket
	for each ticket that follows it, then each ticket that follows that one
		skip a ticket that was already visited
		move its start and end by the change of the end
		add its edit to the record
	the record is one undo step

## Keys

Delete removes the selected ticket, Ctrl+Z undoes, Ctrl+Y or Ctrl+Shift+Z redoes and Ctrl+S saves. They are second binds beside the sheet and text ones and do nothing unless a planner holds the active control. Escape (`Planner.Collapse`) folds an expanded ticket away.

## Related
- [[Sheet Editor]] — the editor the planner is built like
