using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Windows;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ArctisAurora.EngineWork.Rendering
{
    // The application without its main window: a tray icon, the main window hidden, one running copy.
    public static class Background
    {
        private static readonly LogChannel Log = LogChannel.For("Background");

        private const string className = "ArctisAurora.Background";

        // window messages
        private const uint trayMessage = 0x8000 + 1;
        private const uint showMessage = 0x8000 + 2;
        private const uint WM_ENDSESSION = 0x0016;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_RBUTTONUP = 0x0205;

        // Shell_NotifyIcon
        private const uint NIM_ADD = 0;
        private const uint NIM_DELETE = 2;
        private const uint NIF_MESSAGE = 1;
        private const uint NIF_ICON = 2;
        private const uint NIF_TIP = 4;
        private const int IDI_APPLICATION = 32512;
        private const int SM_CXSMICON = 49;

        private static WndProc? _proc;
        private static IntPtr _window;
        private static string _name = "";
        private static Mutex? _instance;

        public static bool active => _window != IntPtr.Zero;

        // set before Init by a host opened on one file: no tray, no single running copy
        public static bool standalone;

        // set before Init by a host that may start in the tray; read once settings are loaded
        public static Func<bool>? startHidden;

        // set before Init by a host whose main window starts hidden with no tray involved
        public static Func<bool>? hidePrimary;

        // host entries the tray menu lists between Open and Quit
        public static readonly List<ContextMenuEntry> trayEntries = new List<ContextMenuEntry>();

        // host replacement for Show on the tray's Open, a tray click and a second copy's signal
        public static Action? open;

        // Tray is enabled and the OS has one.
        public static bool canTray => OperatingSystem.IsWindows() && Tray();

        public static bool startsHidden => startHidden?.Invoke() == true && canTray && !TestRunner.active;

        public static bool primaryStartsHidden => startsHidden || (hidePrimary?.Invoke() == true && !TestRunner.active);

        // When Tray is enabled: a tray icon; unless OnClose is Quit, one running copy per host. A second copy shows the first and exits.
        [A_XSDActionDependency("Background.Init", "Bootstrap", "When Tray is enabled, adds the tray icon and, unless OnClose is Quit, claims the single running copy")]
        public static bool Init()
        {
            if (!canTray || TestRunner.active) return true;

            string name = HostName();
            if (Settings().onClose != WindowSetting.CloseAction.Quit && !Claim(name))
            {
                bool hidden = startsHidden;
                Log.Info($"{name} is already running{(hidden ? "" : " — showing it")}");
                if (!hidden) SignalShow(name);
                Environment.Exit(0);
            }

            return CreateTray(name);
        }

        [A_XSDActionDependency("Background.Remove", "Shutdown", "Takes the tray icon down")]
        public static bool Remove()
        {
            if (!active) return true;
            NotifyIconData icon = Icon();
            Shell_NotifyIconW(NIM_DELETE, ref icon);
            return true;
        }

        private static WindowSetting Settings() =>SettingsRegistry.Get<GraphicsSettings>().window;

        private static bool Tray() => !standalone && SettingsRegistry.Get<GraphicsSettings>().tray.enabled;

        private static string HostName() => Assembly.GetEntryAssembly()?.GetName().Name ?? "Aurora";

        private static bool Claim(string name)
        {
            _instance = new Mutex(true, $"Local\\{className}.{name}", out bool created);
            return created;
        }

        private static void SignalShow(string name)
        {
            IntPtr running = FindWindowW(className, name);
            if (running != IntPtr.Zero) PostMessageW(running, showMessage, IntPtr.Zero, IntPtr.Zero);
        }

        private static bool CreateTray(string name)
        {
            _name = name;
            _proc = Proc;
            IntPtr module = GetModuleHandleW(null);

            WndClassEx wc = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = module,
                lpszClassName = className
            };
            if (RegisterClassExW(ref wc) == 0)
            {
                Log.Error($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            _window = CreateWindowExW(0, className, name, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
            if (_window == IntPtr.Zero)
            {
                Log.Error($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            NotifyIconData icon = Icon();
            icon.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            icon.uCallbackMessage = trayMessage;
            icon.hIcon = TrayIcon();
            icon.szTip = name;
            if (!Shell_NotifyIconW(NIM_ADD, ref icon)) Log.Warn($"tray icon not added");
            return true;
        }

        // The app icon nearest the tray's size, or the system default when the host ships none.
        private static unsafe IntPtr TrayIcon()
        {
            Silk.NET.GLFW.Image[] icons = AGlfwWindow.AppIcons();
            if (icons.Length == 0) return LoadIconW(IntPtr.Zero, (IntPtr)IDI_APPLICATION);

            int want = GetSystemMetrics(SM_CXSMICON);
            Silk.NET.GLFW.Image pick = icons.OrderBy(i => i.Width).FirstOrDefault(i => i.Width >= want, icons.MaxBy(i => i.Width));
            BitmapInfoHeader header = new BitmapInfoHeader
            {
                biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = pick.Width,
                biHeight = -pick.Height,
                biPlanes = 1,
                biBitCount = 32
            };
            IntPtr color = CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
            byte* dst = (byte*)bits;
            for (int i = 0; i < pick.Width * pick.Height * 4; i += 4)
            {
                dst[i] = pick.Pixels[i + 2];
                dst[i + 1] = pick.Pixels[i + 1];
                dst[i + 2] = pick.Pixels[i];
                dst[i + 3] = pick.Pixels[i + 3];
            }

            byte[] maskBits = new byte[(pick.Width + 15) / 16 * 2 * pick.Height];
            IntPtr mask;
            fixed (byte* m = maskBits) mask = CreateBitmap(pick.Width, pick.Height, 1, 1, (IntPtr)m);
            IconInfo info = new IconInfo { fIcon = 1, hbmMask = mask, hbmColor = color };
            IntPtr handle = CreateIconIndirect(ref info);
            DeleteObject(color);
            DeleteObject(mask);
            return handle;
        }

        // The last window's close: quits without a tray; otherwise asks, hides it to the tray, or quits, as OnClose says.
        public static void Close(RenderWindow? window = null)
        {
            window ??= Engine.primary;
            if (!Tray())
            {
                Shutdown.Request();
                return;
            }

            switch (Settings().onClose)
            {
                case WindowSetting.CloseAction.Tray: if (canTray) Hide(window); else Ask(window); break;
                case WindowSetting.CloseAction.Quit: Shutdown.Request(); break;
                default: Ask(window); break;
            }
        }

        private static void Ask(RenderWindow window) =>
            ConfirmWindow.Ask(window, $"Close {HostName()} or minimize it to the tray?", "Don't ask again",
                ("Cancel", _ => { }),
                ("Minimize", canTray ? remember => Answer(window, WindowSetting.CloseAction.Tray, remember) : null),
                ("Close", remember => Answer(window, WindowSetting.CloseAction.Quit, remember)));

        private static void Answer(RenderWindow window, WindowSetting.CloseAction action, bool remember)
        {
            if (remember)
            {
                Settings().onClose = action;
                SettingsRegistry.Commit();
            }

            if (action == WindowSetting.CloseAction.Tray) Hide(window);
            else Shutdown.Request();
        }

        // Saves edited notes and the layout, then hides the window, the main one by default.
        public static void Hide(RenderWindow? window = null)
        {
            window ??= Engine.primary;
            NoteActions.SaveEdited();
            SessionLayout.Capture();
            window.hidden = true;
            window.os.Hide();
        }

        public static void Show()
        {
            foreach (RenderWindow window in Engine.windows.Values)
            {
                if (!window.hidden || window.closeRequested) continue;
                window.hidden = false;
                window.os.Show();
            }
            Engine.primary.Focus();
        }

        private static void Open()
        {
            if (open != null) open();
            else Show();
        }

        public static void HideWindow(RenderWindow window)
        {
            window.hidden = true;
            window.os.Hide();
        }

        public static void ShowWindow(RenderWindow window)
        {
            window.hidden = false;
            window.os.Show();
        }

        // Runs the host with arguments at login, or no longer when arguments is null.
        public static void SetLoginCommand(string? arguments)
        {
            if (!OperatingSystem.IsWindows()) return;

            using Microsoft.Win32.RegistryKey? run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (run == null) return;

            string name = HostName();
            if (arguments == null) run.DeleteValue(name, false);
            else run.SetValue(name, $"\"{Environment.ProcessPath}\" {arguments}");
            Log.Info($"login command {(arguments == null ? "removed" : "set")} for {name}");
        }

        // Shows the main window, then starts the shutdown.
        public static void Quit()
        {
            Show();
            Shutdown.Request();
        }

        private static void OpenMenu(int x, int y) =>
            ContextMenus.OpenAtScreen([
                new ContextMenuButton($"Open {_name}", Open),
                .. trayEntries,
                new ContextMenuButton("Quit", Quit)
            ], x, y, Engine.primary);

        // Dispatched on the main thread by GLFW's event polling.
        private static IntPtr Proc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == trayMessage)
            {
                uint mouse = (uint)lParam & 0xFFFF;
                if (mouse == WM_LBUTTONUP) Engine.Post(Open);
                else if (mouse == WM_RBUTTONUP && GetCursorPos(out CursorPoint at)) Engine.Post(() => OpenMenu(at.x, at.y));
                return IntPtr.Zero;
            }

            if (message == showMessage)
            {
                Engine.Post(Open);
                return IntPtr.Zero;
            }

            if (message == WM_ENDSESSION && wParam != IntPtr.Zero)
            {
                Shutdown.RunPhase(Shutdown.commitPhase);
                return IntPtr.Zero;
            }

            return DefWindowProcW(hwnd, message, wParam, lParam);
        }

        private static NotifyIconData Icon() => new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _window,
            uID = 1,
            szTip = "",
            szInfo = "",
            szInfoTitle = ""
        };

        #region ---- win32 ----
        private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WndClassEx
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIconData
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CursorPoint { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo
        {
            public int fIcon;
            public uint xHotspot;
            public uint yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern IntPtr CreateIconIndirect(ref IconInfo info);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassExW(ref WndClassEx wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string className, string title);

        [DllImport("user32.dll")]
        private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out CursorPoint point);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? name);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
        #endregion
    }
}
