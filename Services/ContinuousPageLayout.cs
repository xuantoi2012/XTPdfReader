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
    /// <summary>Khoảng cách giữa 2 trang và lề trên/dưới ở zoom 1 (như ContinuousPageMarginConverter trước đây).</summary>
    public const double BaseGap = 14;
    public const double MinGap = 4;
    /// <summary>Lề trái/phải/trên/dưới của cả dải trang (DIP, không đổi theo zoom).</summary>
    public const double Margin = 16;
    /// <summary>Viền 1 DIP quanh mỗi trang (Border trong template) — cộng vào kích thước phần tử.</summary>
    public const double BorderThickness = 1;

    private readonly double[] _top;
    private readonly double[] _width;
    private readonly double[] _height;

    public ContinuousPageLayout(IReadOnlyList<(double Width, double Height)> baseSizes, double zoom)
    {
        Zoom = zoom;
        int n = baseSizes.Count;
        _top = new double[n];
        _width = new double[n];
        _height = new double[n];
        Gap = Math.Max(MinGap, BaseGap * zoom);
        double y = Margin, maxWidth = 0;
        for (int i = 0; i < n; i++)
        {
            _top[i] = y;
            _width[i] = Math.Max(1, baseSizes[i].Width * zoom) + 2 * BorderThickness;
            _height[i] = Math.Max(1, baseSizes[i].Height * zoom) + 2 * BorderThickness;
            maxWidth = Math.Max(maxWidth, _width[i]);
            y += _height[i] + (i < n - 1 ? Gap : 0);
        }
        ContentWidth = maxWidth + 2 * Margin;
        ContentHeight = n == 0 ? 2 * Margin : y + Margin;
    }

    public double Zoom { get; }
    public double Gap { get; }
    public int Count => _top.Length;
    /// <summary>Chiều rộng nội dung (trang rộng nhất + 2 lề).</summary>
    public double ContentWidth { get; }
    public double ContentHeight { get; }

    public double Top(int index) => _top[index];
    public double Height(int index) => _height[index];
    public double Width(int index) => _width[index];

    /// <summary>Toạ độ trái của trang: căn giữa trong max(chiều rộng nội dung, chiều rộng khung nhìn).</summary>
    public double Left(int index, double viewportWidth)
        => Margin + (Math.Max(ContentWidth, viewportWidth) - 2 * Margin - _width[index]) / 2;

    /// <summary>Trang chứa (hoặc gần nhất phía trên) toạ độ dọc <paramref name="y"/> của nội dung.</summary>
    public int IndexAt(double y)
    {
        if (Count == 0) return -1;
        int lo = 0, hi = Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_top[mid] <= y) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Các trang giao với dải dọc [y0, y1] của nội dung (-1,-1 nếu không có).</summary>
    public (int First, int Last) Range(double y0, double y1)
    {
        if (Count == 0 || y1 < y0) return (-1, -1);
        int first = IndexAt(y0);
        if (first < 0) return (-1, -1);
        if (_top[first] + _height[first] < y0 && first < Count - 1) first++; // y0 rơi vào khe giữa 2 trang
        int last = IndexAt(y1);
        if (last < first) return (-1, -1);
        return (first, last);
    }

    /// <summary>Điểm neo zoom: trang dưới điểm (x, y) của nội dung + vị trí tương đối trong trang (0..1, có thể
    /// ngoài 0..1 nếu điểm nằm ở khe/lề — vẫn giữ đúng vị trí khi zoom).</summary>
    public (int Index, double FractionX, double FractionY) Anchor(double x, double y, double viewportWidth)
    {
        int i = IndexAt(y);
        if (i < 0) return (-1, 0, 0);
        // Điểm nằm ở khe dưới trang i: neo theo trang gần hơn.
        if (y > _top[i] + _height[i] && i < Count - 1 && y - (_top[i] + _height[i]) > (_top[i + 1] - y)) i++;
        double left = Left(i, viewportWidth);
        return (i, (x - left) / _width[i], (y - _top[i]) / _height[i]);
    }

    /// <summary>Ngược với <see cref="Anchor"/>: toạ độ nội dung của điểm (fx, fy) trong trang.</summary>
    public (double X, double Y) PointFor(int index, double fractionX, double fractionY, double viewportWidth)
        => (Left(index, viewportWidth) + fractionX * _width[index], _top[index] + fractionY * _height[index]);
}
