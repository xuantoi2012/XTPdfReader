using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Services.Capture
{
    /// <summary>A top-level window the capture can pick: its visible frame in snapshot pixels and its place in the z-order (0 = top).</summary>
    internal sealed record CaptureWindow(IntPtr Handle, string Title, Int32Rect Bounds, int ZOrder);

    /// <summary>The screen frozen at the moment the capture starts. <see cref="Origin"/> is the virtual screen's top-left (it can be negative with a monitor on the left).</summary>
    internal sealed record ScreenSnapshot(BitmapSource Image, Point Origin, IReadOnlyList<CaptureWindow> Windows);

    /// <summary>Win32 side of the capture: the frozen virtual screen, the pickable windows and a live grab of one window (also when something covers it).</summary>
    internal static class ScreenGrabber
    {
        private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
        private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
        private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000, PW_RENDERFULLCONTENT = 2;

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

        /// <summary>Virtual screen (all monitors) in the pixels this process sees.</summary>
        public static Int32Rect VirtualScreen => new(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

        /// <summary>The primary monitor, in virtual-screen pixels (it always starts at 0,0).</summary>
        public static Int32Rect PrimaryScreen => new(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));

        /// <summary>Freezes the screen and lists the windows on it. Windows of <paramref name="excludeProcessIds"/> (the capture overlay itself) are left out.</summary>
        public static ScreenSnapshot Snapshot(ISet<uint>? excludeProcessIds = null, ISet<IntPtr>? excludeHandles = null)
        {
            var screen = VirtualScreen;
            var windows = EnumerateWindows(excludeProcessIds, excludeHandles);
            return new ScreenSnapshot(GrabScreen(screen), new Point(screen.X, screen.Y), windows);
        }

        public static BitmapSource GrabScreen(Int32Rect area)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            try
            {
                var (dib, dc, previous, bits) = CreateDib(screenDc, area.Width, area.Height);
                try
                {
                    BitBlt(dc, 0, 0, area.Width, area.Height, screenDc, area.X, area.Y, SRCCOPY | CAPTUREBLT);
                    return ToBitmapSource(bits, area.Width, area.Height, opaque: true);
                }
                finally { SelectObject(dc, previous); DeleteObject(dib); DeleteDC(dc); }
            }
            finally { ReleaseDC(IntPtr.Zero, screenDc); }
        }

        /// <summary>The windows a user would call "a window", top of the z-order first: visible, not minimised, not cloaked (other virtual desktops, hidden UWP shells), not tool tips.</summary>
        public static IReadOnlyList<CaptureWindow> EnumerateWindows(ISet<uint>? excludeProcessIds = null, ISet<IntPtr>? excludeHandles = null)
        {
            var result = new List<CaptureWindow>();
            int order = 0;
            EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
                    if (excludeHandles != null && excludeHandles.Contains(hwnd)) return true;
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (excludeProcessIds != null && excludeProcessIds.Contains(pid)) return true;
                    int style = GetWindowLong(hwnd, GWL_EXSTYLE);
                    if ((style & WS_EX_TOOLWINDOW) != 0 && (style & WS_EX_APPWINDOW) == 0) return true;
                    if ((style & WS_EX_NOACTIVATE) != 0 && (style & WS_EX_APPWINDOW) == 0) return true;
                    if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                    var bounds = VisibleBounds(hwnd);
                    if (bounds.Width < 24 || bounds.Height < 24) return true;
                    var title = new StringBuilder(256);
                    GetWindowText(hwnd, title, title.Capacity);
                    result.Add(new CaptureWindow(hwnd, title.ToString(), bounds, order++));
                }
                catch { /* a window that disappears while listing */ }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        /// <summary>The frame the user sees (Windows 10/11 add an invisible border around the window rectangle).</summary>
        private static Int32Rect VisibleBounds(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out RECT window)) return default;
            var rect = window;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) == 0 &&
                frame.Right > frame.Left && frame.Bottom > frame.Top)
            {
                // The DWM frame is in physical pixels while GetWindowRect follows the process's DPI awareness: only trust it when both agree on the size
                // within the invisible border, otherwise keep the window rectangle (a few pixels of margin beat a wrong place).
                int dw = (window.Right - window.Left) - (frame.Right - frame.Left), dh = (window.Bottom - window.Top) - (frame.Bottom - frame.Top);
                if (dw >= 0 && dw <= 40 && dh >= 0 && dh <= 40) rect = frame;
            }
            return new Int32Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }

        /// <summary>Asks the window to paint itself (works while another window covers it). Null when it painted nothing, e.g. a GPU surface that does not answer.</summary>
        public static BitmapSource? GrabWindow(CaptureWindow window)
        {
            if (!GetWindowRect(window.Handle, out RECT full)) return null;
            int width = full.Right - full.Left, height = full.Bottom - full.Top;
            if (width < 1 || height < 1) return null;
            IntPtr screenDc = GetDC(IntPtr.Zero);
            try
            {
                var (dib, dc, previous, bits) = CreateDib(screenDc, width, height);
                try
                {
                    if (!PrintWindow(window.Handle, dc, PW_RENDERFULLCONTENT)) return null;
                    var whole = ToBitmapSource(bits, width, height, opaque: true);
                    if (IsBlank(whole)) return null;
                    // keep only the visible frame (without the invisible border)
                    var visible = new Int32Rect(Math.Clamp(window.Bounds.X - full.Left, 0, width - 1), Math.Clamp(window.Bounds.Y - full.Top, 0, height - 1), 1, 1);
                    visible.Width = Math.Clamp(window.Bounds.Width, 1, width - visible.X);
                    visible.Height = Math.Clamp(window.Bounds.Height, 1, height - visible.Y);
                    var cropped = new CroppedBitmap(whole, visible);
                    cropped.Freeze();
                    return cropped;
                }
                finally { SelectObject(dc, previous); DeleteObject(dib); DeleteDC(dc); }
            }
            finally { ReleaseDC(IntPtr.Zero, screenDc); }
        }

        /// <summary>True when (almost) every sampled pixel is black: what a window painted by the GPU gives to PrintWindow.</summary>
        internal static bool IsBlank(BitmapSource image)
        {
            int w = image.PixelWidth, h = image.PixelHeight;
            if (w < 1 || h < 1) return true;
            var pixel = new byte[4];
            int steps = 12, dark = 0, total = 0;
            for (int i = 0; i < steps; i++)
            {
                for (int j = 0; j < steps; j++)
                {
                    int x = Math.Min(w - 1, (int)((i + 0.5) * w / steps)), y = Math.Min(h - 1, (int)((j + 0.5) * h / steps));
                    image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
                    total++;
                    if (pixel[0] < 4 && pixel[1] < 4 && pixel[2] < 4) dark++;
                }
            }
            return dark >= total - 1;
        }

        private static (IntPtr Dib, IntPtr Dc, IntPtr Previous, IntPtr Bits) CreateDib(IntPtr referenceDc, int width, int height)
        {
            var info = new BITMAPINFOHEADER { Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
            IntPtr dib = CreateDIBSection(referenceDc, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) throw new InvalidOperationException("Could not allocate the capture bitmap.");
            IntPtr dc = CreateCompatibleDC(referenceDc);
            IntPtr previous = SelectObject(dc, dib);
            return (dib, dc, previous, bits);
        }

        private static BitmapSource ToBitmapSource(IntPtr bits, int width, int height, bool opaque)
        {
            int stride = width * 4;
            var pixels = new byte[stride * height];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            if (opaque) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // GDI leaves the alpha byte at 0
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            image.Freeze();
            return image;
        }
    }
}
