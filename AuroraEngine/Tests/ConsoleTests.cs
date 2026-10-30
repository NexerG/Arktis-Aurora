using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;

namespace ArctisAurora.Tests
{
    internal static class ConsoleTests
    {
        [A_XSDActionDependency("Console.ToggleAndAnswer", "Test")]
        private static IEnumerator<int> ToggleAndAnswer(TestContext t)
        {
            WindowRoot root = t.Show(new StackPanelControl());
            yield return 1;

            ConsoleControl.Toggle();
            ConsoleControl? console = ConsoleControl.current;
            t.Check(console != null && ReferenceEquals(root.children[^1], console), "Toggle attaches the console as the root's last child");
            t.Check(console != null && ReferenceEquals(UIEngine.activeControl, console.input), "its input line takes the focus");
            yield return 2;
            if (console == null) yield break;

            console.input.text = "Help";
            console.input.Commit();
            yield return 2;

            t.Check(console.rows.Count == 2, "Help adds the echoed line and one reply row");
            t.Check(console.rows.Count == 2 && ((LabelControl)console.rows[1]).text.Contains("Quit"), "the reply lists Quit");
            t.Check(ReferenceEquals(UIEngine.activeControl, console.input) && console.input.text.Length == 0,
                "the input is cleared and keeps the focus after Enter");

            ConsoleControl.Toggle();
            t.Check(console.parent == null, "Toggle again detaches it");
            t.Check(UIEngine.activeControl == null, "closing takes the focus off the hidden input");
        }
    }
}
