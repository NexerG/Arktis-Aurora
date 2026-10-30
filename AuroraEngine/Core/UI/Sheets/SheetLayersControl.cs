using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;

namespace ArctisAurora.Core.UI.Sheets
{
    // The shown page's layers, top first: a visibility toggle and a name per row, the edited one lit; then add and delete.
    public class SheetLayersControl : StackPanelControl
    {
        public const float panelWidth = 200f;
        private const float rowHeight = 24f;
        private const float iconSize = 12f;
        private const int fontSize = 13;

        private readonly SheetEditorControl editor;
        private readonly StackPanelControl rows = new StackPanelControl { alpha = 0f, horizontalAlignment = HorizontalAlignment.Stretch };

        // the layers the rows were built from, top first, and each row's parts
        private readonly List<SheetLayer> built = new List<SheetLayer>();
        private readonly List<(IconControl eye, ButtonControl name, LabelControl caption)> parts = new List<(IconControl, ButtonControl, LabelControl)>();

        public SheetLayersControl(SheetEditorControl editor)
        {
            this.editor = editor;
            preferredWidth = panelWidth;
            alpha = 0f;

            AddChild(rows);
            AddChild(Action("Add layer", editor.AddLayer));
            AddChild(Action("Delete layer", editor.DeleteLayer));
            Sync();
        }

        // Rebuilds the rows when layers were added or removed; otherwise only repaints them.
        public void Sync()
        {
            List<SheetLayer> layers = editor.page.layers;
            bool same = built.Count == layers.Count;
            for (int i = 0; same && i < layers.Count; i++)
                same = ReferenceEquals(built[i], layers[layers.Count - 1 - i]);
            if (!same) Rebuild(layers);

            for (int i = 0; i < built.Count; i++)
            {
                (IconControl eye, ButtonControl name, LabelControl caption) = parts[i];
                bool lit = ReferenceEquals(built[i], editor.editLayer);
                eye.iconName = built[i].visible ? "bullet-disc" : "bullet-circle";
                name.PaintOr(null, lit ? PaletteRole.Surface : PaletteRole.Clear);
                caption.PaintOr(null, lit ? PaletteRole.Ink : PaletteRole.MutedInk);
            }
        }

        private void Rebuild(List<SheetLayer> layers)
        {
            for (int i = rows.children.Count - 1; i >= 0; i--)
                rows.children[i].Destroy();
            built.Clear();
            parts.Clear();

            for (int i = layers.Count - 1; i >= 0; i--)
            {
                SheetLayer layer = layers[i];
                StackPanelControl row = new StackPanelControl
                {
                    orientation = Orientation.Horizontal,
                    alpha = 0f,
                    horizontalAlignment = HorizontalAlignment.Stretch
                };

                IconControl eye = new IconControl
                {
                    setName = "default",
                    preferredWidth = iconSize,
                    preferredHeight = iconSize,
                    hitTestable = false,
                    horizontalPosition = 0.5f,
                    verticalPosition = 0.5f
                };
                ButtonControl toggle = Pressable(() => editor.ToggleLayer(layer));
                toggle.preferredWidth = rowHeight;
                toggle.AddChild(eye);

                LabelControl caption = Caption(layer.name);
                ButtonControl name = Pressable(() => editor.EditLayer(layer));
                name.widthStar = 1f;
                name.padding = new Thickness(6f, 0f);
                name.AddChild(caption);

                row.AddChild(toggle);
                row.AddChild(name);
                rows.AddChild(row);
                built.Add(layer);
                parts.Add((eye, name, caption));
            }
        }

        private ButtonControl Action(string text, Action act)
        {
            ButtonControl button = Pressable(act);
            button.horizontalAlignment = HorizontalAlignment.Stretch;
            button.padding = new Thickness(0f, 6f, 0f, 6f + rowHeight);
            button.AddChild(Caption(text));
            return button;
        }

        // A row-high button acting on a left press, then repainting the panel.
        private ButtonControl Pressable(Action act)
        {
            ButtonControl button = new ButtonControl { preferredHeight = rowHeight };
            button.PaintOr(null, PaletteRole.Clear);
            button.RegisterOnPress(e =>
            {
                if (e.button != PointerEvent.leftButton) return false;
                act();
                Sync();
                return true;
            });
            return button;
        }

        private static LabelControl Caption(string text) => new LabelControl
        {
            text = text,
            fontSize = fontSize,
            role = PaletteRole.MutedInk,
            hitTestable = false,
            horizontalPosition = 0f,
            verticalPosition = 0.5f
        };
    }
}
