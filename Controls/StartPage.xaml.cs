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

    public sealed record WorkspaceItem(WorkspaceEntry Entry, string Name, string Meta);

    public sealed record LocationItem(string Name, string Path);

    /// <summary>Màn hình Start (docs/UI_REDESIGN.md, mockup 16): mở file, thư mục hay dùng, Recent (có ghim), Workspaces.</summary>
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
        internal event Action<WorkspaceEntry>? RestoreRequested;
        /// <summary>Các file đang mở (thứ tự tab) và file đang xem — để lưu workspace.</summary>
        internal Func<(IReadOnlyList<string> Files, string? Active)>? CurrentFiles { get; set; }

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

            var workspaces = WorkspaceStore.Items.Select(w => new WorkspaceItem(w, w.Name, w.Files.Count == 1 ? "1 file" : w.Files.Count + " files")).ToList();
            WorkspaceList.ItemsSource = workspaces;
            NoWorkspaceText.Visibility = workspaces.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SaveWorkspaceButton.IsEnabled = (CurrentFiles?.Invoke().Files.Count ?? 0) > 0;
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

        private void SaveWorkspace_Click(object sender, RoutedEventArgs e)
        {
            var (files, active) = CurrentFiles?.Invoke() ?? (Array.Empty<string>(), null);
            if (files.Count == 0) return;
            string? name = TextPromptWindow.Ask(Window.GetWindow(this), "Save workspace", "Name for this workspace:", "");
            if (name == null) return;
            if (WorkspaceStore.Exists(name) &&
                MessageBox.Show(Window.GetWindow(this), $"A workspace named \"{name}\" already exists. Replace it?", "Save workspace",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            WorkspaceStore.Upsert(new WorkspaceEntry(name, files.ToList(), active));
            Reload();
        }

        private void RestoreWorkspace_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is WorkspaceItem item) RestoreRequested?.Invoke(item.Entry);
        }

        private void DeleteWorkspace_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is not WorkspaceItem item) return;
            WorkspaceStore.Remove(item.Name);
            Reload();
        }
    }
}
