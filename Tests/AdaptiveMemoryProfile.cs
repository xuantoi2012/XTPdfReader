using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Domain;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    // Uses fake pressure, real PDFs and an offscreen view; never consumes free RAM to force OS pressure.
    static void ProfileAdaptiveMemory(string firstSource, string secondSource)
    {
        if (Application.Current == null) CreateReaderTestApplication();
        var records = new List<object>();
        string output = Path.Combine(Output, "adaptive-memory-profile.json");
        byte[] SourceHash(string path) { using var stream = File.OpenRead(path); return SHA256.HashData(stream); }
        var before = new[] { SourceHash(firstSource), SourceHash(secondSource) };
        var sizes = ExperimentalMuPdfViewport.CommandAsync(firstSource, "metadata").GetAwaiter().GetResult().GetProperty("sizes");
        var row = new PagePlacement { SourcePath = firstSource, PageNumber = 1, BaseWidth = 1200,
            AspectRatio = sizes[0][1].GetDouble() / sizes[0][0].GetDouble() };
        var view = new ContinuousPdfView { PrefetchPageCount = 0 };
        var host = new Window { Content = view, Width = 1320, Height = 700, WindowStyle = WindowStyle.None,
            ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        view.PageRenderer = (page, width, priority, token) => PdfThumbnailService.RenderPageAsync(page.SourcePath, page.PageNumber - 1, width, token, priority);
        host.Show(); host.UpdateLayout();
        var normal = new AdaptiveMemoryDecision(MemoryPressureState.Normal, 1952 * AdaptiveMemoryPolicy.MiB, "profile baseline");
        try
        {
            CompleteWithDispatcher(AdaptiveMemoryController.ApplyAsync(normal));
            view.SetDocument(new[] { row }, 1);
            var wait = Stopwatch.StartNew();
            while (row.ReaderBitmap == null && wait.Elapsed < TimeSpan.FromSeconds(15)) Pump(TimeSpan.FromMilliseconds(25));
            Check(row.ReaderBitmap is { PixelWidth: 4608 }, "Profile starts with the accepted sharp whole-page tier");
            var visible = row.ReaderBitmap;
            foreach (var (path, page) in new[] { (firstSource, 0), (firstSource, 1), (secondSource, 0), (secondSource, 1), (secondSource, 2), (secondSource, 3) })
            {
                var timer = Stopwatch.StartNew();
                var image = PdfThumbnailService.RenderPageAsync(path, page, 1200).GetAwaiter().GetResult();
                Check(image is { IsFrozen: true, PixelWidth: 4608 }, "Profile workload produces a frozen sharp page");
                Record("page", new { File = Path.GetFileName(path), Page = page + 1, Ms = timer.Elapsed.TotalMilliseconds });
            }
            // Bring both background lanes into the measurement with uncached small previews.
            Task.WhenAll(Enumerable.Range(0, 2).Select(page => PdfThumbnailService.RenderPageAsync(secondSource, page, 341,
                priority: PdfRenderPriority.Background))).GetAwaiter().GetResult();
            Pump(TimeSpan.FromMilliseconds(200));
            var frame = FrameHash();
            Snapshot("normal");
            Pump(TimeSpan.FromMilliseconds(2200));
            CompleteWithDispatcher(AdaptiveMemoryController.ApplyAsync(new(MemoryPressureState.Pressure, 512 * AdaptiveMemoryPolicy.MiB, "injected pressure")));
            Pump(TimeSpan.FromMilliseconds(100));
            Check(FrameHash().SequenceEqual(frame) && ReferenceEquals(row.ReaderBitmap, visible), "Pressure leaves the real displayed frame unchanged");
            Snapshot("pressure");
            CompleteWithDispatcher(AdaptiveMemoryController.ApplyAsync(new(MemoryPressureState.Critical, AdaptiveMemoryPolicy.Floor, "injected critical")));
            Pump(TimeSpan.FromMilliseconds(10200));
            CompleteWithDispatcher(AdaptiveMemoryController.ApplyAsync(new(MemoryPressureState.Critical, AdaptiveMemoryPolicy.Floor, "injected critical")));
            Check(FrameHash().SequenceEqual(frame) && ReferenceEquals(row.ReaderBitmap, visible), "Critical trimming leaves the real displayed frame unchanged");
            Check(ExperimentalMuPdfViewport.RunningBackgroundWorkerCount == 0, "Heavy-file critical profile retires idle background workers");
            Snapshot("critical");
            var revisit = Stopwatch.StartNew();
            var cached = PdfThumbnailService.RenderPageAsync(firstSource, 0, 1100).GetAwaiter().GetResult();
            Check(ReferenceEquals(cached, visible), "Protected real page revisit is still a cache hit under critical pressure");
            Record("critical-revisit", new { Ms = revisit.Elapsed.TotalMilliseconds, CacheHit = true });
            int workersBeforeRecovery = ExperimentalMuPdfViewport.RunningWorkerCount;
            CompleteWithDispatcher(AdaptiveMemoryController.ApplyAsync(new(MemoryPressureState.Normal, 320 * AdaptiveMemoryPolicy.MiB, "gradual recovery")));
            Check(ExperimentalMuPdfViewport.RunningWorkerCount == workersBeforeRecovery, "Recovery restores capacity without starting idle workers");
            foreach (double zoom in new[] { 1.1, 1.5, 2.0, 3.0, 1.0 })
            {
                view.ZoomAt(zoom, new Point(600, 300)); Pump(TimeSpan.FromMilliseconds(120));
                Check(ReferenceEquals(row.ReaderBitmap, visible), "Real nearby zoom retains the original sharp full-page bitmap");
            }
            view.ZoomAt(6, new Point(600, 300));
            wait.Restart();
            while (view.MemoryStats.Regions == 0 && wait.Elapsed < TimeSpan.FromSeconds(15)) Pump(TimeSpan.FromMilliseconds(25));
            Check(view.MemoryStats.Regions > 0, "Deep zoom still produces a real sharp crop after pressure and recovery");
            view.ScrollBy(100, 0); Pump(TimeSpan.FromMilliseconds(250));
            Check(view.MemoryStats.Regions > 0 && ReferenceEquals(row.ReaderBitmap, visible), "Pan preserves the sharp crop and its whole-page fallback");
            Snapshot("recovery-deep-zoom-pan");
            Check(SourceHash(firstSource).SequenceEqual(before[0]) && SourceHash(secondSource).SequenceEqual(before[1]), "Pressure profile preserves both source-file hashes");
            File.WriteAllText(output, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Profile: {output}");
        }
        finally { view.CancelAll(); host.Close(); CompleteWithDispatcher(AdaptiveMemoryController.ApplyAsync(normal)); ExperimentalMuPdfViewport.Shutdown(); }

        byte[] FrameHash()
        {
            var bitmap = new RenderTargetBitmap((int)view.Surface.ActualWidth, (int)view.Surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view.Surface);
            return SHA256.HashData(RegionPixels(bitmap));
        }
        void Record(string phase, object data)
        {
            var record = new { Phase = phase, Data = data }; records.Add(record); Console.WriteLine(JsonSerializer.Serialize(record));
        }
        void Snapshot(string phase)
        {
            using var process = Process.GetCurrentProcess();
            Record(phase, new { ParentPrivateMiB = process.PrivateMemorySize64 / 1048576d, ParentWorkingSetMiB = process.WorkingSet64 / 1048576d,
                WorkerPrivateMiB = ExperimentalMuPdfViewport.WorkerPrivateMiB, Workers = ExperimentalMuPdfViewport.RunningWorkerCount,
                BackgroundWorkers = ExperimentalMuPdfViewport.RunningBackgroundWorkerCount, BridgeMiB = ExperimentalMuPdfViewport.CacheStats.Bytes / 1048576d,
                BridgeBudgetMiB = ExperimentalMuPdfViewport.CurrentCacheBudget / 1048576d, LiveRegions = view.MemoryStats.Regions });
        }
    }
}
