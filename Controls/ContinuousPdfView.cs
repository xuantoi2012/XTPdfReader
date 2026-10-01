using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
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
    private const int MaxPageBitmapWidth = 2304;
    private const int MinPageBitmapWidth = 512;
    /// <summary>Ảnh đầu tiên trong vùng đọc phải đủ rõ; thumbnail sidebar 340 px chỉ dùng cho trang hiện rất nhỏ.</summary>
    internal const int ReadablePageBitmapWidth = 1024;
    /// <summary>Trang hiện nhỏ hơn chừng này (px thiết bị) thì ảnh nhỏ 340 px của thumbnail đã đủ nét.</summary>
    private const double PreviewSufficientPx = ThumbnailCache.RenderThumbnailWidthPx * 1.05;
    /// <summary>Độ phân giải vùng nét lượng tử hoá theo bậc 400 px — zoom nhích 1% không phải vẽ lại.</summary>
    private const int RegionResolutionQuantum = 400;
    private const int MaxRegionFullWidth = 16000;
    /// <summary>Cuộn nhanh: chỉ vẽ các trang đang hiện, chờ <see cref="SettleMilliseconds"/> rồi tải trước trang kế.</summary>
    private const double FastScrollViewportsPerSecond = 4;
    private const int SettleMilliseconds = 40;
    /// <summary>Đang zoom: chờ zoom đứng yên chừng này mới xin ảnh ở độ phân giải mới (giữa chừng chỉ co giãn ảnh có sẵn).</summary>
    private const int ZoomSettleMilliseconds = 60;
    private const int PrefetchPages = 4;
    /// <summary>Trang ngoài [đầu − n, cuối + n] quanh khung nhìn: huỷ việc đang vẽ, bỏ ảnh riêng của view.</summary>
    private const int KeepPages = 4;

    // ── Thành phần ─────────────────────────────────────────────────────────
    private readonly PageSurface _surface;
    private readonly ScrollBar _vbar;
    private readonly ScrollBar _hbar;
    private readonly ContinuousViewport _vp = new();
    private readonly DispatcherTimer _updateTimer;
    private readonly Dictionary<PageRow, PageState> _states = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PageRow, int> _indexOf = new(ReferenceEqualityComparer.Instance);
    private IReadOnlyList<PageRow> _pages = Array.Empty<PageRow>();
    /// <summary>Slot bố cục → chỉ số trang. Cuộn liên tục: mọi trang; 1 trang: chỉ trang đang xem.</summary>
    private int[] _slots = Array.Empty<int>();
    private bool _singlePage;
    private bool _twoPage;
    private int _single;
    private int _rotation;
    private INotifyCollectionChanged? _observed;
    private int _currentPage = -1;

    private static readonly Pen BorderPen = CreateBorderPen();
    private readonly List<AnnotationLayer.BaseImage> _bases = new();

    public ContinuousPdfView()
    {
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
        Unloaded += (_, _) => CancelAll();
        IsVisibleChanged += (_, _) => { if (IsVisible) ScheduleUpdate(immediate: true); };
    }

    // ── API cho ReaderWindow ───────────────────────────────────────────────

    /// <summary>Vẽ 1 trang cả trang ở chiều rộng pixel cho trước (ReaderWindow: cache ảnh trang dùng chung với chế độ 1 trang).</summary>
    internal Func<PageRow, int, PdfRenderPriority, CancellationToken, Task<BitmapSource?>>? PageRenderer { get; set; }

    public double MinZoom { get; set; } = 0.05;
    public double MaxZoom { get; set; } = 4.0;
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
        CancelAll();
        _states.Clear();
        _pages = pages ?? Array.Empty<PageRow>();
        _single = 0;
        RebuildIndex();
        _vp.SetPages(BaseSizes(), Math.Clamp(zoom, MinZoom, MaxZoom));
        _currentPage = _pages.Count > 0 ? 0 : -1;
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
        return _rotation % 180 == 0 ? (row.LayoutWidth, row.LayoutHeight) : (row.LayoutHeight, row.LayoutWidth);
    }

    public double ViewportHeight => _vp.ViewportHeight;

    /// <summary>Khổ giấy của một số trang vừa biết/đổi — dựng lại bố cục, trang ở đỉnh khung nhìn đứng yên.</summary>
    public void RefreshPageSizes()
    {
        _vp.UpdatePageSizes(BaseSizes());
        OnViewChanged(ChangeKind.Layout);
    }

    /// <summary>Đổi zoom, giữ điểm dưới <paramref name="viewPoint"/> (toạ độ trong <see cref="Surface"/>) đứng yên.</summary>
    public void ZoomAt(double zoom, Point viewPoint)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(zoom - _vp.Zoom) < 1e-6) return;
        _vp.ZoomAt(zoom, viewPoint.X, viewPoint.Y);
        OnViewChanged(ChangeKind.Zoom);
    }

    /// <summary>Đổi zoom giữ điểm giữa-đỉnh khung nhìn (vừa chiều rộng, nút +/−).</summary>
    public void ZoomKeepTop(double zoom) => ZoomAt(zoom, new Point(_vp.ViewportWidth / 2, 0));

    public Point ViewportCenter => new(_vp.ViewportWidth / 2, _vp.ViewportHeight / 2);

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
        (u, v) = Unrotate(du, dv);
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
        var (du, dv) = Rotate(u, v);
        point = new Point(rect.X + du * rect.Width, rect.Y + dv * rect.Height);
        return true;
    }

    /// <summary>Điểm trong <see cref="Surface"/> → (u, v) trên trang (chưa kẹp 0..1).</summary>
    internal bool TryViewToPage(PageRow row, Point viewPoint, out double u, out double v)
    {
        u = v = 0;
        if (!TryGetPageRect(row, out Rect rect) || rect.Width <= 0 || rect.Height <= 0) return false;
        (u, v) = Unrotate((viewPoint.X - rect.X) / rect.Width, (viewPoint.Y - rect.Y) / rect.Height);
        return true;
    }

    /// <summary>Hướng trang → hướng đang hiện (xoay khung nhìn theo chiều kim đồng hồ).</summary>
    private (double U, double V) Rotate(double u, double v) => _rotation switch
    {
        90 => (1 - v, u),
        180 => (1 - u, 1 - v),
        270 => (v, 1 - u),
        _ => (u, v)
    };

    private (double U, double V) Unrotate(double du, double dv) => _rotation switch
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
        // Ảnh Reader của trang (dùng chung với chế độ 1 trang) là nội dung cũ; ảnh riêng của view vẫn hiện tạm tới khi có ảnh mới.
        foreach (var row in _pages)
            if (match(row)) row.ReaderBitmap = null;
        foreach (var (row, state) in _states.ToArray())
        {
            if (!match(row)) continue;
            state.Version++;
            state.CancelAll();
            if (dropImages) { state.Bitmap = null; state.Preview = null; state.Regions.Clear(); }
        }
        _surface.InvalidateVisual();
        ScheduleUpdate(immediate: true);
    }

    /// <summary>Cho cửa sổ Debug: số trang đang giữ ảnh riêng, số vùng nét và dung lượng vùng nét.</summary>
    internal (int Pages, int Regions, long RegionBytes) MemoryStats
    {
        get
        {
            int regions = 0;
            long bytes = 0;
            foreach (var state in _states.Values)
                foreach (var region in state.Regions)
                {
                    regions++;
                    bytes += (long)region.Bitmap.PixelWidth * region.Bitmap.PixelHeight * 4;
                }
            return (_states.Count, regions, bytes);
        }
    }

    /// <summary>Huỷ mọi việc vẽ đang chờ (ẩn Viewer, đổi chế độ xem).</summary>
    public void CancelAll()
    {
        foreach (var state in _states.Values) state.CancelAll();
        _updateTimer.Stop();
    }

    // ── Thay đổi khung nhìn ────────────────────────────────────────────────

    private enum ChangeKind { Layout, Navigate, Zoom, UserScroll }

    private long _lastScrollTimestamp, _lastZoomTimestamp;
    private bool _fastScroll;
    private int _scrollDirection = 1;
    private double _lastOffsetY;

    private void OnViewChanged(ChangeKind kind)
    {
        UpdateScrollBars();
        _surface.InvalidateVisual();

        long now = Stopwatch.GetTimestamp();
        double dy = _vp.OffsetY - _lastOffsetY;
        _lastOffsetY = _vp.OffsetY;
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
        ScheduleUpdate(immediate: kind is ChangeKind.Navigate or ChangeKind.UserScroll);
    }

    private bool _updateQueued;

    private void ScheduleUpdate(bool immediate = false)
    {
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
            ZoomAt(ReaderZoomMath.WheelZoom(_vp.Zoom, e.Delta, ZoomStep, MinZoom, MaxZoom), e.GetPosition(_surface));
            UserZoomed?.Invoke();
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

    private void Draw(DrawingContext dc)
    {
        double width = _surface.ActualWidth, height = _surface.ActualHeight;
        // Nền do Grid (Background) vẽ; hình chữ nhật trong suốt để cả vùng nhận chuột (kéo ở khe giữa trang cũng pan được).
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        var (first, last) = _vp.VisibleRange();
        if (first < 0) return;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;

        for (int s = first; s <= last && s < _slots.Length; s++)
        {
            var row = _pages[_slots[s]];
            var (x, y, w, h) = _vp.PageRect(s);
            // Bám pixel thiết bị: viền 1 px sắc, ảnh không nhoè nửa pixel.
            var outer = Snap(new Rect(x, y, w, h), dpi);
            dc.DrawRectangle(Brushes.White, null, outer);
            double b = ContinuousPageLayout.BorderThickness;
            var shown = new Rect(outer.X + b, outer.Y + b, Math.Max(0, outer.Width - 2 * b), Math.Max(0, outer.Height - 2 * b));

            // Xoay khung nhìn: vẽ trang theo hướng của nó trong khung "content" rồi xoay quanh tâm khung đang hiện.
            var content = shown;
            bool rotated = _rotation != 0;
            if (rotated)
            {
                if (_rotation % 180 != 0)
                    content = new Rect(shown.X + (shown.Width - shown.Height) / 2, shown.Y + (shown.Height - shown.Width) / 2, shown.Height, shown.Width);
                dc.PushClip(new RectangleGeometry(shown));
                dc.PushTransform(new RotateTransform(_rotation, shown.X + shown.Width / 2, shown.Y + shown.Height / 2));
            }

            _states.TryGetValue(row, out var state);
            _bases.Clear();
            if (BestBitmap(row, state) is { } bitmap && IsReadableBitmap(bitmap, content.Width * dpi))
            {
                dc.DrawImage(bitmap, content);
                _bases.Add(new AnnotationLayer.BaseImage(bitmap, new Rect(0, 0, 1, 1)));
            }
            if (state is { Regions.Count: > 0 })
            {
                dc.PushClip(new RectangleGeometry(content));
                foreach (var region in state.Regions)
                {
                    var k = region.Key;
                    dc.DrawImage(region.Bitmap, new Rect(
                        content.X + content.Width * k.X / k.FullWidth,
                        content.Y + content.Height * k.Y / k.FullHeight,
                        content.Width * k.Width / k.FullWidth,
                        content.Height * k.Height / k.FullHeight));
                    _bases.Add(new AnnotationLayer.BaseImage(region.Bitmap,
                        new Rect((double)k.X / k.FullWidth, (double)k.Y / k.FullHeight, (double)k.Width / k.FullWidth, (double)k.Height / k.FullHeight)));
                }
                dc.Pop();
            }
            AnnotationLayer.Draw(dc, row, content, dpi, _bases);
            PageDrawn?.Invoke(dc, row, content, dpi);
            if (rotated) { dc.Pop(); dc.Pop(); }
            dc.DrawRectangle(null, BorderPen, new Rect(outer.X + 0.5 / dpi, outer.Y + 0.5 / dpi,
                Math.Max(0, outer.Width - 1 / dpi), Math.Max(0, outer.Height - 1 / dpi)));
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
        if (_pages.Count == 0 || _vp.ViewportHeight <= 0 || !IsVisible) return;
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
        bool zoomSettling = sinceZoom < ZoomSettleMilliseconds;

        var (firstSlot, lastSlot) = _vp.VisibleRange();
        if (firstSlot < 0 || lastSlot >= _slots.Length) return;
        int first = _slots[firstSlot], last = _slots[lastSlot];
        int keepFirst = Math.Max(0, first - KeepPages), keepLast = Math.Min(_pages.Count - 1, last + KeepPages);
        int renderFirst = first - (!scrollSettling && _scrollDirection < 0 ? PrefetchPages : 0);
        int renderLast = last + (!scrollSettling && _scrollDirection >= 0 ? PrefetchPages : 0);

        // Trang đã rời khu vực quanh khung nhìn: huỷ việc vẽ, trả ảnh (ảnh trang còn trong cache chung của ReaderWindow).
        foreach (var (row, state) in _states.ToArray())
        {
            if (_indexOf.TryGetValue(row, out int index) && index >= keepFirst && index <= keepLast)
            {
                if (index < renderFirst || index > renderLast) state.CancelAll();
                continue;
            }
            state.CancelAll();
            _states.Remove(row);
        }

        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var hot = new List<(string, int)>();
        for (int i = Math.Max(0, first - 1); i <= Math.Min(_pages.Count - 1, last + 1); i++)
            hot.Add((_pages[i].SourcePath, _pages[i].PageNumber));
        PdfThumbnailService.SetHotPages(hot);

        // Trang đang hiện: xin thẳng ảnh đúng độ phân giải, rồi vùng nét khi zoom sâu.
        for (int i = first; i <= last; i++)
        {
            var row = _pages[i];
            var state = StateOf(row);
            var best = BestBitmap(row, state);
            bool hasImage = best != null;

            double needed = row.LayoutWidth * _vp.Zoom * dpi;
            int pageWidth = PageBitmapWidth(needed);
            // Zoom nhỏ (nhiều trang trên màn hình): ảnh nhỏ đã đủ nét, không vẽ ảnh trang cho từng trang.
            if (needed <= PreviewSufficientPx)
            {
                if (state.Preview == null && row.Thumbnail == null) RequestPreview(row, state, PdfRenderPriority.Visible);
            }
            // Đang zoom: trang đã có ảnh thì co giãn ảnh đó, chưa xin độ phân giải mới (mỗi nấc zoom một lượt vẽ là lãng phí).
            else if (!(zoomSettling && hasImage) && NeedsPageBitmap(row, state, pageWidth))
            {
                RequestPage(row, state, pageWidth, PdfRenderPriority.Visible);
            }

            if (needed > MaxPageBitmapWidth * 1.03)
            {
                if (!zoomSettling) RequestRegion(SlotOf(i), row, state, needed);
            }
            else if (state.Regions.Count > 0 || state.RegionCts != null)
            {
                state.CancelRegion();
                state.Regions.Clear();
                _surface.InvalidateVisual();
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
        for (int step = 1; step <= PrefetchPages; step++)
        {
            int i = edge + dir * step;
            if (i < 0 || i >= _pages.Count) break;
            var row = _pages[i];
            var state = StateOf(row);
            double needed = row.LayoutWidth * _vp.Zoom * dpi;
            if (needed > PreviewSufficientPx)
            {
                int pageWidth = PageBitmapWidth(needed);
                if (NeedsPageBitmap(row, state, pageWidth)) RequestPage(row, state, pageWidth, PdfRenderPriority.Background);
            }
            else if (BestBitmap(row, state) == null) RequestPreview(row, state, PdfRenderPriority.Thumbnail);
        }
    }

    private static int PageBitmapWidth(double neededPx)
        => (int)Math.Clamp(Math.Ceiling(neededPx / 256) * 256, MinPageBitmapWidth, MaxPageBitmapWidth);

    private static bool NeedsPageBitmap(PageRow row, PageState state, int width)
    {
        if (state.Bitmap is { } bmp && state.BitmapVersion == state.Version &&
            string.Equals(state.BitmapLayers, PdfLayerStateStore.GetToken(row.SourcePath), StringComparison.Ordinal))
            return bmp.PixelWidth < width * 0.97;
        return state.FailedWidth != width || state.FailedVersion != state.Version;
    }

    private PageState StateOf(PageRow row)
    {
        if (!_states.TryGetValue(row, out var state)) _states[row] = state = new PageState();
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
            state.FailedWidth = width; // không xin lại mãi trang không vẽ được
            state.FailedVersion = version;
            return;
        }
        if (!string.Equals(layers, PdfLayerStateStore.GetToken(row.SourcePath), StringComparison.Ordinal)) { ScheduleUpdate(); return; }
        state.Bitmap = bmp;
        state.BitmapLayers = layers;
        state.BitmapVersion = version;
        if (row.ReaderBitmap is not { } existing || existing.PixelWidth <= bmp.PixelWidth) row.ReaderBitmap = bmp;
        if (IsOnScreen(row)) _surface.InvalidateVisual();
        ScheduleUpdate(); // hoàn tất trang này: tiếp tục tải trước mà không cần chờ một lần cuộn/zoom khác
    }

    private void RequestRegion(int slot, PageRow row, PageState state, double neededPx)
    {
        if (!_vp.VisibleFraction(slot, out double dx0, out double dy0, out double dx1, out double dy1)) return;
        // Phần đang hiện → phân số trên trang theo hướng của trang (vùng vẽ PDFium không xoay).
        var (ua, va) = Unrotate(dx0, dy0);
        var (ub, vb) = Unrotate(dx1, dy1);
        double fx0 = Math.Min(ua, ub), fx1 = Math.Max(ua, ub), fy0 = Math.Min(va, vb), fy1 = Math.Max(va, vb);
        int fullWidth = (int)Math.Min(MaxRegionFullWidth,
            Math.Ceiling(Math.Min(neededPx, MaxRegionFullWidth) / RegionResolutionQuantum) * RegionResolutionQuantum);
        double aspect = row.LayoutHeight / Math.Max(1, row.LayoutWidth);
        int fullHeight = Math.Max(1, (int)Math.Round(fullWidth * aspect));
        string layers = PdfLayerStateStore.GetToken(row.SourcePath);

        bool Covers(RegionKey k) => k.FullWidth == fullWidth && k.FullHeight == fullHeight && k.Version == state.Version &&
            string.Equals(k.Layers, layers, StringComparison.Ordinal) &&
            k.X <= fx0 * fullWidth + 0.5 && k.Y <= fy0 * fullHeight + 0.5 &&
            k.X + k.Width >= fx1 * fullWidth - 0.5 && k.Y + k.Height >= fy1 * fullHeight - 0.5;

        if (state.Regions.Any(r => Covers(r.Key))) return;
        if (state.RegionCts != null && state.RegionPending is { } pending)
        {
            if (Covers(pending)) return;
            // Đang pan: vùng đang vẽ dở vẫn còn trên màn hình thì vẽ nốt (vẽ xong sẽ tự xin vùng kế) — huỷ liên tục thì
            // pan đều tay không vùng nào kịp xong.
            bool sameResolution = pending.FullWidth == fullWidth && pending.Version == state.Version &&
                string.Equals(pending.Layers, layers, StringComparison.Ordinal);
            bool stillOnScreen = pending.X < fx1 * fullWidth && pending.X + pending.Width > fx0 * fullWidth &&
                pending.Y < fy1 * fullHeight && pending.Y + pending.Height > fy0 * fullHeight;
            if (sameResolution && stillOnScreen) return;
        }

        var (x, y, w, h) = ContinuousViewport.Region(fx0, fy0, fx1, fy1, fullWidth, fullHeight);
        var key = new RegionKey(fullWidth, fullHeight, x, y, w, h, layers, state.Version);
        state.CancelRegion();
        var cts = new CancellationTokenSource();
        state.RegionCts = cts;
        state.RegionPending = key;
        _ = LoadRegionAsync(row, state, key, cts);
    }

    private async Task LoadRegionAsync(PageRow row, PageState state, RegionKey key, CancellationTokenSource cts)
    {
        BitmapSource? bmp = null;
        try
        {
            bmp = await PdfThumbnailService.RenderPageTileAsync(row.SourcePath, row.PageNumber - 1, key.FullWidth, key.FullHeight,
                new Int32Rect(key.X, key.Y, key.Width, key.Height), cts.Token, key.Layers);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine($"[ContinuousPdfView] region render failed: {ex.Message}"); }
        bool cancelled = cts.IsCancellationRequested;
        if (ReferenceEquals(state.RegionCts, cts)) { state.RegionCts = null; state.RegionPending = null; }
        cts.Dispose();
        if (cancelled || bmp == null || !IsLive(row, state, key.Version)) return;

        // Vùng mới vẽ đè lên trên; giữ thêm 1 vùng cũ bên dưới (phần chưa phủ vẫn nét), bỏ các vùng khác.
        var regions = state.Regions;
        regions.RemoveAll(r => r.Key.Version != key.Version || !string.Equals(r.Key.Layers, key.Layers, StringComparison.Ordinal));
        regions.Add(new RegionImage(key, bmp));
        while (regions.Count > 2) regions.RemoveAt(0);
        if (IsOnScreen(row)) _surface.InvalidateVisual();
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
    private sealed record RegionImage(RegionKey Key, BitmapSource Bitmap);

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
        public int FailedWidth = -1, FailedVersion = -1;
        public readonly List<RegionImage> Regions = new();
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
