using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;
using XTStyle.Controls;
using static XTPdfMergeApp.Services.VisualTreeHelpers;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;

namespace XTPdfMergeApp
{
    /// <summary>Docked PDF viewer control. Giữ tên ReaderWindow để tránh đổi lan rộng,
    /// nhưng không còn là top-level Window riêng.</summary>
    /// <summary>
    /// Cửa sổ chính của app = cửa sổ đọc. Là entry point thật: App.OnStartup tạo nó làm
    /// Application.MainWindow. Nó SỞ HỮU <see cref="DocumentSession"/> (file đang mở, undo/redo, sửa trang)
    /// và là nơi duy nhất tạo cửa sổ ghép phụ <see cref="MergeWorkspaceWindow"/>.
    /// </summary>
    public partial class ReaderWindow : XTWindow
    {
        /// <summary>Cửa sổ đọc đang chạy (app chỉ có 1).</summary>
        public static ReaderWindow? Instance { get; private set; }

        /// <summary>Phiên làm việc của app — cửa sổ ghép mượn qua constructor khi được mở.</summary>
        internal DocumentSession Session { get; }

        private readonly ObservableCollection<DocumentGroup> _groups;

        public ReaderWindow()
        {
            Instance = this;
            Session = new DocumentSession(Dispatcher);
            EditHost = Session;
            var groups = Session.Documents;
            InitializeComponent();
            _groups = groups;
            ReaderContentHost.LostMouseCapture += (_, _) => CancelHighlightDrag();
            // File đang xem bị đóng khỏi workspace (đóng hẳn, không phải chỉ xoá vài trang — trường hợp
            // đó qua NotifyPagesChanged) → chuyển sang file khác đang mở, hết file thì màn trống.
            _groups.CollectionChanged += (_, _) => OnGroupsChanged();
            InitializeShellParts();
            HookContinuousView();
            DiagnosticsReport.ViewerSection = GetViewerDiagnostics;
        }

        private DiagnosticsReport.ViewerStats GetViewerDiagnostics()
        {
            var reader = GetReaderCacheStats();
            var continuous = ReaderContinuousView.MemoryStats;
            int pageCount = _readerGroup?.Pages.Count ?? 0;
            int current = _readerGroup != null && _readerPage != null ? _readerGroup.Pages.IndexOf(_readerPage) : -1;
            return new DiagnosticsReport.ViewerStats(
                reader.Cache, reader.Bytes, reader.Inflight,
                0, 0,
                continuous.Pages, continuous.Regions, continuous.RegionBytes,
                _readerTwoPageMode ? "Two-page" : _readerContinuousMode ? "Continuous" : "Single page", _readerZoom, current, pageCount);
        }

        internal void ShutdownReader()
        {
            ReaderContinuousView.CancelAll();
            _readerPageCts.Cancel();
            _readerPrefetchCts.Cancel();
        }

        /// <summary>True nếu Viewer đã từng hiện ít nhất 1 trang — dùng thay cho check
        /// "_readerPage == null" (giờ là field riêng, private) lúc quyết định có tự mở Viewer khi
        /// mở file đầu tiên trong phiên làm việc hay không.</summary>
        public bool HasAnyPageShown => _readerPage != null;

        /// <summary>Xoá bộ nhớ zoom riêng của group này (RemoveGroup_Click gọi khi user bỏ hẳn 1
        /// window PDF) — an toàn gọi kể cả khi Instance chưa từng mở (no-op).</summary>
        internal void NotifyGroupRemoved(DocumentGroup group) => _readerZoomByGroup.Remove(group);

        /// <summary>Phím tắt điều hướng/zoom khi Viewer đang mở — bỏ qua khi đang gõ trong ô
        /// nhập liệu (VD ReaderPageBox) để không cướp phím mũi tên/Enter của nó. Không còn cần
        /// gate theo Visibility như hồi còn panel nhúng — WPF tự phân luồng input theo ĐÚNG Window
        /// nào đang focus, phím tắt ở đây chỉ bao giờ tới tay khi Viewer thật sự đang active.</summary>
        private void ReaderWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F1)
            {
                Controls.KeyboardShortcutsWindow.ShowFor(this);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F11)
            {
                SetReaderFullScreen(!_readerFullScreen);
                e.Handled = true;
                return;
            }
            if (_readerFullScreen && e.Key == Key.Escape)
            {
                SetReaderFullScreen(false);
                e.Handled = true;
                return;
            }
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.E)
            {
                OpenExport(preferFlatten: false);
                e.Handled = true;
                return;
            }
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.P)
            {
                ReaderPrint_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
            {
                ReaderNew_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.F)
            {
                OpenFind();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F3 && _findHits.Count > 0)
            {
                ReaderSidePanel.Find.Step((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                e.Handled = true;
                return;
            }
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.K)
            {
                if (Palette.IsOpen) Palette.Close(); else OpenPalette();
                e.Handled = true;
                return;
            }
            if (Palette.IsOpen) return; // bảng lệnh tự xử lý phím

            if (Keyboard.FocusedElement is TextBox or ComboBox) return;

            if (_readerTool == ReaderTool.Select && e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                CopySelectedText();
                e.Handled = true;
                return;
            }

            if (_selAnn != null && e.Key == Key.Delete)
            {
                DeleteSelectedAnnotation();
                e.Handled = true;
                return;
            }
            if (_calloutPlacement != null && e.Key == Key.Escape)
            {
                CancelCalloutPlacement();
                e.Handled = true;
                return;
            }
            if (_selAnn != null && e.Key == Key.Escape)
            {
                SelectAnnotation(null, null);
                e.Handled = true;
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.O)
            {
                if (EditHost != null) _ = EditHost.OpenFilesAsync();
                e.Handled = true;
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && (e.Key == Key.Z || e.Key == Key.Y))
            {
                if (e.Key == Key.Y || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) EditHost?.Redo();
                else EditHost?.Undo();
                e.Handled = true;
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.S)
            {
                _ = SaveCurrentGroupAsync(saveAs: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.F12:
                    DiagnosticsWindow.ShowFor(this);
                    e.Handled = true;
                    break;
                case Key.Left:
                case Key.PageUp:
                    _ = NavigateReaderAsync(-1);
                    e.Handled = true;
                    break;
                case Key.Right:
                case Key.PageDown:
                    _ = NavigateReaderAsync(1);
                    e.Handled = true;
                    break;
                case Key.Home:
                    _ = NavigateReaderToIndexAsync(0);
                    e.Handled = true;
                    break;
                case Key.End:
                    if (_readerGroup != null) _ = NavigateReaderToIndexAsync(_readerGroup.Pages.Count - 1);
                    e.Handled = true;
                    break;
                case Key.Up:
                case Key.Down:
                    ReaderContinuousView.ScrollBy(0, (e.Key == Key.Up ? -1 : 1) * 100.0 / 3 * Math.Max(1, SystemParameters.WheelScrollLines));
                    e.Handled = true;
                    break;
                case Key.OemPlus:
                case Key.Add:
                    ReaderZoomIn_Click(this, e);
                    e.Handled = true;
                    break;
                case Key.OemMinus:
                case Key.Subtract:
                    ReaderZoomOut_Click(this, e);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    SetReaderTool(ReaderTool.Hand);
                    e.Handled = true;
                    break;
                case Key.D0:
                case Key.NumPad0:
                    ReaderFitWidth_Click(this, e);
                    e.Handled = true;
                    break;
            }
        }

        private enum ReaderZoomMode { Manual, FitWidth, FitPage }
        private enum ReaderPageView { Single, Continuous, TwoPage }

        private const double ReaderRenderWidthPx = 2200;
        internal const long ReaderCacheBudgetBytes = 64L * 1024 * 1024;
        private const int ReaderAdjacentPrefetchCount = 2;
        private const double ReaderMinZoom = 0.05;
        private const double ReaderMaxZoom = 4.0;
        // 1.25 (25%/nấc) trước đây quá lớn — mỗi nấc lăn chuột nhảy ảnh rõ rệt, cảm giác giật cục.
        // Foxit/Chrome PDF dùng bước nhỏ hơn nhiều (~8-10%/nấc) để zoom mượt hơn.
        private const double ReaderZoomStep = 1.08;
        private static readonly object _readerCacheLock = new();
        private static readonly BitmapMemoryCache<(string Path, int Page, int Width, string Layers)> _readerCache = new(ReaderCacheBudgetBytes);
        private static readonly Dictionary<(string Path, int Page, int Width, string Layers), Task<BitmapSource?>> _readerLoads = new();
        private DocumentGroup? _readerGroup;
        private PageRow? _readerPage;
        private double _readerZoom = 1.0;
        private ReaderZoomMode _readerZoomMode = ReaderZoomMode.FitWidth;
        private long _readerRequestId;
        private CancellationTokenSource _readerPrefetchCts = new();

        /// <summary>Bố cục của vùng xem (ReaderContinuousView — bộ vẽ DUY NHẤT): TRUE = cuộn liên tục qua mọi trang,
        /// FALSE = 1 trang (lăn quá mép = sang trang kế/trước, như Foxit). Lăn chuột cuộn, Ctrl+lăn chuột zoom.</summary>
        private bool _readerContinuousMode;
        private bool _readerTwoPageMode;

        /// <summary>Xoay CHỈ ĐỂ XEM (0/90/180/270) cho mọi trang — không đụng tới file PDF, không ảnh hưởng lúc Lưu/merge.</summary>
        private int _readerRotation;

        /// <summary>Nhớ zoom (mode + mức %) RIÊNG cho từng file (DocumentGroup) — chuyển
        /// qua lại giữa các window trong danh sách không bị mất mức zoom đang xem dở của
        /// từng file đó (khác hẳn trước đây: 1 biến _readerZoom DÙNG CHUNG cho mọi file).</summary>
        private readonly Dictionary<DocumentGroup, (ReaderZoomMode Mode, double Zoom)> _readerZoomByGroup = new();

        internal static (int Cache, int Inflight, long Bytes) GetReaderCacheStats()
        {
            lock (_readerCacheLock) return (_readerCache.Count, _readerLoads.Count, _readerCache.Bytes);
        }

        internal static void ReleaseUnusedSources(HashSet<string> active)
        {
            lock (_readerCacheLock) _readerCache.RemoveWhere(key => !active.Contains(key.Path));
        }

        private CancellationTokenSource _readerPageCts = new();
        /// <param name="width">Chiều rộng pixel cần vẽ (ContinuousPdfView tính theo từng trang).</param>
        private Task<BitmapSource?> GetReaderLoadTask((string Path, int Page) source, bool prefetch = false,
            CancellationToken cancellationToken = default, int? width = null)
        {
            // Key kèm "phiên bản trạng thái layer" của file — xem PdfLayerStateStore / MainWindow.ThumbnailKey.
            var key = RenderCacheKeys.ReaderPage(source.Path, source.Page, width ?? 2304);
            var token = cancellationToken.CanBeCanceled ? cancellationToken : _readerPageCts.Token;
            lock (_readerCacheLock)
            {
                if (_readerCache.TryGetValue(key, out var cached)) return Task.FromResult<BitmapSource?>(cached);
                if (_readerLoads.TryGetValue(key, out var existing)) return existing;
                var completion = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _readerLoads[key] = completion.Task;
                _ = Task.Run(async () =>
                {
                    try { completion.TrySetResult(await RenderAndCacheReaderAsync(key, token, prefetch, completion.Task)); }
                    catch (Exception ex) { completion.TrySetException(ex); }
                });
                return completion.Task;
            }
        }

        private static async Task<BitmapSource?> RenderAndCacheReaderAsync(
            (string Path, int Page, int Width, string Layers) key, CancellationToken token, bool prefetch, Task<BitmapSource?> owner)
        {
            BitmapSource? bmp = null;
            try
            {
                bmp = await PdfThumbnailService.RenderPageAsync(key.Path, key.Page - 1, key.Width, token,
                    prefetch ? PdfRenderPriority.Background : PdfRenderPriority.Visible, key.Layers).ConfigureAwait(false);
                return token.IsCancellationRequested ? null : bmp;
            }
            finally
            {
                lock (_readerCacheLock)
                {
                    if (bmp != null && !token.IsCancellationRequested) _readerCache.Set(key, bmp);
                    if (_readerLoads.TryGetValue(key, out var current) && ReferenceEquals(current, owner))
                        _readerLoads.Remove(key);
                }
            }
        }

        /// <summary>Hiện docked Viewer trong MainWindow.</summary>
        public void ShowAndActivate()
        {
            MergeAppSettingsStore.SetViewerVisible(true);
            Visibility = Visibility.Visible;
            Focus();
        }

        /// <summary>Ẩn docked Viewer và dọn state đang xem.</summary>
        public void HideReader()
        {
            PdfThumbnailService.SetHotPages(Array.Empty<(string, int)>());
            CommitAnnotationEditor(cancel: true);
            CancelHighlightDrag();
            Interlocked.Increment(ref _readerRequestId);
            // Cửa sổ đọc là cửa sổ chính: không ẩn control nữa, chỉ về màn trống.
            _readerPageCts.Cancel();
            _readerPageCts.Dispose();
            _readerPageCts = new();
            _readerPrefetchCts.Cancel();
            _readerPrefetchCts.Dispose();
            _readerPrefetchCts = new();
            lock (_readerCacheLock) _readerCache.Clear();
            PdfThumbnailService.ReleaseCachedPages();
            ShowEmptyReaderState();
            ReaderContinuousView.SetDocument(null, ReaderContinuousView.Zoom);
            _readerGroup = null;
            _readerPage = null;
        }
        /// <summary>MainWindow gọi khi user chỉ ĐỔI SELECTION (không double-click) trong lúc Viewer
        /// đang mở — đồng bộ nội dung xem theo trang mới chọn, giữ nguyên mức zoom hiện tại. Không
        /// tự hiện dock — chỉ nên gọi khi đã biết Viewer đang hiện (IsVisible), vì Organizer là chức
        /// năng chính, chọn trang không tự mở Viewer.</summary>
        internal void NotifySelectionChanged(DocumentGroup group, PageRow page)
        {
            ShowReaderContinuous(group, page);
        }

        /// <summary>Vị trí (0-based) trang đang xem trong <paramref name="group"/>, 0 nếu window đó không đang xem.</summary>
        internal int CurrentPageIndexIn(DocumentGroup group)
            => ReferenceEquals(_readerGroup, group) && _readerPage != null ? Math.Max(0, group.Pages.IndexOf(_readerPage)) : 0;

        /// <summary>Window vừa được lưu đè lên file gốc: placement đã được dựng lại — đọc lại bookmark/layer của file và
        /// quay về đúng vị trí trang đang xem.</summary>
        internal void OnGroupSaved(DocumentGroup group, string path, int keepIndex)
        {
            ReaderSidePanel.InvalidateSource(path, bookmarks: true, layers: true);
            if (!ReferenceEquals(_readerGroup, group) || group.Pages.Count == 0) return;
            var row = group.Pages[Math.Clamp(keepIndex, 0, group.Pages.Count - 1)];
            ShowReaderContinuous(group, row);
        }

        internal void NotifyPagesChanged(DocumentGroup group)
        {
            if (!ReferenceEquals(_readerGroup, group)) return;

            if (!_groups.Contains(group) || group.Pages.Count == 0)
            {
                HideReader();
                return;
            }

            if (_readerPage != null && group.Pages.Contains(_readerPage))
            {
                UpdateReaderChrome(group, _readerPage);
                return;
            }

            ShowReaderContinuous(group, group.Pages[0]);
        }
        internal async Task ShowPageAsync(DocumentGroup group, PageRow row, bool preserveZoomMode = true)
        {
            if (!Dispatcher.CheckAccess())
            {
                await Dispatcher.InvokeAsync(() => ShowPageAsync(group, row, preserveZoomMode)).Task.Unwrap();
                return;
            }
            if (!ReferenceEquals(_readerPage, row)) CommitAnnotationEditor();
            if (!preserveZoomMode) _readerZoomMode = DefaultReaderZoomMode();
            ShowReaderContinuous(group, row);
        }

        private void UpdateReaderChrome(DocumentGroup group, PageRow row)
        {
            int position = group.Pages.IndexOf(row);
            ReaderTitleText.Text = $"{Path.GetFileName(row.SourcePath)} - page {row.PageNumber}";
            ReaderPageBox.Text = position >= 0 ? (position + 1).ToString() : row.Index.ToString();
            ReaderPageTotalText.Text = $"/ {group.Pages.Count}";
            OnReaderCurrentPageChanged(group, row);
        }

        private async Task NavigateReaderAsync(int delta)
        {
            if (_readerGroup == null || _readerPage == null) return;

            int index = _readerGroup.Pages.IndexOf(_readerPage);
            if (index < 0) return;

            await NavigateReaderToIndexAsync(index + delta);
        }

        /// <summary>Tới trang thứ <paramref name="index"/> (0-based, tự kẹp) — Home/End, PageUp/PageDown.</summary>
        private async Task NavigateReaderToIndexAsync(int index)
        {
            if (_readerGroup == null || _readerPage == null || _readerGroup.Pages.Count == 0) return;
            int nextIndex = Math.Clamp(index, 0, _readerGroup.Pages.Count - 1);
            if (ReferenceEquals(_readerGroup.Pages[nextIndex], _readerPage)) return;

            ScrollReaderContinuousTo(_readerGroup.Pages[nextIndex]);
            await Task.CompletedTask;
        }

        private async Task TryNavigateReaderPageFromBoxAsync()
        {
            if (_readerGroup == null) return;

            if (!int.TryParse(ReaderPageBox.Text.Trim(), out int pagePosition))
            {
                if (_readerPage != null)
                    UpdateReaderChrome(_readerGroup, _readerPage);
                return;
            }

            pagePosition = Math.Clamp(pagePosition, 1, _readerGroup.Pages.Count);
            ScrollReaderContinuousTo(_readerGroup.Pages[pagePosition - 1]);
            await Task.CompletedTask;
        }

        /// <summary>Cách hiện trang khi mở file lần đầu (Settings → Display → Zoom when opening a file).</summary>
        private static ReaderZoomMode DefaultReaderZoomMode()
            => AppSettings.ZoomOnOpen == DefaultZoom.FitPage ? ReaderZoomMode.FitPage : ReaderZoomMode.FitWidth;

        private void ReaderViewSingle_Click(object sender, RoutedEventArgs e) => SetReaderViewMode(ReaderPageView.Single);
        private void ReaderViewContinuous_Click(object sender, RoutedEventArgs e) => SetReaderViewMode(ReaderPageView.Continuous);
        private void ReaderViewTwoPage_Click(object sender, RoutedEventArgs e) => SetReaderViewMode(ReaderPageView.TwoPage);

        private void UpdateViewModeButtons()
        {
            ReaderStatusSingleButton.Tag = !_readerContinuousMode ? "Active" : null;
            ReaderStatusContinuousButton.Tag = _readerContinuousMode && !_readerTwoPageMode ? "Active" : null;
            ReaderStatusTwoPageButton.Tag = _readerTwoPageMode ? "Active" : null;
        }

        private void ReaderContinuousToggle_Click(object sender, RoutedEventArgs e)
            => SetReaderViewMode(_readerContinuousMode ? ReaderPageView.Single : ReaderPageView.Continuous);

        private void SetReaderViewMode(ReaderPageView mode)
        {
            _readerContinuousMode = mode != ReaderPageView.Single;
            _readerTwoPageMode = mode == ReaderPageView.TwoPage;
            if (mode == ReaderPageView.Single)
            {
                ReaderContinuousView.TwoPage = false;
                ReaderContinuousView.SinglePage = true;
            }
            else if (mode == ReaderPageView.Continuous)
            {
                ReaderContinuousView.TwoPage = false;
                ReaderContinuousView.SinglePage = false;
            }
            else
            {
                ReaderContinuousView.SinglePage = false;
                ReaderContinuousView.TwoPage = true;
            }
            ReapplyZoomMode();
            UpdateViewModeButtons();
        }

        // ── Chế độ Cuộn liên tục: ContinuousPdfView (1 vùng vẽ kiểu Foxit) ──────────────────────────────────
        // Trước đây: ListBox + panel ảo hoá + phần tử/ảnh/2 canvas tile cho từng trang + binding zoom — nhiều lớp phải
        // khớp nhau mỗi nấc zoom/cuộn (trang trống, màn hình đen sau End + zoom). Giờ vùng vẽ tự tính vị trí mọi trang và
        // vẽ ảnh tốt nhất đang có của từng trang mỗi khung hình; ReaderWindow chỉ gán tài liệu, zoom, trang cần tới.

        private double ReaderContinuousZoom => ReaderContinuousView.Zoom;

        private void HookContinuousView()
        {
            var view = ReaderContinuousView;
            view.MinZoom = ReaderMinZoom;
            view.MaxZoom = ReaderMaxZoom;
            view.ZoomStep = ReaderZoomStep;
            // Ảnh trang dùng chung cache với chế độ 1 trang (_readerCache): trang đã xem ở chế độ nào cũng không vẽ lại.
            view.PageRenderer = (row, width, priority, token) => GetReaderLoadTask((row.SourcePath, row.PageNumber),
                prefetch: priority != PdfRenderPriority.Visible, token, width);
            view.CurrentPageChanged += index =>
            {
                if (_readerGroup == null || !ReferenceEquals(view.Pages, _readerGroup.Pages) || index >= view.Pages.Count) return;
                var row = view.Pages[index];
                if (ReferenceEquals(row, _readerPage)) return;
                _readerPage = row;
                UpdateReaderChrome(_readerGroup, row);
            };
            view.UserZoomed += () => OnContinuousZoomChanged(ReaderZoomMode.Manual);
            view.UserInteraction += PdfThumbnailService.NoteInteraction; // thumbnail/tải trước nhường PDFium cho trang đang xem
            view.ScrollSettled += SyncSidePanelAfterScroll;
            view.ViewChanged += () => { if (_annotationEditor != null) PositionAnnotationEditor(); if (_findHits.Count > 0) ScheduleFindRefresh(); };
            // Như Chromium Viewport.resize_(): đang "vừa chiều rộng" thì đổi cỡ cửa sổ / kéo panel trái → khớp lại zoom.
            view.ViewportResized += () =>
            {
                if (view.Pages.Count > 0) ReapplyZoomMode();
            };
        }

        /// <summary>Bật/chuyển chế độ cuộn liên tục sang đúng file/trang — chỉ gán lại tài liệu khi đổi file (giữ vị trí
        /// cuộn đang xem dở khi chỉ đổi trang trong cùng file).</summary>
        private void ShowReaderContinuous(DocumentGroup group, PageRow row)
        {
            ShowAndActivate();
            ReaderContinuousView.Visibility = Visibility.Visible;
            ReaderEmptyText.Visibility = Visibility.Collapsed;

            bool bound = ReferenceEquals(ReaderContinuousView.Pages, group.Pages);
            bool groupChanged = !ReferenceEquals(_readerGroup, group);
            if (!bound)
            {
                _readerPageCts.Cancel();
                _readerPageCts.Dispose();
                _readerPageCts = new();
            }
            if (groupChanged)
            {
                // Quay lại file đã xem → đúng mức zoom của riêng file đó (như chế độ 1 trang).
                if (_readerZoomByGroup.TryGetValue(group, out var saved)) (_readerZoomMode, _readerZoom) = saved;
                else _readerZoomMode = DefaultReaderZoomMode();
            }
            _readerGroup = group;
            _readerPage = row;
            UpdateReaderChrome(group, row);

            if (!bound)
            {
                // Như Chromium: dựng bố cục từ khổ giấy thật NGAY lần đầu — không hiện trang khổ mặc định rồi mới dời.
                if (!ReferenceEquals(_continuousBindPending, group))
                {
                    _continuousBindPending = group;
                    _ = BindContinuousWhenSizesKnownAsync(group);
                }
                return;
            }

            _ = EnsureContinuousPageSizesAsync(group);
            ScrollReaderContinuousTo(row);
        }

        private DocumentGroup? _continuousBindPending;

        /// <summary>Gán tài liệu cho vùng vẽ sau khi biết khổ giấy mọi trang (tối đa 1,5 s — quá thì dựng bằng khổ mặc
        /// định rồi cập nhật khi đọc xong), đặt zoom, cuộn tới trang đang xem.</summary>
        private async Task BindContinuousWhenSizesKnownAsync(DocumentGroup group)
        {
            try
            {
                await Task.WhenAny(EnsureContinuousPageSizesAsync(group), Task.Delay(1500));
                // Vùng vẽ vừa hiện (Collapsed → Visible): chờ 1 lượt layout để biết chiều rộng khung nhìn (vừa chiều rộng).
                await Dispatcher.Yield(DispatcherPriority.Loaded);
            }
            finally
            {
                if (ReferenceEquals(_continuousBindPending, group)) _continuousBindPending = null;
            }
            if (!ReferenceEquals(_readerGroup, group) || ReferenceEquals(ReaderContinuousView.Pages, group.Pages)) return;
            ReaderContinuousView.SetDocument(group.Pages, _readerZoomMode == ReaderZoomMode.Manual ? _readerZoom : 1.0);
            if (_readerPage is { } current) ScrollReaderContinuousTo(current);
            ReapplyZoomMode();
        }

        private void ScrollReaderContinuousTo(PageRow row)
        {
            _readerPage = row;
            if (_readerGroup == null) return;
            UpdateReaderChrome(_readerGroup, row);
            if (ReferenceEquals(ReaderContinuousView.Pages, _readerGroup.Pages))
                ReaderContinuousView.ScrollToPage(_readerGroup.Pages.IndexOf(row)); // đỉnh trang lên đỉnh khung nhìn
        }

        private double ComputeReaderContinuousFitWidthZoom()
        {
            // Trang RỘNG NHẤT (ReaderRenderWidthPx) vừa khung nhìn, trừ lề + viền của ContinuousPageLayout. Thanh cuộn dọc
            // của vùng vẽ luôn hiện nên chiều rộng khung nhìn không đổi theo nội dung (không vòng lặp khớp lại).
            double width = ReaderContinuousView.ViewportWidth;
            if (width <= 0) return _readerZoom > 0 ? _readerZoom : 1.0; // chưa layout — khớp lại khi có kích thước (ViewportResized)
            double viewportWidth = Math.Max(1, width - 2 * ContinuousPageLayout.Margin - 2 * ContinuousPageLayout.BorderThickness);
            if (_readerTwoPageMode && _readerGroup != null && _readerPage != null)
            {
                int first = Math.Max(0, _readerGroup.Pages.IndexOf(_readerPage) / 2 * 2);
                var (firstWidth, _) = ReaderContinuousView.DisplayBaseSize(first);
                double spreadWidth = firstWidth;
                if (first + 1 < _readerGroup.Pages.Count)
                    spreadWidth += ReaderContinuousView.DisplayBaseSize(first + 1).Width + ContinuousPageLayout.BaseGap;
                return ReaderZoomMath.Clamp(ReaderZoomMath.FitWidthZoom(viewportWidth, spreadWidth), ReaderMinZoom, ReaderMaxZoom);
            }
            // Trang rộng nhất như đang hiện: không xoay = ReaderRenderWidthPx; xoay 90/270 = trang cao nhất.
            double widest = ReaderRenderWidthPx;
            if (_readerRotation % 180 != 0)
            {
                var pages = ReaderContinuousView.Pages;
                widest = 0;
                for (int i = 0; i < pages.Count; i++) widest = Math.Max(widest, ReaderContinuousView.DisplayBaseSize(i).Width);
                if (widest <= 0) widest = ReaderRenderWidthPx;
            }
            return ReaderZoomMath.Clamp(ReaderZoomMath.FitWidthZoom(viewportWidth, widest), ReaderMinZoom, ReaderMaxZoom);
        }

        /// <summary>Trang đang xem hiện trọn trong khung nhìn (Fit page).</summary>
        private double ComputeReaderFitPageZoom()
        {
            var view = ReaderContinuousView;
            if (_readerGroup == null || _readerPage == null || view.ViewportWidth <= 0 || view.ViewportHeight <= 0) return _readerZoom;
            var (w, h) = view.DisplayBaseSize(_readerGroup.Pages.IndexOf(_readerPage));
            if (w <= 0 || h <= 0) return _readerZoom;
            if (_readerTwoPageMode)
            {
                int first = Math.Max(0, _readerGroup.Pages.IndexOf(_readerPage) / 2 * 2);
                (w, h) = view.DisplayBaseSize(first);
                if (first + 1 < _readerGroup.Pages.Count)
                {
                    var (nextWidth, nextHeight) = view.DisplayBaseSize(first + 1);
                    w += nextWidth + ContinuousPageLayout.BaseGap;
                    h = Math.Max(h, nextHeight);
                }
            }
            double extra = 2 * ContinuousPageLayout.Margin + 2 * ContinuousPageLayout.BorderThickness;
            double zoom = Math.Min(Math.Max(1, view.ViewportWidth - extra) / w, Math.Max(1, view.ViewportHeight - extra) / h);
            return ReaderZoomMath.Clamp(zoom, ReaderMinZoom, ReaderMaxZoom);
        }

        /// <summary>Khớp lại zoom theo chế độ đang chọn (Fit width / Fit page) — khi đổi cỡ khung nhìn, xoay, đổi bố cục.</summary>
        private void ReapplyZoomMode()
        {
            if (ReaderContinuousView.Pages.Count == 0) return;
            if (_readerZoomMode == ReaderZoomMode.FitWidth)
                SetReaderContinuousZoom(ComputeReaderContinuousFitWidthZoom(), ReaderZoomMode.FitWidth);
            else if (_readerZoomMode == ReaderZoomMode.FitPage)
            {
                SetReaderContinuousZoom(ComputeReaderFitPageZoom(), ReaderZoomMode.FitPage);
                if (_readerPage != null && _readerGroup != null) ReaderContinuousView.ScrollToPage(_readerGroup.Pages.IndexOf(_readerPage));
            }
        }

        /// <summary>Đổi zoom giữ nguyên điểm ở giữa-đỉnh khung nhìn (vừa chiều rộng). Zoom theo con trỏ: ZoomContinuousAtPoint.</summary>
        private void SetReaderContinuousZoom(double zoom, ReaderZoomMode mode = ReaderZoomMode.Manual)
        {
            ReaderContinuousView.ZoomKeepTop(zoom);
            OnContinuousZoomChanged(mode);
        }

        private void ZoomContinuousAtPoint(double zoom, Point viewPoint)
        {
            ReaderContinuousView.ZoomAt(zoom, viewPoint);
            OnContinuousZoomChanged(ReaderZoomMode.Manual);
        }

        private void OnContinuousZoomChanged(ReaderZoomMode mode)
        {
            _readerZoomMode = mode;
            _readerZoom = ReaderContinuousView.Zoom;
            ReaderZoomText.Text = $"{_readerZoom * 100:0}%";
            _syncingZoomSlider = true;
            ReaderZoomSlider.Value = Math.Clamp(_readerZoom, ReaderZoomSlider.Minimum, ReaderZoomSlider.Maximum);
            _syncingZoomSlider = false;
            if (_readerGroup != null)
                _readerZoomByGroup[_readerGroup] = (_readerZoomMode, _readerZoom);
        }

        private bool _syncingZoomSlider;

        /// <summary>Kéo thanh trượt zoom (kiểu Foxit) — như bấm Zoom in/out nhưng đi thẳng tới giá trị kéo được.</summary>
        private void ReaderZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncingZoomSlider || ReaderContinuousView.Pages.Count == 0) return;
            SetReaderContinuousZoom(e.NewValue);
        }

        private void ReaderMore_Click(object sender, RoutedEventArgs e) => OpenPalette();

        private bool _readerFullScreen;

        /// <summary>Ẩn ribbon/tab file/panel trái/thanh trạng thái, chỉ còn trang — bấm lại icon hoặc Esc để thoát.</summary>
        private void ReaderFullScreen_Click(object sender, RoutedEventArgs e) => SetReaderFullScreen(!_readerFullScreen);

        private void SetReaderFullScreen(bool full)
        {
            if (full == _readerFullScreen) return;
            _readerFullScreen = full;
            _restoreWindowState ??= WindowState;
            if (full)
            {
                _restoreWindowState = WindowState;
                WindowState = WindowState.Maximized;
            }
            else if (_restoreWindowState is { } restore)
            {
                WindowState = restore;
                _restoreWindowState = null;
            }
            var visibility = full ? Visibility.Collapsed : Visibility.Visible;
            ReaderToolbarBar.Visibility = visibility;
            ReaderDocumentTabsRow.Visibility = visibility;
            ReaderStatusBar.Visibility = visibility;
            ReaderPanelSplitter.Visibility = !full && !_panelCollapsed ? Visibility.Visible : Visibility.Collapsed;
            if (full)
            {
                _fullScreenSavedPanelWidth = ReaderSidePanelColumn.Width;
                ReaderSidePanelColumn.Width = new GridLength(0);
            }
            else if (_fullScreenSavedPanelWidth is { } saved)
            {
                ReaderSidePanelColumn.Width = saved;
                _fullScreenSavedPanelWidth = null;
            }
        }

        private GridLength? _fullScreenSavedPanelWidth;

        private WindowState? _restoreWindowState;

        private readonly Dictionary<DocumentGroup, Task> _pageSizeLoads = new();

        /// <summary>
        /// Khổ giấy thật cho mọi trang của <paramref name="group"/> — như Chromium đọc kích thước MỌI trang lúc mở để
        /// dựng bố cục: trang rộng nhất tài liệu có chiều rộng logic DefaultLayoutWidth, trang khác tỉ lệ theo point
        /// (A3 cạnh A1 hiện nhỏ hơn, không bị kéo cùng cỡ). Chỉ đọc file có trang chưa biết kích thước (lần đầu, trang mới
        /// chèn, trang vừa xoay). Trang ở đỉnh khung nhìn giữ nguyên khi bố cục đổi.
        /// </summary>
        private Task EnsureContinuousPageSizesAsync(DocumentGroup group)
        {
            // Lượt đọc đang chạy cho group này → dùng chung (người gọi sau cũng chờ được tới khi có khổ giấy).
            if (_pageSizeLoads.TryGetValue(group, out var running)) return running;
            var task = LoadContinuousPageSizesAsync(group);
            if (!task.IsCompleted) _pageSizeLoads[group] = task;
            return task;
        }

        private async Task LoadContinuousPageSizesAsync(DocumentGroup group)
        {
            try
            {
                var missing = group.Pages.Where(p => p.PageWidthPoints == null).Select(p => p.SourcePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                bool layoutMissing = group.Pages.Any(p => p.PageWidthPoints > 0 && p.BaseWidth == null);
                if (missing.Count == 0 && !layoutMissing) return;

                var sizesByPath = new Dictionary<string, (double Width, double Height)[]?>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in missing)
                    sizesByPath[path] = await PdfThumbnailService.GetPageSizesAsync(path);

                void Apply()
                {
                    foreach (var row in group.Pages)
                    {
                        if (row.PageWidthPoints != null || !sizesByPath.TryGetValue(row.SourcePath, out var sizes)) continue;
                        // 0 = không đọc được: giữ khổ mặc định, không đọc lại mỗi lần cuộn.
                        var (w, h) = sizes != null && row.PageNumber - 1 < sizes.Length ? sizes[row.PageNumber - 1] : (0, 0);
                        row.PageWidthPoints = w;
                        row.PageHeightPoints = h;
                    }
                    double maxWidth = group.Pages.Where(p => p.PageWidthPoints > 0).Select(p => p.PageWidthPoints!.Value)
                        .DefaultIfEmpty(0).Max();
                    if (maxWidth <= 0) return;
                    foreach (var row in group.Pages)
                    {
                        if (row.PageWidthPoints is not > 0 || row.PageHeightPoints is not > 0) continue;
                        double baseWidth = PageRow.DefaultLayoutWidth * row.PageWidthPoints.Value / maxWidth;
                        double aspect = row.PageHeightPoints.Value / row.PageWidthPoints.Value;
                        if (row.BaseWidth != baseWidth) row.BaseWidth = baseWidth;
                        if (row.AspectRatio != aspect) row.AspectRatio = aspect;
                    }
                }

                Apply();
                // Bố cục dựng lại theo khổ thật; trang ở đỉnh khung nhìn đứng yên.
                if (ReferenceEquals(ReaderContinuousView.Pages, group.Pages)) ReaderContinuousView.RefreshPageSizes();
            }
            finally
            {
                _pageSizeLoads.Remove(group);
            }
        }

        // ── Công cụ sửa nhanh kiểu Foxit (xoay trang lưu file, chèn, xuất) ──────────────

        /// <summary>Cửa sổ chủ thực thi các thao tác sửa (nắm selection Organizer, workspace/undo).</summary>
        internal IReaderPageEditHost? EditHost { get; set; }

        /// <summary>Trang mà công cụ sửa áp lên: nếu trang đang xem nằm trong vùng chọn của Organizer
        /// thì lấy cả vùng chọn (xoay/xuất nhiều trang cùng lúc), không thì chỉ trang đang xem —
        /// tránh sửa nhầm 1 vùng chọn cũ user đã quên khi đã chuyển sang xem trang khác.</summary>
        private IReadOnlyList<PageRow> GetEditTargetPages()
        {
            if (_readerGroup == null || _readerPage == null) return Array.Empty<PageRow>();
            // Ưu tiên vùng chọn ở panel thumbnail bên trái (cùng cửa sổ), rồi tới Organizer của cửa sổ ghép.
            var panel = ReaderSidePanel.SelectedPages;
            if (panel.Count > 1 && panel.Contains(_readerPage)) return panel;
            var selected = EditHost?.GetSelectedPages(_readerGroup) ?? Array.Empty<PageRow>();
            return selected.Contains(_readerPage) ? selected : new[] { _readerPage };
        }

        private async void ReaderPageRotateLeft_Click(object sender, RoutedEventArgs e) => await RotateEditTargetsAsync(-90);
        private async void ReaderPageRotateRight_Click(object sender, RoutedEventArgs e) => await RotateEditTargetsAsync(90);

        private async Task RotateEditTargetsAsync(int deltaDegrees)
        {
            var pages = GetEditTargetPages();
            if (pages.Count == 0 || EditHost == null) return;
            await EditHost.RotatePagesAsync(pages, deltaDegrees);
        }

        private async void ReaderInsertPages_Click(object sender, RoutedEventArgs e)
        {
            if (_readerGroup == null || _readerPage == null || EditHost == null) return;
            // Chèn SAU trang đang xem (như Foxit mặc định "After current page").
            int insertIndex = _readerGroup.Pages.IndexOf(_readerPage) + 1;
            await EditHost.InsertPagesFromFileAsync(_readerGroup, insertIndex);
        }

        private async void ReaderExtractPages_Click(object sender, RoutedEventArgs e)
        {
            var pages = GetEditTargetPages();
            if (_readerGroup == null || pages.Count == 0 || EditHost == null) return;
            await EditHost.ExtractPagesAsync(_readerGroup, pages);
        }

        /// <summary>File nguồn vừa bị sửa (xoay trang/annotation) — bỏ mọi ảnh Viewer đang cache của các
        /// trang đó rồi render lại. <paramref name="geometryChanged"/> = false (chỉ thêm annotation): giữ ảnh
        /// cũ trên màn hình tới khi ảnh mới xong, không nhảy lại vị trí cuộn/zoom.</summary>
        internal void OnSourcePagesEdited(string path, IReadOnlyCollection<int> pages, bool geometryChanged)
        {
            ReaderSidePanel.OnSourceEdited(path);
            bool Matches(string p, int page) =>
                pages.Contains(page) && string.Equals(p, path, StringComparison.OrdinalIgnoreCase);

            lock (_readerCacheLock)
            {
                _readerCache.RemoveWhere(key => Matches(key.Path, key.Page));
                foreach (var key in _readerLoads.Keys.Where(key => Matches(key.Path, key.Page)).ToList())
                    _readerLoads.Remove(key);
            }
            // Xoay trang đổi khổ (DocumentSession đã xoá PageWidthPoints): đọc lại kích thước thật cho bố cục.
            if (geometryChanged && _readerGroup != null) _ = EnsureContinuousPageSizesAsync(_readerGroup);

            if (_readerGroup == null || _readerPage == null) return;
            // Chú thích: ảnh cũ nằm yên tới khi ảnh mới xong. Xoay trang: ảnh cũ sai tỉ lệ — bỏ, bố cục dựng lại theo khổ mới.
            ReaderContinuousView.InvalidatePages(r => Matches(r.SourcePath, r.PageNumber), dropImages: geometryChanged);
            if (geometryChanged) ReaderContinuousView.RefreshPageSizes();
        }

        private void ReaderRotateLeft_Click(object sender, RoutedEventArgs e) => SetReaderRotation((_readerRotation - 90 + 360) % 360);
        private void ReaderRotateRight_Click(object sender, RoutedEventArgs e) => SetReaderRotation((_readerRotation + 90) % 360);

        private void SetReaderRotation(int degrees)
        {
            _readerRotation = degrees;
            ReaderContinuousView.ViewRotation = degrees;
            ReapplyZoomMode();
            if (_findHits.Count > 0) ScheduleFindRefresh();
        }

        private async void ReaderPreviousPage_Click(object sender, RoutedEventArgs e)
            => await NavigateReaderAsync(-1);

        private async void ReaderNextPage_Click(object sender, RoutedEventArgs e)
            => await NavigateReaderAsync(1);

        private void ReaderFitPage_Click(object sender, RoutedEventArgs e)
        {
            _readerZoomMode = ReaderZoomMode.FitPage;
            ReapplyZoomMode();
        }

        private void ReaderFitWidth_Click(object sender, RoutedEventArgs e)
        {
            _readerZoomMode = ReaderZoomMode.FitWidth;
            ReapplyZoomMode();
        }

        private void ReaderActualSize_Click(object sender, RoutedEventArgs e)
            => SetReaderContinuousZoom(1.0, ReaderZoomMode.Manual);

        private void ReaderZoomIn_Click(object sender, RoutedEventArgs e)
            => ZoomContinuousAtPoint(ReaderContinuousZoom * ReaderZoomStep, ReaderContinuousView.ViewportCenter);

        private void ReaderZoomOut_Click(object sender, RoutedEventArgs e)
            => ZoomContinuousAtPoint(ReaderContinuousZoom / ReaderZoomStep, ReaderContinuousView.ViewportCenter);

        private async void ReaderPageBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await TryNavigateReaderPageFromBoxAsync();
        }

        private async void ReaderPageBox_LostFocus(object sender, RoutedEventArgs e)
            => await TryNavigateReaderPageFromBoxAsync();

        private void ReaderOpenExternal_Click(object sender, RoutedEventArgs e)
        {
            if (_readerPage == null) return;
            try
            {
                Process.Start(new ProcessStartInfo(_readerPage.SourcePath) { UseShellExecute = true });
            }
            catch { }
        }
    }
}
