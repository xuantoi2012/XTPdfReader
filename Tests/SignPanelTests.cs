using System.IO;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    /// <summary>The Sign and Marks panels open on the right of the reader, and a signed file shows its signature bar and field box.</summary>
    static void TestSignAndMarksPanels()
    {
        Directory.CreateDirectory(Output);
        string plain = Path.Combine(Output, "panels-plain.pdf");
        using (var doc = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfWriter(plain)))
        {
            doc.AddNewPage(new iText.Kernel.Geom.PageSize(595, 842));
            doc.AddNewPage(new iText.Kernel.Geom.PageSize(595, 842));
        }
        var cert = MakeTestCertificate("Nguyen Van A", "Cong ty Thu Nghiem");
        string signed = Path.Combine(Output, "panels-signed.pdf");
        PdfDigitalSignService.SignAsync(new DigitalSignRequest(plain, signed, cert, 1, 0.5, 0.75, 0.95, 0.95, "Cong ty Thu Nghiem", null, "Phe duyet", "Ha Noi", null, false)).GetAwaiter().GetResult();

        RunReaderFlow("panels-plain", async f =>
        {
            await Task.Delay(1000);
            f.Call("OpenSignPanel");
            await Task.Delay(2500);
            Check((bool)f.Get("ToolPanelOpen")!, "The Sign panel opens beside the page");
            var surface = (XTPdfMergeApp.Controls.ReaderAreaSurface)f.Get("_areaSurface")!;
            Check(surface.Active && !surface.PlaceMode && surface.Area != null, "The signature has a box to move on the page being read");
            SavePng(f.Window, "panel-sign");
            f.Call("CloseToolPanel");
            f.Call("OpenMarksPanel");
            await Task.Delay(2500);
            Check((bool)f.Get("ToolPanelOpen")! && surface.Active, "The Marks panel opens in the same place");
            SavePng(f.Window, "panel-marks");
            f.Call("CloseToolPanel");
            Check(!(bool)f.Get("ToolPanelOpen")!, "Closing the panel closes it");
        }, copyFrom: plain);

        RunReaderFlow("panels-signed", async f =>
        {
            await Task.Delay(3000);
            var banner = (System.Windows.FrameworkElement)f.Window.FindName("SignatureBanner");
            Check(banner.Visibility == System.Windows.Visibility.Visible, "A signed file shows the signature bar");
            var fields = PdfSignatureFieldService.All(f.Path);
            Check(fields.Count == 1 && fields[0].Signed, "…and its signature field is known to the reader");
            SavePng(f.Window, "panel-signed");
        }, copyFrom: signed);
    }
}

internal static partial class Program
{
    /// <summary>Two bars up at once (a shape's style bar and the text bar) sit one above the other, never on the same spot.</summary>
    static void TestBarsDoNotOverlap()
    {
        RunReaderFlow("bars-stack", async f =>
        {
            await Task.Delay(800);
            var w = f.Window;
            w.AnchorOverrideForTests = new System.Windows.Rect(300, 400, 200, 100);
            var shape = (System.Windows.Controls.Border)w.FindName("ShapeBar");
            var text = (System.Windows.Controls.Border)w.FindName("TextFormatBar");
            shape.Visibility = System.Windows.Visibility.Visible;
            text.Visibility = System.Windows.Visibility.Visible;
            w.UpdateLayout();
            f.Call("PositionFloatingBars");
            w.UpdateLayout();
            f.Call("PositionFloatingBars");
            var a = new System.Windows.Rect(shape.Margin.Left, shape.Margin.Top, shape.ActualWidth, shape.ActualHeight);
            var b = new System.Windows.Rect(text.Margin.Left, text.Margin.Top, text.ActualWidth, text.ActualHeight);
            Check(!a.IntersectsWith(b) && a.Height > 0 && b.Height > 0, "Shape bar and text bar are stacked, not on top of each other");
            shape.Visibility = text.Visibility = System.Windows.Visibility.Collapsed;
            w.AnchorOverrideForTests = null;
        });
    }
}

internal static partial class Program
{
    /// <summary>Renders the ribbon so it can be looked at (and checks that the split buttons exist and carry their commands).</summary>
    static void TestRibbonSplits()
    {
        RunReaderFlow("ribbon", async f =>
        {
            await Task.Delay(1200);
            var bar = (System.Windows.FrameworkElement)f.Window.FindName("ReaderToolbarBar");
            bar.Visibility = System.Windows.Visibility.Visible;
            f.Window.UpdateLayout();
            SavePng(bar, "ribbon");
            foreach (var name in new[] { "HighlightSplit", "MarkupSplit", "ShapesSplit", "SignSplit" })
                Check(f.Window.FindName(name) is XTPdfMergeApp.Controls.RibbonSplit, name + " is in the ribbon");
            Check(f.Window.FindName("ReaderPrintButton") == null, "Print is no longer in the ribbon (it is in the title bar)");
        });
    }
}

internal static partial class Program
{
    /// <summary>Renders the start-up splash (shadow included) to a PNG.</summary>
    static void TestSplashRender()
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        var splash = new XTPdfMergeApp.Controls.StartupSplash { Left = -32000, Top = -32000, ShowActivated = false };
        splash.Show();
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        SavePng(splash, "splash");
        splash.Close();
        Check(File.Exists(Path.Combine(Output, "splash.png")), "The splash renders");
    }
}

internal static partial class Program
{
    /// <summary>--remote-open file: opens a PDF through the MuPDF worker the way the Reader does, with timings (for files on a network drive).</summary>
    static async Task TestRemoteOpenAsync(string path)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine("remote: " + XTPdfMergeApp.Services.RemoteFileStage.IsRemote(path));
        string staged = await XTPdfMergeApp.Services.RemoteFileStage.ResolveAsync(path);
        Console.WriteLine($"resolved in {watch.Elapsed.TotalSeconds:0.0}s -> {staged}");
        watch.Restart();
        var reply = await XTPdfMergeApp.Services.ExperimentalMuPdfViewport.CommandAsync(path, "metadata");
        Console.WriteLine($"metadata in {watch.Elapsed.TotalSeconds:0.0}s: {reply.GetProperty("count").GetInt32()} pages");
        Check(reply.GetProperty("count").GetInt32() > 0, "The file opens through the worker");
    }
}

internal static partial class Program
{
    /// <summary>--icon-dump: the figures of each icon (index, size) so a Tint layer can name which figures to colour.</summary>
    static void TestIconDump()
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        foreach (var name in new[] { "select", "capture", "captures", "note", "callout", "shape_rect", "shape_oval", "shape_cloud", "shape_arrow", "shape_line", "insert", "trash", "pagerotl", "pagerotr", "extract", "eraser", "hand", "highlight", "hl", "hl_text", "hl_area", "underline", "type", "pencil", "stamp", "ruler" })
        {
            if (System.Windows.Application.Current!.TryFindResource("Ui.Icon." + name) is not System.Windows.Media.Geometry g) { Console.WriteLine(name + ": (none)"); continue; }
            var pg = System.Windows.Media.PathGeometry.CreateFromGeometry(g);
            Console.Write($"{name}: {pg.Figures.Count} figures:");
            for (int i = 0; i < pg.Figures.Count; i++)
            {
                var b = new System.Windows.Media.PathGeometry(new[] { pg.Figures[i] }).Bounds;
                Console.Write($" [{i}] {b.X:0.#},{b.Y:0.#} {b.Width:0.#}x{b.Height:0.#};");
            }
            Console.WriteLine();
        }
    }
}
