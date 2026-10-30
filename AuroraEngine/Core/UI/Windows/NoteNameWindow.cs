using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using Silk.NET.Vulkan;
using System.Globalization;
using System.Numerics;

namespace ArctisAurora.Core.UI.Windows
{
    // Asks for a note's name. One window per process, built on the first ask and hidden between them
    // — a swapchain per prompt would put the build cost inside the gesture.
    public static class NoteNameWindow
    {
        private const string windowName = "note-name";
        private const uint windowWidth = 300;
        private const uint windowHeight = 134;

        private static RenderWindow _window = null!;
        private static RenderWindow? _source;
        private static Control? _restoreActive;
        private static TextBoxControl _field = null!;
        private static StackPanelControl _column = null!;
        private static LabelControl _title = null!;

        // sheet size row
        private static StackPanelControl _sizeRow = null!;
        private static TextBoxControl _rows = null!;
        private static TextBoxControl _columns = null!;

        private static Action<string>? _onConfirm;
        private static Action<string, int, int>? _onConfirmSized;
        private static Action? _onCancel;

        public static bool isOpen { get; private set; }

        // A prompt at a time. A second ask while one is up would strand the first one's callbacks.
        public static void Ask(RenderWindow source, string suggestion, Action<string> onConfirm, Action? onCancel)
        {
            if (isOpen || source == null) return;

            Build();
            _onConfirm = onConfirm;
            ShowSizeRow(false);
            Open(source, suggestion, onCancel);
        }

        // Asks for a sheet's name and its fixed page size.
        public static void AskSheet(RenderWindow source, string suggestion, int rows, int columns, Action<string, int, int> onConfirm, Action? onCancel)
        {
            if (isOpen || source == null) return;

            Build();
            _onConfirmSized = onConfirm;
            _rows.text = rows.ToString(CultureInfo.InvariantCulture);
            _columns.text = columns.ToString(CultureInfo.InvariantCulture);
            ShowSizeRow(true);
            Open(source, suggestion, onCancel);
        }

        private static void ShowSizeRow(bool show)
        {
            _title.text = show ? "Name this sheet" : "Name this note";
            if (show) _sizeRow.Show();
            else _sizeRow.Hide();
        }

        private static unsafe void Open(RenderWindow source, string suggestion, Action? onCancel)
        {
            _source = source;
            _onCancel = onCancel;

            _field.text = suggestion ?? string.Empty;

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

            // The field has to hold the active context before a click reaches it, or the keystrokes
            // meant for the prompt land in whatever was last clicked — the note being closed.
            _restoreActive = UIEngine.activeControl;
            UIEngine.SetActiveControl(_field);
            _field.Focus();

            isOpen = true;
        }

        private static void Confirm()
        {
            string name = _field.text?.Trim();
            if (string.IsNullOrEmpty(name)) return;

            if (_onConfirmSized != null)
            {
                int rows = Count(_rows, SheetFormula.maxRows);
                int columns = Count(_columns, SheetFormula.maxColumns);
                if (rows == 0 || columns == 0) return;

                Action<string, int, int> sized = _onConfirmSized;
                Hide();
                sized(name, rows, columns);
                return;
            }

            Action<string>? confirm = _onConfirm;
            Hide();
            confirm?.Invoke(name);
        }

        // A count in 1..max, or 0.
        private static int Count(TextBoxControl box, int max) =>
            int.TryParse(box.text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= 1 && value <= max ? value : 0;

        private static void Cancel()
        {
            Action? cancel = _onCancel;
            Hide();
            cancel?.Invoke();
        }

        // Cleared before the callbacks, so a handler that opens another prompt is not refused by the
        // one it was called from.
        private static void Hide()
        {
            if (!isOpen) return;

            _window.os.Hide();
            isOpen = false;
            _onConfirm = null;
            _onConfirmSized = null;
            _onCancel = null;

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

            _title = new LabelControl
            {
                text = "Name this note",
                fontSize = 15,
                preferredHeight = 20,
                horizontalPosition = 0f
            };
            _column.AddChild(_title);

            _field = new TextBoxControl
            {
                preferredHeight = 30,
                horizontalAlignment = HorizontalAlignment.Stretch,
                role = PaletteRole.SubField,
                fontSize = 15,
                padding = new Thickness(0, 6, 0, 8)
            };
            _field.onCommit = _ => Confirm();
            _field.onCancel = Cancel;
            _column.AddChild(_field);

            _sizeRow = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Horizontal,
                alpha = 0f,
                horizontalAlignment = HorizontalAlignment.Stretch,
                Spacing = 8
            };
            _rows = SizeBox();
            _columns = SizeBox();
            _sizeRow.AddChild(SizeCaption("Rows"));
            _sizeRow.AddChild(_rows);
            _sizeRow.AddChild(SizeCaption("Columns"));
            _sizeRow.AddChild(_columns);
            _column.AddChild(_sizeRow);

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
            buttons.AddChild(Button("Cancel", Cancel));
            buttons.AddChild(Button("Save", Confirm));
            _column.AddChild(buttons);

            ground.AddChild(_column);
            return ground;
        }

        private static TextBoxControl SizeBox()
        {
            TextBoxControl box = new TextBoxControl
            {
                preferredHeight = 26,
                widthStar = 1f,
                role = PaletteRole.SubField,
                fontSize = 14
            };
            box.onCommit = _ => Confirm();
            box.onCancel = Cancel;
            return box;
        }

        private static LabelControl SizeCaption(string text) => new LabelControl
        {
            text = text,
            fontSize = 14,
            role = PaletteRole.MutedInk,
            hitTestable = false,
            verticalPosition = 0.5f
        };

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
