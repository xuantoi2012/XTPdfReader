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
using XTPdfMergeApp.Services;
using static XTPdfMergeApp.Services.VisualTreeHelpers;
using XTCADStyle.Controls;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;

namespace XTPdfMergeApp
{
    /// <summary>Cửa sổ xem PDF (Viewer) — tách riêng khỏi MainWindow để cửa sổ chính chỉ tập
    /// trung vào merge/sắp xếp trang. CHỈ 1 instance dùng chung (singleton, xem GetOrCreate):
    /// double-click trang khác thì cửa sổ đang có tự đổi sang xem trang đó, không tạo cửa sổ
    /// mới — giống hệt hành vi panel nhúng cũ. Đóng bằng nút X chỉ ẩn (Hide), không huỷ — xem
    /// OnClosing + AllowRealClose (App.xaml.cs/MainWindow.Closing gọi lúc thoát hẳn app).</summary>
    public partial class ReaderWindow : XTCadWindow
    {
        /// <summary>Instance DUY NHẤT đang dùng, null nếu chưa từng mở Viewer lần nào trong phiên
        /// làm việc này. KHÔNG tự tạo mới nếu Instance đã có — double-click trang khác chỉ đổi
        /// nội dung đang xem qua ShowPageAsync, không tạo cửa sổ mới (đúng quyết định "1 Viewer
        /// window dùng chung" thay vì multi-instance).</summary>
        public static ReaderWindow? Instance { get; private set; }

        private readonly ObservableCollection<DocumentGroup> _groups;

        internal static ReaderWindow GetOrCreate(Window owner, ObservableCollection<DocumentGroup> groups)
        {
            if (Instance != null) return Instance;
            Instance = new ReaderWindow(groups) { Owner = owner };
            return Instance;
        }

        private ReaderWindow(ObservableCollection<DocumentGroup> groups)
        {
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
            RestoreWindowBounds();
            _groups = groups;
            // Group đang xem bị xoá khỏi workspace (đóng cả window PDF, không phải chỉ xoá vài
            // trang — trường hợp đó qua NotifyPagesChanged) → tự ẩn Viewer, không cần MainWindow
            // forward việc này qua API riêng.
            _groups.CollectionChanged += (_, _) =>
            {
                if (_readerGroup != null && !_groups.Contains(_readerGroup)) HideReader();
            };
        }

        /// <summary>Áp lại vị trí/kích thước đã lưu lần trước (nếu có) — không có gì để làm nếu
        /// đây là lần đầu tiên mở Viewer trong máy này, giữ nguyên Width/Height mặc định khai báo
        /// trong XAML.</summary>
        private void RestoreWindowBounds()
        {
            var saved = MergeAppSettingsStore.GetReaderWindowBounds();
            if (saved is not { } b) return;

            Left = b.Left;
            Top = b.Top;
            Width = b.Width;
            Height = b.Height;
            if (b.Maximized) WindowState = WindowState.Maximized;
        }

        /// <summary>Lưu lại vị trí/kích thước THẬT sự để khôi phục (RestoreBounds, không phải
        /// Left/Top/Width/Height hiện tại — các giá trị đó phản ánh kích thước ĐÃ MAXIMIZE lúc
        /// đang ở WindowState.Maximized, lưu nhầm sẽ khiến lần mở sau "Normal" bị full màn hình).</summary>
        private void SaveWindowBounds()
        {
            bool maximized = WindowState == WindowState.Maximized;
            Rect bounds = maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
            MergeAppSettingsStore.SetReaderWindowBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height, maximized);
        }

        /// <summary>MainWindow bật cờ này lên rồi mới gọi Close() thật khi thoát cả app — nếu
        /// không, OnClosing sẽ tự Cancel + Hide() thay vì đóng, khiến tiến trình app không bao
        /// giờ thoát hẳn (Window đã Hide() vẫn nằm trong Application.Current.Windows).</summary>
        internal bool AllowRealClose { get; set; }

        private void ReaderWindow_Closing(object? sender, CancelEventArgs e)
        {
            SaveWindowBounds();
            if (AllowRealClose)
            {
                _readerTileRefreshCts.Cancel();
                _readerPageCts.Cancel();
                _readerRealtimeRenderCts?.Cancel();
                _viewportRenderScheduler.Dispose();
                _qualityRestoreTimer.Stop();
                _tilePresentation.Dispose();
                return;
            }
            e.Cancel = true;
            HideReader();
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

            switch (e.Key)
            {
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
                case Key.OemPlus:
                case Key.Add:
                    ZoomReaderAtPoint(_readerZoom * ReaderZoomStep, ReaderViewportCenter());
                    e.Handled = true;
                    break;
                case Key.OemMinus:
                case Key.Subtract:
                    ZoomReaderAtPoint(_readerZoom / ReaderZoomStep, ReaderViewportCenter());
                    e.Handled = true;
                    break;
                case Key.D0:
                case Key.NumPad0:
                    _readerZoomMode = ReaderZoomMode.FitWidth;
                    ApplyReaderZoomMode();
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
        private static readonly BitmapMemoryCache<(string Path, int Page, int Width)> _readerCache = new(ReaderCacheBudgetBytes);
        private static readonly Dictionary<(string Path, int Page, int Width), Task<BitmapSource?>> _readerLoads = new();
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
        private const int ReaderTileSizePx = 640;
        private const int ReaderTileOverscan = 0;

        private const double ReaderTileStartWidthPx = 3200;
        private readonly record struct ReaderTileKey(string Path, int Page, int FullWidth, int FullHeight, int X, int Y, int Width, int Height);
        private readonly record struct ReaderContinuousTileTarget(
            PageRow Row, Canvas ActiveCanvas, Canvas InactiveCanvas, int FullWidth, int FullHeight, double TileScale);
        // Chốt lại danh sách target của lượt UpdateReaderContinuousTilesAsync GẦN NHẤT — để
        // UpdateContinuousTileScalesForCurrentZoom (chạy mỗi nấc zoom, KHÔNG qua debounce) có thể cập
        // nhật scale hiển thị tức thời mà không cần quét lại toàn bộ ListBox mỗi lần.
        private List<ReaderContinuousTileTarget> _lastContinuousTileTargets = new();
        private readonly BitmapMemoryCache<ReaderTileKey> _readerTileCache = new(48L * 1024 * 1024);
        private readonly Dictionary<ReaderTileKey, Task<BitmapSource?>> _readerTileLoads = new();
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

        // Y hệt ActiveReaderTileCanvas ở trên nhưng cho chế độ Cuộn liên tục — mỗi TRANG có 1 cặp
        // canvas A/B RIÊNG (ContinuousTileCanvas/ContinuousTileCanvasB trong DataTemplate), nên
        // "canvas nào đang active" phải theo dõi RIÊNG cho từng PageRow (không phải 1 cờ chung như
        // single-page, vốn chỉ có đúng 1 trang đang xem tại 1 thời điểm).
        private readonly Dictionary<PageRow, bool> _continuousTileActiveIsA = new();

        private bool IsContinuousTileActiveA(PageRow row)
            => !_continuousTileActiveIsA.TryGetValue(row, out var isA) || isA;

        private void SwapContinuousTileCanvas(PageRow row)
            => _continuousTileActiveIsA[row] = !IsContinuousTileActiveA(row);

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

        /// <summary>Chế độ xem: TRUE = cuộn liên tục qua TẤT CẢ trang (ReaderContinuousList),
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
        private bool _readerZoomFrameContinuous;
        /// <summary>Zoom DÙNG CHUNG cho MỌI trang trong chế độ Cuộn liên tục (khác hẳn
        /// _readerZoom của chế độ 1-trang — 1 dải trang dài không có khái niệm "zoom theo
        /// từng trang riêng"). Bind qua RelativeSource từ ReaderContinuousPageTemplate.</summary>
        public static readonly DependencyProperty ReaderContinuousZoomProperty = DependencyProperty.Register(
            nameof(ReaderContinuousZoom), typeof(double), typeof(ReaderWindow), new PropertyMetadata(1.0));
        public double ReaderContinuousZoom
        {
            get => (double)GetValue(ReaderContinuousZoomProperty);
            set => SetValue(ReaderContinuousZoomProperty, value);
        }
        public double ReaderPageBaseWidth => ReaderRenderWidthPx;

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

        private static DateTime _lastContinuousZoomDebugLog = DateTime.MinValue;
        private static readonly string ContinuousZoomDebugLogPath =
            Path.Combine(Path.GetTempPath(), "XTPdfMergeApp_ContinuousZoom.log");

        /// <summary>Throttle giống LogContinuousTileDebug — mỗi nấc lăn chuột gọi CorrectAnchor 1 lần
        /// (~70 lần/giây thực tế), ghi đĩa không throttle từng đó lần/giây tự nó là 1 nguồn giật.</summary>
        private static void LogContinuousZoomDebug(string info)
        {
            if (!RenderDiagnostics.TraceEnabled) return;
            var now = DateTime.Now;
            if ((now - _lastContinuousZoomDebugLog).TotalMilliseconds < 200) return;
            _lastContinuousZoomDebugLog = now;
            try
            {
                string line = $"{now:HH:mm:ss.fff} | {info}";
                File.AppendAllText(ContinuousZoomDebugLogPath, line + Environment.NewLine);
                Debug.WriteLine("[ContinuousZoom] " + line);
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
        private int DesiredReaderWidth()
        {
            double width = _readerContinuousMode ? ReaderRenderWidthPx * ReaderContinuousZoom
                : _readerZoomMode == ReaderZoomMode.Manual ? ReaderRenderWidthPx * _readerZoom
                : Math.Max(640, ReaderScrollViewer.ViewportWidth);
            // Quantized keys avoid fresh renders for tiny resize/zoom changes. Logical page
            // coordinates stay at 2200 DIPs; only the backing bitmap resolution changes.
            return (int)Math.Clamp(Math.Ceiling(width * ReaderDpiScale / 256) * 256, 512, 2304);
        }

        private Task<BitmapSource?> GetReaderLoadTask((string Path, int Page) source, bool prefetch = false,
            CancellationToken cancellationToken = default)
        {
            var key = (source.Path, source.Page, Width: DesiredReaderWidth());
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
            (string Path, int Page, int Width) key, CancellationToken token, bool prefetch, Task<BitmapSource?> owner)
        {
            BitmapSource? bmp = null;
            try
            {
                bmp = await PdfThumbnailService.RenderPageAsync(key.Path, key.Page - 1, key.Width, token,
                    prefetch ? PdfRenderPriority.Background : PdfRenderPriority.Visible).ConfigureAwait(false);
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

        /// <summary>Hiện cửa sổ Viewer (tạo mới lần đầu qua GetOrCreate, các lần sau chỉ Show/Activate
        /// lại NGAY — không tạo lại instance, giữ nguyên trang/zoom đang xem dở, xem lớp GetOrCreate).
        /// KHÔNG còn "mượn/trả" bề rộng cột 0 của MainContentGrid nữa — Viewer giờ là 1 Window độc
        /// lập, không còn chung layout Grid với MainWindow.</summary>
        public void ShowAndActivate()
        {
            MergeAppSettingsStore.SetViewerVisible(true);
            Show();
            Activate();
        }

        /// <summary>Ẩn (KHÔNG đóng — xem AllowRealClose/OnClosing) và dọn state đang xem để lần mở
        /// lại sau (double-click trang khác) không còn dính ảnh/tile của trang cũ.</summary>
        public void HideReader()
        {
            Interlocked.Increment(ref _readerRequestId);
            _qualityRestoreTimer.Stop();
            _tilePresentation.Clear();
            ReaderBitmapScalingMode = BitmapScalingMode.HighQuality;
            MergeAppSettingsStore.SetViewerVisible(false);
            Hide();
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
            ReaderEmptyText.Text = "Chọn một trang để xem";
            ReaderEmptyText.Visibility = Visibility.Visible;
            ReaderContinuousList.ItemsSource = null;
            _readerRealtimeRenderCts?.Cancel();
            _readerGroup = null;
            _readerPage = null;
            _lastContinuousTileTargets.Clear();
            _continuousTileActiveIsA.Clear();
        }
        /// <summary>MainWindow gọi khi user chỉ ĐỔI SELECTION (không double-click) trong lúc Viewer
        /// đang mở — đồng bộ nội dung xem theo trang mới chọn, giữ nguyên mức zoom hiện tại. Không
        /// tự Show() — chỉ nên gọi khi đã biết Viewer đang hiện (IsVisible), vì Organizer là chức
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
            }

            var key = (row.SourcePath, row.PageNumber);
            var bmp = await GetReaderLoadTask(key);
            if (bmp == null && requestId == Volatile.Read(ref _readerRequestId)) bmp = await GetReaderLoadTask(key);
            if (requestId != Volatile.Read(ref _readerRequestId) || !ReferenceEquals(_readerPage, row)) return;

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

        private void UpdateReaderChrome(DocumentGroup group, PageRow row)
        {
            int position = group.Pages.IndexOf(row);
            ReaderTitleText.Text = $"{Path.GetFileName(row.SourcePath)} - trang nguồn {row.PageNumber}";
            ReaderPageBox.Text = position >= 0 ? (position + 1).ToString() : row.Index.ToString();
            ReaderPageTotalText.Text = $"/ {group.Pages.Count}";
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

            int nextIndex = Math.Clamp(index + delta, 0, _readerGroup.Pages.Count - 1);
            if (nextIndex == index) return;

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
            try { bmp = await Task.Run(() => PdfThumbnailService.RenderPageAsync(row.SourcePath, row.PageNumber - 1, widthPx, token), token).ConfigureAwait(true); }
            catch (OperationCanceledException) { return; }

            if (token.IsCancellationRequested || bmp == null) return;
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
        // PdfThumbnailService._pdfiumGate) nên tải hết 1 bộ tile (12-25 viên) có thể mất VÀI GIÂY,
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
            if (!IsVisible || _readerPage == null) return;
            var scroll = _readerContinuousMode ? FindPageScrollViewer(ReaderContinuousList) : ReaderScrollViewer;
            bool moved = scroll != null && _viewportMotion.Update(
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
            if (_readerContinuousMode)
            {
                await UpdateReaderContinuousTilesAsync(requestId);
                return;
            }

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

            int startX = Math.Max(0, ((int)Math.Floor(visible.Left / ReaderTileSizePx) - ReaderTileOverscan) * ReaderTileSizePx);
            int startY = Math.Max(0, ((int)Math.Floor(visible.Top / ReaderTileSizePx) - ReaderTileOverscan) * ReaderTileSizePx);
            int endX = Math.Min(fullWidth, ((int)Math.Ceiling(visible.Right / ReaderTileSizePx) + ReaderTileOverscan) * ReaderTileSizePx);
            int endY = Math.Min(fullHeight, ((int)Math.Ceiling(visible.Bottom / ReaderTileSizePx) + ReaderTileOverscan) * ReaderTileSizePx);

            var needed = new HashSet<ReaderTileKey>();
            for (int y = startY; y < endY; y += ReaderTileSizePx)
            {
                for (int x = startX; x < endX; x += ReaderTileSizePx)
                {
                    int w = Math.Min(ReaderTileSizePx, fullWidth - x);
                    int h = Math.Min(ReaderTileSizePx, fullHeight - y);
                    if (w <= 0 || h <= 0) continue;
                    needed.Add(new ReaderTileKey(_readerPage.SourcePath, _readerPage.PageNumber, fullWidth, fullHeight, x, y, w, h));
                }
            }

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
            RemoveReaderTileVisualsNotIn(needed);
            await RefineCanvasAsync(ActiveReaderTileCanvas, fallback ?? InactiveReaderTileCanvas,
                needed, visible, requestId);
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

        private async Task UpdateReaderContinuousTilesAsync(long requestId)
        {
            if (requestId != Volatile.Read(ref _readerTileRequestId))
            {
                LogContinuousTileDebug($"UpdateReaderContinuousTilesAsync SKIP stale requestId={requestId} current={Volatile.Read(ref _readerTileRequestId)}");
                return;
            }
            if (!_readerContinuousMode || _readerRotation != 0 || _readerGroup == null)
            {
                LogContinuousTileDebug($"SKIP mode/rotation/group: continuousMode={_readerContinuousMode} rotation={_readerRotation} group={(_readerGroup == null ? "NULL" : "ok")}");
                ClearContinuousTileVisuals();
                _visibleReaderTiles.Clear();
                _lastContinuousTileTargets = new();
                return;
            }

            if (FindPageScrollViewer(ReaderContinuousList) is not { } sv ||
                sv.ViewportWidth <= 0 ||
                sv.ViewportHeight <= 0)
            {
                LogContinuousTileDebug("SKIP scrollviewer/viewport invalid");
                ClearContinuousTileVisuals();
                _visibleReaderTiles.Clear();
                _lastContinuousTileTargets = new();
                return;
            }

            var viewport = new Rect(0, 0, sv.ViewportWidth, sv.ViewportHeight);
            double requestedFullWidth = ReaderRenderWidthPx * ReaderContinuousZoom * ReaderDpiScale;
            double fullWidthCapped = Math.Min(requestedFullWidth, ReaderMaxRenderWidthPx);
            var targets = new List<ReaderContinuousTileTarget>();
            int itemsSeen = 0, itemsInViewport = 0, itemsNoCanvas = 0, itemsNoBitmap = 0, itemsBelowThreshold = 0;

            // Đo riêng từng GIAI ĐOẠN của 1 lượt refresh tile — build targets (duyệt visual tree,
            // rẻ) vs xử lý tile (tính needed-set + chờ PDFium/crossfade, có thể tốn) — để biết chính
            // xác "chậm ở đâu" thay vì đoán, xem log cuối hàm này.
            var buildSw = Stopwatch.StartNew();

            foreach (var item in FindVisualChildren<ListBoxItem>(ReaderContinuousList))
            {
                itemsSeen++;
                if (item.DataContext is not PageRow row) continue;
                if (!IsElementInViewport(item, sv, viewport)) continue;
                itemsInViewport++;

                var canvasA = FindVisualChildByName<Canvas>(item, "ContinuousTileCanvas");
                var canvasB = FindVisualChildByName<Canvas>(item, "ContinuousTileCanvasB");
                if (canvasA == null || canvasB == null) { itemsNoCanvas++; continue; }

                bool activeIsA = IsContinuousTileActiveA(row);
                Canvas activeCanvas = activeIsA ? canvasA : canvasB;
                Canvas inactiveCanvas = activeIsA ? canvasB : canvasA;

                var pageBitmap = row.ReaderBitmap;
                if (pageBitmap == null && requestedFullWidth <= 2304)
                {
                    itemsNoBitmap++;
                    // Render only the destination page at its screen resolution.
                    _ = LoadReaderBitmapFor(row);
                    ClearTileCanvas(canvasA);
                    ClearTileCanvas(canvasB);
                    continue;
                }

                if (pageBitmap != null && requestedFullWidth <= pageBitmap.PixelWidth * 1.03)
                {
                    itemsBelowThreshold++;
                    ClearTileCanvas(canvasA);
                    ClearTileCanvas(canvasB);
                    continue;
                }

                // At high zoom, render the visible crop directly rather than requiring
                // a full-page bitmap first. Geometry is cheap and independent of rasterization.
                double? knownAspect = pageBitmap != null
                    ? pageBitmap.PixelHeight / (double)pageBitmap.PixelWidth : row.AspectRatio;
                if (knownAspect is not > 0)
                {
                    if (!row.AspectRatioLoadQueued) _ = LoadPageAspectRatioFor(row);
                    continue;
                }
                int fullWidth = QuantizeTileFullWidth(fullWidthCapped);
                double aspect = knownAspect.Value;
                int fullHeight = Math.Max(1, (int)Math.Round(fullWidth * aspect));
                double tileScale = ReaderRenderWidthPx * ReaderContinuousZoom / fullWidth;
                // Keep old and new resolution canvases in independent coordinate spaces.
                targets.Add(new ReaderContinuousTileTarget(row, activeCanvas, inactiveCanvas, fullWidth, fullHeight, tileScale));
            }

            long buildMs = buildSw.ElapsedMilliseconds;

            // Chốt NGAY sau khi build xong — kể cả khi targets rỗng (dọn sạch danh sách cũ) — để
            // UpdateContinuousTileScalesForCurrentZoom (chạy độc lập mỗi nấc zoom) luôn thấy đúng
            // trạng thái mới nhất, không phải đợi tới cuối hàm này (còn phải tải/định vị tile xong).
            _lastContinuousTileTargets = targets;

            if (targets.Count == 0)
            {
                LogContinuousTileDebug($"NO TARGETS: zoom={ReaderContinuousZoom:F2} requestedFullWidth={requestedFullWidth:F0} " +
                    $"threshold={ReaderTileStartWidthPx} itemsSeen={itemsSeen} itemsInViewport={itemsInViewport} " +
                    $"itemsNoCanvas={itemsNoCanvas} itemsNoBitmap={itemsNoBitmap} itemsBelowThreshold={itemsBelowThreshold} " +
                    $"gateWaitMs={_lastTileGateWaitMs} buildMs={buildMs}");
                _visibleReaderTiles.Clear();
                return;
            }

            var visibleTiles = new HashSet<ReaderTileKey>();
            var refinements = new List<Task>();
            int cacheHits = 0, queued = 0;
            var processSw = Stopwatch.StartNew();

            foreach (var target in targets)
            {
                // ActiveCanvas CHƯA từng cấu hình (Width=0, mặc định trong XAML) — an toàn resize
                // NGAY, vì chưa có gì hiển thị để mà giữ nguyên tránh nhấp nháy cả.
                bool isBootstrap = target.ActiveCanvas.Width <= 0;
                bool coordChanging = !isBootstrap &&
                    (Math.Abs(target.ActiveCanvas.Width - target.FullWidth) > 0.5 ||
                     Math.Abs(target.ActiveCanvas.Height - target.FullHeight) > 0.5);

                Rect visible;
                if (!coordChanging)
                {
                    // Bootstrap HOẶC cùng hệ toạ độ — an toàn resize/đo ngay trên ActiveCanvas như cũ,
                    // không có rủi ro nhấp nháy (bootstrap: chưa có gì cũ để mất; cùng hệ toạ độ:
                    // kích thước/scale không đổi, chỉ thêm/bớt vài tile theo vùng cuộn mới).
                    ConfigureContinuousTileCanvas(target.ActiveCanvas, target.FullWidth, target.FullHeight, target.TileScale);
                    visible = GetVisibleTileRect(target.ActiveCanvas, sv, target.FullWidth, target.FullHeight, target.TileScale);
                }
                else
                {
                    // ĐỔI HỆ TOẠ ĐỘ (zoom vừa vượt sang bậc lượng tử hoá khác) — đo vùng nhìn thấy
                    // theo hệ CŨ trên ActiveCanvas TRƯỚC (canvas này chưa bị đụng tới, vẫn đang hiển
                    // thị đúng), rồi quy đổi sang hệ MỚI qua tỉ lệ phân số — tính được cần tải tile
                    // nào ở độ phân giải MỚI mà KHÔNG phải resize canvas đang hiển thị hệ CŨ trước.
                    double oldFullWidth = target.ActiveCanvas.Width, oldFullHeight = target.ActiveCanvas.Height;
                    double oldTileScale = target.ActiveCanvas.RenderTransform is ScaleTransform { IsFrozen: false } st ? st.ScaleX : 1.0;
                    Rect visibleOld = GetVisibleTileRect(target.ActiveCanvas, sv, (int)oldFullWidth, (int)oldFullHeight, oldTileScale);
                    visible = visibleOld.IsEmpty ? Rect.Empty : new Rect(
                        new Point(visibleOld.Left / oldFullWidth * target.FullWidth, visibleOld.Top / oldFullHeight * target.FullHeight),
                        new Point(visibleOld.Right / oldFullWidth * target.FullWidth, visibleOld.Bottom / oldFullHeight * target.FullHeight));
                }

                if (visible.IsEmpty)
                {
                    if (!coordChanging) ClearTileCanvas(target.ActiveCanvas);
                    // coordChanging mà không đo được vùng nhìn thấy (trang vừa cuộn khuất) — giữ
                    // nguyên ActiveCanvas hệ toạ độ CŨ, không có gì mới để thay, bỏ qua trang này.
                    continue;
                }

                int startX = Math.Max(0, ((int)Math.Floor(visible.Left / ReaderTileSizePx) - ReaderTileOverscan) * ReaderTileSizePx);
                int startY = Math.Max(0, ((int)Math.Floor(visible.Top / ReaderTileSizePx) - ReaderTileOverscan) * ReaderTileSizePx);
                int endX = Math.Min(target.FullWidth, ((int)Math.Ceiling(visible.Right / ReaderTileSizePx) + ReaderTileOverscan) * ReaderTileSizePx);
                int endY = Math.Min(target.FullHeight, ((int)Math.Ceiling(visible.Bottom / ReaderTileSizePx) + ReaderTileOverscan) * ReaderTileSizePx);

                var needed = new HashSet<ReaderTileKey>();
                for (int y = startY; y < endY; y += ReaderTileSizePx)
                {
                    for (int x = startX; x < endX; x += ReaderTileSizePx)
                    {
                        int w = Math.Min(ReaderTileSizePx, target.FullWidth - x);
                        int h = Math.Min(ReaderTileSizePx, target.FullHeight - y);
                        if (w <= 0 || h <= 0) continue;

                        var key = new ReaderTileKey(target.Row.SourcePath, target.Row.PageNumber, target.FullWidth, target.FullHeight, x, y, w, h);
                        needed.Add(key);
                        visibleTiles.Add(key);
                    }
                }

                var destination = target.ActiveCanvas;
                var fallback = target.InactiveCanvas;
                if (coordChanging)
                {
                    destination = target.InactiveCanvas;
                    fallback = target.ActiveCanvas;
                    RetainedTilePresentation.Begin(fallback, destination);
                    ConfigureContinuousTileCanvas(destination, target.FullWidth, target.FullHeight, target.TileScale);
                    SwapContinuousTileCanvas(target.Row);
                }
                RemoveTileVisualsNotIn(destination, needed);
                cacheHits += needed.Count(k => _readerTileCache.ContainsKey(k));
                queued += needed.Count(k => !_readerTileCache.ContainsKey(k));
                refinements.Add(RefineCanvasAsync(destination, fallback, needed, visible, requestId));
            }

            _visibleReaderTiles.Clear();
            foreach (var key in visibleTiles) _visibleReaderTiles.Add(key);

            long processMs = processSw.ElapsedMilliseconds;

            await Task.WhenAll(refinements);
            LogContinuousTileDebug($"zoom={ReaderContinuousZoom:F2} targets={targets.Count} " +
                $"tiles={visibleTiles.Count} cached={cacheHits} queued={queued} buildMs={buildMs} processMs={processMs}");
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

        private static Rect GetVisibleTileRect(Canvas tileCanvas, ScrollViewer scrollViewer, int fullWidth, int fullHeight, double tileScale)
        {
            if (scrollViewer.ViewportWidth <= 0 || scrollViewer.ViewportHeight <= 0 || tileScale <= 0)
                return Rect.Empty;

            Point topLeft;
            try { topLeft = tileCanvas.TranslatePoint(new Point(0, 0), scrollViewer); }
            catch (InvalidOperationException) { return Rect.Empty; }

            var pageRect = new Rect(topLeft.X, topLeft.Y, fullWidth * tileScale, fullHeight * tileScale);
            var viewport = new Rect(0, 0, scrollViewer.ViewportWidth, scrollViewer.ViewportHeight);
            Rect visible = Rect.Intersect(pageRect, viewport);
            if (visible.IsEmpty) return Rect.Empty;

            double x = Math.Clamp((visible.Left - pageRect.Left) / tileScale, 0, fullWidth);
            double y = Math.Clamp((visible.Top - pageRect.Top) / tileScale, 0, fullHeight);
            double right = Math.Clamp((visible.Right - pageRect.Left) / tileScale, 0, fullWidth);
            double bottom = Math.Clamp((visible.Bottom - pageRect.Top) / tileScale, 0, fullHeight);
            if (right <= x || bottom <= y) return Rect.Empty;
            return new Rect(new Point(x, y), new Point(right, bottom));
        }

        private static void ConfigureContinuousTileCanvas(Canvas canvas, int fullWidth, int fullHeight, double tileScale)
        {
            canvas.Width = fullWidth;
            canvas.Height = fullHeight;
            canvas.Visibility = Visibility.Visible;

            // Phòng hờ: ScaleTransform trong DataTemplate lẽ ra không còn bị Freeze nữa sau khi đặt
            // x:Name (xem MainWindow.xaml, ContinuousTileScaleTransform) — NHƯNG nếu vì lý do nào đó
            // (VD style/theme khác đóng băng nó) nó vẫn ở trạng thái read-only, tự thay bằng 1
            // ScaleTransform MỚI thay vì để ném InvalidOperationException (đúng lỗi thật đã bắt được:
            // "Cannot set a property... because it is in a read-only state" — nuốt âm thầm vì gọi qua
            // Task fire-and-forget, khiến TOÀN BỘ tile không bao giờ hiện được ở mọi lần zoom sâu).
            if (canvas.RenderTransform is not ScaleTransform { IsFrozen: false } scale)
            {
                scale = new ScaleTransform();
                canvas.RenderTransform = scale;
            }
            scale.ScaleX = tileScale;
            scale.ScaleY = tileScale;
        }

        private static void ClearTileCanvas(Canvas canvas)
        {
            canvas.Children.Clear();
            canvas.Opacity = 1;
            canvas.Width = 0;
            canvas.Height = 0;
            canvas.Visibility = Visibility.Collapsed;
            // Container RECYCLING (VirtualizationMode=Recycling) có thể tái dùng canvas này cho 1 item
            // khác đúng lúc lệnh dọn dẹp bất đồng bộ này chạy tới (vd tắt Viewer/đổi ItemsSource giữa
            // lúc 1 lượt refresh tile trước đó còn dở dang) — lúc đó ScaleTransform của nó có thể đã bị
            // WPF đưa về trạng thái chỉ-đọc (IsFrozen), gán giá trị sẽ ném InvalidOperationException
            // (đúng lỗi "read-only state" đã gặp khi tắt Viewer ở chế độ cuộn liên tục). Bỏ qua an toàn —
            // canvas đang bị Collapsed/dọn dẹp nên scale của nó không còn ý nghĩa gì nữa.
            if (canvas.RenderTransform is ScaleTransform { IsFrozen: false } scale)
            {
                scale.ScaleX = 1;
                scale.ScaleY = 1;
            }
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
            if (missing.Count > 0) _ = LoadTileBatchAsync(missing, completions,
                _readerTileRefreshCts?.Token ?? CancellationToken.None);
            await Task.WhenAll(pending);
        }

        private async Task LoadTileBatchAsync(List<ReaderTileKey> keys,
            IReadOnlyList<TaskCompletionSource<BitmapSource?>> completions, CancellationToken token)
        {
            try
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var key = keys[i];
                    var bmp = await PdfThumbnailService.RenderPageTileAsync(key.Path, key.Page - 1,
                        key.FullWidth, key.FullHeight, new Int32Rect(key.X, key.Y, key.Width, key.Height), token);
                    if (token.IsCancellationRequested) break;
                    if (bmp != null)
                    {
                        CacheReaderTile(key, bmp);
                        if (_visibleReaderTiles.Contains(key))
                            _ = _tilePresentation.Enqueue(() =>
                            {
                                if (!_visibleReaderTiles.Contains(key)) return;
                                if (_readerContinuousMode) AddReaderTileVisualsForContinuousKey(key, bmp);
                                else AddReaderTileVisual(key, bmp);
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
                    completions[i].TrySetResult(null);
                }
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

        private void AddReaderTileVisualsForContinuousKey(ReaderTileKey key, BitmapSource bmp)
        {
            foreach (var item in FindVisualChildren<ListBoxItem>(ReaderContinuousList))
            {
                if (item.DataContext is not PageRow row) continue;
                if (!string.Equals(row.SourcePath, key.Path, StringComparison.OrdinalIgnoreCase) ||
                    row.PageNumber != key.Page)
                {
                    continue;
                }

                // Kiểm tra CẢ 2 canvas A/B (double buffering, xem SwapContinuousTileCanvas) — tại 1
                // thời điểm chỉ đúng 1 trong 2 đang Visible ở đúng hệ toạ độ của key này, cái còn lại
                // (nếu có) đang là canvas ẨN dùng để chuẩn bị hệ toạ độ KHÁC, bỏ qua.
                foreach (var canvasName in new[] { "ContinuousTileCanvas", "ContinuousTileCanvasB" })
                {
                    var canvas = FindVisualChildByName<Canvas>(item, canvasName);
                    if (canvas == null ||
                        canvas.Visibility != Visibility.Visible ||
                        Math.Abs(canvas.Width - key.FullWidth) > 0.5 ||
                        Math.Abs(canvas.Height - key.FullHeight) > 0.5)
                    {
                        continue;
                    }

                    AddTileVisual(canvas, key, bmp);
                }
            }
        }

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

        private void ClearContinuousTileVisuals()
        {
            foreach (var canvas in FindVisualChildren<Canvas>(ReaderContinuousList)
                         .Where(c => string.Equals(c.Name, "ContinuousTileCanvas", StringComparison.Ordinal) ||
                                     string.Equals(c.Name, "ContinuousTileCanvasB", StringComparison.Ordinal)))
            {
                ClearTileCanvas(canvas);
            }
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
            ClearContinuousTileVisuals();
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
                ReaderContinuousList.Visibility = Visibility.Visible;
                if (_readerGroup != null && _readerPage != null)
                    ShowReaderContinuous(_readerGroup, _readerPage);
            }
            else
            {
                ReaderContinuousList.Visibility = Visibility.Collapsed;
                ReaderContinuousList.ItemsSource = null;
                ReaderScrollViewer.Visibility = Visibility.Visible;
                if (_readerGroup != null && _readerPage != null)
                    _ = ShowPageAsync(_readerGroup, _readerPage, preserveZoomMode: true);
            }
        }

        /// <summary>Bật/chuyển chế độ cuộn liên tục sang đúng window/trang — KHÔNG gọi lại
        /// nếu ItemsSource đã đúng group rồi (tránh mất vị trí cuộn đang xem dở khi chỉ đổi
        /// trang chọn trong CÙNG 1 window).</summary>
        private void ShowReaderContinuous(DocumentGroup group, PageRow row)
        {
            ShowAndActivate();
            ReaderScrollViewer.Visibility = Visibility.Collapsed;
            ReaderContinuousList.Visibility = Visibility.Visible;

            // So với ItemsSource THẬT SỰ đang gán (không phải _readerGroup) — _readerGroup
            // có thể đã trỏ đúng group này từ TRƯỚC (VD vừa bật toggle Cuộn liên tục trong
            // khi Reader đang hiện đúng group đó ở chế độ 1-trang), nhưng ReaderContinuousList
            // chưa hề được gán ItemsSource lần nào — so theo _readerGroup sẽ SAI, bỏ qua luôn
            // bước gán ItemsSource, danh sách hiện trống trơn.
            bool needsRebind = !ReferenceEquals(ReaderContinuousList.ItemsSource, group.Pages);
            if (needsRebind)
            {
                _readerPageCts.Cancel();
                _readerPageCts.Dispose();
                _readerPageCts = new();
                ClearReaderTiles();
            }
            _readerGroup = group;
            _readerPage = row;
            UpdateReaderChrome(group, row);

            if (needsRebind)
            {
                // Dọn theo dõi "canvas A/B nào đang active" của GROUP CŨ — Dictionary giữ tham chiếu
                // PageRow, không dọn thì rò rỉ bộ nhớ dần khi mở/đóng nhiều tài liệu khác nhau trong
                // 1 phiên làm việc dài (các PageRow của tài liệu đã đóng không bao giờ được GC vì
                // Dictionary này vẫn giữ tham chiếu).
                _continuousTileActiveIsA.Clear();
                ReaderContinuousList.ItemsSource = group.Pages;
                if (_readerZoomMode == ReaderZoomMode.Manual)
                    SetReaderContinuousZoom(_readerZoom, ReaderZoomMode.Manual);
                else
                    SetReaderContinuousZoom(ComputeReaderContinuousFitWidthZoom(), ReaderZoomMode.FitWidth);
            }

            ScrollReaderContinuousTo(row);
        }

        private void ScrollReaderContinuousTo(PageRow row)
        {
            _readerPage = row;
            if (_readerGroup != null) UpdateReaderChrome(_readerGroup, row);

            // Đợi 1 nhịp layout (Loaded priority) để ItemsSource vừa gán kịp sinh container
            // trước khi ScrollIntoView — gọi ngay lúc ItemsSource mới gán thường chưa có gì.
            _ = Dispatcher.InvokeAsync(() =>
            {
                ReaderContinuousList.ScrollIntoView(row);
                ScheduleReaderTileRefresh();
            }, DispatcherPriority.Loaded);
        }

        private double ComputeReaderContinuousFitWidthZoom()
        {
            double viewportWidth = Math.Max(1, ReaderContinuousList.ActualWidth - 40);
            return ReaderZoomMath.Clamp(ReaderZoomMath.FitWidthZoom(viewportWidth, ReaderRenderWidthPx), ReaderMinZoom, ReaderMaxZoom);
        }

        private void SetReaderContinuousZoom(double zoom, ReaderZoomMode mode = ReaderZoomMode.Manual)
        {
            double newZoom = ReaderZoomMath.Clamp(zoom, ReaderMinZoom, ReaderMaxZoom);
            bool zoomActuallyChanged = Math.Abs(newZoom - ReaderContinuousZoom) > 0.0001;
            ReaderContinuousZoom = newZoom;
            _readerZoom = ReaderContinuousZoom;
            _readerZoomMode = mode;
            ReaderZoomText.Text = $"{ReaderContinuousZoom * 100:0}%";
            if (_readerGroup != null)
                _readerZoomByGroup[_readerGroup] = (_readerZoomMode, _readerZoom);
            UpdateContinuousTileScalesForCurrentZoom();

            // CHỈ đặt lịch debounce "render lại cho nét" khi zoom THẬT SỰ đổi giá trị — không thì
            // lăn chuột vẫn bắn sự kiện dù đã kẹp trần (newZoom giống hệt giá trị cũ) sẽ liên tục
            // HUỶ + ĐẶT LẠI hẹn giờ debounce, không bao giờ đủ 200ms yên tĩnh THẬT SỰ để tới hạn —
            // log thực tế: đứng yên ở zoom=4.00 suốt 7 giây vẫn chưa load hết tile.
            if (zoomActuallyChanged)
            {
                MarkReaderInteraction();
                ScheduleReaderTileRefresh(resolutionChanged: true);
            }
        }

        /// <summary>Y hệt UpdateReaderTileScaleForCurrentZoom (single-page) nhưng cho danh sách tile
        /// đang có ở chế độ Cuộn liên tục — chạy ở MỌI nấc zoom (rẻ, chỉ gán transform), tách hẳn
        /// khỏi việc render lại tile ở độ phân giải khác (nặng, chỉ làm khi zoom đã dừng hẳn — xem
        /// ScheduleReaderTileRefresh). Dùng lại _lastContinuousTileTargets (chốt ở lần
        /// UpdateReaderContinuousTilesAsync gần nhất) — kiểm tra canvas.Width/Height còn khớp
        /// FullWidth/FullHeight đã lưu trước khi áp (đề phòng container bị VirtualizingPanel tái
        /// dùng cho 1 PageRow khác giữa lúc này, tránh scale nhầm canvas đã "đổi chủ").</summary>
        private void UpdateContinuousTileScalesForCurrentZoom()
        {
            double displayWidth = ReaderRenderWidthPx * ReaderContinuousZoom;
            foreach (var target in _lastContinuousTileTargets)
            {
                // Both buffers can exist during a progressive refresh. Scale each using
                // its own coordinate width, not the requested width of the next render.
                foreach (var canvas in new[] { target.ActiveCanvas, target.InactiveCanvas })
                {
                    if (canvas.Visibility != Visibility.Visible || canvas.Width <= 0 ||
                        !ReferenceEquals(canvas.DataContext, target.Row)) continue;
                    if (canvas.RenderTransform is not ScaleTransform { IsFrozen: false } scale) continue;
                    scale.ScaleX = scale.ScaleY = displayWidth / canvas.Width;
                }
            }
        }

        /// <summary>Lazy-load ảnh độ phân giải Reader cho 1 trang trong danh sách cuộn liên
        /// tục — dùng lại ĐÚNG cache/hàng đợi render (_readerCache/GetReaderLoadTask) của
        /// chế độ 1-trang, nên 1 trang đã xem qua ở chế độ nào cũng không phải render lại ở
        /// chế độ kia.</summary>
        private async Task LoadReaderBitmapFor(PageRow row)
        {
            if (row.ReaderBitmap != null || row.ReaderBitmapLoadQueued) return;
            row.ReaderBitmapLoadQueued = true;

            var key = (row.SourcePath, row.PageNumber);
            var token = _readerPageCts.Token;
            BitmapSource? bmp;
            try { bmp = await GetReaderLoadTask(key).ConfigureAwait(true); }
            finally { row.ReaderBitmapLoadQueued = false; }

            if (token.IsCancellationRequested)
            {
                if (_readerContinuousMode && IsVisible) ScheduleReaderTileRefresh();
                return;
            }

            if (!token.IsCancellationRequested && bmp != null && row.ReaderBitmap == null)
            {
                row.ReaderBitmap = bmp;
                ScheduleReaderTileRefresh();
            }
        }

        /// <summary>Image.Loaded chỉ bắn khi WPF thật sự tạo visual cho item này (kết hợp
        /// VirtualizingPanel của ReaderContinuousList thì chỉ trang đang/gần hiện trên màn
        /// hình mới vào tới đây) — không render cả trăm trang cùng lúc lúc bật chế độ này.</summary>
        private void ReaderContinuousImage_Loaded(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PageRow row) return;
            // Realization includes the virtualization cache outside the viewport.
            // Only the viewport refresh may request expensive raster work.
            if (row.ReaderDisplayBitmap == null && row.AspectRatio == null && !row.AspectRatioLoadQueued)
                _ = LoadPageAspectRatioFor(row);
            ScheduleReaderTileRefresh();
        }

        /// <summary>Lấy tỉ lệ khung hình THẬT của trang (rẻ, không rasterize — xem
        /// PdfThumbnailService.GetPageAspectRatioAsync) ngay khi trang được hiện thực hoá trong
        /// continuous reader mà CHƯA có bitmap nào (Thumbnail/ReaderBitmap) — sửa placeholder height
        /// (ContinuousPagePlaceholderHeightConverter) khớp đúng tỉ lệ trang thật gần như ngay lập tức,
        /// thay vì phải đợi bitmap tải xong (chậm hơn nhiều) mới hết sai — xem log
        /// %TEMP%\XTPdfMergeApp_ContinuousZoom.log đã xác nhận: extent nhảy vọt không tỉ lệ với zoom
        /// đúng vào những lúc trang lần đầu có bitmap thật, do placeholder cố định 1.4142 sai xa tỉ lệ
        /// thật của các khổ giấy không phải A-series (CAD, bản vẽ khổ ngang...).</summary>
        private static async Task LoadPageAspectRatioFor(PageRow row)
        {
            row.AspectRatioLoadQueued = true;
            double? aspect;
            try { aspect = await PdfThumbnailService.GetPageAspectRatioAsync(row.SourcePath, row.PageNumber - 1).ConfigureAwait(true); }
            catch { aspect = null; }
            row.AspectRatioLoadQueued = false;
            if (aspect is > 0)
            {
                row.AspectRatio = aspect;
                if (Instance is { IsVisible: true } reader) reader.ScheduleReaderTileRefresh();
            }
        }

        /// <summary>Zoom hiện tại có thực sự cần bản render "Reader" (2200px, xem
        /// ReaderRenderWidthPx) hay thumbnail (340px, xem RenderThumbnailWidthPx) đã đủ nét rồi —
        /// cùng ngưỡng dung sai ReaderRealtimeRerenderFactor đã dùng cho single-page
        /// (ScheduleReaderRealtimeRerender). Foxit/Chrome PDF không bao giờ tải bản nét nhất cho
        /// MỌI trang đang cuộn qua bất kể zoom — chỉ tải khi kích thước hiển thị thật sự vượt độ
        /// phân giải đang có, giữ RAM tỉ lệ với những gì MẮT THẬT SỰ NHÌN THẤY chứ không phải với
        /// số trang trong tài liệu.</summary>
        private bool ReaderContinuousNeedsFullBitmap()
            => ReaderRenderWidthPx * ReaderContinuousZoom * ReaderDpiScale > MainWindow.RenderThumbnailWidthPx * ReaderRealtimeRerenderFactor;

        /// <summary>Lăn chuột THƯỜNG = cuộn dọc mượt qua các trang (đúng yêu cầu chế độ cuộn
        /// liên tục — khác hẳn chế độ 1-trang, ở đó lăn chuột thường ĐÃ LÀ zoom vì không cần
        /// cuộn tiếp sang trang khác bằng lăn chuột nữa). Ctrl+lăn chuột mới zoom ở đây.</summary>
        private void ReaderContinuousList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (FindPageScrollViewer(ReaderContinuousList) is { } zoomScroll)
                {
                    Point viewportPoint = e.GetPosition(zoomScroll);
                    RequestReaderZoomAtPoint(e.Delta, viewportPoint, continuous: true);
                }
                e.Handled = true;
                return;
            }

            e.Handled = false;
        }

        /// <summary>Cập nhật "đang xem trang mấy" theo trang đang nằm GẦN ĐỈNH viewport nhất
        /// trong lúc cuộn — cùng kỹ thuật viewport-check đã dùng cho danh sách thumbnail
        /// (IsElementInViewport/FindVisualChildren).</summary>
        private void ReaderContinuousList_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_readerGroup == null || !_readerContinuousMode) return;
            if (FindPageScrollViewer(ReaderContinuousList) is not { } sv) return;
            if (sv.ViewportWidth <= 0 || sv.ViewportHeight <= 0) return;

            var viewport = new Rect(0, 0, sv.ViewportWidth, sv.ViewportHeight);
            PageRow? best = null;
            var visibleRows = new HashSet<PageRow>();
            double bestTop = double.MaxValue;
            double currentPageTop = double.MaxValue;

            foreach (var item in FindVisualChildren<ListBoxItem>(ReaderContinuousList))
            {
                if (item.DataContext is not PageRow row) continue;
                if (!IsElementInViewport(item, sv, viewport)) continue;
                visibleRows.Add(row);

                double top = Math.Abs(item.TransformToAncestor(sv).Transform(new Point(0, 0)).Y);
                if (ReferenceEquals(row, _readerPage)) currentPageTop = top;
                if (top < bestTop) { bestTop = top; best = row; }
            }

            if (best != null && _readerPage != null && !visibleRows.Contains(_readerPage))
            {
                // The previous destination has left the screen. Do not queue this
                // destination behind full-page renders and tiles from earlier pages.
                _readerPageCts.Cancel();
                _readerPageCts.Dispose();
                _readerPageCts = new();
                lock (_readerCacheLock) _readerLoads.Clear();
                InvalidateReaderTileWork();
            }

            if (best != null)
            {
                // Hysteresis: chỉ đổi "trang trung tâm" (dùng cho CẢ title lẫn tâm bán kính giữ bitmap
                // viewport) khi ứng viên mới gần đỉnh viewport RÕ RỆT hơn (không
                // phải chỉ nhỉnh hơn vài pixel), hoặc trang đang xem không còn trong viewport nữa —
                // tránh nhấp nháy qua lại giữa 2 trang khi viewport nằm đúng ranh giới (mỗi lần zoom xê
                // dịch nhẹ là "trang gần hơn" đổi phía, kéo theo title + giải phóng/tải lại bitmap dao
                // động liên tục, góp phần cảm giác giật khi zoom).
                const double hysteresisPx = 24;
                bool currentStillVisible = _readerPage != null && visibleRows.Contains(_readerPage);
                bool shouldSwitch = !ReferenceEquals(_readerPage, best) &&
                    (!currentStillVisible || currentPageTop - bestTop > hysteresisPx);

                if (shouldSwitch)
                {
                    LogContinuousZoomDebug($"ScrollChanged -> best page changed: {_readerPage?.PageNumber} -> {best.PageNumber} " +
                        $"offset=({sv.HorizontalOffset:F0},{sv.VerticalOffset:F0}) visibleCount={visibleRows.Count}");
                    _readerPage = best;
                    UpdateReaderChrome(_readerGroup, best);
                }


            }
            else
            {
                LogContinuousZoomDebug($"ScrollChanged -> best=NULL (không tìm thấy item nào trong viewport) offset=({sv.HorizontalOffset:F0},{sv.VerticalOffset:F0})");
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

        /// <summary>Zoom continuous giữ nguyên điểm dưới con trỏ — TÍNH THẲNG bằng công thức tỉ lệ,
        /// KHÔNG còn dùng TranslatePoint/ItemContainerGenerator/FindNearestContinuousPageVisual như
        /// trước. Lý do đổi: mọi trang trong continuous đều scale theo ĐÚNG 1 tỉ lệ zoom chung
        /// (ReaderContinuousZoom) — vị trí cuộn MỚI có thể suy thẳng từ vị trí cuộn CŨ nhân với tỉ lệ
        /// (newZoom/oldZoom), không cần biết trang nào đang ở dưới con trỏ hay đo lại container nào cả.
        /// Cách cũ (TranslatePoint vào 1 Image cụ thể, re-query container mỗi nấc) có 2 nguồn sai lệch
        /// không thể loại bỏ triệt để: (1) container bị VirtualizingPanel recycle cho 1 PageRow khác
        /// giữa lúc đang zoom, (2) Canvas.Width/Height (MultiBinding phản ứng theo zoom) không chắc đã
        /// cập nhật XONG tại đúng thời điểm TranslatePoint đo — WPF xử lý binding không hoàn toàn tức
        /// thời cùng lúc đổi DependencyProperty nguồn. Công thức tỉ lệ không phụ thuộc bất kỳ điều nào
        /// ở trên — chỉ cần VerticalOffset/HorizontalOffset hiện có (luôn đúng, không qua binding nào).
        ///
        /// Sai số DUY NHẤT còn lại: margin cố định giữa các trang (Margin="0,0,0,14" trong
        /// ReaderContinuousPageTemplate) không scale theo zoom — công thức tỉ lệ coi TOÀN BỘ nội dung
        /// (kể cả margin) scale đều, nên lệch dần 1 lượng nhỏ, MƯỢT (không giật) theo số trang đã cuộn
        /// qua kể từ đầu tài liệu — đổi lại hoàn toàn hết hiện tượng giật/nhảy do timing.</summary>
        private readonly record struct ContinuousPageAnchor(PageRow Row, double FractionX, double FractionY);

        /// <summary>Tìm trang ĐANG HIỂN THỊ (container đã hiện thực hoá thật, không phải ước lượng)
        /// nằm dưới viewportPoint, và vị trí TƯƠNG ĐỐI (phân số 0..1 theo chiều rộng/cao của CHÍNH
        /// trang đó) của điểm đó — dùng TransformToAncestor đo trực tiếp trên container thật, không
        /// suy luận từ tổng ExtentHeight/ScrollableHeight của toàn danh sách ảo hoá (số đó với các
        /// trang CHƯA hiện thực hoá chỉ là ước lượng, không chính xác tuyệt đối).</summary>
        private ContinuousPageAnchor? FindContinuousPageAnchorAt(ScrollViewer zoomScroll, Point viewportPoint)
        {
            foreach (var item in FindVisualChildren<ListBoxItem>(ReaderContinuousList))
            {
                if (item.DataContext is not PageRow row) continue;
                if (item.ActualWidth <= 0 || item.ActualHeight <= 0) continue;

                Point topLeft = item.TransformToAncestor(zoomScroll).Transform(new Point(0, 0));
                var bounds = new Rect(topLeft, new Size(item.ActualWidth, item.ActualHeight));
                if (!bounds.Contains(viewportPoint)) continue;

                double fractionX = (viewportPoint.X - bounds.X) / bounds.Width;
                double fractionY = (viewportPoint.Y - bounds.Y) / bounds.Height;
                return new ContinuousPageAnchor(row, fractionX, fractionY);
            }
            return null;
        }

        /// <summary>Neo điểm zoom vào ĐÚNG trang + vị trí tương đối trong lòng trang đó đang nằm dưới
        /// cursor (xem FindContinuousPageAnchorAt), rồi sau khi đổi zoom, tìm LẠI đúng trang đó (cùng
        /// PageRow, tra qua ItemContainerGenerator — KHÔNG giữ tham chiếu Visual cũ vì
        /// VirtualizingPanel recycle container cho trang khác) để tính offset mới sao cho đúng điểm đó
        /// quay lại dưới cursor.
        ///
        /// TRƯỚC ĐÂY dùng công thức tỉ lệ thuần trên TOÀN BỘ ExtentHeight/ScrollableHeight
        /// (offset_mới = offset_cũ × ratio) — đã thử sửa placeholder-aspect sai, margin không scale,
        /// và ép UpdateLayout đồng bộ, nhưng vẫn lệch dần qua nhiều nấc zoom liên tiếp, đặc biệt zoom
        /// out nhỏ rồi zoom in lớn. Nguyên nhân gốc: Extent của VirtualizingStackPanel cho các trang
        /// CHƯA hiện thực hoá luôn chỉ là SỐ ƯỚC LƯỢNG (trung bình/heuristic), không phải số đo thật —
        /// dù mọi trang ĐANG HIỂN THỊ đều đúng tỉ lệ, tổng cộng dồn qua hàng trăm trang ước lượng vẫn
        /// lệch. Neo vào 1 trang ĐANG HIỂN THỊ (đo trực tiếp, không suy luận tổng) loại bỏ hẳn nguồn
        /// sai số này — không còn phụ thuộc độ chính xác của phần còn lại danh sách nữa.</summary>
        private void ZoomContinuousAtPoint(double newZoomRaw, Point viewportPoint)
        {
            if (FindPageScrollViewer(ReaderContinuousList) is not { } zoomScroll)
            {
                SetReaderContinuousZoom(newZoomRaw);
                return;
            }

            double oldZoom = ReaderContinuousZoom;
            double newZoomClamped = ReaderZoomMath.Clamp(newZoomRaw, ReaderMinZoom, ReaderMaxZoom);
            if (oldZoom <= 0 || Math.Abs(newZoomClamped - oldZoom) < 0.0001)
            {
                SetReaderContinuousZoom(newZoomRaw);
                return;
            }

            var anchor = FindContinuousPageAnchorAt(zoomScroll, viewportPoint);
            double offsetBeforeH = zoomScroll.HorizontalOffset;
            double offsetBeforeV = zoomScroll.VerticalOffset;

            SetReaderContinuousZoom(newZoomRaw);

            // BẮT BUỘC UpdateLayout() ở đây, TRƯỚC khi tính/áp offset — Canvas "sizer" (Width/Height
            // phụ thuộc Image.ActualWidth/Height × zoom qua MultiBinding) chỉ được WPF Invalidate, việc
            // đo/sắp xếp lại thật sự bị hoãn tới lượt Render kế tiếp nếu không ép chạy ngay — cần
            // container đã đúng kích thước MỚI trước khi TransformToAncestor đo lại vị trí anchor.
            zoomScroll.UpdateLayout();

            bool anchored = false;
            if (anchor is { } a &&
                ReaderContinuousList.ItemContainerGenerator.ContainerFromItem(a.Row) is FrameworkElement newContainer &&
                newContainer.ActualWidth > 0 && newContainer.ActualHeight > 0)
            {
                Point newTopLeft = newContainer.TransformToAncestor(zoomScroll).Transform(new Point(0, 0));
                double newAnchorX = newTopLeft.X + a.FractionX * newContainer.ActualWidth;
                double newAnchorY = newTopLeft.Y + a.FractionY * newContainer.ActualHeight;

                double requestedH = Math.Max(0, zoomScroll.HorizontalOffset + (newAnchorX - viewportPoint.X));
                double requestedV = Math.Max(0, zoomScroll.VerticalOffset + (newAnchorY - viewportPoint.Y));
                zoomScroll.ScrollToHorizontalOffset(requestedH);
                zoomScroll.ScrollToVerticalOffset(requestedV);
                anchored = true;
            }
            else
            {
                // Cursor không nằm trên trang nào đang hiện thực hoá (vd trỏ vào Padding/nền của
                // ListBox) — fallback về công thức tỉ lệ thuần cũ, vẫn tốt hơn là không làm gì, và chỉ
                // xảy ra ở rìa viewport nên sai số (nếu có) không đáng kể.
                double ratio = newZoomClamped / oldZoom;
                double topPad = ReaderContinuousList.Padding.Top;
                double leftPad = ReaderContinuousList.Padding.Left;
                double cursorContentX = offsetBeforeH + viewportPoint.X;
                double cursorContentY = offsetBeforeV + viewportPoint.Y;
                double newContentX = leftPad + (cursorContentX - leftPad) * ratio;
                double newContentY = topPad + (cursorContentY - topPad) * ratio;
                zoomScroll.ScrollToHorizontalOffset(Math.Max(0, newContentX - viewportPoint.X));
                zoomScroll.ScrollToVerticalOffset(Math.Max(0, newContentY - viewportPoint.Y));
            }

            zoomScroll.UpdateLayout();

            // Tracing is opt-in and throttled, outside normal interactive operation.
            if (RenderDiagnostics.TraceEnabled) try
            {
                string anchorInfo = anchor is { } logged
                    ? $"page={logged.Row.PageNumber} fraction=({logged.FractionX:F2},{logged.FractionY:F2})"
                    : "page=(none)";
                string line = $"{DateTime.Now:HH:mm:ss.fff} | oldZoom={oldZoom:F3} newZoom={newZoomClamped:F3} " +
                    $"viewportPoint=({viewportPoint.X:F0},{viewportPoint.Y:F0}) anchored={anchored} {anchorInfo} " +
                    $"offsetBeforeV={offsetBeforeV:F0} offsetAfterV={zoomScroll.VerticalOffset:F0}";
                LogContinuousZoomDebug(line);
            }
            catch
            {
                // best-effort debug log only
            }
        }

        /// <summary>Gọi từ wheel handler thay vì ZoomReaderAtPoint/ZoomContinuousAtPoint trực
        /// tiếp — nếu chưa có khung hình nào đang chờ áp zoom thì đặt lịch 1 lần ở
        /// DispatcherPriority.Render (đúng nhịp trước khi WPF render khung kế tiếp); nếu đã có
        /// khung đang chờ (vài nấc wheel đến dồn dập trong cùng 1 khung) thì CHỈ cập nhật đích
        /// zoom + điểm neo mới nhất, KHÔNG đặt lịch thêm — dồn nhiều nấc thành đúng 1 lần
        /// UpdateLayout+sửa điểm neo khi khung đó thực sự chạy.</summary>
        private void RequestReaderZoomAtPoint(int wheelDelta, Point viewportPoint, bool continuous)
        {
            double baseZoom = _readerZoomFramePending
                ? _readerZoomFramePendingTarget
                : (continuous ? ReaderContinuousZoom : _readerZoom);
            _readerZoomFramePendingTarget = ReaderZoomMath.WheelZoom(
                baseZoom, wheelDelta, ReaderZoomStep, ReaderMinZoom, ReaderMaxZoom);
            _readerZoomFrameAnchor = viewportPoint;
            _readerZoomFrameContinuous = continuous;

            if (_readerZoomFramePending) return;
            _readerZoomFramePending = true;
            _ = Dispatcher.InvokeAsync(() =>
            {
                _readerZoomFramePending = false;
                if (_readerZoomFrameContinuous) ZoomContinuousAtPoint(_readerZoomFramePendingTarget, _readerZoomFrameAnchor);
                else ZoomReaderAtPoint(_readerZoomFramePendingTarget, _readerZoomFrameAnchor);
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
        private Point ReaderContinuousViewportCenter()
        {
            if (FindPageScrollViewer(ReaderContinuousList) is { } sv)
                return new Point(sv.ViewportWidth / 2, sv.ViewportHeight / 2);
            return new Point(ReaderContinuousList.ActualWidth / 2, ReaderContinuousList.ActualHeight / 2);
        }

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
            if (_readerContinuousMode) { ZoomContinuousAtPoint(ReaderContinuousZoom * ReaderZoomStep, ReaderContinuousViewportCenter()); return; }
            ZoomReaderAtPoint(_readerZoom * ReaderZoomStep, ReaderViewportCenter());
        }

        private void ReaderZoomOut_Click(object sender, RoutedEventArgs e)
        {
            if (_readerContinuousMode) { ZoomContinuousAtPoint(ReaderContinuousZoom / ReaderZoomStep, ReaderContinuousViewportCenter()); return; }
            ZoomReaderAtPoint(_readerZoom / ReaderZoomStep, ReaderViewportCenter());
        }

        /// <summary>Lăn chuột THƯỜNG (không cần giữ Ctrl) = zoom neo theo con trỏ —
        /// đã có kéo-thả để pan (xem ReaderImage_MouseMove) nên khỏi cần dành riêng
        /// lăn chuột cho cuộn dọc nữa, giống quy ước Google Maps/nhiều app xem ảnh.</summary>
        private void ReaderScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
            RequestReaderZoomAtPoint(e.Delta, e.GetPosition(ReaderScrollViewer), continuous: false);
            e.Handled = true;
        }

        private void ReaderContinuousList_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsFromScrollChrome(e.OriginalSource as DependencyObject)) return;
            if (FindPageScrollViewer(ReaderContinuousList) is not { } sv) return;
            _readerPanning = true;
            _readerPanStartMouse = e.GetPosition(ReaderContinuousList);
            _readerPanStartH = sv.HorizontalOffset;
            _readerPanStartV = sv.VerticalOffset;
            ReaderContinuousList.CaptureMouse();
            e.Handled = true;
        }

        private void ReaderContinuousList_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_readerPanning || e.LeftButton != MouseButtonState.Pressed) return;
            if (FindPageScrollViewer(ReaderContinuousList) is not { } sv) return;
            MarkReaderInteraction();
            Point current = e.GetPosition(ReaderContinuousList);
            RequestReaderPanTo(sv,
                _readerPanStartH - (current.X - _readerPanStartMouse.X),
                _readerPanStartV - (current.Y - _readerPanStartMouse.Y));
            e.Handled = true;
        }

        private void ReaderContinuousList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
            => StopContinuousReaderPan();

        private void ReaderContinuousList_LostMouseCapture(object sender, MouseEventArgs e)
            => _readerPanning = false;

        private void StopContinuousReaderPan()
        {
            _readerPanning = false;
            if (ReaderContinuousList.IsMouseCaptured) ReaderContinuousList.ReleaseMouseCapture();
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
