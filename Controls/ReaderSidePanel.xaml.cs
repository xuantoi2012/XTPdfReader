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
    /// Panel trái của cửa sổ đọc: thanh icon + chữ đổi tab Pages / Bookmarks / Layers (docs/UI_REDESIGN.md).
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

        /// <summary>Lệnh trên các trang đang chọn (menu chuột phải, phím tắt) — ReaderWindow thực thi.</summary>
        internal event Action<PageCommand>? PageCommandRequested;
        /// <summary>Có trang trong clipboard (bật/tắt mục Paste của menu).</summary>
        internal Func<bool>? HasPageClipboard { get; set; }

        private void PageMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: string tag } && Enum.TryParse<PageCommand>(tag, out var command))
                PageCommandRequested?.Invoke(command);
        }

        private void ThumbnailList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            int n = ThumbnailList.SelectedItems.Count;
            if (_group == null || n == 0) { e.Handled = true; return; }
            bool clip = HasPageClipboard?.Invoke() ?? false;
            MiPasteAfter.IsEnabled = clip;
            MiPasteBefore.IsEnabled = clip;
            MiDelete.Header = n == 1 ? "Delete page" : $"Delete {n} pages";
        }

        /// <summary>Chuột phải lên trang chưa chọn thì chọn riêng trang đó (như Explorer); đã chọn thì giữ nguyên vùng chọn.</summary>
        private void ThumbItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBoxItem { IsSelected: false } item) return;
            ThumbnailList.SelectedItems.Clear();
            item.IsSelected = true;
        }

        private void ThumbnailList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            PageCommand? command = null;
            if (ctrl && !alt)
            {
                command = key switch
                {
                    Key.C => PageCommand.Copy,
                    Key.X => PageCommand.Cut,
                    Key.V => shift ? PageCommand.PasteBefore : PageCommand.PasteAfter,
                    Key.D => PageCommand.Duplicate,
                    Key.Home when shift => PageCommand.MoveToStart,
                    Key.End when shift => PageCommand.MoveToEnd,
                    _ => null
                };
            }
            else if (alt && !ctrl)
            {
                command = key switch { Key.Up => PageCommand.MoveUp, Key.Down => PageCommand.MoveDown, _ => null };
            }
            else if (key == Key.Delete && !ctrl && !shift)
            {
                command = PageCommand.Delete;
            }
            if (command == null) return;
            PageCommandRequested?.Invoke(command.Value);
            e.Handled = true;
        }

        // ── Kéo thả trang: sắp xếp lại, Ctrl = sao chép, thả lên tab khác (xem ReaderWindow) ──────────

        /// <summary>Trang đang bị kéo và window nguồn (đi kèm DataObject).</summary>
        internal sealed class PageDragData
        {
            public required DocumentGroup Source { get; init; }
            public required List<PageRow> Pages { get; init; }
        }

        /// <summary>Thả vào panel: (dữ liệu kéo, chỉ số chèn trong window đang hiện, sao chép?).</summary>
        internal event Action<PageDragData, int, bool>? PagesDropped;

        private Point _dragOrigin;
        private ListBoxItem? _dragItem;
        private bool _pendingSingleSelect;
        private ScrollViewer? _thumbScroll;

        private ListBoxItem? ItemUnder(DependencyObject? source)
        {
            while (source != null && source is not ListBoxItem) source = System.Windows.Media.VisualTreeHelper.GetParent(source);
            return source as ListBoxItem;
        }

        private void Thumb_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragItem = ItemUnder(e.OriginalSource as DependencyObject);
            _dragOrigin = e.GetPosition(ThumbnailList);
            _pendingSingleSelect = false;
            // Nhấn lên trang đã chọn khi đang chọn nhiều trang: giữ vùng chọn để kéo được cả nhóm; nhả chuột không kéo thì mới chọn riêng trang đó.
            if (_dragItem is { IsSelected: true } && ThumbnailList.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
            {
                _pendingSingleSelect = true;
                e.Handled = true;
                ThumbnailList.Focus();
            }
        }

        private void Thumb_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_pendingSingleSelect && _dragItem != null)
            {
                ThumbnailList.SelectedItems.Clear();
                _dragItem.IsSelected = true;
            }
            _pendingSingleSelect = false;
            _dragItem = null;
        }

        private void Thumb_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null || _group == null) return;
            var delta = e.GetPosition(ThumbnailList) - _dragOrigin;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            var item = _dragItem;
            _dragItem = null;
            _pendingSingleSelect = false;
            if (item.DataContext is PageRow row && !ThumbnailList.SelectedItems.Contains(row))
            {
                ThumbnailList.SelectedItems.Clear();
                item.IsSelected = true;
            }
            var pages = SelectedPages;
            if (pages.Count == 0) return;

            var data = new DataObject(typeof(PageDragData), new PageDragData { Source = _group, Pages = pages.ToList() });
            try { DragDrop.DoDragDrop(ThumbnailList, data, DragDropEffects.Move | DragDropEffects.Copy); }
            finally { DropLine.Visibility = Visibility.Collapsed; }
        }

        /// <summary>Chỉ số chèn (0-based) ứng với vị trí chuột, và vị trí vạch chèn (toạ độ của ThumbnailList).</summary>
        private int InsertIndexAt(Point point, out Rect line)
        {
            line = Rect.Empty;
            if (_group == null) return 0;
            ListBoxItem? best = null;
            Rect bestBounds = Rect.Empty;
            double bestDistance = double.MaxValue;
            foreach (var item in Services.VisualTreeHelpers.FindVisualChildren<ListBoxItem>(ThumbnailList))
            {
                if (item.DataContext is not PageRow || item.ActualWidth <= 0) continue;
                var topLeft = item.TranslatePoint(new Point(0, 0), ThumbnailList);
                var bounds = new Rect(topLeft, new Size(item.ActualWidth, item.ActualHeight));
                double dx = Math.Max(Math.Max(bounds.Left - point.X, 0), point.X - bounds.Right);
                double dy = Math.Max(Math.Max(bounds.Top - point.Y, 0), point.Y - bounds.Bottom);
                double distance = dx * dx + dy * dy;
                if (distance < bestDistance) { best = item; bestBounds = bounds; bestDistance = distance; }
            }
            if (best == null) return 0;
            int index = _group.Pages.IndexOf((PageRow)best.DataContext);
            if (index < 0) return _group.Pages.Count;
            bool before = point.X < bestBounds.Left + bestBounds.Width / 2;
            double x = before ? bestBounds.Left - 1 : bestBounds.Right - 2;
            line = new Rect(x, bestBounds.Top + 4, 3, Math.Max(8, bestBounds.Height - 8));
            return before ? index : index + 1;
        }

        private void Thumb_DragOver(object sender, DragEventArgs e)
        {
            if (_group == null || !e.Data.GetDataPresent(typeof(PageDragData)))
            {
                DropLine.Visibility = Visibility.Collapsed;
                return; // file PDF kéo từ ngoài vào: để cửa sổ chính xử lý (mở file)
            }
            e.Effects = (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy : DragDropEffects.Move;
            var point = e.GetPosition(ThumbnailList);
            InsertIndexAt(point, out var line);
            if (line.IsEmpty) DropLine.Visibility = Visibility.Collapsed;
            else
            {
                DropLine.Margin = new Thickness(line.X + ThumbnailList.Margin.Left, line.Y + ThumbnailList.Margin.Top, 0, 0);
                DropLine.Height = line.Height;
                DropLine.Visibility = Visibility.Visible;
            }
            AutoScrollThumbnails(point);
            e.Handled = true;
        }

        private void AutoScrollThumbnails(Point point)
        {
            _thumbScroll ??= Services.VisualTreeHelpers.FindVisualChildren<ScrollViewer>(ThumbnailList).FirstOrDefault();
            if (_thumbScroll == null) return;
            const double zone = 32, step = 22;
            if (point.Y < zone) _thumbScroll.ScrollToVerticalOffset(Math.Max(0, _thumbScroll.VerticalOffset - step));
            else if (point.Y > ThumbnailList.ActualHeight - zone) _thumbScroll.ScrollToVerticalOffset(_thumbScroll.VerticalOffset + step);
        }

        private void Thumb_DragLeave(object sender, DragEventArgs e) => DropLine.Visibility = Visibility.Collapsed;

        private void Thumb_Drop(object sender, DragEventArgs e)
        {
            DropLine.Visibility = Visibility.Collapsed;
            if (_group == null || e.Data.GetData(typeof(PageDragData)) is not PageDragData data) return;
            int index = InsertIndexAt(e.GetPosition(ThumbnailList), out _);
            bool copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            e.Handled = true;
            PagesDropped?.Invoke(data, index, copy);
        }

        /// <summary>Chọn đúng các trang này trong panel (sau Move/Paste) mà không kích hoạt nhảy trang.</summary>
        internal void SelectPages(IReadOnlyList<PageRow> pages)
        {
            if (_group == null) return;
            _syncingSelection = true;
            try
            {
                ThumbnailList.SelectedItems.Clear();
                foreach (var page in pages)
                    if (_group.Pages.Contains(page)) ThumbnailList.SelectedItems.Add(page);
            }
            finally { _syncingSelection = false; }
            UpdateSelectionBar();
            if (pages.Count > 0 && _group.Pages.Contains(pages[0])) ThumbnailList.ScrollIntoView(pages[0]);
        }

        // Hàng thao tác trang: panel chỉ báo, ReaderWindow gọi handler sẵn có (chèn/xoá/xoay thật/trích xuất).
        internal event RoutedEventHandler? InsertPagesRequested, DeletePagesRequested, RotateLeftRequested, RotateRightRequested, ExtractPagesRequested;
        private void InsertPages_Click(object sender, RoutedEventArgs e) => InsertPagesRequested?.Invoke(sender, e); // sender = nút Insert (chỗ neo menu)
        private void DeletePages_Click(object sender, RoutedEventArgs e) => DeletePagesRequested?.Invoke(this, e);
        private void RotateLeft_Click(object sender, RoutedEventArgs e) => RotateLeftRequested?.Invoke(this, e);
        private void RotateRight_Click(object sender, RoutedEventArgs e) => RotateRightRequested?.Invoke(this, e);
        private void ExtractPages_Click(object sender, RoutedEventArgs e) => ExtractPagesRequested?.Invoke(this, e);

        /// <summary>Trang đang chọn trong panel thumbnail (theo thứ tự trong window).</summary>
        internal IReadOnlyList<PageRow> SelectedPages
            => _group == null ? Array.Empty<PageRow>()
                : ThumbnailList.SelectedItems.Cast<PageRow>().OrderBy(p => _group.Pages.IndexOf(p)).ToList();

        /// <summary>Viewer vừa đổi trang đang xem.</summary>
        internal void SetCurrent(DocumentGroup? group, PageRow? row)
        {
            if (!ReferenceEquals(_group, group))
            {
                if (_group != null) _group.Pages.CollectionChanged -= OnPagesChanged;
                _group = group;
                if (_group != null) _group.Pages.CollectionChanged += OnPagesChanged;
                ThumbnailList.ItemsSource = group?.Pages;
                UpdateCount();
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

        private void OnPagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateCount();

        private void UpdateCount() => PanelCountText.Text = _group == null ? "" : _group.Pages.Count.ToString();

        /// <summary>Thanh "N pages selected" chỉ hiện khi chọn từ 2 trang (1 trang luôn được chọn = trang đang xem).</summary>
        private void UpdateSelectionBar()
        {
            int n = ThumbnailList.SelectedItems.Count;
            SelectionBar.Visibility = _tab == Tab.Thumbnails && n >= 2 ? Visibility.Visible : Visibility.Collapsed;
            SelectionText.Text = n + " pages selected";
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
                if (item.DataContext is PageRow row && row.Thumbnail == null) RequestThumbnail(row);
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
            PanelTitleText.Text = tab switch { Tab.Bookmarks => "Bookmarks", Tab.Layers => "Layers", _ => "Pages" };
            PanelCountChip.Visibility = tab == Tab.Thumbnails ? Visibility.Visible : Visibility.Collapsed;
            PageActionsBar.Visibility = tab == Tab.Thumbnails ? Visibility.Visible : Visibility.Collapsed;
            UpdateSelectionBar();
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
            PanelSourceText.Text = path == null ? "" : "Source: " + Path.GetFileName(path);
            if (path == null)
            {
                ShowEmpty("No document is open.");
                return;
            }

            if (_tab == Tab.Bookmarks)
            {
                if (!_bookmarks.TryGetValue(path, out var task))
                    _bookmarks[path] = task = Task.Run(() => SafeRead(() => PdfOutlineService.ReadBookmarks(path), Array.Empty<PdfBookmarkNode>()));
                var nodes = await task;
                if (_tab != Tab.Bookmarks || !string.Equals(path, _sourcePath, StringComparison.OrdinalIgnoreCase)) return;
                if (nodes.Count == 0) { ShowEmpty("This document has no bookmarks."); return; }
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
                if (!info.HasLayers) { ShowEmpty("This document has no layers."); return; }
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

        // Mỗi ô đang hiện xin 1 thumbnail; ô bị cuộn khỏi panel (container bị tái dùng / unload) thì huỷ lượt chưa vẽ
        // xong — kéo thanh cuộn panel qua hàng trăm trang không còn để lại hàng trăm lượt vẽ trang không ai xem.
        private readonly Dictionary<PageRow, System.Threading.CancellationTokenSource> _thumbnailRequests = new();

        private void RequestThumbnail(PageRow row)
        {
            if (row.Thumbnail != null || _thumbnailRequests.ContainsKey(row)) return;
            var cts = new System.Threading.CancellationTokenSource();
            _thumbnailRequests[row] = cts;
            _ = LoadThumbnailAsync(row, cts);
        }

        private async Task LoadThumbnailAsync(PageRow row, System.Threading.CancellationTokenSource cts)
        {
            try { await ThumbnailCache.LoadPreviewAsync(row, cts.Token, PdfRenderPriority.Thumbnail); }
            finally
            {
                if (_thumbnailRequests.TryGetValue(row, out var current) && ReferenceEquals(current, cts))
                    _thumbnailRequests.Remove(row);
                cts.Dispose();
            }
        }

        private void CancelThumbnail(PageRow row)
        {
            if (!_thumbnailRequests.Remove(row, out var cts)) return;
            cts.Cancel();
        }

        private void ThumbnailImage_Loaded(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is PageRow { Thumbnail: null } row) RequestThumbnail(row);
        }

        private void ThumbnailImage_Unloaded(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is PageRow row) CancelThumbnail(row);
        }

        private void ThumbnailImage_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is PageRow old) CancelThumbnail(old);
            if (e.NewValue is PageRow { Thumbnail: null } row && ((FrameworkElement)sender).IsLoaded) RequestThumbnail(row);
        }

        private void ThumbnailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSelectionBar();
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
        public const string LockedToolTip = "This layer is locked; its visibility cannot be changed";
        public const string UsageToolTip = "This layer is controlled by the file's /AS (usage) rules while viewing; its visibility cannot be changed";

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
