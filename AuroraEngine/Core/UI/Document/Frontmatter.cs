using System.Globalization;
using System.Text;

namespace ArctisAurora.Core.UI.Document
{
    // A Markdown note's metadata block: YAML between --- lines or TOML between +++ lines, kept as written.
    public static class Frontmatter
    {
        // one top-level key as the block holds it
        public struct Entry
        {
            public string key;
            public string value;
            public bool editable;
            public bool list;
            public int line;
            public int count;
        }

        private const string yaml = "---";
        private const string toml = "+++";

        // Splits a leading block off text; false when the text opens with none.
        public static bool Split(string text, out string block, out string body)
        {
            block = body = string.Empty;
            string[] lines = text.Split('\n');
            string open = lines[0].TrimEnd('\r');
            if (open != yaml && open != toml) return false;

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line != open && !(open == yaml && line == "...")) continue;

                block = string.Join("\n", lines.Take(i + 1).Select(l => l.TrimEnd('\r')));
                body = string.Join("\n", lines.Skip(i + 1));
                return true;
            }

            return false;
        }

        public static bool IsToml(string block) => block.StartsWith(toml);

        public static List<Entry> Entries(string? block)
        {
            List<Entry> entries = new List<Entry>();
            if (string.IsNullOrEmpty(block)) return entries;

            string[] lines = block.Split('\n');
            bool isToml = IsToml(block);
            int end = lines.Length - 1;

            for (int i = 1; i < end; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

                if (isToml && trimmed.StartsWith('['))
                {
                    entries.Add(new Entry
                    {
                        key = trimmed,
                        value = string.Join(", ", lines[(i + 1)..end].Select(l => l.Trim()).Where(l => l.Length > 0)),
                        line = i,
                        count = end - i
                    });
                    break;
                }

                if (!isToml && (char.IsWhiteSpace(line[0]) || line[0] == '-')) continue;

                int separator = isToml ? line.IndexOf('=') : KeyEnd(line);
                if (separator <= 0) continue;

                string raw = line[(separator + 1)..].Trim();
                Entry entry = new Entry
                {
                    key = Unquote(line[..separator].Trim()),
                    line = i,
                    count = 1
                };

                int next = i + 1;
                if (isToml)
                {
                    string? close = raw.StartsWith('[') && !raw.EndsWith(']') ? "]"
                                  : (raw.StartsWith("\"\"\"") || raw.StartsWith("'''")) && (raw.Length < 6 || !raw.EndsWith(raw[..3])) ? raw[..3]
                                  : null;
                    if (close != null)
                    {
                        while (next < end && !lines[next].TrimEnd().EndsWith(close)) next++;
                        if (next < end) next++;
                    }
                }
                else
                    while (next < end && lines[next].Length > 0 && (char.IsWhiteSpace(lines[next][0]) || lines[next][0] == '-')) next++;

                entry.count = next - i;
                if (!isToml && entry.count > 1 && raw.Length == 0 && lines[(i + 1)..next].All(l => (l.Trim().StartsWith("- ") || l.Trim() == "-") && KeyEnd(l.Trim()[1..]) < 0))
                {
                    entry.value = string.Join(", ", lines[(i + 1)..next].Select(l => Scalar(l.Trim()[1..].Trim())));
                    entry.editable = entry.list = true;
                }
                else if (entry.count > 1 || raw.StartsWith('|') || raw.StartsWith('>') || raw.StartsWith('{'))
                    entry.value = string.Join(", ", lines[i..next].Select((l, n) => n == 0 ? raw : l.Trim().TrimStart('-').Trim())
                                                                  .Where(l => l.Length > 0));
                else
                {
                    entry.value = Scalar(raw);
                    entry.editable = true;
                }

                entries.Add(entry);
                i = next - 1;
            }

            return entries;
        }

        // The one-line value of key, or null when the block has none it can read.
        public static string? Get(string? block, string key)
        {
            foreach (Entry entry in Entries(block))
                if (entry.editable && string.Equals(entry.key, key, StringComparison.OrdinalIgnoreCase))
                    return entry.value;

            return null;
        }

        // The block with key set to value, or removed when value is null. A missing block becomes YAML.
        public static string? Set(string? block, string key, string? value)
        {
            if (block == null)
            {
                if (value == null) return null;
                block = yaml + "\n" + yaml;
            }

            List<string> lines = block.Split('\n').ToList();
            bool isToml = IsToml(block);

            foreach (Entry entry in Entries(block))
            {
                if (!string.Equals(entry.key, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.editable && entry.value == value) return block;

                string itemIndent = entry.list ? lines[entry.line + 1][..lines[entry.line + 1].IndexOf('-')] : "";
                lines.RemoveRange(entry.line, entry.count);
                if (value != null && entry.list)
                    lines.InsertRange(entry.line, ListLines(entry.key, value, itemIndent));
                else if (value != null) lines.Insert(entry.line, Line(entry.key, value, isToml));
                return string.Join("\n", lines);
            }

            if (value == null) return block;

            int at = lines.Count - 1;
            if (isToml)
                for (int i = 1; i < lines.Count - 1; i++)
                    if (lines[i].TrimStart().StartsWith('[')) { at = i; break; }

            lines.Insert(at, Line(key, value, isToml));
            return string.Join("\n", lines);
        }

        #region ---- values ----
        // Where a YAML key ends: the first colon followed by a space or the end of the line.
        private static int KeyEnd(string line)
        {
            for (int i = 0; i < line.Length; i++)
                if (line[i] == ':' && (i + 1 == line.Length || line[i + 1] == ' ')) return i;

            return -1;
        }

        private static string Scalar(string raw)
        {
            if (raw.StartsWith('"') || raw.StartsWith('\''))
            {
                int close = 1;
                while (close < raw.Length && (raw[close] != raw[0] || (raw[0] == '"' && raw[close - 1] == '\\'))) close++;
                return Unquote(raw[..Math.Min(close + 1, raw.Length)]);
            }
            if (raw.StartsWith('[')) return raw;

            int comment = raw.IndexOf(" #");
            return (comment >= 0 ? raw[..comment] : raw).Trim();
        }

        private static string Unquote(string s)
        {
            if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
                return s[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
            if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'')
                return s[1..^1].Replace("''", "'");

            return s;
        }

        private static string Line(string key, string value, bool isToml) =>
            isToml ? $"{key} = {TomlValue(value)}" : value.Length == 0 ? $"{key}:" : $"{key}: {YamlValue(value)}";

        // A YAML block sequence from a comma-separated value, items at the indent the list had.
        private static IEnumerable<string> ListLines(string key, string value, string indent)
        {
            yield return key + ":";
            foreach (string item in value.Split(',').Select(i => i.Trim()).Where(i => i.Length > 0))
                yield return indent + "- " + YamlValue(item);
        }

        private static string YamlValue(string value)
        {
            if (value.StartsWith('[') && value.EndsWith(']') && !value.StartsWith("[[")) return value;

            bool plain = value.Trim() == value
                         && "-?:,[]{}#&*!|>'\"%@`".IndexOf(value[0]) < 0
                         && !value.Contains(": ") && !value.Contains(" #");
            return plain ? value : Quoted(value);
        }

        private static string TomlValue(string value)
        {
            if (value == "true" || value == "false") return value;
            if (value.StartsWith('[') && value.EndsWith(']')) return value;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return value;

            return Quoted(value);
        }

        private static string Quoted(string value) =>
            new StringBuilder("\"").Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"').ToString();
        #endregion
    }
}
