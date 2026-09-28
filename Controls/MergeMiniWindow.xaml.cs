using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;

namespace XTPdfMergeApp.Controls
{
    /// <summary>
    /// Cửa sổ con của 1 file trong màn hình Merge (docs/UI_REDESIGN.md, P6): header kéo được, lưới thumbnail (kéo thả trang, Ctrl = sao chép),
    /// biểu tượng file kéo CẢ FILE. Cửa sổ không tự đặt vị trí — <see cref="MergeView"/> sắp xếp và nhận các sự kiện di chuyển/đổi cỡ.
    /// </summary>
    public partial class MergeMiniWindow : UserControl
    {
        internal DocumentGroup Group { get; }

        public static readonly DependencyProperty ThumbWidthProperty = DependencyProperty.Register(nameof(ThumbWidth), typeof(double), typeof(MergeMiniWindow),
            new PropertyMetadata(100.0, (d, _) => ((MergeMiniWindow)d).OnThumbWidthChanged()));
        public static readonly DependencyProperty ThumbHeightProperty = DependencyProperty.Register(nameof(ThumbHeight), typeof(double), typeof(MergeMiniWindow), new PropertyMetadata(74.0));
        public static readonly DependencyProperty CellSizeProperty = DependencyProperty.Register(nameof(CellSize), typeof(Size), typeof(MergeMiniWindow), new PropertyMetadata(new Size(112, 112)));

        /// <summary>Bề rộng thumbnail (thanh "Page size" của màn hình Merge).</summary>
        public double ThumbWidth { get => (double)GetValue(ThumbWidthProperty); set => SetValue(ThumbWidthProperty, value); }
        public double ThumbHeight { get => (double)GetValue(ThumbHeightProperty); private set => SetValue(ThumbHeightProperty, value); }
        public Size CellSize { get => (Size)GetValue(CellSizeProperty); private set => SetValue(CellSizeProperty, value); }

        private void OnThumbWidthChanged()
        {
            double w = ThumbWidth, h = Math.Round(w * 0.74);
            ThumbHeight = h;
            CellSize = new Size(w + 12, h + 38);
        }

        private void GoTo_Focus(object sender, MouseButtonEventArgs e) => GoToBox.Focus();

        private void GoTo_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (int.TryParse(GoToBox.Text.Trim(), out int n) && Group.Pages.Count > 0)
            {
                int index = Math.Clamp(n, 1, Group.Pages.Count) - 1;
                Thumbs.SelectedItem = Group.Pages[index];
                Thumbs.ScrollIntoView(Group.Pages[index]);
            }
            GoToBox.Clear();
            Thumbs.Focus();
        }

        internal MergeMiniWindow(DocumentGroup group)
        {
            Group = group;
            InitializeComponent();
            DataContext = group;
            Thumbs.ItemsSource = group.Pages;
            void UpdateCount() => CountText.Text = group.Pages.Count == 1 ? "1 page" : group.Pages.Count + " pages";
            group.Pages.CollectionChanged += (_, _) => UpdateCount();
            UpdateCount();
            PreviewMouseDown += (_, _) => Activated?.Invoke(this);
        }

        // ── Sự kiện lên MergeView ─────────────────────────────────────

        internal event Action<MergeMiniWindow>? Activated;
        internal event Action<MergeMiniWindow, Vector>? MoveDelta;
        internal event Action<MergeMiniWindow, Point>? MoveFinished;
        internal event Action<MergeMiniWindow, Vector, string>? ResizeDelta;
        internal event Action<MergeMiniWindow>? ToggleMaximizeRequested, MinimizeRequested, CloseRequested, SaveRequested;
        /// <summary>Thả trang/file vào cửa sổ: (cửa sổ đích, dữ liệu kéo, chỉ số chèn, sao chép?).</summary>
        internal event Action<MergeMiniWindow, ReaderSidePanel.PageDragData, int, bool>? PagesDropped;
        internal event Action<MergeMiniWindow, IReadOnlyList<PageRow>>? DeleteRequested;

        internal void SetActive(bool active)
            => Frame.BorderBrush = (Brush)FindResource(active ? "Ui.Accent" : "Ui.Border");

        /// <summary>Cho phép đổi cỡ (chỉ layout Free).</summary>
        internal void SetResizable(bool resizable)
        {
            var visibility = resizable ? Visibility.Visible : Visibility.Collapsed;
            ResizeRight.Visibility = ResizeBottom.Visibility = ResizeCorner.Visibility = visibility;
        }

        internal void ShowDropGlow(bool on) => DropGlow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        internal IReadOnlyList<PageRow> SelectedPages
            => Thumbs.SelectedItems.Cast<PageRow>().OrderBy(p => Group.Pages.IndexOf(p)).ToList();

        internal void SelectPages(IReadOnlyList<PageRow> pages)
        {
            Thumbs.SelectedItems.Clear();
            foreach (var page in pages)
                if (Group.Pages.Contains(page)) Thumbs.SelectedItems.Add(page);
            if (pages.Count > 0 && Group.Pages.Contains(pages[0])) Thumbs.ScrollIntoView(pages[0]);
        }

        // ── Header: di chuyển cửa sổ ──────────────────────────────────

        private bool _moving;
        private Point _lastMovePoint;

        private Visual? ParentCanvas => VisualTreeHelper.GetParent(this) as Visual;

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximizeRequested?.Invoke(this);
                e.Handled = true;
                return;
            }
            if (ParentCanvas is not IInputElement parent) return;
            _moving = true;
            _lastMovePoint = e.GetPosition(parent);
            Header.CaptureMouse();
            e.Handled = true;
        }

        private void Header_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_moving || e.LeftButton != MouseButtonState.Pressed || ParentCanvas is not IInputElement parent) return;
            var point = e.GetPosition(parent);
            var delta = point - _lastMovePoint;
            if (delta.Length < 0.5) return;
            _lastMovePoint = point;
            MoveDelta?.Invoke(this, delta);
        }

        private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_moving) return;
            _moving = false;
            Header.ReleaseMouseCapture();
            if (ParentCanvas is IInputElement parent) MoveFinished?.Invoke(this, e.GetPosition(parent));
        }

        private void Resize_DragDelta(object sender, DragDeltaEventArgs e)
            => ResizeDelta?.Invoke(this, new Vector(e.HorizontalChange, e.VerticalChange), (string)((FrameworkElement)sender).Tag);

        private void Save_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this);
        private void Minimize_Click(object sender, RoutedEventArgs e) => MinimizeRequested?.Invoke(this);
        private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximizeRequested?.Invoke(this);
        private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this);

        // ── Kéo cả file (biểu tượng file) ─────────────────────────────

        private Point _fileDragStart;
        private bool _fileDragArmed;

        private void FileHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _fileDragStart = e.GetPosition(FileHandle);
            _fileDragArmed = true;
            FileHandle.CaptureMouse(); // nhận MouseMove kể cả khi chuột rời khỏi biểu tượng nhỏ
            e.Handled = true;
        }

        private void FileHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _fileDragArmed = false;
            FileHandle.ReleaseMouseCapture();
        }

        private void FileHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_fileDragArmed || e.LeftButton != MouseButtonState.Pressed || Group.Pages.Count == 0) return;
            var delta = e.GetPosition(FileHandle) - _fileDragStart;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _fileDragArmed = false;
            FileHandle.ReleaseMouseCapture();
            var data = new DataObject(typeof(ReaderSidePanel.PageDragData),
                new ReaderSidePanel.PageDragData { Source = Group, Pages = Group.Pages.ToList() });
            DragDrop.DoDragDrop(FileHandle, data, DragDropEffects.Move | DragDropEffects.Copy);
        }

        // ── Thumbnail: chọn, kéo trang ────────────────────────────────

        private Point _dragOrigin;
        private ListBoxItem? _dragItem;
        private bool _pendingSingleSelect;
        private ScrollViewer? _thumbScroll;

        private static ListBoxItem? ItemUnder(DependencyObject? source)
        {
            while (source != null && source is not ListBoxItem) source = VisualTreeHelper.GetParent(source);
            return source as ListBoxItem;
        }

        private void Thumb_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragItem = ItemUnder(e.OriginalSource as DependencyObject);
            _dragOrigin = e.GetPosition(Thumbs);
            _pendingSingleSelect = false;
            if (_dragItem is { IsSelected: true } && Thumbs.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
            {
                _pendingSingleSelect = true;
                e.Handled = true;
                Thumbs.Focus();
            }
        }

        private void Thumb_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_pendingSingleSelect && _dragItem != null)
            {
                Thumbs.SelectedItems.Clear();
                _dragItem.IsSelected = true;
            }
            _pendingSingleSelect = false;
            _dragItem = null;
        }

        private void Thumb_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null) return;
            var delta = e.GetPosition(Thumbs) - _dragOrigin;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            var item = _dragItem;
            _dragItem = null;
            _pendingSingleSelect = false;
            if (item.DataContext is PageRow row && !Thumbs.SelectedItems.Contains(row))
            {
                Thumbs.SelectedItems.Clear();
                item.IsSelected = true;
            }
            var pages = SelectedPages;
            if (pages.Count == 0) return;
            var data = new DataObject(typeof(ReaderSidePanel.PageDragData), new ReaderSidePanel.PageDragData { Source = Group, Pages = pages.ToList() });
            try { DragDrop.DoDragDrop(Thumbs, data, DragDropEffects.Move | DragDropEffects.Copy); }
            finally { DropLine.Visibility = Visibility.Collapsed; }
        }

        private void Thumbs_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            var pages = SelectedPages;
            if (pages.Count > 0) DeleteRequested?.Invoke(this, pages);
            e.Handled = true;
        }

        // ── Thả vào cửa sổ ────────────────────────────────────────────

        private int InsertIndexAt(Point point, out Rect line)
        {
            line = Rect.Empty;
            ListBoxItem? best = null;
            Rect bestBounds = Rect.Empty;
            double bestDistance = double.MaxValue;
            foreach (var item in VisualTreeHelpers.FindVisualChildren<ListBoxItem>(Thumbs))
            {
                if (item.DataContext is not PageRow || item.ActualWidth <= 0) continue;
                var topLeft = item.TranslatePoint(new Point(0, 0), Thumbs);
                var bounds = new Rect(topLeft, new Size(item.ActualWidth, item.ActualHeight));
                double dx = Math.Max(Math.Max(bounds.Left - point.X, 0), point.X - bounds.Right);
                double dy = Math.Max(Math.Max(bounds.Top - point.Y, 0), point.Y - bounds.Bottom);
                double distance = dx * dx + dy * dy;
                if (distance < bestDistance) { best = item; bestBounds = bounds; bestDistance = distance; }
            }
            if (best == null) return Group.Pages.Count; // cửa sổ trống / chưa có ô nào: chèn vào cuối
            int index = Group.Pages.IndexOf((PageRow)best.DataContext);
            if (index < 0) return Group.Pages.Count;
            bool before = point.X < bestBounds.Left + bestBounds.Width / 2;
            double x = before ? bestBounds.Left - 1 : bestBounds.Right - 2;
            line = new Rect(x, bestBounds.Top + 4, 3, Math.Max(8, bestBounds.Height - 8));
            return before ? index : index + 1;
        }

        private void Thumb_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(ReaderSidePanel.PageDragData)))
            {
                DropLine.Visibility = Visibility.Collapsed;
                ShowDropGlow(false);
                return;
            }
            e.Effects = (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy : DragDropEffects.Move;
            ShowDropGlow(true);
            var point = e.GetPosition(Thumbs);
            InsertIndexAt(point, out var line);
            if (line.IsEmpty) DropLine.Visibility = Visibility.Collapsed;
            else
            {
                DropLine.Margin = new Thickness(line.X + Thumbs.Margin.Left, line.Y + Thumbs.Margin.Top, 0, 0);
                DropLine.Height = line.Height;
                DropLine.Visibility = Visibility.Visible;
            }
            _thumbScroll ??= VisualTreeHelpers.FindVisualChildren<ScrollViewer>(Thumbs).FirstOrDefault();
            if (_thumbScroll != null)
            {
                if (point.Y < 32) _thumbScroll.ScrollToVerticalOffset(Math.Max(0, _thumbScroll.VerticalOffset - 22));
                else if (point.Y > Thumbs.ActualHeight - 32) _thumbScroll.ScrollToVerticalOffset(_thumbScroll.VerticalOffset + 22);
            }
            e.Handled = true;
        }

        private void Thumb_DragLeave(object sender, DragEventArgs e)
        {
            DropLine.Visibility = Visibility.Collapsed;
            ShowDropGlow(false);
        }

        private void Thumb_Drop(object sender, DragEventArgs e)
        {
            DropLine.Visibility = Visibility.Collapsed;
            ShowDropGlow(false);
            if (e.Data.GetData(typeof(ReaderSidePanel.PageDragData)) is not ReaderSidePanel.PageDragData data) return;
            int index = InsertIndexAt(e.GetPosition(Thumbs), out _);
            bool copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            e.Handled = true;
            PagesDropped?.Invoke(this, data, index, copy);
        }

        // ── Thumbnail theo nhu cầu (như panel Pages) ──────────────────

        private readonly Dictionary<PageRow, System.Threading.CancellationTokenSource> _requests = new();

        private void RequestThumbnail(PageRow row)
        {
            if (row.Thumbnail != null || _requests.ContainsKey(row)) return;
            var cts = new System.Threading.CancellationTokenSource();
            _requests[row] = cts;
            _ = LoadThumbnailAsync(row, cts);
        }

        private async System.Threading.Tasks.Task LoadThumbnailAsync(PageRow row, System.Threading.CancellationTokenSource cts)
        {
            try { await ThumbnailCache.LoadPreviewAsync(row, cts.Token, PdfRenderPriority.Thumbnail); }
            finally
            {
                if (_requests.TryGetValue(row, out var current) && ReferenceEquals(current, cts)) _requests.Remove(row);
                cts.Dispose();
            }
        }

        private void CancelThumbnail(PageRow row)
        {
            if (!_requests.Remove(row, out var cts)) return;
            cts.Cancel();
        }

        private void ThumbImage_Loaded(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is PageRow { Thumbnail: null } row) RequestThumbnail(row);
        }

        private void ThumbImage_Unloaded(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is PageRow row) CancelThumbnail(row);
        }

        private void ThumbImage_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is PageRow old) CancelThumbnail(old);
            if (e.NewValue is PageRow { Thumbnail: null } row && ((FrameworkElement)sender).IsLoaded) RequestThumbnail(row);
        }
    }
}
