using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp.Controls
{
    /// <summary>Trang Settings (docs/UI_REDESIGN.md, mockup 4): Appearance, Display, Performance &amp; memory, Integration. Mọi thay đổi áp dụng ngay.</summary>
    public partial class SettingsPage : UserControl
    {
        private bool _loading;

        public SettingsPage()
        {
            InitializeComponent();
            BuildSwatches();
            for (int i = 0; i <= 8; i++)
                WarmBox.Items.Add(new ComboBoxItem { Content = i == 1 ? "1 file" : i + " files", Tag = i });
            AboutText.Text = AppInfo.DisplayName + " " + AppInfo.Version;
        }

        /// <summary>Xoá bộ đệm file không nằm trên màn hình (ReaderWindow thực hiện, vì biết file nào đang xem).</summary>
        internal Func<Task>? ClearCacheRequested { get; set; }

        /// <summary>Đọc lại giá trị hiện tại vào các ô (gọi mỗi lần mở trang).</summary>
        internal void Reload()
        {
            _loading = true;
            try
            {
                ((AppSettings.Theme switch { "Dark" => ThemeDark, "System" => ThemeSystem, _ => ThemeLight })).IsChecked = true;
                foreach (RadioButton swatch in SwatchHost.Children)
                    swatch.IsChecked = (string)swatch.Tag == AppSettings.Accent;
                ViewModeBox.SelectedIndex = AppSettings.ContinuousByDefault ? 0 : 1;
                ZoomBox.SelectedIndex = (int)AppSettings.ZoomOnOpen;
                DefaultToolBox.SelectedIndex = AppSettings.DefaultTool == "Select" ? 1 : 0;
                PerformanceModeBox.SelectedIndex = (int)AppSettings.PerformanceMode;
                RefreshPerformanceHint();
                WarmBox.SelectedIndex = PdfThumbnailService.WarmFiles is >= 0 and <= 8 ? PdfThumbnailService.WarmFiles : AppSettings.WarmFiles;
                PdfFactoryToggle.IsChecked = MergeAppSettingsStore.GetPdfFactoryViewEnabled();
                PresenceToggle.IsChecked = AppSettings.ShowPresence;
                RefreshPdfFactoryHint();
            }
            finally { _loading = false; }
            RefreshCacheInfo();
        }

        // ── Appearance ────────────────────────────────────────────────

        private void BuildSwatches()
        {
            foreach (string name in new[] { "Orange", "Blue", "Green", "Purple", "Red" })
            {
                var swatch = new RadioButton
                {
                    Style = (Style)FindResource("SwatchItem"),
                    GroupName = "Accent",
                    Tag = name,
                    ToolTip = name,
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ThemeService.AccentSwatch(name)))
                };
                swatch.Checked += (_, _) =>
                {
                    if (_loading) return;
                    AppSettings.Accent = name;
                    ThemeService.ApplySaved();
                };
                SwatchHost.Children.Add(swatch);
            }
        }

        private void Theme_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not RadioButton { Tag: string theme }) return;
            AppSettings.Theme = theme;
            ThemeService.ApplySaved();
        }

        // ── Điều hướng bên trái ───────────────────────────────────────

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            PageAppearance.Visibility = sender == NavAppearance ? Visibility.Visible : Visibility.Collapsed;
            PageDisplay.Visibility = sender == NavDisplay ? Visibility.Visible : Visibility.Collapsed;
            PagePerformance.Visibility = sender == NavPerformance ? Visibility.Visible : Visibility.Collapsed;
            PageIntegration.Visibility = sender == NavIntegration ? Visibility.Visible : Visibility.Collapsed;
            Scroll.ScrollToVerticalOffset(0);
        }

        // ── Display ───────────────────────────────────────────────────

        private void Presence_Changed(object sender, RoutedEventArgs e)
        {
            if (!_loading) AppSettings.ShowPresence = PresenceToggle.IsChecked == true;
        }

        private void ViewMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || ViewModeBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
            AppSettings.ContinuousByDefault = tag == "continuous";
        }

        private void Zoom_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || ZoomBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
            AppSettings.ZoomOnOpen = Enum.Parse<DefaultZoom>(tag);
        }

        private void DefaultTool_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || DefaultToolBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
            AppSettings.DefaultTool = tag;
        }

        // ── Performance & memory ──────────────────────────────────────

        private void PerformanceMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || PerformanceModeBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
            AppSettings.PerformanceMode = Enum.Parse<ReaderPerformanceMode>(tag);
            RefreshPerformanceHint();
        }

        private void RefreshPerformanceHint()
        {
            PerformanceModeHint.Text = AppSettings.PerformanceMode switch
            {
                ReaderPerformanceMode.Balance => "Keeps more recent pages and preloads up to 2 pages ahead. Uses more memory to speed up revisits.",
                ReaderPerformanceMode.Maximum => "Keeps the largest cache and preloads up to 4 pages ahead. Best for powerful PCs; may use several GB of memory.",
                _ => "Reclaims distant pages promptly and preloads 1 page ahead. Recommended for keeping memory usage low."
            };
            PerformanceModeHint.Text += " Applies while reading; all modes reduce memory use when Windows is low on memory.";
        }

        private void Warm_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || WarmBox.SelectedItem is not ComboBoxItem { Tag: int count }) return;
            AppSettings.WarmFiles = count;
            PdfThumbnailService.WarmFiles = count;
        }

        private void RefreshCacheInfo()
        {
            var files = PdfFileBuffer.Snapshot();
            double bytes = files.Sum(f => f.Length * Math.Clamp(f.Loaded, 0, 1));
            string usage = files.Count == 0 ? "Nothing is cached right now." : $"Using {FormatSize(bytes)} for {files.Count} open file{(files.Count == 1 ? "" : "s")}.";
            CacheHint.Text = "Files opened over a network are cached in the temp folder and deleted when the file is closed. " + usage;
        }

        private static string FormatSize(double bytes)
            => bytes >= 1024 * 1024 * 1024 ? (bytes / (1024.0 * 1024 * 1024)).ToString("0.0") + " GB"
             : (bytes / (1024.0 * 1024)).ToString("0") + " MB";

        private async void ClearCache_Click(object sender, RoutedEventArgs e)
        {
            ClearCacheButton.IsEnabled = false;
            try
            {
                if (ClearCacheRequested != null) await ClearCacheRequested();
                await Task.Delay(300);
            }
            finally
            {
                ClearCacheButton.IsEnabled = true;
                RefreshCacheInfo();
            }
        }

        // ── Integration ───────────────────────────────────────────────

        private void PdfFactory_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (PdfFactoryToggle.IsChecked != true)
            {
                PdfFactoryIntegrationService.Disable();
                RefreshPdfFactoryHint();
                return;
            }

            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exePath) || !PdfFactoryIntegrationService.Enable(exePath))
            {
                PdfFactoryToggle.IsChecked = false; // gọi lại nhánh trên (Disable) rồi mới hiện cảnh báo
                RefreshPdfFactoryHint("pdfFactory was not found on this computer (not installed, or nothing has been printed with it yet).");
                return;
            }
            RefreshPdfFactoryHint();
        }

        private void RefreshPdfFactoryHint(string? warning = null)
        {
            if (warning != null)
            {
                PdfFactoryHint.Text = "⚠ " + warning;
                PdfFactoryHint.Foreground = (Brush)FindResource("Ui.Warn");
                return;
            }
            bool on = PdfFactoryToggle.IsChecked == true;
            PdfFactoryHint.Text = on ? "On — “View PDF file” in pdfFactory opens this app." : "Off — pdfFactory uses its own viewer.";
            PdfFactoryHint.Foreground = (Brush)FindResource("Ui.Muted");
        }
    }
}
