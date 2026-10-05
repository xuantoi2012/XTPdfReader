using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// "Ai đang mở file này": mỗi Reader đang mở 1 PDF ghi 1 file nhỏ ẩn cạnh PDF (".tên.xtopen.user@máy.pid") và cập nhật giờ ghi mỗi ~30 giây;
    /// Reader khác trong cùng thư mục (kể cả thư mục mạng) đọc các file đó để biết có ai đang mở. Bật trong Settings (mặc định tắt vì ghi file phụ vào thư mục của PDF).
    /// </summary>
    internal static class XTPresence
    {
        public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(100);
        private static readonly TimeSpan Abandoned = TimeSpan.FromMinutes(10);
        private const string Tag = ".xtopen.";

        private static string Self => XTHistory.CurrentUser + "." + Environment.ProcessId;

        private static string Marker(string pdf, string who)
            => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pdf)) ?? "", "." + Path.GetFileName(pdf) + Tag + who);

        /// <summary>Ghi / làm mới dấu "đang mở" của tiến trình này cho <paramref name="pdf"/>. Lỗi ghi (thư mục chỉ đọc) bị bỏ qua.</summary>
        public static void Touch(string pdf)
        {
            try
            {
                string marker = Marker(pdf, Self);
                if (!File.Exists(marker))
                {
                    File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
                    File.SetAttributes(marker, FileAttributes.Hidden);
                }
                else File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);
            }
            catch { }
        }

        public static void Remove(string pdf)
        {
            try { File.Delete(Marker(pdf, Self)); } catch { }
        }

        /// <summary>Người dùng ("user@máy") của các Reader KHÁC đang mở <paramref name="pdf"/> (dấu còn mới); dọn dấu bị bỏ rơi quá lâu.</summary>
        public static IReadOnlyList<string> Others(string pdf)
        {
            var result = new List<string>();
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(pdf)) ?? "";
                string prefix = "." + Path.GetFileName(pdf) + Tag;
                string self = Self;
                foreach (string file in Directory.EnumerateFiles(dir, prefix + "*"))
                {
                    string who = Path.GetFileName(file)[prefix.Length..];
                    if (who == self) continue;
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(file);
                    if (age > Abandoned) { try { File.Delete(file); } catch { } continue; }
                    if (age > Fresh) continue;
                    int dot = who.LastIndexOf('.');
                    result.Add(dot > 0 ? who[..dot] : who);
                }
            }
            catch { }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
