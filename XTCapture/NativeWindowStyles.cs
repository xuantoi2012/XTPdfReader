using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace XTCapture
{
    /// <summary>Makes a small window behave like a tool tip: it never takes the keyboard focus from the program the user is working in, and stays out of Alt+Tab.</summary>
    internal static class NativeWindowStyles
    {
        private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLong(IntPtr hwnd, int index, IntPtr value);

        public static void NoActivate(Window window)
        {
            window.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(window).Handle;
                SetWindowLong(handle, GWL_EXSTYLE, new IntPtr(GetWindowLong(handle, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
            };
        }
    }
}
