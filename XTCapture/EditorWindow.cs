using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XTStyle.Controls;

namespace XTCapture
{
    /// <summary>
    /// A stored capture opened for drawing and comments: the picture as taken, with everything drawn on it editable (move, resize, change colour / width / text, delete).
    /// Save keeps the drawing beside the picture (<c>markup.json</c>), which itself is never changed. Ctrl+wheel zooms.
    /// </summary>
    internal sealed class EditorWindow : XTWindow
    {
        private static readonly Dictionary<string, EditorWindow> Open = new();

        /// <summary>Tests: windows open far off screen.</summary>
        internal static bool Offscreen { get; set; }

        private CaptureStore.Entry _entry;
        private readonly System.Windows.Media.Imaging.BitmapSource _original;
        private readonly MarkupController _markup = new();
        private readonly MarkupCanvas _markupCanvas = new();
        private readonly MarkupToolbar _toolbar;
        private readonly MarkupTextBox _textBox;
        private readonly Canvas _canvas = new();
        private readonly Border _zoomHost = new();
        private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private string _savedJson;
        private double _zoom;      // 0 = fit the window
        private bool _pointer;

        internal MarkupController Markup => _markup;
        internal MarkupToolbar Bars => _toolbar;
        internal MarkupTextBox TextBox => _textBox;
        internal double Zoom => _zoom <= 0 ? FitZoom() : _zoom;
        internal bool IsDirty => Json() != _savedJson;
        internal CaptureStore.Entry Entry => _entry;

        /// <summary>Opens the capture (one editor per capture; an open one is brought forward).</summary>
        internal static EditorWindow For(CaptureStore.Entry entry, Window? owner)
        {
            if (Open.TryGetValue(entry.Id, out var existing)) { existing.Activate(); return existing; }
            var window = new EditorWindow(entry);
            if (owner != null && !Offscreen) window.Owner = owner;
            Open[entry.Id] = window;
            window.Show();
            return window;
        }

        private EditorWindow(CaptureStore.Entry entry)
        {
            _entry = entry;
            _original = CaptureStore.LoadOriginal(entry);
            Title = TitleFor(false);
            TitleBarMode = TitleBarMode.Dialog;
            Width = 1180;
            Height = 780;
            MinWidth = 640;
            MinHeight = 420;
            ShowInTaskbar = true;
            WindowStartupLocation = Offscreen ? WindowStartupLocation.Manual : WindowStartupLocation.CenterScreen;
            if (Offscreen) { Left = Top = -32000; }
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;

            int w = _original.PixelWidth, h = _original.PixelHeight;
            _markup.Limit = new Size(w, h);
            _markup.Load(CaptureStore.LoadMarkup(entry));
            _savedJson = Json();
            _canvas.Width = w;
            _canvas.Height = h;
            _canvas.Background = Brushes.Transparent;
            RenderOptions.SetBitmapScalingMode(_canvas, BitmapScalingMode.HighQuality);
            _canvas.Children.Add(new Image { Source = _original, Width = w, Height = h, Stretch = Stretch.Fill, IsHitTestVisible = false });
            _markupCanvas.Original = _original;
            _markupCanvas.Controller = _markup;
            _markupCanvas.Width = w;
            _markupCanvas.Height = h;
            _canvas.Children.Add(_markupCanvas);
            _textBox = new MarkupTextBox(_canvas, _markup);
            _toolbar = new MarkupToolbar(_markup);

            _zoomHost.Child = _canvas;
            _zoomHost.HorizontalAlignment = HorizontalAlignment.Center;
            _zoomHost.VerticalAlignment = VerticalAlignment.Center;
            _zoomHost.Margin = new Thickness(16);
            _zoomHost.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Opacity = 0.25, Direction = 270 };
            _scroll.Content = new Grid { Children = { _zoomHost } };
            _scroll.PreviewMouseWheel += (_, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                SetZoom(Zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15));
                e.Handled = true;
            };

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0) };
            actions.Children.Add(Button(CaptureIcons.Save, "Save", "Keep the drawing with the capture (Ctrl+S)", Save, accent: true));
            actions.Children.Add(Button(CaptureIcons.Copy, "Copy", "Copy the picture with the drawing", () => StoreActions.Copy(CurrentPicture())));
            actions.Children.Add(Button(CaptureIcons.Pdf, "PDF", "Export to a one-page PDF", () => StoreActions.ExportPdf(_entry, _original, _markup.Items.ToList(), this)));
            actions.Children.Add(Button(CaptureIcons.Rectangle, "Fit", "Fit the picture to the window", FitToWindow));
            var top = new DockPanel { Margin = new Thickness(10, 8, 10, 4), LastChildFill = false };
            var left = new StackPanel { Orientation = Orientation.Vertical };
            _toolbar.ToolsBar.HorizontalAlignment = HorizontalAlignment.Left;
            _toolbar.PropertyBar.HorizontalAlignment = HorizontalAlignment.Left;
            _toolbar.PropertyBar.Margin = new Thickness(0, 6, 0, 0);
            left.Children.Add(_toolbar.ToolsBar);
            left.Children.Add(_toolbar.PropertyBar);
            DockPanel.SetDock(left, Dock.Left);
            DockPanel.SetDock(actions, Dock.Right);
            top.Children.Add(left);
            top.Children.Add(actions);
            DockPanel.SetDock(top, Dock.Top);
            var root = new DockPanel { Background = TryFindResource("XTWorkspaceBackground") as Brush ?? Brushes.WhiteSmoke };
            root.Children.Add(top);
            root.Children.Add(_scroll);
            Content = root;

            _canvas.MouseLeftButtonDown += (_, e) => { OnPointerDown(e.GetPosition(_canvas), e.ClickCount); if (_pointer) _canvas.CaptureMouse(); e.Handled = true; };
            _canvas.MouseMove += (_, e) => OnPointerMove(e.GetPosition(_canvas));
            _canvas.MouseLeftButtonUp += (_, e) => { OnPointerUp(e.GetPosition(_canvas)); if (_canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture(); e.Handled = true; };
            PreviewKeyDown += (_, e) => { if (OnKey(e.Key)) e.Handled = true; };
            _markup.Changed += () => { Title = TitleFor(IsDirty); UpdateCursor(null); };
            Loaded += (_, _) => FitToWindow();
            SizeChanged += (_, _) => { if (_zoom <= 0) FitToWindow(); };
            Closing += (_, e) => { if (!ConfirmClose()) e.Cancel = true; };
            Closed += (_, _) => Open.Remove(_entry.Id);
        }

        private string TitleFor(bool dirty) => "Capture " + _entry.Created.ToString("dd MMM yyyy HH:mm:ss") + (dirty ? "  •  not saved" : "");

        private string Json() => new MarkupDocument { Items = _markup.Items.ToList() }.ToJson();

        // ── Picture and zoom ────────────────────────────────────────────

        /// <summary>The capture as it looks now (drawing included), as a temporary entry-like source for Copy.</summary>
        private System.Windows.Media.Imaging.BitmapSource CurrentPicture() => MarkupRenderer.Flatten(_original, _markup.Items);

        private double FitZoom()
        {
            double w = Math.Max(50, _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : ActualWidth - 40) - 34, h = Math.Max(50, _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : ActualHeight - 150) - 34;
            return Math.Min(1.0, Math.Min(w / _original.PixelWidth, h / _original.PixelHeight));
        }

        internal void FitToWindow()
        {
            _zoom = 0;
            ApplyZoom(FitZoom());
        }

        private void SetZoom(double zoom)
        {
            _zoom = Math.Clamp(zoom, 0.1, 8);
            ApplyZoom(_zoom);
        }

        private void ApplyZoom(double zoom)
        {
            zoom = Math.Max(0.05, zoom);
            _canvas.LayoutTransform = new ScaleTransform(zoom, zoom);
            _markup.Tolerance = 6 / zoom;
            _markupCanvas.HandleScale = 1 / zoom;
            _markupCanvas.InvalidateVisual();
        }

        // ── Input (internal for the tests) ──────────────────────────────

        internal void OnPointerDown(Point p, int clickCount = 1)
        {
            _textBox.Close(commit: true);
            _markup.PointerDown(p, clickCount);
            _pointer = _markup.GestureActive;
        }

        internal void OnPointerMove(Point p)
        {
            if (_pointer) _markup.PointerMove(p);
            else UpdateCursor(p);
        }

        internal void OnPointerUp(Point p)
        {
            if (!_pointer) return;
            _markup.PointerUp(p);
            _pointer = false;
        }

        private void UpdateCursor(Point? p)
        {
            if (p is not { } point) return;
            var hit = _markup.HitForCursor(point);
            _canvas.Cursor = hit.Kind switch
            {
                CaptureHandle.None => _markup.Tool == MarkupTool.Select ? Cursors.Arrow : Cursors.Cross,
                CaptureHandle.Move => Cursors.SizeAll,
                CaptureHandle.TopLeft or CaptureHandle.BottomRight => Cursors.SizeNWSE,
                CaptureHandle.TopRight or CaptureHandle.BottomLeft => Cursors.SizeNESW,
                CaptureHandle.Top or CaptureHandle.Bottom => Cursors.SizeNS,
                CaptureHandle.Left or CaptureHandle.Right => Cursors.SizeWE,
                _ => Cursors.Cross
            };
        }

        internal bool OnKey(Key key)
        {
            if (_textBox.IsOpen) return false;
            bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (control && key == Key.Z) { _markup.Undo(); return true; }
            if (control && key == Key.Y) { _markup.Redo(); return true; }
            if (control && key == Key.S) { Save(); return true; }
            if (key == Key.Delete && _markup.Selected != null) { _markup.DeleteSelected(); return true; }
            if (key == Key.Escape)
            {
                if (_markup.Tool != MarkupTool.Select) _markup.SetTool(MarkupTool.Select);
                else _markup.Select(null);
                return true;
            }
            return false;
        }

        // ── Save ────────────────────────────────────────────────────────

        internal void Save()
        {
            _textBox.Close(commit: true);
            try
            {
                _entry = CaptureStore.SaveMarkup(_entry, _markup.Items.ToList());
                _savedJson = Json();
                Title = TitleFor(false);
                ToastWindow.Display("Saved");
            }
            catch (Exception ex) { MessageBox.Show(this, "Could not save the drawing: " + ex.Message, "XT Capture", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private bool ConfirmClose()
        {
            _textBox.Close(commit: true);
            if (!IsDirty) return true;
            var answer = MessageBox.Show(this, "Save the changes to this capture?", "XT Capture", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes) Save();
            return answer != MessageBoxResult.Yes || !IsDirty;
        }

        /// <summary>Tests: closes without asking.</summary>
        internal void CloseWithoutAsking()
        {
            _savedJson = Json();
            Close();
        }

        private Border Button(string icon, string text, string tip, Action click, bool accent = false)
        {
            Brush foreground = accent ? Brushes.White : (TryFindResource("Ui.Text") as Brush ?? Brushes.Black);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(CaptureIcons.Create(icon, foreground));
            content.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = foreground });
            var border = new Border
            {
                Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(5), Cursor = Cursors.Hand, ToolTip = tip, Child = content,
                Background = accent ? (TryFindResource("Ui.Accent") as Brush ?? new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB))) : (TryFindResource("Ui.Surface") as Brush ?? Brushes.White),
                BorderThickness = new Thickness(1), BorderBrush = accent ? Brushes.Transparent : (TryFindResource("Ui.Border") as Brush ?? Brushes.LightGray)
            };
            border.MouseLeftButtonUp += (_, _) => click();
            return border;
        }
    }
}
