using System;
using System.Collections.Generic;
using System.IO;
using iText.Kernel.Pdf;

namespace XTPdfMergeApp.Services
{
    /// <summary>Thông tin 1 bản vẽ (sheet) gắn vào TRANG PDF của nó. Đặc tả: docs/XT_PDF_SHEET_INFO.md. Trường rỗng không được ghi.</summary>
    public sealed class XTSheetPageInfo
    {
        public string No { get; set; } = "";
        public string Title { get; set; } = "";
        public string Scale { get; set; } = "";
        public string Group { get; set; } = "";
        public string Subset { get; set; } = "";
        public string Dwg { get; set; } = "";
        public string Layout { get; set; } = "";
        public string Handle { get; set; } = "";
        public string Index { get; set; } = "";
    }

    /// <summary>Thông tin cả bộ hồ sơ, ghi vào Catalog của PDF.</summary>
    public sealed class XTProjectPdfInfo
    {
        public string Name { get; set; } = "";
        public string File { get; set; } = "";
        public string Run { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime PlottedUtc { get; set; } = DateTime.UtcNow;
        public string App { get; set; } = "XTToolbox";
    }

    /// <summary>
    /// Ghi (và đọc) thông tin sheet trong PDF: khoá riêng /XTSheet trong từ điển TRANG và /XTProject trong Catalog.
    /// Khoá nằm trên trang nên đi theo trang khi PDF Reader đổi thứ tự / tách / ghép (PdfMerger chép nguyên từ điển trang).
    /// BẢN NÀY GIỐNG HỆT bản trong XTToolbox (XTDrawing/Services/XTSheetPdfInfo.cs) — sửa cả hai nơi.
    /// </summary>
    public static class XTSheetPdfInfo
    {
        public const int Version = 1;
        public static readonly PdfName SheetKey = new("XTSheet");
        public static readonly PdfName ProjectKey = new("XTProject");

        /// <summary>
        /// Ghi vào <paramref name="pdfPath"/> (đọc → file tạm cùng thư mục → thay). <paramref name="pagesInOrder"/>[i] là thông tin của trang i+1
        /// (null = trang không phải bản vẽ: bìa, mục lục, tờ lót — không ghi gì).
        /// </summary>
        public static void Write(string pdfPath, IReadOnlyList<XTSheetPageInfo?> pagesInOrder, XTProjectPdfInfo project)
        {
            string temp = pdfPath + ".sheetinfo.tmp.pdf";
            if (File.Exists(temp)) File.Delete(temp);
            try
            {
                using (var reader = new PdfReader(pdfPath))
                using (var writer = new PdfWriter(temp))
                using (var doc = new PdfDocument(reader, writer))
                {
                    int pages = doc.GetNumberOfPages();
                    for (int i = 0; i < pagesInOrder.Count && i < pages; i++)
                    {
                        if (pagesInOrder[i] is not { } info) continue;
                        var page = doc.GetPage(i + 1).GetPdfObject();
                        page.Put(SheetKey, ToDictionary(info));
                        page.SetModified();
                    }

                    var proj = new PdfDictionary();
                    proj.Put(PdfName.V, new PdfNumber(Version));
                    Put(proj, "Name", project.Name);
                    Put(proj, "File", project.File);
                    Put(proj, "Run", project.Run);
                    Put(proj, "App", project.App);
                    Put(proj, "Plotted", project.PlottedUtc.ToString("o"));
                    doc.GetCatalog().Put(ProjectKey, proj);
                }
                File.Replace(temp, pdfPath, null, ignoreMetadataErrors: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static PdfDictionary ToDictionary(XTSheetPageInfo info)
        {
            var d = new PdfDictionary();
            d.Put(PdfName.V, new PdfNumber(Version));
            Put(d, "No", info.No);
            Put(d, "Title", info.Title);
            Put(d, "Scale", info.Scale);
            Put(d, "Group", info.Group);
            Put(d, "Subset", info.Subset);
            Put(d, "Dwg", info.Dwg);
            Put(d, "Layout", info.Layout);
            Put(d, "Handle", info.Handle);
            Put(d, "Index", info.Index);
            return d;
        }

        private static void Put(PdfDictionary d, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) d.Put(new PdfName(key), new PdfString(value, iText.IO.Font.PdfEncodings.UNICODE_BIG));
        }

        /// <summary>Đọc thông tin của 1 trang (null nếu trang không có /XTSheet).</summary>
        public static XTSheetPageInfo? ReadPage(PdfPage page)
        {
            var d = page.GetPdfObject().GetAsDictionary(SheetKey);
            if (d == null) return null;
            return new XTSheetPageInfo
            {
                No = Get(d, "No"), Title = Get(d, "Title"), Scale = Get(d, "Scale"), Group = Get(d, "Group"), Subset = Get(d, "Subset"),
                Dwg = Get(d, "Dwg"), Layout = Get(d, "Layout"), Handle = Get(d, "Handle"), Index = Get(d, "Index")
            };
        }

        /// <summary>Đọc thông tin bộ hồ sơ (null nếu PDF không có /XTProject).</summary>
        public static XTProjectPdfInfo? ReadProject(PdfDocument doc)
        {
            var d = doc.GetCatalog().GetPdfObject().GetAsDictionary(ProjectKey);
            if (d == null) return null;
            DateTime.TryParse(Get(d, "Plotted"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var plotted);
            return new XTProjectPdfInfo { Name = Get(d, "Name"), File = Get(d, "File"), Run = Get(d, "Run"), App = Get(d, "App"), PlottedUtc = plotted };
        }

        private static string Get(PdfDictionary d, string key) => d.GetAsString(new PdfName(key))?.ToUnicodeString() ?? "";
    }
}
