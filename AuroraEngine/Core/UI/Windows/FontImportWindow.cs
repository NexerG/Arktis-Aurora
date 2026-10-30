using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using System.Numerics;

namespace ArctisAurora.Core.UI.Windows
{
    // A wait message while a font bakes; shows the error and an OK when the bake fails.
    public static class FontImportWindow
    {
        private const string windowName = "font-import";
        private const uint windowWidth = 320;
        private const uint windowHeight = 80;

        private static RenderWindow _window = null!;
        private static RenderWindow? _source;
        private static LabelControl _text = null!;
        private static StackPanelControl _okRow = null!;
        private static StackPanelControl _column = null!;

        public static bool isOpen { get; private set; }

        public static void Show(RenderWindow source, string text)
        {
            if (source == null) return;

            Build();
            _source = source;
            _text.text = text;
            _okRow.Hide();
            Place();
            isOpen = true;
        }

        public static void Fail(string text)
        {
            if (!isOpen) return;

            _text.text = text;
            _okRow.Show();
            Place();
        }

        public static void Hide()
        {
            if (!isOpen) return;

            _window.os.Hide();
            isOpen = false;
            _source?.os.Focus();
            _source = null;
        }

        // Sizes to the content and centres over the source window.
        private static unsafe void Place()
        {
            RenderWindow source = _source!;
            Vector2 size = _column.Measure(new Vector2(windowWidth, float.MaxValue));
            uint width = UIScaling.ToPixels(source, Math.Max(windowWidth, (uint)MathF.Ceiling(size.X)));
            uint height = UIScaling.ToPixels(source, (uint)MathF.Ceiling(size.Y));
            _window.os.Resize(width, height);
            UIScaling.Apply(_window);

            AGlfwWindow._glfw.GetWindowPos(source.os.handle, out int sx, out int sy);
            AGlfwWindow._glfw.GetWindowSize(source.os.handle, out int sw, out int sh);
            _window.os.SetPosition(sx + (sw - (int)width) / 2, sy + (sh - (int)height) / 2);

            _window.os.Show();
            _window.os.SeedIsInWindow();
        }

        private static void Build()
        {
            if (_window != null) return;

            _window = Engine.OpenMenuWindow(windowName, windowWidth, windowHeight);

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

            _text = new LabelControl { fontSize = 15, preferredHeight = 20, horizontalPosition = 0f };
            _column.AddChild(_text);

            _okRow = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Horizontal,
                alpha = 0f,
                preferredHeight = 24
            };
            _okRow.AddChild(new PanelControl { widthStar = 1, alpha = 0f });
            ButtonControl ok = new ButtonControl
            {
                preferredHeight = 24,
                padding = new Thickness(12f, 0f),
                role = PaletteRole.Chrome,
                cornerRole = CornerRole.Control
            };
            ok.AddChild(new LabelControl { text = "OK", fontSize = 14, role = PaletteRole.MutedInk });
            ok.RegisterOnRelease(_ => { Hide(); return true; });
            _okRow.AddChild(ok);
            _column.AddChild(_okRow);

            ground.AddChild(_column);
            WindowRoot root = new WindowRoot();
            root.AddChild(ground);
            _window.ui.uiRoot = root;
        }
    }
}
