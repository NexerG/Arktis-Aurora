using ArctisAurora.Core.Diagnostics;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml.Linq;
using WinRegistry = Microsoft.Win32.Registry;

namespace ArctisAurora.Core.Filing
{
    // One value under HKEY_CURRENT_USER; an empty name is the key's default value.
    public readonly record struct RegistryEntry(string key, string name, string value);

    // Registers the running executable with the operating system for the types FileTypes marks Register.
    public static class FileAssociations
    {
        private static readonly LogChannel Log = LogChannel.For("Filing");

        // set by a host that offers its file types in Settings
        public static bool enabled;

        public static IEnumerable<FileType> Registered => FileTypes.all.Where(type => type.register);

        // Registers every type and makes this app the default for one.
        public static void Claim(FileType type) => Apply(new[] { type });

        public static void ClaimAll() => Apply(Registered.ToArray());

        private static void Apply(FileType[] claimed)
        {
            string app = Assembly.GetEntryAssembly()?.GetName().Name ?? "Aurora";
            string exe = Environment.ProcessPath ?? "";
            if (OperatingSystem.IsWindows()) ApplyWindows(app, exe, claimed);
            else if (OperatingSystem.IsLinux()) ApplyLinux(app, exe, claimed);
            else
            {
                Log.Warn($"file types cannot be registered on this OS yet");
                return;
            }
            Log.Info($"registered {app} for {string.Join(", ", Registered.Select(t => t.suffix))}; default for {string.Join(", ", claimed.Select(t => t.suffix))}");
        }

        public static string ProgId(string app, FileType type) => app + "." + type.suffix.TrimStart('.');

        // Whether the system already opens this type with the running app.
        public static bool IsDefault(FileType type)
        {
            string app = Assembly.GetEntryAssembly()?.GetName().Name ?? "Aurora";
            if (OperatingSystem.IsWindows()) return IsDefaultWindows(app, type);
            if (OperatingSystem.IsLinux()) return Query("xdg-mime", "query", "default", type.mime) == app.ToLowerInvariant() + ".desktop";
            return false;
        }

        #region ---- windows ----
        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0;

        // ProgIDs, Open With lists and capabilities for every type; the extension's default only for claimed ones.
        public static List<RegistryEntry> WindowsEntries(string app, string exe, IEnumerable<FileType> claimed)
        {
            const string classes = @"Software\Classes\";
            string capabilities = $@"Software\{app}\Capabilities";
            List<RegistryEntry> entries = new List<RegistryEntry>
            {
                new RegistryEntry(capabilities, "ApplicationName", app),
                new RegistryEntry(capabilities, "ApplicationDescription", app),
                new RegistryEntry(@"Software\RegisteredApplications", app, capabilities)
            };

            foreach (FileType type in Registered)
            {
                string progId = ProgId(app, type);
                entries.Add(new RegistryEntry(classes + progId, "", type.description));
                entries.Add(new RegistryEntry(classes + progId + @"\DefaultIcon", "", $"\"{exe}\",0"));
                entries.Add(new RegistryEntry(classes + progId + @"\shell\open\command", "", $"\"{exe}\" \"%1\""));
                entries.Add(new RegistryEntry(classes + type.suffix + @"\OpenWithProgids", progId, ""));
                entries.Add(new RegistryEntry(capabilities + @"\FileAssociations", type.suffix, progId));
            }

            foreach (FileType type in claimed)
                entries.Add(new RegistryEntry(classes + type.suffix, "", ProgId(app, type)));
            return entries;
        }

        [SupportedOSPlatform("windows")]
        private static void ApplyWindows(string app, string exe, FileType[] claimed)
        {
            foreach (RegistryEntry entry in WindowsEntries(app, exe, claimed))
            {
                using RegistryKey key = WinRegistry.CurrentUser.CreateSubKey(entry.key);
                key.SetValue(entry.name, entry.value);
            }
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

            string[] kept = claimed.Where(type => ChosenElsewhere(app, type)).Select(type => type.suffix).ToArray();
            if (kept.Length == 0) return;
            Log.Info($"Windows keeps the app you picked for {string.Join(", ", kept)}; choose {app} in Default apps");
            Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(app)}") { UseShellExecute = true });
        }

        // A default the user picked in Windows, which no app may overwrite.
        [SupportedOSPlatform("windows")]
        private static bool ChosenElsewhere(string app, FileType type)
        {
            foreach (string name in new[] { "UserChoice", "UserChoiceLatest" })
            {
                using RegistryKey? choice = WinRegistry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{type.suffix}\{name}");
                if (choice?.GetValue("ProgId") is string progId && progId != ProgId(app, type)) return true;
            }
            return false;
        }

        [SupportedOSPlatform("windows")]
        private static bool IsDefaultWindows(string app, FileType type)
        {
            string fileExts = $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{type.suffix}\";
            using RegistryKey? latest = WinRegistry.CurrentUser.OpenSubKey(fileExts + "UserChoiceLatest");
            using RegistryKey? choice = WinRegistry.CurrentUser.OpenSubKey(fileExts + "UserChoice");
            using RegistryKey? classes = WinRegistry.ClassesRoot.OpenSubKey(type.suffix);
            return EffectiveProgId(latest?.GetValue("ProgId") as string, choice?.GetValue("ProgId") as string, classes?.GetValue("") as string) == ProgId(app, type);
        }

        // The ProgID Windows opens a type with: the latest user choice, then the older one, then the class default.
        public static string? EffectiveProgId(string? latest, string? choice, string? classes) => latest ?? choice ?? classes;

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
        #endregion

        #region ---- linux ----
        private static readonly XNamespace mimeInfo = "http://www.freedesktop.org/standards/shared-mime-info";

        public static string DesktopEntry(string app, string exe) =>
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            $"Name={app}\n" +
            $"Exec=\"{exe}\" %f\n" +
            "Terminal=false\n" +
            $"MimeType={string.Concat(Registered.Select(type => type.mime + ";"))}\n";

        // Declares the types the system cannot already know: the unregistered application/x- ones.
        public static XElement MimePackage() =>
            new XElement(mimeInfo + "mime-info",
                Registered.Where(type => type.mime.StartsWith("application/x-")).Select(type =>
                    new XElement(mimeInfo + "mime-type", new XAttribute("type", type.mime),
                        new XElement(mimeInfo + "comment", type.description),
                        type.mime.EndsWith("+xml") ? new XElement(mimeInfo + "sub-class-of", new XAttribute("type", "application/xml")) : null,
                        new XElement(mimeInfo + "glob", new XAttribute("pattern", "*" + type.suffix)))));

        private static void ApplyLinux(string app, string exe, FileType[] claimed)
        {
            string data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string applications = Path.Combine(data, "applications");
            string mime = Path.Combine(data, "mime");
            string desktop = app.ToLowerInvariant() + ".desktop";

            Directory.CreateDirectory(applications);
            Directory.CreateDirectory(Path.Combine(mime, "packages"));
            File.WriteAllText(Path.Combine(applications, desktop), DesktopEntry(app, exe));
            MimePackage().Save(Path.Combine(mime, "packages", app.ToLowerInvariant() + ".xml"));

            Run("update-mime-database", mime);
            Run("update-desktop-database", applications);
            foreach (FileType type in claimed)
                Run("xdg-mime", "default", desktop, type.mime);
        }

        private static void Run(string tool, params string[] arguments)
        {
            try
            {
                using Process? process = Process.Start(new ProcessStartInfo(tool, arguments) { UseShellExecute = false });
                process?.WaitForExit();
            }
            catch (Win32Exception)
            {
                Log.Warn($"{tool} is not installed; skipped");
            }
        }

        // Runs a tool and returns its trimmed output, or null when it is not installed.
        private static string? Query(string tool, params string[] arguments)
        {
            try
            {
                using Process? process = Process.Start(new ProcessStartInfo(tool, arguments) { UseShellExecute = false, RedirectStandardOutput = true });
                if (process == null) return null;
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return output;
            }
            catch (Win32Exception)
            {
                return null;
            }
        }
        #endregion
    }
}
