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
    /// <summary>
    /// Direct PDFium renderer for the WPF thumbnail queue.
    /// The service keeps native PDF documents open only for active source files and
    /// returns frozen BitmapSource instances so they can safely cross worker threads.
    /// </summary>
    public static partial class PdfThumbnailService
    {
        private const int MaxRenderPixels = 24_000_000;
        private const int FpdfLcdText = 0x02;
        private const int FpdfNoNativeText = 0x04;
        private const int FpdfRenderLimitedImageCache = 0x200;
        private static readonly PdfRenderGate _pdfiumGate = new();
        private static int _activeNativeCalls;
        private static int _waitingNativeCalls;

        public static int ActiveNativeCalls => Volatile.Read(ref _activeNativeCalls);
        public static int WaitingNativeCalls => Volatile.Read(ref _waitingNativeCalls);

        private sealed class PdfiumGateLease : IDisposable
        {
            private readonly string _caller;
            private readonly long _waitMs;
            private readonly Stopwatch _held = Stopwatch.StartNew();

            public PdfiumGateLease(string caller, long waitMs)
            {
                _caller = caller;
                _waitMs = waitMs;
                RenderDiagnostics.NativeWait.AddMilliseconds(waitMs);
            }

            public void Dispose()
            {
                _held.Stop();
                Interlocked.Decrement(ref _activeNativeCalls);
                _pdfiumGate.Release();
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
            private readonly CancellationTokenSource _retired = new();
            public CancellationToken RetiredToken => _retired.Token;

            public PdfDocumentLease(string sourcePath, IntPtr document, int pageCount)
            {
                SourcePath = sourcePath;
                Document = document;
                PageCount = pageCount;
            }

            public string SourcePath { get; }
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
                if (Document == IntPtr.Zero) return;
                using var native = EnterPdfiumGate();
                RemoveDocumentPages(Document);
                FPDF_CloseDocument(Document);
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

        private static readonly Lazy<bool> _libraryInitialized = new(() =>
        {
            FPDF_InitLibrary();
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { FPDF_DestroyLibrary(); }
                catch { }
            };
            return true;
        }, LazyThreadSafetyMode.ExecutionAndPublication);

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

        private static readonly ConcurrentDictionary<string, Lazy<Task<PdfDocumentLease?>>> _documentCache =
            new(StringComparer.OrdinalIgnoreCase);

        public static int CachedDocumentCount => _documentCache.Count;

        public static async Task<int> GetPageCountAsync(string pdfPath)
        {
            if (_shuttingDown) return 0;
            try
            {
                using var usage = await AcquireDocumentAsync(pdfPath).ConfigureAwait(false);
                return usage?.Lease.PageCount ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        public static void ReleaseUnusedDocuments(IEnumerable<string> activePdfPaths)
        {
            var active = new HashSet<string>(
                activePdfPaths
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(NormalizePath),
                StringComparer.OrdinalIgnoreCase);

            foreach (var key in _documentCache.Keys)
            {
                if (active.Contains(key)) continue;

                if (_documentCache.TryRemove(key, out var lazy) && lazy.IsValueCreated)
                    _ = Task.Run(() => RequestLeaseDisposalWhenReadyAsync(lazy.Value));
            }
        }

        /// <summary>pageIndex is zero-based.</summary>
        public static async Task<BitmapSource?> RenderPageAsync(string pdfPath, int pageIndex, double maxWidth = 96, CancellationToken cancellationToken = default,
            PdfRenderPriority priority = PdfRenderPriority.Visible)
        {
            if (_shuttingDown) return null;
            Interlocked.Increment(ref _inFlightPublicCalls);
            try
            {
                // Kiểm tra lại: shutdown có thể vừa bắt đầu NGAY giữa lúc ta tăng đếm ở trên (trước
                // check này) và lúc PrepareForShutdown đọc _inFlightPublicCalls lần đầu — huỷ sớm nếu
                // vậy, đừng để lọt xuống AcquireDocumentAsync/PDFium nữa.
                if (_shuttingDown) return null;

                using var usage = await AcquireDocumentAsync(pdfPath, cancellationToken).ConfigureAwait(false);
                if (usage == null) return null;

                var lease = usage.Lease;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.RetiredToken);
                cancellationToken = linked.Token;
                if (pageIndex < 0 || pageIndex >= lease.PageCount) return null;

                return await Task.Run(() => RenderPageProgressiveAsync(lease, pageIndex, maxWidth,
                    priority, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlightPublicCalls);
            }
        }

        /// <summary>Chỉ lấy tỉ lệ khung hình THẬT của trang (height/width) — KHÔNG rasterize bitmap
        /// (không FPDFBitmap_Create/FillRect/RenderPageBitmap), rẻ hơn nhiều so với RenderPageAsync.
        /// Dùng để sửa placeholder height (xem ContinuousPagePlaceholderHeightConverter) cho ĐÚNG tỉ lệ
        /// trang thật NGAY khi trang được hiện thực hoá trong continuous reader, thay vì đợi bitmap
        /// (thumbnail/reader) tải xong — CAD workflow có nhiều khổ giấy khác A4 (A0/A1/A3 ngang...), tỉ
        /// lệ giả định 1.4142 sai khá xa với nhiều trang, gây nhảy chiều cao đột ngột không liên quan gì
        /// tới zoom khi bitmap thật load xong giữa lúc đang cuộn/zoom nhanh.</summary>
        public static async Task<double?> GetPageAspectRatioAsync(string pdfPath, int pageIndex, CancellationToken cancellationToken = default)
        {
            if (_shuttingDown) return null;
            Interlocked.Increment(ref _inFlightPublicCalls);
            try
            {
                if (_shuttingDown) return null;

                using var usage = await AcquireDocumentAsync(pdfPath, cancellationToken).ConfigureAwait(false);
                if (usage == null) return null;

                var lease = usage.Lease;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.RetiredToken);
                cancellationToken = linked.Token;
                if (pageIndex < 0 || pageIndex >= lease.PageCount) return null;

                using var native = await EnterPdfiumGateAsync(PdfRenderPriority.Background, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return await Task.Run(() => GetPageAspectRatioCore(lease.Document, pageIndex), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlightPublicCalls);
            }
        }

        private static double? GetPageAspectRatioCore(IntPtr document, int pageIndex)
        {
            return FPDF_GetPageSizeByIndex(document, pageIndex, out double width, out double height) != 0 && width > 0 && height > 0
                ? height / width : null;
        }

        /// <summary>Render một vùng tile của trang ở kích thước full-page đã zoom.</summary>
        public static async Task<BitmapSource?> RenderPageTileAsync(
            string pdfPath, int pageIndex, int fullWidth, int fullHeight, Int32Rect tileRect,
            CancellationToken cancellationToken = default)
        {
            var results = await RenderPageTilesBatchAsync(pdfPath, pageIndex, fullWidth, fullHeight,
                new[] { tileRect }, cancellationToken).ConfigureAwait(false);
            return results[0];
        }

        private static Task<PdfDocumentLease?> GetDocumentLeaseAsync(string pdfPath)
        {
            string normalized = NormalizePath(pdfPath);
            var lazy = _documentCache.GetOrAdd(
                normalized,
                path => new Lazy<Task<PdfDocumentLease?>>(
                    () => Task.Run(() => LoadDocumentLease(path)),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            return lazy.Value;
        }

        private static async Task<PdfDocumentLease.DocumentUsage?> AcquireDocumentAsync(string pdfPath, CancellationToken cancellationToken = default)
        {
            string normalized = NormalizePath(pdfPath);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lease = await GetDocumentLeaseAsync(normalized).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (lease == null) return null;
                if (lease.TryAcquire(out var usage)) return usage;

                // Closing the document invalidates this request. A subsequent explicit open
                // obtains a fresh lease; stale work must not reopen the file in the background.
                return null;
            }
        }

        private static PdfDocumentLease? LoadDocumentLease(string pdfPath)
        {
            IntPtr document = IntPtr.Zero;
            try
            {
                EnsurePdfiumInitialized();

                using var native = EnterPdfiumGate();
                long openStart = Stopwatch.GetTimestamp();
                document = FPDF_LoadDocument(pdfPath, null);
                RenderDiagnostics.DocumentOpen.Record(openStart);
                if (document == IntPtr.Zero)
                {
                    _documentCache.TryRemove(pdfPath, out _);
                    return null;
                }

                int pageCount = FPDF_GetPageCount(document);
                if (pageCount <= 0)
                {
                    FPDF_CloseDocument(document);
                    document = IntPtr.Zero;
                    _documentCache.TryRemove(pdfPath, out _);
                    return null;
                }

                return new PdfDocumentLease(pdfPath, document, pageCount);
            }
            catch
            {
                if (document != IntPtr.Zero)
                {
                    using var native = EnterPdfiumGate();
                    FPDF_CloseDocument(document);
                }
                _documentCache.TryRemove(pdfPath, out _);
                return null;
            }
        }

        /// <summary>One page operation for a tile batch, with cooperative pause/cancel
        /// inside each tile and a global native gate released between render slices.</summary>
        public static async Task<List<BitmapSource?>> RenderPageTilesBatchAsync(
            string pdfPath,
            int pageIndex,
            int fullWidth,
            int fullHeight,
            IReadOnlyList<Int32Rect> tileRects, CancellationToken cancellationToken = default)
        {
            var results = new List<BitmapSource?>(tileRects.Count);
            for (int i = 0; i < tileRects.Count; i++) results.Add(null);
            if (_shuttingDown || tileRects.Count == 0) return results;

            Interlocked.Increment(ref _inFlightPublicCalls);
            try
            {
                if (_shuttingDown) return results;
                if (fullWidth <= 0 || fullHeight <= 0) return results;

                using var usage = await AcquireDocumentAsync(pdfPath, cancellationToken).ConfigureAwait(false);
                if (usage == null) return results;

                var lease = usage.Lease;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.RetiredToken);
                cancellationToken = linked.Token;
                if (pageIndex < 0 || pageIndex >= lease.PageCount) return results;

                await Task.Run(() => RenderTilesProgressiveAsync(lease, pageIndex, fullWidth, fullHeight,
                    tileRects, results, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // best-effort — các phần tử chưa render vẫn giữ null, gọi phía trên tự coi là cache-miss.
            }
            finally
            {
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

        private static void EnsurePdfiumInitialized()
            => _ = _libraryInitialized.Value;

        private static PdfiumGateLease EnterPdfiumGate([CallerMemberName] string caller = "")
        {
            Interlocked.Increment(ref _waitingNativeCalls);
            var sw = Stopwatch.StartNew();
            try
            {
                _pdfiumGate.Wait();
                sw.Stop();
                Interlocked.Increment(ref _activeNativeCalls);
                return new PdfiumGateLease(caller, sw.ElapsedMilliseconds);
            }
            finally
            {
                Interlocked.Decrement(ref _waitingNativeCalls);
            }
        }

        private static async Task<PdfiumGateLease> EnterPdfiumGateAsync(PdfRenderPriority priority = PdfRenderPriority.Visible, CancellationToken cancellationToken = default, [CallerMemberName] string caller = "")
        {
            Interlocked.Increment(ref _waitingNativeCalls);
            var sw = Stopwatch.StartNew();
            try
            {
                await _pdfiumGate.WaitAsync(priority, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                Interlocked.Increment(ref _activeNativeCalls);
                return new PdfiumGateLease(caller, sw.ElapsedMilliseconds);
            }
            finally
            {
                Interlocked.Decrement(ref _waitingNativeCalls);
            }
        }

        private static string NormalizePath(string path)
            => Path.GetFullPath(path);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_InitLibrary();

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_DestroyLibrary();

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDF_LoadDocument")]
        private static extern IntPtr FPDF_LoadDocument(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_CloseDocument(IntPtr document);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDF_GetPageCount(IntPtr document);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDF_GetPageSizeByIndex(IntPtr document, int pageIndex, out double width, out double height);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_ClosePage(IntPtr page);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern double FPDF_GetPageWidth(IntPtr page);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern double FPDF_GetPageHeight(IntPtr page);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFBitmap_Destroy(IntPtr bitmap);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

        [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFBitmap_GetStride(IntPtr bitmap);

    }
}
