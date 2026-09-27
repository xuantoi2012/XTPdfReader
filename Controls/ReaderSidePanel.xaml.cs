using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XTPdfMergeApp.Services;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;

namespace XTPdfMergeApp.Controls
{
    /// <summary>
    /// Panel trái của cửa sổ đọc (mô phỏng Foxit): dải icon đổi tab Trang / Bookmark / Layer.
    /// - Trang: thumbnail của window (DocumentGroup) đang xem.
    /// - Bookmark / Layer: của FILE NGUỒN chứa trang đang xem (1 window đã ghép nhiều file thì đổi theo
    ///   trang đang xem), đọc bằng iText ở luồng nền, cache theo file.
    /// Panel chỉ báo sự kiện; ReaderWindow lo điều hướng và bật/tắt layer.
    /// </summary>
    public partial class ReaderSidePanel : UserControl
    {
        private enum Tab { Thumbnails, Bookmarks, Layers }

        private Tab _tab = Tab.Thumbnails;
        private DocumentGroup? _group;
        private string? _sourcePath;
        private bool _syncingSelection;
        private readonly Dictionary<string, Task<IReadOnlyList<PdfBookmarkNode>>> _bookmarks = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task<PdfLayerInfo>> _layers = new(StringComparer.OrdinalIgnoreCase);

        public ReaderSidePanel() => InitializeComponent();

        /// <summary>Bấm 1 thumbnail (không giữ Ctrl/Shift) → nhảy tới trang đó.</summary>
        internal event Action<PageRow>? PageActivated;
        /// <summary>Bấm 1 bookmark → (file nguồn, số trang 1-based trong file đó).</summary>
        internal event Action<string, int>? BookmarkActivated;
        /// <summary>Bật/tắt 1 layer: (file, thông tin layer của file, id OCG, hiện?).</summary>
        internal event Action<string, PdfLayerInfo, string, bool>? LayerToggled;

        /// <summary>Trang đang chọn trong panel thumbnail (theo thứ tự trong window).</summary>
        internal IReadOnlyList<PageRow> SelectedPages
            => _group == null ? Array.Empty<PageRow>()
                : ThumbnailList.SelectedItems.Cast<PageRow>().OrderBy(p => _group.Pages.IndexOf(p)).ToList();

        /// <summary>Viewer vừa đổi trang đang xem.</summary>
        internal void SetCurrent(DocumentGroup? group, PageRow? row)
        {
            if (!ReferenceEquals(_group, group))
            {
                _group = group;
                ThumbnailList.ItemsSource = group?.Pages;
            }

            if (row != null && !ThumbnailList.SelectedItems.Contains(row))
            {
                _syncingSelection = true;
                ThumbnailList.SelectedItem = row;
                _syncingSelection = false;
                ThumbnailList.ScrollIntoView(row);
            }

            string? path = row?.SourcePath;
            if (!string.Equals(_sourcePath, path, StringComparison.OrdinalIgnoreCase))
            {
                _sourcePath = path;
                _ = RefreshSourceTabAsync();
            }
        }

        /// <summary>File vừa bị sửa (annotation…) hoặc layer vừa đổi — đọc lại khi cần.</summary>
        internal void InvalidateSource(string path, bool bookmarks, bool layers)
        {
            if (bookmarks) _bookmarks.Remove(path);
            if (layers) _layers.Remove(path);
            if (string.Equals(path, _sourcePath, StringComparison.OrdinalIgnoreCase)) _ = RefreshSourceTabAsync();
        }

        /// <summary>Yêu cầu thumbnail cho các trang đang hiện trong panel (sau khi thumbnail bị bỏ vì đổi layer).</summary>
        internal void RequestVisibleThumbnails()
        {
            foreach (var item in Services.VisualTreeHelpers.FindVisualChildren<ListBoxItem>(ThumbnailList))
                if (item.DataContext is PageRow row && row.Thumbnail == null) _ = ThumbnailCache.LoadThumbnailFor(row);
        }

        // ── Đổi tab ───────────────────────────────────────────────────

        private void ThumbnailTab_Click(object sender, RoutedEventArgs e) => SetTab(Tab.Thumbnails);
        private void BookmarkTab_Click(object sender, RoutedEventArgs e) => SetTab(Tab.Bookmarks);
        private void LayerTab_Click(object sender, RoutedEventArgs e) => SetTab(Tab.Layers);

        private void SetTab(Tab tab)
        {
            _tab = tab;
            ThumbnailTabButton.Tag = tab == Tab.Thumbnails ? "Active" : null;
            BookmarkTabButton.Tag = tab == Tab.Bookmarks ? "Active" : null;
            LayerTabButton.Tag = tab == Tab.Layers ? "Active" : null;
            PanelTitleText.Text = tab switch { Tab.Bookmarks => "Bookmark", Tab.Layers => "Layer", _ => "Trang" };
            ThumbnailList.Visibility = tab == Tab.Thumbnails ? Visibility.Visible : Visibility.Collapsed;
            BookmarkTree.Visibility = Visibility.Collapsed;
            LayerTree.Visibility = Visibility.Collapsed;
            PanelEmptyText.Visibility = Visibility.Collapsed;
            PanelSourceText.Visibility = tab == Tab.Thumbnails ? Visibility.Collapsed : Visibility.Visible;
            if (tab != Tab.Thumbnails) _ = RefreshSourceTabAsync();
        }

        private async Task RefreshSourceTabAsync()
        {
            if (_tab == Tab.Thumbnails) return;
            string? path = _sourcePath;
            PanelSourceText.Text = path == null ? "" : "Nguồn: " + Path.GetFileName(path);
            if (path == null)
            {
                ShowEmpty("Chưa mở tài liệu.");
                return;
            }

            if (_tab == Tab.Bookmarks)
            {
                if (!_bookmarks.TryGetValue(path, out var task))
                    _bookmarks[path] = task = Task.Run(() => SafeRead(() => PdfOutlineService.ReadBookmarks(path), Array.Empty<PdfBookmarkNode>()));
                var nodes = await task;
                if (_tab != Tab.Bookmarks || !string.Equals(path, _sourcePath, StringComparison.OrdinalIgnoreCase)) return;
                if (nodes.Count == 0) { ShowEmpty("Tài liệu này không có bookmark."); return; }
                PanelEmptyText.Visibility = Visibility.Collapsed;
                BookmarkTree.ItemsSource = nodes;
                BookmarkTree.Visibility = Visibility.Visible;
            }
            else
            {
                if (!_layers.TryGetValue(path, out var task))
                    _layers[path] = task = Task.Run(() => SafeRead(() => PdfLayerService.ReadLayers(path), PdfLayerInfo.Empty));
                var info = await task;
                if (_tab != Tab.Layers || !string.Equals(path, _sourcePath, StringComparison.OrdinalIgnoreCase)) return;
                if (!info.HasLayers) { ShowEmpty("Tài liệu này không có layer."); return; }
                PanelEmptyText.Visibility = Visibility.Collapsed;
                var hidden = PdfLayerStateStore.GetHiddenOverride(path, out _) ?? info.DefaultHidden;
                LayerTree.ItemsSource = info.Roots.Select(n => new LayerItem(n, hidden)).ToList();
                LayerTree.Tag = (path, info);
                LayerTree.Visibility = Visibility.Visible;
            }
        }

        private static T SafeRead<T>(Func<T> read, T fallback)
        {
            try { return read(); }
            catch { return fallback; } // file hỏng/mã hoá: hiện rỗng, không lỗi
        }

        private void ShowEmpty(string text)
        {
            BookmarkTree.Visibility = Visibility.Collapsed;
            LayerTree.Visibility = Visibility.Collapsed;
            PanelEmptyText.Text = text;
            PanelEmptyText.Visibility = Visibility.Visible;
        }

        // ── Thumbnail ─────────────────────────────────────────────────

        private void ThumbnailImage_Loaded(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is PageRow { Thumbnail: null } row) _ = ThumbnailCache.LoadThumbnailFor(row);
        }

        private void ThumbnailImage_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is PageRow { Thumbnail: null } row && ((FrameworkElement)sender).IsLoaded) _ = ThumbnailCache.LoadThumbnailFor(row);
        }

        private void ThumbnailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingSelection || ThumbnailList.SelectedItems.Count != 1) return;
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0) return;
            if (ThumbnailList.SelectedItem is PageRow row) PageActivated?.Invoke(row);
        }

        // ── Bookmark ──────────────────────────────────────────────────

        private void Bookmark_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PdfBookmarkNode { PageNumber: int page } || _sourcePath == null) return;
            BookmarkActivated?.Invoke(_sourcePath, page);
        }

        // ── Layer ─────────────────────────────────────────────────────

        private void LayerCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox { DataContext: LayerItem { OcgId: { } id, CanToggle: true } item } box) return;
            if (LayerTree.Tag is not ValueTuple<string, PdfLayerInfo> source) return;
            bool visible = box.IsChecked == true;
            // Cùng 1 OCG có thể xuất hiện nhiều chỗ trong /Order — đồng bộ mọi dòng của nó.
            foreach (var other in LayerItem.Flatten(LayerTree.ItemsSource as IEnumerable<LayerItem>))
                if (other.OcgId == id) other.IsVisible = visible;
            LayerToggled?.Invoke(source.Item1, source.Item2, id, visible);
        }
    }

    /// <summary>1 dòng trong cây Layer.</summary>
    public sealed class LayerItem : INotifyPropertyChanged
    {
        public const string LockedToolTip = "Lớp bị khoá, không đổi hiển thị được";
        public const string UsageToolTip = "Lớp do /AS (usage) của file tự điều khiển khi xem — không đổi hiển thị được";

        public LayerItem(PdfLayerNode node, IReadOnlySet<string> hidden)
        {
            Title = node.Title;
            OcgId = node.OcgId;
            IsLocked = node.IsLocked;
            IsUsageControlled = node.IsUsageControlled;
            _isVisible = node.OcgId != null && !hidden.Contains(node.OcgId);
            Children = node.Children.Select(c => new LayerItem(c, hidden)).ToList();
        }

        public string Title { get; }
        public string? OcgId { get; }
        public bool IsLocked { get; }
        public bool IsUsageControlled { get; }
        public IReadOnlyList<LayerItem> Children { get; }
        public bool CanToggle => OcgId != null && !IsLocked && !IsUsageControlled;
        public Visibility CheckBoxVisibility => OcgId != null ? Visibility.Visible : Visibility.Collapsed;
        public FontWeight TitleWeight => OcgId == null ? FontWeights.SemiBold : FontWeights.Normal;
        public string? ToolTipText => IsLocked ? LockedToolTip : IsUsageControlled ? UsageToolTip : null;

        private bool _isVisible;
        public bool IsVisible
        {
            get => _isVisible;
            set { if (_isVisible == value) return; _isVisible = value; Notify(); }
        }

        internal static IEnumerable<LayerItem> Flatten(IEnumerable<LayerItem>? items)
            => items == null ? Enumerable.Empty<LayerItem>() : items.SelectMany(i => new[] { i }.Concat(Flatten(i.Children)));

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
