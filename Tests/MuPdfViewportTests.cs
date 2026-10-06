using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    static void TestThroughputMuPdfViewer(string source)
    {
        Check(ExperimentalMuPdfViewport.ThroughputMode, "High-memory rendering policy is active");
        Check(!XTPdfMergeApp.Controls.ContinuousPdfView.ExactRaster, "Zoom does not wait for a matching-resolution frame");
        if (Application.Current == null) CreateReaderTestApplication();
        var view = new XTPdfMergeApp.Controls.ContinuousPdfView { PrefetchPageCount = 0 };
        var host = new Window { Content = view, Width = 1320, Height = 700, WindowStyle = WindowStyle.None,
            ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        var row = new XTPdfMergeApp.Domain.PagePlacement { SourcePath = source, PageNumber = 1,
            BaseWidth = 1200, AspectRatio = .6, ReaderBitmap = Bitmap(4608, 2764) };
        int pages = 0, regions = 0;
        view.PageRenderer = (_, width, _, _) => { pages++; return Task.FromResult<System.Windows.Media.Imaging.BitmapSource?>(Bitmap(4608, 2764)); };
        view.RegionRenderer = (_, _, _, rects, _, _) => { regions++; return Task.FromResult(rects.Select(r => (System.Windows.Media.Imaging.BitmapSource?)RegionPattern(r)).ToList()); };
        host.Show(); host.UpdateLayout();
        try
        {
            view.SetDocument(new[] { row }, 1);
            Pump(TimeSpan.FromMilliseconds(150));
            int initial = pages;
            foreach (double zoom in new[] { 1.1, 1.5, 2.0, 3.0, 1.0 })
            {
                view.ZoomAt(zoom, new Point(600, 300));
                Pump(TimeSpan.FromMilliseconds(100));
            }
            Check(pages == initial, "Cached sharp page survives nearby zooms without replacement page renders");
            Check(regions == 0, "Nearby zooms do not request crops or change the image resolution tier");
        }
        finally { host.Close(); }
    }

    static async Task TestFastMuPdfAsync(string drainage, string binhDo)
    {
        try
        {
            long loads = PdfThumbnailService.NativePageLoads;
            foreach (var (path, page) in new[] { (drainage, 0), (drainage, 1), (binhDo, 0), (binhDo, 1), (binhDo, 2), (binhDo, 3), (drainage, 0) })
            {
                Check(ExperimentalMuPdfViewport.CanRenderFullPage(path, page, ""), "Fast trial covers requested document/page");
                var watch = Stopwatch.StartNew();
                var bitmap = await PdfThumbnailService.RenderPageAsync(path, page, 1200);
                if (bitmap == null) await ExperimentalMuPdfViewport.RenderFullPageAsync(path, page, 1200, default);
                Check(bitmap is { IsFrozen: true } && bitmap.PixelWidth == (ExperimentalMuPdfViewport.ThroughputMode ? 4608 : 1200) && bitmap.PixelHeight > 0,
                    "Visible full-page MuPDF output reaches WPF at exact width");
                Check(RegionPixels(bitmap!).Any(v => v < 200), "Full page contains drawing content");
                Console.WriteLine(JsonSerializer.Serialize(new { File = System.IO.Path.GetFileName(path), Page = page + 1,
                    FullPageMs = watch.Elapsed.TotalMilliseconds, WorkerMiB = ExperimentalMuPdfViewport.WorkerPrivateMiB }));
            }
            Check(PdfThumbnailService.NativePageLoads == loads, "Visible trial pages do not parse duplicate PDFium pages");
            if (ExperimentalMuPdfViewport.ThroughputMode)
            {
                var first = await ExperimentalMuPdfViewport.RenderFullPageAsync(drainage, 0, 1100, default);
                var second = await ExperimentalMuPdfViewport.RenderFullPageAsync(drainage, 0, 2200, default);
                Check(ReferenceEquals(first, second), "Nearby zoom widths reuse the same sharp WPF bitmap without IPC");
                var watch = Stopwatch.StartNew();
                var background = await Task.WhenAll(
                    PdfThumbnailService.RenderPageAsync(binhDo, 0, 1200, priority: PdfRenderPriority.Background),
                    PdfThumbnailService.RenderPageAsync(binhDo, 1, 1200, priority: PdfRenderPriority.Background),
                    PdfThumbnailService.RenderPageAsync(drainage, 0, 341, priority: PdfRenderPriority.Thumbnail),
                    PdfThumbnailService.RenderPageAsync(drainage, 1, 341, priority: PdfRenderPriority.Thumbnail));
                Check(background.All(b => b is { IsFrozen: true }), "Concurrent background/thumbnail requests return complete images");
                Check(ExperimentalMuPdfViewport.RunningWorkerCount == 4, "Separate foreground and background processes remain available");
                Check(PdfThumbnailService.NativePageLoads == loads, "Thumbnail renders also bypass PDFium");
                Console.WriteLine(JsonSerializer.Serialize(new { BackgroundBatchMs = watch.Elapsed.TotalMilliseconds,
                    Workers = ExperimentalMuPdfViewport.RunningWorkerCount, WorkerMiB = ExperimentalMuPdfViewport.WorkerPrivateMiB }));
                var uncached = new Int32Rect(1101, 991, 1201, 801);
                watch.Restart();
                var concurrent = await Task.WhenAll(
                    ExperimentalMuPdfViewport.RenderAsync(drainage, 0, 6301, 4455, new[] { uncached }, default),
                    ExperimentalMuPdfViewport.RenderAsync(drainage, 1, 6301, 4455, new[] { uncached }, default),
                    ExperimentalMuPdfViewport.RenderAsync(binhDo, 0, 6301, 4455, new[] { uncached }, default, PdfRenderPriority.Background),
                    ExperimentalMuPdfViewport.RenderAsync(binhDo, 1, 6301, 4455, new[] { uncached }, default, PdfRenderPriority.Background));
                Check(concurrent.All(batch => batch.Count == 1 && batch[0] is { PixelWidth: 1201, PixelHeight: 801, IsFrozen: true }),
                    "Four independent uncached render requests deliver correctly sized bitmaps");
                Console.WriteLine(JsonSerializer.Serialize(new { UncachedFourLaneBatchMs = watch.Elapsed.TotalMilliseconds,
                    WorkerMiB = ExperimentalMuPdfViewport.WorkerPrivateMiB,
                    ParentMiB = Process.GetCurrentProcess().PrivateMemorySize64 / 1048576d,
                    BridgeCacheMiB = ExperimentalMuPdfViewport.CacheStats.Bytes / 1048576d }));
                if (ExperimentalMuPdfViewport.BalancedMode)
                    for (int worker = 0; worker < 4; worker++)
                    {
                        var stats = await ExperimentalMuPdfViewport.CommandAsync("", "stats", workerIndex: worker);
                        Check(stats.GetProperty("rasterBytes").GetInt64() == 0 && stats.GetProperty("displayLists").GetInt32() <= 8,
                            "Balanced worker retains bounded display lists, not duplicate raster data");
                    }
                var replay = await ExperimentalMuPdfViewport.RenderAsync(drainage, 0, 6301, 4455, new[] { uncached }, default);
                Check(ReferenceEquals(concurrent[0][0], replay[0]), "Viewport replay reuses the immutable WPF cache entry");
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                bool observed = false;
                try { await ExperimentalMuPdfViewport.RenderFullPageAsync(drainage, 0, 1200, cancelled.Token); }
                catch (OperationCanceledException) { observed = true; }
                Check(observed, "Cache hits respect already-cancelled requests");
            }
            foreach (int width in new[] { 4249, 6001, 7199 })
            {
                int height = (int)Math.Round(width * 842d / 1191);
                var crop = new Int32Rect(133, 191, 1001, 701);
                var batch = await ExperimentalMuPdfViewport.RenderAsync(drainage, 0, width, height, new[] { crop }, default);
                Check(batch[0] is { PixelWidth: 1001, PixelHeight: 701 }, "Fractional transformed clip normalizes exact pixel geometry");
            }
        }
        finally { ExperimentalMuPdfViewport.Shutdown(); }
    }

    static void TestPredictiveMuPdfViewer(string source)
    {
        if (Application.Current == null) CreateReaderTestApplication();
        var view = new XTPdfMergeApp.Controls.ContinuousPdfView { PrefetchPageCount = 0 };
        var host = new Window { Content = view, Width = 1320, Height = 700, WindowStyle = WindowStyle.None,
            ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        var row = new XTPdfMergeApp.Domain.PagePlacement { SourcePath = source, PageNumber = 1,
            BaseWidth = 1200, AspectRatio = .6, ReaderBitmap = Bitmap(512, 307) };
        var widths = new List<int>();
        view.PageRenderer = (_, width, _, _) => Task.FromResult<System.Windows.Media.Imaging.BitmapSource?>(Bitmap(width, (int)(width * .6)));
        view.RegionRenderer = (_, width, _, rects, _, _) =>
        {
            widths.Add(width);
            return Task.FromResult(rects.Select(r => (System.Windows.Media.Imaging.BitmapSource?)RegionPattern(r)).ToList());
        };
        XTPdfMergeApp.Controls.ContinuousPdfView.InvalidateCachedRegions((_, _) => true);
        host.Show(); host.UpdateLayout();
        try
        {
            view.SetDocument(new[] { row }, 3);
            Pump(TimeSpan.FromMilliseconds(300));
            Check(widths.Count == 1 && widths[0] >= 4200, "Predictive viewer requests bounded surplus resolution");
            view.ZoomAt(3.3, new Point(600, 300));
            Pump(TimeSpan.FromMilliseconds(150));
            Check(widths.Count == 1, "Surplus crop stays sharp across nearby zoom without another render");
            view.ZoomAt(3, new Point(600, 300));
            Pump(TimeSpan.FromMilliseconds(150));
            Check(widths.Count == 1, "Returning to prior zoom reuses rendered crop");
            view.ZoomAt(4, new Point(600, 300));
            Pump(TimeSpan.FromMilliseconds(300));
            Check(widths.Count == 2 && widths[1] > widths[0], "Crossing retained resolution requests a new sharp crop");
        }
        finally { view.CancelAll(); host.Close(); }
    }

    static async Task TestMuPdfViewportAsync(string source)
    {
        Check(Math.Abs(XTPdfMergeApp.Controls.ZoomRenderPrediction.Headroom(1, 2, 2.6, 200, 2_000_000) - Math.Exp(.52)) < .001,
            "Zoom prediction leads by measured render latency");
        Check(XTPdfMergeApp.Controls.ZoomRenderPrediction.Headroom(1, 1.08, 2.6, 200, 2_000_000) <= 1.18,
            "Short wheel steps do not speculate far beyond target");
        Check(XTPdfMergeApp.Controls.ZoomRenderPrediction.Headroom(1, 10, 2.6, 500, 16_000_000) == 1,
            "Surplus raster is capped by viewport pixel budget");
        Check(XTPdfMergeApp.Controls.ZoomRenderPrediction.Headroom(2, 1, 2.6, 200, 2_000_000) == 1.18,
            "Zoom-out keeps small bounded sharpness headroom");
        Check(ExperimentalMuPdfViewport.CanRender(source, 0, ""), "Enabled only for designated page one");
        Check(!ExperimentalMuPdfViewport.CanRender(source, 1, ""), "Other pages stay on PDFium");
        Check(!ExperimentalMuPdfViewport.CanRender(source, 0, "changed"), "Layer overrides stay on PDFium");
        Check(!ExperimentalMuPdfViewport.CanRender(source + ".other", 0, ""), "Other documents stay on PDFium");
        var rect = new Int32Rect(2040, 1581, 1920, 1080);
        byte[]? baseline = null;
        try
        {
            for (int i = 0; i < 4; i++)
            {
                var watch = Stopwatch.StartNew();
                var batch = await ExperimentalMuPdfViewport.RenderAsync(source, 0, 6000, 4242, new[] { rect }, default);
                double ms = watch.Elapsed.TotalMilliseconds;
                Check(batch.Count == 1 && batch[0] is { IsFrozen: true }, "Worker delivers immutable WPF bitmap");
                Check(batch[0]!.PixelWidth == 1920 && batch[0]!.PixelHeight == 1080, "Viewport dimensions stay exact");
                var pixels = RegionPixels(batch[0]!);
                Check(pixels.Any(value => value < 200), "Drainage viewport is not blank");
                baseline ??= pixels;
                Check(baseline.SequenceEqual(pixels), "Persistent worker replay is stable");
                Console.WriteLine(JsonSerializer.Serialize(new { Engine = "MuPDF bridge", Run = i, TotalMs = ms,
                    ParentPrivateMiB = Process.GetCurrentProcess().PrivateMemorySize64 / 1048576d,
                    WorkerPrivateMiB = ExperimentalMuPdfViewport.WorkerPrivateMiB }));
            }
            using var cancel = new CancellationTokenSource(20);
            bool cancelled = false;
            var uncachedRect = new Int32Rect(rect.X + 17, rect.Y, rect.Width, rect.Height);
            try { await ExperimentalMuPdfViewport.RenderAsync(source, 0, 6000, 4242, new[] { uncachedRect }, cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Cancelled in-flight request does not publish obsolete image");
            var recovered = await ExperimentalMuPdfViewport.RenderAsync(source, 0, 6000, 4242, new[] { rect }, default);
            Check(RegionPixels(recovered[0]!).SequenceEqual(baseline!), "Cancellation drains protocol before next request");
            bool failed = false;
            try { await ExperimentalMuPdfViewport.RenderAsync(source + ".missing", 0, 6000, 4242, new[] { rect }, default); }
            catch (InvalidOperationException) { failed = true; }
            Check(failed && ExperimentalMuPdfViewport.WorkerPrivateMiB == 0, "Worker errors dispose the process for clean fallback");
            Check(!ExperimentalMuPdfViewport.CanRender(source, 0, ""), "Failed experimental backend stays disabled for this process");
            ExperimentalMuPdfViewport.Shutdown();
            var restarted = await ExperimentalMuPdfViewport.RenderAsync(source, 0, 6000, 4242, new[] { rect }, default);
            Check(RegionPixels(restarted[0]!).SequenceEqual(baseline!), "Worker restarts without changing pixels");
        }
        finally { ExperimentalMuPdfViewport.Shutdown(); }
    }
}
