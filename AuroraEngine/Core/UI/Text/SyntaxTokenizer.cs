namespace ArctisAurora.Core.UI.Text
{
    // What a character of a code block is coloured as.
    public enum SyntaxToken : byte
    {
        Plain, Keyword, String, Number, Comment
    }

    // What a line leaves open for the next one.
    public enum SyntaxState : byte
    {
        None, BlockComment, XmlComment, XmlTag, TripleDouble, TripleSingle, TexMath
    }

    // Colours code a line at a time. C-like rules with per-language keywords, Python's # comments and
    // triple quotes, and an XML mode; any other language gets the C-like rules with common keywords.
    public static class SyntaxTokenizer
    {
        private enum Mode { CLike, Python, Xml, Latex }

        #region ---- keywords ----
        private static readonly HashSet<string> csharp = new HashSet<string>
        {
            "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
            "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "get", "goto", "if",
            "implicit", "in", "init", "int", "interface", "internal", "is", "lock", "long", "nameof", "namespace",
            "new", "not", "null", "object", "operator", "out", "override", "params", "partial", "private",
            "protected", "public", "readonly", "record", "ref", "required", "return", "sbyte", "sealed", "set",
            "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try",
            "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "var", "virtual", "void",
            "volatile", "when", "where", "while", "with", "yield", "and", "or", "value", "dynamic", "global"
        };

        private static readonly HashSet<string> glsl = new HashSet<string>
        {
            "attribute", "binding", "bool", "break", "buffer", "bvec2", "bvec3", "bvec4", "case", "centroid",
            "coherent", "const", "continue", "default", "discard", "do", "double", "dvec2", "dvec3", "dvec4", "else",
            "false", "flat", "float", "for", "highp", "if", "in", "inout", "int", "invariant", "isampler2D", "ivec2",
            "ivec3", "ivec4", "layout", "location", "lowp", "mat2", "mat3", "mat4", "mediump", "noperspective", "out",
            "precision", "push_constant", "readonly", "restrict", "return", "sampler", "sampler2D", "sampler3D",
            "samplerCube", "set", "shared", "smooth", "std140", "std430", "struct", "subroutine", "switch",
            "texture2D", "true", "uint", "uniform", "usampler2D", "uvec2", "uvec3", "uvec4", "varying", "vec2",
            "vec3", "vec4", "void", "volatile", "while", "writeonly"
        };

        private static readonly HashSet<string> python = new HashSet<string>
        {
            "False", "None", "True", "and", "as", "assert", "async", "await", "break", "case", "class", "continue",
            "def", "del", "elif", "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is",
            "lambda", "match", "nonlocal", "not", "or", "pass", "raise", "return", "self", "try", "while", "with",
            "yield"
        };

        private static readonly HashSet<string> common = new HashSet<string>
        {
            "break", "case", "catch", "class", "const", "continue", "default", "do", "else", "enum", "false", "for",
            "function", "if", "import", "let", "new", "null", "return", "static", "struct", "switch", "this", "throw",
            "true", "try", "var", "void", "while"
        };
        #endregion

        // Writes a token per character of line into tokens, starting from what the line above left open.
        public static SyntaxState Tokenize(string line, string? language, SyntaxState state, SyntaxToken[] tokens)
        {
            Array.Clear(tokens, 0, line.Length);
            (Mode mode, HashSet<string> keywords) = Resolve(language);
            return mode switch
            {
                Mode.Xml => Xml(line, state, tokens),
                Mode.Latex => Latex(line, state, tokens),
                Mode.Python => Code(line, state, tokens, keywords, true),
                _ => Code(line, state, tokens, keywords, false)
            };
        }

        private static (Mode, HashSet<string>) Resolve(string? language) => language?.ToLowerInvariant() switch
        {
            "cs" or "csharp" or "c#" => (Mode.CLike, csharp),
            "glsl" or "vert" or "frag" or "comp" or "geom" or "tesc" or "tese" => (Mode.CLike, glsl),
            "py" or "python" => (Mode.Python, python),
            "xml" or "xsd" or "html" or "svg" or "xaml" => (Mode.Xml, common),
            "tex" or "latex" => (Mode.Latex, common),
            _ => (Mode.CLike, common)
        };

        #region ---- C-like and Python ----
        private static SyntaxState Code(string s, SyntaxState state, SyntaxToken[] tokens, HashSet<string> keywords, bool python)
        {
            int i = 0;
            int n = s.Length;
            while (i < n)
            {
                if (state != SyntaxState.None)
                {
                    string close = state == SyntaxState.BlockComment ? "*/" : state == SyntaxState.TripleDouble ? "\"\"\"" : "'''";
                    SyntaxToken token = state == SyntaxState.BlockComment ? SyntaxToken.Comment : SyntaxToken.String;
                    int end = s.IndexOf(close, i, StringComparison.Ordinal);
                    int stop = end < 0 ? n : end + close.Length;
                    Fill(tokens, i, stop, token);
                    i = stop;
                    if (end >= 0) state = SyntaxState.None;
                    continue;
                }

                char c = s[i];
                if (python ? c == '#' : c == '/' && At(s, i, "//"))
                {
                    Fill(tokens, i, n, SyntaxToken.Comment);
                    break;
                }
                if (!python && At(s, i, "/*"))
                {
                    Fill(tokens, i, i + 2, SyntaxToken.Comment);
                    i += 2;
                    state = SyntaxState.BlockComment;
                    continue;
                }
                if (python && (At(s, i, "\"\"\"") || At(s, i, "'''")))
                {
                    Fill(tokens, i, i + 3, SyntaxToken.String);
                    state = c == '"' ? SyntaxState.TripleDouble : SyntaxState.TripleSingle;
                    i += 3;
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    bool verbatim = !python && i > 0 && s[i - 1] == '@';
                    int stop = StringEnd(s, i, c, !verbatim);
                    Fill(tokens, verbatim ? i - 1 : i, stop, SyntaxToken.String);
                    i = stop;
                    continue;
                }
                if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(s[i + 1])))
                {
                    int stop = i + 1;
                    while (stop < n && (char.IsLetterOrDigit(s[stop]) || s[stop] == '_' || s[stop] == '.')) stop++;
                    Fill(tokens, i, stop, SyntaxToken.Number);
                    i = stop;
                    continue;
                }
                if (char.IsLetter(c) || c == '_' || (!python && c == '#'))
                {
                    int stop = i + 1;
                    while (stop < n && (char.IsLetterOrDigit(s[stop]) || s[stop] == '_')) stop++;
                    string word = s[i..stop];
                    if (c == '#' ? stop > i + 1 : keywords.Contains(word)) Fill(tokens, i, stop, SyntaxToken.Keyword);
                    i = stop;
                    continue;
                }
                i++;
            }
            return state;
        }

        // Past the closing quote, or the line's end when none; escape skips a backslash's next character.
        private static int StringEnd(string s, int open, char quote, bool escape)
        {
            for (int i = open + 1; i < s.Length; i++)
            {
                if (escape && s[i] == '\\') i++;
                else if (s[i] == quote) return i + 1;
            }
            return s.Length;
        }
        #endregion

        #region ---- XML ----
        // Tag names and brackets as keywords, attribute values as strings, entities as numbers.
        private static SyntaxState Xml(string s, SyntaxState state, SyntaxToken[] tokens)
        {
            int i = 0;
            int n = s.Length;
            while (i < n)
            {
                if (state == SyntaxState.XmlComment)
                {
                    int end = s.IndexOf("-->", i, StringComparison.Ordinal);
                    int stop = end < 0 ? n : end + 3;
                    Fill(tokens, i, stop, SyntaxToken.Comment);
                    i = stop;
                    if (end >= 0) state = SyntaxState.None;
                    continue;
                }

                char c = s[i];
                if (state == SyntaxState.XmlTag)
                {
                    if (c == '"' || c == '\'')
                    {
                        int stop = StringEnd(s, i, c, false);
                        Fill(tokens, i, stop, SyntaxToken.String);
                        i = stop;
                        continue;
                    }
                    if (c == '>' || At(s, i, "/>") || At(s, i, "?>"))
                    {
                        int stop = c == '>' ? i + 1 : i + 2;
                        Fill(tokens, i, stop, SyntaxToken.Keyword);
                        i = stop;
                        state = SyntaxState.None;
                        continue;
                    }
                    i++;
                    continue;
                }

                if (At(s, i, "<!--"))
                {
                    state = SyntaxState.XmlComment;
                    continue;
                }
                if (c == '<')
                {
                    int stop = i + 1;
                    if (stop < n && (s[stop] == '/' || s[stop] == '?' || s[stop] == '!')) stop++;
                    while (stop < n && (char.IsLetterOrDigit(s[stop]) || s[stop] is '_' or ':' or '-' or '.')) stop++;
                    Fill(tokens, i, stop, SyntaxToken.Keyword);
                    i = stop;
                    state = SyntaxState.XmlTag;
                    continue;
                }
                if (c == '&')
                {
                    int end = s.IndexOf(';', i);
                    if (end > i)
                    {
                        Fill(tokens, i, end + 1, SyntaxToken.Number);
                        i = end + 1;
                        continue;
                    }
                }
                i++;
            }
            return state;
        }
        #endregion

        #region ---- LaTeX ----
        private static readonly HashSet<string> units = new HashSet<string>
        {
            "pt", "pc", "in", "cm", "mm", "bp", "dd", "cc", "sp", "em", "ex", "mu", "fil", "fill", "filll"
        };

        // Control sequences as keywords, math as strings, % comments, numbers with their units.
        private static SyntaxState Latex(string s, SyntaxState state, SyntaxToken[] tokens)
        {
            int i = 0;
            int n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (c == '%')
                {
                    Fill(tokens, i, n, SyntaxToken.Comment);
                    break;
                }
                if (state == SyntaxState.TexMath)
                {
                    int stop = i + 1;
                    if (c == '$')
                    {
                        if (stop < n && s[stop] == '$') stop++;
                        state = SyntaxState.None;
                    }
                    else if (c == '\\')
                    {
                        if (stop < n && s[stop] is ')' or ']') state = SyntaxState.None;
                        stop = ControlEnd(s, i);
                    }
                    Fill(tokens, i, stop, SyntaxToken.String);
                    i = stop;
                    continue;
                }
                if (c == '$' || (c == '\\' && i + 1 < n && s[i + 1] is '(' or '['))
                {
                    int stop = c == '\\' || (i + 1 < n && s[i + 1] == '$') ? i + 2 : i + 1;
                    Fill(tokens, i, stop, SyntaxToken.String);
                    state = SyntaxState.TexMath;
                    i = stop;
                    continue;
                }
                if (c == '\\')
                {
                    int stop = ControlEnd(s, i);
                    Fill(tokens, i, stop, SyntaxToken.Keyword);
                    i = stop;
                    continue;
                }
                if ((char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(s[i + 1]))) && (i == 0 || !char.IsLetter(s[i - 1])))
                {
                    int stop = i + 1;
                    while (stop < n && (char.IsDigit(s[stop]) || s[stop] == '.')) stop++;
                    int unit = stop;
                    while (unit < n && char.IsAsciiLetterLower(s[unit])) unit++;
                    if (units.Contains(s[stop..unit])) stop = unit;
                    Fill(tokens, i, stop, SyntaxToken.Number);
                    i = stop;
                    continue;
                }
                i++;
            }
            return state;
        }

        // Past a control word's letters, or past a control symbol's one character.
        private static int ControlEnd(string s, int backslash)
        {
            int stop = backslash + 1;
            if (stop >= s.Length) return stop;
            if (!char.IsLetter(s[stop])) return stop + 1;
            while (stop < s.Length && char.IsLetter(s[stop])) stop++;
            return stop;
        }
        #endregion

        private static bool At(string s, int i, string what) => string.CompareOrdinal(s, i, what, 0, what.Length) == 0;

        private static void Fill(SyntaxToken[] tokens, int from, int to, SyntaxToken token)
        {
            for (int i = from; i < to; i++)
                tokens[i] = token;
        }
    }
}
