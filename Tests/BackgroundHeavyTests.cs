using System.Diagnostics;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XTPdfMergeApp;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Domain;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    // Exercise the real viewer without activating a window, changing recents,
    // restoring annotations, or sending input to the user's desktop.
    static void ProfileHeavyFilesInBackground(string label, int prefetch, string? sourceManifest = null, string? tuningPath = null,
        bool coldDeepProbe = false)
    {
        using var process = Process.GetCurrentProcess();
        process.PriorityClass = ProcessPriorityClass.BelowNormal;
        var sources = sourceManifest == null
            ? RecentFilesStore.Items.Where(r => r.Size >= 100L * 1024 * 1024 && File.Exists(r.Path)).Take(2).ToArray()
            : JsonSerializer.Deserialize<RecentFile[]>(File.ReadAllText(sourceManifest)) ?? throw new InvalidDataException("Empty source manifest.");
        if (sources.Length == 0) throw new InvalidOperationException("No recent PDFs larger than 100 MB are available.");
        if (int.TryParse(Environment.GetEnvironmentVariable("XTPDF_PDFIUM_INSTANCES"), out int expected) && PdfiumPool.Count != expected)
            throw new InvalidOperationException($"PDFium fallback: requested {expected}, loaded {PdfiumPool.Count}.");
        var stamps = sources.Select(r => (r.Path, new FileInfo(r.Path).Length, File.GetLastWriteTimeUtc(r.Path))).ToArray();
        var app = CreateReaderTestApplication();
        AppSettings.ApplyRuntime();
        var tuning = tuningPath == null ? new ReaderTuningOptions(WarmFiles: PdfThumbnailService.WarmFiles)
            : JsonSerializer.Deserialize<ReaderTuningOptions>(File.ReadAllText(tuningPath)) ?? throw new InvalidDataException("Empty tuning options.");
        tuning.Validate();
        PdfThumbnailService.NativePageCacheCapacity = tuning.NativeCachePages;
        PdfThumbnailService.WarmFiles = tuning.WarmFiles;
        PdfiumPool.CachedPagePenalty = tuning.CachedPagePenalty;
        using var parseAudit = new NativeParseAudit();
        PdfThumbnailService.StartMemoryPolicy();
        var cache = new ReaderPageRenderCache(tuning.ReaderCacheMiB * 1024L * 1024,
            (key, priority, token) => PdfThumbnailService.RenderPageAsync(key.Path, key.Page - 1, key.Width, token, priority, key.Layers),
            tuning.PreviewCacheMiB * 1024L * 1024)
            { ReuseLargerImages = tuning.ReuseRenderedImages };
        var view = new ContinuousPdfView { PrefetchPageCount = prefetch, KeepPrefetchedNativePages = tuning.KeepPrefetchedNativePages,
            PreferViewportRegions = tuning.PreferViewportRegions, ReuseRenderedImages = tuning.ReuseRenderedImages };
        view.CachedPageProvider = (row, width) => cache.TryGetDisplayImage(RenderCacheKeys.ReaderPage(row.SourcePath, row.PageNumber, width));
        int visibleRequests = 0, backgroundRequests = 0, cancelledRequests = 0;
        view.PageRenderer = async (row, width, priority, token) =>
        {
            if (priority == PdfRenderPriority.Visible) Interlocked.Increment(ref visibleRequests);
            else Interlocked.Increment(ref backgroundRequests);
            try { return await cache.GetAsync(RenderCacheKeys.ReaderPage(row.SourcePath, row.PageNumber, width), priority, token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelledRequests); throw; }
        };
        view.UserInteraction += PdfThumbnailService.NoteInteraction;
        view.CurrentPageChanged += index =>
        {
            if (index >= 0 && index < view.Pages.Count) SetHot(view.Pages[index]);
        };
        var host = new Window
        {
            Title = "Background PDF benchmark", Content = view, Width = 1280, Height = 800,
            Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize
        };
        app.MainWindow = host;
        var frame = new DispatcherFrame();
        Exception? failure = null;
        host.Show();
        host.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            var delays = new List<double>();
            bool active = false;
            long previous = Stopwatch.GetTimestamp(), peak = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                long now = Stopwatch.GetTimestamp();
                if (active) delays.Add(Math.Max(0, Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds - 16));
                previous = now;
                process.Refresh();
                peak = Math.Max(peak, process.PrivateMemorySize64);
            };
            timer.Start();
            try
            {
                var cpu = process.TotalProcessorTime;
                var opening = Stopwatch.StartNew();
                var docs = new List<PagePlacement[]>();
                foreach (var source in sources)
                {
                    var sizes = await PdfThumbnailService.GetPageSizesAsync(source.Path) ?? throw new InvalidOperationException("Cannot read page sizes.");
                    double maxWidth = sizes.Max(s => s.Width);
                    docs.Add(sizes.Select((s, i) => new PagePlacement
                    {
                        SourcePath = source.Path, PageNumber = i + 1, PageWidthPoints = s.Width, PageHeightPoints = s.Height,
                        BaseWidth = PagePlacement.DefaultLayoutWidth * s.Width / maxWidth, AspectRatio = s.Height / s.Width
                    }).ToArray());
                }
                // Normalize network availability before measuring interaction. This does not
                // flush OS caches or pretend that the network reads are cold.
                var bufferWait = Stopwatch.StartNew();
                while (sources.Any(s => PdfFileBuffer.GetLoadedFraction(s.Path) is < 1) && bufferWait.Elapsed < TimeSpan.FromSeconds(30))
                    await Task.Delay(50);
                bool buffersReady = sources.All(s => PdfFileBuffer.GetLoadedFraction(s.Path) is >= 1);
                double openMs = opening.Elapsed.TotalMilliseconds;
                parseAudit.SetPhase("active");
                var activeBefore = ReaderTimingSnapshot.Read();
                active = true;
                var scenario = Stopwatch.StartNew();
                double readinessMs = 0;
                bool allReady = true;
                double dpi = VisualTreeHelper.GetDpi(view).DpiScaleX;
                var measurements = new List<object>();
                for (int d = 0; d < docs.Count; d++)
                {
                    var pages = docs[d];
                    SetHot(pages[0]);
                    double zoom = (view.ViewportWidth - 24) / PagePlacement.DefaultLayoutWidth;
                    view.SetDocument(pages, zoom);
                    foreach (int page in new[] { 0, 1, 5, 10, pages.Length / 2 }.Select(p => Math.Min(p, pages.Length - 1)).Distinct())
                    {
                        SetHot(pages[page]);
                        var watch = Stopwatch.StartNew();
                        view.ScrollToPage(page);
                        bool ready = await WaitForPage(pages[page]);
                        readinessMs += watch.Elapsed.TotalMilliseconds;
                        allReady &= ready;
                        process.Refresh();
                        measurements.Add(new { File = d + 1, Page = page + 1, Ready = ready, ReadyMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1), PrivateMB = MB(process.PrivateMemorySize64) });
                        Console.WriteLine($"{label}: file {d + 1}, page {page + 1}: {watch.ElapsedMilliseconds} ms, ready={ready}, private={MB(process.PrivateMemorySize64)} MB");
                        await Task.Delay(300);
                    }
                    int start = Math.Min(20, pages.Length - 1);
                    view.ScrollToPage(start);
                    allReady &= await WaitForPage(pages[start]);
                    var scrolling = Stopwatch.StartNew();
                    int scrollTicks = 0, notReadyTicks = 0;
                    for (int step = 0; step < 100; step++)
                    {
                        view.ScrollBy(0, 65);
                        await Task.Delay(16);
                        scrollTicks++;
                        if (view.VisiblePages().Any(row => !HasRenderedPage(view, row))) notReadyTicks++;
                    }
                    double scrollMs = scrolling.Elapsed.TotalMilliseconds;
                    var stop = Stopwatch.StartNew();
                    int current = Math.Clamp(view.CurrentPage, 0, pages.Length - 1);
                    bool settled = await WaitForPage(pages[current]);
                    readinessMs += stop.Elapsed.TotalMilliseconds;
                    allReady &= settled;
                    measurements.Add(new { File = d + 1, ScrollTicks = scrollTicks, ScrollDistanceDip = 6500, NotReadyTicks = notReadyTicks, ScrollMs = Math.Round(scrollMs, 1), FinalPage = current + 1, Settled = settled, SettleMs = Math.Round(stop.Elapsed.TotalMilliseconds, 1) });
                    var zoomPage = pages[Math.Min(10, pages.Length - 1)];
                    view.ScrollToPage(Math.Min(10, pages.Length - 1));
                    allReady &= await WaitForPage(zoomPage);
                    var zoomWatch = Stopwatch.StartNew();
                    view.ZoomKeepTop(1.0);
                    int requiredWidth = (int)Math.Clamp(Math.Ceiling(zoomPage.LayoutWidth * dpi / 256) * 256, 512, 2304);
                    bool zoomReady = await WaitForPage(zoomPage, requiredWidth);
                    readinessMs += zoomWatch.Elapsed.TotalMilliseconds;
                    allReady &= zoomReady;
                    double zoomMs = zoomWatch.Elapsed.TotalMilliseconds;
                    string? pixelHash = zoomReady ? HashBitmap(zoomPage) : null;
                    process.Refresh();
                    measurements.Add(new { File = d + 1, Zoom = 1.0, Page = zoomPage.PageNumber, ZoomReady = zoomReady, ZoomMs = Math.Round(zoomMs, 1), PixelWidth = zoomPage.ReaderBitmap?.PixelWidth, PixelHash = pixelHash, PrivateMB = MB(process.PrivateMemorySize64) });
                    for (int cycle = 0; cycle < tuning.ZoomCycles; cycle++)
                    {
                        foreach (double level in new[] { 1.25, 1.0 })
                        {
                            var cycleWatch = Stopwatch.StartNew();
                            view.ZoomKeepTop(level);
                            int cycleWidth = (int)Math.Clamp(Math.Ceiling(zoomPage.LayoutWidth * level * dpi / 256) * 256, 512, 2304);
                            bool cycleReady = await WaitForPage(zoomPage, cycleWidth);
                            allReady &= cycleReady;
                            readinessMs += cycleWatch.Elapsed.TotalMilliseconds;
                            measurements.Add(new { File = d + 1, ZoomCycle = cycle + 1, ZoomLevel = level,
                                CycleReady = cycleReady, CycleMs = Math.Round(cycleWatch.Elapsed.TotalMilliseconds, 1) });
                            await Task.Delay(100);
                        }
                    }
                    if (tuning.DeepZoom)
                    {
                        var deep = Stopwatch.StartNew();
                        view.ZoomKeepTop(3.0);
                        bool needsRegion = zoomPage.LayoutWidth * view.Zoom * dpi > 2304 * 1.03;
                        int deepWidth = (int)Math.Clamp(Math.Ceiling(zoomPage.LayoutWidth * view.Zoom * dpi / 256) * 256, 512, 2304);
                        bool deepReady = needsRegion ? await WaitForRegion(zoomPage) : await WaitForPage(zoomPage, deepWidth);
                        allReady &= deepReady;
                        readinessMs += deep.Elapsed.TotalMilliseconds;
                        TryRenderedRegion(view, zoomPage, out var region);
                        if (!needsRegion) region = zoomPage.ReaderBitmap;
                        measurements.Add(new { File = d + 1, DeepZoom = 3.0, DeepReady = deepReady,
                            DeepMs = Math.Round(deep.Elapsed.TotalMilliseconds, 1),
                            RegionWidth = region?.PixelWidth, RegionHeight = region?.PixelHeight,
                            RegionHash = region == null ? null : HashPixels(region) });
                        var pan = Stopwatch.StartNew();
                        view.ScrollBy(600, 400);
                        await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                        bool panReady = needsRegion ? await WaitForRegion(zoomPage) : await WaitForPage(zoomPage, deepWidth);
                        allReady &= panReady;
                        readinessMs += pan.Elapsed.TotalMilliseconds;
                        TryRenderedRegion(view, zoomPage, out var movedRegion);
                        if (!needsRegion) movedRegion = zoomPage.ReaderBitmap;
                        measurements.Add(new { File = d + 1, Pan = true, PanReady = panReady,
                            PanMs = Math.Round(pan.Elapsed.TotalMilliseconds, 1),
                            PanHash = movedRegion == null ? null : HashPixels(movedRegion) });
                    }
                    view.ZoomKeepTop(zoom);
                    await Task.Delay(300);
                }
                // Return to the first document and sample cache reuse, then normal idle reclamation.
                SetHot(docs[0][0]);
                var back = Stopwatch.StartNew();
                view.SetDocument(docs[0], (view.ViewportWidth - 24) / PagePlacement.DefaultLayoutWidth);
                bool returned = await WaitForPage(docs[0][0]);
                double returnMs = back.Elapsed.TotalMilliseconds;
                readinessMs += returnMs;
                allReady &= returned;
                await Task.Delay(1000);
                double scenarioSeconds = scenario.Elapsed.TotalSeconds;
                var activeAfter = ReaderTimingSnapshot.Read();
                var activeRequests = new { Visible = visibleRequests, Background = backgroundRequests, Cancelled = cancelledRequests };
                var activeParses = parseAudit.Snapshot("active");
                parseAudit.SetPhase("idle");
                active = false;
                process.Refresh();
                double beforeIdle = MB(process.PrivateMemorySize64);
                int docsBeforeIdle = PdfThumbnailService.CachedDocumentCount;
                await Task.Delay(25000);
                process.Refresh();
                double afterIdle = MB(process.PrivateMemorySize64);
                int docsAfterIdle = PdfThumbnailService.CachedDocumentCount;
                long peakBeforeResume = peak;
                parseAudit.SetPhase("resume");
                active = true;
                var resumeWatch = Stopwatch.StartNew();
                var resumePages = docs[^1];
                var resumePage = resumePages[Math.Min(60, resumePages.Length - 1)];
                SetHot(resumePage);
                view.SetDocument(resumePages, 1.0);
                view.ScrollToPage(Math.Min(60, resumePages.Length - 1));
                bool resumeReady = await WaitForPage(resumePage, (int)Math.Clamp(Math.Ceiling(resumePage.LayoutWidth * dpi / 256) * 256, 512, 2304));
                allReady &= resumeReady;
                double resumeMs = resumeWatch.Elapsed.TotalMilliseconds;
                active = false;
                if (coldDeepProbe)
                {
                    var coldPage = resumePages[Math.Min(80, resumePages.Length - 1)];
                    SetHot(coldPage);
                    var cold = Stopwatch.StartNew();
                    view.SetDocument(resumePages, 3.0);
                    view.ScrollToPage(Math.Min(80, resumePages.Length - 1));
                    view.TryGetPageRect(coldPage, out var coldRect);
                    Console.WriteLine($"COLD page={coldPage.PageNumber}, rect={coldRect}, viewport={view.ViewportWidth:0.0}x{view.ViewportHeight:0.0}, layoutWidth={coldPage.LayoutWidth:0.0}");
                    bool needsRegion = coldPage.LayoutWidth * view.Zoom * dpi > 2304 * 1.03;
                    int width = (int)Math.Clamp(Math.Ceiling(coldPage.LayoutWidth * view.Zoom * dpi / 256) * 256, 512, 2304);
                    bool ready = needsRegion ? await WaitForRegion(coldPage) : await WaitForPage(coldPage, width);
                    allReady &= ready;
                    TryRenderedRegion(view, coldPage, out var region);
                    measurements.Add(new { ColdDeepPage = coldPage.PageNumber, ColdDeepReady = ready,
                        ColdDeepMs = Math.Round(cold.Elapsed.TotalMilliseconds, 1),
                        FallbackWidth = coldPage.ReaderBitmap?.PixelWidth,
                        SharpHash = region == null ? null : HashPixels(region) });
                }
                var ordered = delays.Order().ToArray();
                var result = new
                {
                    Schema = 3, Label = label, PdfiumInstances = PdfiumPool.Count, Prefetch = prefetch, Tuning = tuning,
                    BuffersReady = buffersReady, OpenMs = Math.Round(openMs, 1), Dpi = dpi,
                    ScenarioSeconds = Math.Round(scenarioSeconds, 3), TotalReadinessMs = Math.Round(readinessMs, 1), AllReady = allReady,
                    Files = sources.Select((s, i) => new { Name = Path.GetFileName(s.Path), SizeMB = MB(s.Size), Pages = docs[i].Length }),
                    PeakPrivateMB = MB(peakBeforeResume), OverallPeakPrivateMB = MB(peak), BeforeIdlePrivateMB = beforeIdle, AfterIdlePrivateMB = afterIdle,
                    WorkingSetMB = MB(process.WorkingSet64), ManagedMB = MB(GC.GetTotalMemory(false)), ReaderCacheMB = MB(cache.Stats.Bytes),
                    RegionCacheMB = MB(ContinuousPdfView.CachedRegionStats.Bytes), RegionCacheEntries = ContinuousPdfView.CachedRegionStats.Count,
                    NativeDocumentsBeforeIdle = docsBeforeIdle, NativeDocumentsAfterIdle = docsAfterIdle,
                    ResumeReady = resumeReady, ResumeMs = Math.Round(resumeMs, 1),
                    CpuSeconds = Math.Round((process.TotalProcessorTime - cpu).TotalSeconds, 2),
                    DispatcherDelayP95Ms = Math.Round(ordered[(int)((ordered.Length - 1) * .95)], 1), DispatcherDelayMaxMs = Math.Round(ordered[^1], 1),
                    ReturnReady = returned, ReturnMs = Math.Round(returnMs, 1), Measurements = measurements,
                    ActiveDiagnostics = activeAfter.Since(activeBefore), ActiveParses = activeParses,
                    ActiveRequests = activeRequests,
                    ResumeParses = parseAudit.Snapshot("resume"),
                    VisibleRequests = visibleRequests, BackgroundRequests = backgroundRequests, CancelledRequests = cancelledRequests,
                    Diagnostics = RenderDiagnostics.Summary,
                    SourcesUnchanged = stamps.All(s => new FileInfo(s.Path).Length == s.Length && File.GetLastWriteTimeUtc(s.Path) == s.Item3)
                };
                string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(Output, "heavy-background-" + label + ".json"), json);
                Console.WriteLine($"RESULT {label}: instances={PdfiumPool.Count}, scenario={scenarioSeconds:0.000}s, waits={readinessMs:0.0}ms, peak={MB(peakBeforeResume)}MB, idle={afterIdle}MB, resume={resumeMs:0.0}ms, ready={allReady}");
                if (!allReady || !buffersReady || !result.SourcesUnchanged) throw new InvalidOperationException("The profile did not finish all pages or its source data changed.");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                timer.Stop();
                view.SetDocument(null, 1);
                cache.Clear();
                ContinuousPdfView.ReleaseUnusedRegionSources(new HashSet<string>());
                PdfThumbnailService.SetHotPages(Array.Empty<(string, int)>());
                host.Close();
                PdfThumbnailService.PrepareForShutdown(TimeSpan.FromSeconds(10));
                frame.Continue = false;
            }

            async Task<bool> WaitForPage(PagePlacement row, int width = 0)
            {
                var timeout = Stopwatch.StartNew();
                while (!Ready() && timeout.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(16);
                return Ready();

                bool Ready() => HasRenderedPage(view, row) && (width == 0 || row.ReaderBitmap?.PixelWidth >= width);
            }
            async Task<bool> WaitForRegion(PagePlacement row)
            {
                var timeout = Stopwatch.StartNew();
                while (!TryRenderedRegion(view, row, out _) && timeout.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(16);
                return TryRenderedRegion(view, row, out _);
            }
        }));
        Dispatcher.PushFrame(frame);
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

        static void SetHot(PagePlacement row) => PdfThumbnailService.SetHotPages(new[] { (row.SourcePath, row.PageNumber) });
        static double MB(long bytes) => Math.Round(bytes / 1048576d, 1);

        static string HashBitmap(PagePlacement row)
            => HashPixels(row.ReaderBitmap!);

        static string HashPixels(BitmapSource bitmap)
        {
            int stride = (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
            int length = stride * bitmap.PixelHeight;
            byte[] pixels = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                bitmap.CopyPixels(pixels, stride, 0);
                return Convert.ToHexString(SHA256.HashData(pixels.AsSpan(0, length)));
            }
            finally { ArrayPool<byte>.Shared.Return(pixels); }
        }
    }
}
