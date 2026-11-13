using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using System.Xml.Linq;

namespace ArctisAurora.Tests
{
    internal static class FileAssociationTests
    {
        private const string app = "Thorium";
        private const string exe = @"C:\Apps\Thorium\Thorium.exe";

        [A_XSDActionDependency("FileTypes.ClaimOne", "Test")]
        private static IEnumerator<int> ClaimOne(TestContext t)
        {
            FileType md = FileTypes.Of("a.md")!;
            List<RegistryEntry> entries = FileAssociations.WindowsEntries(app, exe, new[] { md });

            t.Check(Value(entries, @"Software\Classes\Thorium.md\shell\open\command", "") == $"\"{exe}\" \"%1\"", "a ProgID opens the file with the exe");
            t.Check(FileAssociations.Registered.All(type => Value(entries, $@"Software\Classes\{type.suffix}\OpenWithProgids", FileAssociations.ProgId(app, type)) == ""),
                "every registered type lists the app under Open With");
            t.Check(Value(entries, @"Software\Classes\.md", "") == "Thorium.md", "the claimed type gets the app as its default");
            t.Check(FileAssociations.Registered.Where(type => type != md).All(type => Value(entries, @"Software\Classes\" + type.suffix, "") == null),
                "no other type's default is touched");
            t.Check(Value(entries, @"Software\RegisteredApplications", app) == @"Software\Thorium\Capabilities"
                && Value(entries, @"Software\Thorium\Capabilities\FileAssociations", ".axml") == "Thorium.axml", "capabilities name every type");
            yield break;
        }

        [A_XSDActionDependency("FileTypes.ClaimAll", "Test")]
        private static IEnumerator<int> ClaimAll(TestContext t)
        {
            List<RegistryEntry> entries = FileAssociations.WindowsEntries(app, exe, FileAssociations.Registered);
            t.Check(FileAssociations.Registered.Select(type => type.suffix).OrderBy(s => s).SequenceEqual(new[] { ".axml", ".md", ".txt", ".xml" }),
                "the four registered types are .axml, .xml, .md and .txt");
            t.Check(FileAssociations.Registered.All(type => Value(entries, @"Software\Classes\" + type.suffix, "") == FileAssociations.ProgId(app, type)),
                "claiming all sets every registered type's default");
            yield break;
        }

        [A_XSDActionDependency("FileTypes.LinuxFiles", "Test")]
        private static IEnumerator<int> LinuxFiles(TestContext t)
        {
            string desktop = FileAssociations.DesktopEntry(app, "/opt/thorium/Thorium");
            t.Check(desktop.Contains("Exec=\"/opt/thorium/Thorium\" %f\n"), "the desktop entry opens one file with the exe");
            t.Check(FileAssociations.Registered.All(type => desktop.Contains(type.mime + ";")), $"the desktop entry lists every MIME type: {desktop}");

            XNamespace ns = "http://www.freedesktop.org/standards/shared-mime-info";
            XElement[] declared = FileAssociations.MimePackage().Elements(ns + "mime-type").ToArray();
            t.Check(declared.Length == 1 && (string?)declared[0].Attribute("type") == "application/x-aurora+xml"
                && (string?)declared[0].Element(ns + "glob")?.Attribute("pattern") == "*.axml"
                && (string?)declared[0].Element(ns + "sub-class-of")?.Attribute("type") == "application/xml",
                "the mime package declares only .axml, as an XML subtype");
            yield break;
        }

        [A_XSDActionDependency("FileTypes.IsDefault", "Test")]
        private static IEnumerator<int> IsDefault(TestContext t)
        {
            t.Check(FileAssociations.EffectiveProgId("Thorium.md", "Other.md", "Other.md") == "Thorium.md", "the latest user choice wins");
            t.Check(FileAssociations.EffectiveProgId(null, "Other.md", "Thorium.md") == "Other.md", "an older user choice beats the class default");
            t.Check(FileAssociations.EffectiveProgId(null, null, "Thorium.md") == "Thorium.md", "with no user choice the class default decides");
            t.Check(FileAssociations.EffectiveProgId(null, null, null) == null, "an unassociated type has no ProgID");
            yield break;
        }

        private static string? Value(List<RegistryEntry> entries, string key, string name) =>
            entries.LastOrDefault(e => e.key == key && e.name == name) is { key: not null } entry ? entry.value : null;
    }
}
