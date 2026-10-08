using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XTPdfMergeApp.Services;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// Chế độ Cuộn liên tục kiểu Foxit: MỘT vùng vẽ duy nhất, không ListBox/phần tử cho từng trang.
/// - Vị trí mọi trang là phép tính (<see cref="ContinuousViewport"/>): cuộn/zoom chỉ đổi vài con số rồi vẽ lại ngay
///   khung hình kế tiếp.
/// - Mỗi khung hình dùng ảnh đủ đọc tốt nhất đang có (ảnh trang ≤ 2304 px, vùng nét khi zoom sâu).
///   Thumbnail 340 px chỉ hiện khi trang đủ nhỏ; không phóng thumbnail thô lên vùng đọc.
///   Giữ ảnh đủ đọc cũ trong lúc chờ ảnh nét mới.
/// - Trang đang hiện luôn được xin vẽ ngay ở độ phân giải cần dùng dù đang cuộn;
///   chỉ việc tải trước và đổi độ phân giải khi zoom mới chờ ổn định một nhịp.
/// </summary>
public sealed class ContinuousPdfView : Grid
{
    // Với tài liệu rất dài, thumb theo tỉ lệ đúng có thể nhỏ hơn vài pixel và không còn kéo được.
    // Đây chỉ là kích thước hiển thị tối thiểu; Maximum/Value vẫn là toạ độ cuộn thực của tài liệu.
    private const double MinimumVerticalThumbHeight = 48.0;
    private const double VerticalScrollTrackInsets = 10.0;
    // ── Ngưỡng ─────────────────────────────────────────────────────────────
    /// <summary>Ảnh trang cả trang tối đa (như chế độ 1 trang); zoom sâu hơn thì vẽ vùng đang nhìn.</summary>
    private static int MaxPageBitmapWidth => ExperimentalMuPdfViewport.ThroughputMode ? 4608 : 2304;
    private const int MinPageBitmapWidth = 512;
    /// <summary>Ảnh đầu tiên trong vùng đọc phải đủ rõ; thumbnail sidebar 340 px chỉ dùng cho trang hiện rất nhỏ.</summary>
    internal const int ReadablePageBitmapWidth = 1024;
    /// <summary>Trang hiện nhỏ hơn chừng này (px thiết bị) thì ảnh nhỏ 340 px của thumbnail đã đủ nét.</summary>
    private const double PreviewSufficientPx = ThumbnailCache.RenderThumbnailWidthPx * 1.05;
    /// <summary>Độ phân giải vùng nét lượng tử hoá theo bậc 400 px — zoom nhích 1% không phải vẽ lại.</summary>
    private const int RegionResolutionQuantum = 400;
    private const int MaxRegionFullWidth = 65536;
    /// <summary>Vùng nét vẽ dư 60% so với cỡ đang cần (xem <see cref="ZoomHeadroom"/>); vùng chỉ phủ khung nhìn nên tốn thêm ~2,5× điểm ảnh.</summary>
    private const double RegionHeadroom = 1.6;
    /// <summary>Zoom lớn: khi rảnh, mở rộng vùng nét ra quanh khung nhìn (tới cỡ này) để pan liên tục không đuổi kịp mép vùng
    /// (ảnh nét sẵn quanh khung nhìn thay vì làm nét sau khi pan tới). Vẽ ở mức nền, bị huỷ ngay khi cần vùng thật. XTPDF_WIDE_REGION=0 tắt.</summary>
    internal static bool WideRegions { get; set; } = Environment.GetEnvironmentVariable("XTPDF_WIDE_REGION") != "0";
    private const long WideRegionPixels = 9_000_000;
    private const double WideMargin = 1.0, WideCheckMargin = 0.4;
    private const double RegionLowWater = 1.06, RegionHighWater = 3.0;
    /// <summary>Bộ nhớ đệm vùng nét (cả vùng vẽ trước cho bước zoom kế): mỗi vùng ~4 MB nên 16 MiB chỉ chứa 4 vùng.</summary>
    internal static long RegionCacheBudgetBytes => (ExperimentalMuPdfViewport.BalancedMode ? 80L : ExperimentalMuPdfViewport.ThroughputMode ? 1024L : 128L) * 1024 * 1024;
    private static readonly object RegionCacheLock = new();
    private static readonly BitmapMemoryCache<CachedRegionKey> RegionCache = new(RegionCacheBudgetBytes);
    private static long _regionCacheGeneration;
    private HashSet<(string Path, int Page)> _memoryProtectedPages = new();
    private HashSet<(string Path, int Page)> _nativeProtectedPages = new();
    private HashSet<BitmapSource> _memoryProtectedImages = new(ReferenceEqualityComparer.Instance);
    internal bool IsMemoryProtectedImage(BitmapSource image) => Volatile.Read(ref _memoryProtectedImages).Contains(image);
    internal IEnumerable<(string Path, int Page)> MemoryProtectedPages => Volatile.Read(ref _memoryProtectedPages);
    internal IEnumerable<(string Path, int Page)> NativeProtectedPages => Volatile.Read(ref _nativeProtectedPages);
    internal bool IsMemoryProtected(string path, int page) =>
        Volatile.Read(ref _memoryProtectedPages).Contains((path.ToUpperInvariant(), page));
    private MemoryPressureState _memoryPressure;
    private ReaderPerformanceMode _memoryMode;
    internal static long CurrentRegionCacheBudget { get { lock (RegionCacheLock) return RegionCache.BudgetBytes; } }
    internal static void ApplyRegionMemoryBudget(long bytes)
    {
        lock (RegionCacheLock)
        {
            RegionCache.KeepImage = AdaptiveMemoryController.IsProtectedImage;
            if (AdaptiveMemoryController.HasRegisteredViews && (!ReaderPerformanceProfile.Current.RetainDistantImages || !AdaptiveMemoryController.AllowSpeculation))
                RegionCache.RemoveWhere(key => !AdaptiveMemoryController.IsProtected(key.Path, key.Page));
            RegionCache.SetBudget(bytes);
        }
    }

    // Dispatcher only. Displayed page images/regions are never cleared by memory trimming.
    internal void ApplyMemoryPressure(MemoryPressureState pressure)
    {
        Dispatcher.VerifyAccess();
        bool changed = pressure != _memoryPressure || _memoryMode != ReaderPerformanceProfile.Current.Mode;
        _memoryMode = ReaderPerformanceProfile.Current.Mode;
        _memoryPressure = pressure;
        PublishMemoryProtection();
        if (pressure != MemoryPressureState.Normal)
        {
            _warmCts?.Cancel(); _warmCts = null;
            _speculationCts?.Cancel();
            if (_memoryProtectedPages.Count == 0) { _frozen = null; _pv = null; _rv = null; _bases.Clear(); }
            foreach (var (row, state) in _states.ToArray())
            {
                state.CancelWide();
                if (IsMemoryProtected(row.SourcePath, row.PageNumber)) continue;
                state.CancelAll();
                _states.Remove(row);
            }
        }
        if (changed && !_renderingSuspended && IsVisible) ScheduleUpdate();
    }

    private void PublishMemoryProtection()
    {
        var pages = new HashSet<(string Path, int Page)>();
        var nativePages = new HashSet<(string Path, int Page)>();
        var images = new HashSet<BitmapSource>(ReferenceEqualityComparer.Instance);
        foreach (var row in _retainedTabPages)
        {
            pages.Add((row.SourcePath.ToUpperInvariant(), row.PageNumber));
            if (row.ReaderBitmap != null) images.Add(row.ReaderBitmap);
            if (_tabStates.TryGetValue(row, out var saved))
            {
                if (saved.Bitmap != null) images.Add(saved.Bitmap);
                foreach (var region in saved.Regions) images.Add(region.Bitmap);
            }
        }
        if (!_renderingSuspended && IsVisible && _slots.Length > 0)
        {
            foreach (var viewport in new[] { _vp, _pv, _rv }.OfType<ContinuousViewport>())
            {
                var (first, last) = viewport.VisibleRange();
                if (first >= 0 && last < _slots.Length)
                    for (int slot = Math.Max(0, first - 1); slot <= Math.Min(_slots.Length - 1, last + 1); slot++)
                    {
                        var row = _pages[_slots[slot]];
                        pages.Add((row.SourcePath.ToUpperInvariant(), row.PageNumber));
                        nativePages.Add((row.SourcePath.ToUpperInvariant(), row.PageNumber));
                        if (row.ReaderBitmap != null) images.Add(row.ReaderBitmap);
                        if (row.Thumbnail != null) images.Add(row.Thumbnail);
                    }
            }
        }
        foreach (var (row, state) in _states)
            if (pages.Contains((row.SourcePath.ToUpperInvariant(), row.PageNumber)))
            {
                if (state.Bitmap != null) images.Add(state.Bitmap);
                if (state.Preview != null) images.Add(state.Preview);
                foreach (var region in state.Regions) images.Add(region.Bitmap);
            }
        if (_frozen != null)
            foreach (var (row, frame) in _frozen)
                if (pages.Contains((row.SourcePath.ToUpperInvariant(), row.PageNumber)))
                {
                    if (frame.Page != null) images.Add(frame.Page);
                    foreach (var region in frame.Regions) images.Add(region.Bitmap);
                }
        Volatile.Write(ref _memoryProtectedImages, images);
        Volatile.Write(ref _memoryProtectedPages, pages);
        Volatile.Write(ref _nativeProtectedPages, nativePages);
    }
    /// <summary>Cuộn nhanh: chỉ vẽ các trang đang hiện, chờ <see cref="SettleMilliseconds"/> rồi tải trước trang kế.</summary>
    private const double FastScrollViewportsPerSecond = 4;
    private const int SettleMilliseconds = 150;
    /// <summary>Đang zoom: chờ zoom đứng yên chừng này mới xin ảnh ở độ phân giải mới (giữa chừng chỉ co giãn ảnh có sẵn).</summary>
    private const int ZoomSettleMilliseconds = 24;
    internal int PrefetchPageCount { get; set; } = 4;
    internal bool KeepPrefetchedNativePages { get; set; }
    internal bool PreferViewportRegions { get; set; } = true;
    internal bool ReuseRenderedImages { get; set; } = true;
    internal bool ReuseRegionOverlap { get; set; } = true;
    private bool _renderingSuspended;
    internal bool IsRenderingSuspended
    {
        get => _renderingSuspended;
        set
        {
            if (_renderingSuspended == value) return;
            _renderingSuspended = value;
            if (value)
            {
                CancelAll();
                PdfThumbnailService.SetHotPages(Array.Empty<(string, int)>());
            }
            else ScheduleUpdate(immediate: true);
        }
    }
    /// <summary>Trang ngoài [đầu − n, cuối + n] quanh khung nhìn: huỷ việc đang vẽ, bỏ ảnh riêng của view.</summary>
    private const int KeepPages = 4;
    /// <summary>Số trang phía trước (theo hướng cuộn) được xin trước ảnh xem trước nhỏ để lăn nhanh không gặp trang trắng.</summary>
    private const int PreviewAheadPages = 8;

    // ── Thành phần ─────────────────────────────────────────────────────────
    private readonly PageSurface _surface;
    private readonly ScrollBar _vbar;
    private readonly ScrollBar _hbar;
    private readonly ContinuousViewport _vp = new();
    private readonly DispatcherTimer _updateTimer;
    private readonly Dictionary<PageRow, PageState> _states = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PageRow, PageState> _tabStates = new(ReferenceEqualityComparer.Instance);
    private HashSet<PageRow> _retainedTabPages = new(ReferenceEqualityComparer.Instance);
    internal void RetainTabPages(IEnumerable<PageRow> pages)
    {
        _retainedTabPages = new(pages, ReferenceEqualityComparer.Instance);
        foreach (var row in _tabStates.Keys.Where(r => !_retainedTabPages.Contains(r)).ToArray()) _tabStates.Remove(row);
        PublishMemoryProtection();
    }
    private readonly Dictionary<PageRow, int> _indexOf = new(ReferenceEqualityComparer.Instance);
    private IReadOnlyList<PageRow> _pages = Array.Empty<PageRow>();
    /// <summary>Slot bố cục → chỉ số trang. Cuộn liên tục: mọi trang; 1 trang: chỉ trang đang xem.</summary>
    private int[] _slots = Array.Empty<int>();
    private bool _singlePage;
    private bool _twoPage;
    private int _single;
    private int _rotation;

    /// <summary>How far a page is turned on top of the view rotation (turns that wait for Save): degrees clockwise. Set by the reader.</summary>
    internal Func<PageRow, int>? PageTurn { get; set; }

    /// <summary>The rotation a page is shown with: the view rotation plus its own turn.</summary>
    private int RotationOf(PageRow row) => (_rotation + (PageTurn?.Invoke(row) ?? 0)) % 360;
    private INotifyCollectionChanged? _observed;
    private int _currentPage = -1;

    private static readonly Pen BorderPen = CreateBorderPen();
    private readonly List<AnnotationLayer.BaseImage> _bases = new();

    public ContinuousPdfView()
    {
        AdaptiveMemoryController.Register(this);
        _memoryPressure = AdaptiveMemoryController.State;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _surface = new PageSurface(this) { Focusable = false, Cursor = Cursors.Hand, ClipToBounds = true };
        // Luôn co giãn chất lượng cao (GPU làm, rẻ): nét mảnh bản vẽ CAD không răng cưa/nhấp nháy khi cuộn, không có
        // khoảnh khắc "đổi chất lượng" lúc dừng tay như khi hạ xuống LowQuality trong lúc thao tác.
        RenderOptions.SetBitmapScalingMode(_surface, BitmapScalingMode.HighQuality);
        // Thanh cuộn dọc là một phần cố định của vùng đọc (giống Foxit), không phải
        // scrollbar "tự ẩn". Dùng biến thể rõ hơn của theme để người dùng luôn nhìn
        // thấy vị trí trong tài liệu dài, nhưng vẫn giữ track mảnh.
        _vbar = new ScrollBar { Orientation = Orientation.Vertical, Minimum = 0, Visibility = Visibility.Visible };
        _vbar.SetResourceReference(StyleProperty, "UiReaderVerticalScrollBar");
        _hbar = new ScrollBar { Orientation = Orientation.Horizontal, Minimum = 0, Visibility = Visibility.Collapsed };
        SetColumn(_vbar, 1);
        SetRow(_hbar, 1);
        Children.Add(_surface);
        Children.Add(_vbar);
        Children.Add(_hbar);
        _vbar.Scroll += (_, e) => ScrollFromBar(_vp.OffsetX, e.NewValue);
        _hbar.Scroll += (_, e) => ScrollFromBar(e.NewValue, _vp.OffsetY);

        _surface.SizeChanged += (_, _) =>
        {
            _vp.SetViewportSize(_surface.ActualWidth, _surface.ActualHeight);
            ViewportResized?.Invoke();
            OnViewChanged(ChangeKind.Layout);
        };
        _surface.MouseDown += Surface_MouseDown; // chuột trái hoặc giữa: kéo để pan (công cụ chú thích đã chặn ở Preview)
        _surface.MouseMove += Surface_MouseMove;
        _surface.MouseUp += (_, e) => { if (_panning) { EndPan(); e.Handled = true; } };
        _surface.LostMouseCapture += (_, _) => _panning = false;

        // Chỉ lập lịch công việc async, không rasterize trên UI thread. Ưu tiên Render
        // để dòng MouseWheel/MouseMove liên tục không bỏ đói yêu cầu nét.
        _updateTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(16) };
        _updateTimer.Tick += (_, _) => { _updateTimer.Stop(); UpdateRequests(); };
        Unloaded += (_, _) => { CancelAll(); Volatile.Write(ref _memoryProtectedPages, new()); Volatile.Write(ref _nativeProtectedPages, new()); Volatile.Write(ref _memoryProtectedImages, new(ReferenceEqualityComparer.Instance)); };
        IsVisibleChanged += (_, _) => { if (IsVisible) ScheduleUpdate(immediate: true); };
    }

    // ── API cho ReaderWindow ───────────────────────────────────────────────

    /// <summary>Vẽ 1 trang cả trang ở chiều rộng pixel cho trước (ReaderWindow: cache ảnh trang dùng chung với chế độ 1 trang).</summary>
    internal Func<PageRow, int, PdfRenderPriority, CancellationToken, Task<BitmapSource?>>? PageRenderer { get; set; }
    internal Func<PageRow, int, BitmapSource?>? CachedPageProvider { get; set; }
    internal Func<PageRow, int, int, IReadOnlyList<Int32Rect>, CancellationToken, string, Task<List<BitmapSource?>>>? RegionRenderer { get; set; }

    public double MinZoom { get; set; } = 0.05;
    public double MaxZoom { get; set; } = 32.0;
    public double ZoomStep { get; set; } = 1.08;

    /// <summary>Vẽ thêm lên trang, trong khung nội dung theo hướng của trang (đã xoay khung nhìn, đã cắt theo trang):
    /// (dc, trang, khung trang, hệ số DPI). Lớp chú thích vẽ ở đây để cuộn/zoom khớp tuyệt đối với ảnh trang.</summary>
    internal event Action<DrawingContext, PageRow, Rect, double>? PageDrawn;

    /// <summary>Vị trí/zoom đổi (mọi khung hình có thay đổi) — lớp chú thích đặt lại ô nhập theo đây.</summary>
    public event Action? ViewChanged;
    /// <summary>Trang "đang xem" đổi do người dùng cuộn/zoom (không bắn khi ReaderWindow tự gọi <see cref="ScrollToPage"/>).</summary>
    public event Action<int>? CurrentPageChanged;
    /// <summary>Người dùng zoom bằng Ctrl + lăn chuột.</summary>
    public event Action? UserZoomed;
    /// <summary>Người dùng cuộn/kéo/zoom — ReaderWindow cho thumbnail/tải trước nhường PDFium cho trang đang xem.</summary>
    public event Action? UserInteraction;
    /// <summary>Cuộn nhanh vừa dừng (panel trái đồng bộ lúc này thay vì chạy theo từng trang lướt qua).</summary>
    public event Action? ScrollSettled;
    /// <summary>Khung nhìn đổi cỡ (cửa sổ / panel trái) — ReaderWindow khớp lại "vừa chiều rộng".</summary>
    public event Action? ViewportResized;

    public double Zoom => _vp.Zoom;
    public int CurrentPage => _currentPage;
    public bool IsFastScrolling => _fastScroll;
    internal IReadOnlyList<PageRow> Pages => _pages;
    /// <summary>Phần tử vẽ trang — toạ độ "view" của các hàm bên dưới là toạ độ trong phần tử này.</summary>
    public FrameworkElement Surface => _surface;
    /// <summary>Chiều rộng khung nhìn khi có thanh cuộn dọc (thanh cuộn dọc luôn hiện, nên không đổi theo nội dung).</summary>
    public double ViewportWidth => _surface.ActualWidth > 0 ? _surface.ActualWidth
        : Math.Max(0, ActualWidth - 14); // 12px thanh + margin phải 2px (UiReaderVerticalScrollBar).

    /// <summary>Gán tài liệu (null = trống). Vị trí về đầu; ReaderWindow tự cuộn tới trang cần xem.</summary>
    internal void SetDocument(IReadOnlyList<PageRow>? pages, double zoom)
    {
        if (_observed != null) _observed.CollectionChanged -= Pages_CollectionChanged;
        _observed = pages as INotifyCollectionChanged;
        if (_observed != null) _observed.CollectionChanged += Pages_CollectionChanged;
        if (_currentPage >= 0 && _currentPage < _pages.Count)
        {
            var departing = _pages[_currentPage];
            if (_retainedTabPages.Contains(departing) && _states.TryGetValue(departing, out var saved))
                _tabStates[departing] = saved;
        }
        CancelAll();
        _warmed.Clear(); // Completion bookkeeping belongs to this document, not the previous tab.
        _pv = null; _rv = null; _frozen = null;
        _states.Clear();
        Volatile.Write(ref _memoryProtectedPages, new());
        Volatile.Write(ref _memoryProtectedImages, new(ReferenceEqualityComparer.Instance));
        _pages = pages ?? Array.Empty<PageRow>();
        foreach (var row in _pages)
            if (_tabStates.Remove(row, out var saved)) _states[row] = saved;
        _single = 0;
        RebuildIndex();
        _vp.SetPages(BaseSizes(), Math.Clamp(zoom, MinZoom, MaxZoom));
        // Mixed-size pages are centered in the document strip; at a restored deep zoom
        // its left edge may put the selected page entirely outside the viewport.
        _vp.SetOffset(_vp.MaxOffsetX / 2, 0);
        _currentPage = _pages.Count > 0 ? 0 : -1;
        PublishMemoryProtection();
        OnViewChanged(ChangeKind.Navigate);
    }

    /// <summary>Bố cục 1 trang (Foxit "Single Page"): chỉ trang đang xem; lăn quá mép dưới/trên = sang trang kế/trước.</summary>
    public bool SinglePage
    {
        get => _singlePage;
        set
        {
            if (_singlePage == value) return;
            _singlePage = value;
            if (value) _twoPage = false;
            _single = Math.Clamp(_currentPage, 0, Math.Max(0, _pages.Count - 1));
            RebuildIndex();
            _vp.SetColumns(1);
            _vp.SetPages(BaseSizes(), _vp.Zoom);
            if (_pages.Count > 0) _vp.ScrollToPage(SlotOf(_single));
            OnViewChanged(ChangeKind.Navigate);
        }
    }

    /// <summary>Trải hai trang: toàn bộ tài liệu cuộn theo hàng hai trang, dùng cùng renderer/lớp chú thích/hit-test.</summary>
    public bool TwoPage
    {
        get => _twoPage;
        set
        {
            if (_twoPage == value) return;
            _twoPage = value;
            if (value) _singlePage = false;
            _single = Math.Clamp(_currentPage, 0, Math.Max(0, _pages.Count - 1));
            RebuildIndex();
            _vp.SetColumns(value ? 2 : 1);
            _vp.SetPages(BaseSizes(), _vp.Zoom);
            if (_pages.Count > 0) _vp.ScrollToPage(SlotOf(_single));
            OnViewChanged(ChangeKind.Navigate);
        }
    }

    /// <summary>Xoay khung nhìn (0/90/180/270, chiều kim đồng hồ) — chỉ để xem: ảnh trang không vẽ lại, chỉ xoay lúc vẽ.</summary>
    public int ViewRotation
    {
        get => _rotation;
        set
        {
            value = ((value % 360) + 360) % 360 / 90 * 90;
            if (_rotation == value) return;
            int page = _currentPage;
            _rotation = value;
            _vp.SetPages(BaseSizes(), _vp.Zoom);
            if (page >= 0 && page < _pages.Count) _vp.ScrollToPage(SlotOf(page));
            OnViewChanged(ChangeKind.Navigate);
        }
    }

    /// <summary>Kích thước (DIP, zoom 1) trang <paramref name="index"/> như đang hiện (đã tính xoay khung nhìn).</summary>
    internal (double Width, double Height) DisplayBaseSize(int index)
    {
        if (index < 0 || index >= _pages.Count) return (0, 0);
        var row = _pages[index];
        return RotationOf(row) % 180 == 0 ? (row.LayoutWidth, row.LayoutHeight) : (row.LayoutHeight, row.LayoutWidth);
    }

    public double ViewportHeight => _vp.ViewportHeight;

    /// <summary>Some pages were turned (or turned back): their sizes swap, the pages are laid out again and the one on top stays.</summary>
    public void RefreshTurns()
    {
        int page = _currentPage;
        _vp.SetPages(BaseSizes(), _vp.Zoom);
        if (page >= 0 && page < _pages.Count) _vp.ScrollToPage(SlotOf(page));
        OnViewChanged(ChangeKind.Navigate);
    }

    /// <summary>Khổ giấy của một số trang vừa biết/đổi — dựng lại bố cục, trang ở đỉnh khung nhìn đứng yên.</summary>
    public void RefreshPageSizes()
    {
        _vp.UpdatePageSizes(BaseSizes());
        OnViewChanged(ChangeKind.Layout);
    }

    /// <summary>Đổi zoom, giữ điểm dưới <paramref name="viewPoint"/> (toạ độ trong <see cref="Surface"/>) đứng yên.</summary>
    public void ZoomAt(double zoom, Point viewPoint)
    {
        StopZoomGlide();
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(zoom - _vp.Zoom) < 1e-6) return;
        _vp.ZoomAt(zoom, viewPoint.X, viewPoint.Y);
        OnViewChanged(ChangeKind.Zoom);
    }

    /// <summary>
    /// Zoom bằng lăn chuột: ở chế độ progressive, khung nhìn logic đổi ngay và dùng ảnh tốt nhất đang có;
    /// tốc độ di chuyển được giới hạn theo độ nét của viewport để raster nền bắt kịp. Chế độ cũ vẫn giữ ảnh
    /// đúng cỡ trước khi đổi khung hình khi tắt <see cref="ProgressiveZoomPresentation"/>.
    /// </summary>
    public void ZoomAtWhenReady(double zoom, Point viewPoint)
    {
        bool glide = ExactRaster ? PresentTimeoutMilliseconds > 0 : SmoothZoomGlide && ProgressiveZoomPresentation;
        if (!glide || _pages.Count == 0 || PageRenderer == null || _renderingSuspended)
        {
            StopZoomGlide();
            ZoomAt(zoom, viewPoint);
            return;
        }
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (ZoomRateLimit <= 0) { ApplyZoomWhenReady(zoom, viewPoint); return; }
        // Lăn nhanh: đích zoom chạy theo các nấc, còn zoom đang hiện đi tới đích với tốc độ tối đa ZoomRateLimit (ln/giây) — mỗi bước hiện
        // chỉ đổi cỡ một lượng vừa phải, chuyển động liền mạch thay vì vài bước nhảy lớn. Nấc đơn lẻ nằm trong giới hạn nên áp dụng ngay.
        double current = _vp.Zoom;
        _zoomTarget = Math.Clamp(zoom, current / ZoomLeadLimit, current * ZoomLeadLimit);
        _zoomAnchor = viewPoint;
        _lastGlideTimestamp = Stopwatch.GetTimestamp();
        GlideStep(0.016);
        if (_zoomTarget != null) EnsureGlideTimer();
    }

    /// <summary>Tốc độ zoom cố định của lăn chuột, tính bằng ln(zoom)/giây. 2,6 là nhịp liên tục gần với Foxit trên bản vẽ CAD nặng.
    /// 0 = không giới hạn (mỗi nấc đổi zoom ngay như trước). XTPDF_ZOOM_RATE đổi giá trị.</summary>
    internal static double ZoomRateLimit { get; set; } =
        double.TryParse(Environment.GetEnvironmentVariable("XTPDF_ZOOM_RATE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double rate) ? rate : 2.6;
    /// <summary>Progressive (non-exact) zoom also glides at <see cref="ZoomRateLimit"/> instead of jumping a full wheel step at once:
    /// the jump made the heavy CAD page stall for up to ~0.8 s in the capture test. XTPDF_ZOOM_GLIDE=0 restores the jump.</summary>
    internal static bool SmoothZoomGlide { get; set; } = Environment.GetEnvironmentVariable("XTPDF_ZOOM_GLIDE") != "0";
    internal const double MaxPresentStep = 1.45;
    private const double ZoomLeadLimit = 6.0; // đích không đi trước zoom đang hiện quá ×6: thả tay thì dừng ngay, không trôi tiếp
    private double? _zoomTarget;
    private Point _zoomAnchor;
    private long _lastGlideTimestamp;
    private System.Windows.Threading.DispatcherTimer? _glideTimer;

    private void EnsureGlideTimer()
    {
        _glideTimer ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
        _glideTimer.Tick -= GlideTick;
        _glideTimer.Tick += GlideTick;
        _glideTimer.Start();
    }

    private void GlideTick(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Min(0.05, Stopwatch.GetElapsedTime(_lastGlideTimestamp, now).TotalSeconds);
        _lastGlideTimestamp = now;
        GlideStep(dt);
        if (_zoomTarget != null) return;
        _glideTimer?.Stop();
    }

    private void GlideStep(double dt)
    {
        if (_zoomTarget is not { } target) return;
        double current = _vp.Zoom;
        double remaining = Math.Log(target / current), allowed = ZoomRateLimit * dt;
        double next;
        if (Math.Abs(remaining) <= allowed + 1e-9) { next = target; _zoomTarget = null; }
        else next = current * Math.Exp(Math.Sign(remaining) * allowed);
        ApplyZoomWhenReady(next, _zoomAnchor);
        UserZoomed?.Invoke();
    }

    private void StopZoomGlide()
    {
        _zoomTarget = null;
        _glideTimer?.Stop();
    }

    /// <summary>Zoom hiện tại hoặc đích đang đi tới (để các nấc lăn kế tiếp cộng dồn vào đích).</summary>
    internal double ZoomBase => _zoomTarget ?? _vp.Zoom;

    private void ApplyZoomWhenReady(double zoom, Point viewPoint)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(zoom - _vp.Zoom) < 1e-6) return;
        if (ProgressiveZoomPresentation)
        {
            // Foxit-like interaction: geometry reacts in the input turn. The best
            // cached bitmap is scaled immediately; background tiles replace it as
            // they finish. Never hold a heavy CAD drawing behind a sharp-frame wait.
            _vp.ZoomAt(zoom, viewPoint.X, viewPoint.Y);
            OnViewChanged(ChangeKind.Zoom);
            return;
        }
        _lastZoomAnchor = viewPoint;
        _hasZoomAnchor = true;
        _lastWheelZoomTimestamp = Stopwatch.GetTimestamp();
        _speculationCts?.Cancel(); // việc thật đi trước: dừng vẽ trước
        if (_pv == null)
        {
            _pv = _vp.Clone();
            _pendingSince = Stopwatch.GetTimestamp();
            Freeze(_pv);
        }
        _vp.ZoomAt(zoom, viewPoint.X, viewPoint.Y);
        OnViewChanged(ChangeKind.Zoom);
    }

    /// <summary>Đóng băng ảnh đang hiện của các trang trong <paramref name="vp"/>: ảnh mới về (cho zoom kế) không được lọt vào khung đang hiện.</summary>
    private void Freeze(ContinuousViewport vp)
    {
        _frozen = new Dictionary<PageRow, (BitmapSource?, RegionImage[])>(ReferenceEqualityComparer.Instance);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var (first, last) = vp.VisibleRange();
        for (int s = Math.Max(0, first); first >= 0 && s <= last && s < _slots.Length; s++)
        {
            var row = _pages[_slots[s]];
            if (!_states.TryGetValue(row, out var state)) continue;
            var page = PickBitmap(row, state, vp.PageContentRect(s).Width * dpi);
            // Trang chưa có ảnh nào: để nguyên (hiện ảnh xem trước thô khi nó về) thay vì giữ khung trắng.
            if (page != null || state.Regions.Count > 0) _frozen[row] = (page, state.Regions.ToArray());
        }
    }

    /// <summary>Bỏ giữ khung, hiện khung logic ngay (cuộn/nhảy trang, quá hạn, huỷ).</summary>
    private void CommitPresent()
    {
        if (_pv == null) return;
        _pv = null;
        _rv = null;
        _frozen = null;
        _surface.InvalidateVisual();
    }

    /// <summary>Ảnh đúng cỡ của <see cref="_rv"/> đã về: hiện đúng zoom đó. Nếu zoom logic đã đi tiếp thì bắt đầu vẽ bước kế.</summary>
    private void AdvancePresent()
    {
        _pv = _rv;
        _rv = null;
        _fades.Clear();
        _commitFrame = true; // ảnh đúng cỡ vừa về: hiện luôn, không mờ dần
        if (_pv == null || Math.Abs(_vp.Zoom - _pv.Zoom) < 1e-6) { _pv = null; _frozen = null; ScheduleUpdate(); }
        else
        {
            Freeze(_pv);
            _pendingSince = Stopwatch.GetTimestamp();
            ScheduleUpdate(immediate: true);
        }
        _surface.InvalidateVisual();
    }

    private bool PresentReady(ContinuousViewport vp)
    {
        if (PageRenderer == null) return true;
        var (first, last) = vp.VisibleRange();
        if (first < 0) return true;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        for (int s = first; s <= last && s < _slots.Length; s++)
        {
            var row = _pages[_slots[s]];
            if (!_states.TryGetValue(row, out var state)) return false;
            double needed = row.LayoutWidth * vp.Zoom * dpi;
            if (needed <= PreviewSufficientPx)
            {
                if (state.Preview == null && row.Thumbnail == null) return false;
            }
            else if (needed > RegionStartPx(dpi))
            {
                if (!RegionCovered(vp, s, row, state, needed)) return false;
            }
            else if (NeedsPageBitmap(row, state, ExactPageWidth(needed), needed)) return false;
        }
        return true;
    }

    /// <summary>Gọi sau mỗi lượt xin ảnh/khi ảnh về: ảnh đúng cỡ đã đủ thì hiện, quá hạn thì hiện zoom logic với ảnh tạm, chưa thì hẹn xem lại.</summary>
    private void CheckPresent()
    {
        if (_pv == null || _rv == null) return;
        bool ready = PresentReady(_rv);
        bool late = Stopwatch.GetElapsedTime(_pendingSince).TotalMilliseconds >= PresentTimeoutMilliseconds;
        if (ready) { if (DiagnosticsLog.Enabled) DiagnosticsLog.Event($"PRESENT ready after {Stopwatch.GetElapsedTime(_pendingSince).TotalMilliseconds:0} ms at zoom {_rv.Zoom:0.000}"); AdvancePresent(); return; }
        if (late)
        {
            if (DiagnosticsLog.Enabled) DiagnosticsLog.Event($"PRESENT TIMEOUT after {Stopwatch.GetElapsedTime(_pendingSince).TotalMilliseconds:0} ms at zoom {_rv.Zoom:0.000} (logical {_vp.Zoom:0.000})");
            CommitPresent();
            return;
        }
        _updateTimer.Interval = TimeSpan.FromMilliseconds(16);
        _updateTimer.Start();
    }

    /// <summary>Đổi zoom giữ điểm giữa-đỉnh khung nhìn (vừa chiều rộng, nút +/−).</summary>
    public void ZoomKeepTop(double zoom) => ZoomAt(zoom, new Point(_vp.ViewportWidth / 2, 0));

    public Point ViewportCenter => new(_vp.ViewportWidth / 2, _vp.ViewportHeight / 2);
    internal Point ViewOffset => new(_vp.OffsetX, _vp.OffsetY);
    internal void RestoreViewOffset(Point offset)
    {
        if (_vp.SetOffset(offset.X, offset.Y)) OnViewChanged(ChangeKind.Navigate);
    }

    /// <summary>Đỉnh trang <paramref name="index"/> lên đỉnh khung nhìn (Home/End, ô số trang, bấm thumbnail).</summary>
    public void ScrollToPage(int index) => ScrollToPage(index, bottom: false);

    /// <param name="bottom">Chế độ 1 trang, lùi trang bằng lăn chuột: hiện phần cuối trang (như Foxit).</param>
    private void ScrollToPage(int index, bool bottom)
    {
        if (index < 0 || index >= _pages.Count) return;
        if (_singlePage && index != _single)
        {
            _single = index;
            RebuildIndex();
            _vp.SetPages(BaseSizes(), _vp.Zoom);
        }
        _vp.ScrollToPage(SlotOf(index));
        if (bottom) _vp.SetOffset(_vp.OffsetX, _vp.MaxOffsetY);
        bool changed = _currentPage != index;
        _currentPage = index; // người dùng chọn trang này — không đổi sang trang "hiện nhiều hơn" bên cạnh
        OnViewChanged(ChangeKind.Navigate);
        if (changed && _singlePage) CurrentPageChanged?.Invoke(index);
    }

    /// <summary>Cuộn thêm (DIP) — phím mũi tên.</summary>
    public void ScrollBy(double dx, double dy)
    {
        if (_vp.SetOffset(_vp.OffsetX + dx, _vp.OffsetY + dy)) OnViewChanged(ChangeKind.UserScroll);
    }

    /// <summary>Các trang đang hiện trong khung nhìn.</summary>
    internal IEnumerable<PageRow> VisiblePages()
    {
        var (first, last) = _vp.VisibleRange();
        for (int s = first; s >= 0 && s <= last && s < _slots.Length; s++) yield return _pages[_slots[s]];
    }

    /// <summary>Trang dưới điểm (toạ độ trong <see cref="Surface"/>) + vị trí chuẩn hoá (u, v) trên trang (hướng của trang, không xoay khung nhìn).</summary>
    internal bool TryHitPage(Point viewPoint, out PageRow? row, out double u, out double v)
    {
        row = null;
        if (!_vp.HitTest(viewPoint.X, viewPoint.Y, out int slot, out double du, out double dv) || slot >= _slots.Length)
        {
            u = v = 0;
            return false;
        }
        row = _pages[_slots[slot]];
        (u, v) = Unrotate(du, dv, RotationOf(row));
        return true;
    }

    /// <summary>Khung nội dung trang như đang hiện (không viền, đã xoay khung nhìn) trong toạ độ <see cref="Surface"/>.</summary>
    internal bool TryGetPageRect(PageRow row, out Rect rect)
    {
        rect = Rect.Empty;
        if (!_indexOf.TryGetValue(row, out int index) || SlotOf(index) is not (>= 0 and var slot) || slot >= _vp.Count) return false;
        var (x, y, w, h) = _vp.PageContentRect(slot);
        rect = new Rect(x, y, w, h);
        return true;
    }

    /// <summary>(u, v) trên trang → điểm trong <see cref="Surface"/>. False nếu trang không nằm trong bố cục.</summary>
    internal bool TryPageToView(PageRow row, double u, double v, out Point point)
    {
        point = default;
        if (!TryGetPageRect(row, out Rect rect)) return false;
        var (du, dv) = Rotate(u, v, RotationOf(row));
        point = new Point(rect.X + du * rect.Width, rect.Y + dv * rect.Height);
        return true;
    }

    /// <summary>Điểm trong <see cref="Surface"/> → (u, v) trên trang (chưa kẹp 0..1).</summary>
    internal bool TryViewToPage(PageRow row, Point viewPoint, out double u, out double v)
    {
        u = v = 0;
        if (!TryGetPageRect(row, out Rect rect) || rect.Width <= 0 || rect.Height <= 0) return false;
        (u, v) = Unrotate((viewPoint.X - rect.X) / rect.Width, (viewPoint.Y - rect.Y) / rect.Height, RotationOf(row));
        return true;
    }

    /// <summary>Hướng trang → hướng đang hiện (xoay khung nhìn theo chiều kim đồng hồ).</summary>
    private (double U, double V) Rotate(double u, double v, int rotation) => rotation switch
    {
        90 => (1 - v, u),
        180 => (1 - u, 1 - v),
        270 => (v, 1 - u),
        _ => (u, v)
    };

    private (double U, double V) Unrotate(double du, double dv, int rotation) => rotation switch
    {
        90 => (dv, 1 - du),
        180 => (1 - du, 1 - dv),
        270 => (1 - dv, du),
        _ => (du, dv)
    };

    /// <summary>Trang vừa đổi nội dung (chú thích, layer): vẽ lại. <paramref name="dropImages"/> = true khi hình học đổi
    /// (xoay trang) — ảnh cũ sai tỉ lệ, bỏ ngay; false: ảnh cũ vẫn hiện tới khi ảnh mới xong.</summary>
    internal void InvalidatePages(Func<PageRow, bool> match, bool dropImages)
    {
        foreach (var row in _pages.Where(match))
            InvalidateCachedRegions((path, page) => page == row.PageNumber &&
                string.Equals(path, row.SourcePath, StringComparison.OrdinalIgnoreCase));
        // Ảnh Reader của trang (dùng chung với chế độ 1 trang) là nội dung cũ; ảnh riêng của view vẫn hiện tạm tới khi có ảnh mới.
        foreach (var row in _pages.Concat(_retainedTabPages).Distinct())
            if (match(row)) row.ReaderBitmap = null;
        foreach (var (row, state) in _states.Concat(_tabStates).ToArray())
        {
            if (!match(row)) continue;
            state.Version++;
            state.CancelAll();
            if (dropImages) { state.Bitmap = null; state.Preview = null; state.Regions.Clear(); }
        }
        PublishMemoryProtection();
        _surface.InvalidateVisual();
        ScheduleUpdate(immediate: true);
    }

    /// <summary>Cho cửa sổ Debug: số trang đang giữ ảnh riêng, số vùng nét và dung lượng vùng nét.</summary>
    internal (int Pages, int Regions, long RegionBytes) MemoryStats
    {
        get
        {
            int regions = 0;
            HashSet<BitmapSource> images;
            lock (RegionCacheLock) images = new(RegionCache.Bitmaps, ReferenceEqualityComparer.Instance);
            foreach (var state in _states.Values)
                foreach (var region in state.Regions)
                {
                    regions++;
                    images.Add(region.Bitmap);
                }
            // Live regions often share the LRU bitmap; count their storage only once.
            return (_states.Count, regions, images.Sum(BitmapMemoryCache<int>.SizeOf));
        }
    }

    /// <summary>Huỷ mọi việc vẽ đang chờ (ẩn Viewer, đổi chế độ xem).</summary>
    public void CancelAll()
    {
        _warmCts?.Cancel();
        _warmCts = null;
        foreach (var state in _states.Values) state.CancelAll();
        _updateTimer.Stop();
    }

    // ── Thay đổi khung nhìn ────────────────────────────────────────────────

    private enum ChangeKind { Layout, Navigate, Zoom, UserScroll }

    private long _lastScrollTimestamp, _lastZoomTimestamp, _lastPanTimestamp;
    private double _lastOffsetX;
    private int _panSignX, _panSignY;
    /// <summary>Pan gần đây (ms): trong thời gian này vùng nét được giữ rộng về phía đang đi để ảnh nét sẵn trước khi tới.</summary>
    private const double PanRecentMilliseconds = 700;
    private bool _fastScroll;
    private int _scrollDirection = 1;
    private double _lastOffsetY;

    private void OnViewChanged(ChangeKind kind)
    {
        if (kind is ChangeKind.Navigate or ChangeKind.UserScroll or ChangeKind.Zoom) AdaptiveMemoryController.NoteActivity();
        // Cuộn/nhảy trang khi đang giữ khung: hiện khung logic ngay (khung cũ đã lệch so với vị trí cuộn mới). Đổi cỡ vùng vẽ thì KHÔNG chốt:
        // zoom vào làm hiện thanh cuộn ngang nên vùng vẽ thấp đi vài điểm ảnh — khung đang hiện chỉ cập nhật cỡ khung nhìn của nó.
        if (kind is ChangeKind.Navigate or ChangeKind.UserScroll)
        {
            CommitPresent();
            StopZoomGlide();
            _speculationCts?.Cancel(); // cuộn/nhảy trang: việc thật cần cả hai bản PDFium, không vẽ đoán nữa
            _hasZoomAnchor = false;
        }
        else if (kind == ChangeKind.Layout && _pv != null) { _pv.SetViewportSize(_vp.ViewportWidth, _vp.ViewportHeight); _rv?.SetViewportSize(_vp.ViewportWidth, _vp.ViewportHeight); }
        UpdateScrollBars();
        _surface.InvalidateVisual();

        long now = Stopwatch.GetTimestamp();
        double dy = _vp.OffsetY - _lastOffsetY;
        double dx = _vp.OffsetX - _lastOffsetX;
        _lastOffsetY = _vp.OffsetY;
        _lastOffsetX = _vp.OffsetX;
        if (kind == ChangeKind.UserScroll && (dx != 0 || dy != 0))
        {
            _panSignX = Math.Sign(dx); _panSignY = Math.Sign(dy);
            if (DiagnosticsLog.Enabled && Stopwatch.GetElapsedTime(_lastPanTimestamp).TotalMilliseconds > 400) DiagnosticsLog.Event("PAN start");
            _lastPanTimestamp = now;
        }
        if (kind == ChangeKind.Zoom) _lastZoomTimestamp = now;
        if (kind == ChangeKind.UserScroll && dy != 0 && _vp.ViewportHeight > 0)
        {
            _scrollDirection = Math.Sign(dy);
            double seconds = Math.Max(0.001, (now - _lastScrollTimestamp) / (double)Stopwatch.Frequency);
            if (Math.Abs(dy) / _vp.ViewportHeight / seconds > FastScrollViewportsPerSecond) _fastScroll = true;
            _lastScrollTimestamp = now;
        }
        else if (kind == ChangeKind.Navigate)
        {
            _fastScroll = false; // nhảy trang chủ động: vẽ trang đích ngay
        }

        if (kind != ChangeKind.Navigate)
        {
            int slot = _vp.CurrentPage();
            int current = slot >= 0 && slot < _slots.Length ? _slots[slot] : -1;
            if (current != _currentPage && current >= 0)
            {
                _currentPage = current;
                CurrentPageChanged?.Invoke(current);
            }
        }
        ViewChanged?.Invoke();
        ScheduleUpdate(immediate: kind is ChangeKind.Navigate or ChangeKind.UserScroll || (kind == ChangeKind.Zoom && _pv != null && _rv == null));
    }

    private bool _updateQueued;

    private void ScheduleUpdate(bool immediate = false)
    {
        if (_renderingSuspended) return;
        if (immediate)
        {
            _updateTimer.Stop();
            if (_updateQueued) return;
            _updateQueued = true;
            _ = Dispatcher.InvokeAsync(() => { _updateQueued = false; UpdateRequests(); }, DispatcherPriority.Render);
            return;
        }
        if (!_updateTimer.IsEnabled) _updateTimer.Start();
    }

    private void UpdateScrollBars()
    {
        double maxY = _vp.MaxOffsetY, maxX = _vp.MaxOffsetX;
        _vbar.Maximum = maxY;
        // Track tính chiều dài thumb từ ViewportSize. Nới riêng giá trị trình bày này để
        // thumb không biến thành một chấm ở PDF hàng trăm trang; vị trí kéo vẫn map theo
        // Maximum/Value thật nên không làm sai trang đang xem.
        double usableTrackHeight = Math.Max(1, _vbar.ActualHeight - VerticalScrollTrackInsets);
        double minThumbRatio = Math.Clamp(MinimumVerticalThumbHeight / usableTrackHeight, 0.02, 0.75);
        double visualViewport = maxY > 0
            ? Math.Max(_vp.ViewportHeight, maxY * minThumbRatio / (1 - minThumbRatio))
            : _vp.ViewportHeight;
        _vbar.ViewportSize = visualViewport;
        _vbar.LargeChange = Math.Max(1, _vp.ViewportHeight * 0.9);
        _vbar.SmallChange = 100.0 / 3;
        _vbar.Value = _vp.OffsetY;
        _vbar.IsEnabled = maxY > 0;

        var hVisibility = maxX > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        if (_hbar.Visibility != hVisibility) _hbar.Visibility = hVisibility;
        _hbar.Maximum = maxX;
        _hbar.ViewportSize = _vp.ViewportWidth;
        _hbar.LargeChange = Math.Max(1, _vp.ViewportWidth * 0.9);
        _hbar.SmallChange = 100.0 / 3;
        _hbar.Value = _vp.OffsetX;
    }

    private void ScrollFromBar(double x, double y)
    {
        UserInteraction?.Invoke();
        if (_vp.SetOffset(x, y)) OnViewChanged(ChangeKind.UserScroll);
    }

    private void Pages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Thêm/xoá/chuyển trang: dựng lại bố cục (trang ở đỉnh khung nhìn giữ nguyên nếu còn).
        RebuildIndex();
        foreach (var row in _states.Keys.Where(r => !_indexOf.ContainsKey(r)).ToList())
        {
            _states[row].CancelAll();
            _states.Remove(row);
        }
        _vp.UpdatePageSizes(BaseSizes());
        _currentPage = Math.Min(_currentPage, _pages.Count - 1);
        OnViewChanged(ChangeKind.Layout);
    }

    private void RebuildIndex()
    {
        PageRow? singleRow = _singlePage && _single >= 0 && _single < _pages.Count ? _pages[_single] : null;
        _indexOf.Clear();
        for (int i = 0; i < _pages.Count; i++) _indexOf[_pages[i]] = i;
        if (_singlePage)
        {
            // Trang đang xem còn trong danh sách (thêm/bớt/chuyển trang khác) thì giữ nó.
            if (singleRow != null && _indexOf.TryGetValue(singleRow, out int kept)) _single = kept;
            _single = Math.Clamp(_single, 0, Math.Max(0, _pages.Count - 1));
            _slots = _pages.Count > 0 ? new[] { _single } : Array.Empty<int>();
        }
        else
        {
            _slots = new int[_pages.Count];
            for (int i = 0; i < _slots.Length; i++) _slots[i] = i;
        }
    }

    /// <summary>Slot bố cục của trang, −1 nếu trang không nằm trong bố cục (chế độ 1 trang).</summary>
    private int SlotOf(int pageIndex) => _singlePage ? (pageIndex == _single && _slots.Length > 0 ? 0 : -1) : pageIndex;

    private (double Width, double Height)[] BaseSizes()
    {
        var sizes = new (double, double)[_slots.Length];
        for (int s = 0; s < sizes.Length; s++) sizes[s] = DisplayBaseSize(_slots[s]);
        return sizes;
    }

    // ── Chuột ──────────────────────────────────────────────────────────────

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (_pages.Count == 0) return;
        UserInteraction?.Invoke();
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            double accel = _wheelAccel.Next(Math.Abs(e.Delta) / 120.0, Math.Sign(e.Delta), Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
            ZoomAtWhenReady(ReaderZoomMath.WheelZoom(ZoomBase, (int)Math.Round(e.Delta * accel), ZoomStep, MinZoom, MaxZoom), e.GetPosition(_surface));
            if (_zoomTarget == null) UserZoomed?.Invoke();
        }
        else
        {
            // Như Chrome trên Windows: 100 px mỗi nấc (3 dòng × 33 px), tỉ lệ theo Delta (bàn di chuột cuộn mượt).
            int lines = SystemParameters.WheelScrollLines;
            double perNotch = lines < 0 ? _vp.ViewportHeight : lines * (100.0 / 3);
            double delta = -e.Delta / 120.0 * perNotch;
            if ((modifiers & ModifierKeys.Shift) != 0) ScrollBy(delta, 0);
            else if (_singlePage && delta > 0 && _vp.OffsetY >= _vp.MaxOffsetY - 0.5 && _single < _pages.Count - 1) ScrollToPage(_single + 1, bottom: false);
            else if (_singlePage && delta < 0 && _vp.OffsetY <= 0.5 && _single > 0) ScrollToPage(_single - 1, bottom: true);
            else ScrollBy(0, delta);
        }
        e.Handled = true;
    }

    private readonly ReaderZoomMath.WheelZoomAccelerator _wheelAccel = new();

    private bool _panning;
    private Point _panStart;
    private double _panStartX, _panStartY;

    private void Surface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || _panning) return;
        if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Middle) return;
        _panning = true;
        _panStart = e.GetPosition(_surface);
        _panStartX = _vp.OffsetX;
        _panStartY = _vp.OffsetY;
        _surface.CaptureMouse();
        _surface.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        if (e.LeftButton != MouseButtonState.Pressed && e.MiddleButton != MouseButtonState.Pressed) { EndPan(); return; }
        UserInteraction?.Invoke();
        Point p = e.GetPosition(_surface);
        if (_vp.SetOffset(_panStartX - (p.X - _panStart.X), _panStartY - (p.Y - _panStart.Y)))
            OnViewChanged(ChangeKind.UserScroll);
        e.Handled = true;
    }

    private void EndPan()
    {
        _panning = false;
        if (_surface.IsMouseCaptured) _surface.ReleaseMouseCapture();
        _surface.Cursor = Cursors.Hand;
    }

    // ── Vẽ ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Vẽ đúng cỡ rồi mới hiện (như Foxit). Nét mảnh của bản vẽ CAD được PDFium vẽ tối thiểu 1 điểm ảnh ở độ phân giải của ảnh: ảnh
    /// vẽ nhỏ rồi phóng to thì nét dày/đậm, ảnh vẽ dư rồi thu nhỏ thì nét mảnh/nhạt; mỗi lần thay ảnh khác cỡ là mắt thấy "nét đổi"
    /// (đo trên video 04/10: Foxit giữ nguyên ảnh cũ ~2 s khi vẽ chậm rồi mới hiện ảnh đúng cỡ trong một lần, không có bản trung gian).
    /// Bật: ảnh trang/vùng nét vẽ ĐÚNG cỡ hiển thị (không vẽ dư, không để WPF co giãn), và zoom bằng lăn chuột giữ khung đang hiện
    /// tới khi ảnh đúng cỡ về (hoặc quá <see cref="PresentTimeoutMilliseconds"/>). XTPDF_EXACT_RASTER=0 tắt, trở về chính sách cũ.
    /// </summary>
    private static bool _exactRaster = Environment.GetEnvironmentVariable("XTPDF_EXACT_RASTER") != "0";
    private static readonly bool ForceExact = Environment.GetEnvironmentVariable("XTPDF_FORCE_EXACT") == "1";
    internal static bool ExactRaster
    {
        // XTPDF_FORCE_EXACT=1: measurement switch to compare exact-raster zoom against throughput mode on the MuPDF build; off by default.
        get => (!ExperimentalMuPdfViewport.ThroughputMode || ForceExact) && _exactRaster;
        set => _exactRaster = value;
    }

    /// <summary>Chờ ảnh đúng cỡ tối đa chừng này rồi mới hiện trong chế độ legacy. 0 = không giữ khung.</summary>
    internal static int PresentTimeoutMilliseconds { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("XTPDF_PRESENT_TIMEOUT_MS"), out int presentMs) ? Math.Clamp(presentMs, 0, 5000) : 600;

    /// <summary>
    /// Zoom geometry moves immediately while deep-zoom content is filled by
    /// sufficiently large tiles. Missing deep-zoom coverage stays white instead
    /// of scaling a low-resolution page image into an obvious blur. Set to 0 to
    /// compare the legacy held-frame behavior.
    /// </summary>
    internal static bool ProgressiveZoomPresentation { get; set; } =
        Environment.GetEnvironmentVariable("XTPDF_PROGRESSIVE_ZOOM") != "0";

    /// <summary>Ảnh trang đúng cỡ nằm trong khoảng này so với cỡ cần thì coi là đủ (sai số làm tròn điểm ảnh).</summary>
    private const double ExactLow = 0.985, ExactHigh = 1.04;

    /// <summary>Từ cỡ trang (px thiết bị) này trở lên chỉ vẽ PHẦN ĐANG NHÌN (vùng nét) thay vì cả trang. Chính sách đúng cỡ bật sớm hơn
    /// (trang rộng hơn khung nhìn ~10%): mỗi bước zoom chỉ vẽ cỡ khung nhìn (~1000x560) nên nhanh như Foxit; vẽ cả trang 2304 px mất ~120 ms.</summary>
    private double RegionStartPx(double dpi)
        => ExactRaster ? Math.Clamp(_vp.ViewportWidth * dpi * 1.1, 1100, MaxPageBitmapWidth * 1.03) : MaxPageBitmapWidth * 1.03;

    private static int ExactPageWidth(double neededPx) => (int)Math.Clamp(Math.Round(neededPx), MinPageBitmapWidth, MaxPageBitmapWidth);

    /// <summary>Vẽ trước (ưu tiên nền, lúc nghỉ) ảnh của bước zoom kế theo cả hai hướng: ba nấc đầu của mỗi lượt lăn luôn là ×<see cref="ZoomStep"/>
    /// chính xác nên đoán trước được, nấc đầu hiện gần như tức thì từ bộ nhớ đệm. XTPDF_SPECULATE=0 tắt.</summary>
    internal static bool SpeculateZoomSteps { get; set; } = Environment.GetEnvironmentVariable("XTPDF_SPECULATE") != "0";
    private const int SpeculationWindowMilliseconds = 3000;
    private Point _lastZoomAnchor;
    private bool _hasZoomAnchor;
    private long _lastWheelZoomTimestamp;
    private double _speculatedFor = double.NaN;
    private CancellationTokenSource? _speculationCts;

    // Khung "đang hiện" trong lúc zoom chờ ảnh đúng cỡ: khung nhìn lúc bắt đầu + ảnh đã chọn cho từng trang đang hiện.
    private ContinuousViewport? _pv;
    private ContinuousViewport? _rv; // khung nhìn của bước đang vẽ ảnh đúng cỡ (có thể đứng sau zoom logic khi đang lăn nhanh)
    private Dictionary<PageRow, (BitmapSource? Page, RegionImage[] Regions)>? _frozen;
    private long _pendingSince;
    private bool _commitFrame;

    /// <summary>Thời gian mờ dần (ms) khi ảnh trang/vùng nét hơn thay ảnh đang hiện. Mặc định là 0 để không nhấp nháy.</summary>
    internal static int CrossFadeMilliseconds { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("XTPDF_CROSSFADE_MS"), out int fadeMs) ? Math.Clamp(fadeMs, 0, 1000) : 0;

    /// <summary>Ảnh cũ chỉ được giữ làm nền mờ dần khi nó là ảnh trang thật (không phải thumbnail/ảnh xem trước nhỏ): trang mới mở
    /// không bị kéo dài thêm vì mờ dần từ thumbnail.</summary>
    private const int CrossFadeMinPreviousWidth = 820;

    private sealed class FadeState
    {
        public BitmapSource? Current, Previous;
        public long SwitchTimestamp;
    }

    private sealed class FirstShown { public long Timestamp; }

    private readonly ConditionalWeakTable<PageRow, FadeState> _fades = new();
    private static readonly ConditionalWeakTable<BitmapSource, FirstShown> RegionFirstShown = new();
    private bool _fadeAnimating, _fadeTickQueued;

    private static double FadeAlpha(long since, int fadeMs)
    {
        double t = Stopwatch.GetElapsedTime(since).TotalMilliseconds / fadeMs;
        if (t >= 1) return 1;
        return t <= 0 ? 0 : t * t * (3 - 2 * t); // smoothstep
    }

    private void QueueFadeFrame()
    {
        if (_fadeTickQueued) return;
        _fadeTickQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _fadeTickQueued = false;
            _surface.InvalidateVisual();
        }));
    }

    private void Draw(DrawingContext dc)
    {
        _fadeAnimating = false;
        try { DrawPages(dc); }
        finally { _commitFrame = false; if (_fadeAnimating) QueueFadeFrame(); }
    }

    private void DrawPages(DrawingContext dc)
    {
        double width = _surface.ActualWidth, height = _surface.ActualHeight;
        // Nền do Grid (Background) vẽ; hình chữ nhật trong suốt để cả vùng nhận chuột (kéo ở khe giữa trang cũng pan được).
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        var vp = _pv ?? _vp; // đang chờ ảnh đúng cỡ của zoom mới: vẫn vẽ khung nhìn cũ
        var (first, last) = vp.VisibleRange();
        if (first < 0) return;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;

        for (int s = first; s <= last && s < _slots.Length; s++)
        {
            var row = _pages[_slots[s]];
            var (x, y, w, h) = vp.PageRect(s);
            // Bám pixel thiết bị: viền 1 px sắc, ảnh không nhoè nửa pixel.
            var outer = Snap(new Rect(x, y, w, h), dpi);
            dc.DrawRectangle(Brushes.White, null, outer);
            double b = ContinuousPageLayout.BorderThickness;
            var shown = new Rect(outer.X + b, outer.Y + b, Math.Max(0, outer.Width - 2 * b), Math.Max(0, outer.Height - 2 * b));

            // Xoay khung nhìn: vẽ trang theo hướng của nó trong khung "content" rồi xoay quanh tâm khung đang hiện.
            var content = shown;
            int pageRotation = RotationOf(row);
            bool rotated = pageRotation != 0;
            if (rotated)
            {
                if (pageRotation % 180 != 0)
                    content = new Rect(shown.X + (shown.Width - shown.Height) / 2, shown.Y + (shown.Height - shown.Width) / 2, shown.Height, shown.Width);
                dc.PushClip(new RectangleGeometry(shown));
                dc.PushTransform(new RotateTransform(pageRotation, shown.X + shown.Width / 2, shown.Y + shown.Height / 2));
            }

            _states.TryGetValue(row, out var state);
            _bases.Clear();
            bool hasBase = false;
            // Mọi ảnh đang có đều được vẽ, kể cả thumbnail/ảnh xem trước thô (phóng to nên mờ): trang vừa lăn tới phản hồi tức thì như Foxit,
            // thay vì để trang trắng tới khi ảnh nét về (đo 04/10: lăn nhanh 40 nấc có ~4 trong 12 khung hình là trang trắng hoàn toàn).
            BitmapSource? pageImage;
            IReadOnlyList<RegionImage>? regionList;
            if (_frozen != null && _frozen.TryGetValue(row, out var frozen)) { pageImage = frozen.Page; regionList = frozen.Regions; }
            else { pageImage = PickBitmap(row, state, content.Width * dpi); regionList = state?.Regions; }
            // Keep the last coherent page/region composite visible while the
            // sharper deep-zoom crop is being rendered. The replacement is
            // committed atomically after all tiles are composed, so zoom never
            // exposes white holes or a tile-by-tile assembly to the user.
            if (pageImage is { } bitmap)
            {
                DrawPageImage(dc, row, bitmap, content, dpi, oneToOne: !rotated);
                _bases.Add(new AnnotationLayer.BaseImage(bitmap, new Rect(0, 0, 1, 1)));
                hasBase = true;
            }
            if (regionList is { Count: > 0 })
            {
                dc.PushClip(new RectangleGeometry(content));
                foreach (var region in regionList)
                {
                    var k = region.Key;
                    // Vùng nét mới hiện lên trên nền đã có (ảnh trang hoặc vùng cũ): mờ dần thay vì đổi tức thì.
                    double alpha = 1;
                    if (CrossFadeMilliseconds > 0 && hasBase)
                    {
                        var firstShown = RegionFirstShown.GetValue(region.Bitmap, _ => new FirstShown { Timestamp = Stopwatch.GetTimestamp() });
                        if (_commitFrame) firstShown.Timestamp = Stopwatch.GetTimestamp() - 10 * Stopwatch.Frequency; // ảnh đúng cỡ vừa về: hiện luôn
                        alpha = FadeAlpha(firstShown.Timestamp, CrossFadeMilliseconds);
                        if (alpha < 1) _fadeAnimating = true;
                    }
                    if (alpha < 1) dc.PushOpacity(alpha);
                    dc.DrawImage(region.Bitmap, RegionRect(content, k, dpi, oneToOne: !rotated));
                    if (alpha < 1) dc.Pop();
                    hasBase = true;
                    _bases.Add(new AnnotationLayer.BaseImage(region.Bitmap,
                        new Rect((double)k.X / k.FullWidth, (double)k.Y / k.FullHeight, (double)k.Width / k.FullWidth, (double)k.Height / k.FullHeight)));
                }
                dc.Pop();
            }
            if (DiagnosticsLog.Enabled && row.LayoutWidth * vp.Zoom * dpi > RegionStartPx(dpi) && vp.VisibleFraction(s, out double vx0, out double vy0, out double vx1, out double vy1))
            {
                var (ua, va) = Unrotate(vx0, vy0, pageRotation); var (ub, vb) = Unrotate(vx1, vy1, pageRotation);
                double gx0 = Math.Min(ua, ub), gx1 = Math.Max(ua, ub), gy0 = Math.Min(va, vb), gy1 = Math.Max(va, vb);
                bool full = regionList != null && regionList.Any(r => r.Bitmap != null && r.Key.X <= gx0 * r.Key.FullWidth + 1 && r.Key.Y <= gy0 * r.Key.FullHeight + 1 &&
                    r.Key.X + r.Key.Width >= gx1 * r.Key.FullWidth - 1 && r.Key.Y + r.Key.Height >= gy1 * r.Key.FullHeight - 1);
                DiagnosticsLog.Event(full ? "DRAW sharp" : "DRAW UNSHARP");
            }
            AnnotationLayer.Draw(dc, row, content, dpi, _bases);
            PageDrawn?.Invoke(dc, row, content, dpi);
            if (rotated) { dc.Pop(); dc.Pop(); }
            dc.DrawRectangle(null, BorderPen, new Rect(outer.X + 0.5 / dpi, outer.Y + 0.5 / dpi,
                Math.Max(0, outer.Width - 1 / dpi), Math.Max(0, outer.Height - 1 / dpi)));
        }
    }

    /// <summary>Vẽ ảnh trang; khi nó thay một ảnh trang thật thấp hơn thì mờ dần từ ảnh cũ sang ảnh mới.</summary>
    private void DrawPageImage(DrawingContext dc, PageRow row, BitmapSource bitmap, Rect content, double dpi, bool oneToOne)
    {
        // Ảnh đúng cỡ hiển thị (±1 điểm ảnh): vẽ 1:1 lên lưới điểm ảnh, WPF không lấy mẫu lại nên nét giữ nguyên độ dày như PDFium vẽ.
        if (ExactRaster && oneToOne && Math.Abs(bitmap.PixelWidth - content.Width * dpi) <= 1.0 && Math.Abs(bitmap.PixelHeight - content.Height * dpi) <= 1.5)
            content = new Rect(Math.Round(content.X * dpi) / dpi, Math.Round(content.Y * dpi) / dpi, bitmap.PixelWidth / dpi, bitmap.PixelHeight / dpi);
        if (CrossFadeMilliseconds <= 0) { dc.DrawImage(bitmap, content); return; }
        var fade = _fades.GetOrCreateValue(row);
        if (!ReferenceEquals(fade.Current, bitmap))
        {
            if (fade.Current != null && fade.Current.PixelWidth >= CrossFadeMinPreviousWidth && bitmap.PixelWidth > fade.Current.PixelWidth)
            {
                fade.Previous = fade.Current;
                fade.SwitchTimestamp = Stopwatch.GetTimestamp();
            }
            else fade.Previous = null;
            fade.Current = bitmap;
        }
        if (fade.Previous != null)
        {
            double alpha = FadeAlpha(fade.SwitchTimestamp, CrossFadeMilliseconds);
            if (alpha < 1)
            {
                _fadeAnimating = true;
                dc.DrawImage(fade.Previous, content);
                dc.PushOpacity(alpha);
                dc.DrawImage(bitmap, content);
                dc.Pop();
                return;
            }
            fade.Previous = null;
        }
        dc.DrawImage(bitmap, content);
    }

    /// <summary>Khung vẽ của một vùng nét; vùng đúng cỡ (±1 điểm ảnh) vẽ 1:1 lên lưới điểm ảnh.</summary>
    private static Rect RegionRect(Rect content, RegionKey k, double dpi, bool oneToOne)
    {
        if (ExactRaster && oneToOne && Math.Abs(k.FullWidth - content.Width * dpi) <= 1.0)
            return new Rect(Math.Round(content.X * dpi) / dpi + k.X / dpi, Math.Round(content.Y * dpi) / dpi + k.Y / dpi, k.Width / dpi, k.Height / dpi);
        return new Rect(content.X + content.Width * k.X / k.FullWidth, content.Y + content.Height * k.Y / k.FullHeight,
            content.Width * k.Width / k.FullWidth, content.Height * k.Height / k.FullHeight);
    }

    /// <summary>Ảnh trang để vẽ. Chính sách đúng cỡ: ảnh có cỡ khớp cỡ hiển thị (nét đúng độ dày); không có thì ảnh rộng nhất như cũ
    /// (ảnh to thu nhỏ làm nét mảnh/nhạt đi, nhưng còn hơn trang trắng).</summary>
    private static BitmapSource? PickBitmap(PageRow row, PageState? state, double displayedWidthPx)
    {
        if (!ExactRaster || displayedWidthPx <= 0) return BestBitmap(row, state);
        BitmapSource? exact = null; double bestError = double.MaxValue;
        Consider(state?.Bitmap); Consider(row.ReaderBitmap); Consider(state?.Preview); Consider(row.Thumbnail);
        return exact ?? BestBitmap(row, state);

        void Consider(BitmapSource? candidate)
        {
            if (candidate == null) return;
            double ratio = candidate.PixelWidth / displayedWidthPx;
            if (ratio < ExactLow || ratio > ExactHigh) return;
            double error = Math.Abs(ratio - 1);
            if (error < bestError) { bestError = error; exact = candidate; }
        }
    }

    private static bool IsReadableBitmap(BitmapSource bitmap, double displayedWidthPx)
        => bitmap.PixelWidth >= Math.Min(ReadablePageBitmapWidth, displayedWidthPx) * 0.85;

    private static Rect Snap(Rect r, double dpi)
    {
        double x0 = Math.Round(r.Left * dpi) / dpi, y0 = Math.Round(r.Top * dpi) / dpi;
        double x1 = Math.Round(r.Right * dpi) / dpi, y1 = Math.Round(r.Bottom * dpi) / dpi;
        return new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>Ảnh cả trang nét nhất đang có: ảnh riêng của view, ảnh Reader của trang, ảnh nhỏ.</summary>
    private static BitmapSource? BestBitmap(PageRow row, PageState? state)
    {
        BitmapSource? best = state?.Bitmap;
        Consider(ref best, row.ReaderBitmap);
        Consider(ref best, state?.Preview);
        Consider(ref best, row.Thumbnail);
        return best;

        static void Consider(ref BitmapSource? best, BitmapSource? candidate)
        {
            if (candidate != null && (best == null || candidate.PixelWidth > best.PixelWidth)) best = candidate;
        }
    }

    private static Pen CreateBorderPen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)), 1);
        pen.Freeze();
        return pen;
    }

    // ── Xin vẽ ─────────────────────────────────────────────────────────────

    private void UpdateRequests()
    {
        // Đang giữ khung và chưa có bước nào đang vẽ: bắt đầu vẽ ảnh đúng cỡ cho zoom logic hiện tại.
        if (_pv != null && _rv == null && !_renderingSuspended && _pages.Count > 0 && IsVisible)
        {
            _rv = _vp.Clone();
            // Bước kế không đi xa hơn MaxPresentStep so với khung đang hiện (dù zoom logic đã chạy xa trong lúc bước trước vẽ): mỗi khung
            // hiện ra chỉ đổi cỡ vừa phải như Foxit (≤ ×1,5), chuyển động không bị nhảy cóc khi trang nặng.
            if (_pv != null && _pv.Zoom > 0 && _hasZoomAnchor)
            {
                double ratio = _rv.Zoom / _pv.Zoom;
                if (ratio > MaxPresentStep || ratio < 1 / MaxPresentStep)
                    _rv.ZoomAt(_pv.Zoom * (ratio > 1 ? MaxPresentStep : 1 / MaxPresentStep), _lastZoomAnchor.X, _lastZoomAnchor.Y);
            }
            _pendingSince = Stopwatch.GetTimestamp();
        }
        UpdateRequestsCore(_rv ?? _vp);
        if (_pv == null) { MaybeSpeculate(); return; }
        if (_renderingSuspended || _pages.Count == 0 || !IsVisible) CommitPresent();
        else CheckPresent();
    }

    private void MaybeSpeculate()
    {
        if (!AdaptiveMemoryController.AllowSpeculation || !SpeculateZoomSteps || !ExactRaster || PresentTimeoutMilliseconds <= 0 || !_hasZoomAnchor || _renderingSuspended || _pages.Count == 0 || !IsVisible) return;
        double sinceZoom = Stopwatch.GetElapsedTime(_lastWheelZoomTimestamp).TotalMilliseconds;
        if (sinceZoom > SpeculationWindowMilliseconds || sinceZoom < 60) return; // chưa lăn gần đây, hoặc còn đang lăn
        if (Math.Abs(_vp.Zoom - _speculatedFor) < 1e-9) return;
        _speculatedFor = _vp.Zoom;
        SpeculateAdjacentZooms();
    }

    private void SpeculateAdjacentZooms()
    {
        _speculationCts?.Cancel();
        var cts = _speculationCts = new CancellationTokenSource();
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (int direction in new[] { 1, -1 })
        {
            double zoom = Math.Clamp(_vp.Zoom * Math.Pow(ZoomStep, direction), MinZoom, MaxZoom);
            if (Math.Abs(zoom - _vp.Zoom) < 1e-6) continue;
            var probe = _vp.Clone();
            probe.ZoomAt(zoom, _lastZoomAnchor.X, _lastZoomAnchor.Y);
            var (first, last) = probe.VisibleRange();
            for (int s = Math.Max(0, first); first >= 0 && s <= last && s < _slots.Length; s++)
            {
                var row = _pages[_slots[s]];
                double needed = row.LayoutWidth * probe.Zoom * dpi;
                if (needed <= PreviewSufficientPx) continue;
                if (needed > RegionStartPx(dpi)) SpeculateRegion(probe, s, row, needed, cts.Token);
                else if (PageRenderer is { } renderer)
                    _ = ObserveAsync(renderer(row, ExactPageWidth(needed), PdfRenderPriority.Background, cts.Token));
            }
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); } catch { /* vẽ trước chỉ là cố gắng */ }
    }

    private void SpeculateRegion(ContinuousViewport probe, int slot, PageRow row, double neededPx, CancellationToken token)
    {
        // Bộ vẽ tuỳ chỉnh (kiểm thử) không có khái niệm ưu tiên nền: không vẽ trước.
        if (RegionRenderer != null || !probe.VisibleFraction(slot, out double dx0, out double dy0, out double dx1, out double dy1)) return;
        var (ua, va) = Unrotate(dx0, dy0, RotationOf(row));
        var (ub, vb) = Unrotate(dx1, dy1, RotationOf(row));
        double fx0 = Math.Min(ua, ub), fx1 = Math.Max(ua, ub), fy0 = Math.Min(va, vb), fy1 = Math.Max(va, vb);
        int fullWidth = (int)Math.Min(MaxRegionFullWidth, Math.Round(neededPx));
        int fullHeight = Math.Max(1, (int)Math.Round(fullWidth * (row.LayoutHeight / Math.Max(1, row.LayoutWidth))));
        string layers = PdfLayerStateStore.GetToken(row.SourcePath);
        var (x, y, w, h) = ContinuousViewport.Region(fx0, fy0, fx1, fy1, fullWidth, fullHeight);
        var key = new RegionKey(fullWidth, fullHeight, x, y, w, h, layers, 0);
        var source = RenderCacheKeys.Thumbnail(row.SourcePath, row.PageNumber);
        long generation = Interlocked.Read(ref _regionCacheGeneration);
        lock (RegionCacheLock)
        {
            if (RegionCache.TryFind(k => k.Path == source.Path && k.Page == source.Page && k.Region.FullWidth == fullWidth &&
                string.Equals(k.Region.Layers, layers, StringComparison.Ordinal) &&
                k.Region.X <= x && k.Region.Y <= y && k.Region.X + k.Region.Width >= x + w && k.Region.Y + k.Region.Height >= y + h, out _, out _)) return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var images = await PdfThumbnailService.RenderPageTilesBatchAsync(row.SourcePath, row.PageNumber - 1, fullWidth, fullHeight,
                    new[] { new Int32Rect(x, y, w, h) }, token, layers, false, PdfRenderPriority.Background).ConfigureAwait(false);
                if (token.IsCancellationRequested || images.Count != 1 || images[0] is not { } bitmap) return;
                lock (RegionCacheLock)
                    if (generation == Interlocked.Read(ref _regionCacheGeneration))
                        RegionCache.Set(new CachedRegionKey(source.Path, source.Page, key), bitmap);
            }
            catch { /* vẽ trước chỉ là cố gắng */ }
        });
    }

    private void UpdateRequestsCore(ContinuousViewport vp)
    {
        if (_renderingSuspended || _pages.Count == 0 || _vp.ViewportHeight <= 0 || !IsVisible) return;
        long now = Stopwatch.GetTimestamp();
        double sinceScroll = (now - _lastScrollTimestamp) * 1000.0 / Stopwatch.Frequency;
        bool scrollSettling = _fastScroll && sinceScroll < SettleMilliseconds;
        if (_fastScroll && !scrollSettling)
        {
            _fastScroll = false;
            ScrollSettled?.Invoke();
        }
        _updateTimer.Interval = TimeSpan.FromMilliseconds(16);
        double sinceZoom = (now - _lastZoomTimestamp) * 1000.0 / Stopwatch.Frequency;
        // Chinh sach dung co: xin anh dung co ngay tu nac zoom dau (khong cho lang) vi khung dang hien duoc giu toi khi anh ve.
        bool zoomSettling = sinceZoom < ZoomSettleMilliseconds,
            deferForZoom = zoomSettling && (ProgressiveZoomPresentation || !ExactRaster);

        var (firstSlot, lastSlot) = vp.VisibleRange();
        if (firstSlot < 0 || lastSlot >= _slots.Length) return;
        int first = _slots[firstSlot], last = _slots[lastSlot];
        PublishMemoryProtection();
        int prefetchPages = Math.Clamp(PrefetchPageCount, 0, _memoryPressure == MemoryPressureState.Normal ? ReaderPerformanceProfile.Current.PrefetchPages : _memoryPressure == MemoryPressureState.Critical ? 0 : 1);
        int previewAhead = _memoryPressure == MemoryPressureState.Normal ? PreviewAheadPages : _memoryPressure == MemoryPressureState.Pressure ? 2 : 0;
        int keepPages = 1;
        int keepAhead = Math.Max(keepPages, previewAhead);
        int keepFirst = Math.Max(0, first - (_scrollDirection < 0 ? keepAhead : keepPages)),
            keepLast = Math.Min(_pages.Count - 1, last + (_scrollDirection >= 0 ? keepAhead : keepPages));
        int renderFirst = first - (!scrollSettling && _scrollDirection < 0 ? prefetchPages : 0);
        int renderLast = last + (!scrollSettling && _scrollDirection >= 0 ? prefetchPages : 0);

        // Trang đã rời khu vực quanh khung nhìn: huỷ việc vẽ, trả ảnh (ảnh trang còn trong cache chung của ReaderWindow).
        foreach (var (row, state) in _states.ToArray())
        {
            if (_indexOf.TryGetValue(row, out int index) && index >= keepFirst && index <= keepLast)
            {
                if (index < renderFirst || index > renderLast) state.CancelAll();
                continue;
            }
            state.CancelAll();
            AdaptiveMemoryController.NoteReleasedBytes(state.Bitmap == null ? 0 : BitmapMemoryCache<int>.SizeOf(state.Bitmap));
            _states.Remove(row);
        }

        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var hot = new List<(string, int)>();
        int hotFirst = KeepPrefetchedNativePages ? Math.Min(first - 1, renderFirst) : first - 1;
        int hotLast = KeepPrefetchedNativePages ? Math.Max(last + 1, renderLast) : last + 1;
        for (int i = Math.Max(0, hotFirst); i <= Math.Min(_pages.Count - 1, hotLast); i++)
            hot.Add((_pages[i].SourcePath, _pages[i].PageNumber));
        var focusRow = _currentPage >= 0 && _currentPage < _pages.Count ? _pages[_currentPage] : _pages[first];
        PdfThumbnailService.SetHotPages(hot, (focusRow.SourcePath, focusRow.PageNumber));

        // Trang đang hiện: xin thẳng ảnh đúng độ phân giải, rồi vùng nét khi zoom sâu.
        for (int i = first; i <= last; i++)
        {
            var row = _pages[i];
            var state = StateOf(row);
            var best = BestBitmap(row, state);
            bool hasImage = best != null;

            double needed = row.LayoutWidth * vp.Zoom * dpi;
            double regionStart = RegionStartPx(dpi);
            int pageWidth = ExactRaster
                ? (needed <= regionStart ? ExactPageWidth(needed) : ReadablePageBitmapWidth)
                : PreferredPageBitmapWidth(needed, PreferViewportRegions);
            // Zoom nhỏ (nhiều trang trên màn hình): ảnh nhỏ đã đủ nét, không vẽ ảnh trang cho từng trang.
            if (needed <= PreviewSufficientPx)
            {
                if (state.Preview == null && row.Thumbnail == null) RequestPreview(row, state, PdfRenderPriority.Visible);
            }
            // Đang zoom: trang đã có ảnh thì co giãn ảnh đó, chưa xin độ phân giải mới (mỗi nấc zoom một lượt vẽ là lãng phí).
            else if (needed <= regionStart && !(deferForZoom && hasImage) && NeedsPageBitmap(row, state, pageWidth, needed))
            {
                // Chưa có ảnh nào: xin trước ảnh xem trước nhỏ (rẻ, về nhanh) để có gì đó hiện ngay trong lúc ảnh nét đang vẽ.
                if (!hasImage && state.Preview == null && row.Thumbnail == null) RequestPreview(row, state, PdfRenderPriority.Visible);
                // Đã có ảnh (sắp thiếu): vẽ dư nhiều hơn để vài nấc zoom kế tiếp không phải vẽ lại.
                if (!ExactRaster && state.Bitmap != null && pageWidth < MaxPageBitmapWidth && needed <= MaxPageBitmapWidth * 1.03)
                    pageWidth = PageBitmapWidth(needed, PageRefreshHeadroom);
                RequestPage(row, state, pageWidth, PdfRenderPriority.Visible);
            }
            else if (needed > regionStart && !hasImage && state.Preview == null && row.Thumbnail == null)
            {
                // At deep zoom the center tile is the fast readable fallback. A
                // concurrent 1024px whole-page render only competes with it for
                // PDFium/CPU and is the main cause of CAD interaction stalls.
                RequestPreview(row, state, PdfRenderPriority.Visible);
            }

            if (needed > regionStart)
            {
                bool predictive = ExperimentalMuPdfViewport.CanRender(row.SourcePath, row.PageNumber - 1,
                    PdfLayerStateStore.GetToken(row.SourcePath));
                RequestRegion(vp, SlotOf(i), row, state, needed, renderMissing: predictive || !deferForZoom);
            }
            else if (state.Regions.Count > 0 || state.RegionCts != null)
            {
                state.CancelRegion();
                state.CancelWide();
                state.Regions.Clear();
                _surface.InvalidateVisual();
            }
        }

        // Vòng ảnh xem trước phía trước theo hướng cuộn (rẻ: ~340 px): khi cuộn nhanh, trang vừa lăn tới đã có sẵn ảnh thô để hiện ngay.
        {
            int ahead = _scrollDirection >= 0 ? 1 : -1, aheadEdge = ahead > 0 ? last : first;
            for (int step = 1; step <= previewAhead; step++)
            {
                int i = aheadEdge + ahead * step;
                if (i < 0 || i >= _pages.Count) break;
                var aheadRow = _pages[i];
                var aheadState = StateOf(aheadRow);
                if (BestBitmap(aheadRow, aheadState) == null) RequestPreview(aheadRow, aheadState, PdfRenderPriority.Thumbnail);
            }
        }

        if (zoomSettling)
        {
            // Hết đợt zoom thì xin lại (vùng nét / ảnh trang đúng độ phân giải).
            _updateTimer.Interval = TimeSpan.FromMilliseconds(ZoomSettleMilliseconds - sinceZoom + 5);
            _updateTimer.Start();
            return;
        }
        if (scrollSettling)
        {
            _updateTimer.Start();
            return;
        }

        // Tải trước bốn trang theo hướng cuộn, cùng độ phân giải với trang đang đọc.
        int dir = _scrollDirection >= 0 ? 1 : -1;
        int edge = dir > 0 ? last : first;
        for (int step = 1; step <= prefetchPages; step++)
        {
            int i = edge + dir * step;
            if (i < 0 || i >= _pages.Count) break;
            var row = _pages[i];
            var state = StateOf(row);
            double needed = row.LayoutWidth * vp.Zoom * dpi;
            if (needed > PreviewSufficientPx)
            {
                if (needed > RegionStartPx(dpi))
                {
                    if (BestBitmap(row, state) == null) RequestPreview(row, state, PdfRenderPriority.Thumbnail);
                    continue;
                }
                int pageWidth = ExactRaster
                    ? (needed <= RegionStartPx(dpi) ? ExactPageWidth(needed) : ReadablePageBitmapWidth)
                    : PreferredPageBitmapWidth(needed, PreferViewportRegions);
                if (NeedsPageBitmap(row, state, pageWidth, needed)) RequestPage(row, state, pageWidth, PdfRenderPriority.Background);
            }
            else if (BestBitmap(row, state) == null) RequestPreview(row, state, PdfRenderPriority.Thumbnail);
        }
        MaybeWarmPreviews();
    }

    /// <summary>Rảnh (không cuộn/zoom/pan một lúc): vẽ sẵn ảnh xem trước (340 px) của cả tài liệu, gần trang đang đọc trước, ở ưu tiên thấp nhất
    /// vào bộ nhớ đệm chung — nhảy tới trang xa hoặc zoom ra xem nhiều trang thì có hình ngay thay vì chờ từng trang lạnh được đọc và vẽ
    /// (đo 04/10: nhảy tới trang cuối sau zoom sâu mất 1,5 s, chủ yếu là ~10 trang lạnh vẽ ảnh xem trước). Dừng ngay khi người dùng thao tác.
    /// Đổi lại tốn CPU nền một lúc sau khi mở file và ~0,3 MB RAM mỗi trang. XTPDF_WARM_PREVIEWS=0 tắt.</summary>
    internal static bool WarmAllPreviews { get; set; } = Environment.GetEnvironmentVariable("XTPDF_WARM_PREVIEWS") != "0";
    private const double WarmIdleMilliseconds = 800;
    private const int WarmConcurrency = 2, WarmMaxPages = 600;
    private readonly HashSet<PageRow> _warmed = new(ReferenceEqualityComparer.Instance);
    private CancellationTokenSource? _warmCts;
    private int _warmInflight;

    private void MaybeWarmPreviews()
    {
        if (!AdaptiveMemoryController.AllowSpeculation || !WarmAllPreviews || _pages.Count == 0 || _renderingSuspended || !IsVisible || _pv != null) return;
        long now = Stopwatch.GetTimestamp();
        double idle = Math.Min(Stopwatch.GetElapsedTime(_lastScrollTimestamp, now).TotalMilliseconds,
            Math.Min(Stopwatch.GetElapsedTime(_lastZoomTimestamp, now).TotalMilliseconds, Stopwatch.GetElapsedTime(_lastPanTimestamp, now).TotalMilliseconds));
        bool remaining = _warmed.Count < Math.Min(_pages.Count, WarmMaxPages);
        if (idle < WarmIdleMilliseconds)
        {
            _warmCts?.Cancel();
            _warmCts = null;
            if (remaining) { _updateTimer.Interval = TimeSpan.FromMilliseconds(WarmIdleMilliseconds - idle + 20); _updateTimer.Start(); }
            return;
        }
        if (!remaining || _warmInflight >= WarmConcurrency) return;
        int center = _currentPage >= 0 && _currentPage < _pages.Count ? _currentPage : 0;
        var cts = _warmCts ??= new CancellationTokenSource();
        for (int k = 0; k < _pages.Count && _warmInflight < WarmConcurrency; k++)
        {
            foreach (int i in new[] { center + k, center - k })
            {
                if (i < 0 || i >= _pages.Count || (k == 0 && i != center) || _warmInflight >= WarmConcurrency) continue;
                var row = _pages[i];
                if (!_warmed.Add(row)) continue;
                _warmInflight++;
                _ = WarmOneAsync(row, cts);
            }
        }
    }

    private async Task WarmOneAsync(PageRow row, CancellationTokenSource cts)
    {
        BitmapSource? bmp = null;
        try { bmp = await ThumbnailCache.LoadPreviewAsync(row, cts.Token, PdfRenderPriority.Thumbnail); }
        catch { }
        _warmInflight--;
        if (bmp == null || cts.IsCancellationRequested) _warmed.Remove(row); // dừng giữa chừng: lần rảnh sau thử lại
        ScheduleUpdate();
    }

    /// <summary>Vẽ dư độ phân giải so với cỡ đang cần: vài nấc zoom kế tiếp chỉ phóng NHỎ xuống ảnh đã nét, nên không có khoảng mờ
    /// rồi "nhảy" nét giữa hai mức zoom (đo 02/10 trên trang bản vẽ: mỗi ~3 nấc có 1 lần mờ ~180 ms, 27.000–49.000 điểm ảnh đổi).</summary>
    internal const double ZoomHeadroom = 1.25;

    private static int PageBitmapWidth(double neededPx, double headroom = ZoomHeadroom)
        => (int)Math.Clamp(Math.Ceiling(neededPx * headroom / 256) * 256, MinPageBitmapWidth, MaxPageBitmapWidth);

    internal static int PreferredPageBitmapWidth(double neededPx, bool viewportFirst)
        // A readable fallback is sufficient while the high-zoom viewport region refines.
        => viewportFirst && neededPx > MaxPageBitmapWidth * 1.03
            ? ReadablePageBitmapWidth : PageBitmapWidth(neededPx);

    /// <summary>Ảnh trang hiện có còn đủ dùng khi nó không nhỏ hơn cỡ cần × hệ số này: chưa thay ảnh, nên không có cú "nhảy" do thay ảnh
    /// (ảnh vẽ mới có nét đậm/nhạt khác ảnh cũ bị thu nhỏ). Chỉ vẽ lại khi sắp thiếu, và vẽ dư <see cref="PageRefreshHeadroom"/>.</summary>
    private const double PageLowWater = 1.10;
    private const double PageRefreshHeadroom = 2.0;
    private const int PageRetryInitialMilliseconds = 160;
    private const int PageRetryMaximumMilliseconds = 2_000;

    private bool NeedsPageBitmap(PageRow row, PageState state, int width, double neededPx)
    {
        if (state.Bitmap is { } bmp && state.BitmapVersion == state.Version &&
            string.Equals(state.BitmapLayers, PdfLayerStateStore.GetToken(row.SourcePath), StringComparison.Ordinal))
        {
            // Dung co: anh phai khop co hien thi (ca khi qua to - anh to thu nho lam net manh/nhat di). Che do vung net giu anh du phong 1024 px.
            if (ExactRaster && neededPx <= RegionStartPx(VisualTreeHelper.GetDpi(this).DpiScaleX))
            {
                double reference = ExactPageWidth(neededPx);
                bool mismatch = bmp.PixelWidth < reference * ExactLow || bmp.PixelWidth > reference * ExactHigh;
                return mismatch && state.DeliveredWidth != width;
            }
            if (bmp.PixelWidth >= width * 0.97) return false;
            // Chế độ vùng nét (width = ảnh dự phòng 1024 px) đã xử lý ở trên; ở đây chỉ còn cỡ theo zoom.
            return bmp.PixelWidth < Math.Min(neededPx * PageLowWater, MaxPageBitmapWidth);
        }
        if (state.FailedWidth != width || state.FailedVersion != state.Version) return true;
        // A transient document-open/raster failure used to poison this exact width
        // forever, leaving heavy pages on their thumbnail until another interaction.
        return Stopwatch.GetTimestamp() >= state.RetryAfterTimestamp;
    }

    private PageState StateOf(PageRow row)
    {
        if (!_states.TryGetValue(row, out var state))
        {
            _states[row] = state = new PageState();
            var bitmap = ReuseRenderedImages ? row.ReaderBitmap : null;
            var cached = ReuseRenderedImages ? CachedPageProvider?.Invoke(row, MinPageBitmapWidth) : null;
            if (cached != null && (bitmap == null || cached.PixelWidth > bitmap.PixelWidth)) bitmap = cached;
            if (bitmap != null)
            {
                state.Bitmap = bitmap;
                state.BitmapLayers = PdfLayerStateStore.GetToken(row.SourcePath);
                state.BitmapVersion = state.Version;
                _surface.InvalidateVisual();
            }
        }
        return state;
    }

    private bool IsLive(PageRow row, PageState state, int version)
        => _states.TryGetValue(row, out var current) && ReferenceEquals(current, state) && state.Version == version;

    private void RequestPreview(PageRow row, PageState state, PdfRenderPriority priority)
    {
        if (state.PreviewCts != null || state.Preview != null) return;
        var cts = new CancellationTokenSource();
        state.PreviewCts = cts;
        _ = LoadPreviewAsync(row, state, cts, priority);
    }

    private async Task LoadPreviewAsync(PageRow row, PageState state, CancellationTokenSource cts, PdfRenderPriority priority)
    {
        BitmapSource? bmp = null;
        try { bmp = await ThumbnailCache.LoadPreviewAsync(row, cts.Token, priority); }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(state.PreviewCts, cts)) state.PreviewCts = null;
            cts.Dispose();
        }
        if (bmp == null || !_states.TryGetValue(row, out var current) || !ReferenceEquals(current, state)) return;
        state.Preview = bmp; // giữ chắc (Thumbnail của trang chỉ là tham chiếu yếu)
        if (IsOnScreen(row)) _surface.InvalidateVisual();
    }

    private void RequestPage(PageRow row, PageState state, int width, PdfRenderPriority priority)
    {
        if (PageRenderer is not { } renderer) return;
        if (state.PageCts != null && state.RequestedWidth == width && state.RequestedVersion == state.Version)
        {
            // Đang vẽ đúng ảnh này ở ưu tiên nền mà giờ trang đã hiện: xin lại ở ưu tiên Visible (cache ReaderWindow gộp chung).
            if (priority != PdfRenderPriority.Visible || state.RequestedPriority == PdfRenderPriority.Visible) return;
        }
        state.PageCts?.Cancel();
        var cts = new CancellationTokenSource();
        state.PageCts = cts;
        state.RequestedWidth = width;
        state.RequestedVersion = state.Version;
        state.RequestedPriority = priority;
        _ = LoadPageAsync(renderer, row, state, width, priority, state.Version, cts);
    }

    private async Task LoadPageAsync(Func<PageRow, int, PdfRenderPriority, CancellationToken, Task<BitmapSource?>> renderer,
        PageRow row, PageState state, int width, PdfRenderPriority priority, int version, CancellationTokenSource cts)
    {
        string layers = PdfLayerStateStore.GetToken(row.SourcePath);
        BitmapSource? bmp = null;
        bool renderCancelled = false;
        try { bmp = await renderer(row, width, priority, cts.Token).WaitAsync(cts.Token); }
        catch (OperationCanceledException) { renderCancelled = true; }
        catch (Exception ex) { Debug.WriteLine($"[ContinuousPdfView] page render failed: {ex.Message}"); }
        bool cancelled = cts.IsCancellationRequested;
        if (ReferenceEquals(state.PageCts, cts)) state.PageCts = null;
        cts.Dispose();
        if (cancelled || !IsLive(row, state, version)) return;
        if (renderCancelled)
        {
            // Another placement can cancel a shared render. This is not a failed PDF page.
            ScheduleUpdate();
            return;
        }
        if (bmp == null)
        {
            state.FailedWidth = width;
            state.FailedVersion = version;
            state.FailedAttempts++;
            int retryMs = Math.Min(PageRetryMaximumMilliseconds,
                PageRetryInitialMilliseconds * (1 << Math.Min(4, state.FailedAttempts - 1)));
            state.RetryAfterTimestamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency * retryMs / 1000;
            if (DiagnosticsLog.Enabled) DiagnosticsLog.Event($"RENDER retry {state.FailedAttempts} in {retryMs} ms, page {row.PageNumber}");
            _updateTimer.Interval = TimeSpan.FromMilliseconds(retryMs);
            _updateTimer.Start();
            return;
        }
        if (!string.Equals(layers, PdfLayerStateStore.GetToken(row.SourcePath), StringComparison.Ordinal)) { ScheduleUpdate(); return; }
        state.Bitmap = bmp;
        state.BitmapLayers = layers;
        state.BitmapVersion = version;
        state.DeliveredWidth = width;
        state.FailedAttempts = 0;
        state.RetryAfterTimestamp = 0;
        if (row.ReaderBitmap is not { } existing || existing.PixelWidth <= bmp.PixelWidth) row.ReaderBitmap = bmp;
        if (IsOnScreen(row)) _surface.InvalidateVisual();
        CheckPresent(); // anh dung co vua ve: hien luon khung moi neu da du
        ScheduleUpdate(); // hoàn tất trang này: tiếp tục tải trước mà không cần chờ một lần cuộn/zoom khác
    }

    private bool RegionCovered(ContinuousViewport vp, int slot, PageRow row, PageState state, double neededPx) => RequestRegion(vp, slot, row, state, neededPx, renderMissing: false);

    /// <returns>true neu phan dang nhin cua trang da co vung net phu (san de hien).</returns>
    private bool RequestRegion(ContinuousViewport vp, int slot, PageRow row, PageState state, double neededPx, bool renderMissing = true)
    {
        if (!vp.VisibleFraction(slot, out double dx0, out double dy0, out double dx1, out double dy1)) return true;
        // Phần đang hiện → phân số trên trang theo hướng của trang (vùng vẽ PDFium không xoay).
        var (ua, va) = Unrotate(dx0, dy0, RotationOf(row));
        var (ub, vb) = Unrotate(dx1, dy1, RotationOf(row));
        double fx0 = Math.Min(ua, ub), fx1 = Math.Max(ua, ub), fy0 = Math.Min(va, vb), fy1 = Math.Max(va, vb);
        int fullWidth = ExactRaster
            ? (int)Math.Min(MaxRegionFullWidth, Math.Round(neededPx))
            : (int)Math.Min(MaxRegionFullWidth,
                Math.Ceiling(Math.Min(neededPx * RegionHeadroom, MaxRegionFullWidth) / RegionResolutionQuantum) * RegionResolutionQuantum);
        double aspect = row.LayoutHeight / Math.Max(1, row.LayoutWidth);
        int fullHeight = Math.Max(1, (int)Math.Round(fullWidth * aspect));
        string layers = PdfLayerStateStore.GetToken(row.SourcePath);
        bool predictive = ExperimentalMuPdfViewport.CanRender(row.SourcePath, row.PageNumber - 1, layers);
        if (predictive && renderMissing)
        {
            double visiblePixels = Math.Max(1, (fx1 - fx0) * fullWidth * (fy1 - fy0) * fullHeight);
            double lead = ZoomRenderPrediction.Headroom(vp.Zoom, ZoomBase, ZoomRateLimit,
                state.RegionRenderMilliseconds, visiblePixels);
            fullWidth = (int)Math.Min(MaxRegionFullWidth, Math.Ceiling(fullWidth * lead));
            fullHeight = Math.Max(1, (int)Math.Round(fullWidth * aspect));
        }

        // Vùng cũ còn dùng được nếu nó không bị phóng to (>= cỡ cần × RegionLowWater) và không dư quá nhiều, và phủ phần đang nhìn
        // (so theo phân số trang, vì vùng vẽ ở độ phân giải khác vẫn được vẽ lên đúng chỗ): zoom nhích không thay ảnh → không nhảy nét.
        double minWidth = Math.Min(neededPx * RegionLowWater, MaxRegionFullWidth), maxWidth = Math.Max(neededPx * RegionHighWater, minWidth);
        if (ExactRaster)
        {
            double reference = Math.Min(neededPx, MaxRegionFullWidth);
            minWidth = reference * ExactLow / 0.995; maxWidth = reference * ExactHigh; // dung co: chi nhan vung khop co hien thi
            if (predictive) maxWidth = reference * 2.5;
        }
        bool Covers(RegionKey k) => k.Version == state.Version && string.Equals(k.Layers, layers, StringComparison.Ordinal) &&
            k.FullWidth >= minWidth * 0.995 && k.FullWidth <= maxWidth &&
            k.X <= fx0 * k.FullWidth + 0.5 && k.Y <= fy0 * k.FullHeight + 0.5 &&
            k.X + k.Width >= fx1 * k.FullWidth - 0.5 && k.Y + k.Height >= fy1 * k.FullHeight - 0.5;

        // Đang pan: phần sắp tới (thêm một phần tư bề rộng/cao nhìn thấy về phía đang đi) cũng nên có nét sẵn; nếu chưa thì vẽ vùng kế tiếp
        // ngầm ngay bây giờ — không chờ tới khi phần nhìn thấy chạm mép rồi mới vẽ (lúc đó thấy ảnh nét dần).
        bool panning = renderMissing && Stopwatch.GetElapsedTime(_lastPanTimestamp).TotalMilliseconds < PanRecentMilliseconds && (_panSignX != 0 || _panSignY != 0);
        int panU = 0, panV = 0;
        double ax0 = fx0, ay0 = fy0, ax1 = fx1, ay1 = fy1;
        if (panning)
        {
            var (pu0, pv0) = Unrotate(0.5, 0.5, RotationOf(row));
            var (pu1, pv1) = Unrotate(0.5 + 0.1 * _panSignX, 0.5 + 0.1 * _panSignY, RotationOf(row));
            panU = Math.Sign(Math.Round(pu1 - pu0, 6)); panV = Math.Sign(Math.Round(pv1 - pv0, 6));
            double ex = (fx1 - fx0) * 0.25, ey = (fy1 - fy0) * 0.25;
            if (panU > 0) ax1 = Math.Min(1, fx1 + ex); else if (panU < 0) ax0 = Math.Max(0, fx0 - ex);
            if (panV > 0) ay1 = Math.Min(1, fy1 + ey); else if (panV < 0) ay0 = Math.Max(0, fy0 - ey);
        }
        bool Comfortable(RegionKey k) => Covers(k) &&
            k.X <= ax0 * k.FullWidth + 0.5 && k.Y <= ay0 * k.FullHeight + 0.5 &&
            k.X + k.Width >= ax1 * k.FullWidth - 0.5 && k.Y + k.Height >= ay1 * k.FullHeight - 0.5;

        var source = RenderCacheKeys.Thumbnail(row.SourcePath, row.PageNumber);
        // Vùng rộng chạy riêng, song song, ở mức nền: không chờ vùng thật đang vẽ, nên pan liên tục (kể cả đổi chiều) vẫn có nét sẵn.
        if (renderMissing && ExactRaster && WideRegions && AdaptiveMemoryController.AllowSpeculation) MaybeRequestWide();
        bool covered = state.Regions.Any(r => Covers(r.Key));
        if (covered && (!panning || state.Regions.Any(r => Comfortable(r.Key)) ||
            (state.RegionPending is { } ahead && Comfortable(ahead)))) return true;
        CachedRegionKey cachedKey;
        BitmapSource? cachedBitmap;
        cachedKey = default;
        cachedBitmap = null;
        if (ReuseRenderedImages)
            lock (RegionCacheLock)
                RegionCache.TryFind(k => k.Path == source.Path && k.Page == source.Page && Covers(k.Region with { Version = state.Version }),
                    out cachedKey, out cachedBitmap);
        if (cachedBitmap != null && (!panning || Comfortable(cachedKey.Region with { Version = state.Version })))
        {
            state.CancelRegion();
            state.Regions.Add(new RegionImage(cachedKey.Region with { Version = state.Version }, cachedBitmap));
            while (state.Regions.Count > 2) state.Regions.RemoveAt(0);
            _surface.InvalidateVisual();
            return true;
        }
        if (!renderMissing) return covered;
        if (state.RegionCts != null && state.RegionPending is { } pending)
        {
            if (Covers(pending)) return covered;
            // Đang pan: vùng đang vẽ dở vẫn còn trên màn hình thì vẽ nốt (vẽ xong sẽ tự xin vùng kế) — huỷ liên tục thì
            // pan đều tay không vùng nào kịp xong.
            bool sameResolution = pending.FullWidth == fullWidth && pending.Version == state.Version &&
                string.Equals(pending.Layers, layers, StringComparison.Ordinal);
            bool stillOnScreen = pending.X < fx1 * fullWidth && pending.X + pending.Width > fx0 * fullWidth &&
                pending.Y < fy1 * fullHeight && pending.Y + pending.Height > fy0 * fullHeight;
            if (sameResolution && stillOnScreen) return covered;
            if (predictive && _zoomTarget is { } targetZoom && targetZoom > vp.Zoom &&
                pending.Version == state.Version && string.Equals(pending.Layers, layers, StringComparison.Ordinal) &&
                (double)pending.X / pending.FullWidth < fx1 &&
                (double)(pending.X + pending.Width) / pending.FullWidth > fx0 &&
                (double)pending.Y / pending.FullHeight < fy1 &&
                (double)(pending.Y + pending.Height) / pending.FullHeight > fy0)
                return covered;
        }

        var (x, y, w, h) = ContinuousViewport.Region(fx0, fy0, fx1, fy1, fullWidth, fullHeight, panX: panU, panY: panV);
        var key = new RegionKey(fullWidth, fullHeight, x, y, w, h, layers, state.Version);
        var (pieces, missing, reuseOverlap, rectangles) = PlanRegion(new Int32Rect(x, y, w, h));
        state.CancelRegion();
        var cts = new CancellationTokenSource();
        state.RegionCts = cts;
        state.RegionPending = key;
        _ = LoadRegionAsync(row, state, key, cts, rectangles, missing, pieces, reuseOverlap, wide: false);
        return covered;

        // Những phần của vùng cần vẽ mà ảnh đã có (vùng đang giữ, bộ nhớ đệm) chưa phủ: chỉ vẽ phần thiếu.
        (List<ViewportRegionReuse.Piece> Pieces, Int32Rect[] Missing, bool ReuseOverlap, Int32Rect[] Rectangles) PlanRegion(Int32Rect target)
        {
            var found = new List<ViewportRegionReuse.Piece>();
            if (ReuseRenderedImages && ReuseRegionOverlap)
            {
                foreach (var region in state.Regions)
                    if (Compatible(region.Key)) found.Add(ViewportRegionReuse.CachedPiece(ToRect(region.Key), region.Bitmap, fullWidth, fullHeight, target));
                CachedRegionKey ck; BitmapSource? cb;
                lock (RegionCacheLock)
                    if (RegionCache.TryFind(k => k.Path == source.Path && k.Page == source.Page &&
                        Compatible(k.Region with { Version = state.Version }) &&
                        !ViewportRegionReuse.Intersect(target, ToRect(k.Region)).IsEmpty, out ck, out cb) &&
                        found.All(p => !ReferenceEquals(p.Bitmap, cb)))
                        found.Add(ViewportRegionReuse.CachedPiece(ToRect(ck.Region), cb!, fullWidth, fullHeight, target));
            }
            var gaps = ViewportRegionReuse.Plan(target, found);
            bool overlap = gaps.Length == 0 || gaps.Length != 1 || gaps[0] != target;
            if (!overlap) found.Clear();
            var rects = overlap ? gaps.Select(r => ViewportRegionReuse.WithGutter(r, fullWidth, fullHeight)).ToArray() : gaps;
            return (found, gaps, overlap, rects);
        }

        void MaybeRequestWide()
        {
            var (cx, cy, cw, ch) = ContinuousViewport.Region(fx0, fy0, fx1, fy1, fullWidth, fullHeight, WideCheckMargin, maxPixels: WideRegionPixels);
            bool Holds(RegionKey k) => Compatible(k) && k.X <= cx && k.Y <= cy && k.X + k.Width >= cx + cw && k.Y + k.Height >= cy + ch;
            if (state.Regions.Any(r => Holds(r.Key)) || (state.WidePending is { } inFlight && Holds(inFlight))) return;
            var (wx, wy, ww, wh) = ContinuousViewport.Region(fx0, fy0, fx1, fy1, fullWidth, fullHeight, WideMargin, maxPixels: WideRegionPixels, panX: panU, panY: panV);
            var wideKey = new RegionKey(fullWidth, fullHeight, wx, wy, ww, wh, layers, state.Version);
            var (widePieces, wideMissing, wideOverlap, wideRects) = PlanRegion(new Int32Rect(wx, wy, ww, wh));
            state.CancelWide();
            var wideCts = new CancellationTokenSource();
            state.WideCts = wideCts;
            state.WidePending = wideKey;
            _ = LoadRegionAsync(row, state, wideKey, wideCts, wideRects, wideMissing, widePieces, wideOverlap, wide: true);
        }

        bool Compatible(RegionKey k) => k.FullWidth == fullWidth && k.FullHeight == fullHeight &&
            k.Version == state.Version && string.Equals(k.Layers, layers, StringComparison.Ordinal);
    }

    private static Int32Rect ToRect(RegionKey key) => new(key.X, key.Y, key.Width, key.Height);

    // A complete deep-zoom crop can take hundreds of milliseconds on a CAD page.
    // Keep its coarse page image visible and refine it in center-first chunks instead.
    private const int ProgressiveTileSize = 640;

    private static Int32Rect[] SplitProgressiveTiles(Int32Rect target, int fullWidth, int fullHeight)
    {
        var logical = new List<Int32Rect>();
        int right = target.X + target.Width, bottom = target.Y + target.Height;
        for (int y = target.Y; y < bottom; y += ProgressiveTileSize)
            for (int x = target.X; x < right; x += ProgressiveTileSize)
                logical.Add(new Int32Rect(x, y, Math.Min(ProgressiveTileSize, right - x),
                    Math.Min(ProgressiveTileSize, bottom - y)));

        double cx = target.X + target.Width / 2d, cy = target.Y + target.Height / 2d;
        return logical.OrderBy(rect =>
        {
            double dx = rect.X + rect.Width / 2d - cx, dy = rect.Y + rect.Height / 2d - cy;
            return dx * dx + dy * dy;
        }).Select(rect => ViewportRegionReuse.WithGutter(rect, fullWidth, fullHeight)).ToArray();
    }

    private async Task LoadRegionAsync(PageRow row, PageState state, RegionKey key, CancellationTokenSource cts,
        Int32Rect[] rectangles, Int32Rect[] missing, List<ViewportRegionReuse.Piece> pieces, bool reuseOverlap,
        bool wide = false)
    {
        var priority = wide ? PdfRenderPriority.Background : PdfRenderPriority.Visible;
        long cacheGeneration = Interlocked.Read(ref _regionCacheGeneration);
        BitmapSource? bmp = null;
        var renderWatch = Stopwatch.StartNew();
        // A single crop avoids repeated CAD traversal without duplicating parsed
        // pages. Parallel tiles are an opt-in throughput/memory experiment.
        bool experimentalMuPdf = RegionRenderer == null &&
            ExperimentalMuPdfViewport.CanRender(row.SourcePath, row.PageNumber - 1, key.Layers);
        bool streamTiles = !ExperimentalMuPdfViewport.BalancedMode && !experimentalMuPdf && PdfThumbnailService.ViewportRenderWorkers > 1 && PdfiumPool.Count > 1 &&
            !wide && RegionRenderer == null && !reuseOverlap && rectangles.Length == 1 &&
            (long)rectangles[0].Width * rectangles[0].Height > (long)ProgressiveTileSize * ProgressiveTileSize;
        try
        {
            if (streamTiles)
            {
                var tiles = SplitProgressiveTiles(rectangles[0], key.FullWidth, key.FullHeight);
                state.PresentationEpoch++;
                var images = await PdfThumbnailService.RenderPageTilesStreamingAsync(row.SourcePath, row.PageNumber - 1, key.FullWidth, key.FullHeight,
                    tiles, (_, _) => Task.CompletedTask, cts.Token, key.Layers, priority: priority);
                cts.Token.ThrowIfCancellationRequested();
                state.PresentationEpoch++;
                if (images.Count == tiles.Length && images.All(image => image != null))
                {
                    for (int i = 0; i < images.Count; i++) pieces.Add(new(tiles[i], images[i]!));
                    // Commit one coherent crop even when its tiles ran in parallel.
                    bmp = await Task.Run(() => ViewportRegionReuse.Compose(ToRect(key), pieces, cts.Token), cts.Token);
                }
            }
            else
            {
                List<BitmapSource?> images;
                if (experimentalMuPdf && rectangles.Length > 0)
                {
                    try
                    {
                        images = await ExperimentalMuPdfViewport.RenderAsync(row.SourcePath, row.PageNumber - 1,
                            key.FullWidth, key.FullHeight, rectangles, cts.Token, priority);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        if (ExperimentalMuPdfViewport.ThroughputMode) throw;
                        Debug.WriteLine($"[MuPDF viewport] Falling back to PDFium: {ex.Message}");
                        images = await PdfThumbnailService.RenderPageTilesBatchAsync(row.SourcePath, row.PageNumber - 1,
                            key.FullWidth, key.FullHeight, rectangles, cts.Token, key.Layers, priority: priority);
                    }
                }
                else images = rectangles.Length == 0 ? new List<BitmapSource?>() :
                    await (RegionRenderer?.Invoke(row, key.FullWidth, key.FullHeight, rectangles, cts.Token, key.Layers) ??
                        PdfThumbnailService.RenderPageTilesBatchAsync(row.SourcePath, row.PageNumber - 1, key.FullWidth, key.FullHeight,
                            rectangles, cts.Token, key.Layers, priority: priority));
                cts.Token.ThrowIfCancellationRequested();
                if (images.Count == rectangles.Length && images.All(image => image != null))
                {
                    if (reuseOverlap)
                    {
                        for (int i = 0; i < images.Count; i++) pieces.Add(new(rectangles[i], images[i]!, missing[i]));
                        bmp = await Task.Run(() => ViewportRegionReuse.Compose(ToRect(key), pieces, cts.Token), cts.Token);
                    }
                    else bmp = images[0];
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine($"[ContinuousPdfView] region render failed: {ex.Message}"); }
        bool cancelled = cts.IsCancellationRequested;
        if (wide) { if (ReferenceEquals(state.WideCts, cts)) { state.WideCts = null; state.WidePending = null; } }
        else if (ReferenceEquals(state.RegionCts, cts)) { state.RegionCts = null; state.RegionPending = null; }
        cts.Dispose();
        if (cancelled || !IsLive(row, state, key.Version)) return;
        if (bmp == null) return;
        if (experimentalMuPdf)
            state.RegionRenderMilliseconds = ZoomRenderPrediction.Observe(state.RegionRenderMilliseconds,
                renderWatch.Elapsed.TotalMilliseconds);
        if (!string.Equals(key.Layers, PdfLayerStateStore.GetToken(row.SourcePath), StringComparison.Ordinal))
        {
            ScheduleUpdate();
            return;
        }

        var source = RenderCacheKeys.Thumbnail(row.SourcePath, row.PageNumber);
        if (ReuseRenderedImages)
            lock (RegionCacheLock)
                if (cacheGeneration == Interlocked.Read(ref _regionCacheGeneration))
                    RegionCache.Set(new CachedRegionKey(source.Path, source.Page, key with { Version = 0 }), bmp);

        // Vùng mới vẽ đè lên trên; giữ thêm 1 vùng cũ bên dưới (phần chưa phủ vẫn nét), bỏ các vùng khác.
        var regions = state.Regions;
        if (streamTiles)
        {
            // The temporary tile set has done its job. Retain only the completed
            // crop so diagnostics and the shared cache own these pixels once.
            regions.RemoveAll(region => region.Key.FullWidth == key.FullWidth && region.Key.FullHeight == key.FullHeight &&
                region.Key.Version == key.Version && string.Equals(region.Key.Layers, key.Layers, StringComparison.Ordinal));
        }
        regions.RemoveAll(r => r.Key.Version != key.Version || !string.Equals(r.Key.Layers, key.Layers, StringComparison.Ordinal));
        regions.Add(new RegionImage(key, bmp));
        while (regions.Count > (WideRegions ? 3 : 2)) regions.RemoveAt(0);
        if (IsOnScreen(row)) _surface.InvalidateVisual();
        CheckPresent();
        ScheduleUpdate(); // khung nhìn có thể đã đi tiếp trong lúc vẽ (pan) — xin vùng kế nếu cần
    }

    private bool IsOnScreen(PageRow row)
    {
        if (!_indexOf.TryGetValue(row, out int index) || SlotOf(index) is not (>= 0 and var slot)) return false;
        var (first, last) = _vp.VisibleRange();
        return slot >= first && slot <= last;
    }

    /// <summary>Vẽ lại (chú thích, kết quả tìm… vẽ trong <see cref="PageDrawn"/> đổi).</summary>
    public void Redraw() => _surface.InvalidateVisual();

    // ── Trạng thái từng trang ──────────────────────────────────────────────

    private readonly record struct RegionKey(int FullWidth, int FullHeight, int X, int Y, int Width, int Height, string Layers, int Version);
    private readonly record struct CachedRegionKey(string Path, int Page, RegionKey Region);
    private sealed record RegionImage(RegionKey Key, BitmapSource Bitmap);

    internal static (int Count, long Bytes) CachedRegionStats
    {
        get { lock (RegionCacheLock) return (RegionCache.Count, RegionCache.Bytes); }
    }

    internal static void InvalidateCachedRegions(Func<string, int, bool> match)
    {
        ExperimentalMuPdfViewport.InvalidateRasterCache(match);
        lock (RegionCacheLock)
        {
            Interlocked.Increment(ref _regionCacheGeneration);
            RegionCache.RemoveWhere(k => match(k.Path, k.Page));
        }
    }

    internal static void ReleaseUnusedRegionSources(HashSet<string> active)
        => InvalidateCachedRegions((path, _) => !active.Contains(path));

    private sealed class PageState
    {
        /// <summary>Tăng khi nội dung trang đổi (chú thích…) — ảnh cũ vẫn vẽ tạm nhưng phải xin lại.</summary>
        public int Version;
        public BitmapSource? Bitmap;
        public string? BitmapLayers;
        public int BitmapVersion;
        public BitmapSource? Preview;
        public CancellationTokenSource? PreviewCts;
        public CancellationTokenSource? PageCts;
        public int RequestedWidth, RequestedVersion;
        public PdfRenderPriority RequestedPriority;
        public int FailedWidth = -1, FailedVersion = -1, FailedAttempts;
        public long RetryAfterTimestamp;
        public int PresentationEpoch;
        public double RegionRenderMilliseconds = 200;
        /// <summary>Cỡ đã xin mà <see cref="Bitmap"/> được giao cho. Nhà cung cấp có thể giao ảnh khác cỡ (cache trả ảnh lớn hơn đã có):
        /// đã xin cỡ này rồi thì không xin lặp mãi dù ảnh nhận được không khớp cỡ hiển thị.</summary>
        public int DeliveredWidth = -1;
        public readonly List<RegionImage> Regions = new();
        public CancellationTokenSource? WideCts;
        public RegionKey? WidePending;
        public void CancelWide()
        {
            WideCts?.Cancel();
            WideCts = null;
            WidePending = null;
        }
        public CancellationTokenSource? RegionCts;
        public RegionKey? RegionPending;

        public void CancelRegion()
        {
            RegionCts?.Cancel();
            RegionCts = null;
            RegionPending = null;
        }

        public void CancelAll()
        {
            PreviewCts?.Cancel();
            PreviewCts = null;
            PageCts?.Cancel();
            PageCts = null;
            CancelRegion();
            CancelWide();
        }
    }

    /// <summary>Phần tử vẽ: mọi trang đang hiện vẽ trong 1 lần OnRender.</summary>
    private sealed class PageSurface : FrameworkElement
    {
        private readonly ContinuousPdfView _owner;
        public PageSurface(ContinuousPdfView owner) => _owner = owner;
        protected override void OnRender(DrawingContext dc) => _owner.Draw(dc);
    }
}
