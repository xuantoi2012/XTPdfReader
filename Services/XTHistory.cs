using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using iText.Kernel.Pdf;

namespace XTPdfMergeApp.Services
{
    /// <summary>1 lần lưu có ghi lịch sử: giờ (UTC), người, việc đã làm, và độ dài file TRƯỚC lần lưu đó.</summary>
    internal sealed record HistoryEntry(DateTime TimeUtc, string User, string Action, long LengthBefore)
    {
        public string TimeText => TimeUtc.ToLocalTime().ToString("dd/MM HH:mm");
    }

    /// <summary>
    /// Lịch sử lưu nằm TRONG file PDF (Catalog /XTHistory, tối đa <see cref="MaxEntries"/> dòng mới nhất): đi theo file khi chép, không cần file phụ.
    /// Mỗi lần lưu nối thêm (incremental update) nên độ dài file trước lần lưu chính là điểm cắt để lấy lại phiên bản trước đó.
    /// </summary>
    internal static class XTHistory
    {
        public const int MaxEntries = 200;
        private static readonly PdfName Key = new("XTHistory");
        private static readonly PdfName TimeKey = new("T"), UserKey = new("U"), ActionKey = new("A"), LengthKey = new("L");

        /// <summary>Tên hiển thị của người dùng hiện tại ("user@MÁY").</summary>
        public static string CurrentUser => Environment.UserName + "@" + Environment.MachineName;

        /// <summary>Thêm 1 dòng vào <paramref name="doc"/> (mở ở append mode) — gọi trong cùng lần ghi với thay đổi.</summary>
        public static void Append(PdfDocument doc, string action, long lengthBefore)
        {
            var catalog = doc.GetCatalog().GetPdfObject();
            var old = catalog.GetAsArray(Key);
            var list = new PdfArray();
            int skip = old == null ? 0 : Math.Max(0, old.Size() - (MaxEntries - 1));
            for (int i = skip; old != null && i < old.Size(); i++) list.Add(old.Get(i, false));

            var entry = new PdfDictionary();
            entry.Put(TimeKey, new PdfString(DateTime.UtcNow.ToString("o")));
            entry.Put(UserKey, new PdfString(CurrentUser, iText.IO.Font.PdfEncodings.UNICODE_BIG));
            entry.Put(ActionKey, new PdfString(action, iText.IO.Font.PdfEncodings.UNICODE_BIG));
            entry.Put(LengthKey, new PdfNumber(lengthBefore));
            list.Add(entry);
            catalog.Put(Key, list);
            catalog.SetModified();
        }

        /// <summary>Đọc lịch sử của file (cũ → mới). File không đọc được hoặc chưa có lịch sử → rỗng.</summary>
        public static IReadOnlyList<HistoryEntry> Read(string path)
        {
            try
            {
                var properties = new ReaderProperties();
                if (PdfThumbnailService.TryGetDocumentPassword(path) is { Length: > 0 } password)
                    properties.SetPassword(Encoding.UTF8.GetBytes(password));
                using var doc = new PdfDocument(new PdfReader(path, properties));
                return Read(doc);
            }
            catch { return Array.Empty<HistoryEntry>(); }
        }

        public static IReadOnlyList<HistoryEntry> Read(PdfDocument doc)
        {
            var array = doc.GetCatalog().GetPdfObject().GetAsArray(Key);
            var result = new List<HistoryEntry>();
            for (int i = 0; array != null && i < array.Size(); i++)
            {
                if (array.GetAsDictionary(i) is not { } d) continue;
                DateTime.TryParse(d.GetAsString(TimeKey)?.ToUnicodeString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var time);
                result.Add(new HistoryEntry(time, d.GetAsString(UserKey)?.ToUnicodeString() ?? "", d.GetAsString(ActionKey)?.ToUnicodeString() ?? "",
                    d.GetAsNumber(LengthKey)?.LongValue() ?? 0));
            }
            return result;
        }

        /// <summary>Chép phiên bản của file TRƯỚC lần lưu có <paramref name="lengthBefore"/> ra <paramref name="output"/> (cắt tại điểm cuối của lần lưu trước).</summary>
        public static void SaveVersion(string path, long lengthBefore, string output)
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (lengthBefore <= 0 || lengthBefore > source.Length) throw new InvalidOperationException("That version is no longer in the file.");
            string temp = output + ".tmp";
            try
            {
                using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[1 << 20];
                    long left = lengthBefore;
                    while (left > 0)
                    {
                        int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                        if (read <= 0) break;
                        target.Write(buffer, 0, read);
                        left -= read;
                    }
                }
                File.Move(temp, output, overwrite: true);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }
    }
}
