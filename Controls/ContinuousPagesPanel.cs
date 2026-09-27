using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// Panel ảo hoá cho chế độ Cuộn liên tục, thay VirtualizingStackPanel. Vị trí mọi trang lấy từ
/// <see cref="ContinuousPageLayout"/> (kích thước thật của từng trang × zoom), không từ việc WPF đo phần tử —
/// như Chromium: VirtualizingStackPanel chỉ ƯỚC LƯỢNG chiều cao các trang chưa hiện, nên mỗi nấc zoom phải
/// UpdateLayout 2 lần rồi dò lại vị trí trang dưới con trỏ, vừa chậm vừa lệch (trang nhảy khi zoom). Ở đây:
/// - zoom neo con trỏ = phép tính trên bố cục (<see cref="ZoomAt"/>), chỉ vài trang đang hiện được xếp lại;
/// - chỉ tạo phần tử cho trang giao với khung nhìn ± nửa màn hình (tái dùng phần tử cũ khi cuộn).
/// Phần tử con là ListBoxItem do ListBox sinh; mỗi trang được Arrange đúng kích thước bố cục.
/// </summary>
public sealed class ContinuousPagesPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(ContinuousPagesPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    /// <summary>Cuộn mỗi nấc chuột / mỗi lần bấm mũi tên thanh cuộn (như VirtualizingStackPanel cuộn theo pixel).</summary>
    private const double LineDelta = 48;

    private ContinuousPageLayout? _layout;
    private bool _layoutDirty = true;
    private Size _extent, _viewport;
    private Vector _offset;

    /// <summary>Kích thước cơ sở của trang (zoom 1) — PagePlacement.LayoutWidth/LayoutHeight.</summary>
    private static (double Width, double Height) BaseSizeOf(object item)
        => item is PageRow row ? (row.LayoutWidth, row.LayoutHeight) : (PageRow.DefaultLayoutWidth, PageRow.DefaultLayoutWidth * PageRow.DefaultAspect);

    /// <summary>Bố cục hiện tại (dựng lại nếu zoom / số trang / kích thước trang vừa đổi).</summary>
    internal ContinuousPageLayout Layout
    {
        get
        {
            var items = ItemsControl.GetItemsOwner(this)?.Items;
            int count = items?.Count ?? 0;
            if (_layout == null || _layoutDirty || _layout.Count != count || _layout.Zoom != Zoom)
            {
                var sizes = new (double, double)[count];
                for (int i = 0; i < count; i++) sizes[i] = BaseSizeOf(items![i]);
                _layout = new ContinuousPageLayout(sizes, Zoom);
                _layoutDirty = false;
            }
            return _layout;
        }
    }

    /// <summary>Kích thước 1 hay nhiều trang vừa biết/đổi (đọc xong khổ giấy thật, xoay trang…).</summary>
    internal void InvalidatePageLayout()
    {
        _layoutDirty = true;
        InvalidateMeasure();
    }

    /// <summary>Đổi zoom, giữ nguyên điểm nội dung đang nằm dưới <paramref name="viewportPoint"/> (toạ độ trong khung
    /// nhìn) — tính trên bố cục, không cần đo lại phần tử. <paramref name="applyZoom"/> đổi zoom ở nơi sở hữu nó (Zoom
    /// của panel bind tới đó); panel tự đặt zoom nếu binding chưa kịp cập nhật.</summary>
    internal void ZoomAt(double zoom, Point viewportPoint, Action applyZoom)
    {
        var before = Layout;
        var anchor = before.Count == 0 ? (-1, 0.0, 0.0)
            : before.Anchor(_offset.X + viewportPoint.X, _offset.Y + viewportPoint.Y, _viewport.Width);
        applyZoom();
        if (Zoom != zoom) SetCurrentValue(ZoomProperty, zoom); // không phá binding
        var after = Layout;
        _extent = new Size(after.ContentWidth, after.ContentHeight);
        if (anchor.Item1 >= 0 && anchor.Item1 < after.Count)
        {
            var (x, y) = after.PointFor(anchor.Item1, anchor.Item2, anchor.Item3, _viewport.Width);
            SetOffsets(x - viewportPoint.X, y - viewportPoint.Y);
        }
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    /// <summary>Chạy <paramref name="change"/> (vd gán khổ giấy thật cho các trang) mà trang ở đỉnh khung nhìn không
    /// bị đẩy đi chỗ khác.</summary>
    internal void PreserveTopWhile(Action change)
    {
        var before = Layout;
        var anchor = before.Count == 0 ? (-1, 0.0, 0.0)
            : before.Anchor(_offset.X + _viewport.Width / 2, _offset.Y, _viewport.Width);
        change();
        _layoutDirty = true;
        var after = Layout;
        if (anchor.Item1 >= 0 && anchor.Item1 < after.Count)
        {
            var (x, y) = after.PointFor(anchor.Item1, anchor.Item2, anchor.Item3, _viewport.Width);
            _extent = new Size(after.ContentWidth, after.ContentHeight);
            SetOffsets(x - _viewport.Width / 2, y);
        }
        InvalidateMeasure();
    }

    /// <summary>Cuộn để đỉnh trang <paramref name="index"/> nằm ở đỉnh khung nhìn (Home/End, ô số trang, bấm thumbnail).</summary>
    internal void ScrollToPage(int index)
    {
        var layout = Layout;
        if (index < 0 || index >= layout.Count) return;
        _extent = new Size(layout.ContentWidth, layout.ContentHeight);
        SetOffsets(_offset.X, layout.Top(index) - layout.Gap);
    }

    // ── Đo / xếp ─────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren; // phải truy cập trước ItemContainerGenerator
        var generator = ItemContainerGenerator;
        var layout = Layout;

        var viewport = new Size(
            double.IsInfinity(availableSize.Width) ? layout.ContentWidth : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? layout.ContentHeight : availableSize.Height);
        UpdateScrollInfo(viewport, new Size(layout.ContentWidth, layout.ContentHeight));

        // Tạo phần tử cho trang giao với khung nhìn ± nửa màn hình (cuộn chậm không thấy trang trống).
        double cache = viewport.Height * 0.5;
        var (first, last) = layout.Range(_offset.Y - cache, _offset.Y + viewport.Height + cache);
        if (first >= 0)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
            {
                for (int i = first; i <= last; i++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out bool newlyRealized);
                    if (newlyRealized)
                    {
                        if (childIndex >= children.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(layout.Width(i), layout.Height(i)));
                }
            }
        }
        CleanUpItems(first, last);
        return viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        var layout = Layout;
        for (int c = 0; c < children.Count; c++)
        {
            int i = generator.IndexFromGeneratorPosition(new GeneratorPosition(c, 0));
            if (i < 0 || i >= layout.Count) continue;
            children[c].Arrange(new Rect(layout.Left(i, finalSize.Width) - _offset.X, layout.Top(i) - _offset.Y,
                layout.Width(i), layout.Height(i)));
        }
        return finalSize;
    }

    /// <summary>Trả phần tử của trang ngoài dải [first, last] về cho ListBox tái dùng.</summary>
    private void CleanUpItems(int first, int last)
    {
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        var recycler = generator as IRecyclingItemContainerGenerator;
        for (int c = children.Count - 1; c >= 0; c--)
        {
            var position = new GeneratorPosition(c, 0);
            int i = generator.IndexFromGeneratorPosition(position);
            if (first >= 0 && i >= first && i <= last) continue;
            if (recycler != null) recycler.Recycle(position, 1);
            else generator.Remove(position, 1);
            RemoveInternalChildRange(c, 1);
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                // Đổi tài liệu: bỏ phần tử + vị trí cuộn của tài liệu cũ (ReaderWindow tự cuộn tới trang cần xem).
                if (InternalChildren.Count > 0) RemoveInternalChildRange(0, InternalChildren.Count);
                _offset = new Vector(0, 0);
                ScrollOwner?.InvalidateScrollInfo();
                break;
        }
        _layoutDirty = true;
        base.OnItemsChanged(sender, args);
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index) => ScrollToPage(index);

    // ── IScrollInfo ──────────────────────────────────────────────────────────

    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    private void UpdateScrollInfo(Size viewport, Size extent)
    {
        bool changed = viewport != _viewport || extent != _extent;
        _viewport = viewport;
        _extent = extent;
        // Giữ offset trong phạm vi hợp lệ khi khung nhìn/nội dung đổi kích thước.
        double x = Math.Clamp(_offset.X, 0, Math.Max(0, _extent.Width - _viewport.Width));
        double y = Math.Clamp(_offset.Y, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (x != _offset.X || y != _offset.Y) { _offset = new Vector(x, y); changed = true; }
        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    private void SetOffsets(double x, double y)
    {
        x = Math.Clamp(x, 0, Math.Max(0, _extent.Width - _viewport.Width));
        y = Math.Clamp(y, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (x == _offset.X && y == _offset.Y) return;
        _offset = new Vector(x, y);
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void SetHorizontalOffset(double offset) => SetOffsets(offset, _offset.Y);
    public void SetVerticalOffset(double offset) => SetOffsets(_offset.X, offset);
    public void LineUp() => SetVerticalOffset(_offset.Y - LineDelta);
    public void LineDown() => SetVerticalOffset(_offset.Y + LineDelta);
    public void LineLeft() => SetHorizontalOffset(_offset.X - LineDelta);
    public void LineRight() => SetHorizontalOffset(_offset.X + LineDelta);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width);
    public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width);
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - LineDelta);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + LineDelta);
    public void MouseWheelLeft() => SetHorizontalOffset(_offset.X - LineDelta);
    public void MouseWheelRight() => SetHorizontalOffset(_offset.X + LineDelta);

    /// <summary>WPF xin đưa <paramref name="visual"/> vào khung nhìn (ListBox.ScrollIntoView…): chỉ cuộn khi trang chứa nó
    /// nằm HOÀN TOÀN ngoài khung nhìn (đặt đỉnh trang lên đỉnh khung nhìn). Trang đang hiện một phần thì giữ nguyên —
    /// nếu không, lúc zoom sâu (trang cao hơn màn hình) mọi yêu cầu kiểu này đều kéo về đỉnh trang. Chuyển trang chủ
    /// động (Home/End, PageDown, ô số trang) gọi thẳng <see cref="ScrollToPage"/>.</summary>
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var children = InternalChildren;
        for (int c = 0; c < children.Count; c++)
        {
            if (!ReferenceEquals(children[c], visual) && !children[c].IsAncestorOf(visual)) continue;
            int i = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(c, 0));
            var layout = Layout;
            if (i < 0 || i >= layout.Count) break;
            double top = layout.Top(i), bottom = top + layout.Height(i);
            bool intersects = bottom > _offset.Y && top < _offset.Y + _viewport.Height;
            if (!intersects) ScrollToPage(i);
            break;
        }
        return rectangle;
    }
}
