using System.IO;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Services.Ocr;

internal static partial class Program
{
    /// <summary>OCR kept in memory until Save: the file is untouched, Find / Select / the sheet-info reader see the text at once, Undo / Redo, the unsaved mark, Ctrl+S writes it.</summary>
    static void TestOcrPendingUntilSave()
    {
        if (!OcrService.IsAvailable()) { Console.WriteLine("OCR pending: no embedded Python in this build; skipped"); return; }
        string folder = System.IO.Path.Combine(Output, "ocr-pending-source");
        var (scan, lines, _, _) = BuildOcrScanPdf(folder);

        RunReaderFlow("ocr-pending", async f =>
        {
            string path = f.Path;
            var host = (XTPdfMergeApp.IReaderPageEditHost)f.Window.Session;
            var group = f.Window.Session.Documents[0];
            string before = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            Check(!OcrPendingStore.HasPending(path) && !group.IsDirty, "A scan opens clean, with no OCR text");
            Check(((await PdfThumbnailService.GetWordRectsAsync(path, 1))?.Count ?? 0) == 0, "…and no words (a scan)");

            // the quick OCR dialog of the page menus: reads at once and keeps the text
            var window = new OcrWindow(path, 1, 5) { AutoStart = true, Left = -32000, Top = -32000, ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual };
            window.SelectPages(new[] { 1, 2 });
            window.Show();
            for (int i = 0; i < 100 && window.PendingWords == null; i++) await Task.Delay(100);
            Check(window.PendingWords is { Count: 2 } && window.PendingWords.ContainsKey(1) && window.PendingWords.ContainsKey(2) && window.OutputPath == null, "The quick dialog reads pages 1 and 2 and keeps the words (no copy is written)");
            var words = window.PendingWords!;
            window.Close();

            await host.ApplyOcrAsync(path, words.ToDictionary(p => p.Key, p => (IReadOnlyList<OcrWord>?)p.Value), "OCR 2 pages");
            await Task.Delay(300);
            Check(OcrPendingStore.HasPending(path) && OcrPendingStore.HasPage(path, 1) && !OcrPendingStore.HasPage(path, 3), "The words are pending for pages 1 and 2");
            Check(group.IsDirty, "The tab shows the unsaved mark");
            string during = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            Check(during == before, "The file on disk is not touched");

            var seen = await PdfThumbnailService.GetWordRectsAsync(path, 1);
            Check(seen != null && seen.Count > 30, $"Select / the I-beam see {seen?.Count} words on page 1 at once");
            Check(((await PdfThumbnailService.GetWordRectsAsync(path, 3))?.Count ?? 0) == 0, "…and none on the page that was not read");
            var hits = new List<SearchHit>();
            await PdfThumbnailService.SearchAsync(path, "thiết kế", false, false, (batch, _) => hits.AddRange(batch));
            Check(hits.Count >= 2 && hits.All(h => h.Path == path), $"Find finds \"thiết kế\" on {hits.Select(h => h.PageNumber).Distinct().Count()} pages and reports the file's own path");

            // the sheet-info reader takes the pending text as text (not OCR)
            var number = TitleBlockRegion_Around(lines);
            var read = await SheetInfoReader.ReadAsync(path, new[] { 1 }, _ => new TitleBlockLayout { Number = number }, SheetReadSource.TextLayer, null, default);
            Check(read.Pages[0].Number.Text.Contains("Số hiệu") || read.Pages[0].Number.Text.Length > 0 && !read.Pages[0].Number.FromOcr, "Read sheet info finds text in the area without OCR (\"" + read.Pages[0].Number.Text + "\")");

            // Undo takes the text away, Redo brings it back
            host.Undo();
            await Task.Delay(300);
            Check(!OcrPendingStore.HasPending(path) && !group.IsDirty && ((await PdfThumbnailService.GetWordRectsAsync(path, 1))?.Count ?? 0) == 0, "Undo removes the text and the unsaved mark");
            host.Redo();
            await Task.Delay(300);
            Check(OcrPendingStore.HasPending(path) && group.IsDirty && (await PdfThumbnailService.GetWordRectsAsync(path, 2))?.Count > 30, "Redo brings it back");

            // Ctrl+S: the text goes into the file
            bool saved = await host.SaveGroupAsync(group, saveAs: false);
            await Task.Delay(500);
            Check(saved && !OcrPendingStore.HasPending(path) && !group.IsDirty, "Save writes it and clears the unsaved mark");
            string after = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            Check(after != before, "The file changed on disk");
            using (var doc = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfReader(path)))
                Check(doc.GetNumberOfPages() == 5 && doc.GetPage(3).GetRotation() == 90, "Its pages and rotations are as they were");
            var onDisk = await PdfThumbnailService.GetWordRectsAsync(path, 1);
            Check(onDisk != null && onDisk.Count > 30, $"The saved file itself has {onDisk?.Count} words on page 1");
            Check(XTHistory.Read(path).Any(h => h.Action.Contains("OCR text on 2 pages")), "The file's history has a line for the OCR text");
        }, copyFrom: scan);
    }

    /// <summary>A region around the second line (where "Số hiệu" is), as the sheet-number area of the test.</summary>
    static TitleBlockRegion TitleBlockRegion_Around(List<(string Text, System.Windows.Rect Box)> lines)
    {
        var box = lines[1].Box;
        return new TitleBlockRegion(box.Left, box.Top - 0.005, box.Right, box.Bottom + 0.005);
    }
}
