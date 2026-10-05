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
using XTStyle.Controls;
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
        private enum Tab { Thumbnails, Bookmarks, Layers, Find, Comments, Sheets, History }

        private Tab _tab = Tab.Thumbnails;
        private DocumentGroup? _group;
        private string? _sourcePath;
        private bool _syncingSelection;
        private readonly Dictionary<string, Task<IReadOnlyList<PdfBookmarkNode>>> _bookmarks = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task<PdfLayerInfo>> _layers = new(StringComparer.OrdinalIgnoreCase);

        public ReaderSidePanel()
        {
            InitializeComponent();
            LayersView.HiddenChanged += (path, info, hidden) => LayerHiddenChanged?.Invoke(path, info, hidden);
            CommentsView.CommentActivated += c => CommentActivated?.Invoke(c);
            CommentsView.ResolvedToggled += (c, resolved) => CommentResolvedToggled?.Invoke(c, resolved);
            CommentsView.ReplySubmitted += (c, text) => CommentReplySubmitted?.Invoke(c, text);
            CommentsView.EditRequested += (c, text) => CommentEditRequested?.Invoke(c, text);
            CommentsView.DeleteRequested += c => CommentDeleteRequested?.Invoke(c);
            HistoryView.CountChanged += count => { _historyCount = count; if (_tab == Tab.History) UpdateCount(); };
            SheetsView.PageActivated += row => PageActivated?.Invoke(row);
            SheetsView.SplitRequested += parts => SheetSplitRequested?.Invoke(parts);
            SheetsView.ReadInfoRequested += () => SheetReadInfoRequested?.Invoke();
            SheetsView.PageLabelsRequested += () => SheetPageLabelsRequested?.Invoke();
            SheetsView.LinkNumbersRequested += () => SheetLinkNumbersRequested?.Invoke();
            SheetsView.CountChanged += count => { _sheetCount = count; if (_tab == Tab.Sheets) UpdateCount(); };
            CommentsView.CountChanged += count => { _commentCount = count; if (_tab == Tab.Comments) UpdateCount(); };
            LayersView.ManageRequested += () => ManageLayersRequested?.Invoke();
            LayersView.ExportViewRequested += (names, view) => ExportLayerViewRequested?.Invoke(names, view);
            LayersView.CountChanged += count =>
            {
                _layerCount = count;
                UpdateCount();
            };
        }

        private int? _layerCount, _commentCount, _sheetCount, _historyCount;

        private void HistoryTab_Click(object sender, RoutedEventArgs e) => SetTab(Tab.History, toggleIfAlreadyOpen: true);

        /// <summary>Tách file theo Hạng mục / Subset / DWG từ tab Sheets: (nhãn, các trang) của từng file.</summary>
        internal event Action<IReadOnlyList<(string Label, IReadOnlyList<PageRow> Pages)>>? SheetSplitRequested;

        /// <summary>Tab Sheets → "Read info…".</summary>
        internal event Action? SheetReadInfoRequested;
        /// <summary>Tab Sheets → "Page labels".</summary>
        internal event Action? SheetPageLabelsRequested;
        /// <summary>Tab Sheets → "Link numbers".</summary>
        internal event Action? SheetLinkNumbersRequested;

        /// <summary>Đọc lại danh sách bản vẽ (sau khi ghi thông tin sheet vào file).</summary>
        internal Task RefreshSheetsAsync() => SheetsView.SetGroupAsync(_group);

        /// <summary>Menu thumbnail: chọn mọi trang cùng khổ giấy với trang đang chọn (để xoay / xuất / in riêng).</summary>
        private void SelectSameSize_Click(object sender, RoutedEventArgs e)
        {
            if (_group == null || ThumbnailList.SelectedItem is not PageRow current || current.PageWidthPoints is not > 0 || current.PageHeightPoints is not > 0) return;
            string name = PdfExportService.SizeName(current.PageWidthPoints.Value, current.PageHeightPoints.Value).Name;
            ThumbnailList.SelectedItems.Clear();
            foreach (var page in _group.Pages)
                if (page.PageWidthPoints is > 0 && page.PageHeightPoints is > 0 &&
                    PdfExportService.SizeName(page.PageWidthPoints.Value, page.PageHeightPoints.Value).Name == name)
                    ThumbnailList.SelectedItems.Add(page);
        }

        private void SheetsTab_Click(object sender, RoutedEventArgs e) => SetTab(Tab.Sheets, toggleIfAlreadyOpen: true);
        private string _layerPathsKey = "";

        /// <summary>Bấm 1 thumbnail (không giữ Ctrl/Shift) → nhảy tới trang đó.</summary>
        internal event Action<PageRow>? PageActivated;
        /// <summary>Bấm 1 bookmark → (file nguồn, số trang 1-based trong file đó).</summary>
        internal event Action<string, int>? BookmarkActivated;
        /// <summary>Đổi layer đang tắt của 1 file: (file, thông tin layer của file, tập tắt mới).</summary>
        internal event Action<string, PdfLayerInfo, IReadOnlySet<string>>? LayerHiddenChanged;
        /// <summary>Bấm "Manage…" ở tab Layers.</summary>
        internal event Action? ManageLayersRequested;
        /// <summary>Xuất PDF theo View layer hiện tại: (tên layer đang tắt, tên View).</summary>
        internal event Action<IReadOnlySet<string>, string>? ExportLayerViewRequested;
        /// <summary>Panel vừa tự ghi thẳng vào 1 file nguồn (sửa bookmark) — ReaderWindow cập nhật lại dấu
        /// (size/giờ ghi) đã biết của file đó, để không tự báo nhầm "đã đổi trên đĩa".</summary>
        internal event Action<string>? SourceFileWritten;

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
            try { DragGhost.Run(ThumbnailList, data, pages[0].Thumbnail, pages.Count); }
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

        /// <summary>Trang đang xem trong Viewer — dùng làm mặc định khi "Add bookmark".</summary>
        private PageRow? _currentRow;

        /// <summary>Viewer vừa đổi trang đang xem.</summary>
        internal void SetCurrent(DocumentGroup? group, PageRow? row)
        {
            _currentRow = row;
            if (!ReferenceEquals(_group, group))
            {
                if (_group != null) _group.Pages.CollectionChanged -= OnPagesChanged;
                _group = group;
                if (_group != null) _group.Pages.CollectionChanged += OnPagesChanged;
                ThumbnailList.ItemsSource = group?.Pages;
                UpdateCount();
                if (_tab == Tab.Layers) _ = RefreshLayersAsync(force: false); // đổi window (tab file): tập file khác
                if (_tab == Tab.Comments) _ = RefreshCommentsAsync();
                if (_tab == Tab.Sheets) _ = SheetsView.SetGroupAsync(_group);
                HistoryView.SetGroup(_group);
            }
            if (_tab == Tab.Sheets) SheetsView.SelectRow(row);

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

        private void OnPagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateCount();
            if (_tab == Tab.Layers) _ = RefreshLayersAsync(force: false);
            if (_tab == Tab.Sheets) _ = SheetsView.SetGroupAsync(_group);
        }

        private void UpdateCount()
        {
            string count = _tab == Tab.Layers ? _layerCount?.ToString() ?? "" : _tab == Tab.Comments ? _commentCount?.ToString() ?? "" : _tab == Tab.Sheets ? _sheetCount?.ToString() ?? "" : _tab == Tab.History ? _historyCount?.ToString() ?? ""
                : _tab == Tab.Thumbnails && _group != null ? _group.Pages.Count.ToString() : "";
            string title = _tab switch { Tab.Bookmarks => "Bookmarks", Tab.Layers => "Layers", Tab.Find => "Find", Tab.Comments => "Comments", Tab.Sheets => "Sheets", Tab.History => "History", _ => "Pages" };
            PanelTitleText.Text = count.Length > 0 ? $"{title} ({count})" : title;
        }

        internal event Action? CollapseRequested;
        private void CollapsePanel_Click(object sender, RoutedEventArgs e) => CollapseRequested?.Invoke();

        /// <summary>Hides the list part and keeps only the icon rail (Foxit style); clicking a rail tab brings it back.</summary>
        internal void SetCollapsed(bool collapsed) => PanelContent.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

        private bool IsCollapsed => PanelContent.Visibility != Visibility.Visible;

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
            if (_tab == Tab.Layers) { if (layers) _ = RefreshLayersAsync(force: true); }
            else if (string.Equals(path, _sourcePath, StringComparison.OrdinalIgnoreCase)) _ = RefreshSourceTabAsync();
        }

        /// <summary>Trạng thái layer của file vừa đổi (từ nơi khác ngoài panel): cập nhật ô chọn.</summary>
        internal void SyncLayerStates() => LayersView.SyncFromStore();

        /// <summary>Yêu cầu thumbnail cho các trang đang hiện trong panel (sau khi thumbnail bị bỏ vì đổi layer).</summary>
        internal void RequestVisibleThumbnails()
        {
            foreach (var item in Services.VisualTreeHelpers.FindVisualChildren<ListBoxItem>(ThumbnailList))
                if (item.DataContext is PageRow row && row.Thumbnail == null) RequestThumbnail(row);
        }

        // ── Đổi tab ───────────────────────────────────────────────────

        /// <summary>Chuyển tab theo tên ("Pages", "Bookmarks", "Layers") — cho bảng lệnh.</summary>
        internal void ShowPanel(string name)
            => SetTab(name switch { "Bookmarks" => Tab.Bookmarks, "Layers" => Tab.Layers, "Find" => Tab.Find, "Comments" => Tab.Comments, "Sheets" => Tab.Sheets, "History" => Tab.History, _ => Tab.Thumbnails }, toggleIfAlreadyOpen: false);

        private void CommentsTab_Click(object sender, RoutedEventArgs e) => SetTab(Tab.Comments, toggleIfAlreadyOpen: true);

        /// <summary>Chú thích của các file trong window đang xem (nạp nền, có cache).</summary>
        private Task RefreshCommentsAsync()
        {
            var paths = _group == null ? new List<string>()
                : _group.Pages.Select(p => p.SourcePath).Where(p => !BlankPageService.IsBlankFile(p) && File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return CommentsView.SetFilesAsync(paths);
        }

        /// <summary>File vừa được sửa (xoay trang, lưu…): đọc lại chú thích nếu tab đang mở.</summary>
        internal void OnSourceEdited(string path)
        {
            if (_tab == Tab.Comments) _ = RefreshCommentsAsync();
        }

        private bool _commentsRefreshQueued;

        /// <summary>Chú thích của 1 trang đổi (sửa / undo / đọc xong / ảnh xong): thumbnail vẽ lại, tab Comments đọc lại (gộp nhiều lần báo).</summary>
        internal void OnAnnotationsChanged(string path, int page)
        {
            Controls.PageThumbnailImage.Invalidate(path, page);
            if (_tab != Tab.Comments || _commentsRefreshQueued) return;
            _commentsRefreshQueued = true;
            _ = Dispatcher.InvokeAsync(() =>
            {
                _commentsRefreshQueued = false;
                if (_tab == Tab.Comments) _ = RefreshCommentsAsync();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        internal event Action<CommentInfo>? CommentActivated;
        internal event Action<CommentInfo, bool>? CommentResolvedToggled;
        internal event Action<CommentInfo, string>? CommentReplySubmitted;
        internal event Action<CommentInfo, string>? CommentEditRequested;
        internal event Action<CommentInfo>? CommentDeleteRequested;

        private void FindTab_Click(object sender, RoutedEventArgs e) => SetTab(Tab.Find, toggleIfAlreadyOpen: true);

        /// <summary>Panel Find (ReaderWindow nối sự kiện / cấp danh sách file).</summary>
        internal FindPanel Find => FindView;

        private void ThumbnailTab_Click(object sender, RoutedEventArgs e) => SetTab(Tab.Thumbnails, toggleIfAlreadyOpen: true);
        private void BookmarkTab_Click(object sender, RoutedEventArgs e) => SetTab(Tab.Bookmarks, toggleIfAlreadyOpen: true);
        private void LayerTab_Click(object sender, RoutedEventArgs e) => SetTab(Tab.Layers, toggleIfAlreadyOpen: true);

        /// <summary>Chọn 1 tab khác (Pages/Bookmarks/Layers) — ReaderWindow đóng trang Settings nếu đang mở.</summary>
        internal event Action? RailTabChosen;
        /// <summary>Tab đổi: true = tab cần panel rộng (Layers/Find/Comments/Bookmarks), false = Pages.</summary>
        internal event Action<bool>? WideTabChanged;

        private void SetTab(Tab tab, bool toggleIfAlreadyOpen = false)
        {
            // Nhấn lại chính tab đang mở = thu panel về rail. Khi rail đang thu, nhấn tab đó mở lại như bình thường.
            if (toggleIfAlreadyOpen && _tab == tab && !IsCollapsed)
            {
                CollapseRequested?.Invoke();
                return;
            }
            RailTabChosen?.Invoke();
            _tab = tab;
            ThumbnailTabButton.Tag = tab == Tab.Thumbnails ? "Active" : null;
            BookmarkTabButton.Tag = tab == Tab.Bookmarks ? "Active" : null;
            LayerTabButton.Tag = tab == Tab.Layers ? "Active" : null;
            FindTabButton.Tag = tab == Tab.Find ? "Active" : null;
            FindView.Visibility = tab == Tab.Find ? Visibility.Visible : Visibility.Collapsed;
            HistoryTabButton.Tag = tab == Tab.History ? "Active" : null;
            HistoryView.Visibility = tab == Tab.History ? Visibility.Visible : Visibility.Collapsed;
            SheetsTabButton.Tag = tab == Tab.Sheets ? "Active" : null;
            SheetsView.Visibility = tab == Tab.Sheets ? Visibility.Visible : Visibility.Collapsed;
            CommentsTabButton.Tag = tab == Tab.Comments ? "Active" : null;
            CommentsView.Visibility = tab == Tab.Comments ? Visibility.Visible : Visibility.Collapsed;
            PageActionsBar.Visibility = tab == Tab.Thumbnails ? Visibility.Visible : Visibility.Collapsed;
            UpdateSelectionBar();
            ThumbnailList.Visibility = tab == Tab.Thumbnails ? Visibility.Visible : Visibility.Collapsed;
            BookmarkTree.Visibility = Visibility.Collapsed;
            LayersView.Visibility = tab == Tab.Layers ? Visibility.Visible : Visibility.Collapsed;
            PanelEmptyText.Visibility = Visibility.Collapsed;
            PanelSourceText.Visibility = tab == Tab.Bookmarks ? Visibility.Visible : Visibility.Collapsed;
            BookmarkActionsBar.Visibility = tab == Tab.Bookmarks ? Visibility.Visible : Visibility.Collapsed;
            UpdateCount();
            WideTabChanged?.Invoke(tab != Tab.Thumbnails);
            if (tab == Tab.Comments) _ = RefreshCommentsAsync();
            if (tab == Tab.Sheets) _ = SheetsView.SetGroupAsync(_group);
            if (tab == Tab.Layers) _ = RefreshLayersAsync(force: false);
            else if (tab == Tab.Bookmarks) _ = RefreshSourceTabAsync();
        }

        /// <summary>Nạp layer của mọi file trong window đang xem (bỏ qua nếu tập file không đổi, trừ khi <paramref name="force"/>).</summary>
        private async Task RefreshLayersAsync(bool force)
        {
            if (_tab != Tab.Layers) return;
            var paths = _group == null
                ? new List<string>()
                : _group.Pages.Select(p => p.SourcePath).Where(p => !BlankPageService.IsBlankFile(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string key = string.Join("|", paths);
            if (!force && key == _layerPathsKey && _layerPathsKey.Length > 0)
            {
                LayersView.SyncFromStore();
                return;
            }
            _layerPathsKey = key;
            if (paths.Count == 0) { LayersView.Clear("No document is open."); return; }
            await LayersView.SetFilesAsync(paths, ReadLayersAsync);
        }

        private Task<PdfLayerInfo> ReadLayersAsync(string path)
        {
            if (!_layers.TryGetValue(path, out var task))
                _layers[path] = task = Task.Run(() => SafeRead(() => PdfLayerService.ReadLayers(path), PdfLayerInfo.Empty));
            return task;
        }

        private async Task RefreshSourceTabAsync()
        {
            if (_tab != Tab.Bookmarks) return;
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
        }

        private static T SafeRead<T>(Func<T> read, T fallback)
        {
            try { return read(); }
            catch { return fallback; } // file hỏng/mã hoá: hiện rỗng, không lỗi
        }

        private void ShowEmpty(string text)
        {
            BookmarkTree.Visibility = Visibility.Collapsed;
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

        private void AddBookmark_Click(object sender, RoutedEventArgs e) => _ = AddBookmarkAsync(Array.Empty<int>());

        private void BookmarkAddChild_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is PdfBookmarkNode node) _ = AddBookmarkAsync(node.Path);
        }

        private async Task AddBookmarkAsync(IReadOnlyList<int> parentPath)
        {
            if (_sourcePath is not { } path || _currentRow is not { } row || !string.Equals(row.SourcePath, path, StringComparison.OrdinalIgnoreCase))
            {
                XTGrowl.Info("Open a page from this file first.", Window.GetWindow(this));
                return;
            }
            var prompt = new TextPromptWindow("Add bookmark", "Bookmark name:", $"Page {row.PageNumber}", null) { Owner = Window.GetWindow(this) };
            if (prompt.ShowDialog() != true || prompt.Value is not { } title) return;
            await RunOutlineEditAsync(path, () => PdfOutlineService.AddBookmark(path, parentPath, title, row.PageNumber));
        }

        private void BookmarkRename_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PdfBookmarkNode node || _sourcePath is not { } path) return;
            var prompt = new TextPromptWindow("Rename bookmark", "Bookmark name:", node.Title, null) { Owner = Window.GetWindow(this) };
            if (prompt.ShowDialog() != true || prompt.Value is not { } title) return;
            _ = RunOutlineEditAsync(path, () => PdfOutlineService.RenameBookmark(path, node.Path, title));
        }

        private void BookmarkDelete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PdfBookmarkNode node || _sourcePath is not { } path) return;
            string extra = node.Children.Count > 0 ? $" and its {node.Children.Count} sub-bookmark(s)" : "";
            if (AppDialog.Show(Window.GetWindow(this), $"Delete \"{node.Title}\"{extra}?", "Delete bookmark",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _ = RunOutlineEditAsync(path, () => PdfOutlineService.DeleteBookmark(path, node.Path));
        }

        private void BookmarkMoveUp_Click(object sender, RoutedEventArgs e) => MoveBookmark(sender, -1);
        private void BookmarkMoveDown_Click(object sender, RoutedEventArgs e) => MoveBookmark(sender, +1);

        private void MoveBookmark(object sender, int delta)
        {
            if ((sender as FrameworkElement)?.DataContext is not PdfBookmarkNode node || _sourcePath is not { } path) return;
            _ = RunOutlineEditAsync(path, () => PdfOutlineService.MoveBookmark(path, node.Path, delta));
        }

        /// <summary>Đóng handle PDFium của file → sửa outline bằng iText (thread nền) → mở khoá → đọc lại cây bookmark.
        /// Không đổi ảnh trang (geometryChanged=false), chỉ đổi cây outline.</summary>
        private Task RunOutlineEditAsync(string path, Action edit)
            => RunOutlineEditAsync(path, () => { edit(); return true; });

        private async Task RunOutlineEditAsync(string path, Func<bool> edit)
        {
            if (!await PdfPermissionDialog.RequireAsync(Window.GetWindow(this), new[] { path }, PdfPermissionOperation.Modify)) return;
            if (!await SignedPdfConfirmation.ConfirmAsync(Window.GetWindow(this), new[] { path }, "Update PDF bookmarks", true)) return;
            try
            {
                AnnotationStore.ReleaseReader(path);
                using (await PdfThumbnailService.SuspendDocumentAsync(path, TimeSpan.FromSeconds(3)))
                {
                    bool changed = await Task.Run(edit);
                    if (!changed) return;
                }
            }
            catch (Exception ex)
            {
                XTGrowl.Error("Could not update bookmarks: " + ex.Message, Window.GetWindow(this));
                return;
            }
            SourceFileWritten?.Invoke(path);
            _bookmarks.Remove(path);
            await RefreshSourceTabAsync();
        }
    }
}
