using ArctisAurora.Core.Filing;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using System.Numerics;

namespace ArctisAurora.Core.UI.Windows
{
    // Shows a recovery key once and goes on only after the user says it is saved. One window per process, hidden between shows.
    public static class RecoveryKeyWindow
    {
        private const string windowName = "recovery-key";
        private const uint windowWidth = 460;
        private const uint windowHeight = 220;

        private static RenderWindow _window = null!;
        private static RenderWindow? _source;
        private static LabelControl _key = null!;
        private static LabelControl _hint = null!;
        private static CheckBoxControl _saved = null!;
        private static StackPanelControl _column = null!;

        private static Action? _onConfirm;
        private static Action? _onCancel;

        public static bool isOpen { get; private set; }

        public static unsafe void Show(RenderWindow source, string recovery, Action onConfirm, Action? onCancel)
        {
            if (isOpen || source == null) return;

            Build();
            _source = source;
            _onConfirm = onConfirm;
            _onCancel = onCancel;
            _key.text = recovery;
            _saved.isChecked = false;
            _hint.Hide();

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

        private static void Confirm()
        {
            if (!_saved.isChecked)
            {
                _hint.Show();
                return;
            }

            Action? confirm = _onConfirm;
            Hide();
            confirm?.Invoke();
        }

        private static void Cancel()
        {
            Action? cancel = _onCancel;
            Hide();
            cancel?.Invoke();
        }

        private static void Hide()
        {
            if (!isOpen) return;

            _window.os.Hide();
            isOpen = false;
            _onConfirm = null;
            _onCancel = null;
            _key.text = string.Empty;
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

            _column.AddChild(new LabelControl { text = "Your recovery key", fontSize = 15, preferredHeight = 20, horizontalPosition = 0f });
            _column.AddChild(new LabelControl
            {
                text = "It opens the vault if the password is lost. It is shown only now.",
                fontSize = 13, preferredHeight = 18, horizontalPosition = 0f, role = PaletteRole.MutedInk
            });

            _key = new LabelControl { fontSize = 15, preferredHeight = 22, horizontalPosition = 0f };
            _column.AddChild(_key);

            StackPanelControl savedRow = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Horizontal,
                alpha = 0f,
                preferredHeight = 20,
                Spacing = 8
            };
            _saved = new CheckBoxControl();
            savedRow.AddChild(_saved);
            savedRow.AddChild(new LabelControl { text = "I have saved this recovery key", fontSize = 13, preferredHeight = 18 });
            _column.AddChild(savedRow);

            _hint = new LabelControl { text = "Save the key first", fontSize = 13, preferredHeight = 18, horizontalPosition = 0f, role = PaletteRole.Danger };
            _hint.Hide();
            _column.AddChild(_hint);

            StackPanelControl buttons = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Horizontal,
                alpha = 0f,
                preferredHeight = 24,
                Spacing = 8
            };
            buttons.AddChild(Button("Copy", () => ClipboardText.Set(_key.text)));
            buttons.AddChild(new PanelControl { widthStar = 1, alpha = 0f });
            buttons.AddChild(Button("Cancel", Cancel));
            buttons.AddChild(Button("Lock vault", Confirm));
            _column.AddChild(buttons);

            ground.AddChild(_column);
            return ground;
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
