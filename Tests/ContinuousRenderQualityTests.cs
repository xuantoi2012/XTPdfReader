using System.Diagnostics;
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
    static async Task TestReaderRenderHandoffAsync()
    {
        var key = (Path: "render-handoff.pdf", Page: 1, Width: 1024, Layers: "");
        var oldImage = Bitmap(20, 20); oldImage.Freeze();
        var newImage = Bitmap(30, 30); newImage.Freeze();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishOld = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var cache = new ReaderPageRenderCache(1_000_000, async (_, priority, _) =>
        {
            Interlocked.Increment(ref calls);
            if (priority == PdfRenderPriority.Background)
            {
                started.TrySetResult();
                return await finishOld.Task.ConfigureAwait(false); // imitate a slow native operation returning after cancellation
            }
            return newImage;
        });
        var background = cache.GetAsync(key, PdfRenderPriority.Background, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var foreground = await cache.GetAsync(key, PdfRenderPriority.Visible, default).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Check(ReferenceEquals(foreground, newImage) && calls == 2, "A visible page starts a new foreground render instead of joining its cancelled prefetch");
        finishOld.SetResult(oldImage);
        try { await background.ConfigureAwait(false); throw new Exception("Cancelled prefetch unexpectedly succeeded"); }
        catch (OperationCanceledException) { }
        Check(ReferenceEquals(await cache.GetAsync(key, PdfRenderPriority.Visible, default).ConfigureAwait(false), newImage),
            "Late cancelled prefetch cannot replace a sharper foreground image");

        var ownerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishOwner = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        calls = 0;
        var cancelledCache = new ReaderPageRenderCache(1_000_000, async (_, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                ownerStarted.TrySetResult();
                return await finishOwner.Task.ConfigureAwait(false);
            }
            return newImage;
        });
        using var owner = new CancellationTokenSource();
        var obsolete = cancelledCache.GetAsync(key, PdfRenderPriority.Visible, owner.Token);
        await ownerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        owner.Cancel();
        var replacement = await cancelledCache.GetAsync(key, PdfRenderPriority.Visible, default).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Check(ReferenceEquals(replacement, newImage) && calls == 2, "Returning to a page never reuses the task owned by a cancelled viewport");
        finishOwner.SetResult(oldImage);
        try { await obsolete.ConfigureAwait(false); } catch (OperationCanceledException) { }

        var sharedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishShared = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        calls = 0;
        var sharedCache = new ReaderPageRenderCache(1_000_000, (_, _, _) =>
        {
            Interlocked.Increment(ref calls); sharedStarted.TrySetResult(); return finishShared.Task;
        });
        var a = sharedCache.GetAsync(key, PdfRenderPriority.Visible, default);
        await sharedStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var b = sharedCache.GetAsync(key, PdfRenderPriority.Visible, default);
        finishShared.SetResult(newImage);
        var shared = await Task.WhenAll(a, b).ConfigureAwait(false);
        Check(calls == 1 && shared.All(image => ReferenceEquals(image, newImage)), "Live requests at the same priority still share one native render");

        var invalidatedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishInvalidated = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidatedCache = new ReaderPageRenderCache(1_000_000, (_, _, _) =>
        {
            invalidatedStarted.TrySetResult(); return finishInvalidated.Task;
        });
        var invalidated = invalidatedCache.GetAsync(key, PdfRenderPriority.Visible, default);
        await invalidatedStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        invalidatedCache.Invalidate(_ => true);
        finishInvalidated.SetResult(oldImage);
        try { await invalidated.ConfigureAwait(false); throw new Exception("Invalidated render unexpectedly succeeded"); }
        catch (OperationCanceledException) { }
        Check(invalidatedCache.Stats.Cache == 0, "Source invalidation prevents old in-flight pixels from repopulating the cache");
    }

    static void TestContinuousScrollQuality()
    {
        if (Application.Current == null)
        {
            CreateReaderTestApplication();
        }
        var requests = new List<(int Page, int Width, PdfRenderPriority Priority)>();
        var view = new ContinuousPdfView();
        var thumbnailPixels = new byte[340 * 136 * 4];
        for (int i = 0; i < thumbnailPixels.Length; i += 4) { thumbnailPixels[i + 2] = 255; thumbnailPixels[i + 3] = 255; }
        var thumbnail = BitmapSource.Create(340, 136, 96, 96, PixelFormats.Bgra32, null, thumbnailPixels, 340 * 4); thumbnail.Freeze();
        var host = new Window { Content = view, Width = 1320, Height = 700, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        host.Show(); host.UpdateLayout();
        int expectedWidth = (int)Math.Min(2304, Math.Ceiling(1200 * VisualTreeHelper.GetDpi(view).DpiScaleX * ContinuousPdfView.ZoomHeadroom / 256) * 256);
        var sharp = Bitmap(expectedWidth, (int)Math.Round(expectedWidth * .4)); sharp.Freeze();
        var pages = Enumerable.Range(1, 12).Select(i => new PagePlacement
        {
            SourcePath = "scroll-quality-fixture.pdf", PageNumber = i, BaseWidth = 1200, AspectRatio = .4, Thumbnail = thumbnail
        }).ToArray();
        int cancelledBackground = 0;
        var sharpReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.PageRenderer = async (row, width, priority, token) =>
        {
            requests.Add((row.PageNumber, width, priority));
            if (priority != PdfRenderPriority.Visible)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelledBackground++; throw; }
            }
            await sharpReady.Task.WaitAsync(token);
            return sharp;
        };
        view.Measure(new Size(1320, 700)); view.Arrange(new Rect(0, 0, 1320, 700)); view.UpdateLayout();
        Pump(TimeSpan.FromMilliseconds(60)); // finish the host's Loaded/SizeChanged before binding the document
        try
        {
            view.SetDocument(pages, 1);
            Pump(TimeSpan.FromMilliseconds(60));
            var coldFrame = new RenderTargetBitmap((int)view.Surface.ActualWidth, (int)view.Surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            coldFrame.Render(view.Surface);
            Check(view.TryGetPageRect(pages[0], out var pageRect), "Cold page remains positioned in the viewport while its readable image loads");
            var pixel = new byte[4]; coldFrame.CopyPixels(new Int32Rect((int)pageRect.X + 30, (int)pageRect.Y + 30, 1, 1), pixel, 4, 0);
            // Product decision (04/10): a cold page paints the coarse thumbnail at once instead of staying blank until the sharp render arrives.
            Check(pixel[2] > 240 && pixel[1] < 15 && pixel[0] < 15,
                "Cold viewport paints the sidebar thumbnail immediately instead of leaving the page blank");
            var visiblePage = Rect.Intersect(pageRect, new Rect(0, 0, view.Surface.ActualWidth, view.Surface.ActualHeight));
            var middle = new Int32Rect((int)(visiblePage.X + visiblePage.Width / 2) - 80,
                (int)(visiblePage.Y + visiblePage.Height / 2) - 20, 160, 40);
            var centerPixels = new byte[middle.Width * middle.Height * 4];
            coldFrame.CopyPixels(middle, centerPixels, middle.Width * 4, 0);
            Check(Enumerable.Range(0, centerPixels.Length / 4).All(i => centerPixels[i * 4 + 2] > 240 && centerPixels[i * 4 + 1] < 15 && centerPixels[i * 4] < 15),
                "Cold page shows only the thumbnail (no loading badge) while its sharp render runs asynchronously");
            sharpReady.SetResult(); Pump(TimeSpan.FromMilliseconds(100));
            var initialPages = requests.Where(r => r.Priority == PdfRenderPriority.Visible).GroupBy(r => r.Page).Take(2).ToArray();
            Check(initialPages.Length == 2 && initialPages.All(g => g.First().Width == expectedWidth), "Both adjacent visible pages request screen resolution immediately, without an intermediate render");
            Check(pages[0].ReaderBitmap?.PixelWidth == expectedWidth && pages[1].ReaderBitmap?.PixelWidth == expectedWidth,
                "Both pages refine to screen resolution without another zoom or scroll event");
            var prefetch = requests.Where(r => r.Priority == PdfRenderPriority.Background).GroupBy(r => r.Page).Take(4).ToArray();
            Check(prefetch.Length == 4 && prefetch.All(g => g.First().Width == expectedWidth),
                "All four pages ahead request screen resolution rather than coarse preview images");
            view.IsRenderingSuspended = true;
            requests.Clear();
            view.ScrollToPage(4);
            Pump(TimeSpan.FromMilliseconds(80));
            Check(requests.Count == 0, "A covered viewer cancels work and makes no requests even if layout changes");
            var suspendedHot = (IEnumerable<(string Path, int Index)>)typeof(PdfThumbnailService)
                .GetField("_hotPages", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            Check(!suspendedHot.Any(), "A covered viewer releases its native hot-page protection for idle reclamation");
            view.IsRenderingSuspended = false;
            Pump(TimeSpan.FromMilliseconds(80));
            Check(requests.Any(r => r.Page == 5 && r.Priority == PdfRenderPriority.Visible),
                "Uncovering the viewer resumes sharp rendering without another user interaction");
            view.PrefetchPageCount = 2;
            requests.Clear();
            var coldPages = pages.Select(p => new PagePlacement
            {
                SourcePath = p.SourcePath, PageNumber = p.PageNumber, BaseWidth = 1200, AspectRatio = .4, Thumbnail = thumbnail
            }).ToArray();
            view.SetDocument(coldPages, 1);
            Pump(TimeSpan.FromMilliseconds(80));
            Check(requests.Where(r => r.Priority == PdfRenderPriority.Background).Select(r => r.Page).Distinct().Count() == 2,
                "Background profiling can limit prefetch without reducing visible-page resolution");
            view.PrefetchPageCount = 4;
            view.KeepPrefetchedNativePages = true;
            view.SetDocument(pages, 1);
            Pump(TimeSpan.FromMilliseconds(80));
            var hotPages = (IEnumerable<(string Path, int Index)>)typeof(PdfThumbnailService)
                .GetField("_hotPages", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            Check(hotPages.Select(p => p.Index).Order().SequenceEqual(Enumerable.Range(0, 6)),
                "Native retention experiment includes visible pages and all four prefetched pages");
            view.KeepPrefetchedNativePages = false;
            bool renderedDuringMotion = false;
            int scrollSteps = 0;
            var scroll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(8) };
            scroll.Tick += (_, _) =>
            {
                view.ScrollBy(0, 70);
                renderedDuringMotion |= view.IsFastScrolling && requests.Any(r => r.Page >= 3 && r.Priority == PdfRenderPriority.Visible);
                if (++scrollSteps >= 18) scroll.Stop();
            };
            var motion = Stopwatch.StartNew();
            scroll.Start();
            while (scrollSteps < 18 && motion.Elapsed < TimeSpan.FromSeconds(2)) Pump(TimeSpan.FromMilliseconds(20));
            scroll.Stop();
            Check(scrollSteps == 18, "Scroll quality uses a fixed travel distance regardless of dispatcher timer jitter");
            Check(renderedDuringMotion, "Newly exposed pages render at readable quality while scrolling continues");
            var settle = Stopwatch.StartNew();
            while (pages[view.CurrentPage].ReaderBitmap?.PixelWidth != expectedWidth && settle.Elapsed < TimeSpan.FromSeconds(2))
                Pump(TimeSpan.FromMilliseconds(20));
            int index = view.CurrentPage;
            Check(index >= 2 && pages[index].ReaderBitmap?.PixelWidth == expectedWidth,
                "The current page finishes sharp after scrolling, without getting stuck on a cancelled prefetch");
            Check(cancelledBackground > 0, "Obsolete prefetch is cancelled to release render capacity for visible pages");
            view.TwoPage = true; view.ScrollToPage(0); Pump(TimeSpan.FromMilliseconds(80));
            Check(pages[0].ReaderBitmap?.PixelWidth >= 1024 && pages[1].ReaderBitmap?.PixelWidth >= 1024,
                "Facing-page mode keeps readable images for both pages");
            view.TwoPage = false;
            var mixedPages = new[]
            {
                new PagePlacement { SourcePath = "mixed-size-fixture.pdf", PageNumber = 1, BaseWidth = 2200, AspectRatio = .4 },
                new PagePlacement { SourcePath = "mixed-size-fixture.pdf", PageNumber = 2, BaseWidth = 200, AspectRatio = 1 }
            };
            view.SetDocument(mixedPages, 3);
            view.ScrollToPage(1);
            Check(view.TryGetPageRect(mixedPages[1], out var mixedRect) &&
                mixedRect.Left >= 0 && mixedRect.Right <= view.ViewportWidth,
                "Binding a mixed-size document at deep zoom keeps a narrow selected page horizontally visible");
            Check(Math.Abs(mixedRect.Left + mixedRect.Width / 2 - view.ViewportWidth / 2) < 1,
                "A newly bound deep-zoom document starts horizontally centered instead of off-page");
        }
        finally { view.CancelAll(); host.Close(); }
    }
}
