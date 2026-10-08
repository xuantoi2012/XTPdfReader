using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// A page picture to point at: drag a rectangle (an area to search) or, when <see cref="PlaceSize"/> is set, click to put a box of that size (a signature or stamp) where it should go.
/// Positions are fractions 0..1 of the displayed page, so they fit every page of the same shape. Ctrl + wheel zooms, the wheel and the scroll bars move.
/// </summary>
internal sealed class PageAreaPicker : Grid
{
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Grid _surface = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.White };
    private readonly Image _image = new() { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
    private readonly Canvas _overlay = new() { Background = Brushes.Transparent, Cursor = Cursors.Cross };
    private readonly Rectangle _box = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 5, 3 },
        Fill = new SolidColorBrush(Color.FromArgb(0x26, 0xC0, 0x39, 0x2B)), Visibility = Visibility.Collapsed, IsHitTestVisible = false
    };
    private readonly Image _placed = new() { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed, IsHitTestVisible = false, Opacity = 0.9 };
    private double _zoom = 1, _aspect = 1.414;
    private Point? _dragStart;
    private (double U1, double V1, double U2, double V2)? _area;

    public event Action? AreaChanged;

    public PageAreaPicker()
    {
        _surface.Children.Add(_image);
        _overlay.Children.Add(_box);
        _overlay.Children.Add(_placed);
        _surface.Children.Add(_overlay);
        _scroll.Content = _surface;
        Children.Add(_scroll);
        Background = new SolidColorBrush(Color.FromRgb(0xDD, 0xE3, 0xEA));
        _overlay.MouseLeftButtonDown += Overlay_Down;
        _overlay.MouseMove += Overlay_Move;
        _overlay.MouseLeftButtonUp += Overlay_Up;
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            e.Handled = true;
            SetZoom(_zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2));
        };
        SizeChanged += (_, _) => Layout();
    }

    /// <summary>The area, fractions of the page (null when none is drawn). In place mode this is the box.</summary>
    public (double U1, double V1, double U2, double V2)? Area => _area;

    /// <summary>Width and height of the box to place, as fractions of the page; null = drag any rectangle.</summary>
    public (double W, double H)? PlaceSize { get; set; }

    /// <summary>The picture shown inside the placed box (a signature).</summary>
    public ImageSource? PlaceImage { get => _placed.Source; set { _placed.Source = value; Layout(); } }

    /// <summary>Width / height of the page on screen (the first page picture decides it).</summary>
    public double PageAspect => _aspect;

    public void SetArea((double U1, double V1, double U2, double V2)? area)
    {
        _area = area;
        Layout();
    }

    public async Task ShowPageAsync(string path, int pageNumber)
    {
        var bitmap = await PdfThumbnailService.RenderPageAsync(path, pageNumber - 1, 1600, layerToken: PdfLayerStateStore.GetToken(path));
        if (bitmap == null) return;
        _image.Source = bitmap;
        _aspect = (double)bitmap.PixelHeight / Math.Max(1, bitmap.PixelWidth);
        Layout();
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.5, 8);
        Layout();
    }

    internal void Layout()
    {
        // zoom 1 = the whole page fits the box; the wheel + Ctrl zooms in
        double fit = Math.Min(Math.Max(100, ActualWidth - 24), Math.Max(100, ActualHeight - 24) / Math.Max(0.2, _aspect));
        double width = fit * _zoom;
        _surface.Width = width;
        _surface.Height = width * _aspect;
        _overlay.Width = _surface.Width;
        _overlay.Height = _surface.Height;
        if (_area is not { } a) { _box.Visibility = Visibility.Collapsed; _placed.Visibility = Visibility.Collapsed; return; }
        Canvas.SetLeft(_box, a.U1 * width);
        Canvas.SetTop(_box, a.V1 * _surface.Height);
        _box.Width = Math.Max(1, (a.U2 - a.U1) * width);
        _box.Height = Math.Max(1, (a.V2 - a.V1) * _surface.Height);
        _box.Visibility = Visibility.Visible;
        if (_placed.Source != null && PlaceSize != null)
        {
            Canvas.SetLeft(_placed, Canvas.GetLeft(_box));
            Canvas.SetTop(_placed, Canvas.GetTop(_box));
            _placed.Width = _box.Width;
            _placed.Height = _box.Height;
            _placed.Visibility = Visibility.Visible;
        }
        else _placed.Visibility = Visibility.Collapsed;
    }

    private (double U, double V) Fraction(Point p)
        => (Math.Clamp(p.X / Math.Max(1, _overlay.Width), 0, 1), Math.Clamp(p.Y / Math.Max(1, _overlay.Height), 0, 1));

    private void Overlay_Down(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_overlay);
        if (PlaceSize is { } size) { PlaceAt(p, size); return; }
        _dragStart = p;
        _overlay.CaptureMouse();
        e.Handled = true;
    }

    private void PlaceAt(Point p, (double W, double H) size)
    {
        var (u, v) = Fraction(p);
        double u1 = Math.Clamp(u - size.W / 2, 0, 1 - size.W), v1 = Math.Clamp(v - size.H / 2, 0, 1 - size.H);
        _area = (u1, v1, u1 + size.W, v1 + size.H);
        Layout();
        AreaChanged?.Invoke();
    }

    private void Overlay_Move(object sender, MouseEventArgs e)
    {
        if (PlaceSize is { } size && e.LeftButton == MouseButtonState.Pressed) { PlaceAt(e.GetPosition(_overlay), size); return; }
        if (_dragStart is not { } start) return;
        var (u1, v1) = Fraction(start);
        var (u2, v2) = Fraction(e.GetPosition(_overlay));
        _area = (Math.Min(u1, u2), Math.Min(v1, v2), Math.Max(u1, u2), Math.Max(v1, v2));
        Layout();
    }

    private void Overlay_Up(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart == null) return;
        _dragStart = null;
        _overlay.ReleaseMouseCapture();
        if (_area is { } a && (a.U2 - a.U1 < 0.004 || a.V2 - a.V1 < 0.004)) { _area = null; Layout(); }
        AreaChanged?.Invoke();
    }

    // for the tests
    internal void DrawForTest((double U1, double V1, double U2, double V2) area) { _area = area; Layout(); AreaChanged?.Invoke(); }
    internal void PlaceForTest(double u, double v)
    {
        if (PlaceSize is { } size) PlaceAt(new Point(u * Math.Max(1, _overlay.Width), v * Math.Max(1, _overlay.Height)), size);
    }
}
