using System;
using System.Collections.Generic;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Navigation;

namespace XTPdfMergeApp.Services
{
    /// <summary>1 bookmark. <see cref="PageNumber"/> 1-based trong file nguồn; null nếu mục không trỏ tới
    /// trang nào trong file (link web, đích hỏng…) — vẫn hiện nhưng bấm không nhảy.</summary>
    public sealed record PdfBookmarkNode(string Title, int? PageNumber, IReadOnlyList<PdfBookmarkNode> Children);

    /// <summary>Đọc cây outline (bookmark) bằng iText.</summary>
    public static class PdfOutlineService
    {
        public static IReadOnlyList<PdfBookmarkNode> ReadBookmarks(string path)
        {
            using var doc = new PdfDocument(new PdfReader(path));
            return ReadBookmarks(doc);
        }

        public static IReadOnlyList<PdfBookmarkNode> ReadBookmarks(PdfDocument doc)
        {
            if (doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Outlines) == null)
                return Array.Empty<PdfBookmarkNode>();
            PdfOutline? root;
            try { root = doc.GetOutlines(false); }
            catch { return Array.Empty<PdfBookmarkNode>(); } // outline hỏng: coi như không có bookmark
            if (root == null) return Array.Empty<PdfBookmarkNode>();
            var names = doc.GetCatalog().GetNameTree(PdfName.Dests);
            return Convert(root.GetAllChildren(), doc, names, depth: 0);
        }

        private static List<PdfBookmarkNode> Convert(IList<PdfOutline> outlines, PdfDocument doc, PdfNameTree names, int depth)
        {
            var result = new List<PdfBookmarkNode>();
            if (depth > 64) return result; // outline vòng lặp (file hỏng)
            foreach (var outline in outlines)
            {
                result.Add(new PdfBookmarkNode(
                    outline.GetTitle() ?? "",
                    ResolvePage(outline, doc, names),
                    Convert(outline.GetAllChildren(), doc, names, depth + 1)));
            }
            return result;
        }

        private static int? ResolvePage(PdfOutline outline, PdfDocument doc, PdfNameTree names)
        {
            try
            {
                // Đích nằm ở /Dest, hoặc trong hành động /A << /S /GoTo /D … >>.
                PdfObject? target = outline.GetContent().Get(PdfName.Dest);
                if (target == null && outline.GetContent().GetAsDictionary(PdfName.A) is { } action &&
                    PdfName.GoTo.Equals(action.GetAsName(PdfName.S)))
                    target = action.Get(PdfName.D);
                if (target == null) return null;

                var destination = PdfDestination.MakeDestination(target);
                if (destination.GetDestinationPage(names) is PdfDictionary page)
                {
                    int number = doc.GetPageNumber(page);
                    return number > 0 ? number : null;
                }
                // Đích dạng [số_trang /XYZ …] (tham chiếu trang bằng số — thường gặp ở đích "remote").
                if (destination.GetDestinationPage(names) is PdfNumber index)
                    return index.IntValue() + 1;
                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
