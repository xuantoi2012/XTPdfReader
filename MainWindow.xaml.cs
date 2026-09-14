using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfToolkit.Controls;
using XTPdfMergeApp.Services;
using static XTPdfMergeApp.Services.VisualTreeHelpers;
using XTPdfMergeApp.Workspace;
using XTStyle.Controls;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;

namespace XTPdfMergeApp
{
    /// <summary>
    /// Cửa sổ chính — user tự kéo-thả file PDF (từ Explorer, hoặc từ nút "Mở
    /// thư mục xuất" ở XTSheet/XTPrint bên plugin AutoCAD) vào đây, hoặc bấm
    /// "Thêm file...". Mỗi file hiện thành 1 "WINDOW" riêng (DocumentGroup):
    /// tự do kéo-thả trang qua lại GIỮA các window (đổi file này ↔ file kia)
    /// hoặc sắp xếp lại NGAY TRONG 1 window, và tự do kéo CẢ WINDOW (bằng tiêu
    /// đề) để sắp xếp lại thứ tự — không có "1 output ghép chung": mỗi window
    /// có nút Lưu riêng (SaveGroup_Click), user tự quyết định lưu window nào
    /// ra đâu, có thể lưu lại nhiều lần (không tự đóng window sau khi lưu).
    ///
    /// Vạch chèn trang (RowDropIndicator) nằm NGAY BÊN TRONG từng window
    /// (ClipToBounds) — không dùng overlay chung toàn cột nên không thể hiện
    /// lấn sang window khác. Vạch chèn WINDOW (GroupDropIndicator) vẫn dùng
    /// overlay chung ở cấp toàn cục vì đó là vị trí GIỮA các window.
    ///
    /// Không còn chạy nền/khay hệ thống — đóng cửa sổ là thoát hẳn app (xem
    /// App.xaml.cs). Khi pdfFactory đẩy 1 file mới vào lúc cửa sổ đang mở sẵn
    /// (qua named pipe), CHỈ thêm file, không đụng WindowState/kích thước.
    /// </summary>
    public partial class MainWindow : XTWindow
    {
        /// <summary>Payload kéo-thả 1 cụm trang — giữ luôn tham chiếu hàng NGUỒN để Drop biết rút khỏi đâu (khác hàng đích thì là "chuyển file", cùng hàng thì là "sắp xếp lại").</summary>
        private sealed class PageDragPayload
        {
            public required DocumentGroup SourceGroup { get; init; }
            public required List<PageRow> Pages { get; init; }
        }

        private readonly PdfWorkspace _workspace = new();
        private ObservableCollection<DocumentGroup> _groups => _workspace.Documents;

        private Point _pageDragStart;
        private ListBox? _pageDragSource;
        private bool _pageDeferSelection;
        private bool _pageDeferCtrlToggle;
        private bool _pageDragging;
        private List<PageRow> _pageClipboard = new();
        private DocumentGroup? _pageClipboardSource;
        private bool _pageClipboardIsCut;

        private Point _groupDragStart;
        private DocumentGroup? _groupDragCandidate;
        private bool _layoutSwitchInProgress;
        private bool _splitViewMode;
        private DocumentGroup? _splitLeftGroup;
        private DocumentGroup? _splitRightGroup;
        private DocumentGroup? _focusedGroup;
        private bool _focusRestoreWasSplit;
        private DocumentGroup? _focusRestoreLeftGroup;
        private DocumentGroup? _focusRestoreRightGroup;
        private double _focusRestoreHorizontalOffset;
        private double _focusRestoreVerticalOffset;
        private DocumentGroup? _statusSelectedGroup;
        private PageRow? _statusSelectedPage;
        private int _statusSelectedPageCount;

        /// <summary>Hàng (ngang, mặc định) hay Cột (dọc) — đổi qua LayoutModeButton_Click, chi phối cả DataTemplate/ItemsPanel của GroupsList lẫn hướng cuộn/hướng tính vị trí chèn khi kéo-thả.</summary>
        private enum GroupLayoutMode { Row, Column }
        private GroupLayoutMode _layoutMode = GroupLayoutMode.Row;
        private bool _autoThumbnailSize = true;

        /// <summary>True trong lúc đang kéo file/trang/window ngang qua vùng DropZoneOuterBorder — dùng làm
        /// "safety net" ở MainWindow_PreviewMouseMove: 1 sự kiện MouseMove THƯỜNG (không phải DragOver) sẽ
        /// không bao giờ xảy ra trong lúc drag OLE thật sự đang diễn ra, nên nếu nó xảy ra khi cờ này đang bật
        /// nghĩa là phiên kéo-thả đã kết thúc theo cách KHÔNG bắn DragLeave (huỷ giữa chừng bằng Esc, hoặc thả
        /// ra ngoài ứng dụng) — dọn hiệu ứng ngay, tránh kẹt shadow/dash mãi mãi.</summary>
        private bool _dropHighlightActive;
        private readonly System.Windows.Media.Effects.DropShadowEffect _emptyHintShadow = new()
        {
            Color = Color.FromRgb(3, 109, 246),
            BlurRadius = 30,
            ShadowDepth = 0,
            Opacity = 0
        };

        /// <summary>Border chèn-trang (RowDropIndicator) đang hiện, nếu có — ẩn đi trước khi tính/hiện cái mới, tránh sót nhiều vạch cùng lúc khi kéo qua lại giữa các window.</summary>
        private Border? _activeRowIndicator;
        private Border? _pageScrollDragThumb;
        private ScrollViewer? _pageScrollDragViewer;
        private bool _pageScrollDragHorizontal;
        private double _pageScrollDragStartPosition;
        private double _pageScrollDragStartOffset;
        private const double PageScrollHorizontalMinThumb = 100;
        private const double PageScrollVerticalMinThumb = 72;
        private const double PageScrollHorizontalMaxThumb = 190;
        private const double PageScrollVerticalMaxThumb = 150;

        private readonly HashSet<string> _loadingSourcePaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<ListBox> _thumbnailViewportScanQueued = new();
        private readonly HashSet<ListBox> _nearbyPrefetchScanQueued = new();
        private readonly Dictionary<ListBox, int> _nearbyPrefetchDirections = new();
        private readonly DispatcherTimer _diagnosticsTimer = new(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        private int _activeFileLoadCount;
        private int _activeWarmThumbnailGroups;
        private long _warmThumbnailCompleted;
        private long _prefetchThumbnailCompleted;

        // ── Cỡ xem trước thumbnail — 4 mức, đổi qua ThumbnailSizeCombo ───────
        // DependencyProperty (không phải field thường) để binding trong
        // MergeFileTemplate tự cập nhật ngay khi đổi, qua RelativeSource AncestorType=Window.

        public static readonly DependencyProperty ThumbnailWidthProperty = DependencyProperty.Register(
            nameof(ThumbnailWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(120.0));
        public double ThumbnailWidth
        {
            get => (double)GetValue(ThumbnailWidthProperty);
            set => SetValue(ThumbnailWidthProperty, value);
        }

        public static readonly DependencyProperty ThumbnailHeightProperty = DependencyProperty.Register(
            nameof(ThumbnailHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(90.0));
        public double ThumbnailHeight
        {
            get => (double)GetValue(ThumbnailHeightProperty);
            set => SetValue(ThumbnailHeightProperty, value);
        }

        public static readonly DependencyProperty RowCardWidthProperty = DependencyProperty.Register(
            nameof(RowCardWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(600.0));
        public double RowCardWidth
        {
            get => (double)GetValue(RowCardWidthProperty);
            set => SetValue(RowCardWidthProperty, value);
        }

        public static readonly DependencyProperty AdaptiveDocumentWidthProperty = DependencyProperty.Register(
            nameof(AdaptiveDocumentWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(320.0));
        public double AdaptiveDocumentWidth
        {
            get => (double)GetValue(AdaptiveDocumentWidthProperty);
            set => SetValue(AdaptiveDocumentWidthProperty, value);
        }

        /// <summary>Chiều cao khả dụng cho phần cuộn trang của 1 CỘT (chế độ Column) — cập nhật động theo ActualHeight của GroupsScrollViewer (xem constructor) thay vì số cố định, để cột dùng HẾT chiều cao cửa sổ đang có thay vì bị cắt cụt giữa chừng khi cửa sổ cao hơn 1 giá trị hardcode.</summary>
        public static readonly DependencyProperty ColumnMaxContentHeightProperty = DependencyProperty.Register(
            nameof(ColumnMaxContentHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(480.0));
        public double ColumnMaxContentHeight
        {
            get => (double)GetValue(ColumnMaxContentHeightProperty);
            set => SetValue(ColumnMaxContentHeightProperty, value);
        }

        public static readonly DependencyProperty SavingProgressPercentProperty = DependencyProperty.Register(
            nameof(SavingProgressPercent), typeof(double), typeof(MainWindow), new PropertyMetadata(0.0));
        public double SavingProgressPercent
        {
            get => (double)GetValue(SavingProgressPercentProperty);
            set => SetValue(SavingProgressPercentProperty, value);
        }

        public static readonly DependencyProperty SavingStatusTextProperty = DependencyProperty.Register(
            nameof(SavingStatusText), typeof(string), typeof(MainWindow), new PropertyMetadata("Đang lưu…"));
        public string SavingStatusText
        {
            get => (string)GetValue(SavingStatusTextProperty);
            set => SetValue(SavingStatusTextProperty, value);
        }

        public static readonly DependencyProperty SplitThumbnailItemWidthProperty = DependencyProperty.Register(
            nameof(SplitThumbnailItemWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(176.0));
        public double SplitThumbnailItemWidth
        {
            get => (double)GetValue(SplitThumbnailItemWidthProperty);
            set => SetValue(SplitThumbnailItemWidthProperty, value);
        }

        public static readonly DependencyProperty SplitThumbnailItemHeightProperty = DependencyProperty.Register(
            nameof(SplitThumbnailItemHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(136.0));
        public double SplitThumbnailItemHeight
        {
            get => (double)GetValue(SplitThumbnailItemHeightProperty);
            set => SetValue(SplitThumbnailItemHeightProperty, value);
        }

        public static readonly DependencyProperty SplitPaneContentWidthProperty = DependencyProperty.Register(
            nameof(SplitPaneContentWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(400.0));
        public double SplitPaneContentWidth
        {
            get => (double)GetValue(SplitPaneContentWidthProperty);
            set => SetValue(SplitPaneContentWidthProperty, value);
        }

        private void UpdateSplitPaneContentWidth()
        {
            if (SplitHostGrid == null) return;
            var result = AdaptiveLayoutMath.ComputeSplitPane(
                SplitHostGrid.ActualWidth, _focusedGroup != null, _autoThumbnailSize, ThumbnailWidth, ThumbnailHeight);
            SplitPaneContentWidth = result.ContentWidth;
            ThumbnailWidth = result.ThumbnailWidth;
            ThumbnailHeight = result.ThumbnailHeight;
            // ItemWidth giữ KHÍT đúng thumbnailWidth+gap (KHÔNG chia đều ContentWidth cho số cột
            // như trước) — để VirtualizingWrapPanel tự xếp CÀNG NHIỀU ô càng tốt theo bề rộng thật
            // đang có, thay vì cố định số cột trước rồi giãn mỗi ô ra ăn hết phần dư (đúng bug "còn
            // nhiều chỗ trống mà không thêm cột" đã gặp — xem AdaptiveLayoutMath.ComputeSplitPane).
            SplitThumbnailItemWidth = ThumbnailWidth + AdaptiveLayoutMath.SplitItemGap;
            SplitThumbnailItemHeight = ThumbnailHeight + 6;

            // VirtualizingWrapPanel (Split/Focus, xem DocumentGroupTemplateSplit) không tự nhận ra
            // ItemContainerStyle.Width/Height vừa đổi (chỉ cache theo index, chỉ đo lại khi CUỘN) —
            // ép đo lại ngay, nếu không phải cuộn 1 cái mới thấy layout mới (đúng bug user báo khi
            // đổi cỡ thumbnail lúc đang ở Focus mode). CHỈ InvalidateMeasure() KHÔNG đủ — panel có
            // thể đo lại đúng ItemWidth mới nhưng KHÔNG tự dàn lại số cột/hàng cho tới khi Arrange
            // cũng chạy lại (đúng lỗi đã gặp: chọn cỡ thumbnail lớn ở Chia đôi, còn thừa nhiều chỗ
            // ngang nhưng vẫn chỉ xếp 1 thumbnail/hàng cho tới khi cuộn hoặc thao tác khác kích hoạt
            // Arrange tình cờ) — gọi cả 2 để chắc chắn dàn lại NGAY.
            foreach (var panel in FindVisualChildren<VirtualizingWrapPanel>(SplitHostGrid))
            {
                panel.InvalidateMeasure();
                panel.InvalidateArrange();
            }
        }

        /// <summary>Bề rộng TỐI ĐA của 1 card (chế độ Cột) — khớp đúng ThumbnailWidth + đệm
        /// 2 bên, để tên file dài không kéo giãn card nữa (Border MaxWidth trong
        /// DocumentGroupTemplateColumn), buộc TextTrimming của tiêu đề phải kích hoạt
        /// thay vì Auto-size theo nội dung tự nhiên.</summary>
        public static readonly DependencyProperty CardMaxWidthProperty = DependencyProperty.Register(
            nameof(CardMaxWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(144.0));
        public double CardMaxWidth
        {
            get => (double)GetValue(CardMaxWidthProperty);
            set => SetValue(CardMaxWidthProperty, value);
        }

        public static readonly DependencyProperty ColumnPageListWidthProperty = DependencyProperty.Register(
            nameof(ColumnPageListWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(184.0));
        public double ColumnPageListWidth
        {
            get => (double)GetValue(ColumnPageListWidthProperty);
            set => SetValue(ColumnPageListWidthProperty, value);
        }

        /// <summary>(Width, Height) cho từng mức — tỉ lệ ~4:3 (khớp khung A3 Ngang thường gặp trong bản vẽ dự án). Ảnh render gốc luôn ở độ phân giải cao (xem RenderThumbnailWidthPx) nên đổi cỡ hiển thị KHÔNG cần render lại, chỉ resize khung — tức thì, không giật.</summary>
        private static readonly (double W, double H)[] ThumbnailSizePresets =
        {
            (160, 120), (230, 173), (300, 225), (400, 300)
        };

        private void ThumbnailSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int idx = ThumbnailSizeCombo.SelectedIndex;
            if (idx < 0) return;
            _autoThumbnailSize = idx == 0;
            if (!_autoThumbnailSize)
            {
                int presetIndex = idx - 1;
                if (presetIndex < 0 || presetIndex >= ThumbnailSizePresets.Length) return;
                (ThumbnailWidth, ThumbnailHeight) = ThumbnailSizePresets[presetIndex];
            }
            UpdateAdaptiveLayout();
            UpdateSplitPaneContentWidth(); // đặt lại SplitThumbnailItemWidth/Height luôn, xem hàm này
            _ = Dispatcher.InvokeAsync(QueueVisibleThumbnailScans, DispatcherPriority.ContextIdle);
            // thumbnail + margin item + scrollbar dọc + border/padding card.
            CardMaxWidth = ThumbnailWidth + 48;
            ColumnPageListWidth = ThumbnailWidth + 24;
        }

        public MainWindow()
        {
            InitializeComponent();
            EmptyHint.Effect = _emptyHintShadow;
            // Safety net: 1 phiên kéo-thả file từ ngoài (Explorer) có thể kết thúc mà KHÔNG bắn
            // DragLeave (huỷ bằng Esc, hoặc thả ra ngoài cửa sổ) khiến hiệu ứng highlight bị kẹt
            // mãi mãi. MouseMove THƯỜNG không bao giờ xảy ra trong lúc 1 phiên OLE drag đang diễn ra
            // trên cửa sổ này, nên hễ thấy nó xảy ra trong khi highlight đang bật là dọn ngay.
            PreviewMouseMove += (_, _) => { if (_dropHighlightActive) SetDropHighlight(false); };
            Deactivated += (_, _) => { if (_dropHighlightActive) SetDropHighlight(false); };
            // Viewer là Window RIÊNG, Hide() (không Close()) khi user bấm nút X của nó — nếu vẫn
            // còn Hide() lúc MainWindow đóng, nó vẫn nằm trong Application.Current.Windows và
            // ShutdownMode.OnMainWindowClose sẽ không kích hoạt đúng cách (WPF coi cửa sổ đó vẫn
            // "đang mở"). Phải chủ động Close() THẬT ở đây (AllowRealClose=true bỏ qua
            // ReaderWindow_Closing's Cancel+Hide) trước khi cho MainWindow đóng.
            Closing += (_, _) =>
            {
                if (ReaderWindow.Instance is { } reader)
                {
                    reader.AllowRealClose = true;
                    reader.Close();
                }
            };
            _diagnosticsTimer.Tick += (_, _) => RefreshDiagnosticsOverlay();
            _workspace.History.StateChanged += (_, _) => RefreshUndoRedoUi();

            GroupsList.ItemsSource = _groups;
            _groups.CollectionChanged += (_, _) =>
            {
                bool empty = _groups.Count == 0;
                EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
                if (!_splitViewMode && _focusedGroup == null)
                    GroupsScrollViewer.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
                if (_splitViewMode &&
                    ((_splitLeftGroup != null && !_groups.Contains(_splitLeftGroup)) ||
                     (_splitRightGroup != null && !_groups.Contains(_splitRightGroup))))
                    ResetLayoutButton_Click(this, new RoutedEventArgs());
                if (_focusedGroup != null && !_groups.Contains(_focusedGroup))
                    ExitGroupFocusMode(restorePreviousView: true);
                if (_statusSelectedGroup != null && !_groups.Contains(_statusSelectedGroup))
                    SetStatusSelection(null, null, 0);
                UpdateStatusBar();
            };

            // Trừ ~60px cho phần tiêu đề/đệm của mỗi card (Grid.Row=0 header +
            // margin) — phần còn lại là chiều cao THẬT sự cột có thể dùng để
            // cuộn trang, khớp đúng viewport hiện tại của cửa sổ (resize cửa
            // sổ cũng tự cập nhật lại qua SizeChanged).
            GroupsScrollViewer.SizeChanged += (_, _) =>
            {
                UpdateColumnViewportHeight();
                if (_layoutMode == GroupLayoutMode.Row && !_splitViewMode)
                    UpdateGroupsListRowWidth();
                UpdateAdaptiveLayout();
            };
            SplitHostGrid.SizeChanged += (_, _) => UpdateSplitPaneContentWidth();

            // Maximize qua NÚT (BtnMaximize_Click) đặt WindowState bằng code managed, layout con
            // cascade đúng ngay. Maximize qua DOUBLE-CLICK tiêu đề lại đi qua đường Win32 gốc
            // (HTCAPTION → OS tự gửi WM_SYSCOMMAND/SC_MAXIMIZE, xử lý trong XTWindow.WndProc của
            // XTStyle.NET) — GroupsScrollViewer.SizeChanged đã xác nhận KHÔNG bắn tin cậy trong
            // đường này (đúng bug "double-click title không tự dàn layout" dù nút Maximize vẫn ổn).
            // Hook thẳng StateChanged/SizeChanged của CHÍNH cửa sổ ở đây — đây là nguồn gốc thật của
            // resize (ActualWidth/Height do WPF tự tính sau measure/arrange), không phụ thuộc
            // GroupsScrollViewer có tự nhận ra cascade hay không, nên chắc chắn bắt được MỌI đường
            // maximize/restore bất kể qua nút hay double-click.
            StateChanged += (_, _) =>
            {
                LogWindowResizeDebug($"StateChanged fired -> WindowState={WindowState} ActualWidth={ActualWidth:F0} ActualHeight={ActualHeight:F0}");
                RefreshWorkspaceWidthAfterLayout();
            };
            SizeChanged += (_, e) =>
            {
                LogWindowResizeDebug($"SizeChanged fired -> WindowState={WindowState} NewSize=({e.NewSize.Width:F0},{e.NewSize.Height:F0}) PrevSize=({e.PreviousSize.Width:F0},{e.PreviousSize.Height:F0})");
                RefreshWorkspaceWidthAfterLayout();
            };
            GroupsScrollViewer.SizeChanged += (_, e) =>
                LogWindowResizeDebug($"GroupsScrollViewer.SizeChanged fired -> NewSize=({e.NewSize.Width:F0},{e.NewSize.Height:F0})");

            CleanupAfterMergeCheck.IsChecked = MergeAppSettingsStore.GetCleanupAfterMerge();

            // Nếu user đã bật trước đó, tự kiểm tra registry pdfFactory có
            // còn khớp không (có thể bị lệch nếu cài lại/update pdfFactory)
            // và tự sửa lại ÂM THẦM — không cần hỏi/thông báo gì, chỉ cập
            // nhật dòng trạng thái nhỏ dưới checkbox.
            bool wantPdfFactoryEnabled = MergeAppSettingsStore.GetPdfFactoryViewEnabled();
            UsePdfFactoryViewCheck.IsChecked = wantPdfFactoryEnabled;
            if (wantPdfFactoryEnabled) ReconcilePdfFactoryRegistry();
            RefreshPdfFactoryStatus();
            UpdateStatusBar();
            RefreshUndoRedoUi();
            var savedLayout = MergeAppSettingsStore.GetLayoutMode();
            ApplyLayoutMode(string.Equals(savedLayout, "Column", StringComparison.OrdinalIgnoreCase)
                ? GroupLayoutMode.Column
                : GroupLayoutMode.Row);
            if (MergeAppSettingsStore.GetViewerVisible())
            {
                ViewerToggleButton.IsChecked = true;
                // Hoãn tới sau khi MainWindow đã Show() xong (App.xaml.cs gọi Show() NGAY SAU khi
                // constructor này return) — ReaderWindow.Owner = this sẽ ném
                // InvalidOperationException nếu gán lúc MainWindow còn chưa từng hiện ra lần nào.
                _ = Dispatcher.InvokeAsync(() => EnsureReaderWindow().ShowAndActivate(), DispatcherPriority.Loaded);
            }
            else ViewerToggleButton.IsChecked = false;
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            _workspace.History.Undo();
            RefreshWorkspaceAfterHistoryChange();
        }

        private void RedoButton_Click(object sender, RoutedEventArgs e)
        {
            _workspace.History.Redo();
            RefreshWorkspaceAfterHistoryChange();
        }

        private void RefreshUndoRedoUi()
        {
            if (!Dispatcher.CheckAccess()) { _ = Dispatcher.InvokeAsync(RefreshUndoRedoUi); return; }
            UndoButton.IsEnabled = _workspace.History.CanUndo;
            RedoButton.IsEnabled = _workspace.History.CanRedo;
            UndoButton.ToolTip = _workspace.History.UndoDescription is { } undo ? $"Hoàn tác: {undo}" : "Không có thao tác để hoàn tác";
            RedoButton.ToolTip = _workspace.History.RedoDescription is { } redo ? $"Làm lại: {redo}" : "Không có thao tác để làm lại";
        }

        private void RefreshWorkspaceAfterHistoryChange()
        {
            SetStatusSelection(null, null, 0);
            ReleaseUnusedPdfDocuments();
            _ = Dispatcher.InvokeAsync(QueueVisibleThumbnailScans, DispatcherPriority.ContextIdle);
            UpdateStatusBar();
        }

        /// <summary>Kiểm tra registry "ViewPdf" của pdfFactory có đang trỏ đúng về app này chưa — nếu lệch (bị pdfFactory/cài lại ghi đè) thì tự set lại, không hỏi/không thông báo.</summary>
        private void ReconcilePdfFactoryRegistry()
        {
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exePath)) return;
            if (!PdfFactoryIntegrationService.IsEnabled(exePath))
                PdfFactoryIntegrationService.Enable(exePath);
        }

        /// <summary>Gọi từ App.xaml.cs khi pdfFactory chạy app này qua registry "ViewPdf" (user vừa bấm "View PDF file" trong khay Jobs) — thêm luôn file đó vào danh sách. KHÔNG đụng WindowState/kích thước cửa sổ.</summary>
        public void AddIncomingFile(string path) => AddIncomingFiles(new[] { path });
        public void AddIncomingFiles(IEnumerable<string> paths) => _ = AddFilesAsGroups(paths);

        // ════════════════════════════════════════════════════════════════════
        // Cài đặt — overlay modal (giống mẫu overlay của XTLicenseAdmin)
        // ════════════════════════════════════════════════════════════════════

        private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsOverlay.Visibility = Visibility.Visible;
        private void CloseSettings_Click(object sender, RoutedEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;

        /// <summary>Bấm ra ngoài card (lên nền mờ) thì đóng overlay — bấm bên TRONG card thì SettingsCard_MouseLeftButtonUp đánh dấu Handled để không lọt xuống đây.</summary>
        private void SettingsOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;
        private void SettingsCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

        private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            DiagnosticsOverlay.Visibility = Visibility.Visible;
            RefreshDiagnosticsOverlay();
            if (!_diagnosticsTimer.IsEnabled)
                _diagnosticsTimer.Start();
        }

        private void DiagnosticsOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            DiagnosticsOverlay.Visibility = Visibility.Collapsed;
            _diagnosticsTimer.Stop();
        }

        private void DiagnosticsCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

        private void RefreshDiagnosticsOverlay()
        {
            if (DiagnosticsOverlay.Visibility != Visibility.Visible) return;

            var cacheStats = GetThumbnailCacheStats();
            var readerStats = ReaderWindow.GetReaderCacheStats();
            int totalPages = _groups.Sum(g => g.Pages.Count);
            int loading = LoadingFileCount;
            int activeFileLoads = Volatile.Read(ref _activeFileLoadCount);
            int activeRenderJobs = Volatile.Read(ref _activeThumbnailRenderCount);
            int activeNativePdfium = PdfThumbnailService.ActiveNativeCalls;
            int waitingNativePdfium = PdfThumbnailService.WaitingNativeCalls;
            int activeWarmGroups = Volatile.Read(ref _activeWarmThumbnailGroups);
            int foregroundRequests = Volatile.Read(ref _foregroundThumbnailRequests);
            int activeForegroundLoads = Volatile.Read(ref _activeForegroundThumbnailLoads);
            int backgroundWaiting = Volatile.Read(ref _backgroundPrefetchWaitingCount);
            int activeNearbyPrefetches = Volatile.Read(ref _activeNearbyPrefetches);
            long warmDone = Interlocked.Read(ref _warmThumbnailCompleted);
            long nearbyDone = Interlocked.Read(ref _nearbyPrefetchCompleted);

            ThreadPool.GetAvailableThreads(out int availableWorkers, out int availableIo);
            ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);

            using var process = Process.GetCurrentProcess();
            double privateMb = process.PrivateMemorySize64 / 1024d / 1024d;
            double workingMb = process.WorkingSet64 / 1024d / 1024d;
            double heapMb = GC.GetTotalMemory(false) / 1024d / 1024d;
            int processThreads = process.Threads.Count;

            DiagnosticsSummaryText.Text =
                $"File: {_groups.Count}\n" +
                $"Trang: {totalPages}\n" +
                $"Đang đọc file: {loading} (active {activeFileLoads})";

            DiagnosticsMemoryText.Text =
                $"Private: {privateMb:0} MB\n" +
                $"Working set: {workingMb:0} MB\n" +
                $"GC heap: {heapMb:0} MB\n" +
                $"WPF rendering tier: {RenderCapability.Tier >> 16} (0 = software)";

            DiagnosticsThreadsText.Text =
                $"Process threads: {processThreads}\n" +
                $"ThreadPool workers đang dùng: {maxWorkers - availableWorkers}/{maxWorkers}\n" +
                $"ThreadPool I/O đang dùng: {maxIo - availableIo}/{maxIo}\n" +
                $"Render jobs: {activeRenderJobs}/{ThumbnailRenderConcurrency}\n" +
                $"PDFium native gate: {activeNativePdfium} active, {waitingNativePdfium} waiting";

            DiagnosticsLoadText.Text =
                $"Thumbnail cache: {cacheStats.Cache} ảnh, {cacheStats.Bytes / 1048576d:0.0}/48 MB\n" +
                $"PDF docs/pages cache: {PdfThumbnailService.CachedDocumentCount}/{PdfThumbnailService.CachedNativePageCount}\n" +
                $"Page hits/loads: {PdfThumbnailService.NativePageCacheHits}/{PdfThumbnailService.NativePageLoads}\n" +
                $"Progressive yields: {PdfThumbnailService.ProgressiveYields}, max slice: {PdfThumbnailService.MaxNativeRenderSliceMilliseconds:0.0} ms\n" +
                $"Tile UI queue: {ReaderWindow.Instance?.PendingTilePresentations ?? 0}, max batch: {ReaderWindow.Instance?.MaxTilePresentationMilliseconds ?? 0:0.0} ms\n" +
                RenderDiagnostics.Summary + "\n" +
                $"Reader cache: {readerStats.Cache} ảnh, {readerStats.Bytes / 1048576d:0.0}/160 MB (in-flight {readerStats.Inflight})\n" +
                $"Đang render/in-flight: {cacheStats.Inflight}\n" +
                $"Viewport pending: {foregroundRequests} (active {activeForegroundLoads}/{ForegroundThumbnailConcurrency})\n" +
                $"Prefetch đang chờ: {backgroundWaiting}\n" +
                $"Prefetch gần viewport: {activeNearbyPrefetches} - xong {nearbyDone}\n" +
                $"Warm nhóm đầu: {activeWarmGroups} - xong {warmDone}\n" +
                "Thumbnail tải theo vùng nhìn";

            DiagnosticsHintText.Text =
                $"PDFium native safe mode: app giữ document handle cho file đang active; render jobs chạy nền nhưng native PDFium được serialize để tránh crash. " +
                $"Prefetch gần viewport chạy trước cho hàng/cột vừa scroll; prefetch nền dùng tối đa {BackgroundThumbnailPrefetchConcurrency} slot và giữ cache thumbnail tối đa 48 MB.";
        }

        private void CleanupAfterMergeCheck_Changed(object sender, RoutedEventArgs e)
            => MergeAppSettingsStore.SetCleanupAfterMerge(CleanupAfterMergeCheck.IsChecked == true);

        /// <summary>Bật/tắt do user tự tick trong Cài đặt — không còn MessageBox chặn thao tác, chỉ cập nhật dòng trạng thái nhỏ (PdfFactoryStatusText) ngay dưới checkbox.</summary>
        private void UsePdfFactoryViewCheck_Changed(object sender, RoutedEventArgs e)
        {
            bool on = UsePdfFactoryViewCheck.IsChecked == true;
            if (!on)
            {
                PdfFactoryIntegrationService.Disable();
                RefreshPdfFactoryStatus();
                return;
            }

            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            bool ok = !string.IsNullOrEmpty(exePath) && PdfFactoryIntegrationService.Enable(exePath);
            if (!ok)
            {
                UsePdfFactoryViewCheck.IsChecked = false; // tự Unchecked lại (gọi lại nhánh trên) rồi mới set dòng cảnh báo bên dưới
                RefreshPdfFactoryStatus("Không tìm thấy cấu hình pdfFactory trên máy này (chưa cài, hoặc chưa in lần nào)");
                return;
            }
            RefreshPdfFactoryStatus();
        }

        /// <summary>Cập nhật dòng trạng thái nhỏ dưới checkbox pdfFactory — <paramref name="warning"/> khác null thì hiện cảnh báo (màu WarningBrush), ngược lại hiện "Đang bật"/"Đã tắt" theo đúng trạng thái checkbox hiện tại.</summary>
        private void RefreshPdfFactoryStatus(string? warning = null)
        {
            if (warning != null)
            {
                PdfFactoryStatusText.Text = "⚠ " + warning;
                PdfFactoryStatusText.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
                return;
            }

            bool on = UsePdfFactoryViewCheck.IsChecked == true;
            PdfFactoryStatusText.Text = on ? "● Đang bật" : "○ Đã tắt";
            PdfFactoryStatusText.Foreground = (System.Windows.Media.Brush)FindResource(on ? "SuccessBrush" : "TextMutedBrush");
        }

        private void SetStatusSelection(DocumentGroup? group, PageRow? page, int selectedPageCount)
        {
            _statusSelectedGroup = group;
            _statusSelectedPage = page;
            _statusSelectedPageCount = selectedPageCount;
            UpdateStatusBar();

            // Organizer là chức năng chính: chọn trang không tự mở Viewer. Chỉ đồng bộ
            // nội dung khi user đã chủ động bật cửa sổ Viewer.
            if (group == null || page == null) return;
            var reader = ReaderWindow.Instance;
            if (reader == null || !reader.IsVisible) return;
            reader.NotifySelectionChanged(group, page);
        }

        private int LoadingFileCount
        {
            get
            {
                lock (_loadingSourcePaths)
                    return _loadingSourcePaths.Count;
            }
        }

        private void UpdateStatusBar()
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(UpdateStatusBar);
                return;
            }

            int loading = LoadingFileCount;
            int? splitTotalPages = _splitViewMode && _splitLeftGroup != null && _splitRightGroup != null
                ? _splitLeftGroup.Pages.Count + _splitRightGroup.Pages.Count
                : null;
            FileCountStatusText.Text = StatusBarText.FileCount(splitTotalPages, _groups.Count, loading);

            SelectedFileStatusText.Text = StatusBarText.SelectedFile(
                GroupsList.SelectedItems.Count,
                _statusSelectedGroup?.FileName,
                _statusSelectedPageCount,
                _statusSelectedPage?.PageNumber);
        }

        // ════════════════════════════════════════════════════════════════════
        // DANH SÁCH — mỗi window = 1 DocumentGroup, kéo trang qua lại giữa các window, kéo cả window để sắp xếp
        // ════════════════════════════════════════════════════════════════════

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Chọn file PDF cần thêm",
                Filter = "PDF (*.pdf)|*.pdf",
                Multiselect = true
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            _ = AddFilesAsGroups(dlg.FileNames.ToArray());
        }

        /// <summary>Thêm 1 FILE = 1 WINDOW MỚI (cuối danh sách) — tách sẵn từng trang. Nếu file đó ĐÃ có window riêng rồi thì bỏ qua (không tạo trùng).</summary>
        private Task AddFilesAsGroups(IEnumerable<string> paths)
        {
            var tasks = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(AddFileAsGroup)
                .ToArray();

            return Task.WhenAll(tasks);
        }

        private async Task AddFileAsGroup(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return;

            try { fullPath = Path.GetFullPath(fullPath); }
            catch { return; }

            if (Dispatcher.CheckAccess())
            {
                if (HasGroupForSource(fullPath)) return;
            }
            else if (await Dispatcher.InvokeAsync(() => HasGroupForSource(fullPath)))
            {
                return;
            }

            if (!TryReserveLoadingSource(fullPath)) return;

            // Card hiện NGAY ở trạng thái "đang mở" trước khi biết số trang — user không phải
            // chờ round-trip đếm trang mới thấy có gì đó xuất hiện, dù việc đếm/tải vẫn cần
            // thời gian như cũ (chỉ khác lúc NÀO user thấy phản hồi, không phải làm nhanh hơn).
            DocumentGroup? placeholder = await Dispatcher.InvokeAsync(() => AddOpeningPlaceholder(fullPath));
            UpdateStatusBar();
            if (placeholder == null)
            {
                ReleaseLoadingSource(fullPath);
                return;
            }

            try
            {
                Interlocked.Increment(ref _activeFileLoadCount);
                int pageCount;
                try
                {
                    pageCount = await Task.Run(() => PdfThumbnailService.GetPageCountAsync(fullPath)).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeFileLoadCount);
                }

                await Dispatcher.InvokeAsync(() => FinishOpeningGroup(placeholder, fullPath, pageCount));
            }
            finally
            {
                ReleaseLoadingSource(fullPath);
                UpdateStatusBar();
            }
        }

        private bool HasGroupForSource(string fullPath)
            => _groups.Any(g => string.Equals(g.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase));

        private bool TryReserveLoadingSource(string fullPath)
        {
            lock (_loadingSourcePaths)
            {
                if (_loadingSourcePaths.Contains(fullPath)) return false;
                _loadingSourcePaths.Add(fullPath);
                return true;
            }
        }

        private void ReleaseLoadingSource(string fullPath)
        {
            lock (_loadingSourcePaths)
                _loadingSourcePaths.Remove(fullPath);
        }

        private DocumentGroup? AddOpeningPlaceholder(string fullPath)
        {
            if (HasGroupForSource(fullPath)) return null;
            var group = new DocumentGroup { SourcePath = fullPath };
            group.SetOpening(true);
            _groups.Add(group);
            return group;
        }

        /// <summary>Đổ trang thật vào đúng placeholder đã hiện sẵn từ <see cref="AddOpeningPlaceholder"/>
        /// (xem AddFileAsGroup) — không tạo group mới ở đây, tránh có 2 card cho cùng 1 file.</summary>
        private void FinishOpeningGroup(DocumentGroup group, string fullPath, int pageCount)
        {
            // User có thể đã tự đóng card "đang mở" (nút X) trước khi đếm trang xong.
            if (!_groups.Contains(group)) return;

            if (pageCount <= 0)
            {
                group.SetOpening(false);
                group.SetLoadError("Không mở được file (file hỏng hoặc không đọc được số trang)");
                return;
            }

            group.Pages.AddRange(Enumerable.Range(1, pageCount).Select(p => _workspace.CreatePlacement(fullPath, p)));
            group.SetOpening(false);

            _ = Dispatcher.InvokeAsync(QueueVisibleThumbnailScans, DispatcherPriority.ContextIdle);
            if (group.Pages.Count > 0 && ReaderWindow.Instance?.HasAnyPageShown != true)
                _ = EnsureReaderWindow().ShowPageAsync(group, group.Pages[0], preserveZoomMode: false);
            _ = WarmInitialThumbnailsAsync(group);

            // Không render hết ngay ở đây. Thumbnail được đưa qua queue theo viewport thật
            // của từng ListBox PDF; prefetch nền chỉ cache ảnh và tự nhường cho vùng đang thấy.
        }

        /// <summary>Cache theo (đường dẫn, số trang) — chuyển 1 trang qua lại giữa các
        /// window (copy) hay cuộn qua lại (virtualization tái dùng container) đều
        /// khỏi phải render lại PDF từ đầu, chỉ lấy lại ảnh đã có trong bộ nhớ.</summary>
        private static readonly object _thumbnailLock = new();
        private static long _thumbnailGeneration;
        internal const long ThumbnailCacheBudgetBytes = 48L * 1024 * 1024;
        private static readonly BitmapMemoryCache<(string Path, int Page)> _thumbnailCache = new(ThumbnailCacheBudgetBytes);
        private static readonly Dictionary<(string Path, int Page), Task<BitmapSource?>> _thumbnailLoads = new();
        private const int ThumbnailRenderConcurrency = 4;
        private const int ForegroundThumbnailConcurrency = 8;
        private const int MaxForegroundThumbnailPending = 24;
        private const int BackgroundThumbnailPrefetchConcurrency = 4;
        private const int PrefetchConcurrencyPerGroup = 2;
        private const int NearbyPrefetchConcurrency = 3;
        private const int MaxBackgroundThumbnailInflight = 24;
        private const int MaxForegroundRequestsBeforeBackground = 6;
        private const int MaxForegroundRequestsBeforeNearbyPrefetch = 12;
        private const int NearbyPrefetchPageCount = 28;
        private const int InitialWarmThumbnailCount = 4;
        private static readonly SemaphoreSlim _thumbnailRenderGate = new(ThumbnailRenderConcurrency);
        private static readonly SemaphoreSlim _foregroundThumbnailGate = new(ForegroundThumbnailConcurrency);
        private static readonly SemaphoreSlim _thumbnailPrefetchGate = new(BackgroundThumbnailPrefetchConcurrency);
        private static int _activeThumbnailRenderCount;
        private static int _foregroundThumbnailRequests;
        private static int _activeForegroundThumbnailLoads;
        private static int _backgroundPrefetchWaitingCount;
        private int _activeNearbyPrefetches;

        private long _nearbyPrefetchCompleted;

        internal static async Task LoadThumbnailFor(PageRow row)
        {
            if (!TryReserveThumbnailLoad(row)) return;

            var key = (row.SourcePath, row.PageNumber);
            if (TryGetCachedThumbnail(key, out var cached) && cached != null)
            {
                await SetRowThumbnailAsync(row, cached);
                return;
            }

            BitmapSource? bmp;
            try { bmp = await AwaitThumbnailLoadAsync(key, foreground: true); }
            catch
            {
                await ClearThumbnailQueuedAsync(row);
                return;
            }

            if (bmp == null)
            {
                await ClearThumbnailQueuedAsync(row);
                return;
            }

            await SetRowThumbnailAsync(row, bmp);
        }

        private static bool TryReserveThumbnailLoad(PageRow row)
        {
            if (row.Thumbnail != null || row.ThumbnailLoadQueued) return false;
            row.ThumbnailLoadQueued = true;
            return true;
        }

        private static async Task SetRowThumbnailAsync(PageRow row, BitmapSource bmp)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                if (row.Thumbnail == null) row.Thumbnail = bmp;
                row.ThumbnailLoadQueued = false;
                return;
            }

            try
            {
                await dispatcher.InvokeAsync(() =>
                {
                    if (row.Thumbnail == null) row.Thumbnail = bmp;
                    row.ThumbnailLoadQueued = false;
                }, DispatcherPriority.Background).Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // App đang đóng, Dispatcher shutdown giữa chừng khi lệnh này còn đang chờ tới lượt
                // — không còn ý nghĩa gì để gán thumbnail nữa, bỏ qua thay vì lộ ra thành
                // TaskCanceledException chưa bắt (crash lúc debug khi tắt app giữa lúc đang prefetch).
            }
        }

        private static Task ClearThumbnailQueuedAsync(PageRow row)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                row.ThumbnailLoadQueued = false;
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(() => row.ThumbnailLoadQueued = false, DispatcherPriority.Background).Task;
        }

        private static bool TryGetCachedThumbnail((string Path, int Page) key, out BitmapSource? cached)
        {
            lock (_thumbnailLock)
            {
                if (_thumbnailCache.TryGetValue(key, out cached))
                {
                    return true;
                }

                return false;
            }
        }

        private static (int Cache, int Inflight, long Bytes) GetThumbnailCacheStats()
        {
            lock (_thumbnailLock)
                return (_thumbnailCache.Count, _thumbnailLoads.Count, _thumbnailCache.Bytes);
        }

        private static int GetThumbnailInflightCount()
        {
            lock (_thumbnailLock)
                return _thumbnailLoads.Count;
        }

        private static bool IsThumbnailCachedOrLoading((string Path, int Page) key)
        {
            lock (_thumbnailLock)
                return _thumbnailCache.ContainsKey(key) || _thumbnailLoads.ContainsKey(key);
        }

        private static void CacheThumbnailLocked((string Path, int Page) key, BitmapSource bmp)
            => _thumbnailCache.Set(key, bmp);

        private static Task<BitmapSource?> GetThumbnailLoadTask((string Path, int Page) key)
        {
            lock (_thumbnailLock)
            {
                if (_thumbnailCache.TryGetValue(key, out var cached))
                {
                    return Task.FromResult<BitmapSource?>(cached);
                }

                if (!_thumbnailLoads.TryGetValue(key, out var task))
                {
                    task = RenderAndCacheThumbnailAsync(key);
                    _thumbnailLoads[key] = task;
                }

                return task;
            }
        }

        private static async Task<BitmapSource?> AwaitThumbnailLoadAsync((string Path, int Page) key, bool foreground)
        {
            if (foreground)
                Interlocked.Increment(ref _foregroundThumbnailRequests);

            try
            {
                if (foreground)
                {
                    await _foregroundThumbnailGate.WaitAsync().ConfigureAwait(false);
                    Interlocked.Increment(ref _activeForegroundThumbnailLoads);
                }

                return await GetThumbnailLoadTask(key).ConfigureAwait(false);
            }
            finally
            {
                if (foreground)
                {
                    Interlocked.Decrement(ref _activeForegroundThumbnailLoads);
                    _foregroundThumbnailGate.Release();
                    Interlocked.Decrement(ref _foregroundThumbnailRequests);
                }
            }
        }

        private static async Task WaitForBackgroundPrefetchTurnAsync(bool nearViewport, CancellationToken cancellationToken)
        {
            bool countedAsWaiting = false;
            int delayMs = 25;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int foregroundLimit = nearViewport
                        ? MaxForegroundRequestsBeforeNearbyPrefetch
                        : MaxForegroundRequestsBeforeBackground;
                    bool foregroundBusy = Volatile.Read(ref _foregroundThumbnailRequests) > foregroundLimit;
                    bool queueHasRoom = GetThumbnailInflightCount() < MaxBackgroundThumbnailInflight;
                    if (!foregroundBusy && queueHasRoom) return;

                    if (!countedAsWaiting)
                    {
                        Interlocked.Increment(ref _backgroundPrefetchWaitingCount);
                        countedAsWaiting = true;
                    }

                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    delayMs = Math.Min(160, delayMs + 15);
                }
            }
            finally
            {
                if (countedAsWaiting)
                    Interlocked.Decrement(ref _backgroundPrefetchWaitingCount);
            }
        }

        private static async Task<BitmapSource?> RenderAndCacheThumbnailAsync((string Path, int Page) key)
        {
            long generation = Interlocked.Read(ref _thumbnailGeneration);
            BitmapSource? bmp = null;
            await _thumbnailRenderGate.WaitAsync().ConfigureAwait(false);
            Interlocked.Increment(ref _activeThumbnailRenderCount);
            try
            {
                bmp = await Task.Run(() => PdfThumbnailService.RenderPageAsync(key.Path, key.Page - 1, RenderThumbnailWidthPx, priority: PdfRenderPriority.Thumbnail)).ConfigureAwait(false);
                return bmp;
            }
            finally
            {
                Interlocked.Decrement(ref _activeThumbnailRenderCount);
                _thumbnailRenderGate.Release();
                lock (_thumbnailLock)
                {
                    if (bmp != null && generation == Interlocked.Read(ref _thumbnailGeneration)) CacheThumbnailLocked(key, bmp);
                    _thumbnailLoads.Remove(key);
                }
            }
        }

        private async Task PrefetchRowsAsync(IReadOnlyList<PageRow> rows, bool nearViewport)
        {
            if (rows.Count == 0) return;

            if (nearViewport)
                Interlocked.Increment(ref _activeNearbyPrefetches);

            try
            {
                await Parallel.ForEachAsync(
                    rows,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = nearViewport ? NearbyPrefetchConcurrency : PrefetchConcurrencyPerGroup
                    },
                    async (row, cancellationToken) =>
                    {
                        var key = (row.SourcePath, row.PageNumber);
                        if (IsThumbnailCachedOrLoading(key)) return;

                        BitmapSource? bmp;
                        await WaitForBackgroundPrefetchTurnAsync(nearViewport, cancellationToken).ConfigureAwait(false);
                        await _thumbnailPrefetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            bmp = await GetThumbnailLoadTask(key).ConfigureAwait(false);
                        }
                        catch
                        {
                            bmp = null;
                        }
                        finally
                        {
                            _thumbnailPrefetchGate.Release();
                        }

                        if (bmp == null) return;

                        Interlocked.Increment(ref _prefetchThumbnailCompleted);
                        await SetRowThumbnailAsync(row, bmp).ConfigureAwait(false);
                        if (nearViewport)
                            Interlocked.Increment(ref _nearbyPrefetchCompleted);
                    }).ConfigureAwait(false);
            }
            finally
            {
                if (nearViewport)
                    Interlocked.Decrement(ref _activeNearbyPrefetches);
            }
        }

        private async Task WarmInitialThumbnailsAsync(DocumentGroup group)
        {
            Interlocked.Increment(ref _activeWarmThumbnailGroups);
            try
            {
                List<PageRow> pages = await Dispatcher.InvokeAsync(() =>
                    _groups.Contains(group) ? group.Pages.Take(InitialWarmThumbnailCount).ToList() : new List<PageRow>());

                foreach (var row in pages)
                {
                    var key = (row.SourcePath, row.PageNumber);
                    BitmapSource? bmp;

                    try { bmp = await AwaitThumbnailLoadAsync(key, foreground: true).ConfigureAwait(false); }
                    catch { continue; }

                    if (bmp == null) continue;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_groups.Contains(group) && group.Pages.Contains(row) && row.Thumbnail == null)
                        {
                            row.Thumbnail = bmp;
                            Interlocked.Increment(ref _warmThumbnailCompleted);
                        }
                    }, DispatcherPriority.Send);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeWarmThumbnailGroups);
            }
        }

        /// <summary>Scan viewport thật của từng ListBox PDF thay vì để Image.Loaded enqueue quá rộng.</summary>
        private void PageListBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ListBox listBox)
            {
                UpdatePageScrollOverlay(listBox);
                QueueVisibleThumbnailScan(listBox);
                QueueNearbyThumbnailPrefetch(listBox, direction: 1);
                _ = Dispatcher.InvokeAsync(() => UpdatePageScrollOverlay(listBox), DispatcherPriority.ContextIdle);
            }
        }

        private void PageListBox_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is ListBox listBox)
                UpdatePageScrollOverlay(listBox);
        }

        private void PageListBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            ListBox? listBox = sender as ListBox;
            if (listBox == null && e.OriginalSource is DependencyObject source)
                listBox = FindVisualParent<ListBox>(source);

            if (listBox != null)
            {
                UpdatePageScrollOverlay(listBox);
                QueueVisibleThumbnailScan(listBox);
                double change = (_splitViewMode || _focusedGroup != null) ? e.VerticalChange
                    : _layoutMode == GroupLayoutMode.Row ? e.HorizontalChange : e.VerticalChange;
                int direction = change < 0 ? -1 : 1;
                QueueNearbyThumbnailPrefetch(listBox, direction);
            }
        }

        private void UpdatePageScrollOverlay(ListBox listBox)
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(() => UpdatePageScrollOverlay(listBox), DispatcherPriority.Background);
                return;
            }

            var host = FindVisualParentByName(listBox, "RowIndicatorHost");
            if (host == null)
            {
                LogPageScrollSkip("RowIndicatorHost not found (listBox chưa gắn visual tree?)");
                return;
            }

            var overlay = FindVisualChildByName<Grid>(host, "PageScrollOverlay");
            var thumb = FindVisualChildByName<Border>(host, "PageScrollThumb");
            if (overlay == null || thumb == null)
            {
                LogPageScrollSkip($"overlay={(overlay == null ? "NULL" : "ok")} thumb={(thumb == null ? "NULL" : "ok")}");
                return;
            }

            int thumbCount = FindVisualChildren<Border>(host).Count(b => b.Name == "PageScrollThumb");
            int overlayCount = FindVisualChildren<Grid>(host).Count(g => g.Name == "PageScrollOverlay");
            if (thumbCount > 1 || overlayCount > 1)
            {
                LogPageScrollSkip($"DUPLICATE! thumbCount={thumbCount} overlayCount={overlayCount} host={RuntimeHelpers.GetHashCode(host)} thumbHash={RuntimeHelpers.GetHashCode(thumb)}");
            }

            // ScrollViewer.VerticalScrollBarVisibility="Hidden" của ListBox KHÔNG chặn được ScrollBar
            // thật hiện ra trong 1 số trường hợp (đã xác nhận qua log: ScrollBar.Visibility vẫn ra
            // Visible dù Hidden) — chồng lên overlay PageScrollThumb tự vẽ, trông như "2 thumb song
            // song". Ép Collapsed thẳng tay ở đây, chạy lại mỗi khi Loaded/SizeChanged/ScrollChanged
            // để không phụ thuộc vào việc WPF có tôn trọng Hidden hay không.
            foreach (var sb in FindVisualChildren<System.Windows.Controls.Primitives.ScrollBar>(host))
            {
                if (sb.Visibility != Visibility.Collapsed)
                {
                    LogPageScrollSkip($"native ScrollBar force-collapsed: orientation={sb.Orientation} wasVisibility={sb.Visibility} actualWidth={sb.ActualWidth:F1} actualHeight={sb.ActualHeight:F1}");
                    sb.Visibility = Visibility.Collapsed;
                }
            }

            bool horizontal = IsHorizontalPageScroll(listBox);
            if (FindPageScrollViewer(listBox) is not { } sv)
            {
                overlay.Visibility = Visibility.Collapsed;
                LogPageScrollSkip("ScrollViewer not found");
                return;
            }

            double scrollable = horizontal ? sv.ScrollableWidth : sv.ScrollableHeight;
            double viewport = horizontal ? sv.ViewportWidth : sv.ViewportHeight;
            double extent = horizontal ? sv.ExtentWidth : sv.ExtentHeight;
            if (scrollable <= 0.5 || viewport <= 0)
            {
                overlay.Visibility = Visibility.Collapsed;
                LogPageScrollSkip($"not scrollable: horizontal={horizontal} scrollable={scrollable:F1} viewport={viewport:F1} extent={extent:F1}");
                return;
            }

            double trackLength = horizontal ? overlay.ActualWidth : overlay.ActualHeight;
            if (trackLength <= 1)
            {
                // Overlay CHƯA được đo layout (thường do chính nó đang Collapsed từ 1 lần gọi trước).
                // KHÔNG set Collapsed ở đây — phần tử Collapsed vĩnh viễn có ActualWidth/Height = 0 vì
                // WPF bỏ qua đo/sắp xếp phần tử Collapsed, tạo vòng lặp tự khoá không bao giờ thoát ra
                // được (đây là nguyên nhân thumb biến mất vĩnh viễn dù vẫn còn scroll). Ép Visible rồi
                // thử đo lại ở layout pass sau.
                overlay.Visibility = Visibility.Visible;
                LogPageScrollSkip($"overlay chưa đo layout: horizontal={horizontal} scrollable={scrollable:F1} viewport={viewport:F1} trackLength={trackLength:F1}");
                _ = Dispatcher.InvokeAsync(() => UpdatePageScrollOverlay(listBox), DispatcherPriority.ContextIdle);
                return;
            }

            overlay.Visibility = Visibility.Visible;
            thumb.Cursor = horizontal ? Cursors.SizeWE : Cursors.SizeNS;
            double normalizedExtent = Math.Max(extent, viewport + scrollable);
            double ratio = Math.Clamp(viewport / Math.Max(1, normalizedExtent), 0, 1);
            double thumbLength = ComputePageScrollThumbLength(trackLength, ratio, horizontal);
            double travel = Math.Max(0, trackLength - thumbLength);
            double offset = horizontal ? sv.HorizontalOffset : sv.VerticalOffset;
            double position = scrollable <= 0 ? 0 : (offset / scrollable) * travel;

            if (horizontal)
            {
                thumb.Width = thumbLength;
                thumb.Height = 13;
            }
            else
            {
                thumb.Width = 13;
                thumb.Height = thumbLength;
            }

            // Freezable khai báo inline trong DataTemplate (không x:Name) có thể bị WPF đóng băng
            // (IsFrozen) sau khi template được tối ưu hoá — cùng gốc bug với ScaleTransform ở
            // ClearTileCanvas. Nếu dính, thay hẳn bằng transform mới thay vì bỏ qua (để thumb vẫn
            // dịch chuyển đúng vị trí thay vì đứng im tại chỗ cũ).
            if (thumb.RenderTransform is not TranslateTransform { IsFrozen: false } transform)
            {
                transform = new TranslateTransform();
                thumb.RenderTransform = transform;
                LogPageScrollSkip($"transform replaced (frozen/missing) thumbHash={RuntimeHelpers.GetHashCode(thumb)} newTransformHash={RuntimeHelpers.GetHashCode(transform)}");
            }
            transform.X = horizontal ? Math.Clamp(position, 0, travel) : 0;
            transform.Y = horizontal ? 0 : Math.Clamp(position, 0, travel);

            LogPageScrollThumbDebug(horizontal, trackLength, ratio, thumbLength, thumb, overlay);
        }

        private static DateTime _lastPageScrollDebugLog = DateTime.MinValue;
        private static DateTime _lastPageScrollSkipLog = DateTime.MinValue;
        private static readonly string PageScrollDebugLogPath =
            Path.Combine(Path.GetTempPath(), "XTPdfMergeApp_ScrollDebug.log");

        private static void LogPageScrollSkip(string reason)
        {
            var now = DateTime.Now;
            if ((now - _lastPageScrollSkipLog).TotalMilliseconds < 250) return;
            _lastPageScrollSkipLog = now;
            try
            {
                string line = $"{now:HH:mm:ss.fff} | SKIP | {reason}";
                File.AppendAllText(PageScrollDebugLogPath, line + Environment.NewLine);
                Debug.WriteLine("[PageScrollThumb] " + line);
            }
            catch
            {
                // best-effort debug log only
            }
        }

        private static DateTime _lastDropIndicatorLog = DateTime.MinValue;
        private static readonly string DropIndicatorDebugLogPath =
            Path.Combine(Path.GetTempPath(), "XTPdfMergeApp_DropIndicator.log");

        private static readonly string WindowResizeDebugLogPath =
            Path.Combine(Path.GetTempPath(), "XTPdfMergeApp_WindowResize.log");

        /// <summary>Chẩn đoán bug "double-click tiêu đề maximize không tự dàn layout, bấm nút
        /// Maximize thì lại được" — KHÔNG throttle (sự kiện resize cửa sổ hiếm, do user chủ động gây
        /// ra, không phải hàng trăm lần/giây như tile/zoom) để thấy đủ MỌI lần StateChanged/
        /// SizeChanged/GroupsScrollViewer.SizeChanged có bắn hay không, theo đúng thứ tự thời gian,
        /// khi so sánh giữa 2 cách maximize.</summary>
        private static void LogWindowResizeDebug(string info)
        {
            try
            {
                string line = $"{DateTime.Now:HH:mm:ss.fff} | {info}";
                File.AppendAllText(WindowResizeDebugLogPath, line + Environment.NewLine);
                Debug.WriteLine("[WindowResize] " + line);
            }
            catch
            {
                // best-effort debug log only
            }
        }


        private static void LogDropIndicatorSkip(string info)
        {
            var now = DateTime.Now;
            if ((now - _lastDropIndicatorLog).TotalMilliseconds < 150) return;
            _lastDropIndicatorLog = now;
            try
            {
                string line = $"{now:HH:mm:ss.fff} | {info}";
                File.AppendAllText(DropIndicatorDebugLogPath, line + Environment.NewLine);
                Debug.WriteLine("[DropIndicator] " + line);
            }
            catch
            {
                // best-effort debug log only
            }
        }

        private void LogPageScrollThumbDebug(bool horizontal, double trackLength, double ratio, double computedThumbLength, Border thumb, Grid overlay)
        {
            var now = DateTime.Now;
            if ((now - _lastPageScrollDebugLog).TotalMilliseconds < 250) return;
            _lastPageScrollDebugLog = now;

            try
            {
                double min = horizontal ? PageScrollHorizontalMinThumb : PageScrollVerticalMinThumb;
                string line = $"{now:HH:mm:ss.fff} | {(horizontal ? "H" : "V")} | WindowState={WindowState} " +
                    $"| track={trackLength:F1} ratio={ratio:F3} computed={computedThumbLength:F1} min={min:F1} " +
                    $"| thumb.Width={thumb.ActualWidth:F1} thumb.Height={thumb.ActualHeight:F1} " +
                    $"thumb.MinWidth={thumb.MinWidth:F1} thumb.MinHeight={thumb.MinHeight:F1} " +
                    $"| overlay.ActualWidth={overlay.ActualWidth:F1} overlay.ActualHeight={overlay.ActualHeight:F1}";
                File.AppendAllText(PageScrollDebugLogPath, line + Environment.NewLine);
                Debug.WriteLine("[PageScrollThumb] " + line);
            }
            catch
            {
                // best-effort debug log only
            }
        }

        private bool IsHorizontalPageScroll(ListBox listBox)
            => !_splitViewMode && _focusedGroup == null && _layoutMode == GroupLayoutMode.Row;

        private void PageScrollOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Grid overlay) return;
            if (FindVisualParentByName(e.OriginalSource as DependencyObject ?? overlay, "PageScrollThumb") != null) return;
            if (FindVisualChild<ListBox>(FindVisualParentByName(overlay, "RowIndicatorHost") ?? overlay) is not { } listBox) return;
            if (FindPageScrollViewer(listBox) is not { } sv) return;

            var thumb = FindVisualChildByName<Border>(overlay, "PageScrollThumb");
            bool horizontal = IsHorizontalPageScroll(listBox);
            double scrollable = horizontal ? sv.ScrollableWidth : sv.ScrollableHeight;
            if (scrollable <= 0) return;

            double thumbLength = Math.Max(
                horizontal ? thumb?.ActualWidth ?? PageScrollHorizontalMinThumb : thumb?.ActualHeight ?? PageScrollVerticalMinThumb,
                horizontal ? PageScrollHorizontalMinThumb : PageScrollVerticalMinThumb);
            double trackLength = horizontal ? overlay.ActualWidth : overlay.ActualHeight;
            double travel = Math.Max(1, trackLength - thumbLength);
            Point point = e.GetPosition(overlay);
            double raw = (horizontal ? point.X : point.Y) - thumbLength / 2;
            double position = Math.Clamp(raw, 0, travel);
            double target = (position / travel) * scrollable;
            if (horizontal) sv.ScrollToHorizontalOffset(target);
            else sv.ScrollToVerticalOffset(target);
            UpdatePageScrollOverlay(listBox);
            e.Handled = true;
        }

        private static double ComputePageScrollThumbLength(double trackLength, double viewportRatio, bool horizontal)
        {
            if (trackLength <= 0) return 0;

            double min = horizontal ? PageScrollHorizontalMinThumb : PageScrollVerticalMinThumb;
            double max = horizontal ? PageScrollHorizontalMaxThumb : PageScrollVerticalMaxThumb;
            double maxAllowed = Math.Min(max, trackLength);
            if (maxAllowed <= min) return trackLength;

            double proportional = trackLength * viewportRatio;
            return Math.Clamp(Math.Max(proportional, min), min, maxAllowed);
        }

        private void PageScrollThumb_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border thumb) return;
            var overlay = FindVisualParentByName(thumb, "PageScrollOverlay");
            var host = FindVisualParentByName(thumb, "RowIndicatorHost");
            if (overlay == null || host == null || FindVisualChild<ListBox>(host) is not { } listBox) return;
            if (FindPageScrollViewer(listBox) is not { } sv) return;

            _pageScrollDragThumb = thumb;
            _pageScrollDragViewer = sv;
            _pageScrollDragHorizontal = IsHorizontalPageScroll(listBox);
            Point point = e.GetPosition(overlay);
            _pageScrollDragStartPosition = _pageScrollDragHorizontal ? point.X : point.Y;
            _pageScrollDragStartOffset = _pageScrollDragHorizontal ? sv.HorizontalOffset : sv.VerticalOffset;
            thumb.CaptureMouse();
            e.Handled = true;
        }

        private void PageScrollThumb_MouseMove(object sender, MouseEventArgs e)
        {
            if (!ReferenceEquals(sender, _pageScrollDragThumb) || _pageScrollDragViewer == null) return;
            if (sender is not Border thumb || e.LeftButton != MouseButtonState.Pressed) return;
            var overlay = FindVisualParentByName(thumb, "PageScrollOverlay");
            if (overlay == null) return;

            double minThumb = _pageScrollDragHorizontal ? PageScrollHorizontalMinThumb : PageScrollVerticalMinThumb;
            double thumbLength = Math.Max(_pageScrollDragHorizontal ? thumb.ActualWidth : thumb.ActualHeight, minThumb);
            double trackLength = _pageScrollDragHorizontal ? overlay.ActualWidth : overlay.ActualHeight;
            double travel = Math.Max(1, trackLength - thumbLength);
            Point point = e.GetPosition(overlay);
            double delta = (_pageScrollDragHorizontal ? point.X : point.Y) - _pageScrollDragStartPosition;
            double scrollable = _pageScrollDragHorizontal ? _pageScrollDragViewer.ScrollableWidth : _pageScrollDragViewer.ScrollableHeight;
            double target = Math.Clamp(_pageScrollDragStartOffset + (delta / travel) * scrollable, 0, scrollable);
            if (_pageScrollDragHorizontal) _pageScrollDragViewer.ScrollToHorizontalOffset(target);
            else _pageScrollDragViewer.ScrollToVerticalOffset(target);
            e.Handled = true;
        }

        private void PageScrollThumb_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndPageScrollThumbDrag(sender as Border);
            e.Handled = true;
        }

        private void PageScrollThumb_LostMouseCapture(object sender, MouseEventArgs e)
            => EndPageScrollThumbDrag(sender as Border);

        private void EndPageScrollThumbDrag(Border? thumb)
        {
            if (thumb != null && ReferenceEquals(thumb, _pageScrollDragThumb) && thumb.IsMouseCaptured)
                thumb.ReleaseMouseCapture();
            _pageScrollDragThumb = null;
            _pageScrollDragViewer = null;
        }

        private void QueueVisibleThumbnailScans()
        {
            foreach (var group in _groups)
            {
                if (FindPageListBoxFor(group) is { } listBox)
                    QueueVisibleThumbnailScan(listBox);
            }
        }

        private void QueueVisibleThumbnailScan(ListBox listBox, int delayMs = 0)
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(() => QueueVisibleThumbnailScan(listBox, delayMs), DispatcherPriority.Background);
                return;
            }

            if (!listBox.IsLoaded) return;
            if (!_thumbnailViewportScanQueued.Add(listBox)) return;

            _ = Dispatcher.InvokeAsync(async () =>
            {
                if (delayMs > 0)
                    await Task.Delay(delayMs).ConfigureAwait(true);

                _thumbnailViewportScanQueued.Remove(listBox);
                LoadVisibleThumbnails(listBox);
            }, DispatcherPriority.Background);
        }

        private void LoadVisibleThumbnails(ListBox listBox)
        {
            if (!listBox.IsLoaded || listBox.Items.Count == 0) return;
            if (FindPageScrollViewer(listBox) is not { } scrollViewer) return;
            if (scrollViewer.ViewportWidth <= 0 || scrollViewer.ViewportHeight <= 0) return;

            bool horizontal = !_splitViewMode && _focusedGroup == null && _layoutMode == GroupLayoutMode.Row;
            double lookAheadX = horizontal ? Math.Max(ThumbnailWidth * 1.25, 120) : 12;
            double lookAheadY = horizontal ? 12 : Math.Max(ThumbnailHeight * 1.25, 90);
            var viewport = new Rect(
                -lookAheadX,
                -lookAheadY,
                scrollViewer.ViewportWidth + lookAheadX * 2,
                scrollViewer.ViewportHeight + lookAheadY * 2);

            bool pendingLimitReached = false;
            int queuedThisScan = 0;
            const int maxQueuePerScan = 18;

            foreach (var item in FindVisualChildren<ListBoxItem>(listBox))
            {
                if (Volatile.Read(ref _foregroundThumbnailRequests) >= MaxForegroundThumbnailPending)
                {
                    pendingLimitReached = true;
                    break;
                }

                if (queuedThisScan >= maxQueuePerScan)
                {
                    pendingLimitReached = true;
                    break;
                }

                if (item.DataContext is not PageRow row || row.Thumbnail != null || row.ThumbnailLoadQueued) continue;
                if (!IsElementInViewport(item, scrollViewer, viewport)) continue;

                queuedThisScan++;
                _ = LoadThumbnailFor(row);
            }

            if (pendingLimitReached)
                QueueVisibleThumbnailScan(listBox, 180);
        }

        private void QueueNearbyThumbnailPrefetch(ListBox listBox, int direction, int delayMs = 140)
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(() => QueueNearbyThumbnailPrefetch(listBox, direction, delayMs), DispatcherPriority.Background);
                return;
            }

            if (!listBox.IsLoaded) return;

            _nearbyPrefetchDirections[listBox] = direction < 0 ? -1 : 1;
            if (!_nearbyPrefetchScanQueued.Add(listBox)) return;

            _ = Dispatcher.InvokeAsync(async () =>
            {
                if (delayMs > 0)
                    await Task.Delay(delayMs).ConfigureAwait(true);

                _nearbyPrefetchScanQueued.Remove(listBox);
                int scanDirection = _nearbyPrefetchDirections.TryGetValue(listBox, out var savedDirection)
                    ? savedDirection
                    : 1;
                _nearbyPrefetchDirections.Remove(listBox);

                var rows = GetNearbyRowsToPrefetch(listBox, scanDirection);
                if (rows.Count > 0)
                    _ = PrefetchRowsAsync(rows, nearViewport: true);
            }, DispatcherPriority.Background);
        }

        private List<PageRow> GetNearbyRowsToPrefetch(ListBox listBox, int direction)
        {
            var result = new List<PageRow>(NearbyPrefetchPageCount);
            if (!listBox.IsLoaded || listBox.Items.Count == 0) return result;
            if (FindPageScrollViewer(listBox) is not { } scrollViewer) return result;

            var viewport = new Rect(0, 0, scrollViewer.ViewportWidth, scrollViewer.ViewportHeight);
            int minVisible = int.MaxValue;
            int maxVisible = -1;

            foreach (var item in FindVisualChildren<ListBoxItem>(listBox))
            {
                if (item.DataContext is not PageRow row) continue;
                if (!IsElementInViewport(item, scrollViewer, viewport)) continue;

                int index = listBox.Items.IndexOf(row);
                if (index < 0) continue;

                minVisible = Math.Min(minVisible, index);
                maxVisible = Math.Max(maxVisible, index);
            }

            if (maxVisible < 0) return result;

            if (direction >= 0)
            {
                AddNearbyRows(listBox, maxVisible + 1, step: 1, result);
                AddNearbyRows(listBox, minVisible - 1, step: -1, result);
            }
            else
            {
                AddNearbyRows(listBox, minVisible - 1, step: -1, result);
                AddNearbyRows(listBox, maxVisible + 1, step: 1, result);
            }

            return result;
        }

        private static void AddNearbyRows(ListBox listBox, int startIndex, int step, List<PageRow> result)
        {
            for (int i = startIndex;
                 i >= 0 && i < listBox.Items.Count && result.Count < NearbyPrefetchPageCount;
                 i += step)
            {
                if (listBox.Items[i] is not PageRow row) continue;
                var key = (row.SourcePath, row.PageNumber);
                if (row.Thumbnail != null || IsThumbnailCachedOrLoading(key)) continue;
                result.Add(row);
            }
        }

        /// <summary>Ảnh render gốc LUÔN ở độ phân giải này — phải LỚN HƠN cỡ hiển thị to nhất (400px, "Rất lớn") + chừa dư cho màn hình DPI cao, nếu không ảnh bị phóng to từ nguồn nhỏ → mờ (đúng lỗi đã gặp). Đổi cỡ xem trước chỉ resize khung hiển thị, không render lại.</summary>
        internal const double RenderThumbnailWidthPx = 340;

        private void RemoveGroup_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not DocumentGroup group) return;
            _workspace.Execute(new RemoveDocumentCommand(_workspace, group));
            ReaderWindow.Instance?.NotifyGroupRemoved(group);
            ReleaseUnusedPdfDocuments();
        }

        private void RemoveSelectedPages_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not DocumentGroup group) return;
            var lb = FindPageListBoxFor(group);
            if (lb == null) return;

            var selected = lb.SelectedItems.Cast<PageRow>().ToList();
            if (selected.Count == 0) return;
            _workspace.Execute(new RemovePagesCommand(_workspace, group, selected));
            if (!_groups.Contains(group)) ReaderWindow.Instance?.NotifyGroupRemoved(group);
            else ReaderWindow.Instance?.NotifyPagesChanged(group);
            ReleaseUnusedPdfDocuments();
        }

        private void ReleaseUnusedPdfDocuments()
        {
            var active = new HashSet<string>(_groups.SelectMany(g => g.Pages.Select(p => p.SourcePath)), StringComparer.OrdinalIgnoreCase);
            Interlocked.Increment(ref _thumbnailGeneration);
            lock (_thumbnailLock) _thumbnailCache.RemoveWhere(key => !active.Contains(key.Path));
            ReaderWindow.ReleaseUnusedSources(active);
            PdfThumbnailService.ReleaseUnusedDocuments(active);
        }

        /// <summary>Lưu riêng các trang đang có trong CHÍNH window này ra 1 file PDF — không còn khái niệm "1 output ghép chung", mỗi window tự quyết định lưu ở đâu. KHÔNG đóng window sau khi lưu — user có thể chỉnh tiếp rồi lưu lại (VD lưu đè, hoặc lưu ra tên khác).</summary>
        private CancellationTokenSource? _savingCts;

        private async void SaveGroup_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not DocumentGroup group) return;
            if (group.Pages.Count == 0) return;

            string? initialDir = Path.GetDirectoryName(group.Pages[0].SourcePath);
            using var dlg = new System.Windows.Forms.SaveFileDialog
            {
                Title = "Lưu file PDF",
                Filter = "PDF (*.pdf)|*.pdf",
                DefaultExt = "pdf",
                FileName = SuggestFileName(group),
                InitialDirectory = !string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir) ? initialDir : ""
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            string outputPath = dlg.FileName;

            var pageList = group.Pages.Select(p => (p.SourcePath, p.PageNumber)).ToList();

            bool ok;
            string err = "";
            _savingCts = new CancellationTokenSource();
            var token = _savingCts.Token;
            // Progress<T> chụp SynchronizationContext của UI thread NGAY LÚC TẠO (ở đây) — Report()
            // gọi từ Task.Run bên dưới tự marshal callback này về UI thread, không cần Dispatcher thủ công.
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                SavingProgressPercent = p.Total > 0 ? p.Done * 100.0 / p.Total : 0;
                SavingStatusText = $"Đang ghép trang {p.Done}/{p.Total}…";
            });
            ShowSavingOverlay(pageList.Count);

            try
            {
                // Task.Run — iText xử lý hàng trăm trang có thể mất vài giây tới hơn chục giây,
                // chạy trên UI thread trước đây làm cả app đơ (đúng bug đã bàn). Gộp layer trùng tên
                // (CHUKY_*) LUÔN BẬT — đã xác nhận đúng qua test thật, không còn cấu hình qua UI.
                ok = await Task.Run(() => XTPdfMerger.TryMergePages(pageList, outputPath, out err, null,
                    mergeLayersByName: true, mergeLayersNamePrefix: "CHUKY_", collapseOtherLayersTo: "0",
                    progress: progress, cancellationToken: token));
            }
            catch (OperationCanceledException)
            {
                ok = false;
                err = "";
            }
            finally
            {
                HideSavingOverlay();
                _savingCts.Dispose();
                _savingCts = null;
            }

            if (!ok)
            {
                if (!string.IsNullOrEmpty(err))
                    MessageBox.Show(this, "Lưu thất bại:\n" + err, "Lưu PDF", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (CleanupAfterMergeCheck.IsChecked == true)
                // Theo SourcePath của TỪNG TRANG (không phải group.SourcePath) —
                // 1 trang có thể đã bị kéo sang window của file khác trước khi
                // lưu, group chứa nó lúc này không còn đại diện đúng file gốc.
                CleanupSourceFiles(group.Pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase));

            var openResult = MessageBox.Show(this,
                "Đã lưu xong:\n" + outputPath + "\n\nMở file ngay?",
                "Lưu PDF", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (openResult == MessageBoxResult.Yes)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(outputPath) { UseShellExecute = true }); }
                catch { }
            }
        }

        private void ShowSavingOverlay(int totalPages)
        {
            SavingProgressPercent = 0;
            SavingStatusText = $"Đang ghép trang 0/{totalPages}…";
            SavingOverlay.Visibility = Visibility.Visible;
        }

        private void HideSavingOverlay()
        {
            SavingOverlay.Visibility = Visibility.Collapsed;
        }

        private void SavingCancelButton_Click(object sender, RoutedEventArgs e)
        {
            SavingStatusText = "Đang huỷ…";
            _savingCts?.Cancel();
        }

        private void GroupsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GroupsList.SelectedItems.Count == 0) return;
            _statusSelectedGroup = GroupsList.SelectedItems[GroupsList.SelectedItems.Count - 1] as DocumentGroup;
            _statusSelectedPage = null;
            _statusSelectedPageCount = 0;
            UpdateStatusBar();
        }

        private void GroupsList_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            e.Handled = true;
        }

        private void PageListBox_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            // Selection thumbnail không được phép thay đổi offset ngang/dọc của window.
            // User tự cuộn; drag/drop và selection chỉ thay đổi dữ liệu/trạng thái chọn.
            if (sender is ListBox) e.Handled = true;
        }

        private void MergeSelectedGroups_Click(object sender, RoutedEventArgs e)
        {
            var selectedSet = GroupsList.SelectedItems.Cast<DocumentGroup>().ToHashSet();
            var selectedInOrder = _groups.Where(selectedSet.Contains).ToList();
            if (selectedInOrder.Count < 2)
            {
                MessageBox.Show(this, "Hãy chọn ít nhất 2 file PDF để ghép.", "Ghép đã chọn",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            MergeGroupsInWorkspace(selectedInOrder, renameTarget: null);
        }

        private void MergeAllGroups_Click(object sender, RoutedEventArgs e)
        {
            if (_groups.Count < 2)
            {
                MessageBox.Show(this, "Cần ít nhất 2 file PDF để ghép.", "Ghép tất cả",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            MergeGroupsInWorkspace(_groups.ToList(), renameTarget: "merge.pdf");
        }

        private void MergeGroupsInWorkspace(IReadOnlyList<DocumentGroup> groups, string? renameTarget)
        {
            if (groups.Count < 2) return;
            DocumentGroup target = groups[0];
            _workspace.Execute(new MergeDocumentsCommand(_workspace, groups, renameTarget));
            GroupsList.SelectedItems.Clear();
            GroupsList.SelectedItem = target;
            ReaderWindow.Instance?.NotifyPagesChanged(target);
            ReleaseUnusedPdfDocuments();
            UpdateStatusBar();
        }

        private static string SuggestFileName(DocumentGroup group)
            => FileNamingMath.SuggestFileName(group.Pages.Select(p => p.SourcePath));

        /// <summary>Tìm ListBox (trong ItemContainerGenerator của GroupsList) đang hiện trang của group này — dùng khi cần thao tác selection của đúng window đó từ code (VD RemoveSelectedPages_Click).</summary>
        private ListBox? FindPageListBoxFor(DocumentGroup group)
        {
            if (ReferenceEquals(group, _focusedGroup))
                return FindVisualChildren<ListBox>(SplitLeftContent)
                    .FirstOrDefault(lb => ReferenceEquals(lb.Tag, group));

            if (_splitViewMode)
            {
                ContentControl? host = ReferenceEquals(group, _splitLeftGroup) ? SplitLeftContent
                    : ReferenceEquals(group, _splitRightGroup) ? SplitRightContent : null;
                if (host != null) return FindVisualChildren<ListBox>(host).FirstOrDefault(lb => ReferenceEquals(lb.Tag, group));
            }
            var container = GroupsList.ItemContainerGenerator.ContainerFromItem(group) as FrameworkElement;
            return container == null ? null : FindVisualChild<ListBox>(container);
        }

        private static bool PassedDragThreshold(Point start, Point current)
            => Math.Abs(current.X - start.X) > SystemParameters.MinimumHorizontalDragDistance ||
               Math.Abs(current.Y - start.Y) > SystemParameters.MinimumVerticalDragDistance;


        // ── Kéo TRANG (trong 1 window, hoặc SANG window khác) ────────────────

        /// <summary>WPF mặc định: bấm chuột (không giữ Ctrl) lên 1 item đang nằm trong vùng đã chọn NHIỀU sẽ tự thu vùng chọn về còn đúng item đó NGAY LÚC MouseDown — trước cả khi kịp bắt đầu kéo. Nên phải hoãn việc thu vùng chọn sang MouseUp (chỉ áp dụng nếu KHÔNG có kéo thả xảy ra), để kéo-thả multiselect giữ nguyên được các item đã chọn.</summary>
        private void PageListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsFromScrollChrome(e.OriginalSource as DependencyObject))
            {
                _pageDragSource = null;
                _pageDeferSelection = false;
                return;
            }

            if (sender is not ListBox lb || FindListBoxItem(e.OriginalSource as DependencyObject) is not { } item)
            {
                _pageDragSource = null;
                _pageDeferSelection = false;
                return;
            }

            if (lb.Tag is DocumentGroup ownerGroup)
                SelectGroupWithoutScrolling(ownerGroup);

            _pageDragStart = e.GetPosition(null);
            _pageDragSource = lb;
            _pageDeferSelection = false;
            _pageDeferCtrlToggle = false;

            if (item.DataContext is PageRow row
                && lb.SelectedItems.Count > 1 && lb.SelectedItems.Contains(row)
                && (Keyboard.Modifiers == ModifierKeys.None ||
                    (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control))
            {
                _pageDeferSelection = true;
                _pageDeferCtrlToggle = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                e.Handled = true;
            }
        }

        private void PageListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBox listBox) return;
            if (FindListBoxItem(e.OriginalSource as DependencyObject) is not { } item) return;
            if (!item.IsSelected)
            {
                listBox.SelectedItems.Clear();
                item.IsSelected = true;
            }
        }

        private static ListBox? GetContextPageListBox(object sender)
        {
            if (sender is not MenuItem item) return null;
            return (ItemsControl.ItemsControlFromItemContainer(item) as ContextMenu)?.PlacementTarget as ListBox;
        }

        private void CapturePageClipboard(ListBox listBox, bool cut)
        {
            if (listBox.Tag is not DocumentGroup group) return;
            _pageClipboard = group.Pages.Where(page => listBox.SelectedItems.Contains(page)).ToList();
            _pageClipboardSource = group;
            _pageClipboardIsCut = cut;
        }

        private void PageContextCut_Click(object sender, RoutedEventArgs e)
        {
            if (GetContextPageListBox(sender) is { } listBox) CapturePageClipboard(listBox, cut: true);
        }

        private void PageContextCopy_Click(object sender, RoutedEventArgs e)
        {
            if (GetContextPageListBox(sender) is { } listBox) CapturePageClipboard(listBox, cut: false);
        }

        private void PastePageClipboard(ListBox targetList, int insertIndex)
        {
            if (_pageClipboard.Count == 0 || _pageClipboardSource == null || targetList.Tag is not DocumentGroup target) return;
            var available = _pageClipboard.Where(_pageClipboardSource.Pages.Contains).ToList();
            if (available.Count == 0) return;
            _workspace.Execute(new MovePagesCommand(
                _workspace, _pageClipboardSource, target, available, insertIndex, copy: !_pageClipboardIsCut));
            if (_pageClipboardIsCut)
            {
                _pageClipboard.Clear();
                _pageClipboardSource = null;
                _pageClipboardIsCut = false;
            }
            UpdateStatusBar();
        }

        private void PageContextPasteBefore_Click(object sender, RoutedEventArgs e)
        {
            if (GetContextPageListBox(sender) is not { } listBox) return;
            int index = listBox.SelectedItem == null ? listBox.Items.Count : listBox.Items.IndexOf(listBox.SelectedItem);
            PastePageClipboard(listBox, Math.Max(0, index));
        }

        private void PageContextPasteEnd_Click(object sender, RoutedEventArgs e)
        {
            if (GetContextPageListBox(sender) is { } listBox) PastePageClipboard(listBox, listBox.Items.Count);
        }

        private void PageContextRestoreSource_Click(object sender, RoutedEventArgs e)
        {
            if (GetContextPageListBox(sender) is not { } listBox || listBox.Tag is not DocumentGroup current) return;
            var pages = current.Pages.Where(page => listBox.SelectedItems.Contains(page) && page.IsImported).ToList();
            foreach (var sourceSet in pages.GroupBy(page => page.SourcePath, StringComparer.OrdinalIgnoreCase))
            {
                var sourceWindow = _groups.FirstOrDefault(group =>
                    string.Equals(group.SourcePath, sourceSet.Key, StringComparison.OrdinalIgnoreCase));
                if (sourceWindow == null)
                {
                    MessageBox.Show(this, $"Window nguồn '{Path.GetFileName(sourceSet.Key)}' đang đóng. Hãy Undo hoặc mở lại file nguồn trước khi khôi phục.",
                        "Khôi phục trang", MessageBoxButton.OK, MessageBoxImage.Information);
                    continue;
                }
                int insertAt = sourceWindow.Pages.TakeWhile(page => page.PageNumber < sourceSet.Min(p => p.PageNumber)).Count();
                _workspace.Execute(new MovePagesCommand(_workspace, current, sourceWindow,
                    sourceSet.OrderBy(page => page.PageNumber), insertAt, copy: false));
            }
        }

        private void PageContextDelete_Click(object sender, RoutedEventArgs e)
        {
            if (GetContextPageListBox(sender) is not { } listBox || listBox.Tag is not DocumentGroup group) return;
            var pages = group.Pages.Where(page => listBox.SelectedItems.Contains(page)).ToList();
            if (pages.Count > 0) _workspace.Execute(new RemovePagesCommand(_workspace, group, pages));
        }

        private void DocumentContextSelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item ||
                ItemsControl.ItemsControlFromItemContainer(item) is not ContextMenu menu ||
                menu.PlacementTarget is not FrameworkElement placementTarget ||
                placementTarget.DataContext is not DocumentGroup group) return;
            if (FindPageListBoxFor(group) is not { } listBox) return;
            listBox.SelectAll();
        }

        private void PageListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_pageDeferSelection)
            {
                _pageDragSource = null;
                return;
            }

            _pageDeferSelection = false;
            if (_pageDragging)
            {
                _pageDragSource = null;
                return;
            }

            if (sender is ListBox lb && FindListBoxItem(e.OriginalSource as DependencyObject) is { } item)
            {
                if (_pageDeferCtrlToggle) lb.SelectedItems.Remove(item.DataContext);
                else lb.SelectedItem = item.DataContext;
            }
            _pageDeferCtrlToggle = false;
            _pageDragSource = null;
        }

        private void PageListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox lb || lb.Tag is not DocumentGroup group) return;
            SetStatusSelection(group, lb.SelectedItem as PageRow, lb.SelectedItems.Count);
        }

        private void PageListBox_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (!PassedDragThreshold(_pageDragStart, e.GetPosition(null))) return;
            if (_pageDragSource is not { } lb || lb.Tag is not DocumentGroup group) return;
            if (lb.SelectedItems.Count == 0) return;

            // Giữ ĐÚNG thứ tự hiện tại trong group.Pages (không phải thứ tự chọn).
            var selected = group.Pages.Where(p => lb.SelectedItems.Contains(p)).ToList();
            if (selected.Count == 0) return;

            var payload = new PageDragPayload { SourceGroup = group, Pages = selected };
            _pageDragging = true;
            // Cho phép CẢ Move lẫn Copy — trước đây chỉ khai báo Move, nên
            // khi DragOver cố set e.Effects=Copy cho trường hợp kéo SANG
            // window khác (xem PageListBox_DragOver), WPF chặn thẳng vì Copy
            // không nằm trong allowed effects: con trỏ báo "không thả được"
            // và Drop không bao giờ chạy — đúng bug "không kéo sang file khác được nữa".
            var firstThumbnail = selected.Select(page => page.Thumbnail).FirstOrDefault(image => image != null);
            var ghost = BeginDragGhost(firstThumbnail, selected.Count == 1 ? "1 trang" : $"{selected.Count} trang");
            GiveFeedbackEventHandler feedback = (_, args) =>
            {
                ghost?.Update(GetDragPointerPosition(),
                    (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control);
                args.UseDefaultCursors = true;
            };
            lb.GiveFeedback += feedback;
            try { DragDrop.DoDragDrop(lb, new DataObject("XTPdfMergePages", payload), DragDropEffects.Move | DragDropEffects.Copy); }
            finally
            {
                lb.GiveFeedback -= feedback;
                EndDragGhost(ghost);
                _pageDragging = false;
                _pageDeferSelection = false;
                _pageDragSource = null;
            }
        }

        private Controls.DragGhostAdorner? BeginDragGhost(ImageSource? image, string label)
        {
            var layer = AdornerLayer.GetAdornerLayer(GroupsOverlayGrid);
            if (layer == null) return null;
            var ghost = new Controls.DragGhostAdorner(GroupsOverlayGrid, image, label);
            layer.Add(ghost);
            ghost.Update(GetDragPointerPosition(), copy: false);
            return ghost;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X; public int Y; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out NativePoint point);

        private Point GetDragPointerPosition()
        {
            try
            {
                if (GetCursorPos(out var screenPoint))
                    return GroupsOverlayGrid.PointFromScreen(new Point(screenPoint.X, screenPoint.Y));
            }
            catch (InvalidOperationException) { }
            return Mouse.GetPosition(GroupsOverlayGrid);
        }

        private static void EndDragGhost(Controls.DragGhostAdorner? ghost)
        {
            if (ghost == null) return;
            AdornerLayer.GetAdornerLayer(ghost.AdornedElement)?.Remove(ghost);
        }

        /// <summary>
        /// Chế độ Hàng: lăn chuột THƯỜNG → cuộn DỌC (bubble lên GroupsScrollViewer, không set
        /// Handled ở đây); Shift+lăn chuột → cuộn NGANG trong CHÍNH window đang hover (chủ động,
        /// khi cần xem trang xa hơn — window chỉ hiện 1 hàng thumbnail, xem DocumentGroupTemplateRow).
        /// Chế độ Cột: giữ nguyên — lăn chuột cuộn DỌC trong CHÍNH cột đang hover, đúng hướng
        /// trục chính của cột.
        /// </summary>
        private void PageListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ListBox lb) return;

            if (_splitViewMode || _focusedGroup != null)
            {
                // Để ScrollViewer nội bộ + IScrollInfo của VirtualizingWrapPanel tự xử lý.
                // Chặn ở PreviewMouseWheel làm panel không nhận được wheel và extent có thể
                // chưa kịp cập nhật tại thời điểm ta tự gọi ScrollToVerticalOffset.
                e.Handled = false;
                return;
            }

            if (_layoutMode == GroupLayoutMode.Row)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                {
                    if (FindPageScrollViewer(lb) is { } rowScroll)
                        ScrollThumbnailsBy(rowScroll, e.Delta, 0);
                }
                else
                {
                    ScrollThumbnailsBy(GroupsScrollViewer, 0, e.Delta);
                }
            }
            else
            {
                if (FindPageScrollViewer(lb) is { } columnScroll)
                    ScrollThumbnailsBy(columnScroll, 0, e.Delta);
            }

            e.Handled = true;
        }

        private void GroupsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;
            if (IsInsidePageListBox(e.OriginalSource as DependencyObject)) return;

            if (_layoutMode == GroupLayoutMode.Row)
                ScrollThumbnailsBy(sv, 0, e.Delta);
            else
                ScrollThumbnailsBy(sv, e.Delta, 0);

            e.Handled = true;
        }

        private static bool IsInsidePageListBox(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is ListBox lb && lb.Tag is DocumentGroup) return true;
                source = System.Windows.Media.VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private static void ScrollThumbnailsBy(ScrollViewer sv, double deltaH, double deltaV)
        {
            // Offset pixel trực tiếp tránh timer easing chạy lệch pha với recycling/
            // lazy thumbnail, nguyên nhân tạo cảm giác giật và mép ảnh răng cưa khi cuộn.
            const double wheelScale = 0.85;
            if (deltaH != 0)
                sv.ScrollToHorizontalOffset(Math.Clamp(sv.HorizontalOffset - deltaH * wheelScale, 0, sv.ScrollableWidth));
            if (deltaV != 0)
                sv.ScrollToVerticalOffset(Math.Clamp(sv.VerticalOffset - deltaV * wheelScale, 0, sv.ScrollableHeight));
        }

        /// <summary>
        /// ScrollViewer.ScrollToXOffset là NHẢY thẳng tới vị trí — gọi trực
        /// tiếp mỗi lần lăn chuột (như trước) cho cảm giác "giật cục" vì mỗi
        /// nấc là 1 bước nhảy rời rạc, không có chuyển động mượt giữa 2 vị
        /// trí. Thay bằng easing thủ công: dồn các nấc lăn liên tiếp vào 1
        /// TARGET offset, rồi DispatcherTimer (Render priority, ~60fps) tiến
        /// dần offset THẬT về TARGET theo tỉ lệ mỗi khung hình — tạo cảm giác
        /// "lướt" mượt thay vì nhảy khựng, và tự dừng khi đã tới đích.
        /// </summary>
        private readonly Dictionary<ScrollViewer, (double TargetH, double TargetV, DispatcherTimer Timer)> _smoothScroll = new();

        private void SmoothScrollBy(ScrollViewer sv, double deltaH, double deltaV)
        {
            if (!_smoothScroll.TryGetValue(sv, out var state))
            {
                var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
                state = (sv.HorizontalOffset, sv.VerticalOffset, timer);
                timer.Tick += (_, _) => SmoothScrollTick(sv);
                _smoothScroll[sv] = state;
            }
            else if (!state.Timer.IsEnabled)
            {
                // Timer đang KHÔNG chạy (animation trước đã xong từ lâu) —
                // đồng bộ lại Target = vị trí THẬT hiện tại trước khi cộng
                // dồn delta mới. Trước đây luôn dùng lại Target cũ trong
                // dictionary dù vị trí thật có thể đã bị đổi bởi nguồn khác
                // KHÔNG đi qua hàm này (VD WPF tự cuộn item vào tầm nhìn lúc
                // chọn trang, hoặc AutoScrollIfNearEdge lúc kéo-thả gần mép) —
                // lăn chuột tiếp theo animate dựa trên Target cũ đã lệch,
                // tạo cảm giác "giật" nhảy về vị trí trước đó (đúng bug
                // "select page là tự nhảy về đầu").
                state = (sv.HorizontalOffset, sv.VerticalOffset, state.Timer);
                _smoothScroll[sv] = state;
            }

            const double wheelScale = 1.12;
            double targetH = Math.Clamp(state.TargetH - deltaH * wheelScale, 0, sv.ScrollableWidth);
            double targetV = Math.Clamp(state.TargetV - deltaV * wheelScale, 0, sv.ScrollableHeight);
            _smoothScroll[sv] = (targetH, targetV, state.Timer);
            if (!state.Timer.IsEnabled) state.Timer.Start();
        }

        private void SmoothScrollTick(ScrollViewer sv)
        {
            if (!_smoothScroll.TryGetValue(sv, out var state)) return;

            double h = sv.HorizontalOffset + (state.TargetH - sv.HorizontalOffset) * 0.28;
            double v = sv.VerticalOffset + (state.TargetV - sv.VerticalOffset) * 0.28;

            bool closeEnough = Math.Abs(state.TargetH - h) < 0.5 && Math.Abs(state.TargetV - v) < 0.5;
            if (closeEnough) { h = state.TargetH; v = state.TargetV; state.Timer.Stop(); }

            sv.ScrollToHorizontalOffset(h);
            sv.ScrollToVerticalOffset(v);
        }

        private void StopSmoothScrollAnimations()
        {
            foreach (var state in _smoothScroll.Values)
                state.Timer.Stop();
            _smoothScroll.Clear();
        }

        /// <summary>KHÔNG nhận diện "XTPdfMergePages" thì bỏ qua hẳn (không set e.Handled) để sự kiện bubbling tiếp lên GroupsArea_DragOver — cho phép kéo file PDF mới (từ Explorer), hoặc kéo cả window khác, vẫn hoạt động ngay cả khi đang ở trên 1 window đã có sẵn.</summary>
        private void PageListBox_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("XTPdfMergePages")) return;
            if (sender is not ListBox lb) return;

            // Cùng window (sắp xếp lại) → Move (rút ra rồi chèn lại đúng chỗ).
            // KHÁC window → Copy (giữ nguyên bên nguồn, chỉ thêm bản mới vào
            // đích — xem PageListBox_Drop) — con trỏ đổi icon "+" để user biết
            // trước sẽ COPY chứ không mất trang khỏi window gốc.
            var payload = (PageDragPayload)e.Data.GetData("XTPdfMergePages");
            var targetGroup = lb.Tag as DocumentGroup;
            bool sameGroup = targetGroup != null && ReferenceEquals(payload.SourceGroup, targetGroup);
            bool copyRequested = (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;
            int insertAt = FindInsertIndexInRow(lb, e.GetPosition(lb));
            bool noOpMove = !copyRequested && sameGroup && targetGroup != null && IsSameGroupDropNoOp(targetGroup, payload.Pages, insertAt);
            e.Effects = noOpMove ? DragDropEffects.None : copyRequested ? DragDropEffects.Copy : DragDropEffects.Move;
            ShowDropIndicator(noOpMove ? null : (lb, e.GetPosition(lb)));

            if (FindPageScrollViewer(lb) is { } sv)
                AutoScrollIfNearEdge(sv, e.GetPosition(sv), horizontal: IsHorizontalPageScroll(lb));

            e.Handled = true;
        }

        private void PageListBox_DragLeave(object sender, DragEventArgs e)
        {
            ShowDropIndicator(null);
        }

        private void PageListBox_Drop(object sender, DragEventArgs e)
        {
            ShowDropIndicator(null);

            if (sender is not ListBox targetListBox || targetListBox.Tag is not DocumentGroup targetGroup) return;
            if (!e.Data.GetDataPresent("XTPdfMergePages")) return;
            var payload = (PageDragPayload)e.Data.GetData("XTPdfMergePages");
            if (payload.Pages.Count == 0) return;

            int insertAt = FindInsertIndexInRow(targetListBox, e.GetPosition(targetListBox));
            bool sameGroup = ReferenceEquals(payload.SourceGroup, targetGroup);
            bool copyRequested = (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;
            if (!copyRequested && sameGroup && IsSameGroupDropNoOp(targetGroup, payload.Pages, insertAt))
            {
                e.Handled = true;
                return;
            }

            _workspace.Execute(new MovePagesCommand(
                _workspace, payload.SourceGroup, targetGroup, payload.Pages, insertAt, copyRequested));

            e.Handled = true;
        }

        /// <summary>
        /// Vị trí sẽ CHÈN VÀO (0..Count) trong 1 WINDOW (StackPanel 1
        /// dòng/cột) ứng với toạ độ con trỏ — so theo X ở chế độ Hàng, theo Y
        /// ở chế độ Cột (xem _layoutMode).
        /// </summary>
        private int FindInsertIndexInRow(ListBox rowListBox, Point pointInRow)
        {
            // Duyệt CONTAINER ĐÃ HIỆN THỰC HOÁ qua visual tree (FindVisualChildren, giống cách
            // ReaderContinuousList_ScrollChanged đang làm) thay vì lặp for 0..Items.Count rồi tra
            // ContainerFromIndex(i) — DragOver bắn liên tục theo TỪNG chuyển động chuột trong lúc
            // kéo, với window vài trăm trang, lặp O(tổng số trang) mỗi lần là nguồn gây chậm/khựng
            // khi kéo-thả đã xác nhận (chỉ ~10-40 trang thực sự hiện thực hoá tại 1 thời điểm nhờ
            // virtualization, không cần chạm tới toàn bộ Items.Count).
            var realIndices = new List<int>();
            var bounds = new List<Rect>();
            foreach (var item in FindVisualChildren<ListBoxItem>(rowListBox))
            {
                int i = rowListBox.ItemContainerGenerator.IndexFromContainer(item);
                if (i < 0) continue;
                Point topLeft = item.TranslatePoint(new Point(0, 0), rowListBox);
                realIndices.Add(i);
                bounds.Add(new Rect(topLeft, new Size(item.ActualWidth, item.ActualHeight)));
            }

            // FindVisualChildren duyệt theo thứ tự CÂY VISUAL — với VirtualizationMode=Recycling thứ
            // tự đó không đảm bảo tăng dần theo index trang; DropPositionMath.FindInsertIndex(Wrapped)
            // giả định bounds/realIndices đã sắp đúng thứ tự trang nên phải sort lại trước khi dùng.
            if (realIndices.Count > 1)
            {
                var order = Enumerable.Range(0, realIndices.Count).OrderBy(k => realIndices[k]).ToArray();
                realIndices = order.Select(k => realIndices[k]).ToList();
                bounds = order.Select(k => bounds[k]).ToList();
            }

            // Chỉ Chia đôi/Focus dùng panel WRAP (nhiều thumbnail/hàng) nên cần thuật toán 2D (so
            // hàng trước, cột sau). Hàng vẫn là 1 dải ngang duy nhất (so X), Cột là 1 dải dọc duy
            // nhất (so Y) — cả 2 dùng thuật toán đơn giản 1 trục.
            bool wrapped = IsWrappedPageList(rowListBox);
            int position = wrapped
                ? DropPositionMath.FindInsertIndexWrapped(bounds, pointInRow)
                : DropPositionMath.FindInsertIndex(bounds, pointInRow, horizontal: _layoutMode == GroupLayoutMode.Row);

            if (position < realIndices.Count) return realIndices[position];
            return realIndices.Count > 0 ? realIndices[^1] + 1 : rowListBox.Items.Count;
        }

        private static bool IsSameGroupDropNoOp(DocumentGroup group, IReadOnlyCollection<PageRow> draggedPages, int insertAt)
            => DropPositionMath.IsSameGroupPageDropNoOp(group.Pages.ToList(), draggedPages, insertAt);

        private bool IsWrappedPageList(ListBox listBox)
            => _splitViewMode || _focusedGroup != null ||
               FindVisualChildren<VirtualizingWrapPanel>(listBox).Any();

        // ── Ctrl+↑/↓/Home/End: di chuyển trang đang chọn mà KHÔNG cần kéo chuột — dùng CHUNG
        // MovePagesCommand với kéo-thả (giữ undo/redo, cùng logic no-op) nên hành vi nhất quán,
        // chỉ khác nguồn gọi. Bàn phím tiện hơn hẳn khi cần đẩy trang qua khoảng cách xa trong
        // window nhiều trang (không phải kéo tay + auto-scroll mép).

        /// <summary>Tìm ListBox trang (Tag=DocumentGroup) đang chứa phần tử có keyboard focus — để
        /// biết phím Ctrl+↑/↓/Home/End vừa bấm áp dụng cho WINDOW nào.</summary>
        private ListBox? FindFocusedPageListBox()
            => FindSelfOrVisualParent<ListBox>(Keyboard.FocusedElement as DependencyObject) is { } lb && lb.Tag is DocumentGroup
                ? lb
                : null;

        private static void RestoreSelectionAfterMove(ListBox lb, List<PageRow> pages)
        {
            lb.SelectedItems.Clear();
            foreach (var p in pages) lb.SelectedItems.Add(p);
        }

        /// <summary>delta=-1 đẩy lên 1 bậc, delta=+1 đẩy xuống 1 bậc — giữ nguyên thứ tự tương đối
        /// giữa các trang đang chọn (kể cả khi chọn rời rạc, không liền kề).</summary>
        private void MoveSelectedPagesUpOrDown(int delta)
        {
            if (FindFocusedPageListBox() is not { } lb || lb.Tag is not DocumentGroup group) return;
            if (lb.SelectedItems.Count == 0) return;

            var selected = group.Pages.Where(p => lb.SelectedItems.Contains(p)).ToList();
            if (selected.Count == 0) return;

            var indices = selected.Select(p => group.Pages.IndexOf(p)).ToList();
            int insertAt = delta < 0 ? indices.Min() - 1 : indices.Max() + 2;
            if (insertAt < 0 || insertAt > group.Pages.Count) return; // đã ở đầu/cuối, không còn chỗ để đẩy tiếp
            if (IsSameGroupDropNoOp(group, selected, insertAt)) return;

            _workspace.Execute(new MovePagesCommand(_workspace, group, group, selected, insertAt, copy: false));
            RestoreSelectionAfterMove(lb, selected);
            UpdateStatusBar();
        }

        private void MoveSelectedPagesToEdge(bool toStart)
        {
            if (FindFocusedPageListBox() is not { } lb || lb.Tag is not DocumentGroup group) return;
            if (lb.SelectedItems.Count == 0) return;

            var selected = group.Pages.Where(p => lb.SelectedItems.Contains(p)).ToList();
            if (selected.Count == 0) return;

            int insertAt = toStart ? 0 : group.Pages.Count;
            if (IsSameGroupDropNoOp(group, selected, insertAt)) return;

            _workspace.Execute(new MovePagesCommand(_workspace, group, group, selected, insertAt, copy: false));
            RestoreSelectionAfterMove(lb, selected);
            UpdateStatusBar();
        }

        private static ListBoxItem? FindNearestRealizedItem(ListBox listBox, int insertIndex, out bool afterTarget)
        {
            afterTarget = false;

            for (int i = Math.Min(insertIndex, listBox.Items.Count - 1); i >= 0; i--)
            {
                if (listBox.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item)
                {
                    afterTarget = true;
                    return item;
                }
            }

            for (int i = Math.Max(insertIndex, 0); i < listBox.Items.Count; i++)
            {
                if (listBox.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item)
                    return item;
            }

            return null;
        }

        /// <summary>Vạch đánh dấu vị trí chèn TRANG — nằm NGAY BÊN TRONG window đang kéo qua (RowIndicatorHost/RowDropIndicator của CHÍNH window đó, tìm qua tên phần tử bằng VisualTreeHelper chứ không qua NameScope/FindName) — không còn overlay chung toàn cột nên không thể hiện lấn ra window khác.</summary>
        private void ShowDropIndicator((ListBox Row, Point PointInRow)? target)
        {
            if (_activeRowIndicator != null)
            {
                _activeRowIndicator.Visibility = Visibility.Collapsed;
                _activeRowIndicator = null;
            }
            if (target == null) return;
            var (row, pointInRow) = target.Value;

            if (FindVisualParentByName(row, "RowIndicatorHost") is not System.Windows.Controls.Grid host) return;
            if (FindVisualChildByName<Border>(host, "RowDropIndicator") is not Border indicator) return;

            bool wrapped = IsWrappedPageList(row);
            bool horizontal = wrapped || _layoutMode == GroupLayoutMode.Row;

            if (row.Items.Count == 0)
            {
                Point rowTopLeft = row.TranslatePoint(new Point(0, 0), host);
                if (horizontal) indicator.Height = ThumbnailHeight + 10; else indicator.Width = ThumbnailWidth + 10;
                SetIndicatorGeometry(indicator, rowTopLeft.X + 4, rowTopLeft.Y, horizontal);
                _activeRowIndicator = indicator;
                return;
            }

            int index = FindInsertIndexInRow(row, pointInRow);
            int targetIndex = Math.Clamp(index, 0, row.Items.Count - 1);
            bool afterTarget = index > targetIndex;
            bool usedFallback = false;

            ListBoxItem? item = row.ItemContainerGenerator.ContainerFromIndex(targetIndex) as ListBoxItem;
            if (item == null)
            {
                usedFallback = true;
                item = FindNearestRealizedItem(row, index, out afterTarget);
                if (item == null)
                {
                    LogDropIndicatorSkip($"no item found: wrapped={wrapped} itemsCount={row.Items.Count} index={index} targetIndex={targetIndex}");
                    return;
                }
            }

            // Foxit luôn hiện vạch chèn NGAY GIỮA khe hở 2 trang liền kề — rà vào mép trang bên
            // này hay mép trang bên kia đều ra cùng 1 vị trí. Trước đây vạch áp sát ngay mép trang
            // ĐÍCH nên lệch hẳn về 1 bên, và rà vào mép trang TRƯỚC (không phải đích) thường không
            // thấy gì vì vạch nằm mãi ở phía bên kia. Lấy trung điểm giữa mép trang đích và mép
            // trang LÂN CẬN (nếu có, cùng hàng/cột) để đối xứng đúng như Foxit; không có lân cận
            // (đầu/cuối hàng) thì giữ nguyên mép trang đích như cũ.
            int neighborIndex = afterTarget ? targetIndex + 1 : targetIndex - 1;
            ListBoxItem? neighbor = neighborIndex >= 0 && neighborIndex < row.Items.Count
                ? row.ItemContainerGenerator.ContainerFromIndex(neighborIndex) as ListBoxItem
                : null;

            Point topLeft = item.TranslatePoint(new Point(0, 0), host);
            double finalX, finalY;
            if (horizontal)
            {
                double edgeX = afterTarget ? topLeft.X + item.ActualWidth : topLeft.X;
                ListBoxItem anchorItem = item;
                Point anchorTopLeft = topLeft;

                if (neighbor != null)
                {
                    Point neighborTopLeft = neighbor.TranslatePoint(new Point(0, 0), host);
                    bool sameRow = !wrapped || Math.Abs(neighborTopLeft.Y - topLeft.Y) < item.ActualHeight * 0.5;
                    if (sameRow)
                    {
                        double neighborEdgeX = afterTarget ? neighborTopLeft.X : neighborTopLeft.X + neighbor.ActualWidth;
                        edgeX = (edgeX + neighborEdgeX) / 2;
                    }
                    else if (Math.Abs(pointInRow.Y - neighborTopLeft.Y) < Math.Abs(pointInRow.Y - topLeft.Y))
                    {
                        // Ranh giới xuống-hàng (cuối hàng này / đầu hàng kế) — 2 mép không cùng hàng
                        // nên không có "giữa" thật. Con trỏ đang ở gần hàng của LÂN CẬN hơn (VD rà
                        // vào mép cuối trang 4 nhưng target logic lại là đầu trang 5 ở hàng sau) —
                        // neo theo hàng lân cận để user tự do chọn hiện ở mép nào bằng cách di
                        // chuột qua lại, thay vì luôn nhảy cứng sang mép trang đích theo index thuần logic.
                        edgeX = afterTarget ? neighborTopLeft.X : neighborTopLeft.X + neighbor.ActualWidth;
                        anchorItem = neighbor;
                        anchorTopLeft = neighborTopLeft;
                    }
                }
                finalX = edgeX;
                finalY = anchorTopLeft.Y;

                // RowIndicatorHost có ClipToBounds=False (cố ý, để scrollbar/thumb không bị cắt góc
                // bo tròn) — hàng đang cuộn dở dang (nhô lên trên/xuống dưới viewport) cho topLeft.Y
                // âm hoặc vượt quá host.ActualHeight, khiến vạch tràn thẳng lên tiêu đề window hoặc
                // xuống dưới đáy. Kẹp lại trong đúng phạm vi host trước khi vẽ.
                double clampedTop = Math.Max(0, finalY);
                double clampedBottom = Math.Min(host.ActualHeight, finalY + anchorItem.ActualHeight);
                finalY = clampedTop;
                indicator.Height = Math.Max(0, clampedBottom - clampedTop);
                SetIndicatorGeometry(indicator, finalX, finalY, horizontal: true);
            }
            else
            {
                double edgeY = afterTarget ? topLeft.Y + item.ActualHeight : topLeft.Y;
                if (neighbor != null)
                {
                    Point neighborTopLeft = neighbor.TranslatePoint(new Point(0, 0), host);
                    double neighborEdgeY = afterTarget ? neighborTopLeft.Y : neighborTopLeft.Y + neighbor.ActualHeight;
                    edgeY = (edgeY + neighborEdgeY) / 2;
                }
                double clampedLeft = Math.Max(0, topLeft.X);
                double clampedRight = Math.Min(host.ActualWidth, topLeft.X + item.ActualWidth);
                finalX = clampedLeft;
                finalY = edgeY;
                indicator.Width = Math.Max(0, clampedRight - clampedLeft);
                SetIndicatorGeometry(indicator, finalX, finalY, horizontal: false);
            }
            _activeRowIndicator = indicator;
            LogDropIndicatorSkip($"wrapped={wrapped} horizontal={horizontal} pointInRow=({pointInRow.X:F0},{pointInRow.Y:F0}) itemsCount={row.Items.Count} index={index} targetIndex={targetIndex} afterTarget={afterTarget} usedFallback={usedFallback} topLeft=({topLeft.X:F0},{topLeft.Y:F0}) final=({finalX:F0},{finalY:F0})");
        }

        private static void SetIndicatorGeometry(Border indicator, double x, double y, bool horizontal)
        {
            if (horizontal) indicator.Width = 3;
            else indicator.Height = 3;
            indicator.Margin = new Thickness(x, y, 0, 0);
            indicator.Visibility = Visibility.Visible;
        }

        private void RefreshWorkspaceWidthAfterLayout()
        {
            // THIẾU nhánh Chia đôi là nguyên nhân thật của bug "double-click tiêu đề maximize
            // không tự dàn layout" — xác nhận qua %TEMP%\XTPdfMergeApp_WindowResize.log:
            // Window.SizeChanged bắn ĐÚNG (1936x1048 sau maximize), nhưng GroupsScrollViewer bị
            // Collapsed trong chế độ Chia đôi nên KHÔNG bao giờ tự bắn SizeChanged — và hàm này
            // trước đây chỉ rẽ Row/Column, bỏ sót hẳn UpdateSplitPaneContentWidth (đọc
            // SplitHostGrid.ActualWidth) nên dù Window có resize đúng, layout Chia đôi vẫn không
            // được tính lại. KHÔNG liên quan gì tới double-click vs bấm nút — nút Maximize "có vẻ"
            // ổn chỉ vì lúc đó thường KHÔNG ở chế độ Chia đôi khi test.
            if (_splitViewMode) UpdateSplitPaneContentWidth();
            else if (_layoutMode == GroupLayoutMode.Row) UpdateGroupsListRowWidth();
            else UpdateAdaptiveLayout();
            QueueVisibleThumbnailScans();
        }

        /// <summary>Lấy (tạo nếu chưa có) ReaderWindow dùng chung, đồng thời đồng bộ
        /// ViewerToggleButton.IsChecked/ToolTip theo IsVisible thật của nó — kể cả khi user tự đóng
        /// bằng nút X của Viewer (không đi qua ViewerToggleButton_Click). Đăng ký kiểu bỏ-rồi-đăng-ký-
        /// lại nên gọi nhiều lần an toàn, không bị trùng handler.</summary>
        private ReaderWindow EnsureReaderWindow()
        {
            var reader = ReaderWindow.GetOrCreate(this, _groups);
            reader.IsVisibleChanged -= ReaderWindow_IsVisibleChanged;
            reader.IsVisibleChanged += ReaderWindow_IsVisibleChanged;
            return reader;
        }

        private void ReaderWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            bool visible = (bool)e.NewValue;
            ViewerToggleButton.IsChecked = visible;
            ViewerToggleButton.ToolTip = visible ? "Ẩn Viewer" : "Hiện Viewer";
        }

        private async void ViewerToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewerToggleButton.IsChecked != true)
            {
                ReaderWindow.Instance?.HideReader();
                return;
            }

            var reader = EnsureReaderWindow();
            if (_statusSelectedGroup != null && _statusSelectedPage != null)
                await reader.ShowPageAsync(_statusSelectedGroup, _statusSelectedPage, preserveZoomMode: true);
            else
                reader.ShowAndActivate();
        }

        /// <summary>Ctrl+Z/Y (undo/redo) và Ctrl+↑/↓/Home/End (đẩy trang đang chọn). Phím tắt
        /// điều hướng/zoom của Viewer giờ nằm ở ReaderWindow_PreviewKeyDown riêng, vì Viewer là
        /// Window độc lập — WPF tự phân luồng input theo đúng Window nào đang focus.</summary>
        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                if ((e.Key == Key.Y || (e.Key == Key.Z && shift)) && _workspace.History.CanRedo)
                {
                    _workspace.History.Redo();
                    RefreshWorkspaceAfterHistoryChange();
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Z && !shift && _workspace.History.CanUndo)
                {
                    _workspace.History.Undo();
                    RefreshWorkspaceAfterHistoryChange();
                    e.Handled = true;
                    return;
                }

                // Ctrl+↑/↓/Home/End: đẩy trang đang chọn lên/xuống/về đầu/cuối window mà không cần
                // kéo chuột — tiện hơn hẳn khi window có nhiều trang, khỏi phải kéo tay qua khoảng
                // cách xa kèm auto-scroll mép. Dùng CTRL+Home/End (không phải Home/End trơn) để
                // không đụng hành vi mặc định của ListBox (Home/End trơn = chọn item đầu/cuối).
                switch (e.Key)
                {
                    case Key.Up:
                        MoveSelectedPagesUpOrDown(-1);
                        e.Handled = true;
                        return;
                    case Key.Down:
                        MoveSelectedPagesUpOrDown(1);
                        e.Handled = true;
                        return;
                    case Key.Home:
                        MoveSelectedPagesToEdge(toStart: true);
                        e.Handled = true;
                        return;
                    case Key.End:
                        MoveSelectedPagesToEdge(toStart: false);
                        e.Handled = true;
                        return;
                }
            }
        }

        // ── Double-click 1 trang: chọn + hiện cửa sổ Viewer riêng về Fit width ──

        private async void PageListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBox lb) return;
            if (FindListBoxItem(e.OriginalSource as DependencyObject)?.DataContext is not PageRow row) return;
            if (lb.Tag is not DocumentGroup group) return;

            e.Handled = true;
            lb.SelectedItem = row; // trigger SelectionChanged → SetStatusSelection (chỉ đồng bộ nếu Viewer đã mở)
            await EnsureReaderWindow().ShowPageAsync(group, row, preserveZoomMode: false);
        }

        // ── Kéo CẢ WINDOW (bằng tiêu đề) để sắp xếp lại tự do — không cần nút Lên/Xuống ──

        private void GroupHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                GroupHeader_MouseDoubleClick(sender, e);
                return;
            }

            _groupDragStart = e.GetPosition(null);
            _groupDragCandidate = (sender as FrameworkElement)?.DataContext as DocumentGroup;
            if (_groupDragCandidate != null)
            {
                SelectGroupWithoutScrolling(_groupDragCandidate);
                SetStatusSelection(_groupDragCandidate, null, 0);
            }
        }

        private void SelectGroupWithoutScrolling(DocumentGroup group)
        {
            if (GroupsList.ItemContainerGenerator.ContainerFromItem(group) is not ListBoxItem container) return;
            double horizontal = GroupsScrollViewer.HorizontalOffset;
            double vertical = GroupsScrollViewer.VerticalOffset;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
                GroupsList.SelectedItems.Clear();
            container.IsSelected = true;
            _ = Dispatcher.InvokeAsync(() =>
            {
                GroupsScrollViewer.ScrollToHorizontalOffset(horizontal);
                GroupsScrollViewer.ScrollToVerticalOffset(vertical);
            }, DispatcherPriority.Input);
        }

        private void GroupHeader_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_splitViewMode) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (!PassedDragThreshold(_groupDragStart, e.GetPosition(null))) return;
            if (_groupDragCandidate is not { } group || sender is not DependencyObject dragSource) return;

            _groupDragCandidate = null;
            var ghost = BeginDragGhost(group.Pages.FirstOrDefault()?.Thumbnail,
                $"{group.FileName} · {group.Pages.Count} trang");
            var headerElement = sender as FrameworkElement;
            if (headerElement != null) headerElement.Cursor = Cursors.SizeAll;
            GiveFeedbackEventHandler feedback = (_, args) =>
            {
                ghost?.Update(GetDragPointerPosition(), copy: false);
                args.UseDefaultCursors = true;
            };
            var feedbackSource = dragSource as UIElement;
            if (feedbackSource != null) feedbackSource.GiveFeedback += feedback;
            try
            {
                DragDrop.DoDragDrop(dragSource, new DataObject("XTPdfMergeGroup", group), DragDropEffects.Move);
            }
            finally
            {
                if (feedbackSource != null) feedbackSource.GiveFeedback -= feedback;
                EndDragGhost(ghost);
                if (headerElement != null) headerElement.Cursor = Cursors.Arrow;
                _groupDragCandidate = null;
            }
        }

        private void GroupHeader_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not DocumentGroup group) return;
            e.Handled = true;

            if (ReferenceEquals(_focusedGroup, group))
                ExitGroupFocusMode(restorePreviousView: true);
            else
                EnterGroupFocusMode(group);
        }

        private void EnterGroupFocusMode(DocumentGroup group)
        {
            if (_focusedGroup == null)
            {
                _focusRestoreWasSplit = _splitViewMode;
                _focusRestoreLeftGroup = _splitLeftGroup;
                _focusRestoreRightGroup = _splitRightGroup;
                _focusRestoreHorizontalOffset = GroupsScrollViewer.HorizontalOffset;
                _focusRestoreVerticalOffset = GroupsScrollViewer.VerticalOffset;
            }

            _focusedGroup = group;
            GroupsScrollViewer.Visibility = Visibility.Collapsed;
            SplitLeftColumn.Width = new GridLength(1, GridUnitType.Star);
            SplitDividerColumn.Width = new GridLength(0);
            SplitRightColumn.Width = new GridLength(0);
            SplitLeftContent.Content = group;
            SplitRightContent.Content = null;
            SplitHostGrid.Visibility = Visibility.Visible;
            UpdateSplitPaneContentWidth();

            _ = Dispatcher.InvokeAsync(() =>
            {
                foreach (var lb in FindVisualChildren<ListBox>(SplitLeftContent).Where(lb => lb.Tag is DocumentGroup))
                    QueueVisibleThumbnailScan(lb);
            }, DispatcherPriority.ContextIdle);
        }

        private void ExitGroupFocusMode(bool restorePreviousView)
        {
            if (_focusedGroup == null) return;
            _focusedGroup = null;

            SplitLeftColumn.Width = new GridLength(1, GridUnitType.Star);
            SplitDividerColumn.Width = new GridLength(6);
            SplitRightColumn.Width = new GridLength(1, GridUnitType.Star);

            if (restorePreviousView && _focusRestoreWasSplit &&
                _focusRestoreLeftGroup != null && _focusRestoreRightGroup != null &&
                _groups.Contains(_focusRestoreLeftGroup) && _groups.Contains(_focusRestoreRightGroup))
            {
                _splitLeftGroup = _focusRestoreLeftGroup;
                _splitRightGroup = _focusRestoreRightGroup;
                SplitLeftContent.Content = _splitLeftGroup;
                SplitRightContent.Content = _splitRightGroup;
                SplitHostGrid.Visibility = Visibility.Visible;
                GroupsScrollViewer.Visibility = Visibility.Collapsed;
                UpdateSplitPaneContentWidth();
            }
            else
            {
                SplitLeftContent.Content = _splitLeftGroup;
                SplitRightContent.Content = _splitRightGroup;
                SplitHostGrid.Visibility = _splitViewMode ? Visibility.Visible : Visibility.Collapsed;
                GroupsScrollViewer.Visibility = _splitViewMode || _groups.Count == 0
                    ? Visibility.Collapsed : Visibility.Visible;
                if (!_splitViewMode)
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        GroupsScrollViewer.ScrollToHorizontalOffset(_focusRestoreHorizontalOffset);
                        GroupsScrollViewer.ScrollToVerticalOffset(_focusRestoreVerticalOffset);
                        QueueVisibleThumbnailScans();
                    }, DispatcherPriority.ContextIdle);
            }

            _focusRestoreWasSplit = false;
            _focusRestoreLeftGroup = null;
            _focusRestoreRightGroup = null;
        }

        // ── Kéo file PDF (từ Explorer/XTSheet/XTPrint) thả vào cột phải để tạo WINDOW MỚI, hoặc thả 1 window đang kéo (XTPdfMergeGroup) để sắp xếp lại vị trí ──

        private void GroupsArea_DragOver(object sender, DragEventArgs e)
        {
            bool acceptFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
            bool acceptGroup = e.Data.GetDataPresent("XTPdfMergeGroup");
            bool acceptPages = e.Data.GetDataPresent("XTPdfMergePages");

            if (acceptGroup)
            {
                var draggedGroup = e.Data.GetData("XTPdfMergeGroup") as DocumentGroup;
                Point pointInGroups = e.GetPosition(GroupsList);
                int insertAt = FindGroupInsertIndex(pointInGroups);
                bool noOpMove = draggedGroup == null || IsGroupDropNoOp(draggedGroup, insertAt);
                e.Effects = noOpMove ? DragDropEffects.None : DragDropEffects.Move;
                if (noOpMove) GroupDropIndicator.Visibility = Visibility.Collapsed;
                else ShowGroupDropIndicator(pointInGroups);
            }
            else if (acceptPages)
            {
                GroupDropIndicator.Visibility = Visibility.Collapsed;
                e.Effects = DragDropEffects.Move;
            }
            else
            {
                GroupDropIndicator.Visibility = Visibility.Collapsed;
                e.Effects = acceptFiles ? DragDropEffects.Copy : DragDropEffects.None;
            }

            SetDropHighlight(acceptFiles);

            if (acceptFiles || acceptGroup || acceptPages)
                AutoScrollIfNearEdge(GroupsScrollViewer, e.GetPosition(GroupsScrollViewer), horizontal: _layoutMode == GroupLayoutMode.Column);

            e.Handled = true;
        }

        private void GroupsArea_DragLeave(object sender, DragEventArgs e)
        {
            Point position = e.GetPosition(DropZoneOuterBorder);
            if (position.X >= 0 && position.Y >= 0 &&
                position.X <= DropZoneOuterBorder.ActualWidth &&
                position.Y <= DropZoneOuterBorder.ActualHeight)
                return;
            GroupDropIndicator.Visibility = Visibility.Collapsed;
            SetDropHighlight(false);
        }

        private void SetDropHighlight(bool active)
        {
            // Nếu đã ở đúng trạng thái rồi thì bỏ qua — tránh animation bị "giật lại từ đầu"
            // mỗi khi DragOver bắn liên tục (nhiều lần/giây) trong lúc con trỏ đứng yên.
            if (_dropHighlightActive == active) return;
            _dropHighlightActive = active;

            var accentBrush = (Brush)FindResource("PrimaryBrush");
            var mutedBorderBrush = (Brush)FindResource("BorderBrush");
            var mutedTextBrush = (Brush)FindResource("TextMutedBrush");

            DropZoneOuterBorder.BorderBrush = active ? accentBrush : mutedBorderBrush;
            DropZoneOuterBorder.BorderThickness = new Thickness(active ? 2 : 1);
            EmptyHint.Visibility = active || _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyHintDash.Stroke = active ? accentBrush : mutedBorderBrush;
            EmptyHintDash.StrokeDashArray = active ? null : new DoubleCollection { 5, 4 };
            EmptyHintIcon.Fill = active ? accentBrush : mutedTextBrush;

            var duration = TimeSpan.FromMilliseconds(160);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            EmptyHintDash.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(active ? 1.0 : 0.6, duration) { EasingFunction = ease });
            _emptyHintShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                new DoubleAnimation(active ? 0.38 : 0, duration) { EasingFunction = ease });
            EmptyHintScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(active ? 1.02 : 1.0, duration) { EasingFunction = ease });
            EmptyHintScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(active ? 1.02 : 1.0, duration) { EasingFunction = ease });
        }

        private void DropZoneOuterBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;
            // Double-click ĐÂY chỉ có nghĩa "mở hộp thoại thêm file" khi nó rơi đúng vào khoảng
            // trống của vùng kéo-thả — không phải vào 1 "window" tài liệu nào. Trước đây chỉ loại
            // trừ khi có tổ tiên là ListBoxItem (đúng cho chế độ Hàng/Cột bình thường), nhưng ở chế
            // độ Focus/Split, window đang xem được host qua ContentPresenter (SplitLeftContent/
            // SplitRightContent) — KHÔNG có ListBoxItem tổ tiên — nên double-click vào tiêu đề window
            // lúc đó bị bắt nhầm thành "thêm file" thay vì thoát Focus mode. Kiểm tra theo DataContext
            // (luôn là DocumentGroup ở gốc mỗi "window", bất kể host qua ListBoxItem hay ContentPresenter)
            // để đúng trong MỌI chế độ hiển thị.
            if (HasAncestorWithDataContext<DocumentGroup>(e.OriginalSource as DependencyObject)) return;
            e.Handled = true;
            AddFiles_Click(sender, new RoutedEventArgs());
        }

        private static bool HasAncestorWithDataContext<T>(DependencyObject? source) where T : class
        {
            while (source != null)
            {
                if (source is FrameworkElement fe && fe.DataContext is T) return true;
                source = System.Windows.Media.VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private void GroupsArea_Drop(object sender, DragEventArgs e)
        {
            GroupDropIndicator.Visibility = Visibility.Collapsed;
            SetDropHighlight(false);

            if (e.Data.GetDataPresent("XTPdfMergeGroup") && e.Data.GetData("XTPdfMergeGroup") is DocumentGroup draggedGroup)
            {
                int oldIndex = _groups.IndexOf(draggedGroup);
                if (oldIndex < 0) { e.Handled = true; return; }

                int insertAt = FindGroupInsertIndex(e.GetPosition(GroupsList));
                if (IsGroupDropNoOp(draggedGroup, insertAt))
                {
                    e.Handled = true;
                    return;
                }

                if (insertAt > oldIndex) insertAt--;
                insertAt = Math.Clamp(insertAt, 0, _groups.Count - 1);
                _workspace.Execute(new ReorderDocumentsCommand(_workspace, draggedGroup, insertAt));
                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] fromExplorer)
                foreach (var p in fromExplorer.Where(p => string.Equals(Path.GetExtension(p), ".pdf", StringComparison.OrdinalIgnoreCase)))
                    _ = AddFileAsGroup(p);

            e.Handled = true;
        }

        /// <summary>Vị trí sẽ chèn WINDOW đang kéo vào GIỮA các window khác — so theo Y (chế độ Hàng, xếp dọc) hoặc X (chế độ Cột, xếp ngang).</summary>
        private int FindGroupInsertIndex(Point pointInGroupsList)
        {
            var realIndices = new List<int>();
            var bounds = new List<Rect>();
            for (int i = 0; i < _groups.Count; i++)
            {
                if (GroupsList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;
                Point topLeft = container.TranslatePoint(new Point(0, 0), GroupsList);
                realIndices.Add(i);
                bounds.Add(new Rect(topLeft, new Size(container.ActualWidth, container.ActualHeight)));
            }

            bool horizontal = _layoutMode != GroupLayoutMode.Row;
            int position = DropPositionMath.FindInsertIndex(bounds, pointInGroupsList, horizontal);
            return position < realIndices.Count ? realIndices[position] : _groups.Count;
        }

        private bool IsGroupDropNoOp(DocumentGroup draggedGroup, int insertAt)
            => DropPositionMath.IsReorderNoOp(_groups.IndexOf(draggedGroup), insertAt, _groups.Count);

        private void ShowGroupDropIndicator(Point pointInGroupsList)
        {
            if (_groups.Count == 0) { GroupDropIndicator.Visibility = Visibility.Collapsed; return; }

            bool vertical = _layoutMode == GroupLayoutMode.Row;
            int index = FindGroupInsertIndex(pointInGroupsList);
            int targetIndex = Math.Clamp(index, 0, _groups.Count - 1);
            bool afterTarget = index > targetIndex;

            if (GroupsList.ItemContainerGenerator.ContainerFromIndex(targetIndex) is not FrameworkElement item)
            { GroupDropIndicator.Visibility = Visibility.Collapsed; return; }

            Point topLeft = item.TranslatePoint(new Point(0, 0), GroupsOverlayGrid);
            if (vertical)
            {
                double y = afterTarget ? topLeft.Y + item.ActualHeight : topLeft.Y;
                GroupDropIndicator.Width = item.ActualWidth;
                GroupDropIndicator.Height = 3;
                GroupDropIndicator.Margin = new Thickness(topLeft.X, y, 0, 0);
            }
            else
            {
                double x = afterTarget ? topLeft.X + item.ActualWidth : topLeft.X;
                GroupDropIndicator.Height = item.ActualHeight;
                GroupDropIndicator.Width = 3;
                GroupDropIndicator.Margin = new Thickness(x, topLeft.Y, 0, 0);
            }
            GroupDropIndicator.Visibility = Visibility.Visible;
        }

        /// <summary>Tự cuộn dần khi con trỏ sát mép trong lúc kéo-thả — <paramref name="horizontal"/> chọn trục cuộn.</summary>
        private static void AutoScrollIfNearEdge(ScrollViewer sv, Point pos, bool horizontal)
        {
            if (horizontal)
            {
                if (DropPositionMath.EdgeAutoScrollOffset(pos.X, sv.ActualWidth, sv.HorizontalOffset) is { } offset)
                    sv.ScrollToHorizontalOffset(offset);
            }
            else
            {
                if (DropPositionMath.EdgeAutoScrollOffset(pos.Y, sv.ActualHeight, sv.VerticalOffset) is { } offset)
                    sv.ScrollToVerticalOffset(offset);
            }
        }

        // ── Đổi bố cục Hàng ⇄ Cột ──────────────────────────────────────────

        private void SplitViewButton_Click(object sender, RoutedEventArgs e)
        {
            if (_focusedGroup != null) ExitGroupFocusMode(restorePreviousView: true);
            var selected = GroupsList.SelectedItems.Cast<DocumentGroup>().ToList();
            if (selected.Count != 2)
            {
                System.Windows.MessageBox.Show(this, "Hãy chọn đúng 2 file trực tiếp trong vùng thumbnail rồi bấm Chia đôi.",
                    "Chia đôi", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DocumentGroup left = selected[0], right = selected[1];
            _splitLeftGroup = left;
            _splitRightGroup = right;
            _splitViewMode = true;
            SplitLeftContent.Content = left;
            SplitRightContent.Content = right;
            GroupsScrollViewer.Visibility = Visibility.Collapsed;
            SplitHostGrid.Visibility = Visibility.Visible;
            UpdateSplitPaneContentWidth();
            SplitViewButton.ToolTip = $"Đang chia đôi: {left.FileName} ↔ {right.FileName}";
            LayoutModeButton.IsEnabled = false;
            _ = Dispatcher.InvokeAsync(() =>
            {
                foreach (var lb in FindVisualChildren<ListBox>(SplitHostGrid).Where(lb => lb.Tag is DocumentGroup))
                    QueueVisibleThumbnailScan(lb);
            }, DispatcherPriority.ContextIdle);
            UpdateStatusBar();
        }

        private void ResetLayoutButton_Click(object sender, RoutedEventArgs e)
        {
            if (_focusedGroup != null) ExitGroupFocusMode(restorePreviousView: false);
            _splitViewMode = false;
            _splitLeftGroup = null;
            _splitRightGroup = null;
            SplitLeftContent.Content = null;
            SplitRightContent.Content = null;
            SplitHostGrid.Visibility = Visibility.Collapsed;
            GroupsScrollViewer.Visibility = _groups.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            LayoutModeButton.IsEnabled = true;
            SplitViewButton.ToolTip = "Chọn 2 file để xem cạnh nhau";
            ApplyLayoutMode(GroupLayoutMode.Row);
            ReaderWindow.Instance?.HideReader();
            GroupsScrollViewer.ScrollToHome();
            UpdateStatusBar();
        }

        private async void LayoutModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_layoutSwitchInProgress) return;
            _layoutSwitchInProgress = true;
            LayoutModeButton.IsEnabled = false;
            GroupsList.IsHitTestVisible = false;

            try
            {
                StopSmoothScrollAnimations();
                GroupDropIndicator.Visibility = Visibility.Collapsed;
                ShowDropIndicator(null);

                var nextMode = _layoutMode == GroupLayoutMode.Row ? GroupLayoutMode.Column : GroupLayoutMode.Row;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                ApplyLayoutMode(nextMode);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            }
            finally
            {
                GroupsList.IsHitTestVisible = true;
                LayoutModeButton.IsEnabled = true;
                _layoutSwitchInProgress = false;
            }
        }

        private void ApplyLayoutMode(GroupLayoutMode mode)
        {
            _layoutMode = mode;
            MergeAppSettingsStore.SetLayoutMode(_layoutMode == GroupLayoutMode.Column ? "Column" : "Row");

            if (_layoutMode == GroupLayoutMode.Row)
            {
                GroupsList.ItemTemplate = (DataTemplate)FindResource("DocumentGroupTemplateRow");
                GroupsList.ItemsPanel = (ItemsPanelTemplate)FindResource("GroupsPanelRow");
                GroupsList.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                GroupsList.VerticalContentAlignment = VerticalAlignment.Top;
                GroupsScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                GroupsScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                UpdateGroupsListRowWidth();
                LayoutModeButton.Icon = FindResource("Mat.ViewColumn");
                LayoutModeButton.Text = "Lưới";
                _ = Dispatcher.InvokeAsync(RefreshWorkspaceWidthAfterLayout, DispatcherPriority.ContextIdle);
                return;
            }

            GroupsList.ItemTemplate = (DataTemplate)FindResource("DocumentGroupTemplateColumn");
            GroupsList.ItemsPanel = (ItemsPanelTemplate)FindResource("GroupsPanelColumn");
            // NaN — xoá Width cứng UpdateGroupsListRowWidth có thể đã set lúc còn ở chế độ Hàng, để
            // GroupsList tự rộng theo nội dung (StackPanel ngang); rộng hơn viewport thì
            // GroupsScrollViewer tự cuộn ngang thay vì bị ép khít viewport.
            GroupsList.Width = double.NaN;
            GroupsList.HorizontalContentAlignment = HorizontalAlignment.Left;
            // Stretch (KHÔNG Top) — mỗi cột cao HẾT view chứa nó (đúng bằng border kéo-file), không
            // tự co theo số trang nữa (trang bên trong tự cuộn dọc riêng nếu không đủ chỗ, xem
            // DocumentGroupTemplateColumn).
            GroupsList.VerticalContentAlignment = VerticalAlignment.Stretch;
            // Border ngoài KHÔNG cuộn dọc nữa — mỗi window đã cao hết view và tự cuộn dọc RIÊNG của
            // nó. Cuộn NGANG (Auto, không phải Disabled) chỉ hiện khi có nhiều file hơn chỗ chứa.
            GroupsScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            GroupsScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            LayoutModeButton.Icon = FindResource("Mat.ViewAgenda");
            LayoutModeButton.Text = "Hàng";
            UpdateAdaptiveLayout();
            // Giống nhánh Row: tính lại 1 lần nữa sau khi WPF đã remeasure thật sự
            // (ItemsPanel/ItemTemplate vừa đổi ở trên chưa kịp remeasure tại đây),
            // nếu không GroupsList.Width có thể bị "kẹt" ở giá trị sai cho tới khi
            // có 1 SizeChanged khác (vd. resize cửa sổ) tình cờ sửa lại.
            _ = Dispatcher.InvokeAsync(RefreshWorkspaceWidthAfterLayout, DispatcherPriority.ContextIdle);
        }

        private void UpdateGroupsListRowWidth()
        {
            if (GroupsScrollViewer == null) return;
            double viewportWidth = GroupsScrollViewer.ViewportWidth > 0
                ? GroupsScrollViewer.ViewportWidth
                : GroupsScrollViewer.ActualWidth;
            var result = AdaptiveLayoutMath.ComputeRowWidth(viewportWidth);
            GroupsList.Width = result.GroupsListWidth;
            RowCardWidth = result.RowCardWidth;
            // Chế độ Hàng (Row) không qua UpdateAdaptiveLayout nên phải tự gọi ở đây — xem
            // InvalidateWrapPanelsLayout: kéo splitter/maximize cửa sổ đổi kích thước KHÔNG tự
            // khiến VirtualizingWrapPanel (nếu đang Focus/Chia đôi) dàn lại số cột nếu thiếu bước
            // ép invalidate này.
            InvalidateWrapPanelsLayout();
        }

        private void UpdateColumnViewportHeight()
        {
            if (GroupsScrollViewer == null) return;

            double viewportHeight = GroupsScrollViewer.ViewportHeight;
            if (viewportHeight <= 0 || double.IsInfinity(viewportHeight) || double.IsNaN(viewportHeight))
            {
                Thickness padding = GroupsScrollViewer.Padding;
                viewportHeight = GroupsScrollViewer.ActualHeight - padding.Top - padding.Bottom;
            }

            if (viewportHeight <= 0 || double.IsInfinity(viewportHeight) || double.IsNaN(viewportHeight)) return;

            // DocumentGroupTemplateColumn uses Margin=4 on the outer card. Keep the card's
            // allocated height inside the ScrollViewer viewport so only each page list scrolls.
            ColumnMaxContentHeight = Math.Max(220, viewportHeight - 8);
        }

        private void UpdateAdaptiveLayout()
        {
            if (GroupsScrollViewer == null || GroupsList == null) return;
            UpdateColumnViewportHeight();

            double viewportWidth = GroupsScrollViewer.ViewportWidth > 0
                ? GroupsScrollViewer.ViewportWidth
                : GroupsScrollViewer.ActualWidth;

            var result = AdaptiveLayoutMath.Compute(viewportWidth, _groups.Count, _autoThumbnailSize, ThumbnailWidth, ThumbnailHeight);
            AdaptiveDocumentWidth = result.AdaptiveDocumentWidth;
            ThumbnailWidth = result.ThumbnailWidth;
            ThumbnailHeight = result.ThumbnailHeight;

            CardMaxWidth = AdaptiveDocumentWidth;
            ColumnPageListWidth = Math.Min(AdaptiveDocumentWidth - 10, ThumbnailWidth + 24);
            SplitThumbnailItemHeight = ThumbnailHeight + 6;
            // KHÔNG còn ép GroupsList.Width = đúng viewport ở đây nữa — chế độ Cột giờ là 1 dãy
            // NGANG cuộn được (xem GroupsPanelColumn/ApplyLayoutMode), GroupsList phải được TỰ DO
            // rộng hơn viewport để chứa hết các cột rồi cuộn ngang, ép cứng bằng viewport sẽ chặn
            // mất khả năng cuộn đó.
            UpdateSplitPaneContentWidth();
            InvalidateWrapPanelsLayout();
        }

        /// <summary>WpfToolkit.Controls.VirtualizingWrapPanel đôi khi KHÔNG tự dàn lại số cột dù
        /// kích thước item (ThumbnailWidth/Height, qua binding) hay kích thước viewport chứa nó vừa
        /// đổi — panel ảo hoá cache lại phép đo trước đó để tối ưu hiệu năng, không phải lúc nào
        /// cũng tự phát hiện binding đổi giá trị là đủ để ép đo lại. Xác nhận qua báo lỗi: kéo
        /// ReaderSplitter (đổi bề rộng vùng chứa) hoặc maximize/restore cửa sổ qua tiêu đề (đổi
        /// ActualWidth toàn bộ cây) đều không tự kích hoạt dàn lại, phải thao tác thêm 1 lệnh khác
        /// (đổi chế độ layout chẳng hạn) mới thấy đúng. Gọi InvalidateMeasure/InvalidateArrange
        /// TRỰC TIẾP lên panel ngay sau mỗi lần layout thay đổi để ép nó dàn lại ngay lập tức.</summary>
        private void InvalidateWrapPanelsLayout()
        {
            if (GroupsList == null) return;
            foreach (var panel in FindVisualChildren<VirtualizingWrapPanel>(GroupsList))
            {
                panel.InvalidateMeasure();
                panel.InvalidateArrange();
            }
        }

        /// <summary>Chuyển từng file nguồn (DISTINCT) vào thư mục con "Đã ghép" NGAY CẠNH nơi nó đang nằm — đỡ rác tồn đọng trong Autosave qua thời gian.</summary>
        private static void CleanupSourceFiles(IEnumerable<string> paths)
        {
            foreach (var p in paths)
            {
                try
                {
                    string? dir = Path.GetDirectoryName(p);
                    if (string.IsNullOrWhiteSpace(dir)) continue;

                    string doneDir = Path.Combine(dir, "Đã ghép");
                    Directory.CreateDirectory(doneDir);

                    string dest = Path.Combine(doneDir, Path.GetFileName(p));
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(p, dest);
                }
                catch { }
            }
        }
    }
}
