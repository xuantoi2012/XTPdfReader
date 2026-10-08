using System;
using Microsoft.Win32;

namespace XTCapture
{
    /// <summary>XT Capture's own settings (HKCU\Software\XTCapture): they must not depend on the PDF Reader, which may not be installed or running.</summary>
    internal static class CaptureSettings
    {
        /// <summary>The registry key of the settings. The environment variable XTCAPTURE_REGKEY points a test run at another key, so a real install's choices are never touched.</summary>
        internal static string RegKey { get; set; } = Environment.GetEnvironmentVariable("XTCAPTURE_REGKEY") is { Length: > 0 } custom ? custom : @"Software\XTCapture";

        /// <summary>Raised with the setting's name after a change (the tray program reacts live).</summary>
        public static event Action<string>? Changed;

        /// <summary>Pixels outside a circle / polygon are transparent (true) or white (false).</summary>
        public static bool TransparentOutside { get => GetInt(nameof(TransparentOutside), 0) == 1; set => Set(nameof(TransparentOutside), value ? 1 : 0); }

        /// <summary>The small capture button in the bottom right corner of the screen.</summary>
        public static bool ShowFloatingButton { get => GetInt(nameof(ShowFloatingButton), 1) == 1; set => Set(nameof(ShowFloatingButton), value ? 1 : 0); }

        /// <summary>Where the user dragged the floating button to (screen pixels of its top left corner); (-1, -1) = the default corner.</summary>
        public static (int X, int Y) FloatingButtonPosition
        {
            get => (GetInt("FloatingX", -1), GetInt("FloatingY", -1));
            set { Set("FloatingX", value.X); Set("FloatingY", value.Y); }
        }

        /// <summary>The capture shortcut as text, e.g. "Win+C".</summary>
        public static string Hotkey { get => GetString(nameof(Hotkey), "Win+C"); set => Set(nameof(Hotkey), value); }

        /// <summary>True until the first run has switched on the defaults (start with Windows, the welcome message).</summary>
        public static bool FirstRun { get => GetInt(nameof(FirstRun), 1) == 1; set => Set(nameof(FirstRun), value ? 1 : 0); }

        /// <summary>The "older than N days" the Store proposes for its clean-up.</summary>
        public static int CleanupDays { get => Math.Clamp(GetInt(nameof(CleanupDays), 90), 1, 3650); set => Set(nameof(CleanupDays), Math.Clamp(value, 1, 3650)); }

        /// <summary>The Reader's theme choice is shared (System / Light / Dark and the accent) so both programs look alike.</summary>
        public static string ReaderTheme => ReadReader("Theme", "System");
        public static string ReaderAccent => ReadReader("Accent", "Orange");

        private static string ReadReader(string name, string fallback)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\XTStyle\XTPdfMergeApp");
                return key?.GetValue(name) as string ?? fallback;
            }
            catch { return fallback; }
        }

        private static string GetString(string name, string fallback)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegKey);
                return key?.GetValue(name) as string ?? fallback;
            }
            catch { return fallback; }
        }

        private static int GetInt(string name, int fallback)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegKey);
                return key?.GetValue(name) as int? ?? fallback;
            }
            catch { return fallback; }
        }

        private static void Set(string name, object value)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegKey, writable: true);
                key?.SetValue(name, value);
            }
            catch { /* not writable: the choice only lasts for this run */ }
            Changed?.Invoke(name);
        }
    }
}
