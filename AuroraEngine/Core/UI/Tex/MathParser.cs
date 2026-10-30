using ArctisAurora.Core.Filing;
using System.Globalization;
using System.Text;

namespace ArctisAurora.Core.UI.Tex
{
    public abstract class MathNode { }

    // a {…} group, or the whole formula
    public sealed class MathList : MathNode
    {
        public readonly List<MathNode> items = new List<MathNode>();
    }

    public sealed class MathSymbol : MathNode
    {
        public char ch;
        public FontStyle face;
        public MathClass cls;
        public bool bigOp;
        public bool limits;
    }

    // \text content and operator names
    public sealed class MathText : MathNode
    {
        public string text = string.Empty;
        public FontStyle face;
        public MathClass cls;
        public bool limits;
    }

    public sealed class MathScripts : MathNode
    {
        public MathNode nucleus = null!;
        public MathNode sup;
        public MathNode sub;
        public bool overUnder;
    }

    public sealed class MathFraction : MathNode
    {
        public MathNode num = null!;
        public MathNode den = null!;
        public MathStyle? style;
        public bool noRule;
    }

    public enum MathArrayKind { Matrix, Small, Cases, Array, Aligned, Gathered, Multline }

    // how a display environment spreads over the line width
    public enum MathFill { None, Align, FlAlign, Multline }

    // an environment's rows of cells
    public sealed class MathArray : MathNode
    {
        public MathArrayKind kind;
        public MathFill fill;
        public string columns = string.Empty;
        public readonly List<int> vrules = new List<int>();
        public float pairGap;
        public readonly List<List<MathNode>> rows = new List<List<MathNode>>();
        public readonly List<float> rowSkip = new List<float>();
        public readonly List<int> hlines = new List<int>();
    }

    // \tag's text, drawn at the right edge of a display
    public sealed class MathTag : MathNode
    {
        public string text = string.Empty;
    }

    public sealed class MathFramed : MathNode
    {
        public MathNode body = null!;
    }

    public sealed class MathRadical : MathNode
    {
        public MathNode body = null!;
        public MathNode degree;
    }

    // \left…\right; '\0' is the empty delimiter
    public sealed class MathDelimited : MathNode
    {
        public char left;
        public char right;
        public MathNode body = null!;
    }

    public sealed class MathAccent : MathNode
    {
        public char accent;
        public bool bar;
        public MathNode body = null!;
    }

    public sealed class MathSpace : MathNode
    {
        public float em;
    }

    public sealed class MathError : MathNode
    {
        public string source = string.Empty;
    }

    public static class MathParser
    {
        // Parses TeX math; anything it cannot read makes the whole formula a MathError.
        public static MathNode Parse(string tex)
        {
            Parser parser = new Parser(tex ?? string.Empty);
            MathList list = parser.ParseList(Stop.End);
            return parser.failed ? new MathError { source = tex ?? string.Empty } : list;
        }

        private enum Stop { End, Brace, Bracket, Right, Cell }

        // environment → kind and delimiters
        private static readonly Dictionary<string, (MathArrayKind kind, char left, char right)> environments = new()
        {
            ["matrix"] = (MathArrayKind.Matrix, '\0', '\0'), ["pmatrix"] = (MathArrayKind.Matrix, '(', ')'),
            ["bmatrix"] = (MathArrayKind.Matrix, '[', ']'), ["Bmatrix"] = (MathArrayKind.Matrix, '{', '}'),
            ["vmatrix"] = (MathArrayKind.Matrix, '|', '|'), ["Vmatrix"] = (MathArrayKind.Matrix, '‖', '‖'),
            ["smallmatrix"] = (MathArrayKind.Small, '\0', '\0'), ["cases"] = (MathArrayKind.Cases, '{', '\0'),
            ["array"] = (MathArrayKind.Array, '\0', '\0'),
            ["aligned"] = (MathArrayKind.Aligned, '\0', '\0'), ["split"] = (MathArrayKind.Aligned, '\0', '\0'),
            ["alignedat"] = (MathArrayKind.Aligned, '\0', '\0'),
            ["align"] = (MathArrayKind.Aligned, '\0', '\0'), ["align*"] = (MathArrayKind.Aligned, '\0', '\0'),
            ["flalign"] = (MathArrayKind.Aligned, '\0', '\0'), ["flalign*"] = (MathArrayKind.Aligned, '\0', '\0'),
            ["alignat"] = (MathArrayKind.Aligned, '\0', '\0'), ["alignat*"] = (MathArrayKind.Aligned, '\0', '\0'),
            ["gathered"] = (MathArrayKind.Gathered, '\0', '\0'), ["gather"] = (MathArrayKind.Gathered, '\0', '\0'),
            ["gather*"] = (MathArrayKind.Gathered, '\0', '\0'),
            ["multline"] = (MathArrayKind.Multline, '\0', '\0'), ["multline*"] = (MathArrayKind.Multline, '\0', '\0'),
        };

        // amsmath's \minalignsep, em
        private const float minAlignSep = 1f;

        private enum FontMode { Normal, Roman, Bold, Blackboard }

        private sealed class Parser
        {
            private readonly string s;
            private int pos;
            private FontMode mode;
            public bool failed;

            public Parser(string source) { s = source; }

            private bool AtEnd => pos >= s.Length;

            private void SkipSpace()
            {
                while (!AtEnd && char.IsWhiteSpace(s[pos])) pos++;
            }

            private MathList Fail()
            {
                failed = true;
                return new MathList();
            }

            public MathList ParseList(Stop stop)
            {
                MathList list = new MathList();
                while (!failed)
                {
                    SkipSpace();
                    if (AtEnd)
                    {
                        if (stop != Stop.End) Fail();
                        break;
                    }

                    char c = s[pos];
                    if (c == '}')
                    {
                        if (stop == Stop.Cell) break;
                        if (stop != Stop.Brace) Fail();
                        else pos++;
                        break;
                    }
                    if (c == ']' && stop == Stop.Bracket)
                    {
                        pos++;
                        break;
                    }
                    if (stop == Stop.Right && PeekCommand() == "right") break;
                    if (stop == Stop.Cell && (c == '&' || RowEnds() || PeekCommand() == "end")) break;

                    MathNode atom = ParseScripted();
                    if (atom != null) list.items.Add(atom);
                }
                return list;
            }

            private string PeekCommand()
            {
                if (AtEnd || s[pos] != '\\') return null;
                int end = pos + 1;
                while (end < s.Length && char.IsAsciiLetter(s[end])) end++;
                return s.Substring(pos + 1, end - pos - 1);
            }

            private bool RowEnds() => pos + 1 < s.Length && s[pos] == '\\' && s[pos + 1] == '\\';

            private MathNode ParseScripted()
            {
                MathNode nucleus = ParseAtom();
                if (failed) return null;

                MathNode sup = null, sub = null;
                while (!failed)
                {
                    SkipSpace();
                    if (AtEnd || (s[pos] != '^' && s[pos] != '_')) break;

                    bool isSup = s[pos] == '^';
                    pos++;
                    if ((isSup ? sup : sub) != null) { Fail(); break; }
                    MathNode argument = ParseArgument();
                    if (isSup) sup = argument; else sub = argument;
                }

                if (sup == null && sub == null) return nucleus;
                return new MathScripts { nucleus = nucleus ?? new MathList(), sup = sup, sub = sub };
            }

            // A {…} group, one command, or one character.
            private MathNode ParseArgument()
            {
                SkipSpace();
                if (AtEnd) return Fail();
                if (s[pos] == '{')
                {
                    pos++;
                    return ParseList(Stop.Brace);
                }
                if (s[pos] == '}' || s[pos] == '^' || s[pos] == '_') return Fail();
                return ParseAtom();
            }

            // null for an empty nucleus in front of ^ or _
            private MathNode ParseAtom()
            {
                char c = s[pos];
                switch (c)
                {
                    case '{':
                        pos++;
                        return ParseList(Stop.Brace);
                    case '\\':
                        return ParseCommand();
                    case '^':
                    case '_':
                        return null;
                    case '~':
                        pos++;
                        return new MathSpace { em = 1f / 3f };
                    case '&':
                    case '#':
                    case '$':
                        return Fail();
                    case '\'':
                        pos++;
                        return new MathSymbol { ch = '′', face = FontStyle.Regular, cls = MathClass.Ord };
                }

                pos++;
                return Character(c);
            }

            private MathNode Character(char c)
            {
                if (MathSymbols.IsBigOp(c, out bool limits))
                    return new MathSymbol { ch = c, face = FontStyle.Regular, cls = MathClass.Op, bigOp = true, limits = limits };

                char drawn = c == '-' ? '−' : c == '*' ? '∗' : c;
                return new MathSymbol { ch = drawn, face = FaceFor(drawn), cls = MathSymbols.ClassOf(c) };
            }

            private static bool IsItalicLetter(char c) =>
                char.IsAsciiLetter(c) || (c >= 'α' && c <= 'ω') || c == 'ϑ' || c == 'ϕ' || c == 'ϖ' || c == 'ϱ' || c == 'ϵ';

            private FontStyle FaceFor(char c)
            {
                if (mode == FontMode.Bold) return FontStyle.Bold;
                if (mode == FontMode.Normal && IsItalicLetter(c)) return FontStyle.Italic;
                return FontStyle.Regular;
            }

            private string ReadCommandName()
            {
                pos++;
                if (AtEnd) return null;
                int start = pos;
                if (char.IsAsciiLetter(s[pos]))
                    while (!AtEnd && char.IsAsciiLetter(s[pos])) pos++;
                else
                    pos++;
                return s.Substring(start, pos - start);
            }

            private MathNode ParseCommand()
            {
                string name = ReadCommandName();
                if (name == null) return Fail();

                switch (name)
                {
                    case ",": return new MathSpace { em = 3f / 18f };
                    case ":": return new MathSpace { em = 4f / 18f };
                    case ";": return new MathSpace { em = 5f / 18f };
                    case "!": return new MathSpace { em = -3f / 18f };
                    case " ": return new MathSpace { em = 1f / 3f };
                    case "quad": return new MathSpace { em = 1f };
                    case "qquad": return new MathSpace { em = 2f };
                    case "mkern":
                    case "mskip":
                        return new MathSpace { em = ReadMu() / 18f };

                    case "frac":
                    case "dfrac":
                    case "tfrac":
                    {
                        MathNode num = ParseArgument();
                        MathNode den = ParseArgument();
                        MathStyle? style = name == "dfrac" ? MathStyle.Display : name == "tfrac" ? MathStyle.Text : null;
                        return new MathFraction { num = num, den = den, style = style };
                    }

                    case "sqrt":
                    {
                        MathNode degree = null;
                        SkipSpace();
                        if (!AtEnd && s[pos] == '[')
                        {
                            pos++;
                            degree = ParseList(Stop.Bracket);
                        }
                        return new MathRadical { body = ParseArgument(), degree = degree };
                    }

                    case "left":
                    {
                        char left = ReadDelimiter();
                        MathList body = ParseList(Stop.Right);
                        if (failed || PeekCommand() != "right") return Fail();
                        ReadCommandName();
                        char right = ReadDelimiter();
                        return new MathDelimited { left = left, right = right, body = body };
                    }

                    case "binom":
                    case "dbinom":
                    case "tbinom":
                    {
                        MathNode top = ParseArgument();
                        MathNode bottom = ParseArgument();
                        MathStyle? style = name == "dbinom" ? MathStyle.Display : name == "tbinom" ? MathStyle.Text : null;
                        return new MathDelimited
                        {
                            left = '(', right = ')',
                            body = new MathFraction { num = top, den = bottom, style = style, noRule = true }
                        };
                    }

                    case "overset":
                    case "underset":
                    {
                        MathNode script = ParseArgument();
                        MathNode nucleus = ParseArgument();
                        if (nucleus is MathList { items.Count: 1 } single) nucleus = single.items[0];
                        return name == "overset"
                            ? new MathScripts { nucleus = nucleus, sup = script, overUnder = true }
                            : new MathScripts { nucleus = nucleus, sub = script, overUnder = true };
                    }

                    case "boxed": return new MathFramed { body = ParseArgument() };

                    case "operatorname":
                    {
                        SkipSpace();
                        bool star = Star();
                        return new MathText { text = ReadRawGroup(), face = FontStyle.Regular, cls = MathClass.Op, limits = star };
                    }

                    case "tag":
                    {
                        SkipSpace();
                        bool star = Star();
                        string text = ReadRawGroup();
                        return new MathTag { text = star ? text : "(" + text + ")" };
                    }
                    case "notag":
                    case "nonumber":
                        return null;
                    case "label":
                        ReadRawGroup();
                        return null;

                    case "begin": return Environment();
                    case "substack":
                    {
                        SkipSpace();
                        if (AtEnd || s[pos] != '{') return Fail();
                        pos++;
                        MathArray stack = new MathArray { kind = MathArrayKind.Small };
                        ReadRows(stack, null);
                        return stack.rows.Any(row => row.Count > 1) ? Fail() : stack;
                    }

                    case "text":
                    case "hbox":
                        return new MathText { text = ReadRawGroup(), face = FontStyle.Regular, cls = MathClass.Ord };
                    case "mathrm": return InMode(FontMode.Roman);
                    case "mathbf": return InMode(FontMode.Bold);
                    case "mathbb": return InMode(FontMode.Blackboard);

                    case "bar":
                    case "overline":
                        return new MathAccent { bar = true, body = ParseArgument() };
                }

                if (MathSymbols.accents.TryGetValue(name, out char accent))
                    return new MathAccent { accent = accent, body = ParseArgument() };

                if (MathSymbols.operatorNames.TryGetValue(name, out bool opLimits))
                {
                    string text = name == "liminf" ? "lim inf" : name == "limsup" ? "lim sup" : name;
                    return new MathText { text = text, face = FontStyle.Regular, cls = MathClass.Op, limits = opLimits };
                }

                if (MathSymbols.bigOps.TryGetValue(name, out (char ch, bool limits) op))
                    return new MathSymbol { ch = op.ch, face = FontStyle.Regular, cls = MathClass.Op, bigOp = true, limits = op.limits };

                if (MathSymbols.commands.TryGetValue(name, out (char ch, MathClass cls) symbol))
                    return new MathSymbol { ch = symbol.ch, face = FaceFor(symbol.ch), cls = symbol.cls };

                return Fail();
            }

            // \mathrm, \mathbf and \mathbb: the argument parsed under another font mode
            private MathNode InMode(FontMode inner)
            {
                FontMode outer = mode;
                mode = inner;
                MathNode argument = ParseArgument();
                mode = outer;
                if (inner == FontMode.Blackboard) Blackboard(argument);
                return argument;
            }

            private bool Star()
            {
                if (AtEnd || s[pos] != '*') return false;
                pos++;
                return true;
            }

            // \begin{name} … \end{name}
            private MathNode Environment()
            {
                string name = ReadRawGroup();
                if (failed) return null;
                if (name is "equation" or "equation*")
                {
                    MathList body = ParseList(Stop.Cell);
                    if (failed || PeekCommand() != "end") return Fail();
                    ReadCommandName();
                    return ReadRawGroup() == name ? body : Fail();
                }
                if (!environments.TryGetValue(name, out (MathArrayKind kind, char left, char right) env)) return Fail();

                MathArray array = new MathArray
                {
                    kind = env.kind,
                    fill = name.TrimEnd('*') switch
                    {
                        "align" => MathFill.Align,
                        "flalign" => MathFill.FlAlign,
                        "multline" => MathFill.Multline,
                        _ => MathFill.None
                    }
                };
                if (env.kind == MathArrayKind.Aligned)
                {
                    bool at = name.StartsWith("alignat") || name == "alignedat";
                    if (at) ReadRawGroup();
                    array.pairGap = at ? 0f : minAlignSep;
                }
                if (env.kind == MathArrayKind.Array && !ColumnSpec(array)) return Fail();

                ReadRows(array, name);
                if (failed) return null;
                foreach (List<MathNode> row in array.rows)
                    if ((row.Count > 1 && env.kind is MathArrayKind.Gathered or MathArrayKind.Multline)
                        || (env.kind == MathArrayKind.Array && row.Count > array.columns.Length))
                        return Fail();

                if (env.left == '\0' && env.right == '\0') return array;
                return new MathDelimited { left = env.left, right = env.right, body = array };
            }

            // Rows of cells up to \end{end}, or up to } when end is null.
            private void ReadRows(MathArray array, string end)
            {
                while (!failed)
                {
                    SkipSpace();
                    while (PeekCommand() == "hline")
                    {
                        ReadCommandName();
                        array.hlines.Add(array.rows.Count);
                        SkipSpace();
                    }

                    List<MathNode> row = new List<MathNode> { ParseList(Stop.Cell) };
                    while (!failed && s[pos] == '&')
                    {
                        pos++;
                        row.Add(ParseList(Stop.Cell));
                    }
                    if (failed) return;

                    if (RowEnds())
                    {
                        pos += 2;
                        array.rows.Add(row);
                        array.rowSkip.Add(RowSkip());
                        continue;
                    }

                    if (end == null)
                    {
                        if (s[pos] != '}') { Fail(); return; }
                        pos++;
                    }
                    else
                    {
                        if (PeekCommand() != "end") { Fail(); return; }
                        ReadCommandName();
                        if (ReadRawGroup() != end) { Fail(); return; }
                    }
                    if (array.rows.Count == 0 || row.Count > 1 || row[0] is not MathList { items.Count: 0 }) array.rows.Add(row);
                    return;
                }
            }

            // \\'s optional * and [length], in em
            private float RowSkip()
            {
                Star();
                if (AtEnd || s[pos] != '[') return 0f;
                int close = s.IndexOf(']', pos);
                if (close < 0)
                {
                    Fail();
                    return 0f;
                }

                string length = s.Substring(pos + 1, close - pos - 1).Trim();
                pos = close + 1;
                int unit = 0;
                while (unit < length.Length && (char.IsAsciiDigit(length[unit]) || length[unit] is '.' or '-' or '+')) unit++;
                float? perEm = length[unit..].Trim() switch
                {
                    "em" => 1f, "ex" => 0.430554f, "pt" => 0.1f, "mm" => 0.28453f, "cm" => 2.8453f, "in" => 7.227f, _ => null
                };
                if (perEm == null || !float.TryParse(length[..unit], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                {
                    Fail();
                    return 0f;
                }
                return value * perEm.Value;
            }

            // array's {l c r |} columns
            private bool ColumnSpec(MathArray array)
            {
                string spec = ReadRawGroup();
                StringBuilder columns = new StringBuilder();
                foreach (char ch in spec)
                {
                    if (ch is 'l' or 'c' or 'r') columns.Append(ch);
                    else if (ch == '|') array.vrules.Add(columns.Length);
                    else if (!char.IsWhiteSpace(ch)) return false;
                }
                array.columns = columns.ToString();
                return !failed && columns.Length > 0;
            }

            private static void Blackboard(MathNode node)
            {
                if (node is MathSymbol symbol && MathSymbols.blackboard.TryGetValue(symbol.ch, out char doubleStruck))
                    symbol.ch = doubleStruck;
                else if (node is MathList list)
                    foreach (MathNode item in list.items) Blackboard(item);
            }

            // \mkern's or \mskip's amount in mu; a glue's plus and minus parts are read and dropped.
            private float ReadMu()
            {
                float mu = ReadNumber();
                if (!SkipKeyword("mu"))
                {
                    Fail();
                    return 0f;
                }
                if (SkipKeyword("plus"))
                {
                    ReadNumber();
                    SkipKeyword("mu");
                }
                if (SkipKeyword("minus"))
                {
                    ReadNumber();
                    SkipKeyword("mu");
                }
                return mu;
            }

            private float ReadNumber()
            {
                SkipSpace();
                int start = pos;
                if (!AtEnd && s[pos] is '-' or '+') pos++;
                while (!AtEnd && (char.IsAsciiDigit(s[pos]) || s[pos] == '.')) pos++;
                return float.TryParse(s.AsSpan(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : 0f;
            }

            private bool SkipKeyword(string keyword)
            {
                SkipSpace();
                if (string.CompareOrdinal(s, pos, keyword, 0, keyword.Length) != 0) return false;
                pos += keyword.Length;
                return true;
            }

            // \text's braces, taken verbatim
            private string ReadRawGroup()
            {
                SkipSpace();
                if (AtEnd || s[pos] != '{') { Fail(); return string.Empty; }

                int depth = 0;
                int start = pos + 1;
                for (; pos < s.Length; pos++)
                {
                    if (s[pos] == '\\' && pos + 1 < s.Length) { pos++; continue; }
                    if (s[pos] == '{') depth++;
                    else if (s[pos] == '}' && --depth == 0)
                    {
                        string text = s.Substring(start, pos - start);
                        pos++;
                        return text.Replace("\\{", "{").Replace("\\}", "}");
                    }
                }
                Fail();
                return string.Empty;
            }

            private char ReadDelimiter()
            {
                SkipSpace();
                if (AtEnd) { Fail(); return '\0'; }

                char c = s[pos];
                if (c == '\\')
                {
                    string name = ReadCommandName();
                    if (name != null && MathSymbols.delimiters.TryGetValue(name, out char named)) return named;
                    Fail();
                    return '\0';
                }

                pos++;
                switch (c)
                {
                    case '.': return '\0';
                    case '(': case ')': case '[': case ']': case '|': case '/': return c;
                    case '<': return '⟨';
                    case '>': return '⟩';
                }
                Fail();
                return '\0';
            }
        }
    }
}
