using System;

namespace XTPdfMergeApp.Services
{
    /// <summary>Cấu hình app — persist qua Registry (độc lập với plugin AutoCAD, app này chạy đứng riêng).</summary>
    public static class MergeAppSettingsStore
    {
        private const string RegKey = @"Software\XTStyle\XTPdfMergeApp";

        /// <summary>Sau khi lưu 1 window, tự chuyển các file nguồn vào thư mục con "Đã ghép" (không xoá) — đỡ rác tồn đọng qua thời gian.</summary>
        public static bool GetCleanupAfterMerge()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegKey);
                return (key?.GetValue("CleanupAfterMerge") as int?) == 1;
            }
            catch { return false; }
        }

        public static void SetCleanupAfterMerge(bool value)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegKey, writable: true);
                key?.SetValue("CleanupAfterMerge", value ? 1 : 0);
            }
            catch { }
        }

        /// <summary>Vị trí/kích thước/trạng thái Maximized của ReaderWindow (giờ là Window riêng,
        /// không còn tỉ lệ cột chia với MainWindow) — null nếu chưa từng lưu (lần đầu mở, để
        /// ReaderWindow tự dùng Width/Height mặc định khai báo trong XAML).</summary>
        public static (double Left, double Top, double Width, double Height, bool Maximized)? GetReaderWindowBounds()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegKey);
                if (key?.GetValue("ReaderWindowWidth") is not int w || key.GetValue("ReaderWindowHeight") is not int h)
                    return null;
                int left = key.GetValue("ReaderWindowLeft") as int? ?? 0;
                int top = key.GetValue("ReaderWindowTop") as int? ?? 0;
                bool maximized = (key.GetValue("ReaderWindowMaximized") as int?) == 1;
                return (left, top, w, h, maximized);
            }
            catch { return null; }
        }

        public static void SetReaderWindowBounds(double left, double top, double width, double height, bool maximized)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegKey, writable: true);
                key?.SetValue("ReaderWindowLeft", (int)Math.Round(left));
                key?.SetValue("ReaderWindowTop", (int)Math.Round(top));
                key?.SetValue("ReaderWindowWidth", (int)Math.Round(width));
                key?.SetValue("ReaderWindowHeight", (int)Math.Round(height));
                key?.SetValue("ReaderWindowMaximized", maximized ? 1 : 0);
            }
            catch { }
        }

        // ── Layout / Viewer ──

        public static string GetLayoutMode()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegKey);
                return key?.GetValue("LayoutMode") as string ?? "Row";
            }
            catch { return "Row"; }
        }

        public static void SetLayoutMode(string value)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegKey, writable: true);
                key?.SetValue("LayoutMode", string.Equals(value, "Column", StringComparison.OrdinalIgnoreCase) ? "Column" : "Row");
            }
            catch { }
        }

        public static bool GetViewerVisible()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegKey);
                return (key?.GetValue("ViewerVisible") as int?) == 1;
            }
            catch { return false; }
        }

        public static void SetViewerVisible(bool value)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegKey, writable: true);
                key?.SetValue("ViewerVisible", value ? 1 : 0);
            }
            catch { }
        }

        // ── Tích hợp pdfFactory "View PDF file" — xem PdfFactoryIntegrationService ──

        public static bool GetPdfFactoryViewEnabled()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegKey);
                return (key?.GetValue("PdfFactoryViewEnabled") as int?) == 1;
            }
            catch { return false; }
        }

        public static void SetPdfFactoryViewEnabled(bool value)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegKey, writable: true);
                key?.SetValue("PdfFactoryViewEnabled", value ? 1 : 0);
            }
            catch { }
        }

        private const string PdfFactoryBackupSubKey = RegKey + @"\PdfFactoryBackup";

        /// <summary>Giá trị "ViewPdf" GỐC của pdfFactory trước khi bị ghi đè — rỗng nghĩa là trước đó value này không tồn tại (xoá thay vì ghi lại khi khôi phục).</summary>
        public static bool HasPdfFactoryBackup(string versionKeyName)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PdfFactoryBackupSubKey);
                return key?.GetValue(versionKeyName) != null;
            }
            catch { return false; }
        }

        public static string GetPdfFactoryBackup(string versionKeyName)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PdfFactoryBackupSubKey);
                return key?.GetValue(versionKeyName) as string ?? "";
            }
            catch { return ""; }
        }

        public static void SetPdfFactoryBackup(string versionKeyName, string originalValue)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(PdfFactoryBackupSubKey, writable: true);
                key?.SetValue(versionKeyName, originalValue ?? "");
            }
            catch { }
        }

        public static void ClearPdfFactoryBackup(string versionKeyName)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PdfFactoryBackupSubKey, writable: true);
                key?.DeleteValue(versionKeyName, throwOnMissingValue: false);
            }
            catch { }
        }
    }
}
