using System;
using Microsoft.Win32;

namespace XTCapture
{
    /// <summary>"Start with Windows": a value under HKCU\...\Run (no admin rights, and the user sees it in Task Manager > Startup).</summary>
    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>The Run value's name (tests use another one so a real setting is never touched).</summary>
        public static string ValueName { get; set; } = Environment.GetEnvironmentVariable("XTCAPTURE_AUTOSTART_NAME") is { Length: > 0 } custom ? custom : "XTCapture";

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                    return key?.GetValue(ValueName) is string value && value.Length > 0;
                }
                catch { return false; }
            }
        }

        /// <summary>The command line stored: the program, started in the background (tray and corner button only, no window).</summary>
        public static string CommandFor(string exePath) => "\"" + exePath + "\" --background";

        public static bool Set(bool enabled, string? exePath = null)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                if (key == null) return false;
                if (enabled) key.SetValue(ValueName, CommandFor(exePath ?? Environment.ProcessPath ?? ""));
                else key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }
            catch { return false; }
        }
    }
}
