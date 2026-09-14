using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Point = System.Windows.Point;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XTPdfMergeApp.Domain;
using XTPdfMergeApp.Services;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;

internal static class Program
{
    static readonly string Output = System.IO.Path.Combine(AppContext.BaseDirectory, "results");
    static int _checks;
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            Directory.CreateDirectory(Output);
            // NuGet places PDFium in a runtime-specific directory.
            var dll = Directory.GetFiles(AppContext.BaseDirectory, "pdfium.dll", SearchOption.AllDirectories)
                .First(path => path.Contains("win-x64", StringComparison.OrdinalIgnoreCase));
            foreach (var assembly in new[] { typeof(PdfThumbnailService).Assembly, typeof(Program).Assembly })
                NativeLibrary.SetDllImportResolver(assembly, (name, _, _) => name == "pdfium" ? NativeLibrary.Load(dll) : IntPtr.Zero);
            int viewportArg = Array.IndexOf(args, "--viewport-pdf");
            if (viewportArg >= 0)
            {
                string source = System.IO.Path.GetFullPath(args[viewportArg + 1]);
                int pageArg = Array.IndexOf(args, "--page");
                int page = pageArg >= 0 ? int.Parse(args[pageArg + 1]) - 1 : 0;
                CompareViewportRasterAsync(source, page).GetAwaiter().GetResult();
                Console.WriteLine(RenderDiagnostics.Summary);
                return 0;
            }
            bool baseline = args.Contains("--baseline");
            if (!baseline)
            {
                TestCacheAndOwnership(); TestBulkPages(); TestPresentationQueue(); TestViewportScheduling(); TestRetainedRefinement(); TestViewportMotion(); TestReaderZoomMath();
                TestGateAsync().GetAwaiter().GetResult();
            }
            RunNativeAsync(baseline).GetAwaiter().GetResult();
            Console.WriteLine($"PASS ({_checks} checks) {(baseline ? "baseline" : "optimized")}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }

    static BitmapSource Bitmap(int width = 100, int height = 100)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    static void TestCacheAndOwnership()
    {
        var cache = new BitmapMemoryCache<int>(80_000);
        cache.Set(1, Bitmap()); cache.Set(2, Bitmap());
        cache.TryGetValue(1, out _); cache.Set(3, Bitmap());
        Check(cache.ContainsKey(1) && !cache.ContainsKey(2) && cache.Bytes == 80_000, "Byte budget/LRU");
        cache.Set(1, Bitmap(50, 50));
        Check(cache.Bytes == 50_000, "Replacement byte accounting");
        cache.Clear();
        cache.Set(1, Bitmap(), _ => true); cache.Set(2, Bitmap(), _ => true); cache.Set(3, Bitmap(), _ => true);
        Check(cache.Count == 3, "Visible tiles survive temporary budget overflow");
        cache.Trim(k => k == 3);
        Check(!cache.ContainsKey(1) && cache.ContainsKey(3) && cache.Bytes <= cache.BudgetBytes, "Previously pinned tiles become reclaimable");
        var row = new PagePlacement { SourcePath = "fixture.pdf", PageNumber = 1 };
        var weak = AssignTemporaryBitmap(row);
        Collect();
        Check(!weak.TryGetTarget(out _) && row.ReaderBitmap == null && row.Thumbnail == null, "Undo placement must not own bitmaps");
        Check(row.HasLoadedThumbnailOnce, "Progress survives eviction");
        var image = new Image();
        image.SetBinding(Image.SourceProperty, new Binding(nameof(PagePlacement.ReaderDisplayBitmap)) { Source = row });
        AssignTemporaryBitmap(row);
        Collect();
        Check(image.Source != null && row.ReaderBitmap != null, "Visible WPF binding keeps image alive after cache eviction");
        GC.KeepAlive(image);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference<BitmapSource> AssignTemporaryBitmap(PagePlacement row)
    {
        var bitmap = Bitmap(); row.ReaderBitmap = bitmap; row.Thumbnail = bitmap;
        return new WeakReference<BitmapSource>(bitmap);
    }
    static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    static void TestBulkPages()
    {
        var document = new WorkspaceDocument { SourcePath = "large.pdf" };
        int resets = 0; document.Pages.CollectionChanged += (_, _) => resets++;
        var sw = Stopwatch.StartNew();
        document.Pages.AddRange(Enumerable.Range(1, 10000).Select(p => new PagePlacement { SourcePath = document.SourcePath, PageNumber = p }));
        sw.Stop();
        Check(resets == 1 && document.Pages[9999].Index == 10000, "Bulk pages send one collection notification and assign indices");
        document.Pages[0].Thumbnail = Bitmap(); document.Pages[0].Thumbnail = null;
        Check(document.LoadedThumbnailCount == 1, "Loaded thumbnail count survives release");
        document.Pages.RemoveAt(0);
        Check(document.LoadedThumbnailCount == 0 && document.Pages[0].Index == 1, "Removal refreshes indices and progress");
        Check(!document.IsThumbnailLoading, "Lazy thumbnails do not leave a perpetual loading spinner");
        Console.WriteLine($"10,000 page rows, one bulk notification: {sw.Elapsed.TotalMilliseconds:F1} ms");
    }

    static void TestPresentationQueue()
    {
        using var queue = new FramePresentationQueue(ex => throw ex);
        int displayed = 0;
        var jobs = Enumerable.Range(0, 12).Select(_ => queue.Enqueue(() => displayed++, default)).ToArray();
        queue.DrainFrame();
        Check(displayed > 0 && displayed <= 4 && displayed < 12, "Tile installation is bounded per frame");
        while (queue.PendingCount > 0) queue.DrainFrame();
        Check(displayed == 12 && jobs.All(t => t.IsCompletedSuccessfully), "Queued presentation completes");
        using var cancel = new CancellationTokenSource();
        var stale = queue.Enqueue(() => displayed++, cancel.Token);
        cancel.Cancel(); queue.DrainFrame();
        Check(displayed == 12 && stale.IsCompletedSuccessfully, "Obsolete frame work is skipped");
        var hidden = queue.Enqueue(() => displayed++, default);
        queue.Clear();
        Check(hidden.IsCompletedSuccessfully && queue.PendingCount == 0 && displayed == 12,
            "Hiding reader releases pending presentation closures");
    }

    static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    static void TestViewportScheduling()
    {
        int updates = 0, concurrent = 0, peak = 0;
        using var scheduler = new ViewportRenderScheduler(Dispatcher.CurrentDispatcher, async () =>
        {
            updates++; concurrent++; peak = Math.Max(peak, concurrent);
            await Task.Delay(25); concurrent--;
        });
        var pan = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(5) };
        pan.Tick += (_, _) => scheduler.Request(false);
        pan.Start(); Pump(TimeSpan.FromMilliseconds(220)); pan.Stop();
        Check(updates >= 2, "Continued pan does not starve rendering until mouse release");
        Pump(TimeSpan.FromMilliseconds(80));
        Check(peak == 1 && concurrent == 0, "Viewport updates never overlap");
        int before = updates;
        scheduler.Request(true); Pump(TimeSpan.FromMilliseconds(40));
        scheduler.Request(true); Pump(TimeSpan.FromMilliseconds(40));
        Check(updates == before, "Resolution changes debounce while preserving old image");
        Pump(TimeSpan.FromMilliseconds(160));
        Check(updates == before + 1, "Settled zoom renders once");
        scheduler.Request(false); scheduler.Cancel(); Pump(TimeSpan.FromMilliseconds(40));
        Check(updates == before + 1, "Canceled viewport timer does not revive hidden reader");
    }

    static async Task TestGateAsync()
    {
        var gate = new PdfRenderGate();
        await gate.WaitAsync(PdfRenderPriority.Visible);
        var background = gate.WaitAsync(PdfRenderPriority.Background);
        var visible = gate.WaitAsync(PdfRenderPriority.Visible);
        using var cancel = new CancellationTokenSource();
        var cancelled = gate.WaitAsync(PdfRenderPriority.Visible, cancel.Token);
        cancel.Cancel();
        try { await cancelled; throw new Exception("Canceled waiter completed"); } catch (OperationCanceledException) { _checks++; }
        gate.Release(); await visible.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!background.IsCompleted, "Visible job precedes earlier background job");
        gate.Release(); await background.WaitAsync(TimeSpan.FromSeconds(2)); gate.Release();
        // Simulate a scrollbar jump past hundreds of obsolete visible requests.
        await gate.WaitAsync(PdfRenderPriority.Visible);
        using var oldViewport = new CancellationTokenSource();
        var obsolete = Enumerable.Range(0, 499)
            .Select(_ => gate.WaitAsync(PdfRenderPriority.Visible, oldViewport.Token)).ToArray();
        oldViewport.Cancel();
        var destination = gate.WaitAsync(PdfRenderPriority.Visible);
        gate.Release();
        await destination.WaitAsync(TimeSpan.FromSeconds(2));
        try { await Task.WhenAll(obsolete).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { }
        Check(obsolete.All(t => t.IsCanceled), "Jump drops all 499 obsolete visible requests before destination enters");
        gate.Release();
        int active = 0, peak = 0;
        await Task.WhenAll(Enumerable.Range(0, 100).Select(async i =>
        {
            await gate.WaitAsync((PdfRenderPriority)(i % 3));
            int count = Interlocked.Increment(ref active); peak = Math.Max(peak, count);
            await Task.Yield(); Interlocked.Decrement(ref active); gate.Release();
        }));
        Check(peak == 1, "Concurrent requests preserve native serialization");
        var buffers = new PdfRenderGate(2);
        await buffers.WaitAsync(PdfRenderPriority.Visible); await buffers.WaitAsync(PdfRenderPriority.Visible);
        var third = buffers.WaitAsync(PdfRenderPriority.Visible);
        Check(!third.IsCompleted, "Only two progressive bitmap buffers can be active");
        buffers.Release(); await third; buffers.Release(); buffers.Release();
    }

    static string CreateFixture()
    {
        string path = System.IO.Path.Combine(Output, "fixture.pdf");
        using var pdf = new PdfDocument(new PdfWriter(path));
        for (int i = 0; i < 12; i++)
        {
            var page = pdf.AddNewPage(i % 2 == 0 ? PageSize.A4 : PageSize.A4.Rotate());
            if (i == 2) page.SetRotation(90);
            if (i == 3) page.SetCropBox(new Rectangle(20, 30, 500, 400));
            var canvas = new PdfCanvas(page);
            canvas.SetFillColorRgb(0.2f, 0.5f, 0.8f).Rectangle(30, 40, 150, 170).Fill();
            canvas.BeginText().SetFontAndSize(iText.Kernel.Font.PdfFontFactory.CreateFont(), 18)
                .MoveText(40, 260).ShowText($"PDFium regression page {i + 1}").EndText();
            canvas.SetLineWidth(0.3f);
            for (int j = 0; j < 1000; j++) canvas.MoveTo(10 + j % 500, 10).LineTo(j % 500, 550).Stroke();
        }
        return path;
    }

    static string Hash(BitmapSource bitmap)
    {
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    static async Task CompareViewportRasterAsync(string path, int page = 0)
    {
        // Warm, identical center crop containing vector paths in the fixture.
        double aspect = await PdfThumbnailService.GetPageAspectRatioAsync(path, page)
            ?? throw new Exception("Cannot read page geometry");
        const int fullWidth = 6400;
        int fullHeight = Math.Max(1, (int)Math.Round(fullWidth * aspect));
        int cropWidth = 1920, cropHeight = Math.Min(1280, fullHeight);
        int originX = (fullWidth - cropWidth) / 2, originY = (fullHeight - cropHeight) / 2;
        var reference = await PdfThumbnailService.RenderPageTileAsync(path, page, fullWidth, fullHeight,
            new Int32Rect(originX, originY, cropWidth, cropHeight));
        Check(reference != null, "Reference viewport is available");
        var measurements = new List<object>();
        foreach (int size in new[] { 640, 1280, 1920 })
        {
            var timings = new List<double>();
            var firstTimings = new List<double>();
            var stitchedPixels = new byte[cropWidth * cropHeight * 4];
            for (int pass = 0; pass < 4; pass++)
            {
                var outputs = new List<(Int32Rect Rect, BitmapSource Bitmap)>();
                var sw = Stopwatch.StartNew();
                double firstMs = 0;
                for (int y = 0; y < cropHeight; y += size)
                    for (int x = 0; x < cropWidth; x += size)
                    {
                        var local = new Int32Rect(x, y, Math.Min(size, cropWidth - x), Math.Min(size, cropHeight - y));
                        var bitmap = await PdfThumbnailService.RenderPageTileAsync(path, page, fullWidth, fullHeight,
                            new Int32Rect(x + originX, y + originY, local.Width, local.Height));
                        Check(bitmap != null, "Viewport subdivision render succeeds");
                        if (outputs.Count == 0) firstMs = sw.Elapsed.TotalMilliseconds;
                        outputs.Add((local, bitmap!));
                    }
                sw.Stop();
                if (pass > 0) { timings.Add(sw.Elapsed.TotalMilliseconds); firstTimings.Add(firstMs); }
                if (pass == 3)
                    foreach (var (rect, bitmap) in outputs)
                    {
                        var pixels = new byte[rect.Width * rect.Height * 4];
                        bitmap.CopyPixels(pixels, rect.Width * 4, 0);
                        for (int row = 0; row < rect.Height; row++)
                            Buffer.BlockCopy(pixels, row * rect.Width * 4, stitchedPixels,
                                ((rect.Y + row) * cropWidth + rect.X) * 4, rect.Width * 4);
                    }
            }
            var stitched = BitmapSource.Create(cropWidth, cropHeight, 96, 96, PixelFormats.Bgra32, null,
                stitchedPixels, cropWidth * 4);
            stitched.Freeze();
            var referencePixels = new byte[stitchedPixels.Length];
            reference!.CopyPixels(referencePixels, cropWidth * 4, 0);
            long totalError = 0; int maxError = 0, differentPixels = 0;
            for (int i = 0; i < stitchedPixels.Length; i += 4)
            {
                bool different = false;
                for (int c = 0; c < 4; c++)
                {
                    int error = Math.Abs(stitchedPixels[i + c] - referencePixels[i + c]);
                    totalError += error; maxError = Math.Max(maxError, error); different |= error != 0;
                }
                if (different) differentPixels++;
            }
            // Different clip origins can change PDFium antialiasing. Measure that
            // difference instead of assuming different subdivisions are pixel-identical.
            if (size == 1920) Check(Hash(stitched) == Hash(reference), "Identical viewport request is pixel-stable");
            timings.Sort(); firstTimings.Sort();
            measurements.Add(new { TileSize = size, MedianMilliseconds = timings[1], FirstRegionMilliseconds = firstTimings[1],
                DifferentPixels = differentPixels, MaxChannelDifference = maxError,
                MeanChannelDifference = totalError / (double)stitchedPixels.Length });
        }
        File.WriteAllText(System.IO.Path.Combine(Output, "viewport-raster.json"), JsonSerializer.Serialize(measurements));
        Console.WriteLine("Viewport raster: " + JsonSerializer.Serialize(measurements));
    }

    static void TestViewportMotion()
    {
        var tracker = new ViewportMotionTracker();
        var viewport = new Size(1000, 800);
        Check(!tracker.Update(new Point(0, 0), viewport, false), "First viewport does not cancel itself");
        Check(!tracker.Update(new Point(0, 100), viewport, false), "Small pan preserves in-progress tiles");
        Check(tracker.Update(new Point(0, 300), viewport, false), "Accumulated same-page pan invalidates obsolete work");
        Check(!tracker.Update(new Point(0, 310), viewport, false), "Movement anchor resets after invalidation");
        Check(tracker.Update(new Point(1000, 310), viewport, false), "Horizontal pan also invalidates");
        Check(!tracker.Update(new Point(5000, 5000), viewport, true), "Zoom establishes a new movement anchor");
    }

    static void TestReaderZoomMath()
    {
        const double step = 1.08;
        Check(Math.Abs(ReaderZoomMath.WheelZoom(1.0, 120, step, 0.05, 4.0) - step) < 0.0001,
            "One wheel notch applies one zoom step");
        Check(ReaderZoomMath.WheelZoom(1.0, 30, step, 0.05, 4.0) < step,
            "High-resolution wheel deltas zoom fractionally");
        Check(Math.Abs(ReaderZoomMath.WheelZoom(1.0, 30, step, 0.05, 4.0) - Math.Pow(step, 0.25)) < 0.0001,
            "Fractional wheel delta preserves Chromium-style smoothness");
        Check(Math.Abs(ReaderZoomMath.WheelZoom(1.0, -120, step, 0.05, 4.0) - (1.0 / step)) < 0.0001,
            "Negative wheel delta zooms out by one step");
        Check(Math.Abs(ReaderZoomMath.WheelZoom(4.0, 120, step, 0.05, 4.0) - 4.0) < 0.0001,
            "Wheel zoom respects maximum clamp");
    }

    static void TestRetainedRefinement()
    {
        var root = new Canvas { Width = 20, Height = 10 };
        var old = new Canvas { Width = 20, Height = 10 };
        var next = new Canvas { Width = 20, Height = 10, Visibility = Visibility.Collapsed };
        old.Children.Add(new System.Windows.Shapes.Rectangle { Width = 20, Height = 10, Fill = Brushes.Red });
        root.Children.Add(old); root.Children.Add(next);
        RetainedTilePresentation.Begin(old, next);
        next.Children.Add(new System.Windows.Shapes.Rectangle { Width = 10, Height = 10, Fill = Brushes.Blue });
        root.Measure(new Size(20, 10)); root.Arrange(new Rect(0, 0, 20, 10)); root.UpdateLayout();
        var output = new RenderTargetBitmap(20, 10, 96, 96, PixelFormats.Pbgra32);
        output.Render(root);
        var bytes = new byte[20 * 10 * 4]; output.CopyPixels(bytes, 80, 0);
        Check(bytes[(5 * 20 + 5) * 4] == 255, "First refined tile is visible before its neighbors finish");
        Check(bytes[(5 * 20 + 15) * 4 + 2] == 255, "Unfinished region retains previous imagery");
        RetainedTilePresentation.Complete(old);
        Check(old.Children.Count == 0 && old.Visibility == Visibility.Collapsed, "Completed refinement releases old visual references");
        RetainedTilePresentation.Begin(next, old);
        Check(next.Children.Count == 1 && Panel.GetZIndex(old) > Panel.GetZIndex(next), "Repeated zoom keeps the newest completed pixels underneath");
    }

    static async Task RunNativeAsync(bool baseline)
    {
        string path = CreateFixture();
        Func<int, double, Task<BitmapSource?>> render = baseline
            ? (page, width) => Baseline.PdfThumbnailService.RenderPageAsync(path, page, width)
            : (page, width) => PdfThumbnailService.RenderPageAsync(path, page, width);
        var first = await render(0, 512); Check(first != null && first.IsFrozen, "Native render returns frozen image");
        var hashes = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            var bitmap = (await render(i, 1024))!;
            hashes.Add(Hash(bitmap));
            double? aspect = baseline ? await Baseline.PdfThumbnailService.GetPageAspectRatioAsync(path, i)
                : await PdfThumbnailService.GetPageAspectRatioAsync(path, i);
            Check(aspect.HasValue && Math.Abs(aspect.Value - bitmap.PixelHeight / (double)bitmap.PixelWidth) < 0.002,
                "Page geometry matches rendered image (portrait/landscape/rotation/crop)");
        }
        var rects = new[] { new Int32Rect(0, 0, 640, 640), new Int32Rect(640, 0, 384, 640) };
        var tiles = baseline ? await Baseline.PdfThumbnailService.RenderPageTilesBatchAsync(path, 0, 1024, 1449, rects)
            : await PdfThumbnailService.RenderPageTilesBatchAsync(path, 0, 1024, 1449, rects);
        foreach (var tile in tiles) { Check(tile != null, "Batch renders tile"); hashes.Add(Hash(tile!)); }
        string reference = System.IO.Path.Combine(Output, "baseline-hashes.json");
        if (baseline) File.WriteAllText(reference, JsonSerializer.Serialize(hashes));
        else Check(hashes.SequenceEqual(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(reference))!),
            "Pixel-identical page and tile rendering versus original service");
        // Warm up both pipelines before measuring identical 2200px renders; no test pixel copy
        // or hash allocation is inside the measurement interval.
        await render(0, 2200); Collect();
        long allocated = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 12; i++) Check(await render(i, 2200) != null, "Measured render succeeds");
        sw.Stop(); allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        var measurement = new { Mode = baseline ? "baseline" : "optimized", Renders = 12,
            Milliseconds = sw.Elapsed.TotalMilliseconds, ManagedAllocatedBytes = allocated };
        File.WriteAllText(System.IO.Path.Combine(Output, measurement.Mode + ".json"), JsonSerializer.Serialize(measurement, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(measurement));
        if (!baseline)
        {
            var gate = (PdfRenderGate)typeof(PdfThumbnailService).GetField("_pdfiumGate", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            await gate.WaitAsync(PdfRenderPriority.Visible);
            using var cts = new CancellationTokenSource();
            var pending = PdfThumbnailService.RenderPageTilesBatchAsync(path, 0, 1024, 1449, rects, cts.Token);
            cts.Cancel();
            var cancelled = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Check(cancelled.All(b => b == null), "Canceled native batch leaves gate queue without rendering");
            gate.Release();
            Check(await render(0, 512) != null, "Renderer still works after cancellation");
            using var midBatch = new CancellationTokenSource();
            var longBatch = PdfThumbnailService.RenderPageTilesBatchAsync(path, 0, 1024, 1449,
                Enumerable.Repeat(rects[0], 200).ToArray(), midBatch.Token);
            var deadline = Stopwatch.StartNew();
            while (PdfThumbnailService.ActiveNativeCalls == 0 && !longBatch.IsCompleted && deadline.ElapsedMilliseconds < 2000)
                await Task.Delay(1);
            midBatch.Cancel();
            var partial = await longBatch.WaitAsync(TimeSpan.FromSeconds(5));
            Check(partial.Count(b => b != null) < 200, "Active batch stops before rendering all remaining tiles");
            await gate.WaitAsync(PdfRenderPriority.Visible);
            var closing = PdfThumbnailService.RenderPageAsync(path, 0, 512);
            PdfThumbnailService.ReleaseUnusedDocuments(Array.Empty<string>());
            Check(PdfThumbnailService.CachedDocumentCount == 0, "Document removed from cache");
            Check(await closing.WaitAsync(TimeSpan.FromSeconds(2)) == null, "Closing document cancels its queued render");
            gate.Release();
            Check(await render(0, 512) != null, "Reopen after document retirement");
            long loads = PdfThumbnailService.NativePageLoads;
            for (int i = 0; i < 3; i++) await render(0, 512);
            Check(PdfThumbnailService.NativePageLoads == loads, "Repeated zoom/render reuses a loaded native page");
            for (int i = 0; i < 12; i++) await render(i, 256);
            Check(PdfThumbnailService.CachedNativePageCount <= PdfThumbnailService.NativePageCacheCapacity,
                "Native page LRU is bounded after requests complete");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => render(0, 1024)));
            Check(concurrent.All(b => b != null && Hash(b) == hashes[0]),
                "Concurrent requests for one page do not corrupt its progressive state");
            using var insideTile = new CancellationTokenSource();
            long yielded = PdfThumbnailService.ProgressiveYields;
            long cancelledCount = PdfThumbnailService.CancelledRenders;
            var large = PdfThumbnailService.RenderPageAsync(path, 0, 4000, insideTile.Token, PdfRenderPriority.Background);
            var wait = Stopwatch.StartNew();
            while (PdfThumbnailService.ProgressiveYields == yielded && !large.IsCompleted && wait.ElapsedMilliseconds < 5000)
                await Task.Delay(1);
            Check(PdfThumbnailService.ProgressiveYields > yielded, "A complex render yields within a single bitmap");
            var urgent = PdfThumbnailService.RenderPageAsync(path, 1, 64);
            Check(await Task.WhenAny(urgent, large) == urgent && await urgent != null,
                "Visible page can render between slices of background page");
            insideTile.Cancel();
            Check(await large.WaitAsync(TimeSpan.FromSeconds(5)) == null && PdfThumbnailService.CancelledRenders > cancelledCount,
                "Cancellation stops an already started progressive bitmap");
            Console.WriteLine($"Native pages={PdfThumbnailService.CachedNativePageCount}, page loads/hits={PdfThumbnailService.NativePageLoads}/{PdfThumbnailService.NativePageCacheHits}, progressive yields={PdfThumbnailService.ProgressiveYields}, max slice={PdfThumbnailService.MaxNativeRenderSliceMilliseconds:F1} ms");
            await CompareViewportRasterAsync(path);
            PdfThumbnailService.ReleaseCachedPages();
            wait.Restart();
            while (PdfThumbnailService.CachedNativePageCount != 0 && wait.ElapsedMilliseconds < 2000) await Task.Delay(5);
            Check(PdfThumbnailService.CachedNativePageCount == 0, "Hiding reader releases idle native pages");
        }
    }
}
