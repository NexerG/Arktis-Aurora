using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using System.Globalization;
using System.Numerics;

namespace ArctisAurora.Core.UI
{
    // The format bar for its target note, or whichever holds the caret. It owns no editor: every button resolves
    // the focused one when it is pressed, the same walk the keybinds use. That is why nothing in
    // here may take the active control — the walk starts at the caret's block, and a button that
    // stole it would leave the bar acting on itself.
    [A_XSDType("DocumentToolbar", "UI")]
    public class DocumentToolbarControl : StackPanelControl
    {
        // palette
        [A_XSDElementProperty("HoverColorHex", "UI", "Ground of a bar button while hovered.")]
        public string? hoverHex { get => field; set { field = value; ApplyPalette(); } }

        [A_XSDElementProperty("PressColorHex", "UI", "Ground of a bar button while held.")]
        public string? pressHex { get => field; set { field = value; ApplyPalette(); } }

        [A_XSDElementProperty("IdleInkColorHex", "UI", "Color of a bar glyph that is not lit.")]
        public string? idleInkHex { get => field; set { field = value; ApplyPalette(); } }

        [A_XSDElementProperty("ActiveInkColorHex", "UI", "Color of a bar glyph the caret's span carries.")]
        public string? activeInkHex { get => field; set { field = value; ApplyPalette(); } }

        [A_XSDElementProperty("SeparatorColorHex", "UI", "Color of the rules between bar groups.")]
        public string? separatorHex { get => field; set { field = value; ApplyPalette(); } }

        [A_XSDElementProperty("FieldColorHex", "UI", "Ground of the px field.")]
        public string? fieldHex { get => field; set { field = value; ApplyPalette(); } }

        // The bar's own ground is what a button rests at, so it repaints the parts the way the rest
        // of the palette does.
        public override string colorHex
        {
            get => base.colorHex;
            set { base.colorHex = value; ApplyPalette(); }
        }

        // metrics
        private const int barHeight = 30;
        private const int iconButtonWidth = 34;
        private const int markerButtonWidth = 16;
        private const int iconSize = 14;
        private const int captionSize = 13;
        private const int fontButtonWidth = 140;
        private const int variantButtonWidth = 90;

        // what the variant button shows for a font's plain face
        private const string PlainVariant = "Regular";

        // the font list
        private const float fontListWidth = 220f;
        private const float fontListHeight = 360f;
        private const float fontRowHeight = 26f;
        private const int fontRowSize = 14;

        // what the px field will accept
        private const int minPx = 6;
        private const int maxPx = 200;

        private static readonly (string caption, TextStyleType type)[] stylingOptions =
        {
            ("Text", TextStyleType.Text),
            ("Heading 1", TextStyleType.Heading1),
            ("Heading 2", TextStyleType.Heading2),
            ("Heading 3", TextStyleType.Heading3),
            ("Heading 4", TextStyleType.Heading4),
            ("Heading 5", TextStyleType.Heading5),
            ("Heading 6", TextStyleType.Heading6),
            ("Quote", TextStyleType.Quote),
            ("Code", TextStyleType.Code),
            ("Comment", TextStyleType.Comment),
            ("Horizontal line", TextStyleType.Rule)
        };

        // Default is the colour a fresh span carries, so picking it lets the note drop the attribute
        // again rather than pinning a hex into the file.
        private static readonly (string caption, string hex)[] colorOptions =
        {
            ("Default", ""),
            ("Gray", "#808080"),
            ("Red", "#E06C75"),
            ("Orange", "#D19A66"),
            ("Yellow", "#E5C07B"),
            ("Green", "#98C379"),
            ("Blue", "#61AFEF"),
            ("Purple", "#C678DD")
        };

        internal static readonly (string caption, string hex)[] highlightOptions =
        {
            ("None", ""),
            ("Yellow", MarkdownFormat.DefaultHighlightHex),
            ("Green", "#C8E6A0"),
            ("Blue", "#B4D7F5"),
            ("Pink", "#F5C2DC"),
            ("Orange", "#F7CFA0"),
            ("Purple", "#D9C4F0")
        };

        private static readonly (string caption, ListMarker marker)[] markerOptions =
        {
            ("Filled circle", ListMarker.Disc),
            ("Empty circle", ListMarker.Circle),
            ("Filled triangle", ListMarker.Triangle),
            ("Empty triangle", ListMarker.TriangleOutline),
            ("Filled square", ListMarker.Square),
            ("Empty square", ListMarker.SquareOutline),
            ("1. 2. 3.", ListMarker.Decimal),
            ("A. B. C.", ListMarker.UpperAlpha),
            ("a. b. c.", ListMarker.LowerAlpha),
            ("i. ii. iii.", ListMarker.LowerRoman),
            ("I. II. III.", ListMarker.UpperRoman)
        };

        // The note the bar acts on and reflects; the caret's note when left unset.
        public Func<DocumentEditorControl?>? target;

        private readonly IconControl boldInk;
        private readonly IconControl italicInk;
        private readonly IconControl underlineInk;
        private readonly IconControl strikeInk;
        private readonly IconControl highlightInk;
        private readonly IconControl alignLeftInk;
        private readonly IconControl alignCenterInk;
        private readonly IconControl alignRightInk;
        private readonly IconControl alignJustifyInk;
        private readonly IconControl bulletsInk;
        private readonly IconControl numbersInk;
        private readonly IconControl tasksInk;
        private readonly LabelControl stylingCaption;
        private readonly LabelControl fontCaption;
        private readonly LabelControl variantCaption;
        private readonly LabelControl colorInk;
        private readonly PxBox pxField;

        // The last editor that held the caret. Only the px field uses it: a field has to take the
        // active control to be typed into, which is the one thing the rest of the bar avoids, so the
        // press that focuses it has already ended the walk that would find the note. Every other
        // control resolves live, and so cannot act on a note the caret has left.
        private DocumentEditorControl remembered;

        // What the px field is pointed at, captured on the press that focuses it and held until it
        // commits or is abandoned. No range means the note had nothing selected, and a size with
        // nothing to put it on changes nothing.
        private DocumentEditorControl pxTarget;
        private bool pxRanged;
        private DocumentAddress pxFrom;
        private DocumentAddress pxTo;

        // what the children were last told, so a tick that changes nothing writes nothing
        private bool? shownBold;
        private bool? shownItalic;
        private bool? shownUnderline;
        private bool? shownStrike;
        private bool? shownBullets;
        private bool? shownNumbers;
        private bool? shownTasks;
        private string? shownHighlight;
        private TextStyleType? shownStyling;
        private bool? shownLeft;
        private bool? shownCenter;
        private bool? shownRight;
        private bool? shownJustify;
        private string? shownColor;
        private PaletteRole? shownInk;
        private int? shownPx;
        private string? shownFont;
        private string? shownVariant;

        public override bool takesActiveControl => false;

        public DocumentToolbarControl()
        {
            SetTicking(true);
            orientation = Orientation.Horizontal;
            Spacing = 2f;
            preferredHeight = barHeight;
            horizontalAlignment = HorizontalAlignment.Stretch;

            stylingCaption = Caption(stylingOptions[0].caption);
            AddChild(CaptionButton(stylingCaption, 124, OpenStyling));
            AddChild(Separator());

            fontCaption = Caption(string.Empty);
            AddChild(CaptionButton(fontCaption, fontButtonWidth, OpenFonts));
            variantCaption = Caption(PlainVariant);
            AddChild(CaptionButton(variantCaption, variantButtonWidth, OpenVariants));
            AddChild(Separator());

            boldInk = Ink("bold");
            italicInk = Ink("italic");
            AddChild(IconButton(boldInk, _ => On(TextInputActions.Bold)));
            AddChild(IconButton(italicInk, _ => On(TextInputActions.Italic)));
            underlineInk = Ink("underline");
            AddChild(IconButton(underlineInk, _ => On(TextInputActions.Underline)));
            strikeInk = Ink("strikethrough");
            AddChild(IconButton(strikeInk, _ => On(TextInputActions.Strikethrough)));
            AddChild(Separator());

            colorInk = Caption("A");
            AddChild(CaptionButton(colorInk, 52, OpenColors));
            highlightInk = Ink("highlight");
            AddChild(IconButton(highlightInk, OpenHighlights));
            AddChild(Separator());

            pxField = new PxBox
            {
                preferredWidth = 40,
                preferredHeight = 20,
                fontSize = captionSize,
                role = PaletteRole.Field
            };
            pxField.PaintText(null, PaletteRole.MutedInk);
            pxField.pressed = CapturePx;
            pxField.onCommit = ApplyPx;
            pxField.onCancel = RevertPx;
            pxField.onBlur = AbandonPx;
            AddChild(pxField);
            AddChild(Separator());

            alignLeftInk = Ink("align-left");
            alignCenterInk = Ink("align-center");
            alignRightInk = Ink("align-right");
            alignJustifyInk = Ink("align-justify");
            AddChild(IconButton(alignLeftInk, _ => On(TextInputActions.AlignLeft)));
            AddChild(IconButton(alignCenterInk, _ => On(TextInputActions.AlignCenter)));
            AddChild(IconButton(alignRightInk, _ => On(TextInputActions.AlignRight)));
            AddChild(IconButton(alignJustifyInk, _ => On(TextInputActions.AlignJustify)));
            AddChild(Separator());

            bulletsInk = Ink("bullet-disc");
            AddChild(IconButton(bulletsInk, _ => On(TextInputActions.Bullets)));
            ToolButton markers = NewButton(markerButtonWidth, OpenMarkers);
            markers.AddChild(Ink("chevron-down"));
            AddChild(markers);
            numbersInk = Ink("list-numbered");
            AddChild(IconButton(numbersInk, _ => On(TextInputActions.Numbers)));
            tasksInk = Ink("list-task");
            AddChild(IconButton(tasksInk, _ => On(TextInputActions.Tasks)));
            AddChild(IconButton(Ink("outdent"), _ => On(TextInputActions.Outdent)));
            AddChild(IconButton(Ink("indent"), _ => On(TextInputActions.Indent)));
        }

        private DocumentEditorControl? Target() => target != null ? target() : TextInputActions.Editor();

        // Hands the target note the caret if another had it, then acts.
        private void On(Action action)
        {
            DocumentEditorControl? editor = Target();
            if (editor == null) return;

            if (!ReferenceEquals(TextInputActions.Editor(), editor)) editor.FocusCaret();
            action();
        }

        // Reflects the span the selection starts in. Cheap enough to poll: four comparisons, and a
        // write only when one of them moved.
        public override void OnTick()
        {
            base.OnTick();

            DocumentEditorControl? live = Target();
            if (live != null) remembered = live;

            // Reflecting the remembered editor rather than the live one is what keeps the bar
            // showing the note while the px field holds the focus.
            DocumentEditorControl editor = live ?? remembered;
            CaretStyle? source = editor?.StyleSource;

            Reflect(boldInk, source?.bold == true, ref shownBold);
            Reflect(italicInk, source?.italic == true, ref shownItalic);
            Reflect(underlineInk, source?.underline == true, ref shownUnderline);

            Reflect(strikeInk, source?.strikethrough == true, ref shownStrike);

            string? highlight = source?.highlightHex;
            if (shownHighlight != highlight)
            {
                shownHighlight = highlight;
                highlightInk.PaintOr(highlight ?? idleInkHex, PaletteRole.MutedInk);
            }

            TextStyleType styling = editor?.CaretBlockStyling ?? TextStyleType.Text;
            if (shownStyling != styling)
            {
                shownStyling = styling;
                stylingCaption.text = CaptionFor(styling);
            }

            TextAlignment? alignment = editor?.CanAlign == true ? editor.CaretBlockAlignment : null;
            Reflect(alignLeftInk, alignment == TextAlignment.Left, ref shownLeft);
            Reflect(alignCenterInk, alignment == TextAlignment.Center, ref shownCenter);
            Reflect(alignRightInk, alignment == TextAlignment.Right, ref shownRight);
            Reflect(alignJustifyInk, alignment == TextAlignment.Justify, ref shownJustify);
            BlockControl? block = editor?.CaretBlock;
            bool numbered = block?.listKind == ListKind.Bullet && ListMarkers.IsNumbered(block.listMarker ?? ListMarker.Disc);
            Reflect(bulletsInk, block?.listKind == ListKind.Bullet && !numbered, ref shownBullets);
            Reflect(numbersInk, numbered, ref shownNumbers);
            Reflect(tasksInk, block?.listKind is ListKind.Task or ListKind.SortedTask, ref shownTasks);

            string? color = source.HasValue ? source.Value.colorHex : idleInkHex;
            PaletteRole ink = source.HasValue ? PaletteRole.Ink : PaletteRole.MutedInk;
            if (shownColor != color || shownInk != ink)
            {
                shownColor = color;
                shownInk = ink;
                Paint(colorInk, color, ink);
            }

            string font = source?.fontName ?? "default";
            if (shownFont != font)
            {
                shownFont = font;
                fontCaption.fontName = font;
                fontCaption.text = FontLibrary.FamilyOf(font);
            }

            string variant = source?.variant ?? PlainVariant;
            if (shownVariant != variant)
            {
                shownVariant = variant;
                variantCaption.text = variant;
            }

            // never while it is being typed into, or the reflection would fight the keystrokes
            int px = (int)MathF.Round(source?.fontSize ?? 0f);
            if (!pxField.isEditing && shownPx != px)
            {
                shownPx = px;
                pxField.text = px > 0 ? px.ToString() : string.Empty;
            }
        }

        #region ---- px field ----
        // The press that focuses the field. The note is still the one the active control is about to
        // leave, so this is the last moment its selection can be read.
        private void CapturePx()
        {
            pxTarget = remembered;
            pxRanged = pxTarget != null && pxTarget.SelectedRange(out pxFrom, out pxTo);
        }

        // Enter. A size out of range or unparseable changes nothing; either way the note gets the
        // caret back.
        private void ApplyPx(string value)
        {
            DocumentEditorControl target = pxTarget;
            bool ranged = pxRanged;
            DocumentAddress from = pxFrom;
            DocumentAddress to = pxTo;
            EndPx();

            if (target != null && int.TryParse(value, out int px) && px >= minPx && px <= maxPx)
            {
                if (ranged) target.ApplyStyleTo(from, to, new StyleDelta(fontSize: px));
                else target.ArmStyle(new StyleDelta(fontSize: px));
            }

            target?.FocusCaret();
        }

        // Escape.
        private void RevertPx()
        {
            DocumentEditorControl target = pxTarget;
            EndPx();
            target?.FocusCaret();
        }

        // The focus went somewhere the user pointed it. The active control is theirs to place, so
        // this ends the session and touches nothing else.
        private void AbandonPx() => EndPx();

        // Ending the session first is what keeps the refocus that follows from re-entering here
        // through the field's own blur; clearing the cache is what makes the next tick rewrite the
        // field from the note.
        private void EndPx()
        {
            pxTarget = null;
            pxRanged = false;
            shownPx = null;
        }
        #endregion

        private void Reflect(IconControl ink, bool on, ref bool? shown)
        {
            if (shown == on) return;

            shown = on;
            ink.PaintOr(on ? activeInkHex : idleInkHex, on ? PaletteRole.Accent : PaletteRole.MutedInk);
        }
        #region ---- menus ----
        // The editor is captured here rather than re-resolved inside the entry: a menu row is a
        // plain button and does take the active control, so by the time an entry runs the walk would
        // start at the menu instead of the note.
        private void OpenStyling(ToolButton owner)
        {
            DocumentEditorControl? editor = Target();
            if (editor != null) Drop(owner, StylingEntries(editor));
        }

        internal static List<ContextMenuEntry> StylingEntries(DocumentEditorControl editor)
        {
            List<ContextMenuEntry> entries = new List<ContextMenuEntry>();
            foreach ((string caption, TextStyleType type) in stylingOptions)
            {
                TextStyleType picked = type;
                entries.Add(new ContextMenuButton(caption, picked == TextStyleType.Rule
                    ? editor.InsertRule
                    : () => editor.SetBlockStyling(picked)));
            }
            return entries;
        }

        private void OpenMarkers(ToolButton owner)
        {
            DocumentEditorControl? editor = Target();
            if (editor != null) Drop(owner, MarkerEntries(editor));
        }

        internal static List<ContextMenuEntry> MarkerEntries(DocumentEditorControl editor)
        {
            List<ContextMenuEntry> entries = new List<ContextMenuEntry>();
            foreach ((string caption, ListMarker marker) in markerOptions)
            {
                if (marker == ListMarker.Decimal) entries.Add(new ContextMenuLine());

                ListMarker picked = marker;
                entries.Add(new ContextMenuButton(caption, () =>
                {
                    editor.SetListMarker(picked);
                    editor.FocusCaret();
                }));
            }
            return entries;
        }

        private void OpenColors(ToolButton owner)
        {
            DocumentEditorControl? editor = Target();
            if (editor == null) return;

            List<ContextMenuEntry> entries = new List<ContextMenuEntry>();
            foreach ((string caption, string hex) in colorOptions)
            {
                string picked = hex;
                entries.Add(new ContextMenuButton(caption, () =>
                {
                    editor.ApplyStyle(new StyleDelta(colorHex: picked));
                    editor.FocusCaret();
                }));
            }

            entries.Add(new ContextMenuLine());
            entries.Add(new ContextMenuContent(Picker(editor, editor.StyleSource?.colorHex, hex => new StyleDelta(colorHex: hex))));
            Drop(owner, entries);
        }

        private void OpenHighlights(ToolButton owner)
        {
            DocumentEditorControl? editor = Target();
            if (editor == null) return;

            List<ContextMenuEntry> entries = new List<ContextMenuEntry>();
            foreach ((string caption, string hex) in highlightOptions)
            {
                string picked = hex;
                entries.Add(new ContextMenuButton(caption, () =>
                {
                    editor.ApplyStyle(new StyleDelta(highlightHex: picked));
                    editor.FocusCaret();
                }));
            }

            entries.Add(new ContextMenuLine());
            entries.Add(new ContextMenuContent(Picker(editor, editor.StyleSource?.highlightHex ?? MarkdownFormat.DefaultHighlightHex,
                hex => new StyleDelta(highlightHex: hex))));
            Drop(owner, entries);
        }

        private void OpenFonts(ToolButton owner)
        {
            DocumentEditorControl? editor = Target();
            if (editor != null) Drop(owner, new List<ContextMenuEntry> { new ContextMenuContent(FontList(editor)) });
        }

        // Imported families in their own fonts, then the installed ones in their previews.
        internal static ScrollableControl FontList(DocumentEditorControl editor)
        {
            StackPanelControl rows = new StackPanelControl
            {
                orientation = Orientation.Vertical,
                alpha = 0f,
                horizontalAlignment = HorizontalAlignment.Stretch
            };

            List<(string asset, string family)> imported = FontLibrary.Imported();
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string asset, string family) in imported)
            {
                names.Add(family);
                string picked = asset;
                rows.AddChild(FontRow(family, asset, () => PickFont(editor, picked)));
            }

            rows.AddChild(FontHeader("More fonts"));
            IReadOnlyList<InstalledFamily>? installed = FontCatalog.Families;
            if (installed == null) rows.AddChild(FontHeader("Finding installed fonts…"));
            else
                foreach (InstalledFamily family in installed)
                {
                    if (names.Contains(family.name)) continue;
                    string? preview = FontLibrary.PreviewOf(family.name);
                    InstalledFamily picked = family;
                    rows.AddChild(FontRow(preview ?? family.name, preview != null ? FontPreviews.AssetName : "default",
                        () => FontLibrary.Import(picked, asset => PickFont(editor, asset))));
                }

            ScrollableControl list = new ScrollableControl
            {
                alpha = 0f,
                preferredWidth = fontListWidth,
                preferredHeight = Math.Min(fontListHeight, rows.children.Count * fontRowHeight)
            };
            list.AddChild(rows);
            return list;
        }

        private static void PickFont(DocumentEditorControl editor, string asset)
        {
            editor.ApplyStyle(new StyleDelta(fontName: asset));
            editor.FocusCaret();
        }

        // The caret font's variants; Regular clears the run's own.
        private void OpenVariants(ToolButton owner)
        {
            DocumentEditorControl? editor = Target();
            if (editor != null) Drop(owner, VariantEntries(editor));
        }

        internal static List<ContextMenuEntry> VariantEntries(DocumentEditorControl editor)
        {
            string[] variants = FontLibrary.VariantsOf(editor.StyleSource?.fontName ?? "default");
            List<ContextMenuEntry> entries = new List<ContextMenuEntry>(variants.Length);
            for (int v = 0; v < variants.Length; v++)
            {
                string picked = v == 0 ? string.Empty : variants[v];
                entries.Add(new ContextMenuButton(v == 0 ? PlainVariant : picked, () =>
                {
                    editor.ApplyStyle(new StyleDelta(variant: picked));
                    editor.FocusCaret();
                }));
            }
            return entries;
        }

        private static FontRowButton FontRow(string text, string font, Action pick)
        {
            FontRowButton row = new FontRowButton
            {
                preferredHeight = fontRowHeight,
                horizontalAlignment = HorizontalAlignment.Stretch,
                padding = new Thickness(4f, 10f, 4f, 10f),
                cornerRadius = new CornerRadii(4f),
                stateBinding = "menu-row",
                edgeRole = PaletteRole.Accent
            };
            row.AddChild(new LabelControl
            {
                text = text,
                fontName = font,
                fontSize = fontRowSize,
                horizontalPosition = 0f,
                verticalPosition = 0.5f,
                hitTestable = false
            });
            row.pressed = () =>
            {
                ContextMenus.Close();
                pick();
            };
            return row;
        }

        private static LabelControl FontHeader(string text) => new LabelControl
        {
            text = text,
            fontSize = captionSize,
            role = PaletteRole.MutedInk,
            preferredHeight = fontRowHeight,
            horizontalPosition = 0f,
            padding = new Thickness(4f, 10f, 4f, 10f)
        };

        // A picker styling the editor with each pick.
        private static ColorPickerControl Picker(DocumentEditorControl editor, string? current, Func<string, StyleDelta> delta)
        {
            ColorPickerControl picker = new ColorPickerControl { padding = new Thickness(4f) };
            if (current != null) picker.hex = current;
            picker.onPicked = hex =>
            {
                editor.ApplyStyle(delta(hex));
                editor.FocusCaret();
            };
            return picker;
        }

        // The paper sizes, then the custom size fields.
        internal static List<ContextMenuEntry> SizeEntries(DocumentEditorControl editor, PageLayout current)
        {
            List<ContextMenuEntry> entries = new List<ContextMenuEntry>();
            foreach (PageSize size in Enum.GetValues<PageSize>())
            {
                if (size == PageSize.Custom) continue;

                PageSize picked = size;
                entries.Add(PageEntry(editor, current, size.ToString(), page => page.size = picked));
            }

            entries.Add(new ContextMenuContent(CustomSizeFields(editor, current)));
            return entries;
        }

        // Width and height in millimetres; Enter in either sets them as a custom paper size.
        private static StackPanelControl CustomSizeFields(DocumentEditorControl editor, PageLayout current)
        {
            Vector2 mm = current.SizePx() / PageLayout.PxPerMm;
            TextBoxControl width = SizeField(mm.X);
            TextBoxControl height = SizeField(mm.Y);
            width.onCommit = height.onCommit = _ =>
            {
                SetCustomSize(editor, width.text, height.text);
                editor.FocusCaret();
            };

            StackPanelControl row = new StackPanelControl { orientation = Orientation.Horizontal, Spacing = 4f, padding = new Thickness(4f) };
            row.AddChild(Caption("Custom"));
            row.AddChild(width);
            row.AddChild(Caption("x"));
            row.AddChild(height);
            row.AddChild(Caption("mm"));
            return row;
        }

        private static TextBoxControl SizeField(float mm) => new TextBoxControl
        {
            preferredWidth = 56f,
            preferredHeight = 24f,
            fontSize = captionSize,
            text = mm.ToString("0.#", CultureInfo.InvariantCulture)
        };

        // Sets a custom paper size from two millimetre strings; false when either is not a positive number.
        public static bool SetCustomSize(DocumentEditorControl editor, string width, string height)
        {
            PageLayout? current = editor.Page;
            if (current == null
                || !float.TryParse(width, NumberStyles.Float, CultureInfo.InvariantCulture, out float w)
                || !float.TryParse(height, NumberStyles.Float, CultureInfo.InvariantCulture, out float h)
                || w <= 0f || h <= 0f) return false;

            PageLayout page = current.Clone();
            page.size = PageSize.Custom;
            page.width = w;
            page.height = h;
            page.landscape = false;
            editor.SetPage(page);
            return true;
        }

        // Changes a copy, so an inherited editor-wide page is never written through.
        internal static ContextMenuButton PageEntry(DocumentEditorControl editor, PageLayout current, string caption,
            Action<PageLayout> change) => new ContextMenuButton(caption, () => ChangePage(editor, current, change));

        internal static void ChangePage(DocumentEditorControl editor, PageLayout current, Action<PageLayout> change)
        {
            PageLayout page = current.Clone();
            change(page);
            editor.SetPage(page);
            editor.FocusCaret();
        }

        private static void Drop(ToolButton owner, List<ContextMenuEntry> entries)
        {
            if (ContextMenus.ClosedOn(owner)) return;
            ContextMenus.Open(entries, owner,
                new Vector2(owner.arrangedRect.x, owner.arrangedRect.Bottom));
        }

        internal static string CaptionFor(TextStyleType type)
        {
            foreach ((string caption, TextStyleType option) in stylingOptions)
                if (option == type) return caption;

            return stylingOptions[0].caption;
        }
        #endregion

        #region ---- parts ----
        private ToolButton NewButton(int width, Action<ToolButton> onToolPress)
        {
            ToolButton button = new ToolButton
            {
                preferredWidth = width,
                preferredHeight = barHeight,
                hoverColorHex = hoverHex,
                pressColorHex = pressHex
            };
            button.PaintOr(colorAuthored ? colorHex : null, PaletteRole.Clear);
            button.pressed = onToolPress;
            return button;
        }

        private ToolButton IconButton(IconControl ink, Action<ToolButton> onToolPress)
        {
            ToolButton button = NewButton(iconButtonWidth, onToolPress);
            button.AddChild(ink);
            return button;
        }

        // A caption plus its chevron is two controls, and a button places one — so they travel in a
        // row of their own. The row is transparent: every control paints, and an opaque one here
        // would sit over the button's hover tint.
        private ToolButton CaptionButton(LabelControl caption, int width, Action<ToolButton> onToolPress)
        {
            StackPanelControl row = new StackPanelControl
            {
                orientation = Orientation.Horizontal,
                Spacing = 4f,
                alpha = 0f,
                hitTestable = false,
                horizontalPosition = 0.5f,
                verticalPosition = 0.5f
            };
            row.AddChild(caption);
            row.AddChild(Ink("chevron-down"));

            ToolButton button = NewButton(width, onToolPress);
            button.AddChild(row);
            return button;
        }

        // The parts inside a button are decoration: hit-testable they would answer the press, and a
        // row resolves the active context to itself rather than to the button that owns it.
        private static LabelControl Caption(string text) => new LabelControl
        {
            fontSize = captionSize,
            role = PaletteRole.MutedInk,
            hitTestable = false,
            text = text
        };

        private IconControl Ink(string icon) => new IconControl
        {
            setName = "default",
            iconName = icon,
            preferredWidth = iconSize,
            preferredHeight = iconSize,
            hitTestable = false,
            horizontalPosition = 0.5f,
            verticalPosition = 0.5f
        };

        private PanelControl Separator() => new PanelControl
        {
            preferredWidth = 1,
            preferredHeight = barHeight,
            role = PaletteRole.Line
        };

        // A label's colour reaches its glyphs when its runs are built, which is a measure away.
        private static void Paint(LabelControl label, string? hex, PaletteRole fallback)
        {
            label.PaintOr(hex, fallback);
            label.InvalidateLayout();
        }

        // Repaints what the constructor already built, because the host's attributes arrive after it.
        private void ApplyPalette()
        {
            foreach (Entity child in children)
            {
                switch (child)
                {
                    case PxBox box:
                        box.PaintOr(fieldHex, PaletteRole.Field);
                        box.PaintText(idleInkHex, PaletteRole.MutedInk);
                        break;
                    case ToolButton button:
                        button.PaintOr(colorAuthored ? colorHex : null, PaletteRole.Clear);
                        button.hoverColorHex = hoverHex;
                        button.pressColorHex = pressHex;
                        InkTree(button);
                        break;
                    case PanelControl separator:
                        separator.PaintOr(separatorHex, PaletteRole.Line);
                        break;
                }
            }

            // The lit ones repaint from OnTick, which only writes when its cache moved.
            shownBold = null;
            shownItalic = null;
            shownUnderline = null;
            shownStrike = null;
            shownBullets = null;
            shownNumbers = null;
            shownTasks = null;
            shownHighlight = "";
            shownInk = null;
        }

        private void InkTree(Control control)
        {
            foreach (Entity child in control.children)
            {
                if (child is not Control visual) continue;

                if (visual is IconControl) visual.PaintOr(idleInkHex, PaletteRole.MutedInk);
                else if (visual is LabelControl label) Paint(label, idleInkHex, PaletteRole.MutedInk);

                InkTree(visual);
            }
        }
        #endregion

        // The one control in the bar that does take the active control, because it cannot be typed
        // into otherwise. The press is the hook rather than the focus, since the active context is
        // handed over in the same phase.
        private class PxBox : TextBoxControl
        {
            public Action pressed;

            public override bool OnPointerPress(PointerEvent e)
            {
                if (e.button != PointerEvent.leftButton) return false;

                bool handled = base.OnPointerPress(e);
                pressed?.Invoke();
                return handled;
            }
        }

        // Acts on press, not release: with the active control left alone, the release's "the press
        // target is what is active" gate never matches and no release arrives.
        private class ToolButton : ButtonControl
        {
            public Action<ToolButton> pressed;

            public override bool takesActiveControl => false;

            public override bool OnPointerPress(PointerEvent e)
            {
                base.OnPointerPress(e);
                if (e.button == PointerEvent.leftButton) pressed?.Invoke(this);
                return true;
            }
        }

        // A font list row; acts on press like a ToolButton.
        private sealed class FontRowButton : ButtonControl
        {
            public Action pressed;

            public override bool takesActiveControl => false;

            public override bool OnPointerPress(PointerEvent e)
            {
                base.OnPointerPress(e);
                if (e.button == PointerEvent.leftButton) pressed?.Invoke();
                return true;
            }
        }
    }
}
