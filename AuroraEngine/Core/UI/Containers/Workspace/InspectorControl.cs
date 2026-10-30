using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.Core.UI.Text;

namespace ArctisAurora.Core.UI.Containers.Workspace
{
    // The column beside a Docs or Sheets workspace's panes: Paragraph and Page for a note, Cell and Layers
    // for a sheet, all reflecting and acting on the ribbon's target editor.
    [A_XSDType("Inspector", "UI")]
    public class InspectorControl : StackPanelControl
    {
        // metrics
        private const float columnWidth = 240f;
        private const float headerHeight = 24f;
        private const float labelWidth = 76f;
        private const int captionSize = 12;

        private WorkspaceControl? workspace;
        private readonly StackPanelControl docs;
        private readonly StackPanelControl sheets;

        // Paragraph parts
        private readonly LabelControl styleCaption;
        private readonly IconControl alignLeft, alignCenter, alignRight, alignJustify;
        private readonly SwitchControl listShape;
        private readonly LabelControl checkedCaption;
        private readonly SwitchControl checkedEnd;
        private TextStyleType? shownStyle;
        private bool? shownLeft, shownCenter, shownRight, shownJustify;
        private ListShape? shownList;
        private SortedTasksSetting.CheckedEnd? shownChecked;

        private enum ListShape { None, Bullets, Numbers, Tasks, SortedTasks }

        // Page parts
        private readonly LabelControl paged, pageless, sizeCaption, portrait, landscape;
        private readonly TextBoxControl[] margins = new TextBoxControl[4];
        private readonly PanelControl numbersMark;
        private (PageMode, PageSize, bool, bool, float, float, float, float, float, float)? shownPage;

        // Cell parts
        private readonly LabelControl cellName, numberCaption, reference;
        private readonly PanelControl boldMark;
        private (SheetEditorControl, int, int, SheetFormat, string)? shownCell;

        // Layers, rebuilt for each sheet
        private readonly StackPanelControl layersHost;
        private SheetLayersControl? layers;
        private (SheetEditorControl, SheetPage)? layersFor;

        // Links, rebuilt for each target and after it saves
        private readonly LinksPanelControl links;
        private string? linksFor;
        private bool linksDirty;

        public override bool takesActiveControl => false;

        public InspectorControl()
        {
            SetTicking(true);
            orientation = Orientation.Vertical;
            preferredWidth = columnWidth;
            verticalAlignment = VerticalAlignment.Stretch;
            PaintOr(null, PaletteRole.Surface);

            docs = new StackPanelControl { alpha = 0f, horizontalAlignment = HorizontalAlignment.Stretch };
            sheets = new StackPanelControl { alpha = 0f, horizontalAlignment = HorizontalAlignment.Stretch };
            AddChild(docs);
            AddChild(sheets);

            #region docs
            docs.AddChild(Header("Paragraph", out _));
            StackPanelControl paragraph = Section();
            docs.AddChild(paragraph);

            TabToolsControl style = Tools();
            styleCaption = style.Text("Text", owner => DropNote(owner, DocumentToolbarControl.StylingEntries), true);
            paragraph.AddChild(Row("Style", style));

            TabToolsControl align = Tools();
            alignLeft = align.Icon("align-left", _ => Note(editor => editor.SetAlignment(TextAlignment.Left)));
            alignCenter = align.Icon("align-center", _ => Note(editor => editor.SetAlignment(TextAlignment.Center)));
            alignRight = align.Icon("align-right", _ => Note(editor => editor.SetAlignment(TextAlignment.Right)));
            alignJustify = align.Icon("align-justify", _ => Note(editor => editor.SetAlignment(TextAlignment.Justify)));
            paragraph.AddChild(Row("Align", align));

            listShape = new SwitchControl { options = new[] { "None", "Bullets", "Numbers", "Tasks", "Sorted" }, columns = 3 };
            listShape.onChanged = index =>
            {
                Note(editor => SetList(editor, (ListShape)index));
                shownList = null;
            };
            paragraph.AddChild(Caption("List", PaletteRole.MutedInk));
            paragraph.AddChild(listShape);

            checkedCaption = Caption("Checked", PaletteRole.MutedInk);
            checkedEnd = new SwitchControl { options = new[] { "Bottom", "Top" } };
            checkedEnd.onChanged = index =>
            {
                SettingsRegistry.Get<UISettings>().sortedTasks.checkedEnd = (SortedTasksSetting.CheckedEnd)index;
                SettingsRegistry.Commit();
                Note(editor => editor.SortCaretChecklist());
                shownChecked = null;
            };
            paragraph.AddChild(checkedCaption);
            paragraph.AddChild(checkedEnd);

            docs.AddChild(Header("Page", out _));
            StackPanelControl page = Section();
            docs.AddChild(page);

            TabToolsControl layout = Tools();
            paged = layout.Text("Paged", _ => SetPage(p => p.mode = PageMode.Paged));
            pageless = layout.Text("Pageless", _ => SetPage(p => p.mode = PageMode.Pageless));
            page.AddChild(Row("Layout", layout));

            TabToolsControl size = Tools();
            sizeCaption = size.Text("A4", owner => DropPage(owner, DocumentToolbarControl.SizeEntries), true);
            page.AddChild(Row("Size", size));

            TabToolsControl orientationTools = Tools();
            portrait = orientationTools.Text("Portrait", _ => SetPage(p => p.landscape = false));
            landscape = orientationTools.Text("Landscape", _ => SetPage(p => p.landscape = true));
            page.AddChild(Row("Orientation", orientationTools));

            var sides = RibbonControl.MarginSides();
            for (int pair = 0; pair < 2; pair++)
            {
                StackPanelControl fields = new StackPanelControl { orientation = Orientation.Horizontal, Spacing = 4f, alpha = 0f };
                for (int i = pair * 2; i < pair * 2 + 2; i++)
                {
                    (string side, _, Action<PageLayout, float> set) = sides[i];
                    fields.AddChild(Caption(side, PaletteRole.MutedInk));
                    margins[i] = RibbonControl.MarginField(NoteTarget, set);
                    fields.AddChild(margins[i]);
                }
                page.AddChild(Row(pair == 0 ? "Margins" : "", fields));
            }

            numbersMark = Check(out TabToolsControl.TabToolButton numbers, () => SetPage(p => p.pageNumbers = !p.pageNumbers));
            page.AddChild(Row("Numbers", numbers));
            #endregion

            #region sheets
            sheets.AddChild(Header("Cell", out cellName));
            StackPanelControl cell = Section();
            sheets.AddChild(cell);

            TabToolsControl number = Tools();
            numberCaption = number.Text("General", owner => TabToolsControl.Drop(owner, RibbonControl.NumberEntries(SheetTarget)), true);
            cell.AddChild(Row("Format", number));

            StackPanelControl fills = new StackPanelControl { alpha = 0f, Spacing = 2f };
            TabToolsControl fillRow = Tools();
            for (int i = 0; i < DocumentToolbarControl.highlightOptions.Length; i++)
            {
                if (i == 4) { fills.AddChild(fillRow); fillRow = Tools(); }
                string hex = DocumentToolbarControl.highlightOptions[i].hex;
                fillRow.AddChild(RibbonControl.Swatch(hex, () => Sheet(editor => editor.SetFill(hex))));
            }
            fills.AddChild(fillRow);
            cell.AddChild(Row("Fill", fills));

            boldMark = Check(out TabToolsControl.TabToolButton bold, () => Sheet(editor => editor.ToggleBold()));
            cell.AddChild(Row("Bold", bold));

            TabToolsControl referenceTools = Tools();
            reference = Caption("", PaletteRole.Ink);
            referenceTools.AddChild(reference);
            referenceTools.Text("Copy", _ => Sheet(editor => editor.Copy()));
            cell.AddChild(Row("Reference", referenceTools));

            sheets.AddChild(Header("Layers", out _));
            layersHost = new StackPanelControl { alpha = 0f, horizontalAlignment = HorizontalAlignment.Stretch, padding = new Thickness(4f) };
            sheets.AddChild(layersHost);
            #endregion

            AddChild(Header("Links", out _));
            links = new LinksPanelControl();
            AddChild(links);

            SheetBook.changed += BookChanged;
            Hide();
        }

        public override void OnDestroy()
        {
            SheetBook.changed -= BookChanged;
            base.OnDestroy();
        }

        private void BookChanged(SheetDocument? document) => layers?.Sync();

        #region ---- target ----
        private WorkspacePageControl? Page()
        {
            if (workspace == null || workspace.destroyed)
            {
                Control root = this;
                while (root.parent is Control up) root = up;
                workspace = WorkspaceControl.In(root);
            }
            return workspace?.shown;
        }

        // The splitter right before the inspector, which shows and hides with it.
        private SplitterControl? Grip()
        {
            if (parent is not Control host) return null;

            int index = host.children.IndexOf(this);
            return index > 0 ? host.children[index - 1] as SplitterControl : null;
        }

        private DocumentEditorControl? NoteTarget() => RibbonControl.Target(Page()) as DocumentEditorControl;

        private SheetEditorControl? SheetTarget() => RibbonControl.Target(Page()) as SheetEditorControl;

        private void Note(Action<DocumentEditorControl> action)
        {
            DocumentEditorControl? editor = NoteTarget();
            if (editor == null) return;

            if (!ReferenceEquals(TextInputActions.Editor(), editor)) editor.FocusCaret();
            action(editor);
        }

        private void Sheet(Action<SheetEditorControl> action)
        {
            SheetEditorControl? editor = SheetTarget();
            if (editor?.document != null) editor.On(() => action(editor));
        }

        private void SetPage(Action<PageLayout> change)
        {
            DocumentEditorControl? editor = NoteTarget();
            PageLayout? current = editor?.Page;
            if (editor != null && current != null) DocumentToolbarControl.ChangePage(editor, current, change);
        }

        private void DropNote(Control owner, Func<DocumentEditorControl, List<ContextMenuEntry>> entries)
        {
            DocumentEditorControl? editor = NoteTarget();
            if (editor != null) TabToolsControl.Drop(owner, entries(editor));
        }

        private void DropPage(Control owner, Func<DocumentEditorControl, PageLayout, List<ContextMenuEntry>> entries)
        {
            DocumentEditorControl? editor = NoteTarget();
            PageLayout? current = editor?.Page;
            if (editor != null && current != null) TabToolsControl.Drop(owner, entries(editor, current));
        }
        #endregion

        public override void OnTick()
        {
            base.OnTick();

            WorkspacePageControl? page = Page();
            if (page == null || !page.inspectorShown || page.kind is not (WorkspaceKind.Docs or WorkspaceKind.Sheets))
            {
                Hide();
                Grip()?.Hide();
                return;
            }
            Show();
            Grip()?.Show();

            if (page.kind == WorkspaceKind.Docs)
            {
                docs.Show();
                sheets.Hide();
                ReflectNote(NoteTarget());
            }
            else
            {
                sheets.Show();
                docs.Hide();
                ReflectSheet(SheetTarget());
            }
            ReflectLinks(RibbonControl.Target(page) as IFileEditor);
        }

        #region ---- reflection ----
        private void ReflectLinks(IFileEditor? editor)
        {
            string? path = editor?.path;
            bool dirty = editor?.isDirty == true;
            bool saved = linksDirty && !dirty;
            linksDirty = dirty;
            if (path == linksFor && !saved) return;

            linksFor = path;
            links.Show(path);
        }

        private void ReflectNote(DocumentEditorControl? editor)
        {
            TextStyleType styling = editor?.CaretBlockStyling ?? TextStyleType.Text;
            if (shownStyle != styling)
            {
                shownStyle = styling;
                styleCaption.text = DocumentToolbarControl.CaptionFor(styling);
            }

            TextAlignment? alignment = editor?.CanAlign == true ? editor.CaretBlockAlignment : null;
            TabToolsControl.Light(alignLeft, alignment == TextAlignment.Left, ref shownLeft);
            TabToolsControl.Light(alignCenter, alignment == TextAlignment.Center, ref shownCenter);
            TabToolsControl.Light(alignRight, alignment == TextAlignment.Right, ref shownRight);
            TabToolsControl.Light(alignJustify, alignment == TextAlignment.Justify, ref shownJustify);

            ListShape list = ShapeOf(editor);
            if (shownList != list)
            {
                shownList = list;
                if (listShape.selected != (int)list) listShape.selected = (int)list;
                if (list == ListShape.SortedTasks) { checkedCaption.Show(); checkedEnd.Show(); }
                else { checkedCaption.Hide(); checkedEnd.Hide(); }
            }

            SortedTasksSetting.CheckedEnd end = SettingsRegistry.Get<UISettings>().sortedTasks.checkedEnd;
            if (shownChecked != end)
            {
                shownChecked = end;
                if (checkedEnd.selected != (int)end) checkedEnd.selected = (int)end;
            }

            PageLayout? page = editor?.Page;
            var state = page == null ? ((PageMode, PageSize, bool, bool, float, float, float, float, float, float)?)null
                : (page.mode, page.size, page.landscape, page.pageNumbers, page.width, page.height,
                   page.marginTop, page.marginBottom, page.marginLeft, page.marginRight);
            if (shownPage == state) return;

            shownPage = state;
            RibbonControl.LightCaption(paged, page?.mode == PageMode.Paged);
            RibbonControl.LightCaption(pageless, page?.mode == PageMode.Pageless);
            RibbonControl.LightCaption(portrait, page != null && !page.landscape);
            RibbonControl.LightCaption(landscape, page?.landscape == true);
            Mark(numbersMark, page?.pageNumbers == true);
            sizeCaption.text = SizeCaption(page);

            var sides = RibbonControl.MarginSides();
            for (int i = 0; i < margins.Length; i++)
                if (!margins[i].isEditing) margins[i].text = page == null ? string.Empty : RibbonControl.Mm(sides[i].Item2(page));
        }

        private static ListShape ShapeOf(DocumentEditorControl? editor)
        {
            BlockControl? block = editor?.CaretBlock;
            return block?.listKind switch
            {
                ListKind.Task => ListShape.Tasks,
                ListKind.SortedTask => ListShape.SortedTasks,
                ListKind.Bullet => ListMarkers.IsNumbered(block.listMarker ?? ListMarker.Disc) ? ListShape.Numbers : ListShape.Bullets,
                _ => ListShape.None
            };
        }

        // Turns the caret's paragraphs into the shape picked; picking the shape they have changes nothing.
        private static void SetList(DocumentEditorControl editor, ListShape wanted)
        {
            ListShape current = ShapeOf(editor);
            if (current == wanted) return;

            if (wanted == ListShape.Bullets && current == ListShape.Numbers) editor.SetListMarker(ListMarker.Disc);
            else if (wanted == ListShape.Bullets) editor.ToggleBullets();
            else if (wanted == ListShape.Numbers) editor.ToggleNumbers();
            else if (wanted == ListShape.Tasks) editor.ToggleTasks();
            else if (wanted == ListShape.SortedTasks) editor.ToggleSortedTasks();
            else if (current == ListShape.Numbers) editor.ToggleNumbers();
            else if (current == ListShape.Bullets) editor.ToggleBullets();
            else if (current == ListShape.SortedTasks) editor.ToggleSortedTasks();
            else editor.ToggleTasks();
        }

        private static string SizeCaption(PageLayout? page)
        {
            if (page == null || page.size == PageSize.Custom) return RibbonControl.SizeCaption(page);

            System.Numerics.Vector2 mm = page.SizePx() / PageLayout.PxPerMm;
            return $"{page.size}, {RibbonControl.Mm(mm.X)} x {RibbonControl.Mm(mm.Y)}";
        }

        private void ReflectSheet(SheetEditorControl? editor)
        {
            bool live = editor?.document != null;
            if (live && (layersFor == null || !ReferenceEquals(layersFor.Value.Item1, editor) || !ReferenceEquals(layersFor.Value.Item2, editor!.page)))
            {
                if (layersFor == null || !ReferenceEquals(layersFor.Value.Item1, editor))
                {
                    layers?.Destroy();
                    layers = new SheetLayersControl(editor!);
                    layersHost.AddChild(layers);
                }
                else layers!.Sync();
                layersFor = (editor!, editor!.page);
            }
            else if (!live && layers != null)
            {
                layers.Destroy();
                layers = null;
                layersFor = null;
            }

            if (!live)
            {
                if (shownCell == null) return;
                shownCell = null;
                cellName.text = numberCaption.text = reference.text = string.Empty;
                Mark(boldMark, false);
                return;
            }

            SheetFormat format = editor!.page.Format(editor.activeRow, editor.activeColumn);
            string link = editor.page.name + "!" + SheetDocument.Address(editor.activeRow, editor.activeColumn);
            var state = (editor, editor.activeRow, editor.activeColumn, format, link);
            if (shownCell == state) return;

            shownCell = state;
            cellName.text = SheetDocument.Address(editor.activeRow, editor.activeColumn);
            numberCaption.text = RibbonControl.NumberCaption(format.number);
            reference.text = link;
            Mark(boldMark, format.bold);
        }
        #endregion

        #region ---- parts ----
        private static StackPanelControl Header(string title, out LabelControl detail)
        {
            StackPanelControl header = new StackPanelControl
            {
                orientation = Orientation.Horizontal,
                Spacing = 6f,
                preferredHeight = headerHeight,
                horizontalAlignment = HorizontalAlignment.Stretch,
                padding = new Thickness(0f, 6f, 0f, 10f),
                edgeThickness = new Thickness(0f, 0f, 1f, 0f),
                edgeRole = PaletteRole.Line
            };
            header.PaintOr(null, PaletteRole.Surface);
            LabelControl caption = Caption(title, PaletteRole.Ink);
            caption.style = FontStyle.Bold;
            header.AddChild(caption);
            detail = Caption("", PaletteRole.MutedInk);
            header.AddChild(detail);
            return header;
        }

        private static StackPanelControl Section()
        {
            StackPanelControl section = new StackPanelControl
            {
                Spacing = 6f,
                horizontalAlignment = HorizontalAlignment.Stretch,
                padding = new Thickness(8f, 10f, 8f, 10f),
                edgeThickness = new Thickness(0f, 0f, 1f, 0f),
                edgeRole = PaletteRole.Line
            };
            section.PaintOr(null, PaletteRole.Surface);
            return section;
        }

        private static StackPanelControl Row(string label, Control content)
        {
            StackPanelControl row = new StackPanelControl { orientation = Orientation.Horizontal, Spacing = 8f, alpha = 0f };
            LabelControl caption = Caption(label, PaletteRole.MutedInk);
            caption.preferredWidth = labelWidth;
            row.AddChild(caption);
            row.AddChild(content);
            return row;
        }

        private static TabToolsControl Tools()
        {
            TabToolsControl tools = new TabToolsControl();
            tools.padding = Thickness.Zero;
            return tools;
        }

        private static LabelControl Caption(string text, PaletteRole role) => new LabelControl
        {
            text = text,
            fontSize = captionSize,
            role = role,
            hitTestable = false,
            verticalPosition = 0.5f
        };

        // A box filled with the accent while set.
        private static PanelControl Check(out TabToolsControl.TabToolButton button, Action press)
        {
            PanelControl mark = new PanelControl
            {
                preferredWidth = 14f,
                preferredHeight = 14f,
                hitTestable = false,
                horizontalPosition = 0.5f,
                verticalPosition = 0.5f,
                edgeThickness = new Thickness(1f),
                edgeRole = PaletteRole.Line,
                cornerRole = CornerRole.Control
            };
            Mark(mark, false);

            button = new TabToolsControl.TabToolButton { preferredWidth = 20f, preferredHeight = 20f, verticalAlignment = VerticalAlignment.Center };
            button.PaintOr(null, PaletteRole.Clear);
            button.pressed = _ => press();
            button.AddChild(mark);
            return mark;
        }

        private static void Mark(PanelControl mark, bool on) => mark.PaintOr(null, on ? PaletteRole.Accent : PaletteRole.Field);
        #endregion
    }
}
