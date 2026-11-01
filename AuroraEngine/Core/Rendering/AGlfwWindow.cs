using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using Silk.NET.Core.Native;
using Silk.NET.GLFW;
using Arctis.Imaging;
using ArctisAurora.Core.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using static Silk.NET.GLFW.GlfwCallbacks;
using Cursor = Silk.NET.GLFW.Cursor;
using Monitor = Silk.NET.GLFW.Monitor;

namespace ArctisAurora.EngineWork.Rendering
{
    internal unsafe class AGlfwWindow
    {
        private static readonly Core.Diagnostics.LogChannel Log = Core.Diagnostics.LogChannel.For("Renderer");

        //GLFW window variables
        internal static Glfw _glfw = null!;
        internal WindowHandle* handle;
        internal SurfaceKHR surface;
        internal KhrSurface driverSurface = null!;
        internal volatile bool frameBufferResized = false;
        internal Extent2D windowSize;

        // the OS display scale of the monitor the window is on
        internal float contentScale = 1f;

        // One cursor per shape for the whole process — GLFW creates cursors against the library and
        // only applies them per window, and the hover paths ask for a shape far more often than the
        // shape changes.
        private static readonly Dictionary<CursorShape, IntPtr> cursors = new Dictionary<CursorShape, IntPtr>();

        // app icon images and the pinned pixels they point into
        private static Silk.NET.GLFW.Image[]? _appIcons;
        private static readonly List<byte[]> _appIconPixels = new List<byte[]>();

        internal readonly RenderWindow owner;

        // GLFW entry points Silk.NET does not bind
        private static delegate* unmanaged[Cdecl]<WindowHandle*, float*, float*, void> _getWindowContentScale;
        private static delegate* unmanaged[Cdecl]<WindowHandle*, delegate* unmanaged[Cdecl]<WindowHandle*, float, float, void>, IntPtr> _setWindowContentScaleCallback;

        internal AGlfwWindow(uint width, uint height, RenderWindow owner)
        {
            windowSize = new Extent2D(width, height);
            this.owner = owner;
        }

        [A_XSDActionDependency("Glfw.Init", "Bootstrap")]
        internal static bool InitGlfw()
        {
            _glfw = Glfw.GetApi();
            if (!_glfw.Init())
            {
                Log.Fatal($"GLFW failed to initialize.");
                return false;
            }

            _getWindowContentScale = (delegate* unmanaged[Cdecl]<WindowHandle*, float*, float*, void>)
                _glfw.Context.GetProcAddress("glfwGetWindowContentScale");
            _setWindowContentScaleCallback = (delegate* unmanaged[Cdecl]<WindowHandle*, delegate* unmanaged[Cdecl]<WindowHandle*, float, float, void>, IntPtr>)
                _glfw.Context.GetProcAddress("glfwSetWindowContentScaleCallback");
            return true;
        }

        // The display scale of the primary monitor.
        internal static float PrimaryContentScale()
        {
            _glfw.GetMonitorContentScale(_glfw.GetPrimaryMonitor(), out float scale, out _);
            return scale;
        }

        // Every size under Icons/app, loaded once; empty when the host ships none.
        internal static Silk.NET.GLFW.Image[] AppIcons()
        {
            if (_appIcons != null) return _appIcons;
            List<Silk.NET.GLFW.Image> icons = new List<Silk.NET.GLFW.Image>();
            foreach (string file in VirtualFileSystem.EnumerateAll("Icons/app", "*.png"))
            {
                byte[] data;
                using (Stream iconStream = DataFiles.Open(file))
                {
                    data = new byte[iconStream.Length];
                    iconStream.ReadExactly(data);
                }
                if (!ImageCodec.TryIdentify(data, out ImageHeader header)) throw new ImageFormatException($"'{file}' is not an image");
                DecodeOptions options = new DecodeOptions(PixelLayout.RGBA8, 0, ImageFiles.Limits);
                byte[] pixels = GC.AllocateUninitializedArray<byte>(ImageCodec.RequiredBytes(header, options), pinned: true);
                ImageCodec.Decode(data, pixels, options);
                _appIconPixels.Add(pixels);
                icons.Add(new Silk.NET.GLFW.Image { Width = header.Width, Height = header.Height, Pixels = (byte*)Unsafe.AsPointer(ref pixels[0]) });
            }
            return _appIcons = icons.ToArray();
        }

        private void SetAppIcon()
        {
            Silk.NET.GLFW.Image[] icons = AppIcons();
            if (icons.Length == 0) return;
            fixed (Silk.NET.GLFW.Image* first = icons)
                _glfw.SetWindowIcon(handle, icons.Length, first);
        }

        internal void CreateWindow()
        {
            // hints are sticky until reset, and the ghost window sets several this one must not keep
            _glfw.DefaultWindowHints();
            _glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
            _glfw.WindowHint(WindowHintBool.Resizable, true);
            _glfw.WindowHint(WindowHintBool.Decorated, false);
            _glfw.WindowHint(WindowHintBool.DoubleBuffer, true);
            _glfw.WindowHint(WindowHintBool.Visible, !Background.primaryStartsHidden && !TestRunner.active);
            handle = CreateForMode(SettingsRegistry.Get<GraphicsSettings>());

            if (handle == null)
                throw new Exception("Failed to create window");

            RoundCorners();
            AllowSnapping();
            TouchpadInput.Register();
            SetAppIcon();
            UpdateWindowSize(ref windowSize);
            SetResizeCallback(WindwoResizeCallback);
            SetCloseCallback(WindowCloseCallback);
            TrackContentScale();
            if (TestRunner.active) Cloak(true);
        }

        // A window opened after boot: plain windowed at the size it was constructed with, wherever it
        // is asked to go, rather than on the GraphicsSettings monitor in the GraphicsSettings mode.
        internal void CreateWindow(string title, int x, int y)
        {
            _glfw.DefaultWindowHints();
            _glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
            _glfw.WindowHint(WindowHintBool.Resizable, true);
            _glfw.WindowHint(WindowHintBool.Decorated, false);
            _glfw.WindowHint(WindowHintBool.DoubleBuffer, true);
            _glfw.WindowHint(WindowHintBool.Visible, !TestRunner.active);
            handle = _glfw.CreateWindow((int)windowSize.Width, (int)windowSize.Height, title, null, null);

            if (handle == null)
                throw new Exception("Failed to create window");

            RoundCorners();
            AllowSnapping();
            SetAppIcon();
            _glfw.SetWindowPos(handle, x, y);
            if (TestRunner.active) Cloak(true);
            UpdateWindowSize(ref windowSize);
            SetResizeCallback(WindwoResizeCallback);
            SetCloseCallback(WindowCloseCallback);
            TrackContentScale();
            SeedIsInWindow();
        }

        // Floats above everything, never takes focus and starts hidden — it exists only to show what
        // is being dragged. Not resizable, so it needs no resize callback.
        internal void CreateGhostWindow()
        {
            _glfw.DefaultWindowHints();
            _glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
            _glfw.WindowHint(WindowHintBool.Resizable, false);
            _glfw.WindowHint(WindowHintBool.Decorated, false);
            _glfw.WindowHint(WindowHintBool.DoubleBuffer, true);
            _glfw.WindowHint(WindowHintBool.Floating, true);
            _glfw.WindowHint(WindowHintBool.FocusOnShow, false);
            _glfw.WindowHint(WindowHintBool.Visible, false);
            handle = _glfw.CreateWindow((int)windowSize.Width, (int)windowSize.Height, "", null, null);

            if (handle == null)
                throw new Exception("Failed to create the drag preview window");

            RoundCorners();
            if (TestRunner.active) Cloak(false);
            UpdateWindowSize(ref windowSize);
            ReadContentScale();
        }

        // Floats over its parent and takes focus, so a dismissal has something to leave. Starts
        // hidden like the ghost, so the first open is filled and placed before it is ever seen.
        // withChrome is for one that draws its own minimise/maximise/close: it has to be resizable
        // and must not float, or those buttons have nothing to act on.
        internal void CreateMenuWindow(bool withChrome = false)
        {
            _glfw.DefaultWindowHints();
            _glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
            _glfw.WindowHint(WindowHintBool.Resizable, withChrome);
            _glfw.WindowHint(WindowHintBool.Decorated, false);
            _glfw.WindowHint(WindowHintBool.DoubleBuffer, true);
            _glfw.WindowHint(WindowHintBool.Floating, !withChrome);
            _glfw.WindowHint(WindowHintBool.Visible, false);
            handle = _glfw.CreateWindow((int)windowSize.Width, (int)windowSize.Height, "", null, null);

            if (handle == null)
                throw new Exception("Failed to create the context menu window");

            RoundCorners();
            if (TestRunner.active) Cloak(false);
            if (withChrome)
            {
                AllowSnapping();
                SetResizeCallback(WindwoResizeCallback);
                SetCloseCallback(WindowCloseCallback);
            }
            UpdateWindowSize(ref windowSize);
            TrackContentScale();
        }

        // Reads the display scale and follows it across monitors.
        private void TrackContentScale()
        {
            ReadContentScale();
            _setWindowContentScaleCallback(handle, &OnContentScale);
        }

        private void ReadContentScale()
        {
            float x, y;
            _getWindowContentScale(handle, &x, &y);
            contentScale = x;
        }

        // DWM rounds and clips the window at composition, so an undecorated window opts in the same
        // way a decorated one does and the swapchain is untouched.
        internal void RoundCorners()
        {
            if (!OperatingSystem.IsWindows()) return;
            int preference = Core.UI.Palettes.Default.windowCorners switch
            {
                Core.UI.WindowCorners.Square => squareCorners,
                Core.UI.WindowCorners.Small => smallCorners,
                _ => roundedCorners
            };
            IntPtr window = new GlfwNativeWindow(_glfw, handle).Win32!.Value.Hwnd;
            DwmSetWindowAttribute(window, cornerPreference, ref preference, sizeof(int));
        }

        // DWMWA_WINDOW_CORNER_PREFERENCE and its DWMWCP_DONOTROUND, DWMWCP_ROUND, DWMWCP_ROUNDSMALL
        private const int cornerPreference = 33;
        private const int squareCorners = 1;
        private const int roundedCorners = 2;
        private const int smallCorners = 3;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // Under --test: drawn and presented, never seen on screen, shown without activating.
        private void Cloak(bool show)
        {
            if (!OperatingSystem.IsWindows())
            {
                if (show) _glfw.ShowWindow(handle);
                return;
            }
            int cloaked = 1;
            DwmSetWindowAttribute(Hwnd, cloak, ref cloaked, sizeof(int));
            if (show) ShowWindow(Hwnd, showNoActivate);
        }

        // DWMWA_CLOAK, SW_SHOWNOACTIVATE
        private const int cloak = 13;
        private const int showNoActivate = 4;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int command);

        // The shell arranges nothing that is WS_POPUP, which is what GLFW makes every undecorated
        // window — so snap is bought by taking an ordinary framed window and deleting the frame's
        // geometry in WM_NCCALCSIZE, rather than by declaring the window frameless.
        private void AllowSnapping()
        {
            if (!OperatingSystem.IsWindows()) return;
            IntPtr window = Hwnd;
            SetWindowLongPtr(window, windowStyle, (GetWindowLongPtr(window, windowStyle) & ~popup) | snappableFrame);

            frameProc = FrameProc;
            previousProc = SetWindowLongPtr(window, windowProcedure, Marshal.GetFunctionPointerForDelegate(frameProc));

            SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, frameChanged);
        }

        // Makes the client rect the whole window rect, so the caption and border the restyle added
        // reserve and paint nothing. Every other message is the one GLFW installed.
        private IntPtr FrameProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == rawInput) TouchpadInput.Handle(lParam);
            if (message != ncCalcSize || wParam == IntPtr.Zero)
                return CallWindowProc(previousProc, hwnd, message, wParam, lParam);

            // With no non-client area left, a maximized window's rect overhangs the monitor by the
            // frame it no longer has, and covers the taskbar. The work area is what it should fill.
            if ((GetWindowLongPtr(hwnd, windowStyle) & maximized) != 0)
            {
                MonitorInfo info = new MonitorInfo();
                info.size = (uint)Marshal.SizeOf<MonitorInfo>();
                if (GetMonitorInfo(MonitorFromWindow(hwnd, monitorNearest), ref info))
                    Marshal.StructureToPtr(info.work, lParam, false);
            }
            return IntPtr.Zero;
        }

        // Held for as long as the window is, because native code keeps the pointer to it.
        private WndProc frameProc = null!;
        private IntPtr previousProc;

        private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        // GWL_STYLE, GWLP_WNDPROC, WM_NCCALCSIZE, WM_INPUT
        private const int windowStyle = -16;
        private const int windowProcedure = -4;
        private const uint ncCalcSize = 0x0083;
        private const uint rawInput = 0x00FF;

        // WS_POPUP; WS_CAPTION | WS_THICKFRAME | WS_MAXIMIZEBOX; WS_MAXIMIZE
        private static readonly nint popup = (nint)0x80000000L;
        private static readonly nint snappableFrame = (nint)(0x00C00000L | 0x00040000L | 0x00010000L);
        private static readonly nint maximized = (nint)0x01000000L;

        // SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
        private const uint frameChanged = 0x0020 | 0x0002 | 0x0001 | 0x0004 | 0x0010;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        // The OS window behind the GLFW one, for the Win32 calls that need an owner.
        internal IntPtr Hwnd => new GlfwNativeWindow(_glfw, handle).Win32!.Value.Hwnd;

        internal void Focus()
        {
            if (TestRunner.active) return;
            _glfw.FocusWindow(handle);
        }

        // Raises without activating.
        internal void Raise()
        {
            if (!OperatingSystem.IsWindows()) return;
            IntPtr window = new GlfwNativeWindow(_glfw, handle).Win32!.Value.Hwnd;
            SetWindowPos(window, hwndTop, 0, 0, 0, 0, raiseFlags);
        }

        // HWND_TOP, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
        private static readonly IntPtr hwndTop = IntPtr.Zero;
        private const uint raiseFlags = 0x0001 | 0x0002 | 0x0010;

        // Moves and sizes in one call.
        internal void SetBounds(int x, int y, int width, int height)
        {
            if (!OperatingSystem.IsWindows())
            {
                _glfw.SetWindowPos(handle, x, y);
                _glfw.SetWindowSize(handle, width, height);
                return;
            }
            SetWindowPos(Hwnd, IntPtr.Zero, x, y, width, height, boundsFlags);
        }

        // SWP_NOZORDER | SWP_NOACTIVATE
        private const uint boundsFlags = 0x0004 | 0x0010;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        internal void SetOpacity(float opacity) => _glfw.SetWindowOpacity(handle, opacity);

        internal void Show()
        {
            if (TestRunner.active && OperatingSystem.IsWindows()) ShowWindow(Hwnd, showNoActivate);
            else _glfw.ShowWindow(handle);
        }

        internal void Hide() => _glfw.HideWindow(handle);

        internal void SetFloating(bool floating) => _glfw.SetWindowAttrib(handle, WindowAttributeSetter.Floating, floating);

        internal void SetTitle(string title) => _glfw.SetWindowTitle(handle, title);

        internal void DestroyWindow()
        {
            _glfw.DestroyWindow(handle);
            handle = null;
        }

        // Borderless is a plain window at the monitor's size and position — the window is created
        // undecorated in every mode, so it needs no hint of its own.
        private WindowHandle* CreateForMode(GraphicsSettings settings)
        {
            if (settings.window.mode == WindowSetting.WindowMode.Windowed)
            {
                WindowHandle* windowed = _glfw.CreateWindow((int)windowSize.Width, (int)windowSize.Height, "Arctis Aurora", null, null);
                if (!string.IsNullOrWhiteSpace(settings.monitor.name))
                    CenterOn(windowed, PickMonitor(settings.monitor.name));
                return windowed;
            }

            Monitor* monitor = PickMonitor(settings.monitor.name);
            VideoMode* video = _glfw.GetVideoMode(monitor);

            if (settings.window.mode == WindowSetting.WindowMode.Fullscreen)
                return _glfw.CreateWindow(video->Width, video->Height, "Arctis Aurora", monitor, null);

            _glfw.GetMonitorPos(monitor, out int x, out int y);
            WindowHandle* handle = _glfw.CreateWindow(video->Width, video->Height, "Arctis Aurora", null, null);
            _glfw.SetWindowPos(handle, x, y);
            return handle;
        }

        private void CenterOn(WindowHandle* handle, Monitor* monitor)
        {
            VideoMode* video = _glfw.GetVideoMode(monitor);
            _glfw.GetMonitorPos(monitor, out int x, out int y);
            _glfw.SetWindowPos(handle,
                x + (video->Width - (int)windowSize.Width) / 2,
                y + (video->Height - (int)windowSize.Height) / 2);
        }

        private Monitor* PickMonitor(string preferred)
        {
            Monitor* primary = _glfw.GetPrimaryMonitor();
            if (string.IsNullOrWhiteSpace(preferred)) return primary;

            Monitor** monitors = _glfw.GetMonitors(out int count);
            for (int i = 0; i < count; i++)
            {
                string name = MonitorName(monitors[i]);
                if (name == null || !name.Contains(preferred, StringComparison.OrdinalIgnoreCase)) continue;
                return monitors[i];
            }

            Log.Warn($"no monitor matching '{preferred}' — using {MonitorName(primary)}.");
            return primary;
        }

        // GLFW's own name is the driver description, which is the same string for every panel on a
        // machine, so the name comes from Windows and is joined to the monitor by its position.
        private string MonitorName(Monitor* monitor)
        {
            _glfw.GetMonitorPos(monitor, out int x, out int y);
            return DisplayNames.At(x, y) ?? _glfw.GetMonitorName(monitor);
        }

        internal void SetPosition(int x, int y)
        {
            _glfw.SetWindowPos(handle, x, y);
        }

        internal void Maximize() => _glfw.MaximizeWindow(handle);

        // Where the window would sit if it were restored, and whether it currently is not. GLFW
        // reports the maximized rect while maximized, so the rect worth saving comes from the OS.
        internal (int x, int y, int width, int height, bool maximized) GetPlacement()
        {
            bool isMaximized = _glfw.GetWindowAttrib(handle, WindowAttributeGetter.Maximized);
            if (!isMaximized || !OperatingSystem.IsWindows())
            {
                _glfw.GetWindowPos(handle, out int px, out int py);
                _glfw.GetWindowSize(handle, out int pw, out int ph);
                return (px, py, pw, ph, isMaximized);
            }

            WindowPlacement placement = new WindowPlacement();
            placement.length = (uint)Marshal.SizeOf<WindowPlacement>();
            GetWindowPlacement(Hwnd, ref placement);

            // rcNormalPosition is in workspace coordinates, which differ from screen coordinates by
            // the work area's origin — non-zero only for a taskbar docked top or left.
            MonitorInfo info = new MonitorInfo();
            info.size = (uint)Marshal.SizeOf<MonitorInfo>();
            GetMonitorInfo(MonitorFromWindow(Hwnd, monitorNearest), ref info);

            int ox = info.work.left - info.monitor.left;
            int oy = info.work.top - info.monitor.top;

            return (placement.normal.left + ox, placement.normal.top + oy,
                    placement.normal.right - placement.normal.left,
                    placement.normal.bottom - placement.normal.top, true);
        }

        // Whether a saved rect still lands on a screen that exists.
        internal static bool RectOnAnyMonitor(int x, int y, int width, int height)
        {
            Monitor** monitors = _glfw.GetMonitors(out int count);
            for (int i = 0; i < count; i++)
            {
                _glfw.GetMonitorWorkarea(monitors[i], out int mx, out int my, out int mw, out int mh);
                if (x < mx + mw && x + width > mx && y < my + mh && y + height > my)
                    return true;
            }
            return false;
        }

        // The work area of the monitor under a screen point; the primary's when none holds it.
        internal static (int x, int y, int width, int height) WorkAreaAt(int x, int y)
        {
            Monitor** monitors = _glfw.GetMonitors(out int count);
            for (int i = 0; i < count; i++)
            {
                _glfw.GetMonitorWorkarea(monitors[i], out int mx, out int my, out int mw, out int mh);
                _glfw.GetMonitorPos(monitors[i], out int px, out int py);
                VideoMode* mode = _glfw.GetVideoMode(monitors[i]);
                if (x >= px && x < px + mode->Width && y >= py && y < py + mode->Height)
                    return (mx, my, mw, mh);
            }

            _glfw.GetMonitorWorkarea(_glfw.GetPrimaryMonitor(), out int wx, out int wy, out int ww, out int wh);
            return (wx, wy, ww, wh);
        }

        // Where a window of this size sits centred on the primary monitor, for one whose saved
        // position no longer lands anywhere.
        internal static (int x, int y) CenterOnPrimary(int width, int height)
        {
            _glfw.GetMonitorWorkarea(_glfw.GetPrimaryMonitor(), out int mx, out int my, out int mw, out int mh);
            return (mx + (mw - width) / 2, my + (mh - height) / 2);
        }

        #region ---- win32 placement ----
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPlacement
        {
            public uint length;
            public uint flags;
            public uint showCmd;
            public System.Drawing.Point minPosition;
            public System.Drawing.Point maxPosition;
            public Rect normal;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public uint size;
            public Rect monitor;
            public Rect work;
            public uint flags;
        }

        private const uint monitorNearest = 2;

        [DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        #endregion

        #region ---- native caption drag ----
        // Hands the drag to Windows, which is the only way to get snap, drag-to-restore and the
        // snap preview on a window it does not decorate. This blocks until the button comes up:
        // the pump is called on a timer so layout keeps up with a snap mid-drag, and must never
        // pump messages of its own — a nested PeekMessage would steal the moves the OS loop tracks.
        internal void DragByCaption(Action pump)
        {
            if (!OperatingSystem.IsWindows()) return;
            _pump = pump;
            IntPtr window = Hwnd;
            ReleaseCapture();
            IntPtr timer = SetTimer(window, pumpTimer, 8, _onPumpTimer);

            SendMessage(window, ncLButtonDown, (IntPtr)htCaption, IntPtr.Zero);

            if (timer != IntPtr.Zero) KillTimer(window, pumpTimer);
            _pump = null;
        }

        private static Action _pump;
        private static readonly TimerProc _onPumpTimer = (hwnd, message, id, time) => _pump?.Invoke();

        private const uint ncLButtonDown = 0x00A1;
        private const int htCaption = 2;
        private static readonly UIntPtr pumpTimer = (UIntPtr)1;

        private delegate void TimerProc(IntPtr hwnd, uint message, UIntPtr id, uint time);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint intervalMs, TimerProc callback);

        [DllImport("user32.dll")]
        private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);
        #endregion

        // Publishes the new size itself, for windows with no resize callback to do it.
        internal void Resize(uint width, uint height)
        {
            _glfw.SetWindowSize(handle, (int)width, (int)height);
            UpdateWindowSize(ref windowSize);
            frameBufferResized = true;
        }

        // The cursor-enter callback only fires on a crossing, so a window created under the pointer
        // would never learn it has it.
        internal void SeedIsInWindow()
        {
            if (TestRunner.active)
            {
                owner.isInWindow = false;
                return;
            }
            owner.isInWindow = _glfw.GetWindowAttrib(handle, WindowAttributeGetter.Hovered);
        }

        internal void ChangeCursor(CursorShape shape)
        {
            if (!cursors.TryGetValue(shape, out IntPtr cursor))
            {
                cursor = (IntPtr)_glfw.CreateStandardCursor(shape);
                cursors[shape] = cursor;
            }
            _glfw.SetCursor(handle, (Cursor*)cursor);
        }

        internal void SetResizeCallback(WindowSizeCallback callback)
        {
            _glfw.SetWindowSizeCallback(handle, callback);
        }

        internal void SetCloseCallback(WindowCloseCallback callback)
        {
            _glfw.SetWindowCloseCallback(handle, callback);
        }

        internal void SetCursorPosCallback(CursorPosCallback callback)
        {
            _glfw.SetCursorPosCallback(handle, callback);
        }

        internal void SetKeyCallback(KeyCallback callback)
        {
            _glfw.SetKeyCallback(handle, callback);
        }

        internal void SetScrollCallback(ScrollCallback callback)
        {
            _glfw.SetScrollCallback(handle, callback);
        }

        internal void SetCharCallback(CharCallback callback)
        {
            _glfw.SetCharCallback(handle, callback);
        }

        internal void SetMouseButtonCallback(MouseButtonCallback callback)
        {
            _glfw.SetMouseButtonCallback(handle, callback);
        }

        internal void SetMouseOnWindowCallback(CursorEnterCallback callback)
        {
            _glfw.SetCursorEnterCallback(handle, callback);
        }

        internal void CreateSurface()
        {
            if (!Renderer.vk.TryGetInstanceExtension(Renderer.instance, out driverSurface))
            {
                throw new NotSupportedException("KHR_surface extension not found.");
            }
            VkNonDispatchableHandle _surfaceHandle;
            _glfw.CreateWindowSurface(Renderer.instance.ToHandle(), handle, null, &_surfaceHandle);
            surface = _surfaceHandle.ToSurface();
        }

        internal void UpdateWindowSize(ref Extent2D _extent)
        {
            int _width, _height;
            _glfw.GetFramebufferSize(handle, out _width, out _height);
            _extent.Width = (uint)_width;
            _extent.Height = (uint)_height;
        }

        private void WindwoResizeCallback(WindowHandle* window, int width, int height)
        {
            frameBufferResized = true;
            // width/height here are in screen coordinates; Vulkan needs framebuffer (pixel)
            // size, so query it directly. On minimize this reports 0x0, which Draw() guards on.
            _glfw.GetFramebufferSize(handle, out int fbWidth, out int fbHeight);
            windowSize = new Extent2D((uint)fbWidth, (uint)fbHeight);

            owner.ui.uiRoot?.FitTo(windowSize);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void OnContentScale(WindowHandle* window, float xScale, float yScale)
        {
            foreach (RenderWindow open in Engine.windows.Values)
            {
                if (open.os.handle != window) continue;

                open.os.contentScale = xScale;
                Engine.Post(() => UIScaling.Apply(open));
                return;
            }
        }

        // OS close request (Alt+F4, taskbar, WM_CLOSE) takes the Window.Close path.
        private void WindowCloseCallback(WindowHandle* window)
        {
            Engine.Post(() => WindowActions.Close(owner));
        }
    }
}