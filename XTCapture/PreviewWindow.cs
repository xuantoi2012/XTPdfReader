using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XTStyle.Controls;

namespace XTCapture
{
    /// <summary>A capture shown large (fitted to the window, wheel to zoom), with Copy / Save PNG / PDF. Drawing comments on it comes next.</summary>
    internal sealed class PreviewWindow : XTWindow
    {
        private readonly Image _image = new() { Stretch = Stretch.Uniform };
        private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private double _zoom;

        internal static PreviewWindow ShowFor(CaptureStore.Entry entry, Window owner)
        {
            var window = new PreviewWindow(entry) { Owner = owner };
            window.Show();
            return window;
        }

        internal Image Picture => _image;

        private PreviewWindow(CaptureStore.Entry entry)
        {
            Title = "Capture " + entry.Created.ToString("dd MMM yyyy HH:mm:ss");
            TitleBarMode = TitleBarMode.Dialog;
            Width = 1000;
            Height = 700;
            MinWidth = 420;
            MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
            bar.Children.Add(Button("Copy", CaptureIcons.Copy, () => StoreActions.Copy(entry)));
            bar.Children.Add(Button("Save PNG", CaptureIcons.Save, () => StoreActions.SavePng(entry, this)));
            bar.Children.Add(Button("PDF", CaptureIcons.Pdf, () => StoreActions.ExportPdf(entry, this)));
            bar.Children.Add(Button("Fit", CaptureIcons.Rectangle, FitToWindow));
            DockPanel.SetDock(bar, Dock.Top);

            _image.Source = CaptureStore.LoadOriginal(entry);
            _image.Width = _image.Source.Width;
            _image.Height = _image.Source.Height;
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            _scroll.Content = new Border { Child = _image, Background = Brushes.Transparent };
            _scroll.PreviewMouseWheel += (_, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 && _zoom > 0) return;
                SetZoom((_zoom <= 0 ? FitZoom() : _zoom) * (e.Delta > 0 ? 1.15 : 1 / 1.15));
                e.Handled = true;
            };
            var root = new DockPanel { Background = TryFindResource("Ui.Surface") as Brush ?? Brushes.White };
            root.Children.Add(bar);
            root.Children.Add(_scroll);
            Content = root;
            Loaded += (_, _) => FitToWindow();
            SizeChanged += (_, _) => { if (_zoom <= 0) FitToWindow(); };
        }

        private Border Button(string text, string icon, Action click)
        {
            Brush foreground = TryFindResource("Ui.Text") as Brush ?? Brushes.Black;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(CaptureIcons.Create(icon, foreground));
            content.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = foreground });
            var border = new Border
            {
                Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(5), Cursor = Cursors.Hand, Child = content,
                BorderThickness = new Thickness(1), BorderBrush = TryFindResource("Ui.Border") as Brush ?? Brushes.LightGray
            };
            border.MouseLeftButtonUp += (_, _) => click();
            return border;
        }

        private double FitZoom()
        {
            double w = Math.Max(1, _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : ActualWidth - 20), h = Math.Max(1, _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : ActualHeight - 80);
            var source = _image.Source;
            return Math.Min(1.0, Math.Min(w / source.Width, h / source.Height)); // small pictures are not enlarged
        }

        internal void FitToWindow()
        {
            _zoom = 0;
            Apply(FitZoom());
        }

        private void SetZoom(double zoom)
        {
            _zoom = Math.Clamp(zoom, 0.05, 8);
            Apply(_zoom);
        }

        private void Apply(double zoom)
        {
            _image.Width = _image.Source.Width * zoom;
            _image.Height = _image.Source.Height * zoom;
        }
    }
}
