using ArctisAurora.Core.Animation;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Text;
using Silk.NET.GLFW;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace ArctisAurora.Core.UI.Document
{
    public enum ListKind
    {
        None,
        Bullet,
        Task,
        SortedTask
    }

    // What a list item shows in its indent: a shape, or its number written some way.
    [A_XSDType("ListMarker", "UI")]
    public enum ListMarker
    {
        Disc,
        Circle,
        Triangle,
        TriangleOutline,
        Square,
        SquareOutline,
        Decimal,
        UpperAlpha,
        LowerAlpha,
        LowerRoman,
        UpperRoman
    }

    public static class ListMarkers
    {
        public static bool IsNumbered(ListMarker marker) => marker >= ListMarker.Decimal;

        // the default icon set's glyph for a shape marker
        public static string ShapeIcon(ListMarker marker) => marker switch
        {
            ListMarker.Circle => "bullet-circle",
            ListMarker.Triangle => "bullet-triangle",
            ListMarker.TriangleOutline => "bullet-triangle-outline",
            ListMarker.Square => "bullet-square",
            ListMarker.SquareOutline => "bullet-square-outline",
            _ => "bullet-disc"
        };

        // An item's number as its marker writes it, dot included.
        public static string Format(int number, ListMarker marker) => marker switch
        {
            ListMarker.UpperAlpha => Letters(number).ToUpperInvariant() + ".",
            ListMarker.LowerAlpha => Letters(number) + ".",
            ListMarker.UpperRoman => Roman(number).ToUpperInvariant() + ".",
            ListMarker.LowerRoman => Roman(number) + ".",
            _ => number + "."
        };

        // a..z, then aa, ab — bijective, so there is no zero letter
        private static string Letters(int number)
        {
            string letters = string.Empty;
            for (int n = Math.Max(1, number); n > 0; n = (n - 1) / 26)
                letters = (char)('a' + (n - 1) % 26) + letters;
            return letters;
        }

        private static readonly (int value, string numeral)[] numerals =
        {
            (1000, "m"), (900, "cm"), (500, "d"), (400, "cd"), (100, "c"), (90, "xc"),
            (50, "l"), (40, "xl"), (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i")
        };

        private static string Roman(int number)
        {
            if (number < 1 || number > 3999) return number.ToString();

            StringBuilder roman = new StringBuilder();
            foreach ((int value, string numeral) in numerals)
                for (; number >= value; number -= value) roman.Append(numeral);
            return roman.ToString();
        }
    }

    // A run as a note file writes it: one styled slice of a block's text. It exists at load and save
    // only — in the tree a run is a StyleSpan on the block that holds it.
    [A_XSDType("Run", "UI")]
    public class Run
    {
        [A_XSDElementProperty("Text", "UI", "The slice of the block's text this run covers.")]
        public string text { get; set; } = string.Empty;

        [A_XSDElementProperty("Bold", "UI", "Run is bold.")]
        public bool bold { get; set; }

        [A_XSDElementProperty("Italic", "UI", "Run is italic.")]
        public bool italic { get; set; }

        [A_XSDElementProperty("Strikethrough", "UI", "Run is struck through.")]
        public bool strikethrough { get; set; }

        [A_XSDElementProperty("Underline", "UI", "Run is underlined.")]
        public bool underline { get; set; }

        [A_XSDElementProperty("HighlightHex", "UI", "Colour drawn behind the run's text; absent draws none.")]
        public string highlightHex { get; set; }

        [A_XSDElementProperty("ColorHex", "UI", "Run's text color; absent takes the block's.")]
        public string colorHex { get; set; }

        // Resolves into ColorHex the way VulkanControl's does; a save writes the hex.
        [A_XSDElementProperty("ControlColor", "UI", "Run's text color by name.")]
        public ControlColor controlColor
        {
            get => field;
            set
            {
                field = value;
                colorHex = Control.EnumColorToHex(value);
            }
        }

        [A_XSDElementProperty("Gradient", "UI", "Name of a gradient ramped across the run in place of its color.")]
        public string gradient { get; set; }

        [A_XSDElementProperty("Effect", "UI", "Name of an effect played across the run's letters.")]
        public string effect { get; set; }

        [A_XSDElementProperty("FontName", "UI", "Font family; absent takes the block's.")]
        public string fontName { get; set; }

        [A_XSDElementProperty("Variant", "UI", "The font's variant by name, such as Light or Black; absent, or one the font lacks, is its plain face.")]
        public string variant { get; set; }

        [A_XSDElementProperty("FontSize", "UI", "Type size in pixels; only written when the run chose one.")]
        public float fontSize { get; set; }

        [A_XSDElementProperty("FontSizeAuthored", "UI", "The run's FontSize was chosen, not taken from the styling scheme.")]
        public bool fontSizeAuthored { get; set; }

        [A_XSDElementProperty("StylingType", "UI", "Style this run takes; Inherit follows the block.")]
        public TextStyleType stylingType { get; set; } = TextStyleType.Inherit;

        [A_XSDElementProperty("Image", "UI", "Picture file, relative to the note; the run is the picture and carries no text.")]
        public string image { get; set; }

        [A_XSDElementProperty("Width", "UI", "Picture width in pixels; absent takes the picture's own, fitted to the column.")]
        public float width { get; set; }

        [A_XSDElementProperty("Height", "UI", "Picture height in pixels; absent follows the width at the picture's aspect.")]
        public float height { get; set; }

        [A_XSDElementProperty("Wrap", "UI", "How text flows around the picture; absent keeps it in line with the text.")]
        public PictureWrap wrap { get; set; }

        [A_XSDElementProperty("X", "UI", "A floating picture's offset from the column's left, in pixels.")]
        public float x { get; set; }

        [A_XSDElementProperty("Y", "UI", "A floating picture's offset from its paragraph's top, in pixels.")]
        public float y { get; set; }

        [A_XSDElementProperty("Rotation", "UI", "Picture turn in clockwise degrees; absent is upright.")]
        public float rotation { get; set; }

        [A_XSDElementProperty("Collision", "UI", "What a turned Square picture wraps: its bounding Box or its Shape; absent is Box.")]
        public PictureCollision collision { get; set; }

        [A_XSDElementProperty("Math", "UI", "TeX math source; the run is the formula and carries no text.")]
        public string math { get; set; }

        [A_XSDElementProperty("Display", "UI", "Formula is display math: a line of its own, centred.")]
        public bool display { get; set; }

        [A_XSDElementProperty("Strut", "UI", "An array row's strut in em, height plus depth, as TeX's \\arraystretch × \\baselineskip; absent is 1.2.")]
        public float strut { get; set; }

        [A_XSDElementProperty("Sheet", "UI", "Sheet cell or range shown live, as file#Page!A1 or file#Page!A1:B2; the run carries no text.")]
        public string sheet { get; set; }

        [A_XSDElementProperty("Space", "UI", "Each character's advance in pixels: a fixed-width space that draws nothing; absent is ordinary text.")]
        public float space { get; set; }

        [A_XSDElementProperty("Fill", "UI", "Stretches to take the line's slack, as TeX's \\hfill: the line is not justified or aligned past it; absent is ordinary text.")]
        public bool fill { get; set; }

        [A_XSDElementProperty("Leader", "UI", "A fill's leaders, as TeX's \\leaders: this text repeated across the fill; absent leaves it empty.")]
        public string? leader { get; set; }

        [A_XSDElementProperty("LeaderWidth", "UI", "A leader cell's width in pixels; cells line up across lines from the block's left edge.")]
        public float leaderWidth { get; set; }

        [A_XSDElementProperty("LeaderInset", "UI", "Where the leader text starts inside its cell, in pixels.")]
        public float leaderInset { get; set; }

        [A_XSDElementProperty("Note", "UI", "The id of the footnote this run anchors; its page reserves room for it.")]
        public string? note { get; set; }

        [A_XSDElementProperty("Link", "UI", "Where the run's text links to: a note by name with an optional #heading, or a web address.")]
        public string? link { get; set; }

        [A_XSDElementProperty("UrlStretch", "UI", "A biblatex URL's \\Urlmuskip: stretch between its characters in pixels; absent is none.")]
        public float urlStretch { get; set; }

        [A_XSDElementProperty("UrlWidth", "UI", "A URL's \\Urlmuskip: natural width between its characters in pixels; absent is none.")]
        public float urlWidth { get; set; }

        [A_XSDElementProperty("UrlShrink", "UI", "A URL's \\Urlmuskip: shrink between its characters in pixels; absent is none.")]
        public float urlShrink { get; set; }

        [A_XSDElementProperty("UrlBreakPenalty", "UI", "The penalty of a line ending after one of the URL's ordinary break characters.")]
        public float urlBreakPenalty { get; set; }

        [A_XSDElementProperty("UrlBigBreakPenalty", "UI", "The penalty of a line ending after one of the URL's big break characters, ':' and '-'.")]
        public float urlBigBreakPenalty { get; set; }

        [A_XSDElementProperty("UrlNumPenalty", "UI", "biblatex's biburlnumpenalty: the penalty of a line ending after a URL digit; absent is no break there.")]
        public float urlNumPenalty { get; set; }

        [A_XSDElementProperty("UrlNumWidth", "UI", "biblatex's \\biburlnumskip: natural width after a URL digit in pixels; absent is none.")]
        public float urlNumWidth { get; set; }

        [A_XSDElementProperty("UrlNumStretch", "UI", "biblatex's \\biburlnumskip: stretch after a URL digit in pixels; absent is none.")]
        public float urlNumStretch { get; set; }

        [A_XSDElementProperty("UrlNumShrink", "UI", "biblatex's \\biburlnumskip: shrink after a URL digit in pixels; absent is none.")]
        public float urlNumShrink { get; set; }

        [A_XSDElementProperty("UrlUcPenalty", "UI", "biblatex's biburlucpenalty: the penalty of a line ending after a URL capital letter; absent is no break there.")]
        public float urlUcPenalty { get; set; }

        [A_XSDElementProperty("UrlUcWidth", "UI", "biblatex's \\biburlucskip: natural width after a URL capital letter in pixels; absent is none.")]
        public float urlUcWidth { get; set; }

        [A_XSDElementProperty("UrlUcStretch", "UI", "biblatex's \\biburlucskip: stretch after a URL capital letter in pixels; absent is none.")]
        public float urlUcStretch { get; set; }

        [A_XSDElementProperty("UrlUcShrink", "UI", "biblatex's \\biburlucskip: shrink after a URL capital letter in pixels; absent is none.")]
        public float urlUcShrink { get; set; }

        [A_XSDElementProperty("UrlLcPenalty", "UI", "biblatex's biburllcpenalty: the penalty of a line ending after a URL lowercase letter; absent is no break there.")]
        public float urlLcPenalty { get; set; }

        [A_XSDElementProperty("UrlLcWidth", "UI", "biblatex's \\biburllcskip: natural width after a URL lowercase letter in pixels; absent is none.")]
        public float urlLcWidth { get; set; }

        [A_XSDElementProperty("UrlLcStretch", "UI", "biblatex's \\biburllcskip: stretch after a URL lowercase letter in pixels; absent is none.")]
        public float urlLcStretch { get; set; }

        [A_XSDElementProperty("UrlLcShrink", "UI", "biblatex's \\biburllcskip: shrink after a URL lowercase letter in pixels; absent is none.")]
        public float urlLcShrink { get; set; }

        public FontStyle Style =>
            bold ? (italic ? FontStyle.BoldItalic : FontStyle.Bold)
                 : italic ? FontStyle.Italic : FontStyle.Regular;
    }

    // One block of a note — a paragraph or a heading — as a single control: the whole block's string
    // with its runs as spans over it. Headings are not a subclass; a block names a styling type and
    // the styles scheme says what that looks like.
    public class BlockControl : TextRunControl
    {
        // the block this control shows
        public readonly NoteBlock note;

        public BlockControl() : this(new NoteBlock()) { }

        public BlockControl(NoteBlock note) : base(note.run)
        {
            this.note = note;
        }

        // showing the pointing hand over a link
        private bool overLink;

        // The link at a character slot: the character after it, else the one before.
        public string? LinkAt(int slot) => data.LinkOf(slot) ?? data.LinkOf(slot - 1);

        public override bool OnPointerMove(PointerEvent e)
        {
            bool over = DocumentControl.Following && LinkAt(IndexAt(e.point)) != null;
            if (over != overLink)
            {
                overLink = over;
                UIEngine.WindowOf(this)?.os.ChangeCursor(over ? CursorShape.Hand : CursorShape.Arrow);
            }
            return base.OnPointerMove(e);
        }

        public override bool OnPointerExit(PointerEvent e)
        {
            if (overLink)
            {
                overLink = false;
                UIEngine.WindowOf(this)?.os.ChangeCursor(CursorShape.Arrow);
            }
            return base.OnPointerExit(e);
        }

        public TextStyleType stylingType { get => note.stylingType; set => note.stylingType = value; }
        public TextAlignment alignment { get => note.alignment; set => note.alignment = value; }

        // first-line indent and the gap above the block, px; a null gap takes the layout's block spacing
        public float firstIndent { get => note.firstIndent; set => note.firstIndent = value; }
        public float? spaceBefore { get => note.spaceBefore; set => note.spaceBefore = value; }
        public float shrinkBefore { get => note.shrinkBefore; set => note.shrinkBefore = value; }

        // a break before the block, the page style its page takes, and the running-head marks it sets; null leaves a mark as it was
        public PageBreak pageBreak { get => note.pageBreak; set => note.pageBreak = value; }
        public bool keepWithNext => note.keepWithNext;
        public string? pageStyle { get => note.pageStyle; set => note.pageStyle = value; }
        public string? markLeft { get => note.markLeft; set => note.markLeft = value; }
        public string? markRight { get => note.markRight; set => note.markRight = value; }

        // the footnote or float this block is part of; null in the flow
        public PageInsert? insert { get => note.insert; set => note.insert = value; }

        // a code block's fence language; null when none was named
        public string? language { get => note.language; set => note.language = value; }

        // a code block that wraps its lines; .axml only, so Markdown code never wraps
        public bool codeWrap { get => note.codeWrap; set => note.codeWrap = value; }

        // set by a Paged document: code wraps at the text column whatever codeWrap says
        internal bool pagedWrap
        {
            get => field;
            set
            {
                if (field == value) return;
                field = value;
                SetFlag(ArrangeFlags.MeasureDirty, true);
            }
        }

        // list item state
        public ListKind listKind { get => note.listKind; set => note.listKind = value; }
        public int listLevel { get => note.listLevel; set => note.listLevel = value; }
        public bool isChecked { get => note.isChecked; set => note.isChecked = value; }

        // the item's own marker; null takes the layout's for its level
        public ListMarker? listMarker { get => note.listMarker; set => note.listMarker = value; }

        // the number this item restarts its list at; null counts on from the item above
        public int? listStart { get => note.listStart; set => note.listStart = value; }

        // what the document resolved for this item: the marker and its number in its list
        public ListMarker shownMarker { get; internal set; } = ListMarker.Disc;
        public int listNumber { get; internal set; } = 1;

        // shape, number or checkbox, in the indent
        private Control? marker;
        private float listIndent;
        private bool optimalBreaks;
        private bool widowControl;
        private const float bulletSize = 6f;
        private const float numberGap = 6f;

        // slide from where the content was drawn before a reorder
        internal float? slideFrom;
        private float slideDistance;

        // 1 at the old spot, 0 at its own
        [A_Animatable(typeof(ArrangeData), nameof(ArrangeData.reveal), LayoutChange.Arrange)]
        public float reveal
        {
            get => arrange.reveal;
            set
            {
                arrange.reveal = value;
                InvalidateArrange();
            }
        }

        // code block and rule geometry
        private const float codeInset = 10f;
        private const float ruleWeight = 1f;
        private DocumentLayout? layout;

        // the character a picture span covers
        public const string PictureChar = "￼";

        // Pressing a block focuses the editor above it, never the block itself.
        public override Control? ActiveContextTarget() => (parent as Control)?.ActiveContextTarget();

        // Copies the scheme's sizes onto the block and its spans. A span carrying an authored size
        // keeps it — the scheme only fills in sizes nobody chose.
        public void ApplyLayout(DocumentLayout layout)
        {
            this.layout = layout;
            lineHeight = note.lineHeight ?? layout.lineHeight;
            baselineDepth = layout.baselineDepth;
            lineSkip = layout.lineSkip;
            strut = note.strut;
            tolerance = layout.tolerance;
            pretolerance = layout.pretolerance;
            emergencyStretch = layout.emergencyStretch;
            advanceGrid = layout.advanceGrid;
            mathRules = layout.mathRules;
            fontSize = layout.FontSizeFor(stylingType);
            fontName = layout.FontNameFor(stylingType) ?? "default";

            listIndent = note.listIndent ?? layout.listIndent;
            optimalBreaks = layout.optimalBreaks;
            widowControl = layout.widowControl;
            ApplyInset();
            SyncMarker();
            SizeMarker();

            for (int i = 0; i < spans.Count; i++)
            {
                ref StyleSpan span = ref CollectionsMarshal.AsSpan(spans)[i];
                if (span.fontSizeAuthored) continue;

                span.fontSize = span.stylingType == TextStyleType.Inherit
                    ? 0 : layout.FontSizeFor(span.stylingType);
            }

            InvalidateLayout();
        }

        // Scales the type, the list indent and the marker for a document zoom.
        internal void SetZoom(float zoom)
        {
            if (textZoom == zoom) return;

            textZoom = zoom;
            ApplyInset();
            SizeMarker();
            InvalidateLayout();
        }

        private void ApplyInset()
        {
            Thickness inset = padding;
            bool code = stylingType == TextStyleType.Code;
            inset.left = (listKind != ListKind.None ? (listLevel + 1) * listIndent * textZoom
                       : code ? codeInset * textZoom : 0f) + note.leftIndent * textZoom;
            inset.right = (code ? codeInset * textZoom : 0f) + note.rightIndent * textZoom;
            padding = inset;
        }

        private void SizeMarker()
        {
            if (marker is CheckBoxControl box)
            {
                box.SetScale(textZoom);
                return;
            }
            if (marker is LabelControl number)
            {
                number.fontSize = fontSize;
                number.textZoom = textZoom;
                number.InvalidateLayout();
                return;
            }
            if (marker == null) return;

            marker.preferredWidth = bulletSize * textZoom;
            marker.preferredHeight = bulletSize * textZoom;
        }

        // Wraps inside the indent.
        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            Vector2 desired = base.MeasureCore(new Vector2(MathF.Max(0f, availableSize.X - padding.left - padding.right), availableSize.Y));
            marker?.Measure(availableSize);
            return desired;
        }

        // A span's font: its own, else its styling type's, else the block's.
        protected override string FontFor(in StyleSpan span) =>
            span.fontName ?? (span.stylingType == TextStyleType.Inherit ? null : layout?.FontNameFor(span.stylingType)) ?? fontName;

        internal string FontAt(in StyleSpan span) => FontFor(span);

        protected override TextAlignment Alignment => alignment;

        protected override bool OptimalBreaks => optimalBreaks;

        protected override bool WidowControl => widowControl;

        protected override bool KeepTogether => note.keepTogether;

        protected override float FirstLineIndent => firstIndent * textZoom;

        protected override float LastLineExtra => (note.rightIndent - (note.rightIndentLast ?? note.rightIndent)) * textZoom;

        protected override float LeaderOffset => note.leftIndent * textZoom;

        protected override bool Wraps => stylingType != TextStyleType.Code || codeWrap || pagedWrap;

        // A code block's ground behind each line, and a rule's line in place of text.
        internal override void Emit(float z)
        {
            LayoutRect a = arrange.arranged;
            LayoutRect box = arrange.clip;
            Vector4 clip = new Vector4(box.x, box.y, box.Right, box.Bottom);
            Vector4 bounds = new Vector4(a.x, a.y, a.Right, a.Bottom);

            if (stylingType == TextStyleType.Rule)
            {
                float weight = MathF.Max(1f, (note.ruleWeight ?? ruleWeight) * textZoom);
                float y = Lines is not { Count: > 0 } lines ? a.y
                    : note.ruleWeight != null ? TextOrigin.Y + lines[0].baseline - weight
                    : TextOrigin.Y + lines[0].top + (lines[0].height - weight) * 0.5f;
                WriteRect(UIEngine.Quads, a.x, y, a.width, weight, Palettes.Surface(palette ?? Palettes.Default, PaletteRole.Line),
                          alpha, z, clip, bounds);
                return;
            }

            if (stylingType == TextStyleType.Code && Lines != null)
            {
                uint ground = Palettes.Surface(palette ?? Palettes.Default, PaletteRole.SubField);
                foreach (TextLine line in Lines)
                    WriteRect(UIEngine.Quads, a.x, TextOrigin.Y + line.top, a.width, line.height, ground, alpha, z, clip, bounds);
            }

            base.Emit(z);
        }

        // Places the marker in the indent, centred on the first line.
        protected override void ArrangeCore(LayoutRect finalRect)
        {
            if (slideFrom is float from)
            {
                slideDistance = from - finalRect.y;
                slideFrom = null;
            }
            finalRect.y += slideDistance * reveal;

            base.ArrangeCore(finalRect);
            if (marker == null || Lines == null || Lines.Count == 0) return;

            TextLine first = Lines[0];
            Vector2 size = marker.DesiredSize;
            float indent = listIndent * textZoom;
            float x = marker is LabelControl
                ? TextOrigin.X - numberGap * textZoom - size.X
                : TextOrigin.X - indent + (indent - size.X) * 0.5f;
            float y = TextOrigin.Y + first.top + (first.height - size.Y) * 0.5f;
            marker.Arrange(new LayoutRect(x, y, size.X, size.Y));
        }

        // The document's resolved marker and number for this item.
        internal void ShowMarker(ListMarker resolved, int number)
        {
            if (shownMarker == resolved && listNumber == number) return;

            shownMarker = resolved;
            listNumber = number;
            SyncMarker();
        }

        // Builds, swaps or drops the marker to match the list kind and resolved marker.
        private void SyncMarker()
        {
            bool numbered = ListMarkers.IsNumbered(shownMarker);
            bool fits = listKind switch
            {
                ListKind.Task or ListKind.SortedTask => marker is CheckBoxControl,
                ListKind.Bullet => numbered
                    ? marker is LabelControl
                    : marker is IconControl icon && icon.iconName == ListMarkers.ShapeIcon(shownMarker),
                _ => marker == null
            };

            if (!fits)
            {
                marker?.Destroy();
                marker = listKind switch
                {
                    ListKind.Task or ListKind.SortedTask => new CheckBoxControl
                    {
                        role = PaletteRole.SubField,
                        onChanged = value => Editor()?.SetChecked(this, value)
                    },
                    ListKind.Bullet when numbered => new LabelControl
                    {
                        role = PaletteRole.Ink,
                        hitTestable = false
                    },
                    ListKind.Bullet => new IconControl
                    {
                        iconName = ListMarkers.ShapeIcon(shownMarker),
                        role = PaletteRole.Ink,
                        hitTestable = false
                    },
                    _ => null
                };
                SizeMarker();
                if (marker != null) AddChild(marker);
                InvalidateLayout();
            }

            if (marker is CheckBoxControl box) box.isChecked = isChecked;
            if (marker is LabelControl label)
            {
                string written = ListMarkers.Format(listNumber, shownMarker);
                if (label.text != written)
                {
                    label.text = written;
                    InvalidateLayout();
                }
            }
        }

        // The editor above, however deep the block sits.
        private DocumentEditorControl? Editor()
        {
            for (Entity? e = parent; e != null; e = e.parent)
                if (e is DocumentEditorControl editor) return editor;

            return null;
        }

        // Load: the run's text joins the block's string and its style becomes the next span.
        public void AppendRun(Run run)
        {
            string before = data.text;
            note.AppendRun(run);
            if (run.effect != null) RestartEffect();
            Edited(before);
        }

        // Starts the text effects a freshly built view of loaded spans carries.
        internal void StartEffects()
        {
            if (spans.Exists(span => span.effect != null)) RestartEffect();
        }

        #region ---- text and spans ----
        public void InsertText(int offset, string insert)
        {
            string before = data.text;
            data.InsertText(offset, insert);
            Edited(before);
        }

        public void RemoveText(int offset, int count)
        {
            string before = data.text;
            data.RemoveText(offset, count);
            Edited(before);
        }

        // Keeps [0..offset) and returns a detached block of the same styling holding the rest.
        public BlockControl SplitAt(int offset)
        {
            string before = data.text;
            BlockControl tail = new BlockControl(note.SplitAt(offset))
            {
                fontName = fontName,
                fontSize = fontSize,
                lineHeight = lineHeight,
                baselineDepth = baselineDepth,
                lineSkip = lineSkip,
                strut = strut,
                tolerance = tolerance,
                pretolerance = pretolerance,
                emergencyStretch = emergencyStretch,
                advanceGrid = advanceGrid,
                mathRules = mathRules
            };
            tail.CopyPaint(this);
            Edited(before);

            return tail;
        }

        public BlockSnapshot Snapshot() => note.Snapshot();

        // Replaces everything this block holds.
        public void Restore(BlockSnapshot snapshot)
        {
            note.Restore(snapshot);
            InvalidateLayout();
        }

        public StyleSpan StyleAt(int offset) => data.StyleAt(offset);

        public bool AllSpans(int start, int end, Func<StyleSpan, bool> test) => data.AllSpans(start, end, test);

        public void SetPicture(int offset, StyleSpan picture)
        {
            if (data.SetPicture(offset, picture)) InvalidateLayout();
        }
        #endregion

        public List<Run> Runs() => data.Runs();
    }
}
