using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XTPdfMergeApp.Controls;
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
    /// 3. Mọi thao tác kết thúc bằng 1 callback chung <see cref="CommitAnnotationChange"/> → host đưa thay đổi vào
    ///    <see cref="AnnotationStore"/> (bộ nhớ, có Undo/Redo) → lớp chú thích vẽ lại ngay; file chỉ được ghi khi Save.
    /// </summary>
    public partial class ReaderWindow
    {
        private enum ReaderTool { Hand, Select, Typewriter, Comment, Highlight, Underline, Strikethrough, Stamp, Shape, SnapShot }

        private ReaderTool _readerTool = ReaderTool.Hand;

        /// <summary>1 điểm trên 1 trang đang hiển thị: (U, V) chuẩn hoá 0..1, gốc trên-trái của trang.</summary>
        private readonly record struct PageHit(PageRow Row, double U, double V);

        // ── Chọn công cụ ────────────────────────────────────────────────

        private void ReaderHandTool_Click(object sender, RoutedEventArgs e) => SetReaderTool(ReaderTool.Hand);
        private void ReaderSelectTool_Click(object sender, RoutedEventArgs e) => ToggleReaderTool(ReaderTool.Select);
        private void ReaderTypewriterTool_Click(object sender, RoutedEventArgs e) => ToggleReaderTool(ReaderTool.Typewriter);
        private void ReaderCommentTool_Click(object sender, RoutedEventArgs e) => ToggleReaderTool(ReaderTool.Comment);
        private void ReaderUnderlineTool_Click(object sender, RoutedEventArgs e) => ToggleReaderTool(ReaderTool.Underline);
        private void ReaderStrikethroughTool_Click(object sender, RoutedEventArgs e) => ToggleReaderTool(ReaderTool.Strikethrough);
        private void ReaderSnapShotTool_Click(object sender, RoutedEventArgs e) => ToggleReaderTool(ReaderTool.SnapShot);

        /// <summary>Highlight "Text"/"Area" are 2 separate buttons now (Foxit shows both, not 1 button + a mode toggle bar).</summary>
        private void ReaderHighlightText_Click(object sender, RoutedEventArgs e) => SelectHighlightMode("Text");
        private void ReaderHighlightArea_Click(object sender, RoutedEventArgs e) => SelectHighlightMode("Area");

        private void SelectHighlightMode(string mode)
        {
            if (_readerTool == ReaderTool.Highlight && AppSettings.HighlightMode == mode) { SetReaderTool(ReaderTool.Hand); return; }
            AppSettings.HighlightMode = mode;
            SetReaderTool(ReaderTool.Highlight);
        }

        /// <summary>Mỗi loại hình vẽ 1 nút riêng (kiểu Foxit), không cần bung popup chọn trước.</summary>
        private void ReaderShapeRect_Click(object sender, RoutedEventArgs e) => SelectShapeType(ShapeStyle.Rect);
        private void ReaderShapeCloud_Click(object sender, RoutedEventArgs e) => SelectShapeType(ShapeStyle.Cloud);
        private void ReaderShapeOval_Click(object sender, RoutedEventArgs e) => SelectShapeType(ShapeStyle.Oval);
        private void ReaderShapeArrow_Click(object sender, RoutedEventArgs e) => SelectShapeType(ShapeStyle.Arrow);
        private void ReaderShapeLine_Click(object sender, RoutedEventArgs e) => SelectShapeType(ShapeStyle.Line);

        private void SelectShapeType(string type)
        {
            if (_readerTool == ReaderTool.Shape && _shapeStyle.Type == type) { SetReaderTool(ReaderTool.Hand); return; }
            _shapeStyle = _shapeStyle with { Type = type };
            SetReaderTool(ReaderTool.Shape);
        }

        private void ToggleReaderTool(ReaderTool tool)
            => SetReaderTool(_readerTool == tool ? ReaderTool.Hand : tool);

        private void SetReaderTool(ReaderTool tool)
        {
            CommitAnnotationEditor();
            CancelHighlightDrag();
            CancelShapeDrag();
            _readerTool = tool;
            if (tool != ReaderTool.Hand) SelectAnnotation(null, null);
            UpdateFormatBarVisibility();
            ReaderHandToolButton.Tag = tool == ReaderTool.Hand ? "Active" : null;
            ReaderSelectToolButton.Tag = tool == ReaderTool.Select ? "Active" : null;
            ReaderTypewriterToolButton.Tag = tool == ReaderTool.Typewriter ? "Active" : null;
            ReaderCommentToolButton.Tag = tool == ReaderTool.Comment ? "Active" : null;
            ReaderHighlightTextButton.Tag = tool == ReaderTool.Highlight && AppSettings.HighlightMode != "Area" ? "Active" : null;
            ReaderHighlightAreaButton.Tag = tool == ReaderTool.Highlight && AppSettings.HighlightMode == "Area" ? "Active" : null;
            ReaderUnderlineToolButton.Tag = tool == ReaderTool.Underline ? "Active" : null;
            ReaderStrikethroughToolButton.Tag = tool == ReaderTool.Strikethrough ? "Active" : null;
            ReaderStampToolButton.Tag = tool == ReaderTool.Stamp ? "Active" : null;
            ReaderShapeRectButton.Tag = tool == ReaderTool.Shape && _shapeStyle.Type == ShapeStyle.Rect ? "Active" : null;
            ReaderShapeCloudButton.Tag = tool == ReaderTool.Shape && _shapeStyle.Type == ShapeStyle.Cloud ? "Active" : null;
            ReaderShapeOvalButton.Tag = tool == ReaderTool.Shape && _shapeStyle.Type == ShapeStyle.Oval ? "Active" : null;
            ReaderShapeArrowButton.Tag = tool == ReaderTool.Shape && _shapeStyle.Type == ShapeStyle.Arrow ? "Active" : null;
            ReaderShapeLineButton.Tag = tool == ReaderTool.Shape && _shapeStyle.Type == ShapeStyle.Line ? "Active" : null;
            ReaderSnapShotToolButton.Tag = tool == ReaderTool.SnapShot ? "Active" : null;
            if (tool != ReaderTool.Select) ClearTextSelection();

            // ForceCursor: con trỏ của vùng xem đè lên Cursor="Hand" sẵn có của ReaderImage.
            ReaderContentHost.Cursor = tool switch
            {
                ReaderTool.Select => Cursors.IBeam,
                ReaderTool.Typewriter => Cursors.IBeam,
                ReaderTool.Comment => Cursors.Pen,
                ReaderTool.Highlight or ReaderTool.Underline or ReaderTool.Strikethrough => Cursors.Cross,
                ReaderTool.Stamp => Cursors.Cross,
                ReaderTool.Shape => Cursors.Cross,
                ReaderTool.SnapShot => Cursors.Cross,
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
            if (!ReaderContinuousView.IsVisible) return false;
            Point p = ReaderContentHost.TranslatePoint(pointInHost, ReaderContinuousView.Surface);
            if (!ReaderContinuousView.TryHitPage(p, out var row, out double u, out double v) || row == null) return false;
            hit = new PageHit(row, u, v);
            return true;
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
            if (!ReaderContinuousView.IsVisible) return false;
            Point p = ReaderContentHost.TranslatePoint(pointInHost, ReaderContinuousView.Surface);
            return ReaderContinuousView.TryViewToPage(row, p, out u, out v);
        }

        /// <summary>Điểm (u, v) trên trang → toạ độ trong ReaderInteractionLayer. False nếu trang không đang hiển thị.</summary>
        private bool TryPageToLayer(PageRow row, double u, double v, out Point point)
        {
            point = default;
            if (!ReaderContinuousView.IsVisible || !ReaderContinuousView.TryPageToView(row, u, v, out Point p)) return false;
            point = ReaderContinuousView.Surface.TranslatePoint(p, ReaderInteractionLayer);
            return true;
        }

        // ── Chú thích của trang (AnnotationStore: file + thay đổi chưa lưu) ─────

        /// <summary>Chú thích của trang như đang hiển thị, null khi trang đang được đọc (AnnotationStore.Changed báo khi xong).</summary>
        private static PageAnnotations? GetCachedPageAnnotations(PageRow row) => AnnotationStore.TryGetPage(row.SourcePath, row.PageNumber);

        private static Task<PageAnnotations?> LoadPageAnnotationsAsync(PageRow row) => AnnotationStore.GetPageAsync(row.SourcePath, row.PageNumber);

        /// <summary>Chú thích đổi (sửa, undo, đọc xong, ảnh appearance xong): vẽ lại khung xem + thumbnail, khung chọn bám theo.</summary>
        private void OnAnnotationsChanged(string path, int page)
        {
            ReaderContinuousView.Redraw();
            ReaderSidePanel.OnAnnotationsChanged(path, page);
            if (_selAnn != null && _annMove == null)
            {
                RefreshSelectionFromCache();
                UpdateSelectionVisual();
            }
        }

        /// <summary>Nội dung/kiểu đổi: chú thích được vẽ và lưu lại từ spec (chú thích của file không có /NM nhận tên mới).</summary>
        private static QuickAnnotationSpec Regenerated(QuickAnnotationSpec spec)
            => spec with { ObjectNumber = 0, Generation = 0, Name = spec.Name.StartsWith('#') ? NewAnnotationName() : spec.Name };

        private static QuickAnnotationSpec? FindAnnotationAt(PageAnnotations? page, PageHit hit, params QuickAnnotationKind[] kinds)
            => page?.Annotations.LastOrDefault(a => a.Selectable && kinds.Contains(a.Kind) && a.Contains(hit.U, hit.V));

        // ── Chuột trên vùng xem ─────────────────────────────────────────

        private void ReaderContentHost_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Thanh Find / thanh định dạng nằm đè lên trang: bấm vào chúng không được coi là bấm lên trang.
            if (IsOverlayBar(e.OriginalSource as DependencyObject)) return;
            // Grip resize nằm ngay trên hình đã chọn: để nguyên (không Handled) cho Grip_MouseDown của chính nó xử lý,
            // vì Preview đi từ gốc xuống — handler này chạy TRƯỚC handler của grip nếu không trả sớm ở đây.
            if (e.OriginalSource is System.Windows.Shapes.Rectangle { Tag: string tag } && Array.IndexOf(new[] { "NW", "N", "NE", "E", "SE", "S", "SW", "W" }, tag) >= 0) return;

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
                    // Bấm lên annotation = chọn (kéo = di chuyển, bấm đúp = sửa chữ, Delete = xoá); chỗ khác vẫn kéo cuộn như cũ.
                    if (PickAnnotation(page, hit) is { } picked)
                    {
                        e.Handled = true;
                        Focus();
                        SelectAnnotation(hit.Row, picked);
                        if (e.ClickCount >= 2 && picked.Kind is QuickAnnotationKind.Typewriter or QuickAnnotationKind.Comment)
                            _ = OpenAnnotationEditorAsync(hit, picked.Kind, picked);
                        else
                            BeginAnnotationMove(hit, picked);
                    }
                    else SelectAnnotation(null, null);
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
                    BeginHighlightDrag(hit, QuickAnnotationKind.Highlight);
                    break;

                case ReaderTool.Underline:
                    e.Handled = true;
                    BeginHighlightDrag(hit, QuickAnnotationKind.Underline);
                    break;

                case ReaderTool.Strikethrough:
                    e.Handled = true;
                    BeginHighlightDrag(hit, QuickAnnotationKind.StrikeOut);
                    break;

                case ReaderTool.Stamp:
                    e.Handled = true;
                    _ = PlaceStampAsync(hit);
                    break;

                case ReaderTool.Shape:
                    e.Handled = true;
                    BeginShapeDrag(hit);
                    break;

                case ReaderTool.Select:
                    e.Handled = true;
                    BeginTextSelectionDrag(hit);
                    break;

                case ReaderTool.SnapShot:
                    e.Handled = true;
                    BeginSnapshotDrag(hit);
                    break;
            }
        }

        private void ReaderContentHost_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            Point point = e.GetPosition(ReaderContentHost);
            if (UpdateAnnotationMove(point) || UpdateShapeDrag(point) || UpdateShapeResize(point))
            {
                e.Handled = true;
                return;
            }
            if (_highlightDrag is { } drag)
            {
                UpdateHighlightDrag(drag, point);
                e.Handled = true;
                return;
            }
            if (_textSelDrag is { } selDrag)
            {
                UpdateTextSelectionDrag(selDrag, point);
                e.Handled = true;
                return;
            }
            if (_snapshotDrag is { } snapDrag)
            {
                UpdateSnapshotDrag(snapDrag, point);
                e.Handled = true;
                return;
            }

            UpdateCommentHover(point);
        }

        private void ReaderContentHost_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (FinishAnnotationMove() || FinishShapeDrag(e.GetPosition(ReaderContentHost)) || FinishShapeResize())
            {
                e.Handled = true;
                return;
            }
            if (_highlightDrag is { } drag)
            {
                e.Handled = true;
                FinishHighlightDrag(drag, e.GetPosition(ReaderContentHost));
                return;
            }
            if (_textSelDrag is { } selDrag)
            {
                e.Handled = true;
                _ = FinishTextSelectionDragAsync(selDrag, e.GetPosition(ReaderContentHost));
                return;
            }
            if (_snapshotDrag is { } snapDrag)
            {
                e.Handled = true;
                FinishSnapshotDrag(snapDrag, e.GetPosition(ReaderContentHost));
            }
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

        private sealed record HighlightDrag(PageRow Row, double StartU, double StartV, QuickAnnotationKind Kind);
        private HighlightDrag? _highlightDrag;

        private void BeginHighlightDrag(PageHit hit, QuickAnnotationKind kind)
        {
            if (!TryPageToLayer(hit.Row, hit.U, hit.V, out Point start)) return;
            _highlightDrag = new HighlightDrag(hit.Row, hit.U, hit.V, kind);
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
            // Underline/strikethrough only make sense on real text; Area mode applies to Highlight only.
            if (drag.Kind != QuickAnnotationKind.Highlight || AppSettings.HighlightMode != "Area")
            {
                _ = CommitTextMarkupAsync(drag, end.U, end.V, drag.Kind);
                return;
            }

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
                editor.FontWeight = FontWeights.Normal;
                editor.FontStyle = FontStyles.Normal;
                editor.Foreground = Brushes.Black;
                editor.FontFamily = FontFamily;
                editor.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xC4));
                editor.FontSize = 13;
                editor.TextWrapping = TextWrapping.Wrap;
                editor.Width = 240;
                editor.MinHeight = 72;
            }
            else
            {
                // Cùng font/kiểu/màu với chữ sẽ ghi vào PDF để khi Enter chữ không "nhảy".
                if (existing != null) _textFormat = TextFormat.Decode(existing.Format);
                LoadFormatBar();
                ApplyEditorFormat();
                editor.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
                editor.TextWrapping = TextWrapping.NoWrap;
                editor.Width = double.NaN;
                editor.MinHeight = 0;
            }
            if (existing != null && kind == QuickAnnotationKind.Typewriter)
            {
                AnnotationLayer.Edit.HiddenName = existing.Name;
                ReaderContinuousView.Redraw();
            }
            editor.Visibility = Visibility.Visible;
            UpdateFormatBarVisibility();
            UpdateSelectionVisual();
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
                ReaderAnnotationEditor.FontSize = Math.Max(6, _textFormat.Size * pixelsPerPoint);
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
            else if (_selAnn != null)
            {
                RefreshSelectionFromCache();
                UpdateSelectionVisual();
            }
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
            if (AnnotationLayer.Edit.HiddenName != null)
            {
                AnnotationLayer.Edit.HiddenName = null;
                ReaderContinuousView.Redraw();
            }
            Focus();
            UpdateFormatBarVisibility();
            UpdateSelectionVisual();
            if (cancel) return;

            string format = state.Kind == QuickAnnotationKind.Typewriter ? _textFormat.Encode() : "";
            QuickAnnotationChange change;
            string description;
            string label = state.Kind == QuickAnnotationKind.Comment ? "note" : "typewriter";
            if (state.Existing is not { } existing)
            {
                if (text.Length == 0) return;
                change = new QuickAnnotationChange(null, PdfQuickAnnotationService.WithMeasuredSize(new QuickAnnotationSpec(NewAnnotationName(), state.Kind,
                    state.Row.PageNumber, state.U, state.V, state.U, state.V, text) { Format = format }, state.Geometry));
                description = "Add " + label;
            }
            else if (text == existing.Text && (state.Kind != QuickAnnotationKind.Typewriter || TextFormat.Decode(existing.Format) == _textFormat))
            {
                return;
            }
            else if (text.Length == 0)
            {
                change = new QuickAnnotationChange(existing, null);
                description = "Delete " + label;
            }
            else
            {
                // Ghi chú: chỉ đổi /Contents, giữ icon gốc. Typewriter: chữ nằm trong appearance → vẽ lại từ spec.
                var edited = state.Kind == QuickAnnotationKind.Comment
                    ? existing with { Text = text }
                    : PdfQuickAnnotationService.WithMeasuredSize(Regenerated(existing) with { Text = text, Format = format }, state.Geometry);
                change = new QuickAnnotationChange(existing, edited);
                description = "Edit " + label;
            }
            CommitAnnotationChange(state.Row, change, description);
        }

        /// <summary>Callback chung của cả 3 công cụ: đưa thay đổi cho host ghi vào file + Undo/Redo.</summary>
        private void CommitAnnotationChange(PageRow row, QuickAnnotationChange change, string description)
        {
            if (EditHost == null) return;
            // Tác giả / thời điểm sửa (panel Comments, ghi vào /T, /M lúc Save) — chú thích giữ appearance gốc thì giữ nguyên.
            if (change.Add is { ObjectNumber: 0 } add)
                change = change with { Add = add with { Author = add.Author.Length > 0 ? add.Author : Environment.UserName, Date = DateTime.Now } };
            _ = EditHost.ApplyAnnotationChangesAsync(row.SourcePath, new[] { change }, description);
        }

        private static string NewAnnotationName() => "xt-" + Guid.NewGuid().ToString("N");
    }
}
