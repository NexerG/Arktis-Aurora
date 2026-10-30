using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Text;
using System.Numerics;

namespace ArctisAurora.Core.UI.Sheets
{
    // The page tabs under a sheet: one per page, the shown one lit, a button adding a page, and the layer panel's button.
    public class SheetPageStripControl : StackPanelControl
    {
        public const float stripHeight = 26f;
        private const int fontSize = 13;
        private const float tabInset = 12f;
        private const float menuInset = 4f;

        private readonly SheetEditorControl editor;
        private readonly StackPanelControl row = new StackPanelControl { orientation = Orientation.Horizontal, Spacing = 1f, alpha = 0f };

        // the pages and names the tabs were built from
        private readonly List<(SheetPage page, string name)> built = new List<(SheetPage, string)>();
        private readonly List<PageTab> tabs = new List<PageTab>();

        static SheetPageStripControl() => ContextMenus.Register("sheet-page", new ContextMenu
        {
            entries =
            {
                new ContextMenuButton("Rename", () => TargetTab()?.BeginRename()),
                new ContextMenuButton("Delete", () => { if (TargetTab() is PageTab tab) tab.strip.editor.DeletePage(tab.index); }),
                new ContextMenuButton("Export as CSV", () => { if (TargetTab() is PageTab tab) tab.strip.editor.ExportPage(tab.index); })
            }
        });

        public SheetPageStripControl(SheetEditorControl editor)
        {
            this.editor = editor;
            orientation = Orientation.Horizontal;
            Spacing = 1f;
            preferredHeight = stripHeight;
            horizontalAlignment = HorizontalAlignment.Stretch;
            stopsContextMenu = true;
            PaintOr(null, PaletteRole.Chrome);

            ButtonControl add = new ButtonControl { preferredHeight = stripHeight, padding = new Thickness(tabInset, 0f) };
            add.PaintOr(null, PaletteRole.Chrome);
            add.AddChild(new LabelControl { text = "+", fontSize = fontSize, role = PaletteRole.MutedInk, hitTestable = false, verticalPosition = 0.5f });
            add.RegisterOnPress(e =>
            {
                if (e.button != PointerEvent.leftButton) return false;
                editor.AddPage();
                return true;
            });

            ButtonControl layers = new ButtonControl { preferredHeight = stripHeight, padding = new Thickness(tabInset, 0f) };
            layers.PaintOr(null, PaletteRole.Chrome);
            layers.AddChild(new LabelControl { text = "Layers", fontSize = fontSize, role = PaletteRole.MutedInk, hitTestable = false, verticalPosition = 0.5f });
            layers.RegisterOnPress(e =>
            {
                if (e.button != PointerEvent.leftButton) return false;
                OpenLayers(layers);
                return true;
            });

            AddChild(row);
            AddChild(add);
            AddChild(new PanelControl { widthStar = 1f, alpha = 0f, hitTestable = false });
            AddChild(layers);
        }

        // The layer panel, opened upward from the button so it stays over the sheet.
        private void OpenLayers(ButtonControl owner)
        {
            SheetLayersControl panel = new SheetLayersControl(editor);
            float height = panel.Measure(new Vector2(SheetLayersControl.panelWidth, float.MaxValue)).Y + menuInset * 2f;
            float width = SheetLayersControl.panelWidth + menuInset * 2f;
            ContextMenus.Open(new List<ContextMenuEntry> { new ContextMenuContent(panel) }, owner,
                new Vector2(owner.arrangedRect.Right - width, owner.arrangedRect.y - height), width);
        }

        // Rebuilds the tabs when pages or names moved; otherwise only relights the shown one.
        public void Sync()
        {
            List<SheetPage> pages = editor.document.pages;
            bool same = built.Count == pages.Count;
            for (int i = 0; same && i < pages.Count; i++)
                same = ReferenceEquals(built[i].page, pages[i]) && built[i].name == pages[i].name;

            if (!same) Rebuild(pages);
            for (int i = 0; i < tabs.Count; i++)
                tabs[i].Light(i == editor.pageIndex);
        }

        private void Rebuild(List<SheetPage> pages)
        {
            foreach (PageTab tab in tabs)
                tab.Destroy();
            built.Clear();
            tabs.Clear();

            for (int i = 0; i < pages.Count; i++)
            {
                built.Add((pages[i], pages[i].name));
                PageTab tab = new PageTab(this, i, pages[i].name);
                tabs.Add(tab);
                row.AddChild(tab);
            }
        }

        private static PageTab? TargetTab()
        {
            for (Control? control = ContextMenus.target; control != null; control = control.parent as Control)
                if (control is PageTab tab) return tab;
            return null;
        }

        // One page's tab: a press shows it, a double click or the menu renames it.
        private sealed class PageTab : ButtonControl
        {
            public readonly SheetPageStripControl strip;
            public readonly int index;
            private readonly EditableLabelControl caption;
            private bool? lit;

            public PageTab(SheetPageStripControl strip, int index, string name)
            {
                this.strip = strip;
                this.index = index;
                contextMenu = "sheet-page";
                stopsContextMenu = true;
                preferredHeight = stripHeight;
                padding = new Thickness(tabInset, 0f);

                caption = new EditableLabelControl { text = name, fontSize = fontSize, verticalPosition = 0.5f };
                AddChild(caption);
                RegisterOnTap(e =>
                {
                    if (e.tapCount != 2) return false;
                    BeginRename();
                    return true;
                });
            }

            public void Light(bool on)
            {
                if (lit == on) return;
                lit = on;
                PaintOr(null, on ? PaletteRole.Surface : PaletteRole.Chrome);
                caption.PaintText(null, on ? PaletteRole.Ink : PaletteRole.MutedInk);
            }

            public void BeginRename() => caption.BeginEdit(name => strip.editor.RenamePage(index, name));

            public override bool OnPointerPress(PointerEvent e)
            {
                base.OnPointerPress(e);
                if (e.button == PointerEvent.leftButton) strip.editor.ShowPage(index);
                return true;
            }
        }
    }
}
