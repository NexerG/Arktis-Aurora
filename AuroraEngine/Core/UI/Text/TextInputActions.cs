using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Tex;
using ArctisAurora.EngineWork;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ArctisAurora.Core.UI.Text
{
    public enum CaretMove
    {
        Left,
        Right,
        Up,
        Down,
        LineStart,
        LineEnd,
        PageUp,
        PageDown,
        WordLeft,
        WordRight,
        DocumentStart,
        DocumentEnd
    }

    // A control Ctrl+C/X/V reach. False passes the request on to the control above.
    public interface IClipboardTarget
    {
        bool Copy();
        bool Cut();
        bool Paste(string text);
        bool PasteImage(Image<Rgba32> image);
    }

    // A standalone editable field the text keybinds reach.
    public interface ITextField
    {
        bool isEditing { get; }
        void WriteChar(char c);
        void Backspace(bool word = false);
        void Delete(bool word = false);
        void MoveCaret(CaretMove move, bool extend);
        void SelectAll();
        void Commit();
        void Cancel();
        void Undo();
        void Redo();
    }

    // Keybind actions for text editing. They live in the engine rather than in a host so an
    // application declares which keys reach them in XML and writes no input code of its own.
    public static class TextInputActions
    {
        [A_XSDActionDependency("Text.Write", "Input", "Writes queued characters into the focused text control")]
        public static void Write()
        {
            // drain every char polled this tick — the OS repeat rate can outpace the frame rate
            Queue<char> input = InputHandler.charInputReadQueue;
            if (input.Count == 0) return;

            DocumentEditorControl next = Editor();
            if (next != null)
            {
                // one step per character, so one press is one undo however many the queue held
                while (input.Count > 0)
                    using (next.BeginStep("Typing"))
                    {
                        next.DeleteSelection();
                        next.TypeChar(input.Dequeue());
                    }

                next.MarkDirty();
                return;
            }

            ITextField nextBox = Box();
            if (nextBox == null) return;

            while (input.Count > 0)
                nextBox.WriteChar(input.Dequeue());
        }

        [A_XSDActionDependency("Text.Backspace", "Input", "Deletes the selection, or the character before the caret")]
        public static void Backspace()
        {
            bool word = InputHandler.instance.IsModifierDown(InputModifier.Word);

            DocumentEditorControl next = Editor();
            if (next != null) { next.Backspace(word); return; }

            Box()?.Backspace(word);
        }

        [A_XSDActionDependency("Text.Delete", "Input", "Deletes the selection, or the character after the caret")]
        public static void Delete()
        {
            bool word = InputHandler.instance.IsModifierDown(InputModifier.Word);

            DocumentEditorControl next = Editor();
            if (next != null) { next.Delete(word); return; }

            Box()?.Delete(word);
        }

        [A_XSDActionDependency("Text.Copy", "Input", "Copies the selection to the clipboard")]
        public static void Copy() => ToClipboardTarget(target => target.Copy());

        [A_XSDActionDependency("Text.Cut", "Input", "Moves the selection to the clipboard")]
        public static void Cut() => ToClipboardTarget(target => target.Cut());

        [A_XSDActionDependency("Text.Paste", "Input", "Inserts the clipboard's text at the caret")]
        public static void Paste()
        {
            string? text = ClipboardText.Get();
            if (!string.IsNullOrEmpty(text))
            {
                ToClipboardTarget(target => target.Paste(text));
                return;
            }

            if (!ClipboardImage.TryGet(out Image<Rgba32>? image)) return;
            using (image)
                ToClipboardTarget(target => target.PasteImage(image));
        }

        [A_XSDActionDependency("Text.PasteLink", "Input", "Inserts a live link to the copied sheet cells; a plain paste otherwise")]
        public static void PasteLink()
        {
            if (FormulaPopup.PasteLink()) return;
            DocumentEditorControl editor = Editor();
            if (editor == null || !editor.PasteLink()) Paste();
        }

        // Walks up from the active control until a target handles the request.
        private static void ToClipboardTarget(Func<IClipboardTarget, bool> request)
        {
            for (ArctisAurora.Core.UI.Control control = UIEngine.activeControl;
                 control != null;
                 control = control.parent as ArctisAurora.Core.UI.Control)
                if (control is IClipboardTarget target && request(target)) return;
        }

        [A_XSDActionDependency("Text.NewBlock", "Input", "Splits the caret's block in two, or commits a standalone field")]
        public static void NewBlock()
        {
            DocumentEditorControl next = Editor();
            if (next != null)
            {
                if (!next.EditFormula()) next.SplitBlock();
                return;
            }

            ITextField box = Box();
            if (box is RegexTextControl area && InputHandler.instance.IsModifierDown(InputModifier.Extend)) area.InsertSeparator();
            else box?.Commit();
        }

        [A_XSDActionDependency("Text.SelectAll", "Input", "Selects the whole of the focused note or field")]
        public static void SelectAll()
        {
            DocumentEditorControl next = Editor();
            if (next != null) { next.SelectAll(); return; }

            Box()?.SelectAll();
        }

        [A_XSDActionDependency("Text.Bold", "Input", "Toggles bold over the selection, or for what is typed next")]
        public static void Bold() => Toggle(span => span.IsBold, style => style.bold, on => new StyleDelta(bold: on));

        [A_XSDActionDependency("Text.Italic", "Input", "Toggles italic over the selection, or for what is typed next")]
        public static void Italic() => Toggle(span => span.IsItalic, style => style.italic, on => new StyleDelta(italic: on));

        [A_XSDActionDependency("Text.Underline", "Input", "Toggles underline over the selection, or for what is typed next")]
        public static void Underline() => Toggle(span => span.underline, style => style.underline, on => new StyleDelta(underline: on));

        [A_XSDActionDependency("Text.Strikethrough", "Input", "Toggles strikethrough over the selection, or for what is typed next")]
        public static void Strikethrough() => Toggle(span => span.strikethrough, style => style.strikethrough, on => new StyleDelta(strikethrough: on));

        [A_XSDActionDependency("Text.InlineCode", "Input", "Toggles inline code over the selection, or for what is typed next")]
        public static void InlineCode() => Toggle(span => span.stylingType == TextStyleType.Code, style => style.code, on => new StyleDelta(code: on));

        #region ---- alignment ----
        [A_XSDActionDependency("Text.AlignLeft", "Input", "Aligns the paragraphs under the caret to the left")]
        public static void AlignLeft() => Editor()?.SetAlignment(TextAlignment.Left);

        [A_XSDActionDependency("Text.Bullets", "Input", "Turns the paragraphs under the caret into a bulleted list, or back into text")]
        public static void Bullets() => Editor()?.ToggleBullets();

        [A_XSDActionDependency("Text.Numbers", "Input", "Turns the paragraphs under the caret into a numbered list, or back into text")]
        public static void Numbers() => Editor()?.ToggleNumbers();

        [A_XSDActionDependency("Text.Tasks", "Input", "Turns the paragraphs under the caret into a task list, or back into text")]
        public static void Tasks() => Editor()?.ToggleTasks();

        [A_XSDActionDependency("Text.CycleList", "Input", "Steps the caret's paragraphs through bullets, numbers, tasks, sorted tasks and back to text")]
        public static void CycleList()
        {
            DocumentEditorControl editor = Editor();
            BlockControl block = editor?.CaretBlock;
            if (block == null) return;

            switch (block.listKind)
            {
                case ListKind.None: editor.ToggleBullets(); break;
                case ListKind.Bullet when !ListMarkers.IsNumbered(block.listMarker ?? ListMarker.Disc): editor.ToggleNumbers(); break;
                case ListKind.Bullet: editor.ToggleTasks(); break;
                case ListKind.Task: editor.ToggleSortedTasks(); break;
                case ListKind.SortedTask: editor.ToggleSortedTasks(); break;
            }
        }

        [A_XSDActionDependency("Text.ToggleCheck", "Input", "Ticks or unticks the task under the caret")]
        public static void ToggleCheck()
        {
            DocumentEditorControl editor = Editor();
            BlockControl block = editor?.CaretBlock;
            if (block?.listKind is ListKind.Task or ListKind.SortedTask) editor.SetChecked(block, !block.isChecked);
        }

        [A_XSDActionDependency("Text.Rule", "Input", "Inserts a horizontal line after the caret's paragraph")]
        public static void Rule() => Editor()?.InsertRule();

        [A_XSDActionDependency("Text.AlignCenter", "Input", "Centres the paragraphs under the caret")]
        public static void AlignCenter() => Editor()?.SetAlignment(TextAlignment.Center);

        [A_XSDActionDependency("Text.AlignRight", "Input", "Aligns the paragraphs under the caret to the right")]
        public static void AlignRight() => Editor()?.SetAlignment(TextAlignment.Right);

        [A_XSDActionDependency("Text.AlignJustify", "Input", "Spreads the paragraphs under the caret across the full width")]
        public static void AlignJustify() => Editor()?.SetAlignment(TextAlignment.Justify);
        #endregion

        #region ---- paragraph styles ----
        [A_XSDActionDependency("Text.Paragraph", "Input", "Turns the paragraphs under the caret back into plain text")]
        public static void Paragraph() => Editor()?.SetBlockStyling(TextStyleType.Text);

        [A_XSDActionDependency("Text.Heading1", "Input", "Makes the paragraphs under the caret a level 1 heading, or text again")]
        public static void Heading1() => Block(TextStyleType.Heading1);

        [A_XSDActionDependency("Text.Heading2", "Input", "Makes the paragraphs under the caret a level 2 heading, or text again")]
        public static void Heading2() => Block(TextStyleType.Heading2);

        [A_XSDActionDependency("Text.Heading3", "Input", "Makes the paragraphs under the caret a level 3 heading, or text again")]
        public static void Heading3() => Block(TextStyleType.Heading3);

        [A_XSDActionDependency("Text.Heading4", "Input", "Makes the paragraphs under the caret a level 4 heading, or text again")]
        public static void Heading4() => Block(TextStyleType.Heading4);

        [A_XSDActionDependency("Text.Heading5", "Input", "Makes the paragraphs under the caret a level 5 heading, or text again")]
        public static void Heading5() => Block(TextStyleType.Heading5);

        [A_XSDActionDependency("Text.Heading6", "Input", "Makes the paragraphs under the caret a level 6 heading, or text again")]
        public static void Heading6() => Block(TextStyleType.Heading6);

        [A_XSDActionDependency("Text.Quote", "Input", "Makes the paragraphs under the caret a quote, or text again")]
        public static void Quote() => Block(TextStyleType.Quote);

        [A_XSDActionDependency("Text.CodeBlock", "Input", "Makes the paragraphs under the caret a code block, or text again")]
        public static void CodeBlock() => Block(TextStyleType.Code);

        // Applies the paragraph style, or plain text when the caret's paragraph already has it.
        private static void Block(TextStyleType type)
        {
            DocumentEditorControl editor = Editor();
            if (editor == null) return;

            editor.SetBlockStyling(editor.CaretBlockStyling == type ? TextStyleType.Text : type);
        }
        #endregion

        #region ---- list markers ----
        [A_XSDActionDependency("List.Disc", "Input", "Marks the caret's list level with filled circles")]
        public static void MarkDisc() => Editor()?.SetListMarker(ListMarker.Disc);

        [A_XSDActionDependency("List.Circle", "Input", "Marks the caret's list level with empty circles")]
        public static void MarkCircle() => Editor()?.SetListMarker(ListMarker.Circle);

        [A_XSDActionDependency("List.Triangle", "Input", "Marks the caret's list level with filled triangles")]
        public static void MarkTriangle() => Editor()?.SetListMarker(ListMarker.Triangle);

        [A_XSDActionDependency("List.TriangleOutline", "Input", "Marks the caret's list level with empty triangles")]
        public static void MarkTriangleOutline() => Editor()?.SetListMarker(ListMarker.TriangleOutline);

        [A_XSDActionDependency("List.Square", "Input", "Marks the caret's list level with filled squares")]
        public static void MarkSquare() => Editor()?.SetListMarker(ListMarker.Square);

        [A_XSDActionDependency("List.SquareOutline", "Input", "Marks the caret's list level with empty squares")]
        public static void MarkSquareOutline() => Editor()?.SetListMarker(ListMarker.SquareOutline);

        [A_XSDActionDependency("List.Decimal", "Input", "Numbers the caret's list level 1, 2, 3")]
        public static void MarkDecimal() => Editor()?.SetListMarker(ListMarker.Decimal);

        [A_XSDActionDependency("List.UpperAlpha", "Input", "Numbers the caret's list level A, B, C")]
        public static void MarkUpperAlpha() => Editor()?.SetListMarker(ListMarker.UpperAlpha);

        [A_XSDActionDependency("List.LowerAlpha", "Input", "Numbers the caret's list level a, b, c")]
        public static void MarkLowerAlpha() => Editor()?.SetListMarker(ListMarker.LowerAlpha);

        [A_XSDActionDependency("List.LowerRoman", "Input", "Numbers the caret's list level i, ii, iii")]
        public static void MarkLowerRoman() => Editor()?.SetListMarker(ListMarker.LowerRoman);

        [A_XSDActionDependency("List.UpperRoman", "Input", "Numbers the caret's list level I, II, III")]
        public static void MarkUpperRoman() => Editor()?.SetListMarker(ListMarker.UpperRoman);

        [A_XSDActionDependency("List.ContinueNumbering", "Input", "Numbers the caret's list on from the list above it")]
        public static void ContinueNumbering() => Editor()?.ContinueNumbering();
        #endregion

        #region ---- picture wrap ----
        [A_XSDActionDependency("Picture.WrapInline", "Input", "Puts the selected picture in line with the text")]
        public static void WrapInline() => Editor()?.SetPictureWrap(PictureWrap.Inline);

        [A_XSDActionDependency("Picture.WrapSquare", "Input", "Wraps text around the selected picture's box")]
        public static void WrapSquare() => Editor()?.SetPictureWrap(PictureWrap.Square);

        [A_XSDActionDependency("Picture.WrapTight", "Input", "Wraps text around the selected picture's opaque outline")]
        public static void WrapTight() => Editor()?.SetPictureWrap(PictureWrap.Tight);

        [A_XSDActionDependency("Picture.WrapTopAndBottom", "Input", "Keeps text above and below the selected picture only")]
        public static void WrapTopAndBottom() => Editor()?.SetPictureWrap(PictureWrap.TopAndBottom);

        [A_XSDActionDependency("Picture.WrapBehind", "Input", "Puts the selected picture behind the text")]
        public static void WrapBehind() => Editor()?.SetPictureWrap(PictureWrap.Behind);

        [A_XSDActionDependency("Picture.WrapInFront", "Input", "Puts the selected picture in front of the text")]
        public static void WrapInFront() => Editor()?.SetPictureWrap(PictureWrap.InFront);

        [A_XSDActionDependency("Picture.CollideBox", "Input", "Wraps text around the turned picture's bounding box")]
        public static void CollideBox() => Editor()?.SetPictureCollision(PictureCollision.Box);

        [A_XSDActionDependency("Picture.CollideShape", "Input", "Wraps text around the turned picture's own shape")]
        public static void CollideShape() => Editor()?.SetPictureCollision(PictureCollision.Shape);
        #endregion

        #region ---- formulas ----
        [A_XSDActionDependency("Math.Insert", "Input", "Inserts an inline formula at the caret and opens its source")]
        public static void InsertFormula() => Editor()?.InsertFormula(false);

        [A_XSDActionDependency("Math.InsertDisplay", "Input", "Inserts a display formula at the caret and opens its source")]
        public static void InsertDisplayFormula() => Editor()?.InsertFormula(true);

        private static readonly (string, string)[] pictureFilters = { ("Pictures", "*.png;*.jpg;*.jpeg;*.bmp;*.gif") };

        [A_XSDActionDependency("Text.InsertPicture", "Input", "Picks a picture file and puts it in the focused note at the caret")]
        public static void InsertPicture()
        {
            DocumentEditorControl editor = Editor();
            if (editor?.path == null) return;

            FilePicker.Pick(UIEngine.WindowOf(editor), Path.GetDirectoryName(editor.path)!, pictureFilters, picked =>
            {
                if (picked == null || editor.destroyed) return;

                editor.FocusCaret();
                editor.InsertPictureFile(picked);
            });
        }

        [A_XSDActionDependency("Math.Edit", "Input", "Opens the selected formula's source")]
        public static void EditFormula() => Editor()?.EditFormula();
        #endregion

        #region ---- tables ----
        [A_XSDActionDependency("Table.Insert", "Input", "Inserts a 3x3 table after the caret's paragraph")]
        public static void InsertTable() => Editor()?.InsertTable();

        [A_XSDActionDependency("Table.RowAbove", "Input", "Inserts a row above the caret's")]
        public static void InsertRowAbove() => Editor()?.ChangeTable("Insert row", d => d.InsertTableRow(false));

        [A_XSDActionDependency("Table.RowBelow", "Input", "Inserts a row below the caret's")]
        public static void InsertRowBelow() => Editor()?.ChangeTable("Insert row", d => d.InsertTableRow(true));

        [A_XSDActionDependency("Table.ColumnLeft", "Input", "Inserts a column left of the caret's")]
        public static void InsertColumnLeft() => Editor()?.ChangeTable("Insert column", d => d.InsertTableColumn(false));

        [A_XSDActionDependency("Table.ColumnRight", "Input", "Inserts a column right of the caret's")]
        public static void InsertColumnRight() => Editor()?.ChangeTable("Insert column", d => d.InsertTableColumn(true));

        [A_XSDActionDependency("Table.DeleteRow", "Input", "Deletes the caret's row")]
        public static void DeleteRow() => Editor()?.ChangeTable("Delete row", d => d.DeleteTableRow());

        [A_XSDActionDependency("Table.DeleteColumn", "Input", "Deletes the caret's column")]
        public static void DeleteColumn() => Editor()?.ChangeTable("Delete column", d => d.DeleteTableColumn());

        [A_XSDActionDependency("Table.MergeRight", "Input", "Merges the caret's cell with the next one in its row")]
        public static void MergeCellRight() => Editor()?.ChangeTable("Merge cells", d => d.MergeTableCellRight());

        [A_XSDActionDependency("Table.Split", "Input", "Splits the caret's merged cell back into single cells")]
        public static void SplitCell() => Editor()?.ChangeTable("Split cell", d => d.SplitTableCell());

        [A_XSDActionDependency("Table.Delete", "Input", "Deletes the caret's table")]
        public static void DeleteTable() => Editor()?.ChangeTable("Delete table", d => d.DeleteTable());
        #endregion

        [A_XSDActionDependency("Text.Indent", "Input", "Nests the list items under the caret one level deeper")]
        public static void Indent() => Editor()?.ShiftListLevel(1);

        [A_XSDActionDependency("Text.Outdent", "Input", "Moves the list items under the caret one level out")]
        public static void Outdent() => Editor()?.ShiftListLevel(-1);

        // On over the selection unless all of it already is; off the caret's style with none.
        private static void Toggle(Func<StyleSpan, bool> spanOn, Func<CaretStyle, bool> caretOn, Func<bool, StyleDelta> delta)
        {
            DocumentEditorControl next = Editor();
            CaretStyle? nextSource = next?.StyleSource;
            if (nextSource == null) return;

            bool on = next.SelectionAll(spanOn) ?? caretOn(nextSource.Value);
            next.ApplyStyle(delta(!on));
        }

        [A_XSDActionDependency("Text.Undo", "Input", "Reverses the last edit made to the focused note")]
        public static void Undo()
        {
            DocumentEditorControl next = Editor();
            if (next != null) { next.Undo(); return; }

            Box()?.Undo();
        }

        [A_XSDActionDependency("Text.Redo", "Input", "Reapplies the last edit undone in the focused note")]
        public static void Redo()
        {
            DocumentEditorControl next = Editor();
            if (next != null) { next.Redo(); return; }

            Box()?.Redo();
        }

        [A_XSDActionDependency("Text.Cancel", "Input", "Abandons the edit in a standalone field and restores what it held")]
        public static void Cancel() => Box()?.Cancel();

        [A_XSDActionDependency("Text.Save", "Input", "Writes the focused note back to the file it was loaded from")]
        public static void Save() => Editor()?.Save();

        [A_XSDActionDependency("Notes.EncryptBlocks", "Input", "Asks for a password and encrypts the paragraphs and tables the selection touches")]
        public static void EncryptBlocks() => Editor()?.EncryptBlocks();

        [A_XSDActionDependency("Tex.ExportPdf", "Input", "Writes the focused LaTeX file's preview as a PDF beside it")]
        public static void ExportPdf()
        {
            for (ArctisAurora.Core.UI.Control? control = Editor(); control != null; control = control.parent as ArctisAurora.Core.UI.Control)
                if (control is TexEditorControl tex)
                {
                    tex.ExportPdf();
                    return;
                }
        }

        [A_XSDActionDependency("Text.CaretLeft", "Input")]
        public static void CaretLeft() => Move(CaretMove.Left);

        [A_XSDActionDependency("Text.CaretRight", "Input")]
        public static void CaretRight() => Move(CaretMove.Right);

        [A_XSDActionDependency("Text.CaretUp", "Input")]
        public static void CaretUp() => Move(CaretMove.Up);

        [A_XSDActionDependency("Text.CaretDown", "Input")]
        public static void CaretDown() => Move(CaretMove.Down);

        [A_XSDActionDependency("Text.CaretLineStart", "Input")]
        public static void CaretLineStart() => Move(CaretMove.LineStart);

        [A_XSDActionDependency("Text.CaretLineEnd", "Input")]
        public static void CaretLineEnd() => Move(CaretMove.LineEnd);

        [A_XSDActionDependency("Text.CaretPageUp", "Input")]
        public static void CaretPageUp() => Move(CaretMove.PageUp);

        [A_XSDActionDependency("Text.CaretPageDown", "Input")]
        public static void CaretPageDown() => Move(CaretMove.PageDown);

        // The Extend modifier keeps the anchor instead of collapsing it onto the new position; the
        // Word modifier turns character moves into word moves and line ends into document ends.
        private static void Move(CaretMove move)
        {
            if (InputHandler.instance.IsModifierDown(InputModifier.Word))
                move = move switch
                {
                    CaretMove.Left => CaretMove.WordLeft,
                    CaretMove.Right => CaretMove.WordRight,
                    CaretMove.LineStart => CaretMove.DocumentStart,
                    CaretMove.LineEnd => CaretMove.DocumentEnd,
                    _ => move
                };

            DocumentEditorControl next = Editor();
            if (next != null)
            {
                next.DisarmStyle();
                next.MoveCaret(move, InputHandler.instance.IsModifierDown(InputModifier.Extend));
                return;
            }

            Box()?.MoveCaret(move, InputHandler.instance.IsModifierDown(InputModifier.Extend));
        }

        #region ---- word boundaries ----
        internal enum CharClass { Space, Word, Symbol }

        internal static CharClass ClassOf(char c) =>
            char.IsWhiteSpace(c) ? CharClass.Space
            : char.IsLetterOrDigit(c) || c == '_' ? CharClass.Word
            : CharClass.Symbol;

        // Where a word move from an offset lands inside one string.
        internal static int WordEdge(string s, int offset, int direction)
        {
            int i = Math.Clamp(offset, 0, s.Length);

            if (direction > 0)
            {
                if (i < s.Length && ClassOf(s[i]) is CharClass run && run != CharClass.Space)
                    while (i < s.Length && ClassOf(s[i]) == run) i++;
                while (i < s.Length && ClassOf(s[i]) == CharClass.Space) i++;
                return i;
            }

            while (i > 0 && ClassOf(s[i - 1]) == CharClass.Space) i--;
            if (i > 0)
            {
                CharClass run = ClassOf(s[i - 1]);
                while (i > 0 && ClassOf(s[i - 1]) == run) i--;
            }
            return i;
        }
        #endregion

        // Nearest editor at or above the active control; none past an editing field.
        internal static DocumentEditorControl Editor()
        {
            for (ArctisAurora.Core.UI.Control control = UIEngine.activeControl;
                 control != null;
                 control = control.parent as ArctisAurora.Core.UI.Control)
            {
                if (control is ITextField { isEditing: true }) return null;
                if (control is DocumentEditorControl editor) return editor;
            }

            return null;
        }

        // Nearest editing field at or above whatever the new stack last made active.
        private static ITextField Box()
        {
            for (ArctisAurora.Core.UI.Control control = UIEngine.activeControl;
                 control != null;
                 control = control.parent as ArctisAurora.Core.UI.Control)
                if (control is ITextField box) return box.isEditing ? box : null;

            return null;
        }
    }
}
