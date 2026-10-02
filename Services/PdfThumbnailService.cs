using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Services
{
    /// <summary>Lý do PDFium không mở được file. Mật khẩu chỉ sống trong RAM của phiên hiện tại.</summary>
    public enum PdfOpenFailure { None, Unknown, File, Format, Password, Security }

    public readonly record struct PdfOpenResult(int PageCount, PdfOpenFailure Failure);

    /// <summary>
    /// Direct PDFium renderer for the WPF thumbnail queue.
    /// The service keeps native PDF documents open only for active source files and
    /// returns frozen BitmapSource instances so they can safely cross worker threads.
    /// </summary>
    public static partial class PdfThumbnailService
    {
        private const int MaxRenderPixels = 24_000_000;
        /// <summary>FPDF_ANNOT — vẽ cả annotation (typewriter, ghi chú, highlight…). Thiếu cờ này thì annotation
        /// ghi vào file không bao giờ hiện trong Viewer/thumbnail.</summary>
        private const int FpdfAnnot = 0x01;
        private const int FpdfPrinting = 0x800;
        private const int FpdfLcdText = 0x02;
        private const int FpdfNoNativeText = 0x04;
        private const int FpdfRenderLimitedImageCache = 0x200;
        private static int _activeNativeCalls;
        private static int _waitingNativeCalls;

        public static int ActiveNativeCalls => Volatile.Read(ref _activeNativeCalls);
        public static int WaitingNativeCalls => Volatile.Read(ref _waitingNativeCalls);

        private sealed class PdfiumGateLease : IDisposable
        {
            private readonly PdfiumInstance _pdfium;
            private readonly string _caller;
            private readonly long _waitMs;
            private readonly Stopwatch _held = Stopwatch.StartNew();

            public PdfiumGateLease(PdfiumInstance pdfium, string caller, long waitMs)
            {
                _pdfium = pdfium;
                _caller = caller;
                _waitMs = waitMs;
                NoteNativeActivity();
                RenderDiagnostics.NativeWait.AddMilliseconds(waitMs);
                DiagnosticsLog.Slow("chờ gate", waitMs, $"{caller} bản #{pdfium.Index}");
            }

            public void Dispose()
            {
                _held.Stop();
                NoteNativeActivity();
                _pdfium.GateHeld.AddMilliseconds(_held.Elapsed.TotalMilliseconds);
                Interlocked.Decrement(ref _activeNativeCalls);
                _pdfium.Gate.Release();
                RecordPdfiumGateSample(_caller, _waitMs, _held.ElapsedMilliseconds);
            }
        }

        // Đo tỉ lệ thời gian chờ gate (contention) so với thời gian render thật (held) — gộp
        // thành 1 dòng tổng hợp mỗi ~2s thay vì ghi từng lần gọi (render/tile bắn hàng trăm lần/giây
        // khi cuộn liên tục) để biết gate PDFium có phải nút thắt cổ chai hay không.
        private static long _pdfiumGateCallCount;
        private static long _pdfiumGateTotalWaitMs;
        private static long _pdfiumGateTotalHeldMs;
        private static long _pdfiumGateMaxWaitMs;
        private static DateTime _lastPdfiumGateFlush = DateTime.MinValue;
        private static readonly object _pdfiumGateLogLock = new();
        private static readonly string PdfiumGateLogPath =
            Path.Combine(Path.GetTempPath(), "XTPdfMergeApp_PdfiumGate.log");

        private static void RecordPdfiumGateSample(string caller, long waitMs, long heldMs)
        {
            if (!RenderDiagnostics.TraceEnabled) return;
            Interlocked.Increment(ref _pdfiumGateCallCount);
            Interlocked.Add(ref _pdfiumGateTotalWaitMs, waitMs);
            Interlocked.Add(ref _pdfiumGateTotalHeldMs, heldMs);

            long observed;
            while (waitMs > (observed = Volatile.Read(ref _pdfiumGateMaxWaitMs)))
            {
                if (Interlocked.CompareExchange(ref _pdfiumGateMaxWaitMs, waitMs, observed) == observed) break;
            }

            var now = DateTime.Now;
            lock (_pdfiumGateLogLock)
            {
                if ((now - _lastPdfiumGateFlush).TotalSeconds < 2) return;
                _lastPdfiumGateFlush = now;

                long calls = Interlocked.Exchange(ref _pdfiumGateCallCount, 0);
                long totalWait = Interlocked.Exchange(ref _pdfiumGateTotalWaitMs, 0);
                long totalHeld = Interlocked.Exchange(ref _pdfiumGateTotalHeldMs, 0);
                long maxWait = Interlocked.Exchange(ref _pdfiumGateMaxWaitMs, 0);
                if (calls == 0) return;

                try
                {
                    double waitPct = totalWait + totalHeld == 0 ? 0 : 100.0 * totalWait / (totalWait + totalHeld);
                    string line = $"{now:HH:mm:ss.fff} | calls={calls} avgWait={(double)totalWait / calls:F1}ms " +
                        $"avgHeld={(double)totalHeld / calls:F1}ms maxWait={maxWait}ms totalWait={totalWait}ms " +
                        $"totalHeld={totalHeld}ms waitShare={waitPct:F0}% lastCaller={caller} " +
                        $"active={ActiveNativeCalls} waiting={WaitingNativeCalls}";
                    File.AppendAllText(PdfiumGateLogPath, line + Environment.NewLine);
                    Debug.WriteLine("[PdfiumGate] " + line);
                }
                catch
                {
                    // best-effort debug log only
                }
            }
        }

        private sealed class PdfDocumentLease
        {
            private readonly object _lifetimeGate = new();
            private int _activeUsers;
            private bool _disposeRequested;
            private bool _closed;
            private bool _idlePolicyRetired;
            public bool IdlePolicyRetired => Volatile.Read(ref _idlePolicyRetired);
            private readonly CancellationTokenSource _retired = new();
            private readonly TaskCompletionSource _nativeClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationToken RetiredToken => _retired.Token;
            /// <summary>Hoàn tất khi FPDF_CloseDocument đã chạy xong — lúc đó PDFium nhả hẳn handle
            /// file (Windows mới cho ghi đè file nguồn).</summary>
            public Task NativeClosed => _nativeClosed.Task;

            public PdfDocumentLease(PdfiumInstance pdfium, string sourcePath, IntPtr document, int pageCount,
                string layerToken = "", IDisposable? nativeSource = null)
            {
                Pdfium = pdfium;
                LayerToken = layerToken;
                _nativeSource = nativeSource;
                SourcePath = sourcePath;
                Document = document;
                PageCount = pageCount;
            }

            public string SourcePath { get; }
            /// <summary>Bản PDFium đã mở document này — mọi lệnh trên document/page của nó phải qua bản này.</summary>
            public PdfiumInstance Pdfium { get; }
            /// <summary>Trạng thái layer mà document PDFium này được mở theo (xem PdfLayerStateStore).</summary>
            public string LayerToken { get; }
            /// <summary>Nguồn đọc của FPDF_LoadCustomDocument — chỉ được huỷ SAU FPDF_CloseDocument.</summary>
            private readonly IDisposable? _nativeSource;
            public IntPtr Document { get; }
            public int PageCount { get; }

            public bool TryAcquire(out DocumentUsage? usage)
            {
                lock (_lifetimeGate)
                {
                    if (_disposeRequested || _closed)
                    {
                        usage = null;
                        return false;
                    }

                    _activeUsers++;
                    usage = new DocumentUsage(this);
                    return true;
                }
            }

            public void RequestDispose()
            {
                _retired.Cancel();
                bool closeNow;
                lock (_lifetimeGate)
                {
                    _disposeRequested = true;
                    closeNow = _activeUsers == 0 && !_closed;
                    if (closeNow) _closed = true;
                }

                if (closeNow) QueueCloseNativeDocument();
            }

            public bool TryRetireIdle(Func<bool> removeFromCache)
            {
                lock (_lifetimeGate)
                {
                    if (_activeUsers != 0 || _disposeRequested || _closed || !removeFromCache()) return false;
                    Volatile.Write(ref _idlePolicyRetired, true);
                    _disposeRequested = true;
                    _closed = true;
                }
                _retired.Cancel();
                QueueCloseNativeDocument();
                return true;
            }

            private void ReleaseUsage()
            {
                bool closeNow;
                lock (_lifetimeGate)
                {
                    _activeUsers--;
                    closeNow = _activeUsers == 0 && _disposeRequested && !_closed;
                    if (closeNow) _closed = true;
                }

                // Cancellation must be able to finish without synchronously waiting for
                // another native render to release the global gate.
                if (closeNow) QueueCloseNativeDocument();
            }

            private void QueueCloseNativeDocument()
            {
                Interlocked.Increment(ref _inFlightPublicCalls);
                _ = Task.Run(() =>
                {
                    try { CloseNativeDocument(); }
                    finally { Interlocked.Decrement(ref _inFlightPublicCalls); }
                });
            }

            private void CloseNativeDocument()
            {
                // _closed is claimed under the lifetime lock only after all users release.
                // Native destruction participates in the same global gate as rendering.
                try
                {
                    if (Document == IntPtr.Zero) return;
                    using var native = EnterPdfiumGate(Pdfium);
                    RemoveDocumentPages(Pdfium, Document);
                    Pdfium.CloseDocument(Document);
                    Interlocked.Decrement(ref Pdfium.OpenDocuments);
                }
                finally
                {
                    _nativeSource?.Dispose();
                    _nativeClosed.TrySetResult();
                }
            }

            public sealed class DocumentUsage : IDisposable
            {
                private PdfDocumentLease? _owner;

                internal DocumentUsage(PdfDocumentLease owner) => _owner = owner;
                public PdfDocumentLease Lease => _owner ?? throw new ObjectDisposedException(nameof(DocumentUsage));

                public void Dispose()
                    => Interlocked.Exchange(ref _owner, null)?.ReleaseUsage();
            }
        }

        private static volatile bool _shuttingDown;

        // Đếm TỪ NGAY LÚC VÀO RenderPageAsync/RenderPageTileAsync — KHÁC hẳn _activeNativeCalls/
        // _waitingNativeCalls (chỉ đếm từ lúc vào EnterPdfiumGate). Có 1 khoảng hở giữa 2 mốc này:
        // 1 lệnh đã qua khỏi check "_shuttingDown" ở đầu hàm nhưng còn đang xếp hàng ở
        // AcquireDocumentAsync (CHƯA tới EnterPdfiumGate) hoàn toàn
        // KHÔNG được PrepareForShutdown cũ tính tới — shutdown có thể tiến hành FPDF_DestroyLibrary()
        // ngay trong lúc lệnh đó còn treo, rồi crash ExecutionEngineException khi nó được chạy tiếp
        // (đúng lỗi thật đã gặp: FPDF_LoadPage ném ExecutionEngineException). Đếm từ đầu hàm mới lấp
        // được khoảng hở này.
        private static int _inFlightPublicCalls;

        /// <summary>Gọi lúc app bắt đầu đóng (App.OnExit), TRƯỚC khi ProcessExit huỷ FPDF_InitLibrary.
        /// Chặn mọi lệnh render MỚI ngay lập tức, rồi đợi tối đa <paramref name="timeout"/> cho các lệnh
        /// PDFium đang chạy dở (kể cả đang xếp hàng, chưa thật sự vào tới lệnh native) kết thúc — tránh
        /// crash ExecutionEngineException do gọi vào PDFium sau khi thư viện native đã bị
        /// FPDF_DestroyLibrary() phá huỷ.</summary>
        public static void PrepareForShutdown(TimeSpan timeout)
        {
            _shuttingDown = true;
            var deadline = DateTime.UtcNow + timeout;
            while ((Volatile.Read(ref _inFlightPublicCalls) > 0 ||
                    Volatile.Read(ref _activeNativeCalls) > 0 ||
                    Volatile.Read(ref _waitingNativeCalls) > 0)
                   && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(15);
            }
        }

        /// <summary>Mỗi bản PDFium mở document riêng của nó (handle PDFium không dùng chéo giữa các bản); mọi
        /// bản đọc chung 1 PdfBlockCache nên file chỉ đi qua mạng 1 lần.</summary>
        private readonly record struct DocumentKey(string Path, int Instance);

        private sealed class DocumentKeyComparer : IEqualityComparer<DocumentKey>
        {
            public bool Equals(DocumentKey x, DocumentKey y)
                => x.Instance == y.Instance && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);
            public int GetHashCode(DocumentKey key)
                => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Path), key.Instance);
        }

        private static readonly ConcurrentDictionary<DocumentKey, Lazy<Task<PdfDocumentLease?>>> _documentCache =
            new(new DocumentKeyComparer());
        private static readonly ConcurrentDictionary<string, string> _documentPasswords = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, PdfOpenFailure> _openFailures = new(StringComparer.OrdinalIgnoreCase);

        public static int CachedDocumentCount => _documentCache.Count;

        private static bool HasOpenDocument(string normalizedPath, PdfiumInstance pdfium)
            => _documentCache.TryGetValue(new DocumentKey(normalizedPath, pdfium.Index), out var lazy) && lazy.IsValueCreated;

        /// <summary>Chọn bản PDFium cho 1 việc (xem PdfiumPool.Choose). pageIndex &lt; 0: việc không cần parse trang.</summary>
        private static PdfiumInstance ChooseInstance(string normalizedPath, int pageIndex, PdfRenderPriority priority = PdfRenderPriority.Visible)
        {
            // Chưa có bộ đệm khối (lần mở đầu, file > 500 MB, hết ngân sách RAM): chỉ bản chính — nếu không, mỗi bản
            // sẽ tự đọc file qua mạng. Lần mở đầu trên bản chính tạo bộ đệm; từ đó việc được chia cho mọi bản.
            if (!PdfFileBuffer.IsBuffered(normalizedPath) && !PdfFileBuffer.IsLocalDisk(normalizedPath)) return PdfiumInstance.Primary;
            string pathKey = normalizedPath.ToUpperInvariant();
            return PdfiumPool.Choose(
                instance => pageIndex >= 0 && _parsedPages.ContainsKey((pathKey, pageIndex, instance.Index)),
                instance => HasOpenDocument(normalizedPath, instance),
                reserveFirst: priority != PdfRenderPriority.Visible);
        }

        /// <summary>Mở để đếm trang và trả về nguyên nhân có thể hiển thị cho người dùng nếu PDFium từ chối file.</summary>
        public static async Task<PdfOpenResult> TryGetPageCountAsync(string pdfPath)
        {
            if (_shuttingDown) return new PdfOpenResult(0, PdfOpenFailure.Unknown);
            PdfiumInstance? pdfium = null;
            string? normalized = null;
            try
            {
                normalized = NormalizePath(pdfPath);
                pdfium = ChooseInstance(normalized, -1);
                pdfium.AddLoad(1);
                using var usage = await AcquireDocumentAsync(normalized, pdfium).ConfigureAwait(false);
                if (usage != null) return new PdfOpenResult(usage.Lease.PageCount, PdfOpenFailure.None);
                return new PdfOpenResult(0, _openFailures.TryGetValue(normalized, out var failure) ? failure : PdfOpenFailure.Unknown);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Event("page count failed: " + ex);
                return new PdfOpenResult(0, PdfOpenFailure.Unknown);
            }
            finally
            {
                pdfium?.AddLoad(-1);
            }
        }

        public static async Task<int> GetPageCountAsync(string pdfPath)
            => (await TryGetPageCountAsync(pdfPath).ConfigureAwait(false)).PageCount;

        /// <summary>Trả password chỉ đang giữ trong RAM cho các thao tác đọc cùng tiến trình.</summary>
        internal static string? TryGetDocumentPassword(string pdfPath)
        {
            _documentPasswords.TryGetValue(NormalizePath(pdfPath), out string? password);
            return password;
        }

        /// <summary>Đặt mật khẩu đọc cho một file, chỉ trong bộ nhớ tiến trình; lease cũ bị mở lại an toàn.</summary>
        public static async Task SetDocumentPasswordAsync(string pdfPath, string password)
        {
            string normalized = NormalizePath(pdfPath);
            _documentPasswords[normalized] = password;
            _openFailures.TryRemove(normalized, out _);
            await RetireDocumentAsync(normalized).ConfigureAwait(false);
        }

        /// <summary>Bỏ mật khẩu chỉ lưu trong RAM khi user huỷ mở/đóng file.</summary>
        public static async Task ForgetDocumentPasswordAsync(string pdfPath)
        {
            string normalized = NormalizePath(pdfPath);
            _documentPasswords.TryRemove(normalized, out _);
            _openFailures.TryRemove(normalized, out _);
            await RetireDocumentAsync(normalized).ConfigureAwait(false);
        }

        public static void ReleaseUnusedDocuments(IEnumerable<string> activePdfPaths)
        {
            var active = new HashSet<string>(
                activePdfPaths
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(NormalizePath),
                StringComparer.OrdinalIgnoreCase);

            PdfFileBuffer.ReleaseExcept(active.Contains);
            foreach (string path in _documentPasswords.Keys)
                if (!active.Contains(path)) _documentPasswords.TryRemove(path, out _);
            foreach (string path in _openFailures.Keys)
                if (!active.Contains(path)) _openFailures.TryRemove(path, out _);
            foreach (var key in _documentCache.Keys)
            {
                if (active.Contains(key.Path)) continue;

                if (_documentCache.TryRemove(key, out var lazy) && lazy.IsValueCreated)
                    _ = Task.Run(() => RequestLeaseDisposalWhenReadyAsync(lazy.Value));
            }
        }

        /// <summary>Đóng document PDFium đang giữ (cache đối tượng/stream đã đọc của PDFium nằm trong document: đo 28/09 trên file
        /// CAD 165 MB, ~460 MB heap native đang cấp phát sau khi xem vài chục trang). Lần dùng sau tự mở lại (~50 ms + parse
        /// trang). Lease đang có người dùng chỉ đóng khi họ nhả. <paramref name="includePrimary"/>=false giữ bản #0.</summary>
        public static Task TrimDocumentsAsync(bool includePrimary)
            => TrimDocumentsAsync(index => includePrimary || index != 0);

        private static Task TrimDocumentsAsync(Func<int, bool> instanceFilter)
            => TrimDocumentsAsync(key => instanceFilter(key.Instance));

        private static Task TrimDocumentsAsync(Func<DocumentKey, bool> keyFilter)
        {
            var closing = new List<Task>();
            foreach (var key in _documentCache.Keys)
            {
                if (!keyFilter(key)) continue;
                if (!_documentCache.TryRemove(key, out var lazy) || !lazy.IsValueCreated) continue;
                closing.Add(Task.Run(async () =>
                {
                    try
                    {
                        var lease = await lazy.Value.ConfigureAwait(false);
                        if (lease == null) return;
                        lease.RequestDispose();
                        await lease.NativeClosed.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                    catch { /* best effort */ }
                }));
            }
            return Task.WhenAll(closing);
        }

        private static readonly ConcurrentDictionary<string, int> _suspendedDocuments =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Đóng hẳn handle PDFium của 1 file và chặn mở lại cho tới khi Dispose kết quả —
        /// dùng trước khi ghi đè file nguồn (xoay trang, ghi annotation): FPDF_LoadDocument giữ
        /// file mở suốt đời lease, Windows không cho ghi đè trong lúc đó. Trong lúc bị chặn, mọi
        /// render/đếm trang của file này trả null như cache-miss; caller tự yêu cầu render lại sau.</summary>
        /// <param name="fileWillChange">false = chỉ đóng để mở lại (đổi layer): file không đổi, giữ nguyên bản file trong RAM.</param>
        public static async Task<IDisposable> SuspendDocumentAsync(string pdfPath, TimeSpan timeout, bool fileWillChange = true)
        {
            string normalized = NormalizePath(pdfPath);
            _suspendedDocuments.AddOrUpdate(normalized, 1, (_, count) => count + 1);
            // File sắp bị ghi đè → bản trong RAM không còn đúng. Đổi layer thì file không đổi: trước đây vẫn bỏ bản trong
            // RAM, mỗi lần bật/tắt layer là đọc lại cả file qua mạng.
            if (fileWillChange) PdfFileBuffer.Invalidate(normalized, PdfFileBuffer.InvalidateReason.Edited);
            _layerTails.TryRemove(normalized, out _);
            var suspension = new DocumentSuspension(normalized);
            try
            {
                // Đóng lease của file này ở MỌI bản PDFium. Lặp: 1 request đã qua check suspended ngay trước khi ta
                // đánh dấu có thể vừa kịp tạo lease mới sau lượt TryRemove đầu.
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var keys = _documentCache.Keys
                        .Where(key => string.Equals(key.Path, normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (keys.Length == 0) break;
                    foreach (var key in keys)
                    {
                        if (!_documentCache.TryRemove(key, out var lazy) || !lazy.IsValueCreated) continue;
                        var lease = await lazy.Value.ConfigureAwait(false);
                        if (lease == null) continue;
                        lease.RequestDispose();
                        await lease.NativeClosed.WaitAsync(timeout).ConfigureAwait(false);
                    }
                }
            }
            catch (TimeoutException)
            {
                // Vẫn trả suspension — ghi file phía caller có retry riêng; nếu handle còn giữ thật
                // thì lỗi IO sẽ báo rõ cho user.
            }
            return suspension;
        }

        /// <summary>Đóng lease hiện tại của file (vd vừa đổi trạng thái layer) — lần vẽ kế tiếp tự mở lại theo
        /// trạng thái mới. Dùng lại đúng cơ chế SuspendDocumentAsync/RequestDispose.</summary>
        public static async Task RetireDocumentAsync(string pdfPath)
        {
            using (await SuspendDocumentAsync(pdfPath, TimeSpan.FromSeconds(3), fileWillChange: false).ConfigureAwait(false)) { }
        }

        private sealed class DocumentSuspension(string normalizedPath) : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                while (true)
                {
                    if (!_suspendedDocuments.TryGetValue(normalizedPath, out int count)) return;
                    if (count <= 1)
                    {
                        if (_suspendedDocuments.TryRemove(new KeyValuePair<string, int>(normalizedPath, count))) return;
                    }
                    else if (_suspendedDocuments.TryUpdate(normalizedPath, count - 1, count)) return;
                }
            }
        }

        /// <summary>pageIndex is zero-based.</summary>
        /// <param name="layerToken">Trạng thái layer mà kết quả sẽ được cache theo (PdfLayerStateStore.GetToken);
        /// lệch trạng thái hiện tại → trả null. null = không kiểm tra.</param>
        /// <param name="withAnnotations">Print: include annotations. Screen renders never do (the annotation layer draws them over the page).</param>
        public static async Task<BitmapSource?> RenderPageAsync(string pdfPath, int pageIndex, double maxWidth = 96, CancellationToken cancellationToken = default,
            PdfRenderPriority priority = PdfRenderPriority.Visible, string? layerToken = null, bool withAnnotations = false)
        {
            if (_shuttingDown) return null;
            Interlocked.Increment(ref _inFlightPublicCalls);
            PdfiumInstance? pdfium = null;
            try
            {
                // Kiểm tra lại: shutdown có thể vừa bắt đầu NGAY giữa lúc ta tăng đếm ở trên (trước
                // check này) và lúc PrepareForShutdown đọc _inFlightPublicCalls lần đầu — huỷ sớm nếu
                // vậy, đừng để lọt xuống AcquireDocumentAsync/PDFium nữa.
                if (_shuttingDown) return null;

                string normalized = NormalizePath(pdfPath);
                pdfium = ChooseInstance(normalized, pageIndex, priority);
                pdfium.AddLoad(1);
                using var usage = await AcquireDocumentAsync(normalized, pdfium, cancellationToken, layerToken).ConfigureAwait(false);
                if (usage == null) return null;

                var lease = usage.Lease;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.RetiredToken);
                cancellationToken = linked.Token;
                if (pageIndex < 0 || pageIndex >= lease.PageCount) return null;

                return await Task.Run(() => RenderPageProgressiveAsync(lease, pageIndex, maxWidth,
                    priority, cancellationToken, withAnnotations), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (pdfium != null)
                {
                    pdfium.AddLoad(-1);
                    pdfium.MarkCompleted();
                }
                Interlocked.Decrement(ref _inFlightPublicCalls);
            }
        }

        /// <summary>Chỉ lấy tỉ lệ khung hình THẬT của trang (height/width) — KHÔNG rasterize bitmap
        /// (không FPDFBitmap_Create/FillRect/RenderPageBitmap), rẻ hơn nhiều so với RenderPageAsync.
        /// Chế độ Cuộn liên tục đọc khổ giấy mọi trang một lượt qua <see cref="GetPageSizesAsync"/>.</summary>
        public static async Task<double?> GetPageAspectRatioAsync(string pdfPath, int pageIndex, CancellationToken cancellationToken = default)
        {
            if (_shuttingDown) return null;
            Interlocked.Increment(ref _inFlightPublicCalls);
            PdfiumInstance? pdfium = null;
            try
            {
                if (_shuttingDown) return null;

                string normalized = NormalizePath(pdfPath);
                pdfium = ChooseInstance(normalized, -1); // không parse trang — bản nào đã mở document là đủ
                pdfium.AddLoad(1);
                using var usage = await AcquireDocumentAsync(normalized, pdfium, cancellationToken).ConfigureAwait(false);
                if (usage == null) return null;

                var lease = usage.Lease;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.RetiredToken);
                cancellationToken = linked.Token;
                if (pageIndex < 0 || pageIndex >= lease.PageCount) return null;

                using var native = await EnterPdfiumGateAsync(lease.Pdfium, PdfRenderPriority.Background, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return await Task.Run(() => GetPageAspectRatioCore(lease.Pdfium, lease.Document, pageIndex), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
            finally
            {
                pdfium?.AddLoad(-1);
                Interlocked.Decrement(ref _inFlightPublicCalls);
            }
        }

        /// <summary>Kích thước (point, đã tính /Rotate) của MỌI trang — như Chromium đọc hết khổ giấy lúc mở tài liệu để
        /// dựng bố cục chính xác (ContinuousPageLayout). Không parse nội dung trang (FPDF_GetPageSizeByIndex), 1 lần giữ
        /// gate cho cả file. null = không mở được. Trang không đọc được kích thước → (0, 0).</summary>
        public static async Task<(double Width, double Height)[]?> GetPageSizesAsync(string pdfPath, CancellationToken cancellationToken = default)
        {
            if (_shuttingDown) return null;
            Interlocked.Increment(ref _inFlightPublicCalls);
            PdfiumInstance? pdfium = null;
            try
            {
                if (_shuttingDown) return null;
                string normalized = NormalizePath(pdfPath);
                pdfium = ChooseInstance(normalized, -1);
                pdfium.AddLoad(1);
                using var usage = await AcquireDocumentAsync(normalized, pdfium, cancellationToken).ConfigureAwait(false);
                if (usage == null) return null;
                var lease = usage.Lease;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.RetiredToken);
                // Bố cục cần ngay để hiện đúng khổ giấy → ưu tiên Visible.
                using var native = await EnterPdfiumGateAsync(lease.Pdfium, PdfRenderPriority.Visible, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                return await Task.Run(() =>
                {
                    var sizes = new (double, double)[lease.PageCount];
                    for (int i = 0; i < sizes.Length; i++)
                        sizes[i] = lease.Pdfium.GetPageSizeByIndex(lease.Document, i, out double w, out double h) && w > 0 && h > 0
                            ? (w, h) : (0, 0);
                    return sizes;
                }, linked.Token).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
            finally
            {
                pdfium?.AddLoad(-1);
                Interlocked.Decrement(ref _inFlightPublicCalls);
            }
        }

        private static double? GetPageAspectRatioCore(PdfiumInstance pdfium, IntPtr document, int pageIndex)
        {
            return pdfium.GetPageSizeByIndex(document, pageIndex, out double width, out double height) && width > 0 && height > 0
                ? height / width : null;
        }

        /// <summary>Render một vùng tile của trang ở kích thước full-page đã zoom.</summary>
        public static async Task<BitmapSource?> RenderPageTileAsync(
            string pdfPath, int pageIndex, int fullWidth, int fullHeight, Int32Rect tileRect,
            CancellationToken cancellationToken = default, string? layerToken = null)
        {
            var results = await RenderPageTilesBatchAsync(pdfPath, pageIndex, fullWidth, fullHeight,
                new[] { tileRect }, cancellationToken, layerToken).ConfigureAwait(false);
            return results[0];
        }

        private static Task<PdfDocumentLease?> GetDocumentLeaseAsync(string normalizedPath, PdfiumInstance pdfium)
        {
            var lazy = _documentCache.GetOrAdd(
                new DocumentKey(normalizedPath, pdfium.Index),
                key => new Lazy<Task<PdfDocumentLease?>>(
                    () => Task.Run(() => LoadDocumentLease(key.Path, pdfium)),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            return lazy.Value;
        }

        /// <param name="expectedLayerToken">Trạng thái layer mà caller định dùng làm key cache kết quả. Khác
        /// trạng thái hiện tại (user vừa bật/tắt layer) → trả null thay vì vẽ nhầm trạng thái rồi cache sai key.
        /// null = không quan tâm layer (đếm trang, tỉ lệ trang).</param>
        private static async Task<PdfDocumentLease.DocumentUsage?> AcquireDocumentAsync(string normalized, PdfiumInstance pdfium,
            CancellationToken cancellationToken = default, string? expectedLayerToken = null)
        {
            var key = new DocumentKey(normalized, pdfium.Index);
            for (int attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_suspendedDocuments.ContainsKey(normalized)) return null;
                var leaseTask = GetDocumentLeaseAsync(normalized, pdfium);
                var lease = await leaseTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (lease == null) return null;

                string currentLayers = PdfLayerStateStore.GetToken(normalized);
                if (!string.Equals(lease.LayerToken, currentLayers, StringComparison.Ordinal))
                {
                    // Lease mở theo trạng thái layer cũ (retire chưa kịp tới) — bỏ nó, mở lại theo trạng thái mới.
                    if (_documentCache.TryGetValue(key, out var lazy) && lazy.IsValueCreated &&
                        ReferenceEquals(lazy.Value, leaseTask))
                        _documentCache.TryRemove(new KeyValuePair<DocumentKey, Lazy<Task<PdfDocumentLease?>>>(key, lazy));
                    lease.RequestDispose();
                    if (attempt < 2) continue;
                    return null;
                }
                if (expectedLayerToken != null && !string.Equals(expectedLayerToken, currentLayers, StringComparison.Ordinal))
                    return null;

                if (lease.TryAcquire(out var usage))
                {
                    _docLastUse[key] = Stopwatch.GetTimestamp();
                    return usage;
                }

                // A new request may have read the lease immediately before an idle-only retirement.
                // Retry that benign race; explicit source closure/editing still invalidates stale work.
                if (lease.IdlePolicyRetired && attempt < 2 && !_shuttingDown) continue;

                // Closing the document invalidates this request. A subsequent explicit open
                // obtains a fresh lease; stale work must not reopen the file in the background.
                return null;
            }
        }

        /// <summary>Mở document PDFium cho 1 file. File đang có layer bị user bật/tắt khác mặc định
        /// (PdfLayerStateStore) → mở bằng FPDF_LoadCustomDocument trên "file gốc + phần nối thêm đổi /D/ON,
        /// /D/OFF" (LayeredDocumentSource) — PDFium tự vẽ đúng trạng thái layer, hàm vẽ không phải đổi gì.</summary>
        private static PdfDocumentLease? LoadDocumentLease(string pdfPath, PdfiumInstance instance)
        {
            var cacheKey = new DocumentKey(pdfPath, instance.Index);
            IntPtr document = IntPtr.Zero;
            LayeredDocumentSource? source = null;
            // #0: PDFium đọc file qua bộ đệm khối (khối trang cần được đọc trước, phần còn lại nạp nền). null =
            // file quá lớn / hết ngân sách → FPDF_LoadDocument đọc file như cũ.
            PdfBlockCache? cache = PdfFileBuffer.Acquire(pdfPath);
            PdfiumInstance? pdfium = null;
            try
            {
                pdfium = instance;
                pdfium.EnsureInitialized();

                var hiddenLayers = PdfLayerStateStore.GetHiddenOverride(pdfPath, out string layerToken);
                if (hiddenLayers != null)
                {
                    // iText chạy NGOÀI gate PDFium (đọc /OCProperties có thể mất chút thời gian với file lớn).
                    try
                    {
                        byte[] tail = GetVisibilityTail(pdfPath, hiddenLayers, layerToken, cache, out long originalLength);
                        source = cache != null
                            ? new LayeredDocumentSource(cache, tail)
                            : new LayeredDocumentSource(pdfPath, originalLength, tail);
                        cache = null; // tham chiếu đã giao cho source
                    }
                    catch (Exception ex)
                    {
                        // Không dựng được bản đổi layer (file mã hoá, > 4 GB trên Windows…) → mở bình thường,
                        // vẫn gắn token để không bị coi là lease cũ và mở lại liên tục.
                        Debug.WriteLine("[PdfLayer] Fallback to default layer state: " + ex.Message);
                    }
                }
                if (source == null && cache != null)
                {
                    source = new LayeredDocumentSource(cache, Array.Empty<byte>());
                    cache = null;
                }

                using var native = EnterPdfiumGate(pdfium);
                long openStart = Stopwatch.GetTimestamp();
                _documentPasswords.TryGetValue(pdfPath, out string? password);
                document = source != null
                    ? pdfium.LoadCustomDocument(source.FileAccessPointer, password)
                    : pdfium.LoadDocument(pdfPath, password);
                RenderDiagnostics.DocumentOpen.Record(openStart);
                if (document == IntPtr.Zero)
                {
                    _openFailures[pdfPath] = MapOpenFailure(pdfium.GetLastError());
                    source?.Dispose();
                    _documentCache.TryRemove(cacheKey, out _);
                    return null;
                }

                int pageCount = pdfium.GetPageCount(document);
                if (pageCount <= 0)
                {
                    pdfium.CloseDocument(document);
                    document = IntPtr.Zero;
                    source?.Dispose();
                    _documentCache.TryRemove(cacheKey, out _);
                    return null;
                }

                _openFailures.TryRemove(pdfPath, out _);
                // Nguồn đọc (và bộ đệm khối) phải sống tới sau FPDF_CloseDocument: giao cho lease giữ.
                Interlocked.Increment(ref pdfium.OpenDocuments);
                return new PdfDocumentLease(pdfium, pdfPath, document, pageCount, layerToken, source);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Event("document open failed: " + ex);
                _openFailures[pdfPath] = PdfOpenFailure.Unknown;
                if (document != IntPtr.Zero && pdfium != null)
                {
                    using var native = EnterPdfiumGate(pdfium);
                    pdfium.CloseDocument(document);
                }
                source?.Dispose();
                _documentCache.TryRemove(cacheKey, out _);
                return null;
            }
            finally
            {
                cache?.Release(); // chỉ còn khác null khi chưa kịp giao cho source
            }
        }

        private static PdfOpenFailure MapOpenFailure(uint error) => error switch
        {
            2 => PdfOpenFailure.File,
            3 => PdfOpenFailure.Format,
            4 => PdfOpenFailure.Password,
            5 => PdfOpenFailure.Security,
            _ => PdfOpenFailure.Unknown
        };

        /// <summary>Phần nối thêm đổi /D/ON,/D/OFF theo trạng thái layer — dựng 1 lần (iText) rồi dùng lại cho mọi
        /// bản PDFium mở cùng file cùng trạng thái. Bỏ khi file sắp bị ghi (SuspendDocumentAsync) hoặc file đổi.</summary>
        private static readonly ConcurrentDictionary<string, (string Token, long Length, DateTime LastWriteUtc, byte[] Tail)> _layerTails =
            new(StringComparer.OrdinalIgnoreCase);

        private static byte[] GetVisibilityTail(string pdfPath, IReadOnlySet<string> hiddenLayers, string layerToken,
            PdfBlockCache? cache, out long originalLength)
        {
            var info = new FileInfo(pdfPath);
            if (_layerTails.TryGetValue(pdfPath, out var cached) && cached.Token == layerToken &&
                cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc)
            {
                originalLength = cached.Length;
                return cached.Tail;
            }
            byte[] tail = PdfLayerService.BuildVisibilityTail(pdfPath, hiddenLayers, out originalLength, cache);
            _layerTails[pdfPath] = (layerToken, originalLength, info.LastWriteTimeUtc, tail);
            return tail;
        }

        /// <summary>One page operation for a tile batch, with cooperative pause/cancel
        /// inside each tile and a global native gate released between render slices.</summary>
        public static async Task<List<BitmapSource?>> RenderPageTilesBatchAsync(
            string pdfPath,
            int pageIndex,
            int fullWidth,
            int fullHeight,
            IReadOnlyList<Int32Rect> tileRects, CancellationToken cancellationToken = default, string? layerToken = null,
            bool withAnnotations = false)
        {
            var results = new List<BitmapSource?>(tileRects.Count);
            for (int i = 0; i < tileRects.Count; i++) results.Add(null);
            if (_shuttingDown || tileRects.Count == 0) return results;

            Interlocked.Increment(ref _inFlightPublicCalls);
            PdfiumInstance? pdfium = null;
            try
            {
                if (_shuttingDown) return results;
                if (fullWidth <= 0 || fullHeight <= 0) return results;

                string normalized = NormalizePath(pdfPath);
                pdfium = ChooseInstance(normalized, pageIndex);
                pdfium.AddLoad(1);
                using var usage = await AcquireDocumentAsync(normalized, pdfium, cancellationToken, layerToken).ConfigureAwait(false);
                if (usage == null) return results;

                var lease = usage.Lease;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.RetiredToken);
                cancellationToken = linked.Token;
                if (pageIndex < 0 || pageIndex >= lease.PageCount) return results;

                await Task.Run(() => RenderTilesProgressiveAsync(lease, pageIndex, fullWidth, fullHeight,
                    tileRects, results, cancellationToken, withAnnotations), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // best-effort — các phần tử chưa render vẫn giữ null, gọi phía trên tự coi là cache-miss.
            }
            finally
            {
                if (pdfium != null)
                {
                    pdfium.AddLoad(-1);
                    pdfium.MarkCompleted();
                }
                Interlocked.Decrement(ref _inFlightPublicCalls);
            }
            return results;
        }

        private static async Task RequestLeaseDisposalWhenReadyAsync(Task<PdfDocumentLease?> leaseTask)
        {
            try
            {
                var lease = await leaseTask.ConfigureAwait(false);
                lease?.RequestDispose();
            }
            catch
            {
                // Best effort cleanup only.
            }
        }

        private static PdfiumGateLease EnterPdfiumGate(PdfiumInstance pdfium, [CallerMemberName] string caller = "")
        {
            Interlocked.Increment(ref _waitingNativeCalls);
            var sw = Stopwatch.StartNew();
            try
            {
                pdfium.Gate.Wait();
                sw.Stop();
                pdfium.GateWait.AddMilliseconds(sw.Elapsed.TotalMilliseconds);
                Interlocked.Increment(ref _activeNativeCalls);
                return new PdfiumGateLease(pdfium, caller, sw.ElapsedMilliseconds);
            }
            finally
            {
                Interlocked.Decrement(ref _waitingNativeCalls);
            }
        }

        private static async Task<PdfiumGateLease> EnterPdfiumGateAsync(PdfiumInstance pdfium, PdfRenderPriority priority = PdfRenderPriority.Visible, CancellationToken cancellationToken = default, [CallerMemberName] string caller = "")
        {
            Interlocked.Increment(ref _waitingNativeCalls);
            var sw = Stopwatch.StartNew();
            try
            {
                await pdfium.Gate.WaitAsync(priority, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                pdfium.GateWait.AddMilliseconds(sw.Elapsed.TotalMilliseconds);
                Interlocked.Increment(ref _activeNativeCalls);
                return new PdfiumGateLease(pdfium, caller, sw.ElapsedMilliseconds);
            }
            finally
            {
                Interlocked.Decrement(ref _waitingNativeCalls);
            }
        }

        private static string NormalizePath(string path)
            => Path.GetFullPath(path);

    }
}
