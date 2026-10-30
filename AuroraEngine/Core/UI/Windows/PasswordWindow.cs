using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using System.Numerics;

namespace ArctisAurora.Core.UI.Windows
{
    // Asks for a password, or a new one typed twice. One window per process, hidden between asks.
    public static class PasswordWindow
    {
        private const string windowName = "password";
        private const uint windowWidth = 320;
        private const uint windowHeight = 170;

        private static RenderWindow _window = null!;
        private static RenderWindow? _source;
        private static Control? _restoreActive;
        private static LabelControl _title = null!;
        private static LabelControl _error = null!;
        private static TextBoxControl _field = null!;
        private static TextBoxControl _repeat = null!;
        private static StackPanelControl _column = null!;
        private static ButtonControl _alternative = null!;
        private static LabelControl _alternativeLabel = null!;

        private static Action<string>? _onConfirm;
        private static Action? _onCancel;
        private static Action? _onAlternative;
        private static bool _twice;

        public static bool isOpen { get; private set; }

        // A prompt at a time; twice asks for the password again and refuses a mismatch; alternative is a button that closes it another way.
        public static unsafe void Ask(RenderWindow source, string title, bool twice, string? error, Action<string> onConfirm, Action? onCancel,
            (string caption, Action action)? alternative = null)
        {
            if (isOpen || source == null) return;

            Build();
            _source = source;
            _onConfirm = onConfirm;
            _onCancel = onCancel;
            _onAlternative = alternative?.action;
            _twice = twice;
            _alternativeLabel.text = alternative?.caption ?? string.Empty;
            if (alternative != null) _alternative.Show();
            else _alternative.Hide();

            _title.text = title;
            _field.text = string.Empty;
            _repeat.text = string.Empty;
            if (twice) _repeat.Show();
            else _repeat.Hide();
            ShowError(error);

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

            _restoreActive = UIEngine.activeControl;
            UIEngine.SetActiveControl(_field);
            _field.Focus();

            isOpen = true;
        }

        private static void Confirm()
        {
            string password = _field.text;
            if (password.Length == 0) return;
            if (_twice && password != _repeat.text)
            {
                ShowError("The passwords differ");
                return;
            }

            Action<string>? confirm = _onConfirm;
            Hide();
            confirm?.Invoke(password);
        }

        private static void Cancel()
        {
            Action? cancel = _onCancel;
            Hide();
            cancel?.Invoke();
        }

        private static void Alternative()
        {
            Action? alternative = _onAlternative;
            Hide();
            alternative?.Invoke();
        }

        private static void ShowError(string? error)
        {
            _error.text = error ?? string.Empty;
            if (string.IsNullOrEmpty(error)) _error.Hide();
            else _error.Show();
        }

        private static void Hide()
        {
            if (!isOpen) return;

            _window.os.Hide();
            isOpen = false;
            _onConfirm = null;
            _onCancel = null;
            _onAlternative = null;
            _field.text = string.Empty;
            _repeat.text = string.Empty;

            UIEngine.SetActiveControl(_restoreActive);
            _restoreActive = null;

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

            _title = new LabelControl { fontSize = 15, preferredHeight = 20, horizontalPosition = 0f };
            _column.AddChild(_title);

            _field = Field();
            _column.AddChild(_field);
            _repeat = Field();
            _column.AddChild(_repeat);

            _error = new LabelControl { fontSize = 13, preferredHeight = 18, horizontalPosition = 0f, role = PaletteRole.Danger };
            _column.AddChild(_error);

            StackPanelControl buttons = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Horizontal,
                alpha = 0f,
                preferredHeight = 24,
                Spacing = 8
            };
            _alternative = Button(string.Empty, Alternative);
            _alternativeLabel = (LabelControl)_alternative.children[0];
            buttons.AddChild(_alternative);
            buttons.AddChild(new PanelControl { widthStar = 1, alpha = 0f });
            buttons.AddChild(Button("Cancel", Cancel));
            buttons.AddChild(Button("OK", Confirm));
            _column.AddChild(buttons);

            ground.AddChild(_column);
            return ground;
        }

        private static TextBoxControl Field()
        {
            TextBoxControl field = new TextBoxControl
            {
                masked = true,
                preferredHeight = 30,
                horizontalAlignment = HorizontalAlignment.Stretch,
                role = PaletteRole.SubField,
                fontSize = 15,
                padding = new Thickness(0, 6, 0, 8)
            };
            field.onCommit = _ => Confirm();
            field.onCancel = Cancel;
            return field;
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
