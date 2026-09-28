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
            ThemeToggleButton.IsChecked = ThemeService.IsDark;
            Closing += ReaderWindow_Closing;
            ReaderDocumentTabs.ItemsSource = _groups;
            ReaderSidePanel.PageActivated += row =>
            {
                if (_readerGroup == null || !_readerGroup.Pages.Contains(row)) return;
                NavigateToRow(_readerGroup, row);
            };
            // Hàng thao tác trang của panel Pages → handler sẵn có (chèn/xoá đổi workspace; xoay thật lưu file).
            ReaderSidePanel.InsertPagesRequested += ReaderInsertPages_Click;
            ReaderSidePanel.DeletePagesRequested += ReaderDeletePages_Click;
            ReaderSidePanel.RotateLeftRequested += ReaderPageRotateLeft_Click;
            ReaderSidePanel.RotateRightRequested += ReaderPageRotateRight_Click;
            ReaderSidePanel.ExtractPagesRequested += ReaderExtractPages_Click;
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
            EnsureLoadProgressTimer();
            _lastPageByGroup[group] = row;
            if (!ReferenceEquals(ReaderDocumentTabs.SelectedItem, group))
            {
                _syncingDocumentTabs = true;
                ReaderDocumentTabs.SelectedItem = group;
                ReaderDocumentTabs.ScrollIntoView(group);
                _syncingDocumentTabs = false;
            }
            ScheduleNearbyThumbnailWarmup();
            if (ReaderContinuousView.IsFastScrolling)
            {
                // Đang cuộn nhanh: panel trái không chạy theo từng trang lướt qua (mỗi lần chạy theo lại xin thumbnail
                // cho cả chục trang không ai xem) — đồng bộ 1 lần khi cuộn dừng (SyncSidePanelAfterScroll).
                _pendingSidePanelSync = (group, row);
                return;
            }
            ReaderSidePanel.SetCurrent(group, row);
        }

        private (DocumentGroup Group, PageRow Row)? _pendingSidePanelSync;

        private System.Windows.Threading.DispatcherTimer? _loadProgressTimer;
        private bool _waitingForFullLoad;

        // ── Làm ấm thumbnail quanh trang đang xem (Foxit "xoay xong là mượt") ──────────────────────
        private const int NearbyThumbnailWarmupPages = 16;
        private System.Windows.Threading.DispatcherTimer? _thumbnailWarmupTimer;
        private System.Threading.CancellationTokenSource _thumbnailWarmupCts = new();

        /// <summary>Hẹn làm ấm (400 ms sau lần đổi trang cuối — cuộn liên tục không khởi động lại liên tục).</summary>
        private void ScheduleNearbyThumbnailWarmup()
        {
            if (_thumbnailWarmupTimer == null)
            {
                _thumbnailWarmupTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(400)
                };
                _thumbnailWarmupTimer.Tick += (_, _) =>
                {
                    _thumbnailWarmupTimer!.Stop();
                    if (ReaderContinuousView.IsFastScrolling) { _thumbnailWarmupTimer.Start(); return; }
                    _ = WarmNearbyThumbnailsAsync();
                };
            }
            _thumbnailWarmupCts.Cancel(); // trang đổi: lượt làm ấm cũ (quanh trang cũ) không còn đúng chỗ
            _thumbnailWarmupTimer.Stop();
            _thumbnailWarmupTimer.Start();
        }

        /// <summary>
        /// Vẽ trước ảnh 340 px (dùng chung cho panel thumbnail và ảnh xem trước của Viewer) cho ±16 trang quanh trang
        /// đang xem, gần trước xa sau, ưu tiên Background (nhường ảnh đang hiện; tự đứng chờ khi zoom/pan — #5), chạy song
        /// song trên (số bản PDFium − 1) luồng để luôn chừa 1 bản cho trang đang xem. Chỉ chạy khi file đã nạp xong vào RAM:
        /// trước đó băng thông mạng dành cho trang đang xem và đọc nền.
        /// </summary>
        private async System.Threading.Tasks.Task WarmNearbyThumbnailsAsync()
        {
            _thumbnailWarmupCts.Cancel();
            _thumbnailWarmupCts = new System.Threading.CancellationTokenSource();
            var token = _thumbnailWarmupCts.Token;
            if (_readerGroup is not { } group || _readerPage is not { } current) return;
            int center = group.Pages.IndexOf(current);
            if (center < 0) return;

            var order = new List<PageRow>();
            for (int d = 1; d <= NearbyThumbnailWarmupPages; d++)
                foreach (int i in new[] { center + d, center - d })
                    if (i >= 0 && i < group.Pages.Count) order.Add(group.Pages[i]);
            // Không gồm trang đang xem: Viewer đã xin nó ở ưu tiên Visible — gộp vào lượt Background này sẽ bắt nó chờ.

            bool Loaded(PageRow row)
            {
                try { return PdfFileBuffer.GetLoadedFraction(System.IO.Path.GetFullPath(row.SourcePath)) is >= 1; }
                catch { return false; }
            }

            int parallel = Math.Max(1, PdfiumPool.Count - 1);
            using var slots = new System.Threading.SemaphoreSlim(parallel);
            var work = new List<System.Threading.Tasks.Task>();
            foreach (var row in order)
            {
                if (token.IsCancellationRequested) break;
                if (row.Thumbnail != null || !Loaded(row)) continue;
                try { await slots.WaitAsync(token); }
                catch (OperationCanceledException) { break; }
                work.Add(WarmOneAsync(row));
            }
            try { await System.Threading.Tasks.Task.WhenAll(work); } catch { }

            async System.Threading.Tasks.Task WarmOneAsync(PageRow row)
            {
                try { await ThumbnailCache.LoadPreviewAsync(row, token, PdfRenderPriority.Background); }
                finally { slots.Release(); }
            }
        }

        /// <summary>Thanh trạng thái: "Nạp vào RAM x%" của file đang xem, cập nhật 2 lần/giây, ẩn khi nạp xong.</summary>
        private void EnsureLoadProgressTimer()
        {
            if (_loadProgressTimer != null) return;
            _loadProgressTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _loadProgressTimer.Tick += (_, _) => UpdateLoadProgress();
            _loadProgressTimer.Start();
        }

        private void UpdateLoadProgress()
        {
            double? loaded = null;
            if (_readerPage?.SourcePath is { Length: > 0 } path)
            {
                try { loaded = PdfFileBuffer.GetLoadedFraction(System.IO.Path.GetFullPath(path)); }
                catch { loaded = null; }
            }
            if (loaded is { } fraction && fraction < 1)
            {
                ReaderLoadProgressText.Text = $"Nạp vào RAM {fraction:P0}";
                ReaderLoadProgressText.Visibility = Visibility.Visible;
                _waitingForFullLoad = true;
            }
            else
            {
                ReaderLoadProgressText.Visibility = Visibility.Collapsed;
                // Vừa nạp xong cả file: giờ mới làm ấm thumbnail quanh trang đang xem (trước đó băng thông dành cho trang).
                if (_waitingForFullLoad && loaded is >= 1) ScheduleNearbyThumbnailWarmup();
                _waitingForFullLoad = false;
            }
        }

        /// <summary>Gọi khi cuộn nhanh vừa dừng: đưa panel trái tới trang đang xem, rồi xin thumbnail cho các ô đang hiện
        /// còn trống (kể cả ô mà lượt vẽ dùng chung với Viewer vừa bị huỷ vì trang rời màn hình).</summary>
        private void SyncSidePanelAfterScroll()
        {
            if (_pendingSidePanelSync is not { } pending) return;
            _pendingSidePanelSync = null;
            if (!ReferenceEquals(pending.Group, _readerGroup)) return;
            ReaderSidePanel.SetCurrent(pending.Group, _readerPage ?? pending.Row);
            _ = Dispatcher.InvokeAsync(ReaderSidePanel.RequestVisibleThumbnails, System.Windows.Threading.DispatcherPriority.Background);
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

        /// <summary>Bấm chuột phải lên tab thì chọn tab đó trước, để menu ("Open in default app"…) tác động đúng file.</summary>
        private void ReaderTab_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DocumentGroup group && !ReferenceEquals(ReaderDocumentTabs.SelectedItem, group))
                ReaderDocumentTabs.SelectedItem = group;
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
        private void ReaderShowMergeWindow_Click(object sender, RoutedEventArgs e) => OpenMergeWindow();

        // ── Cửa sổ ghép (phụ) — chỉ tạo ở đây, đóng là huỷ thật ─────────────

        private MergeWorkspaceWindow? _mergeWindow;

        /// <summary>Mở (hoặc đưa lên trước) cửa sổ ghép nhiều file. Cửa sổ ghép mượn <see cref="Session"/>
        /// nên có sẵn mọi file đang mở ở đây; đóng nó không ảnh hưởng gì tới phiên làm việc.</summary>
        internal void OpenMergeWindow()
        {
            if (_mergeWindow == null)
            {
                _mergeWindow = new MergeWorkspaceWindow(Session);
                _mergeWindow.Closed += (_, _) => _mergeWindow = null;
                _mergeWindow.Show();
            }
            if (_mergeWindow.WindowState == WindowState.Minimized) _mergeWindow.WindowState = WindowState.Normal;
            _mergeWindow.Activate();
        }

        /// <summary>Đưa cửa sổ đọc lên trước (vd double-click 1 trang trong cửa sổ ghép).</summary>
        internal void BringToFront()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        private void ReaderWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _mergeWindow?.Close();
            ShutdownReader();
        }

        // ── Theme ─────────────────────────────────────────────────────

        private void ThemeToggleButton_Changed(object sender, RoutedEventArgs e)
        {
            bool dark = ThemeToggleButton.IsChecked == true;
            if (dark == ThemeService.IsDark) return;
            ThemeService.Apply(dark);
            MergeAppSettingsStore.SetTheme(dark ? "Dark" : "Light");
        }

        // ── Kéo-thả file PDF vào cửa sổ để mở ────────────────────────────

        private void ReaderWindow_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = GetDroppedPdfs(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void ReaderWindow_Drop(object sender, DragEventArgs e)
        {
            var files = GetDroppedPdfs(e);
            if (files.Length == 0) return;
            e.Handled = true;
            await Session.OpenFilesInReaderAsync(files);
        }

        private static string[] GetDroppedPdfs(DragEventArgs e)
            => e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths
                ? paths.Where(p => string.Equals(System.IO.Path.GetExtension(p), ".pdf", StringComparison.OrdinalIgnoreCase)).ToArray()
                : Array.Empty<string>();

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
            if (_readerGroup != null && _readerPage != null && !_readerContinuousMode &&
                Matches(_readerPage) && _readerPage.ReaderBitmap != null)
                refreshInPlace.Add(_readerPage);

            foreach (var row in _groups.SelectMany(g => g.Pages).Where(Matches))
            {
                if (refreshInPlace.Contains(row)) continue;
                row.ReaderBitmap = null;
                row.ReaderBitmapLoadQueued = false;
            }

            if (_readerGroup == null || _readerPage == null) return;
            if (_readerContinuousMode)
            {
                // Vùng vẽ giữ ảnh cũ trên màn hình tới khi ảnh theo trạng thái layer mới xong.
                ReaderContinuousView.InvalidatePages(Matches, dropImages: false);
                return;
            }
            ClearReaderTiles();
            foreach (var row in refreshInPlace) _ = RefreshReaderBitmapInPlaceAsync(row);
            if (!_readerContinuousMode && Matches(_readerPage) && refreshInPlace.Count == 0)
                _ = ShowPageAsync(_readerGroup, _readerPage, preserveZoomMode: true);
            ScheduleReaderTileRefresh();
        }
    }
}
