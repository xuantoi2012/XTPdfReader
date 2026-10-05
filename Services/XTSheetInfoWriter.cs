using System;
using System.Collections.Generic;
using System.Linq;
using iText.Kernel.Pdf;

namespace XTPdfMergeApp.Services
{
    /// <summary>Ghi thông tin sheet (số hiệu / tên / tỷ lệ) đọc từ PDF vào trang của chính file đó — chỉ ở Reader (XT_PRINT ghi bằng <c>XTSheetPdfInfo.Write</c>).</summary>
    internal static class XTSheetInfoWriter
    {
        /// <summary>Thêm / cập nhật /XTSheet của các trang (số hiệu, tên, tỷ lệ); trang đã có /XTSheet giữ nguyên các trường khác (hạng mục, DWG…). 1 dòng lịch sử.</summary>
        public static void WriteInPlace(string path, IReadOnlyList<(int Page, XTSheetPageInfo Info)> sheets)
        {
            if (sheets.Count == 0) return;
            PdfPageEditService.EditInPlace(path, $"Sheet info read for {sheets.Count} page{(sheets.Count == 1 ? "" : "s")}", doc => Apply(doc, sheets));
        }

        public static void Apply(PdfDocument doc, IReadOnlyList<(int Page, XTSheetPageInfo Info)> sheets)
        {
            foreach (var (number, info) in sheets)
            {
                if (number < 1 || number > doc.GetNumberOfPages()) continue;
                var page = doc.GetPage(number).GetPdfObject();
                var d = page.GetAsDictionary(XTSheetPdfInfo.SheetKey);
                bool isNew = d == null;
                d ??= new PdfDictionary();
                if (isNew) d.Put(PdfName.V, new PdfNumber(XTSheetPdfInfo.Version));
                Set(d, "No", info.No);
                Set(d, "Title", info.Title);
                Set(d, "Scale", info.Scale);
                page.Put(XTSheetPdfInfo.SheetKey, d);
                d.SetModified();
                page.SetModified();
            }
        }

        private static void Set(PdfDictionary d, string key, string value)
        {
            var name = new PdfName(key);
            if (string.IsNullOrWhiteSpace(value)) d.Remove(name);
            else d.Put(name, new PdfString(value.Trim(), iText.IO.Font.PdfEncodings.UNICODE_BIG));
        }
    }
}
