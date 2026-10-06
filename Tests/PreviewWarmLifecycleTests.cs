using System.Diagnostics;
using System.IO;
using System.Windows;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Domain;
using XTPdfMergeApp.Services;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;

internal static partial class Program
{
    static void TestPreviewWarmDocumentSwitch()
    {
        if (Application.Current == null) CreateReaderTestApplication();
        bool saved = ContinuousPdfView.WarmAllPreviews;
        ContinuousPdfView.WarmAllPreviews = true;
        var view = new ContinuousPdfView { PrefetchPageCount = 0 };
        var host = new Window { Content = view, Width = 1000, Height = 600,
            ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        host.Show(); host.UpdateLayout();
        var sources = new List<string>();
        try
        {
            // Page 20 is outside visible/nearby prefetch ranges. Equal page counts
            // must not let completion of the old tab suppress warming of the new one.
            for (int documentIndex = 0; documentIndex < 2; documentIndex++)
            {
                string source = Path.Combine(Output, $"warm-switch-{Guid.NewGuid():N}.pdf");
                sources.Add(source);
                using (var pdf = new PdfDocument(new PdfWriter(source)))
                    for (int page = 0; page < 20; page++)
                        new PdfCanvas(pdf.AddNewPage(new iText.Kernel.Geom.PageSize(1000, 600)))
                            .MoveTo(20, 20).LineTo(980, 580).Stroke();
                var rows = Enumerable.Range(1, 20).Select(page => new PagePlacement
                    { SourcePath = source, PageNumber = page, BaseWidth = 1000, AspectRatio = .6 }).ToArray();
                view.PageRenderer = (_, width, _, _) => Task.FromResult<System.Windows.Media.Imaging.BitmapSource?>(Bitmap(width, width * 3 / 5));
                view.SetDocument(rows, 1);
                var wait = Stopwatch.StartNew();
                while (rows[^1].Thumbnail == null && wait.Elapsed < TimeSpan.FromSeconds(8))
                    Pump(TimeSpan.FromMilliseconds(25));
                Check(rows[^1].Thumbnail != null,
                    $"Idle preview warming reaches the far page of document {documentIndex + 1} after a tab switch");
            }
        }
        finally
        {
            view.CancelAll(); host.Close(); ContinuousPdfView.WarmAllPreviews = saved;
            ThumbnailCache.Invalidate(key => sources.Contains(key.Path));
        }
    }
}
