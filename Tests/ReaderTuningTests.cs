using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Domain;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    sealed record ReaderTuningOptions(int ReaderCacheMiB = 64, int NativeCachePages = 4,
        int WarmFiles = 2, bool KeepPrefetchedNativePages = false, int ZoomCycles = 0, bool DeepZoom = false,
        int CachedPagePenalty = 3, bool PreferViewportRegions = true, bool ReuseRenderedImages = true, int PreviewCacheMiB = 16)
    {
        public void Validate()
        {
            if (ReaderCacheMiB is < 16 or > 160 || NativeCachePages is < 0 or > 16 ||
                WarmFiles is < 0 or > 64 || ZoomCycles is < 0 or > 4 || CachedPagePenalty is < 0 or > 32 || PreviewCacheMiB is < 0 or > 32)
                throw new ArgumentOutOfRangeException(nameof(ReaderTuningOptions));
        }
    }

    static bool TryRenderedRegion(ContinuousPdfView view, PagePlacement row, out BitmapSource? bitmap)
    {
        bitmap = null;
        var states = (System.Collections.IDictionary)typeof(ContinuousPdfView)
            .GetField("_states", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        if (!states.Contains(row) || states[row] is not { } state) return false;
        if (state.GetType().GetField("RegionCts")!.GetValue(state) != null) return false;
        int quantum = (int)typeof(ContinuousPdfView).GetField("RegionResolutionQuantum", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        int maximum = (int)typeof(ContinuousPdfView).GetField("MaxRegionFullWidth", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        double headroom = (double)typeof(ContinuousPdfView).GetField("RegionHeadroom", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        double needed = row.LayoutWidth * view.Zoom * VisualTreeHelper.GetDpi(view).DpiScaleX;
        int fullWidth = (int)Math.Min(maximum, Math.Ceiling(Math.Min(needed * headroom, maximum) / quantum) * quantum);
        if (!view.TryGetPageRect(row, out var pageRect)) return false;
        var visible = Rect.Intersect(pageRect, new Rect(0, 0, view.Surface.ActualWidth, view.Surface.ActualHeight));
        if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return false;
        int fullHeight = Math.Max(1, (int)Math.Round(fullWidth * row.LayoutHeight / Math.Max(1, row.LayoutWidth)));
        var a = Unrotate((visible.Left - pageRect.Left) / pageRect.Width, (visible.Top - pageRect.Top) / pageRect.Height);
        var b = Unrotate((visible.Right - pageRect.Left) / pageRect.Width, (visible.Bottom - pageRect.Top) / pageRect.Height);
        double x0 = Math.Min(a.X, b.X) * fullWidth, y0 = Math.Min(a.Y, b.Y) * fullHeight;
        double x1 = Math.Max(a.X, b.X) * fullWidth, y1 = Math.Max(a.Y, b.Y) * fullHeight;
        var regions = (System.Collections.IEnumerable)state.GetType().GetField("Regions")!.GetValue(state)!;
        foreach (object region in regions.Cast<object>().Reverse())
        {
            var key = region.GetType().GetProperty("Key")!.GetValue(region)!;
            if ((int)key.GetType().GetProperty("FullWidth")!.GetValue(key)! != fullWidth) continue;
            if ((int)key.GetType().GetProperty("Version")!.GetValue(key)! != (int)state.GetType().GetField("Version")!.GetValue(state)!) continue;
            if ((string)key.GetType().GetProperty("Layers")!.GetValue(key)! != PdfLayerStateStore.GetToken(row.SourcePath)) continue;
            int x = (int)key.GetType().GetProperty("X")!.GetValue(key)!;
            int y = (int)key.GetType().GetProperty("Y")!.GetValue(key)!;
            int width = (int)key.GetType().GetProperty("Width")!.GetValue(key)!;
            int height = (int)key.GetType().GetProperty("Height")!.GetValue(key)!;
            if (x > x0 + .5 || y > y0 + .5 || x + width < x1 - .5 || y + height < y1 - .5) continue;
            bitmap = (BitmapSource)region.GetType().GetProperty("Bitmap")!.GetValue(region)!;
            return true;
        }
        return false;

        (double X, double Y) Unrotate(double x, double y) => view.ViewRotation switch
        {
            90 => (y, 1 - x), 180 => (1 - x, 1 - y), 270 => (1 - y, x), _ => (x, y)
        };
    }

    static void TestReaderTuningAudit()
    {
        new ReaderTuningOptions().Validate();
        Check(new ContinuousPdfView().PreferViewportRegions && ReaderTuningOptionsDefaultsMatch(),
            "Production and no-override profiling default to the measured viewport-first policy");
        // Renders 25% above the needed width (ZoomHeadroom) so the next zoom steps shrink a sharp image instead of enlarging a soft one.
        Check(ContinuousPdfView.PreferredPageBitmapWidth(1200, true) == 1536 &&
            ContinuousPdfView.PreferredPageBitmapWidth(2200, true) == 2304,
            "Viewport-first rendering preserves full screen resolution at normal zoom");
        Check(ContinuousPdfView.PreferredPageBitmapWidth(6600, true) == 1024 &&
            ContinuousPdfView.PreferredPageBitmapWidth(6600, false) == 2304,
            "Deep zoom avoids a redundant full-page upgrade while rendering the sharp viewport region");
        try { new ReaderTuningOptions(NativeCachePages: -1).Validate(); throw new Exception("Invalid tuning was accepted"); }
        catch (ArgumentOutOfRangeException) { Check(true, "Tuning profiles reject invalid native-page capacity"); }
        using var audit = new NativeParseAudit();
        audit.SetPhase("test");
        RenderDiagnostics.RecordNativePageParsed("audit-fixture.pdf", 0, 0);
        RenderDiagnostics.RecordNativePageParsed("audit-fixture.pdf", 0, 1);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(audit.Snapshot("test")));
        var root = json.RootElement;
        Check(root.GetProperty("Parses").GetInt32() == 2 && root.GetProperty("UniquePages").GetInt32() == 1 &&
            root.GetProperty("RepeatedParses").GetInt32() == 1 && root.GetProperty("PagesParsedOnMultipleInstances").GetInt32() == 1,
            "Parse audit distinguishes repeated work from unique pages across replicas");
        if (PdfiumPool.Count < 2) return;
        int previous = PdfiumPool.CachedPagePenalty;
        var cachedInstance = PdfiumPool.Instances[1];
        cachedInstance.AddLoad(1);
        try
        {
            PdfiumPool.CachedPagePenalty = 3;
            Check(PdfiumPool.Choose(i => i.Index == 1, _ => true).Index == 0,
                "Conservative affinity may choose an idle instance instead of a busy parsed page");
            PdfiumPool.CachedPagePenalty = 9;
            Check(PdfiumPool.Choose(i => i.Index == 1, _ => true).Index == 1,
                "Affinity experiment reuses a parsed page when its instance has light queued work");
            Check(PdfiumPool.Choose(_ => false, _ => false, reserveFirst: true).Index != 0,
                "Affinity tuning preserves the instance reserved for foreground work");
        }
        finally { cachedInstance.AddLoad(-1); PdfiumPool.CachedPagePenalty = previous; }

        static bool ReaderTuningOptionsDefaultsMatch()
        {
            var options = new ReaderTuningOptions();
            return options.ReaderCacheMiB == 64 && options.NativeCachePages == 4 && options.WarmFiles == 2 &&
                !options.KeepPrefetchedNativePages && options.CachedPagePenalty == 3 && options.PreferViewportRegions &&
                options.PreviewCacheMiB * 1024L * 1024 == ReaderPageRenderCache.PreviewBudgetBytes;
        }
    }

    sealed class NativeParseAudit : IDisposable
    {
        readonly ConcurrentDictionary<(string Phase, string Path, int Page, int Instance), int> _events = new();
        string _phase = "opening";
        public NativeParseAudit() => RenderDiagnostics.NativePageParsed += Record;
        public void SetPhase(string phase) => Volatile.Write(ref _phase, phase);
        void Record(string path, int page, int instance)
            => _events.AddOrUpdate((Volatile.Read(ref _phase), path, page, instance), 1, (_, count) => count + 1);
        public object Snapshot(string phase)
        {
            var items = _events.Where(e => e.Key.Phase == phase).ToArray();
            var pages = items.GroupBy(e => (e.Key.Path, e.Key.Page)).ToArray();
            int parses = items.Sum(e => e.Value);
            return new
            {
                Parses = parses, UniquePages = pages.Length, RepeatedParses = parses - pages.Length,
                PagesParsedOnMultipleInstances = pages.Count(g => g.Select(e => e.Key.Instance).Distinct().Count() > 1),
                MostRepeated = pages.Where(g => g.Sum(e => e.Value) > 1).OrderByDescending(g => g.Sum(e => e.Value)).Take(8)
                    .Select(g => new { File = System.IO.Path.GetFileName(g.Key.Path), Page = g.Key.Page + 1,
                        Parses = g.Sum(e => e.Value), Instances = g.Select(e => e.Key.Instance).Distinct().Order().ToArray() })
            };
        }
        public void Dispose() => RenderDiagnostics.NativePageParsed -= Record;
    }

    readonly record struct TimingReading(long Count, double TotalMs)
    {
        public static TimingReading Read(RenderDiagnostics.Timing timing) => new(timing.Count, timing.TotalMilliseconds);
        public object Since(TimingReading before) => new { Count = Count - before.Count, TotalMs = Math.Round(TotalMs - before.TotalMs, 1) };
    }

    sealed record ReaderTimingSnapshot(TimingReading NativeWait, TimingReading PageParse, TimingReading Raster,
        TimingReading Copy, TimingReading BufferWait, long Loads, long Hits, long Cancelled, long Yields,
        long Trims, int Gen0, int Gen1, int Gen2, double GcPauseMs, long ManagedAllocated,
        double NativeHeldMs, double CpuSeconds, double ElapsedSeconds)
    {
        public static ReaderTimingSnapshot Read()
        {
            using var process = Process.GetCurrentProcess();
            return new(TimingReading.Read(RenderDiagnostics.NativeWait), TimingReading.Read(RenderDiagnostics.PageOpen),
                TimingReading.Read(RenderDiagnostics.RasterSlice), TimingReading.Read(RenderDiagnostics.BitmapCopy),
                TimingReading.Read(RenderDiagnostics.BufferQueue), PdfThumbnailService.NativePageLoads,
                PdfThumbnailService.NativePageCacheHits, PdfThumbnailService.CancelledRenders,
                PdfThumbnailService.ProgressiveYields, PdfThumbnailService.PolicyTrims,
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration().TotalMilliseconds,
                GC.GetTotalAllocatedBytes(), PdfiumPool.Instances.Sum(i => i.GateHeld.TotalMilliseconds),
                process.TotalProcessorTime.TotalSeconds, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        }
        public object Since(ReaderTimingSnapshot before) => new
        {
            NativeWait = NativeWait.Since(before.NativeWait), PageParse = PageParse.Since(before.PageParse),
            Raster = Raster.Since(before.Raster), Copy = Copy.Since(before.Copy), BufferWait = BufferWait.Since(before.BufferWait),
            NativePageLoads = Loads - before.Loads, NativePageHits = Hits - before.Hits,
            CancelledNativeRenders = Cancelled - before.Cancelled, ProgressiveYields = Yields - before.Yields,
            PolicyTrims = Trims - before.Trims,
            Gen0 = Gen0 - before.Gen0, Gen1 = Gen1 - before.Gen1, Gen2 = Gen2 - before.Gen2,
            GcPauseMs = Math.Round(GcPauseMs - before.GcPauseMs, 1),
            ManagedAllocatedMiB = Math.Round((ManagedAllocated - before.ManagedAllocated) / 1048576d, 1),
            NativeGateHeldMs = Math.Round(NativeHeldMs - before.NativeHeldMs, 1),
            CpuSeconds = Math.Round(CpuSeconds - before.CpuSeconds, 2),
            WallSeconds = Math.Round(ElapsedSeconds - before.ElapsedSeconds, 3)
        };
    }
}
