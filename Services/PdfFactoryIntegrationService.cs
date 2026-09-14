using System;
using System.Linq;
using Microsoft.Win32;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// Tạo/ghi đè registry "ViewPdf" của pdfFactory (Pro) — đây là override
    /// THẬT SỰ cho nút "View PDF file" trong khay Jobs (KHÁC với "ViewPdfDefault"
    /// đã thử trước đó — key đó chỉ là cache mặc định hệ thống, pdfFactory
    /// không dùng để quyết định mở gì nên set vô nghĩa). "ViewPdf" KHÔNG có
    /// sẵn mặc định (đúng ghi chú trong pdfFactory Developer Kit: "một số khoá
    /// không có sẵn, phải tự tạo trước khi dùng") — SetValue của RegistryKey
    /// tự tạo value nếu chưa tồn tại nên không cần code riêng cho việc tạo mới.
    /// KHÔNG liên quan tới file association mặc định của Windows nên không
    /// ảnh hưởng việc mở PDF ở chỗ khác. Số hậu tố phiên bản (8/9/...) có thể
    /// đổi theo máy nên dò theo prefix.
    /// </summary>
    public static class PdfFactoryIntegrationService
    {
        private const string FinePrintRoot = @"Software\FinePrint Software";
        private const string ValueName = "ViewPdf";

        /// <summary>Tên các subkey phiên bản pdfFactory tìm thấy, VD "pdfFactory9". Có thể rỗng nếu chưa cài hoặc chưa từng in lần nào (key chỉ tạo sau lần in đầu).</summary>
        private static string[] FindVersionKeyNames()
        {
            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(FinePrintRoot);
                if (root == null) return Array.Empty<string>();
                return root.GetSubKeyNames()
                    .Where(n => n.StartsWith("pdfFactory", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        private static string BuildCommand(string exePath) => $"\"{exePath}\" \"%1\"";

        /// <summary>Ghép theo cả tên value ("ViewPdf") — tránh đụng backup cũ của lần thử ViewPdfDefault trước đó nếu value đổi trong tương lai.</summary>
        private static string BackupKey(string keyName) => $"{keyName}__{ValueName}";

        public static bool IsEnabled(string exePath)
        {
            var keys = FindVersionKeyNames();
            if (keys.Length == 0) return false;
            string wanted = BuildCommand(exePath);

            foreach (var keyName in keys)
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey($@"{FinePrintRoot}\{keyName}");
                    string? current = key?.GetValue(ValueName) as string;
                    if (!string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase)) return false;
                }
                catch { return false; }
            }
            return true;
        }

        /// <summary>Bật — backup giá trị ViewPdfDefault hiện tại (nếu chưa từng backup) rồi ghi đè trỏ về app này. Trả về false nếu không tìm thấy cài đặt pdfFactory nào trên máy.</summary>
        public static bool Enable(string exePath)
        {
            var keys = FindVersionKeyNames();
            if (keys.Length == 0) return false;

            foreach (var keyName in keys)
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey($@"{FinePrintRoot}\{keyName}", writable: true)
                        ?? Registry.CurrentUser.CreateSubKey($@"{FinePrintRoot}\{keyName}", writable: true);
                    if (key == null) continue;

                    if (!MergeAppSettingsStore.HasPdfFactoryBackup(BackupKey(keyName)))
                    {
                        string? original = key.GetValue(ValueName) as string;
                        MergeAppSettingsStore.SetPdfFactoryBackup(BackupKey(keyName), original ?? "");
                    }

                    key.SetValue(ValueName, BuildCommand(exePath));
                }
                catch { }
            }

            MergeAppSettingsStore.SetPdfFactoryViewEnabled(true);
            return true;
        }

        /// <summary>Tắt — khôi phục đúng giá trị ViewPdfDefault đã backup trước khi bật (hoặc xoá value nếu trước đó chưa từng có, tức pdfFactory chưa in lần nào).</summary>
        public static void Disable()
        {
            foreach (var keyName in FindVersionKeyNames())
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey($@"{FinePrintRoot}\{keyName}", writable: true);
                    if (key == null) continue;

                    if (MergeAppSettingsStore.HasPdfFactoryBackup(BackupKey(keyName)))
                    {
                        string original = MergeAppSettingsStore.GetPdfFactoryBackup(BackupKey(keyName));
                        if (string.IsNullOrEmpty(original)) key.DeleteValue(ValueName, throwOnMissingValue: false);
                        else key.SetValue(ValueName, original);
                        MergeAppSettingsStore.ClearPdfFactoryBackup(BackupKey(keyName));
                    }
                }
                catch { }
            }

            MergeAppSettingsStore.SetPdfFactoryViewEnabled(false);
        }
    }
}
