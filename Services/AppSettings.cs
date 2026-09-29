using System;
using Microsoft.Win32;

namespace XTPdfMergeApp.Services
{
    /// <summary>Cách hiện trang khi mở file lần đầu.</summary>
    public enum DefaultZoom { FitWidth, FitPage }

    /// <summary>
    /// Cài đặt của người dùng (trang Settings), lưu ở HKCU\Software\XTStyle\XTPdfMergeApp cùng các giá trị cũ của <see cref="MergeAppSettingsStore"/>.
    /// Mỗi thay đổi ghi ngay và báo <see cref="Changed"/> (tên cài đặt) để nơi dùng áp dụng tức thì.
    /// </summary>
    internal static class AppSettings
    {
        private const string RegKey = @"Software\XTStyle\XTPdfMergeApp";

        public static event Action<string>? Changed;

        // ── Appearance ────────────────────────────────────────────────

        /// <summary>"Light" (mặc định) | "Dark" | "System" (theo Windows).</summary>
        public static string Theme
        {
            get => Pick(GetString("Theme", "Light"), "Light", "Dark", "System");
            set => Set("Theme", Pick(value, "Light", "Dark", "System"));
        }

        /// <summary>Màu nhấn: "Orange" (mặc định) | "Blue" | "Green" | "Purple" | "Red".</summary>
        public static string Accent
        {
            get => Pick(GetString("Accent", "Orange"), "Orange", "Blue", "Green", "Purple", "Red");
            set => Set("Accent", Pick(value, "Orange", "Blue", "Green", "Purple", "Red"));
        }

        // ── Display ───────────────────────────────────────────────────

        /// <summary>true = cuộn liên tục (mặc định), false = từng trang.</summary>
        public static bool ContinuousByDefault
        {
            get => GetInt("ContinuousByDefault", 1) == 1;
            set => Set("ContinuousByDefault", value ? 1 : 0);
        }

        public static DefaultZoom ZoomOnOpen
        {
            get => Enum.TryParse<DefaultZoom>(GetString("ZoomOnOpen", nameof(DefaultZoom.FitWidth)), out var z) ? z : DefaultZoom.FitWidth;
            set => Set("ZoomOnOpen", value.ToString());
        }

        /// <summary>"Hand" (mặc định) hoặc "Select" — công cụ đang bật khi mở 1 file.</summary>
        public static string DefaultTool
        {
            get => GetString("DefaultTool", "Hand");
            set => Set("DefaultTool", value);
        }

        // ── Performance & memory ──────────────────────────────────────

        /// <summary>Số file dùng gần nhất được giữ document PDFium trong RAM (0 = không giữ).</summary>
        public static int WarmFiles
        {
            get => Math.Clamp(GetInt("WarmFiles", 2), 0, 8);
            set => Set("WarmFiles", Math.Clamp(value, 0, 8));
        }

        /// <summary>"Text" = highlight the text under the drag, "Area" = highlight the dragged rectangle.</summary>
        public static string HighlightMode
        {
            get => GetString("HighlightMode", "Text");
            set => Set("HighlightMode", value);
        }

        /// <summary>Last used drawing-shape style (colour, width), see <see cref="ShapeStyle"/>.</summary>
        public static string ShapeStyleSetting
        {
            get => GetString("ShapeStyle", "");
            set => Set("ShapeStyle", value);
        }

        /// <summary>Last used Typewriter format (font, size, colour, bold, italic), see <see cref="TextFormat"/>.</summary>
        public static string TypewriterFormat
        {
            get => GetString("TypewriterFormat", "");
            set => Set("TypewriterFormat", value);
        }

        // ── Ghép file (hộp thoại Save merged file) ────────────────────

        public static MergeOptions MergeOptionsSaved
        {
            get => new(GetInt("MergeFileBookmarks", 1) == 1, GetInt("MergeKeepBookmarks", 1) == 1, GetInt("MergeLayers", 1) == 1,
                       GetInt("MergePageNumbers", 0) == 1, GetInt("MergeOptimize", 1) == 1);
            set
            {
                Set("MergeFileBookmarks", value.FileBookmarks ? 1 : 0);
                Set("MergeKeepBookmarks", value.KeepBookmarks ? 1 : 0);
                Set("MergeLayers", value.MergeLayers ? 1 : 0);
                Set("MergePageNumbers", value.PageNumbers ? 1 : 0);
                Set("MergeOptimize", value.Optimize ? 1 : 0);
            }
        }

        public static string LastMergeFolder
        {
            get => GetString("LastMergeFolder", "");
            set => Set("LastMergeFolder", value);
        }

        // ── Nền tảng ──────────────────────────────────────────────────

        /// <summary>Windows đang dùng giao diện tối cho ứng dụng?</summary>
        public static bool SystemPrefersDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return (key?.GetValue("AppsUseLightTheme") as int?) == 0;
            }
            catch { return false; }
        }

        /// <summary>Áp các cài đặt có hiệu lực toàn app lúc khởi động (số file ấm…). Biến môi trường XTPDF_* (dùng khi đo) thắng cài đặt.</summary>
        public static void ApplyRuntime()
        {
            if (Environment.GetEnvironmentVariable("XTPDF_WARM_FILES") == null) PdfThumbnailService.WarmFiles = WarmFiles;
        }

        // ── Registry ──────────────────────────────────────────────────

        private static string Pick(string value, params string[] allowed)
        {
            foreach (string a in allowed)
                if (string.Equals(a, value, StringComparison.OrdinalIgnoreCase)) return a;
            return allowed[0];
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
            catch { /* không ghi được: cài đặt chỉ có hiệu lực trong phiên này */ }
            Changed?.Invoke(name);
        }
    }
}
