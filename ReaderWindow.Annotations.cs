using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using static XTPdfMergeApp.Services.VisualTreeHelpers;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp
{
    /// <summary>
    /// Lớp tương tác CHUNG của 3 công cụ chú thích (Typewriter, Ghi chú, Highlight):
    /// 1. Bắt click/kéo trên trang đang hiển thị ở CẢ 2 chế độ xem (1 trang: ReaderImage; cuộn liên
    ///    tục: vùng vẽ ContinuousPdfView, tự biết trang nào nằm ở đâu) — <see cref="TryHitPage"/>.
    /// 2. Quy đổi vị trí màn hình → toạ độ TRANG HIỂN THỊ chuẩn hoá (u, v): 1 trang thì TranslatePoint vào
    ///    chính ReaderImage (tự tính cả zoom/xoay khung nhìn/cuộn), cuộn liên tục thì theo khung trang của
    ///    ContinuousPdfView. Đổi tiếp sang toạ độ PDF (point, theo
    ///    CropBox + /Rotate thật của trang) ở <see cref="PdfPageGeometry"/> lúc ghi file.
    /// 3. Mọi thao tác kết thúc bằng 1 callback chung <see cref="CommitAnnotationChange"/> → host ghi
    ///    annotation bằng iText + đưa vào Undo/Redo → trang render lại ngay.
    /// </summary>
    public partial class ReaderWindow
    {
        private enum ReaderTool { Hand, Typewriter, Comment, Highlight }

        private ReaderTool _readerTool = ReaderTool.Hand;

        /// <summary>1 điểm trên 1 trang đang hiển thị: (U, V) chuẩn hoá 0..1, gốc trên-trái của trang.</summary>
        private readonly record struct PageHit(PageRow Row, double U, double V);

        private sealed record PageAnnotations(PdfPageGeometry Geometry, IReadOnlyList<QuickAnnotationSpec> Annotations);

        private readonly Dictionary<(string Path, int Page), PageAnnotations> _pageAnnotationCache = new();
        private readonly Dictionary<(string Path, int Page), Task<PageAnnotations?>> _pageAnnotationLoads = new();

        // ── Chọn công cụ ────────────────────────────────────────────────

        private void ReaderHandTool_Click(object sender, RoutedEventArgs e) => SetReaderTool(ReaderTool.Hand);
        private void ReaderTypewriterTool_Click(object sender, RoutedEventArgs e) => ToggleReaderTool(ReaderTool.Typewriter);
        private void ReaderCommentTool_Click(object sender, RoutedEventArgs e) => ToggleReaderTool(ReaderTool.Comment);
        private void ReaderHighlightTool_Click(object sender, RoutedEventArgs e) => ToggleReaderTool(ReaderTool.Highlight);

        private void ToggleReaderTool(ReaderTool tool)
            => SetReaderTool(_readerTool == tool ? ReaderTool.Hand : tool);

        private void SetReaderTool(ReaderTool tool)
        {
            CommitAnnotationEditor();
            CancelHighlightDrag();
            _readerTool = tool;
            ReaderHandToolButton.Tag = tool == ReaderTool.Hand ? "Active" : null;
            ReaderTypewriterToolButton.Tag = tool == ReaderTool.Typewriter ? "Active" : null;
            ReaderCommentToolButton.Tag = tool == ReaderTool.Comment ? "Active" : null;
            ReaderHighlightToolButton.Tag = tool == ReaderTool.Highlight ? "Active" : null;

            // ForceCursor: con trỏ của vùng xem đè lên Cursor="Hand" sẵn có của ReaderImage.
            ReaderContentHost.Cursor = tool switch
            {
                ReaderTool.Typewriter => Cursors.IBeam,
                ReaderTool.Comment => Cursors.Pen,
                ReaderTool.Highlight => Cursors.Cross,
                _ => null
            };
            ReaderContentHost.ForceCursor = tool != ReaderTool.Hand;
        }

        private void ReaderDeletePages_Click(object sender, RoutedEventArgs e)
        {
            var pages = GetEditTargetPages();
            if (_readerGroup == null || pages.Count == 0 || EditHost == null) return;
            EditHost.DeletePages(_readerGroup, pages);
        }

        // ── Bắt vị trí trên trang ───────────────────────────────────────

        private bool TryHitPage(Point pointInHost, out PageHit hit)
        {
            hit = default;
            if (_readerContinuousMode)
            {
                if (!ReaderContinuousView.IsVisible) return false;
                Point p = ReaderContentHost.TranslatePoint(pointInHost, ReaderContinuousView.Surface);
                if (!ReaderContinuousView.TryHitPage(p, out var row, out double u, out double v) || row == null) return false;
                hit = new PageHit(row, u, v);
                return true;
            }

            var result = VisualTreeHelper.HitTest(ReaderContentHost, pointInHost);
            for (DependencyObject? node = result?.VisualHit; node != null && !ReferenceEquals(node, ReaderContentHost);
                 node = VisualTreeHelper.GetParent(node))
            {
                if (node is not Image image) continue;
                return ReferenceEquals(image, ReaderImage) && _readerPage != null &&
                       TryGetPagePoint(_readerPage, pointInHost, clamp: false, out hit);
            }
            return false;
        }

        private bool TryGetPagePoint(PageRow row, Point pointInHost, bool clamp, out PageHit hit)
        {
            hit = default;
            if (!TryHostToPage(row, pointInHost, out double u, out double v)) return false;
            if (clamp) { u = Math.Clamp(u, 0, 1); v = Math.Clamp(v, 0, 1); }
            else if (u is < 0 or > 1 || v is < 0 or > 1) return false;
            hit = new PageHit(row, u, v);
            return true;
        }

        /// <summary>Điểm trong ReaderContentHost → (u, v) trên trang (chưa kẹp 0..1). False nếu trang không đang hiển thị.</summary>
        private bool TryHostToPage(PageRow row, Point pointInHost, out double u, out double v)
        {
            u = v = 0;
            if (_readerContinuousMode)
            {
                if (!ReaderContinuousView.IsVisible || !ReaderContinuousView.TryGetPageRect(row, out Rect rect) ||
                    rect.Width <= 0 || rect.Height <= 0) return false;
                Point p = ReaderContentHost.TranslatePoint(pointInHost, ReaderContinuousView.Surface);
                u = (p.X - rect.X) / rect.Width;
                v = (p.Y - rect.Y) / rect.Height;
                return true;
            }
            var image = ReaderImage;
            if (!ReferenceEquals(row, _readerPage) || image.Source == null ||
                image.ActualWidth <= 0 || image.ActualHeight <= 0 || !image.IsVisible) return false;
            Point local = ReaderContentHost.TranslatePoint(pointInHost, image);
            u = local.X / image.ActualWidth;
            v = local.Y / image.ActualHeight;
            return true;
        }

        /// <summary>Điểm (u, v) trên trang → toạ độ trong ReaderInteractionLayer. False nếu trang không đang hiển thị.</summary>
        private bool TryPageToLayer(PageRow row, double u, double v, out Point point)
        {
            point = default;
            if (_readerContinuousMode)
            {
                if (!ReaderContinuousView.IsVisible || !ReaderContinuousView.TryGetPageRect(row, out Rect rect)) return false;
                point = ReaderContinuousView.Surface.TranslatePoint(
                    new Point(rect.X + u * rect.Width, rect.Y + v * rect.Height), ReaderInteractionLayer);
                return true;
            }
            var image = ReaderImage;
            if (!ReferenceEquals(row, _readerPage) || image.Source == null || !image.IsVisible) return false;
            point = image.TranslatePoint(new Point(u * image.ActualWidth, v * image.ActualHeight), ReaderInteractionLayer);
            return true;
        }

        // ── Cache annotation theo trang (hit-test ghi chú đã có, cỡ trang thật) ─────

        private PageAnnotations? GetCachedPageAnnotations(PageRow row)
        {
            var key = (row.SourcePath, row.PageNumber);
            if (_pageAnnotationCache.TryGetValue(key, out var cached)) return cached;
            _ = LoadPageAnnotationsAsync(row);
            return null;
        }

        private Task<PageAnnotations?> LoadPageAnnotationsAsync(PageRow row)
        {
            var key = (row.SourcePath, row.PageNumber);
            if (_pageAnnotationCache.TryGetValue(key, out var cached)) return Task.FromResult<PageAnnotations?>(cached);
            if (_pageAnnotationLoads.TryGetValue(key, out var pending)) return pending;

            var task = LoadPageAnnotationsCoreAsync(key, _annotationCacheGeneration);
            _pageAnnotationLoads[key] = task;
            return task;
        }

        /// <summary>Tăng mỗi lần file bị sửa — kết quả đọc xong sau thời điểm đó là dữ liệu cũ, không cache.</summary>
        private long _annotationCacheGeneration;

        private async Task<PageAnnotations?> LoadPageAnnotationsCoreAsync((string Path, int Page) key, long generation)
        {
            try
            {
                var (geometry, annotations) = await Task.Run(() => PdfQuickAnnotationService.ReadPage(key.Path, key.Page));
                var page = new PageAnnotations(geometry, annotations);
                if (generation == _annotationCacheGeneration) _pageAnnotationCache[key] = page;
                return page;
            }
            catch
            {
                return null; // file đang được ghi/đọc lỗi — lần rê chuột sau thử lại
            }
            finally
            {
                if (generation == _annotationCacheGeneration) _pageAnnotationLoads.Remove(key);
            }
        }

        private void InvalidateAnnotationCache(string path, IReadOnlyCollection<int> pages)
        {
            bool Matches((string Path, int Page) key) =>
                pages.Contains(key.Page) && string.Equals(key.Path, path, StringComparison.OrdinalIgnoreCase);
            foreach (var key in _pageAnnotationCache.Keys.Where(Matches).ToList()) _pageAnnotationCache.Remove(key);
            foreach (var key in _pageAnnotationLoads.Keys.Where(Matches).ToList()) _pageAnnotationLoads.Remove(key);
            _annotationCacheGeneration++;
        }

        private static QuickAnnotationSpec? FindAnnotationAt(PageAnnotations? page, PageHit hit, params QuickAnnotationKind[] kinds)
            => page?.Annotations.LastOrDefault(a => kinds.Contains(a.Kind) && a.Contains(hit.U, hit.V));

        // ── Chuột trên vùng xem ─────────────────────────────────────────

        private void ReaderContentHost_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Click ra ngoài ô nhập = xong (Image không nhận focus nên LostKeyboardFocus không tự bắn).
            if (_annotationEditor != null)
            {
                if (ReaderAnnotationEditor.IsMouseOver) return;
                CommitAnnotationEditor();
                e.Handled = true;
                return;
            }

            if (!TryHitPage(e.GetPosition(ReaderContentHost), out var hit)) return;
            var page = GetCachedPageAnnotations(hit.Row);

            switch (_readerTool)
            {
                case ReaderTool.Hand:
                    // Bấm icon ghi chú khi đang ở Hand = mở ra sửa; chỗ khác vẫn kéo cuộn như cũ.
                    if (e.ClickCount == 1 && FindAnnotationAt(page, hit, QuickAnnotationKind.Comment) is { } note)
                    {
                        e.Handled = true;
                        _ = OpenAnnotationEditorAsync(hit, QuickAnnotationKind.Comment, note);
                    }
                    break;

                case ReaderTool.Typewriter:
                    e.Handled = true;
                    _ = OpenAnnotationEditorAsync(hit, QuickAnnotationKind.Typewriter,
                        FindAnnotationAt(page, hit, QuickAnnotationKind.Typewriter));
                    break;

                case ReaderTool.Comment:
                    e.Handled = true;
                    _ = OpenAnnotationEditorAsync(hit, QuickAnnotationKind.Comment,
                        FindAnnotationAt(page, hit, QuickAnnotationKind.Comment));
                    break;

                case ReaderTool.Highlight:
                    e.Handled = true;
                    BeginHighlightDrag(hit);
                    break;
            }
        }

        private void ReaderContentHost_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            Point point = e.GetPosition(ReaderContentHost);
            if (_highlightDrag is { } drag)
            {
                UpdateHighlightDrag(drag, point);
                e.Handled = true;
                return;
            }

            UpdateCommentHover(point);
        }

        private void ReaderContentHost_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_highlightDrag is not { } drag) return;
            e.Handled = true;
            FinishHighlightDrag(drag, e.GetPosition(ReaderContentHost));
        }

        private void ReaderContentHost_MouseLeave(object sender, MouseEventArgs e)
            => ReaderCommentHoverPopup.Visibility = Visibility.Collapsed;

        // ── Ghi chú: hiện nội dung khi rê chuột lên icon ─────────────────

        private void UpdateCommentHover(Point pointInHost)
        {
            QuickAnnotationSpec? note = null;
            if (_annotationEditor == null && TryHitPage(pointInHost, out var hit))
                note = FindAnnotationAt(GetCachedPageAnnotations(hit.Row), hit, QuickAnnotationKind.Comment);

            if (note == null || string.IsNullOrWhiteSpace(note.Text))
            {
                ReaderCommentHoverPopup.Visibility = Visibility.Collapsed;
                return;
            }

            ReaderCommentHoverText.Text = note.Text;
            ReaderCommentHoverPopup.Visibility = Visibility.Visible;
            ReaderCommentHoverPopup.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double left = Math.Min(pointInHost.X + 14, Math.Max(0, ReaderContentHost.ActualWidth - ReaderCommentHoverPopup.DesiredSize.Width - 4));
            double top = Math.Min(pointInHost.Y + 16, Math.Max(0, ReaderContentHost.ActualHeight - ReaderCommentHoverPopup.DesiredSize.Height - 4));
            Canvas.SetLeft(ReaderCommentHoverPopup, left);
            Canvas.SetTop(ReaderCommentHoverPopup, top);
        }

        // ── Highlight: kéo 1 hình chữ nhật ──────────────────────────────

        private sealed record HighlightDrag(PageRow Row, double StartU, double StartV);
        private HighlightDrag? _highlightDrag;

        private void BeginHighlightDrag(PageHit hit)
        {
            if (!TryPageToLayer(hit.Row, hit.U, hit.V, out Point start)) return;
            _highlightDrag = new HighlightDrag(hit.Row, hit.U, hit.V);
            ReaderCommentHoverPopup.Visibility = Visibility.Collapsed;
            ReaderContentHost.CaptureMouse();
            Canvas.SetLeft(ReaderHighlightRubberBand, start.X);
            Canvas.SetTop(ReaderHighlightRubberBand, start.Y);
            ReaderHighlightRubberBand.Width = ReaderHighlightRubberBand.Height = 0;
            ReaderHighlightRubberBand.Visibility = Visibility.Visible;
        }

        private void UpdateHighlightDrag(HighlightDrag drag, Point pointInHost)
        {
            if (!TryGetPagePoint(drag.Row, pointInHost, clamp: true, out var current)) return;
            if (!TryPageToLayer(drag.Row, drag.StartU, drag.StartV, out Point a) ||
                !TryPageToLayer(drag.Row, current.U, current.V, out Point b)) return;
            Canvas.SetLeft(ReaderHighlightRubberBand, Math.Min(a.X, b.X));
            Canvas.SetTop(ReaderHighlightRubberBand, Math.Min(a.Y, b.Y));
            ReaderHighlightRubberBand.Width = Math.Abs(a.X - b.X);
            ReaderHighlightRubberBand.Height = Math.Abs(a.Y - b.Y);
        }

        private void FinishHighlightDrag(HighlightDrag drag, Point pointInHost)
        {
            bool haveEnd = TryGetPagePoint(drag.Row, pointInHost, clamp: true, out var end);
            bool bigEnough = ReaderHighlightRubberBand.Width >= 4 && ReaderHighlightRubberBand.Height >= 4;
            CancelHighlightDrag();
            if (!haveEnd || !bigEnough) return;

            var spec = new QuickAnnotationSpec(NewAnnotationName(), QuickAnnotationKind.Highlight, drag.Row.PageNumber,
                Math.Min(drag.StartU, end.U), Math.Min(drag.StartV, end.V),
                Math.Max(drag.StartU, end.U), Math.Max(drag.StartV, end.V), "");
            CommitAnnotationChange(drag.Row, new QuickAnnotationChange(null, spec), "Highlight");
        }

        private void CancelHighlightDrag()
        {
            if (_highlightDrag == null) return;
            _highlightDrag = null;
            ReaderHighlightRubberBand.Visibility = Visibility.Collapsed;
            if (ReaderContentHost.IsMouseCaptured) ReaderContentHost.ReleaseMouseCapture();
        }

        // ── Typewriter / Ghi chú: ô nhập chữ ngay tại chỗ ────────────────

        private sealed class AnnotationEditorState
        {
            public required PageRow Row { get; init; }
            public required QuickAnnotationKind Kind { get; init; }
            public required double U { get; init; }
            public required double V { get; init; }
            public required PdfPageGeometry Geometry { get; init; }
            public QuickAnnotationSpec? Existing { get; init; }
        }

        private AnnotationEditorState? _annotationEditor;
        private const double CommentIconPoints = 20;

        private async Task OpenAnnotationEditorAsync(PageHit hit, QuickAnnotationKind kind, QuickAnnotationSpec? existing)
        {
            ReaderCommentHoverPopup.Visibility = Visibility.Collapsed;
            var page = await LoadPageAnnotationsAsync(hit.Row);
            if (page == null || _annotationEditor != null) return;

            // Sửa cái đã có: neo đúng góc trên-trái của nó. Tạo mới: neo tại điểm bấm.
            _annotationEditor = new AnnotationEditorState
            {
                Row = hit.Row,
                Kind = kind,
                U = existing?.U1 ?? hit.U,
                V = existing?.V1 ?? hit.V,
                Geometry = page.Geometry,
                Existing = existing
            };

            var editor = ReaderAnnotationEditor;
            editor.Text = existing?.Text ?? "";
            if (kind == QuickAnnotationKind.Comment)
            {
                editor.FontFamily = FontFamily;
                editor.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xC4));
                editor.FontSize = 13;
                editor.TextWrapping = TextWrapping.Wrap;
                editor.Width = 240;
                editor.MinHeight = 72;
            }
            else
            {
                // Cùng họ font với chữ sẽ ghi vào PDF (Arial) để khi Enter chữ không "nhảy".
                editor.FontFamily = new FontFamily("Arial");
                editor.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
                editor.TextWrapping = TextWrapping.NoWrap;
                editor.Width = double.NaN;
                editor.MinHeight = 0;
            }
            editor.Visibility = Visibility.Visible;
            PositionAnnotationEditor();
            editor.CaretIndex = editor.Text.Length;
            editor.Focus();
            // Lần nhả chuột ngay sau cú bấm có thể kéo focus về vùng xem — focus lại sau khi input xong.
            _ = Dispatcher.InvokeAsync(() => { if (_annotationEditor != null) editor.Focus(); },
                System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>Đặt lại ô nhập theo vị trí/zoom hiện tại của trang (gọi mỗi lần layout đổi: cuộn, zoom).</summary>
        private void PositionAnnotationEditor()
        {
            if (_annotationEditor is not { } state ||
                !TryPageToLayer(state.Row, state.U, state.V, out Point anchor) ||
                !TryPageToLayer(state.Row, state.U + 1.0 / Math.Max(1, state.Geometry.DisplayWidth), state.V, out Point right)) return;

            double pixelsPerPoint = Math.Max(0.1, (right - anchor).Length);

            double left, top;
            if (state.Kind == QuickAnnotationKind.Typewriter)
            {
                // Khớp cỡ chữ + lề 2pt của annotation sẽ ghi → chữ gõ nằm đúng chỗ chữ sau khi ghi.
                ReaderAnnotationEditor.FontSize = Math.Max(8, PdfQuickAnnotationService.TypewriterFontSize * pixelsPerPoint);
                left = anchor.X;
                top = anchor.Y;
            }
            else
            {
                // Ghi chú: ô nhập mở ngay bên phải vị trí icon.
                left = anchor.X + CommentIconPoints * pixelsPerPoint + 4;
                top = anchor.Y;
            }

            if (Math.Abs(Canvas.GetLeft(ReaderAnnotationEditor) - left) > 0.5 || double.IsNaN(Canvas.GetLeft(ReaderAnnotationEditor)))
                Canvas.SetLeft(ReaderAnnotationEditor, left);
            if (Math.Abs(Canvas.GetTop(ReaderAnnotationEditor) - top) > 0.5 || double.IsNaN(Canvas.GetTop(ReaderAnnotationEditor)))
                Canvas.SetTop(ReaderAnnotationEditor, top);
        }

        private void ReaderContentHost_LayoutUpdated(object? sender, EventArgs e)
        {
            if (_annotationEditor != null) PositionAnnotationEditor();
        }

        private void ReaderAnnotationEditor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                CommitAnnotationEditor();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CommitAnnotationEditor(cancel: true);
            }
        }

        private void ReaderAnnotationEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
            => CommitAnnotationEditor();

        /// <summary>Đóng ô nhập. Rỗng khi tạo mới = bỏ; xoá hết chữ của cái đã có = xoá annotation đó.</summary>
        private void CommitAnnotationEditor(bool cancel = false)
        {
            if (_annotationEditor is not { } state) return;
            _annotationEditor = null; // trước khi ẩn: ẩn làm mất focus → LostKeyboardFocus gọi lại hàm này
            string text = ReaderAnnotationEditor.Text.TrimEnd();
            ReaderAnnotationEditor.Visibility = Visibility.Collapsed;
            Focus();
            if (cancel) return;

            QuickAnnotationChange change;
            string description;
            string label = state.Kind == QuickAnnotationKind.Comment ? "ghi chú" : "typewriter";
            if (state.Existing is not { } existing)
            {
                if (text.Length == 0) return;
                change = new QuickAnnotationChange(null, new QuickAnnotationSpec(NewAnnotationName(), state.Kind,
                    state.Row.PageNumber, state.U, state.V, state.U, state.V, text));
                description = "Thêm " + label;
            }
            else if (text == existing.Text)
            {
                return;
            }
            else if (text.Length == 0)
            {
                change = new QuickAnnotationChange(existing, null);
                description = "Xoá " + label;
            }
            else
            {
                // Annotation do app khác tạo (không có /NM) nhận tên mới khi được ghi lại.
                string name = existing.Name.StartsWith('#') ? NewAnnotationName() : existing.Name;
                change = new QuickAnnotationChange(existing, existing with { Name = name, Text = text });
                description = "Sửa " + label;
            }
            CommitAnnotationChange(state.Row, change, description);
        }

        /// <summary>Callback chung của cả 3 công cụ: đưa thay đổi cho host ghi vào file + Undo/Redo.</summary>
        private void CommitAnnotationChange(PageRow row, QuickAnnotationChange change, string description)
        {
            if (EditHost == null) return;
            _ = EditHost.ApplyAnnotationChangesAsync(row.SourcePath, new[] { change }, description);
        }

        private static string NewAnnotationName() => "xt-" + Guid.NewGuid().ToString("N");
    }
}
