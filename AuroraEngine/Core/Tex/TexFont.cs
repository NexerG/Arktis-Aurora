namespace ArctisAurora.Core.Tex
{
    public enum TexFamily { Roman, Sans, Mono }

    // A font selection: family, series and shape at a size in sp.
    public readonly record struct TexFont(TexFamily family, bool bold, bool italic, int size, string? face = null);

    // What a character is set in; baselineSkip in sp, 0 takes the size command's for the font's size; inside a URL,
    // \Urlmuskip's stretch, width and shrink in sp and the penalties of breaking after an ordinary and a big break character;
    // in a biblatex URL, the penalty and \biburlnumskip / ucskip / lcskip glue in sp after a digit, capital and lowercase letter.
    public readonly record struct TexStyle(TexFont font, string? color, bool underline, int baselineSkip = 0,
        int urlStretch = 0, int urlBreakPenalty = 0, int urlBigBreakPenalty = 0, int urlWidth = 0, int urlShrink = 0,
        int urlNumPenalty = 0, int urlNumWidth = 0, int urlNumStretch = 0, int urlNumShrink = 0,
        int urlUcPenalty = 0, int urlUcWidth = 0, int urlUcStretch = 0, int urlUcShrink = 0,
        int urlLcPenalty = 0, int urlLcWidth = 0, int urlLcStretch = 0, int urlLcShrink = 0);
}
