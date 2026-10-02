using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace XTPdfMergeApp.Services;

/// <summary>
/// Thu hồi bộ nhớ PDFium theo chính sách, thay vì để mỗi document giữ cache đối tượng/stream/font của mọi trang từng parse
/// (đo 28/09 trên file CAD 165 MB: ~460 MB heap native sau vài chục trang; đóng document → 195 MB). Với NHIỀU file mở cùng lúc,
/// duyệt 6 file qua các tab làm RAM lên 2,4 GB và còn tăng — nên chính sách tính theo TỪNG document, không chỉ toàn app:
///  • Document cũ (luật C): document của file không có trang nóng nào và không được dùng trong <see cref="DocumentIdleSeconds"/> s
///    thì đóng, kể cả khi app vẫn đang bận với file khác. Chỉ file đang xem (và file vừa xem gần đây) giữ document ấm.
///  • Số file ấm tối đa (luật C2): chỉ <see cref="WarmFiles"/> file dùng gần nhất (file đang xem + file trước đó) giữ document;
///    file thứ ba trở đi bị đóng ngay khi không còn việc — RAM có trần theo số file, không phụ thuộc thời gian đổi tab.
///  • Áp lực bộ nhớ (luật D): RAM máy ≥ <see cref="SystemLoadPercent"/>% hoặc private của app ≥ <see cref="PrivateBudgetMb"/> MB
///    → đóng mọi document không nóng ngay (không chờ hết hạn).
///  • Tái chế (luật A): bản không có việc và không giữ trang nóng, đã parse ≥ <see cref="RecycleAfterPages"/> trang từ lần mở
///    document trước, và không có lệnh native nào trong <see cref="RecycleQuietSeconds"/> s.
///  • Rảnh (luật B): không có lệnh native nào trong <see cref="IdleTrimSeconds"/> s → đóng document không giữ trang nóng;
///    trong <see cref="IdleTrimAllSeconds"/> s → đóng hết.
/// Không bao giờ đóng document đang có việc (Load &gt; 0). Mở lại mất cache font/glyph của PDFium: vẽ lại 4 trang CAD khi zoom
/// 1,8–2 s so với 0,4–0,6 s khi còn ấm — vì vậy các ngưỡng không quá ngắn.
/// Biến môi trường (để đo): XTPDF_DOC_IDLE_S (0 = tắt luật C), XTPDF_WARM_FILES (0 = tắt luật C2), XTPDF_PRIVATE_BUDGET_MB (0 = tắt), XTPDF_SYSTEM_LOAD_PCT,
/// XTPDF_RECYCLE_PAGES (0 = tắt luật A), XTPDF_IDLE_TRIM_S (0 = tắt luật B).
/// XTPDF_HOT_DOCUMENT_COPIES: sau lúc rảnh giữ 1 document ấm cho file nóng (0 = giữ chính sách cũ); không huỷ lease đang có người dùng.
/// </summary>
public static partial class PdfThumbnailService
{
    internal static readonly int RecycleAfterPages = EnvInt("XTPDF_RECYCLE_PAGES", 40, 0, 1000);
    internal static readonly int IdleTrimSeconds = EnvInt("XTPDF_IDLE_TRIM_S", 20, 0, 3600);
    internal static readonly int IdleTrimAllSeconds = Math.Max(IdleTrimSeconds, EnvInt("XTPDF_IDLE_TRIM_ALL_S", 90, 0, 3600));
    internal static readonly int DocumentIdleSeconds = EnvInt("XTPDF_DOC_IDLE_S", 15, 0, 3600);
    internal static readonly int HotDocumentCopies = EnvInt("XTPDF_HOT_DOCUMENT_COPIES", 1, 0, PdfiumPool.MaxInstances);
    internal static volatile int WarmFiles = EnvInt("XTPDF_WARM_FILES", 2, 0, 64); // Settings đổi được lúc chạy (AppSettings.ApplyRuntime)
    internal static readonly int PrivateBudgetMb = EnvInt("XTPDF_PRIVATE_BUDGET_MB", 1500, 0, 1 << 20);
    internal static readonly int SystemLoadPercent = EnvInt("XTPDF_SYSTEM_LOAD_PCT", 80, 0, 100);
    internal const double RecycleQuietSeconds = 15;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<DocumentKey, long> _docLastUse = new(new DocumentKeyComparer());
    private static long _lastNativeActivity = Stopwatch.GetTimestamp();
    private static Timer? _trimTimer;
    private static int _trimRunning;
    private static long _trimmedDocuments;

    /// <summary>Số lần đóng document theo chính sách (bảng Debug).</summary>
    public static long PolicyTrims => Interlocked.Read(ref _trimmedDocuments);

    private static int EnvInt(string name, int fallback, int min, int max)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? Math.Clamp(v, min, max) : fallback;

    private static bool HoldsHotPage(PdfiumInstance instance)
    {
        foreach (var (path, index) in Volatile.Read(ref _hotPages))
            if (_parsedPages.ContainsKey((path, index, instance.Index))) return true;
        return false;
    }

    internal static IReadOnlySet<int> SelectHotDocumentOwners(
        IEnumerable<(int Instance, bool HasFocus, int VisibleHotPages, int HotPages, long LastUse)> candidates, int limit)
        => candidates.OrderByDescending(c => c.HasFocus).ThenByDescending(c => c.VisibleHotPages)
            .ThenByDescending(c => c.HotPages).ThenByDescending(c => c.LastUse)
            .ThenBy(c => c.Instance).Take(Math.Max(0, limit)).Select(c => c.Instance).ToHashSet();

    private static void NoteNativeActivity() => Volatile.Write(ref _lastNativeActivity, Stopwatch.GetTimestamp());

    /// <summary>Bật bộ hẹn thu hồi (gọi 1 lần lúc khởi động app).</summary>
    public static void StartMemoryPolicy()
    {
        if (_trimTimer != null) return;
        _trimTimer = new Timer(_ => TrimTick(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private static void TrimTick()
    {
        if (_shuttingDown || Interlocked.Exchange(ref _trimRunning, 1) == 1) return;
        bool started = false;
        try
        {
            long now = Stopwatch.GetTimestamp();
            double idleSeconds = (now - Volatile.Read(ref _lastNativeActivity)) / (double)Stopwatch.Frequency;
            bool idleAll = IdleTrimSeconds > 0 && idleSeconds >= IdleTrimAllSeconds;
            bool idle = IdleTrimSeconds > 0 && idleSeconds >= IdleTrimSeconds;
            bool quiet = idleSeconds >= RecycleQuietSeconds; // không bao giờ tái chế lúc đang cuộn/zoom (đo: làm giật)

            var instances = PdfiumPool.Instances;
            var hotPaths = new HashSet<string>(Volatile.Read(ref _hotPages).Select(h => h.Path), StringComparer.Ordinal);
            bool pressure = UnderMemoryPressure(out string pressureReason);

            var instancePick = new HashSet<int>();
            foreach (var instance in instances)
            {
                if (Volatile.Read(ref instance.OpenDocuments) == 0 || instance.Load > 0) continue;
                bool holdsHot = HoldsHotPage(instance); // đóng sẽ phải parse lại khi zoom
                if (idleAll || (idle && !holdsHot) ||
                    (RecycleAfterPages > 0 && quiet && !holdsHot && instance.ParsedSinceTrim >= RecycleAfterPages))
                    instancePick.Add(instance.Index);
            }

            // Luật C/D theo từng document (path, bản).
            var keyPick = new HashSet<DocumentKey>(new DocumentKeyComparer());
            var duplicatePick = new HashSet<DocumentKey>(new DocumentKeyComparer());
            long staleTicks = Stopwatch.Frequency * (long)DocumentIdleSeconds;
            HashSet<string>? warm = null;
            if (WarmFiles > 0)
                warm = _documentCache.Keys
                    .GroupBy(k => k.Path, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Max(k => _docLastUse.TryGetValue(k, out long t) ? t : 0L))
                    .Take(WarmFiles).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in _documentCache.Keys)
            {
                if (key.Instance >= instances.Count) continue;
                if (hotPaths.Contains(key.Path.ToUpperInvariant())) continue; // file đang xem giữ document ấm
                bool overLimit = warm != null && !warm.Contains(key.Path);
                bool stale = DocumentIdleSeconds > 0 &&
                             (!_docLastUse.TryGetValue(key, out long last) || now - last >= staleTicks);
                // File ngoài nhóm ấm / lúc thiếu RAM: đóng cả khi còn việc dở (thumbnail nền của file không ai xem — việc bị huỷ,
                // xem lại file thì xin lại). File chỉ "cũ" mà bản còn việc thì để lần sau.
                if (overLimit || pressure) keyPick.Add(key);
                else if (stale && instances[key.Instance].Load == 0) keyPick.Add(key);
            }

            // Bitmap caches survive native retirement. After interaction has stopped, keep the
            // replica covering hot foreground pages, not a second copy of the same PDF's resources.
            if (idle && HotDocumentCopies > 0)
            {
                var focus = Volatile.Read(ref _hotPageFocus);
                foreach (var group in _documentCache.Keys.Where(k => hotPaths.Contains(k.Path.ToUpperInvariant()))
                             .GroupBy(k => k.Path, StringComparer.OrdinalIgnoreCase))
                {
                    var owners = SelectHotDocumentOwners(group.Select(k => (k.Instance,
                        focus != null && focus.Path == k.Path.ToUpperInvariant() && _parsedPages.ContainsKey((focus.Path, focus.Index, k.Instance)),
                        Volatile.Read(ref _hotPages).Count(h => h.Path == k.Path.ToUpperInvariant() &&
                            _parsedPages.TryGetValue((h.Path, h.Index, k.Instance), out byte visible) && visible != 0),
                        Volatile.Read(ref _hotPages).Count(h => h.Path == k.Path.ToUpperInvariant() &&
                            _parsedPages.ContainsKey((h.Path, h.Index, k.Instance))),
                        _docLastUse.TryGetValue(k, out long last) ? last : 0L)), HotDocumentCopies);
                    foreach (var key in group)
                        if (!owners.Contains(key.Instance) && key.Instance < instances.Count && instances[key.Instance].Load == 0)
                            duplicatePick.Add(key);
                }
            }

            if (instancePick.Count == 0 && keyPick.Count == 0 && duplicatePick.Count == 0) return;
            started = true;
            _ = FinishTrimAsync();

            async Task FinishTrimAsync()
            {
                try
                {
                    await TrimDocumentsAsync(key => instancePick.Contains(key.Instance) || keyPick.Contains(key)).ConfigureAwait(false);
                    int duplicates = await TrimIdleDuplicatesAsync(duplicatePick).ConfigureAwait(false);
                    foreach (var instance in instances)
                        if (instancePick.Contains(instance.Index)) instance.ResetParsedSinceTrim();
                    foreach (var key in keyPick) _docLastUse.TryRemove(key, out long _);
                    Interlocked.Add(ref _trimmedDocuments, instancePick.Count + keyPick.Count + duplicates);
                    DiagnosticsLog.Event($"thu hồi document PDFium: bản {string.Join(",", instancePick)} + {keyPick.Count} document cũ + {duplicates} bản trùng" +
                                         (pressure ? $" (áp lực: {pressureReason})" : "") + $" (rảnh {idleSeconds:0} s)");
                }
                catch { /* best effort; retry on a later tick */ }
                finally { Interlocked.Exchange(ref _trimRunning, 0); }
            }
        }
        catch { }
        finally
        {
            if (!started) Interlocked.Exchange(ref _trimRunning, 0);
        }
    }

    private static async Task<int> TrimIdleDuplicatesAsync(IEnumerable<DocumentKey> keys)
    {
        var closed = new List<Task>();
        foreach (var key in keys)
        {
            if (!_documentCache.TryGetValue(key, out var lazy) || !lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully) continue;
            var lease = lazy.Value.Result;
            if (lease == null) continue;
            if (!lease.TryRetireIdle(() => !_shuttingDown && lease.Pdfium.Load == 0 &&
                Stopwatch.GetElapsedTime(Volatile.Read(ref _lastNativeActivity)).TotalSeconds >= IdleTrimSeconds &&
                _documentCache.TryRemove(new KeyValuePair<DocumentKey, Lazy<Task<PdfDocumentLease?>>>(key, lazy)))) continue;
            _docLastUse.TryRemove(key, out _);
            closed.Add(lease.NativeClosed);
        }
        if (closed.Count > 0)
            await Task.WhenAll(closed).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return closed.Count;
    }

    private static bool UnderMemoryPressure(out string reason)
    {
        reason = "";
        if (SystemLoadPercent > 0)
        {
            int load = MemoryProbe.SystemMemoryLoadPercent();
            if (load >= SystemLoadPercent) { reason = $"RAM máy {load}%"; return true; }
        }
        if (PrivateBudgetMb > 0)
        {
            using var process = Process.GetCurrentProcess();
            long mb = process.PrivateMemorySize64 >> 20;
            if (mb >= PrivateBudgetMb) { reason = $"private {mb} MB ≥ {PrivateBudgetMb} MB"; return true; }
        }
        return false;
    }
}
