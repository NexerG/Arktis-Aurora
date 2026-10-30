namespace ArctisAurora.Core.Tex
{
    public enum TexAlign { Justify, Left, Center, Right }

    public enum TexParKind { Text, Heading, Code, Quote }

    public enum TexListKind { None, Itemize, Enumerate, Description }

    // What a paragraph is besides its text.
    public struct TexParStyle
    {
        public TexParKind kind;

        // heading 1-6
        public int level;
        public TexAlign align;

        // list item
        public TexListKind list;
        public int listDepth;
        public int listKindDepth;
        public int itemNumber;

        // first paragraph of an \item
        public bool item;

        // an enumitem label's marker, and its leftmargin in sp; null and 0 leave the defaults
        public string? marker;
        public int leftMargin;

        // first line indented by \parindent
        public bool indent;

        // \parindent when it started, and \leftskip, \rightskip and \parfillskip's width when it ended, sp
        public int indentWidth;
        public int leftSkip;
        public int rightSkip;
        public int parFillSkip;
    }

    public abstract class TexNode { }

    #region ---- horizontal ----
    public sealed class TexChar : TexNode
    {
        public readonly char ch;
        public readonly TexStyle style;

        public TexChar(char ch, TexStyle style)
        {
            this.ch = ch;
            this.style = style;
        }
    }

    public sealed class TexGlueNode : TexNode
    {
        public readonly TexGlue glue;

        // from a space in the source
        public readonly bool interword;
        public readonly TexStyle style;

        // \leaders: the box repeated across the glue; null leaves it empty
        public TexHBox? leader;

        // TeX's space factor at an interword space, 1000 plain
        public int spaceFactor = 1000;

        public TexGlueNode(TexGlue glue, bool interword, TexStyle style)
        {
            this.glue = glue;
            this.interword = interword;
            this.style = style;
        }
    }

    // \rule: a filled box in the paragraph, sp
    public sealed class TexRuleBox : TexNode
    {
        public readonly int width, height, raise;
        public readonly TexStyle style;

        public TexRuleBox(int width, int height, int raise, TexStyle style)
        {
            this.width = width;
            this.height = height;
            this.raise = raise;
            this.style = style;
        }
    }

    public sealed class TexKern : TexNode
    {
        public readonly int width;
        public readonly TexStyle style;

        public TexKern(int width, TexStyle style)
        {
            this.width = width;
            this.style = style;
        }
    }

    public sealed class TexPenalty : TexNode
    {
        public const int Forced = -10000;

        public readonly int penalty;

        public TexPenalty(int penalty) => this.penalty = penalty;
    }

    public sealed class TexMathNode : TexNode
    {
        public string source;
        public readonly bool display;
        public readonly TexStyle style;

        // the footnote this mark anchors, when it is a \footnote's mark in the text
        public TexFootnote? note;

        // an array row's \strutbox height plus depth, em: \arraystretch × \baselineskip; 0 is the layout's
        public float strut;

        public TexMathNode(string source, bool display, TexStyle style)
        {
            this.source = source;
            this.display = display;
            this.style = style;
        }
    }

    public sealed class TexHBox : TexNode
    {
        public readonly List<TexNode> list = new List<TexNode>();

        // \hbox to: the width in sp, its slack taken by the fil glue inside; -1 is the natural width
        public int width = -1;
    }

    // \ref or \cite; text is filled once the whole document has been read.
    public sealed class TexRefNode : TexNode
    {
        public readonly string[] keys;
        public readonly bool cite;
        public readonly TexStyle style;
        public readonly int line;
        public readonly int column;
        public string text = "??";

        // \pageref: filled from the laid-out pages, not the label's text
        public bool page;

        public TexRefNode(string[] keys, bool cite, TexStyle style, int line, int column)
        {
            this.keys = keys;
            this.cite = cite;
            this.style = style;
            this.line = line;
            this.column = column;
        }
    }

    // where a \label stood, so \pageref can find its page
    public sealed class TexLabelMark : TexNode
    {
        public readonly string key;

        public TexLabelMark(string key) => this.key = key;
    }

    // \includegraphics; sizes in sp, 0 = not given.
    public sealed class TexImage : TexNode
    {
        public readonly string path;
        public int width;
        public int height;
        public float scale;
        public float angle;
        public bool keepAspect;

        public TexImage(string path) => this.path = path;
    }
    #endregion

    #region ---- vertical ----
    public sealed class TexParagraph : TexNode
    {
        public TexParStyle style;
        public readonly List<TexNode> list = new List<TexNode>();

        // source line it started on, 0 = none
        public int line;

        // \thispagestyle and the marks set on it; null leaves a mark as it was
        public string? pageStyle;
        public string? markLeft;
        public string? markRight;

        // the language it started in, which picks its hyphenation patterns
        public string language = "english";

        // \baselineskip when it ended, stretched by \baselinestretch, sp
        public int baselineSkip;

        // a caption's \strut on every line
        public bool strut;

        public TexParagraph(TexParStyle style) => this.style = style;
    }

    public sealed class TexVGlue : TexNode
    {
        public readonly TexGlue glue;

        // \addvspace: takes the larger of this and the space already pending, instead of adding
        public readonly bool merge;

        public TexVGlue(TexGlue glue, bool merge = false)
        {
            this.glue = glue;
            this.merge = merge;
        }
    }

    public sealed class TexVPenalty : TexNode
    {
        public readonly int penalty;

        public TexVPenalty(int penalty) => this.penalty = penalty;
    }

    public sealed class TexRuleNode : TexNode { }

    // a \footnote's text, set at the foot of the page its mark lands on
    public sealed class TexFootnote
    {
        public readonly string id;
        public readonly List<TexNode> vlist = new List<TexNode>();

        public TexFootnote(string id) => this.id = id;
    }

    // a figure or table environment: its body, set where [htbp!H] lets the page place it
    public sealed class TexFloat : TexNode
    {
        public readonly string kind;
        public readonly string placement;
        public readonly List<TexNode> vlist = new List<TexNode>();

        public TexFloat(string kind, string placement)
        {
            this.kind = kind;
            this.placement = placement;
        }
    }

    // \newpage, or \clearpage, which also puts out every waiting float
    public sealed class TexPageBreak : TexNode
    {
        public readonly bool clear;

        public TexPageBreak(bool clear) => this.clear = clear;
    }

    // a page style's slots, typeset, by place name (HeadLeft ... FootRight), and its rules, pt
    public sealed class TexPageStyle
    {
        public readonly Dictionary<string, TexParagraph> slots = new Dictionary<string, TexParagraph>();
        public float headRule;
        public float footRule;
    }

    // where \bibliography put the reference list, filled after the document ends
    public sealed class TexBibliographyMark : TexNode { }

    // \@starttoc: where a contents list (toc, lof, lot) is typeset once every entry is known
    public sealed class TexContentsMark : TexNode
    {
        public readonly string extension;

        public TexContentsMark(string extension) => this.extension = extension;
    }

    // tabular column: alignment, and a p{} width in sp (0 = natural)
    public struct TexColumn
    {
        public TexAlign align;
        public int width;
    }

    public sealed class TexTableCell
    {
        public readonly List<TexNode> vlist = new List<TexNode>();
        public int span = 1;
    }

    public enum TexRuleKind { Plain, Heavy, Light, Cmid }

    // \hline, \cline or a booktabs rule above row (rows.Count = under the last), over columns from..to
    public sealed class TexTableRule
    {
        public int row;
        public TexRuleKind kind;
        public int from;
        public int to;
        public bool trimLeft;
        public bool trimRight;

        // sp; 0 = the kind's own
        public int width;
    }

    public sealed class TexTable : TexNode
    {
        public readonly List<TexColumn> columns = new List<TexColumn>();
        public readonly List<List<TexTableCell>> rows = new List<List<TexTableCell>>();

        // horizontal rules in source order; the | count at each column boundary (index = column before it)
        public readonly List<TexTableRule> rules = new List<TexTableRule>();
        public readonly List<int> vrules = new List<int>();

        // the alignment of the paragraph the tabular sits in, taken when it ends
        public TexAlign align = TexAlign.Left;

        // \parindent before a tabular that opens its paragraph, sp
        public int indent;

        // source line of \begin{tabular}
        public int line;
    }
    #endregion
}
