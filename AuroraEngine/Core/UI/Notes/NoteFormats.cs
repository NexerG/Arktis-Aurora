using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.Core.UI.Text;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Notes
{
    // Markdown <-> <Document> tree: one block per line, the subset the note model can hold.
    public static class MarkdownFormat
    {
        // line prefixes
        private static readonly Regex heading = new Regex(@"^(#{1,6}) (.*)$");
        private static readonly Regex task = new Regex(@"^([ \t]*)- \[([ xX])\](?: (.*))?$");
        private static readonly Regex bullet = new Regex(@"^([ \t]*)- (.*)$");
        private static readonly Regex ordered = new Regex(@"^([ \t]*)(\d{1,9})[.)] (.*)$");
        private static readonly Regex lettered = new Regex(@"^([ \t]*)([a-zA-Z]{1,15})([.)])( +)(.*)$");
        private static readonly Regex rule = new Regex(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$");
        private static readonly Regex fenceLine = new Regex(@"^(`{3,}|~{3,})(.*)$");
        private const string fence = "```";
        private const string quote = "> ";

        private const string escapable = "\\`*_~#>-+[]=<.)$";

        // inline HTML Obsidian renders, read and written for what Markdown has no syntax for
        private static readonly Regex htmlTag = new Regex(@"\G<(/?)(u|mark|span)((?:\s+style=""[^""]*"")?)\s*>", RegexOptions.IgnoreCase);
        private static readonly Regex cssColor = new Regex(@"(?<![-\w])color\s*:\s*(#[0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?)");
        private static readonly Regex picture = new Regex(@"\G!\[[^\]|]*(?:\|(\d+)(?:x(\d+))?)?\]\((?:<([^>]*)>|([^)\s]+))\)");
        private static readonly Regex embed = new Regex(@"\G!\[\[(.*?)\]\]");
        private static readonly Regex htmlPicture =new Regex(@"\G<img\s([^>]*?)/?>", RegexOptions.IgnoreCase);
        private static readonly Regex htmlAttribute = new Regex(@"([\w-]+)\s*=\s*""([^""]*)""");
        private static readonly Regex cssBackground =new Regex(@"(?<![-\w])background(?:-color)?\s*:\s*(#[0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?)");

        // what ==text== stands for
        public const string DefaultHighlightHex = "#FFF3A3";

        // list markers written as numbers
        private static readonly string[] numbered = { "Decimal", "UpperAlpha", "LowerAlpha", "LowerRoman", "UpperRoman" };

        // frontmatter key -> the tree attribute it stands for, on <Document>, <DocumentLayout> or <Page>
        private static readonly (string key, string element, string attribute)[] properties =
        {
            ("Palette", "Document", "Palette"),
            ("Created", "Document", "Created"),
            ("Modified", "Document", "Modified"),
            ("ReadOnly", "Document", "ReadOnly"),
            ("LineHeight", "DocumentLayout", "LineHeight"),
            ("BlockSpacing", "DocumentLayout", "BlockSpacing"),
            ("ListIndent", "DocumentLayout", "ListIndent"),
            ("PageMode", "Page", "Mode"),
            ("PageSize", "Page", "Size"),
            ("Landscape", "Page", "Landscape"),
            ("PageWidth", "Page", "Width"),
            ("PageHeight", "Page", "Height"),
            ("MarginTop", "Page", "MarginTop"),
            ("MarginBottom", "Page", "MarginBottom"),
            ("MarginLeft", "Page", "MarginLeft"),
            ("MarginRight", "Page", "MarginRight")
        };

        // frontmatter key for the per-level list markers, <ListLevel> children of <DocumentLayout>
        private const string listMarkersKey = "ListMarkers";

        // Whether a frontmatter key is one the note reads into its own properties.
        public static bool IsProperty(string key) =>
            string.Equals(key, listMarkersKey, StringComparison.OrdinalIgnoreCase)
            || properties.Any(p => string.Equals(p.key, key, StringComparison.OrdinalIgnoreCase));

        #region ---- read ----
        public static XElement Read(string text, string name)
        {
            XElement root = new XElement("Document", new XAttribute("Name", name));
            List<int> listWidths = new List<int>();

            if (Frontmatter.Split(text, out string front, out string body))
            {
                root.SetAttributeValue("Frontmatter", front);
                ReadProperties(root, front);
                text = body;
            }
            string? openFence = null;
            string? language = null;
            List<string>? openMath = null;

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');

                if (openFence != null && ClosesFence(line, openFence))
                {
                    openFence = null;
                    continue;
                }

                Match match;
                if (openFence == null && (match = fenceLine.Match(line)).Success
                    && !(match.Groups[1].Value[0] == '`' && match.Groups[2].Value.Contains('`')))
                {
                    openFence = match.Groups[1].Value;
                    language = match.Groups[2].Value.Trim();
                    continue;
                }

                if (openFence != null)
                {
                    XElement code = Block("Code", new List<XElement> { PlainRun(line) });
                    if (!string.IsNullOrEmpty(language)) code.SetAttributeValue("Language", language);
                    root.Add(code);
                    continue;
                }

                if (openMath != null)
                {
                    if (line.Trim() == "$$")
                    {
                        root.Add(Block(null, new List<XElement> { MathRun(string.Join("\n", openMath), true) }));
                        openMath = null;
                    }
                    else openMath.Add(line);
                    continue;
                }

                if (line.Trim() == "$$")
                {
                    listWidths.Clear();
                    openMath = new List<string>();
                    continue;
                }

                if (rule.IsMatch(line))
                {
                    listWidths.Clear();
                    root.Add(Block("Rule", new List<XElement>()));
                    continue;
                }

                if ((match = task.Match(line)).Success || (match = bullet.Match(line)).Success)
                {
                    bool isTask = match.Groups.Count == 4;
                    XElement block = Block(null, ParseInline(isTask ? match.Groups[3].Value : match.Groups[2].Value));
                    block.SetAttributeValue("List", isTask ? "Task" : "Bullet");

                    int level = ListLevel(listWidths, match.Groups[1].Value);
                    if (level > 0) block.SetAttributeValue("Level", level);
                    if (isTask && match.Groups[2].Value != " ") block.SetAttributeValue("Checked", "true");

                    root.Add(block);
                    continue;
                }

                if ((match = ordered.Match(line)).Success)
                {
                    XElement block = Block(null, ParseInline(match.Groups[3].Value));
                    block.SetAttributeValue("List", "Bullet");
                    block.SetAttributeValue("Marker", "Decimal");
                    block.SetAttributeValue("Start", int.Parse(match.Groups[2].Value));

                    int level = ListLevel(listWidths, match.Groups[1].Value);
                    if (level > 0) block.SetAttributeValue("Level", level);

                    root.Add(block);
                    continue;
                }

                if ((match = lettered.Match(line)).Success && LetteredGap(match) is int gap)
                {
                    int level = ListLevel(listWidths, match.Groups[1].Value);
                    XElement? previous = PreviousItem(root, level);
                    string token = match.Groups[2].Value;
                    string? marker = LetteredMarker(token, (string?)previous?.Attribute("Marker"), previous?.Annotation<string>());
                    if (marker != null)
                    {
                        string rest = match.Groups[4].Value[gap..] + match.Groups[5].Value;
                        XElement block = Block(null, ParseInline(rest));
                        block.SetAttributeValue("List", "Bullet");
                        block.SetAttributeValue("Marker", marker);
                        if (level > 0) block.SetAttributeValue("Level", level);
                        block.AddAnnotation(token);

                        root.Add(block);
                        continue;
                    }
                }

                listWidths.Clear();

                if ((match = heading.Match(line)).Success)
                    root.Add(Block("Heading" + match.Groups[1].Length, ParseInline(match.Groups[2].Value)));
                else if (line.StartsWith(quote))
                    root.Add(Block("Quote", ParseInline(line[quote.Length..])));
                else
                    root.Add(Block(null, ParseInline(line)));
            }

            if (openMath != null) root.Add(Block(null, new List<XElement> { MathRun(string.Join("\n", openMath), true) }));
            KeepListStarts(root);
            return root;
        }

        // Keeps the number a list's first item was written with when it is not 1, and drops the rest.
        private static void KeepListStarts(XElement root)
        {
            List<(int count, string? marker)> counters = new List<(int count, string? marker)>();
            foreach (XElement block in root.Elements())
            {
                if (block.Name.LocalName != "Block") continue;

                int? start = (int?)block.Attribute("Start");
                block.SetAttributeValue("Start", null);
                if (Count(counters, block) != 1 || start is null or 1) continue;

                block.SetAttributeValue("Start", start);
                counters[^1] = (start.Value, counters[^1].marker);
            }
        }

        private static void ReadProperties(XElement root, string block)
        {
            foreach ((string key, string element, string attribute) in properties)
            {
                string? value = Frontmatter.Get(block, key);
                if (value != null) PropertyHolder(root, element, true)!.SetAttributeValue(attribute, value);
            }

            string? markers = Frontmatter.Get(block, listMarkersKey);
            if (markers == null) return;

            XElement layout = PropertyHolder(root, "DocumentLayout", true)!;
            foreach (string name in markers.Trim('[', ']').Split(',').Select(m => m.Trim()))
                if (Enum.TryParse(name, true, out ListMarker marker))
                    layout.Add(new XElement("ListLevel", new XAttribute("Marker", marker)));
        }

        // The element a property lives on, made on the way when create is set.
        private static XElement? PropertyHolder(XElement root, string element, bool create)
        {
            if (element == "Document") return root;

            XElement? layout = root.Elements().FirstOrDefault(e => e.Name.LocalName == "DocumentLayout");
            if (layout == null && create) root.AddFirst(layout = new XElement("DocumentLayout"));
            if (layout == null || element == "DocumentLayout") return layout;

            XElement? page = layout.Elements().FirstOrDefault(e => e.Name.LocalName == "Page");
            if (page == null && create) layout.Add(page = new XElement("Page"));
            return page;
        }

        // Nesting depth from the indent, relative to the list items above it.
        private static int ListLevel(List<int> widths, string indent)
        {
            int width = 0;
            foreach (char c in indent) width += c == '\t' ? 4 : 1;

            while (widths.Count > 0 && widths[^1] > width) widths.RemoveAt(widths.Count - 1);
            if (widths.Count > 0 && widths[^1] == width) return widths.Count - 1;

            widths.Add(width);
            return widths.Count - 1;
        }

        // The same character as the opening fence, at least as many, and nothing else.
        private static bool ClosesFence(string line, string openFence)
        {
            string trimmed = line.Trim();
            return trimmed.Length >= openFence.Length && trimmed.All(c => c == openFence[0]);
        }

        // Spaces the marker takes after it: two after a capital and a period, so "A. Smith" stays prose.
        private static int? LetteredGap(Match match)
        {
            bool upperPeriod = char.IsUpper(match.Groups[2].Value[0]) && match.Groups[3].Value == ".";
            int gap = upperPeriod ? 2 : 1;
            return match.Groups[4].Length >= gap ? gap : null;
        }

        // The marker a lettered item reads as, or null when the token is prose.
        private static string? LetteredMarker(string token, string? previous, string? previousToken)
        {
            bool upper = token.All(char.IsUpper);
            if (!upper && !token.All(char.IsLower)) return null;

            string alpha = upper ? "UpperAlpha" : "LowerAlpha";
            string roman = upper ? "UpperRoman" : "LowerRoman";

            if (previous == alpha && previousToken != null && token == NextLetters(previousToken)) return alpha;
            if (IsRoman(token) && (previous == roman || token.Length > 1 || token is "i" or "I")) return roman;
            return token.Length == 1 ? alpha : null;
        }

        // a..z, aa.. counted on by one, case kept
        private static string NextLetters(string token)
        {
            char[] letters = token.ToCharArray();
            char first = char.IsUpper(token[0]) ? 'A' : 'a';
            for (int i = letters.Length - 1; i >= 0; i--)
            {
                if (letters[i] != first + 25) { letters[i]++; return new string(letters); }
                letters[i] = first;
            }
            return first + new string(letters);
        }

        // The item above at this level, while the list it belongs to is unbroken.
        private static XElement? PreviousItem(XElement root, int level)
        {
            foreach (XElement block in root.Elements().Reverse())
            {
                if (block.Name.LocalName != "Block" || block.Attribute("List") == null) return null;

                int at = (int?)block.Attribute("Level") ?? 0;
                if (at < level) return null;
                if (at == level) return block;
            }
            return null;
        }

        private static bool IsRoman(string token)
        {
            string lower = token.ToLowerInvariant();
            int total = 0, last = 0;
            for (int i = lower.Length - 1; i >= 0; i--)
            {
                int value = lower[i] switch { 'i' => 1, 'v' => 5, 'x' => 10, 'l' => 50, 'c' => 100, 'd' => 500, 'm' => 1000, _ => 0 };
                if (value == 0) return false;
                total += value < last ? -value : value;
                last = Math.Max(last, value);
            }
            return total > 0 && ListMarkers.Format(total, ListMarker.LowerRoman) == lower + ".";
        }

        private static XElement Block(string? styling, List<XElement> runs)
        {
            XElement block = new XElement("Block");
            if (styling != null) block.SetAttributeValue("StylingType", styling);

            if (runs.Count == 0) runs.Add(PlainRun(string.Empty));
            block.Add(runs);
            return block;
        }

        private static XElement PlainRun(string text)
        {
            XElement run = new XElement("Run");
            if (text.Length > 0) run.SetAttributeValue("Text", text);
            return run;
        }

        private static List<XElement> ParseInline(string s)
        {
            List<XElement> runs = new List<XElement>();
            StringBuilder pending = new StringBuilder();
            bool bold = false, italic = false, strike = false, marked = false;
            string? color = null, highlight = null;

            // open HTML tags, each with the colours it replaced
            Stack<(string tag, string? color, string? highlight)> tags = new Stack<(string, string?, string?)>();

            void Flush(bool code)
            {
                if (pending.Length == 0) return;

                XElement run = PlainRun(pending.ToString());
                if (bold) run.SetAttributeValue("Bold", "true");
                if (italic) run.SetAttributeValue("Italic", "true");
                if (strike) run.SetAttributeValue("Strikethrough", "true");
                if (tags.Any(t => t.tag == "u")) run.SetAttributeValue("Underline", "true");
                if (color != null) run.SetAttributeValue("ColorHex", color);
                string? shown = highlight ?? (marked ? DefaultHighlightHex : null);
                if (shown != null) run.SetAttributeValue("HighlightHex", shown);
                if (code) run.SetAttributeValue("StylingType", "Code");
                runs.Add(run);
                pending.Clear();
            }

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];

                if (c == '\\' && i + 1 < s.Length && escapable.IndexOf(s[i + 1]) >= 0)
                {
                    pending.Append(s[++i]);
                    continue;
                }

                if (c == '<' && htmlTag.Match(s, i) is { Success: true } tag)
                {
                    string name = tag.Groups[2].Value.ToLowerInvariant();
                    bool closing = tag.Groups[1].Length > 0;

                    if (closing && tags.Count > 0 && tags.Peek().tag == name)
                    {
                        Flush(false);
                        (_, color, highlight) = tags.Pop();
                        i += tag.Length - 1;
                        continue;
                    }

                    if (!closing && s.IndexOf($"</{name}>", i, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Flush(false);
                        tags.Push((name, color, highlight));

                        string style = tag.Groups[3].Value;
                        if (name == "mark") highlight = DefaultHighlightHex;
                        if (cssBackground.Match(style) is { Success: true } background) highlight = Hex6(background.Groups[1].Value);
                        if (cssColor.Match(style) is { Success: true } foreground) color = Hex6(foreground.Groups[1].Value);

                        i += tag.Length - 1;
                        continue;
                    }
                }

                if (c == '!' && embed.Match(s, i) is { Success: true } link && SheetLinks.IsLink(link.Groups[1].Value))
                {
                    Flush(false);
                    runs.Add(new XElement("Run", new XAttribute("Sheet", link.Groups[1].Value)));
                    i += link.Length - 1;
                    continue;
                }

                if (c == '!' && picture.Match(s, i) is { Success: true } image)
                {
                    Flush(false);
                    XElement run = new XElement("Run");
                    string source = image.Groups[3].Success ? image.Groups[3].Value : Uri.UnescapeDataString(image.Groups[4].Value);
                    run.SetAttributeValue("Image", source);
                    if (image.Groups[1].Success) run.SetAttributeValue("Width", image.Groups[1].Value);
                    if (image.Groups[2].Success) run.SetAttributeValue("Height", image.Groups[2].Value);
                    runs.Add(run);
                    i += image.Length - 1;
                    continue;
                }

                if (c == '<' && htmlPicture.Match(s, i) is { Success: true } img && ReadHtmlPicture(img.Groups[1].Value) is XElement html)
                {
                    Flush(false);
                    runs.Add(html);
                    i += img.Length - 1;
                    continue;
                }

                if (c == '$' && MathAt(s, i, out string tex, out bool display, out int mathEnd))
                {
                    Flush(false);
                    runs.Add(MathRun(tex, display));
                    i = mathEnd;
                    continue;
                }

                if (c == '`')
                {
                    int close = s.IndexOf('`', i + 1);
                    if (close > i + 1)
                    {
                        Flush(false);
                        pending.Append(s, i + 1, close - i - 1);
                        Flush(true);
                        i = close;
                        continue;
                    }
                }
                else if (c == '~' && At(s, i, "~~") && (strike || s.IndexOf("~~", i + 2) >= 0))
                {
                    Flush(false);
                    strike = !strike;
                    i++;
                    continue;
                }
                else if (c == '=' && At(s, i, "==") && (marked || s.IndexOf("==", i + 2) >= 0))
                {
                    Flush(false);
                    marked = !marked;
                    i++;
                    continue;
                }
                else if (c == '*' && At(s, i, "**") && (bold || s.IndexOf("**", i + 2) >= 0))
                {
                    Flush(false);
                    bold = !bold;
                    i++;
                    continue;
                }
                else if ((c == '*' && (italic || s.IndexOf('*', i + 1) >= 0))
                      || (c == '_' && (italic ? !WordAt(s, i + 1) : !WordAt(s, i - 1) && s.IndexOf('_', i + 1) >= 0)))
                {
                    Flush(false);
                    italic = !italic;
                    continue;
                }

                pending.Append(c);
            }

            Flush(false);
            return runs;
        }

        // <img src width height data-wrap data-x data-y data-rotate data-collision> as a picture run; null without a src.
        private static XElement? ReadHtmlPicture(string attributes)
        {
            Dictionary<string, string> found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match a in htmlAttribute.Matches(attributes))
                found[a.Groups[1].Value] = a.Groups[2].Value;
            if (!found.TryGetValue("src", out string? src)) return null;

            XElement run = new XElement("Run", new XAttribute("Image", Uri.UnescapeDataString(src)));
            if (found.TryGetValue("width", out string? width)) run.SetAttributeValue("Width", width);
            if (found.TryGetValue("height", out string? height)) run.SetAttributeValue("Height", height);
            if (found.TryGetValue("data-wrap", out string? wrap) && Enum.TryParse(wrap, true, out PictureWrap parsed) && parsed != PictureWrap.Inline)
            {
                run.SetAttributeValue("Wrap", parsed.ToString());
                if (found.TryGetValue("data-x", out string? x)) run.SetAttributeValue("X", x);
                if (found.TryGetValue("data-y", out string? y)) run.SetAttributeValue("Y", y);
                if (found.TryGetValue("data-collision", out string? collision) && Enum.TryParse(collision, true, out PictureCollision shape)
                    && shape != PictureCollision.Box)
                    run.SetAttributeValue("Collision", shape.ToString());
            }
            if (found.TryGetValue("data-rotate", out string? rotate)) run.SetAttributeValue("Rotation", rotate);
            return run;
        }

        // $…$ or $$…$$ at i, by Obsidian's rules; end is the closing fence's last character.
        private static bool MathAt(string s, int i, out string tex, out bool display, out int end)
        {
            tex = string.Empty;
            end = i;
            display = At(s, i, "$$");
            if (display)
            {
                int close = s.IndexOf("$$", i + 2, StringComparison.Ordinal);
                if (close <= i + 2) return false;
                tex = s.Substring(i + 2, close - i - 2);
                end = close + 1;
                return true;
            }

            if (i + 1 >= s.Length || char.IsWhiteSpace(s[i + 1])) return false;
            for (int close = s.IndexOf('$', i + 1); close > i + 1; close = s.IndexOf('$', close + 1))
            {
                if (s[close - 1] == '\\' || char.IsWhiteSpace(s[close - 1])) continue;
                if (close + 1 < s.Length && char.IsAsciiDigit(s[close + 1])) continue;
                tex = s.Substring(i + 1, close - i - 1);
                end = close;
                return true;
            }
            return false;
        }

        private static XElement MathRun(string tex, bool display)
        {
            XElement run = new XElement("Run", new XAttribute("Math", tex));
            if (display) run.SetAttributeValue("Display", "true");
            return run;
        }

        private static bool At(string s, int i, string marker) => string.CompareOrdinal(s, i, marker, 0, marker.Length) == 0;

        // The note keeps #RRGGBB; an Obsidian alpha pair is dropped.
        private static string Hex6(string hex) => hex.Length > 7 ? hex[..7].ToUpperInvariant() : hex.ToUpperInvariant();

        private static bool WordAt(string s, int i) => i >= 0 && i < s.Length && char.IsLetterOrDigit(s[i]);
        #endregion

        #region ---- write ----
        public static string Write(XElement document)
        {
            List<string> lines = new List<string>();
            List<(int count, string? marker)> counters = new List<(int, string?)>();
            int fenceAt = -1;
            int longestTicks = 0;
            bool afterList = false;
            string? fenceLanguage = null;

            // the fence outgrows any line of backticks inside it
            void CloseFence()
            {
                string ticks = new string('`', Math.Max(fence.Length, longestTicks + 1));
                lines[fenceAt] = ticks + fenceLanguage;
                lines.Add(ticks);
                fenceAt = -1;
                longestTicks = 0;
            }

            foreach (XElement block in document.Elements())
            {
                if (block.Name.LocalName != "Block") continue;

                string styling = (string?)block.Attribute("StylingType") ?? "Text";
                string? language = (string?)block.Attribute("Language");
                List<XElement> runs = block.Elements().ToList();
                int number = Count(counters, block);
                bool code = styling == "Code";

                if (fenceAt >= 0 && (!code || language != fenceLanguage)) CloseFence();
                if (code && fenceAt < 0)
                {
                    fenceAt = lines.Count;
                    lines.Add(fence);
                    fenceLanguage = language;
                }

                if (styling == "Rule") lines.Add(lines.Count == 0 ? "***" : "---");
                else if (styling == "Text" && block.Attribute("List") == null && runs.Count == 1
                         && (string?)runs[0].Attribute("Math") is string display && (bool?)runs[0].Attribute("Display") == true)
                {
                    lines.Add("$$");
                    lines.Add(display);
                    lines.Add("$$");
                }
                else if (fenceAt >= 0)
                {
                    string line = Flatten(runs, out _);
                    string trimmed = line.Trim();
                    if (trimmed.Length > 0 && trimmed.All(c => c == '`')) longestTicks = Math.Max(longestTicks, trimmed.Length);
                    lines.Add(line);
                }
                else lines.Add(BlockLine(block, styling, runs, number, afterList));

                afterList = block.Attribute("List") != null;
            }

            if (fenceAt >= 0) CloseFence();

            string? front = (string?)document.Attribute("Frontmatter");
            bool customPage = (string?)PropertyHolder(document, "Page", false)?.Attribute("Size") == nameof(PageSize.Custom);
            foreach ((string key, string element, string attribute) in properties)
            {
                bool paperSize = element == "Page" && attribute is "Width" or "Height";
                string? value = paperSize && !customPage ? null : (string?)PropertyHolder(document, element, false)?.Attribute(attribute);
                front = Frontmatter.Set(front, key, value);
            }

            List<string> markers = PropertyHolder(document, "DocumentLayout", false)?.Elements()
                .Where(e => e.Name.LocalName == "ListLevel")
                .Select(e => (string?)e.Attribute("Marker") ?? nameof(ListMarker.Disc)).ToList() ?? new List<string>();
            string? existing = Frontmatter.Get(front, listMarkersKey);
            string joined = string.Join(", ", markers);
            front = Frontmatter.Set(front, listMarkersKey,
                markers.Count == 0 ? null : existing != null && !existing.StartsWith('[') ? joined : "[" + joined + "]");

            if (front != null) lines.Insert(0, front);
            else if (lines.Count > 0 && (lines[0] == "---" || lines[0] == "+++")) lines[0] = "\\" + lines[0];

            return string.Join("\n", lines);
        }

        // A list item's number at its level, counted the way DocumentControl.RenumberLists counts.
        private static int Count(List<(int count, string? marker)> counters, XElement block)
        {
            string? list = (string?)block.Attribute("List");
            int level = (int?)block.Attribute("Level") ?? 0;

            if (list == null || list == "None")
            {
                counters.Clear();
                return 0;
            }

            if (counters.Count > level + 1) counters.RemoveRange(level + 1, counters.Count - level - 1);
            if (list == "Task" || list == "SortedTask")
            {
                if (counters.Count > level) counters.RemoveRange(level, counters.Count - level);
                return 0;
            }

            string? marker = (string?)block.Attribute("Marker");
            while (counters.Count <= level) counters.Add((0, marker));
            int number = (int?)block.Attribute("Start") ?? (counters[level].marker == marker ? counters[level].count + 1 : 1);
            counters[level] = (number, marker);
            return number;
        }

        private static string BlockLine(XElement block, string styling, List<XElement> runs, int number, bool afterList)
        {
            string content = WriteInline(runs);
            string? list = (string?)block.Attribute("List");

            if (list == "Bullet" || list == "Task" || list == "SortedTask")
            {
                string indent = new string('\t', (int?)block.Attribute("Level") ?? 0);
                if (list == "Task" || list == "SortedTask")
                    return indent + ((bool?)block.Attribute("Checked") == true ? "- [x] " : "- [ ] ") + content;

                string? marker = (string?)block.Attribute("Marker");
                if (marker == "Decimal") return indent + number + ". " + content;
                if (numbered.Contains(marker))
                    return indent + ListMarkers.Format(number, Enum.Parse<ListMarker>(marker!))
                           + (marker!.StartsWith("Upper") ? "  " : " ") + content;

                return indent + "- " + (task.IsMatch("- " + content) ? "\\" + content : content);
            }

            if (styling.StartsWith("Heading") && int.TryParse(styling["Heading".Length..], out int level))
                return new string('#', level) + " " + content;

            if (styling == "Quote") return quote + content;

            // "1. " escapes its dot, since a backslash before a digit escapes nothing
            if (ordered.Match(content) is { Success: true } numberLike)
                return content.Insert(numberLike.Groups[1].Length + numberLike.Groups[2].Length, "\\");

            // "a. " and "iv) " escape their delimiter the same way
            if (lettered.Match(content) is { Success: true } letterLike && LetteredGap(letterLike) != null)
            {
                string token = letterLike.Groups[2].Value;
                if (LetteredMarker(token, null, null) != null || (afterList && (token.All(char.IsUpper) || token.All(char.IsLower))))
                    return content.Insert(letterLike.Groups[1].Length + token.Length, "\\");
            }

            bool structural = heading.IsMatch(content) || bullet.IsMatch(content) || rule.IsMatch(content)
                              || content.StartsWith(quote) || content.StartsWith(fence) || content.StartsWith("~~~");
            if (!structural) return content;

            int at = content.Length - content.TrimStart(' ', '\t').Length;
            return content.Insert(at, "\\");
        }

        // Unescaped when it reads back as the same runs, escaped when it would not.
        private static string WriteInline(List<XElement> runs)
        {
            string plain = Compose(runs, false);
            string text = Flatten(runs, out List<string> styles);
            string reread = Flatten(ParseInline(plain), out List<string> rereadStyles);

            return reread == text && rereadStyles.SequenceEqual(styles) ? plain : Compose(runs, true);
        }

        private static string Compose(List<XElement> runs, bool escape)
        {
            StringBuilder line = new StringBuilder();
            List<string> open = new List<string>();

            foreach (XElement run in runs)
            {
                string? math = (string?)run.Attribute("Math");
                if (math != null)
                {
                    string mathFence = (bool?)run.Attribute("Display") == true ? "$$" : "$";
                    line.Append(mathFence).Append(math).Append(mathFence);
                    continue;
                }

                string? sheet = (string?)run.Attribute("Sheet");
                if (sheet != null)
                {
                    line.Append("![[").Append(sheet).Append("]]");
                    continue;
                }

                string? image = (string?)run.Attribute("Image");
                if (image != null)
                {
                    string? wrap = (string?)run.Attribute("Wrap");
                    line.Append(wrap == null && run.Attribute("Rotation") == null
                        ? PictureMarkdown(image, (string?)run.Attribute("Width"), (string?)run.Attribute("Height"))
                        : PictureHtml(image, run, wrap));
                    continue;
                }

                string text = (string?)run.Attribute("Text") ?? string.Empty;
                if (text.Length == 0) continue;

                List<string> want = new List<string>();
                string? highlight = (string?)run.Attribute("HighlightHex");
                string? color = (string?)run.Attribute("ColorHex");
                if (highlight != null)
                    want.Add(string.Equals(highlight, DefaultHighlightHex, StringComparison.OrdinalIgnoreCase)
                        ? "==" : $"<mark style=\"background:{highlight}\">");
                if (color != null) want.Add($"<span style=\"color:{color}\">");
                if ((bool?)run.Attribute("Underline") == true) want.Add("<u>");
                if ((bool?)run.Attribute("Strikethrough") == true) want.Add("~~");
                if ((bool?)run.Attribute("Bold") == true) want.Add("**");
                if ((bool?)run.Attribute("Italic") == true) want.Add("*");

                int keep = 0;
                while (keep < open.Count && want.Contains(open[keep])) keep++;
                for (int i = open.Count - 1; i >= keep; i--) line.Append(Closing(open[i]));
                open.RemoveRange(keep, open.Count - keep);

                foreach (string marker in want)
                    if (!open.Contains(marker))
                    {
                        line.Append(marker);
                        open.Add(marker);
                    }

                if ((string?)run.Attribute("StylingType") == "Code")
                    line.Append('`').Append(text).Append('`');
                else if (escape)
                    for (int i = 0; i < text.Length; i++)
                    {
                        char c = text[i];
                        bool needs = c == '\\'
                            ? i + 1 == text.Length || escapable.IndexOf(text[i + 1]) >= 0
                            : "`*_~=<[$".IndexOf(c) >= 0;
                        if (needs) line.Append('\\');
                        line.Append(c);
                    }
                else
                    line.Append(text);
            }

            for (int i = open.Count - 1; i >= 0; i--) line.Append(Closing(open[i]));
            return line.ToString();
        }

        // ![|W](path), or ![|WxH](path) once a height is set.
        private static string PictureMarkdown(string source, string? width, string? height)
        {
            string size = width == null ? string.Empty : height == null ? $"|{Px(width)}" : $"|{Px(width)}x{Px(height)}";
            string path = source.Replace("%", "%25").Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29");
            return $"![{size}]({path})";
        }

        // A floating or turned picture as <img>, the wrap, offset and turn in data- attributes.
        private static string PictureHtml(string source, XElement run, string? wrap)
        {
            StringBuilder tag = new StringBuilder("<img src=\"");
            tag.Append(source.Replace("%", "%25").Replace(" ", "%20").Replace("\"", "%22")).Append('"');
            foreach ((string attribute, string name) in new[] { ("Width", "width"), ("Height", "height") })
                if ((string?)run.Attribute(attribute) is string value) tag.Append($" {name}=\"{Px(value)}\"");
            if (wrap != null)
            {
                tag.Append($" data-wrap=\"{wrap.ToLowerInvariant()}\"");
                tag.Append($" data-x=\"{Px((string?)run.Attribute("X") ?? "0")}\" data-y=\"{Px((string?)run.Attribute("Y") ?? "0")}\"");
                if ((string?)run.Attribute("Collision") is string collision) tag.Append($" data-collision=\"{collision.ToLowerInvariant()}\"");
            }
            if ((string?)run.Attribute("Rotation") is string rotation) tag.Append($" data-rotate=\"{Px(rotation)}\"");
            return tag.Append('>').ToString();
        }

        private static string Px(string value) => MathF.Round(float.Parse(value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture);

        // A Markdown marker closes with itself, an HTML tag with its end tag.
        private static string Closing(string marker) =>
            marker[0] != '<' ? marker : "</" + marker[1..marker.IndexOfAny(new[] { ' ', '>' })] + ">";

        // The runs' text, and one style key per character.
        private static string Flatten(List<XElement> runs, out List<string> styles)
        {
            StringBuilder text = new StringBuilder();
            styles = new List<string>();

            foreach (XElement run in runs)
            {
                string? math = (string?)run.Attribute("Math");
                if (math != null)
                {
                    text.Append(BlockControl.PictureChar);
                    styles.Add($"math|{math}|{(bool?)run.Attribute("Display") == true}");
                    continue;
                }

                string? sheet = (string?)run.Attribute("Sheet");
                if (sheet != null)
                {
                    text.Append(BlockControl.PictureChar);
                    styles.Add($"sheet|{sheet}");
                    continue;
                }

                string? image = (string?)run.Attribute("Image");
                if (image != null)
                {
                    text.Append(BlockControl.PictureChar);
                    styles.Add($"img|{image}|{(string?)run.Attribute("Width")}|{(string?)run.Attribute("Height")}|{(string?)run.Attribute("Wrap")}|{(string?)run.Attribute("X")}|{(string?)run.Attribute("Y")}|{(string?)run.Attribute("Rotation")}|{(string?)run.Attribute("Collision")}");
                    continue;
                }

                string slice = (string?)run.Attribute("Text") ?? string.Empty;
                int mask = ((bool?)run.Attribute("Bold") == true ? 1 : 0)
                         | ((bool?)run.Attribute("Italic") == true ? 2 : 0)
                         | ((bool?)run.Attribute("Strikethrough") == true ? 4 : 0)
                         | ((string?)run.Attribute("StylingType") == "Code" ? 8 : 0)
                         | ((bool?)run.Attribute("Underline") == true ? 16 : 0);
                string key = $"{mask}|{((string?)run.Attribute("ColorHex"))?.ToUpperInvariant()}|{((string?)run.Attribute("HighlightHex"))?.ToUpperInvariant()}";

                text.Append(slice);
                for (int i = 0; i < slice.Length; i++) styles.Add(key);
            }

            return text.ToString();
        }
        #endregion
    }

    // Plain text <-> <Document> tree: one unstyled block per line.
    public static class PlainTextFormat
    {
        public static XElement Read(string text, string name)
        {
            XElement root = new XElement("Document", new XAttribute("Name", name));

            foreach (string line in text.Split('\n'))
            {
                string content = line.TrimEnd('\r');
                XElement run = new XElement("Run");
                if (content.Length > 0) run.SetAttributeValue("Text", content);
                root.Add(new XElement("Block", run));
            }

            return root;
        }

        public static string Write(XElement document)
        {
            StringBuilder text = new StringBuilder();
            bool first = true;

            foreach (XElement block in document.Elements())
            {
                if (block.Name.LocalName != "Block") continue;

                if (!first) text.Append('\n');
                first = false;

                if ((string?)block.Attribute("StylingType") == "Rule")
                {
                    text.Append("---");
                    continue;
                }

                foreach (XElement run in block.Elements())
                    text.Append((string?)run.Attribute("Text"));
            }

            return text.ToString();
        }
    }

    // Source text <-> <Document> tree: one wrapping code block per line.
    public static class TexSourceFormat
    {
        public static XElement Read(string text, string name, string language = "latex")
        {
            XElement root = new XElement("Document", new XAttribute("Name", name));

            foreach (string line in text.Split('\n'))
            {
                string content = line.EndsWith('\r') ? line[..^1] : line;
                XElement run = new XElement("Run");
                if (content.Length > 0) run.SetAttributeValue("Text", content);
                root.Add(new XElement("Block",
                    new XAttribute("StylingType", "Code"), new XAttribute("Language", language), new XAttribute("Wrap", "true"), run));
            }

            return root;
        }

        public static string Write(XElement document, string newline)
        {
            StringBuilder text = new StringBuilder();
            bool first = true;

            foreach (XElement block in document.Elements())
            {
                if (block.Name.LocalName != "Block") continue;

                if (!first) text.Append(newline);
                first = false;

                foreach (XElement run in block.Elements())
                    text.Append((string?)run.Attribute("Text"));
            }

            return text.ToString();
        }
    }
}
