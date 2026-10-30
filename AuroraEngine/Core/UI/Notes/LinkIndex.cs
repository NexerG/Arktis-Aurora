using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Security;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Sheets;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Notes
{
    public enum LinkKind
    {
        Embed, Math, Formula, Property, Link
    }

    // target as written; resolved is the file it names, null when none.
    public readonly record struct LinkEdge(string source, string target, LinkKind kind, string? resolved);

    // One vault's links between files, read from the files and cached by vault id:
    // <Links Version><File Path Size Ticks Sealed><Edge Kind Target/></File></Links>.
    public sealed class LinkIndex
    {
        private static readonly LogChannel Log = LogChannel.For("Links");

        private static readonly Regex wikiLink = new Regex(@"(!?)\[\[(.*?)\]\]");

        // set by the host
        public static Func<string?, string, string?>? findNote;
        public static string? cacheFolder;
        public static Func<string, LinkIndex?>? indexOf;
        public static Action<string>? openFile;

        private static readonly Dictionary<string, LinkIndex> indexes = new Dictionary<string, LinkIndex>(StringComparer.OrdinalIgnoreCase);

        private sealed class FileLinks
        {
            public long size;
            public long ticks;
            public bool isSealed;
            public List<LinkEdge> edges = new List<LinkEdge>();
        }

        public readonly string vault;
        private readonly string? cachePath;
        private readonly Dictionary<string, FileLinks> files = new Dictionary<string, FileLinks>(StringComparer.OrdinalIgnoreCase);

        // files read by the last Refresh
        public int reads { get; private set; }

        private LinkIndex(string vault, string id)
        {
            this.vault = vault;
            if (cacheFolder != null && id.Length > 0) cachePath = Path.Combine(cacheFolder, id + ".links.axml");
            LoadCache();
        }

        public static LinkIndex For(string vault, string id)
        {
            string full = Path.GetFullPath(vault);
            if (!indexes.TryGetValue(full, out LinkIndex? index)) indexes[full] = index = new LinkIndex(full, id);
            return index;
        }

        // Drops a vault's index from memory; its cache stays on disk.
        public static void Close(string vault) => indexes.Remove(Path.GetFullPath(vault));

        #region ---- queries ----
        public IEnumerable<string> Files() => files.Keys;

        public IReadOnlyList<LinkEdge> Outgoing(string path) =>
            files.TryGetValue(Path.GetFullPath(path), out FileLinks? links) ? links.edges : Array.Empty<LinkEdge>();

        public IEnumerable<LinkEdge> Incoming(string path)
        {
            string full = Path.GetFullPath(path);
            return files.Values.SelectMany(links => links.edges).Where(edge => string.Equals(edge.resolved, full, StringComparison.OrdinalIgnoreCase));
        }

        public IEnumerable<LinkEdge> Unresolved() => files.Values.SelectMany(links => links.edges).Where(edge => edge.resolved == null);

        // A file whose links can't be read without its password, or while its vault is locked.
        public bool IsSealed(string path) => files.TryGetValue(Path.GetFullPath(path), out FileLinks? links) && links.isSealed;
        #endregion

        #region ---- refresh ----
        // Re-reads the files that changed since the last refresh, drops deleted ones and resolves every edge again.
        public void Refresh()
        {
            reads = 0;
            string inVault = Path.Combine(vault, "_");
            HashSet<string> present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool changed = false;

            IEnumerable<string> notes = SheetBook.vaultNotes?.Invoke(inVault) ?? Enumerable.Empty<string>();
            IEnumerable<string> sheets = SheetBook.vaultSheets?.Invoke(inVault) ?? Enumerable.Empty<string>();
            foreach (string path in notes.Concat(sheets))
            {
                string full = Path.GetFullPath(path);
                FileInfo info = new FileInfo(full);
                if (!info.Exists) continue;
                present.Add(full);
                if (files.TryGetValue(full, out FileLinks? known) && known.size == info.Length && known.ticks == info.LastWriteTimeUtc.Ticks)
                    continue;

                files[full] = Read(full, info);
                reads++;
                changed = true;
            }

            foreach (string gone in files.Keys.Where(path => !present.Contains(path)).ToList())
            {
                files.Remove(gone);
                changed = true;
            }

            Resolve();
            if (changed) SaveCache();
        }

        private static FileLinks Read(string path, FileInfo info)
        {
            FileLinks links = new FileLinks { size = info.Length, ticks = info.LastWriteTimeUtc.Ticks };
            if (EncryptedFile.IsEncrypted(path))
            {
                links.isSealed = true;
                return links;
            }

            List<LinkEdge> edges = new List<LinkEdge>();
            try
            {
                string text = VaultFiles.ReadText(path);
                if (SheetDocument.IsSheet(path)) FromSheet(path, text, edges);
                else if (FileTypes.KindOf(path) == FileKind.Markdown) FromMarkdown(path, text, edges);
                else FromXml(path, text, edges);
            }
            catch (LockedFileException)
            {
                links.isSealed = true;
            }
            catch (XmlException e)
            {
                Log.Warn($"{path} can't be read for links: {e.Message}");
            }
            catch (IOException)
            {
                links.size = -1;
            }

            links.edges = edges.Distinct().ToList();
            return links;
        }

        private static void FromMarkdown(string path, string text, List<LinkEdge> edges)
        {
            if (Frontmatter.Split(text, out string block, out string body))
            {
                FromProperties(path, block, edges);
                text = body;
            }
            FromText(path, text, true, edges);
            foreach (string reference in SheetLinks.MathReferences(text)) edges.Add(new LinkEdge(path, reference, LinkKind.Math, null));
        }

        private static void FromXml(string path, string text, List<LinkEdge> edges)
        {
            XElement root = XElement.Parse(text);
            if ((string?)root.Attribute("Frontmatter") is string block) FromProperties(path, block, edges);
            foreach (XElement run in root.Descendants().Where(e => e.Name.LocalName == "Run"))
            {
                if ((string?)run.Attribute("Sheet") is string sheet) edges.Add(new LinkEdge(path, sheet, LinkKind.Embed, null));
                if ((string?)run.Attribute("Math") is string math)
                    foreach (string reference in SheetLinks.MathReferences(math)) edges.Add(new LinkEdge(path, reference, LinkKind.Math, null));
                if ((string?)run.Attribute("Text") is string runText) FromText(path, runText, false, edges);
            }
        }

        private static void FromSheet(string path, string text, List<LinkEdge> edges)
        {
            foreach (XElement cell in XElement.Parse(text).Descendants().Where(e => e.Name.LocalName == "Cell"))
                if (SheetFormula.IsFormula(cell.Value))
                    foreach (string file in SheetFormula.FileParts(cell.Value)) edges.Add(new LinkEdge(path, file, LinkKind.Formula, null));
        }

        private static void FromProperties(string path, string block, List<LinkEdge> edges)
        {
            foreach (Frontmatter.Entry entry in Frontmatter.Entries(block))
                foreach (Match match in wikiLink.Matches(entry.value ?? string.Empty))
                    if (match.Groups[2].Value.Trim().Length > 0) edges.Add(new LinkEdge(path, match.Groups[2].Value, LinkKind.Property, null));
        }

        // [[note]] links, and ![[…]] sheet embeds when the text holds live ones.
        private static void FromText(string path, string text, bool embeds, List<LinkEdge> edges)
        {
            foreach (Match match in wikiLink.Matches(text))
            {
                string target = match.Groups[2].Value;
                if (target.Trim().Length == 0) continue;
                bool embed = embeds && match.Groups[1].Value == "!" && SheetLinks.IsLink(target);
                edges.Add(new LinkEdge(path, target, embed ? LinkKind.Embed : LinkKind.Link, null));
            }
        }

        private void Resolve()
        {
            Dictionary<(bool sheet, string name), string?> found = new Dictionary<(bool, string), string?>();
            foreach (FileLinks links in files.Values)
                for (int k = 0; k < links.edges.Count; k++)
                {
                    LinkEdge edge = links.edges[k];
                    bool sheet = edge.kind is LinkKind.Embed or LinkKind.Math or LinkKind.Formula;
                    string name = sheet ? FilePart(edge.target) : edge.target.Split('|', '#')[0].Trim();
                    if (!found.TryGetValue((sheet, name.ToLowerInvariant()), out string? path))
                    {
                        path = name.Length == 0 ? null : sheet ? SheetBook.findSheet?.Invoke(edge.source, name) : findNote?.Invoke(edge.source, name);
                        found[(sheet, name.ToLowerInvariant())] = path;
                    }
                    links.edges[k] = edge with { resolved = path == null ? null : Path.GetFullPath(path) };
                }
        }

        private static string FilePart(string target)
        {
            int hash = target.IndexOf('#');
            return (hash > 0 ? target[..hash] : target).Trim();
        }
        #endregion

        #region ---- cache ----
        private void LoadCache()
        {
            if (cachePath == null || VaultLock.HasLock(vault) || !File.Exists(cachePath)) return;
            try
            {
                foreach (XElement file in XElement.Load(cachePath).Elements("File"))
                {
                    string path = Path.GetFullPath(Path.Combine(vault, (string?)file.Attribute("Path") ?? ""));
                    FileLinks links = new FileLinks
                    {
                        size = (long?)file.Attribute("Size") ?? -1,
                        ticks = (long?)file.Attribute("Ticks") ?? 0,
                        isSealed = (bool?)file.Attribute("Sealed") ?? false
                    };
                    foreach (XElement edge in file.Elements("Edge"))
                        if (Enum.TryParse((string?)edge.Attribute("Kind"), out LinkKind kind))
                            links.edges.Add(new LinkEdge(path, (string?)edge.Attribute("Target") ?? "", kind, null));
                    files[path] = links;
                }
            }
            catch (Exception e) when (e is XmlException or FormatException)
            {
                Log.Warn($"Link cache {cachePath} can't be read and is rebuilt: {e.Message}");
                files.Clear();
            }
        }

        private void SaveCache()
        {
            if (cachePath == null || VaultLock.HasLock(vault)) return;
            XElement root = new XElement("Links", new XAttribute("Version", 1),
                files.Select(pair => new XElement("File",
                    new XAttribute("Path", Path.GetRelativePath(vault, pair.Key)),
                    new XAttribute("Size", pair.Value.size),
                    new XAttribute("Ticks", pair.Value.ticks),
                    pair.Value.isSealed ? new XAttribute("Sealed", true) : null,
                    pair.Value.edges.Select(edge => new XElement("Edge", new XAttribute("Kind", edge.kind), new XAttribute("Target", edge.target))))));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                root.Save(cachePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Link cache {cachePath} can't be written: {e.Message}");
            }
        }
        #endregion
    }
}
