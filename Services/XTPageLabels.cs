using System;
using System.Collections.Generic;
using System.Linq;
using iText.Kernel.Pdf;

namespace XTPdfMergeApp.Services
{
    /// <summary>Ghi nhãn trang (/PageLabels) từ số hiệu bản vẽ để mọi trình xem hiện "KT-05" thay vì "trang 37".</summary>
    internal static class XTPageLabels
    {
        /// <summary>Nhãn của từng trang: số hiệu bản vẽ; trang không có số hiệu (bìa, mục lục, tờ lót) giữ số trang.</summary>
        public static IReadOnlyList<string> LabelsFor(int pageCount, IReadOnlyDictionary<int, XTSheetPageInfo> infos)
            => Enumerable.Range(1, pageCount).Select(n => infos.TryGetValue(n, out var i) && i.No.Trim().Length > 0 ? i.No.Trim() : n.ToString()).ToList();

        public static void Apply(PdfDocument doc, IReadOnlyList<string> labels)
        {
            for (int i = 0; i < labels.Count && i < doc.GetNumberOfPages(); i++)
                doc.GetPage(i + 1).SetPageLabel(null, labels[i]);
            doc.GetCatalog().GetPdfObject().SetModified();
        }

        /// <summary>Ghi nhãn trang vào chính file (1 dòng lịch sử). Trả về số trang có số hiệu.</summary>
        public static int WriteInPlace(string path)
        {
            var infos = XTSheetIndex.Read(path);
            int count;
            using (var doc = new PdfDocument(new PdfReader(path))) count = doc.GetNumberOfPages();
            var labels = LabelsFor(count, infos);
            int sheets = infos.Count(kv => kv.Value.No.Trim().Length > 0);
            if (sheets == 0) return 0;
            PdfPageEditService.EditInPlace(path, $"Page labels set from {sheets} sheet numbers", doc => Apply(doc, labels));
            return sheets;
        }
    }
}
