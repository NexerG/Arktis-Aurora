using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace _Build
{
    // Writes the generated dependency sections of every vault page that claims source files.
    internal static class CodeDocs
    {
        const string Marker = "%% generated below: do not edit %%";
        const string Vault = "Internal/DOCUMENTATION/";
        const string Crlf = "\r\n";
        static readonly string[] GeneratedKeys = { "Namespace", "Types", "Maintainer", "DependsOn", "UsedBy" };
        static readonly Regex ActionAttribute = new Regex("\\bAction=\"([^\"]*)\"", RegexOptions.Compiled);
        static readonly Regex Backticked = new Regex("`([^`]+)`", RegexOptions.Compiled);

        public static int Run(string[] args)
        {
            string root = Path.GetFullPath(args[1]).TrimEnd('\\', '/') + "\\";
            bool check = false;
            string? only = null;
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--check") check = true;
                else if (args[i] == "--only" && i + 1 < args.Length) only = args[++i];
            }
            string onlyPrefix = only == null ? "" : only + "/";

            string mapPath = root + "Internal\\DOCUMENTATION\\Engine\\Maintainers.axml";
            if (!File.Exists(mapPath))
            {
                Console.Out.WriteLine("ERROR " + Vault + "Engine/Maintainers.axml does not exist");
                return 1;
            }
            Maintainer[] maintainers = LoadMaintainers(mapPath);

            string[] files = EnumerateUniverse(root);
            Dictionary<string, int> fileIndex = new Dictionary<string, int>(files.Length, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Length; i++) fileIndex[ToFull(root, files[i])] = i;
            SourceInfo[] infos = new SourceInfo[files.Length];
            for (int i = 0; i < infos.Length; i++) infos[i] = new SourceInfo();
            HashSet<Edge> edges = new HashSet<Edge>(files.Length * 32);

            Project[] projects = LoadProjects(root);
            Analyse(root, projects, files, fileIndex, infos, edges);

            Dictionary<string, List<string>> actionFiles = IndexAxml(root, projects);
            Decision[] decisions = LoadDecisions(root + "Internal\\DOCUMENTATION\\ClaudeMemory\\Decisions\\");

            // pages and claims
            List<Page> pages = LoadPages(root + "Internal\\DOCUMENTATION\\Engine\\");
            int[] fileToPage = new int[files.Length];
            Array.Fill(fileToPage, -1);
            int errors = 0;
            for (int p = 0; p < pages.Count; p++)
            {
                Page page = pages[p];
                for (int c = 0; c < page.claims.Count; c++)
                {
                    string claim = page.claims[c];
                    bool inOnly = only == null || claim.StartsWith(onlyPrefix, StringComparison.Ordinal);
                    if (!File.Exists(ToFull(root, claim)))
                    {
                        if (inOnly) { Console.Out.WriteLine("ERROR " + page.path + ": " + claim + " does not exist"); errors++; }
                        continue;
                    }
                    if (!fileIndex.TryGetValue(ToFull(root, claim), out int f)) continue;
                    if (fileToPage[f] >= 0)
                    {
                        if (inOnly) { Console.Out.WriteLine("ERROR " + claim + ": claimed by " + pages[fileToPage[f]].path + " and " + page.path); errors++; }
                        continue;
                    }
                    fileToPage[f] = p;
                    page.files.Add(f);
                }
            }
            int claimingPages = pages.Count;
            AddStubs(pages, files, fileToPage);
            for (int p = claimingPages; p < pages.Count; p++)
            {
                if (!check || (only != null && !pages[p].claims[0].StartsWith(onlyPrefix, StringComparison.Ordinal))) continue;
                Console.Out.WriteLine("ERROR " + pages[p].claims[0] + ": claimed by no page");
                errors++;
            }

            // per-page facts
            for (int p = 0; p < pages.Count; p++)
            {
                Page page = pages[p];
                page.inScope = only == null || AllUnder(page.claims, onlyPrefix);
                for (int i = 0; i < page.files.Count; i++)
                {
                    SourceInfo info = infos[page.files[i]];
                    AddDistinct(page.namespaces, info.namespaces);
                    AddDistinct(page.types, info.types);
                    string? who = MaintainerOf(maintainers, files[page.files[i]]);
                    if (who != null && !page.maintainers.Contains(who)) page.maintainers.Add(who);
                }
                page.namespaces.Sort(string.CompareOrdinal);
                page.types.Sort(string.CompareOrdinal);
                page.maintainers.Sort(string.CompareOrdinal);
            }
            foreach (Edge edge in edges)
            {
                int from = fileToPage[edge.From];
                int to = fileToPage[edge.To];
                if (from < 0 || to < 0 || from == to) continue;
                pages[from].uses.Add(new UseRow(edge.Here, edge.There, to));
                if (!pages[to].usedBy.TryGetValue(from, out HashSet<string>? members))
                {
                    members = new HashSet<string>(8);
                    pages[to].usedBy[from] = members;
                }
                members.Add(edge.There);
            }

            // write
            int written = 0, created = 0, unchanged = 0, changed = 0;
            string today = DateTime.Today.ToString("yyyy-MM-dd");
            for (int p = 0; p < pages.Count; p++)
            {
                Page page = pages[p];
                if (!page.inScope) continue;
                bool stub = p >= claimingPages;
                if (stub && check) continue;
                string text = stub ? StubText(today, page.claims[0]) : page.text;
                string rendered = Render(text, GenerateKeys(page, pages), GenerateBody(page, pages, files, infos, actionFiles, decisions));
                byte[] bytes = Encode(rendered, page.bom);
                if (page.original != null && bytes.AsSpan().SequenceEqual(page.original)) { unchanged++; continue; }
                if (check)
                {
                    Console.Out.WriteLine("CHANGED " + page.path);
                    changed++;
                    continue;
                }
                string full = ToFull(root, Vault + page.path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, bytes);
                if (stub) created++;
                else written++;
            }

            ReportStale(root, pages, claimingPages);
            Console.Out.WriteLine("codedocs: " + written + " pages written, " + created + " stubs created, " + unchanged + " unchanged");
            return errors > 0 || changed > 0 ? 1 : 0;
        }

        #region Source universe and analysis
        static string[] EnumerateUniverse(string root)
        {
            List<string> found = new List<string>(1024);
            CollectSources(root, root, found);
            string[] files = found.ToArray();
            Array.Sort(files, string.CompareOrdinal);
            return files;
        }

        static void CollectSources(string root, string dir, List<string> found)
        {
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (name == "bin" || name == "obj" || name == ".claude" || name == ".git") continue;
                if (dir == root && name == "Internal") continue;
                CollectSources(root, sub, found);
            }
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs"))
                found.Add(file.Substring(root.Length).Replace('\\', '/'));
        }

        static Project[] LoadProjects(string root)
        {
            List<Project> projects = new List<Project>(8);
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(dir);
                if (name == "Internal") continue;
                string[] csprojs = Directory.GetFiles(dir, "*.csproj");
                if (csprojs.Length != 1) continue;
                XDocument doc = XDocument.Load(csprojs[0]);
                Project project = new Project(name, dir + "\\", Path.GetFileNameWithoutExtension(csprojs[0]));
                List<string> defines = new List<string>(16) { "DEBUG", "TRACE" };
                string[] tfms = Array.Empty<string>();
                foreach (XElement e in doc.Descendants())
                {
                    string local = e.Name.LocalName;
                    if (local == "AssemblyName") project.assemblyName = e.Value.Trim();
                    else if ((local == "TargetFramework" || local == "TargetFrameworks") && tfms.Length == 0) tfms = e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    else if (local == "DefineConstants")
                    {
                        string[] parts = e.Value.Split(';');
                        for (int i = 0; i < parts.Length; i++)
                        {
                            string part = parts[i].Trim();
                            if (part.Length > 0 && !part.StartsWith("$(", StringComparison.Ordinal)) defines.Add(part);
                        }
                    }
                    else if (local == "ProjectReference")
                    {
                        string? include = (string?)e.Attribute("Include");
                        if (include != null) project.references.Add(Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(Path.Combine(dir, include)))!));
                    }
                }
                project.tfm = ChooseTfm(project.folder, tfms);
                int major = MajorOf(project.tfm);
                defines.Add("NET");
                defines.Add("NET" + major + "_0");
                for (int n = 5; n <= major; n++) defines.Add("NET" + n + "_0_OR_GREATER");
                if (project.tfm.Contains("-windows", StringComparison.Ordinal)) defines.Add("WINDOWS");
                project.defines = defines.ToArray();
                projects.Add(project);
            }
            return projects.ToArray();
        }

        // Highest .NET version first, -windows first at equal version, skipping ones with no Debug output.
        static string ChooseTfm(string folder, string[] tfms)
        {
            if (tfms.Length == 0) return "";
            string[] ranked = (string[])tfms.Clone();
            Array.Sort(ranked, static (a, b) =>
            {
                int c = MajorOf(b).CompareTo(MajorOf(a));
                if (c != 0) return c;
                return b.Contains("-windows", StringComparison.Ordinal).CompareTo(a.Contains("-windows", StringComparison.Ordinal));
            });
            for (int i = 0; i < ranked.Length; i++)
                if (Directory.Exists(folder + "obj\\Debug\\" + ranked[i]) && Directory.Exists(folder + "bin\\Debug\\" + ranked[i])) return ranked[i];
            return ranked[0];
        }

        static int MajorOf(string tfm)
        {
            int major = 0;
            for (int i = 3; i < tfm.Length && char.IsAsciiDigit(tfm[i]); i++) major = major * 10 + (tfm[i] - '0');
            return major;
        }

        static void Analyse(string root, Project[] projects, string[] files, Dictionary<string, int> fileIndex, SourceInfo[] infos, HashSet<Edge> edges)
        {
            HashSet<string> own = new HashSet<string>(projects.Length, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < projects.Length; i++) own.Add(projects[i].assemblyName);
            string[] platform = PlatformAssemblies();
            bool[] covered = new bool[files.Length];

            for (int i = 0; i < projects.Length; i++) Compile(projects[i], projects, root, files, own, platform);
            for (int i = 0; i < projects.Length; i++)
            {
                Project project = projects[i];
                CSharpCompilation compilation = project.compilation!;
                int errorCount = 0;
                foreach (Diagnostic d in compilation.GetDiagnostics())
                    if (d.Severity == DiagnosticSeverity.Error) errorCount++;
                Console.Out.WriteLine("ANALYSIS " + project.name + ": " + errorCount + " errors");
                Walk(compilation, project.sourceTrees, fileIndex, infos, edges, covered);
            }

            // files under no project folder
            List<SyntaxTree> loose = new List<SyntaxTree>(4);
            for (int f = 0; f < files.Length; f++)
                if (!covered[f]) loose.Add(Parse(ToFull(root, files[f]), CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview)));
            if (loose.Count == 0) return;
            List<MetadataReference> references = new List<MetadataReference>(platform.Length);
            for (int i = 0; i < platform.Length; i++) references.Add(MetadataReference.CreateFromFile(platform[i]));
            CSharpCompilation looseCompilation = CSharpCompilation.Create("Loose", loose, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            Walk(looseCompilation, loose, fileIndex, infos, edges, covered);
        }

        static void Walk(CSharpCompilation compilation, List<SyntaxTree> trees, Dictionary<string, int> fileIndex, SourceInfo[] infos, HashSet<Edge> edges, bool[] covered)
        {
            for (int t = 0; t < trees.Count; t++)
            {
                SyntaxTree tree = trees[t];
                if (!fileIndex.TryGetValue(tree.FilePath, out int file)) continue;
                covered[file] = true;
                EdgeWalker walker = new EdgeWalker(compilation.GetSemanticModel(tree), fileIndex, edges, infos[file], file);
                walker.Visit(tree.GetRoot());
            }
        }

        static void Compile(Project project, Project[] projects, string root, string[] files, HashSet<string> own, string[] platform)
        {
            if (project.compilation != null) return;
            CSharpParseOptions options = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: project.defines);
            string prefix = project.name + "/";
            for (int f = 0; f < files.Length; f++)
                if (files[f].StartsWith(prefix, StringComparison.Ordinal)) project.sourceTrees.Add(Parse(ToFull(root, files[f]), options));
            List<SyntaxTree> trees = new List<SyntaxTree>(project.sourceTrees.Count + 4);
            trees.AddRange(project.sourceTrees);
            string generated = project.folder + "obj\\Debug\\" + project.tfm;
            if (Directory.Exists(generated))
            {
                foreach (string g in Directory.EnumerateFiles(generated, "*.g.cs")) trees.Add(Parse(g, options));
                foreach (string g in Directory.EnumerateFiles(generated, "*.AssemblyInfo.cs")) trees.Add(Parse(g, options));
            }

            List<MetadataReference> references = new List<MetadataReference>(256);
            HashSet<string> referenced = new HashSet<string>(8, StringComparer.Ordinal);
            CollectProjectReferences(project, projects, root, files, own, platform, referenced, references);

            Dictionary<string, string> byName = new Dictionary<string, string>(256, StringComparer.OrdinalIgnoreCase);
            string bin = project.folder + "bin\\Debug\\" + project.tfm;
            if (Directory.Exists(bin))
                foreach (string dll in Directory.EnumerateFiles(bin, "*.dll"))
                    if (!own.Contains(Path.GetFileNameWithoutExtension(dll)) && IsManaged(dll)) byName[Path.GetFileName(dll)] = dll;
            for (int i = 0; i < platform.Length; i++) byName.TryAdd(Path.GetFileName(platform[i]), platform[i]);
            foreach (string path in byName.Values) references.Add(MetadataReference.CreateFromFile(path));

            project.compilation = CSharpCompilation.Create(project.assemblyName, trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        }

        static void CollectProjectReferences(Project project, Project[] projects, string root, string[] files, HashSet<string> own, string[] platform, HashSet<string> referenced, List<MetadataReference> references)
        {
            for (int r = 0; r < project.references.Count; r++)
            {
                Project? target = Find(projects, project.references[r]);
                if (target == null || !referenced.Add(target.name)) continue;
                Compile(target, projects, root, files, own, platform);
                references.Add(target.compilation!.ToMetadataReference());
                CollectProjectReferences(target, projects, root, files, own, platform, referenced, references);
            }
        }

        static Project? Find(Project[] projects, string name)
        {
            for (int i = 0; i < projects.Length; i++)
                if (projects[i].name == name) return projects[i];
            return null;
        }

        static string[] PlatformAssemblies()
        {
            string[] all = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            string self = AppContext.BaseDirectory;
            int count = 0;
            for (int i = 0; i < all.Length; i++)
                if (!all[i].StartsWith(self, StringComparison.OrdinalIgnoreCase)) all[count++] = all[i];
            return all.AsSpan(0, count).ToArray();
        }

        static bool IsManaged(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader reader = new PEReader(stream);
            try { return reader.HasMetadata; }
            catch (BadImageFormatException) { return false; }
        }

        static SyntaxTree Parse(string path, CSharpParseOptions options) => CSharpSyntaxTree.ParseText(SourceText.From(File.ReadAllText(path), Encoding.UTF8), options, path);
        #endregion

        #region Data, decisions and maintainers
        static Dictionary<string, List<string>> IndexAxml(string root, Project[] projects)
        {
            Dictionary<string, List<string>> index = new Dictionary<string, List<string>>(512, StringComparer.Ordinal);
            List<string> axml = new List<string>(256);
            for (int i = 0; i < projects.Length; i++) CollectAxml(projects[i].folder, axml);
            string[] sorted = axml.ToArray();
            Array.Sort(sorted, string.CompareOrdinal);
            for (int i = 0; i < sorted.Length; i++)
            {
                string rel = sorted[i].Substring(root.Length).Replace('\\', '/');
                foreach (Match m in ActionAttribute.Matches(File.ReadAllText(sorted[i])))
                {
                    string action = m.Groups[1].Value;
                    if (!index.TryGetValue(action, out List<string>? list))
                    {
                        list = new List<string>(2);
                        index[action] = list;
                    }
                    if (!list.Contains(rel)) list.Add(rel);
                }
            }
            return index;
        }

        static void CollectAxml(string dir, List<string> found)
        {
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (name == "bin" || name == "obj" || name == ".claude" || name == ".git") continue;
                CollectAxml(sub, found);
            }
            found.AddRange(Directory.EnumerateFiles(dir, "*.axml"));
        }

        static Decision[] LoadDecisions(string dir)
        {
            List<Decision> decisions = new List<Decision>(160);
            foreach (string path in Directory.EnumerateFiles(dir, "*.md"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (name == "INDEX" || name == "README") continue;
                string[] lines = File.ReadAllLines(path);
                HashSet<string> segments = new HashSet<string>(16, StringComparer.Ordinal);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].StartsWith("**Scope:**", StringComparison.Ordinal)) continue;
                    for (int j = i; j < lines.Length && lines[j].Trim().Length > 0; j++)
                        foreach (Match m in Backticked.Matches(lines[j]))
                        {
                            string token = m.Groups[1].Value;
                            if (token.Contains('/') || HasFileExtension(token)) continue;
                            segments.Add(token.Substring(token.LastIndexOf('.') + 1));
                        }
                    break;
                }
                decisions.Add(new Decision(name, segments));
            }
            Decision[] sorted = decisions.ToArray();
            Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            return sorted;
        }

        static bool HasFileExtension(string token)
        {
            int dot = token.LastIndexOf('.');
            if (dot < 0 || token.Length - dot - 1 is 0 or > 5) return false;
            for (int i = dot + 1; i < token.Length; i++)
                if (!char.IsAsciiLetterLower(token[i]) && !char.IsAsciiDigit(token[i])) return false;
            return true;
        }

        static Maintainer[] LoadMaintainers(string path)
        {
            List<Maintainer> list = new List<Maintainer>(8);
            foreach (XElement e in XDocument.Load(path).Descendants("Maintainer"))
            {
                string glob = (string?)e.Attribute("Path") ?? "";
                string pattern = "^" + Regex.Escape(glob).Replace("\\*\\*", "*").Replace("\\*", ".*").Replace("\\?", ".") + "$";
                list.Add(new Maintainer(new Regex(pattern, RegexOptions.CultureInvariant), (string?)e.Attribute("Name") ?? ""));
            }
            return list.ToArray();
        }

        static string? MaintainerOf(Maintainer[] maintainers, string file)
        {
            for (int i = maintainers.Length - 1; i >= 0; i--)
                if (maintainers[i].Glob.IsMatch(file)) return maintainers[i].Name;
            return null;
        }
        #endregion

        #region Pages
        static List<Page> LoadPages(string engineDir)
        {
            string[] paths = Directory.GetFiles(engineDir, "*.md", SearchOption.AllDirectories);
            for (int i = 0; i < paths.Length; i++) paths[i] = "Engine/" + paths[i].Substring(engineDir.Length).Replace('\\', '/');
            Array.Sort(paths, string.CompareOrdinal);
            allNames = new HashSet<string>(paths.Length * 2, StringComparer.OrdinalIgnoreCase);
            List<Page> pages = new List<Page>(paths.Length);
            for (int i = 0; i < paths.Length; i++)
            {
                allNames.Add(Path.GetFileNameWithoutExtension(paths[i]));
                byte[] bytes = File.ReadAllBytes(engineDir + paths[i].Substring("Engine/".Length).Replace('/', '\\'));
                bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                string text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
                List<string> lines = SplitLines(text);
                int close = ClosingFence(lines);
                if (close < 0) continue;
                List<string> claims = KeyItems(lines, close, "SourceFiles", false);
                if (claims.Count == 0) continue;
                Page page = new Page(paths[i], claims);
                page.text = text;
                page.bom = bom;
                page.original = bytes;
                List<string> system = KeyItems(lines, close, "System", true);
                for (int s = 0; s < system.Count; s++) page.system.Add(system[s].Replace("[[", "").Replace("]]", "").Trim());
                List<string> verified = KeyItems(lines, close, "VerifiedAgainst", true);
                if (verified.Count > 0) page.verifiedAgainst = verified[0];
                pages.Add(page);
            }
            return pages;
        }

        static HashSet<string> allNames = new HashSet<string>(0);

        // Values of a frontmatter key: its block list, plus its inline value when allowed.
        static List<string> KeyItems(List<string> lines, int close, string key, bool inline)
        {
            List<string> items = new List<string>(4);
            for (int i = 1; i < close; i++)
            {
                string bare = Bare(lines[i]);
                if (!bare.StartsWith(key + ":", StringComparison.Ordinal)) continue;
                string value = bare.Substring(key.Length + 1).Trim();
                if (value.Length > 0)
                {
                    if (inline) items.Add(Unquote(value));
                    return items;
                }
                for (int j = i + 1; j < close; j++)
                {
                    string item = Bare(lines[j]);
                    string trimmed = item.TrimStart();
                    if (!IsContinuation(item) || !trimmed.StartsWith("- ", StringComparison.Ordinal)) break;
                    string v = Unquote(trimmed.Substring(2));
                    if (v.Length > 0) items.Add(inline ? v : v.Replace('\\', '/'));
                }
                return items;
            }
            return items;
        }

        static void AddStubs(List<Page> pages, string[] files, int[] fileToPage)
        {
            List<int> unclaimed = new List<int>(files.Length);
            for (int f = 0; f < files.Length; f++)
                if (fileToPage[f] < 0) unclaimed.Add(f);
            // collisions are judged on existing pages and the whole source universe, never on other stubs
            Dictionary<string, int> baseCounts = new Dictionary<string, int>(files.Length, StringComparer.OrdinalIgnoreCase);
            for (int f = 0; f < files.Length; f++)
            {
                string n = Path.GetFileNameWithoutExtension(files[f]);
                baseCounts[n] = baseCounts.GetValueOrDefault(n) + 1;
            }
            Dictionary<string, int> secondCounts = new Dictionary<string, int>(16, StringComparer.OrdinalIgnoreCase);
            for (int f = 0; f < files.Length; f++)
            {
                string n = Path.GetFileNameWithoutExtension(files[f]);
                if (baseCounts[n] < 2) continue;
                string second = n + " (" + ParentFolder(files[f]) + ")";
                secondCounts[second] = secondCounts.GetValueOrDefault(second) + 1;
            }
            string[] names = new string[unclaimed.Count];
            for (int i = 0; i < names.Length; i++)
            {
                string file = files[unclaimed[i]];
                string n = Path.GetFileNameWithoutExtension(file);
                bool shared = baseCounts[n] > 1;
                if (!shared && !allNames.Contains(n))
                {
                    names[i] = n;
                    continue;
                }
                string second = n + " (" + ParentFolder(file) + ")";
                bool taken = allNames.Contains(second) || secondCounts.GetValueOrDefault(second) > (shared ? 1 : 0);
                names[i] = taken ? n + " (" + ProjectOf(file) + " " + ParentFolder(file) + ")" : second;
            }

            for (int i = 0; i < names.Length; i++)
            {
                int f = unclaimed[i];
                string file = files[f];
                string project = ProjectOf(file);
                string folder = file.Substring(0, file.LastIndexOf('/') + 1);
                string vaultFolder = project == "AuroraEngine" ? "Engine/" + folder.Substring(project.Length + 1) : "Engine/" + folder;
                List<string> claims = new List<string>(1) { file };
                Page page = new Page(vaultFolder + names[i] + ".md", claims);
                page.files.Add(f);
                fileToPage[f] = pages.Count;
                pages.Add(page);
            }
        }

        static string StubText(string date, string file) =>
            "---" + Crlf + "date: " + date + Crlf + "Kind: File" + Crlf + "Status: Stub" + Crlf + "System:" + Crlf + "SourceFiles:" + Crlf + "  - " + file + Crlf +
            "cssclasses:" + Crlf + "  - Aurora.css" + Crlf + "---" + Crlf + "## Description" + Crlf + Crlf + Marker + Crlf;

        static string Render(string text, string keys, string body)
        {
            List<string> lines = SplitLines(text);
            int close = ClosingFence(lines);
            StringBuilder sb = new StringBuilder(text.Length + keys.Length + body.Length);
            sb.Append(lines[0]);
            for (int i = 1; i < close; i++)
            {
                if (IsGeneratedKey(Bare(lines[i])))
                {
                    while (i + 1 < close && IsContinuation(Bare(lines[i + 1]))) i++;
                    continue;
                }
                sb.Append(lines[i]);
            }
            sb.Append(keys);
            sb.Append(lines[close]);
            if (!lines[close].EndsWith('\n')) sb.Append(Crlf);
            int marker = -1;
            for (int i = close + 1; i < lines.Count && marker < 0; i++)
                if (Bare(lines[i]).TrimEnd() == Marker) marker = i;
            int end = marker < 0 ? lines.Count : marker;
            for (int i = close + 1; i < end; i++) sb.Append(lines[i]);
            if (marker < 0)
            {
                if (sb[sb.Length - 1] != '\n') sb.Append(Crlf);
                sb.Append(Crlf);
            }
            sb.Append(Marker).Append(Crlf).Append(body);
            return sb.ToString();
        }

        static string GenerateKeys(Page page, List<Page> pages)
        {
            StringBuilder sb = new StringBuilder(256);
            AppendKey(sb, "Namespace", page.namespaces, true, false);
            AppendKey(sb, "Types", page.types, false, false);
            AppendKey(sb, "Maintainer", page.maintainers, true, false);
            List<string> dependsOn = new List<string>(page.uses.Count);
            foreach (UseRow row in page.uses)
                if (!dependsOn.Contains(pages[row.Page].name)) dependsOn.Add(pages[row.Page].name);
            dependsOn.Sort(string.CompareOrdinal);
            AppendKey(sb, "DependsOn", dependsOn, false, true);
            List<string> usedBy = new List<string>(page.usedBy.Count);
            foreach (int p in page.usedBy.Keys) usedBy.Add(pages[p].name);
            usedBy.Sort(string.CompareOrdinal);
            AppendKey(sb, "UsedBy", usedBy, false, true);
            return sb.ToString();
        }

        static void AppendKey(StringBuilder sb, string key, List<string> values, bool scalarWhenOne, bool links)
        {
            sb.Append(key).Append(':');
            if (values.Count == 1 && scalarWhenOne)
            {
                sb.Append(' ').Append(values[0]).Append(Crlf);
                return;
            }
            sb.Append(Crlf);
            for (int i = 0; i < values.Count; i++)
            {
                if (links) sb.Append("  - \"[[").Append(values[i]).Append("]]\"").Append(Crlf);
                else sb.Append("  - ").Append(values[i]).Append(Crlf);
            }
        }

        static string GenerateBody(Page page, List<Page> pages, string[] files, SourceInfo[] infos, Dictionary<string, List<string>> actionFiles, Decision[] decisions)
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("## Dependencies").Append(Crlf).Append(Crlf);

            sb.Append("### Uses").Append(Crlf).Append(Crlf);
            UseRow[] uses = new UseRow[page.uses.Count];
            page.uses.CopyTo(uses);
            Array.Sort(uses, (a, b) =>
            {
                int c = string.CompareOrdinal(a.Here, b.Here);
                if (c == 0) c = string.CompareOrdinal(pages[a.Page].name, pages[b.Page].name);
                return c != 0 ? c : string.CompareOrdinal(a.There, b.There);
            });
            if (uses.Length == 0) sb.Append("None.").Append(Crlf);
            else
            {
                sb.Append("| Here | Uses | Page |").Append(Crlf).Append("| --- | --- | --- |").Append(Crlf);
                for (int i = 0; i < uses.Length; i++)
                    sb.Append("| `").Append(uses[i].Here).Append("` | `").Append(uses[i].There).Append("` | [[").Append(pages[uses[i].Page].name).Append("]] |").Append(Crlf);
            }

            sb.Append(Crlf).Append("### Used by").Append(Crlf).Append(Crlf);
            UsedByRow[] usedBy = new UsedByRow[page.usedBy.Count];
            int n = 0;
            foreach (KeyValuePair<int, HashSet<string>> pair in page.usedBy)
            {
                Page user = pages[pair.Key];
                string[] members = new string[pair.Value.Count];
                pair.Value.CopyTo(members);
                Array.Sort(members, string.CompareOrdinal);
                string system = string.Join(", ", user.system.Count > 0 ? user.system : user.namespaces);
                usedBy[n++] = new UsedByRow(system, user.name, "`" + string.Join("`, `", members) + "`");
            }
            Array.Sort(usedBy, static (a, b) =>
            {
                int c = string.CompareOrdinal(a.System, b.System);
                return c != 0 ? c : string.CompareOrdinal(a.Page, b.Page);
            });
            if (usedBy.Length == 0) sb.Append("None.").Append(Crlf);
            else
            {
                sb.Append("| System | Page | Uses here |").Append(Crlf).Append("| --- | --- | --- |").Append(Crlf);
                for (int i = 0; i < usedBy.Length; i++)
                    sb.Append("| ").Append(usedBy[i].System).Append(" | [[").Append(usedBy[i].Page).Append("]] | ").Append(usedBy[i].Members).Append(" |").Append(Crlf);
            }

            sb.Append(Crlf).Append("### Named by data").Append(Crlf).Append(Crlf);
            List<DataRow> data = new List<DataRow>(4);
            for (int i = 0; i < page.files.Count; i++)
            {
                List<ActionRef> actions = infos[page.files[i]].actions;
                for (int a = 0; a < actions.Count; a++)
                {
                    if (!actionFiles.TryGetValue(actions[a].Name, out List<string>? named)) continue;
                    for (int k = 0; k < named.Count; k++)
                    {
                        DataRow row = new DataRow(actions[a].Name, actions[a].Category, named[k]);
                        if (!data.Contains(row)) data.Add(row);
                    }
                }
            }
            data.Sort(static (a, b) =>
            {
                int c = string.CompareOrdinal(a.Action, b.Action);
                if (c == 0) c = string.CompareOrdinal(a.Category, b.Category);
                return c != 0 ? c : string.CompareOrdinal(a.File, b.File);
            });
            if (data.Count == 0) sb.Append("None.").Append(Crlf);
            else
            {
                sb.Append("| Action | Category | File |").Append(Crlf).Append("| --- | --- | --- |").Append(Crlf);
                for (int i = 0; i < data.Count; i++)
                    sb.Append("| `").Append(data[i].Action).Append("` | ").Append(data[i].Category).Append(" | `").Append(data[i].File).Append("` |").Append(Crlf);
            }

            sb.Append(Crlf).Append("### Decisions").Append(Crlf).Append(Crlf);
            int listed = 0;
            for (int d = 0; d < decisions.Length; d++)
            {
                for (int t = 0; t < page.types.Count; t++)
                {
                    if (!decisions[d].Segments.Contains(page.types[t])) continue;
                    sb.Append("- [[").Append(decisions[d].Name).Append("]]").Append(Crlf);
                    listed++;
                    break;
                }
            }
            if (listed == 0) sb.Append("None.").Append(Crlf);
            return sb.ToString();
        }
        #endregion

        #region Staleness
        static void ReportStale(string root, List<Page> pages, int claimingPages)
        {
            for (int p = 0; p < claimingPages; p++)
            {
                Page page = pages[p];
                if (page.verifiedAgainst == null) continue;
                List<string> rootPaths = new List<string>(page.claims.Count);
                List<string> thoriumPaths = new List<string>(page.claims.Count);
                for (int c = 0; c < page.claims.Count; c++)
                {
                    if (page.claims[c].StartsWith("Thorium/", StringComparison.Ordinal)) thoriumPaths.Add(page.claims[c].Substring("Thorium/".Length));
                    else rootPaths.Add(page.claims[c]);
                }
                int total = 0;
                bool unknown = false;
                if (rootPaths.Count > 0) Accumulate(RevCount(root, page.verifiedAgainst, rootPaths), ref total, ref unknown);
                if (thoriumPaths.Count > 0) Accumulate(RevCount(root + "Thorium", page.verifiedAgainst, thoriumPaths), ref total, ref unknown);
                if (unknown) Console.Out.WriteLine("STALE " + page.path + ": unknown commit " + page.verifiedAgainst);
                else if (total > 0) Console.Out.WriteLine("STALE " + page.path + ": " + total + " commits since " + page.verifiedAgainst);
            }
        }

        static void Accumulate(int count, ref int total, ref bool unknown)
        {
            if (count < 0) unknown = true;
            else total += count;
        }

        static int RevCount(string repo, string commit, List<string> paths)
        {
            ProcessStartInfo info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("-C");
            info.ArgumentList.Add(repo);
            info.ArgumentList.Add("rev-list");
            info.ArgumentList.Add("--count");
            info.ArgumentList.Add(commit + "..HEAD");
            info.ArgumentList.Add("--");
            for (int i = 0; i < paths.Count; i++) info.ArgumentList.Add(paths[i]);
            using Process process = Process.Start(info)!;
            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 && int.TryParse(output.Trim(), out int count) ? count : -1;
        }
        #endregion

        #region Primitives
        static List<string> SplitLines(string text)
        {
            List<string> lines = new List<string>(text.Length / 32 + 1);
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                lines.Add(text.Substring(start, i + 1 - start));
                start = i + 1;
            }
            if (start < text.Length) lines.Add(text.Substring(start));
            return lines;
        }

        static int ClosingFence(List<string> lines)
        {
            if (lines.Count == 0 || Bare(lines[0]).TrimEnd() != "---") return -1;
            for (int i = 1; i < lines.Count; i++)
                if (Bare(lines[i]).TrimEnd() == "---") return i;
            return -1;
        }

        static bool IsGeneratedKey(string bare)
        {
            for (int k = 0; k < GeneratedKeys.Length; k++)
                if (bare.StartsWith(GeneratedKeys[k] + ":", StringComparison.Ordinal)) return true;
            return false;
        }

        static byte[] Encode(string text, bool bom)
        {
            int count = Encoding.UTF8.GetByteCount(text);
            int offset = bom ? 3 : 0;
            byte[] bytes = new byte[count + offset];
            if (bom) { bytes[0] = 0xEF; bytes[1] = 0xBB; bytes[2] = 0xBF; }
            Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, offset);
            return bytes;
        }

        static bool AllUnder(List<string> claims, string prefix)
        {
            for (int i = 0; i < claims.Count; i++)
                if (!claims[i].StartsWith(prefix, StringComparison.Ordinal)) return false;
            return true;
        }

        static void AddDistinct(List<string> into, List<string> from)
        {
            for (int i = 0; i < from.Count; i++)
                if (!into.Contains(from[i])) into.Add(from[i]);
        }

        static bool IsContinuation(string bare) => bare.Length > 0 && (bare[0] == ' ' || bare[0] == '\t');
        static string Bare(string line) => line.TrimEnd('\r', '\n');
        static string Unquote(string value) => value.Trim().Trim('"').Trim();
        static string ToFull(string root, string rel) => root + rel.Replace('/', '\\');
        static string ProjectOf(string file) => file.Substring(0, Math.Max(0, file.IndexOf('/')));

        static string ParentFolder(string file)
        {
            int last = file.LastIndexOf('/');
            return last < 0 ? "" : file.Substring(file.LastIndexOf('/', last - 1) + 1, last - file.LastIndexOf('/', last - 1) - 1);
        }
        #endregion

        #region Types
        sealed class Project
        {
            public readonly string name;
            public readonly string folder;
            public string assemblyName;
            public string tfm = "";
            public string[] defines = Array.Empty<string>();
            public readonly List<string> references = new List<string>(2);
            public readonly List<SyntaxTree> sourceTrees = new List<SyntaxTree>(256);
            public CSharpCompilation? compilation;

            public Project(string name, string folder, string assemblyName)
            {
                this.name = name;
                this.folder = folder;
                this.assemblyName = assemblyName;
            }
        }

        // facts the walker collects from one source file
        sealed class SourceInfo
        {
            public readonly List<string> namespaces = new List<string>(1);
            public readonly List<string> types = new List<string>(2);
            public readonly List<ActionRef> actions = new List<ActionRef>(0);
        }

        sealed class Page
        {
            public readonly string path;
            public readonly string name;
            public readonly List<string> claims;
            public readonly List<int> files = new List<int>(4);
            public readonly List<string> system = new List<string>(1);
            public string? verifiedAgainst;
            public string text = "";
            public bool bom;
            public byte[]? original;
            public bool inScope;

            // generated content
            public readonly List<string> namespaces = new List<string>(1);
            public readonly List<string> types = new List<string>(4);
            public readonly List<string> maintainers = new List<string>(1);
            public readonly HashSet<UseRow> uses = new HashSet<UseRow>(16);
            public readonly Dictionary<int, HashSet<string>> usedBy = new Dictionary<int, HashSet<string>>(8);

            public Page(string path, List<string> claims)
            {
                this.path = path;
                this.claims = claims;
                name = Path.GetFileNameWithoutExtension(path);
            }
        }

        readonly record struct Edge(int From, string Here, int To, string There);
        readonly record struct ActionRef(string Name, string Category);
        readonly record struct UseRow(string Here, string There, int Page);
        readonly record struct UsedByRow(string System, string Page, string Members);
        readonly record struct DataRow(string Action, string Category, string File);
        readonly record struct Decision(string Name, HashSet<string> Segments);
        readonly record struct Maintainer(Regex Glob, string Name);

        // Records one file's namespaces, top-level types, actions and cross-file references.
        sealed class EdgeWalker : CSharpSyntaxWalker
        {
            readonly SemanticModel model;
            readonly Dictionary<string, int> fileIndex;
            readonly HashSet<Edge> edges;
            readonly SourceInfo info;
            readonly int file;
            string here = "";
            string ns = "";
            int typeDepth;

            public EdgeWalker(SemanticModel model, Dictionary<string, int> fileIndex, HashSet<Edge> edges, SourceInfo info, int file)
            {
                this.model = model;
                this.fileIndex = fileIndex;
                this.edges = edges;
                this.info = info;
                this.file = file;
            }

            public override void Visit(SyntaxNode? node)
            {
                if (node == null) return;
                string savedHere = here;
                string savedNs = ns;
                int savedDepth = typeDepth;
                switch (node)
                {
                    case BaseNamespaceDeclarationSyntax declaration:
                        ns = ns.Length == 0 ? declaration.Name.ToString() : ns + "." + declaration.Name.ToString();
                        if (!info.namespaces.Contains(ns)) info.namespaces.Add(ns);
                        break;
                    case BaseTypeDeclarationSyntax type:
                        DeclareType(type.Identifier.Text, node);
                        break;
                    case DelegateDeclarationSyntax type:
                        DeclareType(type.Identifier.Text, node);
                        break;
                    case BaseMethodDeclarationSyntax method:
                        here = NameOf(model.GetDeclaredSymbol(node));
                        if (method is MethodDeclarationSyntax declared) CollectActions(declared);
                        break;
                    case BasePropertyDeclarationSyntax:
                        here = NameOf(model.GetDeclaredSymbol(node));
                        break;
                    case SimpleNameSyntax name:
                        ISymbol? symbol = model.GetSymbolInfo(name).Symbol;
                        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor && IsAttributeName(name)) symbol = ctor.ContainingType;
                        if (symbol is INamedTypeSymbol && IsQualifier(name)) break;
                        Record(symbol);
                        break;
                    case BaseObjectCreationExpressionSyntax or ConstructorInitializerSyntax or PrimaryConstructorBaseTypeSyntax:
                        Record(model.GetSymbolInfo(node).Symbol);
                        break;
                }
                base.Visit(node);
                here = savedHere;
                ns = savedNs;
                typeDepth = savedDepth;
            }

            void DeclareType(string name, SyntaxNode node)
            {
                if (typeDepth == 0 && !info.types.Contains(name)) info.types.Add(name);
                typeDepth++;
                here = NameOf(model.GetDeclaredSymbol(node));
            }

            void CollectActions(MethodDeclarationSyntax method)
            {
                for (int l = 0; l < method.AttributeLists.Count; l++)
                {
                    SeparatedSyntaxList<AttributeSyntax> attributes = method.AttributeLists[l].Attributes;
                    for (int a = 0; a < attributes.Count; a++)
                    {
                        AttributeSyntax attribute = attributes[a];
                        string name = attribute.Name is QualifiedNameSyntax q ? q.Right.Identifier.Text : attribute.Name.ToString();
                        if (name != "A_XSDActionDependency" && name != "A_XSDActionDependencyAttribute") continue;
                        if (attribute.ArgumentList == null || attribute.ArgumentList.Arguments.Count == 0) continue;
                        string? action = ConstantString(attribute.ArgumentList.Arguments[0].Expression);
                        if (action == null) continue;
                        string category = attribute.ArgumentList.Arguments.Count > 1 ? ConstantString(attribute.ArgumentList.Arguments[1].Expression) ?? "Any" : "Any";
                        info.actions.Add(new ActionRef(action, category));
                    }
                }
            }

            string? ConstantString(ExpressionSyntax expression)
            {
                Optional<object?> value = model.GetConstantValue(expression);
                return value.HasValue ? value.Value as string : null;
            }

            void Record(ISymbol? symbol)
            {
                if (here.Length == 0 || symbol == null) return;
                ISymbol? target = Normalise(symbol);
                if (target == null) return;
                int to = DeclaringFile(target);
                if (to < 0 || to == file) return;
                edges.Add(new Edge(file, here, to, NameOf(target)));
            }

            int DeclaringFile(ISymbol symbol)
            {
                int first = -1;
                System.Collections.Immutable.ImmutableArray<SyntaxReference> references = symbol.DeclaringSyntaxReferences;
                for (int i = 0; i < references.Length; i++)
                    if (fileIndex.TryGetValue(references[i].SyntaxTree.FilePath, out int f) && (first < 0 || f < first)) first = f;
                return first;
            }

            static ISymbol? Normalise(ISymbol symbol)
            {
                switch (symbol)
                {
                    case IMethodSymbol method:
                        method = (method.ReducedFrom ?? method).OriginalDefinition;
                        switch (method.MethodKind)
                        {
                            case MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise:
                                return method.AssociatedSymbol?.OriginalDefinition;
                            case MethodKind.LocalFunction or MethodKind.AnonymousFunction:
                                return null;
                        }
                        return method;
                    case INamedTypeSymbol type:
                        return type.TypeKind == TypeKind.Error || type.IsTupleType || type.IsAnonymousType ? null : type.OriginalDefinition;
                    case IFieldSymbol or IPropertySymbol or IEventSymbol:
                        INamedTypeSymbol? owner = symbol.ContainingType;
                        return owner == null || owner.IsTupleType || owner.IsAnonymousType ? null : symbol.OriginalDefinition;
                }
                return null;
            }

            static bool IsAttributeName(SimpleNameSyntax name)
            {
                SyntaxNode node = name;
                if (node.Parent is QualifiedNameSyntax q && q.Right == node) node = q;
                return node.Parent is AttributeSyntax attribute && attribute.Name == node;
            }

            // True for the left side of Type.Member, whose member is recorded on its own.
            static bool IsQualifier(SimpleNameSyntax name)
            {
                SyntaxNode node = name;
                if (node.Parent is MemberAccessExpressionSyntax inner && inner.Name == node) node = inner;
                else if (node.Parent is QualifiedNameSyntax q && q.Right == node) node = q;
                return node.Parent is MemberAccessExpressionSyntax access && access.Expression == node
                    || node.Parent is QualifiedNameSyntax qualified && qualified.Left == node;
            }

            static string NameOf(ISymbol? symbol)
            {
                switch (symbol)
                {
                    case null:
                        return "";
                    case INamedTypeSymbol type:
                        return type.ContainingType == null ? type.Name : NameOf(type.ContainingType) + "." + type.Name;
                    case IMethodSymbol method:
                        switch (method.MethodKind)
                        {
                            case MethodKind.Constructor or MethodKind.StaticConstructor:
                                return NameOf(method.ContainingType) + "..ctor";
                            case MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise:
                                return NameOf(method.AssociatedSymbol);
                            case MethodKind.LocalFunction or MethodKind.AnonymousFunction:
                                return NameOf(method.ContainingSymbol);
                        }
                        break;
                }
                return symbol.ContainingType == null ? symbol.Name : NameOf(symbol.ContainingType) + "." + symbol.Name;
            }
        }
        #endregion
    }
}
