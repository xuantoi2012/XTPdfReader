using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp.Controls
{
    /// <summary>1 dòng Recent.</summary>
    public sealed record RecentItem(string Path, string Name, string Folder, string Meta, bool Pinned, bool Missing);

    public sealed record LocationItem(string Name, string Path);

    /// <summary>Màn hình Start: mở file, thư mục hay dùng, Recent (có ghim), công cụ nhanh.</summary>
    public partial class StartPage : UserControl
    {
        public StartPage()
        {
            InitializeComponent();
            TitleText.Text = AppInfo.DisplayName;
        }

        /// <summary>Mở hộp thoại chọn file (tham số = thư mục bắt đầu, null = mặc định).</summary>
        internal event Action<string?>? OpenDialogRequested;
        internal event Action<string>? OpenPathRequested;
        internal event Action? OpenFolderRequested;
        internal event Action? NewPdfRequested;
        internal event Action? MergePdfRequested;
        internal event Action? ExportPdfRequested;
        internal event Action? PrintPdfRequested;
        internal event Action? CaptureRequested;
        internal event Action? CapturesRequested;
        internal Func<bool>? HasActiveDocument { get; set; }

        internal void Reload()
        {
            var locations = new List<LocationItem>();
            void Add(string name, string? path)
            {
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && locations.All(l => !string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase)))
                    locations.Add(new LocationItem(name, path));
            }
            var recent = RecentFilesStore.Items;
            Add("Last used folder", recent.Select(r => Path.GetDirectoryName(r.Path)).FirstOrDefault(d => d != null));
            Add("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            Add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            Add("Downloads", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
            Locations.ItemsSource = locations;

            RecentList.ItemsSource = recent.Select(ToItem).ToList();
            NoRecentText.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            bool hasDocument = HasActiveDocument?.Invoke() == true;
            ExportPdfButton.IsEnabled = PrintPdfButton.IsEnabled = hasDocument;
            DocumentToolsHint.Visibility = hasDocument ? Visibility.Collapsed : Visibility.Visible;
        }

        private static RecentItem ToItem(RecentFile r)
        {
            bool missing = !File.Exists(r.Path);
            string meta = missing ? "File not found" : $"{Ago(r.OpenedUtc)} · {Size(r.Size)}" + (r.Pages > 0 ? $" · {r.Pages} page" + (r.Pages == 1 ? "" : "s") : "");
            return new RecentItem(r.Path, Path.GetFileName(r.Path), Path.GetDirectoryName(r.Path) ?? "", meta, r.Pinned, missing);
        }

        private static string Ago(DateTime utc)
        {
            var span = DateTime.UtcNow - utc;
            if (span.TotalMinutes < 1) return "Just now";
            if (span.TotalHours < 1) return (int)span.TotalMinutes + " min ago";
            if (span.TotalDays < 1) return (int)span.TotalHours + (span.TotalHours < 2 ? " hour ago" : " hours ago");
            if (span.TotalDays < 2) return "Yesterday";
            return utc.ToLocalTime().ToString("MMM d");
        }

        private static string Size(long bytes)
            => bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0") + " GB" : Math.Max(1, bytes >> 20) + " MB";

        private void OpenFile_Click(object sender, RoutedEventArgs e) => OpenDialogRequested?.Invoke(null);
        private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenFolderRequested?.Invoke();

        private void Location_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is LocationItem item) OpenDialogRequested?.Invoke(item.Path);
        }

        private void Recent_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not RecentItem { Missing: false } item) return;
            OpenPathRequested?.Invoke(item.Path);
        }

        private void Pin_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is not RecentItem item) return;
            RecentFilesStore.TogglePin(item.Path);
            Reload();
        }

        private void ClearRecent_Click(object sender, RoutedEventArgs e)
        {
            RecentFilesStore.ClearUnpinned();
            Reload();
        }

        private void NewPdf_Click(object sender, RoutedEventArgs e) => NewPdfRequested?.Invoke();
        private void MergePdf_Click(object sender, RoutedEventArgs e) => MergePdfRequested?.Invoke();
        private void ExportPdf_Click(object sender, RoutedEventArgs e) => ExportPdfRequested?.Invoke();
        private void PrintPdf_Click(object sender, RoutedEventArgs e) => PrintPdfRequested?.Invoke();
        private void Capture_Click(object sender, RoutedEventArgs e) => CaptureRequested?.Invoke();
        private void Captures_Click(object sender, RoutedEventArgs e) => CapturesRequested?.Invoke();
    }
}
