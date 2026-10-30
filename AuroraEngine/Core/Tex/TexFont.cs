namespace ArctisAurora.Core.Tex
{
    public enum TexFamily { Roman, Sans, Mono }

    // A font selection: family, series and shape at a size in sp.
    public readonly record struct TexFont(TexFamily family, bool bold, bool italic, int size, string? face = null);

    // What a character is set in; baselineSkip in sp, 0 takes the size command's for the font's size; inside a biblatex URL,
    // the stretch between characters in sp and the penalties of breaking after an ordinary and a big break character.
    public readonly record struct TexStyle(TexFont font, string? color, bool underline, int baselineSkip = 0,
        int urlStretch = 0, int urlBreakPenalty = 0, int urlBigBreakPenalty = 0);
}
