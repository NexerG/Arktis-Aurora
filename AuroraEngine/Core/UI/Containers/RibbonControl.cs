using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.Core.UI.Text;
using System.Globalization;
using System.Numerics;

namespace ArctisAurora.Core.UI.Containers
{
    // The command row of a Docs or Sheets workspace: category tabs, the picked category's tools and the
    // inspector toggle. Every entry acts on the active editor of the shown workspace's focused pane.
    [A_XSDType("Ribbon", "UI")]
    public class RibbonControl : StackPanelControl
    {
        // metrics
        private const float rowHeight = 30f;
        private const int captionSize = 12;
        private const float categoryInset = 10f;
        private const float toolSize = 20f;
        private const float swatchSize = 14f;
        private const float iconSize = 12f;
        private const float marginFieldWidth = 40f;

        private static readonly string[] docsCategories = { "Format", "Insert", "Page" };
        private static readonly string[] sheetsCategories = { "Format", "Insert", "Data" };

        // margin presets, mm
        private static readonly (string caption, float mm)[] marginOptions =
        {
            ("Normal", 25.4f),
            ("Narrow", 12.7f),
            ("Wide", 50.8f)
        };

        private static readonly (string caption, string? number)[] numberOptions =
        {
            ("General", null),
            ("Number", "#,##0.00"),
            ("Percent", "0.00%"),
            ("Currency", "€#,##0.00")
        };

        private static readonly List<RibbonControl> ribbons = new List<RibbonControl>();

        private WorkspaceControl? workspace;
        private WorkspaceKind? shownKind;
        private string? shownCategory;

        private readonly StackPanelControl categories;
        private readonly StackPanelControl stripHost;
        private readonly Dictionary<(WorkspaceKind, string), Control> strips = new Dictionary<(WorkspaceKind, string), Control>();
        private readonly IconControl inspectorInk;
        private bool? shownInspector;

        // Docs Page parts and what they last showed
        private LabelControl? pagedCaption, pagelessCaption, sizeCaption, portraitCaption, landscapeCaption, numbersCaption;
        private (PageMode, PageSize, bool, bool, float, float)? shownPage;

        // Sheets Format parts and what they last showed
        private IconControl? sheetBold;
        private bool? shownSheetBold;

        // Sheets Data parts, and whether the target was a CSV when they last changed
        private Control? importCsv, convertCsvButton;
        private bool? shownCsv;

        private static readonly (string, string)[] csvFilters = { ("CSV", "*.csv") };

        [A_XSDElementProperty("ConvertCsv", "UI", "Runs when a CSV's Data tab asks to become a sheet; the entry shows only when this is set.")]
        public Action? convertCsv;

        public override bool takesActiveControl => false;

        public RibbonControl()
        {
            SetTicking(true);
            orientation = Orientation.Horizontal;
            preferredHeight = rowHeight;
            horizontalAlignment = HorizontalAlignment.Stretch;
            padding = new Thickness(0f, 6f, 0f, 4f);
            edgeThickness = new Thickness(0f, 0f, 1f, 0f);
            edgeRole = PaletteRole.Line;
            PaintOr(null, PaletteRole.Surface);

            categories = new StackPanelControl { orientation = Orientation.Horizontal, alpha = 0f };
            stripHost = new StackPanelControl { orientation = Orientation.Horizontal, alpha = 0f };
            AddChild(categories);
            AddChild(Rule());
            AddChild(stripHost);
            AddChild(new PanelControl { widthStar = 1f, alpha = 0f, hitTestable = false });

            TabToolsControl toggle = new TabToolsControl();
            inspectorInk = toggle.Icon("panel-right", _ => ToggleInspector());
            AddChild(toggle);

            ribbons.Add(this);
            Hide();
        }

        public override void OnDestroy()
        {
            ribbons.Remove(this);
            base.OnDestroy();
        }

        #region ---- target ----
        // True when a ribbon serves this editor in its pane's workspace, so the pane shows no tab-row tools.
        internal static bool Covers(TabViewControl view, IFileEditor? editor)
        {
            if (ribbons.Count == 0) return false;

            WorkspacePageControl? page = WorkspacePageControl.Of(view);
            return page != null && Serves(page.kind, editor);
        }

        private static bool Serves(WorkspaceKind kind, IFileEditor? editor) =>
            kind == WorkspaceKind.Docs ? editor is DocumentEditorControl
            : kind == WorkspaceKind.Sheets && editor is SheetEditorControl;

        // The pane last worked in on the page, else the one it remembers, else its first.
        internal static TabViewControl? FocusedView(WorkspacePageControl page)
        {
            TabViewControl? focused = TabViewControl.focused;
            if (focused != null && !focused.destroyed && ReferenceEquals(WorkspacePageControl.Of(focused), page)) return focused;
            if (page.lastFocused is TabViewControl last && !last.destroyed && ReferenceEquals(WorkspacePageControl.Of(last), page)) return last;
            return TabViewControl.TabViews(page).FirstOrDefault();
        }

        internal static IFileEditor? Target(WorkspacePageControl? page)
        {
            TabViewControl? view = page != null ? FocusedView(page) : null;
            return view?.activeItem != null ? TabViewControl.FileEditorOf(view.activeItem) : null;
        }

        // The workspace in this ribbon's window, found once.
        internal WorkspaceControl? Workspace()
        {
            if (workspace != null && !workspace.destroyed) return workspace;

            Control root = this;
            while (root.parent is Control up) root = up;
            workspace = WorkspaceControl.In(root);
            return workspace;
        }

        private WorkspacePageControl? Page() => Workspace()?.shown;

        private DocumentEditorControl? NoteTarget() => Target(Page()) as DocumentEditorControl;

        private SheetEditorControl? SheetTarget() => Target(Page()) as SheetEditorControl;

        // Gives the target note the caret if another had it, then acts.
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
        #endregion

        public override void OnTick()
        {
            base.OnTick();

            WorkspacePageControl? page = Page();
            if (page == null || page.kind is not (WorkspaceKind.Docs or WorkspaceKind.Sheets))
            {
                Hide();
                return;
            }
            Show();

            if (shownKind != page.kind)
            {
                shownKind = page.kind;
                shownCategory = null;
                foreach (TabViewControl view in TabViewControl.TabViews(page))
                    view.SyncTools();
            }

            string[] names = page.kind == WorkspaceKind.Docs ? docsCategories : sheetsCategories;
            string category = Array.IndexOf(names, page.ribbonCategory) >= 0 ? page.ribbonCategory : names[0];
            if (shownCategory != category) ShowCategory(page.kind, names, category);

            FocusedView(page)?.SyncTools();

            TabToolsControl.Light(inspectorInk, page.inspectorShown, ref shownInspector);

            if (page.kind == WorkspaceKind.Docs && category == "Page") ReflectPage();
            else if (page.kind == WorkspaceKind.Sheets && category == "Format") ReflectSheet();
            else if (page.kind == WorkspaceKind.Sheets && category == "Data") ReflectData();
        }

        private void ToggleInspector()
        {
            WorkspacePageControl? page = Page();
            if (page != null) page.inspectorShown = !page.inspectorShown;
        }

        #region ---- categories ----
        private void ShowCategory(WorkspaceKind kind, string[] names, string category)
        {
            shownCategory = category;

            for (int i = categories.children.Count - 1; i >= 0; i--)
                categories.children[i].Destroy();
            foreach (string name in names)
                categories.AddChild(CategoryButton(name, name == category));

            if (!strips.TryGetValue((kind, category), out Control? strip))
            {
                strip = BuildStrip(kind, category);
                strips[(kind, category)] = strip;
                stripHost.AddChild(strip);
            }
            foreach (Entity child in stripHost.children)
                if (child is Control control)
                {
                    if (ReferenceEquals(control, strip)) control.Show();
                    else control.Hide();
                }

            shownPage = null;
            shownSheetBold = null;
            shownCsv = null;
        }

        private TabToolsControl.TabToolButton CategoryButton(string name, bool on)
        {
            LabelControl caption = new LabelControl
            {
                text = name,
                fontSize = captionSize,
                role = on ? PaletteRole.Ink : PaletteRole.MutedInk,
                hitTestable = false,
                verticalPosition = 0.5f
            };
            TabToolsControl.TabToolButton button = new TabToolsControl.TabToolButton
            {
                verticalAlignment = VerticalAlignment.Stretch,
                padding = new Thickness(0f, categoryInset, 0f, categoryInset),
                edgeThickness = on ? new Thickness(0f, 0f, 2f, 0f) : Thickness.Zero,
                edgeRole = PaletteRole.Accent
            };
            button.PaintOr(null, PaletteRole.Surface);
            button.pressed = _ =>
            {
                WorkspacePageControl? page = Page();
                if (page != null) page.ribbonCategory = name;
            };
            button.AddChild(caption);
            return button;
        }

        private Control BuildStrip(WorkspaceKind kind, string category) => (kind, category) switch
        {
            (WorkspaceKind.Docs, "Format") => new DocumentToolbarControl
            {
                target = NoteTarget,
                alpha = 0f,
                horizontalAlignment = HorizontalAlignment.Left
            },
            (WorkspaceKind.Docs, "Insert") => DocsInsert(),
            (WorkspaceKind.Docs, "Page") => DocsPage(),
            (WorkspaceKind.Sheets, "Format") => SheetsFormat(),
            (WorkspaceKind.Sheets, "Insert") => SheetsInsert(),
            _ => SheetsData()
        };
        #endregion

        #region ---- docs ----
        private TabToolsControl DocsInsert()
        {
            TabToolsControl row = new TabToolsControl();
            IconText(row, "table", "Table", () => Note(editor => editor.InsertTable()));
            IconText(row, "picture", "Picture...", () => Note(editor => TextInputActions.InsertPicture()));
            row.Separator();
            IconText(row, "sigma", "Formula", () => Note(editor => editor.InsertFormula(false)));
            IconText(row, "sigma-display", "Display formula", () => Note(editor => editor.InsertFormula(true)));
            IconText(row, "link", "Sheet link", () => Note(editor => TextInputActions.PasteLink()));
            row.Separator();
            IconText(row, "code", "Code block", () => Note(editor => editor.SetBlockStyling(TextStyleType.Code)));
            IconText(row, "quote", "Quote", () => Note(editor => editor.SetBlockStyling(TextStyleType.Quote)));
            IconText(row, "divider", "Divider", () => Note(editor => editor.InsertRule()));
            return row;
        }

        private TabToolsControl DocsPage()
        {
            TabToolsControl row = new TabToolsControl();
            pagedCaption = row.Text("Paged", _ => SetPage(page => page.mode = PageMode.Paged));
            pagelessCaption = row.Text("Pageless", _ => SetPage(page => page.mode = PageMode.Pageless));
            row.Separator();
            sizeCaption = row.Text("A4", owner => DropPage(owner, DocumentToolbarControl.SizeEntries), true);
            row.Separator();
            portraitCaption = row.Text("Portrait", _ => SetPage(page => page.landscape = false));
            landscapeCaption = row.Text("Landscape", _ => SetPage(page => page.landscape = true));
            row.Separator();
            row.Text("Margins", owner => DropPage(owner, MarginEntries), true);
            row.Separator();
            numbersCaption = row.Text("Page numbers", _ => SetPage(page => page.pageNumbers = !page.pageNumbers));
            return row;
        }

        private void SetPage(Action<PageLayout> change)
        {
            DocumentEditorControl? editor = NoteTarget();
            PageLayout? current = editor?.Page;
            if (editor != null && current != null) DocumentToolbarControl.ChangePage(editor, current, change);
        }

        private void DropPage(Control owner, Func<DocumentEditorControl, PageLayout, List<ContextMenuEntry>> entries)
        {
            DocumentEditorControl? editor = NoteTarget();
            PageLayout? current = editor?.Page;
            if (editor != null && current != null) TabToolsControl.Drop(owner, entries(editor, current));
        }

        private void ReflectPage()
        {
            PageLayout? page = NoteTarget()?.Page;
            (PageMode, PageSize, bool, bool, float, float)? state = page == null ? null
                : (page.mode, page.size, page.landscape, page.pageNumbers, page.width, page.height);
            if (shownPage == state) return;

            shownPage = state;
            LightCaption(pagedCaption!, page?.mode == PageMode.Paged);
            LightCaption(pagelessCaption!, page?.mode == PageMode.Pageless);
            LightCaption(portraitCaption!, page != null && !page.landscape);
            LightCaption(landscapeCaption!, page?.landscape == true);
            LightCaption(numbersCaption!, page?.pageNumbers == true);
            sizeCaption!.text = SizeCaption(page);
        }

        internal static string SizeCaption(PageLayout? page) =>
            page == null ? string.Empty
            : page.size == PageSize.Custom ? $"{Mm(page.width)} x {Mm(page.height)}"
            : page.size.ToString();

        // The presets, then a field per side.
        internal static List<ContextMenuEntry> MarginEntries(DocumentEditorControl editor, PageLayout current)
        {
            List<ContextMenuEntry> entries = new List<ContextMenuEntry>();
            foreach ((string caption, float mm) in marginOptions)
                entries.Add(DocumentToolbarControl.PageEntry(editor, current, caption,
                    page => page.marginTop = page.marginBottom = page.marginLeft = page.marginRight = mm));

            entries.Add(new ContextMenuLine());
            StackPanelControl row = new StackPanelControl { orientation = Orientation.Horizontal, Spacing = 4f, padding = new Thickness(4f) };
            foreach ((string side, Func<PageLayout, float> get, Action<PageLayout, float> set) in MarginSides())
            {
                row.AddChild(SideCaption(side));
                TextBoxControl field = MarginField(() => editor, set);
                field.text = Mm(get(current));
                row.AddChild(field);
            }
            entries.Add(new ContextMenuContent(row));
            return entries;
        }

        internal static (string, Func<PageLayout, float>, Action<PageLayout, float>)[] MarginSides() => new (string, Func<PageLayout, float>, Action<PageLayout, float>)[]
        {
            ("T", page => page.marginTop, (page, mm) => page.marginTop = mm),
            ("B", page => page.marginBottom, (page, mm) => page.marginBottom = mm),
            ("L", page => page.marginLeft, (page, mm) => page.marginLeft = mm),
            ("R", page => page.marginRight, (page, mm) => page.marginRight = mm)
        };

        // A millimetre field; Enter sets that side of the target's page, anything unparseable changes nothing.
        internal static TextBoxControl MarginField(Func<DocumentEditorControl?> target, Action<PageLayout, float> set)
        {
            TextBoxControl field = new TextBoxControl
            {
                preferredWidth = marginFieldWidth,
                preferredHeight = 22f,
                fontSize = captionSize,
                role = PaletteRole.Field
            };
            field.onCommit = text =>
            {
                DocumentEditorControl? editor = target();
                PageLayout? current = editor?.Page;
                if (editor == null || current == null) return;

                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float mm) && mm >= 0f)
                    DocumentToolbarControl.ChangePage(editor, current, page => set(page, mm));
                else editor.FocusCaret();
            };
            return field;
        }

        internal static string Mm(float mm) => mm.ToString("0.#", CultureInfo.InvariantCulture);

        private static LabelControl SideCaption(string text) => new LabelControl
        {
            text = text,
            fontSize = captionSize,
            role = PaletteRole.MutedInk,
            verticalPosition = 0.5f
        };
        #endregion

        #region ---- sheets ----
        private TabToolsControl SheetsFormat()
        {
            TabToolsControl row = new TabToolsControl();
            sheetBold = row.Icon("bold", _ => Sheet(editor => editor.ToggleBold()));
            row.Separator();
            row.Text("Format", owner => TabToolsControl.Drop(owner, NumberEntries(SheetTarget)), true);
            row.AddChild(Glyph("$", () => Sheet(editor => editor.SetNumberFormat(numberOptions[3].number))));
            row.AddChild(Glyph("%", () => Sheet(editor => editor.SetNumberFormat(numberOptions[2].number))));
            row.Text("0.00", _ => Sheet(editor => editor.SetNumberFormat(numberOptions[1].number)));
            row.Separator();
            foreach ((_, string hex) in DocumentToolbarControl.highlightOptions)
                row.AddChild(Swatch(hex, () => Sheet(editor => editor.SetFill(hex))));
            return row;
        }

        private TabToolsControl SheetsInsert()
        {
            TabToolsControl row = new TabToolsControl();
            row.Text("fx Formula", _ => SheetTarget()?.FocusFormula());
            IconText(row, "sigma", "Sum", () => Sheet(editor => editor.AutoSum()));
            row.Separator();
            IconText(row, "link", "Paste link", () => Sheet(editor => editor.PasteLink()));
            IconText(row, "copy", "Copy reference", () => Sheet(editor => editor.Copy()));
            row.Separator();
            IconText(row, "plus", "Page", () => Sheet(editor => editor.AddPage()));
            IconText(row, "layers", "Layer", () => Sheet(editor => editor.AddLayer()));
            return row;
        }

        private TabToolsControl SheetsData()
        {
            TabToolsControl row = new TabToolsControl();
            importCsv = IconText(row, "import", "Import CSV...", ImportCsv);
            IconText(row, "export", "Export page as CSV", () => Sheet(editor => editor.ExportPage(editor.pageIndex)));
            convertCsvButton = IconText(row, "convert", "Convert CSV to sheet", () => convertCsv?.Invoke());
            return row;
        }

        private void ImportCsv()
        {
            SheetEditorControl? editor = SheetTarget();
            if (editor?.document == null) return;

            string start = editor.path != null ? Path.GetDirectoryName(editor.path)! : string.Empty;
            FilePicker.Pick(UIEngine.WindowOf(this), start, csvFilters, picked =>
            {
                if (picked != null && !editor.destroyed) editor.On(() => editor.ImportPage(picked));
            });
        }

        // Import only into a sheet, Convert only from a CSV.
        private void ReflectData()
        {
            bool? csv = SheetTarget()?.document?.isCsv;
            if (shownCsv == csv) return;

            shownCsv = csv;
            if (csv == false) importCsv!.Show(); else importCsv!.Hide();
            if (csv == true && convertCsv != null) convertCsvButton!.Show(); else convertCsvButton!.Hide();
        }

        private void ReflectSheet()
        {
            SheetEditorControl? editor = SheetTarget();
            bool bold = editor?.document != null && !editor.editing && editor.page.Format(editor.activeRow, editor.activeColumn).bold;
            TabToolsControl.Light(sheetBold!, bold, ref shownSheetBold);
        }

        // General, Number, Percent and Currency for whichever sheet the target is when an entry runs.
        internal static List<ContextMenuEntry> NumberEntries(Func<SheetEditorControl?> target) =>
            numberOptions.Select(option => (ContextMenuEntry)new ContextMenuButton(option.caption, () =>
            {
                SheetEditorControl? editor = target();
                if (editor?.document != null) editor.On(() => editor.SetNumberFormat(option.number));
            })).ToList();

        internal static string NumberCaption(string? number) =>
            Array.Find(numberOptions, option => option.number == number).caption ?? "Custom";

        // A glyph and its caption on one button.
        private static TabToolsControl.TabToolButton IconText(TabToolsControl row, string icon, string text, Action press)
        {
            StackPanelControl content = new StackPanelControl
            {
                orientation = Orientation.Horizontal,
                Spacing = 6f,
                alpha = 0f,
                hitTestable = false,
                horizontalPosition = 0.5f,
                verticalPosition = 0.5f
            };
            IconControl ink = new IconControl
            {
                setName = "default",
                iconName = icon,
                preferredWidth = iconSize,
                preferredHeight = iconSize,
                hitTestable = false,
                verticalPosition = 0.5f
            };
            ink.PaintOr(null, PaletteRole.MutedInk);
            content.AddChild(ink);
            content.AddChild(new LabelControl { text = text, fontSize = captionSize, role = PaletteRole.Ink, hitTestable = false, verticalPosition = 0.5f });

            TabToolsControl.TabToolButton button = new TabToolsControl.TabToolButton
            {
                preferredHeight = toolSize,
                verticalAlignment = VerticalAlignment.Center,
                padding = new Thickness(0f, 6f, 0f, 6f),
                cornerRole = CornerRole.Control
            };
            button.PaintOr(null, PaletteRole.Clear);
            button.pressed = _ => press();
            button.AddChild(content);
            row.AddChild(button);
            return button;
        }

        // A one-character caption straight in its button.
        private static TabToolsControl.TabToolButton Glyph(string text, Action press)
        {
            TabToolsControl.TabToolButton button = new TabToolsControl.TabToolButton
            {
                preferredWidth = toolSize,
                preferredHeight = toolSize,
                verticalAlignment = VerticalAlignment.Center,
                cornerRole = CornerRole.Control
            };
            button.PaintOr(null, PaletteRole.Clear);
            button.pressed = _ => press();
            button.AddChild(new LabelControl
            {
                text = text,
                fontSize = captionSize,
                role = PaletteRole.Ink,
                hitTestable = false,
                horizontalPosition = 0.5f,
                verticalPosition = 0.5f
            });
            return button;
        }

        // A fill chip; the empty hex is no fill and shows the field colour.
        internal static TabToolsControl.TabToolButton Swatch(string hex, Action press)
        {
            PanelControl chip = new PanelControl
            {
                preferredWidth = swatchSize,
                preferredHeight = swatchSize,
                hitTestable = false,
                horizontalPosition = 0.5f,
                verticalPosition = 0.5f,
                edgeThickness = new Thickness(1f),
                edgeRole = PaletteRole.Line
            };
            chip.PaintOr(string.IsNullOrEmpty(hex) ? null : hex, PaletteRole.Field);

            TabToolsControl.TabToolButton button = new TabToolsControl.TabToolButton
            {
                preferredWidth = toolSize,
                preferredHeight = toolSize,
                verticalAlignment = VerticalAlignment.Center,
                cornerRole = CornerRole.Control
            };
            button.PaintOr(null, PaletteRole.Clear);
            button.pressed = _ => press();
            button.AddChild(chip);
            return button;
        }
        #endregion

        // Accent when on, ink when off.
        internal static void LightCaption(LabelControl caption, bool on)
        {
            caption.PaintOr(null, on ? PaletteRole.Accent : PaletteRole.Ink);
            caption.InvalidateLayout();
        }

        private static PanelControl Rule() => new PanelControl
        {
            preferredWidth = 1f,
            preferredHeight = 16f,
            margin = new Thickness(0f, 6f, 0f, 6f),
            verticalAlignment = VerticalAlignment.Center,
            hitTestable = false,
            role = PaletteRole.Line
        };
    }
}
