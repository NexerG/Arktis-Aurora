namespace ArctisAurora.Core.Filing.Serialization
{
    // A mountable source of engine/application data. Today the only backend is DirectoryMount
    // (a folder on disk). A PakMount (Valve-style archive) can be added later with no call-site
    // changes, because every lookup goes through VirtualFileSystem rather than touching the disk
    // directly. Logical paths are forward-slash, relative to a Data root (e.g. "XML/Documents/UI/UI.ui.axml").
    public interface IDataMount
    {
        bool FileExists(string relativePath);
        bool DirExists(string relativeDir);
        Stream Open(string relativePath);
        string GetFullPath(string relativePath);
        IEnumerable<string> Enumerate(string relativeDir, string pattern);
    }

    // Directory-backed mount: maps logical paths onto a real folder.
    public sealed class DirectoryMount : IDataMount
    {
        public string Root { get; }

        public DirectoryMount(string root)
        {
            Root = Path.GetFullPath(root);
        }

        private string Full(string rel) =>
            Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));

        public bool FileExists(string rel) => File.Exists(Full(rel));
        public bool DirExists(string rel) => Directory.Exists(Full(rel));
        public Stream Open(string rel) => File.OpenRead(Full(rel));
        public string GetFullPath(string rel) => Full(rel);

        public IEnumerable<string> Enumerate(string relDir, string pattern)
        {
            string dir = Full(relDir);
            return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : Array.Empty<string>();
        }
    }

    // Ordered, priority-first virtual file system. Single-file lookups return the first mount that
    // has the file (so an application can override an engine default by relative path). "Enumerate
    // all" unions across mounts, with the highest-priority mount winning on a file-name clash.
    public static class VirtualFileSystem
    {
        private static readonly List<IDataMount> _mounts = new List<IDataMount>();

        public static IReadOnlyList<IDataMount> Mounts => _mounts;

        // Mounts are appended in descending priority (mount the application layer first, engine last).
        public static void Mount(IDataMount mount) => _mounts.Add(mount);
        public static void MountFirst(IDataMount mount) => _mounts.Insert(0, mount);
        public static void ClearMounts() => _mounts.Clear();
        public static void Replace(IDataMount mount, IDataMount with) => _mounts[_mounts.IndexOf(mount)] = with;

        public static bool TryResolveFile(string relativePath, out string fullPath)
        {
            foreach (IDataMount m in _mounts)
            {
                if (m.FileExists(relativePath))
                {
                    fullPath = m.GetFullPath(relativePath);
                    return true;
                }
            }
            fullPath = null;
            return false;
        }

        // First mount that has the file; if none has it, falls back to the primary mount's path
        // (useful for write targets and clearer "file not found" messages).
        public static string ResolveFile(string relativePath)
        {
            if (TryResolveFile(relativePath, out string p))
                return p;
            return _mounts.Count > 0 ? _mounts[0].GetFullPath(relativePath) : relativePath;
        }

        // First mount that contains the directory; falls back to the primary mount's path.
        public static string ResolveDir(string relativeDir)
        {
            foreach (IDataMount m in _mounts)
                if (m.DirExists(relativeDir))
                    return m.GetFullPath(relativeDir);
            return _mounts.Count > 0 ? _mounts[0].GetFullPath(relativeDir) : relativeDir;
        }

        public static Stream Open(string relativePath)
        {
            foreach (IDataMount m in _mounts)
                if (m.FileExists(relativePath))
                    return m.Open(relativePath);
            throw new FileNotFoundException("No mount contains data file: " + relativePath);
        }

        // Union of files across all mounts under relativeDir; first mount wins on name clash.
        public static string[] EnumerateAll(string relativeDir, string pattern)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> result = new List<string>();
            foreach (IDataMount m in _mounts)
            {
                foreach (string f in m.Enumerate(relativeDir, pattern))
                {
                    if (seen.Add(Path.GetFileName(f)))
                        result.Add(f);
                }
            }
            return result.ToArray();
        }
    }
}
