using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Editing;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.EngineWork;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace ArctisAurora.Core.UI.Text
{
    // An editable field a pattern cuts into segments, each a wrapping run, stacked top to bottom.
    public sealed class RegexTextControl : ContainerControl, IContext, IClipboardTarget, ITextField
    {
        // Parts the field places one by one.
        private sealed class FieldParts : ContainerControl
        {
            public FieldParts() => hitTestable = false;

            protected override Vector2 MeasureCore(Vector2 availableSize)
            {
                arrange.desired = availableSize;
                return availableSize;
            }

            protected override void ArrangeCore(LayoutRect finalRect) => WriteArranged(finalRect);

            // Arranges again once its children are placed, so its bounds hold this frame's.
            internal void Settle(LayoutRect rect)
            {
                SetFlag(ArrangeFlags.ArrangeDirty, true);
                Arrange(rect);
            }
        }

        // One edit as the field before and after it.
        private sealed class FieldEdit : IEditRecord
        {
            private readonly RegexTextControl field;
            private readonly (string text, int anchor, int cursor) before;
            private readonly (string text, int anchor, int cursor) after;

            public FieldEdit(RegexTextControl field, (string, int, int) before, (string, int, int) after)
            {
                this.field = field;
                this.before = before;
                this.after = after;
            }

            public void Undo() => field.Restore(before);

            public void Redo() => field.Restore(after);
        }

        public const int maxLines = 8;
        private const float emptyLineMark = 4f;
        private static readonly Vector2 unbounded = new Vector2(float.MaxValue, float.MaxValue);

        public Action<string>? onCommit;
        public Action? onCancel;
        public Action? onBlur;
        public Action<string>? onEdited;

        // what cuts the text into segments, and what Shift+Enter inserts
        public Regex pattern = new Regex("\n");
        public string separator = "\n";

        // selections behind the text, the caret over it
        private readonly FieldParts selectionLayer = new FieldParts();
        private readonly FieldParts textLayer = new FieldParts();
        private readonly CaretControl caret = new CaretControl();
        private readonly List<PanelControl> selections = new List<PanelControl>();
        private readonly List<TextRunControl> runs = new List<TextRunControl>();

        // each segment's first character and length, and its top in content space from the last measure
        private readonly List<(int start, int length)> segments = new List<(int, int)>();
        private readonly List<float> tops = new List<float>();
        private bool layoutStale = true;

        // caret state; equal anchor and cursor means nothing is selected
        private string content = string.Empty;
        private int anchor;
        private int cursor;
        private string committed = string.Empty;
        private float? goalX;

        // how far the text is slid up to keep the caret in the field
        private float scrollY;
        private float contentWidth;
        private float contentHeight;
        private int size = 14;

        private readonly UndoStack history = new UndoStack();

        public bool isEditing { get; private set; }

        public string text
        {
            get => content;
            set
            {
                content = value ?? string.Empty;
                committed = content;
                anchor = cursor = content.Length;
                Split();
            }
        }

        public int fontSize
        {
            get => size;
            set
            {
                size = value;
                foreach (TextRunControl run in runs)
                    run.fontSize = value;
                InvalidateLayout();
            }
        }

        public IReadOnlyList<(int start, int length)> Segments => segments;

        public RegexTextControl()
        {
            clipOutOfBounds = true;
            caret.hitTestable = false;

            base.AddChild(selectionLayer);
            base.AddChild(textLayer);
            base.AddChild(caret);

            caret.Blur();
            Split();
        }

        // A field is its parts; nothing is authored inside it.
        public override void AddChild(Entity entity) =>
            throw new Exception("RegexTextControl takes no children.");

        public void Focus()
        {
            isEditing = true;
            caret.Focus();
            history.Clear();
            SelectAll();
        }

        public void SelectAll()
        {
            anchor = 0;
            cursor = content.Length;
            InvalidateArrange();
        }

        #region ---- segments ----
        // Re-cuts the text and hands each segment to a run.
        private void Split()
        {
            segments.Clear();
            int from = 0;
            foreach (Match match in pattern.Matches(content))
            {
                if (match.Length == 0) continue;
                segments.Add((from, match.Index - from));
                from = match.Index + match.Length;
            }
            segments.Add((from, content.Length - from));

            while (runs.Count < segments.Count)
            {
                TextRunControl run = new TextRunControl { fontSize = size, hitTestable = false };
                runs.Add(run);
                textLayer.AddChild(run);
            }
            for (int i = 0; i < runs.Count; i++)
            {
                string piece = i < segments.Count ? content.Substring(segments[i].start, segments[i].length) : string.Empty;
                if (runs[i].text != piece) runs[i].text = piece;
            }

            layoutStale = true;
            InvalidateLayout();
        }

        // The segment an offset falls in; an offset inside a match counts as the segment before it.
        private int SegmentOf(int offset)
        {
            for (int i = segments.Count - 1; i > 0; i--)
                if (offset >= segments[i].start) return i;
            return 0;
        }

        private int End(int segment) => segments[segment].start + segments[segment].length;

        // An offset inside a match moved to the segment edge in the given direction.
        private int Snap(int offset, bool forward)
        {
            int i = SegmentOf(offset);
            if (offset <= End(i) || i + 1 >= segments.Count) return offset;
            return forward ? segments[i + 1].start : End(i);
        }
        #endregion

        #region ---- editing ----
        public void WriteChar(char c)
        {
            if (c == '\0') return;
            Insert(c.ToString());
        }

        // Inserts the separator, starting a new segment at the caret.
        public void InsertSeparator()
        {
            if (!isEditing) return;
            Insert(separator);
        }

        private void Insert(string inserted)
        {
            var before = State();
            DeleteSelection();
            content = content[..cursor] + inserted + content[cursor..];
            cursor += inserted.Length;
            Changed(before);
        }

        // At a segment's start, removes the whole match before it.
        public void Backspace(bool word = false)
        {
            var before = State();
            if (!DeleteSelection() && cursor > 0)
            {
                int i = SegmentOf(cursor);
                int from = i > 0 && cursor == segments[i].start ? End(i - 1)
                    : word ? Math.Max(segments[i].start, TextInputActions.WordEdge(content, cursor, -1)) : cursor - 1;
                content = content[..from] + content[cursor..];
                cursor = from;
            }
            Changed(before);
        }

        // At a segment's end, removes the whole match after it.
        public void Delete(bool word = false)
        {
            var before = State();
            if (!DeleteSelection() && cursor < content.Length)
            {
                int i = SegmentOf(cursor);
                int to = i + 1 < segments.Count && cursor == End(i) ? segments[i + 1].start
                    : word ? Math.Min(End(i), TextInputActions.WordEdge(content, cursor, 1)) : cursor + 1;
                content = content[..cursor] + content[to..];
            }
            Changed(before);
        }

        private void Changed((string text, int anchor, int cursor) before)
        {
            Split();
            cursor = Snap(cursor, true);
            anchor = cursor;
            goalX = null;
            Record(before);
        }

        public void MoveCaret(CaretMove move, bool extend)
        {
            if (move is not (CaretMove.Up or CaretMove.Down)) goalX = null;

            switch (move)
            {
                case CaretMove.Left: if (cursor > 0) cursor = Snap(cursor - 1, false); break;
                case CaretMove.Right: if (cursor < content.Length) cursor = Snap(cursor + 1, true); break;
                case CaretMove.WordLeft: cursor = Snap(TextInputActions.WordEdge(content, cursor, -1), false); break;
                case CaretMove.WordRight: cursor = Snap(TextInputActions.WordEdge(content, cursor, 1), true); break;
                case CaretMove.Up: cursor = Vertical(-1); break;
                case CaretMove.Down: cursor = Vertical(1); break;
                case CaretMove.LineStart: cursor = LineEdge(false); break;
                case CaretMove.LineEnd: cursor = LineEdge(true); break;
                case CaretMove.PageUp:
                case CaretMove.DocumentStart: cursor = 0; break;
                default: cursor = content.Length; break;
            }

            if (!extend) anchor = cursor;
            InvalidateArrange();
        }

        // The offset a visual line above or below, nearest the column the vertical moves started from; past either end, that end.
        private int Vertical(int direction)
        {
            if (layoutStale) return cursor;

            int i = SegmentOf(cursor);
            CaretGeometry at = runs[i].CaretAt(cursor - segments[i].start);
            goalX ??= at.x;

            int line = LineOf(runs[i].Lines, at.top) + direction;
            while (line < 0 || line >= runs[i].Lines.Count)
            {
                i += direction;
                if (i < 0) return 0;
                if (i >= segments.Count) return content.Length;
                line = direction < 0 ? runs[i].Lines.Count - 1 : 0;
            }

            TextLine target = runs[i].Lines[line];
            return segments[i].start + runs[i].IndexAt(runs[i].TextOrigin + new Vector2(goalX.Value, target.top + target.height * 0.5f));
        }

        // The start or end of the caret's visual line; a wrapped line ends before the space it broke at.
        private int LineEdge(bool end)
        {
            if (layoutStale) return cursor;

            int i = SegmentOf(cursor);
            IReadOnlyList<TextLine> lines = runs[i].Lines;
            int index = LineOf(lines, runs[i].CaretAt(cursor - segments[i].start).top);
            TextLine line = lines[index];
            if (line.segments.Count == 0) return cursor;

            if (!end) return segments[i].start + line.segments[0].charStart;
            LineSegment last = line.segments[^1];
            int offset = last.charStart + last.charCount;
            if (index < lines.Count - 1 && offset > 0 && runs[i].text[offset - 1] == ' ') offset--;
            return segments[i].start + offset;
        }

        private static int LineOf(IReadOnlyList<TextLine> lines, float top)
        {
            int index = 0;
            for (int j = 0; j < lines.Count; j++)
                if (lines[j].top <= top + 0.5f) index = j;
            return index;
        }

        public void Commit()
        {
            isEditing = false;
            caret.Blur();
            history.Clear();
            committed = content;
            InvalidateArrange();
            onCommit?.Invoke(committed);
        }

        // Restores what the field held when it was last committed.
        public void Cancel()
        {
            isEditing = false;
            caret.Blur();
            history.Clear();
            content = committed;
            anchor = cursor = content.Length;
            Split();
            onCancel?.Invoke();
        }

        public void Undo() => history.Undo();

        public void Redo() => history.Redo();

        private (string text, int anchor, int cursor) State() => (content, anchor, cursor);

        private void Restore((string text, int anchor, int cursor) state)
        {
            content = state.text;
            anchor = state.anchor;
            cursor = state.cursor;
            Split();
            onEdited?.Invoke(content);
        }

        // One step per change; a key that changed nothing records nothing.
        private void Record((string text, int anchor, int cursor) before)
        {
            if (before.text == content) return;

            using (history.Begin("Edit"))
                history.Push(new FieldEdit(this, before, State()));
            onEdited?.Invoke(content);
        }

        public bool Copy()
        {
            if (!isEditing) return false;
            if (anchor != cursor) ClipboardText.Set(Selected());
            return true;
        }

        public bool Cut()
        {
            if (!isEditing) return false;
            if (anchor == cursor) return true;

            ClipboardText.Set(Selected());
            Insert(string.Empty);
            return true;
        }

        // Line breaks become the separator; other control characters but tabs drop.
        public bool Paste(string text)
        {
            if (!isEditing) return false;

            StringBuilder clean = new StringBuilder(text.Length);
            foreach (char c in text.Replace("\r\n", "\n").Replace('\r', '\n'))
                if (c == '\n') clean.Append(separator);
                else if (c == '\t' || !char.IsControl(c)) clean.Append(c);

            Insert(clean.ToString());
            return true;
        }

        public bool PasteImage(Image<Rgba32> image) => false;

        private string Selected() => content[Math.Min(anchor, cursor)..Math.Max(anchor, cursor)];

        private bool DeleteSelection()
        {
            int from = Math.Min(anchor, cursor);
            int to = Math.Max(anchor, cursor);
            if (from == to) return false;

            content = content[..from] + content[to..];
            cursor = anchor = from;
            return true;
        }
        #endregion

        #region ---- focus ----
        public void OnContextAdded(string context) { }

        public void OnContextRemoved(string context)
        {
            if (context == "ActiveControl") LoseFocus();
        }

        private void LoseFocus()
        {
            for (Control control = UIEngine.activeControl; control != null; control = control.parent as Control)
                if (ReferenceEquals(control, this)) return;

            isEditing = false;
            caret.Blur();
            InvalidateArrange();
            onBlur?.Invoke();
        }
        #endregion

        #region ---- pointer ----
        public override bool OnPointerPress(PointerEvent e)
        {
            if (e.button != PointerEvent.leftButton) return false;

            isEditing = true;
            caret.Focus();
            goalX = null;

            cursor = Snap(OffsetAt(e.point), true);
            if (!InputHandler.instance.IsModifierDown(InputModifier.Extend)) anchor = cursor;

            InvalidateArrange();
            return true;
        }

        // A double click selects the word under the caret, a triple click the whole field.
        public override bool OnPointerTap(PointerEvent e)
        {
            if (!isEditing) return false;

            if (e.tapCount == 2)
            {
                int i = SegmentOf(cursor);
                anchor = Math.Max(segments[i].start, TextInputActions.WordEdge(content, Math.Min(cursor + 1, End(i)), -1));
                cursor = Math.Min(End(i), TextInputActions.WordEdge(content, anchor, 1));
                while (cursor > anchor && content[cursor - 1] == ' ') cursor--;
                InvalidateArrange();
            }
            else SelectAll();
            return true;
        }

        private int OffsetAt(Vector2 point)
        {
            int i = 0;
            while (i < segments.Count - 1 && point.Y >= runs[i + 1].arrangedRect.y) i++;
            return segments[i].start + runs[i].IndexAt(point);
        }
        #endregion

        #region ---- layout ----
        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            float w = preferredWidth > 0 ? preferredWidth : MathF.Max(minWidth, availableSize.X);
            contentWidth = MathF.Max(1f, w - padding.left - padding.right);

            selectionLayer.Measure(availableSize);
            textLayer.Measure(availableSize);

            tops.Clear();
            float height = 0f;
            for (int i = 0; i < runs.Count; i++)
            {
                float runHeight = runs[i].Measure(new Vector2(contentWidth, float.MaxValue)).Y;
                if (i >= segments.Count) continue;
                tops.Add(height);
                height += runHeight;
            }
            contentHeight = height;
            layoutStale = false;

            float edges = padding.top + padding.bottom;
            float most = MathF.Max(minHeight, maxLines * size * runs[0].lineHeight + edges);
            float h = preferredHeight > 0 ? preferredHeight : Math.Clamp(height + edges, minHeight, most);

            arrange.desired = new Vector2(w, h);
            SetFlag(ArrangeFlags.MeasureDirty, false);
            return arrange.desired;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            WriteArranged(finalRect);
            selectionLayer.Arrange(finalRect);
            textLayer.Arrange(finalRect);

            LayoutRect inner = finalRect.Shrink(padding);
            if (isEditing && !layoutStale) FollowCaret(inner.height);
            else if (!isEditing) scrollY = 0f;

            for (int i = 0; i < runs.Count; i++)
                runs[i].Arrange(i < segments.Count && i < tops.Count
                    ? new LayoutRect(inner.x, inner.y + tops[i] - scrollY, inner.width, runs[i].DesiredSize.Y)
                    : LayoutRect.Empty);

            ArrangeCaretAndSelection();

            selectionLayer.Settle(finalRect);
            textLayer.Settle(finalRect);
            SetFlag(ArrangeFlags.ArrangeDirty, false);
        }

        // Slides the text so the caret's line stays inside a field this tall.
        private void FollowCaret(float height)
        {
            int i = SegmentOf(cursor);
            CaretGeometry at = runs[i].CaretAt(cursor - segments[i].start);
            float top = tops[i] + at.top;
            if (top + at.height - scrollY > height) scrollY = top + at.height - height;
            if (top < scrollY) scrollY = top;
            scrollY = MathF.Max(0f, MathF.Min(scrollY, contentHeight - height));
        }

        // Both collapse to nothing when the field is not being edited.
        private void ArrangeCaretAndSelection()
        {
            int used = 0;
            if (isEditing && !layoutStale)
            {
                int i = SegmentOf(cursor);
                Vector2 origin = runs[i].TextOrigin;
                CaretGeometry at = runs[i].CaretAt(cursor - segments[i].start);
                caret.Arrange(new LayoutRect(origin.X + at.x, origin.Y + at.top, CaretControl.Width, at.height));

                int from = Math.Min(anchor, cursor);
                int to = Math.Max(anchor, cursor);
                if (from != to)
                    for (int s = SegmentOf(from); s <= SegmentOf(to) && s < segments.Count; s++)
                        used = SelectLines(s, from, to, used);
            }
            else caret.Arrange(LayoutRect.Empty);

            for (int i = used; i < selections.Count; i++)
                selections[i].Arrange(LayoutRect.Empty);
        }

        // One rect per line of a segment the range covers; a line holding only a crossed break gets a short mark.
        private int SelectLines(int segment, int from, int to, int used)
        {
            TextRunControl run = runs[segment];
            int start = segments[segment].start;
            Vector2 origin = run.TextOrigin;
            IReadOnlyList<TextLine> lines = run.Lines;

            for (int l = 0; l < lines.Count; l++)
            {
                TextLine line = lines[l];
                int lineStart = start + (line.segments.Count > 0 ? line.segments[0].charStart : 0);
                int lineEnd = line.segments.Count > 0 ? start + line.segments[^1].charStart + line.segments[^1].charCount : lineStart;
                int a = Math.Max(from, lineStart);
                int b = Math.Min(to, lineEnd);
                bool crosses = to > lineEnd;
                if (a > b || (a == b && !crosses)) continue;

                float lineRight = line.left + line.width;
                float left = a == lineStart ? line.left : a == lineEnd ? lineRight : run.CaretAt(a - start).x;
                float right = b == lineEnd ? lineRight : run.CaretAt(b - start).x;
                if (crosses && l == lines.Count - 1) right += emptyLineMark;
                if (right <= left) continue;

                if (used == selections.Count)
                {
                    PanelControl panel = new PanelControl { hitTestable = false, role = PaletteRole.Line };
                    panel.parent = selectionLayer;
                    selectionLayer.children.Add(panel);
                    MarkTreeOrderDirty();
                    panel.Measure(unbounded);
                    selections.Add(panel);
                }
                selections[used++].Arrange(new LayoutRect(origin.X + left, origin.Y + line.top, right - left, line.height));
            }
            return used;
        }
        #endregion
    }
}
