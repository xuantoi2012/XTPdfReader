using System.Diagnostics;
using System.IO;
using System.Reflection;
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
    static void TestHotDocumentRetention()
    {
        var candidates = new[] { (Instance: 0, HasFocus: true, VisibleHotPages: 1, HotPages: 3, LastUse: 1L), (Instance: 1, HasFocus: true, VisibleHotPages: 1, HotPages: 1, LastUse: 9L) };
        Check(PdfThumbnailService.SelectHotDocumentOwners(candidates, 1).SetEquals(new[] { 0 }),
            "Idle consolidation keeps the replica covering the most hot pages, not the latest thumbnail worker");
        Check(PdfThumbnailService.SelectHotDocumentOwners(new[] { (0, true, 1, 1, 1L), (1, true, 1, 1, 9L) }, 1).SetEquals(new[] { 1 }),
            "Equal hot-page coverage keeps the most recently used native document");
        Check(PdfThumbnailService.SelectHotDocumentOwners(new[] { (1, true, 1, 1, 9L), (0, true, 1, 1, 9L) }, 1).SetEquals(new[] { 0 }),
            "Idle document ownership has a stable primary-instance tie break");
        Check(PdfThumbnailService.SelectHotDocumentOwners(candidates, 2).Count == 2,
            "The retention experiment can keep both warm replicas");
        Check(PdfThumbnailService.SelectHotDocumentOwners(new[] { (0, true, 1, 1, 1L), (1, true, 0, 3, 9L) }, 1).SetEquals(new[] { 0 }),
            "A visible hot page takes precedence over several prefetched native page handles");
        Check(PdfThumbnailService.SelectHotDocumentOwners(new[] { (0, true, 1, 1, 1L), (1, false, 3, 3, 9L) }, 1).SetEquals(new[] { 0 }),
            "The explicitly focused page takes precedence over several other visible hot pages");
    }

    static async Task TestReaderPreviewRetentionAsync()
    {
        int calls = 0;
        var cache = new ReaderPageRenderCache(2L * 1024 * 1024, (key, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<BitmapSource?>(Bitmap(key.Width, key.Width / 8));
        }, 256 * 1024);
        var key = (Path: "preview-retention.pdf", Page: 1, Width: 2048, Layers: "");
        await cache.GetAsync(key, PdfRenderPriority.Visible, default).ConfigureAwait(false);
        await cache.GetAsync(key with { Page = 2 }, PdfRenderPriority.Visible, default).ConfigureAwait(false);
        var preview = cache.TryGetDisplayImage(key);
        Check(cache.TryGet(key) == null && preview?.PixelWidth == 512 && preview.IsFrozen,
            "Evicting a full page retains a frozen 512px display preview in a separate bounded cache");
        Check(cache.TryGetDisplayImage(key with { Layers = "changed" }) == null,
            "Display previews never cross PDF layer versions");
        Check(cache.PreviewStats.Bytes <= 256 * 1024 && cache.Stats.Bytes <= 2L * 1024 * 1024 + 256 * 1024,
            "Diagnostics include preview storage without exceeding the combined image budgets");
        await cache.GetAsync(key, PdfRenderPriority.Visible, default).ConfigureAwait(false);
        Check(calls == 3 && cache.TryGet(key)?.PixelWidth == 2048,
            "A display preview cannot substitute for sharp native refinement");
        await cache.GetAsync(key with { Page = 3 }, PdfRenderPriority.Visible, default).ConfigureAwait(false);
        Check(cache.TryGetDisplayImage(key with { Page = 2 }) == null && cache.PreviewStats.Count == 2,
            "Preview eviction follows a bounded LRU independent of full-page eviction");
        cache.Invalidate(k => k.Path == key.Path);
        Check(cache.TryGetDisplayImage(key) == null && cache.Stats.Bytes == 0,
            "Source edits and closure invalidate both image tiers");
        var disabled = new ReaderPageRenderCache(1, (k, _, _) => Task.FromResult<BitmapSource?>(Bitmap(2048, 256)), 0);
        await disabled.GetAsync(key, PdfRenderPriority.Visible, default).ConfigureAwait(false);
        Check(disabled.TryGetDisplayImage(key) == null && disabled.PreviewStats.Bytes == 0,
            "A zero-budget profile reproduces the old full-page-only cache");
    }

    static async Task TestIdleDocumentRetirementAsync()
    {
        string path = Path.Combine(Output, "idle-document-retirement.pdf");
        using (var pdf = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfWriter(path)))
            pdf.AddNewPage(new iText.Kernel.Geom.PageSize(300, 200));
        var image = await PdfThumbnailService.RenderPageAsync(path, 0, 1024).ConfigureAwait(false);
        var type = typeof(PdfThumbnailService);
        var cache = (System.Collections.IEnumerable)type.GetField("_documentCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        object entry = cache.Cast<object>().Single(e =>
        {
            var candidate = e.GetType().GetProperty("Key")!.GetValue(e)!;
            return string.Equals((string)candidate.GetType().GetProperty("Path")!.GetValue(candidate)!, path, StringComparison.OrdinalIgnoreCase);
        });
        object key = entry.GetType().GetProperty("Key")!.GetValue(entry)!;
        object lazy = entry.GetType().GetProperty("Value")!.GetValue(entry)!;
        object task = lazy.GetType().GetProperty("Value")!.GetValue(lazy)!;
        object lease = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var instance = (PdfiumInstance)lease.GetType().GetProperty("Pdfium")!.GetValue(lease)!;
        var keys = Array.CreateInstance(key.GetType(), 1); keys.SetValue(key, 0);
        var trim = type.GetMethod("TrimIdleDuplicatesAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var activity = type.GetField("_lastNativeActivity", BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] acquire = { null };
        Check((bool)lease.GetType().GetMethod("TryAcquire")!.Invoke(lease, acquire)!, "An idle fixture lease can be acquired");
        var usage = (IDisposable)acquire[0]!;
        try
        {
            SetIdle();
            Check(await Trim() == 0, "Idle consolidation never retires a lease with an active user");
            usage.Dispose(); usage = null!;
            instance.AddLoad(1);
            try { SetIdle(); Check(await Trim() == 0, "A queued operation also protects an idle candidate"); }
            finally { instance.AddLoad(-1); }
            activity.SetValue(null, Stopwatch.GetTimestamp());
            Check(await Trim() == 0, "Resuming native interaction aborts stale idle selections");
            SetIdle();
            Check(await Trim() == 1 && (bool)lease.GetType().GetProperty("IdlePolicyRetired")!.GetValue(lease)!,
                "Only a truly idle unreferenced document is retired and marked safe for acquisition retry");
            Check(image is { IsFrozen: true, PixelWidth: 1024 }, "Native retirement leaves already-rendered pixels valid");
            var reopened = await PdfThumbnailService.RenderPageAsync(path, 0, 1280).ConfigureAwait(false);
            Check(reopened?.PixelWidth == 1280, "A subsequent explicit request safely opens and renders the retired document again");
        }
        finally
        {
            usage?.Dispose(); activity.SetValue(null, Stopwatch.GetTimestamp());
            PdfThumbnailService.ReleaseUnusedDocuments(Array.Empty<string>());
        }
        void SetIdle() => activity.SetValue(null, Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * 25d));
        Task<int> Trim() => (Task<int>)trim.Invoke(null, new object[] { keys })!;
    }

    sealed record RetentionStep(string Stage, int Page, double Zoom, bool Ready, double ReadyMs,
        int PixelWidth, int PixelHeight, string? PixelHash, double PrivateMiB);

    // Same workload in separate below-normal processes; no input, recents, settings, or source writes.
    static void ProfileReaderRetention(string label, int previewMiB, string manifest)
    {
        using var process = Process.GetCurrentProcess();
        process.PriorityClass = ProcessPriorityClass.BelowNormal;
        if (previewMiB is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(previewMiB));
        var source = JsonSerializer.Deserialize<RecentFile[]>(File.ReadAllText(manifest))!.Last();
        long sourceLength = new FileInfo(source.Path).Length;
        DateTime sourceTime = File.GetLastWriteTimeUtc(source.Path);
        var app = CreateReaderTestApplication();
        AppSettings.ApplyRuntime();
        PdfThumbnailService.StartMemoryPolicy();
        var cache = new ReaderPageRenderCache(64L * 1024 * 1024,
            (key, priority, token) => PdfThumbnailService.RenderPageAsync(key.Path, key.Page - 1, key.Width, token, priority, key.Layers),
            previewMiB * 1024L * 1024);
        var view = new ContinuousPdfView { PrefetchPageCount = 2 };
        view.CachedPageProvider = (row, width) => cache.TryGetDisplayImage(RenderCacheKeys.ReaderPage(row.SourcePath, row.PageNumber, width));
        view.PageRenderer = (row, width, priority, token) => cache.GetAsync(RenderCacheKeys.ReaderPage(row.SourcePath, row.PageNumber, width), priority, token);
        view.UserInteraction += PdfThumbnailService.NoteInteraction;
        var host = new Window { Content = view, Width = 1280, Height = 800, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize };
        app.MainWindow = host;
        var frame = new DispatcherFrame();
        Exception? failure = null;
        host.Show();
        host.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            long peak = 0;
            var samples = new List<double>();
            long previous = Stopwatch.GetTimestamp();
            bool active = false;
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                long now = Stopwatch.GetTimestamp();
                if (active) samples.Add(Math.Max(0, Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds - 16));
                previous = now;
                process.Refresh(); peak = Math.Max(peak, process.PrivateMemorySize64);
            };
            timer.Start();
            try
            {
                var sizes = await PdfThumbnailService.GetPageSizesAsync(source.Path) ?? throw new InvalidOperationException("Cannot read PDF sizes.");
                double maxWidth = sizes.Max(s => s.Width);
                var pages = sizes.Select((s, i) => new PagePlacement { SourcePath = source.Path, PageNumber = i + 1,
                    PageWidthPoints = s.Width, PageHeightPoints = s.Height, BaseWidth = PagePlacement.DefaultLayoutWidth * s.Width / maxWidth,
                    AspectRatio = s.Height / s.Width }).ToArray();
                var buffer = Stopwatch.StartNew();
                while (PdfFileBuffer.GetLoadedFraction(source.Path) < 1 && buffer.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(50);
                if (PdfFileBuffer.GetLoadedFraction(source.Path) < 1) throw new InvalidOperationException("File buffer did not finish.");
                double dpi = VisualTreeHelper.GetDpi(view).DpiScaleX;
                var steps = new List<RetentionStep>();
                var diagnostics = ReaderTimingSnapshot.Read();
                var cpu = process.TotalProcessorTime;
                active = true;
                view.SetDocument(pages, 1);
                foreach (int index in Enumerable.Range(0, 9).Select(i => Math.Min(i * 10, pages.Length - 1)).Distinct())
                    await Navigate(index, 1, "cold-page");

                // Force the same full-image eviction in every process, leaving the small tier alone.
                // This is a controlled cache probe, not a claim about screen presentation latency.
                var revisit = pages[Math.Min(50, pages.Length - 1)];
                var revisitKey = RenderCacheKeys.ReaderPage(revisit.SourcePath, revisit.PageNumber, 512);
                var fullImages = (BitmapMemoryCache<(string Path, int Page, int Width, string Layers)>)typeof(ReaderPageRenderCache)
                    .GetField("_images", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
                fullImages.RemoveWhere(k => k.Path == revisit.SourcePath && k.Page == revisit.PageNumber);
                bool fullEvicted = cache.TryGet(revisitKey) == null;
                var fallback = cache.TryGetDisplayImage(revisitKey);
                revisit.ReaderBitmap = null;
                view.SetDocument(pages, 1);
                view.ScrollToPage(revisit.PageNumber - 1);
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                bool immediateFallback = StateImage(revisit) != null;
                await Navigate(revisit.PageNumber - 1, 1, "evicted-revisit");
                await Navigate(Math.Min(10, pages.Length - 1), 1, "before-idle");
                var current = pages[Math.Min(10, pages.Length - 1)];
                // Populate the same hot page on the thumbnail replica as well as the visible one.
                await PdfThumbnailService.RenderPageAsync(source.Path, current.PageNumber - 1, 340, default, PdfRenderPriority.Thumbnail);
                await Task.Delay(1000);
                active = false;
                process.Refresh();
                double beforeIdle = MiB(process.PrivateMemorySize64);
                int documentsBeforeIdle = PdfThumbnailService.CachedDocumentCount;
                var beforeIdleTiming = ReaderTimingSnapshot.Read();
                await Task.Delay(25000);
                process.Refresh();
                double idle = MiB(process.PrivateMemorySize64);
                int documentsAfterIdle = PdfThumbnailService.CachedDocumentCount;
                var instancesAfterIdle = PdfiumPool.Instances.Select(i => new { i.Index, i.OpenDocuments, i.CachedPages, i.PagesParsed }).ToArray();
                var idleTiming = ReaderTimingSnapshot.Read();
                string memoryAfterIdle = MemoryProbe.Describe();
                active = true;
                await Navigate(current.PageNumber - 1, 1.25, "resume-sharper");
                await Navigate(current.PageNumber - 1, 3, "resume-deep");
                await Navigate(current.PageNumber - 1, 1, "resume-normal");
                active = false;
                var end = ReaderTimingSnapshot.Read();
                samples.Sort();
                var result = new { Label = label, PreviewMiB = previewMiB, HotDocumentCopies = PdfThumbnailService.HotDocumentCopies,
                    PdfiumInstances = PdfiumPool.Count, File = Path.GetFileName(source.Path), SourceMiB = MiB(sourceLength), Pages = pages.Length,
                    Dpi = dpi, ViewportWidth = view.ViewportWidth, ViewportHeight = view.ViewportHeight,
                    AllReady = steps.All(s => s.Ready), PeakPrivateMiB = MiB(peak), BeforeIdlePrivateMiB = beforeIdle,
                    IdlePrivateMiB = idle, DocumentsBeforeIdle = documentsBeforeIdle, DocumentsAfterIdle = documentsAfterIdle,
                    InstancesAfterIdle = instancesAfterIdle,
                    PreviewEntries = cache.PreviewStats.Count, PreviewBytes = cache.PreviewStats.Bytes,
                    CombinedImageCacheMiB = MiB(cache.Stats.Bytes), FullImageEvicted = fullEvicted,
                    EvictedProbePage = revisit.PageNumber, DisplayFallbackAvailable = fallback != null, ImmediateStateFallback = immediateFallback,
                    CpuSeconds = Math.Round((process.TotalProcessorTime - cpu).TotalSeconds, 3),
                    DispatcherDelayP95Ms = samples.Count == 0 ? 0 : Math.Round(samples[(int)((samples.Count - 1) * .95)], 2),
                    IdleDiagnostics = idleTiming.Since(beforeIdleTiming), ResumeDiagnostics = end.Since(idleTiming),
                    Diagnostics = end.Since(diagnostics), MemoryAfterIdle = memoryAfterIdle, Steps = steps,
                    SourcesUnchanged = new FileInfo(source.Path).Length == sourceLength && File.GetLastWriteTimeUtc(source.Path) == sourceTime };
                File.WriteAllText(Path.Combine(Output, "retention-" + label + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"{label}: peak={result.PeakPrivateMiB}, idle={idle}, native docs={documentsBeforeIdle}->{documentsAfterIdle}, fallback={immediateFallback}");
                if (!result.AllReady || !result.SourcesUnchanged || !fullEvicted) throw new InvalidOperationException("Invalid retention workload.");

                BitmapSource? StateImage(PagePlacement row)
                {
                    var states = (System.Collections.IDictionary)typeof(ContinuousPdfView).GetField("_states", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                    return states.Contains(row) ? states[row]?.GetType().GetField("Bitmap")!.GetValue(states[row]) as BitmapSource : null;
                }

                async Task Navigate(int index, double zoom, string stage)
                {
                    var row = pages[index];
                    var watch = Stopwatch.StartNew();
                    view.ZoomKeepTop(zoom);
                    view.ScrollToPage(index);
                    int width = ContinuousPdfView.PreferredPageBitmapWidth(row.LayoutWidth * zoom * dpi, true);
                    bool region = row.LayoutWidth * zoom * dpi > 2304 * 1.03;
                    while (!Ready() && watch.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(16);
                    double readyMs = watch.Elapsed.TotalMilliseconds;
                    bool ready = Ready();
                    await Task.Delay(300);
                    BitmapSource? image;
                    if (region) TryRenderedRegion(view, row, out image); else image = StateImage(row);
                    process.Refresh();
                    steps.Add(new(stage, row.PageNumber, zoom, ready, Math.Round(readyMs, 2), image?.PixelWidth ?? 0,
                        image?.PixelHeight ?? 0, image == null ? null : PixelHash(image), MiB(process.PrivateMemorySize64)));
                    Console.WriteLine($"{label}: {stage} p{row.PageNumber} zoom={zoom} {readyMs:0.0}ms ready={ready}");
                    bool Ready() => region ? TryRenderedRegion(view, row, out _) : StateImage(row)?.PixelWidth >= width;
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                timer.Stop(); view.SetDocument(null, 1); cache.Clear();
                ContinuousPdfView.ReleaseUnusedRegionSources(new HashSet<string>());
                PdfThumbnailService.SetHotPages(Array.Empty<(string, int)>());
                host.Close(); PdfThumbnailService.PrepareForShutdown(TimeSpan.FromSeconds(10));
                frame.Continue = false;
            }
        }));
        Dispatcher.PushFrame(frame);
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        static double MiB(long bytes) => Math.Round(bytes / 1048576d, 2);
        static string PixelHash(BitmapSource image)
        {
            int stride = (image.PixelWidth * image.Format.BitsPerPixel + 7) / 8;
            var pixels = new byte[stride * image.PixelHeight];
            image.CopyPixels(pixels, stride, 0);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
        }
    }
}
