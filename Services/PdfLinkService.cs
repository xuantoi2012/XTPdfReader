using System;
using System.Linq;
using System.Threading.Tasks;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Navigation;

namespace XTPdfMergeApp.Services
{
    /// <summary>Đích của Link annotation: trang khác trong cùng PDF hoặc URI bên ngoài.</summary>
    internal sealed record PdfLinkTarget(int? PageNumber, string? Uri);

    /// <summary>Đọc Link annotation bằng cùng read-only iText document đang dùng cho annotation, nên click không mở thêm file handle.</summary>
    internal static class PdfLinkService
    {
        public static Task<PdfLinkTarget?> FindAtAsync(string path, int pageNumber, double u, double v)
            => Task.Run(() => FindAt(path, pageNumber, u, v));

        private static PdfLinkTarget? FindAt(string path, int pageNumber, double u, double v)
        {
            try
            {
                var reader = AnnotationStore.Reader(path);
                lock (reader.Sync)
                {
                    var document = reader.Document();
                    if (pageNumber < 1 || pageNumber > document.GetNumberOfPages()) return null;
                    var page = document.GetPage(pageNumber);
                    var geometry = PdfQuickAnnotationService.GetGeometry(page);
                    var (x, y) = geometry.DisplayToUser(u, v);
                    foreach (var annotation in page.GetAnnotations().Reverse())
                    {
                        if (!PdfName.Link.Equals(annotation.GetSubtype())) continue;
                        var obj = annotation.GetPdfObject();
                        var rect = obj.GetAsArray(PdfName.Rect);
                        if (rect == null || rect.Size() < 4) continue;
                        double left = rect.GetAsNumber(0)?.DoubleValue() ?? 0;
                        double bottom = rect.GetAsNumber(1)?.DoubleValue() ?? 0;
                        double right = rect.GetAsNumber(2)?.DoubleValue() ?? 0;
                        double top = rect.GetAsNumber(3)?.DoubleValue() ?? 0;
                        if (x < Math.Min(left, right) || x > Math.Max(left, right) || y < Math.Min(bottom, top) || y > Math.Max(bottom, top)) continue;

                        var action = obj.GetAsDictionary(PdfName.A);
                        if (PdfName.URI.Equals(action?.GetAsName(PdfName.S)))
                            return new PdfLinkTarget(null, action.GetAsString(PdfName.URI)?.ToUnicodeString());

                        PdfObject? destination = PdfName.GoTo.Equals(action?.GetAsName(PdfName.S))
                            ? action!.Get(PdfName.D) : obj.Get(PdfName.Dest);
                        int? destinationPage = PageFromDestination(destination, document);
                        if (destinationPage != null) return new PdfLinkTarget(destinationPage, null);
                    }
                }
            }
            catch
            {
                // Link không hợp lệ/mã hoá không cản trở việc đọc trang.
            }
            return null;
        }

        private static int? PageFromDestination(PdfObject? destination, PdfDocument document)
        {
            if (destination == null) return null;
            try
            {
                // PdfDestination resolves both direct arrays and /Dests name-tree entries (PDF table of contents often uses the latter).
                var resolved = PdfDestination.MakeDestination(destination)
                    .GetDestinationPage(document.GetCatalog().GetNameTree(PdfName.Dests));
                if (resolved is PdfDictionary page)
                {
                    int number = document.GetPageNumber(page);
                    return number > 0 ? number : null;
                }
                if (resolved is PdfNumber index) return index.IntValue() + 1;
            }
            catch
            {
                // Invalid link destinations are ignored just like an unknown external target.
            }
            return null;
        }
    }
}
