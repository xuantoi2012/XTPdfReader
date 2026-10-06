using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Domain;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    static void ProfileReclaimMemory(string firstSource, string secondSource, string mode, int pageNumber, bool neighbors = false)
    {
        var modes = new[] { "reference", "reference-background-gc", "cache-only", "cache-gc", "cache-background-gc", "park-spare-gc", "park-spare-background-gc", "park-all-gc", "cache-gc-heap" };
        if (!modes.Contains(mode)) throw new ArgumentException("Unknown reclamation experiment: " + mode);
        if (Application.Current == null) CreateReaderTestApplication();
        var records = new List<object>();
        string output = Path.Combine(Output, $"reclaim-{mode}-page-{pageNumber}-{(neighbors ? "neighbors" : "single")}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        byte[] SourceHash(string path) { using var stream = File.OpenRead(path); return SHA256.HashData(stream); }
        var sourceHashes = new[] { SourceHash(firstSource), SourceHash(secondSource) };
        var sizes = ExperimentalMuPdfViewport.CommandAsync(firstSource, "metadata").GetAwaiter().GetResult().GetProperty("sizes");
        var row = new PagePlacement { SourcePath = firstSource, PageNumber = pageNumber, BaseWidth = 1200,
            AspectRatio = sizes[pageNumber - 1][1].GetDouble() / sizes[pageNumber - 1][0].GetDouble() };
        var adjacent = new PagePlacement { SourcePath = firstSource, PageNumber = pageNumber + 1, BaseWidth = 1200,
            AspectRatio = sizes[pageNumber][1].GetDouble() / sizes[pageNumber][0].GetDouble() };
        var view = new ContinuousPdfView { PrefetchPageCount = neighbors ? 1 : 0 };
        var host = new Window { Content = view, Width = 1320, Height = 700, WindowStyle = WindowStyle.None,
            ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        view.PageRenderer = (page, width, priority, token) => PdfThumbnailService.RenderPageAsync(page.SourcePath, page.PageNumber - 1, width, token, priority);
        host.Show(); host.UpdateLayout();
        var normal = new AdaptiveMemoryDecision(MemoryPressureState.Normal, 1952 * AdaptiveMemoryPolicy.MiB, "experiment baseline");
        double maxDispatcherGap = 0;
        long lastTick = Stopwatch.GetTimestamp();
        var heartbeat = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        heartbeat.Tick += (_, _) => { long now = Stopwatch.GetTimestamp(); maxDispatcherGap = Math.Max(maxDispatcherGap, Stopwatch.GetElapsedTime(lastTick, now).TotalMilliseconds); lastTick = now; };
        heartbeat.Start();
        try
        {
            CompleteWithDispatcher(AdaptiveMemoryController.ApplyAsync(normal));
            view.SetDocument(neighbors ? new[] { row, adjacent } : new[] { row }, 1);
            WaitUntil(() => row.ReaderBitmap != null && (!neighbors || adjacent.ReaderBitmap != null), "Sharp full-page renders arrive");
            Check(row.ReaderBitmap is { PixelWidth: 4608 }, "Experiment preserves the accepted sharp whole-page tier");
            var visible = row.ReaderBitmap;
            // Additional source/tab pages fill cold bitmap ownership and both native foreground contexts.
            foreach (var (path, page) in new[] { (firstSource, 0), (firstSource, 1), (firstSource, pageNumber - 1),
                (firstSource, pageNumber), (secondSource, 0), (secondSource, 1), (secondSource, 2), (secondSource, 3) })
                Check(PdfThumbnailService.RenderPageAsync(path, page, 1200).GetAwaiter().GetResult() is { IsFrozen: true }, "Workload page renders");
            Task.WhenAll(Enumerable.Range(0, 2).Select(page => PdfThumbnailService.RenderPageAsync(secondSource, page, 341,
                priority: PdfRenderPriority.Background))).GetAwaiter().GetResult();
            Pump(TimeSpan.FromMilliseconds(200));
            byte[] frame = FrameHash();
            Snapshot("normal");
            CompleteWithDispatcher(AdaptiveMemoryController.ApplyAsync(new(MemoryPressureState.Critical, AdaptiveMemoryPolicy.Floor, "experiment reference")));
            Pump(TimeSpan.FromMilliseconds(10200));
            CompleteWithDispatcher(AdaptiveMemoryController.ApplyAsync(new(MemoryPressureState.Critical, AdaptiveMemoryPolicy.Floor, "experiment reference")));
            Snapshot("adaptive-reference");
            maxDispatcherGap = 0; lastTick = Stopwatch.GetTimestamp();
            var reclaim = Stopwatch.StartNew();
            int retired = 0;
            if (mode is not "reference" and not "reference-background-gc")
            {
                // Zero means keep actual displayed image references only, never clear them.
                XTPdfMergeApp.ReaderWindow.ApplyReaderMemoryBudget(0);
                ContinuousPdfView.ApplyRegionMemoryBudget(0);
                ThumbnailCache.ApplyMemoryBudget(0);
                ExperimentalMuPdfViewport.ApplyMemoryBudget(0);
            }
            if (mode.StartsWith("park-"))
                retired = ExperimentalMuPdfViewport.RetireIdleWorkers(mode.StartsWith("park-spare") ? (pageNumber - 1) & 1 : -1, 2);
            if (mode is "cache-gc" or "park-spare-gc" or "park-all-gc" or "cache-gc-heap")
                CompleteWithDispatcher(Task.Run(() =>
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
                    if (mode == "cache-gc-heap") MemoryProbe.CompactNativeHeaps();
                }));
            if (mode is "cache-background-gc" or "park-spare-background-gc" or "reference-background-gc")
                CompleteWithDispatcher(Task.Run(() => GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false)));
            double reclaimMs = reclaim.Elapsed.TotalMilliseconds;
            Pump(TimeSpan.FromMilliseconds(1000));
            double reclaimDispatcherGap = maxDispatcherGap;
            Check(FrameHash().SequenceEqual(frame) && ReferenceEquals(row.ReaderBitmap, visible), "Reclamation preserves every pixel of the displayed frame");
            Snapshot("reclaimed", new { ReclaimMs = reclaimMs, MaxDispatcherGapMs = reclaimDispatcherGap, Retired = retired });
            var watch = Stopwatch.StartNew();
            Check(ReferenceEquals(PdfThumbnailService.RenderPageAsync(firstSource, pageNumber - 1, 1100).GetAwaiter().GetResult(), visible),
                "Visible page revisit remains a cache hit after reclamation");
            Record("visible-revisit", new { Ms = watch.Elapsed.TotalMilliseconds });
            foreach (double zoom in new[] { 1.1, 1.5, 2.0, 3.0, 1.0 })
            {
                view.ZoomAt(zoom, new Point(600, 300)); Pump(TimeSpan.FromMilliseconds(100));
                Check(ReferenceEquals(row.ReaderBitmap, visible), "Nearby zoom retains its sharp page");
            }
            maxDispatcherGap = 0; lastTick = Stopwatch.GetTimestamp();
            watch.Restart(); view.ZoomAt(6, new Point(600, 300));
            WaitUntil(() => view.MemoryStats.Regions > 0, "Deep zoom receives a sharp crop after reclamation");
            double deepZoomMs = watch.Elapsed.TotalMilliseconds;
            view.ScrollBy(100, 0); Pump(TimeSpan.FromMilliseconds(300));
            Check(view.MemoryStats.Regions > 0, "Pan retains sharp regions");
            Record("deep-zoom-pan", new { SharpCropMs = deepZoomMs, MaxDispatcherGapMs = maxDispatcherGap });
            Snapshot("after-interaction");
            if (neighbors)
            {
                var savedAdjacent = adjacent.ReaderBitmap;
                view.ZoomAt(1, new Point(600, 300)); Pump(TimeSpan.FromMilliseconds(150));
                Check(view.MemoryStats.Regions == 0, "Fit view clears old deep-zoom regions before neighbor measurement");
                view.ScrollToPage(1); Pump(TimeSpan.FromMilliseconds(100));
                watch.Restart();
                Check(ReferenceEquals(PdfThumbnailService.RenderPageAsync(firstSource, pageNumber, 1100).GetAwaiter().GetResult(), savedAdjacent),
                    "Neighbor page stays sharp and cached after reclamation");
                Record("neighbor-revisit", new { Ms = watch.Elapsed.TotalMilliseconds });
                watch.Restart(); view.ZoomAt(6, new Point(600, 300));
                WaitUntil(() => view.MemoryStats.Regions > 0, "Neighbor deep zoom receives a sharp crop");
                Record("neighbor-deep-zoom", new { SharpCropMs = watch.Elapsed.TotalMilliseconds });
            }
            Check(SourceHash(firstSource).SequenceEqual(sourceHashes[0]) && SourceHash(secondSource).SequenceEqual(sourceHashes[1]),
                "Reclamation and rendering preserve both source hashes");
            File.WriteAllText(output, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Profile: {output}");
        }
        finally { heartbeat.Stop(); view.CancelAll(); host.Close(); CompleteWithDispatcher(AdaptiveMemoryController.ApplyAsync(normal)); ExperimentalMuPdfViewport.Shutdown(); }

        void WaitUntil(Func<bool> ready, string message)
        {
            var timeout = Stopwatch.StartNew();
            while (!ready() && timeout.Elapsed < TimeSpan.FromSeconds(20)) Pump(TimeSpan.FromMilliseconds(10));
            Check(ready(), message);
        }
        byte[] FrameHash()
        {
            var bitmap = new RenderTargetBitmap((int)view.Surface.ActualWidth, (int)view.Surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view.Surface); return SHA256.HashData(RegionPixels(bitmap));
        }
        void Record(string phase, object data)
        {
            var record = new { Mode = mode, Page = pageNumber, Neighbors = neighbors, Phase = phase, Data = data };
            records.Add(record); Console.WriteLine(JsonSerializer.Serialize(record));
        }
        void Snapshot(string phase, object? action = null)
        {
            using var process = Process.GetCurrentProcess();
            double parent = process.PrivateMemorySize64 / 1048576d, workers = ExperimentalMuPdfViewport.WorkerPrivateMiB;
            Record(phase, new { ParentPrivateMiB = parent, ParentWorkingSetMiB = process.WorkingSet64 / 1048576d,
                WorkerPrivateMiB = workers, CombinedPrivateMiB = parent + workers, Workers = ExperimentalMuPdfViewport.RunningWorkerCount,
                BridgeMiB = ExperimentalMuPdfViewport.CacheStats.Bytes / 1048576d, ManagedMiB = GC.GetTotalMemory(false) / 1048576d,
                Gen2Collections = GC.CollectionCount(2), Action = action });
        }
    }
}
