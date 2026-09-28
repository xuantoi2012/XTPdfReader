using System;
using System.Diagnostics;
using System.Threading;

namespace XTPdfMergeApp.Services;

/// <summary>
/// Thu hồi bộ nhớ PDFium theo chính sách, thay vì để document giữ cache đối tượng/stream của mọi trang từng parse
/// (đo 28/09 trên file CAD 165 MB: ~460 MB heap native đang cấp phát sau vài chục trang; đóng document → 195 MB).
///  • Tái chế (chỉ khi không có lệnh native nào trong 15 s): bản không có việc và không giữ trang nóng nào, đã parse ≥ <see cref="RecycleAfterPages"/> trang từ lần mở
///    document trước → đóng document của bản đó (mở lại ~50 ms khi cần).
///  • Rảnh: không có lệnh native nào trong <see cref="IdleTrimSeconds"/> s → đóng document của bản không giữ trang nóng nào;
///    trong <see cref="IdleTrimAllSeconds"/> s → đóng hết.
/// Vì sao không tái chế sớm hơn: document đóng rồi mở lại mất cache font/glyph/hình của PDFium — đo 28/09 vẽ lại 4 trang CAD
/// khi zoom mất 1,8–2 s (170–199 lát) so với 0,4–0,6 s (30–50 lát) khi document còn ấm.
/// Biến môi trường (để đo): XTPDF_RECYCLE_PAGES (0 = tắt tái chế), XTPDF_IDLE_TRIM_S (0 = tắt cả thu hồi khi rảnh).
/// </summary>
public static partial class PdfThumbnailService
{
    internal static readonly int RecycleAfterPages = EnvInt("XTPDF_RECYCLE_PAGES", 40, 0, 1000);
    internal static readonly int IdleTrimSeconds = EnvInt("XTPDF_IDLE_TRIM_S", 20, 0, 3600);
    internal static readonly int IdleTrimAllSeconds = Math.Max(IdleTrimSeconds, EnvInt("XTPDF_IDLE_TRIM_ALL_S", 90, 0, 3600));

    internal const double RecycleQuietSeconds = 15;
    private static long _lastNativeActivity = Stopwatch.GetTimestamp();
    private static Timer? _trimTimer;
    private static int _trimRunning;
    private static long _lastTrimDocuments;

    /// <summary>Số lần đóng document theo chính sách (bảng Debug).</summary>
    public static long PolicyTrims => Interlocked.Read(ref _lastTrimDocuments);

    private static int EnvInt(string name, int fallback, int min, int max)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? Math.Clamp(v, min, max) : fallback;

    private static bool HoldsHotPage(PdfiumInstance instance)
    {
        foreach (var (path, index) in Volatile.Read(ref _hotPages))
            if (_parsedPages.ContainsKey((path, index, instance.Index))) return true;
        return false;
    }

    private static void NoteNativeActivity() => Volatile.Write(ref _lastNativeActivity, Stopwatch.GetTimestamp());

    /// <summary>Bật bộ hẹn thu hồi (gọi 1 lần lúc khởi động app).</summary>
    public static void StartMemoryPolicy()
    {
        if (_trimTimer != null || (RecycleAfterPages == 0 && IdleTrimSeconds == 0)) return;
        _trimTimer = new Timer(_ => TrimTick(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private static void TrimTick()
    {
        if (_shuttingDown || Interlocked.Exchange(ref _trimRunning, 1) == 1) return;
        bool started = false;
        try
        {
            double idleSeconds = (Stopwatch.GetTimestamp() - Volatile.Read(ref _lastNativeActivity)) / (double)Stopwatch.Frequency;
            bool idleAll = IdleTrimSeconds > 0 && idleSeconds >= IdleTrimAllSeconds;
            bool idle = IdleTrimSeconds > 0 && idleSeconds >= IdleTrimSeconds;
            bool quiet = idleSeconds >= RecycleQuietSeconds; // không bao giờ thu hồi lúc đang cuộn/zoom (đo: làm giật)
            var pick = new System.Collections.Generic.HashSet<int>();
            foreach (var instance in PdfiumPool.Instances)
            {
                if (Volatile.Read(ref instance.OpenDocuments) == 0 || instance.Load > 0) continue;
                bool holdsPages = HoldsHotPage(instance); // trang nóng (đang xem/tải trước): đóng sẽ phải parse lại khi zoom, giữ
                if (idleAll || (idle && !holdsPages) ||
                    (RecycleAfterPages > 0 && quiet && !holdsPages && instance.ParsedSinceTrim >= RecycleAfterPages))
                    pick.Add(instance.Index);
            }
            if (pick.Count == 0) return;
            started = true;
            TrimDocumentsAsync(index => pick.Contains(index)).ContinueWith(_ =>
            {
                foreach (var instance in PdfiumPool.Instances)
                    if (pick.Contains(instance.Index)) instance.ResetParsedSinceTrim();
                Interlocked.Add(ref _lastTrimDocuments, pick.Count);
                DiagnosticsLog.Event($"thu hồi document PDFium: bản {string.Join(",", pick)} (rảnh {idleSeconds:0} s)");
                Interlocked.Exchange(ref _trimRunning, 0);
            });
        }
        catch { }
        finally
        {
            if (!started) Interlocked.Exchange(ref _trimRunning, 0);
        }
    }
}
