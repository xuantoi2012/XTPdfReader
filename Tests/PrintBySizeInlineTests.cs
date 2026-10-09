using System.IO;
using iText.IO.Image;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    /// <summary>Two A4 and two A3 pages; pages 2 and 3 carry a color picture, the others are lines only.</summary>
    static string BuildColorMixPdf(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "colormix.pdf");
        byte[] png;
        using (var bmp = new System.Drawing.Bitmap(300, 300))
        {
            for (int y = 0; y < 300; y++) for (int x = 0; x < 300; x++) bmp.SetPixel(x, y, System.Drawing.Color.FromArgb(220, 30 + x % 200, 60));
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            png = ms.ToArray();
        }
        using (var doc = new PdfDocument(new PdfWriter(path)))
        {
            for (int i = 1; i <= 4; i++)
            {
                var page = doc.AddNewPage(i > 2 ? PageSize.A3 : PageSize.A4);
                var canvas = new PdfCanvas(page);
                canvas.MoveTo(50, 50).LineTo(300, 300).Stroke();
                var picture = ImageDataFactory.Create(png);
                if (i == 1) { canvas.AddImageFittedIntoRectangle(picture, new Rectangle(20, 20, 20, 20), false); }       // 20 x 20 pt: a logo (0.08% of the sheet)
                if (i == 2) canvas.AddImageFittedIntoRectangle(picture, new Rectangle(0, 0, page.GetPageSize().GetWidth(), page.GetPageSize().GetHeight()), false);          // the whole sheet
                if (i == 3) canvas.AddImageFittedIntoRectangle(picture, new Rectangle(0, 0, page.GetPageSize().GetWidth() * 0.9f, page.GetPageSize().GetHeight() * 0.8f), false); // most of the sheet
            }
        }
        return path;
    }

    /// <summary>The print dialog: pages with color pictures are picked as color, the cards follow the range, the size ticks and the color pages.</summary>
    static void TestPrintBySizeInline()
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        string pdf = BuildColorMixPdf(System.IO.Path.Combine(Output, "bysize-inline"));
        var found = PdfColorPages.Find(pdf, 5);
        Check(found.SetEquals(new[] { 2, 3 }), "Pages with real color are found; the small logo of page 1 is not (" + string.Join(",", found.OrderBy(n => n)) + ")");

        var window = new PrintWindow(new[] { (pdf, 1), (pdf, 2), (pdf, 3), (pdf, 4) }, 0) { Left = -32000, Top = -32000, ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual };
        window.Show();
        WaitSync(3000);
        var host = (System.Windows.Controls.Border)window.FindName("BySizeHost")!;
        var panel = (PrintBySizePanel)host.Child;
        var colorBox = (System.Windows.Controls.TextBox)window.FindName("ColorPagesBox")!;
        var stat = (System.Windows.Controls.TextBlock)window.FindName("ColorStat")!;
        Check(colorBox.Text == "2-3", "The color pages are filled in from the pictures: " + colorBox.Text);
        Check(stat.Text.StartsWith("2 of 4"), "…and counted: " + stat.Text);
        SavePng(window, "print-bysize");
        string[] keys() => panel.RowsForTest.Select(r => r.Class).OrderBy(k => k).ToArray();
        Check(keys().SequenceEqual(new[] { "A3 · color", "A3", "A4 · color", "A4" }.OrderBy(k => k)), "A card for each size and color: " + string.Join(" | ", keys()));

        colorBox.Text = "1";
        WaitSync(400);
        Check(keys().SequenceEqual(new[] { "A3", "A4 · color", "A4" }.OrderBy(k => k)), "Color pages typed by hand change the cards: " + string.Join(" | ", keys()));
        colorBox.Text = "2-3";
        WaitSync(300);

        var pagesRange = (System.Windows.Controls.RadioButton)window.FindName("PagesRange")!;
        var rangeBox = (System.Windows.Controls.TextBox)window.FindName("RangeBox")!;
        pagesRange.IsChecked = true;
        rangeBox.Text = "3-4";
        WaitSync(400);
        Check(keys().SequenceEqual(new[] { "A3 · color", "A3" }.OrderBy(k => k)), "Range 3-4: only the A3 cards: " + string.Join(" | ", keys()));

        var bySize = (System.Windows.Controls.RadioButton)window.FindName("PagesBySize")!;
        bySize.IsChecked = true;
        WaitSync(400);
        var checks = ((System.Windows.Controls.StackPanel)window.FindName("SizeChecks")!).Children.OfType<System.Windows.Controls.CheckBox>().ToList();
        Check(checks.Count == 2 && ((System.Windows.FrameworkElement)window.FindName("SizeChecks")!).Visibility == System.Windows.Visibility.Visible, "Pages by paper size lists the two sizes");
        checks.First(c => c.Content.ToString()!.StartsWith("A3")).IsChecked = false;
        WaitSync(400);
        Check(keys().SequenceEqual(new[] { "A4 · color", "A4" }.OrderBy(k => k)), "A3 unticked: only the A4 cards: " + string.Join(" | ", keys()));
        window.Close();
    }

    static void WaitSync(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(20);
        }
    }
}

internal static partial class Program
{
    /// <summary>Manual check on the real drawing sets (paths in XT_COLOR_FILES, separated by |): how many pages count as color pages at each limit, and how long the reading takes.</summary>
    static void TestColorPagesRealFile()
    {
        foreach (string source in (Environment.GetEnvironmentVariable("XT_COLOR_FILES") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!File.Exists(source)) { Console.WriteLine("Color pages: missing " + source); continue; }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var share = PdfColorPages.ShareAsync(source).GetAwaiter().GetResult();
            Console.WriteLine($"{System.IO.Path.GetFileName(source)}: {share.Count} pages read in {watch.Elapsed.TotalSeconds:0.0}s");
            foreach (int tenths in new[] { 1, 2, 5, 10, 20, 50 })
                Console.WriteLine($"  from {tenths / 10.0:0.0}% colored: {PdfColorPages.PagesAbove(share, tenths).Count} pages");
            Console.WriteLine("  first pages: " + string.Join(", ", share.OrderBy(f => f.Key).Take(8).Select(f => $"p{f.Key}={f.Value:0.00}%")));
            Console.WriteLine("  most colored: " + string.Join(", ", share.OrderByDescending(f => f.Value).Take(10).Select(f => $"p{f.Key}={f.Value:0.0}%")));
        }
    }
}
