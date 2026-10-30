using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Text;
using System.Numerics;

namespace ArctisAurora.Core.UI.Planner
{
    // A category's name and colour in a popup; Enter or Apply writes them as one step, Delete removes the category.
    internal sealed class PlannerCategoryPopup
    {
        // popup sizing
        private const float width = 300f;
        private const float rowHeight = 26f;
        private const float captionWidth = 70f;
        private const int fontSize = 14;

        private readonly PlannerEditorControl editor;
        private readonly Control from;
        private readonly PlannerCategory? category;
        private readonly TextBoxControl name = new TextBoxControl();
        private string colorHex;
        private bool done;

        private PlannerCategoryPopup(PlannerEditorControl editor, Control from, PlannerCategory? category)
        {
            this.editor = editor;
            this.from = from;
            this.category = category;
            colorHex = category?.colorHex ?? PlannerDocument.defaultColor;
        }

        // Opens on a category, or on a new one when category is null.
        public static void Open(PlannerEditorControl editor, Control from, Vector2 point, PlannerCategory? category) =>
            new PlannerCategoryPopup(editor, from, category).Show(point);

        private void Show(Vector2 point)
        {
            StackPanelControl content = new StackPanelControl { alpha = 0f, Spacing = 4f, horizontalAlignment = HorizontalAlignment.Stretch };

            name.text = category?.name ?? "New category";
            name.preferredHeight = rowHeight;
            name.fontSize = fontSize;
            name.role = PaletteRole.Field;
            name.widthStar = 1f;
            name.onCommit = _ => Finish();
            name.onCancel = Cancel;
            content.AddChild(Row("Name", name));

            ColorPickerControl picker = new ColorPickerControl { hex = colorHex };
            picker.onPicked = hex => colorHex = hex;
            content.AddChild(Row("Colour", picker));

            StackPanelControl buttons = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Horizontal,
                Spacing = 4f,
                alpha = 0f,
                preferredHeight = rowHeight,
                horizontalAlignment = HorizontalAlignment.Stretch
            };
            if (category != null && editor.document.categories.Count > 1)
                buttons.AddChild(Button("Delete", PaletteRole.SubField, Delete));
            buttons.AddChild(new PanelControl { widthStar = 1f, alpha = 0f, hitTestable = false });
            buttons.AddChild(Button("Apply", PaletteRole.Accent, Finish));
            content.AddChild(buttons);

            ContextMenus.Open(new List<ContextMenuEntry> { new ContextMenuContent(content) }, from, point, width, onClosed: Cancel);
            UIEngine.SetActiveControl(name);
            name.Focus();
        }

        private static Control Row(string caption, Control field)
        {
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
                preferredHeight = rowHeight,
                verticalPosition = 0.5f
            });
            row.AddChild(field);
            return row;
        }

        private static ButtonControl Button(string text, PaletteRole role, Action press)
        {
            ButtonControl button = new ButtonControl { preferredHeight = rowHeight, padding = new Thickness(12f, 0f) };
            button.PaintOr(null, role);
            button.AddChild(new LabelControl { text = text, fontSize = fontSize, role = PaletteRole.Ink, hitTestable = false, verticalPosition = 0.5f });
            button.RegisterOnPress(e =>
            {
                if (e.button != PointerEvent.leftButton) return false;
                press();
                return true;
            });
            return button;
        }

        private void Finish()
        {
            if (done) return;
            Close();
            string trimmed = name.text.Trim();
            editor.ApplyCategory(category, trimmed.Length > 0 ? trimmed : category?.name ?? "New category", colorHex);
        }

        private void Delete()
        {
            if (done || category == null) return;
            Close();
            editor.DeleteCategory(category);
        }

        private void Cancel()
        {
            if (done) return;
            Close();
        }

        private void Close()
        {
            done = true;
            ContextMenus.Close();
            UIEngine.SetActiveControl(from);
        }
    }
}
