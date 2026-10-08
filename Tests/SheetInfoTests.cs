using System.IO;
using System.Windows;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Services.Ocr;

internal static partial class Program
{
    /// <summary>The redesigned Read sheet info window on a scan: areas drawn / moved / resized, OCR of just the areas, per-page results, keeping typed values, zoom.</summary>
    static void TestReadSheetInfoOnScan()
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        if (!OcrService.IsAvailable()) { Console.WriteLine("Sheet info on a scan: no embedded Python in this build; skipped"); return; }
        string prior = TitleBlockStore.FilePath;
        TitleBlockStore.FilePath = System.IO.Path.Combine(Output, "sheet-info-areas.json");
        try { File.Delete(TitleBlockStore.FilePath); } catch { }
        try
        {
            string folder = System.IO.Path.Combine(Output, "sheet-info");
            var (scan, lines, _, _) = BuildOcrScanPdf(folder);
            // where the words are: one OCR run of the first page
            var page1 = OcrService.RunAsync(scan, new[] { 0 }, new OcrOptions(), null, default).GetAwaiter().GetResult()[0];
            OcrWord Word(string text) => page1.Words.First(w => w.Text.Contains(text, StringComparison.Ordinal));
            TitleBlockRegion Around(IEnumerable<OcrWord> words) => new(words.Min(w => w.U1) - 0.006, words.Min(w => w.V1) - 0.006, words.Max(w => w.U2) + 0.006, words.Max(w => w.V2) + 0.006);
            var number = Around(new[] { Word("KC-02") });
            var scale = Around(new[] { Word("1/100") });
            var titleWords = page1.Words.Where(w => w.V1 < lines[0].Box.Bottom + 0.01 && w.V2 > lines[0].Box.Top - 0.01).ToList();
            var title = Around(titleWords);

            var window = new ReadSheetInfoWindow(scan, new[] { 1, 2, 3, 4 }, 1);
            Offscreen(window);
            Pump(TimeSpan.FromMilliseconds(1500));
            Check(window.Rows.Count == 4 && window.CurrentRow?.Page == 1 && window.CurrentRow.SizeKey.Length > 0, "The pages are listed and the first is shown (" + window.CurrentRow?.SizeKey + ")");
            Check(window.Rows.Select(r => r.SizeKey).Distinct().Count() == 1, "A page turned 90 / 270 degrees has the same paper size as the others (the key follows the displayed page)");

            // draw an area with the mouse: the page is 1000 units wide, so a point is (fraction x 1000, fraction x height)
            double height = 1000.0 * 1500 / 2480;
            window.SelectField(0);
            window.OverlayDown(new Point(number.Left * 1000, number.Top * height));
            window.OverlayMove(new Point((number.Left + number.Right) / 2 * 1000, (number.Top + number.Bottom) / 2 * height));
            window.OverlayUp(new Point(number.Right * 1000, number.Bottom * height));
            var drawn = window.CurrentAreas.Number;
            Check(drawn != null && Math.Abs(drawn.Left - number.Left) < 0.002 && Math.Abs(drawn.Right - number.Right) < 0.002 && Math.Abs(drawn.Bottom - number.Bottom) < 0.002, "Dragging on the page draws the sheet number area");
            Check(TitleBlockStore.Get(window.CurrentRow!.SizeKey)?.Number is { } saved && Math.Abs(saved.Left - number.Left) < 0.002, "...and it is remembered for this paper size");
            // move it by dragging inside, then resize it with the right handle
            double cx = (number.Left + number.Right) / 2 * 1000, cy = (number.Top + number.Bottom) / 2 * height;
            window.OverlayDown(new Point(cx, cy));
            window.OverlayMove(new Point(cx + 20, cy + 10));
            window.OverlayUp(new Point(cx + 20, cy + 10));
            var moved = window.CurrentAreas.Number!;
            Check(Math.Abs(moved.Left - (number.Left + 0.02)) < 0.002 && Math.Abs(moved.Top - (number.Top + 10 / height)) < 0.002 && Math.Abs((moved.Right - moved.Left) - (number.Right - number.Left)) < 0.001, "Dragging inside an area moves it as it is");
            double midY = (moved.Top + moved.Bottom) / 2 * height;
            window.OverlayDown(new Point(moved.Right * 1000, midY)); // the east handle
            window.OverlayMove(new Point(moved.Right * 1000 + 30, midY));
            window.OverlayUp(new Point(moved.Right * 1000 + 30, midY));
            var resized = window.CurrentAreas.Number!;
            Check(Math.Abs(resized.Right - (moved.Right + 0.03)) < 0.002 && Math.Abs(resized.Left - moved.Left) < 0.001 && Math.Abs(resized.Top - moved.Top) < 0.001, "A handle of the selected area resizes it");

            // the three areas, exact
            window.SetAreaForTest(0, number);
            window.SetAreaForTest(1, title);
            window.SetAreaForTest(2, scale);
            Check(window.CurrentAreas.HasAny && window.CurrentAreas.Title != null && window.CurrentAreas.Scale != null, "All three areas are set");

            // read all pages: the PDF has no text, so the areas are read by OCR
            window.Dispatcher.Invoke(() => { _ = window.ReadAllForTestAsync(); });
            WaitFor(() => !window.IsBusy, 60000);
            var rows = window.Rows;
            Console.WriteLine("read: " + string.Join(" | ", rows.Select(r => $"{r.Page}: [{r.Number}] [{r.Title}] [{r.Scale}]")));
            Check(rows.All(r => r.Number.Contains("KC-02") && r.Scale.Contains("1/100")), "Every page has its sheet number and scale read from the scan (also the pages turned 90 / 270)");
            Check(rows.All(r => r.Title.Contains("CẤU", StringComparison.OrdinalIgnoreCase) || r.Title.Contains("DẦM", StringComparison.OrdinalIgnoreCase) || r.Title.Contains("SÀN", StringComparison.OrdinalIgnoreCase)), "...and the title");
            Check(rows.All(r => r.NumberFromOcr && r.ScaleFromOcr && r.StatusBrush == System.Windows.Media.Brushes.Orange), "OCR fields are flagged (amber) for a look");
            Check(window.StatusText.Contains("by OCR"), "The status says how many fields were read by OCR (" + window.StatusText + ")");

            // per page: each page's result is on its page
            window.Dispatcher.Invoke(() => { _ = window.ShowPageForTestAsync(3); });
            WaitFor(() => window.CurrentRow?.Page == 3, 5000);
            Pump(TimeSpan.FromMilliseconds(600));
            Check(window.NumberBox.Text.Contains("KC-02") && window.CurrentRow!.Page == 3, "Page 3 shows its own fields");
            SavePng(window, "sheet-info-window");

            // typed values survive a new read
            window.NumberBox.Text = "A-101";
            Check(window.CurrentRow!.NumberEdited && !window.CurrentRow.NumberFromOcr && window.CurrentRow.StatusBrush == System.Windows.Media.Brushes.RoyalBlue, "Typing in a field marks it as the user's");
            window.Dispatcher.Invoke(() => { _ = window.ReadAllForTestAsync(); });
            WaitFor(() => !window.IsBusy, 60000);
            Check(rows[2].Number == "A-101" && rows[0].Number.Contains("KC-02"), "Reading again keeps what the user typed");

            // PDF text only: nothing in a scan
            window.SourceBox.SelectedIndex = 1;
            window.Dispatcher.Invoke(() => { _ = window.ReadAllForTestAsync(); });
            WaitFor(() => !window.IsBusy, 30000);
            Check(rows[0].Number == "" && rows[2].Number == "A-101" && window.StatusText.Contains("no text layer"), "PDF text only finds nothing on a scan and says why");

            // one page only
            window.SourceBox.SelectedIndex = 0;
            window.Dispatcher.Invoke(() => { _ = window.ReadPagesForTestAsync(new[] { 2 }); });
            WaitFor(() => !window.IsBusy, 30000);
            Check(rows[1].Number.Contains("KC-02") && rows[0].Number == "" && rows[3].Number == "", "Reading one page changes only that page");

            // zoom and pan
            window.Dispatcher.Invoke(() => { _ = window.ShowPageForTestAsync(1); });
            WaitFor(() => window.CurrentRow?.Page == 1, 5000);
            Pump(TimeSpan.FromMilliseconds(500));
            window.FitToWindow();
            double fit = window.Zoom;
            window.SetZoomAround(fit * 2, new Point(500, 300));
            Check(Math.Abs(window.Zoom - fit * 2) < 1e-6 && fit > 0, "Zoom in around a point");
            window.FitToWindow();
            Check(Math.Abs(window.Zoom - fit) < 1e-6, "Fit brings it back");

            // the table of all pages
            window.SetTableView(true);
            Check(window.TableVisible, "The table view shows all pages for bulk edits");
            window.SetTableView(false);
            Check(!window.TableVisible, "...and the page view comes back");

            window.ApplyForTest();
            Check(window.Result.Count == 2 && window.Result.All(r => r.Info.No.Length > 0), "Writing takes the pages that have data (" + window.Result.Count + ")");
            window.Close();
        }
        finally { TitleBlockStore.FilePath = prior; }
    }

    /// <summary>Waits (pumping the UI) until <paramref name="done"/> is true.</summary>
    static void WaitFor(Func<bool> done, int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!done() && DateTime.UtcNow < until) Pump(TimeSpan.FromMilliseconds(80));
        Pump(TimeSpan.FromMilliseconds(150));
    }
}
