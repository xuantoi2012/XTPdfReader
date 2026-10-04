using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// Cache + hàng đợi render thumbnail dùng chung cho cả app (panel trang của cửa sổ đọc, Organizer của
    /// cửa sổ ghép). Trước đây là static trong MainWindow; tách ra để cửa sổ đọc (chủ app) không phụ thuộc
    /// cửa sổ ghép.
    /// </summary>
    internal static class ThumbnailCache
    {
        /// <summary>Ảnh render gốc LUÔN ở độ phân giải này — phải LỚN HƠN cỡ hiển thị to nhất (400px, "Rất lớn") + chừa dư cho màn hình DPI cao, nếu không ảnh bị phóng to từ nguồn nhỏ → mờ (đúng lỗi đã gặp). Đổi cỡ xem trước chỉ resize khung hiển thị, không render lại.</summary>
        internal const double RenderThumbnailWidthPx = 340;

        /// <summary>Cache theo (đường dẫn, số trang) — chuyển 1 trang qua lại giữa các
        /// window (copy) hay cuộn qua lại (virtualization tái dùng container) đều
        /// khỏi phải render lại PDF từ đầu, chỉ lấy lại ảnh đã có trong bộ nhớ.</summary>
        internal static readonly object _thumbnailLock = new();
        internal static long _thumbnailGeneration;
        internal const long ThumbnailCacheBudgetBytes = 160L * 1024 * 1024;
        internal static readonly BitmapMemoryCache<(string Path, int Page, string Layers)> _thumbnailCache = new(ThumbnailCacheBudgetBytes);
        internal static readonly Dictionary<(string Path, int Page, string Layers), Task<BitmapSource?>> _thumbnailLoads = new();
        internal const int ThumbnailRenderConcurrency = 4;
        internal const int ForegroundThumbnailConcurrency = 8;
        internal const int MaxForegroundThumbnailPending = 24;
        internal const int BackgroundThumbnailPrefetchConcurrency = 4;
        internal const int PrefetchConcurrencyPerGroup = 2;
        internal const int NearbyPrefetchConcurrency = 3;
        internal const int MaxBackgroundThumbnailInflight = 24;
        internal const int MaxForegroundRequestsBeforeBackground = 6;
        internal const int MaxForegroundRequestsBeforeNearbyPrefetch = 12;
        internal const int NearbyPrefetchPageCount = 28;
        internal const int InitialWarmThumbnailCount = 4;
        internal static readonly SemaphoreSlim _thumbnailRenderGate = new(ThumbnailRenderConcurrency);
        internal static readonly SemaphoreSlim _foregroundThumbnailGate = new(ForegroundThumbnailConcurrency);
        internal static readonly SemaphoreSlim _thumbnailPrefetchGate = new(BackgroundThumbnailPrefetchConcurrency);
        internal static int _activeThumbnailRenderCount;
        internal static int _foregroundThumbnailRequests;
        internal static int _activeForegroundThumbnailLoads;
        internal static int _backgroundPrefetchWaitingCount;

        internal static async Task LoadThumbnailFor(PageRow row)
        {
            if (!TryReserveThumbnailLoad(row)) return;

            var key = ThumbnailKey(row);
            if (TryGetCachedThumbnail(key, out var cached) && cached != null)
            {
                await SetRowThumbnailAsync(row, cached, key.Layers);
                return;
            }

            BitmapSource? bmp;
            try { bmp = await AwaitThumbnailLoadAsync(key, foreground: true); }
            catch
            {
                await ClearThumbnailQueuedAsync(row);
                return;
            }

            if (bmp == null)
            {
                await ClearThumbnailQueuedAsync(row);
                return;
            }

            await SetRowThumbnailAsync(row, bmp, key.Layers);
        }

        /// <summary>
        /// Tối ưu #1 "ảnh thấp trước, ảnh nét sau": ảnh 340 px của trang, vẽ ở mức ưu tiên Visible (không xếp
        /// sau hàng đợi thumbnail nền) — Viewer hiện tạm ngay (~parse + 65 ms trên bản vẽ CAD dày) trong lúc ảnh
        /// nét (~190 ms) đang vẽ. Dùng chung cache thumbnail nên panel trang cũng được lợi.
        /// <paramref name="cancellationToken"/>: trang đã rời màn hình trước khi kịp vẽ → bỏ (không chiếm PDFium của
        /// trang đang hiện). Lượt vẽ bị bỏ không vào cache; ai đang chờ chung lượt đó nhận null và tự xin lại khi cần.
        /// </summary>
        internal static async Task<BitmapSource?> LoadPreviewAsync(PageRow row, CancellationToken cancellationToken = default,
            PdfRenderPriority priority = PdfRenderPriority.Visible)
        {
            var key = ThumbnailKey(row);
            BitmapSource? bmp;
            if (TryGetCachedThumbnail(key, out var cached) && cached != null) bmp = cached;
            else
            {
                Task<BitmapSource?> task;
                lock (_thumbnailLock)
                {
                    if (!_thumbnailLoads.TryGetValue(key, out task!))
                    {
                        task = RenderAndCacheThumbnailAsync(key, priority, throttle: false, cancellationToken);
                        _thumbnailLoads[key] = task;
                    }
                }
                try { bmp = await task.WaitAsync(cancellationToken).ConfigureAwait(true); }
                catch { return null; }
            }
            if (bmp != null) await SetRowThumbnailAsync(row, bmp, key.Layers);
            return bmp;
        }

        internal static bool TryReserveThumbnailLoad(PageRow row)
        {
            if (row.Thumbnail != null || row.ThumbnailLoadQueued) return false;
            row.ThumbnailLoadQueued = true;
            return true;
        }

        /// <summary>Key cache thumbnail: kèm "phiên bản trạng thái layer" của file (PdfLayerStateStore) — bật/tắt
        /// layer đổi key nên không bao giờ lấy nhầm ảnh của trạng thái khác, và quay lại trạng thái cũ thì
        /// trúng lại ảnh đã vẽ.</summary>
        internal static (string Path, int Page, string Layers) ThumbnailKey(PageRow row)
            => RenderCacheKeys.Thumbnail(row.SourcePath, row.PageNumber);

        /// <summary>Gán thumbnail cho row nếu row chưa có — và CHỈ khi ảnh vẫn đúng trạng thái layer hiện tại
        /// (ảnh đang tải dở lúc user bật/tắt layer thì bỏ, không gán ảnh cũ lên).</summary>
        internal static async Task SetRowThumbnailAsync(PageRow row, BitmapSource bmp, string layers)
        {
            void Apply()
            {
                if (row.Thumbnail == null && string.Equals(PdfLayerStateStore.GetToken(row.SourcePath), layers, StringComparison.Ordinal))
                    row.Thumbnail = bmp;
                row.ThumbnailLoadQueued = false;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                Apply();
                return;
            }

            try
            {
                await dispatcher.InvokeAsync(Apply, DispatcherPriority.Background).Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // App đang đóng, Dispatcher shutdown giữa chừng khi lệnh này còn đang chờ tới lượt
                // — không còn ý nghĩa gì để gán thumbnail nữa, bỏ qua thay vì lộ ra thành
                // TaskCanceledException chưa bắt (crash lúc debug khi tắt app giữa lúc đang prefetch).
            }
        }

        internal static Task ClearThumbnailQueuedAsync(PageRow row)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                row.ThumbnailLoadQueued = false;
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(() => row.ThumbnailLoadQueued = false, DispatcherPriority.Background).Task;
        }

        internal static bool TryGetCachedThumbnail((string Path, int Page, string Layers) key, out BitmapSource? cached)
        {
            lock (_thumbnailLock)
            {
                if (_thumbnailCache.TryGetValue(key, out cached))
                {
                    return true;
                }

                return false;
            }
        }

        internal static (int Cache, int Inflight, long Bytes) GetThumbnailCacheStats()
        {
            lock (_thumbnailLock)
                return (_thumbnailCache.Count, _thumbnailLoads.Count, _thumbnailCache.Bytes);
        }

        internal static int GetThumbnailInflightCount()
        {
            lock (_thumbnailLock)
                return _thumbnailLoads.Count;
        }

        internal static bool IsThumbnailCachedOrLoading((string Path, int Page, string Layers) key)
        {
            lock (_thumbnailLock)
                return _thumbnailCache.ContainsKey(key) || _thumbnailLoads.ContainsKey(key);
        }

        internal static void CacheThumbnailLocked((string Path, int Page, string Layers) key, BitmapSource bmp)
            => _thumbnailCache.Set(key, bmp);

        internal static Task<BitmapSource?> GetThumbnailLoadTask((string Path, int Page, string Layers) key)
        {
            lock (_thumbnailLock)
            {
                if (_thumbnailCache.TryGetValue(key, out var cached))
                {
                    return Task.FromResult<BitmapSource?>(cached);
                }

                if (!_thumbnailLoads.TryGetValue(key, out var task))
                {
                    task = RenderAndCacheThumbnailAsync(key);
                    _thumbnailLoads[key] = task;
                }

                return task;
            }
        }

        internal static async Task<BitmapSource?> AwaitThumbnailLoadAsync((string Path, int Page, string Layers) key, bool foreground)
        {
            if (foreground)
                Interlocked.Increment(ref _foregroundThumbnailRequests);

            try
            {
                if (foreground)
                {
                    await _foregroundThumbnailGate.WaitAsync().ConfigureAwait(false);
                    Interlocked.Increment(ref _activeForegroundThumbnailLoads);
                }

                return await GetThumbnailLoadTask(key).ConfigureAwait(false);
            }
            finally
            {
                if (foreground)
                {
                    Interlocked.Decrement(ref _activeForegroundThumbnailLoads);
                    _foregroundThumbnailGate.Release();
                    Interlocked.Decrement(ref _foregroundThumbnailRequests);
                }
            }
        }

        internal static async Task WaitForBackgroundPrefetchTurnAsync(bool nearViewport, CancellationToken cancellationToken)
        {
            bool countedAsWaiting = false;
            int delayMs = 25;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int foregroundLimit = nearViewport
                        ? MaxForegroundRequestsBeforeNearbyPrefetch
                        : MaxForegroundRequestsBeforeBackground;
                    bool foregroundBusy = Volatile.Read(ref _foregroundThumbnailRequests) > foregroundLimit;
                    bool queueHasRoom = GetThumbnailInflightCount() < MaxBackgroundThumbnailInflight;
                    if (!foregroundBusy && queueHasRoom) return;

                    if (!countedAsWaiting)
                    {
                        Interlocked.Increment(ref _backgroundPrefetchWaitingCount);
                        countedAsWaiting = true;
                    }

                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    delayMs = Math.Min(160, delayMs + 15);
                }
            }
            finally
            {
                if (countedAsWaiting)
                    Interlocked.Decrement(ref _backgroundPrefetchWaitingCount);
            }
        }

        internal static async Task<BitmapSource?> RenderAndCacheThumbnailAsync((string Path, int Page, string Layers) key,
            PdfRenderPriority priority = PdfRenderPriority.Thumbnail, bool throttle = true, CancellationToken cancellationToken = default)
        {
            long generation = Interlocked.Read(ref _thumbnailGeneration);
            BitmapSource? bmp = null;
            // Ảnh tạm cho Viewer (throttle=false) không xếp hàng sau giới hạn 4 thumbnail đồng thời.
            if (throttle) await _thumbnailRenderGate.WaitAsync().ConfigureAwait(false);
            Interlocked.Increment(ref _activeThumbnailRenderCount);
            try
            {
                bmp = await Task.Run(() => PdfThumbnailService.RenderPageAsync(key.Path, key.Page - 1, RenderThumbnailWidthPx,
                    cancellationToken, priority: priority, layerToken: key.Layers)).ConfigureAwait(false);
                return bmp;
            }
            finally
            {
                Interlocked.Decrement(ref _activeThumbnailRenderCount);
                if (throttle) _thumbnailRenderGate.Release();
                lock (_thumbnailLock)
                {
                    if (bmp != null && generation == Interlocked.Read(ref _thumbnailGeneration)) CacheThumbnailLocked(key, bmp);
                    _thumbnailLoads.Remove(key);
                }
            }
        }


        /// <summary>Bỏ ảnh cache thoả điều kiện; mọi lần tải đang dở trước thời điểm này không được cache nữa.</summary>
        internal static void Invalidate(Func<(string Path, int Page, string Layers), bool> predicate)
        {
            Interlocked.Increment(ref _thumbnailGeneration);
            lock (_thumbnailLock)
            {
                _thumbnailCache.RemoveWhere(predicate);
                foreach (var key in new List<(string Path, int Page, string Layers)>(_thumbnailLoads.Keys))
                    if (predicate(key)) _thumbnailLoads.Remove(key);
            }
        }

        internal static (int Cache, int Inflight, long Bytes) Stats => GetThumbnailCacheStats();
    }
}
