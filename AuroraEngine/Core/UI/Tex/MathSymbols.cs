namespace ArctisAurora.Core.UI.Tex
{
    public enum MathClass { Ord, Op, Bin, Rel, Open, Close, Punct, Inner }

    public static class MathSymbols
    {
        // command name without the backslash → character and class
        public static readonly Dictionary<string, (char ch, MathClass cls)> commands = new()
        {
            // lowercase Greek
            ["alpha"] = ('α', MathClass.Ord), ["beta"] = ('β', MathClass.Ord), ["gamma"] = ('γ', MathClass.Ord),
            ["delta"] = ('δ', MathClass.Ord), ["epsilon"] = ('ϵ', MathClass.Ord), ["varepsilon"] = ('ε', MathClass.Ord),
            ["zeta"] = ('ζ', MathClass.Ord), ["eta"] = ('η', MathClass.Ord), ["theta"] = ('θ', MathClass.Ord),
            ["vartheta"] = ('ϑ', MathClass.Ord), ["iota"] = ('ι', MathClass.Ord), ["kappa"] = ('κ', MathClass.Ord),
            ["lambda"] = ('λ', MathClass.Ord), ["mu"] = ('μ', MathClass.Ord), ["nu"] = ('ν', MathClass.Ord),
            ["xi"] = ('ξ', MathClass.Ord), ["omicron"] = ('ο', MathClass.Ord), ["pi"] = ('π', MathClass.Ord),
            ["varpi"] = ('ϖ', MathClass.Ord), ["rho"] = ('ρ', MathClass.Ord), ["varrho"] = ('ϱ', MathClass.Ord),
            ["sigma"] = ('σ', MathClass.Ord), ["varsigma"] = ('ς', MathClass.Ord), ["tau"] = ('τ', MathClass.Ord),
            ["upsilon"] = ('υ', MathClass.Ord), ["phi"] = ('ϕ', MathClass.Ord), ["varphi"] = ('φ', MathClass.Ord),
            ["chi"] = ('χ', MathClass.Ord), ["psi"] = ('ψ', MathClass.Ord), ["omega"] = ('ω', MathClass.Ord),

            // uppercase Greek
            ["Gamma"] = ('Γ', MathClass.Ord), ["Delta"] = ('Δ', MathClass.Ord), ["Theta"] = ('Θ', MathClass.Ord),
            ["Lambda"] = ('Λ', MathClass.Ord), ["Xi"] = ('Ξ', MathClass.Ord), ["Pi"] = ('Π', MathClass.Ord),
            ["Sigma"] = ('Σ', MathClass.Ord), ["Upsilon"] = ('Υ', MathClass.Ord), ["Phi"] = ('Φ', MathClass.Ord),
            ["Psi"] = ('Ψ', MathClass.Ord), ["Omega"] = ('Ω', MathClass.Ord),

            // ordinary symbols
            ["infty"] = ('∞', MathClass.Ord), ["partial"] = ('∂', MathClass.Ord), ["nabla"] = ('∇', MathClass.Ord),
            ["forall"] = ('∀', MathClass.Ord), ["exists"] = ('∃', MathClass.Ord), ["nexists"] = ('∄', MathClass.Ord),
            ["emptyset"] = ('∅', MathClass.Ord), ["varnothing"] = ('∅', MathClass.Ord), ["neg"] = ('¬', MathClass.Ord),
            ["lnot"] = ('¬', MathClass.Ord), ["angle"] = ('∠', MathClass.Ord), ["top"] = ('⊤', MathClass.Ord),
            ["bot"] = ('⊥', MathClass.Ord), ["hbar"] = ('ℏ', MathClass.Ord), ["ell"] = ('ℓ', MathClass.Ord),
            ["wp"] = ('℘', MathClass.Ord), ["Re"] = ('ℜ', MathClass.Ord), ["Im"] = ('ℑ', MathClass.Ord),
            ["aleph"] = ('ℵ', MathClass.Ord), ["prime"] = ('′', MathClass.Ord), ["surd"] = ('√', MathClass.Ord),
            ["vdots"] = ('⋮', MathClass.Ord), ["backslash"] = ('\\', MathClass.Ord), ["vert"] = ('|', MathClass.Ord),
            ["Vert"] = ('‖', MathClass.Ord), ["|"] = ('‖', MathClass.Ord),
            ["ldots"] = ('…', MathClass.Inner), ["dots"] = ('…', MathClass.Inner), ["cdots"] = ('⋯', MathClass.Inner),
            ["ddots"] = ('⋱', MathClass.Inner),

            // escaped characters
            ["{"] = ('{', MathClass.Open), ["}"] = ('}', MathClass.Close), ["%"] = ('%', MathClass.Ord),
            ["$"] = ('$', MathClass.Ord), ["#"] = ('#', MathClass.Ord), ["&"] = ('&', MathClass.Ord),
            ["_"] = ('_', MathClass.Ord), ["square"] = ('□', MathClass.Ord), ["Box"] = ('□', MathClass.Ord),

            // binary operators
            ["pm"] = ('±', MathClass.Bin), ["mp"] = ('∓', MathClass.Bin), ["times"] = ('×', MathClass.Bin),
            ["div"] = ('÷', MathClass.Bin), ["cdot"] = ('⋅', MathClass.Bin), ["ast"] = ('∗', MathClass.Bin),
            ["star"] = ('⋆', MathClass.Bin), ["circ"] = ('∘', MathClass.Bin), ["bullet"] = ('∙', MathClass.Bin),
            ["cap"] = ('∩', MathClass.Bin), ["cup"] = ('∪', MathClass.Bin), ["wedge"] = ('∧', MathClass.Bin),
            ["land"] = ('∧', MathClass.Bin), ["vee"] = ('∨', MathClass.Bin), ["lor"] = ('∨', MathClass.Bin),
            ["oplus"] = ('⊕', MathClass.Bin), ["ominus"] = ('⊖', MathClass.Bin), ["otimes"] = ('⊗', MathClass.Bin),
            ["oslash"] = ('⊘', MathClass.Bin), ["odot"] = ('⊙', MathClass.Bin),

            // relations
            ["le"] = ('≤', MathClass.Rel), ["leq"] = ('≤', MathClass.Rel), ["ge"] = ('≥', MathClass.Rel),
            ["geq"] = ('≥', MathClass.Rel), ["ne"] = ('≠', MathClass.Rel), ["neq"] = ('≠', MathClass.Rel),
            ["equiv"] = ('≡', MathClass.Rel), ["approx"] = ('≈', MathClass.Rel), ["sim"] = ('∼', MathClass.Rel),
            ["simeq"] = ('≃', MathClass.Rel), ["cong"] = ('≅', MathClass.Rel), ["ll"] = ('≪', MathClass.Rel),
            ["gg"] = ('≫', MathClass.Rel), ["prec"] = ('≺', MathClass.Rel), ["succ"] = ('≻', MathClass.Rel),
            ["subset"] = ('⊂', MathClass.Rel), ["supset"] = ('⊃', MathClass.Rel), ["subseteq"] = ('⊆', MathClass.Rel),
            ["supseteq"] = ('⊇', MathClass.Rel), ["in"] = ('∈', MathClass.Rel), ["notin"] = ('∉', MathClass.Rel),
            ["ni"] = ('∋', MathClass.Rel), ["propto"] = ('∝', MathClass.Rel), ["mid"] = ('∣', MathClass.Rel),
            ["parallel"] = ('∥', MathClass.Rel), ["perp"] = ('⊥', MathClass.Rel), ["vdash"] = ('⊢', MathClass.Rel),
            ["therefore"] = ('∴', MathClass.Rel), ["because"] = ('∵', MathClass.Rel),

            // arrows
            ["to"] = ('→', MathClass.Rel), ["rightarrow"] = ('→', MathClass.Rel), ["leftarrow"] = ('←', MathClass.Rel),
            ["gets"] = ('←', MathClass.Rel), ["uparrow"] = ('↑', MathClass.Rel), ["downarrow"] = ('↓', MathClass.Rel),
            ["leftrightarrow"] = ('↔', MathClass.Rel), ["updownarrow"] = ('↕', MathClass.Rel), ["mapsto"] = ('↦', MathClass.Rel),
            ["Rightarrow"] = ('⇒', MathClass.Rel), ["Leftarrow"] = ('⇐', MathClass.Rel), ["Uparrow"] = ('⇑', MathClass.Rel),
            ["Downarrow"] = ('⇓', MathClass.Rel), ["Leftrightarrow"] = ('⇔', MathClass.Rel), ["iff"] = ('⟺', MathClass.Rel),
            ["implies"] = ('⟹', MathClass.Rel), ["impliedby"] = ('⟸', MathClass.Rel), ["longrightarrow"] = ('⟶', MathClass.Rel),
            ["longleftarrow"] = ('⟵', MathClass.Rel), ["longleftrightarrow"] = ('⟷', MathClass.Rel),
            ["Longrightarrow"] = ('⟹', MathClass.Rel), ["Longleftarrow"] = ('⟸', MathClass.Rel),
            ["Longleftrightarrow"] = ('⟺', MathClass.Rel), ["longmapsto"] = ('⟼', MathClass.Rel),

            // delimiters
            ["langle"] = ('⟨', MathClass.Open), ["rangle"] = ('⟩', MathClass.Close), ["lceil"] = ('⌈', MathClass.Open),
            ["rceil"] = ('⌉', MathClass.Close), ["lfloor"] = ('⌊', MathClass.Open), ["rfloor"] = ('⌋', MathClass.Close),
            ["lbrace"] = ('{', MathClass.Open), ["rbrace"] = ('}', MathClass.Close), ["lvert"] = ('|', MathClass.Open),
            ["rvert"] = ('|', MathClass.Close), ["lVert"] = ('‖', MathClass.Open), ["rVert"] = ('‖', MathClass.Close),

            // punctuation
            ["colon"] = (':', MathClass.Punct),
        };

        // big operators; true when they take limits in display style
        public static readonly Dictionary<string, (char ch, bool limits)> bigOps = new()
        {
            ["sum"] = ('∑', true), ["prod"] = ('∏', true), ["coprod"] = ('∐', true),
            ["bigcap"] = ('⋂', true), ["bigcup"] = ('⋃', true), ["bigwedge"] = ('⋀', true), ["bigvee"] = ('⋁', true),
            ["int"] = ('∫', false), ["iint"] = ('∬', false), ["iiint"] = ('∭', false), ["oint"] = ('∮', false),
        };

        // operator names drawn upright; true when they take limits in display style
        public static readonly Dictionary<string, bool> operatorNames = new()
        {
            ["arccos"] = false, ["arcsin"] = false, ["arctan"] = false, ["arg"] = false, ["cos"] = false,
            ["cosh"] = false, ["cot"] = false, ["coth"] = false, ["csc"] = false, ["deg"] = false,
            ["dim"] = false, ["exp"] = false, ["hom"] = false, ["ker"] = false, ["lg"] = false,
            ["ln"] = false, ["log"] = false, ["sec"] = false, ["sin"] = false, ["sinh"] = false,
            ["tan"] = false, ["tanh"] = false,
            ["det"] = true, ["gcd"] = true, ["inf"] = true, ["lim"] = true, ["liminf"] = true,
            ["limsup"] = true, ["max"] = true, ["min"] = true, ["Pr"] = true, ["sup"] = true,
        };

        // accents over their argument; \bar and \overline are a rule, not a glyph
        public static readonly Dictionary<string, char> accents = new()
        {
            ["hat"] = 'ˆ', ["tilde"] = '˜', ["dot"] = '˙', ["ddot"] = '¨', ["vec"] = '⃗',
            ["acute"] = '´', ["check"] = 'ˇ', ["breve"] = '˘',
        };

        // \mathbb letters the charset carries
        public static readonly Dictionary<char, char> blackboard = new()
        {
            ['C'] = 'ℂ', ['N'] = 'ℕ', ['P'] = 'ℙ', ['Q'] = 'ℚ', ['R'] = 'ℝ', ['Z'] = 'ℤ',
        };

        // \left / \right delimiter names
        public static readonly Dictionary<string, char> delimiters = new()
        {
            ["langle"] = '⟨', ["rangle"] = '⟩', ["lceil"] = '⌈', ["rceil"] = '⌉', ["lfloor"] = '⌊', ["rfloor"] = '⌋',
            ["lbrace"] = '{', ["rbrace"] = '}', ["{"] = '{', ["}"] = '}', ["vert"] = '|', ["lvert"] = '|', ["rvert"] = '|',
            ["Vert"] = '‖', ["lVert"] = '‖', ["rVert"] = '‖', ["|"] = '‖',
        };

        private static readonly Dictionary<char, MathClass> classByChar = BuildClasses();

        private static Dictionary<char, MathClass> BuildClasses()
        {
            Dictionary<char, MathClass> map = new()
            {
                ['+'] = MathClass.Bin, ['-'] = MathClass.Bin, ['*'] = MathClass.Bin, ['−'] = MathClass.Bin,
                ['='] = MathClass.Rel, ['<'] = MathClass.Rel, ['>'] = MathClass.Rel, [':'] = MathClass.Rel,
                ['('] = MathClass.Open, ['['] = MathClass.Open, [')'] = MathClass.Close, [']'] = MathClass.Close,
                ['!'] = MathClass.Close, ['?'] = MathClass.Close, [','] = MathClass.Punct, [';'] = MathClass.Punct,
            };
            foreach ((char ch, MathClass cls) in commands.Values)
                map.TryAdd(ch, cls);
            return map;
        }

        // Class of a character typed straight into the source.
        public static MathClass ClassOf(char c) => classByChar.TryGetValue(c, out MathClass cls) ? cls : MathClass.Ord;

        // A character typed straight into the source that is a big operator.
        public static bool IsBigOp(char c, out bool limits)
        {
            foreach ((char ch, bool takesLimits) in bigOps.Values)
                if (ch == c) { limits = takesLimits; return true; }
            limits = false;
            return false;
        }
    }
}
