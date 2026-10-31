using System.Globalization;
using System.Text;

namespace ArctisAurora.Core.Tex
{
    // biblatex in a document: its package options and the customizations its preamble declares.
    public sealed class TexBiblatexSettings
    {
        // package options
        public string style = "numeric";
        public string sorting = "";
        public int maxBibNames = 3, minBibNames = 1, maxAlphaNames = 3, minAlphaNames = 1;
        public bool givenInits, abbreviate = true, sortCites, eprint = true, doi = true, url = true, isbn = true;
        public string date = "comp", urlDate = "short", autoLang = "none";

        // \DefineBibliographyStrings per language, \DeclareDelimFormat's code by name, \DeclareNameAlias's name format
        public readonly Dictionary<string, Dictionary<string, string>> strings = new Dictionary<string, Dictionary<string, string>>();
        public readonly Dictionary<string, string> delims = new Dictionary<string, string>();
        public string nameFormat = "given-family";

        public bool Alphabetic => style == "alphabetic";
    }

    // biblatex's standard styles: cited entries labelled, sorted and written as a thebibliography in its list layout.
    public static class TexBiblatex
    {
        private readonly record struct Item(TexBibEntry entry, string label, string sortName, string year, string title, int cited);

        // biblatex's punctuation tracker: a delimiter waits for the next field, and no period follows one already there
        private struct Units
        {
            public StringBuilder text;
            public string pending;
            public bool sentence;
            public bool printed;
        }

        // english.lbx and lithuanian.lbx: each string's long and short form
        private static readonly Dictionary<string, (string full, string brief)> english = new Dictionary<string, (string, string)>
        {
            ["bibliography"] = ("Bibliography", "Bibliography"), ["references"] = ("References", "References"), ["and"] = ("and", "and"),
            ["andothers"] = ("et al.", "et al."), ["in"] = ("in", "in"), ["urlseen"] = ("visited on", "visited on"),
            ["editor"] = ("editor", "ed."), ["editors"] = ("editors", "eds."), ["byeditor"] = ("edited by", "ed. by"),
            ["edition"] = ("edition", "ed."), ["version"] = ("version", "version"), ["page"] = ("page", "p."), ["pages"] = ("pages", "pp."),
            ["mathesis"] = ("Master's thesis", "MA thesis"), ["phdthesis"] = ("PhD thesis", "PhD thesis"), ["volume"] = ("volume", "vol."),
            ["number"] = ("number", "no.")
        };

        private static readonly Dictionary<string, (string full, string brief)> lithuanian = new Dictionary<string, (string, string)>
        {
            ["bibliography"] = ("Bibliografija", "Bibliografija"), ["references"] = ("Literatūros sąrašas", "Literatūra"), ["and"] = ("ir", "ir"),
            ["andothers"] = ("ir kiti", "ir kt."), ["in"] = ("iš", "iš"), ["urlseen"] = ("tikrinta", "tikrinta"),
            ["editor"] = ("sudarytojas", "sud."), ["editors"] = ("sudarytojai", "sud."), ["byeditor"] = ("sudarė", "sud."),
            ["edition"] = ("leidimas", "leid."), ["version"] = ("versija", "vers."), ["page"] = ("puslapis", "p."), ["pages"] = ("puslapiai", "p."),
            ["mathesis"] = ("magistro darbas", "mag. darb."), ["phdthesis"] = ("disertacija", "disert."), ["volume"] = ("tomas", "t."),
            ["number"] = ("numeris", "Nr.")
        };

        private static readonly string[] englishMonths = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };

        #region ---- options ----
        // One key[=value] of \usepackage[…]{biblatex}; false for a key not read.
        public static bool Option(TexBiblatexSettings s, string key, string? value)
        {
            bool on = value == null || value == "true";
            switch (key)
            {
                case "style" or "bibstyle": s.style = value ?? s.style; break;
                case "sorting": s.sorting = value ?? s.sorting; break;
                case "maxnames" or "maxbibnames": s.maxBibNames = Count(value, s.maxBibNames); break;
                case "minnames" or "minbibnames": s.minBibNames = Count(value, s.minBibNames); break;
                case "maxalphanames": s.maxAlphaNames = Count(value, s.maxAlphaNames); break;
                case "minalphanames": s.minAlphaNames = Count(value, s.minAlphaNames); break;
                case "giveninits" or "firstinits": s.givenInits = on; break;
                case "abbreviate": s.abbreviate = on; break;
                case "sortcites": s.sortCites = on; break;
                case "eprint": s.eprint = on; break;
                case "doi": s.doi = on; break;
                case "url": s.url = on; break;
                case "isbn": s.isbn = on; break;
                case "date": s.date = value ?? s.date; break;
                case "urldate": s.urlDate = value ?? s.urlDate; break;
                case "alldates":
                    s.date = value ?? s.date;
                    s.urlDate = value ?? s.urlDate;
                    break;
                case "autolang": s.autoLang = value ?? s.autoLang; break;
                case "backend" or "citestyle" or "seconds": break;
                default: return false;
            }
            return true;
        }

        private static int Count(string? value, int fallback) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : fallback;

        // A bibliography string in a language: the document's \DefineBibliographyStrings first, then the lbx tables.
        public static string String(TexBiblatexSettings s, string language, string key)
        {
            if (s.strings.TryGetValue(language, out Dictionary<string, string>? defined) && defined.TryGetValue(key, out string? text)) return text;
            Dictionary<string, (string full, string brief)> table = language == "lithuanian" ? lithuanian : english;
            if (!table.TryGetValue(key, out (string full, string brief) pair) && !english.TryGetValue(key, out pair)) return key;
            return s.abbreviate ? pair.brief : pair.full;
        }
        #endregion

        #region ---- formatting ----
        // The cited entries as LaTeX: the list layout and heading of the style, then a \bibitem each in sorting order.
        public static string Format(List<TexBibEntry> cited, TexBiblatexSettings s, string language, string? title)
        {
            Item[] items = Sorted(cited, s, language);
            StringBuilder bbl = new StringBuilder(4096);
            string references = title ?? String(s, language, "references");
            bbl.Append("\\def\\refname{").Append(references).Append("}\\def\\bibname{").Append(title ?? String(s, language, "bibliography")).Append('}');
            bbl.Append("\\def\\@biblabelsep{1em}\\def\\@mkbiblab#1{").Append(s.Alphabetic ? "#1\\hss" : "\\hss#1").Append("}%\n");
            bbl.Append("\\def\\@bibsetup#1{\\labelsep\\@biblabelsep\\relax\\labelwidth0pt\\relax");
            for (int i = 0; i < items.Length; i++)
                bbl.Append("\\settowidth\\@tempdima{\\@biblabel{").Append(items[i].label).Append("}}\\ifdim\\@tempdima>\\labelwidth\\labelwidth\\@tempdima\\fi");
            bbl.Append("\\leftmargin\\labelwidth\\advance\\leftmargin\\labelsep\\leftskip\\leftmargin\\parindent0pt\\parskip0pt\\interlinepenalty5000\\relax}%\n");
            bbl.Append("\\begin{thebibliography}{}\n\n");
            for (int i = 0; i < items.Length; i++)
            {
                TexBibEntry entry = items[i].entry;
                string? langid = s.autoLang == "none" ? null : entry["langid"]?.Trim().ToLowerInvariant();
                bool foreign = langid != null && langid != language;
                if (foreign) bbl.Append("\\begin{otherlanguage*}{").Append(langid).Append("}\n");
                bbl.Append("\\bibitem[").Append(items[i].label).Append("]{").Append(entry.key).Append("}\n");
                bbl.Append(Entry(entry, s, language)).Append("\n\n");
                if (foreign) bbl.Append("\\end{otherlanguage*}\n");
            }
            bbl.Append("\\end{thebibliography}\n");
            return bbl.ToString();
        }

        // The entries in the sorting scheme's order, labelled: alphabetic labels with a, b, c for the same label, else numbers.
        private static Item[] Sorted(List<TexBibEntry> cited, TexBiblatexSettings s, string language)
        {
            Item[] items = new Item[cited.Count];
            for (int i = 0; i < items.Length; i++)
            {
                TexBibEntry e = cited[i];
                items[i] = new Item(e, s.Alphabetic ? Label(e, s) : "", SortName(e), Year(e) ?? "", e["sorttitle"] ?? e["title"] ?? "", i);
            }

            string sorting = s.sorting.Length > 0 ? s.sorting : s.Alphabetic ? "anyt" : "nty";
            if (sorting != "none")
            {
                CompareInfo compare = Culture(language).CompareInfo;
                Array.Sort(items, (a, b) =>
                {
                    int order = 0;
                    foreach (char part in sorting)
                    {
                        order = part switch
                        {
                            'a' => compare.Compare(a.label, b.label),
                            'n' => compare.Compare(a.sortName, b.sortName),
                            'y' => compare.Compare(a.year, b.year),
                            'd' => -compare.Compare(a.year, b.year),
                            't' => compare.Compare(a.title, b.title),
                            _ => 0
                        };
                        if (order != 0) return order;
                    }
                    return a.cited.CompareTo(b.cited);
                });
            }

            if (!s.Alphabetic)
            {
                for (int i = 0; i < items.Length; i++)
                    items[i] = items[i] with { label = (i + 1).ToString(CultureInfo.InvariantCulture) };
                return items;
            }
            Dictionary<string, int> same = new Dictionary<string, int>(items.Length);
            for (int i = 0; i < items.Length; i++)
                same[items[i].label] = same.TryGetValue(items[i].label, out int n) ? n + 1 : 1;
            Dictionary<string, char> next = new Dictionary<string, char>();
            for (int i = 0; i < items.Length; i++)
            {
                if (same[items[i].label] < 2) continue;
                char suffix = next.TryGetValue(items[i].label, out char c) ? c : 'a';
                next[items[i].label] = (char)(suffix + 1);
                items[i] = items[i] with { label = items[i].label + suffix };
            }
            return items;
        }

        private static CultureInfo Culture(string language) => language switch
        {
            "lithuanian" => CultureInfo.GetCultureInfo("lt-LT"),
            "english" or "american" or "USenglish" or "british" or "UKenglish" => CultureInfo.GetCultureInfo("en-US"),
            _ => CultureInfo.InvariantCulture
        };

        // One entry's text: the standard styles' driver for its type.
        private static string Entry(TexBibEntry e, TexBiblatexSettings s, string language)
        {
            Units u = new Units { text = new StringBuilder(256), pending = "" };
            string type = e.type switch
            {
                "mastersthesis" or "phdthesis" => "thesis",
                "conference" => "inproceedings",
                "electronic" or "www" => "online",
                "article" or "book" or "inproceedings" or "thesis" or "online" => e.type,
                _ => "misc"
            };
            bool quoted = type is "article" or "inproceedings" or "thesis";
            bool hasAuthor = e["author"] != null;

            Print(ref u, Names(e["author"], s, language) ?? (type == "article" || type == "inproceedings" || type == "thesis" ? null : EditorNames(e, s, language)));
            SetUnit(ref u, s.delims.TryGetValue("nametitledelim", out string? nameTitle) ? nameTitle : ". ", true);
            Print(ref u, Title(e, quoted, language));
            NewUnit(ref u);

            switch (type)
            {
                case "article":
                    ByEditor(ref u, e, s, language);
                    NewUnit(ref u);
                    Print(ref u, Str(ref u, s, language, "in") + ":");
                    SetUnit(ref u, " ");
                    Print(ref u, Emph(e["journaltitle"] ?? e["journal"]));
                    SetUnitStar(ref u, " ");
                    Print(ref u, e["volume"] is string volume ? e["number"] is string number ? volume + "." + number : volume : null);
                    SetUnit(ref u, " ");
                    Print(ref u, Date(e, s) is string issued ? "(" + issued + ")" : null);
                    NewUnit(ref u);
                    Print(ref u, e["note"]);
                    SetUnit(ref u, ", ");
                    Print(ref u, Pages(ref u, e["pages"], s, language));
                    NewUnit(ref u);
                    break;
                case "book":
                    if (hasAuthor) ByEditor(ref u, e, s, language);
                    NewUnit(ref u);
                    Print(ref u, Edition(ref u, e["edition"], s, language));
                    NewUnit(ref u);
                    Print(ref u, e["volume"] is string bookVolume ? Str(ref u, s, language, "volume") + "~" + bookVolume : null);
                    NewUnit(ref u);
                    Print(ref u, e["series"]);
                    SetUnitStar(ref u, " ");
                    Print(ref u, e["series"] != null ? e["number"] : null);
                    NewUnit(ref u);
                    Print(ref u, e["note"]);
                    NewUnit(ref u);
                    Place(ref u, e["publisher"], e, s);
                    NewUnit(ref u);
                    if (s.isbn) Print(ref u, Prefixed("ISBN", e["isbn"]));
                    NewUnit(ref u);
                    break;
                case "inproceedings":
                    Print(ref u, Str(ref u, s, language, "in") + ":");
                    SetUnit(ref u, " ");
                    Print(ref u, Emph(e["booktitle"]));
                    NewUnit(ref u);
                    ByEditor(ref u, e, s, language);
                    NewUnit(ref u);
                    Print(ref u, e["volume"]);
                    NewUnit(ref u);
                    Print(ref u, e["series"]);
                    NewUnit(ref u);
                    Print(ref u, e["note"]);
                    NewUnit(ref u);
                    Print(ref u, e["organization"]);
                    NewUnit(ref u);
                    Place(ref u, e["publisher"], e, s);
                    SetUnit(ref u, ", ");
                    Print(ref u, Pages(ref u, e["pages"], s, language));
                    NewUnit(ref u);
                    if (s.isbn) Print(ref u, Prefixed("ISBN", e["isbn"]));
                    NewUnit(ref u);
                    break;
                case "thesis":
                    Print(ref u, e["note"]);
                    NewUnit(ref u);
                    string kind = e["type"] ?? (e.type == "phdthesis" ? "phdthesis" : "mathesis");
                    Print(ref u, kind is "mathesis" or "phdthesis" ? Str(ref u, s, language, kind) : kind);
                    NewUnit(ref u);
                    Place(ref u, e["institution"] ?? e["school"], e, s);
                    NewUnit(ref u);
                    if (s.isbn) Print(ref u, Prefixed("ISBN", e["isbn"]));
                    NewUnit(ref u);
                    break;
                case "online":
                    ByEditor(ref u, e, s, language);
                    NewUnit(ref u);
                    Print(ref u, e["version"] is string onlineVersion ? Str(ref u, s, language, "version") + "~" + onlineVersion : null);
                    NewUnit(ref u);
                    Print(ref u, e["note"]);
                    NewUnit(ref u);
                    Print(ref u, e["organization"]);
                    NewUnit(ref u);
                    Print(ref u, Date(e, s));
                    NewUnit(ref u);
                    break;
                default:
                    ByEditor(ref u, e, s, language);
                    NewUnit(ref u);
                    Print(ref u, e["howpublished"]);
                    NewUnit(ref u);
                    Print(ref u, e["type"]);
                    NewUnit(ref u);
                    Print(ref u, e["version"] is string miscVersion ? Str(ref u, s, language, "version") + "~" + miscVersion : null);
                    NewUnit(ref u);
                    Print(ref u, e["note"]);
                    NewUnit(ref u);
                    Place(ref u, e["organization"], e, s);
                    NewUnit(ref u);
                    break;
            }

            if (s.doi) Print(ref u, e["doi"] is string doi ? "DOI: \\url{" + Escaped(doi) + "}" : null);
            NewUnit(ref u);
            if (s.eprint) Print(ref u, Eprint(e));
            NewUnit(ref u);
            if (s.url && e["url"] is string url)
            {
                Print(ref u, "URL: \\url{" + Escaped(url) + "}");
                SetUnit(ref u, " ");
                Print(ref u, UrlDate(e, s) is string seen ? "(" + String(s, language, "urlseen") + " " + seen + ")" : null);
            }
            NewUnit(ref u);
            Print(ref u, e["addendum"]);

            if (u.text.Length > 0 && !EndsWith(u.text, ".?!")) u.text.Append('.');
            return u.text.ToString();
        }

        // location: publisher (or organization, institution), date
        private static void Place(ref Units u, string? by, TexBibEntry e, TexBiblatexSettings s)
        {
            Print(ref u, e["location"] ?? e["address"]);
            SetUnitStar(ref u, by != null ? ": " : ", ");
            Print(ref u, by);
            SetUnitStar(ref u, ", ");
            Print(ref u, Date(e, s));
        }

        private static void ByEditor(ref Units u, TexBibEntry e, TexBiblatexSettings s, string language)
        {
            if (e["author"] == null || Names(e["editor"], s, language) is not string editors) return;
            Print(ref u, Str(ref u, s, language, "byeditor") + " " + editors);
        }

        private static string? Title(TexBibEntry e, bool quoted, string language)
        {
            if (e["title"] is not string title) return null;
            if (e["subtitle"] is string subtitle) title = EndsWith(new StringBuilder(title), ".?!") ? title + " " + subtitle : title + ". " + subtitle;
            if (!quoted) return Emph(title);
            return language == "lithuanian" ? "„" + title + "“" : "“" + title + "”";
        }

        private static string? Emph(string? text) => text == null ? null : "\\emph{" + text + "}";

        private static string? Prefixed(string name, string? value) => value == null ? null : name + ": " + value;

        // eprint type: identifier [class], arXiv for an arXiv archive prefix
        private static string? Eprint(TexBibEntry e)
        {
            if (e["eprint"] is not string id) return null;
            string kind = e["eprinttype"] ?? e["archiveprefix"] ?? "eprint";
            string? group = e["eprintclass"] ?? e["primaryclass"];
            if (kind.Equals("arxiv", StringComparison.OrdinalIgnoreCase)) kind = "arXiv";
            string text = kind + ": \\url{" + Escaped(id) + "}";
            return group == null ? text : text + " \\texttt{[" + group + "]}";
        }

        private static string? Pages(ref Units u, string? pages, TexBiblatexSettings s, string language)
        {
            if (pages == null) return null;
            bool range = pages.IndexOf('-') >= 0 || pages.IndexOf(',') >= 0;
            return Str(ref u, s, language, range ? "pages" : "page") + "~" + TexBibliography.Dashed(pages);
        }

        private static string? Edition(ref Units u, string? edition, TexBiblatexSettings s, string language)
        {
            if (edition == null) return null;
            if (!int.TryParse(edition, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return edition;
            string ordinal = language != "english" ? n + "." : (n % 100) is 11 or 12 or 13 ? n + "th"
                : (n % 10) switch { 1 => n + "st", 2 => n + "nd", 3 => n + "rd", _ => n + "th" };
            return ordinal + "~" + String(s, language, "edition");
        }
        #endregion

        #region ---- names ----
        // A name list in the bibliography: shortened past maxnames, joined by the delimiters, "and others" as the string.
        private static string? Names(string? field, TexBiblatexSettings s, string language)
        {
            if (field == null) return null;
            List<string> names = TexBibliography.SplitNames(field);
            bool others = names.Count > 0 && names[^1].Equals("others", StringComparison.OrdinalIgnoreCase);
            if (others) names.RemoveAt(names.Count - 1);
            int shown = names.Count;
            if (names.Count > s.maxBibNames)
            {
                shown = Math.Min(s.minBibNames, names.Count);
                others = true;
            }
            if (shown == 0) return null;

            StringBuilder text = new StringBuilder(64);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0)
                {
                    if (i < shown - 1 || others) text.Append(s.delims.TryGetValue("multinamedelim", out string? multi) ? multi : ", ");
                    else if (s.delims.TryGetValue("finalnamedelim", out string? final)) text.Append(final);
                    else text.Append(shown > 2 && language == "english" ? "," : "").Append(' ').Append(String(s, language, "and")).Append(' ');
                }
                bool reversed = s.nameFormat == "family-given" || s.nameFormat == "family-given/given-family" && i == 0;
                text.Append(Display(TexBibliography.ParseName(names[i]), reversed, s.givenInits));
            }
            if (others) text.Append(s.delims.TryGetValue("andothersdelim", out string? before) ? before : " ").Append(String(s, language, "andothers"));
            return text.ToString();
        }

        // editors with editor or editors after them, for a work with no author
        private static string? EditorNames(TexBibEntry e, TexBiblatexSettings s, string language)
        {
            if (Names(e["editor"], s, language) is not string editors) return null;
            return editors + ", " + String(s, language, TexBibliography.SplitNames(e["editor"]!).Count > 1 ? "editors" : "editor");
        }

        // given-family: Given von Family Jr; family-given: Family Jr, Given von
        private static string Display(TexBibliography.Name n, bool reversed, bool initials)
        {
            StringBuilder given = new StringBuilder(32);
            for (int i = 0; i < n.first.Count; i++)
            {
                if (i > 0) given.Append(initials ? '~' : ' ');
                given.Append(initials ? TexBibliography.Initial(n.first[i]) : n.first[i]);
            }
            string von = string.Join(" ", n.von), family = string.Join(" ", n.last), jr = string.Join(" ", n.jr);

            StringBuilder text = new StringBuilder(64);
            if (reversed)
            {
                text.Append(family);
                if (jr.Length > 0) text.Append(' ').Append(jr);
                if (given.Length > 0 || von.Length > 0) text.Append(',');
                if (given.Length > 0) text.Append(' ').Append(given);
                if (von.Length > 0) text.Append(' ').Append(von);
                return text.ToString();
            }
            if (given.Length > 0) text.Append(given).Append(' ');
            if (von.Length > 0) text.Append(von).Append(' ');
            text.Append(family);
            if (jr.Length > 0) text.Append(' ').Append(jr);
            return text.ToString();
        }
        #endregion

        #region ---- labels, sorting and dates ----
        // labelalpha: shorthand whole, else the label field or the names, then the year's last two digits.
        private static string Label(TexBibEntry e, TexBiblatexSettings s)
        {
            if (e["shorthand"] is string shorthand) return shorthand;
            string year = Year(e) ?? "";
            string tail = year.Length > 2 ? year[^2..] : year;
            if (e["label"] is string given) return given + tail;

            string? field = e["author"] ?? e["editor"] ?? e["translator"];
            if (field == null) return tail;
            List<string> names = TexBibliography.SplitNames(field);
            bool others = names.Count > 0 && names[^1].Equals("others", StringComparison.OrdinalIgnoreCase);
            if (others) names.RemoveAt(names.Count - 1);
            int shown = names.Count;
            if (names.Count > s.maxAlphaNames)
            {
                shown = Math.Min(s.minAlphaNames, names.Count);
                others = true;
            }

            StringBuilder label = new StringBuilder(8);
            for (int i = 0; i < shown; i++)
            {
                string family = TexBibliography.Purify(string.Join(" ", TexBibliography.ParseName(names[i]).last), true).Replace(" ", "");
                int width = shown == 1 && !others ? 3 : 1;
                label.Append(family, 0, Math.Min(width, family.Length));
            }
            if (others) label.Append("\\labelalphaothers ");
            return label.Append(tail).ToString();
        }

        // sortname: each name's family, given, prefix and suffix; the title when there is no name
        private static string SortName(TexBibEntry e)
        {
            if (e["sortkey"] is string key) return key;
            string? field = e["sortname"] ?? e["author"] ?? e["editor"] ?? e["translator"];
            if (field == null) return e["sorttitle"] ?? e["title"] ?? "";
            List<string> names = TexBibliography.SplitNames(field);
            StringBuilder sort = new StringBuilder(64);
            for (int i = 0; i < names.Count; i++)
            {
                TexBibliography.Name n = TexBibliography.ParseName(names[i]);
                if (i > 0) sort.Append("  ");
                sort.Append(TexBibliography.Purify(string.Join(" ", n.last) + " " + string.Join(" ", n.first) + " " + string.Join(" ", n.von) + " " + string.Join(" ", n.jr), true));
            }
            return sort.ToString();
        }

        // the year from year, or from date's yyyy-mm-dd
        private static string? Year(TexBibEntry e)
        {
            string? year = e["year"] ?? e["date"];
            if (year == null) return null;
            int cut = year.IndexOfAny(new[] { '-', '/' }, 1);
            return TexBibliography.Purify(cut > 0 ? year[..cut] : year, true).Trim();
        }

        // the date in the date option's form: year, iso, or month and year
        private static string? Date(TexBibEntry e, TexBiblatexSettings s)
        {
            if (Year(e) is not string year) return null;
            if (s.date == "year") return year;
            (int month, int day) = MonthDay(e["date"], e["month"]);
            if (s.date is "iso" or "iso8601")
                return month == 0 ? year : day == 0 ? $"{year}-{month:00}" : $"{year}-{month:00}-{day:00}";
            return month == 0 ? year : englishMonths[month - 1] + " " + year;
        }

        // urldate: iso or year, else day/month/year
        private static string? UrlDate(TexBibEntry e, TexBiblatexSettings s)
        {
            if (e["urldate"] is not string urlDate) return null;
            string[] parts = urlDate.Split('-');
            if (s.urlDate == "year") return parts[0];
            if (s.urlDate is "iso" or "iso8601") return urlDate.Trim();
            return parts.Length == 3 ? parts[2] + "/" + parts[1] + "/" + parts[0] : urlDate.Trim();
        }

        private static (int month, int day) MonthDay(string? date, string? month)
        {
            if (date != null)
            {
                string[] parts = date.Split('-');
                int m = parts.Length > 1 ? Count(parts[1], 0) : 0;
                return (m is >= 1 and <= 12 ? m : 0, parts.Length > 2 ? Count(parts[2], 0) : 0);
            }
            if (month == null) return (0, 0);
            if (int.TryParse(month, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) return (number is >= 1 and <= 12 ? number : 0, 0);
            for (int i = 0; i < englishMonths.Length; i++)
                if (englishMonths[i].StartsWith(month.Trim(), StringComparison.OrdinalIgnoreCase)) return (i + 1, 0);
            return (0, 0);
        }
        #endregion

        #region ---- units ----
        private static void Print(ref Units u, string? field)
        {
            u.printed = !string.IsNullOrEmpty(field);
            if (!u.printed) return;
            if (u.text.Length > 0)
            {
                string pending = u.pending;
                if (pending.Length > 0 && pending[0] == '.' && EndsWith(u.text, ".?!")) pending = pending[1..];
                else if (pending.Length > 0 && pending[0] is ',' or ':' or ';' && EndsWith(u.text, "?!")) pending = pending[1..];
                u.text.Append(pending);
            }
            u.text.Append(field);
            u.pending = "";
            u.sentence = false;
        }

        private static void NewUnit(ref Units u) => SetUnit(ref u, ". ", true);

        private static void SetUnit(ref Units u, string delimiter, bool sentence = false)
        {
            if (u.text.Length == 0) return;
            if (u.pending == ". " && delimiter == ". ") return;
            u.pending = delimiter;
            u.sentence = sentence;
        }

        // \setunit*: only when the last field printed
        private static void SetUnitStar(ref Units u, string delimiter)
        {
            if (u.printed) SetUnit(ref u, delimiter);
        }

        // A string, capitalized where it starts a sentence.
        private static string Str(ref Units u, TexBiblatexSettings s, string language, string key)
        {
            string text = String(s, language, key);
            if ((u.text.Length > 0 && !u.sentence) || text.Length == 0 || text[0] == '\\') return text;
            return char.ToUpper(text[0], Culture(language)) + text[1..];
        }

        // Whether the text ends in one of the marks, past closing braces and quotes.
        private static bool EndsWith(StringBuilder text, string marks)
        {
            for (int i = text.Length - 1; i >= 0; i--)
            {
                char c = text[i];
                if (c is '}' or '”' or '“' or '\'') continue;
                return marks.IndexOf(c) >= 0;
            }
            return false;
        }

        // a URL's % and # escaped, as \url reads them back
        private static string Escaped(string url) => url.Replace("%", "\\%").Replace("#", "\\#");
        #endregion
    }
}
