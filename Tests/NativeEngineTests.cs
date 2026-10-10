using System.IO;
using Path = System.IO.Path;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Services.Ocr;
using XTPdfMergeApp.Services.TextEdit;

internal static partial class Program
{
    static async Task TestNativeSharedTransportAsync()
    {
        string path = CreateFixture();
        string? prior = Environment.GetEnvironmentVariable("XTPDF_MUPDF_SHARED_MEMORY");
        var cases = new[] {
            (Width: 600, Height: 800, Rect: new System.Windows.Int32Rect(13, 19, 201, 151), Alpha: false),
            (Width: 2200, Height: 0, Rect: new System.Windows.Int32Rect(0, 0, 2200, 0), Alpha: false),
            (Width: 6001, Height: 4249, Rect: new System.Windows.Int32Rect(133, 191, 1001, 701), Alpha: true),
            (Width: 128, Height: 0, Rect: new System.Windows.Int32Rect(0, 0, 128, 0), Alpha: false) };
        var baseline = new List<byte[]>();
        try
        {
            Environment.SetEnvironmentVariable("XTPDF_MUPDF_SHARED_MEMORY", "0");
            foreach (var c in cases)
            {
                var image = (await ExperimentalMuPdfViewport.RenderAsync(path, 0, c.Width, c.Height, new[] { c.Rect }, default, alpha: c.Alpha))[0]!;
                baseline.Add(RegionPixels(image));
            }
            ExperimentalMuPdfViewport.InvalidateRasterCache((p, _) => string.Equals(p, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            Environment.SetEnvironmentVariable("XTPDF_MUPDF_SHARED_MEMORY", "1");
            var previous = new List<System.Windows.Media.Imaging.BitmapSource>();
            for (int i = 0; i < cases.Length; i++)
            {
                var c = cases[i];
                var image = (await ExperimentalMuPdfViewport.RenderAsync(path, 0, c.Width, c.Height, new[] { c.Rect }, default, alpha: c.Alpha))[0]!;
                Check(image.IsFrozen && RegionPixels(image).SequenceEqual(baseline[i]), "Shared raster matches pipe pixels, including alpha and fractional crops");
                previous.Add(image);
                for (int j = 0; j < previous.Count; j++)
                    Check(RegionPixels(previous[j]).SequenceEqual(baseline[j]), "WPF retains its image when shared memory is reused or resized");
            }
            var stats = await ExperimentalMuPdfViewport.CommandAsync("", "stats", workerIndex: 0);
            Check(stats.GetProperty("sharedFrames").GetInt64() >= cases.Length, "Native worker actually uses shared transport");
            Check(stats.GetProperty("sharedBytes").GetInt64() <= 16 * 1024 * 1024, "Small frame releases an oversized shared mapping");
            Check(ExperimentalMuPdfViewport.WorkerSharedMemoryBytes >= stats.GetProperty("sharedBytes").GetInt64(),
                "Memory policy accounts for retained shared mappings");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            bool cancelled = false;
            try { await ExperimentalMuPdfViewport.RenderAsync(path, 0, 128, 0, new[] { cases[3].Rect }, cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Shared cache hits respect cancellation");
            await ExperimentalMuPdfViewport.RetireAsync(path);
            stats = await ExperimentalMuPdfViewport.CommandAsync("", "stats", workerIndex: 0);
            Check(stats.GetProperty("sharedBytes").GetInt64() == 0, "Closing a document releases the shared mapping");
            Check(ExperimentalMuPdfViewport.WorkerSharedMemoryBytes == 0, "Closing removes shared memory from app accounting");
            ExperimentalMuPdfViewport.Shutdown();
            var recovered = await ExperimentalMuPdfViewport.RenderAsync(path, 0, 128, 0, new[] { cases[3].Rect }, default);
            Check(RegionPixels(recovered[0]!).SequenceEqual(baseline[3]), "Shared transport recovers after worker restart");
            var batches = new List<List<System.Windows.Media.Imaging.BitmapSource?>>();
            foreach (string mode in new[] { "0", "1" })
            {
                Environment.SetEnvironmentVariable("XTPDF_MUPDF_SHARED_MEMORY", mode);
                ExperimentalMuPdfViewport.InvalidateRasterCache((p, _) => string.Equals(p, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                var images = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => ExperimentalMuPdfViewport.RenderAsync(
                    path, i & 1, 600 + i, 800, new[] { new System.Windows.Int32Rect(11 + i, 19, 201, 151) }, default)));
                batches.Add(images.SelectMany(b => b).ToList());
            }
            for (int i = 0; i < 16; i++)
                Check(RegionPixels(batches[0][i]!).SequenceEqual(RegionPixels(batches[1][i]!)),
                    "Concurrent foreground lanes preserve result ownership and order");
        }
        finally
        {
            Environment.SetEnvironmentVariable("XTPDF_MUPDF_SHARED_MEMORY", prior);
            await ExperimentalMuPdfViewport.RetireAsync(path);
        }
    }

    static async Task BenchmarkNativeTransportAsync(string path, int page)
    {
        string? prior = Environment.GetEnvironmentVariable("XTPDF_MUPDF_SHARED_MEMORY");
        var measurements = new List<object>();
        try
        {
            await PdfThumbnailService.GetPageSizeRangeAsync(path, page-1, 1);
            ExperimentalMuPdfViewport.DisplayWidthHint = 1200;
            foreach (int width in new[] { 4608, 8192 })
            {
                string? hash = null;
                foreach (string mode in new[] { "0", "1" })
                {
                    Environment.SetEnvironmentVariable("XTPDF_MUPDF_SHARED_MEMORY", mode);
                    var times = new List<double>();
                    long allocated = 0;
                    System.Windows.Media.Imaging.BitmapSource? last = null;
                    for (int i = 0; i < 14; i++)
                    {
                        ExperimentalMuPdfViewport.InvalidateRasterCache((p, _) => string.Equals(p, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                        long before = GC.GetTotalAllocatedBytes(true);
                        var timer = System.Diagnostics.Stopwatch.StartNew();
                        last = (await ExperimentalMuPdfViewport.RenderAsync(path, page-1, width, 0,
                            new[] { new System.Windows.Int32Rect(0, 0, width, 0) }, default))[0];
                        double ms = timer.Elapsed.TotalMilliseconds;
                        long bytes = GC.GetTotalAllocatedBytes(true) - before;
                        Check(last is { IsFrozen: true }, "Transport benchmark produces a complete immutable frame");
                        if (i >= 2) { times.Add(ms); allocated += bytes; }
                    }
                    string actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(RegionPixels(last!)));
                    Check(hash == null || hash == actual, "Pipe and shared-memory WPF frames are pixel-identical");
                    hash = actual;
                    times.Sort();
                    var result = new { Width = width, Transport = mode == "1" ? "shared" : "pipe", Samples = times.Count,
                        P50Ms = (times[5] + times[6]) / 2, P95Ms = times[11], ManagedBytesPerFrame = allocated / times.Count };
                    measurements.Add(result);
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
                }
            }
            if (page >= 2)
            {
                await PdfThumbnailService.GetPageSizeRangeAsync(path, page-2, 2);
                string[]? hashes = null;
                foreach (string mode in new[] { "0", "1" })
                {
                    Environment.SetEnvironmentVariable("XTPDF_MUPDF_SHARED_MEMORY", mode);
                    var times = new List<double>();
                    List<System.Windows.Media.Imaging.BitmapSource?>[]? last = null;
                    for (int i = 0; i < 10; i++)
                    {
                        ExperimentalMuPdfViewport.InvalidateRasterCache((p, _) => string.Equals(p, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                        var timer = System.Diagnostics.Stopwatch.StartNew();
                        last = await Task.WhenAll(Enumerable.Range(page-2, 2).Select(p => ExperimentalMuPdfViewport.RenderAsync(
                            path, p, 4608, 0, new[] { new System.Windows.Int32Rect(0, 0, 4608, 0) }, default)));
                        if (i >= 2) times.Add(timer.Elapsed.TotalMilliseconds);
                    }
                    string[] actual = last!.Select(b => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(RegionPixels(b[0]!)))).ToArray();
                    Check(hashes == null || hashes.SequenceEqual(actual), "Two concurrent lanes preserve the same page pixels");
                    hashes = actual;
                    times.Sort();
                    var result = new { Width = 4608, Frames = 2, Transport = mode == "1" ? "shared" : "pipe",
                        Samples = times.Count, P50Ms = (times[3] + times[4]) / 2, P95Ms = times[7],
                        Mode = ReaderPerformanceProfile.Current.Mode.ToString() };
                    measurements.Add(result); Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
                }
            }
            File.WriteAllText(Path.Combine(Output, "native-transport-bench.json"), System.Text.Json.JsonSerializer.Serialize(measurements));
        }
        finally { Environment.SetEnvironmentVariable("XTPDF_MUPDF_SHARED_MEMORY", prior); ExperimentalMuPdfViewport.Shutdown(); }
    }

    static async Task BenchmarkNativeCancellationAsync(string path, int page)
    {
        string? prior = Environment.GetEnvironmentVariable("XTPDF_NATIVE_CANCEL");
        var measurements = new List<object>();
        try
        {
            await PdfThumbnailService.GetPageSizeRangeAsync(path, page-1, 1);
            ExperimentalMuPdfViewport.DisplayWidthHint = 1200;
            foreach (string mode in new[] { "0", "1" })
            {
                Environment.SetEnvironmentVariable("XTPDF_NATIVE_CANCEL", mode);
                // Warm document/list and shared allocation before measuring cancellation.
                await ExperimentalMuPdfViewport.RenderAsync(path, page-1, 8192, 0,
                    new[] { new System.Windows.Int32Rect(0, 0, 8192, 0) }, default);
                var before = await ExperimentalMuPdfViewport.CommandAsync("", "stats", workerIndex: (page-1) & 1);
                var times = new List<double>();
                for (int i = 0; i < 8; i++)
                {
                    ExperimentalMuPdfViewport.InvalidateRasterCache((p, _) => string.Equals(p, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(35));
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    bool cancelled = false;
                    try { await ExperimentalMuPdfViewport.RenderAsync(path, page-1, 8192, 0,
                        new[] { new System.Windows.Int32Rect(0, 0, 8192, 0) }, cancel.Token); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled, "Cancelled heavy frame never reaches the presentation cache");
                    times.Add(watch.Elapsed.TotalMilliseconds);
                }
                var after = await ExperimentalMuPdfViewport.CommandAsync("", "stats", workerIndex: (page-1) & 1);
                long aborted = after.GetProperty("cancelledRenders").GetInt64() - before.GetProperty("cancelledRenders").GetInt64();
                Check(mode == "0" ? aborted == 0 : aborted > 0, "Native cancellation benchmark exercises the selected mode");
                times.Sort();
                var result = new { NativeCancellation = mode == "1", CancelAfterMs = 35, Samples = times.Count,
                    P50Ms = (times[3] + times[4]) / 2, P95Ms = times[7], AbortedRenders = aborted };
                measurements.Add(result); Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
            }
            File.WriteAllText(Path.Combine(Output, "native-cancel-bench.json"), System.Text.Json.JsonSerializer.Serialize(measurements));
        }
        finally { Environment.SetEnvironmentVariable("XTPDF_NATIVE_CANCEL", prior); ExperimentalMuPdfViewport.Shutdown(); }
    }

    static async Task TestNativeRenderTimingsAsync()
    {
        string path = CreateFixture();
        long prepare = RenderDiagnostics.MuPdfPrepare.Count, roundTrip = RenderDiagnostics.MuPdfRoundTrip.Count;
        long copy = RenderDiagnostics.BitmapCopy.Count, wait = RenderDiagnostics.NativeWait.Count;
        var rects = new[] { new System.Windows.Int32Rect(13, 19, 201, 151) };
        try
        {
            var first = await ExperimentalMuPdfViewport.RenderAsync(path, 0, 600, 800, rects, default);
            Check(first[0] is { IsFrozen: true, PixelWidth: 201, PixelHeight: 151 }, "Instrumented native render returns a usable image");
            Check(RenderDiagnostics.MuPdfPrepare.Count == prepare + 1 && RenderDiagnostics.MuPdfRoundTrip.Count == roundTrip + 1,
                "Native timing records preparation and complete worker round trip once");
            Check(RenderDiagnostics.BitmapCopy.Count == copy + 1 && RenderDiagnostics.NativeWait.Count >= wait + 1,
                "Native timing records WPF copy and scheduling wait");
            var cached = await ExperimentalMuPdfViewport.RenderAsync(path, 0, 600, 800, rects, default);
            Check(ReferenceEquals(first[0], cached[0]), "Replay bypasses IPC and image creation");
            Check(RenderDiagnostics.MuPdfRoundTrip.Count == roundTrip + 1 && RenderDiagnostics.BitmapCopy.Count == copy + 1,
                "Cache hits do not pollute native render/copy measurements");
        }
        finally { await ExperimentalMuPdfViewport.RetireAsync(path); }
    }

    static async Task TestNativeMetadataAsync()
    {
        Check(ExperimentalMuPdfViewport.UsesXtNativeWorker, "Metadata regression uses the native worker");
        string path = Path.Combine(Output, "native-mixed-sizes.pdf");
        using (var doc = new PdfDocument(new PdfWriter(path)))
            for (int i = 0; i < 41; i++) doc.AddNewPage(new PageSize(200 + i, 300 + i)).SetRotation(i % 2 == 0 ? 0 : 90);
        try
        {
            var count = await ExperimentalMuPdfViewport.CommandAsync(path, "metadata", data: new { countOnly = true });
            Check(count.GetProperty("count").GetInt32() == 41 && !count.TryGetProperty("sizes", out _), "Opening reads only the page count");
            var range = await ExperimentalMuPdfViewport.CommandAsync(path, "metadata", data: new { first = 17, count = 3 });
            Check(range.GetProperty("count").GetInt32() == 41 && range.GetProperty("first").GetInt32() == 17 && range.GetProperty("sizes").GetArrayLength() == 3,
                "A metadata batch retains its first index and total count");
            var sizes = await PdfThumbnailService.GetPageSizesAsync(path);
            Check(sizes?.Length == 41, "Batched metadata includes the final partial batch");
            for (int i = 0; i < 41; i++)
                Check(sizes![i] == (i % 2 == 0 ? (200d + i, 300d + i) : (300d + i, 200d + i)), "Batched page sizes preserve order and rotation");
            Check(Math.Abs((await PdfThumbnailService.GetPageAspectRatioAsync(path, 17))!.Value - 317d / 217) < .0001,
                "A single page aspect uses its actual ranged dimensions");
            var end = await PdfThumbnailService.GetPageSizeRangeAsync(path, 40, 16);
            Check(end?.Length == 1, "The final range is clamped to the document");
            bool rejected = false;
            try { await ExperimentalMuPdfViewport.CommandAsync(path, "metadata", data: new { first = -1, count = 1 }); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Invalid metadata ranges are rejected without crashing the app");
            Check((await PdfThumbnailService.GetPageSizeRangeAsync(path, 0, 1))?[0] == (200d, 300d), "Metadata recovers after a worker error");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool cancelledRange = false;
            try { await PdfThumbnailService.GetPageSizeRangeAsync(path, 0, 1, cancelled.Token); }
            catch (OperationCanceledException) { cancelledRange = true; }
            Check(cancelledRange, "Cancelled metadata requests do not return partial sizes");
        }
        finally { await ExperimentalMuPdfViewport.RetireAsync(path); }
    }

    static void TestNativeOnlyInstallation()
    {
        Check(!Directory.Exists(Path.Combine(AppContext.BaseDirectory, "MuPdfRuntime")), "This test installation contains no Python runtime");
        foreach (var script in new[] { "MuPdfWorker.py", "OcrWorker.py", "TextEditWorker.py" })
            Check(!File.Exists(Path.Combine(AppContext.BaseDirectory, script)), "No Python worker scripts are shipped");
        Check(OcrService.NativeWorker != null && OcrService.IsAvailable(), "OCR is available through native and Vietnamese tessdata");
        Check(TextEditService.NativeWorker != null && TextEditService.IsAvailable, "Text and object editing are available through native");
    }
}
