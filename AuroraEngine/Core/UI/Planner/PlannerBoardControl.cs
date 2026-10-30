using ArctisAurora.Core.Filing;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.Core.Users;
using ArctisAurora.EngineWork;
using CursorShape = Silk.NET.GLFW.CursorShape;
using System.Globalization;
using System.Numerics;

namespace ArctisAurora.Core.UI.Planner
{
    // A planner's tickets as cards in a column per category.
    public class PlannerBoardControl : StackPanelControl
    {
        public const float minColumnWidth = 160f;
        public const float maxColumnWidth = 600f;
        private const float edgeGrab = 3f;
        private const float gap = 12f;
        private const float inset = 8f;
        private const float stripWidth = 4f;
        private const int titleSize = 14;
        private const int detailSize = 12;

        private const float markWidth = 3f;
        private const float dropGap = 28f;
        private const float chipHeight = 18f;
        private const float dragThreshold = 4f;

        private readonly PlannerEditorControl editor;

        // the built columns, left to right, and their cards top to bottom
        private readonly List<(PlannerCategory category, StackPanelControl column, List<TicketCard> cards)> columns =
            new List<(PlannerCategory, StackPanelControl, List<TicketCard>)>();

        // the drop marker: the column outlined and a gap opened before the card a drop lands before, or after the last
        private StackPanelControl? markedColumn;
        private TicketCard? markedCard;
        private int markedIndex = -1;
        private bool rebuildPending;

        // the column whose right edge is being dragged
        private int resizing = -1;
        private float pressWidth;
        private float pressX;
        private CursorShape shownCursor = CursorShape.Arrow;

        public PlannerBoardControl(PlannerEditorControl editor)
        {
            this.editor = editor;
            orientation = Orientation.Horizontal;
            alpha = 0f;
            Rebuild();
        }

        // sizes at the content zoom
        private float Zoom => editor.contentZoom;
        private int Font(int size) => Math.Max(1, (int)MathF.Round(size * Zoom));

        // Re-lays the board at the editor's content zoom.
        internal void Rezoom() => Rebuild();

        // Tickets or categories changed; the columns are built again next tick, once however many edits land.
        internal void RebuildSoon()
        {
            if (rebuildPending) return;
            rebuildPending = true;
            Engine.Post(() =>
            {
                rebuildPending = false;
                if (!destroyed) Rebuild();
            });
        }

        // Every column built again.
        private void Rebuild()
        {
            for (Control? control = UIEngine.activeControl?.parent as Control; control != null; control = control.parent as Control)
                if (ReferenceEquals(control, this))
                {
                    UIEngine.SetActiveControl(this);
                    break;
                }

            foreach (Control column in children.OfType<Control>().ToList())
                column.Destroy();
            columns.Clear();
            markedColumn = null;
            markedCard = null;
            markedIndex = -1;
            Spacing = gap * Zoom;
            padding = new Thickness(gap * Zoom);

            PlannerDocument document = editor.document;
            foreach (PlannerCategory category in document.categories)
            {
                List<PlannerTicket> tickets = document.TicketsIn(category).ToList();
                StackPanelControl column = new StackPanelControl
                {
                    preferredWidth = category.width * Zoom,
                    verticalAlignment = VerticalAlignment.Top,
                    Spacing = inset * Zoom,
                    padding = new Thickness(inset * Zoom),
                    cornerRole = CornerRole.Control
                };
                column.PaintOr(null, PaletteRole.Chrome);
                column.contextMenu = "planner-category";
                column.stopsContextMenu = true;
                column.RegisterOnPress(e =>
                {
                    editor.PickCategory(category, column, e.point);
                    return false;
                });
                column.RegisterOnTap(e =>
                {
                    if (e.tapCount != 2) return false;
                    editor.EditPickedCategory();
                    return true;
                });

                StackPanelControl heading = new StackPanelControl { orientation = Orientation.Horizontal, Spacing = inset * Zoom, alpha = 0f, hitTestable = false };
                PanelControl swatch = new PanelControl { preferredWidth = 10f * Zoom, preferredHeight = 10f * Zoom, verticalAlignment = VerticalAlignment.Center, cornerRole = CornerRole.Control, hitTestable = false };
                swatch.PaintOr(category.colorHex, PaletteRole.Accent);
                heading.AddChild(swatch);
                LabelControl title = Text($"{category.name}  {tickets.Count}", Font(titleSize), PaletteRole.Ink, FontStyle.Bold, true);
                title.widthStar = 1f;
                heading.AddChild(title);
                column.AddChild(heading);

                List<TicketCard> cards = new List<TicketCard>();
                foreach (PlannerTicket ticket in tickets)
                {
                    TicketCard card = new TicketCard(this, ticket);
                    Fill(card, document, ticket, ReferenceEquals(ticket, editor.expanded));
                    cards.Add(card);
                    column.AddChild(card);
                }
                columns.Add((category, column, cards));
                AddChild(column);
            }
        }

        public TicketCard? CardOf(PlannerTicket ticket) =>
            columns.SelectMany(c => c.cards).FirstOrDefault(card => ReferenceEquals(card.ticket, ticket));

        public IReadOnlyList<TicketCard> CardsIn(PlannerCategory category) =>
            columns.Find(c => ReferenceEquals(c.category, category)).cards ?? new List<TicketCard>();

        // A colour strip over the name, the dates and the creator.
        private void Fill(StackPanelControl card, PlannerDocument document, PlannerTicket ticket, bool expanded)
        {
            PanelControl strip = new PanelControl { preferredHeight = stripWidth * Zoom, horizontalAlignment = HorizontalAlignment.Stretch, cornerRole = CornerRole.Control, hitTestable = false };
            strip.PaintOr(document.ColorOf(ticket), PaletteRole.Accent);
            card.AddChild(strip);

            StackPanelControl body = new StackPanelControl { Spacing = 2f * Zoom, padding = new Thickness(inset * Zoom), alpha = 0f, hitTestable = false };
            body.AddChild(Text(ticket.name, Font(titleSize), PaletteRole.Ink, FontStyle.Regular, true));
            if (expanded)
                foreach (string line in ticket.description.Split('\n'))
                    body.AddChild(Text(line, Font(detailSize), PaletteRole.Ink, FontStyle.Regular, true));
            body.AddChild(Text(Dates(ticket.time), Font(detailSize), PaletteRole.MutedInk, FontStyle.Regular, true));
            if (ticket.creator is User creator)
                body.AddChild(Text($"by {creator.name}", Font(detailSize), PaletteRole.MutedInk, FontStyle.Regular, true));
            if (ticket.assignees.Count > 0)
            {
                WrapPanelControl chips = new WrapPanelControl { Spacing = 4f * Zoom, alpha = 0f, hitTestable = false };
                foreach (User assignee in ticket.assignees)
                {
                    StackPanelControl chip = new StackPanelControl { preferredHeight = chipHeight * Zoom, padding = new Thickness(6f * Zoom, 0f), cornerRole = CornerRole.Control, hitTestable = false };
                    chip.PaintOr(null, PaletteRole.SubField);
                    LabelControl initials = Text(assignee.initials, Font(detailSize), PaletteRole.Ink, FontStyle.Bold);
                    initials.verticalPosition = 0.5f;
                    chip.AddChild(initials);
                    chips.AddChild(chip);
                }
                body.AddChild(chips);
            }
            card.AddChild(body);
        }

        #region ---- drop ----
        // The column under a point, the nearest one beside it otherwise, and how many of its other cards sit above the point.
        private bool DropAt(Control dragged, Vector2 point, out int column, out int index)
        {
            column = -1;
            index = 0;
            if (dragged is not TicketCard card || !ReferenceEquals(card.board, this) || columns.Count == 0) return false;

            float nearest = float.MaxValue;
            for (int i = 0; i < columns.Count; i++)
            {
                LayoutRect rect = columns[i].column.arrangedRect;
                float distance = point.X < rect.x ? rect.x - point.X : point.X > rect.Right ? point.X - rect.Right : 0f;
                if (distance < nearest)
                {
                    nearest = distance;
                    column = i;
                }
            }

            foreach (TicketCard other in columns[column].cards)
                if (!ReferenceEquals(other, card) && other.arrangedRect.y + other.arrangedRect.height * 0.5f < point.Y) index++;
            return true;
        }

        public override bool DraggingOverStart(Control dragged, Vector2 point) => Mark(dragged, point);

        public override bool DraggingOver(Control dragged, Vector2 point) => Mark(dragged, point);

        public override bool DraggingOverEnd(Control dragged)
        {
            if (dragged is not TicketCard) return false;
            Unmark();
            return true;
        }

        public override bool FinishDrag(Control dragged, Vector2 point)
        {
            if (!DropAt(dragged, point, out int column, out int index)) return false;
            editor.MoveTicket(((TicketCard)dragged).ticket, columns[column].category, index);
            return true;
        }

        private bool Mark(Control dragged, Vector2 point)
        {
            if (!DropAt(dragged, point, out int column, out int index)) return false;

            StackPanelControl outlined = columns[column].column;
            if (ReferenceEquals(outlined, markedColumn) && index == markedIndex) return true;

            Unmark();
            markedColumn = outlined;
            markedIndex = index;
            outlined.edgeRole = PaletteRole.Accent;
            outlined.edgeThickness = new Thickness(markWidth * 0.5f);

            List<TicketCard> others = columns[column].cards.Where(c => !ReferenceEquals(c, dragged)).ToList();
            if (others.Count == 0) return true;
            markedCard = others[Math.Min(index, others.Count - 1)];
            markedCard.margin = index < others.Count ? new Thickness(dropGap * Zoom, 0f, 0f, 0f) : new Thickness(0f, 0f, dropGap * Zoom, 0f);
            return true;
        }

        private void Unmark()
        {
            if (markedColumn != null) markedColumn.edgeThickness = new Thickness(0f);
            if (markedCard != null) markedCard.margin = new Thickness(0f);
            markedColumn = null;
            markedCard = null;
            markedIndex = -1;
        }
        #endregion

        #region ---- column resize ----
        private int EdgeAt(Vector2 point)
        {
            for (int i = 0; i < columns.Count; i++)
                if (MathF.Abs(point.X - columns[i].column.arrangedRect.Right) <= edgeGrab) return i;
            return -1;
        }

        public override bool OnPointerPress(PointerEvent e)
        {
            if (e.button == PointerEvent.leftButton && EdgeAt(e.point) is int edge && edge >= 0)
            {
                resizing = edge;
                pressWidth = columns[edge].category.width;
                pressX = e.point.X;
                StartDrag();
                return true;
            }
            return base.OnPointerPress(e);
        }

        // A click off every card folds the expanded one away.
        public override bool OnPointerRelease(PointerEvent e)
        {
            if (e.button == PointerEvent.leftButton && e.tapCount == 1) editor.Expand(null);
            return base.OnPointerRelease(e);
        }

        public override void OnDrag(PointerEvent e)
        {
            base.OnDrag(e);
            if (resizing < 0) return;
            float width = Math.Clamp(pressWidth + (e.point.X - pressX) / Zoom, minColumnWidth, maxColumnWidth);
            columns[resizing].category.width = width;
            columns[resizing].column.preferredWidth = width * Zoom;
        }

        public override void OnDragStop(bool accepted)
        {
            base.OnDragStop(accepted);
            if (resizing < 0) return;
            if (columns[resizing].category.width != pressWidth) editor.document.unsaved = true;
            resizing = -1;
            ShowCursor(CursorShape.Arrow);
        }

        public override bool OnPointerMove(PointerEvent e)
        {
            if (resizing < 0) ShowCursor(EdgeAt(e.point) >= 0 ? CursorShape.HResize : CursorShape.Arrow);
            return base.OnPointerMove(e);
        }

        public override bool OnPointerExit(PointerEvent e)
        {
            if (resizing < 0) ShowCursor(CursorShape.Arrow);
            return base.OnPointerExit(e);
        }

        private void ShowCursor(CursorShape shape)
        {
            if (shownCursor == shape) return;
            shownCursor = shape;
            UIEngine.WindowOf(this)?.os.ChangeCursor(shape);
        }
        #endregion

        // One ticket's card; a press dragged past a few pixels carries it to another place on the board.
        public sealed class TicketCard : StackPanelControl
        {
            public readonly PlannerBoardControl board;
            public readonly PlannerTicket ticket;
            private Vector2 grab;
            private bool armed;

            public TicketCard(PlannerBoardControl board, PlannerTicket ticket)
            {
                this.board = board;
                this.ticket = ticket;
                horizontalAlignment = HorizontalAlignment.Stretch;
                cornerRole = CornerRole.Control;
                PaintOr(null, PaletteRole.Surface);
            }

            public override bool OnPointerPress(PointerEvent e)
            {
                base.OnPointerPress(e);
                board.editor.PickCategory(board.editor.document.CategoryOf(ticket), this, e.point);
                armed = e.button == PointerEvent.leftButton;
                grab = e.point;
                return armed;
            }

            // Keeps taps from reaching the column.
            public override bool OnPointerTap(PointerEvent e)
            {
                base.OnPointerTap(e);
                return true;
            }

            public override bool OnPointerMove(PointerEvent e)
            {
                if (armed)
                {
                    if (!InputHandler.instance.IsKeyDown(Keys.MouseLeft)) armed = false;
                    else if ((e.point - grab).Length() >= dragThreshold)
                    {
                        armed = false;
                        StartDrag();
                        DragGhost.Show(this);
                    }
                }
                return base.OnPointerMove(e);
            }

            // A click selects the card and opens its description.
            public override bool OnPointerRelease(PointerEvent e)
            {
                armed = false;
                if (e.button != PointerEvent.leftButton || e.tapCount != 1) return base.OnPointerRelease(e);
                base.OnPointerRelease(e);
                board.editor.Select(ticket);
                board.editor.Expand(ticket);
                return true;
            }

            public override void OnDragStop(bool accepted)
            {
                DragGhost.Hide();
                base.OnDragStop(accepted);
            }
        }

        private static LabelControl Text(string text, int size, PaletteRole role, FontStyle style, bool wraps = false)
        {
            LabelControl label = wraps ? new WrappingLabel() : new LabelControl();
            label.text = text;
            label.fontSize = size;
            label.style = style;
            label.hitTestable = false;
            label.PaintOr(null, role);
            return label;
        }

        // "7 Oct - 9 Oct" for whole days, with times otherwise.
        public static string Dates(TimeRange time)
        {
            if (time.allDay)
            {
                DateTime last = time.end.AddDays(-1);
                return last <= time.start
                    ? DateFormats.DayMonth(time.start, true)
                    : $"{DateFormats.DayMonth(time.start, false)} - {DateFormats.DayMonth(last, true)}";
            }
            return $"{DateFormats.DayMonth(time.start, false)} {DateFormats.Time(time.start)} - {DateFormats.DayMonth(time.end, false)} {DateFormats.Time(time.end)}";
        }
    }

    // A label that wraps at the width it is measured to.
    internal sealed class WrappingLabel : LabelControl
    {
        protected override bool Wraps => true;
    }
}
