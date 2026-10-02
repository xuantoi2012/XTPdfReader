using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XTPdfMergeApp;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Domain;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    static void ProfileRegionPan(string label, bool reuse, int sourceIndex, string manifest, bool quick = false, bool qualityEveryStep = false)
    {
        using var process = Process.GetCurrentProcess();
        process.PriorityClass = ProcessPriorityClass.BelowNormal;
        var source = JsonSerializer.Deserialize<RecentFile[]>(File.ReadAllText(manifest))![sourceIndex];
        string sourceHash;
        using (var input = File.OpenRead(source.Path)) sourceHash = Convert.ToHexString(SHA256.HashData(input));
        var app = CreateReaderTestApplication();
        AppSettings.ApplyRuntime(); PdfThumbnailService.StartMemoryPolicy();
        var cache = new ReaderPageRenderCache(64L * 1024 * 1024, (key, priority, token) =>
            PdfThumbnailService.RenderPageAsync(key.Path, key.Page - 1, key.Width, token, priority, key.Layers));
        var view = new ContinuousPdfView { PrefetchPageCount = 0, ReuseRegionOverlap = reuse };
        view.PageRenderer = (row, width, priority, token) => cache.GetAsync(RenderCacheKeys.ReaderPage(row.SourcePath, row.PageNumber, width), priority, token);
        long nativePixels = 0; int nativePasses = 0; double nativeMs = 0;
        var nativeRectangles = new List<Int32Rect[]>();
        view.RegionRenderer = async (row, width, height, rects, token, layers) =>
        {
            nativePixels += rects.Sum(r => (long)r.Width * r.Height); nativePasses += rects.Count;
            nativeRectangles.Add(rects.ToArray());
            var watch = Stopwatch.StartNew();
            var images = await PdfThumbnailService.RenderPageTilesBatchAsync(row.SourcePath, row.PageNumber - 1, width, height, rects, token, layers);
            nativeMs += watch.Elapsed.TotalMilliseconds; return images;
        };
        var host = new Window { Content = view, Width = 1280, Height = 800, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize };
        app.MainWindow = host;
        var frame = new DispatcherFrame(); Exception? failure = null;
        host.Show(); host.UpdateLayout();
        host.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            long peak = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) => { process.Refresh(); peak = Math.Max(peak, process.PrivateMemorySize64); };
            try
            {
                var sizes = await PdfThumbnailService.GetPageSizesAsync(source.Path) ?? throw new Exception("Cannot read source");
                int page = Math.Min(10, sizes.Length - 1);
                var size = sizes[page];
                var row = new PagePlacement { SourcePath = source.Path, PageNumber = page + 1, BaseWidth = 1200,
                    AspectRatio = size.Height / size.Width, PageWidthPoints = size.Width, PageHeightPoints = size.Height };
                var bufferWait = Stopwatch.StartNew();
                while (PdfFileBuffer.GetLoadedFraction(source.Path) < 1 && bufferWait.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(50);
                timer.Start();
                view.SetDocument(new[] { row }, 3);
                await WaitReady(row);
                while (row.ReaderBitmap == null) await Task.Delay(8);
                await Task.Delay(200);
                nativePixels = 0; nativePasses = 0; nativeMs = 0;
                var cpu = process.TotalProcessorTime;
                var measurements = new List<object>();
                var quality = new List<int>();
                var steps = Enumerable.Repeat((X: 80d, Y: 180d), 6)
                    .Concat(Enumerable.Repeat((X: -80d, Y: -180d), 6))
                    .Concat(Enumerable.Repeat((X: -160d, Y: 0d), 3)).ToArray();
                for (int i = 0; i < steps.Length; i++)
                {
                    long beforePixels = nativePixels; int beforePasses = nativePasses;
                    var watch = Stopwatch.StartNew();
                    view.ScrollBy(steps[i].X, steps[i].Y);
                    await WaitReady(row);
                    measurements.Add(new { Step = i, ReadyMs = watch.Elapsed.TotalMilliseconds,
                        NativePixels = nativePixels - beforePixels, NativePasses = nativePasses - beforePasses });
                    if (qualityEveryStep) quality.Add(await VerifyPanPixelsAsync(view, row));
                    await Task.Delay(60);
                }
                double cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds;
                timer.Stop(); process.Refresh(); peak = Math.Max(peak, process.PrivateMemorySize64);
                double beforeIdle = process.PrivateMemorySize64 / 1048576d;
                await Task.Delay(quick ? 0 : 25000); process.Refresh();
                double afterIdle = process.PrivateMemorySize64 / 1048576d;
                if (!TryRenderedRegion(view, row, out var bitmap)) throw new Exception("Missing final region");
                var states = (System.Collections.IDictionary)typeof(ContinuousPdfView).GetField("_states", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                var state = states[row]!;
                var regions = (System.Collections.IEnumerable)state.GetType().GetField("Regions")!.GetValue(state)!;
                var region = regions.Cast<object>().Single(r => ReferenceEquals(r.GetType().GetProperty("Bitmap")!.GetValue(r), bitmap));
                var key = region.GetType().GetProperty("Key")!.GetValue(region)!;
                int Field(string name) => (int)key.GetType().GetProperty(name)!.GetValue(key)!;
                var rect = new Int32Rect(Field("X"), Field("Y"), Field("Width"), Field("Height"));
                var reference = await PdfThumbnailService.RenderPageTileAsync(source.Path, page, Field("FullWidth"), Field("FullHeight"), rect)
                    ?? throw new Exception("Quality reference failed");
                var actualPixels = RegionPixels(bitmap!); var referencePixels = RegionPixels(reference);
                view.TryGetPageRect(row, out var pageRect);
                var visible = Rect.Intersect(pageRect, new Rect(0, 0, view.Surface.ActualWidth, view.Surface.ActualHeight));
                var visiblePixels = new Int32Rect(
                    Math.Max(0, (int)Math.Floor((visible.Left - pageRect.Left) / pageRect.Width * Field("FullWidth")) - rect.X),
                    Math.Max(0, (int)Math.Floor((visible.Top - pageRect.Top) / pageRect.Height * Field("FullHeight")) - rect.Y),
                    (int)Math.Ceiling(visible.Width / pageRect.Width * Field("FullWidth")),
                    (int)Math.Ceiling(visible.Height / pageRect.Height * Field("FullHeight")));
                int maxDifference = 0, maxVisibleDifference = 0, changed = 0;
                var significant = new List<int>();
                for (int i = 0; i < actualPixels.Length; i += 4)
                {
                    bool differs = false;
                    for (int c = 0; c < 4; c++)
                    {
                        int delta = Math.Abs(actualPixels[i + c] - referencePixels[i + c]);
                        maxDifference = Math.Max(maxDifference, delta); differs |= delta != 0;
                        int x = i / 4 % rect.Width, y = i / 4 / rect.Width;
                        if (x >= visiblePixels.X && x < visiblePixels.X + visiblePixels.Width &&
                            y >= visiblePixels.Y && y < visiblePixels.Y + visiblePixels.Height)
                            maxVisibleDifference = Math.Max(maxVisibleDifference, delta);
                    }
                    if (differs) changed++;
                    if (Enumerable.Range(0, 4).Any(c => Math.Abs(actualPixels[i + c] - referencePixels[i + c]) > 3)) significant.Add(i / 4);
                }
                if (significant.Count > 0)
                {
                    Console.WriteLine($"Significant pixels {significant.Count}, crop {rect}, bounds " +
                        $"{significant.Min(i => i % rect.Width)},{significant.Min(i => i / rect.Width)}.." +
                        $"{significant.Max(i => i % rect.Width)},{significant.Max(i => i / rect.Width)}");
                    Console.WriteLine("Columns: " + string.Join(", ", significant.GroupBy(i => i % rect.Width).OrderByDescending(g => g.Count()).Take(12).Select(g => $"{g.Key}:{g.Count()}")));
                    Console.WriteLine("Rows: " + string.Join(", ", significant.GroupBy(i => i / rect.Width).OrderByDescending(g => g.Count()).Take(12).Select(g => $"{g.Key}:{g.Count()}")));
                    Console.WriteLine($"Visible {visiblePixels}, maximum difference {maxVisibleDifference}");
                    Console.WriteLine("Native crops: " + string.Join("; ", nativeRectangles.Select(batch => string.Join(" / ", batch))));
                    Console.WriteLine("Visible differences: " + string.Join("; ", significant.Where(i =>
                        i % rect.Width >= visiblePixels.X && i % rect.Width < visiblePixels.X + visiblePixels.Width &&
                        i / rect.Width >= visiblePixels.Y && i / rect.Width < visiblePixels.Y + visiblePixels.Height).Take(15)
                        .Select(i => $"{i % rect.Width},{i / rect.Width}:{actualPixels[i * 4]}/{referencePixels[i * 4]}")));
                    foreach (var entry in new[] { ("actual", bitmap!), ("reference", reference) })
                    {
                        using var output = File.Create(Path.Combine(Output, "region-pan-" + label + "-" + entry.Item1 + ".png"));
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(entry.Item2)); encoder.Save(output);
                    }
                }
                string finalHash;
                using (var input = File.OpenRead(source.Path)) finalHash = Convert.ToHexString(SHA256.HashData(input));
                var result = new { Label = label, Quick = quick, QualityEveryStep = qualityEveryStep, StepPixelDifferences = quality,
                    ReuseOverlap = reuse, SourceIndex = sourceIndex, File = Path.GetFileName(source.Path),
                    Page = page + 1, ViewportWidth = view.ViewportWidth, Height = view.Surface.ActualHeight,
                    NativePixels = nativePixels, NativePasses = nativePasses, NativeMs = nativeMs, ActiveCpuSeconds = cpuSeconds,
                    PeakPrivateMiB = peak / 1048576d, BeforeIdlePrivateMiB = beforeIdle, AfterIdlePrivateMiB = afterIdle,
                    RegionCacheMiB = ContinuousPdfView.CachedRegionStats.Bytes / 1048576d,
                    MaxChannelDifference = maxDifference, ChangedPixelPercent = changed * 100d / (actualPixels.Length / 4),
                    MaxVisibleChannelDifference = maxVisibleDifference,
                    SourcesUnchanged = sourceHash == finalHash, Measurements = measurements };
                File.WriteAllText(Path.Combine(Output, "region-pan-" + label + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(JsonSerializer.Serialize(result));
                if (!result.SourcesUnchanged || maxVisibleDifference > 3) throw new Exception("Source or visible pixel quality check failed");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                timer.Stop(); view.SetDocument(null, 1); cache.Clear(); ContinuousPdfView.InvalidateCachedRegions((_, _) => true);
                host.Close(); PdfThumbnailService.PrepareForShutdown(TimeSpan.FromSeconds(10)); frame.Continue = false;
            }
        }));
        Dispatcher.PushFrame(frame);
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

        async Task WaitReady(PagePlacement row)
        {
            var watch = Stopwatch.StartNew();
            while (!TryRenderedRegion(view, row, out _) && watch.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(4);
            if (!TryRenderedRegion(view, row, out _)) throw new TimeoutException("Viewport did not become sharp");
        }
    }

    static async Task<int> VerifyPanPixelsAsync(ContinuousPdfView view, PagePlacement row)
    {
        if (!TryRenderedRegion(view, row, out var bitmap)) throw new Exception("Missing quality frame");
        var states = (System.Collections.IDictionary)typeof(ContinuousPdfView).GetField("_states", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        var state = states[row]!;
        var regions = (System.Collections.IEnumerable)state.GetType().GetField("Regions")!.GetValue(state)!;
        var region = regions.Cast<object>().Single(r => ReferenceEquals(r.GetType().GetProperty("Bitmap")!.GetValue(r), bitmap));
        var key = region.GetType().GetProperty("Key")!.GetValue(region)!;
        int Field(string name) => (int)key.GetType().GetProperty(name)!.GetValue(key)!;
        var rect = new Int32Rect(Field("X"), Field("Y"), Field("Width"), Field("Height"));
        var reference = await PdfThumbnailService.RenderPageTileAsync(row.SourcePath, row.PageNumber - 1,
            Field("FullWidth"), Field("FullHeight"), rect) ?? throw new Exception("Quality reference failed");
        view.TryGetPageRect(row, out var pageRect);
        var visible = Rect.Intersect(pageRect, new Rect(0, 0, view.Surface.ActualWidth, view.Surface.ActualHeight));
        int left = Math.Max(0, (int)Math.Floor((visible.Left - pageRect.Left) / pageRect.Width * Field("FullWidth")) - rect.X);
        int top = Math.Max(0, (int)Math.Floor((visible.Top - pageRect.Top) / pageRect.Height * Field("FullHeight")) - rect.Y);
        int right = Math.Min(rect.Width, (int)Math.Ceiling((visible.Right - pageRect.Left) / pageRect.Width * Field("FullWidth")) - rect.X);
        int bottom = Math.Min(rect.Height, (int)Math.Ceiling((visible.Bottom - pageRect.Top) / pageRect.Height * Field("FullHeight")) - rect.Y);
        var actual = RegionPixels(bitmap!); var expected = RegionPixels(reference);
        int maximum = 0;
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
                for (int c = 0; c < 4; c++)
                {
                    int i = (y * rect.Width + x) * 4 + c;
                    maximum = Math.Max(maximum, Math.Abs(actual[i] - expected[i]));
                }
        Console.WriteLine($"Pan quality crop {rect}: visible maximum channel difference {maximum}");
        if (maximum > 3) throw new Exception("Visible pixel quality failed during pan");
        return maximum;
    }
}
