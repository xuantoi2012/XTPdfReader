using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows.Media;

namespace XTPdfMergeApp.Services;

/// <summary>
/// Nội dung cửa sổ Debug dạng chữ thuần, chia mục, căn cột — đọc được, copy/dán được (thay vì chụp màn hình).
/// Mục "RAM" tách từng khoản đo được, phần còn lại (chủ yếu PDFium parse trang + WPF) tính bằng hiệu.
/// </summary>
internal static class DiagnosticsReport
{
    /// <summary>Số liệu tải thumbnail của cửa sổ ghép (chỉ có khi cửa sổ đó đang mở).</summary>
    public static Func<string>? ThumbnailSection { get; set; }

    /// <summary>Số liệu của Viewer (cache ảnh trang, tile, vùng vẽ cuộn liên tục).</summary>
    public static Func<ViewerStats>? ViewerSection { get; set; }

    /// <summary>Kết quả lần bấm "Dọn RAM" gần nhất (private trước → sau).</summary>
    public static string? LastCollect { get; private set; }

    /// <summary>Thu gom rác .NET hết mức (kể cả nén vùng mảng lớn) và chạy finalizer — ảnh WPF đã bỏ nhưng chưa được thu gom
    /// giữ bộ nhớ native tới lúc này. Phần private còn lại sau khi dọn là bộ nhớ thật sự đang/đã được PDFium giữ.</summary>
    public static void CollectNow()
    {
        try { PdfThumbnailService.TrimDocumentsAsync(includePrimary: true).Wait(TimeSpan.FromSeconds(6)); } catch { }
        using var before = Process.GetCurrentProcess();
        long privateBefore = before.PrivateMemorySize64, gcBefore = GC.GetTotalMemory(false);
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var after = Process.GetCurrentProcess();
        LastCollect = $"{DateTime.Now:HH:mm:ss}: private {Mb(privateBefore):0} → {Mb(after.PrivateMemorySize64):0} MB, " +
                      $"GC heap {Mb(gcBefore):0} → {Mb(GC.GetTotalMemory(false)):0} MB";
    }

    internal readonly record struct ViewerStats(
        int ReaderCacheCount, long ReaderCacheBytes, int ReaderInflight,
        int RegionCacheCount, long RegionCacheBytes,
        int ContinuousPages, int ContinuousRegions, long ContinuousRegionBytes,
        string Mode, double Zoom, int CurrentPage, int PageCount);

    public static string Build()
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.AppendLine($"XTPdfMergeApp — Debug   {DateTime.Now:HH:mm:ss}");
        sb.AppendLine();

        // ── RAM ──
        using var process = Process.GetCurrentProcess();
        double privateMb = Mb(process.PrivateMemorySize64);
        double workingMb = Mb(process.WorkingSet64);
        double gcMb = Mb(GC.GetTotalMemory(false));
        double fileMb = Mb(PdfFileBuffer.ReservedBytes);
        var thumbs = ThumbnailCache.GetThumbnailCacheStats();
        var viewer = ViewerSection?.Invoke();
        double readerMb = Mb(viewer?.ReaderCacheBytes ?? 0);
        double retainedRegionMb = Mb(viewer?.RegionCacheBytes ?? 0);
        double regionMb = Mb(viewer?.ContinuousRegionBytes ?? 0);
        double thumbMb = Mb(thumbs.Bytes);
        // File PDF trong RAM là mảng byte managed (đã nằm trong GC heap); ảnh WPF là bộ nhớ native.
        double bitmapsMb = readerMb + regionMb + thumbMb;
        double restMb = Math.Max(0, privateMb - gcMb - bitmapsMb);

        Section(sb, "RAM");
        Row(sb, "Private (Task Manager)", $"{privateMb,8:0} MB");
        Row(sb, "Working set", $"{workingMb,8:0} MB");
        Row(sb, "GC heap (managed)", $"{gcMb,8:0} MB");
        Row(sb, "Bộ đệm file PDF (đĩa tạm, ngoài RAM)", $"{fileMb,8:0} MB");
        Row(sb, "Ảnh trang (Reader cache)", $"{readerMb,8:0} MB  / 80    ({viewer?.ReaderCacheCount ?? 0} ảnh gồm preview, đang vẽ {viewer?.ReaderInflight ?? 0})");
        Row(sb, "Cache vùng nét", $"{retainedRegionMb,8:0} MB  / 16    ({viewer?.RegionCacheCount ?? 0} ảnh)");
        Row(sb, "Vùng nét (live + cache, không trùng)", $"{regionMb,8:0} MB        ({viewer?.ContinuousRegions ?? 0} vùng live)");
        Row(sb, "Thumbnail", $"{thumbMb,8:0} MB  / 48    ({thumbs.Cache} ảnh, đang vẽ {thumbs.Inflight})");
        Row(sb, "Còn lại ≈ PDFium + WPF + khác", $"{restMb,8:0} MB   (= private − GC − ảnh)");
        if (LastCollect != null) Row(sb, "Lần \"Dọn RAM\" gần nhất", LastCollect);
        sb.AppendLine(MemoryProbe.Describe());
        sb.AppendLine();

        // ── File trong RAM ──
        var (created, changed, closed, edited) = PdfFileBuffer.Counters;
        Section(sb, "File PDF trong RAM");
        foreach (var (path, length, loaded) in PdfFileBuffer.Snapshot())
            Row(sb, Trim(System.IO.Path.GetFileName(path), 36), $"{Mb(length),8:0} MB, đã nạp {loaded:P0}");
        Row(sb, "Tạo bộ đệm", $"{created} lần");
        Row(sb, "Bỏ bộ đệm: file đổi / đóng / app ghi", $"{changed} / {closed} / {edited}");
        sb.AppendLine();

        // ── PDFium ──
        Section(sb, ExperimentalMuPdfViewport.BalancedMode ? $"MuPDF: {ExperimentalMuPdfViewport.RunningWorkerCount} workers, {ExperimentalMuPdfViewport.WorkerPrivateMiB:0} MiB" : $"PDFium — {PdfiumPool.Count}/{PdfiumPool.DesiredCount} bản song song, {Environment.ProcessorCount} nhân logic");
        sb.AppendLine("  #  file              việc  đang  chờ gate TB/max ms  bận  (phiên)  trang parse  page giữ  doc");
        try
        {
            foreach (var i in ExperimentalMuPdfViewport.BalancedMode ? Array.Empty<PdfiumInstance>() : PdfiumPool.Instances)
            {
                var (recent, session) = i.SampleBusy();
                sb.AppendLine(string.Format(ci, "  {0,-2} {1,-16} {2,6} {3,5}  {4,18} {5,5:P0} {6,7:P0} {7,12} {8,9} {9,4}",
                    i.Index, Trim(i.Name, 16), i.Completed, i.Load, i.GateWait.ToString(), recent, session,
                    i.PagesParsed, Volatile.Read(ref i.CachedPages), Volatile.Read(ref i.OpenDocuments)));
            }
        }
        catch (Exception ex) { sb.AppendLine("  " + ex.Message); }
        Row(sb, "Native đang chạy / chờ", $"{PdfThumbnailService.ActiveNativeCalls} / {PdfThumbnailService.WaitingNativeCalls}");
        Row(sb, "Doc / page handle trong cache", $"{PdfThumbnailService.CachedDocumentCount} / {PdfThumbnailService.CachedNativePageCount}");
        Row(sb, "Page cache hit / load", $"{PdfThumbnailService.NativePageCacheHits} / {PdfThumbnailService.NativePageLoads}");
        Row(sb, "Progressive yield / slice max", $"{PdfThumbnailService.ProgressiveYields} / {PdfThumbnailService.MaxNativeRenderSliceMilliseconds:0.0} ms");
        Row(sb, "Thu hồi document (tái chế/rảnh)", $"{PdfThumbnailService.PolicyTrims} lần (≤{PdfThumbnailService.WarmFiles} file ấm, cũ {PdfThumbnailService.DocumentIdleSeconds} s, sau {PdfThumbnailService.RecycleAfterPages} trang, rảnh {PdfThumbnailService.IdleTrimSeconds}/{PdfThumbnailService.IdleTrimAllSeconds} s)");
        Row(sb, "Nhường khi zoom/pan", $"{PdfThumbnailService.InteractionDeferrals}");
        sb.AppendLine();

        // ── Thời gian ──
        Section(sb, "Thời gian (TB/max ms, cả phiên)");
        Row(sb, "Chờ gate / hàng trang / buffer", $"{RenderDiagnostics.NativeWait} | {RenderDiagnostics.PageQueue} | {RenderDiagnostics.BufferQueue}");
        Row(sb, "Mở doc / parse trang", $"{RenderDiagnostics.DocumentOpen} ({RenderDiagnostics.DocumentOpen.Count} lần) | {RenderDiagnostics.PageOpen} ({RenderDiagnostics.PageOpen.Count} lần)");
        Row(sb, "Raster slice / copy WPF", $"{RenderDiagnostics.RasterSlice} | {RenderDiagnostics.BitmapCopy}");
        Row(sb, "File → RAM cả file", $"{RenderDiagnostics.FileBufferRead}  ({RenderDiagnostics.FileBufferRead.Count} lần)");
        Row(sb, "Khối 256 KB cần gấp", $"{RenderDiagnostics.FileBlockRead}  ({RenderDiagnostics.FileBlockRead.Count} lần)");
        sb.AppendLine();

        // ── Viewer ──
        if (viewer is { } v)
        {
            Section(sb, "Viewer");
            Row(sb, "Chế độ / zoom", $"{v.Mode}, {v.Zoom * 100:0}%");
            Row(sb, "Trang", v.PageCount > 0 ? $"{v.CurrentPage + 1}/{v.PageCount}" : "-");
            Row(sb, "Trang đang giữ ảnh (cuộn liên tục)", $"{v.ContinuousPages}");
            sb.AppendLine();
        }

        // ── Luồng ──
        ThreadPool.GetAvailableThreads(out int availableWorkers, out int availableIo);
        ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
        Section(sb, "Luồng");
        Row(sb, "Process threads", $"{process.Threads.Count}");
        Row(sb, "ThreadPool worker / I/O đang dùng", $"{maxWorkers - availableWorkers} / {maxIo - availableIo}");
        Row(sb, "WPF rendering tier", $"{RenderCapability.Tier >> 16} (0 = software)");
        sb.AppendLine();

        if (ThumbnailSection?.Invoke() is { Length: > 0 } thumbnails)
        {
            Section(sb, "Tải thumbnail (cửa sổ ghép)");
            sb.AppendLine(thumbnails);
            sb.AppendLine();
        }

        sb.AppendLine("Ghi chú: XTPDF_PDFIUM_INSTANCES=1..8 (đặt trước khi mở app) để đổi số bản PDFium.");
        return sb.ToString();
    }

    private static double Mb(long bytes) => bytes / 1048576.0;

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static void Section(StringBuilder sb, string title)
    {
        sb.AppendLine(title);
        sb.AppendLine(new string('─', Math.Min(96, Math.Max(20, title.Length))));
    }

    private static void Row(StringBuilder sb, string label, string value)
        => sb.AppendLine($"  {label,-36} {value}");
}
