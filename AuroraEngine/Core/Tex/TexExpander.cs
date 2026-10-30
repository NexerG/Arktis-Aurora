using System.Globalization;
using System.Text;

namespace ArctisAurora.Core.Tex
{
    // TeX's gullet plus the assignments of its main control: expands macros and expandable primitives,
    // performs definitions, groups and register assignments, and hands every other token to the caller.
    public sealed class TexExpander
    {
        private enum Op
        {
            Relax, Par,
            Def, Gdef, Edef, Xdef, Let, Futurelet, Global, Long,
            Chardef, Countdef, Dimendef, Skipdef, Toksdef,
            Count, Dimen, Skip, Toks, Advance, Multiply, Divide, Catcode,
            Begingroup, Endgroup,
            Expandafter, Noexpand, Csname, Endcsname, String, The,
            If, Ifx, Ifcat, Ifnum, Ifdim, Ifodd, Ifcase, Iftrue, Iffalse, Ifdefined, Ifcsname, Else, Or, Fi,
            NewCommand, RenewCommand, ProvideCommand, NewEnvironment, RenewEnvironment, Begin, End, EndEnvironment,
            MakeAtLetter, MakeAtOther, NewIf, NewCounter, SetCounter, AddToCounter, StepCounter, Value, IfNextChar, IfStar,
            NewLength, SetLength, AddToLength, Dimexpr, Pagetotal,
            Caller
        }

        private enum GroupKind { Simple, SemiSimple, Environment }

        private enum RegisterKind { Count, Dimen, Skip, Toks, Char }

        // limits
        private const int MaxSteps = 1_000_000;
        private const int MaxInputDepth = 10_000;
        private const int MaxDimen = 0x3FFFFFFF;
        private const int Unity = 65536;

        // em and ex of the fixed 10pt font
        private const int EmWidth = 655360;
        private const int ExHeight = 282168;

        private const string EndEnvironmentName = "\u0001endenv";

        #region ---- meanings ----
        private abstract class Meaning { }

        private sealed class Primitive : Meaning
        {
            public readonly Op op;
            public readonly bool expandable;

            // a caller primitive's name
            public readonly string? name;

            public Primitive(Op op, string? name = null)
            {
                this.op = op;
                this.name = name;
                expandable = op is >= Op.Expandafter and <= Op.Fi && op != Op.Endcsname || op is Op.Value or Op.IfNextChar or Op.IfStar;
            }
        }

        private sealed class Macro : Meaning
        {
            public TexToken[] parameters = Array.Empty<TexToken>();
            public TexToken[] body = Array.Empty<TexToken>();
            public TexToken[]? optional;
            public bool isLong;
        }

        private sealed class CharMeaning : Meaning
        {
            public readonly TexToken token;

            public CharMeaning(TexToken token) => this.token = token;
        }

        private sealed class Register : Meaning
        {
            public readonly RegisterKind kind;
            public readonly int index;

            public Register(RegisterKind kind, int index)
            {
                this.kind = kind;
                this.index = index;
            }
        }

        private static readonly Dictionary<string, Primitive> primitives = new (string name, Op op)[]
        {
            ("relax", Op.Relax), ("par", Op.Par),
            ("def", Op.Def), ("gdef", Op.Gdef), ("edef", Op.Edef), ("xdef", Op.Xdef), ("let", Op.Let), ("futurelet", Op.Futurelet),
            ("global", Op.Global), ("long", Op.Long),
            ("chardef", Op.Chardef), ("countdef", Op.Countdef), ("dimendef", Op.Dimendef), ("skipdef", Op.Skipdef), ("toksdef", Op.Toksdef),
            ("count", Op.Count), ("dimen", Op.Dimen), ("skip", Op.Skip), ("toks", Op.Toks),
            ("advance", Op.Advance), ("multiply", Op.Multiply), ("divide", Op.Divide), ("catcode", Op.Catcode),
            ("begingroup", Op.Begingroup), ("endgroup", Op.Endgroup),
            ("expandafter", Op.Expandafter), ("noexpand", Op.Noexpand), ("csname", Op.Csname), ("endcsname", Op.Endcsname),
            ("string", Op.String), ("the", Op.The),
            ("if", Op.If), ("ifx", Op.Ifx), ("ifcat", Op.Ifcat), ("ifnum", Op.Ifnum), ("ifdim", Op.Ifdim), ("ifodd", Op.Ifodd),
            ("ifcase", Op.Ifcase), ("iftrue", Op.Iftrue), ("iffalse", Op.Iffalse), ("ifdefined", Op.Ifdefined), ("ifcsname", Op.Ifcsname),
            ("else", Op.Else), ("or", Op.Or), ("fi", Op.Fi),
            ("newcommand", Op.NewCommand), ("renewcommand", Op.RenewCommand), ("providecommand", Op.ProvideCommand),
            ("newenvironment", Op.NewEnvironment), ("renewenvironment", Op.RenewEnvironment),
            ("begin", Op.Begin), ("end", Op.End), (EndEnvironmentName, Op.EndEnvironment),
            ("makeatletter", Op.MakeAtLetter), ("makeatother", Op.MakeAtOther), ("newif", Op.NewIf),
            ("newcounter", Op.NewCounter), ("setcounter", Op.SetCounter), ("addtocounter", Op.AddToCounter), ("stepcounter", Op.StepCounter), ("value", Op.Value),
            ("@ifnextchar", Op.IfNextChar), ("@ifstar", Op.IfStar),
            ("newlength", Op.NewLength), ("setlength", Op.SetLength), ("addtolength", Op.AddToLength),
            ("dimexpr", Op.Dimexpr), ("pagetotal", Op.Pagetotal)
        }.ToDictionary(p => p.name, p => new Primitive(p.op));
        #endregion

        #region ---- scoped tables ----
        private sealed class Group
        {
            public readonly GroupKind kind;
            public readonly string? environment;
            public readonly int line;
            public readonly List<Action> saves = new List<Action>();

            public Group(GroupKind kind, string? environment, int line)
            {
                this.kind = kind;
                this.environment = environment;
                this.line = line;
            }
        }

        // A value per key with TeX's save stack: a local change is undone at its group's end, a global one survives it.
        private sealed class Table<TKey, TValue> where TKey : notnull
        {
            private readonly Dictionary<TKey, (TValue value, int level)> entries = new Dictionary<TKey, (TValue, int)>();
            private readonly Func<TKey, TValue> initial;

            public Table(Func<TKey, TValue> initial) => this.initial = initial;

            public TValue Get(TKey key) => entries.TryGetValue(key, out (TValue value, int level) entry) ? entry.value : initial(key);

            public void Set(TKey key, TValue value, bool global, List<Group> groups)
            {
                int level = global ? 1 : groups.Count + 1;
                bool had = entries.TryGetValue(key, out (TValue value, int level) old);
                if (level > 1 && (!had || old.level != level))
                {
                    groups[^1].saves.Add(() =>
                    {
                        if (entries.TryGetValue(key, out (TValue value, int level) now) && now.level == 1) return;
                        if (had) entries[key] = old;
                        else entries.Remove(key);
                    });
                }
                entries[key] = (value, level);
            }
        }

        #endregion

        #region ---- input ----
        private sealed class Level
        {
            public readonly TexToken[] tokens;
            public readonly bool noexpand;
            public int index;

            public Level(TexToken[] tokens, bool noexpand)
            {
                this.tokens = tokens;
                this.noexpand = noexpand;
            }
        }

        private readonly struct Cond
        {
            public readonly Op limit;
            public readonly int line;

            public Cond(Op limit, int line)
            {
                this.limit = limit;
                this.line = line;
            }
        }
        #endregion

        public readonly List<TexError> errors = new List<TexError>();

        private TexReader reader;
        private readonly List<Level> input = new List<Level>();

        // files read over the source: the reader each suspended, what runs at its end, where it was input, the input levels under it
        private readonly Stack<(TexReader outer, Action? atEnd, int line, int column, int floor)> files = new Stack<(TexReader, Action?, int, int, int)>();
        private readonly List<Group> groups = new List<Group>();
        private readonly List<Cond> conds = new List<Cond>();

        // the equivalents
        private readonly Table<string, Meaning?> meanings = new Table<string, Meaning?>(name => primitives.GetValueOrDefault(name));
        private readonly Table<char, TexCatcode> catcodes = new Table<char, TexCatcode>(InitialCatcode);
        private readonly Table<int, int> counts = new Table<int, int>(_ => 0);
        private readonly Table<int, int> dimens = new Table<int, int>(_ => 0);
        private readonly Table<int, TexGlue> skips = new Table<int, TexGlue>(_ => default);
        private readonly Table<int, TexToken[]> toks = new Table<int, TexToken[]>(_ => Array.Empty<TexToken>());

        // counter name -> the counters its \stepcounter zeroes
        private readonly Dictionary<string, List<string>> resets = new Dictionary<string, List<string>>();

        // the current font's em and ex in sp; null is the fixed 10pt font
        public Func<int>? quad, xHeight;

        // hands undefined control sequences to the caller instead of reporting them
        public bool passUndefined;

        // names that exist only in math; one met undefined in text is set alone as math, as LuaLaTeX does
        public Func<string, bool>? mathOnly;

        // @'s catcode in the current group, which a package read saves and puts back
        public TexCatcode atCatcode
        {
            get => catcodes.Get('@');
            set => catcodes.Set('@', value, false, groups);
        }

        // last position read from the source
        private int line, column;

        private bool noexpandRead;
        private int steps;
        private bool halted;
        private bool finished;
        private int nextCounter = 256;
        private int nextLength = 256;

        // set when a scanned dimension read \pagetotal, which is always 0 here
        public bool pagetotalRead;

        public TexExpander(string source, string prelude = "")
        {
            reader = new TexReader(source, prelude, c => catcodes.Get(c), (message, l, c) => errors.Add(new TexError(l, c, message)));
        }

        // The next token for the typesetter; false at the end of the source.
        public bool Next(out TexToken token)
        {
            while (GetX(out token))
            {
                if (!token.IsCs && token.cat != TexCatcode.Active)
                {
                    if (Character(token)) return true;
                    continue;
                }

                Meaning? meaning = MeaningOf(token);
                if (noexpandRead) continue;
                switch (meaning)
                {
                    case null:
                        if (passUndefined) return true;
                        if (mathOnly != null && token.IsCs && mathOnly(token.name!))
                        {
                            Error("Missing $ inserted");
                            TexToken shift = TexToken.Char('$', TexCatcode.MathShift, token.line, token.column);
                            Push(new[] { token, shift });
                            token = shift;
                            return true;
                        }
                        Error($"Undefined control sequence {token}");
                        continue;
                    case CharMeaning c:
                        TexToken implicitChar = TexToken.Char(c.token.ch, c.token.cat, token.line, token.column);
                        if (!Character(implicitChar)) continue;
                        token = implicitChar;
                        return true;
                    case Register { kind: RegisterKind.Char } r:
                        token = TexToken.Char((char)r.index, TexCatcode.Other, token.line, token.column);
                        return true;
                    case Primitive { op: Op.Relax or Op.Par } p:
                        token = TexToken.Cs(p.op == Op.Par ? "par" : "relax", token.line, token.column);
                        return true;
                    case Primitive { op: Op.Caller } caller:
                        token = TexToken.Cs(caller.name!, token.line, token.column);
                        return true;
                    default:
                        Command(token, meaning);
                        continue;
                }
            }

            Finish();
            return false;
        }

        private static TexCatcode InitialCatcode(char c) => c switch
        {
            '\\' => TexCatcode.Escape,
            '{' => TexCatcode.BeginGroup,
            '}' => TexCatcode.EndGroup,
            '$' => TexCatcode.MathShift,
            '&' => TexCatcode.AlignTab,
            '\r' => TexCatcode.EndLine,
            '#' => TexCatcode.Parameter,
            '^' => TexCatcode.Superscript,
            '_' => TexCatcode.Subscript,
            '\0' => TexCatcode.Ignored,
            ' ' or '\t' => TexCatcode.Space,
            '~' => TexCatcode.Active,
            '%' => TexCatcode.Comment,
            '\x7f' => TexCatcode.Invalid,
            _ => char.IsLetter(c) ? TexCatcode.Letter : TexCatcode.Other
        };

        #region ---- reading ----
        private bool Get(out TexToken t)
        {
            noexpandRead = false;
            while (true)
            {
                int floor = files.Count > 0 ? files.Peek().floor : 0;
                while (input.Count > floor)
                {
                    Level top = input[^1];
                    if (top.index < top.tokens.Length)
                    {
                        t = top.tokens[top.index++];
                        noexpandRead = top.noexpand;
                        return true;
                    }
                    input.RemoveAt(input.Count - 1);
                }

                if (halted)
                {
                    t = default;
                    return false;
                }
                if (reader.Next(out t)) break;
                if (files.Count == 0) return false;
                (TexReader outer, Action? atEnd, _, _, _) = files.Pop();
                reader = outer;
                atEnd?.Invoke();
            }

            if (files.Count > 0)
            {
                (_, _, int at, int col, _) = files.Peek();
                t = t.IsCs ? TexToken.Cs(t.name!, at, col) : TexToken.Char(t.ch, t.cat, at, col);
            }
            line = t.line;
            column = t.column;
            return true;
        }

        private bool GetX(out TexToken t)
        {
            while (Get(out t))
            {
                if (noexpandRead || !Expandable(t, out Meaning? meaning)) return true;
                Expand(t, meaning!);
            }
            return false;
        }

        private bool GetNonBlankX(out TexToken t)
        {
            while (GetX(out t))
                if (!IsSpace(t)) return true;
            return false;
        }

        private bool GetNonBlank(out TexToken t)
        {
            while (Get(out t))
                if (!IsSpace(t)) return true;
            return false;
        }

        private void Back(TexToken t) => Push(new[] { t }, false);

        private void Push(TexToken[] tokens, bool noexpand = false)
        {
            if (tokens.Length == 0) return;
            while (input.Count > 0 && input[^1].index >= input[^1].tokens.Length) input.RemoveAt(input.Count - 1);
            if (input.Count >= MaxInputDepth)
            {
                Halt("TeX capacity exceeded, sorry [input stack size]");
                return;
            }
            input.Add(new Level(tokens, noexpand));
        }

        private static bool IsSpace(TexToken t) => !t.IsCs && t.cat == TexCatcode.Space;

        private static bool IsActiveOrCs(TexToken t) => t.IsCs || t.cat == TexCatcode.Active;

        private static string Key(TexToken t) => t.name ?? "\0" + t.ch;

        private Meaning? MeaningOf(TexToken t) => IsActiveOrCs(t) ? meanings.Get(Key(t)) : null;

        private bool Expandable(TexToken t, out Meaning? meaning)
        {
            meaning = MeaningOf(t);
            return meaning is Macro or Primitive { expandable: true };
        }

        public void Error(string message)
        {
            if (!halted) errors.Add(new TexError(line, column, message));
        }

        private void Halt(string message)
        {
            Error(message);
            halted = true;
            input.Clear();
        }

        private void Finish()
        {
            if (finished || halted) return;
            finished = true;

            for (int i = conds.Count - 1; i >= 0; i--)
                Error($"\\end occurred when the \\if on line {conds[i].line} was incomplete");
            for (int i = groups.Count - 1; i >= 0; i--)
                Error(groups[i].kind == GroupKind.Environment
                    ? $"LaTeX Error: \\begin{{{groups[i].environment}}} on input line {groups[i].line} not ended"
                    : $"\\end occurred inside a group opened on line {groups[i].line}");
        }
        #endregion

        #region ---- main control ----
        // Opens or closes a group for a brace; false when the token is dropped.
        private bool Character(TexToken t)
        {
            switch (t.cat)
            {
                case TexCatcode.BeginGroup:
                    groups.Add(new Group(GroupKind.Simple, null, line));
                    return true;
                case TexCatcode.EndGroup:
                    if (groups.Count > 0 && groups[^1].kind == GroupKind.Simple)
                    {
                        CloseGroup();
                        return true;
                    }
                    Error(groups.Count == 0 ? "Too many }'s" : "Extra }, or forgotten \\endgroup");
                    return false;
                case TexCatcode.Parameter:
                    Error("You can't use macro parameter character # here");
                    return false;
                default:
                    return true;
            }
        }

        private void CloseGroup()
        {
            Group group = groups[^1];
            groups.RemoveAt(groups.Count - 1);
            for (int i = group.saves.Count - 1; i >= 0; i--)
                group.saves[i]();
        }

        private static bool Prefixable(Meaning? m) =>
            m is Register { kind: not RegisterKind.Char }
            || m is Primitive { op: >= Op.Def and <= Op.Catcode };

        private void Command(TexToken t, Meaning meaning)
        {
            bool global = false, isLong = false;
            while (meaning is Primitive { op: Op.Global or Op.Long } prefix)
            {
                if (prefix.op == Op.Global) global = true;
                else isLong = true;

                do
                {
                    if (!GetX(out t)) return;
                } while (IsSpace(t) || MeaningOf(t) is Primitive { op: Op.Relax });

                Meaning? next = MeaningOf(t);
                if (!Prefixable(next))
                {
                    Error($"You can't use a prefix with {t}");
                    Back(t);
                    return;
                }
                meaning = next!;
            }

            if (isLong && meaning is not Primitive { op: Op.Def or Op.Gdef or Op.Edef or Op.Xdef })
                Error($"You can't use \\long with {t}");

            if (meaning is Register register)
            {
                AssignRegister(register.kind, register.index, global);
                return;
            }

            Primitive p = (Primitive)meaning;
            switch (p.op)
            {
                case Op.Def or Op.Gdef or Op.Edef or Op.Xdef:
                    Define(p.op, global || p.op is Op.Gdef or Op.Xdef, isLong);
                    break;
                case Op.Let:
                    Let(global);
                    break;
                case Op.Futurelet:
                    FutureLet(global);
                    break;
                case Op.Chardef or Op.Countdef or Op.Dimendef or Op.Skipdef or Op.Toksdef:
                    Shorthand(p.op, global);
                    break;
                case Op.Count or Op.Dimen or Op.Skip or Op.Toks:
                    AssignRegister(KindOf(p.op), ScanRegisterNumber(), global);
                    break;
                case Op.Advance or Op.Multiply or Op.Divide:
                    Arithmetic(p.op, global);
                    break;
                case Op.Catcode:
                    char c = ScanCharCode();
                    ScanOptionalEquals();
                    int code = ScanInt();
                    if (code is < 0 or > 15) Error($"Invalid code ({code}), should be between 0 and 15");
                    else catcodes.Set(c, (TexCatcode)code, global, groups);
                    break;
                case Op.Begingroup:
                    groups.Add(new Group(GroupKind.SemiSimple, null, line));
                    break;
                case Op.Endgroup:
                    if (groups.Count > 0 && groups[^1].kind != GroupKind.Simple) CloseGroup();
                    else Error("Extra \\endgroup");
                    break;
                case Op.Endcsname:
                    Error("Extra \\endcsname");
                    break;
                case Op.NewCommand or Op.RenewCommand or Op.ProvideCommand:
                    NewCommand(p.op, t);
                    break;
                case Op.NewEnvironment or Op.RenewEnvironment:
                    NewEnvironment(p.op, t);
                    break;
                case Op.Begin:
                    Begin(t);
                    break;
                case Op.End:
                    End(t);
                    break;
                case Op.EndEnvironment:
                    if (groups.Count > 0 && groups[^1].kind == GroupKind.Environment) CloseGroup();
                    else Error("Extra \\endgroup");
                    break;
                case Op.MakeAtLetter or Op.MakeAtOther:
                    catcodes.Set('@', p.op == Op.MakeAtLetter ? TexCatcode.Letter : TexCatcode.Other, false, groups);
                    break;
                case Op.NewIf:
                    NewIf();
                    break;
                case Op.NewCounter:
                    NewCounter(t);
                    break;
                case Op.SetCounter or Op.AddToCounter:
                    CounterAssign(p.op, t);
                    break;
                case Op.StepCounter:
                    if (NameArgument(t) is string stepped) StepCounter(stepped);
                    break;
                case Op.NewLength:
                    NewLength(t);
                    break;
                case Op.SetLength or Op.AddToLength:
                    TexToken[]? target = Argument(t, true);
                    TexToken[]? amount = target == null ? null : Argument(t, true);
                    if (amount == null) break;
                    IEnumerable<TexToken> assignment = target!.Concat(amount).Append(TexToken.Cs("relax"));
                    Push((p.op == Op.AddToLength ? assignment.Prepend(TexToken.Cs("advance")) : assignment).ToArray());
                    break;
                case Op.Dimexpr or Op.Pagetotal:
                    Error($"You can't use {t} in this mode");
                    break;
            }
        }

        private static RegisterKind KindOf(Op op) => op switch
        {
            Op.Count or Op.Countdef => RegisterKind.Count,
            Op.Dimen or Op.Dimendef => RegisterKind.Dimen,
            Op.Skip or Op.Skipdef => RegisterKind.Skip,
            Op.Toks or Op.Toksdef => RegisterKind.Toks,
            _ => RegisterKind.Char
        };
        #endregion

        #region ---- definitions ----
        private bool GetDefinable(out TexToken name)
        {
            bool got = Get(out name);
            if (got && IsActiveOrCs(name)) return true;
            Error("Missing control sequence inserted");
            if (got) Back(name);
            return false;
        }

        private void Define(Op op, bool global, bool isLong)
        {
            if (!GetDefinable(out TexToken name)) return;

            List<TexToken> parameters = new List<TexToken>();
            int arity = 0;
            while (true)
            {
                if (!Get(out TexToken t))
                {
                    Error($"File ended while scanning definition of {name}");
                    return;
                }
                if (t.IsChar(t.ch, TexCatcode.BeginGroup)) break;
                if (t.IsChar(t.ch, TexCatcode.Parameter))
                {
                    if (!Get(out TexToken digit))
                    {
                        Error($"File ended while scanning definition of {name}");
                        return;
                    }
                    if (digit.IsChar((char)('1' + arity), TexCatcode.Other) && arity < 9)
                    {
                        parameters.Add(TexToken.Param(++arity));
                        continue;
                    }
                    Error("Parameters must be numbered consecutively");
                    Back(digit);
                    continue;
                }
                parameters.Add(t);
            }

            List<TexToken>? body = op is Op.Edef or Op.Xdef ? ExpandedBody(name) : Balanced(name, true, true);
            if (body == null) return;

            Macro macro = new Macro
            {
                parameters = parameters.ToArray(),
                body = Slots(body, arity, name),
                isLong = isLong
            };
            meanings.Set(Key(name), macro, global, groups);
        }

        // A body's #n become parameter slots and ## a single #.
        private TexToken[] Slots(List<TexToken> body, int arity, TexToken owner)
        {
            List<TexToken> result = new List<TexToken>(body.Count);
            for (int i = 0; i < body.Count; i++)
            {
                TexToken t = body[i];
                if (!t.IsChar(t.ch, TexCatcode.Parameter) || i + 1 >= body.Count)
                {
                    result.Add(t);
                    continue;
                }

                TexToken next = body[++i];
                if (next.IsChar(next.ch, TexCatcode.Parameter)) result.Add(next);
                else if (next is { name: null, cat: TexCatcode.Other, ch: >= '1' and <= '9' } && next.ch - '0' <= arity) result.Add(TexToken.Param(next.ch - '0'));
                else
                {
                    Error($"Illegal parameter number in definition of {owner}");
                    result.Add(next);
                }
            }
            return result.ToArray();
        }

        // Tokens up to the brace that closes an already opened group; null when the source ends first.
        private List<TexToken>? Balanced(TexToken owner, bool isLong, bool definition)
        {
            List<TexToken> tokens = new List<TexToken>();
            int depth = 0;
            while (true)
            {
                if (!Get(out TexToken t))
                {
                    Error(definition ? $"File ended while scanning definition of {owner}" : $"File ended while scanning use of {owner}");
                    return null;
                }
                if (!isLong && t.IsCs && t.name == "par")
                {
                    Error($"Paragraph ended before {owner} was complete");
                    Back(t);
                    return null;
                }
                if (t.IsChar(t.ch, TexCatcode.BeginGroup)) depth++;
                else if (t.IsChar(t.ch, TexCatcode.EndGroup) && depth-- == 0) return tokens;
                tokens.Add(t);
            }
        }

        private List<TexToken>? ExpandedBody(TexToken owner)
        {
            List<TexToken> tokens = new List<TexToken>();
            int depth = 0;
            while (true)
            {
                if (!Get(out TexToken t))
                {
                    Error($"File ended while scanning definition of {owner}");
                    return null;
                }
                if (!noexpandRead && Expandable(t, out Meaning? meaning))
                {
                    if (meaning is Primitive { op: Op.The }) tokens.AddRange(TheTokens());
                    else if (meaning is Primitive { op: Op.IfNextChar or Op.IfStar }) tokens.Add(t);
                    else Expand(t, meaning!);
                    if (halted) return null;
                    continue;
                }
                if (t.IsChar(t.ch, TexCatcode.BeginGroup)) depth++;
                else if (t.IsChar(t.ch, TexCatcode.EndGroup) && depth-- == 0) return tokens;
                tokens.Add(t);
            }
        }

        private void Let(bool global)
        {
            if (!GetDefinable(out TexToken name)) return;
            if (!Get(out TexToken target)) return;
            if (target.IsChar('=', TexCatcode.Other))
            {
                if (!Get(out target)) return;
                if (IsSpace(target) && !Get(out target)) return;
            }
            meanings.Set(Key(name), IsActiveOrCs(target) ? MeaningOf(target) : new CharMeaning(target), global, groups);
        }

        private void FutureLet(bool global)
        {
            if (!GetDefinable(out TexToken name)) return;
            if (!Get(out TexToken first) || !Get(out TexToken second)) return;
            meanings.Set(Key(name), IsActiveOrCs(second) ? MeaningOf(second) : new CharMeaning(second), global, groups);
            Back(second);
            Back(first);
        }

        private void Shorthand(Op op, bool global)
        {
            if (!GetDefinable(out TexToken name)) return;
            meanings.Set(Key(name), primitives["relax"], global, groups);
            ScanOptionalEquals();
            int index = op == Op.Chardef ? ScanCharCode() : ScanRegisterNumber();
            meanings.Set(Key(name), new Register(KindOf(op), index), global, groups);
        }
        #endregion

        #region ---- registers and arithmetic ----
        private void AssignRegister(RegisterKind kind, int index, bool global)
        {
            ScanOptionalEquals();
            switch (kind)
            {
                case RegisterKind.Count: counts.Set(index, ScanInt(), global, groups); break;
                case RegisterKind.Dimen: dimens.Set(index, ScanDimen(), global, groups); break;
                case RegisterKind.Skip: skips.Set(index, ScanGlue(), global, groups); break;
                case RegisterKind.Toks:
                    TexToken[]? value = ScanToksValue();
                    if (value != null) toks.Set(index, value, global, groups);
                    break;
            }
        }

        private TexToken[]? ScanToksValue()
        {
            TexToken t;
            do
            {
                if (!GetX(out t))
                {
                    Error("Missing { inserted");
                    return null;
                }
            } while (IsSpace(t) || MeaningOf(t) is Primitive { op: Op.Relax });

            if (MeaningOf(t) is Register { kind: RegisterKind.Toks } or Primitive { op: Op.Toks } && RegisterOf(t, out _, out int index))
                return toks.Get(index);
            if (!t.IsChar(t.ch, TexCatcode.BeginGroup) && !(MeaningOf(t) is CharMeaning c && c.token.cat == TexCatcode.BeginGroup))
            {
                Error("Missing { inserted");
                Back(t);
                return null;
            }
            return Balanced(t, true, true)?.ToArray();
        }

        // The register a token names: \count5, a \countdef name, and so on. Reads the number after a bare \count.
        private bool RegisterOf(TexToken t, out RegisterKind kind, out int index)
        {
            switch (MeaningOf(t))
            {
                case Register r when r.kind != RegisterKind.Char:
                    kind = r.kind;
                    index = r.index;
                    return true;
                case Primitive { op: Op.Count or Op.Dimen or Op.Skip or Op.Toks } p:
                    kind = KindOf(p.op);
                    index = ScanRegisterNumber();
                    return true;
                default:
                    kind = RegisterKind.Char;
                    index = 0;
                    return false;
            }
        }

        private void Arithmetic(Op op, bool global)
        {
            if (!GetNonBlankX(out TexToken t)) return;
            if (!RegisterOf(t, out RegisterKind kind, out int index) || kind == RegisterKind.Toks)
            {
                Error($"You can't use {t} after \\{op.ToString().ToLowerInvariant()}");
                Back(t);
                return;
            }
            ScanKeyword("by");

            switch (kind)
            {
                case RegisterKind.Count:
                    {
                        long now = counts.Get(index);
                        long result = op == Op.Advance ? now + ScanInt() : Combine(now, op);
                        if (result is > int.MaxValue or < -int.MaxValue) Error("Arithmetic overflow");
                        else counts.Set(index, (int)result, global, groups);
                        break;
                    }
                case RegisterKind.Dimen:
                    {
                        long now = dimens.Get(index);
                        long result = op == Op.Advance ? now + ScanDimen() : Combine(now, op);
                        if (result is > MaxDimen or < -MaxDimen) Error("Arithmetic overflow");
                        else dimens.Set(index, (int)result, global, groups);
                        break;
                    }
                case RegisterKind.Skip:
                    {
                        TexGlue now = skips.Get(index);
                        if (op == Op.Advance)
                        {
                            TexGlue add = ScanGlue();
                            now.width += add.width;
                            (now.stretch, now.stretchOrder) = AddOrdered(now.stretch, now.stretchOrder, add.stretch, add.stretchOrder);
                            (now.shrink, now.shrinkOrder) = AddOrdered(now.shrink, now.shrinkOrder, add.shrink, add.shrinkOrder);
                        }
                        else
                        {
                            int n = ScanInt();
                            if (op == Op.Divide && n == 0)
                            {
                                Error("Arithmetic overflow");
                                break;
                            }
                            now.width = op == Op.Multiply ? now.width * n : now.width / n;
                            now.stretch = op == Op.Multiply ? now.stretch * n : now.stretch / n;
                            now.shrink = op == Op.Multiply ? now.shrink * n : now.shrink / n;
                        }
                        skips.Set(index, now, global, groups);
                        break;
                    }
            }
        }

        // \multiply or \divide by the integer that follows.
        private long Combine(long now, Op op)
        {
            int n = ScanInt();
            if (op == Op.Multiply) return now * n;
            if (n != 0) return now / n;
            return long.MaxValue;
        }

        private static (int, byte) AddOrdered(int a, byte aOrder, int b, byte bOrder)
        {
            if (b == 0) return (a, aOrder);
            if (a == 0 || aOrder < bOrder) return (b, bOrder);
            if (aOrder == bOrder) return (a + b, aOrder);
            return (a, aOrder);
        }
        #endregion

        #region ---- scanning numbers ----
        private void ScanOptionalEquals()
        {
            if (GetNonBlankX(out TexToken t) && !t.IsChar('=', TexCatcode.Other)) Back(t);
        }

        private void SkipOptionalSpace()
        {
            if (GetX(out TexToken t) && !IsSpace(t)) Back(t);
        }

        // Matches a keyword, letters in either case, after optional spaces; puts back what it read when it fails.
        public bool ScanKeyword(string keyword)
        {
            List<TexToken> matched = new List<TexToken>();
            while (matched.Count < keyword.Length)
            {
                if (!GetX(out TexToken t))
                {
                    Push(matched.ToArray());
                    return false;
                }
                if (!IsActiveOrCs(t) && t.param == 0 && char.ToLowerInvariant(t.ch) == keyword[matched.Count]) matched.Add(t);
                else if (IsSpace(t) && matched.Count == 0) continue;
                else
                {
                    Back(t);
                    Push(matched.ToArray());
                    return false;
                }
            }
            return true;
        }

        // Optional signs and spaces, then the first token after them; false when the source ends first.
        private bool ScanSigns(out TexToken t, out bool negative)
        {
            negative = false;
            while (GetNonBlankX(out t))
            {
                if (t.IsChar('-', TexCatcode.Other)) negative = !negative;
                else if (!t.IsChar('+', TexCatcode.Other)) return true;
            }
            Error("Missing number, treated as zero");
            return false;
        }

        // An internal number: count, dimen or skip register, a \chardef or \catcode. Dimensions in sp, glue as its width.
        private bool Internal(TexToken t, out RegisterKind kind, out int value, out TexGlue glue)
        {
            glue = default;
            value = 0;
            kind = RegisterKind.Count;
            Meaning? meaning = MeaningOf(t);
            if (meaning is Register { kind: RegisterKind.Char } chardef)
            {
                value = chardef.index;
                return true;
            }
            if (meaning is Primitive { op: Op.Catcode })
            {
                value = (int)catcodes.Get(ScanCharCode());
                return true;
            }
            if (meaning is Primitive { op: Op.Dimexpr or Op.Pagetotal } dimension)
            {
                kind = RegisterKind.Dimen;
                pagetotalRead |= dimension.op == Op.Pagetotal;
                value = dimension.op == Op.Dimexpr ? DimExpr() : 0;
                return true;
            }
            if (!RegisterOf(t, out kind, out int index) || kind == RegisterKind.Toks) return false;

            switch (kind)
            {
                case RegisterKind.Count: value = counts.Get(index); break;
                case RegisterKind.Dimen: value = dimens.Get(index); break;
                case RegisterKind.Skip:
                    glue = skips.Get(index);
                    value = glue.width;
                    break;
            }
            return true;
        }

        // An e-TeX \dimexpr in sp: + - between terms, * / by integers, parentheses; an ending \relax is taken.
        private int DimExpr()
        {
            long value = DimSum();
            if (GetNonBlankX(out TexToken t) && MeaningOf(t) is not Primitive { op: Op.Relax }) Back(t);
            if (value is > MaxDimen or < -MaxDimen)
            {
                Error("Arithmetic overflow");
                return 0;
            }
            return (int)value;
        }

        private long DimSum()
        {
            long sum = DimTerm();
            while (GetNonBlankX(out TexToken t))
            {
                if (t.IsChar('+', TexCatcode.Other)) sum += DimTerm();
                else if (t.IsChar('-', TexCatcode.Other)) sum -= DimTerm();
                else
                {
                    Back(t);
                    break;
                }
            }
            return sum;
        }

        private long DimTerm()
        {
            long value;
            if (GetNonBlankX(out TexToken t) && t.IsChar('(', TexCatcode.Other))
            {
                value = DimSum();
                bool read = GetNonBlankX(out TexToken close);
                if (!read || !close.IsChar(')', TexCatcode.Other))
                {
                    Error("Missing ) inserted for expression");
                    if (read) Back(close);
                }
            }
            else
            {
                Back(t);
                value = ScanDimen();
            }

            while (GetNonBlankX(out t))
            {
                if (t.IsChar('*', TexCatcode.Other)) value *= ScanInt();
                else if (t.IsChar('/', TexCatcode.Other))
                {
                    int d = ScanInt();
                    if (d == 0) Error("Arithmetic overflow");
                    else value = (long)Math.Round(value / (double)d, MidpointRounding.AwayFromZero);
                }
                else
                {
                    Back(t);
                    break;
                }
            }
            return value;
        }

        private int ScanRegisterNumber()
        {
            int n = ScanInt();
            if (n is >= 0 and <= 32767) return n;
            Error($"Bad register code ({n})");
            return 0;
        }

        private char ScanCharCode()
        {
            int n = ScanInt();
            if (n is >= 0 and <= 0xFFFF) return (char)n;
            Error($"Bad character code ({n})");
            return '\0';
        }

        public int ScanInt()
        {
            if (!ScanSigns(out TexToken t, out bool negative)) return 0;

            long value = 0;
            if (Internal(t, out _, out int internalValue, out _)) value = internalValue;
            else if (t.IsChar('`', TexCatcode.Other))
            {
                if (!Get(out TexToken c)) Error("Missing number, treated as zero");
                else if (c.IsCs && c.name!.Length != 1)
                {
                    Error("Improper alphabetic constant");
                    value = '0';
                }
                else value = c.IsCs ? c.name![0] : c.ch;
                SkipOptionalSpace();
            }
            else
            {
                int radix = t.IsChar('\'', TexCatcode.Other) ? 8 : t.IsChar('"', TexCatcode.Other) ? 16 : 10;
                bool more = radix == 10 || GetX(out t);
                bool any = false, tooBig = false;
                while (more)
                {
                    int d = Digit(t, radix);
                    if (d < 0) break;
                    any = true;
                    if (!tooBig)
                    {
                        value = value * radix + d;
                        if (value > int.MaxValue)
                        {
                            tooBig = true;
                            value = int.MaxValue;
                        }
                    }
                    more = GetX(out t);
                }

                if (!any)
                {
                    Error("Missing number, treated as zero");
                    if (more) Back(t);
                }
                else
                {
                    if (tooBig) Error("Number too big");
                    if (more && !IsSpace(t)) Back(t);
                }
            }
            return (int)(negative ? -value : value);
        }

        private static int Digit(TexToken t, int radix)
        {
            if (t.IsCs || t.param != 0) return -1;
            if (t.cat == TexCatcode.Other && t.ch >= '0' && t.ch <= '9' && t.ch - '0' < radix) return t.ch - '0';
            if (radix == 16 && t.cat is TexCatcode.Other or TexCatcode.Letter && t.ch is >= 'A' and <= 'F') return t.ch - 'A' + 10;
            return -1;
        }

        public int ScanDimen() => ScanDimen(false, out _);

        // A dimension in sp, by tex.web's arithmetic; with fil allowed, a stretch order 1-3 instead of a unit.
        private int ScanDimen(bool fil, out byte order)
        {
            order = 0;
            if (!ScanSigns(out TexToken t, out bool negative)) return 0;
            return DimenAfter(t, negative, fil, out order);
        }

        // The rest of a dimension, from its first token after the signs.
        private int DimenAfter(TexToken t, bool negative, bool fil, out byte order)
        {
            order = 0;
            long whole = 0;
            int fraction = 0;

            if (Internal(t, out RegisterKind kind, out int internalValue, out _))
            {
                if (kind is RegisterKind.Dimen or RegisterKind.Skip) return Signed(internalValue, negative, false);
                whole = internalValue;
            }
            else if (Digit(t, 10) >= 0 || t.IsChar('.', TexCatcode.Other) || t.IsChar(',', TexCatcode.Other))
            {
                bool more = true;
                while (more && Digit(t, 10) is int d and >= 0)
                {
                    whole = Math.Min(whole * 10 + d, int.MaxValue);
                    more = GetX(out t);
                }
                if (more && (t.IsChar('.', TexCatcode.Other) || t.IsChar(',', TexCatcode.Other)))
                {
                    List<int> digits = new List<int>();
                    while ((more = GetX(out t)) && Digit(t, 10) is int d and >= 0)
                        if (digits.Count < 17) digits.Add(d);
                    fraction = RoundDecimals(digits);
                }
                if (more) Back(t);
            }
            else
            {
                Back(t);
                whole = ScanInt();
            }
            return Units(whole, fraction, negative, fil, out order);
        }

        // whole + fraction/2^16 times the unit that follows.
        private int Units(long whole, int fraction, bool negative, bool fil, out byte order)
        {
            order = 0;
            bool arithError = false;
            long value;
            if (whole < 0)
            {
                negative = !negative;
                whole = -whole;
            }

            // an internal quantity as the unit
            if (GetNonBlankX(out TexToken u))
            {
                if (Internal(u, out _, out int unit, out _))
                {
                    value = NxPlusY(whole, unit, XnOverD(unit, fraction, Unity, out _), ref arithError);
                    return Signed(value, negative, arithError);
                }
                Back(u);
            }

            if (fil && ScanKeyword("fil"))
            {
                order = 1;
                while (order < 3 && ScanKeyword("l")) order++;
                value = Attach(whole, fraction, ref arithError);
                SkipOptionalSpace();
                return Signed(value, negative, arithError);
            }

            int font = ScanKeyword("em") ? quad?.Invoke() ?? EmWidth : ScanKeyword("ex") ? xHeight?.Invoke() ?? ExHeight : 0;
            if (font != 0)
            {
                value = NxPlusY(whole, font, XnOverD(font, fraction, Unity, out _), ref arithError);
                SkipOptionalSpace();
                return Signed(value, negative, arithError);
            }

            ScanKeyword("true");
            if (ScanKeyword("sp"))
            {
                SkipOptionalSpace();
                return Signed(whole, negative, false);
            }

            if (!ScanKeyword("pt"))
            {
                (int num, int den) = ScanPhysicalUnit();
                if (num == 0) Error("Illegal unit of measure (pt inserted)");
                else
                {
                    whole = XnOverD(whole, num, den, out long remainder);
                    long f = (num * (long)fraction + Unity * remainder) / den;
                    whole += f / Unity;
                    fraction = (int)(f % Unity);
                }
            }
            value = Attach(whole, fraction, ref arithError);
            SkipOptionalSpace();
            return Signed(value, negative, arithError);
        }

        private (int, int) ScanPhysicalUnit()
        {
            if (ScanKeyword("in")) return (7227, 100);
            if (ScanKeyword("pc")) return (12, 1);
            if (ScanKeyword("cm")) return (7227, 254);
            if (ScanKeyword("mm")) return (7227, 2540);
            if (ScanKeyword("bp")) return (7227, 7200);
            if (ScanKeyword("dd")) return (1238, 1157);
            if (ScanKeyword("cc")) return (14856, 1157);
            return (0, 0);
        }

        private long Attach(long whole, int fraction, ref bool arithError)
        {
            if (whole < 16384) return whole * Unity + fraction;
            arithError = true;
            return MaxDimen;
        }

        private int Signed(long value, bool negative, bool arithError)
        {
            if (arithError || value > MaxDimen)
            {
                Error("Dimension too large");
                value = MaxDimen;
            }
            return (int)(negative ? -value : value);
        }

        private static int RoundDecimals(List<int> digits)
        {
            int a = 0;
            for (int k = digits.Count - 1; k >= 0; k--)
                a = (a + digits[k] * 2 * Unity) / 10;
            return (a + 1) / 2;
        }

        private static long XnOverD(long x, long n, long d, out long remainder)
        {
            long product = Math.Abs(x) * n;
            long q = product / d;
            remainder = product % d;
            if (x >= 0) return q;
            remainder = -remainder;
            return -q;
        }

        private static long NxPlusY(long n, long x, long y, ref bool arithError)
        {
            long result = n * x + y;
            if (Math.Abs(result) > MaxDimen) arithError = true;
            return result;
        }

        public TexGlue ScanGlue()
        {
            if (!ScanSigns(out TexToken t, out bool negative)) return default;

            TexGlue glue = default;
            if (Internal(t, out RegisterKind kind, out int value, out TexGlue internalGlue))
            {
                if (kind == RegisterKind.Skip)
                {
                    if (!negative) return internalGlue;
                    internalGlue.width = -internalGlue.width;
                    internalGlue.stretch = -internalGlue.stretch;
                    internalGlue.shrink = -internalGlue.shrink;
                    return internalGlue;
                }
                glue.width = kind == RegisterKind.Dimen ? Signed(value, negative, false) : Units(value, 0, negative, false, out _);
            }
            else glue.width = DimenAfter(t, negative, false, out _);

            if (ScanKeyword("plus")) glue.stretch = ScanDimen(true, out glue.stretchOrder);
            if (ScanKeyword("minus")) glue.shrink = ScanDimen(true, out glue.shrinkOrder);
            return glue;
        }
        #endregion

        #region ---- expansion ----
        private void Expand(TexToken t, Meaning meaning)
        {
            if (++steps > MaxSteps)
            {
                Halt("Expansion limit reached; a macro probably calls itself");
                return;
            }

            if (meaning is Macro macro)
            {
                MacroCall(t, macro);
                return;
            }

            Primitive p = (Primitive)meaning;
            switch (p.op)
            {
                case Op.Expandafter:
                    {
                        if (!Get(out TexToken first) || !Get(out TexToken second)) return;
                        if (!noexpandRead && Expandable(second, out Meaning? m)) Expand(second, m!);
                        else Back(second);
                        Back(first);
                        break;
                    }
                case Op.Noexpand:
                    {
                        if (!Get(out TexToken next)) return;
                        Push(new[] { next }, Expandable(next, out _));
                        break;
                    }
                case Op.Csname:
                    {
                        string? name = CsnameName();
                        if (name == null) return;
                        if (meanings.Get(name) == null) meanings.Set(name, primitives["relax"], false, groups);
                        Back(TexToken.Cs(name, t.line, t.column));
                        break;
                    }
                case Op.String:
                    if (Get(out TexToken target)) Push(Chars(target.IsCs ? "\\" + target.name : target.ch.ToString()));
                    break;
                case Op.The:
                    Push(TheTokens());
                    break;
                case Op.Else:
                    if (conds.Count == 0 || conds[^1].limit == Op.Fi) Error("Extra \\else");
                    else SkipToFi();
                    break;
                case Op.Or:
                    if (conds.Count == 0 || conds[^1].limit != Op.Or) Error("Extra \\or");
                    else SkipToFi();
                    break;
                case Op.Fi:
                    if (conds.Count == 0) Error("Extra \\fi");
                    else conds.RemoveAt(conds.Count - 1);
                    break;
                case Op.Value:
                    {
                        string? name = NameArgument(t);
                        if (name == null) return;
                        if (meanings.Get("c@" + name) is Register { kind: RegisterKind.Count }) Back(TexToken.Cs("c@" + name, t.line, t.column));
                        else
                        {
                            Error($"LaTeX Error: No counter '{name}' defined");
                            Back(TexToken.Char('0', TexCatcode.Other));
                        }
                        break;
                    }
                case Op.IfNextChar:
                    {
                        if (!GetNonBlank(out TexToken wanted)) return;
                        TexToken[]? yes = Argument(t, true);
                        TexToken[]? no = yes == null ? null : Argument(t, true);
                        if (no == null) return;
                        bool got = GetNonBlank(out TexToken next);
                        if (got) Back(next);
                        Push(got && SameMeaning(next, wanted) ? yes! : no);
                        break;
                    }
                case Op.IfStar:
                    {
                        TexToken[]? yes = Argument(t, true);
                        TexToken[]? no = yes == null ? null : Argument(t, true);
                        if (no == null) return;
                        bool got = GetNonBlank(out TexToken next);
                        bool star = got && next.IsChar('*', TexCatcode.Other);
                        if (got && !star) Back(next);
                        Push(star ? yes! : no);
                        break;
                    }
                default:
                    Conditional(p.op);
                    break;
            }
        }

        private static TexToken[] Chars(string text) =>
            text.Select(c => TexToken.Char(c, c == ' ' ? TexCatcode.Space : TexCatcode.Other)).ToArray();

        // Expands to \endcsname and returns the name; null when it was cut short.
        private string? CsnameName()
        {
            StringBuilder name = new StringBuilder();
            while (true)
            {
                if (!GetX(out TexToken t))
                {
                    Error("Missing \\endcsname inserted");
                    return null;
                }
                if (!IsActiveOrCs(t))
                {
                    name.Append(t.ch);
                    continue;
                }
                if (MeaningOf(t) is not Primitive { op: Op.Endcsname })
                {
                    Error("Missing \\endcsname inserted");
                    Back(t);
                }
                return name.ToString();
            }
        }

        private TexToken[] TheTokens()
        {
            if (!GetX(out TexToken t)) return Chars("0");

            Meaning? meaning = MeaningOf(t);
            if (meaning is Register { kind: RegisterKind.Char } chardef) return Chars(Number(chardef.index));
            if (meaning is Primitive { op: Op.Catcode }) return Chars(Number((int)catcodes.Get(ScanCharCode())));
            if (RegisterOf(t, out RegisterKind kind, out int index))
                return kind switch
                {
                    RegisterKind.Count => Chars(Number(counts.Get(index))),
                    RegisterKind.Dimen => Chars(Scaled(dimens.Get(index)) + "pt"),
                    RegisterKind.Skip => Chars(GlueText(skips.Get(index))),
                    _ => toks.Get(index)
                };

            Error($"You can't use {t} after \\the");
            return Chars("0");
        }

        private static string Number(int n) => n.ToString(CultureInfo.InvariantCulture);

        // tex.web's print_scaled: the fewest decimals that read back to the same sp.
        private static string Scaled(int s)
        {
            StringBuilder text = new StringBuilder();
            if (s < 0)
            {
                text.Append('-');
                s = -s;
            }
            text.Append(s / Unity).Append('.');
            s = 10 * (s % Unity) + 5;
            int delta = 10;
            do
            {
                if (delta > Unity) s += 0x8000 - 50000;
                text.Append((char)('0' + s / Unity));
                s = 10 * (s % Unity);
                delta *= 10;
            } while (s > delta);
            return text.ToString();
        }

        private static string GlueText(TexGlue g)
        {
            string text = Scaled(g.width) + "pt";
            if (g.stretch != 0) text += " plus " + Scaled(g.stretch) + Order(g.stretchOrder);
            if (g.shrink != 0) text += " minus " + Scaled(g.shrink) + Order(g.shrinkOrder);
            return text;
        }

        private static string Order(byte order) => order == 0 ? "pt" : "fil" + new string('l', order - 1);
        #endregion

        #region ---- macro calls ----
        private void MacroCall(TexToken t, Macro macro)
        {
            TexToken[] ps = macro.parameters;
            List<TexToken[]> args = new List<TexToken[]>(9);
            int pi = 0;

            while (pi < ps.Length && ps[pi].param == 0)
            {
                if (!Get(out TexToken x))
                {
                    Error($"File ended while scanning use of {t}");
                    return;
                }
                if (!x.Same(ps[pi]))
                {
                    Error($"Use of {t} doesn't match its definition");
                    Back(x);
                    return;
                }
                pi++;
            }

            if (macro.optional != null)
            {
                args.Add(Optional(t) ?? macro.optional);
                pi++;
            }

            while (pi < ps.Length)
            {
                int start = ++pi;
                while (pi < ps.Length && ps[pi].param == 0) pi++;
                TexToken[]? arg = pi == start ? Argument(t, macro.isLong) : Delimited(t, ps[start..pi], macro.isLong);
                if (arg == null) return;
                args.Add(arg);
            }

            List<TexToken> output = new List<TexToken>(macro.body.Length);
            foreach (TexToken b in macro.body)
                if (b.param > 0) output.AddRange(args[b.param - 1]);
                else output.Add(b);
            Push(output.ToArray());
        }

        // An undelimited argument: one token, or a group without its braces.
        public TexToken[]? Argument(TexToken owner, bool isLong)
        {
            if (!GetNonBlank(out TexToken t))
            {
                Error($"File ended while scanning use of {owner}");
                return null;
            }
            if (t.IsChar(t.ch, TexCatcode.EndGroup))
            {
                Error($"Argument of {owner} has an extra }}");
                Back(t);
                return null;
            }
            if (!isLong && t.IsCs && t.name == "par")
            {
                Error($"Paragraph ended before {owner} was complete");
                Back(t);
                return null;
            }
            if (!t.IsChar(t.ch, TexCatcode.BeginGroup)) return new[] { t };
            return Balanced(owner, isLong, false)?.ToArray();
        }

        private TexToken[]? Delimited(TexToken owner, TexToken[] delimiter, bool isLong)
        {
            List<TexToken> arg = new List<TexToken>();
            List<int> depths = new List<int>();
            int depth = 0;
            while (true)
            {
                if (!Get(out TexToken x))
                {
                    Error($"File ended while scanning use of {owner}");
                    return null;
                }
                if (!isLong && x.IsCs && x.name == "par")
                {
                    Error($"Paragraph ended before {owner} was complete");
                    Back(x);
                    return null;
                }
                if (x.IsChar(x.ch, TexCatcode.EndGroup) && depth == 0)
                {
                    Error($"Argument of {owner} has an extra }}");
                    return null;
                }

                depths.Add(depth);
                arg.Add(x);
                if (x.IsChar(x.ch, TexCatcode.BeginGroup)) depth++;
                else if (x.IsChar(x.ch, TexCatcode.EndGroup)) depth--;

                if (depth == 0 && x.Same(delimiter[^1]) && Ends(arg, depths, delimiter))
                {
                    arg.RemoveRange(arg.Count - delimiter.Length, delimiter.Length);
                    return Stripped(arg);
                }
            }
        }

        private static bool Ends(List<TexToken> arg, List<int> depths, TexToken[] delimiter)
        {
            int offset = arg.Count - delimiter.Length;
            if (offset < 0) return false;
            for (int i = 0; i < delimiter.Length; i++)
                if (depths[offset + i] != 0 || !arg[offset + i].Same(delimiter[i])) return false;
            return true;
        }

        // A delimited argument that is exactly one group loses its outer braces.
        private static TexToken[] Stripped(List<TexToken> arg)
        {
            if (arg.Count < 2 || !arg[0].IsChar(arg[0].ch, TexCatcode.BeginGroup) || !arg[^1].IsChar(arg[^1].ch, TexCatcode.EndGroup))
                return arg.ToArray();

            int depth = 0;
            for (int i = 0; i < arg.Count - 1; i++)
            {
                if (arg[i].IsChar(arg[i].ch, TexCatcode.BeginGroup)) depth++;
                else if (arg[i].IsChar(arg[i].ch, TexCatcode.EndGroup)) depth--;
                if (depth == 0) return arg.ToArray();
            }
            return arg.GetRange(1, arg.Count - 2).ToArray();
        }

        // LaTeX's [optional] argument, up to the first ] outside braces; null when there is none.
        public TexToken[]? Optional(TexToken owner)
        {
            if (!GetNonBlank(out TexToken t)) return null;
            if (!t.IsChar('[', TexCatcode.Other))
            {
                Back(t);
                return null;
            }
            return Delimited(owner, new[] { TexToken.Char(']', TexCatcode.Other) }, true);
        }
        #endregion

        #region ---- conditionals ----
        private void Conditional(Op op)
        {
            int at = line;
            bool result;
            switch (op)
            {
                case Op.If or Op.Ifcat:
                    {
                        GetX(out TexToken a);
                        GetX(out TexToken b);
                        result = op == Op.If ? CharCode(a) == CharCode(b) : Category(a) == Category(b);
                        break;
                    }
                case Op.Ifx:
                    {
                        Get(out TexToken a);
                        Get(out TexToken b);
                        result = SameMeaning(a, b);
                        break;
                    }
                case Op.Ifnum:
                    {
                        int a = ScanInt();
                        char relation = ScanRelation(op);
                        int b = ScanInt();
                        result = relation == '<' ? a < b : relation == '>' ? a > b : a == b;
                        break;
                    }
                case Op.Ifdim:
                    {
                        int a = ScanDimen();
                        char relation = ScanRelation(op);
                        int b = ScanDimen();
                        result = relation == '<' ? a < b : relation == '>' ? a > b : a == b;
                        break;
                    }
                case Op.Ifodd:
                    result = ScanInt() % 2 != 0;
                    break;
                case Op.Ifdefined:
                    result = Get(out TexToken d) && (!IsActiveOrCs(d) || MeaningOf(d) != null);
                    break;
                case Op.Ifcsname:
                    {
                        string? name = CsnameName();
                        result = name != null && meanings.Get(name) != null;
                        break;
                    }
                case Op.Ifcase:
                    Case(ScanInt(), at);
                    return;
                default:
                    result = op == Op.Iftrue;
                    break;
            }

            if (result)
            {
                conds.Add(new Cond(Op.Else, at));
                return;
            }
            while (true)
            {
                Op stop = SkipBranch(at);
                if (stop == Op.Or)
                {
                    Error("Extra \\or");
                    continue;
                }
                if (stop == Op.Else) conds.Add(new Cond(Op.Fi, at));
                return;
            }
        }

        private void Case(int n, int at)
        {
            while (true)
            {
                if (n == 0)
                {
                    conds.Add(new Cond(Op.Or, at));
                    return;
                }
                Op stop = SkipBranch(at);
                if (stop == Op.Or) n--;
                else
                {
                    if (stop == Op.Else) conds.Add(new Cond(Op.Fi, at));
                    return;
                }
            }
        }

        // Skips to the \else, \or or \fi of this level unexpanded; Relax when the source ends first.
        private Op SkipBranch(int at)
        {
            int depth = 0;
            while (Get(out TexToken x))
            {
                if (MeaningOf(x) is not Primitive p) continue;
                if (p.op is >= Op.If and <= Op.Ifcsname) depth++;
                else if (p.op == Op.Fi)
                {
                    if (depth == 0) return Op.Fi;
                    depth--;
                }
                else if (depth == 0 && p.op is Op.Else or Op.Or) return p.op;
            }
            Error($"Incomplete \\if; all text was ignored after line {at}");
            return Op.Relax;
        }

        private void SkipToFi()
        {
            int at = conds[^1].line;
            conds.RemoveAt(conds.Count - 1);
            while (true)
            {
                Op stop = SkipBranch(at);
                if (stop is Op.Fi or Op.Relax) return;
            }
        }

        private char ScanRelation(Op op)
        {
            bool got = GetNonBlankX(out TexToken t);
            if (got && t.cat == TexCatcode.Other && !t.IsCs && t.ch is '<' or '=' or '>') return t.ch;
            Error($"Missing = inserted for \\{op.ToString().ToLowerInvariant()}");
            if (got) Back(t);
            return '=';
        }

        private int CharCode(TexToken t)
        {
            if (!IsActiveOrCs(t)) return t.ch;
            return MeaningOf(t) is CharMeaning c ? c.token.ch : -1;
        }

        private int Category(TexToken t)
        {
            if (!IsActiveOrCs(t)) return (int)t.cat;
            return MeaningOf(t) is CharMeaning c ? (int)c.token.cat : 16;
        }

        private bool SameMeaning(TexToken a, TexToken b)
        {
            bool aCs = IsActiveOrCs(a), bCs = IsActiveOrCs(b);
            if (!aCs && !bCs) return a.ch == b.ch && a.cat == b.cat;

            Meaning? ma = aCs ? MeaningOf(a) : new CharMeaning(a);
            Meaning? mb = bCs ? MeaningOf(b) : new CharMeaning(b);
            return Equal(ma, mb);
        }

        private static bool Equal(Meaning? a, Meaning? b)
        {
            if (ReferenceEquals(a, b)) return true;
            return (a, b) switch
            {
                (CharMeaning x, CharMeaning y) => x.token.ch == y.token.ch && x.token.cat == y.token.cat,
                (Register x, Register y) => x.kind == y.kind && x.index == y.index,
                (Macro x, Macro y) => x.isLong == y.isLong && Same(x.parameters, y.parameters) && Same(x.body, y.body)
                    && (x.optional == null ? y.optional == null : y.optional != null && Same(x.optional, y.optional)),
                _ => false
            };
        }

        private static bool Same(TexToken[] a, TexToken[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!a[i].Same(b[i])) return false;
            return true;
        }
        #endregion

        #region ---- LaTeX ----
        public bool TakeStar()
        {
            if (!GetNonBlank(out TexToken t)) return false;
            if (t.IsChar('*', TexCatcode.Other)) return true;
            Back(t);
            return false;
        }

        // \cmd or {\cmd}.
        private TexToken? CommandName(TexToken owner)
        {
            if (!GetNonBlank(out TexToken t))
            {
                Error($"File ended while scanning use of {owner}");
                return null;
            }
            if (IsActiveOrCs(t)) return t;
            if (t.IsChar(t.ch, TexCatcode.BeginGroup))
            {
                List<TexToken>? inner = Balanced(owner, true, false);
                List<TexToken> named = inner?.Where(x => !IsSpace(x)).ToList() ?? new List<TexToken>();
                if (named.Count == 1 && IsActiveOrCs(named[0])) return named[0];
            }
            else Back(t);
            Error("LaTeX Error: Missing control sequence");
            return null;
        }

        public string? NameArgument(TexToken owner)
        {
            TexToken[]? arg = Argument(owner, true);
            if (arg == null) return null;
            StringBuilder name = new StringBuilder();
            foreach (TexToken t in arg)
            {
                if (IsActiveOrCs(t))
                {
                    Error("Missing \\endcsname inserted");
                    return null;
                }
                name.Append(t.ch);
            }
            return name.ToString();
        }

        // [n][default]: the argument count and the optional first argument's default.
        private bool ArgumentSpec(TexToken owner, out int arity, out TexToken[]? optional)
        {
            arity = 0;
            optional = null;
            TexToken[]? count = Optional(owner);
            if (count == null) return true;

            string digits = new string(count.Where(c => !IsSpace(c)).Select(c => c.IsCs ? '?' : c.ch).ToArray());
            if (!int.TryParse(digits, out arity) || arity is < 0 or > 9)
            {
                Error("LaTeX Error: Bad argument count; it must be a number from 0 to 9");
                arity = 0;
            }
            TexToken[]? def = Optional(owner);
            if (arity > 0) optional = def;
            return true;
        }

        private Macro MacroOf(TexToken[] body, int arity, TexToken[]? optional, bool isLong, TexToken owner) => new Macro
        {
            parameters = Enumerable.Range(1, arity).Select(TexToken.Param).ToArray(),
            body = Slots(body.ToList(), arity, owner),
            optional = optional,
            isLong = isLong
        };

        private void NewCommand(Op op, TexToken owner)
        {
            bool star = TakeStar();
            if (CommandName(owner) is not TexToken name) return;
            ArgumentSpec(owner, out int arity, out TexToken[]? optional);
            TexToken[]? body = Argument(owner, true);
            if (body == null) return;

            bool defined = MeaningOf(name) != null;
            if (op == Op.NewCommand && defined)
            {
                Error($"LaTeX Error: Command {name} already defined");
                return;
            }
            if (op == Op.ProvideCommand && defined) return;
            if (op == Op.RenewCommand && !defined) Error($"LaTeX Error: Command {name} undefined");

            meanings.Set(Key(name), MacroOf(body, arity, optional, !star, name), false, groups);
        }

        private void NewEnvironment(Op op, TexToken owner)
        {
            bool star = TakeStar();
            string? name = NameArgument(owner);
            if (name == null) return;
            ArgumentSpec(owner, out int arity, out TexToken[]? optional);
            TexToken[]? begin = Argument(owner, true);
            TexToken[]? end = begin == null ? null : Argument(owner, true);
            if (end == null) return;

            bool defined = meanings.Get(name) != null;
            if (op == Op.NewEnvironment && defined)
            {
                Error($"LaTeX Error: Environment {name} already defined");
                return;
            }
            if (op == Op.RenewEnvironment && !defined) Error($"LaTeX Error: Environment {name} undefined");

            TexToken cs = TexToken.Cs(name);
            meanings.Set(name, MacroOf(begin!, arity, optional, !star, cs), false, groups);
            meanings.Set("end" + name, MacroOf(end, 0, null, !star, TexToken.Cs("end" + name)), false, groups);
        }

        private void Begin(TexToken owner)
        {
            string? name = NameArgument(owner);
            if (name == null) return;

            groups.Add(new Group(GroupKind.Environment, name, line));
            if (meanings.Get(name) == null) Error($"LaTeX Error: Environment {name} undefined");
            else Back(TexToken.Cs(name, owner.line, owner.column));
        }

        private void End(TexToken owner)
        {
            string? name = NameArgument(owner);
            if (name == null) return;

            Group? open = groups.Count > 0 && groups[^1].kind == GroupKind.Environment ? groups[^1] : null;
            if (open == null)
            {
                Error($"LaTeX Error: \\end{{{name}}} without a matching \\begin");
                return;
            }
            if (open.environment != name)
                Error($"LaTeX Error: \\begin{{{open.environment}}} on input line {open.line} ended by \\end{{{name}}}");

            List<TexToken> tail = new List<TexToken>(2);
            if (meanings.Get("end" + name) != null) tail.Add(TexToken.Cs("end" + name, owner.line, owner.column));
            tail.Add(TexToken.Cs(EndEnvironmentName, owner.line, owner.column));
            Push(tail.ToArray());
        }

        private void NewIf()
        {
            if (!GetDefinable(out TexToken name)) return;
            if (name.name is not { Length: > 2 } full || !full.StartsWith("if"))
            {
                Error($"\\newif needs a name beginning with \\if, not {name}");
                return;
            }

            string stem = full[2..];
            meanings.Set(full, primitives["iffalse"], false, groups);
            foreach ((string suffix, string value) in new[] { ("true", "iftrue"), ("false", "iffalse") })
                meanings.Set(stem + suffix, new Macro { body = new[] { TexToken.Cs("let"), TexToken.Cs(full), TexToken.Cs(value) } }, false, groups);
        }

        private void NewCounter(TexToken owner)
        {
            string? name = NameArgument(owner);
            if (name == null) return;
            TexToken[]? within = Optional(owner);

            if (meanings.Get("c@" + name) != null)
            {
                Error($"LaTeX Error: Command \\c@{name} already defined");
                return;
            }
            if (nextCounter > 32767)
            {
                Error("No room for a new \\count");
                return;
            }

            int index = nextCounter++;
            counts.Set(index, 0, true, groups);
            meanings.Set("c@" + name, new Register(RegisterKind.Count, index), true, groups);
            meanings.Set("the" + name, new Macro { body = new[] { TexToken.Cs("the"), TexToken.Cs("c@" + name) } }, true, groups);

            if (within == null) return;
            string outer = new string(within.Where(c => !IsSpace(c)).Select(c => c.ch).ToArray());
            if (meanings.Get("c@" + outer) is not Register { kind: RegisterKind.Count })
            {
                Error($"LaTeX Error: No counter '{outer}' defined");
                return;
            }
            if (!resets.TryGetValue(outer, out List<string>? inner)) resets[outer] = inner = new List<string>();
            inner.Add(name);
        }

        // \newlength{\name}: a new skip register, globally zero.
        private void NewLength(TexToken owner)
        {
            TexToken[]? arg = Argument(owner, true);
            TexToken[]? names = arg?.Where(a => !IsSpace(a)).ToArray();
            if (names is not { Length: 1 } || !names[0].IsCs)
            {
                Error("Missing control sequence inserted");
                return;
            }
            if (meanings.Get(names[0].name!) != null)
            {
                Error($"LaTeX Error: Command {names[0]} already defined");
                return;
            }
            if (nextLength > 32767)
            {
                Error("No room for a new \\skip");
                return;
            }

            int index = nextLength++;
            skips.Set(index, default, true, groups);
            meanings.Set(names[0].name!, new Register(RegisterKind.Skip, index), true, groups);
        }

        private void CounterAssign(Op op, TexToken owner)
        {
            string? name = NameArgument(owner);
            TexToken[]? arg = name == null ? null : Argument(owner, true);
            if (arg == null) return;

            if (meanings.Get("c@" + name) is not Register { kind: RegisterKind.Count } counter)
            {
                Error($"LaTeX Error: No counter '{name}' defined");
                return;
            }

            Push(arg.Append(TexToken.Cs("relax")).ToArray());
            int value = ScanInt();
            if (Get(out TexToken after) && MeaningOf(after) is not Primitive { op: Op.Relax }) Back(after);

            long result = op == Op.SetCounter ? value : (long)counts.Get(counter.index) + value;
            if (result is > int.MaxValue or < -int.MaxValue) Error("Arithmetic overflow");
            else counts.Set(counter.index, (int)result, true, groups);
        }

        // A LaTeX counter's value; 0 and an error when there is none.
        public int Counter(string name)
        {
            if (meanings.Get("c@" + name) is Register { kind: RegisterKind.Count } counter) return counts.Get(counter.index);
            Error($"LaTeX Error: No counter '{name}' defined");
            return 0;
        }

        // Adds one globally and zeroes the counters numbered within it, and theirs.
        public void StepCounter(string name)
        {
            if (meanings.Get("c@" + name) is not Register { kind: RegisterKind.Count } counter)
            {
                Error($"LaTeX Error: No counter '{name}' defined");
                return;
            }
            counts.Set(counter.index, counts.Get(counter.index) + 1, true, groups);
            ResetWithin(name, 0);
        }

        private void ResetWithin(string name, int depth)
        {
            if (depth > 16 || !resets.TryGetValue(name, out List<string>? inner)) return;
            foreach (string reset in inner)
            {
                if (meanings.Get("c@" + reset) is Register { kind: RegisterKind.Count } counter) counts.Set(counter.index, 0, true, groups);
                ResetWithin(reset, depth + 1);
            }
        }
        #endregion

        #region ---- typesetter interface ----
        // A count, dimen or skip register's value by name (dimensions and glue widths in sp); null when it names none.
        public int? RegisterValue(string name)
        {
            switch (meanings.Get(name))
            {
                case Register { kind: RegisterKind.Count } r: return counts.Get(r.index);
                case Register { kind: RegisterKind.Dimen } r: return dimens.Get(r.index);
                case Register { kind: RegisterKind.Skip } r: return skips.Get(r.index).width;
                default: return null;
            }
        }

        // A skip register's glue by name, a dimen as rigid glue; null when it names neither.
        public TexGlue? RegisterGlue(string name)
        {
            switch (meanings.Get(name))
            {
                case Register { kind: RegisterKind.Skip } r: return skips.Get(r.index);
                case Register { kind: RegisterKind.Dimen } r: return new TexGlue { width = dimens.Get(r.index) };
                default: return null;
            }
        }

        // A control sequence Next hands to the caller unexpanded, under its own name.
        public void DefinePrimitive(string name) => meanings.Set(name, new Primitive(Op.Caller, name), true, groups);

        public bool IsDefined(string name) => meanings.Get(name) != null;

        // Runs when the current group ends; never, outside any group.
        public void Save(Action restore)
        {
            if (groups.Count > 0) groups[^1].saves.Add(restore);
        }

        public void Insert(TexToken[] tokens) => Push(tokens);

        // The tokens fully expanded, as \edef would; null when expansion stopped.
        public TexToken[]? Expanded(TexToken[] tokens, TexToken owner)
        {
            Push(tokens.Append(TexToken.Char('}', TexCatcode.EndGroup)).ToArray());
            return ExpandedBody(owner)?.ToArray();
        }

        // Source text read with the current catcodes and inserted, its tokens carrying no position.
        public void InsertSource(string text)
        {
            TexReader inserted = new TexReader(text, "", c => catcodes.Get(c), (message, _, _) => Error(message));
            List<TexToken> tokens = new List<TexToken>();
            while (inserted.Next(out TexToken t))
                tokens.Add(t.IsCs ? TexToken.Cs(t.name!) : TexToken.Char(t.ch, t.cat));
            Push(tokens.ToArray());
        }

        // A file read over the source with the catcodes as it is read; its tokens and errors carry where it was input.
        public void InputSource(string text, Action? atEnd = null)
        {
            int at = line, col = column;
            files.Push((reader, atEnd, at, col, input.Count));
            reader = new TexReader(text, "", c => catcodes.Get(c), (message, _, _) => errors.Add(new TexError(at, col, message)));
        }

        // Whether the next token comes from the source itself, so raw characters can be read.
        public bool CanReadRaw => !input.Exists(l => l.index < l.tokens.Length);

        // Source characters up to a terminator, which is consumed; null when the source ends first.
        public string? ReadRaw(string terminator) => CanReadRaw ? reader.ReadRaw(terminator) : null;

        // The next source character on this line.
        public bool ReadRawChar(out char c)
        {
            c = '\0';
            return CanReadRaw && reader.ReadRawChar(out c);
        }
        #endregion
    }
}
