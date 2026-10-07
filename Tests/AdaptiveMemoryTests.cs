using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Domain;
using XTPdfMergeApp.Services;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;

internal static partial class Program
{
    static void CompleteWithDispatcher(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(30)) Pump(TimeSpan.FromMilliseconds(10));
        if (!task.IsCompleted) throw new TimeoutException("Memory-policy integration did not complete");
        task.GetAwaiter().GetResult();
    }

    static void TestAdaptiveMemory()
    {
        const long mib = AdaptiveMemoryPolicy.MiB;
        var highProfile = ReaderPerformanceProfile.For(ReaderPerformanceMode.Balance);
        var maxProfile = ReaderPerformanceProfile.For(ReaderPerformanceMode.Maximum);
        var saving = ReaderPerformanceProfile.For(ReaderPerformanceMode.MemorySaving);
        Check(saving.CacheLimit < highProfile.CacheLimit && highProfile.CacheLimit < maxProfile.CacheLimit &&
              saving.SoftLimit < highProfile.SoftLimit && highProfile.SoftLimit < maxProfile.SoftLimit,
            "The three profiles increase image retention and memory thresholds progressively");
        Check(new AdaptiveMemoryPolicy().Update(strongPlaceholder(), TimeSpan.Zero, highProfile).State == MemoryPressureState.Critical,
            "Balance still responds to critical Windows memory pressure");
        Check(AppSettings.ParsePerformanceMode("Performance") == ReaderPerformanceMode.Balance && AppSettings.ParsePerformanceMode("Ultra") == ReaderPerformanceMode.Maximum,
            "Old Performance / Ultra preferences migrate to Balance / Maximum");
        static SystemMemorySample strongPlaceholder() => new(32 * 1024 * AdaptiveMemoryPolicy.MiB, 100 * AdaptiveMemoryPolicy.MiB, 99,
            48 * 1024 * AdaptiveMemoryPolicy.MiB, 100 * AdaptiveMemoryPolicy.MiB, 100 * AdaptiveMemoryPolicy.MiB, 0);
        Check(ReaderPerformanceProfile.For((ReaderPerformanceMode)999).Mode == ReaderPerformanceMode.Balance, "Invalid profile falls back to Balance");
        Check(AppSettings.ParsePerformanceMode("High") == ReaderPerformanceMode.Balance, "An existing High preference migrates to Balance");
        Check(AppSettings.ParsePerformanceMode("") == ReaderPerformanceMode.Balance, "A missing or invalid preference uses Balance");
        Check(AppSettings.ParsePerformanceMode("MemorySaving") == ReaderPerformanceMode.MemorySaving, "An explicit memory-saving preference is preserved");
        var strong = new SystemMemorySample(32 * 1024 * mib, 24 * 1024 * mib, 25, 48 * 1024 * mib, 32 * 1024 * mib, 1800 * mib, 800 * mib);
        Check(new AdaptiveMemoryPolicy().Update(strong, TimeSpan.Zero).State == MemoryPressureState.Critical, "Default still reclaims a large app footprint");
        Check(new AdaptiveMemoryPolicy().Update(strong, TimeSpan.Zero, maxProfile).State == MemoryPressureState.Normal, "Maximum allows a larger app footprint when the machine is healthy");
        Check(new AdaptiveMemoryPolicy().Update(strong with { AvailablePhysical = 200 * mib, PhysicalLoadPercent = 97 }, TimeSpan.Zero, maxProfile).State == MemoryPressureState.Critical,
            "Maximum never bypasses critical machine-memory protection");
        var switching = new AdaptiveMemoryPolicy();
        switching.Update(strong, TimeSpan.Zero);
        Check(switching.Update(strong, TimeSpan.FromSeconds(1), maxProfile).State == MemoryPressureState.Normal, "Raising profile re-evaluates app pressure without waiting for recovery");
        Check(switching.Update(strong, TimeSpan.FromSeconds(2)).State == MemoryPressureState.Critical, "Lowering profile immediately restores the strict app threshold");
        var growing = new AdaptiveMemoryPolicy();
        AdaptiveMemoryDecision maxDecision = default;
        for (int i = 0; i < 60; i++) maxDecision = growing.Update(strong, TimeSpan.FromSeconds(i), maxProfile);
        Check(maxDecision.RetentionTarget == maxProfile.CacheLimit && highProfile.CacheLimit < maxProfile.CacheLimit, "Maximum cache grows gradually to its configured ceiling");
        Check(growing.Update(strong with { ParentPrivate = 350 * mib, WorkerPrivate = 350 * mib }, TimeSpan.FromSeconds(61)).RetentionTarget == AdaptiveMemoryPolicy.NormalCacheLimit,
            "Switching back lowers the cache ceiling in one update");
        var healthy = new SystemMemorySample(16 * 1024 * mib, 10 * 1024 * mib, 37, 24 * 1024 * mib, 16 * 1024 * mib, 350 * mib, 350 * mib);
        var low = healthy with { AvailablePhysical = 1500 * mib, PhysicalLoadPercent = 90 };
        var critical = low with { AvailablePhysical = 400 * mib, PhysicalLoadPercent = 97 };
        var policy = new AdaptiveMemoryPolicy();
        var normal = policy.Update(healthy, TimeSpan.Zero);
        Check(normal.State == MemoryPressureState.Normal && normal.RetentionTarget <= AdaptiveMemoryPolicy.NormalCacheLimit, "Normal cache stays bounded even with abundant machine RAM");
        var pressured = policy.Update(low, TimeSpan.FromSeconds(2));
        Check(pressured.State == MemoryPressureState.Pressure && pressured.BackgroundLanes == 1 && pressured.RetentionTarget <= 768 * mib,
            "Low physical headroom reduces retention and background concurrency immediately");
        var urgent = policy.Update(critical, TimeSpan.FromSeconds(4));
        Check(urgent.State == MemoryPressureState.Critical && urgent.RetentionTarget == AdaptiveMemoryPolicy.Floor && urgent.BackgroundLanes == 0,
            "Critical state preserves a working-memory floor");
        Check(policy.Update(default, TimeSpan.FromSeconds(5)) == urgent, "Failed probes do not create pressure or recover the policy");
        Check(policy.Update(healthy, TimeSpan.FromSeconds(6)).State == MemoryPressureState.Critical, "A healthy sample does not immediately restart speculation");
        Check(policy.Update(healthy, TimeSpan.FromSeconds(40)).State == MemoryPressureState.Critical, "Recovery waits for the sustained delay");
        policy.Update(low, TimeSpan.FromSeconds(41));
        Check(policy.Update(healthy, TimeSpan.FromSeconds(60)).State == MemoryPressureState.Critical, "A renewed low sample resets the recovery clock");
        var recovered = policy.Update(healthy, TimeSpan.FromSeconds(106));
        Check(recovered.State == MemoryPressureState.Normal && recovered.RetentionTarget == AdaptiveMemoryPolicy.Floor + 64 * mib,
            "Sustained recovery gradually restores budgets instead of refilling caches");
        var commitPolicy = new AdaptiveMemoryPolicy();
        Check(commitPolicy.Update(healthy with { CommitAvailable = 200 * mib }, TimeSpan.Zero).State == MemoryPressureState.Critical,
            "Low system commit headroom triggers pressure even with free physical memory");
        var small = new AdaptiveMemoryPolicy().Update(healthy with { TotalPhysical = 2 * 1024 * mib, AvailablePhysical = 1200 * mib }, TimeSpan.Zero);
        Check(small.RetentionTarget >= AdaptiveMemoryPolicy.Floor && small.RetentionTarget < 512 * mib, "Budgets scale down on a small-memory machine");
        var large = new AdaptiveMemoryPolicy();
        AdaptiveMemoryDecision largeDecision = default;
        for (int tick = 0; tick < 100; tick++) largeDecision = large.Update(healthy with { TotalPhysical = 128 * 1024 * mib, AvailablePhysical = 96 * 1024 * mib }, TimeSpan.FromSeconds(tick * 2));
        Check(largeDecision.RetentionTarget == AdaptiveMemoryPolicy.NormalCacheLimit, "Large machines retain a bounded on-demand cache target");
        Check(new AdaptiveMemoryPolicy().Update(healthy with { ParentPrivate = 800 * mib, WorkerPrivate = 300 * mib }, TimeSpan.Zero).State == MemoryPressureState.Pressure,
            "App soft threshold triggers pressure even when machine RAM is plentiful");
        Check(new AdaptiveMemoryPolicy().Update(healthy with { ParentPrivate = 1200 * mib, WorkerPrivate = 400 * mib }, TimeSpan.Zero).State == MemoryPressureState.Critical,
            "App urgent threshold stops speculation independently of OS pressure");
        var budget = new RenderMemoryBudget(100);
        using (var held = budget.AcquireAsync(80, PdfRenderPriority.Background, default).GetAwaiter().GetResult())
        {
            using var cancellation = new CancellationTokenSource();
            var waiting = budget.AcquireAsync(40, PdfRenderPriority.Visible, cancellation.Token);
            Check(!waiting.IsCompleted && budget.Stats.Used == 80, "Concurrent render reservations respect the byte budget");
            cancellation.Cancel();
            try { waiting.GetAwaiter().GetResult(); Check(false, "Cancelled reservation must not complete"); } catch (OperationCanceledException) { }
        }
        Check(budget.Stats.Used == 0, "Cancellation and lease disposal release all reservations");
        using (var oversized = budget.AcquireAsync(200, PdfRenderPriority.Visible, default).GetAwaiter().GetResult())
            Check(budget.Stats.Used == 200, "Oversized visible render can run alone without deadlocking");
        Check(MemoryProbe.TrySampleSystemMemory(out var actual) && actual.IsValid, "Windows probe reads physical memory and global commit headroom");

        var bounded = new BitmapMemoryCache<int>(mib);
        var pinned = Bitmap(64, 64); var cold = Bitmap(64, 64);
        bounded.Set(1, pinned); bounded.Set(2, cold);
        bounded.KeepImage = bitmap => ReferenceEquals(bitmap, pinned);
        bounded.SetBudget(1);
        Check(bounded.Count == 1 && bounded.TryGetValue(1, out var retained) && ReferenceEquals(retained, pinned),
            "Pinned visible image may exceed retention budget while cold images are evicted");
        bounded.KeepImage = null; bounded.Trim();
        Check(bounded.Count == 0, "An image becomes reclaimable when it leaves the viewport");
        using (var visibleCancellation = new CancellationTokenSource())
        using (var started = new SemaphoreSlim(0))
        {
            var pending = new ReaderPageRenderCache(mib, async (_, _, token) =>
            {
                started.Release();
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return null;
            });
            var background = pending.GetAsync(("background", 1, 100, ""), PdfRenderPriority.Background, default);
            var foreground = pending.GetAsync(("foreground", 1, 100, ""), PdfRenderPriority.Visible, visibleCancellation.Token);
            Check(started.Wait(TimeSpan.FromSeconds(5)) && started.Wait(TimeSpan.FromSeconds(5)), "Foreground and background fixture requests are in flight");
            pending.ApplyMemoryBudget(1, _ => false, cancelBackground: true);
            bool cancelled = false;
            try { background.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && !foreground.IsCompleted, "Pressure cancels obsolete background requests while preserving foreground work");
            visibleCancellation.Cancel();
            try { foreground.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            Check(pending.Stats.Bytes == 0, "Cancelled queued work cannot repopulate the reduced cache");
        }

        if (Application.Current == null) CreateReaderTestApplication();
        string source = Path.Combine(Output, "adaptive-memory.pdf");
        using (var pdf = new PdfDocument(new PdfWriter(source)))
            for (int page = 0; page < 20; page++)
                new PdfCanvas(pdf.AddNewPage(new iText.Kernel.Geom.PageSize(1000, 600)))
                    .MoveTo(20, 20).LineTo(980, 580).Stroke();
        byte[] before = SHA256.HashData(File.ReadAllBytes(source));
        var rows = Enumerable.Range(1, 20).Select(page => new PagePlacement
            { SourcePath = source, PageNumber = page, BaseWidth = 1000, AspectRatio = .6 }).ToArray();
        var view = new ContinuousPdfView { PrefetchPageCount = 4 };
        var host = new Window { Content = view, Width = 700, Height = 400, ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        host.Show(); host.UpdateLayout();
        var sharpFixture = Bitmap(4608, 2764);
        view.PageRenderer = (_, _, _, _) => Task.FromResult<BitmapSource?>(sharpFixture);
        try
        {
            view.SetDocument(rows, 1);
            Pump(TimeSpan.FromMilliseconds(300));
            var displayed = rows[0].ReaderBitmap;
            Check(displayed != null, "Offscreen view has a readable page before pressure");
            var readerCache = new ReaderPageRenderCache(64 * mib, (key, _, _) =>
                Task.FromResult<BitmapSource?>(key.Width == displayed!.PixelWidth ? displayed : Bitmap(key.Width, 10)), previewBudget: 0)
                { ReuseLargerImages = false };
            var liveKey = (source, 1, displayed!.PixelWidth, "");
            readerCache.GetAsync(liveKey, PdfRenderPriority.Visible, default).GetAwaiter().GetResult();
            var obsoleteKey = (source, 1, displayed.PixelWidth + 1, "");
            readerCache.GetAsync(obsoleteKey, PdfRenderPriority.Visible, default).GetAwaiter().GetResult();
            // Actual native replies populate the bridge; page affinity covers both background workers.
            for (int page = 0; page < 12; page++)
                ExperimentalMuPdfViewport.RenderFullPageAsync(source, page, 64, default, PdfRenderPriority.Background).GetAwaiter().GetResult();
            for (int page = 0; page < 2; page++)
                ExperimentalMuPdfViewport.RenderFullPageAsync(source, page, 65, default, PdfRenderPriority.Background).GetAwaiter().GetResult();
            Pump(TimeSpan.FromMilliseconds(2200));
            CompleteWithDispatcher(AdaptiveMemoryController.ApplyAsync(urgent));
            readerCache.ApplyMemoryBudget(1, key => AdaptiveMemoryController.IsProtected(key.Path, key.Page), true);
            Check(ReferenceEquals(readerCache.TryGet(liveKey), displayed) && readerCache.TryGet(obsoleteKey) == null,
                "Live bitmap stays pinned while obsolete zoom resolutions of the same page can be evicted");
            Check(!AdaptiveMemoryController.AllowSpeculation && AdaptiveMemoryController.BackgroundLanes == 0, "Critical pressure suspends speculative work");
            using (var stoppedPrefetch = new CancellationTokenSource())
            {
                var waiting = ThumbnailCache.WaitForBackgroundPrefetchTurnAsync(true, stoppedPrefetch.Token);
                Check(!waiting.IsCompleted, "Future background thumbnail warming waits during pressure");
                stoppedPrefetch.Cancel();
                try { waiting.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            }
            Check(ReferenceEquals(rows[0].ReaderBitmap, displayed) && view.MemoryStats.Pages > 0,
                "Pressure preserves the displayed sharp bitmap and live page state");
            Check(ReaderWindowBudgetTotal() <= AdaptiveMemoryPolicy.Floor, "Reader, bridge, regions and thumbnails receive coordinated budgets");
            for (int worker = 2; worker < 4; worker++)
            {
                var stats = ExperimentalMuPdfViewport.CommandAsync("", "stats", workerIndex: worker).GetAwaiter().GetResult();
                Check(stats.GetProperty("documents").GetInt32() == 1 && stats.GetProperty("displayLists").GetInt32() <= 1,
                    "Idle native trim retains visible native pages and discards distant display lists");
            }
            int foregroundBefore = ExperimentalMuPdfViewport.RunningWorkerCount - ExperimentalMuPdfViewport.RunningBackgroundWorkerCount;
            Pump(TimeSpan.FromMilliseconds(10200));
            CompleteWithDispatcher(AdaptiveMemoryController.ApplyAsync(urgent));
            Check(ExperimentalMuPdfViewport.RunningBackgroundWorkerCount == 0 && ExperimentalMuPdfViewport.RunningWorkerCount == foregroundBefore,
                "Critical pressure retires idle background workers and preserves foreground workers");
            var rerender = ExperimentalMuPdfViewport.RenderFullPageAsync(source, 19, 66, default).GetAwaiter().GetResult();
            Check(rerender is { IsFrozen: true, PixelWidth: 66 }, "Foreground rendering works while speculative work is suspended");
            CompleteWithDispatcher(AdaptiveMemoryController.ApplyAsync(recovered));
            Check(AdaptiveMemoryController.AllowSpeculation && ReferenceEquals(rows[0].ReaderBitmap, displayed), "Recovery restores speculation without replacing the live image");
            foreach (double zoom in new[] { 1.08, 1.2, 1.0 })
            {
                view.ZoomAt(zoom, new Point(350, 200));
                Pump(TimeSpan.FromMilliseconds(80));
            }
            Check(ReferenceEquals(rows[0].ReaderBitmap, displayed), "Nearby zoom after recovery continues to use the retained sharp image");
            // Normal limits are restored lazily through the next request, without restarting all workers.
            for (int page = 2; page < 8; page++)
                ExperimentalMuPdfViewport.RenderFullPageAsync(source, page, 67, default, PdfRenderPriority.Background).GetAwaiter().GetResult();
            var normalStats = ExperimentalMuPdfViewport.CommandAsync("", "stats", workerIndex: 2).GetAwaiter().GetResult();
            Check(normalStats.GetProperty("displayLists").GetInt32() > 1 && normalStats.GetProperty("displayLists").GetInt32() <= 8,
                "Native cache limits recover on demand without protocol corruption");
            Check(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(before), "Native memory trimming never changes the source PDF");
            Check(!Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Contains("pdfium", StringComparison.OrdinalIgnoreCase)),
                "Adaptive policy uses MuPDF without loading the legacy backend");
            Check(DiagnosticsReport.Build().Contains("RAM thích ứng"), "Diagnostics expose adaptive pressure and budgets");
        }
        finally
        {
            view.CancelAll(); host.Close();
            CompleteWithDispatcher(AdaptiveMemoryController.ApplyAsync(normal));
            AdaptiveMemoryController.Stop();
            ExperimentalMuPdfViewport.Shutdown();
        }
        Check(!AdaptiveMemoryController.IsProtected(source, 1), "Unloaded views no longer pin cache ownership");
        CompleteWithDispatcher(AdaptiveMemoryController.ApplyAsync(normal));
        Check(ExperimentalMuPdfViewport.RunningWorkerCount == 0, "Restoring budgets does not preemptively start workers");
        AdaptiveMemoryController.Start(() => critical, TimeSpan.FromMilliseconds(20));
        try
        {
            var applied = Stopwatch.StartNew();
            while ((AdaptiveMemoryController.State != MemoryPressureState.Critical || ReaderWindowBudgetTotal() > AdaptiveMemoryPolicy.Floor) &&
                applied.Elapsed < TimeSpan.FromSeconds(5)) Pump(TimeSpan.FromMilliseconds(10));
            Check(AdaptiveMemoryController.State == MemoryPressureState.Critical && ReaderWindowBudgetTotal() <= AdaptiveMemoryPolicy.Floor,
                "Timer applies an injected critical sample through dispatcher and synchronized cache budgets");
        }
        finally { AdaptiveMemoryController.Stop(); }
        CompleteWithDispatcher(AdaptiveMemoryController.ApplyAsync(normal));
        using (var sampleEntered = new ManualResetEventSlim())
        using (var releaseSample = new ManualResetEventSlim())
        {
            int samples = 0;
            AdaptiveMemoryController.Start(() =>
            {
                Interlocked.Increment(ref samples); sampleEntered.Set();
                releaseSample.Wait(TimeSpan.FromSeconds(5));
                return critical;
            }, TimeSpan.FromMilliseconds(20));
            try
            {
                Check(sampleEntered.Wait(TimeSpan.FromSeconds(5)), "Injected memory sampler runs off the view dispatcher");
                AdaptiveMemoryController.Stop(); AdaptiveMemoryController.Stop();
                releaseSample.Set();
                Pump(TimeSpan.FromMilliseconds(200));
                Check(AdaptiveMemoryController.State == MemoryPressureState.Normal && samples == 1,
                    "Stopping the controller cancels a late sample and prevents timer mutation after disposal");
            }
            finally { releaseSample.Set(); AdaptiveMemoryController.Stop(); }
        }
    }

    static long ReaderWindowBudgetTotal() => XTPdfMergeApp.ReaderWindow.CurrentReaderCacheBudget +
        ContinuousPdfView.CurrentRegionCacheBudget + ThumbnailCache.CurrentBudgetBytes + ExperimentalMuPdfViewport.CurrentCacheBudget;
}
