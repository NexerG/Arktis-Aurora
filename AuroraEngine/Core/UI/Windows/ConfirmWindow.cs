using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using Silk.NET.Vulkan;
using System.Numerics;

namespace ArctisAurora.Core.UI.Windows
{
    // Asks a question with a row of answers and an optional check line. One window per process,
    // built on the first ask and hidden between them, the same shape as NoteNameWindow.
    public static class ConfirmWindow
    {
        private const string windowName = "confirm";
        private const uint windowWidth = 260;
        private const uint windowHeight = 82;

        private static RenderWindow _window = null!;
        private static RenderWindow? _source;
        private static StackPanelControl _column = null!;
        private static CheckBoxControl? _check;

        public static bool isOpen { get; private set; }

        public static void Ask(RenderWindow source, string message, Action onConfirm, Action? onCancel) =>
            Ask(source, message, null,
                ("Cancel", _ => onCancel?.Invoke()),
                ("Confirm", _ => onConfirm()));

        // A prompt at a time, as with the name prompt — a second ask would strand the first one's
        // callbacks. Each answer is handed the check line's state.
        public static unsafe void Ask(RenderWindow source, string message, string? check, params (string caption, Action<bool>? answer)[] answers)
        {
            if (isOpen || source == null) return;

            Build();
            _source = source;
            Fill(message, check, answers);

            Vector2 size = _column.Measure(new Vector2(windowWidth, float.MaxValue));
            uint width = UIScaling.ToPixels(source, Math.Max(windowWidth, (uint)MathF.Ceiling(size.X)));
            uint height = UIScaling.ToPixels(source, (uint)MathF.Ceiling(size.Y));
            _window.os.Resize(width, height);
            UIScaling.Apply(_window);

            AGlfwWindow._glfw.GetWindowPos(source.os.handle, out int sx, out int sy);
            AGlfwWindow._glfw.GetWindowSize(source.os.handle, out int sw, out int sh);
            _window.os.SetPosition(sx + (sw - (int)width) / 2, sy + (sh - (int)height) / 2);

            _window.os.Show();
            _window.os.Focus();
            _window.os.SeedIsInWindow();

            isOpen = true;
        }

        private static void Answer(Action<bool> answer)
        {
            bool isChecked = _check?.isChecked ?? false;
            Hide();
            answer(isChecked);
        }

        // Cleared before the callbacks, so a handler that asks again is not refused by the ask it
        // was called from.
        private static void Hide()
        {
            if (!isOpen) return;

            _window.os.Hide();
            isOpen = false;

            _source?.os.Focus();
            _source = null;
        }

        private static void Build()
        {
            if (_window != null) return;

            _window = Engine.OpenMenuWindow(windowName, windowWidth, windowHeight);

            WindowRoot root = new WindowRoot();
            root.AddChild(Content());
            _window.ui.uiRoot = root;
        }

        private static Control Content()
        {
            PanelControl ground = new PanelControl
            {
                role = PaletteRole.Surface,
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };

            _column = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Vertical,
                alpha = 0f,
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch,
                padding = new Thickness(14),
                Spacing = 10
            };

            ground.AddChild(_column);
            return ground;
        }

        // Rebuilds the message, check line and answers for one ask.
        private static void Fill(string message, string? check, (string caption, Action<bool>? answer)[] answers)
        {
            foreach (Entity child in _column.children.ToArray())
                child.Destroy();

            _column.AddChild(new LabelControl
            {
                text = message ?? string.Empty,
                fontSize = 15,
                preferredHeight = 20,
                horizontalPosition = 0f
            });

            _check = null;
            if (check != null)
            {
                StackPanelControl line = new StackPanelControl
                {
                    orientation = StackPanelControl.Orientation.Horizontal,
                    alpha = 0f,
                    preferredHeight = 22,
                    Spacing = 8
                };
                _check = new CheckBoxControl { role = PaletteRole.SubField };
                line.AddChild(_check);
                line.AddChild(new LabelControl { text = check, fontSize = 14, role = PaletteRole.MutedInk, widthStar = 1, horizontalPosition = 0f });
                _column.AddChild(line);
            }

            StackPanelControl buttons = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Horizontal,
                alpha = 0f,
                preferredHeight = 24,
                Spacing = 8
            };

            // The row fills the column, so the buttons are pushed right by a star child rather than
            // by aligning the row itself.
            buttons.AddChild(new PanelControl { widthStar = 1, alpha = 0f });
            foreach ((string caption, Action<bool>? answer) in answers)
            {
                ButtonControl button = Button(caption, () => Answer(answer!));
                button.enabled = answer != null;
                buttons.AddChild(button);
            }
            _column.AddChild(buttons);

            _column.InvalidateLayout();
        }

        private static ButtonControl Button(string caption, Action action)
        {
            ButtonControl button = new ButtonControl
            {
                preferredHeight = 24,
                padding = new Thickness(12f, 0f),
                role = PaletteRole.Chrome,
                cornerRole = CornerRole.Control
            };
            button.AddChild(new LabelControl { text = caption, fontSize = 14, role = PaletteRole.MutedInk });
            button.RegisterOnRelease(_ => { action(); return true; });
            return button;
        }
    }
}
