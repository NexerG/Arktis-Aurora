using ArctisAurora.Core.Editing;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.Core.UI.Windows;
using System.Numerics;

namespace ArctisAurora.Core.UI.Planner
{
    public enum PlannerView { Gantt, Board, Calendar }

    // One open planner: a toolbar over the Gantt chart or the board, and the file behind them.
    public class PlannerEditorControl : StackPanelControl, IFileEditor
    {
        private const float buttonHeight = 20f;
        private const float buttonInset = 8f;
        private const int fontSize = 13;
        private const float chevronSize = 12f;
        private const float trackInset = 2f;

        public PlannerDocument document { get; private set; } = null!;
        public string? path { get; private set; }
        public bool unsaved => document.unsaved;
        public UndoStack undo => document.undo;

        bool IFileEditor.isDirty => unsaved;

        public PlannerView view { get; private set; }
        public PlannerZoom zoom { get; private set; } = PlannerZoom.Day;
        public float contentZoom { get; private set; } = DocumentEditorControl.DocumentZoom;
        public PlannerTicket? selected { get; private set; }
        public PlannerTicket? expanded { get; private set; }

        // the calendar's span and the day it shows; no anchor means today
        public CalendarSpan span { get; private set; } = CalendarSpan.Week;
        private DateOnly? anchorDay;
        public DateOnly anchor => anchorDay ?? DateOnly.FromDateTime(Now);
        public bool unsnapped { get; private set; }

        // the category last pressed on, and where, for the category popup
        public PlannerCategory? pickedCategory { get; private set; }
        private Control? pickedFrom;
        private Vector2 pickedAt;

        // the time the today line and new tickets use
        public Func<DateTime> clock = () => DateTime.Now;
        public DateTime Now => clock();

        public readonly ScrollableControl scroller;
        public GanttChartControl? chart { get; private set; }
        public PlannerBoardControl? board { get; private set; }
        public CalendarControl? calendar { get; private set; }

        // the tab-row tools and their buttons, lit for the shown view, zoom and span
        public Control tools { get; }
        private readonly ButtonControl[] viewButtons = new ButtonControl[3];
        private readonly ButtonControl[] zoomButtons = new ButtonControl[4];
        private readonly ButtonControl[] spanButtons = new ButtonControl[3];
        private readonly ButtonControl[] stepButtons = new ButtonControl[3];
        private readonly ButtonControl addButton;
        private readonly ButtonControl addCategoryButton;
        private readonly StackPanelControl spanTrack;
        private readonly StackPanelControl zoomTrack;

        // honoured at the end of the scroller's Arrange
        private SessionTab? pendingView;
        private DateTime? pendingLeft;
        private float? pendingTop;

        public PlannerEditorControl()
        {
            horizontalAlignment = HorizontalAlignment.Stretch;
            verticalAlignment = VerticalAlignment.Stretch;
            PaintOr(null, PaletteRole.Surface);

            TabToolsControl toolbar = new TabToolsControl();
            tools = toolbar;
            viewButtons[0] = Button("Gantt", () => ShowView(PlannerView.Gantt));
            viewButtons[1] = Button("Board", () => ShowView(PlannerView.Board));
            viewButtons[2] = Button("Calendar", () => ShowView(PlannerView.Calendar));
            toolbar.AddChild(Segment(viewButtons));
            addButton = Button("+ Ticket", AddTicket);
            toolbar.AddChild(addButton);
            addCategoryButton = Button("+ Category", AddCategory);
            toolbar.AddChild(addCategoryButton);
            toolbar.Separator();
            stepButtons[0] = Chevron("chevron-left-outline", () => Step(-1));
            stepButtons[1] = Button("Today", GoToday);
            stepButtons[2] = Chevron("chevron-right-outline", () => Step(1));
            foreach (ButtonControl button in stepButtons)
                toolbar.AddChild(button);
            for (int i = 0; i < spanButtons.Length; i++)
            {
                CalendarSpan shown = (CalendarSpan)i;
                spanButtons[i] = Button(shown.ToString(), () => ShowSpan(shown));
            }
            spanTrack = Segment(spanButtons);
            toolbar.AddChild(spanTrack);
            for (int i = 0; i < zoomButtons.Length; i++)
            {
                PlannerZoom level = (PlannerZoom)i;
                zoomButtons[i] = Button(level.ToString(), () => SetZoom(level));
            }
            zoomTrack = Segment(zoomButtons);
            toolbar.AddChild(zoomTrack);

            scroller = new PlannerScroller(this);
            AddChild(scroller);
        }

        public void LoadPath(string nameOrPath)
        {
            string full = Path.GetFullPath(Path.IsPathRooted(nameOrPath) ? nameOrPath : Paths.Doc(nameOrPath));
            Load(PlannerBook.Open(full));
            path = full;
        }

        public void Load(PlannerDocument loaded)
        {
            Release();
            document = loaded;
            document.changed += DocumentChanged;
            selected = null;
            expanded = null;
            Build();
        }

        public override void OnDestroy()
        {
            Release();
            tools.Destroy();
            base.OnDestroy();
        }

        // Lets go of the shown document, and of its PlannerBook copy when one was opened by path.
        private void Release()
        {
            if (document == null) return;
            document.changed -= DocumentChanged;
            if (path != null) PlannerBook.Close(path, document);
            path = null;
        }

        // An edit or its undo: the file is unsaved and the shown view redraws.
        private void DocumentChanged()
        {
            document.unsaved = true;
            if (selected != null && !document.tickets.Contains(selected)) selected = null;
            if (expanded != null && (!document.tickets.Contains(expanded) || expanded.description.Length == 0)) expanded = null;
            chart?.RowsChanged();
            board?.RebuildSoon();
            calendar?.Changed();
        }

        // The shown view, fresh, at the top left.
        private void Build()
        {
            chart?.Destroy();
            board?.Destroy();
            calendar?.Destroy();
            chart = null;
            board = null;
            calendar = null;

            if (view == PlannerView.Gantt)
            {
                chart = new GanttChartControl(this);
                scroller.AddChild(chart);
            }
            else if (view == PlannerView.Board)
            {
                board = new PlannerBoardControl(this);
                scroller.AddChild(board);
            }
            else
            {
                calendar = new CalendarControl(this);
                scroller.AddChild(calendar);
                pendingTop = MorningTop();
            }
            scroller.SetScrollOffset(Vector2.Zero);
            Light();
        }

        // The day grid's scroll that shows 07:00 at the top.
        private float MorningTop() => 7f * CalendarControl.hourHeight * contentZoom;

        // Re-lays the shown view at a content zoom.
        internal void SetContentZoom(float value)
        {
            if (value == contentZoom) return;
            contentZoom = value;
            chart?.Rezoom();
            board?.Rezoom();
            calendar?.Rezoom();
            scroller.InvalidateArrange();
        }

        public void ShowView(PlannerView shown)
        {
            if (shown == view) return;
            view = shown;
            Build();
        }

        // Keeps the time at the viewport's left edge where it was.
        public void SetZoom(PlannerZoom level)
        {
            if (level == zoom) return;
            if (chart != null) pendingLeft = chart.TimeAt(chart.arrangedRect.x + scroller.GetScrollOffset().X + chart.nameWidth);
            zoom = level;
            chart?.RowsChanged();
            scroller.InvalidateArrange();
            Light();
        }

        // Day and Week open the grid at 07:00 when they follow Month.
        public void ShowSpan(CalendarSpan shown)
        {
            if (shown == span) return;
            if (span == CalendarSpan.Month) pendingTop = MorningTop();
            span = shown;
            unsnapped = false;
            calendar?.Changed();
            scroller.InvalidateArrange();
            Light();
        }

        // A day, a week or a month back or forward.
        public void Step(int direction)
        {
            anchorDay = span switch
            {
                CalendarSpan.Day => anchor.AddDays(direction),
                CalendarSpan.Week => anchor.AddDays(7 * direction),
                _ => anchor.AddMonths(direction)
            };
            unsnapped = false;
            calendar?.Changed();
        }

        // Moves by days whatever the span, starting the view at the anchor.
        public void StepDays(int days)
        {
            anchorDay = anchor.AddDays(days);
            unsnapped = true;
            calendar?.Changed();
        }

        public void GoToday()
        {
            anchorDay = null;
            unsnapped = false;
            calendar?.Changed();
        }

        #region ---- editing ----
        public void Select(PlannerTicket? ticket)
        {
            if (ReferenceEquals(selected, ticket)) return;
            selected = ticket;
            chart?.InvalidateLayout();
        }

        // Shows a ticket's description in every view; null, or a ticket without one, folds it away.
        public void Expand(PlannerTicket? ticket)
        {
            if (ticket != null && ticket.description.Length == 0) ticket = null;
            if (ReferenceEquals(expanded, ticket)) return;
            expanded = ticket;
            chart?.RowsChanged();
            board?.RebuildSoon();
            calendar?.Changed();
        }

        // Opens the ticket popup for a new ticket under the toolbar button.
        public void AddTicket() =>
            PlannerTicketPopup.Open(this, addButton, new Vector2(addButton.arrangedRect.x, addButton.arrangedRect.Bottom), null);

        // Opens the ticket popup on the selected ticket, under its bar or block.
        public void EditSelected()
        {
            if (selected == null) return;
            if (chart != null)
            {
                LayoutRect bar = chart.BarRect(selected);
                PlannerTicketPopup.Open(this, chart, new Vector2(bar.x, bar.Bottom), selected);
            }
            else if (calendar?.BlockRect(selected) is LayoutRect block)
                PlannerTicketPopup.Open(this, calendar, new Vector2(block.x, block.Bottom), selected);
            else if (board?.CardOf(selected) is PlannerBoardControl.TicketCard card)
                PlannerTicketPopup.Open(this, card, new Vector2(card.arrangedRect.x, card.arrangedRect.Bottom), selected);
        }

        // Opens the ticket popup for a new ticket over a time.
        public void NewAt(TimeRange time, Control from, Vector2 point) =>
            PlannerTicketPopup.Open(this, from, point, null, time);

        // Files the selected ticket last under another category.
        public void MoveSelected(PlannerCategory category)
        {
            if (selected == null || ReferenceEquals(document.CategoryOf(selected), category)) return;
            PlannerTicket draft = selected.Clone();
            draft.categoryId = category.id;
            draft.order = document.NextOrder(category);
            Record("Move to category", new PlannerTicketEdit(document, selected, selected.Clone(), draft));
        }

        // Files a ticket under a category at a place in its column; the column is renumbered in one step.
        public void MoveTicket(PlannerTicket ticket, PlannerCategory category, int index)
        {
            List<PlannerTicket> column = document.TicketsIn(category).Where(t => !ReferenceEquals(t, ticket)).ToList();
            column.Insert(Math.Clamp(index, 0, column.Count), ticket);

            List<(PlannerTicket, PlannerTicket, PlannerTicket)> changed = new List<(PlannerTicket, PlannerTicket, PlannerTicket)>();
            for (int i = 0; i < column.Count; i++)
            {
                PlannerTicket filed = column[i];
                if (filed.order == i && filed.categoryId == category.id) continue;
                PlannerTicket after = filed.Clone();
                after.order = i;
                after.categoryId = category.id;
                changed.Add((filed, filed.Clone(), after));
            }
            if (changed.Count > 0) Record("Move ticket", new PlannerTicketEdit(document, changed));
        }

        public void DeleteSelected()
        {
            if (selected == null) return;
            Record("Delete ticket", new PlannerTicketAddEdit(document, selected, document.tickets.IndexOf(selected), false));
        }

        // A popup's draft: written over its ticket, or added when the ticket is new.
        public void Apply(PlannerTicket? ticket, PlannerTicket draft)
        {
            if (ticket == null)
            {
                Record("Add ticket", new PlannerTicketAddEdit(document, draft, document.tickets.Count, true));
                Select(draft);
                return;
            }
            Record("Edit ticket", new PlannerTicketEdit(document, WithFollowers(ticket, ticket.Clone(), draft)));
        }

        // A drag that already moved the ticket, recorded as one step.
        internal void CommitTime(PlannerTicket ticket, PlannerTicket before, string label)
        {
            if (ticket.time == before.time) return;
            Record(label, new PlannerTicketEdit(document, WithFollowers(ticket, before, ticket.Clone())));
        }

        // The ticket's edit, and every ticket downstream of it shifted by how far its end moved.
        private List<(PlannerTicket, PlannerTicket, PlannerTicket)> WithFollowers(PlannerTicket ticket, PlannerTicket before, PlannerTicket after)
        {
            List<(PlannerTicket, PlannerTicket, PlannerTicket)> edits = new List<(PlannerTicket, PlannerTicket, PlannerTicket)> { (ticket, before, after) };
            TimeSpan shift = after.time.end - before.time.end;
            if (shift == TimeSpan.Zero) return edits;

            HashSet<PlannerTicket> seen = new HashSet<PlannerTicket> { ticket };
            Queue<PlannerTicket> next = new Queue<PlannerTicket>(document.Followers(ticket));
            while (next.TryDequeue(out PlannerTicket? follower))
            {
                if (!seen.Add(follower)) continue;
                PlannerTicket moved = follower.Clone();
                moved.time = follower.time with { start = follower.time.start + shift, end = follower.time.end + shift };
                edits.Add((follower, follower.Clone(), moved));
                foreach (PlannerTicket further in document.Followers(follower))
                    next.Enqueue(further);
            }
            return edits;
        }

        public void PickCategory(PlannerCategory? category, Control from, Vector2 point)
        {
            pickedCategory = category;
            pickedFrom = from;
            pickedAt = point;
        }

        // Opens the category popup for a new category under the toolbar button.
        public void AddCategory() =>
            PlannerCategoryPopup.Open(this, addCategoryButton, new Vector2(addCategoryButton.arrangedRect.x, addCategoryButton.arrangedRect.Bottom), null);

        // Opens the category popup on the category last pressed on, where it was pressed.
        public void EditPickedCategory()
        {
            if (pickedCategory == null || pickedFrom == null || !document.categories.Contains(pickedCategory)) return;
            PlannerCategoryPopup.Open(this, pickedFrom, pickedAt, pickedCategory);
        }

        // Opens the ticket popup for a new ticket in the category last pressed on, where it was pressed.
        public void AddTicketToPickedCategory()
        {
            if (pickedCategory == null || pickedFrom == null || !document.categories.Contains(pickedCategory)) return;
            PlannerTicketPopup.Open(this, pickedFrom, pickedAt, null, category: pickedCategory);
        }

        // A popup's name and colour: written over its category, or a new category when it is null.
        public void ApplyCategory(PlannerCategory? category, string name, string colorHex)
        {
            if (category == null)
            {
                Record("Add category", new PlannerCategoryAddEdit(document, new PlannerCategory { name = name, colorHex = colorHex }, document.categories.Count, true));
                return;
            }
            if (category.name == name && category.colorHex == colorHex) return;
            Record("Edit category", new PlannerCategoryEdit(document, category, (category.name, category.colorHex), (name, colorHex)));
        }

        // Removes a category, never the last one.
        public void DeleteCategory(PlannerCategory category)
        {
            if (document.categories.Count <= 1 || !document.categories.Contains(category)) return;
            Record("Delete category", new PlannerCategoryAddEdit(document, category, document.categories.IndexOf(category), false));
        }

        // Removes a category and every ticket filed under it as one step, never the last category.
        public void DeleteCategoryWithTickets(PlannerCategory category)
        {
            if (document.categories.Count <= 1 || !document.categories.Contains(category)) return;
            using (undo.Begin("Delete category"))
            {
                foreach (PlannerTicket ticket in document.tickets.Where(t => ReferenceEquals(document.CategoryOf(t), category)).ToList())
                {
                    IEditRecord edit = new PlannerTicketAddEdit(document, ticket, document.tickets.IndexOf(ticket), false);
                    edit.Redo();
                    undo.Push(edit);
                }
                IEditRecord removal = new PlannerCategoryAddEdit(document, category, document.categories.IndexOf(category), false);
                removal.Redo();
                undo.Push(removal);
            }
        }

        // Asks before deleting the category last pressed on and its tickets.
        public void ConfirmDeletePickedCategory()
        {
            if (pickedCategory is not PlannerCategory category || document.categories.Count <= 1 || !document.categories.Contains(category)) return;
            int count = document.TicketsIn(category).Count();
            ConfirmWindow.Ask(UIEngine.WindowOf(this), $"Delete \"{category.name}\" and its {count} ticket{(count == 1 ? "" : "s")}?",
                () => DeleteCategoryWithTickets(category), null);
        }

        public void Undo() => undo.Undo();

        public void Redo() => undo.Redo();

        private void Record(string label, IEditRecord edit)
        {
            using (undo.Begin(label))
            {
                edit.Redo();
                undo.Push(edit);
            }
        }
        #endregion

        #region ---- file ----
        public void Save()
        {
            if (path == null) return;
            document.Save(path);
            document.unsaved = false;
        }

        public void Repath(string newPath, string name)
        {
            path = newPath;
            document.name = name;
        }
        #endregion

        #region ---- toolbar ----
        private ButtonControl Button(string text, Action press)
        {
            ButtonControl button = Pressable(press);
            button.padding = new Thickness(buttonInset, 0f);
            button.AddChild(new LabelControl { text = text, fontSize = fontSize, role = PaletteRole.MutedInk, hitTestable = false, verticalPosition = 0.5f });
            return button;
        }

        private static ButtonControl Chevron(string icon, Action press)
        {
            ButtonControl button = Pressable(press);
            button.preferredWidth = buttonHeight;
            IconControl ink = new IconControl
            {
                setName = "default",
                iconName = icon,
                preferredWidth = chevronSize,
                preferredHeight = chevronSize,
                hitTestable = false,
                horizontalPosition = 0.5f,
                verticalPosition = 0.5f
            };
            ink.PaintOr(null, PaletteRole.MutedInk);
            button.AddChild(ink);
            return button;
        }

        private static ButtonControl Pressable(Action press)
        {
            ButtonControl button = new ButtonControl { preferredHeight = buttonHeight, verticalAlignment = VerticalAlignment.Center, cornerRole = CornerRole.Tab };
            button.PaintOr(null, PaletteRole.Clear);
            button.RegisterOnPress(e =>
            {
                if (e.button != PointerEvent.leftButton) return false;
                press();
                return true;
            });
            return button;
        }

        // Recessed track holding one pick-one button set.
        private static StackPanelControl Segment(ButtonControl[] buttons)
        {
            StackPanelControl track = new StackPanelControl
            {
                orientation = Orientation.Horizontal,
                Spacing = trackInset,
                padding = new Thickness(trackInset),
                verticalAlignment = VerticalAlignment.Center,
                cornerRole = CornerRole.Control
            };
            track.PaintOr(null, PaletteRole.Field);
            foreach (ButtonControl button in buttons)
            {
                button.cornerRole = CornerRole.Control;
                track.AddChild(button);
            }
            return track;
        }

        private void Light()
        {
            for (int i = 0; i < viewButtons.Length; i++)
                Light(viewButtons[i], i == (int)view);
            for (int i = 0; i < zoomButtons.Length; i++)
                Light(zoomButtons[i], i == (int)zoom);
            ShowIf(zoomTrack, view == PlannerView.Gantt);
            for (int i = 0; i < spanButtons.Length; i++)
                Light(spanButtons[i], i == (int)span);
            ShowIf(spanTrack, view == PlannerView.Calendar);
            foreach (ButtonControl button in stepButtons)
                ShowIf(button, view == PlannerView.Calendar);
        }

        private static void ShowIf(Control button, bool shown)
        {
            if (shown) button.Show();
            else button.Hide();
        }

        private static void Light(ButtonControl button, bool on)
        {
            button.PaintOr(null, on ? PaletteRole.Surface : PaletteRole.Clear);
            foreach (LabelControl caption in button.children.OfType<LabelControl>())
                caption.PaintOr(null, on ? PaletteRole.Ink : PaletteRole.MutedInk);
        }
        #endregion

        #region ---- view ----
        // View, zoom, span, day, scroll: TopBlock, CaretBlock, AnchorBlock, TopOffset, ScrollX/TopDelta.
        public SessionTab ViewState()
        {
            if (pendingView != null) return pendingView;

            Vector2 scroll = scroller.GetScrollOffset();
            return new SessionTab
            {
                topBlock = (int)view,
                caretBlock = (int)zoom,
                anchorBlock = (int)span,
                topOffset = anchorDay?.DayNumber ?? 0,
                scrollX = scroll.X,
                topDelta = scroll.Y
            };
        }

        public void RestoreView(SessionTab restored)
        {
            if (restored.caretBlock >= 0 && restored.caretBlock < zoomButtons.Length) zoom = (PlannerZoom)restored.caretBlock;
            if (restored.anchorBlock >= 0 && restored.anchorBlock < spanButtons.Length) span = (CalendarSpan)restored.anchorBlock;
            anchorDay = restored.topOffset > 0 ? DateOnly.FromDayNumber(restored.topOffset) : null;
            if (restored.topBlock >= 0 && restored.topBlock < viewButtons.Length) view = (PlannerView)restored.topBlock;
            if (document != null) Build();

            pendingView = restored;
            scroller.InvalidateArrange();
        }

        // The view's viewport; applies a restored scroll or keeps the left edge's time across a zoom.
        private sealed class PlannerScroller : ScrollableControl
        {
            private readonly PlannerEditorControl editor;

            public PlannerScroller(PlannerEditorControl editor)
            {
                this.editor = editor;
                scrollDirection = ScrollDirection.Both;
                horizontalAlignment = HorizontalAlignment.Stretch;
                heightStar = 1f;
                PaintOr(null, PaletteRole.Surface);
            }

            protected override void ArrangeCore(LayoutRect finalRect)
            {
                base.ArrangeCore(finalRect);

                Vector2 before = GetScrollOffset();
                if (editor.pendingView != null)
                {
                    SessionTab view = editor.pendingView;
                    editor.pendingView = null;
                    editor.pendingLeft = null;
                    editor.pendingTop = null;
                    SetScrollOffset(new Vector2(view.scrollX, view.topDelta));
                }
                else if (editor.pendingLeft is DateTime left && editor.chart != null)
                {
                    editor.pendingLeft = null;
                    SetScrollOffset(new Vector2(editor.chart.Offset(left), before.Y));
                }
                else if (editor.pendingTop is float top)
                {
                    editor.pendingTop = null;
                    SetScrollOffset(new Vector2(0f, top));
                }

                if (GetScrollOffset() != before) base.ArrangeCore(finalRect);
                else SetFlag(ArrangeFlags.ArrangeDirty, false);
            }
        }
        #endregion
    }
}
