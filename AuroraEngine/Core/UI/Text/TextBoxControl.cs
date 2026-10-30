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

namespace ArctisAurora.Core.UI.Text
{
    // A one-line editable field: the text, a caret and a selection box under one control.
    [A_XSDType("TextBox", "UI")]
    public class TextBoxControl : ContainerControl, IContext, IClipboardTarget, ITextField
    {
        // The text itself, handing its presses to the box, which owns the caret.
        private class FieldLine : TextRunControl
        {
            public override Control? ActiveContextTarget() => (parent as Control)?.ActiveContextTarget();

            public override bool OnPointerPress(PointerEvent e) => false;
        }

        // One edit as the field before and after it.
        private sealed class FieldEdit : IEditRecord
        {
            private readonly TextBoxControl box;
            private readonly (string text, int anchor, int cursor) before;
            private readonly (string text, int anchor, int cursor) after;

            public FieldEdit(TextBoxControl box, (string, int, int) before, (string, int, int) after)
            {
                this.box = box;
                this.before = before;
                this.after = after;
            }

            public void Undo() => box.Restore(before);

            public void Redo() => box.Restore(after);
        }

        public Action<string>? onCommit;
        public Action? onCancel;
        public Action? onBlur;
        public Action<string>? onEdited;

        // selection first: children draw in order, so it lands behind the text
        private readonly PanelControl selection = new PanelControl();
        private readonly FieldLine line = new FieldLine();
        private readonly CaretControl caret = new CaretControl();

        // where a selection started; equal to the cursor means nothing is selected
        private int anchor;
        private int cursor;
        private string committed = string.Empty;

        // how far the text is slid left to keep the caret in the box
        private float scrollX;

        // this edit's history, from the last focus
        private readonly UndoStack history = new UndoStack();

        public bool isEditing { get; private set; }

        // a masked field's string, shown as one mask character per character
        private bool _masked;
        private string secret = string.Empty;
        private const char maskChar = '*';

        [A_XSDElementProperty("Text", "UI", "The string being edited.")]
        public string text
        {
            get => content;
            set
            {
                content = value ?? string.Empty;
                committed = content;
                anchor = cursor = content.Length;
            }
        }

        [A_XSDElementProperty("Masked", "UI", "Shows a mask character in place of each character and refuses copy.")]
        public bool masked
        {
            get => _masked;
            set
            {
                string current = content;
                _masked = value;
                content = current;
            }
        }

        private string content
        {
            get => _masked ? secret : line.text;
            set
            {
                if (!_masked)
                {
                    line.text = value;
                    return;
                }
                secret = value;
                line.text = new string(maskChar, value.Length);
            }
        }

        [A_XSDElementProperty("FontSize", "UI", "Font size in pixels.")]
        public int fontSize
        {
            get => (int)line.fontSize;
            set => line.fontSize = value;
        }

        [A_XSDElementProperty("TextColorHex", "UI", "Colour of the text.")]
        public string? textColorHex
        {
            get => line.colorHex;
            set => PaintText(value, PaletteRole.Ink);
        }

        [A_XSDElementProperty("SelectionColorHex", "UI", "Ground of the selected range.")]
        public string? selectionColorHex
        {
            get => selection.colorHex;
            set => selection.PaintOr(value, PaletteRole.Line);
        }

        [A_XSDElementProperty("CaretColorHex", "UI", "Colour of the insertion point.")]
        public string? caretColorHex
        {
            get => caret.colorHex;
            set => caret.PaintOr(value, PaletteRole.Ink);
        }

        public TextBoxControl()
        {
            clipOutOfBounds = true;

            selection.hitTestable = false;
            selection.role = PaletteRole.Line;
            caret.hitTestable = false;

            base.AddChild(selection);
            base.AddChild(line);
            base.AddChild(caret);

            caret.Blur();
        }

        // Paints the text an authored hex, or the role when there is none.
        public void PaintText(string? hex, PaletteRole fallback) => line.PaintOr(hex, fallback);

        // A box is its three parts; nothing is authored inside it.
        public override void AddChild(Entity entity) =>
            throw new Exception("TextBoxControl takes no children.");

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
            cursor = line.text.Length;
            InvalidateArrange();
        }

        #region ---- editing ----
        public void WriteChar(char c)
        {
            if (c == '\0') return;

            var before = State();
            DeleteSelection();
            content = content[..cursor] + c + content[cursor..];
            cursor++;
            anchor = cursor;
            Record(before);
            InvalidateLayout();
        }

        public void Backspace(bool word = false)
        {
            var before = State();
            if (!DeleteSelection() && cursor > 0)
            {
                int from = word ? TextInputActions.WordEdge(line.text, cursor, -1) : cursor - 1;
                content = content[..from] + content[cursor..];
                cursor = from;
            }
            anchor = cursor;
            Record(before);
            InvalidateLayout();
        }

        public void Delete(bool word = false)
        {
            var before = State();
            if (!DeleteSelection() && cursor < line.text.Length)
            {
                int to = word ? TextInputActions.WordEdge(line.text, cursor, 1) : cursor + 1;
                content = content[..cursor] + content[to..];
            }

            anchor = cursor;
            Record(before);
            InvalidateLayout();
        }

        // One line, so the vertical and page moves collapse onto its ends.
        public void MoveCaret(CaretMove move, bool extend)
        {
            switch (move)
            {
                case CaretMove.Left: if (cursor > 0) cursor--; break;
                case CaretMove.Right: if (cursor < line.text.Length) cursor++; break;
                case CaretMove.WordLeft: cursor = TextInputActions.WordEdge(line.text, cursor, -1); break;
                case CaretMove.WordRight: cursor = TextInputActions.WordEdge(line.text, cursor, 1); break;
                case CaretMove.Up:
                case CaretMove.PageUp:
                case CaretMove.DocumentStart:
                case CaretMove.LineStart: cursor = 0; break;
                default: cursor = line.text.Length; break;
            }

            if (!extend) anchor = cursor;
            InvalidateArrange();
        }

        public void Commit()
        {
            isEditing = false;
            caret.Blur();
            history.Clear();
            committed = content;
            onCommit?.Invoke(committed);
        }

        // Restores what the box held when it was last committed.
        public void Cancel()
        {
            isEditing = false;
            caret.Blur();
            history.Clear();
            content = committed;
            anchor = cursor = committed.Length;
            onCancel?.Invoke();
            InvalidateLayout();
        }

        public void Undo() => history.Undo();

        public void Redo() => history.Redo();

        private (string text, int anchor, int cursor) State() => (content, anchor, cursor);

        private void Restore((string text, int anchor, int cursor) state)
        {
            content = state.text;
            anchor = state.anchor;
            cursor = state.cursor;
            InvalidateLayout();
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
            if (anchor != cursor && !_masked) ClipboardText.Set(Selected());
            return true;
        }

        public bool Cut()
        {
            if (!isEditing) return false;
            if (anchor == cursor || _masked) return true;

            var before = State();
            ClipboardText.Set(Selected());
            DeleteSelection();
            Record(before);
            InvalidateLayout();
            return true;
        }

        // One line, so line breaks and tabs become spaces and other control characters drop.
        public bool Paste(string text)
        {
            if (!isEditing) return false;

            StringBuilder flat = new StringBuilder(text.Length);
            foreach (char c in text.Replace("\r\n", "\n"))
                if (c == '\n' || c == '\r' || c == '\t') flat.Append(' ');
                else if (!char.IsControl(c)) flat.Append(c);

            var before = State();
            DeleteSelection();
            content = content[..cursor] + flat + content[cursor..];
            cursor += flat.Length;
            anchor = cursor;
            Record(before);
            InvalidateLayout();
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

        // Raised only when the context went somewhere outside the box. The session ends here rather
        // than in the handler, so the caret leaves with the focus whatever onBlur decides to do.
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

            cursor = line.IndexAt(e.point);
            if (!InputHandler.instance.IsModifierDown(InputModifier.Extend)) anchor = cursor;

            InvalidateArrange();
            return true;
        }

        // A double click selects the word under the caret, a triple click the whole field.
        public override bool OnPointerTap(PointerEvent e)
        {
            if (!isEditing) return false;

            if (e.tapCount == 2) SelectWordAt(cursor);
            else SelectAll();
            return true;
        }

        // Selects the word an offset touches, preferring the one to its right.
        private void SelectWordAt(int offset)
        {
            string s = line.text;
            anchor = TextInputActions.WordEdge(s, Math.Min(offset + 1, s.Length), -1);
            cursor = TextInputActions.WordEdge(s, anchor, 1);
            while (cursor > anchor && s[cursor - 1] == ' ') cursor--;
            InvalidateArrange();
        }
        #endregion

        #region ---- layout ----
        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            float w = preferredWidth > 0 ? preferredWidth : MathF.Max(minWidth, availableSize.X);
            float h = preferredHeight > 0 ? preferredHeight : MathF.Max(minHeight, availableSize.Y);

            line.Measure(new Vector2(float.MaxValue, float.MaxValue));

            arrange.desired = new Vector2(w, h);
            SetFlag(ArrangeFlags.MeasureDirty, false);
            return arrange.desired;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            WriteArranged(finalRect);

            LayoutRect inner = finalRect.Shrink(padding);

            // The text is centred on the box rather than filling it, so a tall box does not push the
            // one line it holds to the top.
            float textHeight = line.DesiredSize.Y;
            if (isEditing) FollowCaret(inner.width);
            else scrollX = 0f;
            line.Arrange(new LayoutRect(inner.x - scrollX, inner.y + (inner.height - textHeight) * 0.5f,
                MathF.Max(inner.width, line.DesiredSize.X), textHeight));

            ArrangeCaretAndSelection();

            SetFlag(ArrangeFlags.ArrangeDirty, false);
        }

        // Slides the text so the caret stays inside a box this wide.
        private void FollowCaret(float width)
        {
            float x = line.CaretAt(cursor).x;
            float room = width - CaretControl.Width;
            if (x - scrollX > room) scrollX = x - room;
            if (x < scrollX) scrollX = x;
            scrollX = MathF.Max(0f, MathF.Min(scrollX, line.DesiredSize.X + CaretControl.Width - width));
        }

        // Both collapse to nothing when the box is not being edited — a zero-area quad neither draws
        // nor hit-tests.
        private void ArrangeCaretAndSelection()
        {
            if (!isEditing)
            {
                caret.Arrange(LayoutRect.Empty);
                selection.Arrange(LayoutRect.Empty);
                return;
            }

            Vector2 origin = line.TextOrigin;
            CaretGeometry at = line.CaretAt(cursor);
            caret.Arrange(new LayoutRect(origin.X + at.x, origin.Y + at.top, CaretControl.Width, at.height));

            if (anchor == cursor)
            {
                selection.Arrange(LayoutRect.Empty);
                return;
            }

            CaretGeometry other = line.CaretAt(anchor);
            float left = MathF.Min(at.x, other.x);
            float right = MathF.Max(at.x, other.x);
            selection.Arrange(new LayoutRect(origin.X + left, origin.Y + at.top, right - left, at.height));
        }
        #endregion
    }
}
