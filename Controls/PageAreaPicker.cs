using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// A page picture to point at, like the page of Read sheet info: Fit / 100% / zoom buttons, Ctrl + wheel zooms around the pointer, right-drag or middle-drag pans, and the picture is drawn
/// again sharper when zoomed. Drag on the page to draw a rectangle (an area to search); drag inside it to move it, drag its squares to resize it. In <see cref="PlaceMode"/> the rectangle is a
/// stamp or signature: click puts it there, drag moves it, the corners resize it keeping its shape. Positions are fractions 0..1 of the displayed page, so they fit every page of the same size.
/// </summary>
internal sealed class PageAreaPicker : DockPanel
{
    private const double BaseWidth = 1000;
    private static readonly string[] HandleNames = { "NW", "N", "NE", "E", "SE", "S", "SW", "W" };

    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    private readonly Grid _surface = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12), Background = Brushes.White };
    private readonly Image _image = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Canvas _overlay = new() { Background = Brushes.Transparent, Cursor = Cursors.Cross };
    private readonly Rectangle _box = new() { Visibility = Visibility.Collapsed, Cursor = Cursors.SizeAll };
    private readonly Image _placed = new() { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed, IsHitTestVisible = false, Opacity = 0.9 };
    private readonly Rectangle[] _handles;
    private readonly List<Rectangle> _marks = new();
    private readonly TextBlock _zoomText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0), MinWidth = 44, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _pageText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private readonly DispatcherTimer _rerender = new() { Interval = TimeSpan.FromMilliseconds(220) };

    private string _path = "";
    private IReadOnlyList<int> _pages = Array.Empty<int>();
    private int _index;
    private double _pixelW = BaseWidth, _pixelH = BaseWidth * 1.414, _zoom = 1, _pageWidthPt = 595;
    private bool _fit = true;
    private int _renderedWidth, _version;
    private (double U1, double V1, double U2, double V2)? _area;

    private enum Drag { None, Draw, Move, Resize, Pan }
    private Drag _drag;
    private string _handle = "";
    private Point _start, _panStart;
    private (double U1, double V1, double U2, double V2) _origin;
    private double _panX, _panY;

    public event Action? AreaChanged;
    public event Action? PageChanged;

    public PageAreaPicker()
    {
        Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        _handles = HandleNames.Select(name => new Rectangle { Tag = name, Fill = Brushes.White, Visibility = Visibility.Collapsed, Cursor = HandleCursor(name) }).ToArray();
        _box.Fill = new SolidColorBrush(Color.FromArgb(0x26, 0xC0, 0x39, 0x2B));
        _box.Stroke = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
        foreach (var h in _handles) h.Stroke = _box.Stroke;
        _surface.Children.Add(_image);
        _surface.Children.Add(_overlay);
        _overlay.Children.Add(_placed);
        _overlay.Children.Add(_box);
        foreach (var h in _handles) _overlay.Children.Add(h);
        _scroll.Content = _surface;

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        bar.Children.Add(Small("Fit", "Fit the page to the window (Ctrl+0)", () => FitToWindow()));
        bar.Children.Add(Small("100%", "Actual size of the sheet", () => SetZoomAround(ActualSizeZoom(), null)));
        bar.Children.Add(Small("−", "Zoom out", () => SetZoomAround(_zoom / 1.25, null)));
        bar.Children.Add(_zoomText);
        bar.Children.Add(Small("+", "Zoom in", () => SetZoomAround(_zoom * 1.25, null)));
        bar.Children.Add(Small("◀", "Previous page", () => Step(-1)));
        bar.Children.Add(Small("▶", "Next page", () => Step(1)));
        bar.Children.Add(_pageText);
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        Children.Add(new Border { BorderBrush = R("Ui.Border"), BorderThickness = new Thickness(1), Background = R("Ui.Panel"), ClipToBounds = true, Child = _scroll });

        _overlay.MouseLeftButtonDown += Overlay_Down;
        _overlay.MouseMove += Overlay_Move;
        _overlay.MouseLeftButtonUp += Overlay_Up;
        _scroll.PreviewMouseRightButtonDown += PanStart;
        _scroll.PreviewMouseDown += (s, e) => { if (e.ChangedButton == MouseButton.Middle) PanStart(s, e); };
        _scroll.PreviewMouseMove += PanMove;
        _scroll.PreviewMouseUp += PanEnd;
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            SetZoomAround(_zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), e.GetPosition(_surface));
            e.Handled = true;
        };
        _scroll.SizeChanged += (_, _) => { if (_fit) FitToWindow(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.D0 && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { FitToWindow(); e.Handled = true; }
        };
        _rerender.Tick += async (_, _) => { _rerender.Stop(); await RenderCurrentAsync(); };
        ApplySurfaceSize();
        UpdateZoomText();
    }

    // ── public surface ───────────────────────────────────────────────

    /// <summary>The rectangle, fractions of the page (null when none). In place mode this is the stamp's place and size.</summary>
    public (double U1, double V1, double U2, double V2)? Area => _area;

    /// <summary>True: the rectangle is a stamp (click puts it, corners resize keeping the shape). False: drag any rectangle.</summary>
    public bool PlaceMode { get; set; }

    /// <summary>Width / height of the stamp on the page (used by the corners in place mode).</summary>
    public double PlaceAspect { get; set; } = 3;

    /// <summary>The picture shown inside the placed box (a signature).</summary>
    public ImageSource? PlaceImage { get => _placed.Source; set { _placed.Source = value; DrawOverlay(); } }

    public string CurrentPath => _path;
    public int CurrentPage => _index >= 0 && _index < _pages.Count ? _pages[_index] : 0;
    public double Zoom => _zoom;
    public int MarkCount => _marks.Count;

    /// <summary>Height / width of the page picture on screen.</summary>
    public double PageAspect => _pixelH / Math.Max(1, _pixelW);

    public void SetArea((double U1, double V1, double U2, double V2)? area)
    {
        _area = area;
        DrawOverlay();
    }

    /// <summary>Shows these pages (1 based) of the file, starting with <paramref name="start"/>; the arrows step through them.</summary>
    public async Task SetPagesAsync(string path, IReadOnlyList<int> pages, int? start = null)
    {
        _path = path;
        _pages = pages;
        _index = Math.Max(0, start is { } s ? pages.ToList().IndexOf(s) : 0);
        await ShowCurrentAsync();
    }

    /// <summary>True when the arrows already step through exactly these pages.</summary>
    internal bool SamePages(IReadOnlyList<int> pages) => _pages.SequenceEqual(pages);

    public Task ShowPageAsync(string path, int pageNumber) => SetPagesAsync(path, new[] { pageNumber });

    /// <summary>The width of the page in points (decides what "100%" is).</summary>
    public void SetPageSize(double widthPt) => _pageWidthPt = widthPt > 0 ? widthPt : _pageWidthPt;

    /// <summary>Green boxes over what was found on the page shown (fractions of the page).</summary>
    public void ShowMarks(IEnumerable<(double U1, double V1, double U2, double V2)> boxes)
    {
        foreach (var m in _marks) _overlay.Children.Remove(m);
        _marks.Clear();
        foreach (var b in boxes)
        {
            var mark = new Rectangle { Stroke = new SolidColorBrush(Color.FromRgb(0x0F, 0x8B, 0x6D)), Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x0F, 0x8B, 0x6D)), IsHitTestVisible = false, Tag = b };
            _marks.Add(mark);
            _overlay.Children.Insert(0, mark);
        }
        DrawOverlay();
    }

    // ── page, zoom, pan ──────────────────────────────────────────────

    private XTButton Small(string text, string tip, Action click)
    {
        var button = new XTButton { Text = text, Height = 26, MinWidth = 34, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 4, 0), ToolTip = tip };
        button.SetResourceReference(StyleProperty, "UiGhostButton");
        button.Click += (_, _) => click();
        return button;
    }

    private async void Step(int delta)
    {
        if (_pages.Count < 2) return;
        _index = Math.Clamp(_index + delta, 0, _pages.Count - 1);
        await ShowCurrentAsync();
    }

    private async Task ShowCurrentAsync()
    {
        if (CurrentPage == 0) return;
        int version = ++_version;
        _pageText.Text = _pages.Count > 1 ? $"Page {CurrentPage}  ({_index + 1} of {_pages.Count})" : $"Page {CurrentPage}";
        _renderedWidth = 0;
        var bitmap = await PdfThumbnailService.RenderPageAsync(_path, CurrentPage - 1, 1400, layerToken: PdfLayerStateStore.GetToken(_path));
        if (bitmap == null || version != _version) return;
        SetBitmap(bitmap);
        if (_fit) FitToWindow(); else ApplyZoom(_zoom);
        PageChanged?.Invoke();
    }

    private void SetBitmap(BitmapSource bitmap)
    {
        _image.Source = bitmap;
        _renderedWidth = bitmap.PixelWidth;
        _pixelW = BaseWidth;
        _pixelH = BaseWidth * bitmap.PixelHeight / Math.Max(1, bitmap.PixelWidth);
        ApplySurfaceSize();
    }

    private void ApplySurfaceSize()
    {
        _image.Width = _overlay.Width = _surface.Width = _pixelW;
        _image.Height = _overlay.Height = _surface.Height = _pixelH;
    }

    private async Task RenderCurrentAsync()
    {
        if (CurrentPage == 0) return;
        int version = _version;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int wanted = (int)Math.Clamp(BaseWidth * _zoom * dpi, 800, 5200);
        if (wanted <= _renderedWidth * 1.15 && wanted >= _renderedWidth * 0.5) return; // sharp enough
        var bitmap = await PdfThumbnailService.RenderPageAsync(_path, CurrentPage - 1, wanted, layerToken: PdfLayerStateStore.GetToken(_path));
        if (bitmap == null || version != _version) return;
        _image.Source = bitmap;
        _renderedWidth = bitmap.PixelWidth;
    }

    private double ActualSizeZoom() => _pageWidthPt * 96.0 / 72.0 / BaseWidth;

    public void FitToWindow()
    {
        _fit = true;
        double w = Math.Max(50, _scroll.ActualWidth - 44), h = Math.Max(50, _scroll.ActualHeight - 44);
        ApplyZoom(Math.Min(w / _pixelW, h / _pixelH));
    }

    public void SetZoomAround(double zoom, Point? anchorInSurface)
    {
        _fit = false;
        zoom = Math.Clamp(zoom, 0.1, 10);
        Point? before = anchorInSurface is { } a ? _surface.TranslatePoint(a, _scroll) : null;
        ApplyZoom(zoom);
        if (anchorInSurface is { } point && before is { } was)
        {
            _scroll.UpdateLayout();
            var after = _surface.TranslatePoint(point, _scroll);
            _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset + after.X - was.X);
            _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + after.Y - was.Y);
        }
    }

    private void ApplyZoom(double zoom)
    {
        _zoom = Math.Max(0.05, zoom);
        _surface.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        UpdateZoomText();
        DrawOverlay(); // lines and squares keep their size on screen
        _rerender.Stop();
        _rerender.Start();
    }

    private void UpdateZoomText() => _zoomText.Text = Math.Round(_zoom / Math.Max(0.01, ActualSizeZoom()) * 100) + "%";

    private void PanStart(object sender, MouseEventArgs e)
    {
        _drag = Drag.Pan;
        _panStart = e.GetPosition(_scroll);
        _panX = _scroll.HorizontalOffset;
        _panY = _scroll.VerticalOffset;
        _scroll.CaptureMouse();
        _scroll.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void PanMove(object sender, MouseEventArgs e)
    {
        if (_drag != Drag.Pan) return;
        var now = e.GetPosition(_scroll);
        _scroll.ScrollToHorizontalOffset(_panX - (now.X - _panStart.X));
        _scroll.ScrollToVerticalOffset(_panY - (now.Y - _panStart.Y));
    }

    private void PanEnd(object sender, MouseButtonEventArgs e)
    {
        if (_drag != Drag.Pan || e.ChangedButton == MouseButton.Left) return;
        _drag = Drag.None;
        _scroll.ReleaseMouseCapture();
        _scroll.Cursor = null;
    }

    // ── the rectangle: draw, move, resize ────────────────────────────

    private static Cursor HandleCursor(string name) => name switch
    {
        "NW" or "SE" => Cursors.SizeNWSE,
        "NE" or "SW" => Cursors.SizeNESW,
        "N" or "S" => Cursors.SizeNS,
        _ => Cursors.SizeWE
    };

    private (double U, double V) Fraction(Point p) => (Math.Clamp(p.X / _pixelW, 0, 1), Math.Clamp(p.Y / _pixelH, 0, 1));

    private void DrawOverlay()
    {
        double thin = 1.5 / _zoom, square = 9 / _zoom;
        foreach (var mark in _marks)
        {
            var (u1, v1, u2, v2) = ((double, double, double, double))mark.Tag;
            Canvas.SetLeft(mark, u1 * _pixelW);
            Canvas.SetTop(mark, v1 * _pixelH);
            mark.Width = Math.Max(3, (u2 - u1) * _pixelW);
            mark.Height = Math.Max(3, (v2 - v1) * _pixelH);
            mark.StrokeThickness = thin;
        }
        if (_area is not { } a)
        {
            _box.Visibility = _placed.Visibility = Visibility.Collapsed;
            foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
            return;
        }
        double x = a.U1 * _pixelW, y = a.V1 * _pixelH, w = Math.Max(1, (a.U2 - a.U1) * _pixelW), h2 = Math.Max(1, (a.V2 - a.V1) * _pixelH);
        _box.StrokeThickness = thin;
        _box.StrokeDashArray = new DoubleCollection { 5, 3 };
        Canvas.SetLeft(_box, x);
        Canvas.SetTop(_box, y);
        _box.Width = w;
        _box.Height = h2;
        _box.Visibility = Visibility.Visible;
        if (_placed.Source != null && PlaceMode)
        {
            Canvas.SetLeft(_placed, x);
            Canvas.SetTop(_placed, y);
            _placed.Width = w;
            _placed.Height = h2;
            _placed.Visibility = Visibility.Visible;
        }
        else _placed.Visibility = Visibility.Collapsed;
        double midX = x + w / 2, midY = y + h2 / 2;
        var centres = new Dictionary<string, (double X, double Y)>
        {
            ["NW"] = (x, y), ["N"] = (midX, y), ["NE"] = (x + w, y), ["E"] = (x + w, midY),
            ["SE"] = (x + w, y + h2), ["S"] = (midX, y + h2), ["SW"] = (x, y + h2), ["W"] = (x, midY)
        };
        foreach (var handle in _handles)
        {
            var c = centres[(string)handle.Tag];
            handle.Width = handle.Height = square;
            handle.StrokeThickness = thin;
            Canvas.SetLeft(handle, c.X - square / 2);
            Canvas.SetTop(handle, c.Y - square / 2);
            handle.Visibility = Visibility.Visible;
        }
    }

    private void Overlay_Down(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_overlay);
        _start = p;
        if (e.OriginalSource is Rectangle { Tag: string name } && HandleNames.Contains(name) && _area is { } current)
        {
            _drag = Drag.Resize;
            _handle = name;
            _origin = current;
        }
        else if (_area is { } a && p.X >= a.U1 * _pixelW && p.X <= a.U2 * _pixelW && p.Y >= a.V1 * _pixelH && p.Y <= a.V2 * _pixelH)
        {
            _drag = Drag.Move;
            _origin = a;
        }
        else if (PlaceMode && _area is { } placed)
        {
            // a click elsewhere puts the stamp there (its centre under the pointer) and the drag goes on moving it
            var (u, v) = Fraction(p);
            double w = placed.U2 - placed.U1, h = placed.V2 - placed.V1;
            double u1 = Math.Clamp(u - w / 2, 0, 1 - w), v1 = Math.Clamp(v - h / 2, 0, 1 - h);
            _origin = (u1, v1, u1 + w, v1 + h);
            _area = _origin;
            _drag = Drag.Move;
            DrawOverlay();
            AreaChanged?.Invoke();
        }
        else
        {
            _drag = Drag.Draw;
            var (u, v) = Fraction(p);
            _area = (u, v, u, v);
        }
        _overlay.CaptureMouse();
        e.Handled = true;
    }

    private void Overlay_Move(object sender, MouseEventArgs e)
    {
        if (_drag is not (Drag.Draw or Drag.Move or Drag.Resize) || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_overlay);
        var (cu, cv) = Fraction(p);
        switch (_drag)
        {
            case Drag.Draw:
            {
                var (su, sv) = Fraction(_start);
                _area = (Math.Min(su, cu), Math.Min(sv, cv), Math.Max(su, cu), Math.Max(sv, cv));
                break;
            }
            case Drag.Move:
            {
                double du = (p.X - _start.X) / _pixelW, dv = (p.Y - _start.Y) / _pixelH;
                double w = _origin.U2 - _origin.U1, h = _origin.V2 - _origin.V1;
                double u1 = Math.Clamp(_origin.U1 + du, 0, 1 - w), v1 = Math.Clamp(_origin.V1 + dv, 0, 1 - h);
                _area = (u1, v1, u1 + w, v1 + h);
                break;
            }
            case Drag.Resize:
                _area = Resized(_origin, _handle, cu, cv);
                break;
        }
        DrawOverlay();
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
            double height = (u2 - u1) * _pixelW / Math.Max(0.05, PlaceAspect) / _pixelH;
            if (handle.Contains('N')) v1 = v2 - height; else v2 = v1 + height;
            if (v1 < 0 || v2 > 1) return o; // it would leave the page
        }
        return (u1, v1, u2, v2);
    }

    private void Overlay_Up(object sender, MouseButtonEventArgs e)
    {
        if (_drag is not (Drag.Draw or Drag.Move or Drag.Resize)) return;
        _drag = Drag.None;
        _overlay.ReleaseMouseCapture();
        if (!PlaceMode && _area is { } a && (a.U2 - a.U1 < 0.004 || a.V2 - a.V1 < 0.004)) _area = null;
        DrawOverlay();
        AreaChanged?.Invoke();
    }

    // ── for the tests ────────────────────────────────────────────────

    internal void DrawForTest((double U1, double V1, double U2, double V2) area) { _area = area; DrawOverlay(); AreaChanged?.Invoke(); }

    /// <summary>Puts the stamp's centre at (u, v), keeping its size.</summary>
    internal void PlaceForTest(double u, double v)
    {
        if (_area is not { } a) return;
        double w = a.U2 - a.U1, h = a.V2 - a.V1;
        double u1 = Math.Clamp(u - w / 2, 0, 1 - w), v1 = Math.Clamp(v - h / 2, 0, 1 - h);
        _area = (u1, v1, u1 + w, v1 + h);
        DrawOverlay();
        AreaChanged?.Invoke();
    }

    /// <summary>Drags a square of the rectangle to (u, v), as the mouse would.</summary>
    internal void ResizeForTest(string handle, double u, double v)
    {
        if (_area is not { } a) return;
        _area = Resized(a, handle, u, v);
        DrawOverlay();
        AreaChanged?.Invoke();
    }
}
