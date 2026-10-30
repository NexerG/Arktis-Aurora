using ArctisAurora.Core.Editing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using System.Numerics;

namespace ArctisAurora.Tests
{
    internal static class InputTests
    {
        [A_XSDActionDependency("Input.ClickFiresRelease", "Test")]
        private static IEnumerator<int> ClickFiresRelease(TestContext t)
        {
            int released = 0;
            ButtonControl button = new ButtonControl
            {
                preferredWidth = 120f,
                preferredHeight = 40f,
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top
            };
            button.RegisterOnRelease(_ => { released++; return true; });
            t.Show(button);
            yield return 2;

            yield return t.Click(button);
            t.Check(released == 1, "one click fires onRelease once");
        }

        [A_XSDActionDependency("Input.KeyWithModifierFiresKeybind", "Test")]
        private static IEnumerator<int> KeyWithModifierFiresKeybind(TestContext t)
        {
            t.Show(new StackPanelControl());
            yield return 2;

            yield return t.Key(Keys.GraveAccent, Keys.LeftControl);
            ConsoleControl? console = ConsoleControl.current;
            t.Check(console != null && console.parent != null, "Ctrl+` fires Console.Toggle and the console opens");

            if (console != null && console.parent != null) ConsoleControl.Toggle();
        }

        [A_XSDActionDependency("Input.DragMovesSplitter", "Test")]
        private static IEnumerator<int> DragMovesSplitter(TestContext t)
        {
            StackPanelControl row = new StackPanelControl { orientation = StackPanelControl.Orientation.Horizontal };
            StackPanelControl left = new StackPanelControl { preferredWidth = 200f };
            SplitterControl splitter = new SplitterControl { preferredWidth = 6f };
            row.AddChild(left);
            row.AddChild(splitter);
            row.AddChild(new StackPanelControl { widthStar = 1f });
            t.Show(row);
            yield return 2;

            LayoutRect grip = splitter.arrangedRect;
            Vector2 to = new Vector2(grip.x + grip.width * 0.5f + 50f, grip.y + grip.height * 0.5f);
            yield return t.Drag(splitter, to);
            yield return 2;

            t.Check(left.preferredWidth == 250f, "dragging the splitter 50 right widens the pane before it to 250");
            t.Check(left.arrangedRect.width == 250f, "and the pane is arranged 250 wide");
        }

        [A_XSDActionDependency("Input.RepeatJoinsUndoStep", "Test")]
        private static IEnumerator<int> RepeatJoinsUndoStep(TestContext t)
        {
            UndoStack stack = new UndoStack();
            List<int> undone = new List<int>();
            void Step(string label, bool join, int id)
            {
                using (stack.Begin(label, join))
                    stack.Push(new CountRecord(undone, id));
            }

            Step("Backspace", false, 1);
            Step("Backspace", true, 2);
            Step("Backspace", true, 3);
            stack.Undo();
            t.Check(undone.SequenceEqual(new[] { 3, 2, 1 }) && !stack.CanUndo, "a press and its repeats undo as one step");

            stack.Clear();
            undone.Clear();
            Step("Backspace", false, 1);
            Step("Backspace", false, 2);
            Step("Typing", true, 3);
            stack.Undo();
            t.Check(undone.SequenceEqual(new[] { 3 }), "a repeat under another label starts its own step");
            stack.Undo();
            t.Check(undone.SequenceEqual(new[] { 3, 2 }), "two presses stay two steps");

            stack.Redo();
            Step("Backspace", true, 4);
            stack.Undo();
            t.Check(undone.SequenceEqual(new[] { 3, 2, 4 }), "a repeat does not join a step with redo pending");
            yield break;
        }

        [A_XSDActionDependency("Input.DropdownInsidePopup", "Test")]
        private static IEnumerator<int> DropdownInsidePopup(TestContext t)
        {
            StackPanelControl ground = new StackPanelControl();
            t.Show(ground);
            yield return 2;

            string? picked = null;
            bool closed = false;
            DropdownControl dropdown = new DropdownControl { options = new[] { "One", "Two" }, selected = "One", preferredWidth = 120f, preferredHeight = 24f };
            dropdown.onPicked = value => picked = value;
            StackPanelControl content = new StackPanelControl { alpha = 0f };
            content.AddChild(dropdown);
            ContextMenus.Open(new List<ContextMenuEntry> { new ContextMenuContent(content) }, ground, new Vector2(40f, 40f), 200f, onClosed: () => closed = true);
            yield return 20;

            yield return t.Click(dropdown);
            yield return 20;
            ContextMenuControl.Row? two = Rows(ground).FirstOrDefault(row => (row.entry as ContextMenuButton)?.text == "Two");
            t.Check(two != null && !closed, "the dropdown's list opens and the popup holding it stays open");
            if (two == null) yield break;

            yield return t.Click(two);
            yield return 2;
            t.Check(picked == "Two" && dropdown.selected == "Two", "picking an option reaches the dropdown");
            t.Check(!closed && dropdown.parent != null && !Rows(ground).Any(row => (row.entry as ContextMenuButton)?.text == "Two"),
                "the pick closes the list and leaves the popup open");

            ContextMenus.Close();
            t.Check(closed, "closing the menu closes the popup");
        }

        [A_XSDActionDependency("Input.SourceSubmenu", "Test")]
        private static IEnumerator<int> SourceSubmenu(TestContext t)
        {
            StackPanelControl ground = new StackPanelControl();
            t.Show(ground);
            yield return 2;

            int built = 0;
            ContextMenus.RegisterSource("test-source", () =>
            {
                built++;
                return new List<ContextMenuEntry> { new ContextMenuButton($"Built {built}", () => { }) };
            });
            ContextMenuSubmenu more = new ContextMenuSubmenu { text = "More", source = "test-source" };
            ContextMenus.Open(new List<ContextMenuEntry> { more }, ground, new Vector2(40f, 40f), 160f);
            yield return 20;
            t.Check(built == 0, "a sourced submenu builds nothing until it opens");

            ContextMenuControl.Row row = Rows(ground).First(r => ReferenceEquals(r.entry, more));
            yield return t.MoveTo(row);
            yield return 20;
            t.Check(built == 1 && Rows(ground).Any(r => (r.entry as ContextMenuButton)?.text == "Built 1"), "hovering it builds its entries then");
            ContextMenus.Close();
        }

        [A_XSDActionDependency("Input.DropToggles", "Test")]
        private static IEnumerator<int> DropToggles(TestContext t)
        {
            TabToolsControl tools = new TabToolsControl();
            LabelControl caption = tools.Text("Size", owner =>
                TabToolsControl.Drop(owner, new List<ContextMenuEntry> { new ContextMenuButton("A4", () => { }) }), true);
            t.Show(tools);
            yield return 2;

            Control button = (Control)((Control)caption.parent).parent;
            bool Open() => Rows(tools).Any(row => (row.entry as ContextMenuButton)?.text == "A4");

            yield return t.Click(button);
            yield return 20;
            t.Check(Open(), "a press on a drop button opens its menu");

            yield return t.Click(button);
            yield return 20;
            t.Check(!Open() && ContextMenus.target == null, "a second press on the same button closes the menu and does not reopen it");

            yield return t.Click(button);
            yield return 20;
            t.Check(Open(), "a third press opens it again");
            ContextMenus.Close();
        }

        // Menu rows open in the control's window.
        private static List<ContextMenuControl.Row> Rows(Control within)
        {
            List<ContextMenuControl.Row> rows = new List<ContextMenuControl.Row>();
            Collect(UIEngine.WindowOf(within)!.ui.uiRoot, rows);
            return rows;
        }

        private static void Collect(ArctisAurora.Core.ECS.EngineEntity.Entity entity, List<ContextMenuControl.Row> rows)
        {
            if (entity is ContextMenuControl.Row row && !row.destroyed) rows.Add(row);
            foreach (ArctisAurora.Core.ECS.EngineEntity.Entity child in entity.children)
                Collect(child, rows);
        }

        private sealed class CountRecord : IEditRecord
        {
            private readonly List<int> _undone;
            private readonly int _id;

            public CountRecord(List<int> undone, int id)
            {
                _undone = undone;
                _id = id;
            }

            public void Undo() => _undone.Add(_id);

            public void Redo() { }
        }
    }
}
