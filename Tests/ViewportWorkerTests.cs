using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    static async Task TestViewportWorkersAsync()
    {
        string source = CreateFixture();
        var rects = new[] { new Int32Rect(100, 90, 420, 320), new Int32Rect(520, 90, 420, 320),
            new Int32Rect(100, 410, 420, 280), new Int32Rect(520, 410, 420, 280) };
        int saved = PdfThumbnailService.ViewportRenderWorkers;
        try
        {
            Check(PdfiumPool.Count >= 2, "Native test has at least two independent PDFium instances");
            PdfThumbnailService.ViewportRenderWorkers = 1;
            var baseline = await PdfThumbnailService.RenderPageTilesStreamingAsync(source, 0, 1200, 1600,
                rects, (_, _) => Task.CompletedTask);
            PdfThumbnailService.ViewportRenderWorkers = 2;
            long loads = PdfThumbnailService.NativePageLoads;
            var delivered = new List<Int32Rect>();
            var parallel = await PdfThumbnailService.RenderPageTilesStreamingAsync(source, 0, 1200, 1600,
                rects, (rect, image) => { Check(image.IsFrozen, "Worker bitmap is immutable"); delivered.Add(rect); return Task.CompletedTask; });
            Check(delivered.Count == rects.Length && delivered.Distinct().Count() == rects.Length,
                "Shared worker queue delivers every tile exactly once");
            Check(PdfThumbnailService.NativePageLoads == loads + 1,
                "Parallel batch warms a second independent page instead of sharing its native handle");
            for (int i = 0; i < rects.Length; i++)
                Check(baseline[i] != null && parallel[i] != null &&
                    RegionPixels(baseline[i]!).SequenceEqual(RegionPixels(parallel[i]!)),
                    $"Parallel tile {i} preserves sequential pixel output and result order");
            using var cancellation = new CancellationTokenSource();
            int callbacks = 0;
            await PdfThumbnailService.RenderPageTilesStreamingAsync(source, 0, 1200, 1600,
                rects, (_, _) => { callbacks++; cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token);
            Check(callbacks == 1, "Cancellation stops further tile publication");
            Check(PdfiumPool.Instances.All(instance => instance.Load == 0), "Cancelled worker batch releases instance load");
            var recovered = await PdfThumbnailService.RenderPageTilesStreamingAsync(source, 0, 1200, 1600,
                rects, (_, _) => Task.CompletedTask);
            Check(recovered.All(image => image != null), "Workers remain usable after cancellation");
            var simultaneous = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
                PdfThumbnailService.RenderPageTilesStreamingAsync(source, 0, 1200, 1600, rects, (_, _) => Task.CompletedTask)));
            Check(simultaneous.All(batch => batch.All(image => image != null)),
                "Concurrent viewport batches safely serialize each native page context");
        }
        finally { PdfThumbnailService.ViewportRenderWorkers = saved; }
    }

    static async Task BenchmarkViewportWorkersAsync(string source, int pageNumber, int workers)
    {
        PdfThumbnailService.ViewportRenderWorkers = Math.Max(1, workers);
        int fullWidth = 6000;
        double aspect = await PdfThumbnailService.GetPageAspectRatioAsync(source, pageNumber - 1)
            ?? throw new InvalidOperationException("Could not load benchmark page");
        int fullHeight = (int)Math.Round(fullWidth * aspect);
        var target = new Int32Rect((fullWidth - 1920) / 2, Math.Max(0, (fullHeight - 1080) / 2),
            1920, Math.Min(1080, fullHeight));
        var rects = new List<Int32Rect>();
        for (int y = target.Y; y < target.Y + target.Height; y += 640)
            for (int x = target.X; x < target.X + target.Width; x += 640)
                rects.Add(ViewportRegionReuse.WithGutter(new Int32Rect(x, y,
                    Math.Min(640, target.X + target.Width - x), Math.Min(640, target.Y + target.Height - y)), fullWidth, fullHeight));
        var durations = new List<double>();
        string? pixelHash = null;
        for (int run = 0; run < 4; run++)
        {
            long pageLoads = PdfThumbnailService.NativePageLoads;
            var watch = Stopwatch.StartNew();
            double firstTile = -1;
            BitmapSource image;
            if (workers == 0)
            {
                image = await PdfThumbnailService.RenderPageTileAsync(source, pageNumber - 1, fullWidth, fullHeight, target)
                    ?? throw new InvalidOperationException("Viewport crop failed");
                firstTile = watch.Elapsed.TotalMilliseconds;
            }
            else
            {
                var images = await PdfThumbnailService.RenderPageTilesStreamingAsync(source, pageNumber - 1, fullWidth, fullHeight,
                    rects, (_, _) => { if (firstTile < 0) firstTile = watch.Elapsed.TotalMilliseconds; return Task.CompletedTask; });
                if (images.Any(bitmap => bitmap == null)) throw new InvalidOperationException("Incomplete tile batch");
                image = ViewportRegionReuse.Compose(target, rects.Select((rect, index) =>
                    new ViewportRegionReuse.Piece(rect, images[index]!)).ToArray(), default);
            }
            double elapsed = watch.Elapsed.TotalMilliseconds;
            if (run > 0) durations.Add(elapsed);
            var pixels = RegionPixels(image);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
            pixelHash ??= hash;
            Check(pixelHash == hash, "Repeated viewport renders are stable");
            Check(pixels.Where((_, index) => index % 4 < 3).Any(value => value < 230), "CAD crop is not blank");
            using var process = Process.GetCurrentProcess();
            Console.WriteLine(JsonSerializer.Serialize(new { Source = System.IO.Path.GetFileName(source), pageNumber, workers,
                Pool = PdfiumPool.Count, run, FirstTileMs = firstTile, CompositeMs = elapsed,
                PageLoads = PdfThumbnailService.NativePageLoads - pageLoads,
                PrivateMiB = process.PrivateMemorySize64 / 1048576d, PixelHash = hash }));
        }
        Console.WriteLine(JsonSerializer.Serialize(new { workers, WarmMedianMs = durations.Order().ElementAt(1) }));
    }
}
