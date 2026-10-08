using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using iText.IO.Image;
using iText.Kernel.Geom;
using Rect = System.Windows.Rect;
using Point = System.Windows.Point;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Services.Ocr;

internal static partial class Program
{
    private static readonly (string Text, double Size)[] OcrLines =
    {
        ("BẢN VẼ KẾT CẤU DẦM SÀN TẦNG 2", 52),
        ("Tỷ lệ: 1/100    Số hiệu: KC-02    Ngày: 08/10/2026", 38),
        ("Dầm D1 300x600, thép Ø20 a150, cao độ +3.600", 38),
        ("Chủ đầu tư: Công ty Cổ phần Xây dựng Hà Nội", 30),
        ("Người thiết kế: Nguyễn Văn Tới    Kiểm tra: Trần Thị Hương", 27),
    };

    /// <summary>A scanned sheet: Vietnamese text drawn as a picture (2480 x 1500 px = 300 dpi on a 595 x 360 pt page), with the display box (page fractions) of every line.</summary>
    static (BitmapSource Picture, List<(string Text, System.Windows.Rect Box)> Lines) OcrScanPicture(bool blurred = false)
    {
        const int width = 2480, height = 1500;
        var lines = new List<(string, System.Windows.Rect)>();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, width, height));
            double y = 90;
            foreach (var (text, size) in OcrLines)
            {
                var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("vi-VN"), FlowDirection.LeftToRight, new Typeface("Arial"), size * 1.6, Brushes.Black, 1.0);
                dc.DrawText(formatted, new System.Windows.Point(120, y));
                lines.Add((text, new System.Windows.Rect(120.0 / width, y / height, formatted.WidthIncludingTrailingWhitespace / width, formatted.Height / height)));
                y += formatted.Height * 1.9;
            }
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        BitmapSource picture = new FormatConvertedBitmap(target, PixelFormats.Gray8, null, 0);
        if (blurred)
        {
            // a scan is never as crisp as a render: a soft edge and grain
            var pixels = new byte[width * height];
            picture.CopyPixels(pixels, width, 0);
            var soft = new byte[pixels.Length];
            var random = new Random(5);
            for (int yy = 1; yy < height - 1; yy++)
                for (int xx = 1; xx < width - 1; xx++)
                {
                    int sum = pixels[yy * width + xx] * 4 + pixels[yy * width + xx - 1] + pixels[yy * width + xx + 1] + pixels[(yy - 1) * width + xx] + pixels[(yy + 1) * width + xx];
                    soft[yy * width + xx] = (byte)Math.Clamp(sum / 8 + random.Next(-18, 18), 0, 255);
                }
            picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, soft, width);
        }
        picture.Freeze();
        return (picture, lines);
    }

    /// <summary>Five pages: crisp scan, soft scan, crisp scan on a /Rotate 90 page, on a /Rotate 270 page, and a page with real text.</summary>
    static (string Path, List<(string Text, Rect Box)> Lines, BitmapSource Picture, BitmapSource Soft) BuildOcrScanPdf(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        var (picture, lines) = OcrScanPicture();
        var (soft, _) = OcrScanPicture(blurred: true);
        string scan = System.IO.Path.Combine(folder, "scan.pdf");
        using (var doc = new PdfDocument(new PdfWriter(scan)))
        {
            foreach (var (image, rotation) in new[] { (picture, 0), (soft, 0), (picture, 90), (picture, 270) })
            {
                var placed = rotation switch { 90 => (BitmapSource)new TransformedBitmap(image, new RotateTransform(270)), 270 => new TransformedBitmap(image, new RotateTransform(90)), _ => image };
                float w = (float)(placed.PixelWidth * 0.24), h = (float)(placed.PixelHeight * 0.24);
                var page = doc.AddNewPage(new PageSize(w, h));
                page.SetRotation(rotation);
                using var stream = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(placed));
                encoder.Save(stream);
                new PdfCanvas(page).AddImageFittedIntoRectangle(ImageDataFactory.Create(stream.ToArray()), new Rectangle(0, 0, w, h), false);
            }
            var textPage = doc.AddNewPage(PageSize.A4);
            new PdfCanvas(textPage).BeginText().SetFontAndSize(iText.Kernel.Font.PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA), 14).MoveText(72, 700).ShowText("This page already has real text so it is not read again.").EndText();
        }
        return (scan, lines, picture, soft);
    }

    /// <summary>OCR end to end on scanned Vietnamese sheets: the words, an invisible text layer on pages of every rotation, Find and the sheet-info reader on the result.</summary>
    static void TestOcrEndToEnd()
    {
        if (!OcrService.IsAvailable())
        {
            Console.WriteLine("OCR: not available in this build (no embedded Python); skipped");
            return;
        }
        string folder = System.IO.Path.Combine(Output, "ocr");
        var (scan, lines, picture, soft) = BuildOcrScanPdf(folder);
        var reported = new List<(int, int)>();
        var started = DateTime.UtcNow;
        var results = OcrService.RunAsync(scan, null, new OcrOptions(), new Progress<(int Done, int Total)>(p => { lock (reported) reported.Add(p); }), default).GetAwaiter().GetResult();
        Console.WriteLine($"OCR of 5 pages took {(DateTime.UtcNow - started).TotalSeconds:0.0} s, words per page: {string.Join(", ", results.Select(r => r.Words.Count))}");
        Check(results.Count == 5 && results.Select(r => r.PageIndex).SequenceEqual(new[] { 0, 1, 2, 3, 4 }), "Every page is answered, in order");
        Check(results[4].Skipped && results[4].Words.Count == 0 && !results[0].Skipped, "A page that already has text is skipped by default");
        Check(reported.Count > 0 && reported[^1] == (5, 5), "Progress ends at 5 of 5");

        // what was read: the words of the crisp page
        string readText = string.Join(" ", results[0].Words.Select(w => w.Text));
        foreach (string expected in new[] { "KC-02", "08/10/2026", "300x600,", "Hà", "Nội", "thiết", "Nguyễn", "Hương", "1/100" })
            Check(readText.Contains(expected, StringComparison.Ordinal), $"The crisp page reads \"{expected}\"");
        var wanted = lines.SelectMany(l => l.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        double recall = wanted.Count(w => results[0].Words.Any(r => r.Text == w)) / (double)wanted.Count;
        double softRecall = wanted.Count(w => results[1].Words.Any(r => r.Text == w)) / (double)wanted.Count;
        Console.WriteLine($"word recall: crisp {recall:0.00}, soft scan {softRecall:0.00}");
        Check(recall >= 0.80 && softRecall >= 0.70, $"Most words are right (crisp {recall:0.00}, soft {softRecall:0.00})");

        // the first words sit on the line they were drawn on
        var firstLine = lines[1].Box;
        var onLine = results[0].Words.FirstOrDefault(w => w.Text.StartsWith("Tỷ", StringComparison.Ordinal));
        Check(onLine != null && onLine.U1 > firstLine.Left - 0.01 && onLine.U1 < firstLine.Left + 0.03 && Math.Abs((onLine.V1 + onLine.V2) / 2 - (firstLine.Top + firstLine.Height / 2)) < 0.02, "A word is found where it was drawn");

        // the invisible layer, on a copy
        string output = PdfOcrWriter.OutputPathFor(scan);
        int written = PdfOcrWriter.Write(scan, output, results);
        Check(written > 100 && output.EndsWith("scan (OCR).pdf") && File.Exists(output), $"A searchable copy is written ({written} words)");
        Check(PdfOcrWriter.OutputPathFor(scan).EndsWith("scan (OCR 2).pdf"), "A second run does not overwrite the first");
        Check(new FileInfo(scan).Length < new FileInfo(output).Length + 50000 && new FileInfo(output).Length < new FileInfo(scan).Length + 400000, "The copy is only a little bigger (the text layer)");
        using (var doc = new PdfDocument(new PdfReader(output)))
            Check(doc.GetNumberOfPages() == 5 && doc.GetPage(3).GetRotation() == 90 && doc.GetPage(4).GetRotation() == 270, "Pages and rotations are kept");

        foreach (int page in new[] { 1, 2, 3, 4 })
        {
            var words = PdfThumbnailService.GetWordRectsAsync(output, page).GetAwaiter().GetResult();
            Check(words != null && words.Count > 35, $"Page {page}: the viewer finds {words?.Count} words in the new text layer");
            if (words == null) continue;
            // the displayed page is the same on every rotation: every word of the new layer must lie on one of the lines that were drawn
            bool OnALine(double u, double v) => lines.Any(l => u > l.Box.Left - 0.02 && u < l.Box.Right + 0.02 && v > l.Box.Top - 0.025 && v < l.Box.Bottom + 0.025);
            int onLines = words.Count(w => OnALine((w.U1 + w.U2) / 2, (w.V1 + w.V2) / 2));
            Check(onLines >= words.Count * 0.95, $"Page {page} (rotation {(page == 3 ? 90 : page == 4 ? 270 : 0)}): {onLines} of {words.Count} words lie on the lines, where the picture shows them");
        }
        var hits = new List<SearchHit>();
        PdfThumbnailService.SearchAsync(output, "thiết kế", false, false, (batch, _) => hits.AddRange(batch)).GetAwaiter().GetResult();
        Check(hits.Count >= 3 && hits.Select(h => h.PageNumber).Distinct().Count() >= 3, $"Find finds \"thiết kế\" on {hits.Select(h => h.PageNumber).Distinct().Count()} scanned pages (before: none)");
        var before = new List<SearchHit>();
        PdfThumbnailService.SearchAsync(scan, "thiết kế", false, false, (batch, _) => before.AddRange(batch)).GetAwaiter().GetResult();
        Check(before.Count == 0, "The scan itself has nothing to find");

        // a region read for the sheet number: the title-block reader now has text on a scan
        var region = new TitleBlockRegion(lines[1].Box.Left + lines[1].Box.Width * 0.38, lines[1].Box.Top - 0.01, lines[1].Box.Left + lines[1].Box.Width * 0.62, lines[1].Box.Bottom + 0.01);
        using (var doc = new PdfDocument(new PdfReader(output)))
        {
            foreach (int page in new[] { 1, 3, 4 })
            {
                var rect = TitleBlockReader.ToUserSpace(doc.GetPage(page), region);
                var text = TitleBlockReader.TextIn(doc.GetPage(page), region);
                Check(text.Contains("KC-02") || text.Contains("KC"), $"Page {page}: the region of the sheet number reads \"{text}\"");
            }
        }

        // options: read even the page that has text, and one page only
        var again = OcrService.RunAsync(scan, new[] { 4 }, new OcrOptions(SkipPagesWithText: false), null, default).GetAwaiter().GetResult();
        Check(again.Count == 1 && !again[0].Skipped, "With 'skip' off the page with text is read as well");
        var cancel = new CancellationTokenSource();
        cancel.CancelAfter(300);
        bool cancelled = false;
        try { OcrService.RunAsync(scan, null, new OcrOptions(), null, cancel.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "A running OCR stops when cancelled");
    }
}

internal static partial class Program
{
    /// <summary>The OCR dialog: page choices, the page list text, a run that ends in a searchable copy, a run on pages that already have text, cancelling.</summary>
    static void TestOcrWindow()
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        Check(XTPdfMergeApp.Controls.OcrWindow.ParsePages("1-3, 5", 6)!.SequenceEqual(new[] { 0, 1, 2, 4 }) && XTPdfMergeApp.Controls.OcrWindow.ParsePages("2;2,1", 4)!.SequenceEqual(new[] { 0, 1 }), "Page lists: ranges, commas, no repeats, in order");
        Check(XTPdfMergeApp.Controls.OcrWindow.ParsePages("0", 6) == null && XTPdfMergeApp.Controls.OcrWindow.ParsePages("7", 6) == null && XTPdfMergeApp.Controls.OcrWindow.ParsePages("3-1", 6) == null && XTPdfMergeApp.Controls.OcrWindow.ParsePages("a", 6) == null && XTPdfMergeApp.Controls.OcrWindow.ParsePages("", 6) == null, "Bad page lists are refused");
        if (!OcrService.IsAvailable()) { Console.WriteLine("OCR window: no embedded Python in this build; the run is skipped"); return; }

        string folder = System.IO.Path.Combine(Output, "ocr-window");
        var (scan, _, _, _) = BuildOcrScanPdf(folder);
        var window = new XTPdfMergeApp.Controls.OcrWindow(scan, 3, 5) { Left = -32000, Top = -32000, ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual };
        window.Show();
        Pump(TimeSpan.FromMilliseconds(300));
        Check(window.StartButton.IsEnabled && window.AllPagesButton.IsChecked == true && window.SkipBox.IsChecked == true, "All pages and 'skip pages with text' are the defaults");
        window.RangeBox.Text = "9";
        window.RangeButton.IsChecked = true;
        RunOnDispatcher(window.StartAsync);
        Check(window.OutputPath == null && window.StatusText.Contains("1-3, 7") && window.StartButton.IsEnabled, "A bad page list is explained and nothing starts");

        window.RangeBox.Text = "2-3";
        SavePng(window, "ocr-window");
        RunOnDispatcher(window.StartAsync);
        Check(window.OutputPath is { } output && File.Exists(output) && output.EndsWith("scan (OCR).pdf"), "Pages 2-3 end in a searchable copy beside the original (" + window.StatusText.Replace(Environment.NewLine, " ") + ")");
        if (window.OutputPath is { } copy)
        {
            var onPage2 = PdfThumbnailService.GetWordRectsAsync(copy, 2).GetAwaiter().GetResult();
            var onPage1 = PdfThumbnailService.GetWordRectsAsync(copy, 1).GetAwaiter().GetResult();
            Check(onPage2?.Count > 30 && (onPage1?.Count ?? 0) == 0, "Only the chosen pages got text");
        }

        var textOnly = new XTPdfMergeApp.Controls.OcrWindow(scan, 5, 5) { Left = -32000, Top = -32000, ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual };
        textOnly.Show();
        textOnly.RangeBox.Text = "5";
        textOnly.RangeButton.IsChecked = true;
        RunOnDispatcher(textOnly.StartAsync);
        Check(textOnly.OutputPath == null && textOnly.StatusText.Contains("already has text") && !File.Exists(PdfOcrWriter.OutputPathFor(scan).Replace("(OCR 2)", "(OCR 3)")) , "A page that already has text: nothing to read, no copy left behind (" + textOnly.StatusText + ")");
        textOnly.Close();
        window.Close();
    }

    /// <summary>Runs an async UI action to its end on the test's dispatcher.</summary>
    static void RunOnDispatcher(Func<Task> action)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        Exception? failure = null;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await action(); }
            catch (Exception ex) { failure = ex; }
            finally { frame.Continue = false; }
        }));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        if (failure != null) throw new Exception(failure.Message, failure);
    }
}
