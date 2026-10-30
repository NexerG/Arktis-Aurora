using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Threading;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using Silk.NET.GLFW;
using System.Globalization;
using System.Numerics;

namespace ArctisAurora.Core.UI.Planner
{
    public enum CalendarSpan { Day, Week, Month }

    // A planner's tickets on a Day/Week time grid or a Month grid; only what is in the viewport has controls.
    public class CalendarControl : ContainerControl
    {
        // Parts its owner lays out one by one.
        private sealed class CalendarParts : ContainerControl
        {
            public CalendarParts() => hitTestable = false;

            protected override Vector2 MeasureCore(Vector2 availableSize)
            {
                foreach (Entity child in children)
                    if (child is Control control) control.Measure(unbounded);
                arrange.desired = availableSize;
                return availableSize;
            }

            protected override void ArrangeCore(LayoutRect finalRect) => WriteArranged(finalRect);

            internal void Settle(LayoutRect rect)
            {
                SetFlag(ArrangeFlags.ArrangeDirty, true);
                Arrange(rect);
            }
        }

        // Parts of one kind in one layer; what an arrange does not take is hidden.
        private sealed class CalendarPool<T> where T : Control, new()
        {
            private readonly CalendarControl owner;
            private readonly CalendarParts layer;
            private readonly Action<T> setup;
            private readonly List<T> parts = new List<T>();
            private int used;

            public CalendarPool(CalendarControl owner, CalendarParts layer, Action<T> setup)
            {
                this.owner = owner;
                this.layer = layer;
                this.setup = setup;
                owner.hides.Add(HideRest);
            }

            public T Next()
            {
                if (used == parts.Count)
                {
                    T part = new T { hitTestable = false };
                    setup(part);
                    owner.Adopt(layer, part);
                    parts.Add(part);
                }
                return parts[used++];
            }

            public IReadOnlyList<T> All => parts;

            private void HideRest(LayoutRect hidden)
            {
                for (int i = used; i < parts.Count; i++)
                    parts[i].Arrange(hidden);
                used = 0;
            }
        }

        private enum PieceKind { Timed, AllDay, Chip }

        private enum Grip { Move, Start, End }

        // one ticket's block in a day column, an all-day lane or a month cell
        private struct Piece
        {
            public PlannerTicket ticket;
            public PieceKind kind;
            public int day;
            public int lastDay;
            public int column;
            public int columns;
            public LayoutRect rect;
        }

        // geometry, design pixels
        public const float gutterWidth = 56f;
        public const float headerHeight = 24f;
        public const float laneHeight = 22f;
        public const float hourHeight = 48f;
        private const float blockInset = 2f;
        private const float minBlockHeight = 4f;
        private const float labelHeight = 18f;
        private const float cellHeader = 20f;
        private const float chipHeight = 18f;
        private const float lineWidth = 1f;
        private const float nowWidth = 2f;
        private const float dotSize = 8f;
        private const float outlineWidth = 2f;
        private const float grabHeight = 6f;
        private const float textInset = 4f;
        private const float attachmentAlpha = 0.4f;
        private const float todayAlpha = 0.12f;
        private const int fontSize = 12;
        private const float slideRate = 0.25f;
        private const float laneSeconds = 0.08f;
        private const double snapDuration = 0.15;
        private const float weekStartAlpha = 0.35f;

        public static readonly TimeSpan snap = TimeSpan.FromMinutes(15);
        private static readonly Vector2 unbounded = new Vector2(float.MaxValue, float.MaxValue);

        private readonly PlannerEditorControl editor;
        private readonly List<Piece> pieces = new List<Piece>();
        private int lanes;

        // layers, in draw order
        private readonly CalendarParts grid = new CalendarParts();
        private readonly CalendarParts blocks = new CalendarParts();
        private readonly CalendarParts texts = new CalendarParts();
        private readonly CalendarParts marks = new CalendarParts();
        private readonly CalendarParts gutter = new CalendarParts();
        private readonly CalendarParts headerBack = new CalendarParts();
        private readonly CalendarParts header = new CalendarParts();
        private readonly CalendarParts strip = new CalendarParts();
        private readonly CalendarParts cards = new CalendarParts();
        private readonly PanelControl[] outline = new PanelControl[4];

        // pooled parts
        private readonly List<Action<LayoutRect>> hides = new List<Action<LayoutRect>>();
        private readonly CalendarPool<PanelControl> lines;
        private readonly CalendarPool<PanelControl> weekStartParts;
        private readonly CalendarPool<PanelControl> todayParts;
        private readonly CalendarPool<PanelControl> todayDots;
        private readonly CalendarPool<PanelControl> blockParts;
        private readonly CalendarPool<PanelControl> attachmentParts;
        private readonly CalendarPool<LabelControl> blockNames;
        private readonly CalendarPool<LabelControl> numbers;
        private readonly CalendarPool<PanelControl> nowParts;
        private readonly CalendarPool<LabelControl> hours;
        private readonly CalendarPool<PanelControl> headerParts;
        private readonly CalendarPool<PanelControl> todayPills;
        private readonly CalendarPool<LabelControl> dayNames;
        private readonly CalendarPool<PanelControl> stripParts;
        private readonly CalendarPool<LabelControl> stripNames;
        private readonly CalendarPool<PanelControl> cardParts;
        private readonly CalendarPool<LabelControl> cardNames;
        private readonly CalendarPool<WrappingLabel> cardTexts;

        // the expanded ticket's card this arrange, empty when none is shown
        private LayoutRect expandedRect;

        // the block being dragged, as it was at the press
        private PlannerTicket? dragged;
        private PlannerTicket? draggedBefore;
        private PieceKind draggedKind;
        private Grip grip;
        private Vector2 pressPoint;
        private CursorShape shownCursor = CursorShape.Arrow;

        // the minute the now line was drawn at
        private DateTime shownMinute;
        private float slide;
        private float shownLanes = -1f;

        // the slide easing onto a whole day or week once the scroll gesture ends
        private bool forward;
        private bool settling;
        private float settleFrom;
        private float settleTo;
        private double settleStart;
        private CalendarSpan laidSpan;

        public CalendarControl(PlannerEditorControl editor)
        {
            this.editor = editor;
            contextMenu = "planner";
            stopsContextMenu = true;

            AddChild(grid);
            AddChild(blocks);
            AddChild(texts);
            AddChild(marks);
            AddChild(gutter);
            AddChild(headerBack);
            AddChild(header);
            AddChild(strip);
            AddChild(cards);
            for (int i = 0; i < outline.Length; i++)
            {
                outline[i] = new PanelControl { hitTestable = false };
                outline[i].PaintOr(null, PaletteRole.Ink);
                AddChild(outline[i]);
            }

            lines = new CalendarPool<PanelControl>(this, grid, part => part.PaintOr(null, PaletteRole.Line));
            weekStartParts = new CalendarPool<PanelControl>(this, grid, part => { part.PaintOr(null, PaletteRole.Line); part.alpha = weekStartAlpha; });
            todayParts = new CalendarPool<PanelControl>(this, grid, part => { part.PaintOr(null, PaletteRole.Accent); part.alpha = todayAlpha; });
            todayDots = new CalendarPool<PanelControl>(this, grid, part => { part.PaintOr(null, PaletteRole.Accent); part.cornerRole = CornerRole.Control; });
            blockParts = new CalendarPool<PanelControl>(this, blocks, part => { part.PaintOr(null, PaletteRole.Accent); part.cornerRole = CornerRole.Control; });
            attachmentParts = new CalendarPool<PanelControl>(this, blocks, part => { part.PaintOr(null, PaletteRole.Accent); part.alpha = attachmentAlpha; });
            blockNames = new CalendarPool<LabelControl>(this, texts, label => Style(label, PaletteRole.Ink, FontStyle.Regular));
            numbers = new CalendarPool<LabelControl>(this, texts, label => Style(label, PaletteRole.Ink, FontStyle.Regular));
            nowParts = new CalendarPool<PanelControl>(this, marks, part => { part.PaintOr(null, PaletteRole.Danger); part.cornerRole = CornerRole.Control; });
            hours = new CalendarPool<LabelControl>(this, gutter, label => Style(label, PaletteRole.MutedInk, FontStyle.Regular));
            headerParts = new CalendarPool<PanelControl>(this, headerBack, part => part.PaintOr(null, PaletteRole.Chrome));
            todayPills = new CalendarPool<PanelControl>(this, headerBack, part => { part.PaintOr(null, PaletteRole.Accent); part.cornerRole = CornerRole.Control; });
            dayNames = new CalendarPool<LabelControl>(this, header, label => Style(label, PaletteRole.Ink, FontStyle.Regular));
            stripParts = new CalendarPool<PanelControl>(this, strip, part => { part.PaintOr(null, PaletteRole.Accent); part.cornerRole = CornerRole.Control; });
            stripNames = new CalendarPool<LabelControl>(this, strip, label => Style(label, PaletteRole.Ink, FontStyle.Regular));
            cardParts = new CalendarPool<PanelControl>(this, cards, part => { part.PaintOr(null, PaletteRole.Accent); part.cornerRole = CornerRole.Control; });
            cardNames = new CalendarPool<LabelControl>(this, cards, label => Style(label, PaletteRole.Ink, FontStyle.Regular));
            cardTexts = new CalendarPool<WrappingLabel>(this, cards, label => Style(label, PaletteRole.Ink, FontStyle.Regular));

            SetTicking(true);
        }

        // Tickets, span or anchor changed; the pieces are rebuilt.
        internal void Changed() => InvalidateLayout();

        // Re-lays the calendar at the editor's content zoom.
        internal void Rezoom()
        {
            int size = FontSize;
            foreach (LabelControl label in blockNames.All.Concat(numbers.All).Concat(hours.All).Concat(dayNames.All)
                         .Concat(stripNames.All).Concat(cardNames.All).Concat(cardTexts.All))
                label.fontSize = size;
            InvalidateLayout();
        }

        // Horizontal wheel steps through days, or weeks on the Month grid.
        public override bool OnPointerScroll(PointerEvent e)
        {
            if (e.delta.X == 0f) return base.OnPointerScroll(e);

            if (!editor.unsnapped)
            {
                slide = 0f;
                editor.StepDays(first.DayNumber - editor.anchor.DayNumber);
            }
            slide -= e.delta.X * slideRate;
            int steps = (int)MathF.Floor(slide);
            slide -= steps;
            if (steps != 0) editor.StepDays(IsMonth ? steps * 7 : steps);
            forward = e.delta.X < 0f;
            settling = false;
            Changed();
            return true;
        }

        // Settles the slide onto the next whole day or week in the direction it was moving.
        public override void OnPointerScrollEnd()
        {
            if (!editor.unsnapped || slide <= 0f) return;
            settleFrom = slide;
            settleTo = forward ? 1f : 0f;
            settleStart = Engine.totalTime;
            settling = true;
            Changed();
        }

        // Redraws when the minute turns, and asks for a frame then.
        public override void OnTick()
        {
            base.OnTick();
            if (settling && !editor.unsnapped) settling = false;
            if (settling)
            {
                float t = (float)Math.Min(1.0, (Engine.totalTime - settleStart) / snapDuration);
                float rest = 1f - t;
                slide = settleFrom + (settleTo - settleFrom) * (1f - rest * rest * rest);
                if (t >= 1f)
                {
                    settling = false;
                    slide = 0f;
                    if (settleTo > 0f) editor.StepDays(IsMonth ? 7 : 1);
                }
                else FrameScheduler.RequestFrameAt(Engine.totalTime + 0.001);
                Changed();
            }
            float target = Math.Max(1, lanes);
            if (shownLanes != target)
            {
                shownLanes += (target - shownLanes) * (1f - MathF.Exp(-(float)Engine.deltaTime.TotalSeconds / laneSeconds));
                if (MathF.Abs(target - shownLanes) < 0.01f) shownLanes = target;
                else FrameScheduler.RequestFrameAt(Engine.totalTime + 0.001);
                InvalidateLayout();
            }
            DateTime now = editor.Now;
            DateTime minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
            if (minute != shownMinute)
            {
                shownMinute = minute;
                InvalidateArrange();
            }
            FrameScheduler.RequestFrameAt(Engine.totalTime + (minute.AddMinutes(1) - now).TotalSeconds);
        }

        #region ---- geometry ----
        private CalendarSpan Span => editor.span;

        private bool IsMonth => editor.span == CalendarSpan.Month;

        // The first day shown: the anchor, its week's start, or the week start on or before its month's first.
        public DateOnly first
        {
            get
            {
                DateOnly anchor = editor.anchor;
                if (editor.unsnapped) return Span == CalendarSpan.Month ? DateFormats.WeekStart(anchor) : anchor;
                DateOnly from = Span == CalendarSpan.Month ? new DateOnly(anchor.Year, anchor.Month, 1) : anchor;
                return Span == CalendarSpan.Day ? from : DateFormats.WeekStart(from);
            }
        }

        private DateTime First => first.ToDateTime(TimeOnly.MinValue);

        private int Days => Span switch
        {
            CalendarSpan.Day => 1,
            CalendarSpan.Week => 7,
            _ => 42
        };

        // the part of a day (a week on the Month grid) the view has slid past first
        private float Slide => editor.unsnapped ? slide : 0f;

        private int Shown => Days + (Slide > 0f ? (IsMonth ? 7 : 1) : 0);

        // sizes at the content zoom
        private float Zoom => editor.contentZoom;
        private float Gutter => gutterWidth * Zoom;
        private float HeaderSize => headerHeight * Zoom;
        private float LaneSize => laneHeight * Zoom;
        private float HourSize => hourHeight * Zoom;
        private float BlockInset => blockInset * Zoom;
        private float MinBlock => minBlockHeight * Zoom;
        private float LabelSize => labelHeight * Zoom;
        private float CellHeader => cellHeader * Zoom;
        private float ChipSize => chipHeight * Zoom;
        private float DotSize => dotSize * Zoom;
        private float TextInset => textInset * Zoom;
        private int FontSize => Math.Max(1, (int)MathF.Round(fontSize * Zoom));

        // The pinned header and all-day strip.
        public float TopHeight => HeaderSize + shownLanes * LaneSize;

        public float BodyTop => arrangedRect.y + TopHeight;

        private float ColumnWidth => (arrangedRect.width - Gutter) / Days;

        private float ColumnX(int day) => arrangedRect.x + Gutter + (day - Slide) * ColumnWidth;

        private float Y(DateTime day, DateTime time) => BodyTop + (float)(time - day).TotalHours * HourSize;

        private float CellWidth => arrangedRect.width / 7f;

        private float CellHeight => (arrangedRect.height - HeaderSize) / 6f;

        private LayoutRect Cell(int index) =>
            new LayoutRect(arrangedRect.x + index % 7 * CellWidth, arrangedRect.y + HeaderSize + (index / 7 - Slide) * CellHeight, CellWidth, CellHeight);

        private LayoutRect Hidden => new LayoutRect(arrangedRect.x, arrangedRect.y, 0f, 0f);

        private LayoutRect Viewport() =>
            parent is ScrollableControl scroller ? scroller.arrangedRect.Shrink(scroller.padding) : arrangedRect;

        // A point on the Day/Week grid at a time, in the middle of its day's column.
        public Vector2 PointAt(DateTime time)
        {
            int day = (time.Date - First).Days;
            return new Vector2(ColumnX(day) + ColumnWidth * 0.5f, Y(time.Date, time));
        }

        // The time under a point on the Day/Week grid, unsnapped.
        public DateTime TimeAt(Vector2 point)
        {
            int day = Math.Clamp((int)MathF.Floor((point.X - ColumnX(0)) / ColumnWidth), 0, Shown - 1);
            float hoursIn = Math.Clamp((point.Y - BodyTop) / HourSize, 0f, 24f);
            return First.AddDays(day).AddHours(hoursIn);
        }

        // The day under a point: its column, or its month cell.
        public DateOnly DayAt(Vector2 point) => first.AddDays(DayIndex(point));

        private int DayIndex(Vector2 point)
        {
            if (!IsMonth) return Math.Clamp((int)MathF.Floor((point.X - ColumnX(0)) / ColumnWidth), 0, Shown - 1);
            int column = Math.Clamp((int)MathF.Floor((point.X - arrangedRect.x) / CellWidth), 0, 6);
            int row = Math.Clamp((int)MathF.Floor((point.Y - arrangedRect.y - HeaderSize) / CellHeight + Slide), 0, Shown / 7 - 1);
            return row * 7 + column;
        }

        // The first shown block of a ticket.
        public LayoutRect? BlockRect(PlannerTicket ticket)
        {
            foreach (Piece piece in pieces)
                if (ReferenceEquals(piece.ticket, ticket) && piece.rect.width > 0f) return piece.rect;
            return null;
        }

        public PlannerTicket? TicketAt(Vector2 point) => PieceAt(point) is int index ? pieces[index].ticket : null;

        // A ticket that runs all day or a day or more sits in the all-day strip.
        private static bool Long(PlannerTicket ticket) => ticket.time.allDay || ticket.time.end - ticket.time.start >= TimeSpan.FromDays(1);

        // Columns for overlapping spans: each takes the first free column of its cluster, sized by the cluster's column count.
        public static (int column, int columns)[] Pack(IReadOnlyList<(DateTime start, DateTime end)> spans)
        {
            (int column, int columns)[] packed = new (int, int)[spans.Count];
            List<int> order = Enumerable.Range(0, spans.Count).OrderBy(i => spans[i].start).ThenByDescending(i => spans[i].end).ToList();
            List<DateTime> columnEnds = new List<DateTime>();
            List<int> cluster = new List<int>();
            DateTime clusterEnd = DateTime.MinValue;

            foreach (int i in order)
            {
                (DateTime start, DateTime end) = spans[i];
                if (cluster.Count > 0 && start >= clusterEnd)
                {
                    foreach (int member in cluster)
                        packed[member].columns = columnEnds.Count;
                    cluster.Clear();
                    columnEnds.Clear();
                }

                int column = columnEnds.FindIndex(taken => taken <= start);
                if (column < 0)
                {
                    column = columnEnds.Count;
                    columnEnds.Add(end);
                }
                else columnEnds[column] = end;

                packed[i].column = column;
                cluster.Add(i);
                if (cluster.Count == 1 || end > clusterEnd) clusterEnd = end;
            }
            foreach (int member in cluster)
                packed[member].columns = columnEnds.Count;
            return packed;
        }
        #endregion

        #region ---- layout ----
        // Pieces from the tickets: all-day lanes and packed day columns, or month chips.
        private void BuildPieces()
        {
            pieces.Clear();
            lanes = 0;
            PlannerDocument document = editor.document;
            DateTime start = First;
            int days = Shown;

            if (IsMonth)
            {
                for (int d = 0; d < days; d++)
                {
                    DateTime dayStart = start.AddDays(d);
                    DateTime dayEnd = dayStart.AddDays(1);
                    List<PlannerTicket> on = document.tickets
                        .Where(ticket => ticket.time.start < dayEnd && (ticket.time.end > dayStart || ticket.time.start >= dayStart))
                        .OrderByDescending(Long).ThenBy(ticket => ticket.time.start).ToList();
                    for (int i = 0; i < on.Count; i++)
                        pieces.Add(new Piece { ticket = on[i], kind = PieceKind.Chip, day = d, column = i, columns = on.Count });
                }
                return;
            }

            DateTime last = start.AddDays(days);
            List<(DateTime, DateTime)> spans = new List<(DateTime, DateTime)>();
            foreach (PlannerTicket ticket in document.tickets)
            {
                if (!Long(ticket) || ticket.time.end <= start || ticket.time.start >= last) continue;
                int from = Math.Max(0, (ticket.time.start.Date - start).Days);
                int to = Math.Min(days - 1, (ticket.time.end.AddTicks(-1).Date - start).Days);
                pieces.Add(new Piece { ticket = ticket, kind = PieceKind.AllDay, day = from, lastDay = to });
                spans.Add((start.AddDays(from), start.AddDays(to + 1)));
            }
            (int column, int columns)[] packed = Pack(spans);
            for (int i = 0; i < packed.Length; i++)
            {
                Piece piece = pieces[i];
                piece.column = packed[i].column;
                pieces[i] = piece;
                lanes = Math.Max(lanes, piece.column + 1);
            }

            for (int d = 0; d < days; d++)
            {
                DateTime dayStart = start.AddDays(d);
                DateTime dayEnd = dayStart.AddDays(1);
                int firstPiece = pieces.Count;
                spans.Clear();
                foreach (PlannerTicket ticket in document.tickets)
                {
                    if (Long(ticket) || ticket.OuterStart >= dayEnd || ticket.OuterEnd <= dayStart) continue;
                    pieces.Add(new Piece { ticket = ticket, kind = PieceKind.Timed, day = d });
                    spans.Add((Max(ticket.OuterStart, dayStart), Min(ticket.OuterEnd, dayEnd)));
                }
                packed = Pack(spans);
                for (int i = 0; i < packed.Length; i++)
                {
                    Piece piece = pieces[firstPiece + i];
                    (piece.column, piece.columns) = packed[i];
                    pieces[firstPiece + i] = piece;
                }
            }
        }

        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            BuildPieces();
            if (shownLanes < 0f || Span != laidSpan) shownLanes = Math.Max(1, lanes);
            laidSpan = Span;
            foreach (Entity child in children)
                if (child is Control control) control.Measure(unbounded);

            arrange.desired = new Vector2(availableSize.X, IsMonth ? availableSize.Y : TopHeight + 24f * HourSize);
            return arrange.desired;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            WriteArranged(finalRect);
            CalendarParts[] layers = { grid, blocks, texts, marks, gutter, headerBack, header, strip, cards };
            foreach (CalendarParts layer in layers)
                layer.Arrange(finalRect);

            LayoutRect view = Viewport();
            if (IsMonth) ArrangeMonth();
            else ArrangeWeek(view);
            ArrangeExpanded(view);
            ArrangeSelection(view);

            foreach (Action<LayoutRect> hide in hides)
                hide(Hidden);
            foreach (CalendarParts layer in layers)
                layer.Settle(finalRect);
            if (Slide > 0f) ClipSlid(view);
            if (!IsMonth) ClipSubtree(strip, new LayoutRect(arrangedRect.x, view.y, arrangedRect.width, TopHeight));
        }

        // Keeps slid days off the hour gutter, or slid weeks off the weekday header.
        private void ClipSlid(LayoutRect view)
        {
            if (IsMonth)
            {
                float top = arrangedRect.y + HeaderSize;
                LayoutRect cells = new LayoutRect(arrangedRect.x, top, arrangedRect.width, arrangedRect.Bottom - top);
                foreach (CalendarParts layer in new[] { grid, blocks, texts, marks, cards })
                    ClipSubtree(layer, cells);
                return;
            }

            float left = arrangedRect.x + Gutter;
            LayoutRect days = new LayoutRect(left, arrangedRect.y, arrangedRect.Right - left, arrangedRect.height);
            foreach (CalendarParts layer in new[] { grid, blocks, texts, marks, headerBack, header, strip, cards })
                ClipSubtree(layer, days);
            float pinned = view.y + TopHeight;
            ClipSubtree(gutter, new LayoutRect(arrangedRect.x, pinned, Gutter, arrangedRect.Bottom - pinned));
        }

        private void ArrangeWeek(LayoutRect view)
        {
            DateTime start = First;
            float columnWidth = ColumnWidth;
            float left = arrangedRect.x + Gutter;
            float pinned = view.y + TopHeight;

            int fromHour = Math.Clamp((int)MathF.Floor((pinned - BodyTop) / HourSize), 0, 24);
            int toHour = Math.Clamp((int)MathF.Ceiling((view.Bottom - BodyTop) / HourSize), 0, 24);
            for (int h = fromHour; h <= toHour; h++)
            {
                float y = BodyTop + h * HourSize;
                lines.Next().Arrange(new LayoutRect(left, y, arrangedRect.width - Gutter, lineWidth));
                if (h < 24) PlaceText(hours.Next(), DateFormats.Hour(start.AddHours(h)), new LayoutRect(arrangedRect.x, y, Gutter, LabelSize));
            }
            for (int d = 0; d <= Shown; d++)
                lines.Next().Arrange(new LayoutRect(ColumnX(d) - lineWidth, view.y, lineWidth, view.height));
            int weekStart = ((int)DateFormats.FirstDay - (int)start.DayOfWeek + 7) % 7;
            for (int d = weekStart; d < Shown; d += 7)
                weekStartParts.Next().Arrange(new LayoutRect(ColumnX(d), view.y, columnWidth, view.height));

            DateTime now = editor.Now;
            int today = (now.Date - start).Days;
            if (today >= 0 && today < Shown)
                todayParts.Next().Arrange(new LayoutRect(ColumnX(today), view.y, columnWidth, view.height));

            PlannerDocument document = editor.document;
            for (int i = 0; i < pieces.Count; i++)
            {
                Piece piece = pieces[i];
                if (piece.kind != PieceKind.Timed) continue;

                PlannerTicket ticket = piece.ticket;
                DateTime dayStart = start.AddDays(piece.day);
                DateTime dayEnd = dayStart.AddDays(1);
                float x = ColumnX(piece.day) + columnWidth * piece.column / piece.columns + BlockInset;
                float width = MathF.Max(0f, columnWidth / piece.columns - BlockInset * 2f);

                DateTime from = Max(ticket.time.start, dayStart);
                DateTime to = Min(ticket.time.end, dayEnd);
                piece.rect = Hidden;
                if (to > from)
                {
                    float top = Y(dayStart, from);
                    piece.rect = new LayoutRect(x, top, width, MathF.Max(MinBlock, Y(dayStart, to) - top));
                    Paint(blockParts.Next(), document.ColorOf(ticket)).Arrange(piece.rect);
                    if (piece.rect.height >= LabelSize)
                        PlaceText(blockNames.Next(), ticket.name, new LayoutRect(x, top, width, LabelSize));
                }
                pieces[i] = piece;

                DateTime cursor = ticket.OuterStart;
                foreach (PlannerAttachment attachment in ticket.attachments.Where(a => a.before).Concat(ticket.attachments.Where(a => !a.before)))
                {
                    if (!attachment.before && cursor < ticket.time.end) cursor = ticket.time.end;
                    DateTime a = Max(cursor, dayStart);
                    DateTime b = Min(cursor + attachment.duration, dayEnd);
                    cursor += attachment.duration;
                    if (b <= a) continue;
                    float top = Y(dayStart, a);
                    Paint(attachmentParts.Next(), KindColor(attachment, document, ticket)).Arrange(new LayoutRect(x, top, width, Y(dayStart, b) - top));
                }
            }

            if (today >= 0 && today < Shown)
            {
                float y = Y(now.Date, now);
                nowParts.Next().Arrange(new LayoutRect(ColumnX(today), y - nowWidth * 0.5f, columnWidth, nowWidth));
                nowParts.Next().Arrange(new LayoutRect(ColumnX(today) - DotSize * 0.5f, y - DotSize * 0.5f, DotSize, DotSize));
            }

            headerParts.Next().Arrange(new LayoutRect(view.x, view.y, view.width, TopHeight));
            lines.Next().Arrange(new LayoutRect(view.x, view.y + HeaderSize - lineWidth, view.width, lineWidth));
            lines.Next().Arrange(new LayoutRect(view.x, pinned - lineWidth, view.width, lineWidth));
            for (int d = 0; d < Shown; d++)
            {
                DateTime day = start.AddDays(d);
                bool startsWeek = (d - weekStart) % 7 == 0;
                LabelControl name = Role(dayNames.Next(), day == now.Date ? PaletteRole.Chrome : startsWeek ? PaletteRole.MutedInk : PaletteRole.Ink);
                name.style = day == now.Date || startsWeek ? FontStyle.Bold : FontStyle.Regular;
                PlaceCentred(name, day.ToString("ddd d", CultureInfo.InvariantCulture), new LayoutRect(ColumnX(d), view.y, columnWidth, HeaderSize));
                if (day != now.Date) continue;
                LayoutRect n = name.arrangedRect;
                todayPills.Next().Arrange(new LayoutRect(n.x - TextInset * 2f, n.y - TextInset * 0.5f, n.width + TextInset * 4f, n.height + TextInset));
            }

            for (int i = 0; i < pieces.Count; i++)
            {
                Piece piece = pieces[i];
                if (piece.kind != PieceKind.AllDay) continue;
                float x = ColumnX(piece.day) + BlockInset;
                piece.rect = new LayoutRect(x, view.y + HeaderSize + piece.column * LaneSize + BlockInset,
                    ColumnX(piece.lastDay + 1) - x - BlockInset, LaneSize - BlockInset * 2f);
                Paint(stripParts.Next(), document.ColorOf(piece.ticket)).Arrange(piece.rect);
                PlaceText(stripNames.Next(), piece.ticket.name, piece.rect);
                pieces[i] = piece;
            }
        }

        // Weekday names over six weeks of cells; a cell lists what fits and "+N more".
        private void ArrangeMonth()
        {
            DateTime start = First;
            DateTime today = editor.Now.Date;
            int month = editor.anchor.Month;
            float cellWidth = CellWidth;
            float cellHeight = CellHeight;
            int fits = Math.Max(0, (int)MathF.Floor((cellHeight - CellHeader - BlockInset) / ChipSize));

            headerParts.Next().Arrange(new LayoutRect(arrangedRect.x, arrangedRect.y, arrangedRect.width, HeaderSize));
            for (int c = 0; c < 7; c++)
                PlaceCentred(Regular(Role(dayNames.Next(), PaletteRole.MutedInk)), start.AddDays(c).ToString("ddd", CultureInfo.InvariantCulture),
                    new LayoutRect(arrangedRect.x + c * cellWidth, arrangedRect.y, cellWidth, HeaderSize));
            for (int r = 0; r <= Shown / 7; r++)
                lines.Next().Arrange(new LayoutRect(arrangedRect.x, arrangedRect.y + HeaderSize + (r - Slide) * cellHeight - (r == 6 && Slide == 0f ? lineWidth : 0f), arrangedRect.width, lineWidth));
            for (int c = 1; c < 7; c++)
                lines.Next().Arrange(new LayoutRect(arrangedRect.x + c * cellWidth, arrangedRect.y + HeaderSize, lineWidth, arrangedRect.height - HeaderSize));
            weekStartParts.Next().Arrange(new LayoutRect(arrangedRect.x, arrangedRect.y + HeaderSize, cellWidth, arrangedRect.height - HeaderSize));

            for (int d = 0; d < Shown; d++)
            {
                DateTime day = start.AddDays(d);
                PaletteRole role = day == today ? PaletteRole.Chrome : day.Month == month ? PaletteRole.Ink : PaletteRole.MutedInk;
                LabelControl number = Role(numbers.Next(), role);
                PlaceText(number, day.Day.ToString(CultureInfo.InvariantCulture), Cell(d) with { height = CellHeader });
                if (day != today) continue;
                todayParts.Next().Arrange(Cell(d));
                LayoutRect n = number.arrangedRect;
                float side = MathF.Max(n.width, n.height) + TextInset;
                todayDots.Next().Arrange(new LayoutRect(n.x + (n.width - side) * 0.5f, n.y + (n.height - side) * 0.5f, side, side));
            }

            PlannerDocument document = editor.document;
            for (int i = 0; i < pieces.Count; i++)
            {
                Piece piece = pieces[i];
                LayoutRect cell = Cell(piece.day);
                int shown = piece.columns <= fits ? piece.columns : Math.Max(0, fits - 1);
                piece.rect = Hidden;
                if (piece.column < shown)
                {
                    piece.rect = new LayoutRect(cell.x + BlockInset, cell.y + CellHeader + piece.column * ChipSize, cellWidth - BlockInset * 2f, ChipSize - BlockInset);
                    Paint(blockParts.Next(), document.ColorOf(piece.ticket)).Arrange(piece.rect);
                    string text = Long(piece.ticket) ? piece.ticket.name
                        : DateFormats.Time(piece.ticket.time.start) + " " + piece.ticket.name;
                    PlaceText(blockNames.Next(), text, piece.rect);
                }
                else if (piece.column == shown && fits > 0)
                    PlaceText(Role(numbers.Next(), PaletteRole.MutedInk), $"+{piece.columns - shown} more",
                        new LayoutRect(cell.x, cell.y + CellHeader + shown * ChipSize, cellWidth, ChipSize));
                pieces[i] = piece;
            }
        }

        // A ticket's first block that is not under the pinned header.
        private LayoutRect? ShownBlock(PlannerTicket ticket, LayoutRect view)
        {
            foreach (Piece piece in pieces)
                if (ReferenceEquals(piece.ticket, ticket) && piece.rect.width > 0f && !(piece.kind == PieceKind.Timed && piece.rect.y < view.y + TopHeight))
                    return piece.rect;
            return null;
        }

        // The expanded ticket's block grown down over what lies below it, deep enough for its description.
        private void ArrangeExpanded(LayoutRect view)
        {
            expandedRect = LayoutRect.Empty;
            if (editor.expanded is not PlannerTicket ticket || ShownBlock(ticket, view) is not LayoutRect block) return;

            PanelControl card = Paint(cardParts.Next(), editor.document.ColorOf(ticket));
            LabelControl title = cardNames.Next();
            float room = MathF.Max(0f, block.width - TextInset * 2f);
            List<WrappingLabel> lines = new List<WrappingLabel>();
            float height = 0f;
            foreach (string line in ticket.description.Split('\n'))
            {
                WrappingLabel text = cardTexts.Next();
                text.text = line;
                height += text.Measure(new Vector2(room, float.MaxValue)).Y;
                lines.Add(text);
            }
            expandedRect = block with { height = MathF.Max(block.height, LabelSize + height + TextInset) };

            card.Arrange(expandedRect);
            string name = !IsMonth || Long(ticket) ? ticket.name : DateFormats.Time(ticket.time.start) + " " + ticket.name;
            PlaceText(title, name, expandedRect with { height = LabelSize });
            float y = expandedRect.y + LabelSize;
            foreach (WrappingLabel text in lines)
            {
                text.Arrange(new LayoutRect(expandedRect.x + TextInset, y, room, text.DesiredSize.Y));
                y += text.DesiredSize.Y;
            }
        }

        // An outline around the selected ticket's first block that is not under the pinned header, or its expanded card.
        private void ArrangeSelection(LayoutRect view)
        {
            LayoutRect? found = editor.selected is PlannerTicket selected ? ShownBlock(selected, view) : null;
            if (found != null && ReferenceEquals(editor.selected, editor.expanded) && expandedRect.width > 0f) found = expandedRect;

            if (found is not LayoutRect block)
            {
                foreach (PanelControl edge in outline)
                    edge.Arrange(Hidden);
                return;
            }

            LayoutRect ring = new LayoutRect(block.x - outlineWidth, block.y - outlineWidth, block.width + outlineWidth * 2f, block.height + outlineWidth * 2f);
            outline[0].Arrange(new LayoutRect(ring.x, ring.y, ring.width, outlineWidth));
            outline[1].Arrange(new LayoutRect(ring.x, ring.Bottom - outlineWidth, ring.width, outlineWidth));
            outline[2].Arrange(new LayoutRect(ring.x, ring.y, outlineWidth, ring.height));
            outline[3].Arrange(new LayoutRect(ring.Right - outlineWidth, ring.y, outlineWidth, ring.height));
        }

        private static string KindColor(PlannerAttachment attachment, PlannerDocument document, PlannerTicket ticket) =>
            PlannerAttachmentKinds.Find(attachment.kind)?.colorHex ?? document.ColorOf(ticket);

        private static PanelControl Paint(PanelControl part, string color)
        {
            if (part.colorHex != color) part.PaintOr(color, PaletteRole.Accent);
            return part;
        }

        private static LabelControl Role(LabelControl label, PaletteRole role)
        {
            if (label.role != role) label.PaintOr(null, role);
            return label;
        }

        private static LabelControl Regular(LabelControl label)
        {
            label.style = FontStyle.Regular;
            return label;
        }

        private void Style(LabelControl label, PaletteRole role, FontStyle style)
        {
            label.fontSize = FontSize;
            label.style = style;
            label.PaintOr(null, role);
        }

        private void PlaceText(LabelControl label, string text, LayoutRect cell)
        {
            label.text = text;
            label.Measure(unbounded);

            Vector2 size = label.DesiredSize;
            float room = MathF.Max(0f, cell.width - TextInset * 2f);
            label.Arrange(new LayoutRect(cell.x + TextInset, cell.y + (MathF.Min(cell.height, LabelSize) - size.Y) * 0.5f, MathF.Min(size.X, room), size.Y));
        }

        private static void PlaceCentred(LabelControl label, string text, LayoutRect band)
        {
            label.text = text;
            label.Measure(unbounded);

            Vector2 size = label.DesiredSize;
            float width = MathF.Min(size.X, band.width);
            label.Arrange(new LayoutRect(band.x + (band.width - width) * 0.5f, band.y + (band.height - size.Y) * 0.5f, width, size.Y));
        }

        private void Adopt(CalendarParts layer, Control part)
        {
            part.parent = layer;
            layer.children.Add(part);
            MarkTreeOrderDirty();
            part.Measure(unbounded);
        }
        #endregion

        #region ---- pointer ----
        // The top-most shown piece under a point; timed blocks under the pinned header do not count.
        private int? PieceAt(Vector2 point)
        {
            LayoutRect view = Viewport();
            bool underHeader = !IsMonth && point.Y < view.y + TopHeight;
            for (int i = pieces.Count - 1; i >= 0; i--)
            {
                Piece piece = pieces[i];
                if (piece.rect.width <= 0f || (piece.kind == PieceKind.Timed && underHeader)) continue;
                if (piece.rect.Contains(point)) return i;
            }
            return null;
        }

        // A timed block's top edge moves its start, its bottom edge its end; anywhere else moves it.
        private Grip GripOf(Piece piece, Vector2 point)
        {
            if (piece.kind != PieceKind.Timed) return Grip.Move;
            DateTime dayStart = First.AddDays(piece.day);
            float inside = MathF.Min(grabHeight, piece.rect.height / 3f);
            if (piece.ticket.time.start >= dayStart && point.Y <= piece.rect.y + inside) return Grip.Start;
            if (piece.ticket.time.end <= dayStart.AddDays(1) && point.Y >= piece.rect.Bottom - inside) return Grip.End;
            return Grip.Move;
        }

        public override bool OnPointerPress(PointerEvent e)
        {
            base.OnPointerPress(e);
            int? hit = PieceAt(e.point);
            PlannerTicket? ticket = hit is int index ? pieces[index].ticket : null;
            editor.PickCategory(ticket != null ? editor.document.CategoryOf(ticket) : null, this, e.point);
            if (e.button == PointerEvent.rightButton)
            {
                editor.Select(ticket);
                return false;
            }
            if (e.button != PointerEvent.leftButton) return false;

            editor.Select(ticket);
            if (hit is int pressed && ticket != null)
            {
                dragged = ticket;
                draggedBefore = ticket.Clone();
                draggedKind = pieces[pressed].kind;
                grip = GripOf(pieces[pressed], e.point);
                pressPoint = e.point;
                StartDrag();
            }
            return true;
        }

        // A click on a block opens its description; anywhere else folds it away.
        public override bool OnPointerRelease(PointerEvent e)
        {
            if (e.button == PointerEvent.leftButton && e.tapCount == 1) editor.Expand(TicketAt(e.point));
            return base.OnPointerRelease(e);
        }

        // Timed blocks move and resize in 15-minute steps; all-day blocks and month chips move by days.
        public override void OnDrag(PointerEvent e)
        {
            base.OnDrag(e);
            if (dragged == null || draggedBefore == null) return;

            TimeRange was = draggedBefore.time;
            TimeSpan unit = draggedKind == PieceKind.Timed ? snap : TimeSpan.FromDays(1);
            TimeSpan delta = draggedKind == PieceKind.Timed
                ? TimeSpan.FromTicks((long)Math.Round((double)(TimeAt(e.point) - TimeAt(pressPoint)).Ticks / snap.Ticks) * snap.Ticks)
                : TimeSpan.FromDays(DayIndex(e.point) - DayIndex(pressPoint));

            dragged.time = grip switch
            {
                Grip.Move => was with { start = was.start + delta, end = was.end + delta },
                Grip.Start => was with { start = Min(was.start + delta, was.end - unit) },
                _ => was with { end = Max(was.end + delta, was.start + unit) }
            };
            InvalidateLayout();
        }

        public override void OnDragStop(bool accepted)
        {
            base.OnDragStop(accepted);
            if (dragged == null || draggedBefore == null) return;

            PlannerTicket ticket = dragged;
            PlannerTicket before = draggedBefore;
            dragged = null;
            draggedBefore = null;
            editor.CommitTime(ticket, before, grip == Grip.Move ? "Move ticket" : "Resize ticket");
            ShowCursor(CursorShape.Arrow);
        }

        public override bool OnPointerMove(PointerEvent e)
        {
            bool handled = base.OnPointerMove(e);
            if (dragged == null)
                ShowCursor(PieceAt(e.point) is int index && GripOf(pieces[index], e.point) != Grip.Move ? CursorShape.VResize : CursorShape.Arrow);
            return handled;
        }

        public override bool OnPointerExit(PointerEvent e)
        {
            if (dragged == null) ShowCursor(CursorShape.Arrow);
            return base.OnPointerExit(e);
        }

        // A double click edits the ticket under it, or opens a new one there: an hour on the grid, all day in the strip or a month cell.
        public override bool OnPointerTap(PointerEvent e)
        {
            if (e.tapCount != 2) return base.OnPointerTap(e);

            if (TicketAt(e.point) is PlannerTicket ticket)
            {
                editor.Select(ticket);
                editor.EditSelected();
                return true;
            }

            LayoutRect view = Viewport();
            if (IsMonth)
            {
                if (e.point.Y < arrangedRect.y + HeaderSize) return base.OnPointerTap(e);
                DateOnly day = DayAt(e.point);
                editor.NewAt(TimeRange.Days(day, day), this, e.point);
            }
            else if (e.point.X < arrangedRect.x + Gutter || e.point.Y < view.y + HeaderSize) return base.OnPointerTap(e);
            else if (e.point.Y < view.y + TopHeight)
            {
                DateOnly day = DayAt(e.point);
                editor.NewAt(TimeRange.Days(day, day), this, e.point);
            }
            else
            {
                DateTime at = TimeAt(e.point);
                DateTime start = new DateTime(at.Ticks - at.Ticks % snap.Ticks);
                editor.NewAt(new TimeRange(start, start.AddHours(1), false, null), this, e.point);
            }
            return true;
        }

        private void ShowCursor(CursorShape shape)
        {
            if (shownCursor == shape) return;
            shownCursor = shape;
            UIEngine.WindowOf(this)?.os.ChangeCursor(shape);
        }

        private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

        private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
        #endregion
    }
}
