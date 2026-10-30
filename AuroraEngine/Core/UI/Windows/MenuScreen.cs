using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using Silk.NET.Vulkan;

namespace ArctisAurora.Core.UI.Windows
{
    // A named screen in its own menu window, centred over the window that asked for it. The OS
    // window and its GLFW handle are the engine's, so an application asks for a screen and gets back
    // the parsed root to fill.
    public static class MenuScreen
    {
        // Null when the screen is already up — that one is raised instead, and there is nothing to
        // fill.
        public static unsafe WindowRoot? Open(string name, string document, uint width, uint height, RenderWindow source)
        {
            if (source == null) return null;

            if (Engine.windows.TryGetValue(name, out RenderWindow existing))
            {
                existing.os.Show();
                existing.os.Focus();
                return null;
            }

            uint pixelWidth = UIScaling.ToPixels(source, width);
            uint pixelHeight = UIScaling.ToPixels(source, height);
            RenderWindow window = Engine.OpenMenuWindow(name, pixelWidth, pixelHeight, true);

            WindowRoot root = (WindowRoot)Control.ParseXML(document);
            window.ui.uiRoot = root;

            window.os.Resize(pixelWidth, pixelHeight);
            root.FitTo(window.os.windowSize);

            AGlfwWindow._glfw.GetWindowPos(source.os.handle, out int sx, out int sy);
            AGlfwWindow._glfw.GetWindowSize(source.os.handle, out int sw, out int sh);
            window.os.SetPosition(sx + (sw - (int)pixelWidth) / 2, sy + (sh - (int)pixelHeight) / 2);

            window.os.Show();
            window.os.Focus();
            window.os.SeedIsInWindow();

            return root;
        }
    }
}
