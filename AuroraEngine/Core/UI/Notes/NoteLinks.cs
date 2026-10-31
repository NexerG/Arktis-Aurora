using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Sheets;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Notes
{
    // Note links follow a note renamed on disk and a heading whose text changed: in open notes and in the files.
    public static class NoteLinks
    {
        // How the host lists the notes in the vault of a note.
        public static Func<string?, IEnumerable<string>>? vaultNotes;

        // a note link in a file: a <Run> tag carrying Link, its Link and Text attributes, and [[target|alias]] outside ![[
        private static readonly Regex xmlRun = new Regex(@"<Run\b[^>]*\sLink=""[^""]*""[^>]*>");
        private static readonly Regex xmlAttribute = new Regex(@"(\s(Link|Text)="")([^""]*)("")");
        private static readonly Regex mdLink = new Regex(@"(?<!!)\[\[([^\[\]|]+)((?:\|[^\[\]]*)?)\]\]");

        // Links naming the note by its old name or path name it by its new one.
        public static void Renamed(string oldPath, string newPath, IEnumerable<string> notes)
        {
            string oldFull = Path.GetFullPath(oldPath).Replace('\\', '/');
            string newName = Path.GetFileNameWithoutExtension(newPath);

            Rewrite(notes, "Update links in", (note, target) =>
            {
                int hash = target.IndexOf('#');
                string name = hash < 0 ? target : target[..hash];
                if (name.Trim().Length == 0 || !Names(name, oldFull)) return null;

                int slash = name.LastIndexOf('/');
                return (slash < 0 ? newName : name[..(slash + 1)] + newName) + (hash < 0 ? string.Empty : target[hash..]);
            });
        }

        // Links to a heading of the note follow its new text.
        public static void HeadingRenamed(string notePath, string oldHeading, string newHeading)
        {
            if (vaultNotes == null) return;

            string full = Path.GetFullPath(notePath).Replace('\\', '/');
            string wanted = oldHeading.Trim();

            Rewrite(vaultNotes(notePath), "Update links in", (note, target) =>
            {
                int hash = target.IndexOf('#');
                if (hash < 0 || !target[(hash + 1)..].Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase)) return null;

                string name = target[..hash];
                bool here = name.Trim().Length == 0
                    ? string.Equals(Path.GetFullPath(note).Replace('\\', '/'), full, StringComparison.OrdinalIgnoreCase)
                    : Names(name, full);
                return here ? name + "#" + newHeading.Trim() : null;
            });
        }

        // Whether a link's name part points at the note at full: its bare name, or a path ending with or without the extension.
        private static bool Names(string name, string full)
        {
            string n = name.Trim().Replace('\\', '/');
            string bare = full[..^Path.GetExtension(full).Length];
            return bare.EndsWith("/" + n, StringComparison.OrdinalIgnoreCase) || full.EndsWith("/" + n, StringComparison.OrdinalIgnoreCase);
        }

        private static void Rewrite(IEnumerable<string> notes, string prompt, Func<string, string, string?> rename)
        {
            foreach (string note in notes)
            {
                Func<string, string?> inNote = target => rename(note, target);
                if (TabViewControl.FindOpenDocument(note, out _) is TabItemControl item)
                    TabViewControl.EditorOf(item)?.RenameNoteLinks(inNote);
                SheetLinks.RewriteFile(note, prompt, (text, markdown) => RenamedText(text, markdown, inNote));
            }
        }

        // A note's text with its links renamed, and a run's text where it showed the old target; null when nothing changed.
        private static string? RenamedText(string text, bool markdown, Func<string, string?> rename)
        {
            bool any = false;
            string rewritten = markdown
                ? mdLink.Replace(text, match =>
                {
                    if (rename(match.Groups[1].Value) is not string renamed) return match.Value;
                    any = true;
                    return "[[" + renamed + match.Groups[2].Value + "]]";
                })
                : xmlRun.Replace(text, run =>
                {
                    string? link = null, shown = null;
                    foreach (Match attribute in xmlAttribute.Matches(run.Value))
                    {
                        string value = XElement.Parse($"<a v=\"{attribute.Groups[3].Value}\"/>").Attribute("v")!.Value;
                        if (attribute.Groups[2].Value == "Link") link = value;
                        else shown = value;
                    }
                    if (link == null || rename(link) is not string renamed) return run.Value;

                    any = true;
                    return xmlAttribute.Replace(run.Value, attribute =>
                        attribute.Groups[2].Value == "Link" || shown == link
                            ? attribute.Groups[1].Value + SheetLinks.Escaped(renamed) + attribute.Groups[4].Value
                            : attribute.Value);
                });
            return any ? rewritten : null;
        }
    }
}
