using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Imaging;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace ArctisAurora.Core.Filing
{
    // A picture on the OS clipboard: a PNG stream, a device-independent bitmap, or a copied image file.
    public static class ClipboardImage
    {
        private static readonly LogChannel Log = LogChannel.For("Filing");

        // standard clipboard formats
        private const uint dib = 8;
        private const uint hDrop = 15;
        private const uint dibV5 = 17;

        private static readonly string[] imageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tga", ".tif", ".tiff", ".ico", ".cur",
            ".pbm", ".pgm", ".ppm", ".pnm", ".pam", ".qoi", ".ff", ".pcx", ".xbm", ".xpm", ".jls" };

        public static bool TryGet([NotNullWhen(true)] out Arctis.Imaging.Image? image)
        {
            image = null;
            if (!OperatingSystem.IsWindows()) return false;
            if (!OpenClipboard(IntPtr.Zero))
            {
                Log.Warn($"the clipboard is held by another application.");
                return false;
            }

            try
            {
                uint png = RegisterClipboardFormatW("PNG");
                if (png != 0 && Read(png) is byte[] pngBytes)
                    image = ImageFiles.Load(pngBytes);
                else if (Read(dibV5) is byte[] v5)
                    image = Arctis.Imaging.Image.LoadDib(v5, Arctis.Imaging.PixelLayout.RGBA8, ImageFiles.Limits);
                else if (Read(dib) is byte[] plain)
                    image = Arctis.Imaging.Image.LoadDib(plain, Arctis.Imaging.PixelLayout.RGBA8, ImageFiles.Limits);
                else if (DroppedImage() is string path)
                    image = ImageFiles.Load(path);
            }
            catch (Exception e)
            {
                Log.Warn($"clipboard picture failed to decode: {e.Message}");
                image = null;
            }
            finally
            {
                CloseClipboard();
            }

            return image != null;
        }

        // A clipboard format's bytes, or null when it is absent.
        private static byte[]? Read(uint format)
        {
            if (!IsClipboardFormatAvailable(format)) return null;

            IntPtr handle = GetClipboardData(format);
            if (handle == IntPtr.Zero) return null;

            IntPtr data = GlobalLock(handle);
            if (data == IntPtr.Zero) return null;

            try
            {
                byte[] bytes = new byte[(int)GlobalSize(handle)];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }

        // The first image file among copied files.
        private static string? DroppedImage()
        {
            if (!IsClipboardFormatAvailable(hDrop)) return null;

            IntPtr drop = GetClipboardData(hDrop);
            if (drop == IntPtr.Zero) return null;

            uint count = DragQueryFileW(drop, uint.MaxValue, null, 0);
            for (uint i = 0; i < count; i++)
            {
                uint length = DragQueryFileW(drop, i, null, 0);
                StringBuilder path = new StringBuilder((int)length + 1);
                DragQueryFileW(drop, i, path, length + 1);

                string candidate = path.ToString();
                if (imageExtensions.Contains(Path.GetExtension(candidate).ToLowerInvariant()) && File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr owner);

        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint format);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterClipboardFormatW(string name);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern UIntPtr GlobalSize(IntPtr handle);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFileW(IntPtr drop, uint index, StringBuilder? file, uint length);
    }
}
