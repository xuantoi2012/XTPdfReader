using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using XTPdfMergeApp.Services;
using static XTPdfMergeApp.Services.VisualTreeHelpers;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;

namespace XTPdfMergeApp
{
    /// <summary>
    /// Phần "cửa sổ đọc chính" của ReaderWindow: tab file đang mở, panel trái (Trang/Bookmark/Layer),
    /// các lệnh ribbon gọi sang cửa sổ chủ (mở file, hoàn tác, cửa sổ ghép) và bật/tắt layer.
    /// </summary>
    public partial class ReaderWindow
    {
        /// <summary>Trang xem gần nhất của từng file — chuyển tab quay lại đúng chỗ đang đọc.</summary>
        private readonly Dictionary<DocumentGroup, PageRow> _lastPageByGroup = new();
        private bool _syncingDocumentTabs;

        private void InitializeShellParts()
        {
            ReaderDocumentTabs.ItemsSource = _groups;
            ReaderSidePanel.PageActivated += row =>
            {
                if (_readerGroup == null || !_readerGroup.Pages.Contains(row)) return;
                NavigateToRow(_readerGroup, row);
            };
            ReaderSidePanel.BookmarkActivated += NavigateToSourcePage;
            ReaderSidePanel.LayerToggled += OnLayerToggled;
            ShowEmptyReaderState();
        }

        private void NavigateToRow(DocumentGroup group, PageRow row)
        {
            if (_readerContinuousMode) ShowReaderContinuous(group, row);
            else _ = ShowPageAsync(group, row, preserveZoomMode: true);
        }

        /// <summary>Gọi mỗi khi trang đang xem đổi (UpdateReaderChrome) — đồng bộ tab file + panel trái.</summary>
        private void OnReaderCurrentPageChanged(DocumentGroup group, PageRow row)
        {
            _lastPageByGroup[group] = row;
            if (!ReferenceEquals(ReaderDocumentTabs.SelectedItem, group))
            {
                _syncingDocumentTabs = true;
                ReaderDocumentTabs.SelectedItem = group;
                ReaderDocumentTabs.ScrollIntoView(group);
                _syncingDocumentTabs = false;
            }
            ReaderSidePanel.SetCurrent(group, row);
        }

        private void ShowEmptyReaderState()
        {
            ReaderEmptyText.Text = "Mở file PDF để xem — nút Mở trên ribbon, hoặc kéo-thả file vào cửa sổ.";
            ReaderEmptyText.Visibility = Visibility.Visible;
            ReaderTitleText.Text = "";
            ReaderPageBox.Text = "";
            ReaderPageTotalText.Text = "";
            ReaderSidePanel.SetCurrent(null, null);
            _syncingDocumentTabs = true;
            ReaderDocumentTabs.SelectedItem = null;
            _syncingDocumentTabs = false;
        }

        /// <summary>File đang xem vừa bị đóng khỏi workspace → chuyển sang file bên cạnh, hết file thì màn trống.</summary>
        private void OnGroupsChanged()
        {
            foreach (var removed in _lastPageByGroup.Keys.Where(g => !_groups.Contains(g)).ToList())
                _lastPageByGroup.Remove(removed);
            if (_readerGroup == null || _groups.Contains(_readerGroup)) return;
            var next = _groups.FirstOrDefault(g => g.Pages.Count > 0);
            if (next == null)
            {
                HideReader();
                return;
            }
            ShowGroup(next);
        }

        private void ShowGroup(DocumentGroup group)
        {
            if (group.Pages.Count == 0) return;
            var row = _lastPageByGroup.TryGetValue(group, out var last) && group.Pages.Contains(last) ? last : group.Pages[0];
            NavigateToRow(group, row);
        }

        private void ReaderDocumentTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingDocumentTabs || ReaderDocumentTabs.SelectedItem is not DocumentGroup group) return;
            if (ReferenceEquals(group, _readerGroup)) return;
            ShowGroup(group);
        }

        private void ReaderCloseDocument_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DocumentGroup group) EditHost?.CloseDocument(group);
            e.Handled = true;
        }

        // ── Lệnh ribbon gọi sang cửa sổ chủ ────────────────────────────

        private async void ReaderOpenFiles_Click(object sender, RoutedEventArgs e)
        {
            if (EditHost != null) await EditHost.OpenFilesAsync();
        }

        private void ReaderUndo_Click(object sender, RoutedEventArgs e) => EditHost?.Undo();
        private void ReaderRedo_Click(object sender, RoutedEventArgs e) => EditHost?.Redo();
        private void ReaderShowMergeWindow_Click(object sender, RoutedEventArgs e) => EditHost?.ShowMergeWindow();

        // ── Bookmark ──────────────────────────────────────────────────

        /// <summary>Bookmark trỏ tới trang <paramref name="pageNumber"/> của file <paramref name="sourcePath"/> —
        /// tìm placement tương ứng trong window đang xem (window có thể đã bị sắp xếp lại/ghép).</summary>
        private void NavigateToSourcePage(string sourcePath, int pageNumber)
        {
            if (_readerGroup == null) return;
            var row = FindRowForSourcePage(_readerGroup.Pages, sourcePath, pageNumber);
            if (row != null) NavigateToRow(_readerGroup, row);
        }

        internal static PageRow? FindRowForSourcePage(IEnumerable<PageRow> pages, string sourcePath, int pageNumber)
            => pages.FirstOrDefault(p => p.PageNumber == pageNumber &&
                                         string.Equals(p.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));

        // ── Layer ─────────────────────────────────────────────────────

        private async void OnLayerToggled(string path, PdfLayerInfo info, string ocgId, bool visible)
        {
            if (EditHost == null) return;
            var hidden = new HashSet<string>(PdfLayerStateStore.GetHiddenOverride(path, out _) ?? info.DefaultHidden);
            if (visible) hidden.Remove(ocgId);
            else hidden.Add(ocgId);
            await EditHost.SetLayerHiddenAsync(path, hidden, info.DefaultHidden);
        }

        /// <summary>Trạng thái layer của file vừa đổi (lease PDFium cũ đã đóng). Trang đang hiện: vẽ lại TẠI CHỖ
        /// (ảnh cũ giữ tới khi ảnh mới xong); trang khác của file: bỏ ảnh để lần hiện sau lấy theo key mới.</summary>
        internal void OnLayerStateChanged(string path)
        {
            bool Matches(PageRow r) => string.Equals(r.SourcePath, path, StringComparison.OrdinalIgnoreCase);

            ReaderSidePanel.RequestVisibleThumbnails();
            var refreshInPlace = new HashSet<PageRow>();
            if (_readerGroup != null && _readerPage != null)
            {
                if (_readerContinuousMode)
                {
                    foreach (var item in FindVisualChildren<ListBoxItem>(ReaderContinuousList))
                        if (item.DataContext is PageRow row && Matches(row) && row.ReaderBitmap != null) refreshInPlace.Add(row);
                }
                else if (Matches(_readerPage) && _readerPage.ReaderBitmap != null)
                {
                    refreshInPlace.Add(_readerPage);
                }
            }

            foreach (var row in _groups.SelectMany(g => g.Pages).Where(Matches))
            {
                if (refreshInPlace.Contains(row)) continue;
                row.ReaderBitmap = null;
                row.ReaderBitmapLoadQueued = false;
            }

            if (_readerGroup == null || _readerPage == null) return;
            ClearReaderTiles();
            foreach (var row in refreshInPlace) _ = RefreshReaderBitmapInPlaceAsync(row);
            if (!_readerContinuousMode && Matches(_readerPage) && refreshInPlace.Count == 0)
                _ = ShowPageAsync(_readerGroup, _readerPage, preserveZoomMode: true);
            ScheduleReaderTileRefresh();
        }
    }
}
