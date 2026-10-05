using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using iText.Kernel.Pdf;

namespace XTPdfMergeApp.Services
{
    /// <summary>Đọc thông tin sheet (/XTSheet, xem XTSheetPdfInfo) của cả file và khớp các sheet giữa bản cũ và bản revision.</summary>
    internal static class XTSheetIndex
    {
        /// <summary>Thông tin sheet theo số trang (1-based) của file; chỉ có các trang mang /XTSheet. File không đọc được → rỗng.</summary>
        public static IReadOnlyDictionary<int, XTSheetPageInfo> Read(string path)
        {
            var result = new Dictionary<int, XTSheetPageInfo>();
            try
            {
                var properties = new ReaderProperties();
                if (PdfThumbnailService.TryGetDocumentPassword(path) is { Length: > 0 } password)
                    properties.SetPassword(Encoding.UTF8.GetBytes(password));
                using var doc = new PdfDocument(new PdfReader(path, properties));
                for (int i = 1; i <= doc.GetNumberOfPages(); i++)
                    if (XTSheetPdfInfo.ReadPage(doc.GetPage(i)) is { } info) result[i] = info;
            }
            catch { /* không đọc được: coi như không có thông tin sheet */ }
            return result;
        }

        /// <summary>Khoá nhận diện 1 bản vẽ: số hiệu; không có thì DWG + layout. null = không đủ thông tin để khớp.</summary>
        public static string? Key(XTSheetPageInfo info)
        {
            if (!string.IsNullOrWhiteSpace(info.No)) return "N:" + info.No.Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(info.Dwg) && !string.IsNullOrWhiteSpace(info.Layout))
                return "D:" + info.Dwg.Trim().ToUpperInvariant() + "|" + info.Layout.Trim().ToUpperInvariant();
            return null;
        }

        /// <summary>
        /// Ghép trang của <paramref name="target"/> (theo thứ tự, null = trang không có thông tin sheet) với trang cùng khoá trong
        /// <paramref name="revision"/>. Trả về (chỉ số trong target 0-based, số trang trong revision 1-based) và các khoá chỉ có ở revision.
        /// </summary>
        public static (List<(int TargetIndex, int RevisionPage)> Pairs, List<int> UnmatchedRevisionPages) Match(
            IReadOnlyList<XTSheetPageInfo?> target, IReadOnlyDictionary<int, XTSheetPageInfo> revision)
        {
            var revisionByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (page, info) in revision.OrderBy(kv => kv.Key))
                if (Key(info) is { } key) revisionByKey.TryAdd(key, page);

            var pairs = new List<(int, int)>();
            var used = new HashSet<int>();
            for (int i = 0; i < target.Count; i++)
                if (target[i] is { } info && Key(info) is { } key && revisionByKey.TryGetValue(key, out int page))
                {
                    pairs.Add((i, page));
                    used.Add(page);
                }
            var unmatched = revision.Keys.Where(p => !used.Contains(p)).OrderBy(p => p).ToList();
            return (pairs, unmatched);
        }
    }
}
