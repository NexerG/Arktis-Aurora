using ArctisAurora.Core.Commands;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;

namespace ArctisAurora.Tests
{
    internal static class CommandTests
    {
        [A_XSDActionDependency("Commands.HelpListsCommands", "Test")]
        private static IEnumerator<int> HelpListsCommands(TestContext t)
        {
            string? reply = null;
            CommandConsole.Execute("Help", r => reply = r);
            t.Check(reply != null, "Help answers in the same tick");
            t.Check(reply != null && reply.Contains("Quit") && reply.Contains("Wait") && reply.Contains("UI.DumpTree"),
                "Help lists Quit, Wait and the Input action UI.DumpTree");
            yield break;
        }

        [A_XSDActionDependency("Commands.UnknownIsAnError", "Test")]
        private static IEnumerator<int> UnknownIsAnError(TestContext t)
        {
            string? reply = null;
            CommandConsole.Execute("NoSuchCommand; Help", r => reply = r);
            string[] lines = reply?.Split('\n') ?? Array.Empty<string>();
            t.Check(lines.Length == 2, "one reply line per command");
            t.Check(lines.Length > 0 && lines[0] == "error: no command named NoSuchCommand", "an unknown name answers error:");
            yield break;
        }

        [A_XSDActionDependency("Commands.ArgumentsConvert", "Test")]
        private static IEnumerator<int> ArgumentsConvert(TestContext t)
        {
            t.Check(CommandConsole.TryConvert("42", typeof(int), out object? i) && (int)i! == 42, "int");
            t.Check(CommandConsole.TryConvert("1.5", typeof(float), out object? f) && (float)f! == 1.5f, "float, invariant culture");
            t.Check(CommandConsole.TryConvert("true", typeof(bool), out object? b) && (bool)b!, "bool");
            t.Check(CommandConsole.TryConvert("hello", typeof(string), out object? s) && (string)s! == "hello", "string");
            t.Check(CommandConsole.TryConvert("vertical", typeof(StackPanelControl.Orientation), out object? e)
                && (StackPanelControl.Orientation)e! == StackPanelControl.Orientation.Vertical, "enum, any case");
            t.Check(!CommandConsole.TryConvert("abc", typeof(int), out _), "a non-number is not an int");
            yield break;
        }

        [A_XSDActionDependency("Commands.WaitResumesOnTime", "Test")]
        private static IEnumerator<int> WaitResumesOnTime(TestContext t)
        {
            string? reply = null;
            CommandConsole.Execute("Wait 100; Help", r => reply = r);
            t.Check(reply == null, "a waiting line has not replied yet");
            yield return 5;
            t.Check(reply == null, "still waiting after 5 ticks (83 ms)");
            yield return 3;
            t.Check(reply != null && reply.Contains("Quit"), "resumed and answered by 8 ticks (133 ms)");
        }
    }
}
