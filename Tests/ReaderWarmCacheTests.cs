using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Domain;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    static async Task TestReaderLargerCacheAsync()
    {
        int calls = 0;
        var cache = new ReaderPageRenderCache(16 * 1024 * 1024, (key, _, _) =>
        {
            calls++;
            var bitmap = Bitmap(key.Width, 64); bitmap.Freeze();
            return Task.FromResult<BitmapSource?>(bitmap);
        });
        var key = (Path: "larger-cache-fixture.pdf", Page: 1, Width: 2048, Layers: "");
        var sharp = await cache.GetAsync(key, PdfRenderPriority.Visible, default).ConfigureAwait(false);
        Check(ReferenceEquals(cache.TryGet(key with { Width = 1024 }), sharp),
            "Warm lookup can immediately retrieve a larger cached page without a render task");
        Check(ReferenceEquals(await cache.GetAsync(key with { Width = 1280 }, PdfRenderPriority.Visible, default).ConfigureAwait(false), sharp) && calls == 1,
            "A larger cached page satisfies a lower-resolution request without native work");
        await cache.GetAsync(key with { Width = 2304 }, PdfRenderPriority.Visible, default).ConfigureAwait(false);
        Check(calls == 2, "A smaller image cannot satisfy a sharper page request");
        await cache.GetAsync(key with { Width = 1024, Layers = "changed" }, PdfRenderPriority.Visible, default).ConfigureAwait(false);
        Check(calls == 3, "Warm page reuse never crosses layer versions");
        cache.Invalidate(k => k.Path == key.Path && k.Page == key.Page);
        Check(cache.TryGet(key with { Width = 512 }) == null && cache.Stats.Bytes == 0,
            "Source invalidation removes every retained resolution of the page");
    }

    /// <summary>Cross-fade: a sharper page image replacing an adequate one blends in instead of switching in one frame; 0 ms disables it.</summary>
    static void TestCrossFade()
    {
        if (Application.Current == null) CreateReaderTestApplication();
        int saved = ContinuousPdfView.CrossFadeMilliseconds;
        try
        {
            var (blended0, final0) = Run(0);
            var (blended100, final100) = Run(100);
            Check(blended0 == 0 && final0, "With cross-fade disabled the sharper image replaces the old one in a single step");
            Check(blended100 >= 1 && final100, $"A sharper page image fades in over the old one (blended frames={blended100}) and ends fully on the new image");
        }
        finally { ContinuousPdfView.CrossFadeMilliseconds = saved; }

        (int Blended, bool Final) Run(int fadeMs)
        {
            ContinuousPdfView.CrossFadeMilliseconds = fadeMs;
            var view = new ContinuousPdfView { PrefetchPageCount = 0 };
            var host = new Window { Content = view, Width = 1320, Height = 700, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
            host.Show(); host.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(60));
            try
            {
                static BitmapSource Solid(int width, byte b, byte g, byte r)
                {
                    var data = new byte[width * (width * 2 / 5) * 4];
                    for (int i = 0; i < data.Length; i += 4) { data[i] = b; data[i + 1] = g; data[i + 2] = r; data[i + 3] = 255; }
                    var bmp = BitmapSource.Create(width, width * 2 / 5, 96, 96, PixelFormats.Bgra32, null, data, width * 4); bmp.Freeze();
                    return bmp;
                }
                var red = Solid(1024, 0, 0, 255); var blue = Solid(2304, 255, 0, 0);
                var sharp = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var row = new PagePlacement { SourcePath = "crossfade-fixture.pdf", PageNumber = 1, BaseWidth = 1000, AspectRatio = .4 };
                view.PageRenderer = (_, width, _, token) => width <= 1280 ? Task.FromResult<BitmapSource?>(red) : sharp.Task.WaitAsync(token);
                view.SetDocument(new[] { row }, 1);
                Pump(TimeSpan.FromMilliseconds(150));
                view.ZoomKeepTop(2);
                Pump(TimeSpan.FromMilliseconds(150)); // the sharper request is now waiting
                sharp.SetResult(blue);
                int blended = 0; byte[] last = new byte[4];
                for (int i = 0; i < 25; i++)
                {
                    Pump(TimeSpan.FromMilliseconds(16));
                    var frame = new RenderTargetBitmap((int)view.Surface.ActualWidth, (int)view.Surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    frame.Render(view.Surface);
                    view.TryGetPageRect(row, out var rect);
                    var visible = Rect.Intersect(rect, new Rect(0, 0, view.Surface.ActualWidth, view.Surface.ActualHeight));
                    frame.CopyPixels(new Int32Rect((int)(visible.X + visible.Width / 2), (int)(visible.Y + visible.Height / 2), 1, 1), last, 4, 0);
                    if (last[0] > 25 && last[2] > 25) blended++;
                }
                return (blended, last[0] > 240 && last[2] < 15);
            }
            finally { view.CancelAll(); host.Close(); }
        }
    }

    static void TestReaderWarmImages()
    {
        if (Application.Current == null) CreateReaderTestApplication();
        var view = new ContinuousPdfView { PrefetchPageCount = 0 };
        var host = new Window { Content = view, Width = 1320, Height = 700, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        host.Show(); host.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(60));
        try
        {
            var pixels = new byte[640 * 256 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            { pixels[i] = 180; pixels[i + 1] = 80; pixels[i + 2] = 20; pixels[i + 3] = 255; }
            var warm = BitmapSource.Create(640, 256, 96, 96, PixelFormats.Bgra32, null, pixels, 640 * 4); warm.Freeze();
            var row = new PagePlacement { SourcePath = "warm-zoom-fixture.pdf", PageNumber = 1, BaseWidth = 1000, AspectRatio = .4 };
            var refinement = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            int requests = 0;
            view.PageRenderer = (r, width, priority, token) =>
            {
                requests++;
                return width <= 768 ? Task.FromResult<BitmapSource?>(warm) : refinement.Task.WaitAsync(token); // 768 = 500 px needed x 1.25 headroom, rounded up; the 640 px warm bitmap answers it
            };
            view.SetDocument(new[] { row }, .5);
            Pump(TimeSpan.FromMilliseconds(80));
            Check(row.ReaderBitmap?.PixelWidth == 640, "Small-zoom fixture starts with its actual screen-resolution page");
            view.ZoomKeepTop(1);
            var refinementWait = Stopwatch.StartNew();
            while (requests < 2 && refinementWait.Elapsed < TimeSpan.FromSeconds(2)) Pump(TimeSpan.FromMilliseconds(20));
            var frame = new RenderTargetBitmap((int)view.Surface.ActualWidth, (int)view.Surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            frame.Render(view.Surface);
            view.TryGetPageRect(row, out var rect);
            var pixel = new byte[4];
            frame.CopyPixels(new Int32Rect((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2), 1, 1), pixel, 4, 0);
            Check(requests >= 2 && pixel[0] > 150 && pixel[1] < 120 && pixel[2] < 60,
                $"Zoom keeps the already-rendered 640px page visible while sharp refinement is delayed (requests={requests}, BGR={pixel[0]},{pixel[1]},{pixel[2]})");
            requests = 0;
            view.SetDocument(new[] { row }, .5);
            Pump(TimeSpan.FromMilliseconds(80));
            Check(requests == 0, "Returning to a page immediately reuses its retained adequate image");
            row.ReaderBitmap = null;
            view.CachedPageProvider = (_, _) => warm;
            view.SetDocument(new[] { row }, .5);
            Pump(TimeSpan.FromMilliseconds(80));
            Check(requests == 0, "A warm cache can hydrate a page whose weak placement image has been released");
            var small = new TransformedBitmap(warm, new ScaleTransform(.8, .8)); small.Freeze();
            row.ReaderBitmap = null;
            view.CachedPageProvider = (_, _) => small;
            view.SetDocument(new[] { row }, 1);
            Pump(TimeSpan.FromMilliseconds(80));
            var previewFrame = new RenderTargetBitmap((int)view.Surface.ActualWidth, (int)view.Surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            previewFrame.Render(view.Surface);
            view.TryGetPageRect(row, out var previewRect);
            previewFrame.CopyPixels(new Int32Rect((int)(previewRect.X + previewRect.Width / 2), (int)(previewRect.Y + previewRect.Height / 2), 1, 1), pixel, 4, 0);
            Check(small.PixelWidth == 512 && pixel[0] > 150 && pixel[1] < 120 && pixel[2] < 60,
                $"A bounded 512px cache preview paints actual page pixels while full-resolution refinement is delayed (width={small.PixelWidth}, BGR={pixel[0]},{pixel[1]},{pixel[2]})");
            view.CachedPageProvider = null;

            string source = System.IO.Path.Combine(Output, "warm-region-fixture.pdf");
            using (var pdf = new PdfDocument(new PdfWriter(source)))
            {
                var page = pdf.AddNewPage(new PageSize(500, 200));
                new PdfCanvas(page).SetFillColor(new iText.Kernel.Colors.DeviceRgb(20, 80, 180)).Rectangle(0, 0, 500, 200).Fill();
            }
            var full = Bitmap(1280, 512); full.Freeze();
            view.PageRenderer = (_, _, _, _) => Task.FromResult<BitmapSource?>(full);
            var regionPage = new PagePlacement { SourcePath = source, PageNumber = 1, BaseWidth = 1200, AspectRatio = .4 };
            var otherPage = new PagePlacement { SourcePath = "other-warm-fixture.pdf", PageNumber = 1, BaseWidth = 1200, AspectRatio = .4 };
            ContinuousPdfView.InvalidateCachedRegions((_, _) => true);
            view.SetDocument(new[] { regionPage }, 3);
            WaitRegion(regionPage);
            TryRenderedRegion(view, regionPage, out var original);
            long raster = RenderDiagnostics.RasterSlice.Count;
            Check(original != null && ContinuousPdfView.CachedRegionStats.Count == 1 &&
                ContinuousPdfView.CachedRegionStats.Bytes <= ContinuousPdfView.RegionCacheBudgetBytes,
                "Sharp viewport regions have bounded cache ownership after rendering");
            Check(view.MemoryStats.RegionBytes == ContinuousPdfView.CachedRegionStats.Bytes,
                "Diagnostics count shared live and cached region storage only once");
            view.SetDocument(new[] { otherPage }, 1);
            Pump(TimeSpan.FromMilliseconds(80));
            Check(view.MemoryStats.Regions == 0 && view.MemoryStats.RegionBytes == ContinuousPdfView.CachedRegionStats.Bytes,
                "Diagnostics still account for retained crop storage after its document is unbound");
            view.SetDocument(new[] { regionPage }, 3);
            WaitRegion(regionPage);
            TryRenderedRegion(view, regionPage, out var returned);
            Check(ReferenceEquals(original, returned) && RenderDiagnostics.RasterSlice.Count == raster,
                "Returning from another document restores its viewport region without native rasterization");
            view.SetDocument(new[] { otherPage }, 1);
            ContinuousPdfView.InvalidateCachedRegions((path, page) => path == source && page == 1);
            Check(ContinuousPdfView.CachedRegionStats.Count == 0,
                "An inactive source edit can invalidate its viewport cache independently of the bound document");
            view.SetDocument(new[] { regionPage }, 3);
            WaitRegion(regionPage);
            TryRenderedRegion(view, regionPage, out var fresh);
            Check(!ReferenceEquals(original, fresh) && RenderDiagnostics.RasterSlice.Count > raster,
                "An invalidated viewport is rasterized again rather than presenting stale source pixels");
            ContinuousPdfView.ReleaseUnusedRegionSources(new HashSet<string>());
            Check(ContinuousPdfView.CachedRegionStats.Bytes == 0, "Closing all sources releases cached viewport images");
            view.SetDocument(null, 1);
            PdfThumbnailService.SetHotPages(Array.Empty<(string, int)>());
            PdfThumbnailService.ReleaseUnusedDocuments(Array.Empty<string>());
        }
        finally
        {
            view.CancelAll(); host.Close();
            ContinuousPdfView.InvalidateCachedRegions((_, _) => true);
        }

        void WaitRegion(PagePlacement page)
        {
            var wait = Stopwatch.StartNew();
            while (!TryRenderedRegion(view, page, out _) && wait.Elapsed < TimeSpan.FromSeconds(5)) Pump(TimeSpan.FromMilliseconds(20));
            Check(TryRenderedRegion(view, page, out _), "The sampled viewport region fully covers the page's visible area");
        }
    }
}
