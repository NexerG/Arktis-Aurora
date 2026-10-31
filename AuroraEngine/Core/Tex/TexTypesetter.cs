using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Filing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ArctisAurora.Core.Tex
{
    // TeX's stomach for a LaTeX document: turns the expander's tokens into paragraphs of characters, glue and math.
    public sealed class TexTypesetter
    {
        private static readonly LogChannel Log = LogChannel.For("Tex");

        private const int Unity = 65536;

        private enum Mode { Vertical, Horizontal }

        private sealed class ListFrame
        {
            public readonly TexListKind kind;
            public readonly ListFrame? parent;
            public readonly int depth;
            public readonly int kindDepth;
            public int count;
            public bool started;
            public ListSettings settings = new ListSettings();
            // \topsep, with \partopsep when it began in vertical mode, sp: the skip above and below it
            public TexGlue topSkip;

            public ListFrame(TexListKind kind, ListFrame? parent)
            {
                this.kind = kind;
                this.parent = parent;
                depth = (parent?.depth ?? 0) + 1;
                kindDepth = 1;
                for (ListFrame? p = parent; p != null; p = p.parent)
                    if (p.kind == kind) kindDepth++;
            }
        }

        // what a heading or footnote sets aside while its text is typeset
        private readonly record struct Frame(Mode mode, TexParagraph? par, List<TexNode> vtarget, TexStyle style,
            ListFrame? list, TexAlign align, bool quote);

        // a tabular being read: the open row and cell, and the style each cell starts from
        private sealed class TableBuild
        {
            public readonly TexTable table;
            public readonly TexStyle style;
            public List<TexTableCell>? row;
            public TexTableCell? cell;
            public int column;

            public TableBuild(TexTable table, TexStyle style)
            {
                this.table = table;
                this.style = style;
            }
        }

        // enumitem keys; null leaves the class's value, dimensions in sp
        private sealed class ListSettings
        {
            public TexToken[]? label;
            public int? leftMargin, itemSep, parSep, topSep;

            public void Apply(ListSettings other)
            {
                label = other.label ?? label;
                leftMargin = other.leftMargin ?? leftMargin;
                itemSep = other.itemSep ?? itemSep;
                parSep = other.parSep ?? parSep;
                topSep = other.topSep ?? topSep;
            }
        }

        // titlesec or \@startsection for one section level; null skips leave the class's, sp
        private sealed class TitleFormat
        {
            public TexToken[]? format, label, before, after;
            public int sep, indent;
            public int? beforeSkip, afterSkip;
            public bool? runIn;
        }

        private sealed class FixedMetrics : ITexFontMetrics
        {
            public int Quad(TexFont font) => font.size;

            public int XHeight(TexFont font) => (int)(font.size * 0.430554);

            public int Width(string text, TexFont font) => text.Length * font.size / 2;
        }

        // LaTeX's size commands at the 10pt, 11pt and 12pt class options
        private static readonly string[] sizeNames = { "tiny", "scriptsize", "footnotesize", "small", "normalsize", "large", "Large", "LARGE", "huge", "Huge" };
        private static readonly float[][] sizePoints =
        {
            new[] { 5f, 7f, 8f, 9f, 10f, 12f, 14.4f, 17.28f, 20.74f, 24.88f },
            new[] { 6f, 8f, 9f, 10f, 10.95f, 12f, 14.4f, 17.28f, 20.74f, 24.88f },
            new[] { 6f, 8f, 10f, 10.95f, 12f, 14.4f, 17.28f, 20.74f, 24.88f, 24.88f }
        };
        private static readonly float[][] baselineSkipPoints =
        {
            new[] { 6f, 8f, 9.5f, 11f, 12f, 14f, 18f, 22f, 25f, 30f },
            new[] { 7f, 9.5f, 11f, 12f, 13.6f, 14f, 18f, 22f, 25f, 30f },
            new[] { 7f, 9.5f, 12f, 13.6f, 14.5f, 18f, 22f, 25f, 30f, 30f }
        };
        private const int NormalSize = 4;

        // setspace's \onehalfspacing and \doublespacing stretch at 10pt, 11pt and 12pt
        private static readonly float[] oneHalfStretches = { 1.25f, 1.213f, 1.241f };
        private static readonly float[] doubleStretches = { 1.667f, 1.618f, 1.655f };

        private static readonly string[] sectionNames = { "chapter", "section", "subsection", "subsubsection", "paragraph", "subparagraph" };

        // packages whose commands are here, or that need none
        private static readonly HashSet<string> knownPackages = new HashSet<string>
        {
            "amsmath", "amssymb", "amsfonts", "geometry", "xcolor", "color", "hyperref", "url",
            "inputenc", "fontenc", "lmodern", "textcomp", "babel", "microtype", "graphicx", "graphics", "booktabs", "fancyhdr",
            "ulem", "enumitem", "titlesec", "mathptmx", "times", "tgtermes", "newtxtext", "setspace", "fontspec",
            "polyglossia", "caption", "indentfirst", "tocloft", "tocbibind", "biblatex", "silence", "amsthm", "algorithm"
        };

        // what TeX reads before a row's first cell: \noalign rules, \omit (\multicolumn) and the tabular's end
        private static readonly HashSet<string> rowStart = new HashSet<string>
        {
            "hline", "cline", "toprule", "midrule", "bottomrule", "cmidrule", "multicolumn", "endtabular"
        };

        // url.sty's \UrlBreaks, \UrlBigBreaks (- only under [hyphens]) and \UrlNoBreaks, unless the document defines them
        private const string UrlBreaks = ".@\\/!_|;>]),?&'+=#";
        private const string UrlBigBreaks = ":";
        private const string UrlHyphenBigBreaks = ":-";
        private const string UrlNoBreaks = "([{<";

        // a URL character as url.sty sets it: an ordinary, binary, relation or opening math atom
        private enum UrlAtom : byte { Ord, Bin, Rel, Open }

        // biblatex's \biburlsetup: its big and ordinary break characters
        private const string BibUrlBigBreaks = ":-";
        private const string BibUrlBreaks = ".@/\\!_|;>])},?'+=#$&*^\"";

        // packages that set the roman family in Times
        private static readonly HashSet<string> timesPackages = new HashSet<string> { "mathptmx", "times", "tgtermes", "newtxtext" };

        // article's \textwidth at 10pt, 11pt, 12pt and its \textheight, pt
        public static readonly float[] textWidths = { 345f, 360f, 390f };
        public const float TextHeight = 550f;
        private const float MmPerPt = 25.4f / 72.27f;

        // the standard classes' spacing at 10pt, 11pt, 12pt, pt: \parindent, \topsep, \itemsep (= \parsep), \intextsep (= \floatsep), \abovedisplayskip
        private static readonly float[] parIndents = { 15f, 17f, 18f };
        private static readonly float[] topSeps = { 8f, 9f, 10f };
        private static readonly float[] partopSeps = { 2f, 3f, 3f };
        private static readonly float[] itemSeps = { 4f, 4.5f, 5f };
        public static readonly float[] floatSeps = { 12f, 12f, 14f };
        public const float TextFloatSep = 20f;
        public static readonly float[] displaySkips = { 10f, 11f, 12f };
        public static readonly float[] belowDisplayShortSkips = { 6f, 6.5f, 6.5f };
        // the same skips' shrink, pt
        private static readonly float[] topSepShrinks = { 4f, 5f, 6f };
        public static readonly float[] inTextSepShrinks = { 2f, 2f, 4f };
        private static readonly float[] partopSepShrinks = { 1f, 1f, 2f };
        private static readonly float[] itemSepShrinks = { 1f, 1f, 1f };
        public static readonly float[] displaySkipShrinks = { 5f, 6f, 7f };
        public static readonly float[] belowDisplayShortSkipShrinks = { 3f, 3f, 3f };
        public static readonly float[] footnoteSkips = { 9f, 10f, 10.8f };

        private static readonly string[] rasterExtensions = { ".png", ".jpg", ".jpeg" };
        private static readonly string[] vectorExtensions = { ".pdf", ".eps", ".ps" };

        private static readonly Regex mathLabel = new Regex(@"\\label\s*\{([^}]*)\}");
        private static readonly Regex mathRef = new Regex(@"\\ref\s*\{([^}]*)\}");
        private static readonly Regex mathEqref = new Regex(@"\\eqref\s*\{([^}]*)\}");
        private static readonly Regex mathTag = new Regex(@"\\tag\s*(\*?)\s*\{([^}]*)\}");
        private static readonly Regex mathNotag = new Regex(@"\\(?:notag|nonumber)(?![a-zA-Z])\s*");

        // amsmath environments that live inside math, written back into the formula's source
        private static readonly HashSet<string> mathEnvironments = new HashSet<string>
        {
            "matrix", "pmatrix", "bmatrix", "Bmatrix", "vmatrix", "Vmatrix", "smallmatrix", "cases", "array",
            "aligned", "alignedat", "gathered", "split"
        };

        public readonly TexExpander expander;
        public List<TexError> errors => expander.errors;

        // the main vertical list, and every \footnote in order
        public readonly List<TexNode> vlist = new List<TexNode>();
        public readonly List<TexFootnote> footnotes = new List<TexFootnote>();

        // \hyphenation words
        public readonly Dictionary<string, bool[]> hyphenation = new Dictionary<string, bool[]>();

        // page styles: the document's, and every style a page can take, typeset once the document is read
        public string pageStyle = "plain";
        public readonly Dictionary<string, TexPageStyle> pageStyles = new Dictionary<string, TexPageStyle>();

        // what \thepage, \leftmark and \rightmark leave in a page style's slot
        public const char PageField = '';
        public const char LeftMarkField = '';
        public const char RightMarkField = '';

        // document class and page
        public string documentClass = "article";
        public int sizeOption;
        public string paper = "letter";
        public float? marginTop, marginBottom, marginLeft, marginRight;
        // geometry's headsep and footskip, mm; null keeps the page style's defaults
        public float? headSep, footSkip;
        // geometry's paperwidth/paperheight or \pdfpagewidth/\pdfpageheight, mm; fitHeight cuts the last page to its content plus this
        public float? customWidth, customHeight, fitHeight;
        // the roman family is Times (mathptmx and kin), not Latin Modern
        public bool times;
        // fontspec's main, sans and mono fonts as the source names them, and the files Path= gave a name
        public string? mainFont, sansFont, monoFont;
        public readonly Dictionary<string, (string regular, string? bold, string? italic, string? boldItalic)> fontFiles =
            new Dictionary<string, (string, string?, string?, string?)>(StringComparer.OrdinalIgnoreCase);
        // polyglossia and babel: the language in force, the main one a package set, and whether a heading's first paragraph indents
        private string language = "english";
        private string? mainLanguage;
        private bool indentFirst, indentFirstPackage;
        private readonly Dictionary<string, bool> indentFirsts = new Dictionary<string, bool> { ["lithuanian"] = true };

        // \label key -> its text, cited keys in citation order, \bibitem key -> its label
        public readonly Dictionary<string, string> labels = new Dictionary<string, string>();
        public readonly List<string> citations = new List<string>();
        public readonly Dictionary<string, string> bibLabels = new Dictionary<string, string>();

        private readonly ITexFontMetrics metrics;
        private readonly string? folder;
        private readonly Dictionary<string, Action<TexToken>> commands = new Dictionary<string, Action<TexToken>>();
        private readonly Stack<Frame> frames = new Stack<Frame>();

        // the state the save stack restores
        private TexStyle style;
        private ListFrame? list;
        private TexAlign align;
        private bool quote;
        private TexHBox? box;

        // \leaders: its box is next, then the box waiting for the glue it fills
        private bool leaderPending;
        private TexHBox? leaderBox;

        private Mode mode;
        private TexParagraph? par;
        private List<TexNode> vtarget;

        // what stops the next paragraph's indent: \noindent, a heading until text, an environment's end until a blank line
        private bool noIndent, afterHeading, afterEnvironment;

        // the skip under the heading being set, sp
        private float headingAfter;
        private bool inDocument = true;
        private bool done;
        private bool itemPending;
        private bool boundary;
        private bool suppressSpace;
        private bool warnedSmallCaps;

        // TeX's space factor, held while the node that set it is still last in the list
        private int spaceFactor = 1000;
        private TexNode? factorNode;
        private bool frenchSpacing;

        // last source line a dispatched token came from
        private int sourceLine;

        // references resolved once the document has been read
        private readonly List<TexRefNode> refs = new List<TexRefNode>();
        private readonly List<(TexMathNode node, int line)> mathRefs = new List<(TexMathNode, int)>();
        private string currentLabel = "";

        // floats, pictures and tabulars, innermost tabular last
        private string? floatKind;
        private TexFloat? openFloat;
        private int captionStart = -1, captionEnd = -1;
        private TexAlign captionJustify;
        private string algorithmStyle = "ruled";
        private List<string> graphicsPath = new List<string> { "" };
        private readonly List<TableBuild> tables = new List<TableBuild>();
        private readonly List<(List<TexNode> target, TexTable table)> unaligned = new List<(List<TexNode>, TexTable)>();

        // bibliography: \bibliographystyle, \bibliography's files and where each list stood
        private string bibStyle = "";
        private string[] bibFiles = Array.Empty<string>();
        private readonly List<(List<TexNode> list, TexBibliographyMark mark)> bibMarks = new List<(List<TexNode>, TexBibliographyMark)>(2);
        private bool allCited;
        private int bibCount;
        private int bibItems;
        // \parskip around the bibliography, as \@trivlist keeps it in \@outerparskip
        private TexGlue bibOuterParskip;
        // inside a biblatex bibliography, where \url takes \biburlsetup's breaks, penalties and stretch
        private bool bibUrls;
        // \usepackage[hyphens]{url}
        private bool urlHyphens;

        // biblatex: its settings once loaded, each entry's place in it
        private TexBiblatexSettings? biblatex;
        private readonly Dictionary<string, int> bibOrder = new Dictionary<string, int>();

        // contents: every list's entries as ranges of one token list in document order, where each \@starttoc stood,
        // the heading's \addcontentsline waiting for its paragraph, the entries keyed so far, where the bibliography's
        // entries go (-1 at the end), whether the one being typeset is an entry itself, and whether tocbibind makes every one
        private readonly List<TexToken> contentsTokens = new List<TexToken>(1024);
        private readonly List<(string extension, int start, int count)> contentsEntries = new List<(string, int, int)>(64);
        private readonly List<(List<TexNode> list, TexContentsMark mark)> contentsMarks = new List<(List<TexNode>, TexContentsMark)>(4);
        private TexToken[]? headingContents;
        private int contentsKeys;
        private int contentsAt = -1;
        private bool bibInContents;
        private bool bibTocbibind;

        // page style slot sources by style and place, the styles fancyhdr defined, the one \fancyhead writes to, the styles used,
        // what the next paragraph takes, the numbered heading whose marks are pending, and whether a slot is being typeset
        private readonly Dictionary<string, Dictionary<string, TexToken[]>> styleSlots = new Dictionary<string, Dictionary<string, TexToken[]>>();
        private readonly HashSet<string> fancyStyles = new HashSet<string> { "fancy" };
        private string fancyTarget = "fancy";
        private readonly HashSet<string> usedStyles = new HashSet<string>();
        private string? pendingStyle, pendingMarkLeft, pendingMarkRight;
        private (int depth, string number)? headingMark;
        private bool inSlot;

        // what the preamble typesets, dropped
        private readonly List<TexNode> preamble = new List<TexNode>();
        private bool preambleReported;

        // \definecolor names
        private readonly Dictionary<string, (int r, int g, int b)> definedColors = new Dictionary<string, (int, int, int)>();

        // enumitem: \setlist keys by list name ("" for every list)
        private readonly Dictionary<string, ListSettings> listSettings = new Dictionary<string, ListSettings>();

        // titlesec: \titleformat and \titlespacing by section depth, and the after-code of the heading being set
        private readonly Dictionary<int, TitleFormat> titleFormats = new Dictionary<int, TitleFormat>();
        private TexToken[]? headingAfterCode;
        private TexStyle? headingOuterStyle;

        // \@startsection's format for the heading it starts
        private TitleFormat? startSection;

        // the sectioning command being dispatched has had its titlesec \<name>break inserted
        private bool sectionBreakRun;

        // class and package options: the \documentclass options, and the file being read's \DeclareOption code and given options
        private string[] classOptions = Array.Empty<string>();
        private Dictionary<string, TexToken[]> declaredOptions = new Dictionary<string, TexToken[]>();
        private TexToken[]? otherOption;
        private string[] givenOptions = Array.Empty<string>();

        // classes and packages read from beside the source
        private readonly HashSet<string> loadedFiles = new HashSet<string>();

        // \fontsize waiting for \selectfont, sp; how many \MakeUppercase arguments are being set
        private int? pendingSize;
        private int pendingSkip;
        private int uppercase;

        // \baselinestretch now, and the one the document's first paragraph ended under
        private float lineStretch = 1f;
        private float? bodyStretch;
        // setspace loaded: floats and footnotes are single-spaced
        private bool setspace;
        // unicode-math or mathspec loaded: math is placed by the font's OpenType constants, else by classic TeX's
        public bool unicodeMath { get; private set; }
        // \centerline's line: a box on the page, so the \baselineskip outside it, not its own; sp, 0 = none
        private int centerSkip, keepSkip;
        // a trivlist environment's first paragraph: its \parskip is taken back, as \@item's \addvspace{-\parskip} does
        private bool environmentStart;
        // the open center, flushleft or flushright's \topsep and \partopsep, sp
        private TexGlue alignedSkip;

        private List<TexNode> HList => box?.list ?? par!.list;

        // \normalsize at the class option, sp
        public int normalSize => Size(NormalSize);

        // \parindent in pt, and whether \hyphenpenalty allows hyphens
        public float parIndent => (expander.RegisterValue("parindent") ?? 0) / (float)Unity;
        public bool hyphenate => (expander.RegisterValue("hyphenpenalty") ?? 0) < 10000;

        // \tolerance, \pretolerance and \emergencystretch (pt) at the end of the document
        public int tolerance => expander.RegisterValue("tolerance") ?? 200;
        public int pretolerance => expander.RegisterValue("pretolerance") ?? 100;
        public float emergencyStretch => (expander.RegisterValue("emergencystretch") ?? 0) / (float)Unity;

        // the \baselinestretch the whole document is set at
        public float lineSpread => bodyStretch ?? lineStretch;

        // setspace's \displayskipstretch, which also makes the display skips rigid; null without setspace
        public float? displaySkipStretch => setspace ? lineSpread : null;

        // the body's line height as a multiple of \normalsize: its \baselineskip, stretched
        public float lineHeight => baselineSkipPoints[sizeOption][NormalSize] / sizePoints[sizeOption][NormalSize] * lineSpread;

        private bool Chapters => documentClass is "report" or "book";

        // paper, mm
        public float paperWidth => customWidth ?? (paper == "a4" ? 210f : 215.9f);
        public float paperHeight => customHeight ?? (paper == "a4" ? 297f : 279.4f);

        // \textwidth and \textheight, sp: the class's, or the paper less geometry's margins
        public int textWidth => Extent(paperWidth, marginLeft, marginRight, textWidths[sizeOption]);
        public int textHeight => Extent(paperHeight, marginTop, marginBottom, TextHeight);

        // folder is where pictures and .bib files are looked for; null finds none.
        public TexTypesetter(string source, ITexFontMetrics? metrics = null, string? folder = null)
        {
            this.metrics = metrics ?? new FixedMetrics();
            this.folder = folder;
            vtarget = vlist;
            style = new TexStyle(new TexFont(TexFamily.Roman, false, false, Size(NormalSize)), null, false);
            expander = new TexExpander(source, TexFormat.Prelude);
            expander.quad = () => this.metrics.Quad(style.font);
            expander.xHeight = () => this.metrics.XHeight(style.font);
            Register();
            DefaultPageStyles();
        }

        public void Run()
        {
            while (!done && expander.Next(out TexToken t)) Dispatch(t);
            EndParagraph();
            TypesetBibliography();
            TypesetContents();
            TypesetPageStyles();
            Resolve();
        }

        private void Dispatch(TexToken t)
        {
            if (t.line > 0) sourceLine = t.line;
            if (tables.Count > 0 && tables[^1].cell == null && (t.IsCs ? commands.ContainsKey(t.name!) && !rowStart.Contains(t.name!) : t.cat != TexCatcode.Space))
                StartCell();
            if (!t.IsCs) Character(uppercase > 0 && char.IsLower(t.ch) ? TexToken.Char(char.ToUpperInvariant(t.ch), t.cat, t.line, t.column) : t);
            else if (commands.TryGetValue(t.name!, out Action<TexToken>? command)) command(t);
        }

        #region ---- commands ----
        private void Register()
        {
            void On(string name, Action<TexToken> action)
            {
                expander.DefinePrimitive(name);
                commands[name] = action;
            }

            On("documentclass", DocumentClass);
            On("usepackage", UsePackage);
            On("geometry", t => Geometry(expander.Argument(t, true)));
            On("LoadClass", t =>
            {
                TexToken[]? options = expander.Optional(t);
                string? name = expander.NameArgument(t);
                if (name == null) return;
                expander.Optional(t);
                SetClass(options, name);
            });
            On("RequirePackage", t =>
            {
                TexToken[]? options = expander.Optional(t);
                string? names = expander.NameArgument(t);
                if (names == null) return;
                expander.Optional(t);
                Packages(options, names);
            });
            foreach (string name in new[] { "ProvidesClass", "ProvidesPackage", "NeedsTeXFormat" })
                On(name, t =>
                {
                    if (expander.Argument(t, false) != null) expander.Optional(t);
                });
            On("DeclareOption", DeclareOption);
            On("ProcessOptions", t =>
            {
                expander.TakeStar();
                RunOptions(givenOptions);
            });
            On("ExecuteOptions", t =>
            {
                string? names = expander.NameArgument(t);
                if (names != null) RunOptions(names.Split(',').Select(n => n.Trim()));
            });
            On("@startsection", StartSection);
            On("input", t => InputFile(t, false));
            On("include", t => InputFile(t, true));
            On("centerline", t =>
            {
                TexToken[]? body = expander.Argument(t, true);
                if (body == null) return;
                centerSkip = BaselineSkip(style);
                expander.Insert(new[] { TexToken.Cs("par") }.Concat(Grouped(new[] { TexToken.Cs("centering"), TexToken.Cs("@keepskip") }.Concat(body)
                    .Append(TexToken.Cs("par")))).ToArray());
            });
            On("@keepskip", _ => keepSkip = centerSkip);
            On("fontsize", t =>
            {
                TexToken[]? size = expander.Argument(t, false);
                TexToken[]? skip = size == null ? null : expander.Argument(t, false);
                if (skip == null) return;
                pendingSize = Dimen(size!);
                pendingSkip = Dimen(skip);
            });
            On("selectfont", _ =>
            {
                if (pendingSize is int size) SetStyle(style with { font = style.font with { size = size }, baselineSkip = pendingSkip });
                pendingSize = null;
            });
            On("setstretch", t =>
            {
                if (expander.Argument(t, false) is TexToken[] factor) Stretch(Number(factor));
            });
            On("linespread", t =>
            {
                if (expander.Argument(t, false) is TexToken[] factor) Stretch(Number(factor));
            });
            On("onehalfspacing", _ => Stretch(oneHalfStretches[sizeOption]));
            On("doublespacing", _ => Stretch(doubleStretches[sizeOption]));
            On("singlespacing", _ => Stretch(1f));
            On("MakeUppercase", t =>
            {
                TexToken[]? body = expander.Argument(t, true);
                if (body == null) return;
                uppercase++;
                expander.Insert(Grouped(body.Append(TexToken.Cs("@endupper"))));
            });
            On("@endupper", _ => uppercase--);
            On("titlepage", _ =>
            {
                PageBreak(true);
                expander.Insert(new[] { TexToken.Cs("thispagestyle") }.Concat(Grouped(Chars("empty"))).ToArray());
            });
            On("endtitlepage", _ => PageBreak(true));
            On("@ifundefined", t =>
            {
                string? name = expander.NameArgument(t);
                TexToken[]? yes = name == null ? null : expander.Argument(t, true);
                TexToken[]? no = yes == null ? null : expander.Argument(t, true);
                if (no == null) return;
                expander.Insert(new[] { TexToken.Cs("ifcsname") }.Concat(Chars(name!.Trim())).Append(TexToken.Cs("endcsname"))
                    .Concat(no).Append(TexToken.Cs("else")).Concat(yes!).Append(TexToken.Cs("fi")).ToArray());
            });

            // fontspec
            On("setmainfont", t => mainFont = FontName(t));
            On("setsansfont", t => sansFont = FontName(t));
            On("setmonofont", t => monoFont = FontName(t));
            On("fontspec", t =>
            {
                if (FontName(t) is string name) SetFont(style.font with { face = name });
            });
            On("newfontface", NewFontFace);
            On("newfontfamily", NewFontFace);

            // polyglossia and babel
            On("setmainlanguage", MainLanguage);
            On("setdefaultlanguage", MainLanguage);
            On("setotherlanguage", t =>
            {
                expander.Optional(t);
                expander.NameArgument(t);
            });
            On("setotherlanguages", t => expander.NameArgument(t));
            On("SetLanguageKeys", SetLanguageKeys);
            On("selectlanguage", t =>
            {
                if (expander.NameArgument(t) is string name) SelectLanguage(name.Trim(), true);
            });
            On("foreignlanguage", t =>
            {
                expander.Optional(t);
                TexToken[]? text = expander.NameArgument(t) == null ? null : expander.Argument(t, true);
                if (text != null) expander.Insert(Grouped(text));
            });
            foreach (string name in new[] { "otherlanguage", "otherlanguage*" })
            {
                bool captions = name == "otherlanguage";
                On(name, t =>
                {
                    expander.Optional(t);
                    if (expander.NameArgument(t) is string other) SelectLanguage(other.Trim(), captions);
                });
                On("end" + name, _ => { });
            }

            // caption
            On("captionsetup", CaptionSetup);

            // a hyperref anchor; nothing to set
            On("phantomsection", _ => { });
            On("printbibliography", PrintBibliography);
            On("addbibresource", t =>
            {
                expander.Optional(t);
                if (expander.NameArgument(t)?.Trim() is not string file) return;
                string[] files = new string[bibFiles.Length + 1];
                bibFiles.CopyTo(files, 0);
                files[^1] = file;
                bibFiles = files;
            });
            On("DefineBibliographyStrings", DefineBibliographyStrings);
            On("DeclareDelimFormat", t =>
            {
                expander.TakeStar();
                TexToken[]? context = expander.Optional(t);
                string? name = expander.NameArgument(t)?.Trim();
                TexToken[]? code = name == null ? null : expander.Argument(t, true);
                if (code != null && context == null && biblatex != null) biblatex.delims[name!] = Source(code);
            });
            On("DeclareNameAlias", t =>
            {
                string? alias = expander.NameArgument(t)?.Trim();
                string? format = alias == null ? null : expander.NameArgument(t)?.Trim();
                if (format == null || biblatex == null) return;
                if (alias is "default" or "author" or "editor" && format is "given-family" or "family-given" or "family-given/given-family")
                    biblatex.nameFormat = format;
                else Log.Info($"biblatex name alias {alias} = {format} is not supported and is ignored");
            });
            On("DeclareFieldFormat", t =>
            {
                expander.TakeStar();
                expander.Optional(t);
                if (expander.Argument(t, true) != null && expander.Argument(t, true) != null) Log.Info($"biblatex {t} is read and ignored");
            });
            foreach (string name in new[] { "newbibmacro", "renewbibmacro", "providebibmacro" })
                On(name, t =>
                {
                    expander.TakeStar();
                    if (expander.Argument(t, true) == null) return;
                    expander.Optional(t);
                    expander.Optional(t);
                    if (expander.Argument(t, true) != null) Log.Info($"biblatex {t} is read and ignored");
                });
            On("DeclareBibliographyDriver", t =>
            {
                if (expander.Argument(t, true) != null && expander.Argument(t, true) != null) Log.Info($"biblatex {t} is read and ignored");
            });
            On("defbibheading", t =>
            {
                if (expander.Argument(t, true) == null) return;
                expander.Optional(t);
                if (expander.Argument(t, true) != null) Log.Info($"biblatex {t} is read and ignored");
            });
            On("WarningFilter", t =>
            {
                expander.TakeStar();
                expander.Optional(t);
                if (expander.Argument(t, true) != null) expander.Argument(t, true);
            });
            expander.InsertSource($"\\countdef\\year=247 \\year={DateTime.Today.Year}\\relax ");
            On("document", _ =>
            {
                EndParagraph();
                inDocument = true;
                vtarget = vlist;
                PageDimens();
                if (mainLanguage != null) SelectLanguage(mainLanguage, true);
            });
            On("@pagedimens", _ => PageDimens());
            On("enddocument", _ =>
            {
                EndParagraph();
                done = true;
            });
            On("today", _ => AddText(Today()));
            On("pdfpageheight", _ => PdfPage(false));
            On("pdfpagewidth", _ => PdfPage(true));
            On("definecolor", DefineColor);
            On("newlist", NewList);
            On("setlist", SetList);
            On("titleformat", TitleFormatCommand);
            On("titlespacing", TitleSpacing);
            On("@endheadafter", _ =>
            {
                EndParagraph();
                if (headingOuterStyle is TexStyle outer) style = outer;
                headingOuterStyle = null;
                VSkip(new TexGlue { width = (int)headingAfter });
                afterHeading = true;
            });
            On("rule", RuleBox);

            for (int i = 0; i < sectionNames.Length; i++)
            {
                int depth = i;
                On(sectionNames[i], t =>
                {
                    if (!sectionBreakRun)
                    {
                        sectionBreakRun = true;
                        string name = sectionNames[depth] + "break";
                        expander.Insert(new[] { TexToken.Cs("ifcsname") }.Concat(Chars(name))
                            .Concat(new[] { TexToken.Cs("endcsname"), TexToken.Cs(name), TexToken.Cs("fi"), t }).ToArray());
                        return;
                    }
                    sectionBreakRun = false;
                    Section(t, depth);
                });
            }
            On("@endhead", _ =>
            {
                TexStyle inHeading = style;
                Resume();
                if (headingMark is (int depth, string number) && vtarget.Count > 0 && vtarget[^1] is TexParagraph heading)
                    HeadingMarks(heading, depth, number);
                headingMark = null;
                afterHeading = true;
                if (headingAfterCode != null)
                {
                    headingOuterStyle = style;
                    style = inHeading;
                    expander.Insert(headingAfterCode.Append(TexToken.Cs("@endheadafter")).ToArray());
                    headingAfterCode = null;
                }
                else VSkip(new TexGlue { width = (int)headingAfter });
                InsertHeadingContents();
            });
            On("@runin", _ =>
            {
                Space(1f);
                suppressSpace = true;
                InsertHeadingContents();
            });
            On("addcontentsline", AddContentsLine);
            On("addtocontents", t =>
            {
                string? extension = expander.NameArgument(t)?.Trim();
                TexToken[]? text = extension == null ? null : expander.Argument(t, true);
                if (text == null) return;
                int start = contentsTokens.Count;
                contentsTokens.AddRange(Protected(text, t));
                AddContents(extension!, start);
            });
            On("@starttoc", t =>
            {
                string? extension = expander.NameArgument(t)?.Trim();
                if (extension == null) return;
                EndParagraph();
                EnsureDocument();
                TexContentsMark mark = new TexContentsMark(extension);
                vtarget.Add(mark);
                contentsMarks.Add((vtarget, mark));
            });
            On("@endcontents", _ => done = true);
            On("@afterindenttrue", _ => afterHeading = false);

            On("bfseries", _ => SetFont(style.font with { bold = true }));
            On("mdseries", _ => SetFont(style.font with { bold = false }));
            On("itshape", _ => SetFont(style.font with { italic = true }));
            On("slshape", _ => SetFont(style.font with { italic = true }));
            On("upshape", _ => SetFont(style.font with { italic = false }));
            On("scshape", _ =>
            {
                if (!warnedSmallCaps) Log.Info($"small caps are set upright: Latin Modern has no small-caps face here");
                warnedSmallCaps = true;
                SetFont(style.font with { italic = false });
            });
            On("rmfamily", _ => SetFont(style.font with { family = TexFamily.Roman, face = null }));
            On("sffamily", _ => SetFont(style.font with { family = TexFamily.Sans, face = null }));
            On("ttfamily", _ => SetFont(style.font with { family = TexFamily.Mono, face = null }));
            On("normalfont", _ => SetFont(style.font with { family = TexFamily.Roman, bold = false, italic = false, face = null }));
            On("em", _ => SetFont(style.font with { italic = !style.font.italic }));
            for (int i = 0; i < sizeNames.Length; i++)
            {
                int size = i;
                On(sizeNames[i], _ => SetStyle(style with { font = style.font with { size = Size(size) }, baselineSkip = 0 }));
            }
            On("@underline", _ => SetStyle(style with { underline = true }));
            On("color", Color);

            On("itemize", t => BeginList(TexListKind.Itemize, "itemize", t));
            On("enumerate", t => BeginList(TexListKind.Enumerate, "enumerate", t));
            On("description", t => BeginList(TexListKind.Description, "description", t));
            foreach (string name in new[] { "enditemize", "endenumerate", "enddescription" })
                On(name, _ => EndEnvironment(list?.depth ?? 1, list?.topSkip));
            foreach (string name in new[] { "endquote", "endquotation" })
                On(name, _ => EndEnvironment((list?.depth ?? 0) + 1));
            foreach (string name in new[] { "endcenter", "endflushleft", "endflushright" })
                On(name, _ => EndEnvironment((list?.depth ?? 0) + 1, alignedSkip));
            On("item", Item);
            On("@label", _ =>
            {
                Interword();
                suppressSpace = true;
            });
            On("quote", _ => BeginQuote());
            On("quotation", _ => BeginQuote());
            On("center", _ => BeginAligned(TexAlign.Center));
            On("flushleft", _ => BeginAligned(TexAlign.Left));
            On("flushright", _ => BeginAligned(TexAlign.Right));
            On("centering", _ => SetAlign(TexAlign.Center));
            On("raggedright", _ => SetAlign(TexAlign.Left));
            On("raggedleft", _ => SetAlign(TexAlign.Right));
            On("verbatim", Verbatim);
            On("verb", Verb);

            On("(", _ => MathUntil(TexToken.Cs(")"), false));
            On("[", _ => MathUntil(TexToken.Cs("]"), true));
            On(")", _ => expander.Error("LaTeX Error: Bad math environment delimiter"));
            On("]", _ => expander.Error("LaTeX Error: Bad math environment delimiter"));
            foreach ((string env, bool display) in new[] { ("displaymath", true), ("math", false) })
            {
                On(env, _ => MathUntil(TexToken.Cs("end" + env), display));
                On("end" + env, _ => { });
            }
            foreach (string env in new[] { "equation", "equation*", "align", "align*", "gather", "gather*", "flalign", "flalign*",
                         "alignat", "alignat*", "multline", "multline*" })
            {
                On(env, _ => MathUntil(TexToken.Cs("end" + env), true, env));
                On("end" + env, _ => { });
            }
            foreach (string env in mathEnvironments)
            {
                On(env, _ => expander.Error("Missing $ inserted"));
                On("end" + env, _ => { });
            }
            On("eqref", t =>
            {
                AddText("(");
                Reference(t, false);
                AddText(")");
            });
            On("footnote", Footnote);
            On("@endnote", _ => Resume());

            On("label", Label);
            On("ref", t => Reference(t, false));
            On("pageref", t => Reference(t, false, true));

            On("newpage", _ => PageBreak(false));
            On("clearpage", _ => PageBreak(true));
            On("pagestyle", t => SetPageStyle(t, false));
            On("thispagestyle", t => SetPageStyle(t, true));
            On("markboth", t => Mark(t, true));
            On("markright", t => Mark(t, false));
            On("thepage", _ => Field(PageField));
            On("leftmark", _ => Field(LeftMarkField));
            On("rightmark", _ => Field(RightMarkField));
            On("@endtypeset", _ => Resume());
            On("fancyhf", t => FancySlots(t, "HF"));
            On("fancyhead", t => FancySlots(t, "H"));
            On("fancyfoot", t => FancySlots(t, "F"));
            foreach ((string name, string place) in new[] { ("lhead", "HeadLeft"), ("chead", "HeadCenter"), ("rhead", "HeadRight"),
                         ("lfoot", "FootLeft"), ("cfoot", "FootCenter"), ("rfoot", "FootRight") })
                On(name, t =>
                {
                    TexToken[]? body = expander.Argument(t, true);
                    if (body != null) SetSlot(place, body);
                });
            On("fancypagestyle", FancyPageStyle);
            On("@endfancystyle", _ => fancyTarget = "fancy");
            On("cite", t => Reference(t, true));
            On("parencite", t => Reference(t, true));
            On("autocite", t => Reference(t, true));
            On("nocite", NoCite);
            On("bibliographystyle", t => bibStyle = expander.NameArgument(t)?.Trim() ?? bibStyle);
            On("bibliography", Bibliography);
            On("thebibliography", BeginBibliography);
            On("endthebibliography", _ => EndParagraph());
            On("bibitem", BibItem);
            On("@endbib", _ => done = true);

            foreach (string kind in new[] { "figure", "table" })
                foreach (string name in new[] { kind, kind + "*" })
                {
                    On(name, t => BeginFloat(t, kind));
                    On("end" + name, _ => EndFloat());
                }
            On("caption", Caption);
            On("@endcaption", _ => EndCaption());
            On("includegraphics", IncludeGraphics);
            On("graphicspath", GraphicsPath);

            On("tabular", BeginTabular);
            On("endtabular", _ => EndTabular());
            On("multicolumn", MultiColumn);
            On("hline", _ => Rule(TexRuleKind.Plain));
            On("cline", t =>
            {
                TexToken[]? range = expander.Argument(t, false);
                if (range == null) return;
                (int from, int to) = Columns(range);
                Rule(TexRuleKind.Plain, from, to);
            });
            foreach ((string name, TexRuleKind kind) in new[] { ("toprule", TexRuleKind.Heavy), ("midrule", TexRuleKind.Light), ("bottomrule", TexRuleKind.Heavy) })
                On(name, t => Rule(kind, width: OptionalWidth(expander.Optional(t))));
            On("cmidrule", t =>
            {
                int width = OptionalWidth(expander.Optional(t));
                bool left = false, right = false;
                if (expander.ScanKeyword("("))
                    while (!expander.ScanKeyword(")") && expander.Next(out TexToken trim))
                    {
                        if (trim.IsCs) continue;
                        left |= trim.ch == 'l';
                        right |= trim.ch == 'r';
                    }
                TexToken[]? range = expander.Argument(t, false);
                if (range == null) return;
                (int from, int to) = Columns(range);
                Rule(TexRuleKind.Cmid, from, to, width, left, right);
            });

            On("\\", t =>
            {
                expander.TakeStar();
                TexToken[]? skip = expander.Optional(t);
                if (box != null) return;
                if (tables.Count > 0) EndRow();
                else if (mode == Mode.Vertical) expander.Error("LaTeX Error: There's no line here to end");
                else if (align != TexAlign.Justify) CenterCr(skip);
                else HList.Add(new TexPenalty(TexPenalty.Forced));
            });
            On("hskip", _ =>
            {
                TexGlue glue = expander.ScanGlue();
                StartParagraph();
                HList.Add(new TexGlueNode(glue, false, style) { leader = leaderBox });
                leaderBox = null;
            });
            On("leaders", _ =>
            {
                StartParagraph();
                leaderPending = true;
            });
            On("settowidth", SetToWidth);
            On("ignorespaces", _ => suppressSpace = true);
            On("@", _ => spaceFactor = 1000);
            On("frenchspacing", _ => FrenchSpacing(true));
            On("nonfrenchspacing", _ => FrenchSpacing(false));
            On("vskip", _ =>
            {
                TexGlue glue = expander.ScanGlue();
                EndParagraph();
                vtarget.Add(new TexVGlue(glue));
            });
            On("kern", _ =>
            {
                int width = expander.ScanDimen();
                if (mode == Mode.Horizontal) HList.Add(new TexKern(width, style));
                else vtarget.Add(new TexVGlue(new TexGlue { width = width }));
            });
            On("mskip", _ =>
            {
                TexGlue mu = expander.ScanGlue(true);
                TexGlue glue = new TexGlue
                {
                    width = MuToSp(mu.width), stretchOrder = mu.stretchOrder, shrinkOrder = mu.shrinkOrder,
                    stretch = mu.stretchOrder > 0 ? mu.stretch : MuToSp(mu.stretch), shrink = mu.shrinkOrder > 0 ? mu.shrink : MuToSp(mu.shrink)
                };
                StartParagraph();
                HList.Add(new TexGlueNode(glue, false, style) { leader = leaderBox });
                leaderBox = null;
            });
            On("mkern", _ =>
            {
                int width = MuToSp(expander.ScanDimen(true));
                StartParagraph();
                HList.Add(new TexKern(width, style));
            });
            On("penalty", _ =>
            {
                int penalty = expander.ScanInt();
                if (mode == Mode.Horizontal) HList.Add(new TexPenalty(penalty));
                else vtarget.Add(new TexVPenalty(penalty));
            });
            On("hrule", _ =>
            {
                while (expander.ScanKeyword("width") || expander.ScanKeyword("height") || expander.ScanKeyword("depth"))
                    expander.ScanDimen();
                EndParagraph();
                vtarget.Add(new TexRuleNode());
            });
            On("quad", _ => Space(1f));
            On("qquad", _ => Space(2f));
            On(",", _ => Kern(3f / 18f));
            On(":", _ => Kern(4f / 18f));
            On(";", _ => Kern(5f / 18f));
            On("!", _ => Kern(-3f / 18f));
            On(" ", _ => Interword());
            On("nobreakspace", _ =>
            {
                StartParagraph();
                HList.Add(new TexPenalty(10000));
                Interword();
            });
            On("-", _ => AddText("­"));
            On("hyphenation", t =>
            {
                TexToken[]? words = expander.Argument(t, true);
                if (words == null) return;
                foreach (string word in Text(words).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    if (TexHyphenator.Exception(word) is (string plain, bool[] points)) hyphenation[plain] = points;
            });
            On("noindent", _ =>
            {
                if (mode == Mode.Horizontal) return;
                noIndent = true;
                StartParagraph();
            });
            On("indent", _ => StartParagraph());
            On("mbox", Box);
            On("hbox", Box);
            On("url", t =>
            {
                TexToken[]? arg = expander.Argument(t, true);
                if (arg == null) return;
                TexStyle outer = style;
                style = style with { font = style.font with { family = TexFamily.Mono, face = null } };
                style = bibUrls
                    ? style with { urlBreakPenalty = expander.Counter("biburlbreakpenalty"), urlBigBreakPenalty = expander.Counter("biburlbigbreakpenalty") }
                    : style with
                    {
                        urlBreakPenalty = expander.RegisterValue("UrlBreakPenalty") ?? expander.RegisterValue("binoppenalty") ?? 0,
                        urlBigBreakPenalty = expander.RegisterValue("UrlBigBreakPenalty") ?? expander.RegisterValue("relpenalty") ?? 0
                    };
                TexGlue mu = expander.RegisterMuGlue(bibUrls ? "biburlbigskip" : "Urlmuskip") ?? default;
                if (mu.stretchOrder > 0 || mu.shrinkOrder > 0) Log.Once().Info($"infinite \\Urlmuskip stretch or shrink in a URL is set as finite");
                style = style with { urlWidth = MuToSp(mu.width), urlStretch = MuToSp(mu.stretch), urlShrink = MuToSp(mu.shrink) };
                if (bibUrls)
                {
                    (int penalty, int width, int stretch, int shrink) num = UrlLetters("biburlnumpenalty", "biburlnumskip");
                    (int penalty, int width, int stretch, int shrink) uc = UrlLetters("biburlucpenalty", "biburlucskip");
                    (int penalty, int width, int stretch, int shrink) lc = UrlLetters("biburllcpenalty", "biburllcskip");
                    style = style with
                    {
                        urlNumPenalty = num.penalty, urlNumWidth = num.width, urlNumStretch = num.stretch, urlNumShrink = num.shrink,
                        urlUcPenalty = uc.penalty, urlUcWidth = uc.width, urlUcStretch = uc.stretch, urlUcShrink = uc.shrink,
                        urlLcPenalty = lc.penalty, urlLcWidth = lc.width, urlLcStretch = lc.stretch, urlLcShrink = lc.shrink
                    };
                }
                string breaks = bibUrls ? BibUrlBreaks : UrlCharacters("UrlBreaks", UrlBreaks);
                string bigBreaks = bibUrls ? BibUrlBigBreaks : UrlCharacters("UrlBigBreaks", urlHyphens ? UrlHyphenBigBreaks : UrlBigBreaks);
                string noBreaks = UrlCharacters("UrlNoBreaks", UrlNoBreaks);
                string url = Text(arg).Replace("\\%", "%").Replace("\\#", "#").Replace("\\_", "_").Replace("\\&", "&");
                StringBuilder broken = new StringBuilder(url.Length + 16);
                UrlAtom previous = UrlAtom.Open;
                for (int i = 0; i < url.Length; i++)
                {
                    UrlAtom atom = AtomOf(url[i], breaks, bigBreaks, noBreaks);
                    UrlAtom next = i + 1 < url.Length ? AtomOf(url[i + 1], breaks, bigBreaks, noBreaks) : UrlAtom.Ord;
                    if (atom == UrlAtom.Bin && (previous != UrlAtom.Ord || next == UrlAtom.Rel)) atom = UrlAtom.Ord;
                    broken.Append(url[i]);
                    if (i < url.Length - 1 && atom == UrlAtom.Bin) broken.Append('');
                    else if (i < url.Length - 1 && atom == UrlAtom.Rel && next != UrlAtom.Rel) broken.Append('');
                    if (i < url.Length - 1 && bibUrls && UrlLetterMarker(url[i], style) is char marker) broken.Append(marker);
                    previous = atom;
                }
                AddText(broken.ToString());
                style = outer;
            });
            On("href", t =>
            {
                if (expander.Argument(t, true) == null) return;
                TexToken[]? text = expander.Argument(t, true);
                if (text == null) return;
                expander.Insert(Grouped(new[] { TexToken.Cs("@underline") }.Concat(text)));
            });
            On("hyperref", t =>
            {
                if (expander.Optional(t) == null) return;
                TexToken[]? text = expander.Argument(t, true);
                if (text != null) expander.Insert(Grouped(text));
            });

            foreach (KeyValuePair<string, string> symbol in symbols)
            {
                string text = symbol.Value;
                On(symbol.Key, _ => AddText(text));
            }
            foreach (KeyValuePair<string, char> accent in accents)
            {
                char mark = accent.Value;
                On(accent.Key, t => Accent(t, mark));
            }

            commands["par"] = _ =>
            {
                if (mode == Mode.Vertical) afterEnvironment = false;
                EndParagraph();
            };
            commands["relax"] = _ => { };
        }

        private void DocumentClass(TexToken t)
        {
            TexToken[]? options = expander.Optional(t);
            string? name = expander.NameArgument(t);
            if (name == null) return;
            inDocument = false;
            vtarget = preamble;
            classOptions = Options(options).ToArray();
            if (!ReadPackage(name + ".cls", classOptions)) SetClass(null, name);
        }

        // A class or package beside the source, read with @ a letter and its own options; false when there is none.
        private bool ReadPackage(string file, IEnumerable<string> options)
        {
            string? path = folder == null ? null : Path.Combine(folder, file);
            if (path == null || !File.Exists(path)) return false;
            if (!loadedFiles.Add(file)) return true;

            (Dictionary<string, TexToken[]> declared, TexToken[]? other, string[] given) = (declaredOptions, otherOption, givenOptions);
            (declaredOptions, otherOption, givenOptions) = (new Dictionary<string, TexToken[]>(), null, options.ToArray());
            ReadCode(VaultFiles.ReadText(path), () => (declaredOptions, otherOption, givenOptions) = (declared, other, given));
            return true;
        }

        // \input{name} or \include{name}: name.tex or name beside the source; \include clears the page around it.
        private void InputFile(TexToken t, bool include)
        {
            string? name = expander.NameArgument(t)?.Trim();
            if (name == null) return;
            string? path = folder == null ? null : new[] { name + ".tex", name }.Select(n => Path.Combine(folder, n)).FirstOrDefault(File.Exists);
            if (path == null)
            {
                expander.Error($"LaTeX Error: File `{name}.tex' not found");
                return;
            }
            if (include) PageBreak(true);
            expander.InputSource(VaultFiles.ReadText(path), include ? () => expander.Insert(new[] { TexToken.Cs("clearpage") }) : null);
        }

        // The standard class a \documentclass or \LoadClass names, at its options and the \documentclass options.
        private void SetClass(TexToken[]? options, string name)
        {
            foreach (string option in Options(options).Concat(classOptions))
                switch (option)
                {
                    case "10pt": sizeOption = 0; break;
                    case "11pt": sizeOption = 1; break;
                    case "12pt": sizeOption = 2; break;
                    case "a4paper": paper = "a4"; break;
                    case "letterpaper": paper = "letter"; break;
                }
            if (name is "article" or "report" or "book") documentClass = name;
            else Log.Info($"document class {name} is set as article");
            pageStyle = documentClass == "book" ? "headings" : "plain";
            style = style with { font = style.font with { size = Size(NormalSize) } };
            expander.InsertSource($"\\global\\parindent={parIndents[sizeOption].ToString(CultureInfo.InvariantCulture)}pt\\relax ");
            if (Chapters) expander.InsertSource(@"\gdef\thesection{\thechapter.\arabic{section}}");
            if (Chapters) ReadCode(TexFormat.ChapterContents);
        }

        private void UsePackage(TexToken t)
        {
            TexToken[]? options = expander.Optional(t);
            string? names = expander.NameArgument(t);
            if (names == null) return;
            if (inDocument) expander.Error("LaTeX Error: Can be used only in preamble");
            Packages(options, names);
        }

        private void Packages(TexToken[]? options, string names)
        {
            List<string> local = new List<string>();
            foreach (string name in names.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0))
            {
                if (folder != null && File.Exists(Path.Combine(folder, name + ".sty")))
                {
                    local.Add(name);
                    continue;
                }
                if (name == "geometry" && options != null) Geometry(options);
                if (name == "babel") BabelLanguages(options);
                if (name == "indentfirst") indentFirstPackage = indentFirst = true;
                if (name == "setspace") setspace = true;
                if (name == "unicode-math" || name == "mathspec") unicodeMath = true;
                if (name == "tocloft" && loadedFiles.Add(name)) ReadCode(TexFormat.Tocloft);
                if (name == "amsthm" && loadedFiles.Add(name)) ReadCode(TexFormat.Amsthm);
                if (name == "algorithm" && loadedFiles.Add(name)) Algorithm(options);
                if (name == "tocbibind" && loadedFiles.Add(name)) TocBibind(options);
                if (name == "biblatex") BiblatexOptions(options);
                if (name == "url")
                    foreach (string option in Options(options))
                        if (option == "hyphens") urlHyphens = true;
                if (name is "url" or "hyperref" && !expander.IsDefined("UrlBreakPenalty"))
                    expander.InsertSource("\\mathchardef\\UrlBreakPenalty=\\binoppenalty \\mathchardef\\UrlBigBreakPenalty=\\relpenalty ");
                if (name is "url" or "hyperref" && !expander.IsDefined("Urlmuskip")) expander.InsertSource("\\newmuskip\\Urlmuskip \\Urlmuskip=0mu ");
                if (timesPackages.Contains(name)) times = true;
                if (!knownPackages.Contains(name)) Log.Info($"package {name} is not supported and is ignored");
            }
            for (int i = local.Count - 1; i >= 0; i--)
                ReadPackage(local[i] + ".sty", Options(options));
        }

        // biblatex's key=value options; its punctuation commands when it first loads.
        private void BiblatexOptions(TexToken[]? options)
        {
            if (biblatex == null)
            {
                biblatex = new TexBiblatexSettings();
                ReadCode(TexFormat.Biblatex);
            }
            if (options == null) return;
            List<TexToken[]> keys = Split(options);
            for (int i = 0; i < keys.Count; i++)
            {
                TexToken[] option = keys[i];
                int equals = Array.FindIndex(option, x => x.IsChar('=', TexCatcode.Other));
                string key = Text(equals < 0 ? option : option[..equals]).Trim();
                string? value = equals < 0 ? null : Text(Unbraced(option[(equals + 1)..])).Trim();
                if (!TexBiblatex.Option(biblatex, key, value)) Log.Info($"biblatex option {key} is not supported and is ignored");
            }
        }

        // \DeclareOption{name}{code}, or \DeclareOption*{code} for any option not declared.
        private void DeclareOption(TexToken t)
        {
            if (expander.TakeStar())
            {
                otherOption = expander.Argument(t, true);
                return;
            }
            string? name = expander.NameArgument(t);
            TexToken[]? code = name == null ? null : expander.Argument(t, true);
            if (code != null) declaredOptions[name!.Trim()] = code;
        }

        // Each option's declared code in order, \DeclareOption*'s for one not declared.
        private void RunOptions(IEnumerable<string> names)
        {
            List<TexToken> code = new List<TexToken>();
            foreach (string name in names)
                if (declaredOptions.TryGetValue(name, out TexToken[]? declared)) code.AddRange(declared);
                else if (otherOption != null) code.AddRange(otherOption);
            expander.Insert(code.ToArray());
        }

        // geometry's paper and margin keys; other keys are ignored.
        private void Geometry(TexToken[]? options)
        {
            if (options == null) return;
            foreach (TexToken[] option in Split(options))
            {
                int equals = Array.FindIndex(option, x => x.IsChar('=', TexCatcode.Other));
                string key = Text(equals < 0 ? option : option[..equals]).Trim();
                if (equals < 0)
                {
                    if (key is "a4paper" or "letterpaper") paper = key[..^5];
                    continue;
                }
                if (key == "paper")
                {
                    string value = Text(option[(equals + 1)..]).Trim();
                    if (value is "a4paper" or "letterpaper") paper = value[..^5];
                    continue;
                }

                float mm = Millimetres(option[(equals + 1)..]);
                switch (key)
                {
                    case "margin": marginTop = marginBottom = marginLeft = marginRight = mm; break;
                    case "hmargin": marginLeft = marginRight = mm; break;
                    case "vmargin": marginTop = marginBottom = mm; break;
                    case "left" or "lmargin" or "inner": marginLeft = mm; break;
                    case "right" or "rmargin" or "outer": marginRight = mm; break;
                    case "top" or "tmargin": marginTop = mm; break;
                    case "bottom" or "bmargin": marginBottom = mm; break;
                    case "headsep": headSep = mm; break;
                    case "footskip": footSkip = mm; break;
                    case "paperwidth": customWidth = mm; break;
                    case "paperheight": customHeight = mm; break;
                }
            }
        }

        // \pdfpageheight=…; a \pagetotal in it cuts the last page to its content plus the rest instead.
        private void PdfPage(bool width)
        {
            expander.ScanKeyword("=");
            expander.pagetotalRead = false;
            float mm = expander.ScanDimen() / (float)Unity * MmPerPt;
            if (width) customWidth = mm;
            else if (expander.pagetotalRead) fitHeight = mm;
            else customHeight = mm;
        }

        // \definecolor{name}{model}{spec}
        private void DefineColor(TexToken t)
        {
            string? name = expander.NameArgument(t);
            string? model = name == null ? null : expander.NameArgument(t);
            string? spec = model == null ? null : expander.NameArgument(t);
            if (spec == null) return;
            if (ResolveColor(model!.Trim(), spec.Trim()) is not string hex)
            {
                expander.Error($"Package xcolor Error: Undefined color model `{model}'");
                return;
            }
            int rgb = int.Parse(hex[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            definedColors[name!.Trim()] = (rgb >> 16 & 255, rgb >> 8 & 255, rgb & 255);
        }

        // \rule[raise]{width}{height}: a box in the paragraph; a paragraph of rules alone becomes a rule line.
        private void RuleBox(TexToken t)
        {
            TexToken[]? raise = expander.Optional(t);
            TexToken[]? width = expander.Argument(t, false);
            TexToken[]? height = width == null ? null : expander.Argument(t, false);
            if (height == null) return;
            StartParagraph();
            HList.Add(new TexRuleBox(Dimen(width!), Dimen(height), raise == null ? 0 : Dimen(raise), style));
        }

        // A value with one pair of outer braces taken off.
        private static TexToken[] Unbraced(TexToken[] value)
        {
            TexToken[] trimmed = value.SkipWhile(v => v.IsChar(v.ch, TexCatcode.Space)).Reverse().SkipWhile(v => v.IsChar(v.ch, TexCatcode.Space)).Reverse().ToArray();
            if (trimmed.Length < 2 || !trimmed[0].IsChar(trimmed[0].ch, TexCatcode.BeginGroup) || !trimmed[^1].IsChar(trimmed[^1].ch, TexCatcode.EndGroup)) return trimmed;
            int depth = 0;
            for (int i = 0; i < trimmed.Length - 1; i++)
            {
                if (trimmed[i].IsChar(trimmed[i].ch, TexCatcode.BeginGroup)) depth++;
                else if (trimmed[i].IsChar(trimmed[i].ch, TexCatcode.EndGroup)) depth--;
                if (depth == 0) return trimmed;
            }
            return trimmed[1..^1];
        }

        private float Millimetres(TexToken[] value) => Dimen(value) / (float)Unity * 25.4f / 72.27f;

        // A dimension written in tokens, sp.
        private int Dimen(TexToken[] value)
        {
            expander.Insert(value.Append(TexToken.Cs("relax")).ToArray());
            int sp = expander.ScanDimen();
            while (expander.Next(out TexToken rest) && !(rest.IsCs && rest.name == "relax")) { }
            return sp;
        }

        // \textwidth and its kin from the class, paper and geometry.
        private void PageDimens()
        {
            List<TexToken> tokens = new List<TexToken>();
            void Set(string name, int sp)
            {
                tokens.Add(TexToken.Cs("global"));
                tokens.Add(TexToken.Cs(name));
                tokens.AddRange(Chars("=" + sp.ToString(CultureInfo.InvariantCulture) + "sp"));
                tokens.Add(TexToken.Cs("relax"));
            }

            foreach (string name in new[] { "textwidth", "linewidth", "columnwidth" })
                Set(name, textWidth);
            Set("textheight", textHeight);
            Set("paperwidth", (int)(paperWidth / MmPerPt * Unity));
            Set("paperheight", (int)(paperHeight / MmPerPt * Unity));
            expander.Insert(tokens.ToArray());
        }

        private static int Extent(float paper, float? before, float? after, float defaultPt)
        {
            if (before == null && after == null) return (int)(defaultPt * Unity);
            float side = (paper - defaultPt * MmPerPt) / 2f;
            return (int)((paper - (before ?? side) - (after ?? side)) / MmPerPt * Unity);
        }

        // \chapter (0) to \subparagraph (5).
        private void Section(TexToken t, int depth)
        {
            if (depth == 0 && !Chapters)
            {
                expander.Error(@"Undefined control sequence \chapter");
                depth = 1;
            }
            bool star = expander.TakeStar();
            TexToken[]? shortTitle = expander.Optional(t);
            TexToken[]? title = expander.Argument(t, true);
            if (title == null) return;
            EndParagraph();
            int ex = metrics.XHeight(new TexFont(TexFamily.Roman, false, false, Size(NormalSize)));
            TitleFormat? titled = titleFormats.GetValueOrDefault(depth) ?? startSection;
            startSection = null;
            VSkip(titled?.beforeSkip is int before ? new TexGlue { width = before }
                : new TexGlue { width = (int)(depth switch { 0 => 50f * Unity, 1 => 3.5f * ex, _ => 3.25f * ex }), shrink = depth == 0 ? 0 : (int)(0.2f * ex) }, true);
            headingAfter = titled?.afterSkip ?? depth switch { 0 => 40f * Unity, 1 => 2.3f * ex, _ => 1.5f * ex };

            int top = Chapters ? 0 : 1;
            bool numbered = !star && depth <= (Chapters ? 2 : 3);
            if (numbered) expander.StepCounter(sectionNames[depth]);
            string number = numbered ? Text(expander.Expanded(new[] { TexToken.Cs("the" + sectionNames[depth]) }, t) ?? Array.Empty<TexToken>()) : "";
            if (numbered) SetLabel(number);
            headingMark = !star && depth <= 2 ? (depth, number) : null;
            headingContents = star ? null : ContentsLine("toc", sectionNames[depth], numbered ? number : null, shortTitle ?? title);

            TexToken[] indent = titled?.indent > 0
                ? new[] { TexToken.Cs("hskip") }.Concat(Chars(titled.indent.ToString(CultureInfo.InvariantCulture) + "sp")).Append(TexToken.Cs("relax")).ToArray()
                : Array.Empty<TexToken>();
            if (titled?.runIn ?? depth >= 4)
            {
                noIndent = true;
                StartParagraph();
                TexToken[] shown = number.Length > 0 ? Chars(number).Append(TexToken.Cs("quad")).Concat(title).ToArray() : title;
                TexToken[] font = titled?.runIn == true ? titled.format ?? Array.Empty<TexToken>() : new[] { TexToken.Cs("bfseries") };
                expander.Insert(indent.Concat(Grouped(font.Concat(shown))).Append(TexToken.Cs("@runin")).ToArray());
                return;
            }
            if (expander.RegisterGlue("parskip") is TexGlue { width: not 0 } parskip) VSkip(parskip);

            TexFont normal = new TexFont(TexFamily.Roman, true, false, Size(NormalSize));
            int heading = depth - top + 1;
            if (depth == 0 && numbered)
            {
                Suspend(new TexParStyle { kind = TexParKind.Text, align = TexAlign.Left }, normal with { size = Size(8) });
                AddText("Chapter " + number);
                Resume();
                number = "";
            }

            if (titled?.format != null)
            {
                Suspend(new TexParStyle { kind = TexParKind.Heading, level = heading, align = TexAlign.Left }, normal with { bold = false });
                List<TexToken> set = new List<TexToken>(indent.Concat(titled.format));
                if (number.Length > 0 && titled.label == null) set.AddRange(Chars(number).Append(TexToken.Cs("quad")));
                else if (number.Length > 0 && titled.label!.Length > 0)
                {
                    set.AddRange(titled.label.SelectMany(l => l.IsCs && l.name == "the" + sectionNames[depth] ? Chars(number) : new[] { l }));
                    set.Add(TexToken.Cs("hskip"));
                    set.AddRange(Chars(titled.sep.ToString(CultureInfo.InvariantCulture) + "sp"));
                    set.Add(TexToken.Cs("relax"));
                }
                set.AddRange(titled.before ?? Array.Empty<TexToken>());
                set.AddRange(Grouped(title));
                set.Add(TexToken.Cs("@endhead"));
                headingAfterCode = titled.after;
                expander.Insert(set.ToArray());
                return;
            }

            int size = depth switch { 0 => 9, 1 => 6, 2 => 5, _ => NormalSize };
            Suspend(new TexParStyle { kind = TexParKind.Heading, level = heading, align = TexAlign.Left }, normal with { size = Size(size) });
            if (number.Length > 0)
            {
                AddText(number);
                Space(1f);
            }
            expander.Insert(title.Append(TexToken.Cs("@endhead")).ToArray());
        }

        // \@startsection{name}{level}{indent}{beforeskip}{afterskip}{style}, then the heading's own * [short] {title}; a negative afterskip runs in.
        private void StartSection(TexToken t)
        {
            TexToken[]? name = expander.Argument(t, false);
            TexToken[]? level = name == null ? null : expander.Argument(t, false);
            TexToken[]? indent = level == null ? null : expander.Argument(t, false);
            TexToken[]? before = indent == null ? null : expander.Argument(t, false);
            TexToken[]? after = before == null ? null : expander.Argument(t, false);
            TexToken[]? format = after == null ? null : expander.Argument(t, true);
            if (format == null) return;
            if (!int.TryParse(Text(level!).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int depth) || depth < 0 || depth >= sectionNames.Length)
            {
                expander.Error($"\\@startsection level {Text(level!).Trim()} is not 0 to {sectionNames.Length - 1}");
                return;
            }

            int Skip(TexToken[] value) => Dimen(value.TakeWhile(v => !(v.IsCs && v.name is "@plus" or "@minus")).ToArray());
            int afterSkip = Skip(after!);
            startSection = new TitleFormat
            {
                format = format, indent = Dimen(indent!), beforeSkip = Math.Abs(Skip(before!)), afterSkip = Math.Abs(afterSkip), runIn = afterSkip < 0
            };
            Section(t, depth);
        }

        private void Color(TexToken t)
        {
            TexToken[]? model = expander.Optional(t);
            string? spec = expander.NameArgument(t);
            if (spec == null) return;
            string? hex = ResolveColor(model == null ? "" : Text(model).Trim(), spec.Trim());
            if (hex == null) expander.Error($"Package xcolor Error: Undefined color `{spec}'");
            else SetStyle(style with { color = hex });
        }

        // Sets \baselinestretch; a change after the document's first paragraph is noted and not set.
        private void Stretch(float factor)
        {
            if (bodyStretch is float body && body != factor) Log.Once().Info($"line spacing {factor} after the first paragraph is ignored; the document stays at {body}");
            lineStretch = factor;
        }

        // fontspec's [options]{name}[options]; the name, its Path= files recorded.
        private string? FontName(TexToken t)
        {
            TexToken[]? before = expander.Optional(t);
            TexToken[]? name = expander.Argument(t, false);
            if (name == null) return null;
            TexToken[]? after = expander.Optional(t);
            string font = Text(name).Trim();
            FontFiles(font, before, after);
            return font;
        }

        // fontspec's Path, Extension and UprightFont/BoldFont/ItalicFont/BoldItalicFont as files under the source's folder, * for the name.
        private void FontFiles(string name, TexToken[]? before, TexToken[]? after)
        {
            string? path = null, extension = null, upright = null, bold = null, italic = null, boldItalic = null;
            foreach (TexToken[]? options in new[] { before, after })
            {
                if (options == null) continue;
                foreach (TexToken[] option in Split(options))
                {
                    int equals = Array.FindIndex(option, x => x.IsChar('=', TexCatcode.Other));
                    if (equals < 0) continue;
                    string value = Text(Unbraced(option[(equals + 1)..])).Trim();
                    switch (Text(option[..equals]).Trim())
                    {
                        case "Path": path = value; break;
                        case "Extension": extension = value; break;
                        case "UprightFont": upright = value; break;
                        case "BoldFont": bold = value; break;
                        case "ItalicFont": italic = value; break;
                        case "BoldItalicFont": boldItalic = value; break;
                    }
                }
            }
            if (folder == null || (path == null && extension == null)) return;

            string? FileOf(string? face) => face == null ? null : Path.GetFullPath(Path.Combine(folder, path ?? "", face.Replace("*", name) + extension));
            fontFiles[name] = (FileOf(upright ?? name)!, FileOf(bold), FileOf(italic), FileOf(boldItalic));
        }

        // \newfontface\cs[options]{name}[options]: \cs selects the face and a pending \fontsize.
        private void NewFontFace(TexToken t)
        {
            TexToken[]? command = expander.Argument(t, false);
            if (FontName(t) is not string name || command == null || command.Length == 0 || !command[0].IsCs) return;
            string cs = command[0].name!;
            expander.DefinePrimitive(cs);
            commands[cs] = owner =>
            {
                SetFont(style.font with { face = name });
                commands["selectfont"](owner);
            };
        }

        // polyglossia's \setmainlanguage[options]{name}: in force now, its captions from \begin{document}.
        private void MainLanguage(TexToken t)
        {
            expander.Optional(t);
            if (expander.NameArgument(t) is not string name) return;
            mainLanguage = name.Trim();
            SelectLanguage(mainLanguage, inDocument);
        }

        // babel's last language option is the main language.
        private void BabelLanguages(TexToken[]? options)
        {
            foreach (string option in Options(options))
                if (expander.IsDefined("captions" + option)) mainLanguage = language = option;
        }

        // \SetLanguageKeys{name}{keys}: indentfirst; other keys are read and ignored.
        private void SetLanguageKeys(TexToken t)
        {
            string? name = expander.NameArgument(t)?.Trim();
            TexToken[]? keys = name == null ? null : expander.Argument(t, true);
            if (keys == null) return;
            foreach (string option in Options(keys))
            {
                string[] pair = option.Split('=');
                if (pair[0].Trim() != "indentfirst") continue;
                bool on = pair.Length < 2 || pair[1].Trim() == "true";
                indentFirsts[name!] = on;
                if (name == language) indentFirst = indentFirstPackage || on;
            }
        }

        // \frenchspacing or \nonfrenchspacing until the group ends.
        private void FrenchSpacing(bool on)
        {
            bool outer = frenchSpacing;
            expander.Save(() => frenchSpacing = outer);
            frenchSpacing = on;
        }

        // The language paragraphs start in, and with captions its \captions<name>, until the group ends.
        private void SelectLanguage(string name, bool captions)
        {
            string outer = language;
            bool outerIndent = indentFirst;
            expander.Save(() =>
            {
                language = outer;
                indentFirst = outerIndent;
            });
            language = name;
            indentFirst = indentFirstPackage || indentFirsts.TryGetValue(name, out bool on) && on;
            if (captions && expander.IsDefined("captions" + name)) expander.Insert(new[] { TexToken.Cs("captions" + name) });
        }

        // caption's \captionsetup[kind]{keys}: name, labelsep, labelfont, font, textfont, justification and singlelinecheck
        // become \caption@key[@kind] macros the group scopes; other keys are read and ignored.
        private void CaptionSetup(TexToken t)
        {
            expander.TakeStar();
            TexToken[]? kind = expander.Optional(t);
            TexToken[]? keys = expander.Argument(t, true);
            if (keys == null) return;
            string suffix = kind == null ? "" : "@" + Text(kind).Trim();
            List<TexToken> defs = new List<TexToken>();
            foreach (TexToken[] option in Split(keys))
            {
                int equals = Array.FindIndex(option, x => x.IsChar('=', TexCatcode.Other));
                if (equals < 0) continue;
                string key = Text(option[..equals]).Trim();
                TexToken[] value = Unbraced(option[(equals + 1)..]);
                TexToken[]? body = key switch
                {
                    "name" or "justification" or "singlelinecheck" => value,
                    "labelsep" => LabelSeparator(Text(value).Trim()),
                    "labelfont" or "font" or "textfont" => CaptionFont(value),
                    _ => null
                };
                if (body == null) continue;
                string macro = "caption@" + key switch { "labelsep" => "sep", "labelfont" => "lfont", "textfont" => "tfont", _ => key } + suffix;
                defs.Add(TexToken.Cs("def"));
                defs.Add(TexToken.Cs(macro));
                defs.AddRange(Grouped(body));
            }
            expander.Insert(defs.ToArray());
        }

        private static TexToken[]? LabelSeparator(string separator) => separator switch
        {
            "colon" => new[] { TexToken.Char(':', TexCatcode.Other), TexToken.Cs("space") },
            "period" => new[] { TexToken.Char('.', TexCatcode.Other), TexToken.Cs("space") },
            "space" => new[] { TexToken.Cs("space") },
            "quad" => new[] { TexToken.Cs("quad") },
            "endash" => new[] { TexToken.Cs("space"), TexToken.Cs("textendash"), TexToken.Cs("space") },
            "none" => Array.Empty<TexToken>(),
            _ => null
        };

        // caption's font keys: bf, it, small, … as the switches they name.
        private static TexToken[] CaptionFont(TexToken[] value)
        {
            List<TexToken> switches = new List<TexToken>();
            foreach (string item in Options(value))
            {
                string? cs = item switch
                {
                    "bf" => "bfseries", "md" => "mdseries", "it" => "itshape", "sl" => "slshape", "sc" => "scshape", "up" => "upshape",
                    "rm" => "rmfamily", "sf" => "sffamily", "tt" => "ttfamily", "normalfont" => "normalfont",
                    _ => Array.IndexOf(sizeNames, item) >= 0 ? item : null
                };
                if (cs != null) switches.Add(TexToken.Cs(cs));
            }
            return switches.ToArray();
        }

        // \vskip; merge takes the larger of it and what is pending, as \addvspace does
        private void VSkip(TexGlue glue, bool merge = false) => vtarget.Add(new TexVGlue(glue, merge));

        // a list-level skip in sp: full at the outer level, half at the second, a quarter deeper
        private TexGlue ListSkip(float[] points, float[] shrinks, int depth)
        {
            float level = depth <= 1 ? 1f : depth == 2 ? 2f : 4f;
            return new TexGlue { width = (int)(points[sizeOption] * Unity / level), shrink = (int)(shrinks[sizeOption] * Unity / level) };
        }

        private static TexGlue Plus(TexGlue a, TexGlue b) => new TexGlue { width = a.width + b.width, shrink = a.shrink + b.shrink };

        // a list key's skip in sp, rigid, as enumitem keys are read
        private static TexGlue? Rigid(int? sp) => sp is int width ? new TexGlue { width = width } : null;

        // A list or a trivlist environment ends: \topsep below it, and no indent until a blank line.
        private void EndEnvironment(int depth, TexGlue? topSep = null)
        {
            EndParagraph();
            VSkip(topSep ?? ListSkip(topSeps, topSepShrinks, depth), true);
            afterEnvironment = true;
        }

        // A list environment: the keys \setlist gave every list, its kind and its name, then its own [keys].
        private void BeginList(TexListKind kind, string name, TexToken t)
        {
            bool vertical = mode != Mode.Horizontal;
            EndParagraph();
            ListSettings settings = new ListSettings();
            foreach (string key in new[] { "", kind.ToString().ToLowerInvariant(), name }.Distinct())
                if (listSettings.TryGetValue(key, out ListSettings? set)) settings.Apply(set);
            if (expander.Optional(t) is TexToken[] options) settings.Apply(ListKeys(options));
            if (settings.label != null && LabelMarker(settings.label) == null && kind != TexListKind.Description)
                Log.Info($"list label {Text(settings.label)} is set as the default marker");

            int depth = (list?.depth ?? 0) + 1;
            TexGlue top = Rigid(settings.topSep) ?? ListSkip(topSeps, topSepShrinks, depth);
            TexGlue sep = ListSkip(itemSeps, itemSepShrinks, depth);
            TexGlue item = Rigid(settings.itemSep) ?? sep, par = Rigid(settings.parSep) ?? sep;
            TexGlue skip = TopSkip(top, vertical);
            VSkip(Plus(skip, OuterParskip()), true);
            ListFrame frame = new ListFrame(kind, list) { settings = settings, topSkip = skip };
            if (frame.depth > 4) expander.Error("LaTeX Error: Too deeply nested");
            ListFrame? outer = list;
            expander.Save(() => list = outer);
            list = frame;
            expander.InsertSource($"\\topsep={top.width}sp minus{top.shrink}sp\\itemsep={item.width}sp minus{item.shrink}sp\\parsep={par.width}sp minus{par.shrink}sp\\relax ");
        }

        private void Item(TexToken t)
        {
            TexToken[]? label = expander.Optional(t);
            EndParagraph();
            if (list == null) expander.Error("LaTeX Error: Lonely \\item--perhaps a missing list environment");
            else if (label == null)
            {
                list.count++;
                if (list.kind == TexListKind.Enumerate) SetLabel(EnumerateLabel(list));
            }

            if (list != null)
            {
                if (list.started) VSkip(Plus(expander.RegisterGlue("itemsep") ?? default, expander.RegisterGlue("parsep") ?? default));
                list.started = true;
            }
            itemPending = true;
            StartParagraph();
            suppressSpace = true;
            if (label != null)
                expander.Insert(Grouped(list?.kind == TexListKind.Description ? new[] { TexToken.Cs("descriptionlabel") }.Concat(Grouped(label)) : label)
                    .Append(TexToken.Cs("@label")).ToArray());
        }

        // \newlist{name}{itemize|enumerate|description}{depth}
        private void NewList(TexToken t)
        {
            string? name = expander.NameArgument(t)?.Trim();
            string? type = name == null ? null : expander.NameArgument(t)?.Trim();
            if (type == null || expander.Argument(t, false) == null) return;
            if (!Enum.TryParse(type, true, out TexListKind kind))
            {
                expander.Error($"Package enumitem Error: Unknown list type `{type}'");
                return;
            }
            expander.DefinePrimitive(name!);
            commands[name!] = begin => BeginList(kind, name!, begin);
            expander.DefinePrimitive("end" + name);
            commands["end" + name] = _ => EndEnvironment(list?.depth ?? 1, Rigid(list?.settings.topSep));
        }

        // \setlist[names]{keys}; levels in the names are read as every level
        private void SetList(TexToken t)
        {
            TexToken[]? names = expander.Optional(t);
            TexToken[]? keys = expander.Argument(t, false);
            if (keys == null) return;
            ListSettings settings = ListKeys(keys);
            string[] targets = names == null ? new[] { "" } : Keys(Text(names)).Where(n => !n.All(char.IsDigit)).DefaultIfEmpty("").ToArray();
            foreach (string target in targets)
            {
                if (!listSettings.TryGetValue(target, out ListSettings? set)) listSettings[target] = set = new ListSettings();
                set.Apply(settings);
            }
        }

        private ListSettings ListKeys(TexToken[] keys)
        {
            ListSettings settings = new ListSettings();
            foreach (TexToken[] option in Split(keys))
            {
                int equals = Array.FindIndex(option, x => x.IsChar('=', TexCatcode.Other));
                string key = Text(equals < 0 ? option : option[..equals]).Trim();
                TexToken[] value = equals < 0 ? Array.Empty<TexToken>() : Unbraced(option[(equals + 1)..]);
                switch (key)
                {
                    case "label": settings.label = value; break;
                    case "leftmargin" when Text(value).Trim() != "*": settings.leftMargin = Dimen(value); break;
                    case "itemsep": settings.itemSep = Dimen(value); break;
                    case "parsep": settings.parSep = Dimen(value); break;
                    case "topsep": settings.topSep = Dimen(value); break;
                    case "noitemsep": settings.itemSep = settings.parSep = 0; break;
                    case "nosep": settings.itemSep = settings.parSep = settings.topSep = 0; break;
                    default: Log.Info($"enumitem key {key} is not supported and is ignored"); break;
                }
            }
            return settings;
        }

        // The list marker a label's commands draw, or null for one there is none of.
        private static string? LabelMarker(TexToken[] label)
        {
            foreach (TexToken token in label)
                if (token.IsCs && token.name switch
                    {
                        "rule" or "blacksquare" => "Square",
                        "square" => "SquareOutline",
                        "textbullet" or "bullet" => "Disc",
                        "circ" or "textopenbullet" => "Circle",
                        "blacktriangleright" or "blacktriangle" => "Triangle",
                        "triangleright" or "triangle" => "TriangleOutline",
                        "arabic" => "Decimal",
                        "alph" => "LowerAlpha",
                        "Alph" => "UpperAlpha",
                        "roman" => "LowerRoman",
                        "Roman" => "UpperRoman",
                        _ => null
                    } is string marker)
                    return marker;
            return null;
        }

        // \titleformat{\cmd}[shape]{format}{label}{sep}{before}[after], or \titleformat*{\cmd}{format}
        private void TitleFormatCommand(TexToken t)
        {
            bool star = expander.TakeStar();
            int depth = TitleDepth(expander.Argument(t, false));
            if (star)
            {
                TexToken[]? only = expander.Argument(t, true);
                if (only != null && depth >= 0) Titled(depth).format = only;
                return;
            }

            TexToken[]? shape = expander.Optional(t);
            TexToken[]? format = expander.Argument(t, true);
            TexToken[]? label = format == null ? null : expander.Argument(t, true);
            TexToken[]? sep = label == null ? null : expander.Argument(t, true);
            TexToken[]? before = sep == null ? null : expander.Argument(t, true);
            if (before == null || depth < 0) return;
            TexToken[]? after = expander.Optional(t);
            if (shape != null && Text(shape).Trim() is not ("hang" or "block"))
                Log.Info($"titlesec shape {Text(shape).Trim()} is set as hang");

            TitleFormat titled = Titled(depth);
            titled.format = format;
            titled.label = label;
            titled.sep = Dimen(sep!);
            titled.before = before;
            titled.after = after;
        }

        // \titlespacing*{\cmd}{left}{before}{after}[right]; *n is n ex
        private void TitleSpacing(TexToken t)
        {
            expander.TakeStar();
            int depth = TitleDepth(expander.Argument(t, false));
            TexToken[]? left = expander.Argument(t, false);
            TexToken[]? before = left == null ? null : expander.Argument(t, false);
            TexToken[]? after = before == null ? null : expander.Argument(t, false);
            if (after == null) return;
            expander.Optional(t);
            if (depth < 0) return;

            int ex = metrics.XHeight(new TexFont(TexFamily.Roman, false, false, Size(NormalSize)));
            int Skip(TexToken[] value)
            {
                string text = Text(value).Trim();
                if (text.StartsWith('*') && float.TryParse(text[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out float n)) return (int)(n * ex);
                return Dimen(value);
            }
            Titled(depth).indent = Skip(left!);
            Titled(depth).beforeSkip = Skip(before!);
            Titled(depth).afterSkip = Skip(after);
        }

        // The section depth a \titleformat names, or -1 with a note for one that is not a sectioning command.
        private static int TitleDepth(TexToken[]? command)
        {
            string? name = command?.Where(c => c.IsCs).Select(c => c.name).FirstOrDefault();
            int depth = name == null ? -1 : Array.IndexOf(sectionNames, name);
            if (depth < 0) Log.Info($"titlesec: {(command == null ? "" : Text(command))} is not a sectioning command and is ignored");
            return depth;
        }

        private TitleFormat Titled(int depth)
        {
            if (!titleFormats.TryGetValue(depth, out TitleFormat? titled)) titleFormats[depth] = titled = new TitleFormat();
            return titled;
        }

        private void BeginQuote()
        {
            EndParagraph();
            VSkip(ListSkip(topSeps, topSepShrinks, (list?.depth ?? 0) + 1), true);
            bool outer = quote;
            expander.Save(() => quote = outer);
            quote = true;
        }

        private void BeginAligned(TexAlign to)
        {
            bool vertical = mode != Mode.Horizontal;
            EndParagraph();
            TexGlue outer = alignedSkip;
            expander.Save(() => alignedSkip = outer);
            alignedSkip = TopSkip(ListSkip(topSeps, topSepShrinks, (list?.depth ?? 0) + 1), vertical);
            VSkip(alignedSkip, true);
            SetAlign(to);
            environmentStart = true;
        }

        // \\ in centred or ragged text, as \@centercr: the paragraph ends, \addvspace{-\parskip} takes back the next one's \parskip, then [skip].
        private void CenterCr(TexToken[]? skip)
        {
            EndParagraph();
            TexGlue parskip = OuterParskip();
            VSkip(new TexGlue { width = -parskip.width, shrink = -parskip.shrink }, true);
            if (skip != null) VSkip(new TexGlue { width = Dimen(skip) });
        }

        // The \parskip a paragraph here gets: the list's \parsep inside a list, else the register.
        private TexGlue OuterParskip() =>
            list != null ? Rigid(list.settings.parSep) ?? ListSkip(itemSeps, itemSepShrinks, list.depth) : expander.RegisterGlue("parskip") ?? default;

        // A trivlist's \topsep, and \partopsep with it when it began in vertical mode, after a blank line.
        private TexGlue TopSkip(TexGlue top, bool vertical) =>
            vertical ? Plus(top, new TexGlue { width = (int)(partopSeps[sizeOption] * Unity), shrink = (int)(partopSepShrinks[sizeOption] * Unity) }) : top;

        private void SetAlign(TexAlign to)
        {
            TexAlign outer = align;
            expander.Save(() => align = outer);
            align = to;
        }

        private void Verbatim(TexToken t)
        {
            EndParagraph();
            string? text = expander.CanReadRaw ? expander.ReadRaw("\\end{verbatim}") : null;
            if (text == null)
            {
                expander.Error(expander.CanReadRaw ? "File ended while scanning use of \\@xverbatim" : "LaTeX Error: verbatim illegal in command argument");
                return;
            }

            List<string> lines = text.Split('\n').ToList();
            int sourceAt = t.line;
            if (lines.Count > 1 && string.IsNullOrWhiteSpace(lines[0])) { lines.RemoveAt(0); sourceAt++; }
            if (lines.Count > 1 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);

            TexStyle code = style with { font = style.font with { family = TexFamily.Mono, bold = false, italic = false, face = null } };
            int depth = (list?.depth ?? 0) + 1;
            VSkip(ListSkip(topSeps, topSepShrinks, depth), true);
            foreach (string line in lines)
            {
                TexParagraph paragraph = new TexParagraph(new TexParStyle { kind = TexParKind.Code, align = TexAlign.Left }) { line = sourceAt > 0 ? sourceAt++ : 0 };
                foreach (char c in line)
                    paragraph.list.Add(new TexChar(c, code));
                Add(paragraph);
            }
            VSkip(ListSkip(topSeps, topSepShrinks, depth), true);
            afterEnvironment = true;

            expander.Insert(new[] { TexToken.Cs("end"), TexToken.Char('{', TexCatcode.BeginGroup) }
                .Concat("verbatim".Select(c => TexToken.Char(c, TexCatcode.Letter)))
                .Append(TexToken.Char('}', TexCatcode.EndGroup)).ToArray());
        }

        private void Verb(TexToken t)
        {
            if (!expander.CanReadRaw)
            {
                expander.Error("LaTeX Error: \\verb illegal in command argument");
                return;
            }
            if (!expander.ReadRawChar(out char delimiter) || delimiter == '*' && !expander.ReadRawChar(out delimiter))
            {
                expander.Error("LaTeX Error: \\verb ended by end of line");
                return;
            }
            string? text = expander.ReadRaw(delimiter.ToString());
            if (text == null || text.Contains('\n'))
            {
                expander.Error("LaTeX Error: \\verb ended by end of line");
                if (text == null) return;
            }

            TexStyle outer = style;
            style = style with { font = style.font with { family = TexFamily.Mono, face = null } };
            AddText(text);
            style = outer;
        }

        private void Footnote(TexToken t)
        {
            expander.Optional(t);
            TexToken[]? text = expander.Argument(t, true);
            if (text == null) return;

            expander.StepCounter("footnote");
            SetLabel(expander.Counter("footnote").ToString(CultureInfo.InvariantCulture));
            string mark = "{}^{" + expander.Counter("footnote").ToString(CultureInfo.InvariantCulture) + "}";
            TexFootnote note = new TexFootnote((footnotes.Count + 1).ToString(CultureInfo.InvariantCulture));
            footnotes.Add(note);
            StartParagraph();
            HList.Add(new TexMathNode(mark, false, style) { note = note });

            TexFont small = new TexFont(TexFamily.Roman, false, false, Size(2));
            Suspend(new TexParStyle { kind = TexParKind.Text }, small, note.vlist);
            HList.Add(new TexMathNode(mark, false, style));
            Interword();
            expander.Insert(text.Append(TexToken.Cs("@endnote")).ToArray());
        }

        private void Box(TexToken t)
        {
            int width = -1;
            if (expander.ScanKeyword("to")) width = expander.ScanDimen();
            else if (expander.ScanKeyword("spread")) expander.ScanDimen();
            StartParagraph();
            if (!expander.Next(out TexToken next)) return;
            if (!next.IsChar(next.ch, TexCatcode.BeginGroup))
            {
                expander.Insert(new[] { next });
                return;
            }

            TexHBox made = new TexHBox { width = width };
            if (leaderPending) leaderBox = made;
            else HList.Add(made);
            leaderPending = false;
            TexHBox? outer = box;
            expander.Save(() => box = outer);
            box = made;
        }

        // \settowidth{\len}{text}: the text's natural width in the current font.
        private void SetToWidth(TexToken t)
        {
            TexToken[]? target = expander.Argument(t, true);
            TexToken[]? text = target == null ? null : expander.Argument(t, true);
            if (text == null) return;
            int width = Width(Typeset(text, style.font).list);
            TexToken[] amount = Chars(width.ToString(CultureInfo.InvariantCulture) + "sp");
            TexToken[] assignment = new TexToken[target!.Length + amount.Length + 1];
            target.CopyTo(assignment, 0);
            amount.CopyTo(assignment, target.Length);
            assignment[^1] = TexToken.Cs("relax");
            expander.Insert(assignment);
        }

        // An hlist's natural width, sp; math and pictures count nothing.
        private int Width(List<TexNode> hlist)
        {
            int width = 0;
            StringBuilder run = new StringBuilder();
            TexFont font = default;
            for (int i = 0; i < hlist.Count; i++)
            {
                if (hlist[i] is TexChar c)
                {
                    if (run.Length > 0 && c.style.font != font)
                    {
                        width += metrics.Width(run.ToString(), font);
                        run.Clear();
                    }
                    font = c.style.font;
                    run.Append(c.ch);
                    continue;
                }
                width += hlist[i] switch
                {
                    TexGlueNode g => g.glue.width,
                    TexKern k => k.width,
                    TexRuleBox r => r.width,
                    TexHBox b => b.width >= 0 ? b.width : Width(b.list),
                    TexRefNode r => metrics.Width(r.text, r.style.font),
                    _ => 0
                };
            }
            if (run.Length > 0) width += metrics.Width(run.ToString(), font);
            return width;
        }

        private void Accent(TexToken t, char mark)
        {
            TexToken[]? arg = expander.Argument(t, false);
            if (arg == null) return;
            TexToken[] body = arg.Where(x => !(x.IsChar(x.ch, TexCatcode.Space))).ToArray();
            if (body.Length == 0)
            {
                AddText(spacingAccents.GetValueOrDefault(mark, mark).ToString());
                return;
            }

            char letter = body[0].IsCs ? body[0].name switch { "i" => 'i', "j" => 'j', _ => '\0' } : body[0].ch;
            if (letter == '\0' || body.Length > 1)
            {
                expander.Error($"LaTeX Error: {t} takes one letter");
                return;
            }

            string composed = (letter.ToString() + mark).Normalize(NormalizationForm.FormC);
            if (composed.Length == 1) AddText(composed);
            else
            {
                Log.Warn($"{t}{letter} has no precomposed character; set without its accent");
                AddText(letter.ToString());
            }
        }
        #endregion

        #region ---- references ----
        private void SetLabel(string text)
        {
            string outer = currentLabel;
            expander.Save(() => currentLabel = outer);
            currentLabel = text;
        }

        // An enumerate item as \ref prints it: 1, 1a, 1(a)i, 1(a)iA.
        private static string EnumerateLabel(ListFrame list)
        {
            List<int> counts = new List<int>();
            for (ListFrame? f = list; f != null; f = f.parent)
                if (f.kind == TexListKind.Enumerate) counts.Insert(0, f.count);

            string[] parts = counts.Select((n, i) => (i % 4) switch
            {
                0 => n.ToString(CultureInfo.InvariantCulture),
                1 => Alpha(n, 'a'),
                2 => Roman(n),
                _ => Alpha(n, 'A')
            }).ToArray();
            string text = parts[0];
            if (parts.Length == 2) text += parts[1];
            if (parts.Length >= 3) text += "(" + parts[1] + ")" + parts[2];
            if (parts.Length >= 4) text += parts[3];
            return text;
        }

        private static string Alpha(int n, char first) => n is >= 1 and <= 26 ? ((char)(first + n - 1)).ToString() : n.ToString(CultureInfo.InvariantCulture);

        private static string Roman(int n)
        {
            StringBuilder roman = new StringBuilder();
            foreach ((int value, string numeral) in new[] { (1000, "m"), (900, "cm"), (500, "d"), (400, "cd"), (100, "c"), (90, "xc"),
                         (50, "l"), (40, "xl"), (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i") })
                for (; n >= value; n -= value) roman.Append(numeral);
            return roman.ToString();
        }

        private void Label(TexToken t)
        {
            string? key = expander.NameArgument(t)?.Trim();
            if (key == null) return;
            if (labels.ContainsKey(key)) expander.Error($"LaTeX Warning: Label `{key}' multiply defined");
            MarkLabel(key);
        }

        // A label at the current position: in the paragraph being set, else at the end of the last one.
        private void MarkLabel(string key)
        {
            labels[key] = currentLabel;
            if (mode == Mode.Horizontal) HList.Add(new TexLabelMark(key));
            else if (vtarget.LastOrDefault(n => n is not TexVGlue) is TexParagraph last) last.list.Add(new TexLabelMark(key));
        }

        // \ref{key}, \pageref{key}, or \cite[note]{keys} as [labels, note].
        private void Reference(TexToken t, bool cite, bool page = false)
        {
            TexToken[]? note = cite ? expander.Optional(t) : null;
            if (!cite) expander.TakeStar();
            string? names = expander.NameArgument(t);
            if (names == null) return;
            string[] keys = cite ? Keys(names) : new[] { names.Trim() };

            StartParagraph();
            if (cite)
            {
                foreach (string key in keys)
                    if (!citations.Contains(key)) citations.Add(key);
                AddText("[");
            }
            suppressSpace = false;
            boundary = true;
            TexRefNode node = new TexRefNode(keys, cite, style, t.line, t.column) { page = page };
            refs.Add(node);
            HList.Add(node);
            if (!cite) return;

            if (note == null || note.All(x => x.IsChar(x.ch, TexCatcode.Space))) AddText("]");
            else expander.Insert(new[] { TexToken.Char(',', TexCatcode.Other), TexToken.Char(' ', TexCatcode.Space) }
                .Concat(note).Append(TexToken.Char(']', TexCatcode.Other)).ToArray());
        }

        private void NoCite(TexToken t)
        {
            string? names = expander.NameArgument(t);
            if (names == null) return;
            foreach (string key in Keys(names))
                if (key == "*") allCited = true;
                else if (!citations.Contains(key)) citations.Add(key);
        }

        private static string[] Keys(string names) => names.Split(',').Select(k => k.Trim()).Where(k => k.Length > 0).ToArray();

        // Each \ref and \cite gets its text, now every label and \bibitem is known.
        private void Resolve()
        {
            foreach (TexRefNode r in refs)
                if (r.page) Look(labels, r.keys[0], "Reference", r.line, r.column, "??");
                else r.text = r.cite ? CiteText(r) : Look(labels, r.keys[0], "Reference", r.line, r.column, "??");
            foreach ((TexMathNode node, int line) in mathRefs)
            {
                node.source = mathRef.Replace(node.source, m => @"\text{" + Look(labels, m.Groups[1].Value.Trim(), "Reference", line, 0, "??") + "}");
                node.source = mathEqref.Replace(node.source, m => @"\text{(" + Look(labels, m.Groups[1].Value.Trim(), "Reference", line, 0, "??") + ")}");
            }
        }

        // A citation's labels: in bibliography order under biblatex's sortcites, joined by its style's delimiter.
        private string CiteText(TexRefNode r)
        {
            string[] keys = r.keys;
            if (biblatex is { sortCites: true })
            {
                keys = (string[])keys.Clone();
                Array.Sort(keys, (a, b) => (bibOrder.TryGetValue(a, out int x) ? x : int.MaxValue).CompareTo(bibOrder.TryGetValue(b, out int y) ? y : int.MaxValue));
            }
            string delimiter = biblatex is { Alphabetic: true } ? "; " : ", ";
            StringBuilder text = new StringBuilder(16);
            for (int i = 0; i < keys.Length; i++)
            {
                if (i > 0) text.Append(delimiter);
                text.Append(Look(bibLabels, keys[i], "Citation", r.line, r.column, "?"));
            }
            return text.ToString();
        }

        private string Look(Dictionary<string, string> table, string key, string what, int line, int column, string missing)
        {
            if (table.TryGetValue(key, out string? text)) return text;
            errors.Add(new TexError(line, column, $"LaTeX Warning: {what} `{key}' undefined"));
            return missing;
        }
        #endregion

        #region ---- pages ----
        private void PageBreak(bool clear)
        {
            EndParagraph();
            if (mode == Mode.Vertical) vtarget.Add(new TexPageBreak(clear));
        }

        // the four standard page styles and fancyhdr's, one-sided, as source
        private void DefaultPageStyles()
        {
            TexToken[] page = { TexToken.Cs("thepage") };
            TexToken[] Slanted(string mark) => new[] { TexToken.Cs("slshape"), TexToken.Cs(mark) };
            styleSlots["empty"] = new Dictionary<string, TexToken[]>();
            styleSlots["plain"] = new Dictionary<string, TexToken[]> { ["FootCenter"] = page };
            styleSlots["headings"] = new Dictionary<string, TexToken[]> { ["HeadLeft"] = Slanted("rightmark"), ["HeadRight"] = page };
            styleSlots["myheadings"] = new Dictionary<string, TexToken[]>(styleSlots["headings"]);
            styleSlots["fancy"] = new Dictionary<string, TexToken[]>
            {
                ["HeadLeft"] = Slanted("leftmark"),
                ["HeadRight"] = Slanted("rightmark"),
                ["FootCenter"] = page
            };
        }

        // whether the page style makes \@mkboth a \markboth: headings, myheadings and fancyhdr's styles
        private bool MarksHeadings => pageStyle is "headings" or "myheadings" || fancyStyles.Contains(pageStyle);

        // \pagestyle sets every page's; \thispagestyle the page the paragraph being set, or the next one, lands on.
        private void SetPageStyle(TexToken t, bool thisPage)
        {
            string? name = expander.NameArgument(t)?.Trim();
            if (name == null) return;
            if (!styleSlots.ContainsKey(name))
            {
                expander.Error($@"Undefined control sequence \ps@{name}");
                return;
            }

            if (!thisPage) pageStyle = name;
            else
            {
                usedStyles.Add(name);
                if (mode == Mode.Horizontal) par!.pageStyle = name;
                else pendingStyle = name;
            }
        }

        private void TakePending(TexParagraph p)
        {
            p.pageStyle ??= pendingStyle;
            p.markLeft ??= pendingMarkLeft;
            p.markRight ??= pendingMarkRight;
            pendingStyle = pendingMarkLeft = pendingMarkRight = null;
        }

        // \markboth{left}{right} or \markright{right}.
        private void Mark(TexToken t, bool both)
        {
            TexToken[]? left = both ? expander.Argument(t, true) : null;
            if (both && left == null) return;
            TexToken[]? right = expander.Argument(t, true);
            if (right == null) return;

            string? leftText = left == null ? null : MarkText(Typeset(left));
            string rightText = MarkText(Typeset(right));
            if (mode == Mode.Horizontal)
            {
                par!.markLeft = leftText ?? par.markLeft;
                par.markRight = rightText;
            }
            else
            {
                pendingMarkLeft = leftText ?? pendingMarkLeft;
                pendingMarkRight = rightText;
            }
        }

        // \sectionmark and \chaptermark under headings and fancyhdr, one-sided.
        private void HeadingMarks(TexParagraph heading, int depth, string number)
        {
            bool fancy = fancyStyles.Contains(pageStyle);
            if (!fancy && pageStyle != "headings") return;

            string text = MarkText(heading);
            string title = depth > 0 && number.Length > 0 && text.StartsWith(number, StringComparison.Ordinal) ? text[number.Length..].TrimStart() : text;
            string Numbered(string after) => number.Length > 0 ? number + after : "";
            if (Chapters)
            {
                string chapter = (number.Length > 0 ? "CHAPTER " + number + ". " : "") + title.ToUpperInvariant();
                if (depth == 0 && fancy) (heading.markLeft, heading.markRight) = (chapter, "");
                else if (depth == 0) heading.markRight = chapter;
                else if (depth == 1 && fancy) heading.markRight = (Numbered(". ") + title).ToUpperInvariant();
            }
            else
            {
                string section = (Numbered("  ") + title).ToUpperInvariant();
                if (depth == 1 && fancy) (heading.markLeft, heading.markRight) = (section, "");
                else if (depth == 1) heading.markRight = section;
                else if (depth == 2 && fancy) heading.markRight = Numbered("  ") + title;
            }
        }

        // A paragraph as one line of plain text, ligatures taken apart.
        private static string MarkText(TexParagraph p)
        {
            StringBuilder text = new StringBuilder();
            void Walk(List<TexNode> nodes)
            {
                foreach (TexNode node in nodes)
                    switch (node)
                    {
                        case TexChar c:
                            text.Append(c.ch switch { 'ﬀ' => "ff", 'ﬁ' => "fi", 'ﬂ' => "fl", 'ﬃ' => "ffi", 'ﬄ' => "ffl", '­' => "", _ => c.ch.ToString() });
                            break;
                        case TexGlueNode or TexKern:
                            if (text.Length > 0 && text[^1] != ' ') text.Append(' ');
                            break;
                        case TexHBox b:
                            Walk(b.list);
                            break;
                        case TexRefNode r:
                            text.Append(r.text);
                            break;
                    }
            }
            Walk(p.list);
            return text.ToString().Trim();
        }

        // \thepage, \leftmark, \rightmark: a field in a page style's slot; outside one there is no page to know.
        private void Field(char field)
        {
            if (!inSlot)
            {
                AddText("??");
                return;
            }
            StartParagraph();
            HList.Add(new TexChar(field, style));
        }

        // \fancyhf, \fancyhead, \fancyfoot: [L,C,R with E,O] into the style being defined; one-sided, so E-only entries are dropped.
        private void FancySlots(TexToken t, string parts)
        {
            TexToken[]? spec = expander.Optional(t);
            TexToken[]? body = expander.Argument(t, true);
            if (body == null) return;

            IEnumerable<string> items = spec == null ? new[] { "" } : Text(spec).ToUpperInvariant().Split(',').Select(s => s.Trim());
            foreach (string item in items)
            {
                if (item.Contains('E') && !item.Contains('O')) continue;
                string sides = item.Any(c => c is 'L' or 'C' or 'R') ? item : "LCR";
                string bands = item.Any(c => c is 'H' or 'F') && parts.Length > 1 ? item : parts;
                foreach (char band in bands.Where(c => c is 'H' or 'F'))
                    foreach (char side in sides.Where(c => c is 'L' or 'C' or 'R'))
                        SetSlot((band == 'H' ? "Head" : "Foot") + (side == 'L' ? "Left" : side == 'C' ? "Center" : "Right"), body);
            }
        }

        private void SetSlot(string place, TexToken[] body)
        {
            Dictionary<string, TexToken[]> slots = styleSlots[fancyTarget];
            if (body.Length == 0) slots.Remove(place);
            else slots[place] = body;
        }

        // \fancypagestyle{name}{definitions}: a style that starts from fancy's slots.
        private void FancyPageStyle(TexToken t)
        {
            string? name = expander.NameArgument(t)?.Trim();
            TexToken[]? body = expander.Argument(t, true);
            if (name == null || body == null) return;
            styleSlots[name] = new Dictionary<string, TexToken[]>(styleSlots["fancy"]);
            fancyStyles.Add(name);
            fancyTarget = name;
            expander.Insert(body.Append(TexToken.Cs("@endfancystyle")).ToArray());
        }

        // Every style a page can take, each slot typeset in the normal font.
        private void TypesetPageStyles()
        {
            usedStyles.Add(pageStyle);
            float headRule = RuleWidth("headrulewidth");
            float footRule = RuleWidth("footrulewidth");
            inSlot = true;
            foreach (string name in usedStyles)
            {
                if (!styleSlots.TryGetValue(name, out Dictionary<string, TexToken[]>? slots)) continue;
                TexPageStyle typeset = new TexPageStyle();
                if (fancyStyles.Contains(name))
                {
                    typeset.headRule = headRule;
                    typeset.footRule = footRule;
                }
                foreach (KeyValuePair<string, TexToken[]> slot in slots)
                    typeset.slots[slot.Key] = Typeset(slot.Value);
                pageStyles[name] = typeset;
            }
            inSlot = false;
        }

        private float RuleWidth(string macro)
        {
            expander.Insert(new[] { TexToken.Cs(macro), TexToken.Cs("relax") });
            return expander.ScanDimen() / (float)Unity;
        }

        // Typesets tokens on their own, in a group, into one paragraph the document never sees.
        private TexParagraph Typeset(TexToken[] tokens, TexFont? font = null)
        {
            int depth = frames.Count;
            Suspend(new TexParStyle { kind = TexParKind.Text, align = TexAlign.Left }, font ?? new TexFont(TexFamily.Roman, false, false, Size(NormalSize)), new List<TexNode>());
            TexParagraph result = par!;
            expander.Insert(Grouped(tokens).Append(TexToken.Cs("@endtypeset")).ToArray());
            while (frames.Count > depth && expander.Next(out TexToken t)) Dispatch(t);
            return result;
        }
        #endregion

        #region ---- floats and pictures ----
        // Sets the body aside in a TexFloat where the environment stands; inside a box, note, cell or float it stays inline.
        private void BeginFloat(TexToken t, string kind)
        {
            TexToken[]? option = expander.Optional(t);
            string? outer = floatKind;
            TexFloat? outerFloat = openFloat;
            expander.Save(() =>
            {
                floatKind = outer;
                openFloat = outerFloat;
            });
            floatKind = kind;

            if (box != null || frames.Count > 0 || tables.Count > 0 || outer != null)
            {
                expander.Error("LaTeX Error: Not in outer par mode");
                openFloat = null;
                EndParagraph();
                VSkip(new TexGlue { width = (int)(floatSeps[sizeOption] * Unity) }, true);
                return;
            }

            EnsureDocument();
            captionStart = captionEnd = -1;
            openFloat = new TexFloat(kind, Placement(option == null ? "" : Text(option)));
            if (mode == Mode.Horizontal) HList.Add(openFloat);
            else vtarget.Add(openFloat);
            frames.Push(new Frame(mode, par, vtarget, style, list, align, quote));
            vtarget = openFloat.vlist;
            mode = Mode.Vertical;
            par = null;
            list = null;
            quote = false;
            align = TexAlign.Justify;
            style = new TexStyle(new TexFont(TexFamily.Roman, false, false, Size(NormalSize)), null, false);
            noIndent = true;
            expander.Insert(new[] { TexToken.Cs("@parboxrestore") });
        }

        private void EndFloat()
        {
            if (openFloat == null)
            {
                EndParagraph();
                VSkip(new TexGlue { width = (int)(floatSeps[sizeOption] * Unity) }, true);
                afterEnvironment = true;
                return;
            }
            if (openFloat.kind == "algorithm")
            {
                EndParagraph();
                FloatStyle(openFloat.vlist);
            }
            Resume();
            if (mode == Mode.Vertical) afterEnvironment = true;
            else if (HList.Count > 1 && HList[^2] is TexGlueNode { interword: true }) suppressSpace = true;
        }

        // The float package's styles: ruled sets the caption on top between rules and closes with one, plain and boxed put it last.
        private void FloatStyle(List<TexNode> vlist)
        {
            TexNode[] caption = captionStart >= 0 && captionEnd >= captionStart && captionEnd <= vlist.Count ? new TexNode[captionEnd - captionStart] : Array.Empty<TexNode>();
            vlist.CopyTo(Math.Max(captionStart, 0), caption, 0, caption.Length);
            vlist.RemoveRange(Math.Max(captionStart, 0), caption.Length);
            if (algorithmStyle != "ruled")
            {
                vlist.AddRange(caption);
                return;
            }
            TexGlue kern = new TexGlue { width = 2 * Unity };
            vlist.InsertRange(0, new TexNode[] { new TexRuleNode(), new TexVGlue(kern) });
            vlist.InsertRange(2, caption);
            vlist.InsertRange(2 + caption.Length, new TexNode[] { new TexVGlue(kern), new TexRuleNode(), new TexVGlue(kern) });
            vlist.Add(new TexVGlue(kern));
            vlist.Add(new TexRuleNode());
        }

        // The letters of [htbp!H] that mean something, tbp when none; a lone h becomes ht, as LaTeX does.
        private static string Placement(string option)
        {
            string letters = new string(option.Where(c => "htbpH!".Contains(c)).Distinct().ToArray());
            if (letters.Replace("!", "").Length == 0) return "tbp";
            return letters.Replace("!", "") == "h" ? letters + "t" : letters;
        }

        // \fnum@kind's label, caption's separator and fonts, then the text, centred; numbered within the chapter in report and book.
        private void Caption(TexToken t)
        {
            TexToken[]? shortText = expander.Optional(t);
            TexToken[]? text = expander.Argument(t, true);
            if (text == null) return;
            if (floatKind == null)
            {
                expander.Error("LaTeX Error: \\caption outside float");
                return;
            }

            expander.StepCounter(floatKind);
            string kind = floatKind;
            bool algorithm = kind == "algorithm";
            string number = algorithm ? Text(expander.Expanded(new[] { TexToken.Cs("thealgorithm") }, t) ?? Array.Empty<TexToken>())
                : (Chapters ? expander.Counter("chapter").ToString(CultureInfo.InvariantCulture) + "." : "")
                + expander.Counter(floatKind).ToString(CultureInfo.InvariantCulture);
            SetLabel(number);
            EndParagraph();
            if (!algorithm && HoldsContent(vtarget.Count)) VSkip(expander.RegisterGlue("abovecaptionskip") ?? default);
            captionStart = openFloat != null && vtarget == openFloat.vlist ? vtarget.Count : -1;
            captionJustify = CaptionSetting("justification", kind, t) switch
            {
                "centering" => TexAlign.Center, "raggedright" => TexAlign.Left, "raggedleft" => TexAlign.Right, _ => TexAlign.Justify
            };
            bool singleLineCheck = CaptionSetting("singlelinecheck", kind, t) is not ("false" or "off" or "no");
            TexAlign start = algorithm && algorithmStyle == "ruled" ? TexAlign.Justify : singleLineCheck ? TexAlign.Center : captionJustify;
            Suspend(new TexParStyle { kind = TexParKind.Text, align = start }, style.font);
            par!.strut = !algorithm;

            TexToken open = TexToken.Char('{', TexCatcode.BeginGroup), close = TexToken.Char('}', TexCatcode.EndGroup);
            List<TexToken> tokens = new List<TexToken>(text.Length + number.Length + 16) { open, TexToken.Cs(CaptionMacro("font", kind)!), open };
            tokens.Add(TexToken.Cs(CaptionMacro("lfont", kind)!));
            if (CaptionMacro("name", kind) is string name)
            {
                tokens.Add(TexToken.Cs("let"));
                tokens.Add(TexToken.Cs(kind + "name"));
                tokens.Add(TexToken.Cs(name));
            }
            tokens.Add(TexToken.Cs("fnum@" + kind));
            if (!algorithm) tokens.AddRange(Grouped(Chars(number)));
            tokens.Add(close);
            tokens.Add(TexToken.Cs(CaptionMacro("sep", kind)!));
            tokens.Add(open);
            tokens.Add(TexToken.Cs(CaptionMacro("tfont", kind)!));
            tokens.AddRange(text);
            tokens.Add(close);
            tokens.Add(close);
            tokens.Add(TexToken.Cs("@endcaption"));
            tokens.AddRange(ContentsLine(kind == "figure" ? "lof" : algorithm ? "loa" : "lot", kind, number, shortText ?? text));
            expander.Insert(tokens.ToArray());
        }

        // caption's singlelinecheck: a caption that fits on one line stays centred, a longer one takes its justification.
        private void EndCaption()
        {
            bool strut = par is { strut: true };
            if (par != null && Width(par.list) > (expander.RegisterValue("linewidth") ?? textWidth)) align = captionJustify;
            Resume();
            if (captionStart >= 0) captionEnd = vtarget.Count;
            if (strut && !HoldsContent(vtarget.Count - 1)) VSkip(expander.RegisterGlue("abovecaptionskip") ?? default);
        }

        // Whether the vertical list holds anything but glue before end: a caption after it takes \abovecaptionskip above, one first takes it below.
        private bool HoldsContent(int end)
        {
            for (int i = 0; i < end; i++)
                if (vtarget[i] is not TexVGlue) return true;
            return false;
        }

        // a \captionsetup value for a float kind as text, empty when unset
        private string CaptionSetting(string key, string kind, TexToken t) =>
            CaptionMacro(key, kind) is string macro ? Text(expander.Expanded(new[] { TexToken.Cs(macro) }, t) ?? Array.Empty<TexToken>()).Trim() : "";

        // caption's setting for a float kind: its own \caption@key@kind, else \caption@key, else null.
        private string? CaptionMacro(string key, string kind) =>
            expander.IsDefined($"caption@{key}@{kind}") ? $"caption@{key}@{kind}" : expander.IsDefined("caption@" + key) ? "caption@" + key : null;

        // graphicx's width, height, scale, angle and keepaspectratio; other keys are ignored.
        private void IncludeGraphics(TexToken t)
        {
            expander.TakeStar();
            TexToken[]? options = expander.Optional(t);
            TexToken[]? arg = expander.Argument(t, false);
            if (arg == null) return;
            string? path = FindGraphic(Text(arg).Trim());
            if (path == null) return;

            TexImage image = new TexImage(path);
            foreach (TexToken[] option in options == null ? new List<TexToken[]>() : Split(options))
            {
                int equals = Array.FindIndex(option, x => x.IsChar('=', TexCatcode.Other));
                string key = Text(equals < 0 ? option : option[..equals]).Trim();
                TexToken[] value = equals < 0 ? Array.Empty<TexToken>() : option[(equals + 1)..];
                switch (key)
                {
                    case "width": image.width = Dimen(value); break;
                    case "height" or "totalheight": image.height = Dimen(value); break;
                    case "scale": image.scale = Number(value); break;
                    case "angle": image.angle = Number(value); break;
                    case "keepaspectratio": image.keepAspect = equals < 0 || Text(value).Trim() == "true"; break;
                }
            }

            StartParagraph();
            suppressSpace = false;
            boundary = true;
            HList.Add(image);
        }

        private float Number(TexToken[] value)
        {
            if (float.TryParse(Text(value).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float n)) return n;
            expander.Error("Missing number, treated as zero");
            return 0f;
        }

        // The file under the source's folder and each \graphicspath entry; no extension tries PNG and JPEG.
        private string? FindGraphic(string name)
        {
            bool named = Path.GetExtension(name).Length > 0;
            if (folder != null)
                foreach (string prefix in graphicsPath)
                {
                    string stem = Path.Combine(folder, prefix, name);
                    IEnumerable<string> candidates = named ? new[] { stem } : rasterExtensions.Concat(vectorExtensions).Select(e => stem + e);
                    foreach (string candidate in candidates)
                    {
                        if (!File.Exists(candidate)) continue;
                        if (vectorExtensions.Contains(Path.GetExtension(candidate).ToLowerInvariant()))
                        {
                            expander.Error($"LaTeX Error: {Path.GetFileName(candidate)}: PDF and EPS pictures are not supported");
                            return null;
                        }
                        return Path.GetFullPath(candidate);
                    }
                }
            expander.Error($"LaTeX Error: File `{name}' not found");
            return null;
        }

        // \graphicspath{{a/}{b/}}
        private void GraphicsPath(TexToken t)
        {
            TexToken[]? arg = expander.Argument(t, false);
            if (arg == null) return;
            graphicsPath = new List<string> { "" };
            for (int i = 0; i < arg.Length; i++)
                if (arg[i].IsChar(arg[i].ch, TexCatcode.BeginGroup))
                {
                    i--;
                    graphicsPath.Add(Text(Group(arg, ref i)));
                }
        }
        #endregion

        #region ---- tabular ----
        private void BeginTabular(TexToken t)
        {
            expander.Optional(t);
            TexToken[]? spec = expander.Argument(t, false);
            if (spec == null) return;
            if (tables.Count > 0 && tables[^1].cell == null) StartCell();
            bool opens = mode == Mode.Vertical && tables.Count == 0 && box == null;
            EndParagraph();
            EnsureDocument();

            TexTable table = new TexTable { line = sourceLine };
            if (opens)
            {
                table.opens = true;
                if (IndentsNext) table.indent = expander.RegisterValue("parindent") ?? 0;
                noIndent = afterHeading = afterEnvironment = false;
                if (list != null && !itemPending) VSkip(Rigid(list.settings.parSep) ?? ListSkip(itemSeps, itemSepShrinks, list.depth));
                if (list == null && keepSkip == 0 && !environmentStart && expander.RegisterGlue("parskip") is TexGlue { width: not 0 } parskip) VSkip(parskip);
                environmentStart = false;
            }
            ColumnSpec(spec, table, 0);
            if (table.columns.Count == 0) table.columns.Add(new TexColumn { align = TexAlign.Left });
            vtarget.Add(table);
            unaligned.Add((vtarget, table));
            tables.Add(new TableBuild(table, style));
        }

        // l c r p{} m{} b{} | @{} !{} >{} <{} *{n}{spec}
        private void ColumnSpec(TexToken[] spec, TexTable table, int depth)
        {
            for (int i = 0; i < spec.Length; i++)
            {
                TexToken s = spec[i];
                if (!s.IsCs && s.cat == TexCatcode.Space) continue;
                switch (s.IsCs ? '\0' : s.ch)
                {
                    case 'l': table.columns.Add(new TexColumn { align = TexAlign.Left }); break;
                    case 'c': table.columns.Add(new TexColumn { align = TexAlign.Center }); break;
                    case 'r': table.columns.Add(new TexColumn { align = TexAlign.Right }); break;
                    case 'p' or 'm' or 'b': table.columns.Add(new TexColumn { align = TexAlign.Justify, width = Dimen(Group(spec, ref i)) }); break;
                    case '|':
                        while (table.vrules.Count <= table.columns.Count) table.vrules.Add(0);
                        table.vrules[table.columns.Count]++;
                        break;
                    case '@' or '!' or '>' or '<': Group(spec, ref i); break;
                    case '*':
                        int.TryParse(Text(Group(spec, ref i)).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count);
                        TexToken[] repeated = Group(spec, ref i);
                        for (int n = 0; n < Math.Min(count, 100) && depth < 8; n++)
                            ColumnSpec(repeated, table, depth + 1);
                        break;
                    default:
                        expander.Error("LaTeX Error: Illegal character in array arg");
                        break;
                }
            }
        }

        // The braced group or single token after i; i is left on its last token.
        private static TexToken[] Group(TexToken[] tokens, ref int i)
        {
            i++;
            while (i < tokens.Length && !tokens[i].IsCs && tokens[i].cat == TexCatcode.Space) i++;
            if (i >= tokens.Length) return Array.Empty<TexToken>();
            if (!tokens[i].IsChar(tokens[i].ch, TexCatcode.BeginGroup)) return new[] { tokens[i] };

            int start = i + 1, depth = 0;
            for (; i < tokens.Length; i++)
            {
                if (tokens[i].IsChar(tokens[i].ch, TexCatcode.BeginGroup)) depth++;
                else if (tokens[i].IsChar(tokens[i].ch, TexCatcode.EndGroup) && --depth == 0) return tokens[start..i];
            }
            return tokens[start..];
        }

        // A cell is set like a heading: the outer state suspended, the cell's own list the target.
        private void StartCell()
        {
            TableBuild b = tables[^1];
            if (b.row == null)
            {
                b.row = new List<TexTableCell>();
                b.table.rows.Add(b.row);
                b.column = 0;
            }
            TexTableCell cell = new TexTableCell();
            b.row.Add(cell);
            b.cell = cell;

            TexAlign to = b.column < b.table.columns.Count ? b.table.columns[b.column].align : TexAlign.Left;
            Suspend(new TexParStyle { kind = TexParKind.Text, align = to }, b.style.font, cell.vlist);
            par!.strut = true;
            style = b.style;
            suppressSpace = true;
        }

        private void EndCell()
        {
            TableBuild b = tables[^1];
            if (b.cell == null) return;
            Resume();
            b.column += b.cell.span;
            b.cell = null;
        }

        // &
        private void NextCell()
        {
            TableBuild b = tables[^1];
            if (b.cell == null) StartCell();
            EndCell();
            if (b.column >= b.table.columns.Count)
            {
                expander.Error("Extra alignment tab has been changed to \\cr");
                b.row = null;
            }
            StartCell();
        }

        // \\ in a tabular
        private void EndRow()
        {
            TableBuild b = tables[^1];
            if (b.cell == null) StartCell();
            EndCell();
            b.row = null;
        }

        private void EndTabular()
        {
            if (tables.Count == 0) return;
            EndCell();
            tables.RemoveAt(tables.Count - 1);
        }

        private void MultiColumn(TexToken t)
        {
            TexToken[]? count = expander.Argument(t, false);
            TexToken[]? spec = count == null ? null : expander.Argument(t, false);
            TexToken[]? text = spec == null ? null : expander.Argument(t, true);
            if (text == null) return;
            if (tables.Count == 0)
            {
                expander.Error("Misplaced \\omit");
                return;
            }

            TableBuild b = tables[^1];
            if (b.cell == null) StartCell();
            int.TryParse(Text(count!).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int span);
            b.cell!.span = Math.Max(1, span);
            TexTable own = new TexTable();
            ColumnSpec(spec!, own, 0);
            align = own.columns.Count > 0 ? own.columns[0].align : TexAlign.Center;
            expander.Insert(Grouped(text));
        }

        // \hline, \cline and the booktabs rules, at the row boundary they stand on; columns 1-based, 0 = all.
        private void Rule(TexRuleKind kind, int from = 0, int to = 0, int width = 0, bool trimLeft = false, bool trimRight = false)
        {
            if (tables.Count == 0)
            {
                expander.Error("Misplaced \\noalign");
                return;
            }
            TableBuild b = tables[^1];
            int row = b.row == null ? b.table.rows.Count : b.table.rows.Count - 1;
            int last = b.table.columns.Count - 1;
            b.table.rules.Add(new TexTableRule
            {
                row = row,
                kind = kind,
                from = from > 0 ? Math.Min(from - 1, last) : 0,
                to = to > 0 ? Math.Min(to - 1, last) : last,
                width = width,
                trimLeft = trimLeft,
                trimRight = trimRight
            });
        }

        // a-b, or a lone column
        private (int from, int to) Columns(TexToken[] range)
        {
            string[] ends = Text(range).Split('-');
            int.TryParse(ends[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int from);
            int to = from;
            if (ends.Length > 1) int.TryParse(ends[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out to);
            return (Math.Max(1, from), Math.Max(Math.Max(1, from), to));
        }

        private int OptionalWidth(TexToken[]? option) => option == null ? 0 : Dimen(option);
        #endregion

        #region ---- contents ----
        // \addcontentsline{ext}{level}{text}: the entry as \contentsline source, its page a \pageref to a label placed here.
        private void AddContentsLine(TexToken t)
        {
            TexToken[]? extension = expander.Argument(t, false);
            TexToken[]? level = extension == null ? null : expander.Argument(t, false);
            TexToken[]? text = level == null ? null : expander.Argument(t, true);
            if (text == null) return;

            string key = "@toc" + (++contentsKeys).ToString(CultureInfo.InvariantCulture);
            MarkLabel(key);
            int start = contentsTokens.Count;
            contentsTokens.Add(TexToken.Cs("contentsline"));
            contentsTokens.AddRange(Grouped(Protected(level!, t)));
            contentsTokens.AddRange(Grouped(Protected(text, t)));
            contentsTokens.Add(TexToken.Char('{', TexCatcode.BeginGroup));
            contentsTokens.Add(TexToken.Cs("pageref"));
            contentsTokens.AddRange(Grouped(Chars(key)));
            contentsTokens.Add(TexToken.Char('}', TexCatcode.EndGroup));
            AddContents(Text(Protected(extension!, t)).Trim(), start);
        }

        // The entry made of contentsTokens from start on, in document order; the bibliography's go where it stood.
        private void AddContents(string extension, int start)
        {
            (string, int, int) entry = (extension, start, contentsTokens.Count - start);
            if (contentsAt >= 0) contentsEntries.Insert(contentsAt++, entry);
            else contentsEntries.Add(entry);
        }

        // The tokens expanded as LaTeX's \protected@write does: everything but what \protect guards.
        private TexToken[] Protected(TexToken[] tokens, TexToken owner)
        {
            TexToken[] guarded = new TexToken[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
                guarded[i] = tokens[i].IsCs && tokens[i].name == "protect" ? TexToken.Cs("noexpand") : tokens[i];
            return expander.Expanded(guarded, owner) ?? tokens;
        }

        // \addcontentsline{extension}{level}{\protect\numberline{number}title} as a heading or caption writes it; number null for none.
        private static TexToken[] ContentsLine(string extension, string level, string? number, TexToken[] title)
        {
            TexToken open = TexToken.Char('{', TexCatcode.BeginGroup), close = TexToken.Char('}', TexCatcode.EndGroup);
            TexToken[] line = new TexToken[1 + extension.Length + 2 + level.Length + 2 + (number == null ? 0 : number.Length + 4) + title.Length + 2];
            int at = 0;
            line[at++] = TexToken.Cs("addcontentsline");
            foreach (string name in new[] { extension, level })
            {
                line[at++] = open;
                foreach (TexToken c in Chars(name))
                    line[at++] = c;
                line[at++] = close;
            }
            line[at++] = open;
            if (number != null)
            {
                line[at++] = TexToken.Cs("protect");
                line[at++] = TexToken.Cs("numberline");
                line[at++] = open;
                foreach (TexToken c in Chars(number))
                    line[at++] = c;
                line[at++] = close;
            }
            title.CopyTo(line, at);
            line[^1] = close;
            return line;
        }

        private void InsertHeadingContents()
        {
            if (headingContents == null) return;
            expander.Insert(headingContents);
            headingContents = null;
        }

        // Each \@starttoc's list, typeset where it stood once every entry is known.
        private void TypesetContents()
        {
            foreach ((List<TexNode> list, TexContentsMark mark) in contentsMarks)
            {
                int length = 5;
                foreach ((string extension, int _, int count) in contentsEntries)
                    if (extension == mark.extension) length += count;
                TexToken[] source = new TexToken[length];
                int at = 0;
                source[at++] = TexToken.Cs("begingroup");
                source[at++] = TexToken.Cs("@starttocsetup");
                foreach ((string extension, int start, int count) in contentsEntries)
                    if (extension == mark.extension)
                    {
                        contentsTokens.CopyTo(start, source, at, count);
                        at += count;
                    }
                source[at++] = TexToken.Cs("par");
                source[at++] = TexToken.Cs("endgroup");
                source[at] = TexToken.Cs("@endcontents");

                List<TexNode> made = new List<TexNode>(length / 8);
                vtarget = made;
                mode = Mode.Vertical;
                par = null;
                done = false;
                expander.Insert(source);
                while (!done && expander.Next(out TexToken t)) Dispatch(t);
                EndParagraph();

                int place = list.IndexOf(mark);
                list.RemoveAt(place);
                list.InsertRange(place, made);
            }
        }

        // tocbibind: the contents, lists and bibliography each an entry in the contents, unless an option says not.
        private void TocBibind(TexToken[]? options)
        {
            List<string> given = new List<string>(Options(options));
            StringBuilder code = new StringBuilder();
            foreach ((string option, string command, string name, string extension) in new[]
                     {
                         ("nottoc", "tableofcontents", "contentsname", "toc"), ("notlof", "listoffigures", "listfigurename", "lof"),
                         ("notlot", "listoftables", "listtablename", "lot")
                     })
                if (!given.Contains(option))
                    code.Append($@"\def\{command}{{\csname\@toclevel\endcsname*{{\{name}\@mkboth{{}}{{}}}}\addcontentsline{{toc}}{{\@toclevel}}{{\{name}}}\@starttoc{{{extension}}}}}");
            if (!given.Contains("notbib")) bibTocbibind = true;
            ReadCode(code.ToString());
        }

        // algorithm's float: ruled, plain or boxed, numbered within section or chapter when asked.
        private void Algorithm(TexToken[]? options)
        {
            string within = "";
            foreach (string option in Options(options))
            {
                if (option is "plain" or "ruled" or "boxed") algorithmStyle = option;
                if (option is "section" or "chapter") within = option;
            }
            foreach (string name in new[] { "algorithm", "algorithm*" })
            {
                expander.DefinePrimitive(name);
                commands[name] = t => BeginFloat(t, "algorithm");
                expander.DefinePrimitive("end" + name);
                commands["end" + name] = _ => EndFloat();
            }
            ReadCode((within.Length == 0
                ? @"\newcounter{algorithm}"
                : $@"\newcounter{{algorithm}}[{within}]\def\thealgorithm{{\the{within}.\arabic{{algorithm}}}}")
                + (algorithmStyle == "ruled" ? @"\def\caption@lfont@algorithm{\bfseries}\def\caption@sep@algorithm{\space}" : "") + TexFormat.Algorithm);
        }

        // Package code read as \RequirePackage reads a file: @ a letter inside, its catcode before put back after.
        private void ReadCode(string code, Action? atEnd = null)
        {
            TexCatcode outer = expander.atCatcode;
            expander.InputSource(code, () =>
            {
                atEnd?.Invoke();
                expander.atCatcode = outer;
            });
            expander.atCatcode = TexCatcode.Letter;
        }
        #endregion

        #region ---- bibliography ----
        private void Bibliography(TexToken t)
        {
            string? names = expander.NameArgument(t);
            if (names == null) return;
            bibFiles = Keys(names);
            if (!inDocument) return;
            PlaceBibliography(null, false);
        }

        // biblatex's \printbibliography[heading=…, title=…]: the list goes here; plain BibTeX when biblatex is not loaded.
        private void PrintBibliography(TexToken t)
        {
            TexToken[]? options = expander.Optional(t);
            if (bibStyle.Length == 0) bibStyle = "plain";
            string? title = null;
            bool inContents = false;
            if (options != null)
            {
                List<TexToken[]> keys = Split(options);
                for (int i = 0; i < keys.Count; i++)
                {
                    TexToken[] option = keys[i];
                    int equals = Array.FindIndex(option, x => x.IsChar('=', TexCatcode.Other));
                    if (equals < 0) continue;
                    string key = Text(option[..equals]).Trim();
                    string value = Text(Unbraced(option[(equals + 1)..])).Trim();
                    if (key == "heading" && value == "bibintoc") inContents = true;
                    else if (key == "title") title = value;
                }
            }
            PlaceBibliography(title, inContents);
        }

        // \DefineBibliographyStrings{language}{key = text, …}
        private void DefineBibliographyStrings(TexToken t)
        {
            string? name = expander.NameArgument(t)?.Trim();
            TexToken[]? definitions = name == null ? null : expander.Argument(t, true);
            if (definitions == null || biblatex == null) return;
            if (!biblatex.strings.TryGetValue(name!, out Dictionary<string, string>? strings))
                biblatex.strings[name!] = strings = new Dictionary<string, string>();
            List<TexToken[]> pairs = Split(definitions);
            for (int i = 0; i < pairs.Count; i++)
            {
                TexToken[] pair = pairs[i];
                int equals = Array.FindIndex(pair, x => x.IsChar('=', TexCatcode.Other));
                if (equals > 0) strings[Text(pair[..equals]).Trim()] = Source(Unbraced(pair[(equals + 1)..])).Trim();
            }
        }

        // The bibliography is typeset here once every \cite is known.
        private void PlaceBibliography(string? title, bool inContents)
        {
            EndParagraph();
            EnsureDocument();
            TexBibliographyMark mark = new TexBibliographyMark
            {
                title = title, inContents = inContents || bibTocbibind && biblatex == null, language = language, contentsAt = contentsEntries.Count
            };
            vtarget.Add(mark);
            bibMarks.Add((vtarget, mark));
        }

        private void BeginBibliography(TexToken t)
        {
            TexToken[] widest = Grouped(expander.Argument(t, false) ?? Array.Empty<TexToken>());
            EndParagraph();
            bibCount = 0;
            bibItems = 0;
            if (biblatex != null)
            {
                bool outer = bibUrls;
                expander.Save(() => bibUrls = outer);
                bibUrls = true;
                FrenchSpacing(true);
            }
            TexToken[] setup = new TexToken[widest.Length + 1];
            setup[0] = TexToken.Cs("@bibsetup");
            widest.CopyTo(setup, 1);
            expander.Insert(setup);
            TexGlue parsep = ListSkip(itemSeps, itemSepShrinks, 1);
            bibOuterParskip = expander.RegisterGlue("parskip") ?? default;
            expander.InsertSource($"\\parskip={parsep.width}sp minus{parsep.shrink}sp\\relax ");
            TexToken[] heading =
            {
                TexToken.Cs(Chapters ? "chapter" : "section"), TexToken.Char('*', TexCatcode.Other), TexToken.Char('{', TexCatcode.BeginGroup),
                TexToken.Cs(Chapters ? "bibname" : "refname"), TexToken.Char('}', TexCatcode.EndGroup)
            };
            if (bibInContents)
                expander.Insert(ContentsLine("toc", Chapters ? "chapter" : "section", null, new[] { TexToken.Cs(Chapters ? "bibname" : "refname") }));
            expander.Insert(heading);
        }

        // \bibitem[label]{key}: a paragraph hanging from [label], or [n] counted, in the label column.
        private void BibItem(TexToken t)
        {
            TexToken[]? label = expander.Optional(t);
            string? key = expander.NameArgument(t)?.Trim();
            if (key == null) return;
            EndParagraph();

            TexToken[] shown = label ?? Chars((++bibCount).ToString(CultureInfo.InvariantCulture));
            bibLabels[key] = Text(shown.Where(x => x.IsCs || x.cat is not (TexCatcode.BeginGroup or TexCatcode.EndGroup))).Replace("\\labelalphaothers", "+");
            bibOrder[key] = bibItems;
            if (bibItems > 0) VSkip(ListSkip(itemSeps, itemSepShrinks, 1));
            else if (afterHeading)
            {
                TexGlue parskip = expander.RegisterGlue("parskip") ?? default;
                VSkip(new TexGlue { width = bibOuterParskip.width - parskip.width, shrink = bibOuterParskip.shrink - parskip.shrink }, !bibInContents && !MarksHeadings);
            }
            else VSkip(ListSkip(topSeps, topSepShrinks, 1), true);
            bibItems++;
            noIndent = true;
            StartParagraph();
            TexToken[] grouped = Grouped(shown);
            TexToken[] item = new TexToken[grouped.Length + 1];
            item[0] = TexToken.Cs("@bibitem");
            grouped.CopyTo(item, 1);
            expander.Insert(item);
        }

        // BibTeX's job, once every \cite is known: the cited entries as a thebibliography, typeset where \bibliography stood.
        private void TypesetBibliography()
        {
            if (bibMarks.Count == 0) return;

            List<TexBibEntry> entries = new List<TexBibEntry>();
            foreach (string file in bibFiles)
            {
                string name = file.EndsWith(".bib", StringComparison.OrdinalIgnoreCase) ? file : file + ".bib";
                string? path = folder == null ? null : Path.Combine(folder, name);
                if (path == null || !File.Exists(path))
                {
                    expander.Error($"I couldn't open database file {name}");
                    continue;
                }
                TexBibliography.Parse(VaultFiles.ReadText(path), entries, message => expander.Error($"{message} in {name}"));
            }
            if (biblatex == null && !TexBibliography.styles.Contains(bibStyle))
            {
                expander.Error(bibStyle.Length == 0 ? "I found no \\bibliographystyle command" : $"I couldn't open style file {bibStyle}.bst");
                bibStyle = "plain";
            }

            Dictionary<string, TexBibEntry> byKey = new Dictionary<string, TexBibEntry>();
            foreach (TexBibEntry entry in entries)
                byKey.TryAdd(entry.key, entry);
            IEnumerable<string> keys = allCited ? citations.Concat(entries.Select(e => e.key)).Distinct() : citations;
            List<TexBibEntry> cited = keys.Where(byKey.ContainsKey).Select(k => byKey[k]).ToList();

            // each list in document order; a list's own contents entry moves the later lists' entry places along
            int shift = 0;
            foreach ((List<TexNode> list, TexBibliographyMark mark) in bibMarks)
            {
                List<TexNode> made = new List<TexNode>();
                vtarget = made;
                mode = Mode.Vertical;
                par = null;
                done = false;
                bibInContents = mark.inContents;
                contentsAt = mark.contentsAt + shift;
                expander.Insert(new[] { TexToken.Cs("@endbib") });
                if (biblatex != null) ReadCode(TexBiblatex.Format(cited, biblatex, mark.language, mark.title));
                else expander.InsertSource(TexBibliography.Format(cited, bibStyle));
                while (!done && expander.Next(out TexToken t)) Dispatch(t);
                EndParagraph();
                shift = contentsAt - mark.contentsAt;
                contentsAt = -1;

                int at = list.IndexOf(mark);
                list.RemoveAt(at);
                list.InsertRange(at, made);
            }
        }
        #endregion

        #region ---- building ----
        private void Character(TexToken t)
        {
            switch (t.cat)
            {
                case TexCatcode.BeginGroup or TexCatcode.EndGroup:
                    boundary = true;
                    return;
                case TexCatcode.MathShift:
                    MathShift();
                    return;
                case TexCatcode.Space:
                    if (suppressSpace) suppressSpace = false;
                    else if (mode == Mode.Horizontal) Interword(HList.Count > 0 && HList[^1] == factorNode ? spaceFactor : 1000);
                    return;
                case TexCatcode.AlignTab:
                    if (tables.Count > 0) NextCell();
                    else expander.Error("Misplaced alignment tab character &");
                    return;
                case TexCatcode.Superscript or TexCatcode.Subscript:
                    expander.Error("Missing $ inserted");
                    expander.Insert(new[] { t });
                    MathShift();
                    return;
                default:
                    AddChar(t.ch);
                    return;
            }
        }

        private void AddChar(char c)
        {
            StartParagraph();
            suppressSpace = false;
            List<TexNode> hlist = HList;
            bool joins = !boundary;
            boundary = false;
            if (hlist.Count == 0 || hlist[^1] != factorNode) spaceFactor = 1000;
            int code = SfCode(c);
            if (code != 0) spaceFactor = code > 1000 && spaceFactor < 1000 ? 1000 : code;
            bool straightQuote = false;
            if (style.font.family != TexFamily.Mono)
            {
                if (c == '`') c = '‘';
                else if (c == '\'') c = '’';
                else if (c == '"') (c, straightQuote) = ('”', true);
                if (joins && hlist.Count > 0 && hlist[^1] is TexChar last && last.style == style && Ligature(last.ch, c) is char merged and not '\0'
                    && (merged is not ('ﬀ' or 'ﬁ' or 'ﬂ' or 'ﬃ' or 'ﬄ') || JoinsF(style.font)))
                {
                    hlist[^1] = factorNode = new TexChar(merged, style);
                    return;
                }
            }
            hlist.Add(factorNode = new TexChar(c, style) { straightQuote = straightQuote });
        }

        // f-ligatures: the TFM fonts and Latin Modern; other fontspec fonts take only TeX's text ligatures.
        private bool JoinsF(TexFont font)
        {
            string? name = font.face ?? font.family switch { TexFamily.Roman => mainFont, TexFamily.Sans => sansFont, _ => monoFont };
            return name == null || name.StartsWith("Latin Modern", StringComparison.OrdinalIgnoreCase);
        }

        private static char Ligature(char a, char b) => (a, b) switch
        {
            ('-', '-') => '–',
            ('–', '-') => '—',
            ('‘', '‘') => '“',
            ('’', '’') => '”',
            ('!', '‘') => '¡',
            ('?', '‘') => '¿',
            ('f', 'f') => 'ﬀ',
            ('f', 'i') => 'ﬁ',
            ('f', 'l') => 'ﬂ',
            ('ﬀ', 'i') => 'ﬃ',
            ('ﬀ', 'l') => 'ﬄ',
            _ => '\0'
        };

        private void AddText(string text)
        {
            StartParagraph();
            suppressSpace = false;
            boundary = false;
            foreach (char c in text)
                HList.Add(new TexChar(c, style));
        }

        private void Interword(int factor = 1000)
        {
            StartParagraph();
            int quad = metrics.Quad(style.font);
            TexGlue glue = style.font.family == TexFamily.Mono
                ? new TexGlue { width = (int)(quad * 0.525) }
                : new TexGlue { width = quad / 3, stretch = quad / 6, shrink = quad / 9 };
            HList.Add(new TexGlueNode(glue, true, style) { spaceFactor = factor });
        }

        // TeX's \sfcode under \nonfrenchspacing, Unicode capitals at 999; 0 leaves the factor as it was.
        private int SfCode(char c) => c switch
        {
            '.' or '?' or '!' => frenchSpacing ? 1000 : 3000,
            ':' => frenchSpacing ? 1000 : 2000,
            ';' => frenchSpacing ? 1000 : 1500,
            ',' => frenchSpacing ? 1000 : 1250,
            ')' or ']' or '\'' or '’' or '”' => 0,
            _ => char.IsUpper(c) ? 999 : 1000
        };

        private void Space(float em)
        {
            StartParagraph();
            HList.Add(new TexGlueNode(new TexGlue { width = (int)(metrics.Quad(style.font) * em) }, false, style));
        }

        private void Kern(float em)
        {
            StartParagraph();
            HList.Add(new TexKern((int)(metrics.Quad(style.font) * em), style));
        }

        // whether the next paragraph opens with \parindent
        private bool IndentsNext => !noIndent && !(afterHeading && !indentFirst) && !afterEnvironment && list == null && !quote;

        private void StartParagraph()
        {
            if (mode == Mode.Horizontal) return;
            if (tables.Count > 0 && tables[^1].cell == null)
            {
                StartCell();
                return;
            }
            EnsureDocument();

            TexParStyle parStyle = new TexParStyle { kind = quote ? TexParKind.Quote : TexParKind.Text, indentWidth = expander.RegisterValue("parindent") ?? 0 };
            parStyle.indent = IndentsNext;
            noIndent = afterHeading = afterEnvironment = false;
            if (list != null && !itemPending) VSkip(Rigid(list.settings.parSep) ?? ListSkip(itemSeps, itemSepShrinks, list.depth));
            if (list == null && keepSkip == 0 && !environmentStart && expander.RegisterGlue("parskip") is TexGlue { width: not 0 } parskip) VSkip(parskip);
            environmentStart = false;
            if (list != null)
            {
                parStyle.list = list.kind;
                parStyle.listDepth = list.depth;
                parStyle.listKindDepth = list.kindDepth;
                parStyle.itemNumber = list.count;
                parStyle.item = itemPending;
                parStyle.marker = list.settings.label is TexToken[] label ? LabelMarker(label) : null;
                parStyle.leftMargin = list.settings.leftMargin ?? 0;
            }
            itemPending = false;
            par = new TexParagraph(parStyle) { line = sourceLine, language = language };
            mode = Mode.Horizontal;
            boundary = true;
        }

        private void EndParagraph()
        {
            for (int i = unaligned.Count - 1; i >= 0; i--)
                if (unaligned[i].target == vtarget)
                {
                    unaligned[i].table.align = align == TexAlign.Justify ? TexAlign.Left : align;
                    unaligned.RemoveAt(i);
                }
            if (mode != Mode.Horizontal || box != null) return;
            List<TexNode> hlist = par!.list;
            while (hlist.Count > 0 && hlist[^1] is TexGlueNode { interword: true }) hlist.RemoveAt(hlist.Count - 1);
            par.style.align = align;
            par.style.leftSkip = expander.RegisterValue("leftskip") ?? 0;
            par.style.rightSkip = expander.RegisterValue("rightskip") ?? 0;
            par.style.parFillSkip = expander.RegisterValue("parfillskip") ?? 0;
            par.interlinePenalty = expander.RegisterValue("interlinepenalty") ?? 0;
            if (align != TexAlign.Justify) par.style.indent = false;
            if (hlist.Count > 0 || par.style.item)
            {
                if (vtarget == vlist && inDocument) bodyStretch ??= lineStretch;
                par.baselineSkip = (int)((keepSkip != 0 ? keepSkip : BaselineSkip(style)) * (SingleSpaced ? 1f : lineSpread));
                if (vtarget == vlist) TakePending(par);
                vtarget.Add(par);
            }
            keepSkip = 0;
            par = null;
            mode = Mode.Vertical;
        }

        private void Add(TexParagraph paragraph)
        {
            EnsureDocument();
            vtarget.Add(paragraph);
        }

        private void EnsureDocument()
        {
            if (inDocument || preambleReported) return;
            expander.Error("LaTeX Error: Missing \\begin{document}");
            preambleReported = true;
        }

        // Starts a paragraph of its own for a heading or a note's text; Resume puts the outer state back.
        private void Suspend(TexParStyle parStyle, TexFont font, List<TexNode>? target = null)
        {
            EnsureDocument();
            frames.Push(new Frame(mode, par, vtarget, style, list, align, quote));
            vtarget = target ?? vtarget;
            list = null;
            quote = false;
            align = parStyle.align;
            style = new TexStyle(font, null, false);
            par = new TexParagraph(parStyle) { line = sourceLine, language = language };
            mode = Mode.Horizontal;
            boundary = true;
        }

        private void Resume()
        {
            if (frames.Count == 0) return;
            EndParagraph();
            Frame frame = frames.Pop();
            mode = frame.mode;
            par = frame.par;
            vtarget = frame.vtarget;
            style = frame.style;
            list = frame.list;
            align = frame.align;
            quote = frame.quote;
        }

        private void SetFont(TexFont font) => SetStyle(style with { font = font });

        private void SetStyle(TexStyle to)
        {
            TexStyle outer = style;
            expander.Save(() => style = outer);
            style = to;
        }

        private int Size(int index) => (int)Math.Round(sizePoints[sizeOption][index] * Unity);

        // A style's \baselineskip, sp: its own, else the size command's whose size is nearest its font's.
        private int BaselineSkip(TexStyle s)
        {
            if (s.baselineSkip != 0) return s.baselineSkip;
            float[] sizes = sizePoints[sizeOption];
            float points = s.font.size / (float)Unity;
            int nearest = 0;
            for (int i = 1; i < sizes.Length; i++)
                if (MathF.Abs(sizes[i] - points) < MathF.Abs(sizes[nearest] - points)) nearest = i;
            return (int)Math.Round(baselineSkipPoints[sizeOption][nearest] / sizes[nearest] * s.font.size);
        }

        // whether the paragraphs being set go into a footnote
        private bool InNote
        {
            get
            {
                for (int i = 0; i < footnotes.Count; i++)
                    if (footnotes[i].vlist == vtarget) return true;
                return false;
            }
        }

        // setspace single-spaces footnotes and floats, except an [H] float's body
        private bool SingleSpaced => setspace && (floatKind != null && (openFloat == null || !openFloat.placement.Contains('H') || par is { strut: true } && tables.Count == 0) || InNote);
        #endregion

        #region ---- math ----
        // $ starts inline math, $$ display math.
        private void MathShift()
        {
            StringBuilder source = new StringBuilder();
            bool display = false, first = true;
            int depth = 0;
            expander.passUndefined = true;
            TexToken? after = null;
            while (expander.Next(out TexToken t))
            {
                if (!t.IsCs && t.cat == TexCatcode.BeginGroup) depth++;
                else if (!t.IsCs && t.cat == TexCatcode.EndGroup && depth-- == 0)
                {
                    expander.Error("Extra }, or forgotten $");
                    after = t;
                    break;
                }
                if (!t.IsCs && t.cat == TexCatcode.MathShift)
                {
                    if (first && !display)
                    {
                        display = true;
                        continue;
                    }
                    if (display)
                    {
                        bool got = expander.Next(out TexToken second);
                        if (!got || second.IsCs || second.cat != TexCatcode.MathShift)
                        {
                            expander.Error("Display math should end with $$");
                            if (got) after = second;
                        }
                    }
                    break;
                }
                first = false;
                if (Ends(t))
                {
                    after = t;
                    break;
                }
                Append(source, t);
            }
            expander.passUndefined = false;
            Place(source.ToString(), display);
            if (after is TexToken pending) Dispatch(pending);
        }

        // \( \), \[ \] and the math environments, up to their closing token; amsmath's numbered and wrapped.
        private void MathUntil(TexToken close, bool display, string? environment = null)
        {
            StringBuilder source = new StringBuilder();
            expander.passUndefined = true;
            TexToken? after = null;
            bool closed = false;
            while (expander.Next(out TexToken t))
            {
                if (t.IsCs && t.name == close.name)
                {
                    closed = true;
                    break;
                }
                if (Ends(t))
                {
                    after = t;
                    break;
                }
                Append(source, t);
            }
            expander.passUndefined = false;
            if (!closed) expander.Error($"Missing {close} inserted");
            Place(environment == null ? source.ToString() : Number(environment, source.ToString()), display);
            if (after is TexToken pending) Dispatch(pending);
        }

        // amsmath's numbering: a \tag on each numbered row, its \label pointed at the number; the body wrapped in its environment.
        private string Number(string environment, string body)
        {
            bool numbered = !environment.EndsWith('*');
            bool once = environment is "equation" or "multline";
            List<string> rows = once ? new List<string> { body } : Rows(body);
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < rows.Count; i++)
            {
                string row = rows[i];
                Match label = mathLabel.Match(row);
                row = mathLabel.Replace(row, "");
                bool notag = mathNotag.IsMatch(row);
                row = mathNotag.Replace(row, "");

                Match tag = mathTag.Match(row);
                string? number = tag.Success ? tag.Groups[2].Value : null;
                if (number == null && numbered && !notag && row.Trim().Length > 0)
                {
                    expander.StepCounter("equation");
                    number = (Chapters ? expander.Counter("chapter").ToString(CultureInfo.InvariantCulture) + "." : "")
                        + expander.Counter("equation").ToString(CultureInfo.InvariantCulture);
                    row += @"\tag{" + number + "}";
                }
                if (label.Success)
                {
                    string key = label.Groups[1].Value.Trim();
                    if (labels.ContainsKey(key)) expander.Error($"LaTeX Warning: Label `{key}' multiply defined");
                    labels[key] = number ?? currentLabel;
                }

                if (i > 0) result.Append(@"\\");
                result.Append(row);
            }
            return environment.StartsWith("equation") ? result.ToString() : $@"\begin{{{environment}}}{result}\end{{{environment}}}";
        }

        // A body split at its top-level \\, outside braces and nested environments.
        private static List<string> Rows(string body)
        {
            List<string> rows = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c == '{') depth++;
                else if (c == '}') depth--;
                else if (c != '\\' || i + 1 >= body.Length) continue;
                else if (body[i + 1] == '\\' && depth == 0)
                {
                    rows.Add(body.Substring(start, i - start));
                    start = i + 2;
                    i++;
                }
                else if (string.CompareOrdinal(body, i, @"\begin", 0, 6) == 0) depth++;
                else if (string.CompareOrdinal(body, i, @"\end", 0, 4) == 0) depth--;
                else i++;
            }
            rows.Add(body.Substring(start));
            return rows;
        }

        // A token that cannot be in math: \par, a heading's or note's end, an environment's end.
        private bool Ends(TexToken t)
        {
            if (!t.IsCs) return false;
            bool ends = t.name == "par" || t.name!.StartsWith("@end")
                || t.name.StartsWith("end") && commands.ContainsKey(t.name) && !mathEnvironments.Contains(t.name[3..]);
            if (ends) expander.Error("Missing $ inserted");
            return ends;
        }

        private static void Append(StringBuilder source, TexToken t)
        {
            if (!t.IsCs)
            {
                source.Append(t.ch);
                return;
            }
            string name = t.name!;
            if (mathEnvironments.Contains(name))
            {
                source.Append(@"\begin{").Append(name).Append('}');
                return;
            }
            if (name.StartsWith("end") && mathEnvironments.Contains(name[3..]))
            {
                source.Append(@"\end{").Append(name[3..]).Append('}');
                return;
            }
            source.Append('\\').Append(name);
            if (name.Length > 0 && char.IsLetter(name[^1])) source.Append(' ');
        }

        private void Place(string source, bool display)
        {
            StartParagraph();
            TexMathNode node = new TexMathNode(mathLabel.Replace(source, "").Trim(), display, style);
            if (node.source.Contains(@"\begin{")) node.strut = ArrayStrut();
            HList.Add(node);
            if (mathRef.IsMatch(node.source) || mathEqref.IsMatch(node.source)) mathRefs.Add((node, sourceLine));
            suppressSpace = display;
            boundary = true;
        }

        // An array row's \strutbox here, em: \arraystretch × \baselineskip over the font size.
        private float ArrayStrut()
        {
            TexToken name = TexToken.Cs("arraystretch");
            string text = Text(expander.Expanded(new[] { name }, name) ?? Array.Empty<TexToken>()).Trim();
            float stretch = float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? parsed : 1f;
            float skip = BaselineSkip(style) * (SingleSpaced ? 1f : lineSpread);
            return stretch * skip / style.font.size;
        }
        #endregion

        #region ---- helpers ----
        private static TexToken[] Grouped(IEnumerable<TexToken> tokens) =>
            new[] { TexToken.Char('{', TexCatcode.BeginGroup) }.Concat(tokens).Append(TexToken.Char('}', TexCatcode.EndGroup)).ToArray();

        // Text as letter and other tokens.
        private static TexToken[] Chars(string text) =>
            text.Select(c => TexToken.Char(c, char.IsLetter(c) ? TexCatcode.Letter : TexCatcode.Other)).ToArray();

        // Tokens as their characters, a control sequence as \name.
        private static string Text(IEnumerable<TexToken> tokens)
        {
            StringBuilder text = new StringBuilder();
            foreach (TexToken t in tokens)
                if (t.IsCs) text.Append('\\').Append(t.name);
                else text.Append(t.ch);
            return text.ToString();
        }

        // Tokens as source to read again: a control word gets a space after it, so it ends where it did.
        private static string Source(TexToken[] tokens)
        {
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < tokens.Length; i++)
            {
                TexToken t = tokens[i];
                if (!t.IsCs) text.Append(t.ch);
                else
                {
                    text.Append('\\').Append(t.name);
                    if (t.name!.Length > 0 && char.IsLetter(t.name[^1])) text.Append(' ');
                }
            }
            return text.ToString();
        }

        // Comma-separated items outside braces.
        private static List<TexToken[]> Split(TexToken[] tokens)
        {
            List<TexToken[]> items = new List<TexToken[]>();
            List<TexToken> item = new List<TexToken>();
            int depth = 0;
            foreach (TexToken t in tokens)
            {
                if (t.IsChar(t.ch, TexCatcode.BeginGroup)) depth++;
                else if (t.IsChar(t.ch, TexCatcode.EndGroup)) depth--;
                if (depth == 0 && t.IsChar(',', TexCatcode.Other))
                {
                    items.Add(item.ToArray());
                    item.Clear();
                }
                else item.Add(t);
            }
            items.Add(item.ToArray());
            return items.Where(i => i.Any(t => !(t.IsChar(t.ch, TexCatcode.Space)))).ToList();
        }

        // mu (65536 = 1mu) in sp at the current font: 18mu to its em.
        private int MuToSp(int mu) => (int)((long)mu * style.font.size / (18L * 65536));

        // biblatex's break after a URL digit, capital or lowercase letter: its counter's penalty and its muskip in sp; none at penalty 0.
        private (int penalty, int width, int stretch, int shrink) UrlLetters(string counter, string skip)
        {
            int penalty = expander.Counter(counter);
            if (penalty <= 0) return default;
            TexGlue mu = expander.RegisterMuGlue(skip) ?? default;
            if (mu.stretchOrder > 0 || mu.shrinkOrder > 0) Log.Once().Info($"infinite \\{skip} stretch or shrink in a URL is set as finite");
            return (penalty, MuToSp(mu.width), MuToSp(mu.stretch), MuToSp(mu.shrink));
        }

        // The marker after a URL digit, capital or lowercase letter whose biblatex break penalty is set; null for any other character.
        private static char? UrlLetterMarker(char c, in TexStyle style) =>
            c is >= '0' and <= '9' ? (style.urlNumPenalty > 0 ? '' : null)
            : c is >= 'A' and <= 'Z' ? (style.urlUcPenalty > 0 ? '' : null)
            : c is >= 'a' and <= 'z' ? (style.urlLcPenalty > 0 ? '' : null) : null;

        // The math atom url.sty sets a URL character as; a later list wins, as \Url@MathSetup assigns them in turn.
        private static UrlAtom AtomOf(char c, string breaks, string bigBreaks, string noBreaks) =>
            noBreaks.IndexOf(c) >= 0 ? UrlAtom.Open : bigBreaks.IndexOf(c) >= 0 ? UrlAtom.Rel : breaks.IndexOf(c) >= 0 ? UrlAtom.Bin : UrlAtom.Ord;

        // The characters a \do list macro names (\do\x or \do x, through parameterless macros it calls); fallback when undefined.
        private string UrlCharacters(string macro, string fallback)
        {
            TexToken[]? body = expander.MacroBody(macro);
            if (body == null) return fallback;
            StringBuilder chars = new StringBuilder(body.Length);
            AppendDoList(body, chars, 0);
            return chars.ToString();
        }

        private void AppendDoList(TexToken[] body, StringBuilder chars, int depth)
        {
            for (int i = 0; i < body.Length; i++)
            {
                if (!body[i].IsCs) continue;
                if (body[i].name == "do" && i + 1 < body.Length)
                {
                    TexToken x = body[++i];
                    if (!x.IsCs) chars.Append(x.ch);
                    else if (x.name!.Length == 1) chars.Append(x.name[0]);
                }
                else if (depth < 8 && expander.MacroBody(body[i].name!) is TexToken[] inner) AppendDoList(inner, chars, depth + 1);
            }
        }

        private static IEnumerable<string> Options(TexToken[]? tokens) =>
            tokens == null ? Enumerable.Empty<string>() : Split(tokens).Select(o => Text(o).Trim());

        private static string Today()
        {
            DateTime today = DateTime.Today;
            return $"{CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(today.Month)} {today.Day}, {today.Year}";
        }

        // xcolor: a named colour with name!percent[!other] mixing, or an HTML, rgb, RGB or gray value; #RRGGBB.
        private string? ResolveColor(string model, string spec)
        {
            (int r, int g, int b)? rgb = model switch
            {
                "" => Named(spec),
                "HTML" => spec.Length == 6 && int.TryParse(spec, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex)
                    ? (hex >> 16 & 255, hex >> 8 & 255, hex & 255) : null,
                "rgb" => Floats(spec, 3) is float[] f ? (Byte(f[0]), Byte(f[1]), Byte(f[2])) : null,
                "RGB" => Floats(spec, 3) is float[] i ? ((int)i[0], (int)i[1], (int)i[2]) : null,
                "gray" => Floats(spec, 1) is float[] level ? (Byte(level[0]), Byte(level[0]), Byte(level[0])) : null,
                _ => null
            };
            return rgb is (int r, int g, int b) ? $"#{Math.Clamp(r, 0, 255):X2}{Math.Clamp(g, 0, 255):X2}{Math.Clamp(b, 0, 255):X2}" : null;
        }

        private (int r, int g, int b)? Named(string spec)
        {
            string[] parts = spec.Split('!');
            if (!definedColors.TryGetValue(parts[0], out (int r, int g, int b) color) && !namedColors.TryGetValue(parts[0], out color)) return null;
            if (parts.Length == 1) return color;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float percent)) return null;
            (int r, int g, int b) other = (255, 255, 255);
            if (parts.Length > 2 && !namedColors.TryGetValue(parts[2], out other)) return null;
            float t = Math.Clamp(percent, 0f, 100f) / 100f;
            return (Mix(color.r, other.r, t), Mix(color.g, other.g, t), Mix(color.b, other.b, t));
        }

        private static int Mix(int a, int b, float t) => (int)MathF.Round(a * t + b * (1f - t));

        private static int Byte(float unit) => (int)MathF.Round(unit * 255f);

        private static float[]? Floats(string spec, int count)
        {
            string[] parts = spec.Split(',');
            if (parts.Length != count) return null;
            float[] values = new float[count];
            for (int i = 0; i < count; i++)
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])) return null;
            return values;
        }
        #endregion

        #region ---- tables ----
        // text-mode control sequences that stand for characters
        private static readonly Dictionary<string, string> symbols = new Dictionary<string, string>
        {
            ["%"] = "%", ["&"] = "&", ["$"] = "$", ["#"] = "#", ["_"] = "_", ["{"] = "{", ["}"] = "}",
            ["i"] = "ı", ["j"] = "ȷ", ["ss"] = "ß", ["SS"] = "SS", ["ae"] = "æ", ["AE"] = "Æ", ["oe"] = "œ", ["OE"] = "Œ",
            ["o"] = "ø", ["O"] = "Ø", ["l"] = "ł", ["L"] = "Ł", ["aa"] = "å", ["AA"] = "Å",
            ["dag"] = "†", ["ddag"] = "‡", ["S"] = "§", ["P"] = "¶", ["copyright"] = "©", ["pounds"] = "£",
            ["ldots"] = "…", ["dots"] = "…", ["textellipsis"] = "…",
            ["textbackslash"] = "\\", ["textasciitilde"] = "~", ["textasciicircum"] = "^", ["textbar"] = "|",
            ["textless"] = "<", ["textgreater"] = ">", ["textunderscore"] = "_", ["slash"] = "/",
            ["textendash"] = "–", ["textemdash"] = "—", ["textquoteleft"] = "‘", ["textquoteright"] = "’",
            ["textquotedblleft"] = "“", ["textquotedblright"] = "”", ["quotesinglbase"] = "‚", ["quotedblbase"] = "„",
            ["guillemotleft"] = "«", ["guillemotright"] = "»", ["guilsinglleft"] = "‹", ["guilsinglright"] = "›",
            ["textbullet"] = "•", ["textdagger"] = "†", ["textdaggerdbl"] = "‡", ["textsection"] = "§", ["textparagraph"] = "¶",
            ["textregistered"] = "®", ["textcopyright"] = "©", ["textdegree"] = "°", ["euro"] = "€", ["texteuro"] = "€",
            ["textperthousand"] = "‰", ["textexclamdown"] = "¡", ["textquestiondown"] = "¿",
            ["TeX"] = "TeX", ["LaTeX"] = "LaTeX", ["LaTeXe"] = "LaTeX2e"
        };

        // xcolor's base colours
        private static readonly Dictionary<string, (int r, int g, int b)> namedColors = new Dictionary<string, (int, int, int)>
        {
            ["black"] = (0, 0, 0), ["white"] = (255, 255, 255), ["red"] = (255, 0, 0), ["green"] = (0, 255, 0),
            ["blue"] = (0, 0, 255), ["cyan"] = (0, 255, 255), ["magenta"] = (255, 0, 255), ["yellow"] = (255, 255, 0),
            ["gray"] = (128, 128, 128), ["darkgray"] = (64, 64, 64), ["lightgray"] = (191, 191, 191), ["brown"] = (191, 128, 64),
            ["lime"] = (191, 255, 0), ["olive"] = (128, 128, 0), ["orange"] = (255, 128, 0), ["pink"] = (255, 191, 191),
            ["purple"] = (191, 0, 64), ["teal"] = (0, 128, 128), ["violet"] = (128, 0, 128)
        };

        // accent commands and the combining marks they put on a letter
        private static readonly Dictionary<string, char> accents = new Dictionary<string, char>
        {
            ["'"] = '́', ["`"] = '̀', ["^"] = '̂', ["\""] = '̈', ["~"] = '̃', ["="] = '̄',
            ["."] = '̇', ["u"] = '̆', ["v"] = '̌', ["H"] = '̋', ["c"] = '̧', ["k"] = '̨',
            ["r"] = '̊', ["d"] = '̣', ["b"] = '̱'
        };

        // an accent over nothing
        private static readonly Dictionary<char, char> spacingAccents = new Dictionary<char, char>
        {
            ['́'] = '´', ['̀'] = '`', ['̂'] = 'ˆ', ['̈'] = '¨', ['̃'] = '˜', ['̄'] = '¯',
            ['̇'] = '˙', ['̆'] = '˘', ['̌'] = 'ˇ', ['̋'] = '˝', ['̧'] = '¸', ['̨'] = '˛',
            ['̊'] = '˚'
        };
        #endregion
    }
}
