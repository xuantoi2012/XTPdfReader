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
            Title = AppInfo.DisplayName;
            InitializeSettings();
            InitializeStart();
            InitializeMerge();
            InitializeFind();
            InitializeRecovery();
            InitializeToolbarOverflow();
            Closing += ReaderWindow_Closing;
            InitializeDocumentTabs();
            ReaderSidePanel.PageActivated += row =>
            {
                if (_readerGroup == null || !_readerGroup.Pages.Contains(row)) return;
                NavigateToRow(_readerGroup, row);
            };
            // Hàng thao tác trang của panel Pages → handler sẵn có (chèn/xoá đổi workspace; xoay thật lưu file).
            ReaderSidePanel.InsertPagesRequested += ReaderInsertMenu_Click;
            ReaderSidePanel.DeletePagesRequested += ReaderDeletePages_Click;
            ReaderSidePanel.RotateLeftRequested += ReaderPageRotateLeft_Click;
            ReaderSidePanel.RotateRightRequested += ReaderPageRotateRight_Click;
            ReaderSidePanel.ExtractPagesRequested += ReaderExtractPages_Click;
            ReaderSidePanel.PageCommandRequested += OnPageCommand;
            ReaderSidePanel.PagesDropped += (data, index, copy) =>
            {
                if (_readerGroup == null || EditHost == null) return;
                var target = _readerGroup;
                var result = EditHost.MovePages(data.Source, target, data.Pages, index, copy);
                if (result.Count > 0 && ReferenceEquals(_readerGroup, target)) ReaderSidePanel.SelectPages(result);
            };
            ReaderSidePanel.HasPageClipboard = () => EditHost?.HasPageClipboard == true;
            ReaderSidePanel.BookmarkActivated += NavigateToSourcePage;
            ReaderSidePanel.CommentActivated += c => NavigateToSourcePage(c.Path, c.Page);
            ReaderSidePanel.CommentResolvedToggled += async (c, resolved) =>
            {
                if (EditHost != null) await EditHost.SetCommentResolvedAsync(c.Path, c.Page, c.Name, resolved);
            };
            ReaderSidePanel.CommentReplySubmitted += (c, text) => AddInlineReply(c, text);
            ReaderSidePanel.CommentEditRequested += (c, text) => EditInlineComment(c, text);
            ReaderSidePanel.CommentDeleteRequested += c => DeleteInlineComment(c);
            ReaderSidePanel.LayerHiddenChanged += OnLayerHiddenChanged;
            ReaderSidePanel.ExportLayerViewRequested += OnExportLayerView;
            ReaderSidePanel.SourceFileWritten += path => Session.RefreshDiskStamp(path);
            AnnotationStore.Changed += OnAnnotationsChanged;
            ShowEmptyReaderState();
        }

        private void NavigateToRow(DocumentGroup group, PageRow row)
        {
            if (_readerContinuousMode) ShowReaderContinuous(group, row);
            else _ = ShowPageAsync(group, row, preserveZoomMode: true);
        }

        private System.Windows.Threading.DispatcherTimer? _positionTimer;
        private (string Path, int Page, int Mode, double Zoom)? _pendingPosition;

        /// <summary>Nhớ trang + kiểu zoom đang xem của file (ghi sau ~1,2 s không đổi, và khi đóng cửa sổ) để mở lại về đúng chỗ.</summary>
        private void ScheduleViewPositionSave(PageRow row)
        {
            if (string.IsNullOrEmpty(row.SourcePath) || BlankPageService.IsBlankFile(row.SourcePath)) return;
            _pendingPosition = (row.SourcePath, row.PageNumber, (int)_readerZoomMode, _readerZoom);
            if (_positionTimer == null)
            {
                _positionTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                _positionTimer.Tick += (_, _) => FlushViewPosition();
            }
            _positionTimer.Stop();
            _positionTimer.Start();
        }

        private void FlushViewPosition()
        {
            _positionTimer?.Stop();
            if (_pendingPosition is { } p)
            {
                _pendingPosition = null;
                try { ViewPositionStore.Note(p.Path, p.Page, p.Mode, p.Zoom); } catch { /* không ghi được: bỏ qua */ }
            }
        }

        /// <summary>Trang đầu tiên hiện khi mở file: về trang/zoom đã nhớ nếu có, không thì trang 1 theo cài đặt mặc định.</summary>
        internal System.Threading.Tasks.Task ShowFirstPageAsync(DocumentGroup group)
        {
            var row = group.Pages[0];
            bool preserve = false;
            var saved = string.IsNullOrEmpty(group.SourcePath) ? null : ViewPositionStore.Find(group.SourcePath);
            if (saved != null && saved.Page >= 1 && saved.Page <= group.Pages.Count && group.Pages[saved.Page - 1].SourcePath == group.SourcePath
                && Enum.IsDefined(typeof(ReaderZoomMode), saved.ZoomMode))
            {
                row = group.Pages[saved.Page - 1];
                _readerZoomMode = (ReaderZoomMode)saved.ZoomMode;
                if (_readerZoomMode == ReaderZoomMode.Manual) _readerZoom = Math.Clamp(saved.Zoom, ReaderMinZoom, ReaderMaxZoom);
                preserve = true;
            }
            return ShowPageAsync(group, row, preserveZoomMode: preserve);
        }

        /// <summary>Gọi mỗi khi trang đang xem đổi (UpdateReaderChrome) — đồng bộ tab file + panel trái.</summary>
        private void OnReaderCurrentPageChanged(DocumentGroup group, PageRow row)
        {
            ScheduleRecovery();
            EnsureLoadProgressTimer();
            _lastPageByGroup[group] = row;
            ScheduleViewPositionSave(row);
            UpdateDocumentTabs(group);
            if (!ReferenceEquals(ReaderDocumentTabs.SelectedItem, group))
            {
                _syncingDocumentTabs = true;
                ReaderDocumentTabs.SelectedItem = group;
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
        private const int NearbyThumbnailWarmupPages = 4;
        private System.Windows.Threading.DispatcherTimer? _thumbnailWarmupTimer;
        private System.Threading.CancellationTokenSource? _thumbnailWarmupCts;

        private void StopNearbyThumbnailWarmup()
        {
            _thumbnailWarmupTimer?.Stop();
            _thumbnailWarmupCts?.Cancel();
        }

        /// <summary>Hẹn làm ấm 600 ms sau lần đổi trang cuối.</summary>
        private void ScheduleNearbyThumbnailWarmup()
        {
            if (ReaderContinuousView.IsRenderingSuspended) return;
            if (_thumbnailWarmupTimer == null)
            {
                _thumbnailWarmupTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(600)
                };
                _thumbnailWarmupTimer.Tick += (_, _) =>
                {
                    _thumbnailWarmupTimer!.Stop();
                    if (ReaderContinuousView.IsRenderingSuspended) return;
                    if (ReaderContinuousView.IsFastScrolling) { _thumbnailWarmupTimer.Start(); return; }
                    _ = WarmNearbyThumbnailsAsync();
                };
            }
            _thumbnailWarmupCts?.Cancel(); // trang đổi: lượt làm ấm cũ (quanh trang cũ) không còn đúng chỗ
            _thumbnailWarmupTimer.Stop();
            _thumbnailWarmupTimer.Start();
        }

        /// <summary>
        /// Vẽ trước ảnh 340 px (dùng chung cho panel thumbnail và ảnh xem trước của Viewer) cho ±4 trang quanh trang
        /// đang xem, gần trước xa sau, ưu tiên Background (nhường ảnh đang hiện; tự đứng chờ khi zoom/pan — #5), chạy song
        /// song trên (số bản PDFium − 1) luồng để luôn chừa 1 bản cho trang đang xem. Chỉ chạy khi file đã nạp xong vào RAM:
        /// trước đó băng thông mạng dành cho trang đang xem và đọc nền.
        /// </summary>
        private async System.Threading.Tasks.Task WarmNearbyThumbnailsAsync()
        {
            if (ReaderContinuousView.IsRenderingSuspended) return;
            _thumbnailWarmupCts?.Cancel();
            using var cancellation = new System.Threading.CancellationTokenSource();
            _thumbnailWarmupCts = cancellation;
            var token = cancellation.Token;
            try
            {
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
            finally
            {
                if (ReferenceEquals(_thumbnailWarmupCts, cancellation)) _thumbnailWarmupCts = null;
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
                ReaderLoadProgressText.Text = $"Loading into memory {fraction:P0}";
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
            ReaderEmptyText.Text = "Open a PDF with Ctrl+O, or drag files into the window.";
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
            UpdateDocumentTabs(_readerGroup);
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
            ShowStart(false);
            if (ReferenceEquals(group, _readerGroup)) return;
            Dispatcher.BeginInvoke(new Action(UpdateDiskBanner), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            ShowGroup(group);
        }

        /// <summary>Bấm chuột phải lên tab thì chọn tab đó trước, để menu ("Open in default app"…) tác động đúng file.</summary>
        private void ReaderTab_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DocumentGroup group && !ReferenceEquals(ReaderDocumentTabs.SelectedItem, group))
                ReaderDocumentTabs.SelectedItem = group;
        }

        /// <summary>Bấm lên tab file đang xem Start (tab đó đã "selected" sẵn trong ListBox nên SelectionChanged không tự bắn):
        /// đóng Start thủ công, như bấm lên 1 tab thật.</summary>
        private void ReaderTab_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => ShowStart(false);

        private async void ReaderCloseDocument_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is not DocumentGroup group || EditHost == null) return;
            if (group.IsDirty)
            {
                var answer = AppDialog.Show(this, $"Save changes to \"{group.FileName}\" before closing?", "Unsaved changes",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Cancel) return;
                if (answer == MessageBoxResult.Yes && !await EditHost.SaveGroupAsync(group, saveAs: false)) return;
            }
            EditHost.CloseDocument(group);
        }

        // ── Insert ▾: từ file đang mở / từ đĩa / trang trắng (chèn SAU vùng chọn, hoặc sau trang đang xem) ──────

        private void ReaderInsertMenu_Click(object sender, RoutedEventArgs e)
        {
            if (_readerGroup == null || _readerPage == null || EditHost == null) return;
            var group = _readerGroup;
            var reference = _readerPage;
            var selected = ReaderSidePanel.SelectedPages.Select(p => group.Pages.IndexOf(p)).Where(i => i >= 0).ToList();
            int insertIndex = (selected.Count > 0 ? selected.Max() : group.Pages.IndexOf(reference)) + 1;

            void SelectResult(IReadOnlyList<PageRow> result)
            {
                if (result.Count > 0 && ReferenceEquals(_readerGroup, group)) ReaderSidePanel.SelectPages(result);
            }

            var menu = new ContextMenu();
            var openFiles = new MenuItem { Header = "From an open file" };
            foreach (var other in _groups.Where(g => !ReferenceEquals(g, group) && g.Pages.Count > 0).ToList())
            {
                var source = other;
                var item = new MenuItem { Header = source.FileName };
                item.Click += (_, _) => SelectResult(EditHost.MovePages(source, group, source.Pages.ToList(), insertIndex, copy: true));
                openFiles.Items.Add(item);
            }
            openFiles.IsEnabled = openFiles.Items.Count > 0;
            menu.Items.Add(openFiles);

            var disk = new MenuItem { Header = "From disk…" };
            disk.Click += async (_, _) => await EditHost.InsertPagesFromFileAsync(group, insertIndex);
            menu.Items.Add(disk);

            menu.Items.Add(new Separator());
            var blank = new MenuItem { Header = "Blank page" };
            blank.Click += async (_, _) => SelectResult(await EditHost.InsertBlankPageAsync(group, insertIndex, reference));
            menu.Items.Add(blank);

            menu.PlacementTarget = sender as UIElement ?? ReaderSidePanel;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // ── Kéo trang lên tab: giữ ~0,5 s chuyển tab; thả lên tab = thêm vào cuối file đó ─────────

        private System.Windows.Threading.DispatcherTimer? _tabSpringTimer;
        private DocumentGroup? _tabSpringGroup;
        private ListBoxItem? _tabDropHover;

        private ListBoxItem? TabItemAt(object? source)
        {
            var element = source as DependencyObject;
            while (element != null && element is not ListBoxItem) element = System.Windows.Media.VisualTreeHelper.GetParent(element);
            return element as ListBoxItem;
        }

        private void SetTabDropHover(ListBoxItem? item)
        {
            if (ReferenceEquals(_tabDropHover, item)) return;
            if (_tabDropHover != null) _tabDropHover.Tag = null;
            _tabDropHover = item;
            if (item != null) item.Tag = "DropHover";
        }

        private void StopTabSpring()
        {
            _tabSpringTimer?.Stop();
            _tabSpringGroup = null;
        }

        private void ReaderTabs_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(Controls.ReaderSidePanel.PageDragData))) return; // file PDF: cửa sổ chính tự xử lý
            e.Effects = (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy : DragDropEffects.Move;
            e.Handled = true;
            var item = TabItemAt(e.OriginalSource);
            SetTabDropHover(item);
            if (item?.DataContext is not DocumentGroup group || ReferenceEquals(group, _readerGroup)) { StopTabSpring(); return; }
            if (ReferenceEquals(group, _tabSpringGroup)) return;
            _tabSpringGroup = group;
            _tabSpringTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _tabSpringTimer.Tick -= TabSpringTick;
            _tabSpringTimer.Tick += TabSpringTick;
            _tabSpringTimer.Stop();
            _tabSpringTimer.Start();
        }

        private void TabSpringTick(object? sender, EventArgs e)
        {
            var group = _tabSpringGroup;
            StopTabSpring();
            if (group != null && _groups.Contains(group)) ReaderDocumentTabs.SelectedItem = group;
        }

        private void ReaderTabs_DragLeave(object sender, DragEventArgs e)
        {
            SetTabDropHover(null);
            StopTabSpring();
        }

        private void ReaderTabs_Drop(object sender, DragEventArgs e)
        {
            SetTabDropHover(null);
            StopTabSpring();
            if (EditHost == null || e.Data.GetData(typeof(Controls.ReaderSidePanel.PageDragData)) is not Controls.ReaderSidePanel.PageDragData data) return;
            if (TabItemAt(e.OriginalSource)?.DataContext is not DocumentGroup group) return;
            e.Handled = true;
            bool copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            var result = EditHost.MovePages(data.Source, group, data.Pages, group.Pages.Count, copy);
            if (result.Count > 0 && ReferenceEquals(_readerGroup, group)) ReaderSidePanel.SelectPages(result);
        }

        // ── Lệnh trang từ panel Pages (menu chuột phải, phím tắt) ────────────────

        private async void OnPageCommand(PageCommand command)
        {
            if (_readerGroup == null || EditHost == null) return;
            var group = _readerGroup;
            IReadOnlyList<PageRow> pages = ReaderSidePanel.SelectedPages;
            if (pages.Count == 0 && _readerPage != null) pages = new[] { _readerPage };
            if (pages.Count == 0) return;
            var indexes = pages.Select(p => group.Pages.IndexOf(p)).Where(i => i >= 0).ToList();
            if (indexes.Count == 0) return;
            int first = indexes.Min(), last = indexes.Max(), count = group.Pages.Count;
            IReadOnlyList<PageRow> result = Array.Empty<PageRow>();

            switch (command)
            {
                case PageCommand.Copy: EditHost.CopyPages(group, pages, cut: false); return;
                case PageCommand.Cut: EditHost.CopyPages(group, pages, cut: true); return;
                case PageCommand.PasteAfter: result = EditHost.PastePages(group, last + 1); break;
                case PageCommand.PasteBefore: result = EditHost.PastePages(group, first); break;
                case PageCommand.MoveToStart: result = EditHost.MovePages(group, group, pages, 0, copy: false); break;
                case PageCommand.MoveUp: result = EditHost.MovePages(group, group, pages, Math.Max(0, first - 1), copy: false); break;
                case PageCommand.MoveDown: result = EditHost.MovePages(group, group, pages, Math.Min(count, last + 2), copy: false); break;
                case PageCommand.MoveToEnd: result = EditHost.MovePages(group, group, pages, count, copy: false); break;
                case PageCommand.MoveToPosition:
                {
                    int max = Math.Max(1, count - indexes.Count + 1);
                    int? position = Controls.NumberPromptWindow.Ask(this, "Move to position", "New position of the first selected page", 1, max, Math.Min(first + 1, max));
                    if (position == null) return;
                    result = EditHost.MovePages(group, group, pages, ToOriginalInsertIndex(position.Value - 1, indexes), copy: false);
                    break;
                }
                case PageCommand.Duplicate: result = EditHost.MovePages(group, group, pages, last + 1, copy: true); break;
                case PageCommand.RotateLeft: await RotateEditTargetsAsync(-90); return;
                case PageCommand.RotateRight: await RotateEditTargetsAsync(90); return;
                case PageCommand.Extract: await EditHost.ExtractPagesAsync(group, pages); return;
                case PageCommand.Delete: EditHost.DeletePages(group, pages); return;
            }
            if (result.Count > 0 && ReferenceEquals(_readerGroup, group)) ReaderSidePanel.SelectPages(result);
        }

        /// <summary>Chỉ số chèn (theo danh sách GỐC, tính cả các trang đang bị rút ra) sao cho sau khi rút, trang đầu nằm đúng
        /// <paramref name="finalIndex"/> (0-based) trong danh sách mới — MovePagesCommand tự trừ số trang chọn nằm trước điểm chèn.</summary>
        private static int ToOriginalInsertIndex(int finalIndex, IReadOnlyList<int> selectedIndexes)
        {
            int insert = finalIndex;
            for (int guard = 0; guard < 64; guard++)
            {
                int next = finalIndex + selectedIndexes.Count(i => i < insert);
                if (next == insert) break;
                insert = next;
            }
            return insert;
        }

        // ── Save / Save As (Ctrl+S, Ctrl+Shift+S, menu chuột phải tab — chuột phải đã chọn tab đó) ──
        private async System.Threading.Tasks.Task SaveCurrentGroupAsync(bool saveAs)
        {
            if (_readerGroup == null || EditHost == null) return;
            await EditHost.SaveGroupAsync(_readerGroup, saveAs);
        }

        private async void ReaderTabSave_Click(object sender, RoutedEventArgs e) => await SaveCurrentGroupAsync(saveAs: false);
        private async void ReaderTabSaveAs_Click(object sender, RoutedEventArgs e) => await SaveCurrentGroupAsync(saveAs: true);

        // ── Lệnh ribbon gọi sang cửa sổ chủ ────────────────────────────

        private async void ReaderOpenFiles_Click(object sender, RoutedEventArgs e)
        {
            if (EditHost != null) await EditHost.OpenFilesAsync();
        }

        private void ReaderUndo_Click(object sender, RoutedEventArgs e) => EditHost?.Undo();
        private void ReaderRedo_Click(object sender, RoutedEventArgs e) => EditHost?.Redo();
        private void ReaderShowMergeWindow_Click(object sender, RoutedEventArgs e) => ShowMerge(true);

        // ── Cửa sổ ghép (phụ) — chỉ tạo ở đây, đóng là huỷ thật ─────────────


        /// <summary>Mở (hoặc đưa lên trước) cửa sổ ghép nhiều file. Cửa sổ ghép mượn <see cref="Session"/>
        /// nên có sẵn mọi file đang mở ở đây; đóng nó không ảnh hưởng gì tới phiên làm việc.</summary>
        internal void OpenMergeWindow() => ShowMerge(true);

        private Controls.MergeWindow? _mergeWindow;

        /// <summary>The Merge window is created on first use (it is a separate window, mockup 6) and only hidden when closed.</summary>
        private Controls.MergeWindow EnsureMergeWindow()
        {
            if (_mergeWindow != null) return _mergeWindow;
            var window = new Controls.MergeWindow { Owner = this };
            var view = window.View;
            view.DoneRequested += () => window.Hide();
            view.HistoryStateChanged += (_, _) => ScheduleRecovery();
            view.OpenFileRequested += async () =>
            {
                if (EditHost != null) await EditHost.OpenFilesAsync();
                view.BeginSession(_groups);
            };
            return _mergeWindow = window;
        }

        private void InitializeMerge()
        {
            ReaderSidePanel.WideTabChanged += wide =>
            {
                // Mockup: Pages panel 300 px, the list-style panels 340 px (+ 64 px rail). Only grow/shrink when the user has not resized it.
                double current = ReaderSidePanelColumn.Width.Value;
                if (wide && Math.Abs(current - 364) < 1) ReaderSidePanelColumn.Width = new GridLength(404);
                else if (!wide && Math.Abs(current - 404) < 1) ReaderSidePanelColumn.Width = new GridLength(364);
            };
        }

        /// <summary>Hiện/ẩn màn hình Merge (phủ lên panel + vùng xem).</summary>
        private void ShowMerge(bool show)
        {
            if (!show) { _mergeWindow?.Hide(); return; }
            var window = EnsureMergeWindow();
            // Chỉ tạo bàn nháp lúc mở lại. Gọi lệnh Merge khi cửa sổ đang hiện phải giữ nguyên
            // thao tác chưa export của người dùng, không được âm thầm nạp lại từ các tab chính.
            if (!window.IsVisible) window.View.BeginSession(_groups);
            if (_readerGroup != null) window.View.ShowGroup(_readerGroup);
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }

        /// <summary>Đưa cửa sổ đọc lên trước (vd double-click 1 trang trong cửa sổ ghép).</summary>
        internal void BringToFront()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        private bool _closingConfirmed;

        private async void ReaderWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            FlushViewPosition();
            if (!_closingConfirmed && _mergeWindow?.View.HasUnsavedDraft == true &&
                AppDialog.Show(this, "The Merge window contains an unfinished draft. Close the app and discard it?\n\nChoose No to return to Merge and export the draft first.",
                    "Unfinished merge draft", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            { e.Cancel = true; _mergeWindow.Show(); _mergeWindow.Activate(); return; }
            if (!_closingConfirmed && EditHost?.GetDirtyGroups() is { Count: > 0 } dirty)
            {
                string question = dirty.Count == 1
                    ? $"Save changes to \"{dirty[0].FileName}\" before closing?"
                    : $"{dirty.Count} files have unsaved changes. Save them before closing?";
                var answer = AppDialog.Show(this, question, "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Cancel) { e.Cancel = true; return; }
                if (answer == MessageBoxResult.Yes)
                {
                    e.Cancel = true; // lưu xong mới đóng thật
                    foreach (var group in dirty)
                        if (!await EditHost.SaveGroupAsync(group, saveAs: false)) return;
                    _closingConfirmed = true;
                    Close();
                    return;
                }
            }
            _mergeWindow?.CloseForReal();
            CompleteRecoveryClose();
            ShutdownReader();
        }

        // ── Settings ──────────────────────────────────────────────────

        private void InitializeSettings()
        {
            ReaderSidePanel.CollapseRequested += () => SetPanelCollapsed(true);
            ReaderSidePanel.RailTabChosen += () => SetPanelCollapsed(false);
            Loaded += (_, _) =>
            {
                SetPanelCollapsed(true); // like Foxit: the left panel starts hidden (icon rail only)
                UpdateViewModeButtons();
                // Chế độ xem mặc định (Settings → Display): cuộn liên tục mặc định; nút Continuous vẫn chuyển qua lại.
                // Đợi bố cục xong (vùng xem có kích thước thật) rồi mới bật, nếu không vùng cuộn liên tục tính zoom trên viewport 0.
                if (AppSettings.ContinuousByDefault)
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!_readerContinuousMode) ReaderContinuousToggle_Click(this, new RoutedEventArgs());
                    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                // Công cụ mặc định (Settings → Display): Hand (giữ nguyên như trước) hoặc Select.
                if (AppSettings.DefaultTool == "Select") SetReaderTool(ReaderTool.Select);
            };
        }

        private void ShowSettings(bool show)
        {
            if (!show) return;
            if (_settingsWindow is { IsLoaded: true })
            {
                if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
                _settingsWindow.Activate();
                return;
            }
            var window = _settingsWindow = new Controls.SettingsWindow(ClearFileCachesAsync) { Owner = this };
            try { window.ShowDialog(); }
            finally
            {
                if (ReferenceEquals(_settingsWindow, window)) _settingsWindow = null;
            }
        }

        private Controls.SettingsWindow? _settingsWindow;

        // ── Side panel collapse/restore ───────────────────────────────

        private double _panelWidthBeforeHide = 364;
        private bool _panelCollapsed;

        /// <summary>Collapses the side panel to its icon rail (or restores it).</summary>
        private void SetPanelCollapsed(bool collapse)
        {
            if (collapse == _panelCollapsed) return;
            _panelCollapsed = collapse;
            if (collapse)
            {
                _panelWidthBeforeHide = ReaderSidePanelColumn.Width.Value;
                ReaderSidePanel.SetCollapsed(true);
                ReaderPanelSplitter.Visibility = Visibility.Collapsed;
                ReaderSidePanelColumn.MinWidth = 44;
                ReaderSidePanelColumn.MaxWidth = 44;
                ReaderSidePanelColumn.Width = new GridLength(44);
            }
            else
            {
                ReaderSidePanel.SetCollapsed(false);
                ReaderPanelSplitter.Visibility = Visibility.Visible;
                ReaderSidePanelColumn.MaxWidth = 640;
                ReaderSidePanelColumn.MinWidth = 304;
                ReaderSidePanelColumn.Width = new GridLength(Math.Max(304, _panelWidthBeforeHide));
            }
        }

        private async void ReaderNew_Click(object sender, RoutedEventArgs e)
        {
            var size = Controls.NewBlankPdfWindow.Ask(this);
            if (size == null) return;
            ShowStart(false);
            await Session.CreateBlankDocumentAsync(size.Value.WidthPoints, size.Value.HeightPoints);
        }

        /// <summary>"Clear cache": đóng document PDFium đang giữ và bỏ bộ đệm file của các file KHÔNG nằm trên màn hình (đọc lại khi cần).</summary>
        private async System.Threading.Tasks.Task ClearFileCachesAsync()
        {
            var onScreen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_readerGroup != null)
                foreach (var page in _readerGroup.Pages) onScreen.Add(System.IO.Path.GetFullPath(page.SourcePath));
            await PdfThumbnailService.TrimDocumentsAsync(includePrimary: true);
            PdfFileBuffer.ReleaseExcept(path => onScreen.Contains(path));
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

        private async void OnLayerHiddenChanged(string path, PdfLayerInfo info, IReadOnlySet<string> hidden)
        {
            if (EditHost == null) return;
            await EditHost.SetLayerHiddenAsync(path, hidden, info.DefaultHidden);
        }

        /// <summary>"Export PDF with this view…" của panel Layers → hộp thoại Export với "flatten theo View" được chọn sẵn.</summary>
        private void OnExportLayerView(IReadOnlySet<string> hiddenNames, string viewName) => OpenExport(preferFlatten: true);

        /// <summary>Hộp thoại Export / Split cho window đang xem.</summary>
        private async void OpenExport(bool preferFlatten)
        {
            if (_readerGroup == null || _readerGroup.Pages.Count == 0) return;
            if (!await Controls.PdfPermissionDialog.RequireAsync(this, _readerGroup.Pages.Select(p => p.SourcePath), PdfPermissionOperation.Copy)) return;
            List<(string SourcePath, int PageNumber)> pages;
            try { pages = await AnnotationWorkingCopy.MapAsync(_readerGroup.Pages.Select(p => (p.SourcePath, p.PageNumber))); }
            catch (Exception ex) { AppDialog.Show(this, "Could not prepare the PDF for export:\n" + ex.Message, "Export PDF", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            string dir = System.IO.Path.GetDirectoryName(_readerGroup.SourcePath) ?? "";
            string baseName = System.IO.Path.GetFileNameWithoutExtension(_readerGroup.FileName);
            var dialog = new Controls.ExportWindow(pages, baseName, System.IO.Directory.Exists(dir) ? dir : "", preferFlatten,
                _readerGroup.Pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList()) { Owner = this };
            if (dialog.ShowDialog() == true)
                XTStyle.Controls.XTGrowl.Success(dialog.Written == 1 ? "Exported 1 file" : $"Exported {dialog.Written} files", this);
        }

        /// <summary>Trạng thái layer của file vừa đổi (lease PDFium cũ đã đóng). Trang đang hiện: vẽ lại TẠI CHỖ
        /// (ảnh cũ giữ tới khi ảnh mới xong); trang khác của file: bỏ ảnh để lần hiện sau lấy theo key mới.</summary>
        internal void OnLayerStateChanged(string path)
        {
            bool Matches(PageRow r) => string.Equals(r.SourcePath, path, StringComparison.OrdinalIgnoreCase);
            Controls.ContinuousPdfView.InvalidateCachedRegions((p, _) => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

            ReaderSidePanel.RequestVisibleThumbnails();
            foreach (var row in _groups.SelectMany(g => g.Pages).Where(Matches))
            {
                row.ReaderBitmap = null;
                row.ReaderBitmapLoadQueued = false;
            }
            // Vùng vẽ giữ ảnh cũ trên màn hình tới khi ảnh theo trạng thái layer mới xong.
            if (_readerGroup != null) ReaderContinuousView.InvalidatePages(Matches, dropImages: false);
        }
    }
}
