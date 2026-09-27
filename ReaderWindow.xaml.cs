using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;
using XTStyle.Controls;
using static XTPdfMergeApp.Services.VisualTreeHelpers;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;

namespace XTPdfMergeApp
{
    /// <summary>Docked PDF viewer control. Giữ tên ReaderWindow để tránh đổi lan rộng,
    /// nhưng không còn là top-level Window riêng.</summary>
    /// <summary>
    /// Cửa sổ chính của app = cửa sổ đọc. Là entry point thật: App.OnStartup tạo nó làm
    /// Application.MainWindow. Nó SỞ HỮU <see cref="DocumentSession"/> (file đang mở, undo/redo, sửa trang)
    /// và là nơi duy nhất tạo cửa sổ ghép phụ <see cref="MergeWorkspaceWindow"/>.
    /// </summary>
    public partial class ReaderWindow : XTWindow
    {
        /// <summary>Cửa sổ đọc đang chạy (app chỉ có 1).</summary>
        public static ReaderWindow? Instance { get; private set; }

        /// <summary>Phiên làm việc của app — cửa sổ ghép mượn qua constructor khi được mở.</summary>
        internal DocumentSession Session { get; }

        private readonly ObservableCollection<DocumentGroup> _groups;

        public ReaderWindow()
        {
            Instance = this;
            Session = new DocumentSession(Dispatcher);
            EditHost = Session;
            var groups = Session.Documents;
            InitializeComponent();
            _viewportRenderScheduler = new ViewportRenderScheduler(Dispatcher, () =>
                RunReaderTileRefreshAsync(Volatile.Read(ref _readerTileRequestId), _readerTileRefreshCts.Token));
            _tilePresentation = new FramePresentationQueue(ex => LogContinuousTileDebug($"Tile presentation failed: {ex.Message}"));
            _qualityRestoreTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                { Interval = TimeSpan.FromMilliseconds(150) };
            _qualityRestoreTimer.Tick += (_, _) =>
            {
                _qualityRestoreTimer.Stop();
                ReaderBitmapScalingMode = BitmapScalingMode.HighQuality;
            };
            _groups = groups;
            ReaderContentHost.LostMouseCapture += (_, _) => CancelHighlightDrag();
            // File đang xem bị đóng khỏi workspace (đóng hẳn, không phải chỉ xoá vài trang — trường hợp
            // đó qua NotifyPagesChanged) → chuyển sang file khác đang mở, hết file thì màn trống.
            _groups.CollectionChanged += (_, _) => OnGroupsChanged();
            InitializeShellParts();
            HookContinuousView();
            DiagnosticsReport.ViewerSection = GetViewerDiagnostics;
        }

        private DiagnosticsReport.ViewerStats GetViewerDiagnostics()
        {
            var reader = GetReaderCacheStats();
            var continuous = ReaderContinuousView.MemoryStats;
            int pageCount = _readerGroup?.Pages.Count ?? 0;
            int current = _readerGroup != null && _readerPage != null ? _readerGroup.Pages.IndexOf(_readerPage) : -1;
            return new DiagnosticsReport.ViewerStats(
                reader.Cache, reader.Bytes, reader.Inflight,
                _readerTileCache.Count, _readerTileCache.Bytes,
                continuous.Pages, continuous.Regions, continuous.RegionBytes,
                _readerContinuousMode ? "Cuộn liên tục" : "1 trang", _readerZoom, current, pageCount);
        }

        internal void ShutdownReader()
        {
            ReaderContinuousView.CancelAll();
            _readerTileRefreshCts.Cancel();
            _readerPageCts.Cancel();
            _readerPrefetchCts.Cancel();
            _readerRealtimeRenderCts?.Cancel();
            _viewportRenderScheduler.Dispose();
            _qualityRestoreTimer.Stop();
            _tilePresentation.Dispose();
        }

        /// <summary>True nếu Viewer đã từng hiện ít nhất 1 trang — dùng thay cho check
        /// "_readerPage == null" (giờ là field riêng, private) lúc quyết định có tự mở Viewer khi
        /// mở file đầu tiên trong phiên làm việc hay không.</summary>
        public bool HasAnyPageShown => _readerPage != null;

        /// <summary>Xoá bộ nhớ zoom riêng của group này (RemoveGroup_Click gọi khi user bỏ hẳn 1
        /// window PDF) — an toàn gọi kể cả khi Instance chưa từng mở (no-op).</summary>
        internal void NotifyGroupRemoved(DocumentGroup group) => _readerZoomByGroup.Remove(group);

        /// <summary>Phím tắt điều hướng/zoom khi Viewer đang mở — bỏ qua khi đang gõ trong ô
        /// nhập liệu (VD ReaderPageBox) để không cướp phím mũi tên/Enter của nó. Không còn cần
        /// gate theo Visibility như hồi còn panel nhúng — WPF tự phân luồng input theo ĐÚNG Window
        /// nào đang focus, phím tắt ở đây chỉ bao giờ tới tay khi Viewer thật sự đang active.</summary>
        private void ReaderWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox or ComboBox) return;

            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && (e.Key == Key.Z || e.Key == Key.Y))
            {
                if (e.Key == Key.Y || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) EditHost?.Redo();
                else EditHost?.Undo();
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.F12:
                    DiagnosticsWindow.ShowFor(this);
                    e.Handled = true;
                    break;
                case Key.Left:
                case Key.PageUp:
                    _ = NavigateReaderAsync(-1);
                    e.Handled = true;
                    break;
                case Key.Right:
                case Key.PageDown:
                    _ = NavigateReaderAsync(1);
                    e.Handled = true;
                    break;
                case Key.Home:
                    _ = NavigateReaderToIndexAsync(0);
                    e.Handled = true;
                    break;
                case Key.End:
                    if (_readerGroup != null) _ = NavigateReaderToIndexAsync(_readerGroup.Pages.Count - 1);
                    e.Handled = true;
                    break;
                case Key.Up:
                case Key.Down:
                    if (!_readerContinuousMode) break;
                    ReaderContinuousView.ScrollBy(0, (e.Key == Key.Up ? -1 : 1) * 100.0 / 3 * Math.Max(1, SystemParameters.WheelScrollLines));
                    e.Handled = true;
                    break;
                case Key.OemPlus:
                case Key.Add:
                    ReaderZoomIn_Click(this, e);
                    e.Handled = true;
                    break;
                case Key.OemMinus:
                case Key.Subtract:
                    ReaderZoomOut_Click(this, e);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    SetReaderTool(ReaderTool.Hand);
                    e.Handled = true;
                    break;
                case Key.D0:
                case Key.NumPad0:
                    ReaderFitWidth_Click(this, e);
                    e.Handled = true;
                    break;
            }
        }

        private readonly FramePresentationQueue _tilePresentation;
        private readonly DispatcherTimer _qualityRestoreTimer;
        public static readonly DependencyProperty ReaderBitmapScalingModeProperty = DependencyProperty.Register(
            nameof(ReaderBitmapScalingMode), typeof(BitmapScalingMode), typeof(ReaderWindow),
            new PropertyMetadata(BitmapScalingMode.HighQuality));
        public BitmapScalingMode ReaderBitmapScalingMode
        {
            get => (BitmapScalingMode)GetValue(ReaderBitmapScalingModeProperty);
            private set => SetValue(ReaderBitmapScalingModeProperty, value);
        }
        public int PendingTilePresentations => _tilePresentation.PendingCount;
        public double MaxTilePresentationMilliseconds => _tilePresentation.MaxBatchMilliseconds;

        private void MarkReaderInteraction()
        {
            PdfThumbnailService.NoteInteraction(); // #5: thumbnail/tải trước nhường gate PDFium cho vùng đang zoom/pan
            ReaderBitmapScalingMode = BitmapScalingMode.LowQuality;
            _qualityRestoreTimer.Stop();
            _qualityRestoreTimer.Start();
        }

        private enum ReaderZoomMode { Manual, FitWidth, FitPage }

        private const double ReaderRenderWidthPx = 2200;
        internal const long ReaderCacheBudgetBytes = 160L * 1024 * 1024;
        private const int ReaderAdjacentPrefetchCount = 2;
        private const double ReaderMinZoom = 0.05;
        private const double ReaderMaxZoom = 4.0;
        // 1.25 (25%/nấc) trước đây quá lớn — mỗi nấc lăn chuột nhảy ảnh rõ rệt, cảm giác giật cục.
        // Foxit/Chrome PDF dùng bước nhỏ hơn nhiều (~8-10%/nấc) để zoom mượt hơn.
        private const double ReaderZoomStep = 1.08;
        private static readonly object _readerCacheLock = new();
        private static readonly BitmapMemoryCache<(string Path, int Page, int Width, string Layers)> _readerCache = new(ReaderCacheBudgetBytes);
        private static readonly Dictionary<(string Path, int Page, int Width, string Layers), Task<BitmapSource?>> _readerLoads = new();
        private DocumentGroup? _readerGroup;
        private PageRow? _readerPage;
        private double _readerZoom = 1.0;
        private ReaderZoomMode _readerZoomMode = ReaderZoomMode.FitWidth;
        private long _readerRequestId;
        private CancellationTokenSource _readerPrefetchCts = new();

        /// <summary>Độ phân giải PX GỐC của bitmap ĐANG hiển thị trong ReaderImage — có thể
        /// là bản render cơ sở (ReaderRenderWidthPx) hoặc bản đã "nâng cấp" nét hơn khi zoom
        /// sâu (xem ScheduleReaderRealtimeRerender). Image dùng Stretch=Fill trên khung
        /// Width/Height cố định nên đổi bitmap không cần bù transform gì — chỉ đổi ĐỘ NÉT; giá
        /// trị này chỉ còn dùng để so ngưỡng cần render lại/tile hay chưa.</summary>
        private double _readerBitmapNativeWidthPx = ReaderRenderWidthPx;

        /// <summary>Tỉ lệ Cao/Rộng THẬT của trang (bất biến theo độ phân giải render) — dùng để
        /// tính Fit width/page theo kích thước BASELINE (ReaderRenderWidthPx) thay vì theo bitmap
        /// đang hiển thị thực tế, vì bitmap đó có thể đã bị "nâng cấp" độ phân giải.</summary>
        private double _readerPageAspect = 1.0;

        private CancellationTokenSource? _readerRealtimeRenderCts;

        private const double ReaderTileStartWidthPx = 3200;
        /// <summary>Layers = "phiên bản trạng thái layer" của file lúc yêu cầu tile (PdfLayerStateStore).</summary>
        private readonly record struct ReaderTileKey(string Path, int Page, int FullWidth, int FullHeight, int X, int Y, int Width, int Height, string Layers);

        private static bool IsCurrentLayerState(PageRow row, string layers)
            => string.Equals(PdfLayerStateStore.GetToken(row.SourcePath), layers, StringComparison.Ordinal);
        private readonly BitmapMemoryCache<ReaderTileKey> _readerTileCache = new(48L * 1024 * 1024);
        private readonly Dictionary<ReaderTileKey, Task<BitmapSource?>> _readerTileLoads = new();
        /// <summary>Token huỷ của từng lượt vẽ tile/vùng đang chạy — để bỏ riêng lượt có vùng đã ra khỏi màn hình.</summary>
        private readonly Dictionary<ReaderTileKey, CancellationTokenSource> _readerTileLoadCts = new();
        private readonly HashSet<ReaderTileKey> _visibleReaderTiles = new();
        private readonly HashSet<ReaderTileKey> _pendingReaderTiles = new();
        private long _readerTileRequestId;

        // ReaderTileCanvas/ReaderTileCanvasB — double buffering: canvas KHÔNG hiển thị dùng để chuẩn
        // Each buffer keeps its own geometry; incoming tiles cover the old image
        // progressively, and the fallback releases its images after completion.
        private bool _readerTileActiveIsA = true;
        private Canvas ActiveReaderTileCanvas => _readerTileActiveIsA ? ReaderTileCanvas : ReaderTileCanvasB;
        private Canvas InactiveReaderTileCanvas => _readerTileActiveIsA ? ReaderTileCanvasB : ReaderTileCanvas;
        private ScaleTransform ActiveReaderTileScaleTransform => _readerTileActiveIsA ? ReaderTileScaleTransform : ReaderTileScaleTransformB;
        private ScaleTransform InactiveReaderTileScaleTransform => _readerTileActiveIsA ? ReaderTileScaleTransformB : ReaderTileScaleTransform;
        private RotateTransform ActiveReaderTileRotateTransform => _readerTileActiveIsA ? ReaderTileRotateTransform : ReaderTileRotateTransformB;
        private RotateTransform InactiveReaderTileRotateTransform => _readerTileActiveIsA ? ReaderTileRotateTransformB : ReaderTileRotateTransform;

        // (fullWidth, fullHeight) hệ toạ độ mà lượt tile TRƯỚC đã dùng — đổi zoom là đổi luôn hệ này,
        // tile cũ (đặt Canvas.Left/Top/Width/Height theo hệ CŨ) phải bị xoá NGAY LẬP TỨC trước khi áp
        // scale MỚI cho ReaderTileScaleTransform, không thì có 1 khung hình tile cũ bị biến dạng sai vị
        // trí/kích thước (đúng bug "zoom chưa chính xác" — Canvas đổi scale tức thì nhưng con bên trong
        // vẫn còn từ hệ toạ độ cũ tới tận khi vòng async dọn dẹp chạy xong ở cuối UpdateReaderTilesAsync).
        private (int Width, int Height) _readerTileCoordSpace;
        private const double ReaderRealtimeRerenderFactor = 1.05; // render lại sớm hơn khi zoom vượt độ phân giải bitmap đang có
        private const double ReaderMaxRenderWidthPx = 7600; // chặn trần RAM/CPU khi zoom cực sâu

        // Lượng tử hoá độ phân giải NÉT của tile theo bậc 400px thay vì bám sát TỪNG GIÁ TRỊ ZOOM LIÊN
        // TỤC — nếu không, mỗi nấc zoom (dù chỉ nhích 1%) đều ra 1 "fullWidth" khác, ReaderTileKey đổi
        // theo, toàn bộ tile đang có bị coi là "khác hệ toạ độ" và phải render lại từ đầu (PDFium) —
        // log thực tế cho thấy đúng vậy: cacheHits=0 liên tục suốt lúc đang kéo zoom, chỉ có cacheHits
        // khi zoom đứng yên TUYỆT ĐỐI. Hàng chục lượt render PDF liên tiếp mỗi ~120ms chính là nguồn
        // giật khi zoom sâu (KHÔNG giật ở zoom nhỏ vì dưới ngưỡng tile, không đụng tới đường này).
        // Lượng tử hoá "fullWidth" (độ phân giải RENDER) về bậc rời rạc trong khi vẫn dùng zoom LIÊN
        // TỤC thật cho tileScale (kích thước HIỂN THỊ) — độ nét chỉ đổi theo bậc (Ítre-render hơn hẳn,
        // đúng cách Chrome/Acrobat làm: không rasterize lại ở MỌI mức zoom, chỉ ở vài mức rời rạc rồi
        // scale nhẹ qua GPU cho các mức ở giữa), còn kích thước hiển thị vẫn mượt theo đúng zoom hiện tại.
        private const double ReaderTileResolutionQuantumPx = 400;

        private static int QuantizeTileFullWidth(double fullWidthCapped)
            => Math.Max(1, (int)(Math.Ceiling(fullWidthCapped / ReaderTileResolutionQuantumPx) * ReaderTileResolutionQuantumPx));

        /// <summary>Chế độ xem: TRUE = cuộn liên tục qua TẤT CẢ trang (ReaderContinuousView),
        /// FALSE = 1 trang 1 lần (ReaderScrollViewer/ReaderImage như cũ). Toggle qua nút
        /// ReaderContinuousToggle — lăn chuột thường cuộn dọc mượt, Ctrl+lăn chuột mới zoom
        /// (khác chế độ 1-trang: ở đó lăn chuột thường ĐÃ LÀ zoom, vì không cần cuộn qua
        /// trang khác bằng lăn chuột nữa).</summary>
        private bool _readerContinuousMode;

        /// <summary>Xoay CHỈ ĐỂ XEM (0/90/180/270) — không đụng tới file PDF gốc, không
        /// ảnh hưởng lúc Lưu/merge. Reset về 0 mỗi khi đổi sang trang khác (xoay là tiện
        /// ích tạm thời cho ĐÚNG trang đang nhìn, không mang theo giữa các trang).</summary>
        private int _readerRotation;

        /// <summary>Nhớ zoom (mode + mức %) RIÊNG cho từng file (DocumentGroup) — chuyển
        /// qua lại giữa các window trong danh sách không bị mất mức zoom đang xem dở của
        /// từng file đó (khác hẳn trước đây: 1 biến _readerZoom DÙNG CHUNG cho mọi file).</summary>
        private readonly Dictionary<DocumentGroup, (ReaderZoomMode Mode, double Zoom)> _readerZoomByGroup = new();

        // Kéo để pan (giữ chuột trái HOẶC chuột giữa kéo, như Foxit/Word) — tách biệt
        // hẳn khỏi SmoothScrollBy (easing): pan cần bám NGAY theo vị trí chuột, dùng
        // easing ở đây sẽ tạo cảm giác trễ/"cao su" sai với thao tác kéo trực tiếp.
        private bool _readerPanning;
        private Point _readerPanStartMouse;
        private double _readerPanStartH, _readerPanStartV;
        private bool _readerPanFramePending;
        private ScrollViewer? _readerPanFrameScrollViewer;
        private double _readerPanFrameTargetH, _readerPanFrameTargetV;

        // Gộp NHIỀU nấc lăn chuột đến trong CÙNG 1 khung hình render thành ĐÚNG 1 lần áp
        // zoom+UpdateLayout+sửa điểm neo (giống cách trình duyệt/Figma coalesce input về 1 lần
        // mỗi khung hình thay vì làm việc lại từ đầu cho từng sự kiện wheel) — trước đây mỗi nấc
        // wheel gọi UpdateLayout() đồng bộ + 2 lượt "sửa điểm neo" (1 ngay, 1 ở Render priority)
        // riêng cho NẤC ĐÓ; lăn chuột nhanh (nhiều nấc/giây) khiến các lượt sửa của các nấc chồng
        // lấn, mỗi lượt đo lại vị trí trên 1 layout có thể chưa ổn định từ nấc trước → cảm giác
        // giật (nhiều lượt layout ép buộc/giây) và thỉnh thoảng neo sai điểm (lượt sửa trễ của nấc
        // N ghi đè lên kết quả đã đúng của nấc N+1). Gộp về 1 lần/khung hình vừa giảm hẳn số lần
        // ép layout, vừa đảm bảo chỉ có 1 chuỗi "áp zoom → sửa điểm neo" chạy tại một thời điểm.
        private bool _readerZoomFramePending;
        private double _readerZoomFramePendingTarget;
        private Point _readerZoomFrameAnchor;
        private static DateTime _lastContinuousTileDebugLog = DateTime.MinValue;
        private static readonly string ContinuousTileDebugLogPath =
            Path.Combine(Path.GetTempPath(), "XTPdfMergeApp_ContinuousTile.log");

        private static void LogContinuousTileDebug(string info)
        {
            if (!RenderDiagnostics.TraceEnabled) return;
            var now = DateTime.Now;
            if ((now - _lastContinuousTileDebugLog).TotalMilliseconds < 200) return;
            _lastContinuousTileDebugLog = now;
            try
            {
                string line = $"{now:HH:mm:ss.fff} | {info}";
                File.AppendAllText(ContinuousTileDebugLogPath, line + Environment.NewLine);
                Debug.WriteLine("[ContinuousTile] " + line);
            }
            catch
            {
                // best-effort debug log only
            }
        }

        internal static (int Cache, int Inflight, long Bytes) GetReaderCacheStats()
        {
            lock (_readerCacheLock) return (_readerCache.Count, _readerLoads.Count, _readerCache.Bytes);
        }

        internal static void ReleaseUnusedSources(HashSet<string> active)
        {
            lock (_readerCacheLock) _readerCache.RemoveWhere(key => !active.Contains(key.Path));
            Instance?._readerTileCache.RemoveWhere(key => !active.Contains(key.Path));
        }

        private CancellationTokenSource _readerPageCts = new();
        private double ReaderDpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;
        /// <summary>Độ phân giải ảnh trang của chế độ 1 trang (chế độ Cuộn liên tục: ContinuousPdfView tự tính theo từng trang).</summary>
        private int DesiredReaderWidth()
        {
            double width = _readerZoomMode == ReaderZoomMode.Manual ? ReaderRenderWidthPx * _readerZoom
                : Math.Max(640, ReaderScrollViewer.ViewportWidth);
            // Quantized keys avoid fresh renders for tiny resize/zoom changes. Logical page
            // coordinates stay at 2200 DIPs; only the backing bitmap resolution changes.
            return (int)Math.Clamp(Math.Ceiling(width * ReaderDpiScale / 256) * 256, 512, 2304);
        }

        /// <param name="width">Chiều rộng pixel cần vẽ; null = theo zoom chế độ 1 trang (<see cref="DesiredReaderWidth"/>).</param>
        private Task<BitmapSource?> GetReaderLoadTask((string Path, int Page) source, bool prefetch = false,
            CancellationToken cancellationToken = default, int? width = null)
        {
            // Key kèm "phiên bản trạng thái layer" của file — xem PdfLayerStateStore / MainWindow.ThumbnailKey.
            var key = RenderCacheKeys.ReaderPage(source.Path, source.Page, width ?? DesiredReaderWidth());
            var token = cancellationToken.CanBeCanceled ? cancellationToken : _readerPageCts.Token;
            lock (_readerCacheLock)
            {
                if (_readerCache.TryGetValue(key, out var cached)) return Task.FromResult<BitmapSource?>(cached);
                if (_readerLoads.TryGetValue(key, out var existing)) return existing;
                var completion = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _readerLoads[key] = completion.Task;
                _ = Task.Run(async () =>
                {
                    try { completion.TrySetResult(await RenderAndCacheReaderAsync(key, token, prefetch, completion.Task)); }
                    catch (Exception ex) { completion.TrySetException(ex); }
                });
                return completion.Task;
            }
        }

        private void ResetReaderSpeculativeWork(DocumentGroup group, PageRow row)
        {
            _readerPrefetchCts.Cancel();
            _readerPrefetchCts.Dispose();
            _readerPrefetchCts = new();

            int currentIndex = group.Pages.IndexOf(row);
            var keep = new List<(string Path, int Page)>();
            if (currentIndex >= 0)
            {
                for (int i = Math.Max(0, currentIndex - ReaderAdjacentPrefetchCount);
                     i <= Math.Min(group.Pages.Count - 1, currentIndex + ReaderAdjacentPrefetchCount);
                     i++)
                {
                    keep.Add((group.Pages[i].SourcePath, group.Pages[i].PageNumber));
                }
            }

            lock (_readerCacheLock)
            {
                _readerLoads.Clear();
                _readerCache.Trim(key => keep.Any(page =>
                    page.Page == key.Page &&
                    string.Equals(page.Path, key.Path, StringComparison.OrdinalIgnoreCase)));
            }
            PdfThumbnailService.ReleaseCachedPages();
        }

        private static async Task<BitmapSource?> RenderAndCacheReaderAsync(
            (string Path, int Page, int Width, string Layers) key, CancellationToken token, bool prefetch, Task<BitmapSource?> owner)
        {
            BitmapSource? bmp = null;
            try
            {
                bmp = await PdfThumbnailService.RenderPageAsync(key.Path, key.Page - 1, key.Width, token,
                    prefetch ? PdfRenderPriority.Background : PdfRenderPriority.Visible, key.Layers).ConfigureAwait(false);
                return token.IsCancellationRequested ? null : bmp;
            }
            finally
            {
                lock (_readerCacheLock)
                {
                    if (bmp != null && !token.IsCancellationRequested) _readerCache.Set(key, bmp);
                    if (_readerLoads.TryGetValue(key, out var current) && ReferenceEquals(current, owner))
                        _readerLoads.Remove(key);
                }
            }
        }

        /// <summary>Hiện docked Viewer trong MainWindow.</summary>
        public void ShowAndActivate()
        {
            MergeAppSettingsStore.SetViewerVisible(true);
            Visibility = Visibility.Visible;
            Focus();
        }

        /// <summary>Ẩn docked Viewer và dọn state đang xem.</summary>
        public void HideReader()
        {
            PdfThumbnailService.SetHotPages(Array.Empty<(string, int)>());
            CommitAnnotationEditor(cancel: true);
            CancelHighlightDrag();
            Interlocked.Increment(ref _readerRequestId);
            _qualityRestoreTimer.Stop();
            _tilePresentation.Clear();
            ReaderBitmapScalingMode = BitmapScalingMode.HighQuality;
            // Cửa sổ đọc là cửa sổ chính: không ẩn control nữa, chỉ về màn trống.
            _readerPageCts.Cancel();
            _readerPageCts.Dispose();
            _readerPageCts = new();
            _readerPrefetchCts.Cancel();
            _readerPrefetchCts.Dispose();
            _readerPrefetchCts = new();
            lock (_readerCacheLock) _readerCache.Clear();
            _readerTileCache.Clear();
            PdfThumbnailService.ReleaseCachedPages();
            ReaderImage.Source = null;
            ClearReaderTiles();
            ShowEmptyReaderState();
            ReaderContinuousView.SetDocument(null, ReaderContinuousView.Zoom);
            _readerRealtimeRenderCts?.Cancel();
            _readerGroup = null;
            _readerPage = null;
        }
        /// <summary>MainWindow gọi khi user chỉ ĐỔI SELECTION (không double-click) trong lúc Viewer
        /// đang mở — đồng bộ nội dung xem theo trang mới chọn, giữ nguyên mức zoom hiện tại. Không
        /// tự hiện dock — chỉ nên gọi khi đã biết Viewer đang hiện (IsVisible), vì Organizer là chức
        /// năng chính, chọn trang không tự mở Viewer.</summary>
        internal void NotifySelectionChanged(DocumentGroup group, PageRow page)
        {
            if (_readerContinuousMode)
            {
                ShowReaderContinuous(group, page);
                return;
            }

            _ = ShowPageAsync(group, page, preserveZoomMode: true);
        }

        internal void NotifyPagesChanged(DocumentGroup group)
        {
            if (!ReferenceEquals(_readerGroup, group)) return;

            if (!_groups.Contains(group) || group.Pages.Count == 0)
            {
                HideReader();
                return;
            }

            if (_readerPage != null && group.Pages.Contains(_readerPage))
            {
                UpdateReaderChrome(group, _readerPage);
                return;
            }

            if (_readerContinuousMode)
            {
                ShowReaderContinuous(group, group.Pages[0]);
                return;
            }

            _ = ShowPageAsync(group, group.Pages[0], preserveZoomMode: true);
        }
        internal async Task ShowPageAsync(DocumentGroup group, PageRow row, bool preserveZoomMode = true)
        {
            if (!Dispatcher.CheckAccess())
            {
                await Dispatcher.InvokeAsync(() => ShowPageAsync(group, row, preserveZoomMode)).Task.Unwrap();
                return;
            }

            ShowAndActivate();
            if (!ReferenceEquals(_readerPage, row)) CommitAnnotationEditor();
            bool groupChanged = !ReferenceEquals(_readerGroup, group);
            if (!ReferenceEquals(_readerPage, row))
            {
                _readerPageCts.Cancel();
                _readerPageCts.Dispose();
                _readerPageCts = new();
            }
            _readerGroup = group;
            _readerPage = row;
            long requestId = Interlocked.Increment(ref _readerRequestId);
            ResetReaderSpeculativeWork(group, row);
            ClearReaderTiles();

            if (!preserveZoomMode)
            {
                _readerZoomMode = ReaderZoomMode.FitWidth;
            }
            else if (groupChanged)
            {
                // Quay lại 1 file đã xem trước đó → khôi phục ĐÚNG mức zoom của
                // riêng file đó, thay vì luôn về Fit width như file mới toanh.
                if (_readerZoomByGroup.TryGetValue(group, out var saved))
                {
                    _readerZoomMode = saved.Mode;
                    _readerZoom = saved.Zoom;
                }
                else
                {
                    _readerZoomMode = ReaderZoomMode.FitWidth;
                }
            }

            _readerRotation = 0;
            ReaderRotateTransform.Angle = 0;
            ReaderTileRotateTransform.Angle = 0;
            ReaderTileRotateTransformB.Angle = 0;

            UpdateReaderChrome(group, row);
            // #3: chế độ 1 trang — giữ bản đã parse của trang đang xem và 2 trang kề (Trang trước/sau không parse lại).
            int hotIndex = group.Pages.IndexOf(row);
            PdfThumbnailService.SetHotPages(Enumerable.Range(hotIndex - 1, 3)
                .Where(i => i >= 0 && i < group.Pages.Count)
                .Select(i => (group.Pages[i].SourcePath, group.Pages[i].PageNumber)));
            BitmapSource? preview = row.ReaderDisplayBitmap;
            if (preview != null)
            {
                ReaderImage.Source = preview;
                _readerBitmapNativeWidthPx = preview.PixelWidth;
                _readerPageAspect = preview.PixelWidth > 0 ? preview.PixelHeight / (double)preview.PixelWidth : 1.0;
                ReaderEmptyText.Visibility = Visibility.Collapsed;
                ApplyReaderZoomMode(resetScroll: true);
            }
            else
            {
                ReaderEmptyText.Text = "Đang tải trang...";
                ReaderEmptyText.Visibility = Visibility.Visible;
                ReaderImage.Source = null;
                // #1: xin ảnh thấp (340 px, ưu tiên Visible) TRƯỚC ảnh nét — hiện ngay khi có, ảnh nét thay sau.
                _ = ShowLowResPreviewAsync(row, requestId);
            }

            var key = (row.SourcePath, row.PageNumber);
            string layers = PdfLayerStateStore.GetToken(row.SourcePath);
            var bmp = await GetReaderLoadTask(key);
            if (bmp == null && requestId == Volatile.Read(ref _readerRequestId)) bmp = await GetReaderLoadTask(key);
            if (requestId != Volatile.Read(ref _readerRequestId) || !ReferenceEquals(_readerPage, row)) return;
            if (!IsCurrentLayerState(row, layers)) return; // user vừa bật/tắt layer — OnLayerStateChanged tự vẽ lại

            if (bmp == null)
            {
                if (preview == null)
                {
                    ReaderEmptyText.Text = "Không render được trang này";
                    ReaderEmptyText.Visibility = Visibility.Visible;
                }
                return;
            }

            row.ReaderBitmap = bmp;
            ReaderImage.Source = bmp;
            _readerBitmapNativeWidthPx = bmp.PixelWidth;
            _readerPageAspect = bmp.PixelWidth > 0 ? bmp.PixelHeight / (double)bmp.PixelWidth : 1.0;
            _readerRealtimeRenderCts?.Cancel();
            ReaderEmptyText.Visibility = Visibility.Collapsed;
            ApplyReaderZoomMode(resetScroll: true);
            QueueReaderAdjacentPrefetch(group, row);
        }

        /// <summary>Tối ưu #1: hiện ảnh thấp trong lúc ảnh nét của trang đang vẽ. Bỏ qua nếu ảnh nét đã tới trước
        /// hoặc user đã chuyển trang.</summary>
        private async Task ShowLowResPreviewAsync(PageRow row, long requestId)
        {
            var preview = await ThumbnailCache.LoadPreviewAsync(row);
            if (preview == null || requestId != Volatile.Read(ref _readerRequestId) || !ReferenceEquals(_readerPage, row)) return;
            if (ReaderImage.Source != null) return; // ảnh nét đã hiện
            ReaderImage.Source = preview;
            _readerBitmapNativeWidthPx = preview.PixelWidth;
            _readerPageAspect = preview.PixelWidth > 0 ? preview.PixelHeight / (double)preview.PixelWidth : 1.0;
            ReaderEmptyText.Visibility = Visibility.Collapsed;
            ApplyReaderZoomMode(resetScroll: true);
        }

        private void UpdateReaderChrome(DocumentGroup group, PageRow row)
        {
            int position = group.Pages.IndexOf(row);
            ReaderTitleText.Text = $"{Path.GetFileName(row.SourcePath)} - trang nguồn {row.PageNumber}";
            ReaderPageBox.Text = position >= 0 ? (position + 1).ToString() : row.Index.ToString();
            ReaderPageTotalText.Text = $"/ {group.Pages.Count}";
            OnReaderCurrentPageChanged(group, row);
        }

        private void QueueReaderAdjacentPrefetch(DocumentGroup group, PageRow row)
        {
            int index = group.Pages.IndexOf(row);
            if (index < 0) return;

            for (int distance = 1; distance <= ReaderAdjacentPrefetchCount; distance++)
            {
                QueueReaderPrefetchAt(group, index - distance);
                QueueReaderPrefetchAt(group, index + distance);
            }
        }

        private void QueueReaderPrefetchAt(DocumentGroup group, int index)
        {
            if (index < 0 || index >= group.Pages.Count) return;
            var row = group.Pages[index];
            _ = GetReaderLoadTask((row.SourcePath, row.PageNumber), prefetch: true, _readerPrefetchCts.Token);
        }

        private async Task NavigateReaderAsync(int delta)
        {
            if (_readerGroup == null || _readerPage == null) return;

            int index = _readerGroup.Pages.IndexOf(_readerPage);
            if (index < 0) return;

            await NavigateReaderToIndexAsync(index + delta);
        }

        /// <summary>Tới trang thứ <paramref name="index"/> (0-based, tự kẹp) — Home/End, PageUp/PageDown.</summary>
        private async Task NavigateReaderToIndexAsync(int index)
        {
            if (_readerGroup == null || _readerPage == null || _readerGroup.Pages.Count == 0) return;
            int nextIndex = Math.Clamp(index, 0, _readerGroup.Pages.Count - 1);
            if (ReferenceEquals(_readerGroup.Pages[nextIndex], _readerPage)) return;

            var target = _readerGroup.Pages[nextIndex];
            if (_readerContinuousMode)
            {
                ScrollReaderContinuousTo(target);
                return;
            }

            await ShowPageAsync(_readerGroup, target, preserveZoomMode: true);
        }

        private async Task TryNavigateReaderPageFromBoxAsync()
        {
            if (_readerGroup == null) return;

            if (!int.TryParse(ReaderPageBox.Text.Trim(), out int pagePosition))
            {
                if (_readerPage != null)
                    UpdateReaderChrome(_readerGroup, _readerPage);
                return;
            }

            pagePosition = Math.Clamp(pagePosition, 1, _readerGroup.Pages.Count);
            var target = _readerGroup.Pages[pagePosition - 1];
            if (_readerContinuousMode)
            {
                ScrollReaderContinuousTo(target);
                return;
            }

            await ShowPageAsync(_readerGroup, target, preserveZoomMode: true);
        }

        private void ApplyReaderZoomMode(bool resetScroll = false)
        {
            if (ReaderImage.Source is not BitmapSource) return;

            var (viewportWidth, viewportHeight) = ReaderViewportContentSize();

            // Luôn tính theo kích thước BASELINE (ReaderRenderWidthPx x tỉ lệ trang thật,
            // _readerPageAspect) — KHÔNG theo bitmap đang hiển thị thực tế (có thể đã được
            // "nâng cấp" nét hơn ở độ phân giải cao hơn lúc zoom sâu, xem
            // ScheduleReaderRealtimeRerender) — để _readerZoom luôn mang đúng 1 ý nghĩa cố
            // định bất kể đang hiện bản thường hay bản đã nâng cấp.
            var (effectiveWidth, effectiveHeight) = ReaderZoomMath.EffectivePageSize(ReaderRenderWidthPx, _readerPageAspect, _readerRotation);

            if (_readerZoomMode == ReaderZoomMode.FitWidth)
                SetReaderZoom(ReaderZoomMath.FitWidthZoom(viewportWidth, effectiveWidth), ReaderZoomMode.FitWidth);
            else if (_readerZoomMode == ReaderZoomMode.FitPage)
                SetReaderZoom(ReaderZoomMath.FitPageZoom(viewportWidth, viewportHeight, effectiveWidth, effectiveHeight), ReaderZoomMode.FitPage);
            else
                SetReaderZoom(_readerZoom, ReaderZoomMode.Manual);

            if (resetScroll)
            {
                ReaderScrollViewer.ScrollToHorizontalOffset(0);
                ReaderScrollViewer.ScrollToVerticalOffset(0);
            }
        }

        /// <summary>Kích thước viewport THỰC (đã trừ Padding của ReaderScrollViewer) dùng xuyên
        /// suốt cho mọi phép tính Fit/neo zoom — tách thành 1 hàm để ApplyReaderZoomMode,
        /// ApplyReaderLayout và ZoomReaderAtPoint luôn dùng ĐÚNG 1 công thức, không lệch nhau.</summary>
        private (double Width, double Height) ReaderViewportContentSize() => (
            Math.Max(1, ReaderScrollViewer.ViewportWidth - ReaderScrollViewer.Padding.Left - ReaderScrollViewer.Padding.Right),
            Math.Max(1, ReaderScrollViewer.ViewportHeight - ReaderScrollViewer.Padding.Top - ReaderScrollViewer.Padding.Bottom));

        private void SetReaderZoom(double zoom)
            => SetReaderZoom(zoom, ReaderZoomMode.Manual);

        private void SetReaderZoom(double zoom, ReaderZoomMode mode)
        {
            _readerZoomMode = mode;
            double newZoom = ReaderZoomMath.Clamp(zoom, ReaderMinZoom, ReaderMaxZoom);
            bool zoomActuallyChanged = Math.Abs(newZoom - _readerZoom) > 0.0001;
            _readerZoom = newZoom;
            ApplyReaderLayout();
            ReaderZoomText.Text = $"{_readerZoom * 100:0}%";

            if (_readerGroup != null)
                _readerZoomByGroup[_readerGroup] = (_readerZoomMode, _readerZoom);

            // Zoom OUT xuống dưới ngưỡng cần tile → co NGAY (đồng bộ) ReaderTileCanvas, không đợi
            // ScheduleReaderTileRefresh (async, trễ 1 nhịp) mới co.
            if (_readerZoom * ReaderRenderWidthPx * ReaderDpiScale < ReaderTileStartWidthPx)
                ClearReaderTiles(invalidate: false);

            ScheduleReaderRealtimeRerender();

            // CHỈ đặt lịch debounce "render lại cho nét" khi zoom THẬT SỰ đổi giá trị — không thì
            // lăn chuột vẫn bắn sự kiện dù đã kẹp trần/không còn đổi (newZoom giống hệt _readerZoom
            // cũ, chỉ là kẹp lại đúng số cũ) sẽ liên tục HUỶ + ĐẶT LẠI hẹn giờ debounce, không bao
            // giờ đủ 200ms yên tĩnh THẬT SỰ để nó tới hạn — log thực tế: đứng yên ở zoom=4.00 suốt
            // 7 giây vẫn chưa load hết tile, vì mỗi lần chuột vẫn bắn (dù trị số không đổi) lại reset
            // debounce từ đầu.
            if (zoomActuallyChanged)
            {
                MarkReaderInteraction();
                ScheduleReaderTileRefresh(resolutionChanged: true);
            }
        }

        /// <summary>Tính lại TOÀN BỘ vị trí/kích thước hiển thị trang đang xem (kích thước "sizer"
        /// ReaderContentCanvas cấp cho ScrollViewer + vị trí ảnh + overlay tile) — THUẦN CÔNG THỨC
        /// từ 3 con số zoom/rotation/aspect hiện có, KHÔNG đọc lại vị trí đã đo qua layout (khác hẳn
        /// cách cũ dùng LayoutTransform + UpdateLayout() + TranslatePoint đo lại sau khi zoom). Nhờ
        /// vậy đổi zoom/rotation chỉ cần gọi hàm này 1 LẦN DUY NHẤT là ảnh + vùng cuộn đã đúng ngay
        /// trong cùng 1 khung hình — không có bước "áp tạm rồi tự sửa lại" nên không còn hiện tượng
        /// nhảy/giật giữa 2 khung hình như cách cũ. Đây đúng là cách Chrome PDF viewer/Acrobat làm:
        /// transform chỉ tác động lúc VẼ (RenderTransform, không đụng đo-layout), phần cần layout thật
        /// (kích thước cuộn) được tính trước bằng công thức rồi áp thẳng.</summary>
        private void ApplyReaderLayout()
        {
            if (ReaderImage.Source is not BitmapSource) return;

            var layout = ComputeReaderEffectiveLayout();

            ReaderContentCanvas.Width = layout.SizerWidth;
            ReaderContentCanvas.Height = layout.SizerHeight;

            ReaderScaleTransform.ScaleX = _readerZoom;
            ReaderScaleTransform.ScaleY = _readerZoom;
            ReaderRotateTransform.Angle = _readerRotation;

            double nativeWidth = ReaderRenderWidthPx;
            double nativeHeight = ReaderRenderWidthPx * _readerPageAspect;
            ReaderImage.Width = nativeWidth;
            ReaderImage.Height = nativeHeight;
            Canvas.SetLeft(ReaderImage, layout.TargetX - nativeWidth / 2 + layout.EffectiveWidth / 2);
            Canvas.SetTop(ReaderImage, layout.TargetY - nativeHeight / 2 + layout.EffectiveHeight / 2);

            PositionReaderTileCanvas(layout);
            UpdateReaderTileScaleForCurrentZoom();
        }

        /// <summary>Cập nhật ĐỘ PHÓNG của overlay tile theo ĐÚNG zoom hiện tại — chạy ở MỌI nấc zoom
        /// (rẻ, chỉ gán 1 giá trị transform, không đụng PDFium/render gì) — TÁCH HẲN khỏi việc quyết
        /// định có cần RENDER LẠI tile ở độ phân giải khác hay không (việc đó nặng, chỉ nên làm khi
        /// zoom đã DỪNG hẳn, xem ScheduleReaderTileRefresh). Nhờ tách 2 việc này, tile ĐANG CÓ luôn
        /// scale mượt theo đúng zoom sống động — không cần chờ debounce mới "trông đúng cỡ", và
        /// KHÔNG cần xoá/vẽ lại tile chỉ vì zoom nhích — đúng cách Foxit/Chrome PDF làm: zoom mượt =
        /// scale tức thời nội dung ĐANG CÓ, còn nét-lại-cho-đúng-độ-phân-giải là việc riêng, ít khi
        /// xảy ra hơn nhiều.</summary>
        private void UpdateReaderTileScaleForCurrentZoom()
        {
            foreach (var (canvas, scale) in new[] {
                (ReaderTileCanvas, ReaderTileScaleTransform),
                (ReaderTileCanvasB, ReaderTileScaleTransformB) })
            {
                if (canvas.Visibility != Visibility.Visible || canvas.Width <= 0 || scale.IsFrozen) continue;
                scale.ScaleX = scale.ScaleY = ReaderRenderWidthPx * _readerZoom / canvas.Width;
            }
        }

        /// <summary>Kích thước/vị trí "hiệu dụng" (đã xoay + zoom) của trang đang xem, và kích thước
        /// "sizer" ScrollViewer cần để vừa centered khi nhỏ hơn viewport / vừa cuộn được khi lớn hơn —
        /// TÍNH TOÁN THUẦN, không phụ thuộc bất kỳ giá trị đã đo qua layout nào ngoài ViewportWidth/
        /// Height hiện có (đọc property thường, không cần UpdateLayout()).</summary>
        private readonly record struct ReaderEffectiveLayout(
            double EffectiveWidth, double EffectiveHeight,
            double SizerWidth, double SizerHeight,
            double TargetX, double TargetY);

        private ReaderEffectiveLayout ComputeReaderEffectiveLayout()
        {
            var (effW0, effH0) = ReaderZoomMath.EffectivePageSize(ReaderRenderWidthPx, _readerPageAspect, _readerRotation);
            double effW = effW0 * _readerZoom, effH = effH0 * _readerZoom;
            var (viewportW, viewportH) = ReaderViewportContentSize();
            double sizerW = Math.Max(viewportW, effW), sizerH = Math.Max(viewportH, effH);
            return new ReaderEffectiveLayout(effW, effH, sizerW, sizerH, (sizerW - effW) / 2, (sizerH - effH) / 2);
        }

        /// <summary>Overlay tile (bản render nét hơn khi zoom sâu, xem UpdateReaderTilesAsync) phải
        /// phủ ĐÚNG lên vùng ảnh baseline đang hiển thị — dùng lại chung layout.EffectiveWidth/Height/
        /// TargetX/Y đã tính cho ảnh chính, chỉ khác kích thước khối gốc (fullWidth/fullHeight của tile
        /// thay vì baseline width) nên công thức định tâm giống hệt ApplyReaderLayout ở trên. Định vị
        /// CẢ 2 canvas (A/B, retained tile layers) — canvas nào đang KHÔNG hiển thị (Visibility
        /// != Visible) tự bỏ qua, không tốn gì thêm.</summary>
        private void PositionReaderTileCanvas(ReaderEffectiveLayout layout)
        {
            PositionOneReaderTileCanvas(ReaderTileCanvas, ReaderTileRotateTransform, layout);
            PositionOneReaderTileCanvas(ReaderTileCanvasB, ReaderTileRotateTransformB, layout);
        }

        private void PositionOneReaderTileCanvas(Canvas tileCanvas, RotateTransform rotateTransform, ReaderEffectiveLayout layout)
        {
            if (tileCanvas.Visibility != Visibility.Visible) return;
            double fullWidth = tileCanvas.Width, fullHeight = tileCanvas.Height;
            if (fullWidth <= 0 || fullHeight <= 0) return;

            rotateTransform.Angle = _readerRotation;
            Canvas.SetLeft(tileCanvas, layout.TargetX - fullWidth / 2 + layout.EffectiveWidth / 2);
            Canvas.SetTop(tileCanvas, layout.TargetY - fullHeight / 2 + layout.EffectiveHeight / 2);
        }

        /// <summary>Zoom sâu quá độ phân giải bitmap đang có (>115%) → âm thầm render lại ở
        /// độ phân giải khớp với zoom hiện tại (debounce 200ms để không render dồn dập khi
        /// đang lăn chuột liên tục) — fix "zoom to quá thì vẫn mờ" vì trước đây zoom chỉ là
        /// phóng to bitmap cố định 2200px bằng ScaleTransform (nội suy), không render lại.</summary>
        private void ScheduleReaderRealtimeRerender()
        {
            if (_readerGroup == null || _readerPage == null) return;

            _readerRealtimeRenderCts?.Cancel();
            double neededWidthPx = ReaderRenderWidthPx * _readerZoom * ReaderDpiScale;
            if (neededWidthPx <= _readerBitmapNativeWidthPx * ReaderRealtimeRerenderFactor) return;
            if (neededWidthPx >= ReaderTileStartWidthPx)
            {
                _readerRealtimeRenderCts?.Cancel();
                ScheduleReaderTileRefresh();
                return;
            }

            double targetWidthPx = Math.Min(neededWidthPx, ReaderMaxRenderWidthPx);
            if (targetWidthPx <= _readerBitmapNativeWidthPx * ReaderRealtimeRerenderFactor) return; // đã chạm trần, bitmap hiện có đủ rồi

            _readerRealtimeRenderCts?.Cancel();
            var cts = new CancellationTokenSource();
            _readerRealtimeRenderCts = cts;
            _ = RunReaderRealtimeRerenderAsync(_readerGroup, _readerPage, targetWidthPx, cts.Token);
        }

        private async Task RunReaderRealtimeRerenderAsync(DocumentGroup group, PageRow row, double widthPx, CancellationToken token)
        {
            try { await Task.Delay(120, token).ConfigureAwait(true); }
            catch (OperationCanceledException) { return; }
            if (token.IsCancellationRequested) return;
            if (!ReferenceEquals(_readerGroup, group) || !ReferenceEquals(_readerPage, row)) return;

            BitmapSource? bmp;
            string layers = PdfLayerStateStore.GetToken(row.SourcePath);
            try { bmp = await Task.Run(() => PdfThumbnailService.RenderPageAsync(row.SourcePath, row.PageNumber - 1, widthPx, token,
                layerToken: layers), token).ConfigureAwait(true); }
            catch (OperationCanceledException) { return; }

            if (token.IsCancellationRequested || bmp == null || !IsCurrentLayerState(row, layers)) return;
            if (!ReferenceEquals(_readerGroup, group) || !ReferenceEquals(_readerPage, row)) return;

            // Image dùng Stretch=Fill trên khung Width/Height cố định (ReaderRenderWidthPx) nên đổi
            // sang bitmap độ phân giải cao hơn KHÔNG cần đụng transform/vị trí gì — chỉ đổi độ nét.
            ReaderImage.Source = bmp;
            _readerBitmapNativeWidthPx = bmp.PixelWidth;

            // Bitmap zoom-sâu chỉ giữ cho trang đang xem; không đẩy vào cache chung để tránh giữ RAM lớn quá lâu.
        }

        // ── Chế độ Cuộn liên tục — TẤT CẢ trang của 1 window xếp dọc, cuộn mượt qua lại ──

        // One cancellation generation per resolution/page. Pan reuses the current
        // generation so continuous motion does not repeatedly cancel useful tile work.
        private CancellationTokenSource _readerTileRefreshCts = new();
        private readonly ViewportRenderScheduler _viewportRenderScheduler;

        // Đảm bảo KHÔNG BAO GIỜ có 2 lượt UpdateReaderTilesAsync chạy CHỒNG LẤN nhau — quan trọng vì
        // PDFium bị khoá 1 luồng cho TOÀN BỘ ứng dụng (native library không an toàn gọi đồng thời, xem
        // PdfiumInstance.Gate) nên tải hết 1 bộ tile (12-25 viên) có thể mất VÀI GIÂY,
        // lâu hơn hẳn 200ms debounce — nếu zoom vẫn tiếp tục đổi trong lúc đó, lượt debounce MỚI sẽ
        // khởi động trong khi lượt CŨ còn dở dang (đang await PDFium), 2 lượt cùng đọc/ghi
        // _continuousTileActiveIsA (canvas nào đang active cho từng trang) ĐỘC LẬP nhau → tráo canvas
        // sai/chồng chéo, gây "nhảy loạn xạ" đã gặp. Semaphore này xếp hàng lượt MỚI chờ lượt CŨ xong
        // hẳn rồi mới bắt đầu (với dữ liệu MỚI NHẤT tại thời điểm đó), không bao giờ chạy cùng lúc.
        private readonly SemaphoreSlim _readerTileUpdateGate = new(1, 1);

        private readonly ViewportMotionTracker _viewportMotion = new();

        private void ScheduleReaderTileRefresh(bool resolutionChanged = false)
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(() => ScheduleReaderTileRefresh(resolutionChanged), DispatcherPriority.Background);
                return;
            }
            // Chế độ Cuộn liên tục: ContinuousPdfView tự xin ảnh/vùng nét cho trang đang hiện.
            if (!IsVisible || _readerPage == null || _readerContinuousMode) return;
            var scroll = ReaderScrollViewer;
            bool moved = _viewportMotion.Update(
                new Point(scroll.HorizontalOffset, scroll.VerticalOffset),
                new Size(Math.Max(0, scroll.ViewportWidth), Math.Max(0, scroll.ViewportHeight)), resolutionChanged);
            if (resolutionChanged || moved) InvalidateReaderTileWork();
            _viewportRenderScheduler.Request(resolutionChanged);
        }

        private void InvalidateReaderTileWork()
        {
            Interlocked.Increment(ref _readerTileRequestId);
            _readerTileRefreshCts.Cancel();
            _readerTileRefreshCts.Dispose();
            _readerTileRefreshCts = new();
            _readerTileLoads.Clear();
            _readerTileLoadCts.Clear(); // đã huỷ chung qua _readerTileRefreshCts
            _tilePresentation.Clear();
            _pendingReaderTiles.Clear();
            _readerTileCache.Trim(key => _visibleReaderTiles.Contains(key));
        }

        /// <summary>Thời gian CHỜ _readerTileUpdateGate của lượt refresh gần nhất — đo riêng ở đây
        /// (không phải bên trong UpdateReaderContinuousTilesAsync) vì đây là thời gian XẾP HÀNG, khác
        /// với thời gian XỬ LÝ thật bên trong; tách 2 số này ra mới biết "chậm vì đang chờ lượt trước
        /// xong" hay "chậm vì chính lượt này xử lý lâu" — 2 nguyên nhân cần fix khác hẳn nhau.</summary>
        private long _lastTileGateWaitMs;

        private async Task RunReaderTileRefreshAsync(long requestId, CancellationToken token)
        {
            if (token.IsCancellationRequested) return;

            var gateWaitSw = Stopwatch.StartNew();
            try { await _readerTileUpdateGate.WaitAsync(token).ConfigureAwait(true); }
            catch (OperationCanceledException) { return; }
            _lastTileGateWaitMs = gateWaitSw.ElapsedMilliseconds;
            try
            {
                // Có thể đã có 1 lượt MỚI HƠN chạy xong trong lúc ta xếp hàng chờ gate (lượt CŨ vẫn
                // đang tải PDFium) — bỏ qua nếu đã lỗi thời, không làm lại việc dữ liệu cũ.
                if (requestId != Volatile.Read(ref _readerTileRequestId))
                {
                    if (_lastTileGateWaitMs > 50)
                        LogContinuousTileDebug($"requestId={requestId} SKIP stale sau khi chờ gate {_lastTileGateWaitMs}ms (lượt trước xử lý lâu hơn debounce 200ms)");
                    return;
                }

                // Exception ở đây trước giờ thành "unobserved task exception" (gọi qua fire-and-forget)
                // — .NET hiện đại KHÔNG crash app vì việc này mà chỉ âm thầm nuốt mất; đúng lỗi thật đã
                // bắt được (ScaleTransform bị Freeze) nhờ log ở đây, giữ lại phòng còn sót lỗi tương tự.
                await UpdateReaderTilesAsync(requestId);
            }
            catch (Exception ex)
            {
                LogContinuousTileDebug($"EXCEPTION in UpdateReaderTilesAsync: {ex}");
            }
            finally
            {
                _readerTileUpdateGate.Release();
            }
        }

        private async Task UpdateReaderTilesAsync(long requestId)
        {
            if (requestId != Volatile.Read(ref _readerTileRequestId))
            {
                LogContinuousTileDebug($"UpdateReaderTilesAsync SKIP stale requestId={requestId} current={Volatile.Read(ref _readerTileRequestId)}");
                return;
            }
            if (_readerContinuousMode) return;

            if (_readerRotation != 0 || _readerPage == null || ReaderImage.Source is not BitmapSource)
            {
                ClearReaderTiles(invalidate: false);
                return;
            }

            double requestedFullWidth = ReaderRenderWidthPx * _readerZoom * ReaderDpiScale;
            if (requestedFullWidth < ReaderTileStartWidthPx || requestedFullWidth <= _readerBitmapNativeWidthPx * 1.03)
            {
                ClearReaderTiles(invalidate: false);
                return;
            }

            int fullWidth = QuantizeTileFullWidth(Math.Min(requestedFullWidth, ReaderMaxRenderWidthPx));
            int fullHeight = Math.Max(1, (int)Math.Round(fullWidth * _readerPageAspect));
            double displayWidth = ReaderRenderWidthPx * _readerZoom;
            double tileScale = displayWidth / fullWidth;

            // Tính vùng nhìn thấy THUẦN CÔNG THỨC (từ scroll offset + layout hiện có) — KHÔNG cần
            // ReaderTileCanvas đã resize về đúng fullWidth/fullHeight MỚI trước (khác cách đo cũ qua
            // TranslatePoint, bắt buộc canvas đổi TRƯỚC mới đo đúng) — nhờ vậy tính được cần tải tile
            // nào ở độ phân giải MỚI mà CHƯA phải đụng/xoá canvas đang hiển thị hệ toạ độ CŨ.
            Rect visible = ComputeReaderTileVisibleRect(fullWidth, fullHeight);
            if (visible.IsEmpty)
            {
                ClearReaderTiles(invalidate: false);
                return;
            }

            var needed = new HashSet<ReaderTileKey>
            {
                ComputeViewportRegion(_readerPage.SourcePath, _readerPage.PageNumber, fullWidth, fullHeight, visible)
            };

            Canvas? fallback = null;
            if (_readerTileCoordSpace != (fullWidth, fullHeight))
            {
                fallback = ActiveReaderTileCanvas;
                var next = InactiveReaderTileCanvas;
                RetainedTilePresentation.Begin(fallback, next);
                next.Width = fullWidth;
                next.Height = fullHeight;
                InactiveReaderTileScaleTransform.ScaleX = InactiveReaderTileScaleTransform.ScaleY = tileScale;
                InactiveReaderTileRotateTransform.Angle = _readerRotation;
                _readerTileActiveIsA = !_readerTileActiveIsA;
                _readerTileCoordSpace = (fullWidth, fullHeight);
            }
            ActiveReaderTileScaleTransform.ScaleX = ActiveReaderTileScaleTransform.ScaleY = tileScale;
            ActiveReaderTileCanvas.Visibility = Visibility.Visible;
            PositionReaderTileCanvas(ComputeReaderEffectiveLayout());
            _visibleReaderTiles.Clear();
            foreach (var key in needed) _visibleReaderTiles.Add(key);
            // #4: vùng cũ giữ trên màn hình tới khi vùng mới hiện xong (vùng mới vẽ đè lên), rồi mới gỡ.
            await RefineCanvasAsync(ActiveReaderTileCanvas, fallback ?? InactiveReaderTileCanvas,
                needed, visible, requestId);
            if (requestId == Volatile.Read(ref _readerTileRequestId)) RemoveReaderTileVisualsNotIn(needed);
        }

        // ── Tối ưu #4: zoom sâu vẽ 1 VÙNG/trang thay vì lưới tile 640 px (cách của Chromium) ─────────────────
        // Đo trên bản vẽ CAD dày nét (PdfBench mục 2d): phủ vùng xem 1650×880 ở zoom 200% bằng tile 640 px mất
        // 184 ms (9 tile, mỗi tile PDFium duyệt lại TOÀN BỘ đối tượng của trang, lại phủ rộng hơn vùng xem), vẽ
        // đúng vùng xem 1 lần chỉ 51 ms. ReaderTileKey vốn là hình chữ nhật bất kỳ nên cache, hiển thị, giữ ảnh
        // cũ khi đổi độ phân giải… giữ nguyên — chỉ đổi cách chọn hình chữ nhật.
        // Lề mỗi phía: pan trong lề không phải vẽ lại. Đo PdfBench 2d (zoom 200%): lề 25% → 115 ms, 12,5% → 75 ms
        // (tile 640 cũ: 185 ms) — chọn 12,5%.
        private const double RegionMarginFraction = 0.125;
        private const int RegionSnapPx = 64;
        private const long MaxRegionPixels = 12_000_000;

        /// <summary>Vùng cần vẽ của 1 trang: dùng lại vùng đang hiện nếu nó còn chứa trọn phần đang nhìn; nếu
        /// không thì vùng mới = phần đang nhìn + lề, bám lưới 64 px, không quá <see cref="MaxRegionPixels"/>.</summary>
        private ReaderTileKey ComputeViewportRegion(string path, int page, int fullWidth, int fullHeight, Rect visible)
        {
            string layers = RenderCacheKeys.TileLayers(path);
            foreach (var k in _visibleReaderTiles)
            {
                if (k.Page == page && k.FullWidth == fullWidth && k.FullHeight == fullHeight &&
                    string.Equals(k.Layers, layers, StringComparison.Ordinal) &&
                    string.Equals(k.Path, path, StringComparison.OrdinalIgnoreCase) &&
                    k.X <= visible.Left && k.Y <= visible.Top &&
                    k.X + k.Width >= visible.Right && k.Y + k.Height >= visible.Bottom)
                    return k;
            }

            double marginX = Math.Max(RegionSnapPx * 2, visible.Width * RegionMarginFraction);
            double marginY = Math.Max(RegionSnapPx * 2, visible.Height * RegionMarginFraction);
            while (true)
            {
                int x0 = Math.Max(0, (int)Math.Floor((visible.Left - marginX) / RegionSnapPx) * RegionSnapPx);
                int y0 = Math.Max(0, (int)Math.Floor((visible.Top - marginY) / RegionSnapPx) * RegionSnapPx);
                int x1 = Math.Min(fullWidth, (int)Math.Ceiling((visible.Right + marginX) / RegionSnapPx) * RegionSnapPx);
                int y1 = Math.Min(fullHeight, (int)Math.Ceiling((visible.Bottom + marginY) / RegionSnapPx) * RegionSnapPx);
                if ((long)(x1 - x0) * (y1 - y0) <= MaxRegionPixels || (marginX < 1 && marginY < 1))
                    return new ReaderTileKey(path, page, fullWidth, fullHeight, x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0), layers);
                marginX /= 2;
                marginY /= 2;
            }
        }

        /// <summary>Vùng NHÌN THẤY của trang, quy đổi ra toạ độ tile fullWidth×fullHeight — THUẦN
        /// CÔNG THỨC từ ComputeReaderEffectiveLayout + scroll offset hiện có, KHÔNG cần đo qua
        /// TranslatePoint (nên không phụ thuộc việc ReaderTileCanvas đã resize về đúng fullWidth/
        /// fullHeight này hay chưa) — cho phép tính "cần tải tile nào" ở độ phân giải MỚI trước khi
        /// đụng tới canvas đang hiển thị hệ toạ độ CŨ (xem UpdateReaderTilesAsync).</summary>
        private Rect ComputeReaderTileVisibleRect(int fullWidth, int fullHeight)
        {
            if (ReaderScrollViewer.ViewportWidth <= 0 || ReaderScrollViewer.ViewportHeight <= 0) return Rect.Empty;
            var layout = ComputeReaderEffectiveLayout();
            if (layout.EffectiveWidth <= 0 || layout.EffectiveHeight <= 0) return Rect.Empty;

            double viewportLeft = ReaderScrollViewer.HorizontalOffset;
            double viewportTop = ReaderScrollViewer.VerticalOffset;
            double viewportRight = viewportLeft + ReaderScrollViewer.ViewportWidth;
            double viewportBottom = viewportTop + ReaderScrollViewer.ViewportHeight;

            double pageLeft = layout.TargetX, pageTop = layout.TargetY;
            double pageRight = pageLeft + layout.EffectiveWidth, pageBottom = pageTop + layout.EffectiveHeight;

            double ix0 = Math.Max(viewportLeft, pageLeft), iy0 = Math.Max(viewportTop, pageTop);
            double ix1 = Math.Min(viewportRight, pageRight), iy1 = Math.Min(viewportBottom, pageBottom);
            if (ix1 <= ix0 || iy1 <= iy0) return Rect.Empty;

            double fracLeft = Math.Clamp((ix0 - pageLeft) / layout.EffectiveWidth, 0, 1);
            double fracTop = Math.Clamp((iy0 - pageTop) / layout.EffectiveHeight, 0, 1);
            double fracRight = Math.Clamp((ix1 - pageLeft) / layout.EffectiveWidth, 0, 1);
            double fracBottom = Math.Clamp((iy1 - pageTop) / layout.EffectiveHeight, 0, 1);

            return new Rect(
                new Point(fracLeft * fullWidth, fracTop * fullHeight),
                new Point(fracRight * fullWidth, fracBottom * fullHeight));
        }

        private async Task RefineCanvasAsync(Canvas canvas, Canvas fallback,
            HashSet<ReaderTileKey> needed, Rect visible, long requestId)
        {
            var owner = canvas.DataContext;
            // Visible center first. No border overscan ahead of pixels actually on screen.
            var ordered = needed.OrderBy(k => Math.Pow(k.X + k.Width * .5 - (visible.X + visible.Width * .5), 2)
                + Math.Pow(k.Y + k.Height * .5 - (visible.Y + visible.Height * .5), 2)).ToList();
            await PresentCanvasTilesAsync(canvas, ordered, requestId);
            if (requestId != Volatile.Read(ref _readerTileRequestId)) return;
            if (ordered.Count > 0)
            {
                await EnsureReaderTileBatchLoadedAsync(ordered);
            }
            if (requestId != Volatile.Read(ref _readerTileRequestId) || !ReferenceEquals(canvas.DataContext, owner)) return;
            await PresentCanvasTilesAsync(canvas, ordered, requestId);
            if (requestId != Volatile.Read(ref _readerTileRequestId) || !ReferenceEquals(canvas.DataContext, owner)) return;
            if (needed.All(k => _readerTileCache.ContainsKey(k))) RetainedTilePresentation.Complete(fallback);
            foreach (var key in needed) _pendingReaderTiles.Remove(key);
            _readerTileCache.Trim(k => _visibleReaderTiles.Contains(k) || _pendingReaderTiles.Contains(k));
        }

        // Deduplicated, ordered batch; each completion is presented independently.
        private async Task EnsureReaderTileBatchLoadedAsync(List<ReaderTileKey> keys)
        {
            var pending = new List<Task<BitmapSource?>>();
            var missing = new List<ReaderTileKey>();
            var completions = new List<TaskCompletionSource<BitmapSource?>>();
            foreach (var key in keys)
            {
                _pendingReaderTiles.Add(key);
                if (_readerTileCache.ContainsKey(key)) continue;
                if (_readerTileLoads.TryGetValue(key, out var existing)) { pending.Add(existing); continue; }
                var completion = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _readerTileLoads[key] = completion.Task;
                pending.Add(completion.Task);
                missing.Add(key);
                completions.Add(completion);
            }
            if (missing.Count > 0)
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_readerTileRefreshCts?.Token ?? CancellationToken.None);
                foreach (var key in missing) _readerTileLoadCts[key] = cts;
                _ = LoadTileBatchAsync(missing, completions, cts);
            }
            await Task.WhenAll(pending);
        }

        private async Task LoadTileBatchAsync(List<ReaderTileKey> keys,
            IReadOnlyList<TaskCompletionSource<BitmapSource?>> completions, CancellationTokenSource cts)
        {
            var token = cts.Token;
            try
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var key = keys[i];
                    var bmp = await PdfThumbnailService.RenderPageTileAsync(key.Path, key.Page - 1,
                        key.FullWidth, key.FullHeight, new Int32Rect(key.X, key.Y, key.Width, key.Height), token, key.Layers);
                    if (token.IsCancellationRequested) break;
                    if (bmp != null)
                    {
                        CacheReaderTile(key, bmp);
                        if (_visibleReaderTiles.Contains(key))
                            _ = _tilePresentation.Enqueue(() =>
                            {
                                if (!_visibleReaderTiles.Contains(key)) return;
                                if (!_readerContinuousMode) AddReaderTileVisual(key, bmp);
                            }, token);
                    }
                    completions[i].TrySetResult(bmp);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LogContinuousTileDebug($"Tile rendering failed: {ex.Message}"); }
            finally
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    if (_readerTileLoads.TryGetValue(keys[i], out var current) && ReferenceEquals(current, completions[i].Task))
                        _readerTileLoads.Remove(keys[i]);
                    if (_readerTileLoadCts.TryGetValue(keys[i], out var owner) && ReferenceEquals(owner, cts))
                        _readerTileLoadCts.Remove(keys[i]);
                    completions[i].TrySetResult(null);
                }
                cts.Dispose();
            }
        }

        private void CacheReaderTile(ReaderTileKey key, BitmapSource bmp) =>
            _readerTileCache.Set(key, bmp, k => _visibleReaderTiles.Contains(k) || _pendingReaderTiles.Contains(k));

        private Task PresentCanvasTilesAsync(Canvas canvas, IEnumerable<ReaderTileKey> keys, long requestId)
        {
            var jobs = new List<Task>();
            var owner = canvas.DataContext;
            var token = _readerTileRefreshCts.Token;
            foreach (var key in keys)
            {
                if (canvas.Children.OfType<Image>().Any(i => i.Tag is ReaderTileKey existing && existing.Equals(key))) continue;
                if (!_readerTileCache.TryGetValue(key, out var bitmap)) continue;
                jobs.Add(_tilePresentation.Enqueue(() =>
                {
                    if (requestId == Volatile.Read(ref _readerTileRequestId) && ReferenceEquals(canvas.DataContext, owner))
                        AddTileVisual(canvas, key, bitmap);
                }, token));
            }
            return Task.WhenAll(jobs);
        }

        private void AddReaderTileVisual(ReaderTileKey key, BitmapSource bmp)
            => AddTileVisual(ActiveReaderTileCanvas, key, bmp);

        private void AddTileVisual(Canvas canvas, ReaderTileKey key, BitmapSource bmp)
        {
            foreach (var child in canvas.Children)
                if (child is Image existing && existing.Tag is ReaderTileKey existingKey && existingKey.Equals(key))
                    return;

            var image = new Image
            {
                Source = bmp,
                Width = key.Width,
                Height = key.Height,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
                Tag = key
            };
            BindingOperations.SetBinding(image, RenderOptions.BitmapScalingModeProperty,
                new Binding(nameof(ReaderBitmapScalingMode)) { Source = this });
            Canvas.SetLeft(image, key.X);
            Canvas.SetTop(image, key.Y);
            canvas.Children.Add(image);
        }

        private void RemoveReaderTileVisualsNotIn(HashSet<ReaderTileKey> needed)
            => RemoveTileVisualsNotIn(ActiveReaderTileCanvas, needed);

        private static void RemoveTileVisualsNotIn(Canvas canvas, HashSet<ReaderTileKey> needed)
        {
            for (int i = canvas.Children.Count - 1; i >= 0; i--)
            {
                if (canvas.Children[i] is Image image &&
                    image.Tag is ReaderTileKey key &&
                    !needed.Contains(key))
                {
                    canvas.Children.RemoveAt(i);
                }
            }
        }

        private void ClearReaderTileVisuals()
        {
            ReaderTileCanvas.Children.Clear();
            ReaderTileCanvasB.Children.Clear();
            _visibleReaderTiles.Clear();
        }

        private void ClearReaderTiles(bool invalidate = true)
        {
            if (invalidate)
            {
                _viewportRenderScheduler.Cancel();
                InvalidateReaderTileWork();
                _readerTileCache.Clear();
            }
            _pendingReaderTiles.Clear();
            ClearReaderTileVisuals();
            _readerTileCache.Trim();
            ReaderTileCanvas.Visibility = Visibility.Collapsed;
            ReaderTileCanvasB.Visibility = Visibility.Collapsed;
            _readerTileCoordSpace = default;
        }

        private void ReaderContinuousToggle_Click(object sender, RoutedEventArgs e)
        {
            Interlocked.Increment(ref _readerRequestId);
            _readerRealtimeRenderCts?.Cancel();
            _readerContinuousMode = !_readerContinuousMode;

            if (_readerContinuousMode)
            {
                ClearReaderTiles();
                ReaderScrollViewer.Visibility = Visibility.Collapsed;
                ReaderContinuousView.Visibility = Visibility.Visible;
                if (_readerGroup != null && _readerPage != null)
                    ShowReaderContinuous(_readerGroup, _readerPage);
            }
            else
            {
                ReaderContinuousView.Visibility = Visibility.Collapsed;
                ReaderContinuousView.SetDocument(null, ReaderContinuousView.Zoom);
                ReaderScrollViewer.Visibility = Visibility.Visible;
                if (_readerGroup != null && _readerPage != null)
                    _ = ShowPageAsync(_readerGroup, _readerPage, preserveZoomMode: true);
            }
        }

        // ── Chế độ Cuộn liên tục: ContinuousPdfView (1 vùng vẽ kiểu Foxit) ──────────────────────────────────
        // Trước đây: ListBox + panel ảo hoá + phần tử/ảnh/2 canvas tile cho từng trang + binding zoom — nhiều lớp phải
        // khớp nhau mỗi nấc zoom/cuộn (trang trống, màn hình đen sau End + zoom). Giờ vùng vẽ tự tính vị trí mọi trang và
        // vẽ ảnh tốt nhất đang có của từng trang mỗi khung hình; ReaderWindow chỉ gán tài liệu, zoom, trang cần tới.

        private double ReaderContinuousZoom => ReaderContinuousView.Zoom;

        private void HookContinuousView()
        {
            var view = ReaderContinuousView;
            view.MinZoom = ReaderMinZoom;
            view.MaxZoom = ReaderMaxZoom;
            view.ZoomStep = ReaderZoomStep;
            // Ảnh trang dùng chung cache với chế độ 1 trang (_readerCache): trang đã xem ở chế độ nào cũng không vẽ lại.
            view.PageRenderer = (row, width, priority, token) => GetReaderLoadTask((row.SourcePath, row.PageNumber),
                prefetch: priority != PdfRenderPriority.Visible, token, width);
            view.CurrentPageChanged += index =>
            {
                if (_readerGroup == null || !ReferenceEquals(view.Pages, _readerGroup.Pages) || index >= view.Pages.Count) return;
                var row = view.Pages[index];
                if (ReferenceEquals(row, _readerPage)) return;
                _readerPage = row;
                UpdateReaderChrome(_readerGroup, row);
            };
            view.UserZoomed += () => OnContinuousZoomChanged(ReaderZoomMode.Manual);
            view.UserInteraction += PdfThumbnailService.NoteInteraction; // thumbnail/tải trước nhường PDFium cho trang đang xem
            view.ScrollSettled += SyncSidePanelAfterScroll;
            view.ViewChanged += () => { if (_annotationEditor != null) PositionAnnotationEditor(); };
            // Như Chromium Viewport.resize_(): đang "vừa chiều rộng" thì đổi cỡ cửa sổ / kéo panel trái → khớp lại zoom.
            view.ViewportResized += () =>
            {
                if (_readerContinuousMode && _readerZoomMode == ReaderZoomMode.FitWidth && view.Pages.Count > 0)
                    SetReaderContinuousZoom(ComputeReaderContinuousFitWidthZoom(), ReaderZoomMode.FitWidth);
            };
        }

        /// <summary>Bật/chuyển chế độ cuộn liên tục sang đúng file/trang — chỉ gán lại tài liệu khi đổi file (giữ vị trí
        /// cuộn đang xem dở khi chỉ đổi trang trong cùng file).</summary>
        private void ShowReaderContinuous(DocumentGroup group, PageRow row)
        {
            ShowAndActivate();
            ReaderScrollViewer.Visibility = Visibility.Collapsed;
            ReaderContinuousView.Visibility = Visibility.Visible;

            bool bound = ReferenceEquals(ReaderContinuousView.Pages, group.Pages);
            bool groupChanged = !ReferenceEquals(_readerGroup, group);
            if (!bound)
            {
                _readerPageCts.Cancel();
                _readerPageCts.Dispose();
                _readerPageCts = new();
                ClearReaderTiles();
            }
            if (groupChanged)
            {
                // Quay lại file đã xem → đúng mức zoom của riêng file đó (như chế độ 1 trang).
                if (_readerZoomByGroup.TryGetValue(group, out var saved)) (_readerZoomMode, _readerZoom) = saved;
                else _readerZoomMode = ReaderZoomMode.FitWidth;
            }
            _readerGroup = group;
            _readerPage = row;
            UpdateReaderChrome(group, row);

            if (!bound)
            {
                // Như Chromium: dựng bố cục từ khổ giấy thật NGAY lần đầu — không hiện trang khổ mặc định rồi mới dời.
                if (!ReferenceEquals(_continuousBindPending, group))
                {
                    _continuousBindPending = group;
                    _ = BindContinuousWhenSizesKnownAsync(group);
                }
                return;
            }

            _ = EnsureContinuousPageSizesAsync(group);
            ScrollReaderContinuousTo(row);
        }

        private DocumentGroup? _continuousBindPending;

        /// <summary>Gán tài liệu cho vùng vẽ sau khi biết khổ giấy mọi trang (tối đa 1,5 s — quá thì dựng bằng khổ mặc
        /// định rồi cập nhật khi đọc xong), đặt zoom, cuộn tới trang đang xem.</summary>
        private async Task BindContinuousWhenSizesKnownAsync(DocumentGroup group)
        {
            try
            {
                await Task.WhenAny(EnsureContinuousPageSizesAsync(group), Task.Delay(1500));
                // Vùng vẽ vừa hiện (Collapsed → Visible): chờ 1 lượt layout để biết chiều rộng khung nhìn (vừa chiều rộng).
                await Dispatcher.Yield(DispatcherPriority.Loaded);
            }
            finally
            {
                if (ReferenceEquals(_continuousBindPending, group)) _continuousBindPending = null;
            }
            if (!_readerContinuousMode || !ReferenceEquals(_readerGroup, group) ||
                ReferenceEquals(ReaderContinuousView.Pages, group.Pages)) return;
            ReaderContinuousView.SetDocument(group.Pages,
                _readerZoomMode == ReaderZoomMode.Manual ? _readerZoom : ComputeReaderContinuousFitWidthZoom());
            OnContinuousZoomChanged(_readerZoomMode);
            if (_readerPage is { } current) ScrollReaderContinuousTo(current);
        }

        private void ScrollReaderContinuousTo(PageRow row)
        {
            _readerPage = row;
            if (_readerGroup == null) return;
            UpdateReaderChrome(_readerGroup, row);
            if (ReferenceEquals(ReaderContinuousView.Pages, _readerGroup.Pages))
                ReaderContinuousView.ScrollToPage(_readerGroup.Pages.IndexOf(row)); // đỉnh trang lên đỉnh khung nhìn
        }

        private double ComputeReaderContinuousFitWidthZoom()
        {
            // Trang RỘNG NHẤT (ReaderRenderWidthPx) vừa khung nhìn, trừ lề + viền của ContinuousPageLayout. Thanh cuộn dọc
            // của vùng vẽ luôn hiện nên chiều rộng khung nhìn không đổi theo nội dung (không vòng lặp khớp lại).
            double width = ReaderContinuousView.ViewportWidth;
            if (width <= 0) return _readerZoom > 0 ? _readerZoom : 1.0; // chưa layout — khớp lại khi có kích thước (ViewportResized)
            double viewportWidth = Math.Max(1, width - 2 * ContinuousPageLayout.Margin - 2 * ContinuousPageLayout.BorderThickness);
            return ReaderZoomMath.Clamp(ReaderZoomMath.FitWidthZoom(viewportWidth, ReaderRenderWidthPx), ReaderMinZoom, ReaderMaxZoom);
        }

        /// <summary>Đổi zoom giữ nguyên điểm ở giữa-đỉnh khung nhìn (vừa chiều rộng). Zoom theo con trỏ: ZoomContinuousAtPoint.</summary>
        private void SetReaderContinuousZoom(double zoom, ReaderZoomMode mode = ReaderZoomMode.Manual)
        {
            ReaderContinuousView.ZoomKeepTop(zoom);
            OnContinuousZoomChanged(mode);
        }

        private void ZoomContinuousAtPoint(double zoom, Point viewPoint)
        {
            ReaderContinuousView.ZoomAt(zoom, viewPoint);
            OnContinuousZoomChanged(ReaderZoomMode.Manual);
        }

        private void OnContinuousZoomChanged(ReaderZoomMode mode)
        {
            _readerZoomMode = mode;
            _readerZoom = ReaderContinuousView.Zoom;
            ReaderZoomText.Text = $"{_readerZoom * 100:0}%";
            if (_readerGroup != null)
                _readerZoomByGroup[_readerGroup] = (_readerZoomMode, _readerZoom);
        }

        private readonly Dictionary<DocumentGroup, Task> _pageSizeLoads = new();

        /// <summary>
        /// Khổ giấy thật cho mọi trang của <paramref name="group"/> — như Chromium đọc kích thước MỌI trang lúc mở để
        /// dựng bố cục: trang rộng nhất tài liệu có chiều rộng logic DefaultLayoutWidth, trang khác tỉ lệ theo point
        /// (A3 cạnh A1 hiện nhỏ hơn, không bị kéo cùng cỡ). Chỉ đọc file có trang chưa biết kích thước (lần đầu, trang mới
        /// chèn, trang vừa xoay). Trang ở đỉnh khung nhìn giữ nguyên khi bố cục đổi.
        /// </summary>
        private Task EnsureContinuousPageSizesAsync(DocumentGroup group)
        {
            // Lượt đọc đang chạy cho group này → dùng chung (người gọi sau cũng chờ được tới khi có khổ giấy).
            if (_pageSizeLoads.TryGetValue(group, out var running)) return running;
            var task = LoadContinuousPageSizesAsync(group);
            if (!task.IsCompleted) _pageSizeLoads[group] = task;
            return task;
        }

        private async Task LoadContinuousPageSizesAsync(DocumentGroup group)
        {
            try
            {
                var missing = group.Pages.Where(p => p.PageWidthPoints == null).Select(p => p.SourcePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                bool layoutMissing = group.Pages.Any(p => p.PageWidthPoints > 0 && p.BaseWidth == null);
                if (missing.Count == 0 && !layoutMissing) return;

                var sizesByPath = new Dictionary<string, (double Width, double Height)[]?>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in missing)
                    sizesByPath[path] = await PdfThumbnailService.GetPageSizesAsync(path);

                void Apply()
                {
                    foreach (var row in group.Pages)
                    {
                        if (row.PageWidthPoints != null || !sizesByPath.TryGetValue(row.SourcePath, out var sizes)) continue;
                        // 0 = không đọc được: giữ khổ mặc định, không đọc lại mỗi lần cuộn.
                        var (w, h) = sizes != null && row.PageNumber - 1 < sizes.Length ? sizes[row.PageNumber - 1] : (0, 0);
                        row.PageWidthPoints = w;
                        row.PageHeightPoints = h;
                    }
                    double maxWidth = group.Pages.Where(p => p.PageWidthPoints > 0).Select(p => p.PageWidthPoints!.Value)
                        .DefaultIfEmpty(0).Max();
                    if (maxWidth <= 0) return;
                    foreach (var row in group.Pages)
                    {
                        if (row.PageWidthPoints is not > 0 || row.PageHeightPoints is not > 0) continue;
                        double baseWidth = PageRow.DefaultLayoutWidth * row.PageWidthPoints.Value / maxWidth;
                        double aspect = row.PageHeightPoints.Value / row.PageWidthPoints.Value;
                        if (row.BaseWidth != baseWidth) row.BaseWidth = baseWidth;
                        if (row.AspectRatio != aspect) row.AspectRatio = aspect;
                    }
                }

                Apply();
                // Bố cục dựng lại theo khổ thật; trang ở đỉnh khung nhìn đứng yên.
                if (ReferenceEquals(ReaderContinuousView.Pages, group.Pages)) ReaderContinuousView.RefreshPageSizes();
            }
            finally
            {
                _pageSizeLoads.Remove(group);
            }
        }

        // ── Công cụ sửa nhanh kiểu Foxit (xoay trang lưu file, chèn, xuất) ──────────────

        /// <summary>Cửa sổ chủ thực thi các thao tác sửa (nắm selection Organizer, workspace/undo).</summary>
        internal IReaderPageEditHost? EditHost { get; set; }

        /// <summary>Trang mà công cụ sửa áp lên: nếu trang đang xem nằm trong vùng chọn của Organizer
        /// thì lấy cả vùng chọn (xoay/xuất nhiều trang cùng lúc), không thì chỉ trang đang xem —
        /// tránh sửa nhầm 1 vùng chọn cũ user đã quên khi đã chuyển sang xem trang khác.</summary>
        private IReadOnlyList<PageRow> GetEditTargetPages()
        {
            if (_readerGroup == null || _readerPage == null) return Array.Empty<PageRow>();
            // Ưu tiên vùng chọn ở panel thumbnail bên trái (cùng cửa sổ), rồi tới Organizer của cửa sổ ghép.
            var panel = ReaderSidePanel.SelectedPages;
            if (panel.Count > 1 && panel.Contains(_readerPage)) return panel;
            var selected = EditHost?.GetSelectedPages(_readerGroup) ?? Array.Empty<PageRow>();
            return selected.Contains(_readerPage) ? selected : new[] { _readerPage };
        }

        private async void ReaderPageRotateLeft_Click(object sender, RoutedEventArgs e) => await RotateEditTargetsAsync(-90);
        private async void ReaderPageRotateRight_Click(object sender, RoutedEventArgs e) => await RotateEditTargetsAsync(90);

        private async Task RotateEditTargetsAsync(int deltaDegrees)
        {
            var pages = GetEditTargetPages();
            if (pages.Count == 0 || EditHost == null) return;
            await EditHost.RotatePagesAsync(pages, deltaDegrees);
        }

        private async void ReaderInsertPages_Click(object sender, RoutedEventArgs e)
        {
            if (_readerGroup == null || _readerPage == null || EditHost == null) return;
            // Chèn SAU trang đang xem (như Foxit mặc định "After current page").
            int insertIndex = _readerGroup.Pages.IndexOf(_readerPage) + 1;
            await EditHost.InsertPagesFromFileAsync(_readerGroup, insertIndex);
        }

        private async void ReaderExtractPages_Click(object sender, RoutedEventArgs e)
        {
            var pages = GetEditTargetPages();
            if (_readerGroup == null || pages.Count == 0 || EditHost == null) return;
            await EditHost.ExtractPagesAsync(_readerGroup, pages);
        }

        /// <summary>File nguồn vừa bị sửa (xoay trang/annotation) — bỏ mọi ảnh Viewer đang cache của các
        /// trang đó rồi render lại. <paramref name="geometryChanged"/> = false (chỉ thêm annotation): giữ ảnh
        /// cũ trên màn hình tới khi ảnh mới xong, không nhảy lại vị trí cuộn/zoom.</summary>
        internal void OnSourcePagesEdited(string path, IReadOnlyCollection<int> pages, bool geometryChanged)
        {
            bool Matches(string p, int page) =>
                pages.Contains(page) && string.Equals(p, path, StringComparison.OrdinalIgnoreCase);

            lock (_readerCacheLock)
            {
                _readerCache.RemoveWhere(key => Matches(key.Path, key.Page));
                foreach (var key in _readerLoads.Keys.Where(key => Matches(key.Path, key.Page)).ToList())
                    _readerLoads.Remove(key);
            }
            _readerTileCache.RemoveWhere(key => Matches(key.Path, key.Page));
            InvalidateAnnotationCache(path, pages);
            // Xoay trang đổi khổ (DocumentSession đã xoá PageWidthPoints): đọc lại kích thước thật cho bố cục.
            if (geometryChanged && _readerGroup != null) _ = EnsureContinuousPageSizesAsync(_readerGroup);

            if (_readerGroup == null || _readerPage == null) return;
            var affected = _readerGroup.Pages.Where(r => Matches(r.SourcePath, r.PageNumber)).ToList();
            if (_readerContinuousMode)
            {
                // Chú thích: ảnh cũ nằm yên tới khi ảnh mới xong. Xoay trang: ảnh cũ sai tỉ lệ — bỏ, bố cục dựng lại theo khổ mới.
                ReaderContinuousView.InvalidatePages(r => Matches(r.SourcePath, r.PageNumber), dropImages: geometryChanged);
                if (geometryChanged) ReaderContinuousView.RefreshPageSizes();
            }
            else if (affected.Contains(_readerPage))
            {
                if (geometryChanged) _ = ShowPageAsync(_readerGroup, _readerPage, preserveZoomMode: true);
                else _ = RefreshReaderBitmapInPlaceAsync(_readerPage);
            }
        }

        /// <summary>Render lại 1 trang cùng kích thước rồi thay ảnh tại chỗ (không qua trạng thái "Đang
        /// tải"), sau đó làm mới tile nét cao.</summary>
        private async Task RefreshReaderBitmapInPlaceAsync(PageRow row)
        {
            string layers = PdfLayerStateStore.GetToken(row.SourcePath);
            var bmp = await GetReaderLoadTask((row.SourcePath, row.PageNumber));
            if (bmp == null || !IsCurrentLayerState(row, layers)) return;
            row.ReaderBitmap = bmp;
            if (!_readerContinuousMode && ReferenceEquals(_readerPage, row))
            {
                ClearReaderTiles();
                ReaderImage.Source = bmp;
                _readerBitmapNativeWidthPx = bmp.PixelWidth;
                ApplyReaderLayout();
            }
            ScheduleReaderTileRefresh();
        }

        private void ReaderRotateLeft_Click(object sender, RoutedEventArgs e) => SetReaderRotation((_readerRotation - 90 + 360) % 360);
        private void ReaderRotateRight_Click(object sender, RoutedEventArgs e) => SetReaderRotation((_readerRotation + 90) % 360);

        private void SetReaderRotation(int degrees)
        {
            _readerRotation = degrees;
            ClearReaderTiles();
            if (_readerZoomMode != ReaderZoomMode.Manual) ApplyReaderZoomMode();
            else ApplyReaderLayout();
        }

        /// <summary>Zoom NHƯNG giữ nguyên điểm ảnh đang nằm dưới <paramref name="viewportPoint"/>
        /// (toạ độ trong ReaderScrollViewer) — TÍNH THẲNG bằng công thức (điểm neo tính theo % vị
        /// trí trong ảnh TRƯỚC khi đổi zoom, rồi suy ra offset cuộn MỚI cần có), không đo lại vị trí
        /// qua layout sau khi áp zoom như cách cũ (LayoutTransform + UpdateLayout() + TranslatePoint).
        /// Nhờ vậy chỉ 1 lần gán offset DUY NHẤT, không có bước "áp tạm rồi tự sửa lại" nên không còn
        /// độ trễ 1 khung hình giữa lúc ảnh phóng to/nhỏ và lúc cuộn bù lại — hết hẳn cảm giác giật/neo
        /// sai điểm khi lăn chuột nhanh.</summary>
        private void ZoomReaderAtPoint(double newZoomRaw, Point viewportPoint)
        {
            if (ReaderImage.Source is not BitmapSource) { SetReaderZoom(newZoomRaw); return; }

            var oldLayout = ComputeReaderEffectiveLayout();
            double contentX = ReaderScrollViewer.HorizontalOffset + viewportPoint.X;
            double contentY = ReaderScrollViewer.VerticalOffset + viewportPoint.Y;
            double fracX = oldLayout.EffectiveWidth > 0 ? (contentX - oldLayout.TargetX) / oldLayout.EffectiveWidth : 0.5;
            double fracY = oldLayout.EffectiveHeight > 0 ? (contentY - oldLayout.TargetY) / oldLayout.EffectiveHeight : 0.5;

            SetReaderZoom(newZoomRaw);

            var newLayout = ComputeReaderEffectiveLayout();
            double newContentX = newLayout.TargetX + fracX * newLayout.EffectiveWidth;
            double newContentY = newLayout.TargetY + fracY * newLayout.EffectiveHeight;
            ReaderScrollViewer.ScrollToHorizontalOffset(Math.Max(0, newContentX - viewportPoint.X));
            ReaderScrollViewer.ScrollToVerticalOffset(Math.Max(0, newContentY - viewportPoint.Y));
        }

        /// <summary>Gọi từ wheel handler thay vì ZoomReaderAtPoint/ZoomContinuousAtPoint trực
        /// tiếp — nếu chưa có khung hình nào đang chờ áp zoom thì đặt lịch 1 lần ở
        /// DispatcherPriority.Render (đúng nhịp trước khi WPF render khung kế tiếp); nếu đã có
        /// khung đang chờ (vài nấc wheel đến dồn dập trong cùng 1 khung) thì CHỈ cập nhật đích
        /// zoom + điểm neo mới nhất, KHÔNG đặt lịch thêm — dồn nhiều nấc thành đúng 1 lần
        /// UpdateLayout+sửa điểm neo khi khung đó thực sự chạy.</summary>
        private void RequestReaderZoomAtPoint(int wheelDelta, Point viewportPoint)
        {
            double baseZoom = _readerZoomFramePending ? _readerZoomFramePendingTarget : _readerZoom;
            _readerZoomFramePendingTarget = ReaderZoomMath.WheelZoom(
                baseZoom, wheelDelta, ReaderZoomStep, ReaderMinZoom, ReaderMaxZoom);
            _readerZoomFrameAnchor = viewportPoint;

            if (_readerZoomFramePending) return;
            _readerZoomFramePending = true;
            _ = Dispatcher.InvokeAsync(() =>
            {
                _readerZoomFramePending = false;
                if (!_readerContinuousMode) ZoomReaderAtPoint(_readerZoomFramePendingTarget, _readerZoomFrameAnchor);
            }, DispatcherPriority.Render);
        }

        private void RequestReaderPanTo(ScrollViewer scrollViewer, double horizontalOffset, double verticalOffset)
        {
            _readerPanFrameScrollViewer = scrollViewer;
            _readerPanFrameTargetH = Math.Clamp(horizontalOffset, 0, scrollViewer.ScrollableWidth);
            _readerPanFrameTargetV = Math.Clamp(verticalOffset, 0, scrollViewer.ScrollableHeight);

            if (_readerPanFramePending) return;
            _readerPanFramePending = true;
            _ = Dispatcher.InvokeAsync(() =>
            {
                _readerPanFramePending = false;
                if (_readerPanFrameScrollViewer is not { } target) return;
                target.ScrollToHorizontalOffset(_readerPanFrameTargetH);
                target.ScrollToVerticalOffset(_readerPanFrameTargetV);
                _readerPanFrameScrollViewer = null;
            }, DispatcherPriority.Render);
        }

        private Point ReaderViewportCenter() => new(ReaderScrollViewer.ViewportWidth / 2, ReaderScrollViewer.ViewportHeight / 2);

        private async void ReaderPreviousPage_Click(object sender, RoutedEventArgs e)
            => await NavigateReaderAsync(-1);

        private async void ReaderNextPage_Click(object sender, RoutedEventArgs e)
            => await NavigateReaderAsync(1);

        private void ReaderFitPage_Click(object sender, RoutedEventArgs e)
        {
            // Chế độ Cuộn liên tục không có khái niệm "1 trang vừa khít viewport" (nội
            // dung là 1 dải cuộn dài) — coi Fit page = Fit width cho đỡ khó hiểu thay vì
            // vô tác dụng.
            if (_readerContinuousMode) { SetReaderContinuousZoom(ComputeReaderContinuousFitWidthZoom(), ReaderZoomMode.FitWidth); return; }
            _readerZoomMode = ReaderZoomMode.FitPage;
            ApplyReaderZoomMode();
        }

        private void ReaderFitWidth_Click(object sender, RoutedEventArgs e)
        {
            if (_readerContinuousMode) { SetReaderContinuousZoom(ComputeReaderContinuousFitWidthZoom(), ReaderZoomMode.FitWidth); return; }
            _readerZoomMode = ReaderZoomMode.FitWidth;
            ApplyReaderZoomMode();
        }

        private void ReaderZoomIn_Click(object sender, RoutedEventArgs e)
        {
            if (_readerContinuousMode) { ZoomContinuousAtPoint(ReaderContinuousZoom * ReaderZoomStep, ReaderContinuousView.ViewportCenter); return; }
            ZoomReaderAtPoint(_readerZoom * ReaderZoomStep, ReaderViewportCenter());
        }

        private void ReaderZoomOut_Click(object sender, RoutedEventArgs e)
        {
            if (_readerContinuousMode) { ZoomContinuousAtPoint(ReaderContinuousZoom / ReaderZoomStep, ReaderContinuousView.ViewportCenter); return; }
            ZoomReaderAtPoint(_readerZoom / ReaderZoomStep, ReaderViewportCenter());
        }

        /// <summary>Lăn chuột THƯỜNG (không cần giữ Ctrl) = zoom neo theo con trỏ —
        /// đã có kéo-thả để pan (xem ReaderImage_MouseMove) nên khỏi cần dành riêng
        /// lăn chuột cho cuộn dọc nữa, giống quy ước Google Maps/nhiều app xem ảnh.</summary>
        private void ReaderScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
            RequestReaderZoomAtPoint(e.Delta, e.GetPosition(ReaderScrollViewer));
            e.Handled = true;
        }

        private void ReaderScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_readerZoomMode != ReaderZoomMode.Manual)
                ApplyReaderZoomMode();
            else
                // Zoom Manual không đổi, nhưng "sizer"/vị trí centered phụ thuộc viewport
                // (xem ComputeReaderEffectiveLayout) — phải tính lại khi panel đổi kích thước,
                // WPF không còn tự làm việc này giúp mình như hồi còn Grid tự canh giữa.
                ApplyReaderLayout();
            ScheduleReaderTileRefresh();
        }

        private void ReaderScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
            => ScheduleReaderTileRefresh();

        private void ReaderImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount >= 2)
            {
                _readerPanning = false;
                if (_readerZoomMode == ReaderZoomMode.FitWidth && _readerZoom < 0.95)
                    SetReaderZoom(1.0);
                else
                {
                    _readerZoomMode = ReaderZoomMode.FitWidth;
                    ApplyReaderZoomMode();
                }

                e.Handled = true;
                return;
            }

            // Click đơn → bắt đầu kéo pan (bám thẳng theo chuột, KHÔNG qua
            // SmoothScrollBy — easing sẽ tạo độ trễ sai cảm giác "kéo" trực tiếp).
            BeginReaderPan(e.GetPosition(ReaderScrollViewer));
            e.Handled = true;
        }

        /// <summary>Chuột GIỮA cũng pan được (song song chuột trái) — tiện khi cần giữ
        /// chuột trái để làm việc khác (VD bôi đen vùng trong tương lai) mà vẫn muốn di
        /// chuyển view.</summary>
        private void ReaderImage_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            BeginReaderPan(e.GetPosition(ReaderScrollViewer));
            e.Handled = true;
        }

        private void ReaderImage_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            EndReaderPan();
            e.Handled = true;
        }

        private void BeginReaderPan(Point viewportPoint)
        {
            _readerPanning = true;
            _readerPanStartMouse = viewportPoint;
            _readerPanStartH = ReaderScrollViewer.HorizontalOffset;
            _readerPanStartV = ReaderScrollViewer.VerticalOffset;
            ReaderImage.CaptureMouse();
            ReaderImage.Cursor = Cursors.SizeAll;
        }

        private void ReaderImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_readerPanning) return;
            if (e.LeftButton != MouseButtonState.Pressed && e.MiddleButton != MouseButtonState.Pressed)
            {
                EndReaderPan();
                return;
            }

            MarkReaderInteraction();
            Point current = e.GetPosition(ReaderScrollViewer);
            double dx = current.X - _readerPanStartMouse.X;
            double dy = current.Y - _readerPanStartMouse.Y;
            RequestReaderPanTo(ReaderScrollViewer, _readerPanStartH - dx, _readerPanStartV - dy);
        }

        private void ReaderImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndReaderPan();

        private void ReaderImage_LostMouseCapture(object sender, MouseEventArgs e) => EndReaderPan();

        private void EndReaderPan()
        {
            if (!_readerPanning) return;
            _readerPanning = false;
            if (ReaderImage.IsMouseCaptured) ReaderImage.ReleaseMouseCapture();
            ReaderImage.Cursor = Cursors.Hand;
        }

        private async void ReaderPageBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await TryNavigateReaderPageFromBoxAsync();
        }

        private async void ReaderPageBox_LostFocus(object sender, RoutedEventArgs e)
            => await TryNavigateReaderPageFromBoxAsync();

        private void ReaderOpenExternal_Click(object sender, RoutedEventArgs e)
        {
            if (_readerPage == null) return;
            try
            {
                Process.Start(new ProcessStartInfo(_readerPage.SourcePath) { UseShellExecute = true });
            }
            catch { }
        }
    }
}
