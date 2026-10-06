using System.IO;
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
    static BitmapSource RegionPattern(Int32Rect rect)
    {
        var pixels = new byte[rect.Width * rect.Height * 4];
        for (int y = 0; y < rect.Height; y++)
            for (int x = 0; x < rect.Width; x++)
            {
                int i = (y * rect.Width + x) * 4;
                pixels[i] = (byte)((x + rect.X) * 13 + (y + rect.Y) * 3);
                pixels[i + 1] = (byte)((x + rect.X) * 7 + (y + rect.Y) * 11);
                pixels[i + 2] = (byte)((x + rect.X) * 5 + (y + rect.Y) * 17);
                pixels[i + 3] = 255;
            }
        var result = BitmapSource.Create(rect.Width, rect.Height, 96, 96, PixelFormats.Bgra32, null, pixels, rect.Width * 4);
        result.Freeze(); return result;
    }

    static byte[] RegionPixels(BitmapSource image)
    {
        if (image.Format != PixelFormats.Bgra32) image = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0); return pixels;
    }

    static async Task TestRegionReuseAsync()
    {
        var target = new Int32Rect(50, 30, 64, 48);
        var old = new Int32Rect(40, 20, 60, 80);
        var pieces = new List<ViewportRegionReuse.Piece> { new(old, RegionPattern(old)) };
        var missing = ViewportRegionReuse.Plan(target, pieces);
        Check(missing.Length == 1 && missing.Sum(r => (long)r.Width * r.Height) < target.Width * target.Height / 2,
            "Single-axis pan requests one missing band when enough old pixels are reusable");
        var diagonal = new Int32Rect(40, 20, 60, 48);
        Check(ViewportRegionReuse.Plan(target, new[] { new ViewportRegionReuse.Piece(diagonal, RegionPattern(diagonal)) }).SequenceEqual(new[] { target }),
            "Diagonal gaps fall back to one crop when their bounding band would not save work");
        foreach (var rect in missing)
        {
            var padded = ViewportRegionReuse.WithGutter(rect, 200, 200);
            pieces.Add(new(padded, RegionPattern(padded), rect));
        }
        var composed = await Task.Run(() => ViewportRegionReuse.Compose(target, pieces, default));
        Check(composed.IsFrozen && RegionPixels(composed).SequenceEqual(RegionPixels(RegionPattern(target))),
            "Background composition preserves every BGRA pixel, including nonzero origins and overlapping gutters");
        var halves = new[] { new ViewportRegionReuse.Piece(new(50, 30, 32, 48), RegionPattern(new(50, 30, 32, 48))),
            new ViewportRegionReuse.Piece(new(82, 30, 32, 48), RegionPattern(new(82, 30, 32, 48))) };
        Check(ViewportRegionReuse.Plan(target, halves).Length == 0,
            "The union of cached pieces can cover a viewport without native rendering");
        var pageEdge = ViewportRegionReuse.CachedPiece(new(0, 0, 100, 80), RegionPattern(new(0, 0, 100, 80)), 200, 200);
        Check(pageEdge.CopyBounds == new Int32Rect(0, 0, 84, 64),
            "Cached native crop edges are excluded, while true page edges remain usable");
        var guardTarget = new Int32Rect(40, 30, 60, 40);
        var paddedGuard = ViewportRegionReuse.WithGutter(guardTarget, 200, 200);
        var invalidGutter = RegionPixels(RegionPattern(paddedGuard));
        for (int y = 0; y < paddedGuard.Height; y++)
            for (int x = 0; x < paddedGuard.Width; x++)
                if (x + paddedGuard.X < guardTarget.X || x + paddedGuard.X >= guardTarget.X + guardTarget.Width ||
                    y + paddedGuard.Y < guardTarget.Y || y + paddedGuard.Y >= guardTarget.Y + guardTarget.Height)
                    Array.Clear(invalidGutter, (y * paddedGuard.Width + x) * 4, 4);
        var guardedBitmap = BitmapSource.Create(paddedGuard.Width, paddedGuard.Height, 96, 96, PixelFormats.Bgra32,
            null, invalidGutter, paddedGuard.Width * 4);
        guardedBitmap.Freeze();
        var guardPieces = new[] { new ViewportRegionReuse.Piece(target, RegionPattern(target)),
            new ViewportRegionReuse.Piece(paddedGuard, guardedBitmap, guardTarget) };
        Check(RegionPixels(ViewportRegionReuse.Compose(target, guardPieces, default)).SequenceEqual(RegionPixels(RegionPattern(target))),
            "Native gutter pixels cannot overwrite valid cached pixels outside the requested missing strip");
        var center = new Int32Rect(58, 38, 48, 32);
        Check(ViewportRegionReuse.Plan(target, new[] { new ViewportRegionReuse.Piece(center, RegionPattern(center)) }).SequenceEqual(new[] { target }),
            "Fragmented missing coverage falls back to one crop rather than many native passes");
        var tiny = new Int32Rect(50, 30, 4, 4);
        Check(ViewportRegionReuse.Plan(target, new[] { new ViewportRegionReuse.Piece(tiny, RegionPattern(tiny)) }).SequenceEqual(new[] { target }),
            "Low overlap does not pay the composition cost");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { ViewportRegionReuse.Compose(target, halves, cancelled.Token); throw new Exception("Cancelled composition succeeded"); }
        catch (OperationCanceledException) { }
        Check(true, "Cancelled composition never publishes a frame");
        try { ViewportRegionReuse.Compose(target, Array.Empty<ViewportRegionReuse.Piece>(), default); throw new Exception("Incomplete composition succeeded"); }
        catch (ArgumentException) { }
        Check(true, "Incomplete coverage cannot expose uninitialized pixel storage");

        var random = new Random(2718);
        for (int sample = 0; sample < 80; sample++)
        {
            var bounds = new Int32Rect(random.Next(40, 95), random.Next(20, 65), random.Next(8, 80), random.Next(8, 60));
            var sources = new[] { new ViewportRegionReuse.Piece(bounds, RegionPattern(bounds)) };
            var plan = ViewportRegionReuse.Plan(target, sources);
            var all = sources.Concat(plan.Select(r => new ViewportRegionReuse.Piece(r, RegionPattern(r)))).ToArray();
            Check(plan.Length <= 2 && RegionPixels(ViewportRegionReuse.Compose(target, all, default)).SequenceEqual(RegionPixels(RegionPattern(target))),
                $"Region coverage and composition agree for randomized pan {sample}");
        }

        string source = System.IO.Path.Combine(Output, "region-reuse-pixels.pdf");
        using (var doc = new PdfDocument(new PdfWriter(source)))
        {
            var page = doc.AddNewPage(new PageSize(1000, 600));
            var canvas = new PdfCanvas(page);
            canvas.SetLineWidth(.7f);
            for (int x = 0; x < 1000; x += 7) canvas.MoveTo(x, 0).LineTo(1000 - x, 600).Stroke();
            canvas.SaveState().SetExtGState(new iText.Kernel.Pdf.Extgstate.PdfExtGState().SetFillOpacity(.45f));
            canvas.SetFillColor(new iText.Kernel.Colors.DeviceRgb(20, 90, 190)).Rectangle(100, 140, 780, 340).Fill().RestoreState();
            canvas.BeginText().SetFontAndSize(iText.Kernel.Font.PdfFontFactory.CreateFont(), 24).MoveText(250, 350)
                .ShowText("Viewport overlap: text, thin paths and transparency").EndText();
            canvas.BeginText().SetFontAndSize(iText.Kernel.Font.PdfFontFactory.CreateFont(), 160).MoveText(460, 300)
                .ShowText("WAVY").EndText();
        }
        var nativeTarget = new Int32Rect(700, 400, 1120, 620);
        var nativeOld = new Int32Rect(500, 350, 1000, 750);
        var cached = await PdfThumbnailService.RenderPageTileAsync(source, 0, 3000, 1800, nativeOld) ?? throw new Exception("Fixture did not render");
        var nativePieces = new List<ViewportRegionReuse.Piece> { ViewportRegionReuse.CachedPiece(nativeOld, cached, 3000, 1800) };
        var nativePlan = ViewportRegionReuse.Plan(nativeTarget, nativePieces);
        var nativeMissing = nativePlan.Select(r => ViewportRegionReuse.WithGutter(r, 3000, 1800)).ToArray();
        var strips = await PdfThumbnailService.RenderPageTilesBatchAsync(source, 0, 3000, 1800, nativeMissing);
        for (int i = 0; i < strips.Count; i++) nativePieces.Add(new(nativeMissing[i], strips[i] ?? throw new Exception("Strip did not render"), nativePlan[i]));
        var reference = await PdfThumbnailService.RenderPageTileAsync(source, 0, 3000, 1800, nativeTarget) ?? throw new Exception("Reference did not render");
        var actual = await Task.Run(() => ViewportRegionReuse.Compose(nativeTarget, nativePieces, default));
        var actualPixels = RegionPixels(actual); var referencePixels = RegionPixels(reference);
        var different = Enumerable.Range(0, actualPixels.Length / 4).Where(i =>
            Enumerable.Range(0, 4).Any(c => actualPixels[i * 4 + c] != referencePixels[i * 4 + c])).ToArray();
        if (different.Length > 0)
        {
            Console.WriteLine($"Region pixel differences: {different.Length}/{actualPixels.Length / 4}; " +
                $"bounds={different.Min(i => i % nativeTarget.Width)},{different.Min(i => i / nativeTarget.Width)}.." +
                $"{different.Max(i => i % nativeTarget.Width)},{different.Max(i => i / nativeTarget.Width)}; " +
                $"max={different.Max(i => Enumerable.Range(0, 4).Max(c => Math.Abs(actualPixels[i * 4 + c] - referencePixels[i * 4 + c])))}");
            foreach (var entry in new[] { ("region-actual.png", actual), ("region-reference.png", reference) })
            {
                using var stream = File.Create(System.IO.Path.Combine(Output, entry.Item1));
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(entry.Item2)); encoder.Save(stream);
            }
        }
        int maximumDifference = different.Length == 0 ? 0 : different.Max(i =>
            Enumerable.Range(0, 4).Max(c => Math.Abs(actualPixels[i * 4 + c] - referencePixels[i * 4 + c])));
        Check(maximumDifference <= 2 && different.Length < actualPixels.Length / 4 / 20,
            "Native composition preserves text, paths and transparency within measured crop anti-alias rounding (2/255, under 5% of pixels)");

        // The streaming API makes completed tiles available before the batch
        // finishes; the viewer may choose to compose them before presentation.
        var streamRects = new[] { new Int32Rect(120, 100, 420, 320), new Int32Rect(540, 100, 420, 320) };
        var firstTile = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstTile = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<Int32Rect>();
        var streaming = PdfThumbnailService.RenderPageTilesStreamingAsync(source, 0, 1200, 720, streamRects,
            async (rect, _) =>
            {
                delivered.Add(rect);
                if (delivered.Count == 1)
                {
                    firstTile.TrySetResult();
                    await releaseFirstTile.Task.ConfigureAwait(false);
                }
            });
        await firstTile.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!streaming.IsCompleted, "First native tile is published before the viewport batch completes");
        releaseFirstTile.TrySetResult();
        var streamed = await streaming.WaitAsync(TimeSpan.FromSeconds(5));
        Check(delivered.Count == streamRects.Length && delivered.Distinct().Count() == streamRects.Length &&
            delivered.All(streamRects.Contains) && streamed.All(tile => tile != null),
            "Streaming delivers each requested tile once even when parallel callbacks arrive out of order");
    }

    static void TestRegionReuseViewer()
    {
        if (Application.Current == null) CreateReaderTestApplication();
        var view = new ContinuousPdfView { PrefetchPageCount = 0 };
        var host = new Window { Content = view, Width = 1320, Height = 700, WindowStyle = WindowStyle.None,
            ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        host.Show(); host.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(60));
        var row = new PagePlacement { SourcePath = "region-reuse-viewer-fixture.pdf", PageNumber = 1, BaseWidth = 1200, AspectRatio = .6 };
        var calls = new List<Int32Rect[]>();
        var layerCalls = new List<string>();
        TaskCompletionSource<List<BitmapSource?>>? delayed = null;
        view.PageRenderer = (_, width, _, _) => Task.FromResult<BitmapSource?>(Bitmap(width, (int)(width * .6)));
        view.RegionRenderer = (_, _, _, rects, _, layers) =>
        {
            calls.Add(rects.ToArray());
            layerCalls.Add(layers);
            return delayed?.Task ?? Task.FromResult(rects.Select(r => (BitmapSource?)RegionPattern(r)).ToList());
        };
        ContinuousPdfView.InvalidateCachedRegions((_, _) => true);
        try
        {
            view.SetDocument(new[] { row }, ExperimentalMuPdfViewport.ThroughputMode ? 6 : 3); Wait();
            Check(calls.Count == 1 && calls[0].Length == 1, "Cold deep viewport still uses one crop");
            long originalArea = calls[0].Sum(r => (long)r.Width * r.Height);
            view.ScrollBy(320, 0); Wait();
            Check(calls.Count == 2 && calls[1].Length == 1 && calls[1].Sum(r => (long)r.Width * r.Height) < originalArea * .6,
                "The real viewer routes a small horizontal pan to one bounded missing band");
            Check(view.MemoryStats.Regions <= 2 && ContinuousPdfView.CachedRegionStats.Bytes <= ContinuousPdfView.RegionCacheBudgetBytes,
                "Spatial reuse retains the existing live-region count and byte cache budget");
            int beforeEdit = calls.Count;
            view.InvalidatePages(r => ReferenceEquals(r, row), dropImages: false); Wait();
            Check(calls.Count == beforeEdit + 1 && calls[^1].Length == 1 &&
                calls[^1].Sum(r => (long)r.Width * r.Height) >= originalArea * .9, // a full crop (a reused missing band is under 60%); the edge-clipped crop after the pan can be a few percent smaller
                "Source invalidation renders a fresh crop instead of composing pixels from an old document version");
            int beforeLayer = calls.Count;
            PdfLayerStateStore.SetHidden(row.SourcePath, new HashSet<string> { "fixture-layer" }, new HashSet<string>());
            view.ScrollBy(0, 1); Wait();
            Check(calls.Count == beforeLayer + 1 && calls[^1].Length == 1 &&
                layerCalls[^1] == PdfLayerStateStore.GetToken(row.SourcePath),
                "A changed layer token renders fresh pixels instead of reusing a different layer view");
            view.ViewRotation = 90; Wait();
            Check(TryRenderedRegion(view, row, out _), "Rotated viewports finish a valid unrotated-source region");
            delayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            view.ScrollBy(0, 230); Pump(TimeSpan.FromMilliseconds(100));
            int before = calls.Count;
            var cacheBefore = ContinuousPdfView.CachedRegionStats;
            var rects = calls[^1];
            view.SetDocument(null, 1);
            delayed.SetResult(rects.Select(r => (BitmapSource?)RegionPattern(r)).ToList());
            Pump(TimeSpan.FromMilliseconds(100));
            Check(view.MemoryStats.Regions == 0 && calls.Count == before && ContinuousPdfView.CachedRegionStats == cacheBefore,
                "A late native strip cannot resurrect an unbound document or schedule new work");
        }
        finally { view.CancelAll(); host.Close(); PdfLayerStateStore.Forget(row.SourcePath); ContinuousPdfView.InvalidateCachedRegions((_, _) => true); }

        void Wait()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Pump(TimeSpan.FromMilliseconds(70));
            while (!TryRenderedRegion(view, row, out _) && watch.Elapsed < TimeSpan.FromSeconds(5)) Pump(TimeSpan.FromMilliseconds(20));
            Check(TryRenderedRegion(view, row, out _), "Spatial-reuse viewer presents a complete viewport");
        }
    }
}
