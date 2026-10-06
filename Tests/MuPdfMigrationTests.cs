using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using Path = System.IO.Path;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    static async Task TestMuPdfMigrationAsync()
    {
        Check(ExperimentalMuPdfViewport.BalancedMode, "Balanced MuPDF backend is enabled");
        string path = Path.Combine(Output, "mupdf-text.pdf");
        using (var doc = new PdfDocument(new PdfWriter(path)))
        {
            var page = doc.AddNewPage(new PageSize(200, 100));
            new PdfCanvas(page).BeginText().SetFontAndSize(PdfFontFactory.CreateFont(StandardFonts.HELVETICA), 12)
                .MoveText(40, 40).ShowText("Alpha alpha alphabet").EndText();
            doc.AddNewPage(new PageSize(300, 200)).SetRotation(90);
        }
        try
        {
            var opened = await PdfThumbnailService.TryGetPageCountAsync(path);
            Check(opened.PageCount == 2 && opened.Failure == PdfOpenFailure.None, "MuPDF metadata opens the PDF");
            var sizes = await PdfThumbnailService.GetPageSizesAsync(path);
            Check(sizes is { Length: 2 } && sizes[0] == (200d, 100d) && sizes[1] == (200d, 300d), "Page dimensions include rotation");
            Check(await PdfThumbnailService.GetPageAspectRatioAsync(path, 1) is double aspect && Math.Abs(aspect - 2d/3) < .001,
                "Aspect ratio uses displayed page dimensions");
            var hits = new List<SearchHit>();
            var summary = await PdfThumbnailService.SearchAsync(path, "Alpha", true, true, (batch, _) => hits.AddRange(batch));
            Check(summary == new SearchSummary(2, 1) && hits.Count == 1 && hits[0].Rects.Count > 0, "Case-sensitive search reports hit geometry and blank pages");
            hits.Clear();
            await PdfThumbnailService.SearchAsync(path, "alpha", false, true, (batch, _) => hits.AddRange(batch));
            Check(hits.Count == 2, "Whole-word search excludes alphabet");
            var selection = await PdfThumbnailService.SelectTextWithStringAsync(path, 1, 41, 43, 69, 43);
            Check(selection.HasValue && selection.Value.Text.Contains("Alpha") && selection.Value.Rects.Count > 0,
                "Selection returns text and normalized rectangles");
            Check((await PdfThumbnailService.SelectTextAsync(path, 1, 41, 43, 69, 43))?.Count > 0, "Selection rectangle-only API uses MuPDF");
            var tiles = await PdfThumbnailService.RenderPageTilesBatchAsync(path, 0, 600, 300, new[] { new Int32Rect(0, 0, 600, 300) }, withAnnotations: true);
            Check(tiles[0] is { PixelWidth: 600, PixelHeight: 300, IsFrozen: true }, "Print band renders without PDFium");
            int callbacks = 0;
            await PdfThumbnailService.RenderPageTilesStreamingAsync(path, 0, 600, 300, new[] { new Int32Rect(0, 0, 300, 300), new Int32Rect(300, 0, 300, 300) },
                (_, _) => { callbacks++; return Task.CompletedTask; });
            Check(callbacks == 2, "Streaming callbacks remain supported");
            await TestLayerToggleRendersAsync();
            await TestWidgetRenderingAsync();
            await TestSaveSafetyAsync();
            string encrypted = Path.Combine(Output, "mupdf-password.pdf");
            using (var doc = new PdfDocument(new PdfWriter(encrypted, new WriterProperties().SetStandardEncryption(
                Encoding.UTF8.GetBytes("reader"), Encoding.UTF8.GetBytes("owner"), EncryptionConstants.ALLOW_PRINTING, EncryptionConstants.ENCRYPTION_AES_128)))) doc.AddNewPage();
            Check((await PdfThumbnailService.TryGetPageCountAsync(encrypted)).Failure == PdfOpenFailure.Password, "Missing password is classified correctly");
            await PdfThumbnailService.SetDocumentPasswordAsync(encrypted, "wrong");
            Check((await PdfThumbnailService.TryGetPageCountAsync(encrypted)).Failure == PdfOpenFailure.Password, "Wrong password stays retryable");
            await PdfThumbnailService.SetDocumentPasswordAsync(encrypted, "reader");
            Check((await PdfThumbnailService.TryGetPageCountAsync(encrypted)).PageCount == 1, "Correct password opens encrypted PDF");
            Check(await PdfThumbnailService.RenderPageAsync(encrypted, 0, 300) != null, "Encrypted page renders with the session password");
            await PdfThumbnailService.ForgetDocumentPasswordAsync(encrypted);
            Check((await PdfThumbnailService.TryGetPageCountAsync(encrypted)).Failure == PdfOpenFailure.Password, "Forgetting the password retires authenticated documents");
            using (await PdfThumbnailService.SuspendDocumentAsync(path, TimeSpan.FromSeconds(5)))
            {
                Check(await PdfThumbnailService.RenderPageAsync(path, 0, 100) == null, "Suspended sources cannot reopen a native renderer");
                Check(await PdfThumbnailService.RenderPageTileAsync(path, 0, 100, 100, new Int32Rect(0, 0, 50, 50)) == null,
                    "Suspended sources cannot reopen through the tile API");
                using var doc = new PdfDocument(new PdfWriter(path)); doc.AddNewPage();
            }
            Check(await PdfThumbnailService.RenderPageAsync(path, -1, 100) == null, "Invalid page indices do not enter legacy renderer paths");
            Check(await PdfThumbnailService.RenderPageAsync(path, 0, 100, layerToken: "stale") == null, "Obsolete layer tokens do not enter legacy renderer paths");
            Check((await PdfThumbnailService.GetPageCountAsync(path)) == 1, "File replacement retires native handles and stale cached metadata");
            Check(DiagnosticsReport.Build().Contains("MuPDF") && RenderDiagnostics.Summary.Contains("MuPDF"), "Diagnostics use MuPDF without loading PDFium");
            string manyPages = Path.Combine(Output, "mupdf-lru.pdf");
            using (var doc = new PdfDocument(new PdfWriter(manyPages)))
                for (int page = 0; page < 19; page++) doc.AddNewPage(new PageSize(100, 100));
            for (int page = 0; page < 19; page++)
                Check(await PdfThumbnailService.RenderPageAsync(manyPages, page, 64) is { IsFrozen: true }, "Display-list eviction does not break later renders");
            for (int worker = 0; worker < 4; worker++)
            {
                var stats = await ExperimentalMuPdfViewport.CommandAsync("", "stats", workerIndex: worker);
                Check(stats.GetProperty("documents").GetInt32() <= 2 && stats.GetProperty("displayLists").GetInt32() <= 8 &&
                    stats.GetProperty("rasterBytes").GetInt64() == 0, "Worker cache budgets remain bounded across many pages and documents");
            }
            await ExperimentalMuPdfViewport.ReleaseUnusedAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            Check(ExperimentalMuPdfViewport.CacheStats.Count == 0, "Closing documents releases bridge bitmap ownership");
            for (int worker = 0; worker < 4; worker++)
            {
                var stats = await ExperimentalMuPdfViewport.CommandAsync("", "stats", workerIndex: worker);
                Check(stats.GetProperty("documents").GetInt32() == 0 && stats.GetProperty("displayLists").GetInt32() == 0,
                    "Closing documents releases native handles and retained display lists");
            }
            Check(!Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Contains("pdfium", StringComparison.OrdinalIgnoreCase)),
                "No PDFium DLL was loaded through metadata, search, selection, layer, print, widgets, memory or editing paths");
        }
        finally { ExperimentalMuPdfViewport.Shutdown(); }
    }
}
