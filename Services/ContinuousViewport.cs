using System;
using System.Collections.Generic;

namespace XTPdfMergeApp.Services;

/// <summary>
/// Trạng thái khung nhìn của chế độ Cuộn liên tục kiểu Foxit: bố cục (<see cref="ContinuousPageLayout"/>), zoom, vị trí
/// cuộn, kích thước khung nhìn — thuần số học, không phụ thuộc WPF (kiểm thử được ngoài Windows).
/// Mọi thao tác (cuộn, zoom quanh con trỏ, nhảy trang, đổi khổ giấy) chỉ đổi vài con số; vùng vẽ đọc lại các con số
/// này và vẽ ngay khung hình kế tiếp bằng ảnh đang có.
/// Đơn vị: DIP. Toạ độ "view" = toạ độ trong khung nhìn (gốc trên-trái vùng vẽ).
/// </summary>
internal sealed class ContinuousViewport
{
    private IReadOnlyList<(double Width, double Height)> _baseSizes = Array.Empty<(double, double)>();
    private int _columns = 1;

    public ContinuousPageLayout Layout { get; private set; } = new(Array.Empty<(double, double)>(), 1);
    public double Zoom => Layout.Zoom;
    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }
    public double ViewportWidth { get; private set; }
    public double ViewportHeight { get; private set; }
    public int Count => Layout.Count;
    public int Columns => _columns;

    public double MaxOffsetX => Math.Max(0, Layout.ContentWidth - ViewportWidth);
    public double MaxOffsetY => Math.Max(0, Layout.ContentHeight - ViewportHeight);

    /// <summary>Bản sao độc lập (bố cục là bất biến nên dùng chung): giữ lại khung nhìn "đang hiện" trong lúc zoom mới chờ ảnh đúng cỡ.</summary>
    public ContinuousViewport Clone() => new()
    {
        _baseSizes = _baseSizes, _columns = _columns, Layout = Layout,
        OffsetX = OffsetX, OffsetY = OffsetY, ViewportWidth = ViewportWidth, ViewportHeight = ViewportHeight
    };

    /// <summary>Tài liệu mới: kích thước cơ sở (zoom 1) của mọi trang. Vị trí cuộn về đầu.</summary>
    public void SetPages(IReadOnlyList<(double Width, double Height)> baseSizes, double zoom)
    {
        _baseSizes = baseSizes;
        Layout = new ContinuousPageLayout(baseSizes, zoom, _columns);
        OffsetX = OffsetY = 0;
    }

    /// <summary>Đổi bố cục một/trải hai trang, giữ trang ở giữa đỉnh khung nhìn nếu có thể.</summary>
    public void SetColumns(int columns)
    {
        columns = Math.Clamp(columns, 1, 2);
        if (_columns == columns) return;
        var anchor = AnchorAt(ViewportWidth / 2, 0);
        _columns = columns;
        Layout = new ContinuousPageLayout(_baseSizes, Zoom, _columns);
        Restore(anchor, ViewportWidth / 2, 0);
    }

    /// <summary>Khổ giấy của một số trang vừa biết/đổi — trang ở đỉnh khung nhìn (điểm giữa-đỉnh) đứng yên.</summary>
    public void UpdatePageSizes(IReadOnlyList<(double Width, double Height)> baseSizes)
    {
        var anchor = AnchorAt(ViewportWidth / 2, 0);
        _baseSizes = baseSizes;
        Layout = new ContinuousPageLayout(baseSizes, Zoom, _columns);
        Restore(anchor, ViewportWidth / 2, 0);
    }

    /// <summary>Khung nhìn đổi cỡ. Offset kẹp lại trong phạm vi hợp lệ.</summary>
    public void SetViewportSize(double width, double height)
    {
        ViewportWidth = Math.Max(0, width);
        ViewportHeight = Math.Max(0, height);
        SetOffset(OffsetX, OffsetY);
    }

    /// <returns>true nếu vị trí thật sự đổi.</returns>
    public bool SetOffset(double x, double y)
    {
        x = double.IsNaN(x) ? 0 : Math.Clamp(x, 0, MaxOffsetX);
        y = double.IsNaN(y) ? 0 : Math.Clamp(y, 0, MaxOffsetY);
        if (x == OffsetX && y == OffsetY) return false;
        OffsetX = x;
        OffsetY = y;
        return true;
    }

    /// <summary>Đổi zoom, giữ điểm nội dung dưới (<paramref name="viewX"/>, <paramref name="viewY"/>) đứng yên —
    /// như Chromium Viewport.setZoomInternal_: chỉ là phép tính trên bố cục.</summary>
    public void ZoomAt(double zoom, double viewX, double viewY)
    {
        if (zoom <= 0 || double.IsNaN(zoom)) return;
        var anchor = AnchorAt(viewX, viewY);
        Layout = new ContinuousPageLayout(_baseSizes, zoom, _columns);
        Restore(anchor, viewX, viewY);
    }

    /// <summary>Đỉnh trang <paramref name="index"/> lên đỉnh khung nhìn (chừa 1 khe phía trên); cột ngang giữ nguyên.</summary>
    public void ScrollToPage(int index)
    {
        if (index < 0 || index >= Count) return;
        SetOffset(OffsetX, Layout.Top(index) - Layout.Gap);
    }

    private (int Index, double FractionX, double FractionY) AnchorAt(double viewX, double viewY)
        => Count == 0 ? (-1, 0, 0) : Layout.Anchor(OffsetX + viewX, OffsetY + viewY, ViewportWidth);

    private void Restore((int Index, double FractionX, double FractionY) anchor, double viewX, double viewY)
    {
        if (anchor.Index < 0 || anchor.Index >= Count) { SetOffset(OffsetX, OffsetY); return; }
        var (x, y) = Layout.PointFor(anchor.Index, anchor.FractionX, anchor.FractionY, ViewportWidth);
        SetOffset(x - viewX, y - viewY);
    }

    /// <summary>Các trang giao khung nhìn (mở rộng thêm <paramref name="extra"/> DIP mỗi phía).</summary>
    public (int First, int Last) VisibleRange(double extra = 0)
        => Count == 0 || ViewportHeight <= 0 ? (-1, -1) : Layout.Range(OffsetY - extra, OffsetY + ViewportHeight + extra);

    /// <summary>Khung trang <paramref name="index"/> trong toạ độ view (kể cả viền 1 DIP).</summary>
    public (double X, double Y, double Width, double Height) PageRect(int index)
        => (Layout.Left(index, ViewportWidth) - OffsetX, Layout.Top(index) - OffsetY, Layout.Width(index), Layout.Height(index));

    /// <summary>Vùng vẽ nội dung trang (bỏ viền) trong toạ độ view.</summary>
    public (double X, double Y, double Width, double Height) PageContentRect(int index)
    {
        var (x, y, w, h) = PageRect(index);
        double b = ContinuousPageLayout.BorderThickness;
        return (x + b, y + b, Math.Max(0, w - 2 * b), Math.Max(0, h - 2 * b));
    }

    /// <summary>Trang dưới điểm view + vị trí chuẩn hoá (u, v) trong phần nội dung trang (0..1).</summary>
    public bool HitTest(double viewX, double viewY, out int index, out double u, out double v)
    {
        index = -1; u = v = 0;
        if (Count == 0) return false;
        int i = Layout.IndexAt(OffsetX + viewX, OffsetY + viewY, ViewportWidth);
        if (i < 0) return false;
        var (x, y, w, h) = PageContentRect(i);
        if (w <= 0 || h <= 0) return false;
        double pu = (viewX - x) / w, pv = (viewY - y) / h;
        if (pu < 0 || pu > 1 || pv < 0 || pv > 1) return false;
        index = i; u = pu; v = pv;
        return true;
    }

    /// <summary>Trang "đang xem" (như Chromium getMostVisiblePage): trang có tỉ lệ phần hiện lớn nhất — nhiều trang hiện
    /// trọn (zoom nhỏ) thì lấy trang trên cùng trong số đó; trang cao hơn màn hình thì lấy trang chiếm nhiều khung nhìn nhất.</summary>
    public int CurrentPage()
    {
        var (first, last) = VisibleRange();
        if (first < 0) return Count > 0 ? Math.Clamp(Layout.IndexAt(OffsetY), 0, Count - 1) : -1;
        int best = first;
        double bestVisible = -1;
        for (int i = first; i <= last; i++)
        {
            double top = Layout.Top(i) - OffsetY, bottom = top + Layout.Height(i);
            double height = Math.Max(1, Layout.Height(i));
            double shown = Math.Min(bottom, ViewportHeight) - Math.Max(top, 0);
            // Tỉ lệ phần hiện, tính theo phần khung nhìn nếu trang cao hơn khung nhìn (trang cao bị cắt vẫn "hiện trọn").
            double visible = shown / Math.Min(height, ViewportHeight);
            if (visible > bestVisible + 0.001) { bestVisible = visible; best = i; }
        }
        return best;
    }

    /// <summary>Phần trang <paramref name="index"/> đang nằm trong khung nhìn, theo phân số của trang (0..1) — rỗng nếu
    /// trang không hiện.</summary>
    public bool VisibleFraction(int index, out double x0, out double y0, out double x1, out double y1)
    {
        x0 = y0 = x1 = y1 = 0;
        if (index < 0 || index >= Count) return false;
        var (x, y, w, h) = PageContentRect(index);
        if (w <= 0 || h <= 0) return false;
        double ix0 = Math.Max(0, x), iy0 = Math.Max(0, y);
        double ix1 = Math.Min(ViewportWidth, x + w), iy1 = Math.Min(ViewportHeight, y + h);
        if (ix1 <= ix0 || iy1 <= iy0) return false;
        x0 = (ix0 - x) / w; y0 = (iy0 - y) / h;
        x1 = (ix1 - x) / w; y1 = (iy1 - y) / h;
        return true;
    }

    /// <summary>
    /// Vùng cần vẽ nét của trang khi zoom sâu (cách của Chromium: vẽ đúng phần đang nhìn thay vì cả trang), trong toạ độ
    /// pixel của trang ở độ phân giải <paramref name="fullWidth"/>×<paramref name="fullHeight"/>: phần đang nhìn + lề
    /// <paramref name="marginFraction"/> mỗi phía (pan trong lề không phải vẽ lại), bám lưới <paramref name="snap"/> px,
    /// không quá <paramref name="maxPixels"/> (thu lề nếu cần).
    /// </summary>
    public static (int X, int Y, int Width, int Height) Region(double fx0, double fy0, double fx1, double fy1,
        int fullWidth, int fullHeight, double marginFraction = 0.125, int snap = 64, long maxPixels = 12_000_000, int panX = 0, int panY = 0)
    {
        double left = fx0 * fullWidth, top = fy0 * fullHeight, right = fx1 * fullWidth, bottom = fy1 * fullHeight;
        double marginX = Math.Max(snap * 2, (right - left) * marginFraction);
        double marginY = Math.Max(snap * 2, (bottom - top) * marginFraction);
        // Đang pan: dồn lề về phía đang đi (ảnh nét sẵn ở nơi sắp tới), bớt lề phía sau.
        const double Lead = 2.5, Trail = 0.5;
        while (true)
        {
            double mL = panX > 0 ? marginX * Trail : panX < 0 ? marginX * Lead : marginX, mR = panX > 0 ? marginX * Lead : panX < 0 ? marginX * Trail : marginX;
            double mT = panY > 0 ? marginY * Trail : panY < 0 ? marginY * Lead : marginY, mB = panY > 0 ? marginY * Lead : panY < 0 ? marginY * Trail : marginY;
            int x0 = Math.Max(0, (int)Math.Floor((left - Math.Max(snap, mL)) / snap) * snap);
            int y0 = Math.Max(0, (int)Math.Floor((top - Math.Max(snap, mT)) / snap) * snap);
            int x1 = Math.Min(fullWidth, (int)Math.Ceiling((right + Math.Max(snap, mR)) / snap) * snap);
            int y1 = Math.Min(fullHeight, (int)Math.Ceiling((bottom + Math.Max(snap, mB)) / snap) * snap);
            if ((long)(x1 - x0) * (y1 - y0) <= maxPixels || (marginX < 1 && marginY < 1))
                return (x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
            marginX /= 2;
            marginY /= 2;
        }
    }
}
