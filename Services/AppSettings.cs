using System;
using System.Collections.Generic;
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

        /// <summary>How dark thin lines are drawn: 0 = as the file says (a hairline fades to pale grey), 1 = at least 1.2 px and a darker edge curve (default), 2 = at least 1.6 px and darker still. Applies when the page renderers start.</summary>
        public static int LineWeight
        {
            get => Math.Clamp(GetInt("LineWeight", 1), 0, 2);
            set => Set("LineWeight", Math.Clamp(value, 0, 2));
        }

        /// <summary>The minimum line width in screen pixels for <see cref="LineWeight"/>.</summary>
        public static double MinLinePixels => LineWeight switch { 0 => 0.0, 2 => 1.6, _ => 1.2 };

        /// <summary>Gamma applied to every page picture (thumbnails too): above 1 darkens the soft edges of lines and letters, white stays white.</summary>
        public static double LineGamma => LineWeight switch { 0 => 1.0, 2 => 1.8, _ => 1.4 };

        /// <summary>A page counts as a color page for printing when at least this many tenths of a percent of it is colored (a logo in the title block covers about 0.1%, so it does not make every sheet a color sheet).</summary>
        public static int ColorPageTenths
        {
            get => Math.Clamp(GetInt("ColorPageTenths", 10), 1, 1000);
            set => Set("ColorPageTenths", Math.Clamp(value, 1, 1000));
        }

        /// <summary>"en" (English, US) | "vi" (Vietnamese); empty = not chosen yet (the app then follows the language of Windows).</summary>
        public static string Language
        {
            get => GetString("Language", "");
            set => Set("Language", value is "en" or "vi" ? value : "");
        }

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

        /// <summary>Báo ai khác đang mở cùng file (ghi file dấu ẩn cạnh PDF, xem XTPresence). Bật mặc định.</summary>
        public static bool ShowPresence
        {
            get => GetInt("ShowPresence", 1) == 1;
            set => Set("ShowPresence", value ? 1 : 0);
        }

        /// <summary>Clean up printed files (Print inbox > Clean up) only offers PDFs older than this many days; 0 = any age.</summary>
        public static int PrintedCleanupDays
        {
            get => Math.Clamp(GetInt("PrintedCleanupDays", 30), 0, 3650);
            set => Set("PrintedCleanupDays", Math.Clamp(value, 0, 3650));
        }

        /// <summary>Folder the print agent saves printed PDFs into; empty = the default under %LocalAppData%\XTPdfReader\Printed.</summary>
        public static string PrintedFolder
        {
            get => GetString("PrintedFolder", "");
            set => Set("PrintedFolder", value ?? "");
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

        /// <summary>Merge screen layout: "Columns" (max 3 across, scroll sideways) or "Rows" (one thumbnail row per window).</summary>
        public static string MergeLayoutMode
        {
            get => GetString("MergeLayoutMode", "Columns");
            set => Set("MergeLayoutMode", value);
        }

        public static ReaderPerformanceMode PerformanceMode
        {
            get
            {
                return ParsePerformanceMode(GetString("ReaderPerformanceMode", "Balance"));
            }
            set
            {
                var mode = Enum.IsDefined(value) ? value : ReaderPerformanceMode.Balance;
                ReaderPerformanceProfile.Apply(mode);
                Set("ReaderPerformanceMode", mode.ToString());
            }
        }

        internal static ReaderPerformanceMode ParsePerformanceMode(string saved)
        {
            // Older builds had five profiles: High/Performance fold into Balance, Ultra into Maximum.
            if (saved is "High" or "Performance") return ReaderPerformanceMode.Balance;
            if (saved == "Ultra") return ReaderPerformanceMode.Maximum;
            return Enum.TryParse<ReaderPerformanceMode>(saved, out var mode) && Enum.IsDefined(mode) ? mode : ReaderPerformanceMode.Balance;
        }

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

        /// <summary>Last look of the Callout tool (<see cref="CalloutStyle"/> text); "" = the default look.</summary>
        public static string CalloutStyleSetting
        {
            get => GetString("CalloutStyle", "");
            set => Set("CalloutStyle", value);
        }

        /// <summary>Last style of ONE shape tool (rect, cloud, oval, arrow, line), encoded <see cref="ShapeStyle"/>; "" = never set.</summary>
        public static string GetShapeStyleFor(string type) => GetString("ShapeStyle." + type, "");
        public static void SetShapeStyleFor(string type, string encoded) => Set("ShapeStyle." + type, encoded);

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
                       GetInt("MergePageNumbers", 0) == 1, GetInt("MergeOptimize", 1) == 1,
                       GetInt("MergeKeepLayersOn", 0) == 1 ? ReadNames(GetString("MergeKeepLayers", "")) : null,
                       GetString("MergeCollapseName", MergeOptions.DefaultCollapseName));
            set
            {
                Set("MergeFileBookmarks", value.FileBookmarks ? 1 : 0);
                Set("MergeKeepBookmarks", value.KeepBookmarks ? 1 : 0);
                Set("MergeLayers", value.MergeLayers ? 1 : 0);
                Set("MergePageNumbers", value.PageNumbers ? 1 : 0);
                Set("MergeOptimize", value.Optimize ? 1 : 0);
                Set("MergeKeepLayersOn", value.KeepLayers != null ? 1 : 0);
                if (value.KeepLayers != null) Set("MergeKeepLayers", System.Text.Json.JsonSerializer.Serialize(value.KeepLayers));
                Set("MergeCollapseName", value.CollapseLayerName);
            }
        }

        private static List<string> ReadNames(string json)
        {
            try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
            catch { return new List<string>(); }
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
            ReaderPerformanceProfile.Apply(PerformanceMode);
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
