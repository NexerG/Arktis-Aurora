using System.Globalization;
using System.Text;

namespace ArctisAurora.Core.UI.Sheets
{
    public enum SheetValueKind : byte { Empty, Number, Text, Error }

    // What a cell holds once evaluated; an error keeps its code in text.
    public readonly struct SheetValue
    {
        public readonly SheetValueKind kind;
        public readonly double number;
        public readonly string? text;

        private SheetValue(SheetValueKind kind, double number, string? text)
        {
            this.kind = kind;
            this.number = number;
            this.text = text;
        }

        public static readonly SheetValue Empty = default;

        public static SheetValue Number(double value) =>
            double.IsFinite(value) ? new SheetValue(SheetValueKind.Number, value, null) : Error(SheetFormula.num);

        public static SheetValue Text(string text) => new SheetValue(SheetValueKind.Text, 0d, text);

        public static SheetValue Error(string code) => new SheetValue(SheetValueKind.Error, 0d, code);

        // A typed cell: a number keeps the text it was typed as.
        public static SheetValue FromRaw(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return Empty;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                return new SheetValue(SheetValueKind.Number, value, raw);
            return Text(raw);
        }

        public string? Display() => kind switch
        {
            SheetValueKind.Number => text ?? number.ToString("G15", CultureInfo.InvariantCulture),
            SheetValueKind.Empty => null,
            _ => text
        };

        // A number through its cell's format; anything else as Display().
        public string? Display(string? format) =>
            kind == SheetValueKind.Number && format != null ? number.ToString(format, CultureInfo.InvariantCulture) : Display();
    }

    // A parsed "=…" cell.
    public sealed class SheetFormula
    {
        // error codes
        public const string div0 = "#DIV/0!";
        public const string value = "#VALUE!";
        public const string name = "#NAME?";
        public const string reference = "#REF!";
        public const string cycle = "#CYCLE!";
        public const string parse = "#ERROR!";
        public const string num = "#NUM!";
        public const string locked = "#LOCKED!";
        public const string vault = "#VAULT!";

        // grid bounds
        public const int maxRows = 1048576;
        public const int maxColumns = 16384;

        private readonly Node root;

        private SheetFormula(Node root) => this.root = root;

        public static bool IsFormula(string? raw) => raw != null && raw.Length > 0 && raw[0] == '=';

        public static SheetFormula Parse(string raw)
        {
            Parser parser = new Parser(raw, 1);
            Node? node = parser.Formula();
            return new SheetFormula(node ?? new Constant(SheetValue.Error(parse)));
        }

        // The formula with each page the rename answers for (file part, or null on its own file) replaced.
        public static string RenamePage(string raw, Func<string?, string, string?> rename) =>
            RenamePrefix(raw, (file, page) => rename(file, page) is string renamed && renamed != page ? (file, renamed) : null);

        // The formula with each prefix the callback answers for (file part, or null on its own file) replaced; unparseable text is left alone.
        public static string RenamePrefix(string raw, Func<string?, string, (string? file, string page)?> rename)
        {
            List<(int start, int end, string? file, string page)> prefixes = new List<(int, int, string?, string)>();
            if (new Parser(raw, 1, prefixes).Formula() == null) return raw;

            string result = raw;
            for (int k = prefixes.Count - 1; k >= 0; k--)
            {
                (int start, int end, string? file, string page) = prefixes[k];
                (string? file, string page)? renamed = rename(file, page);
                if (renamed is not { } to || to.file == file && to.page == page) continue;
                result = result[..start] + Prefix(to.file, to.page) + result[end..];
            }
            return result;
        }

        // The other files the formula reads, by their file part; none when it doesn't parse.
        public static List<string> FileParts(string raw)
        {
            List<(int start, int end, string? file, string page)> prefixes = new List<(int, int, string?, string)>();
            if (new Parser(raw, 1, prefixes).Formula() == null) return new List<string>();
            return prefixes.Where(p => p.file != null).Select(p => p.file!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The formula with each address on a page onPage answers for (file part, page; both null on the formula's own page)
        // moved by count when at or past at; a negative count moves those past the removed bands. Unparseable text is left alone.
        public static string ShiftCells(string raw, Func<string?, string?, bool> onPage, bool column, int at, int count)
        {
            List<(int start, int end, string? file, string? page)> addresses = new List<(int, int, string?, string?)>();
            if (new Parser(raw, 1, null, addresses).Formula() == null) return raw;

            int from = count < 0 ? at - count : at;
            string result = raw;
            for (int k = addresses.Count - 1; k >= 0; k--)
            {
                (int start, int end, string? file, string? page) = addresses[k];
                if (!onPage(file, page) || !SheetDocument.TryParseAddress(raw[start..end], out int row, out int col)) continue;
                if ((column ? col : row) < from) continue;
                string moved = column ? SheetDocument.Address(row, col + count) : SheetDocument.Address(row + count, col);
                result = result[..start] + moved + result[end..];
            }
            return result;
        }

        // "Page", "[file]Page", quoted when a name needs it; the "!" is the caller's.
        public static string Prefix(string? file, string page)
        {
            bool plain = page.Length > 0 && (char.IsAsciiLetter(page[0]) || page[0] == '_') && Plain(page, false)
                && (file == null || Plain(file, true));
            string body = file == null ? page : "[" + file + "]" + page;
            return plain ? body : "'" + body.Replace("'", "''") + "'";
        }

        private static bool Plain(string name, bool path)
        {
            foreach (char c in name)
                if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '.' && !(path && c == '/')) return false;
            return name.Length > 0;
        }

        public SheetValue Evaluate(SheetCalc calc, SheetPage home)
        {
            SheetValue result = root.Evaluate(calc, home);
            return result.kind == SheetValueKind.Empty ? SheetValue.Number(0d) : result;
        }

        // Every cell this formula reads; a page that does not exist reads nothing.
        public void References(SheetCalc calc, SheetPage home, HashSet<SheetCellId> into) => root.References(calc, home, into);

        #region ---- nodes ----
        private abstract class Node
        {
            public abstract SheetValue Evaluate(SheetCalc calc, SheetPage home);

            public virtual void References(SheetCalc calc, SheetPage home, HashSet<SheetCellId> into) { }
        }

        private sealed class Constant : Node
        {
            private readonly SheetValue result;

            public Constant(SheetValue result) => this.result = result;

            public override SheetValue Evaluate(SheetCalc calc, SheetPage home) => result;
        }

        // A cell, or a block of cells when it spans more than one.
        private sealed class Reference : Node
        {
            public readonly string? file;
            public readonly string? page;
            public readonly int top, left, bottom, right;

            public Reference(string? file, string? page, int top, int left, int bottom, int right)
            {
                this.file = file;
                this.page = page;
                (this.top, this.bottom) = (Math.Min(top, bottom), Math.Max(top, bottom));
                (this.left, this.right) = (Math.Min(left, right), Math.Max(left, right));
            }

            public bool IsRange => top != bottom || left != right;

            public SheetPage? Target(SheetCalc calc, SheetPage home) => page == null ? home : calc.PageNamed(home, file, page);

            // The error for a target that did not resolve.
            public SheetValue Missing(SheetPage home) =>
                SheetValue.Error(file != null && SheetBook.IsLocked(SheetBook.PathOf(SheetBook.OwnerOf(home)), file) ? locked : reference);

            public override SheetValue Evaluate(SheetCalc calc, SheetPage home)
            {
                SheetPage? target = Target(calc, home);
                if (target == null) return Missing(home);
                if (IsRange) return SheetValue.Error(value);
                return calc.Read(target, top, left);
            }

            public override void References(SheetCalc calc, SheetPage home, HashSet<SheetCellId> into)
            {
                SheetPage? target = Target(calc, home);
                if (target == null) return;
                for (int r = top; r <= bottom; r++)
                    for (int c = left; c <= right; c++)
                        into.Add(new SheetCellId(target, SheetDocument.Key(r, c)));
            }
        }

        private sealed class Negate : Node
        {
            private readonly Node operand;

            public Negate(Node operand) => this.operand = operand;

            public override SheetValue Evaluate(SheetCalc calc, SheetPage home)
            {
                SheetValue v = operand.Evaluate(calc, home);
                return AsNumber(v, out double n) ? SheetValue.Number(-n) : Failure(v);
            }

            public override void References(SheetCalc calc, SheetPage home, HashSet<SheetCellId> into) => operand.References(calc, home, into);
        }

        private sealed class Binary : Node
        {
            private readonly char op;
            private readonly Node left, right;

            public Binary(char op, Node left, Node right)
            {
                this.op = op;
                this.left = left;
                this.right = right;
            }

            public override SheetValue Evaluate(SheetCalc calc, SheetPage home)
            {
                SheetValue a = left.Evaluate(calc, home);
                if (!AsNumber(a, out double x)) return Failure(a);
                SheetValue b = right.Evaluate(calc, home);
                if (!AsNumber(b, out double y)) return Failure(b);

                return op switch
                {
                    '+' => SheetValue.Number(x + y),
                    '-' => SheetValue.Number(x - y),
                    '*' => SheetValue.Number(x * y),
                    '/' => y == 0d ? SheetValue.Error(div0) : SheetValue.Number(x / y),
                    _ => SheetValue.Number(Math.Pow(x, y))
                };
            }

            public override void References(SheetCalc calc, SheetPage home, HashSet<SheetCellId> into)
            {
                left.References(calc, home, into);
                right.References(calc, home, into);
            }
        }

        // = <> < > <= >=, as 1 or 0; text compares ignoring case and sorts after numbers.
        private sealed class Compare : Node
        {
            private readonly string op;
            private readonly Node left, right;

            public Compare(string op, Node left, Node right)
            {
                this.op = op;
                this.left = left;
                this.right = right;
            }

            public override SheetValue Evaluate(SheetCalc calc, SheetPage home)
            {
                SheetValue a = left.Evaluate(calc, home);
                if (a.kind == SheetValueKind.Error) return a;
                SheetValue b = right.Evaluate(calc, home);
                if (b.kind == SheetValueKind.Error) return b;

                int order;
                if (a.kind == SheetValueKind.Text || b.kind == SheetValueKind.Text)
                {
                    int rankA = a.kind == SheetValueKind.Number ? 0 : 1;
                    int rankB = b.kind == SheetValueKind.Number ? 0 : 1;
                    order = rankA != rankB ? rankA - rankB : string.Compare(a.text ?? "", b.text ?? "", StringComparison.OrdinalIgnoreCase);
                }
                else
                    order = a.number.CompareTo(b.number);

                bool result = op switch
                {
                    "=" => order == 0,
                    "<>" => order != 0,
                    "<" => order < 0,
                    ">" => order > 0,
                    "<=" => order <= 0,
                    _ => order >= 0
                };
                return SheetValue.Number(result ? 1d : 0d);
            }

            public override void References(SheetCalc calc, SheetPage home, HashSet<SheetCellId> into)
            {
                left.References(calc, home, into);
                right.References(calc, home, into);
            }
        }

        private sealed class Call : Node
        {
            private readonly string function;
            private readonly List<Node> arguments;

            public Call(string function, List<Node> arguments)
            {
                this.function = function;
                this.arguments = arguments;
            }

            public override SheetValue Evaluate(SheetCalc calc, SheetPage home)
            {
                switch (function.ToUpperInvariant())
                {
                    case "SUM":
                    case "MIN":
                    case "MAX":
                    case "AVERAGE":
                        SheetValue failure = Aggregate(calc, home, out double sum, out int count, out double min, out double max);
                        if (failure.kind == SheetValueKind.Error) return failure;
                        return function.ToUpperInvariant() switch
                        {
                            "SUM" => SheetValue.Number(sum),
                            "MIN" => SheetValue.Number(count == 0 ? 0d : min),
                            "MAX" => SheetValue.Number(count == 0 ? 0d : max),
                            _ => count == 0 ? SheetValue.Error(div0) : SheetValue.Number(sum / count)
                        };
                    case "ROUND":
                        return Rounded(calc, home, MidpointRounding.AwayFromZero, MidpointRounding.AwayFromZero);
                    case "ROUNDUP":
                        return Rounded(calc, home, MidpointRounding.ToPositiveInfinity, MidpointRounding.ToNegativeInfinity);
                    case "ROUNDDOWN":
                        return Rounded(calc, home, MidpointRounding.ToZero, MidpointRounding.ToZero);
                    case "IF":
                        if (arguments.Count < 2 || arguments.Count > 3) return SheetValue.Error(value);
                        SheetValue test = arguments[0].Evaluate(calc, home);
                        if (!AsNumber(test, out double condition)) return Failure(test);
                        if (condition != 0d) return arguments[1].Evaluate(calc, home);
                        return arguments.Count == 3 ? arguments[2].Evaluate(calc, home) : SheetValue.Number(0d);
                    default:
                        return SheetValue.Error(name);
                }
            }

            // Sum, count, min and max of every argument; a range skips text and blanks. An error comes back, Empty otherwise.
            private SheetValue Aggregate(SheetCalc calc, SheetPage home, out double sum, out int count, out double min, out double max)
            {
                sum = 0d;
                count = 0;
                min = double.MaxValue;
                max = double.MinValue;
                foreach (Node argument in arguments)
                {
                    if (argument is Reference cells)
                    {
                        SheetPage? target = cells.Target(calc, home);
                        if (target == null) return cells.Missing(home);
                        for (int r = cells.top; r <= cells.bottom; r++)
                            for (int c = cells.left; c <= cells.right; c++)
                            {
                                SheetValue v = calc.Read(target, r, c);
                                if (v.kind == SheetValueKind.Error) return v;
                                if (v.kind != SheetValueKind.Number) continue;
                                sum += v.number;
                                count++;
                                min = Math.Min(min, v.number);
                                max = Math.Max(max, v.number);
                            }
                        continue;
                    }

                    SheetValue result = argument.Evaluate(calc, home);
                    if (!AsNumber(result, out double n)) return Failure(result);
                    sum += n;
                    count++;
                    min = Math.Min(min, n);
                    max = Math.Max(max, n);
                }
                return SheetValue.Empty;
            }

            // ROUND/ROUNDUP/ROUNDDOWN(number, [digits]) on the 15 significant digits a double shows.
            private SheetValue Rounded(SheetCalc calc, SheetPage home, MidpointRounding positive, MidpointRounding negative)
            {
                if (arguments.Count < 1 || arguments.Count > 2) return SheetValue.Error(value);
                SheetValue a = arguments[0].Evaluate(calc, home);
                if (!AsNumber(a, out double x)) return Failure(a);
                double digits = 0d;
                if (arguments.Count == 2)
                {
                    SheetValue b = arguments[1].Evaluate(calc, home);
                    if (!AsNumber(b, out digits)) return Failure(b);
                }
                if (Math.Abs(x) >= 1e15) return SheetValue.Number(x);

                int d = (int)Math.Clamp(Math.Truncate(digits), -15d, 15d);
                MidpointRounding mode = x < 0d ? negative : positive;
                decimal m = (decimal)x;
                if (d >= 0) return SheetValue.Number((double)Math.Round(m, d, mode));
                decimal scale = (decimal)Math.Pow(10d, -d);
                return SheetValue.Number((double)(Math.Round(m / scale, 0, mode) * scale));
            }

            public override void References(SheetCalc calc, SheetPage home, HashSet<SheetCellId> into)
            {
                foreach (Node argument in arguments)
                    argument.References(calc, home, into);
            }
        }

        private static bool AsNumber(SheetValue v, out double n)
        {
            n = v.number;
            return v.kind == SheetValueKind.Number || v.kind == SheetValueKind.Empty;
        }

        private static SheetValue Failure(SheetValue v) => v.kind == SheetValueKind.Error ? v : SheetValue.Error(value);
        #endregion

        #region ---- parser ----
        // Lowest to highest: = <> < > <= >=, + -, * /, ^ (left to right), unary - +, then operands.
        private struct Parser
        {
            private readonly string s;
            private int i;

            // "[file]Page" and "Page" spans met, when collected
            private readonly List<(int start, int end, string? file, string page)>? prefixes;

            // cell address spans met, when collected
            private readonly List<(int start, int end, string? file, string? page)>? addresses;

            public Parser(string s, int start, List<(int, int, string?, string)>? prefixes = null,
                List<(int, int, string?, string?)>? addresses = null)
            {
                this.s = s;
                i = start;
                this.prefixes = prefixes;
                this.addresses = addresses;
            }

            public Node? Formula()
            {
                Node? node = Comparison();
                SkipSpace();
                return i == s.Length ? node : null;
            }

            private Node? Comparison()
            {
                Node? node = Additive();
                while (node != null && Operator() is string op)
                {
                    Node? right = Additive();
                    node = right == null ? null : new Compare(op, node, right);
                }
                return node;
            }

            // A comparison operator, consumed; null leaves the position alone.
            private string? Operator()
            {
                char c = Peek();
                if (c == '=')
                {
                    i++;
                    return "=";
                }
                if (c != '<' && c != '>') return null;
                i++;
                char next = i < s.Length ? s[i] : '\0';
                if (next != '=' && !(c == '<' && next == '>')) return c.ToString();
                i++;
                return c == '>' ? ">=" : next == '=' ? "<=" : "<>";
            }

            private Node? Additive()
            {
                Node? node = Multiplicative();
                while (node != null && (Peek() == '+' || Peek() == '-'))
                {
                    char op = s[i++];
                    Node? right = Multiplicative();
                    node = right == null ? null : new Binary(op, node, right);
                }
                return node;
            }

            private Node? Multiplicative()
            {
                Node? node = Power();
                while (node != null && (Peek() == '*' || Peek() == '/'))
                {
                    char op = s[i++];
                    Node? right = Power();
                    node = right == null ? null : new Binary(op, node, right);
                }
                return node;
            }

            private Node? Power()
            {
                Node? node = Unary();
                while (node != null && Peek() == '^')
                {
                    i++;
                    Node? right = Unary();
                    node = right == null ? null : new Binary('^', node, right);
                }
                return node;
            }

            private Node? Unary()
            {
                char c = Peek();
                if (c == '-' || c == '+')
                {
                    i++;
                    Node? operand = Unary();
                    if (operand == null) return null;
                    return c == '-' ? new Negate(operand) : operand;
                }
                return Primary();
            }

            private Node? Primary()
            {
                char c = Peek();
                if (c == '(')
                {
                    i++;
                    Node? inner = Comparison();
                    if (inner == null || Peek() != ')') return null;
                    i++;
                    return inner;
                }
                if (char.IsAsciiDigit(c) || c == '.') return Number();
                if (c == '\'')
                {
                    int start = i;
                    string? quoted = Quoted();
                    if (quoted == null) return null;

                    string? file = null;
                    string page = quoted;
                    if (quoted.StartsWith('['))
                    {
                        int close = quoted.IndexOf(']');
                        if (close < 2 || close == quoted.Length - 1) return null;
                        file = quoted[1..close];
                        page = quoted[(close + 1)..];
                    }
                    prefixes?.Add((start, i, file, page));
                    if (Peek() != '!') return null;
                    i++;
                    return Cells(file, page);
                }
                if (c == '[')
                {
                    int start = i;
                    int close = s.IndexOf(']', i);
                    if (close < i + 2) return null;
                    string file = s[(i + 1)..close];
                    i = close + 1;
                    string page = Word();
                    if (page.Length == 0) return null;
                    prefixes?.Add((start, i, file, page));
                    if (Peek() != '!') return null;
                    i++;
                    return Cells(file, page);
                }
                if (char.IsAsciiLetter(c) || c == '_')
                {
                    int start = i;
                    string word = Word();
                    int end = i;
                    char next = Peek();
                    if (next == '(') return Function(word);
                    if (next == '!')
                    {
                        prefixes?.Add((start, end, null, word));
                        i++;
                        return Cells(null, word);
                    }
                    return Address(word, start, null, null);
                }
                return null;
            }

            private Node? Number()
            {
                int start = i;
                while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.')) i++;
                if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                {
                    int mark = i++;
                    if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                    if (i < s.Length && char.IsAsciiDigit(s[i]))
                        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
                    else
                        i = mark;
                }
                return double.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                    ? new Constant(SheetValue.Number(n))
                    : null;
            }

            private Node? Function(string function)
            {
                i++;
                List<Node> arguments = new List<Node>();
                if (Peek() == ')')
                {
                    i++;
                    return new Call(function, arguments);
                }
                while (true)
                {
                    Node? argument = Comparison();
                    if (argument == null) return null;
                    arguments.Add(argument);
                    char c = Peek();
                    i++;
                    if (c == ')') return new Call(function, arguments);
                    if (c != ',') return null;
                }
            }

            // After "Page!" or "[file]Page!": an address, or a range.
            private Node? Cells(string? file, string page)
            {
                SkipSpace();
                if (i >= s.Length || !char.IsAsciiLetter(s[i])) return null;
                int start = i;
                return Address(Word(), start, file, page);
            }

            // A1, or A1:B3 when a second address follows.
            private Node? Address(string first, int firstStart, string? file, string? page)
            {
                if (!TryCell(first, out int row, out int column)) return Bad(first);
                addresses?.Add((firstStart, firstStart + first.Length, file, page));
                if (Peek() != ':') return new Reference(file, page, row, column, row, column);

                i++;
                SkipSpace();
                if (i >= s.Length || !char.IsAsciiLetter(s[i])) return null;
                int secondStart = i;
                string second = Word();
                if (!TryCell(second, out int row2, out int column2)) return Bad(second);
                addresses?.Add((secondStart, secondStart + second.Length, file, page));
                return new Reference(file, page, row, column, row2, column2);
            }

            // A word that is not a cell: off the grid when shaped like one, otherwise an unknown name.
            private static Node Bad(string word)
            {
                int letters = 0;
                while (letters < word.Length && char.IsAsciiLetter(word[letters])) letters++;
                bool shaped = letters > 0 && letters < word.Length;
                for (int k = letters; shaped && k < word.Length; k++)
                    shaped = char.IsAsciiDigit(word[k]);
                return new Constant(SheetValue.Error(shaped ? reference : name));
            }

            private static bool TryCell(string word, out int row, out int column)
            {
                row = column = -1;
                int letters = 0;
                while (letters < word.Length && char.IsAsciiLetter(word[letters])) letters++;
                if (letters == 0 || letters > 3 || letters == word.Length) return false;
                return SheetDocument.TryParseAddress(word, out row, out column)
                    && row >= 0 && column >= 0 && row < maxRows && column < maxColumns;
            }

            private string Word()
            {
                int start = i;
                while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '.')) i++;
                return s.Substring(start, i - start);
            }

            // 'name', with '' for a quote inside it.
            private string? Quoted()
            {
                i++;
                StringBuilder text = new StringBuilder();
                while (i < s.Length)
                {
                    if (s[i] == '\'')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '\'')
                        {
                            text.Append('\'');
                            i += 2;
                            continue;
                        }
                        i++;
                        return text.ToString();
                    }
                    text.Append(s[i++]);
                }
                return null;
            }

            private char Peek()
            {
                SkipSpace();
                return i < s.Length ? s[i] : '\0';
            }

            private void SkipSpace()
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }
        }
        #endregion
    }
}
