using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp.Controls;

/// <summary>What the reader window lends to <see cref="ReaderAreaSurface"/>: its pages and the maths between the screen and a page.</summary>
internal sealed record ReaderAreaHost(
    Func<PageRow?> CurrentRow,
    Func<PageRow, double, double, Point?> PageToLayer,
    Func<PageRow, Point, (double U, double V)?> HostToPage,
    Func<Point, (PageRow Row, double U, double V)?> HitPage,
    Func<string, IReadOnlyList<int>, int?, Task> Navigate,
    Action<Cursor?> SetCursor);

/// <summary>
/// The rectangle of a batch tool drawn straight on the page the user is reading (instead of a second picture in a window). Drag on the page to draw,
/// drag inside to move, drag a square to resize; in <see cref="PlaceMode"/> a click puts the stamp there and a corner keeps its shape. The reader's
/// own zoom, scroll and page changes carry it along; the tool panel reads and sets <see cref="Area"/> in fractions of the page.
/// </summary>
internal sealed class ReaderAreaSurface : IAreaSurface
{
    private static readonly string[] HandleNames = { "NW", "N", "NE", "E", "SE", "S", "SW", "W" };
    private static readonly Color Red = Color.FromRgb(0xC0, 0x39, 0x2B);

    private readonly Canvas _layer;
    private readonly ReaderAreaHost _host;
    private readonly Rectangle _box = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false, StrokeDashArray = new DoubleCollection { 5, 3 }, StrokeThickness = 1.5 };
    private readonly Image _placed = new() { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed, IsHitTestVisible = false, Opacity = 0.9 };
    private readonly Rectangle[] _handles;
    private readonly List<Rectangle> _marks = new();
    private IReadOnlyList<(double U1, double V1, double U2, double V2)> _markBoxes = Array.Empty<(double, double, double, double)>();
    private (double U1, double V1, double U2, double V2)? _area;
    private PageRow? _row;
    private bool _active;

    private enum Drag { None, Draw, Move, Resize }
    private Drag _drag;
    private string _handle = "";
    private (double U, double V) _start;
    private (double U1, double V1, double U2, double V2) _origin;

    public event Action? AreaChanged;
    public event Action? PageChanged;

    public ReaderAreaSurface(Canvas layer, ReaderAreaHost host)
    {
        _layer = layer;
        _host = host;
        _box.Fill = new SolidColorBrush(Color.FromArgb(0x26, Red.R, Red.G, Red.B));
        _box.Stroke = new SolidColorBrush(Red);
        _handles = HandleNames.Select(name => new Rectangle { Tag = name, Width = 9, Height = 9, Fill = Brushes.White, Stroke = _box.Stroke, StrokeThickness = 1.5, Visibility = Visibility.Collapsed, IsHitTestVisible = false }).ToArray();
        foreach (var element in new UIElement[] { _placed, _box }.Concat(_handles)) { Panel.SetZIndex(element, 6); _layer.Children.Add(element); }
    }

    // ── IAreaSurface ─────────────────────────────────────────────────

    public (double U1, double V1, double U2, double V2)? Area => _area;
    public bool PlaceMode { get; set; }
    public double PlaceAspect { get; set; } = 3;
    public ImageSource? PlaceImage { get => _placed.Source; set { _placed.Source = value; Refresh(); } }
    public string CurrentPath => _row?.SourcePath ?? "";
    public int CurrentPage => _row?.PageNumber ?? 0;

    public double PageAspect
    {
        get
        {
            if (_row is { } row && _host.PageToLayer(row, 0, 0) is { } a && _host.PageToLayer(row, 1, 1) is { } b && b.X - a.X > 1) return (b.Y - a.Y) / (b.X - a.X);
            return 1.414;
        }
    }

    public void SetPageSize(double widthPt) { }

    public void SetArea((double U1, double V1, double U2, double V2)? area)
    {
        _area = area;
        Refresh();
    }

    public async Task SetPagesAsync(string path, IReadOnlyList<int> pages, int? start = null)
    {
        if (_row is { } row && string.Equals(row.SourcePath, path, StringComparison.OrdinalIgnoreCase) && pages.Contains(row.PageNumber)) { Refresh(); return; }
        await _host.Navigate(path, pages, start);
        FollowReader(raise: false);
    }

    public bool SamePages(IReadOnlyList<int> pages) => _row is { } row && pages.Contains(row.PageNumber);

    public void ShowMarks(IEnumerable<(double U1, double V1, double U2, double V2)> boxes)
    {
        _markBoxes = boxes.ToList();
        Refresh();
    }

    // ── on and off, following the reader ─────────────────────────────

    public bool Active => _active;

    /// <summary>The file the tool works on: pages of other files (a merged document) show no rectangle.</summary>
    public string? BoundPath { get; set; }

    public void Activate()
    {
        _active = true;
        FollowReader(raise: false);
        _host.SetCursor(Cursors.Cross);
    }

    public void Deactivate()
    {
        _active = false;
        _drag = Drag.None;
        _area = null;
        _markBoxes = Array.Empty<(double, double, double, double)>();
        _row = null;
        Refresh();
        _host.SetCursor(null);
    }

    /// <summary>The reader now shows another page as its current one: the rectangle moves there and the tool may switch to that paper size.</summary>
    public void OnReaderPageChanged()
    {
        if (_active) FollowReader();
    }

    private void FollowReader(bool raise = true)
    {
        var current = _host.CurrentRow();
        if (BoundPath != null && current != null && !string.Equals(current.SourcePath, BoundPath, StringComparison.OrdinalIgnoreCase)) current = null;
        bool changed = current?.SourcePath != _row?.SourcePath || current?.PageNumber != _row?.PageNumber;
        _row = current;
        Refresh();
        if (changed && raise) PageChanged?.Invoke();
    }

    /// <summary>Puts the shapes where the page is on screen now (called on every layout change of the page view).</summary>
    public void Refresh()
    {
        foreach (var mark in _marks) _layer.Children.Remove(mark);
        _marks.Clear();
        if (!_active || _row is not { } row) { HideShapes(); return; }
        foreach (var box in _markBoxes)
        {
            if (Corners(row, box) is not { } r) continue;
            var mark = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x0F, 0x8B, 0x6D)), Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x0F, 0x8B, 0x6D)),
                IsHitTestVisible = false, StrokeThickness = 1.5, Width = Math.Max(3, r.Width), Height = Math.Max(3, r.Height)
            };
            Canvas.SetLeft(mark, r.X); Canvas.SetTop(mark, r.Y);
            Panel.SetZIndex(mark, 5);
            _marks.Add(mark);
            _layer.Children.Add(mark);
        }
        if (_area is not { } a || Corners(row, a) is not { } rect) { HideShapes(); return; }
        Canvas.SetLeft(_box, rect.X); Canvas.SetTop(_box, rect.Y);
        _box.Width = Math.Max(1, rect.Width); _box.Height = Math.Max(1, rect.Height);
        _box.Visibility = Visibility.Visible;
        if (_placed.Source != null && PlaceMode)
        {
            Canvas.SetLeft(_placed, rect.X); Canvas.SetTop(_placed, rect.Y);
            _placed.Width = _box.Width; _placed.Height = _box.Height;
            _placed.Visibility = Visibility.Visible;
        }
        else _placed.Visibility = Visibility.Collapsed;
        foreach (var handle in _handles)
        {
            var c = HandleCentre(rect, (string)handle.Tag);
            Canvas.SetLeft(handle, c.X - handle.Width / 2); Canvas.SetTop(handle, c.Y - handle.Height / 2);
            handle.Visibility = Visibility.Visible;
        }
    }

    private void HideShapes()
    {
        _box.Visibility = _placed.Visibility = Visibility.Collapsed;
        foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
    }

    private Rect? Corners(PageRow row, (double U1, double V1, double U2, double V2) a)
    {
        if (_host.PageToLayer(row, a.U1, a.V1) is not { } p1 || _host.PageToLayer(row, a.U2, a.V2) is not { } p2) return null;
        return new Rect(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), Math.Abs(p2.X - p1.X), Math.Abs(p2.Y - p1.Y));
    }

    private static Point HandleCentre(Rect r, string name) => name switch
    {
        "NW" => new Point(r.Left, r.Top), "N" => new Point(r.Left + r.Width / 2, r.Top), "NE" => new Point(r.Right, r.Top),
        "E" => new Point(r.Right, r.Top + r.Height / 2), "SE" => new Point(r.Right, r.Bottom), "S" => new Point(r.Left + r.Width / 2, r.Bottom),
        "SW" => new Point(r.Left, r.Bottom), _ => new Point(r.Left, r.Top + r.Height / 2)
    };

    private static Cursor HandleCursor(string name) => name switch
    {
        "NW" or "SE" => Cursors.SizeNWSE, "NE" or "SW" => Cursors.SizeNESW, "N" or "S" => Cursors.SizeNS, _ => Cursors.SizeWE
    };

    // ── the mouse (called by the reader's content host) ──────────────

    private string? HandleAt(Point p)
    {
        if (_row is not { } row || _area is not { } a || Corners(row, a) is not { } rect) return null;
        foreach (var name in HandleNames)
        {
            var c = HandleCentre(rect, name);
            if (Math.Abs(p.X - c.X) <= 6 && Math.Abs(p.Y - c.Y) <= 6) return name;
        }
        return null;
    }

    private bool InsideBox(Point p) => _row is { } row && _area is { } a && Corners(row, a) is { } rect && rect.Contains(p);

    /// <summary>Left button down on the page area. True when the surface took it (the reader must not also act on it).</summary>
    public bool OnDown(Point p)
    {
        if (!_active || _row is not { } row) return false;
        if (HandleAt(p) is { } name && _area is { } current)
        {
            _drag = Drag.Resize; _handle = name; _origin = current;
        }
        else if (InsideBox(p) && _host.HostToPage(row, p) is { } inside && _area is { } a)
        {
            _drag = Drag.Move; _origin = a; _start = inside;
        }
        else if (_host.HitPage(p) is { } hit && (BoundPath == null || string.Equals(hit.Row.SourcePath, BoundPath, StringComparison.OrdinalIgnoreCase)))
        {
            if (hit.Row.SourcePath != row.SourcePath || hit.Row.PageNumber != row.PageNumber)
            {
                _row = hit.Row;
                PageChanged?.Invoke(); // the tool may pick the paper size of that page (and so its own area)
            }
            if (PlaceMode && _area is { } placed)
            {
                double w = placed.U2 - placed.U1, h = placed.V2 - placed.V1;
                double u1 = Math.Clamp(hit.U - w / 2, 0, 1 - w), v1 = Math.Clamp(hit.V - h / 2, 0, 1 - h);
                _origin = (u1, v1, u1 + w, v1 + h);
                _area = _origin;
                _drag = Drag.Move; _start = (hit.U, hit.V);
                Refresh();
                AreaChanged?.Invoke();
            }
            else if (!PlaceMode)
            {
                _drag = Drag.Draw; _start = (hit.U, hit.V);
                _area = (hit.U, hit.V, hit.U, hit.V);
                Refresh();
            }
            else return false;
        }
        else return false;
        return true;
    }

    public bool OnMove(Point p)
    {
        if (!_active) return false;
        if (_drag == Drag.None)
        {
            _host.SetCursor(HandleAt(p) is { } h ? HandleCursor(h) : InsideBox(p) ? Cursors.SizeAll : Cursors.Cross);
            return false;
        }
        if (_row is not { } row || _host.HostToPage(row, p) is not { } at) return true;
        double cu = Math.Clamp(at.U, 0, 1), cv = Math.Clamp(at.V, 0, 1);
        switch (_drag)
        {
            case Drag.Draw:
                _area = (Math.Min(_start.U, cu), Math.Min(_start.V, cv), Math.Max(_start.U, cu), Math.Max(_start.V, cv));
                break;
            case Drag.Move:
            {
                double w = _origin.U2 - _origin.U1, h = _origin.V2 - _origin.V1;
                double u1 = Math.Clamp(_origin.U1 + at.U - _start.U, 0, 1 - w), v1 = Math.Clamp(_origin.V1 + at.V - _start.V, 0, 1 - h);
                _area = (u1, v1, u1 + w, v1 + h);
                break;
            }
            case Drag.Resize:
                _area = Resized(_origin, _handle, cu, cv);
                break;
        }
        Refresh();
        return true;
    }

    public bool OnUp()
    {
        if (_drag == Drag.None) return false;
        _drag = Drag.None;
        if (!PlaceMode && _area is { } a && (a.U2 - a.U1 < 0.004 || a.V2 - a.V1 < 0.004)) _area = null;
        Refresh();
        AreaChanged?.Invoke();
        return true;
    }

    /// <summary>The rectangle after a square is dragged to (u, v); in place mode a corner keeps the shape of the stamp.</summary>
    private (double U1, double V1, double U2, double V2) Resized((double U1, double V1, double U2, double V2) o, string handle, double u, double v)
    {
        const double min = 0.006;
        double u1 = o.U1, v1 = o.V1, u2 = o.U2, v2 = o.V2;
        if (handle.Contains('W')) u1 = Math.Min(u, u2 - min);
        if (handle.Contains('E')) u2 = Math.Max(u, u1 + min);
        if (handle.Contains('N')) v1 = Math.Min(v, v2 - min);
        if (handle.Contains('S')) v2 = Math.Max(v, v1 + min);
        if (PlaceMode && handle.Length == 2)
        {
            double height = (u2 - u1) / Math.Max(0.05, PlaceAspect) / PageAspect;
            if (handle.Contains('N')) v1 = v2 - height; else v2 = v1 + height;
            if (v1 < 0 || v2 > 1) return o; // it would leave the page
        }
        return (u1, v1, u2, v2);
    }
}
