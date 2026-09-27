using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp
{
    /// <summary>
    /// Cửa sổ Debug riêng (F12 ở cửa sổ đọc, hoặc nút Debug ở cửa sổ ghép): chữ đơn cách, căn cột, tự làm mới mỗi giây,
    /// kéo to/nhỏ được, nút Copy để dán số liệu thay vì chụp màn hình. Không chặn thao tác trên cửa sổ chính.
    /// </summary>
    internal sealed class DiagnosticsWindow : Window
    {
        private static DiagnosticsWindow? _instance;

        private readonly TextBox _text;
        private readonly Button _pauseButton;
        private readonly DispatcherTimer _timer;
        private bool _paused;

        public static void ShowFor(Window? owner)
        {
            if (_instance == null)
            {
                _instance = new DiagnosticsWindow();
                if (owner != null && owner.IsVisible) _instance.Owner = owner;
                _instance.Show();
            }
            else
            {
                if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
                _instance.Activate();
            }
        }

        private DiagnosticsWindow()
        {
            Title = "Debug — XTPdfMergeApp";
            Width = 900;
            Height = 720;
            MinWidth = 480;
            MinHeight = 300;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = true;
            Background = Brushes.White;

            _text = new TextBox
            {
                IsReadOnly = true,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                FontSize = 13,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 10, 12, 10),
                Background = Brushes.White,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            };

            var copyButton = MakeButton("Copy", (_, _) => CopyText());
            var saveButton = MakeButton("Lưu file…", (_, _) => SaveText());
            _pauseButton = MakeButton("Tạm dừng", (_, _) => TogglePause());
            var collectButton = MakeButton("Dọn RAM (GC)", (_, _) => { DiagnosticsReport.CollectNow(); bool p = _paused; _paused = false; Refresh(); _paused = p; });
            collectButton.ToolTip = "Thu gom rác .NET + ảnh WPF đã bỏ. RAM còn lại sau khi dọn ≈ PDFium thật sự giữ.";
            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8)
            };
            bar.Children.Add(collectButton);
            bar.Children.Add(_pauseButton);
            bar.Children.Add(copyButton);
            bar.Children.Add(saveButton);

            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Bottom);
            root.Children.Add(bar);
            root.Children.Add(_text);
            Content = root;

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Refresh();
            Loaded += (_, _) => { Refresh(); _timer.Start(); };
            Closed += (_, _) => { _timer.Stop(); _instance = null; };
        }

        private static Button MakeButton(string text, RoutedEventHandler click)
        {
            var button = new Button { Content = text, MinWidth = 90, Height = 28, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 0, 10, 0) };
            button.Click += click;
            return button;
        }

        private void Refresh()
        {
            if (_paused) return;
            string text;
            try { text = DiagnosticsReport.Build(); }
            catch (Exception ex) { text = "Lỗi khi lấy số liệu: " + ex; }
            // Giữ vị trí cuộn khi làm mới (không nhảy về đầu mỗi giây).
            double v = _text.VerticalOffset, h = _text.HorizontalOffset;
            _text.Text = text;
            _text.ScrollToVerticalOffset(v);
            _text.ScrollToHorizontalOffset(h);
        }

        private void TogglePause()
        {
            _paused = !_paused;
            _pauseButton.Content = _paused ? "Tiếp tục" : "Tạm dừng";
            if (!_paused) Refresh();
        }

        private void CopyText()
        {
            try { Clipboard.SetText(DiagnosticsReport.Build()); }
            catch (Exception ex) { MessageBox.Show(this, "Không copy được: " + ex.Message, Title); }
        }

        private void SaveText()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"XTPdf-debug-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                Filter = "Text (*.txt)|*.txt"
            };
            if (dialog.ShowDialog(this) != true) return;
            try { File.WriteAllText(dialog.FileName, DiagnosticsReport.Build()); }
            catch (Exception ex) { MessageBox.Show(this, "Không lưu được: " + ex.Message, Title); }
        }
    }
}
