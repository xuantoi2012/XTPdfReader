using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Domain;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    static void ProfileWideScrollMemory(string source, string label, int pageCount, bool readerCache = false, ReaderPerformanceMode? mode = null)
    {
        if (Application.Current == null) CreateReaderTestApplication();
        AppSettings.ApplyRuntime();
        if (mode is { } selected) ReaderPerformanceProfile.Apply(selected);
        byte[] Hash() { using var stream = File.OpenRead(source); return SHA256.HashData(stream); }
        var hash = Hash();
        var sizes = ExperimentalMuPdfViewport.CommandAsync(source, "metadata").GetAwaiter().GetResult().GetProperty("sizes");
        var rows = Enumerable.Range(0, sizes.GetArrayLength()).Select(i => new PagePlacement
        {
            SourcePath = source, PageNumber = i + 1, BaseWidth = 1200,
            AspectRatio = sizes[i][1].GetDouble() / sizes[i][0].GetDouble()
        }).ToArray();
        pageCount = Math.Min(pageCount, rows.Length);
        if (pageCount < 31) throw new ArgumentOutOfRangeException(nameof(pageCount), "Profile needs at least 31 pages");
        var view = new ContinuousPdfView { PrefetchPageCount = 4 };
        view.PageRenderer = (row, width, priority, token) => PdfThumbnailService.RenderPageAsync(row.SourcePath, row.PageNumber - 1, width, token, priority);
        if (readerCache)
        {
            // Exercise the same shared cache owned by the production ReaderWindow.
            var cache = (ReaderPageRenderCache)typeof(XTPdfMergeApp.ReaderWindow).GetField("_readerCache", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            view.CachedPageProvider = (row, width) => cache.TryGetDisplayImage(RenderCacheKeys.ReaderPage(row.SourcePath, row.PageNumber, width));
            view.PageRenderer = (row, width, priority, token) => cache.GetAsync(RenderCacheKeys.ReaderPage(row.SourcePath, row.PageNumber, width), priority, token);
        }
        var host = new Window { Content = view, Width = 1320, Height = 700, ShowActivated = false,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None, Left = -32000, Top = -32000 };
        var samples = new List<object>(); var latencies = new List<double>();
        double peak = 0, maxGap = 0; long previousTick = Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => { maxGap = Math.Max(maxGap, Stopwatch.GetElapsedTime(previousTick).TotalMilliseconds);
            previousTick = Stopwatch.GetTimestamp(); peak = Math.Max(peak, Private()); };
        try
        {
            host.Show(); host.UpdateLayout(); AdaptiveMemoryController.Start(); timer.Start();
            view.SetDocument(rows, 1); Ready(0); Sample("opened", 1);
            for (int i = 0; i < pageCount; i++)
            {
                // UserScroll exercises the real settling/cancellation path, including scrollbar-sized jumps.
                view.ScrollBy(0, i == 0 ? 0 : 1200 * (rows[i - 1].AspectRatio ?? 1) + 24);
                Pump(TimeSpan.FromMilliseconds(120));
                if (i % 20 == 19) { view.ScrollToPage(i); Ready(i); Sample("forward", i + 1); }
            }
            for (int i = pageCount - 1; i >= 0; i -= 10)
            {
                view.ScrollToPage(i); Ready(i); Pump(TimeSpan.FromMilliseconds(100));
                Sample("reverse", i + 1);
            }
            view.ScrollToPage(30); Ready(30);
            var sharp = rows[30].ReaderBitmap;
            foreach (double zoom in new[] { 1.5, 2.0, 3.0, 1.0 })
            { view.ZoomAt(zoom, new Point(600, 300)); Pump(TimeSpan.FromMilliseconds(100));
                Check(ReferenceEquals(sharp, rows[30].ReaderBitmap), "Nearby zoom preserves the sharp image"); }
            var watch = Stopwatch.StartNew(); view.ZoomAt(6, new Point(600, 300));
            Wait(() => view.MemoryStats.Regions > 0, "Deep zoom gets a sharp region");
            double deepMs = watch.Elapsed.TotalMilliseconds;
            view.ScrollBy(100, 0); Pump(TimeSpan.FromMilliseconds(300));
            Check(view.MemoryStats.Regions > 0, "Pan preserves sharp regions");
            view.ZoomAt(1, new Point(600, 300)); Pump(TimeSpan.FromMilliseconds(500));
            var pixelsBeforeIdle = SHA256.HashData(RegionPixels(rows[30].ReaderBitmap!));
            Sample("before-idle", 31); Pump(TimeSpan.FromSeconds(12)); Sample("idle-12s", 31);
            Check(SHA256.HashData(RegionPixels(rows[30].ReaderBitmap!)).SequenceEqual(pixelsBeforeIdle), "Idle reclamation preserves sharp page pixels");
            Check(Hash().SequenceEqual(hash), "Wide scrolling does not modify the source PDF");
            var summary = new { Label = label, Source = source, Pages = pageCount, Mode = ReaderPerformanceProfile.Current.Mode.ToString(), ReaderCache = readerCache, PeakPrivateMiB = peak,
                FinalPrivateMiB = Private(), DeepZoomMs = deepMs, SharpArrivalMedianMs = latencies.Order().ElementAt(latencies.Count / 2),
                SharpArrivalMaxMs = latencies.Max(), DispatcherMaxGapMs = maxGap, RasterReservationPeakMiB = ExperimentalMuPdfViewport.RasterMemoryStats.Peak / 1048576d,
                RecycledWorkers = ExperimentalMuPdfViewport.RecycledWorkerCount, Policy = AdaptiveMemoryController.Describe(), Samples = samples };
            string output = Path.Combine(Output, $"wide-scroll-{label}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(output, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(summary)); Console.WriteLine("Profile: " + output);
        }
        finally { timer.Stop(); AdaptiveMemoryController.Stop(); view.CancelAll(); host.Close(); ExperimentalMuPdfViewport.Shutdown(); }
        double Private() { using var process = Process.GetCurrentProcess(); return process.PrivateMemorySize64 / 1048576d + ExperimentalMuPdfViewport.WorkerPrivateMiB; }
        void Wait(Func<bool> predicate, string message)
        { var watch = Stopwatch.StartNew(); while (!predicate() && watch.Elapsed < TimeSpan.FromSeconds(30)) Pump(TimeSpan.FromMilliseconds(10)); Check(predicate(), message); }
        void Ready(int index)
        { var watch = Stopwatch.StartNew(); Wait(() => rows[index].ReaderBitmap is { PixelWidth: >= 4608 }, "Sharp page " + (index + 1)); latencies.Add(watch.Elapsed.TotalMilliseconds); }
        void Sample(string phase, int page)
        { var sample = new { Phase = phase, Page = page, PrivateMiB = Private(), BridgeMiB = ExperimentalMuPdfViewport.CacheStats.Bytes / 1048576d,
            Workers = ExperimentalMuPdfViewport.RunningWorkerCount, Policy = AdaptiveMemoryController.Describe() };
            samples.Add(sample); Console.WriteLine(JsonSerializer.Serialize(sample)); }
    }
}
