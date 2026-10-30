using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Text;
using System.Globalization;
using System.Numerics;

namespace ArctisAurora.Core.UI.Sheets
{
    // Columns and rows to add to a fixed sheet, asked in a popup at the + edge that was right-clicked.
    internal sealed class SheetGrowPopup
    {
        // popup sizing
        private const float width = 240f;
        private const float rowHeight = 26f;
        private const float captionWidth = 110f;
        private const int fontSize = 14;

        private readonly SheetEditorControl editor;
        private readonly Control from;
        private readonly TextBoxControl horizontal = new TextBoxControl();
        private readonly TextBoxControl vertical = new TextBoxControl();
        private bool done;

        // the popup being edited; null when none is open
        private static SheetGrowPopup? open;

        private SheetGrowPopup(SheetEditorControl editor, Control from)
        {
            this.editor = editor;
            this.from = from;
        }

        // Opens with the rows box focused when vertical, else the columns box.
        public static void Open(SheetEditorControl editor, Control from, Vector2 point, bool vertical) =>
            new SheetGrowPopup(editor, from).Show(point, vertical);

        // Focuses the other box; false when no popup is open.
        public static bool Tab()
        {
            if (open == null || open.done) return false;
            TextBoxControl next = ReferenceEquals(UIEngine.activeControl, open.horizontal) ? open.vertical : open.horizontal;
            UIEngine.SetActiveControl(next);
            next.Focus();
            return true;
        }

        private void Show(Vector2 point, bool rows)
        {
            StackPanelControl content = new StackPanelControl { alpha = 0f, horizontalAlignment = HorizontalAlignment.Stretch };
            content.AddChild(Row("Add horizontal", horizontal));
            content.AddChild(Row("Add vertical", vertical));
            ContextMenus.Open(new List<ContextMenuEntry> { new ContextMenuContent(content) }, from, point, width, onClosed: Cancel);

            TextBoxControl first = rows ? vertical : horizontal;
            UIEngine.SetActiveControl(first);
            first.Focus();
            open = this;
        }

        private Control Row(string caption, TextBoxControl box)
        {
            box.text = "0";
            box.preferredHeight = rowHeight;
            box.fontSize = fontSize;
            box.role = PaletteRole.Field;
            box.widthStar = 1f;
            box.onCommit = _ => Finish();
            box.onCancel = Cancel;

            StackPanelControl row = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Horizontal,
                alpha = 0f,
                horizontalAlignment = HorizontalAlignment.Stretch
            };
            row.AddChild(new LabelControl
            {
                text = caption,
                fontSize = fontSize,
                role = PaletteRole.MutedInk,
                hitTestable = false,
                preferredWidth = captionWidth,
                verticalPosition = 0.5f
            });
            row.AddChild(box);
            return row;
        }

        private void Finish()
        {
            if (done) return;
            Close();
            editor.Grow(Count(vertical), Count(horizontal));
        }

        private void Cancel()
        {
            if (done) return;
            Close();
        }

        private void Close()
        {
            done = true;
            if (open == this) open = null;
            ContextMenus.Close();
            UIEngine.SetActiveControl(from);
        }

        private static int Count(TextBoxControl box) =>
            int.TryParse(box.text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0;
    }
}
