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
    static void ProfileWarmCache(string label, bool reuse, string manifest)
    {
        using var process = Process.GetCurrentProcess();
        process.PriorityClass = ProcessPriorityClass.BelowNormal;
        var source = JsonSerializer.Deserialize<RecentFile[]>(File.ReadAllText(manifest))!.Last();
        var stamp = new FileInfo(source.Path);
        long sourceLength = stamp.Length;
        DateTime sourceTime = stamp.LastWriteTimeUtc;
        var app = CreateReaderTestApplication();
        AppSettings.ApplyRuntime();
        PdfThumbnailService.StartMemoryPolicy();
        var cache = new ReaderPageRenderCache(64L * 1024 * 1024,
            (key, priority, token) => PdfThumbnailService.RenderPageAsync(key.Path, key.Page - 1, key.Width, token, priority, key.Layers),
            reuse ? ReaderPageRenderCache.PreviewBudgetBytes : 0)
            { ReuseLargerImages = reuse };
        var view = new ContinuousPdfView { PrefetchPageCount = 4, ReuseRenderedImages = reuse };
        int requests = 0;
        view.CachedPageProvider = (row, width) => cache.TryGetDisplayImage(RenderCacheKeys.ReaderPage(row.SourcePath, row.PageNumber, width));
        view.PageRenderer = (row, width, priority, token) =>
        {
            requests++;
            return cache.GetAsync(RenderCacheKeys.ReaderPage(row.SourcePath, row.PageNumber, width), priority, token);
        };
        view.CurrentPageChanged += index =>
        {
            if (index >= 0 && index < view.Pages.Count)
                PdfThumbnailService.SetHotPages(new[] { (view.Pages[index].SourcePath, view.Pages[index].PageNumber) });
        };
        var host = new Window { Title = "Background warm cache benchmark", Content = view, Width = 1280, Height = 800,
            Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize };
        app.MainWindow = host;
        var frame = new DispatcherFrame();
        Exception? failure = null;
        host.Show();
        host.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            long peak = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) => { process.Refresh(); peak = Math.Max(peak, process.PrivateMemorySize64); };
            timer.Start();
            try
            {
                var sizes = await PdfThumbnailService.GetPageSizesAsync(source.Path) ?? throw new InvalidOperationException("Cannot read PDF sizes.");
                double maxWidth = sizes.Max(s => s.Width);
                var pages = sizes.Select((s, i) => new PagePlacement { SourcePath = source.Path, PageNumber = i + 1,
                    PageWidthPoints = s.Width, PageHeightPoints = s.Height,
                    BaseWidth = PagePlacement.DefaultLayoutWidth * s.Width / maxWidth, AspectRatio = s.Height / s.Width }).ToArray();
                var buffer = Stopwatch.StartNew();
                while (PdfFileBuffer.GetLoadedFraction(source.Path) < 1 && buffer.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(50);
                if (PdfFileBuffer.GetLoadedFraction(source.Path) < 1) throw new InvalidOperationException("PDF file buffer did not finish.");
                var target = pages[Math.Min(10, pages.Length - 1)];
                double dpi = VisualTreeHelper.GetDpi(view).DpiScaleX;
                var measurements = new List<object>();
                bool allReady = true;
                var cpu = process.TotalProcessorTime;
                var start = ReaderTimingSnapshot.Read();
                await Bind(.5, "initial-fit", false);
                foreach (double zoom in new[] { 1.0, 1.25, 1.0 }) await Zoom(zoom, "first-zoom");
                for (int cycle = 1; cycle <= 3; cycle++)
                {
                    await Bind(.5, "warm-fit-" + cycle, false);
                    foreach (double zoom in new[] { 1.0, 1.25, 1.0 }) await Zoom(zoom, "warm-zoom-" + cycle);
                }
                await Bind(3, "initial-deep", true);
                for (int cycle = 1; cycle <= 3; cycle++)
                {
                    await Bind(1, "warm-rebind-normal-" + cycle, false);
                    await Bind(3, "warm-rebind-deep-" + cycle, true);
                }
                var end = ReaderTimingSnapshot.Read();
                double activeCpu = (process.TotalProcessorTime - cpu).TotalSeconds;
                process.Refresh();
                double beforeIdle = MiB(process.PrivateMemorySize64);
                await Task.Delay(25000);
                process.Refresh();
                var result = new { Label = label, ReuseRenderedImages = reuse, PdfiumInstances = PdfiumPool.Count,
                    File = Path.GetFileName(source.Path), SizeMiB = MiB(sourceLength), Pages = pages.Length, Page = target.PageNumber,
                    ViewportWidth = view.ViewportWidth, ViewportHeight = view.ViewportHeight, Dpi = dpi,
                    AllReady = allReady, PeakPrivateMiB = MiB(peak), BeforeIdlePrivateMiB = beforeIdle,
                    AfterIdlePrivateMiB = MiB(process.PrivateMemorySize64), ReaderCacheMiB = MiB(cache.Stats.Bytes),
                    RegionCacheMiB = MiB(ContinuousPdfView.CachedRegionStats.Bytes), RegionCacheEntries = ContinuousPdfView.CachedRegionStats.Count,
                    ActiveCpuSeconds = Math.Round(activeCpu, 3), ActiveDiagnostics = end.Since(start), Measurements = measurements,
                    SourcesUnchanged = new FileInfo(source.Path).Length == sourceLength && File.GetLastWriteTimeUtc(source.Path) == sourceTime };
                File.WriteAllText(Path.Combine(Output, "warm-cache-" + label + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(JsonSerializer.Serialize(result));
                if (!allReady || !result.SourcesUnchanged) throw new InvalidOperationException("Warm cache scenario failed.");

                async Task Bind(double zoom, string stage, bool region)
                {
                    await Measure(stage, zoom, region, () => { view.SetDocument(pages, zoom); view.ScrollToPage(target.PageNumber - 1); });
                }
                async Task Zoom(double zoom, string stage) => await Measure(stage, zoom, false, () => view.ZoomKeepTop(zoom));
                async Task Measure(string stage, double zoom, bool region, Action navigate)
                {
                    var before = ReaderTimingSnapshot.Read();
                    int beforeRequests = requests;
                    var watch = Stopwatch.StartNew();
                    navigate();
                    int width = ContinuousPdfView.PreferredPageBitmapWidth(target.LayoutWidth * zoom * dpi, true);
                    while (!Ready() && watch.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(16);
                    double readyMs = watch.Elapsed.TotalMilliseconds;
                    bool ready = Ready();
                    allReady &= ready;
                    // Allow queued cache hits/prefetch to finish before attributing work to the next transition.
                    await Task.Delay(200);
                    BitmapSource? sharp;
                    if (region) TryRenderedRegion(view, target, out sharp); else sharp = target.ReaderBitmap;
                    string? hash = null;
                    if (sharp != null)
                    {
                        int stride = (sharp.PixelWidth * sharp.Format.BitsPerPixel + 7) / 8;
                        byte[] pixels = new byte[stride * sharp.PixelHeight];
                        sharp.CopyPixels(pixels, stride, 0);
                        hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
                    }
                    measurements.Add(new { Stage = stage, Zoom = zoom, Ready = ready, ReadyMs = Math.Round(readyMs, 2),
                        Requests = requests - beforeRequests, PixelWidth = sharp?.PixelWidth, PixelHash = hash,
                        Diagnostics = ReaderTimingSnapshot.Read().Since(before) });
                    Console.WriteLine($"{label} {stage} zoom={zoom}: ready={ready}, {readyMs:0.00} ms");

                    bool Ready()
                    {
                        if (region) return TryRenderedRegion(view, target, out _);
                        var states = (System.Collections.IDictionary)typeof(ContinuousPdfView).GetField("_states", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                        if (!states.Contains(target) || states[target] is not { } state) return false;
                        return state.GetType().GetField("Bitmap")!.GetValue(state) is BitmapSource image && image.PixelWidth >= width;
                    }
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
    }
}
