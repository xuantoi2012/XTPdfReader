using System;
using System.Collections.Generic;

namespace XTPdfMergeApp.Services;

/// <summary>
/// Bố cục chế độ Cuộn liên tục, tính HOÀN TOÀN bằng số học từ kích thước thật của mọi trang — cùng cách
/// <c>chrome_pdf::DocumentLayout</c> của Chromium: trang xếp dọc, căn giữa theo chiều ngang, khoảng cách
/// giữa trang và lề tỉ lệ theo zoom. Không phụ thuộc WPF đo từng phần tử, nên:
/// - khổ giấy thật: trang A3 hiện nhỏ hơn A1 đúng tỉ lệ (kích thước cơ sở do caller tính từ point của PDF);
/// - biết chính xác toạ độ MỌI trang, kể cả trang chưa hiện → zoom giữ đúng điểm dưới con trỏ, không nhảy;
/// - tìm trang đang hiện bằng tìm kiếm nhị phân.
/// Đơn vị: DIP. Kích thước "cơ sở" = kích thước trang (kể cả viền) ở zoom 1.
/// </summary>
internal sealed class ContinuousPageLayout
{
    /// <summary>Khoảng cách giữa 2 trang và lề trên/dưới ở zoom 1.</summary>
    public const double BaseGap = 14;
    public const double MinGap = 4;
    /// <summary>Lề trái/phải/trên/dưới của cả dải trang (DIP, không đổi theo zoom).</summary>
    public const double Margin = 16;
    /// <summary>Viền 1 DIP quanh mỗi trang (Border trong template) — cộng vào kích thước phần tử.</summary>
    public const double BorderThickness = 1;

    private readonly double[] _top;
    private readonly double[] _width;
    private readonly double[] _height;
    private readonly double[] _rowWidth;
    private readonly double[] _rowOffset;
    private readonly int[] _rowFirst;
    private readonly double[] _rowTop;
    private readonly double[] _rowHeight;

    /// <param name="columns">1 for normal/single layouts, 2 for facing-page spreads.</param>
    public ContinuousPageLayout(IReadOnlyList<(double Width, double Height)> baseSizes, double zoom, int columns = 1)
    {
        Zoom = zoom;
        int n = baseSizes.Count;
        Columns = Math.Clamp(columns, 1, 2);
        _top = new double[n];
        _width = new double[n];
        _height = new double[n];
        _rowWidth = new double[n];
        _rowOffset = new double[n];
        int rowCount = (n + Columns - 1) / Columns;
        _rowFirst = new int[rowCount];
        _rowTop = new double[rowCount];
        _rowHeight = new double[rowCount];
        Gap = Math.Max(MinGap, BaseGap * zoom);
        for (int i = 0; i < n; i++)
        {
            _width[i] = Math.Max(1, baseSizes[i].Width * zoom) + 2 * BorderThickness;
            _height[i] = Math.Max(1, baseSizes[i].Height * zoom) + 2 * BorderThickness;
        }

        double y = Margin, maxWidth = 0;
        for (int row = 0, first = 0; first < n; row++, first += Columns)
        {
            int last = Math.Min(n, first + Columns);
            double rowWidth = 0, rowHeight = 0;
            for (int i = first; i < last; i++)
            {
                if (i > first) rowWidth += Gap;
                _rowOffset[i] = rowWidth;
                rowWidth += _width[i];
                rowHeight = Math.Max(rowHeight, _height[i]);
            }

            _rowFirst[row] = first;
            _rowTop[row] = y;
            _rowHeight[row] = rowHeight;
            for (int i = first; i < last; i++)
            {
                _top[i] = y;
                _rowWidth[i] = rowWidth;
            }
            maxWidth = Math.Max(maxWidth, rowWidth);
            y += rowHeight + (row < rowCount - 1 ? Gap : 0);
        }
        ContentWidth = maxWidth + 2 * Margin;
        ContentHeight = n == 0 ? 2 * Margin : y + Margin;
    }

    public double Zoom { get; }
    public double Gap { get; }
    public int Columns { get; }
    public int Count => _top.Length;
    /// <summary>Chiều rộng nội dung (trang rộng nhất + 2 lề).</summary>
    public double ContentWidth { get; }
    public double ContentHeight { get; }

    public double Top(int index) => _top[index];
    public double Height(int index) => _height[index];
    public double Width(int index) => _width[index];

    /// <summary>Toạ độ trái của trang: căn giữa cả hàng trang trong max(chiều rộng nội dung, chiều rộng khung nhìn).</summary>
    public double Left(int index, double viewportWidth)
        => Margin + (Math.Max(ContentWidth, viewportWidth) - 2 * Margin - _rowWidth[index]) / 2 + _rowOffset[index];

    /// <summary>Trang chứa (hoặc gần nhất phía trên) toạ độ dọc <paramref name="y"/> của nội dung.</summary>
    public int IndexAt(double y)
    {
        int row = RowAt(y);
        return row < 0 ? -1 : _rowFirst[row];
    }

    /// <summary>Trang dưới toạ độ nội dung. Ở spread hai trang, chọn đúng trang bên trái/phải.</summary>
    public int IndexAt(double x, double y, double viewportWidth)
    {
        int first = IndexAt(y);
        if (first < 0 || Columns == 1 || first + 1 >= Count) return first;
        int second = first + 1;
        double split = (Left(first, viewportWidth) + Width(first) + Left(second, viewportWidth)) / 2;
        return x >= split ? second : first;
    }

    private int RowAt(double y)
    {
        if (_rowFirst.Length == 0) return -1;
        int lo = 0, hi = _rowFirst.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_rowTop[mid] <= y) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Các trang giao với dải dọc [y0, y1] của nội dung (-1,-1 nếu không có).</summary>
    public (int First, int Last) Range(double y0, double y1)
    {
        if (Count == 0 || y1 < y0) return (-1, -1);
        int first = IndexAt(y0);
        if (first < 0) return (-1, -1);
        int firstRow = RowAt(y0);
        if (_rowTop[firstRow] + _rowHeight[firstRow] < y0 && firstRow < _rowFirst.Length - 1) first = _rowFirst[firstRow + 1];
        int lastRow = RowAt(y1);
        int last = lastRow < 0 ? -1 : Math.Min(Count - 1, _rowFirst[lastRow] + Columns - 1);
        if (last < first) return (-1, -1);
        return (first, last);
    }

    /// <summary>Điểm neo zoom: trang dưới điểm (x, y) của nội dung + vị trí tương đối trong trang (0..1, có thể
    /// ngoài 0..1 nếu điểm nằm ở khe/lề — vẫn giữ đúng vị trí khi zoom).</summary>
    public (int Index, double FractionX, double FractionY) Anchor(double x, double y, double viewportWidth)
    {
        int i = IndexAt(x, y, viewportWidth);
        if (i < 0) return (-1, 0, 0);
        // Điểm nằm ở khe dưới một hàng: neo theo hàng gần hơn.
        int row = RowAt(y);
        if (row >= 0 && y > _rowTop[row] + _rowHeight[row] && row < _rowFirst.Length - 1 &&
            y - (_rowTop[row] + _rowHeight[row]) > (_rowTop[row + 1] - y))
            i = _rowFirst[row + 1];
        double left = Left(i, viewportWidth);
        return (i, (x - left) / _width[i], (y - _top[i]) / _height[i]);
    }

    /// <summary>Ngược với <see cref="Anchor"/>: toạ độ nội dung của điểm (fx, fy) trong trang.</summary>
    public (double X, double Y) PointFor(int index, double fractionX, double fractionY, double viewportWidth)
        => (Left(index, viewportWidth) + fractionX * _width[index], _top[index] + fractionY * _height[index]);
}
