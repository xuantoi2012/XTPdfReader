using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Services.Capture;

namespace XTPdfMergeApp.Controls
{
    internal enum CapturePick { Window, Rectangle, Ellipse, Polygon }
    internal enum CaptureAction { Copy, Edit }
    internal sealed record CaptureOutcome(CaptureAction Action, BitmapSource Image);

    /// <summary>
    /// The screen frozen and dimmed (like Zalo's capture): hover a window to see its dashed frame and click to take it, or pick a rectangle
    /// (drag, or click two corners), an ellipse or a polygon. Enter copies, "Edit" opens the picture in the Reader. Esc / right-click leaves.
    /// All drawing is in snapshot pixels: the canvas is scaled by 1 / <c>scale</c> so one canvas unit is one screen pixel.
    /// </summary>
    internal sealed class CaptureOverlayWindow : Window
    {
        private enum Phase { Idle, Dragging, FirstCorner, Polygon, Selected }

        private readonly ScreenSnapshot _snapshot;
        private readonly double _scale;
        private readonly List<CaptureWindow> _windows;
        private readonly Func<CaptureWindow, BitmapSource?> _grabWindow;

        private readonly Canvas _canvas;
        private readonly Canvas _chrome = new();
        private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x78, 0, 0, 0)), IsHitTestVisible = false };
        private readonly Path _outlineHalo = new() { Stroke = Brushes.White, StrokeThickness = 3, IsHitTestVisible = false };
        private readonly Path _outline = new() { Stroke = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false };
        private readonly Polyline _polyline = new() { Stroke = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), StrokeThickness = 2, IsHitTestVisible = false };
        private readonly Border _sizeTag = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1F, 0x29, 0x37)), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 12 };
        private readonly Border _modeBar = new();
        private readonly Border _actionBar = new() { Visibility = Visibility.Collapsed };
        private readonly Dictionary<CapturePick, Border> _modeButtons = new();
        private Border? _outsideButton;
        private TextBlock? _outsideText;

        private CapturePick _mode = CapturePick.Window;
        private Phase _phase = Phase.Idle;
        private Point _start, _cursor;
        private readonly List<Point> _vertices = new();
        private CaptureRegion? _region;
        private CaptureWindow? _pickedWindow;
        private int _cycle;
        private bool _transparentOutside = AppSettings.CaptureTransparentOutside;

        public CaptureOutcome? Outcome { get; private set; }

        /// <summary>The region that is currently picked (tests read it).</summary>
        internal CaptureRegion? Region => _region;
        internal CaptureWindow? HoveredWindow { get; private set; }
        internal CapturePick Mode => _mode;
        internal bool ActionBarVisible => _actionBar.Visibility == Visibility.Visible;

        public CaptureOverlayWindow(ScreenSnapshot snapshot, double scale, bool atScreen = true, Func<CaptureWindow, BitmapSource?>? grabWindow = null)
        {
            _snapshot = snapshot;
            _scale = scale <= 0 ? 1 : scale;
            _grabWindow = grabWindow ?? ScreenGrabber.GrabWindow;
            // Window frames are in screen pixels; the picture starts at the virtual screen's origin.
            _windows = snapshot.Windows.Select(w => w with { Bounds = new Int32Rect(w.Bounds.X - (int)snapshot.Origin.X, w.Bounds.Y - (int)snapshot.Origin.Y, w.Bounds.Width, w.Bounds.Height) }).ToList();

            int pixelWidth = snapshot.Image.PixelWidth, pixelHeight = snapshot.Image.PixelHeight;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = atScreen;
            AllowsTransparency = false;
            Background = Brushes.Black;
            Cursor = Cursors.Cross;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = atScreen ? snapshot.Origin.X / _scale : -32000;
            Top = atScreen ? snapshot.Origin.Y / _scale : -32000;
            Width = pixelWidth / _scale;
            Height = pixelHeight / _scale;
            ShowActivated = true;
            Focusable = true;

            _canvas = new Canvas { Width = pixelWidth, Height = pixelHeight, LayoutTransform = new ScaleTransform(1 / _scale, 1 / _scale), Background = Brushes.Transparent, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(_canvas, BitmapScalingMode.NearestNeighbor);
            _canvas.Children.Add(new Image { Source = snapshot.Image, Width = pixelWidth, Height = pixelHeight, Stretch = Stretch.Fill, IsHitTestVisible = false });
            _canvas.Children.Add(_dim);
            _canvas.Children.Add(_outlineHalo);
            _canvas.Children.Add(_outline);
            _canvas.Children.Add(_polyline);
            var grid = new Grid { Background = Brushes.Black };
            grid.Children.Add(_canvas);
            _chrome.IsHitTestVisible = true;
            _chrome.Background = null;
            grid.Children.Add(_chrome);
            Content = grid;

            _sizeTag.Child = _sizeText;
            _chrome.Children.Add(_sizeTag);
            BuildModeBar();
            BuildActionBar();
            UpdateDim();

            _canvas.MouseMove += (_, e) => OnPointerMove(e.GetPosition(_canvas));
            _canvas.MouseLeftButtonDown += (_, e) => { OnPointerDown(e.GetPosition(_canvas), e.ClickCount); e.Handled = true; };
            _canvas.MouseLeftButtonUp += (_, e) => { OnPointerUp(e.GetPosition(_canvas)); e.Handled = true; };
            _canvas.MouseRightButtonDown += (_, e) => { OnCancel(); e.Handled = true; };
            _canvas.MouseWheel += (_, e) => { if (_mode == CapturePick.Window && _phase != Phase.Selected) CycleWindow(e.Delta < 0 ? 1 : -1); e.Handled = true; };
            PreviewKeyDown += (_, e) => { if (OnKey(e.Key)) e.Handled = true; };
            SourceInitialized += (_, _) =>
            {
                if (!atScreen) return;
                // exact pixels, whatever rounding the DIP position went through
                var handle = new WindowInteropHelper(this).Handle;
                SetWindowPos(handle, new IntPtr(-1), (int)snapshot.Origin.X, (int)snapshot.Origin.Y, pixelWidth, pixelHeight, 0x0010 /* NOACTIVATE */ | 0x0040 /* SHOWWINDOW */);
            };
            Loaded += (_, _) => { Activate(); Focus(); Keyboard.Focus(this); };
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        // ── Toolbars ────────────────────────────────────────────────────

        private static Brush Themed(string key, Brush fallback) => Application.Current?.TryFindResource(key) as Brush ?? fallback;

        private Border MakeButton(string text, string tip, Action click, bool accent = false)
        {
            var label = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = accent ? Brushes.White : Themed("Ui.Text", Brushes.Black) };
            var border = new Border
            {
                Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(2), CornerRadius = new CornerRadius(5), Cursor = Cursors.Hand, ToolTip = tip, Child = label,
                Background = accent ? new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)) : Brushes.Transparent
            };
            border.MouseEnter += (_, _) => { if (!accent && border.Tag as string != "on") border.Background = Themed("Ui.Hover", new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB))); };
            border.MouseLeave += (_, _) => { if (!accent && border.Tag as string != "on") border.Background = Brushes.Transparent; };
            border.MouseLeftButtonDown += (_, e) => e.Handled = true;
            border.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
            return border;
        }

        private Border BarFrame(StackPanel content) => new()
        {
            Background = Themed("Ui.Surface", Brushes.White), BorderBrush = Themed("Ui.Border", Brushes.LightGray), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(4), Child = content,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.3, Direction = 270 }
        };

        private void BuildModeBar()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (mode, name, key) in new[] { (CapturePick.Window, "Window", "W"), (CapturePick.Rectangle, "Rectangle", "R"), (CapturePick.Ellipse, "Circle", "E"), (CapturePick.Polygon, "Polygon", "P") })
            {
                var captured = mode;
                var button = MakeButton(name, name + " (" + key + ")", () => SetMode(captured));
                _modeButtons[mode] = button;
                row.Children.Add(button);
            }
            row.Children.Add(MakeButton("✕", "Cancel (Esc)", Cancel));
            var frame = BarFrame(row);
            _modeBar.Child = frame;
            var primary = ScreenGrabber.PrimaryScreen;
            _modeBar.Loaded += (_, _) => PlaceModeBar(primary);
            _modeBar.SizeChanged += (_, _) => PlaceModeBar(primary);
            _chrome.Children.Add(_modeBar);
            HighlightMode();
        }

        private void PlaceModeBar(Int32Rect primary)
        {
            // top centre of the primary monitor (in DIPs of this window)
            double centre = (primary.X + primary.Width / 2.0 - _snapshot.Origin.X) / _scale;
            Canvas.SetLeft(_modeBar, Math.Max(0, centre - _modeBar.ActualWidth / 2));
            Canvas.SetTop(_modeBar, 12 + (primary.Y - _snapshot.Origin.Y) / _scale);
        }

        private void BuildActionBar()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(MakeButton("Copy  ↵", "Copy the picture (Enter)", () => Finish(CaptureAction.Copy), accent: true));
            row.Children.Add(MakeButton("Edit", "Save to the Captures and open it to add text and comments", () => Finish(CaptureAction.Edit)));
            _outsideButton = MakeButton("", "Outside the shape: white or transparent", () =>
            {
                _transparentOutside = !_transparentOutside;
                AppSettings.CaptureTransparentOutside = _transparentOutside;
                RefreshOutsideLabel();
            });
            _outsideText = (TextBlock)_outsideButton.Child;
            row.Children.Add(_outsideButton);
            row.Children.Add(MakeButton("✕", "Cancel (Esc)", Cancel));
            _actionBar.Child = BarFrame(row);
            _chrome.Children.Add(_actionBar);
            RefreshOutsideLabel();
        }

        private void RefreshOutsideLabel()
        {
            if (_outsideText != null) _outsideText.Text = _transparentOutside ? "Outside: clear" : "Outside: white";
        }

        private void HighlightMode()
        {
            foreach (var (mode, button) in _modeButtons)
            {
                bool on = mode == _mode;
                button.Tag = on ? "on" : null;
                button.Background = on ? Themed("Ui.Hover", new SolidColorBrush(Color.FromRgb(0xDB, 0xEA, 0xFE))) : Brushes.Transparent;
                if (button.Child is TextBlock text) text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        // ── Input (public for the tests: they drive the overlay without a real mouse) ──

        internal void SetMode(CapturePick mode)
        {
            _mode = mode;
            Reset();
            HighlightMode();
        }

        private void Reset()
        {
            _phase = Phase.Idle;
            _region = null;
            _pickedWindow = null;
            _vertices.Clear();
            _polyline.Points.Clear();
            _actionBar.Visibility = Visibility.Collapsed;
            _sizeTag.Visibility = Visibility.Collapsed;
            HoveredWindow = null;
            _outline.Data = _outlineHalo.Data = null;
            UpdateDim();
            if (_mode == CapturePick.Window) UpdateHover(_cursor);
        }

        private void Cancel()
        {
            Outcome = null;
            try { DialogResult = false; } catch (InvalidOperationException) { Close(); }
        }

        private void OnCancel()
        {
            if (_phase == Phase.Idle) Cancel(); else Reset();
        }

        internal bool OnKey(Key key)
        {
            switch (key)
            {
                case Key.Escape: OnCancel(); return true;
                case Key.Enter when _phase == Phase.Selected: Finish(CaptureAction.Copy); return true;
                case Key.Enter when _phase == Phase.Polygon: ClosePolygon(); return true;
                case Key.Back when _phase == Phase.Polygon:
                    if (_vertices.Count > 0) _vertices.RemoveAt(_vertices.Count - 1);
                    if (_vertices.Count == 0) _phase = Phase.Idle;
                    UpdatePolygonVisual();
                    return true;
                case Key.Tab when _mode == CapturePick.Window && _phase != Phase.Selected: CycleWindow(1); return true;
                case Key.W when _phase is Phase.Idle or Phase.Selected: SetMode(CapturePick.Window); return true;
                case Key.R when _phase is Phase.Idle or Phase.Selected: SetMode(CapturePick.Rectangle); return true;
                case Key.E when _phase is Phase.Idle or Phase.Selected: SetMode(CapturePick.Ellipse); return true;
                case Key.P when _phase is Phase.Idle or Phase.Selected: SetMode(CapturePick.Polygon); return true;
            }
            return false;
        }

        internal void OnPointerMove(Point p)
        {
            _cursor = p;
            switch (_phase)
            {
                case Phase.Idle when _mode == CapturePick.Window:
                    UpdateHover(p);
                    break;
                case Phase.Dragging:
                case Phase.FirstCorner:
                    SetRegion(_mode == CapturePick.Ellipse ? CaptureRegion.Ellipse(_start, p) : CaptureRegion.Rectangle(_start, p), showBar: false);
                    break;
                case Phase.Polygon:
                    UpdatePolygonVisual();
                    break;
            }
        }

        internal void OnPointerDown(Point p, int clickCount = 1)
        {
            _cursor = p;
            if (_phase == Phase.Selected) { Reset(); }
            switch (_mode)
            {
                case CapturePick.Window:
                    UpdateHover(p);
                    if (HoveredWindow is { } window)
                    {
                        _pickedWindow = window;
                        SetRegion(CaptureRegion.Rectangle(new Point(window.Bounds.X, window.Bounds.Y), new Point(window.Bounds.X + window.Bounds.Width, window.Bounds.Y + window.Bounds.Height)), showBar: true);
                    }
                    break;
                case CapturePick.Rectangle:
                case CapturePick.Ellipse:
                    if (_phase == Phase.FirstCorner)
                    {
                        var region = _mode == CapturePick.Ellipse ? CaptureRegion.Ellipse(_start, p) : CaptureRegion.Rectangle(_start, p);
                        if (region.IsUsable) SetRegion(region, showBar: true); else Reset();
                    }
                    else { _start = p; _phase = Phase.Dragging; }
                    break;
                case CapturePick.Polygon:
                    if (_phase == Phase.Polygon && _vertices.Count >= 3 && (clickCount >= 2 || Distance(p, _vertices[0]) <= 10)) { ClosePolygon(); break; }
                    _vertices.Add(p);
                    _phase = Phase.Polygon;
                    UpdatePolygonVisual();
                    break;
            }
        }

        internal void OnPointerUp(Point p)
        {
            _cursor = p;
            if (_phase != Phase.Dragging) return;
            if (Distance(p, _start) < 4) { _phase = Phase.FirstCorner; return; } // a click, not a drag: the second corner comes with the next click
            var region = _mode == CapturePick.Ellipse ? CaptureRegion.Ellipse(_start, p) : CaptureRegion.Rectangle(_start, p);
            if (region.IsUsable) SetRegion(region, showBar: true); else Reset();
        }

        private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        // ── Window hover ────────────────────────────────────────────────

        private List<CaptureWindow> WindowsAt(Point p)
            => _windows.Where(w => p.X >= w.Bounds.X && p.X < w.Bounds.X + w.Bounds.Width && p.Y >= w.Bounds.Y && p.Y < w.Bounds.Y + w.Bounds.Height).OrderBy(w => w.ZOrder).ToList();

        private void CycleWindow(int step)
        {
            var under = WindowsAt(_cursor);
            if (under.Count == 0) return;
            _cycle = ((_cycle + step) % under.Count + under.Count) % under.Count;
            UpdateHover(_cursor, keepCycle: true);
        }

        private void UpdateHover(Point p, bool keepCycle = false)
        {
            var under = WindowsAt(p);
            if (!keepCycle && (HoveredWindow == null || !under.Any(w => w.Handle == HoveredWindow.Handle))) _cycle = 0;
            HoveredWindow = under.Count == 0 ? null : under[Math.Min(_cycle, under.Count - 1)];
            if (HoveredWindow is not { } window) { _outline.Data = _outlineHalo.Data = null; _sizeTag.Visibility = Visibility.Collapsed; UpdateDim(); return; }
            var region = CaptureRegion.Rectangle(new Point(window.Bounds.X, window.Bounds.Y), new Point(window.Bounds.X + window.Bounds.Width, window.Bounds.Y + window.Bounds.Height));
            ShowRegion(region, string.IsNullOrWhiteSpace(window.Title) ? null : window.Title);
        }

        // ── Region ──────────────────────────────────────────────────────

        private void SetRegion(CaptureRegion region, bool showBar)
        {
            _region = region;
            ShowRegion(region, null);
            if (showBar)
            {
                _phase = Phase.Selected;
                PlaceActionBar(region);
            }
        }

        private void ShowRegion(CaptureRegion region, string? label)
        {
            var geometry = region.ToGeometry();
            _outline.Data = geometry;
            _outlineHalo.Data = geometry;
            UpdateDim(geometry);
            var b = region.Bounds;
            _sizeText.Text = (label == null ? "" : Truncate(label, 40) + "   ") + $"{b.Width} × {b.Height}";
            _sizeTag.Visibility = Visibility.Visible;
            _sizeTag.UpdateLayout();
            Canvas.SetLeft(_sizeTag, Math.Max(0, b.X / _scale));
            Canvas.SetTop(_sizeTag, Math.Max(0, b.Y / _scale - 26));
        }

        private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

        private void UpdateDim(Geometry? clear = null)
        {
            var full = new RectangleGeometry(new Rect(0, 0, _snapshot.Image.PixelWidth, _snapshot.Image.PixelHeight));
            _dim.Data = clear == null ? full : new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { full, clear } };
        }

        private void PlaceActionBar(CaptureRegion region)
        {
            _outsideButton!.Visibility = region.Kind == CaptureShapeKind.Rectangle ? Visibility.Collapsed : Visibility.Visible;
            _actionBar.Visibility = Visibility.Visible;
            _actionBar.UpdateLayout();
            _actionBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double width = _actionBar.DesiredSize.Width, height = _actionBar.DesiredSize.Height;
            var b = region.Bounds;
            double right = (b.X + b.Width) / _scale, bottom = (b.Y + b.Height) / _scale, screenW = _snapshot.Image.PixelWidth / _scale, screenH = _snapshot.Image.PixelHeight / _scale;
            double left = Math.Clamp(right - width, 4, Math.Max(4, screenW - width - 4));
            double top = bottom + 8 + height <= screenH ? bottom + 8 : Math.Max(4, b.Y / _scale - height - 8);
            if (top + height > screenH) top = Math.Max(4, bottom - height - 8); // a region that fills the screen: the bar goes inside it
            Canvas.SetLeft(_actionBar, left);
            Canvas.SetTop(_actionBar, top);
        }

        // ── Polygon ─────────────────────────────────────────────────────

        private void UpdatePolygonVisual()
        {
            _polyline.Points = new PointCollection(_vertices.Concat(_phase == Phase.Polygon ? new[] { _cursor } : Array.Empty<Point>()));
            if (_vertices.Count >= 3) UpdateDim(CaptureRegion.PolygonGeometry(_vertices));
            else UpdateDim();
            _sizeTag.Visibility = Visibility.Collapsed;
        }

        private void ClosePolygon()
        {
            if (_vertices.Count < 3) return;
            var region = CaptureRegion.Polygon(_vertices);
            _polyline.Points.Clear();
            if (!region.IsUsable) { Reset(); return; }
            SetRegion(region, showBar: true);
        }

        // ── Result ──────────────────────────────────────────────────────

        internal CaptureOutcome? Build(CaptureAction action)
        {
            if (_region is not { } region || !region.IsUsable) return null;
            BitmapSource? image = null;
            // A window that something covers (or that was cycled to) is asked to paint itself; if that gives nothing, the frozen pixels are used.
            if (_mode == CapturePick.Window && _pickedWindow is { } window && IsCovered(window))
                image = _grabWindow(window);
            image ??= CaptureImaging.Crop(_snapshot.Image, region, _transparentOutside);
            return new CaptureOutcome(action, image);
        }

        private bool IsCovered(CaptureWindow window)
        {
            var r = window.Bounds;
            return _windows.Any(other => other.ZOrder < window.ZOrder &&
                other.Bounds.X < r.X + r.Width && other.Bounds.X + other.Bounds.Width > r.X &&
                other.Bounds.Y < r.Y + r.Height && other.Bounds.Y + other.Bounds.Height > r.Y);
        }

        internal void Finish(CaptureAction action)
        {
            var outcome = Build(action);
            if (outcome == null) return;
            Outcome = outcome;
            try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
        }
    }
}
