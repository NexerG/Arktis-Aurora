using ArctisAurora.Core.Commands;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Threading;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;

namespace ArctisAurora.Core.UI.Controls
{
    // The command console across the top of the primary window: reply rows over an input line, hosted as the window
    // root's last child so it paints over the tree and is hit first.
    public class ConsoleControl : StackPanelControl
    {
        private const int maxRows = 200;
        private const float consoleHeight = 240f;
        private const int rowFontSize = 13;

        private static ConsoleControl? _console;

        private readonly ScrollableControl _scroll = new ScrollableControl();
        private readonly StackPanelControl _rows = new StackPanelControl();
        private readonly TextBoxControl _input = new TextBoxControl();

        // the newest row, scrolled into view once it has been laid out
        private LabelControl? _newest;
        private long _addedFrame;

        internal static ConsoleControl? current => _console;
        internal TextBoxControl input => _input;
        internal IReadOnlyList<Entity> rows => _rows.children;

        public ConsoleControl()
        {
            role = PaletteRole.Ground;
            padding = new Thickness(6f);
            Spacing = 4f;
            preferredHeight = consoleHeight;
            horizontalAlignment = HorizontalAlignment.Stretch;
            verticalAlignment = VerticalAlignment.Top;

            _scroll.heightStar = 1f;
            _scroll.AddChild(_rows);
            AddChild(_scroll);

            _input.horizontalAlignment = HorizontalAlignment.Stretch;
            _input.onCommit = Submit;
            _input.onCancel = Toggle;
            AddChild(_input);
        }

        [A_XSDActionDependency("Console.Toggle", "Input", "Opens or closes the command console over the primary window")]
        public static void Toggle()
        {
            WindowRoot root = Engine.primary.ui.uiRoot;
            if (root == null) return;

            if (_console != null && ReferenceEquals(_console.parent, root))
            {
                if (ReferenceEquals(UIEngine.activeControl, _console._input)) UIEngine.SetActiveControl(null!);
                root.RemoveChild(_console);
                return;
            }

            if (_console == null || _console.parent != null) _console = new ConsoleControl();
            root.AddChild(_console);
            _console.FocusInput();
        }

        private void FocusInput()
        {
            UIEngine.SetActiveControl(_input);
            _input.Focus();
        }

        private void Submit(string line)
        {
            _input.text = string.Empty;
            if (line.Trim().Length > 0)
            {
                AddRow("> " + line);
                CommandConsole.Execute(line, reply =>
                {
                    foreach (string text in reply.Split('\n')) AddRow(text);
                });
            }
            FocusInput();
        }

        private void AddRow(string text)
        {
            if (_rows.children.Count >= maxRows) _rows.children[0].Destroy();

            _newest = new LabelControl { text = text, fontSize = rowFontSize, horizontalAlignment = HorizontalAlignment.Left };
            _rows.AddChild(_newest);
            _addedFrame = FrameScheduler.Frame;
            SetTicking(true);
        }

        public override void OnTick()
        {
            base.OnTick();
            if (FrameScheduler.Frame <= _addedFrame) return;

            if (_newest != null) _scroll.ScrollIntoView(_newest.arrangedRect);
            SetTicking(false);
        }
    }
}
