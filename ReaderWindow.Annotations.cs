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
        private enum ReaderTool { Hand, Select, Typewriter, Comment, Callout, Pencil, Highlight, Underline, Strikethrough, Squiggly, Eraser, Stamp, Shape, SnapShot }

        private ReaderTool _readerTool = ReaderTool.Hand;

        /// <summary>1 điểm trên 1 trang đang hiển thị: (U, V) chuẩn hoá 0..1, gốc trên-trái của trang.</summary>
        private readonly record struct PageHit(PageRow Row, double U, double V);
        private sealed record LinkPress(PageHit Hit, Point Start);
        private LinkPress? _linkPress;

        // ── Chọn công cụ ────────────────────────────────────────────────

        private void ReaderHandTool_Click(object sender, RoutedEventArgs e) => SetReaderTool(ReaderTool.Hand);
        private void ReaderSelectTool_Click(object sender, RoutedEventArgs e) => ToggleReaderTool(ReaderTool.Select);
        private void ReaderTypewriterTool_Click(object sender, RoutedEventArgs e) => ToggleReaderTool(ReaderTool.Typewriter);
        private void ReaderCommentTool_Click(object sender, RoutedEventArgs e) => ToggleReaderTool(ReaderTool.Comment);
        private void ReaderCalloutTool_Click(object sender, RoutedEventArgs e) => ToggleReaderTool(ReaderTool.Callout);
        private void ReaderPencilTool_Click(object sender, RoutedEventArgs e) => ToggleReaderTool(ReaderTool.Pencil);
        private void ReaderUnderlineTool_Click(object sender, RoutedEventArgs e) => ToggleReaderTool(ReaderTool.Underline);
        private void ReaderStrikethroughTool_Click(object sender, RoutedEventArgs e) => ToggleReaderTool(ReaderTool.Strikethrough);
        private void ReaderSquigglyTool_Click(object sender, RoutedEventArgs e) => ToggleReaderTool(ReaderTool.Squiggly);
        private void ReaderEraserTool_Click(object sender, RoutedEventArgs e) => ToggleReaderTool(ReaderTool.Eraser);
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
            if (_readerPage is { } page && tool is not (ReaderTool.Hand or ReaderTool.Select) &&
                !PdfPermissionDialog.Require(this, new[] { page.SourcePath },
                    tool == ReaderTool.SnapShot ? PdfPermissionOperation.Copy : PdfPermissionOperation.Annotate)) return;
            CommitAnnotationEditor();
            CancelHighlightDrag();
            CancelInkDrag();
            CancelShapeDrag();
            CancelCalloutPlacement();
            _readerTool = tool;
            if (tool != ReaderTool.Hand) SelectAnnotation(null, null);
            UpdateFormatBarVisibility();
            ReaderHandToolButton.Tag = tool == ReaderTool.Hand ? "Active" : null;
            ReaderSelectToolButton.Tag = tool == ReaderTool.Select ? "Active" : null;
            ReaderTypewriterToolButton.Tag = tool == ReaderTool.Typewriter ? "Active" : null;
            ReaderCommentToolButton.Tag = tool == ReaderTool.Comment ? "Active" : null;
            ReaderCalloutToolButton.Tag = tool == ReaderTool.Callout ? "Active" : null;
            ReaderPencilToolButton.Tag = tool == ReaderTool.Pencil ? "Active" : null;
            ReaderHighlightTextButton.Tag = tool == ReaderTool.Highlight && AppSettings.HighlightMode != "Area" ? "Active" : null;
            ReaderHighlightAreaButton.Tag = tool == ReaderTool.Highlight && AppSettings.HighlightMode == "Area" ? "Active" : null;
            ReaderUnderlineToolButton.Tag = tool == ReaderTool.Underline ? "Active" : null;
            ReaderStrikethroughToolButton.Tag = tool == ReaderTool.Strikethrough ? "Active" : null;
            ReaderSquigglyToolButton.Tag = tool == ReaderTool.Squiggly ? "Active" : null;
            ReaderEraserToolButton.Tag = tool == ReaderTool.Eraser ? "Active" : null;
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
                ReaderTool.Callout => Cursors.Pen,
                ReaderTool.Pencil => Cursors.Pen,
                ReaderTool.Highlight or ReaderTool.Underline or ReaderTool.Strikethrough or ReaderTool.Squiggly => Cursors.Cross,
                ReaderTool.Eraser => Cursors.No,
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
            _linkPress = null;
            // Thanh Find / thanh định dạng nằm đè lên trang: bấm vào chúng không được coi là bấm lên trang.
            if (IsOverlayBar(e.OriginalSource as DependencyObject)) return;
            // Grip resize nằm ngay trên hình đã chọn: để nguyên (không Handled) cho Grip_MouseDown của chính nó xử lý,
            // vì Preview đi từ gốc xuống — handler này chạy TRƯỚC handler của grip nếu không trả sớm ở đây.
            if (e.OriginalSource is System.Windows.Shapes.Rectangle { Tag: string tag } && Array.IndexOf(new[] { "NW", "N", "NE", "E", "SE", "S", "SW", "W", "LineA", "LineB" }, tag) >= 0) return;

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
                        if (e.ClickCount >= 2 && picked.Kind == QuickAnnotationKind.Callout)
                            _ = OpenAnnotationEditorAsync(hit, picked.Kind, picked);
                        else if (e.ClickCount >= 2 && picked.Kind == QuickAnnotationKind.Typewriter)
                            _ = OpenAnnotationEditorAsync(hit, picked.Kind, picked);
                        else
                            // Ghi chú (icon Note): thả chuột không kéo = hiện popup ngay tại chỗ (kiểu Word) —
                            // xem FinishAnnotationMove (chỉ khi move.Moved vẫn false, tức bấm chứ không kéo).
                            BeginAnnotationMove(hit, picked);
                    }
                    else
                    {
                        SelectAnnotation(null, null);
                        // Để ContinuousPdfView vẫn bắt được kéo-pan. Chỉ mouse-up gần đúng điểm bấm mới kích hoạt Link.
                        _linkPress = new LinkPress(hit, e.GetPosition(ReaderContentHost));
                    }
                    break;

                case ReaderTool.Typewriter:
                    e.Handled = true;
                    _ = OpenAnnotationEditorAsync(hit, QuickAnnotationKind.Typewriter,
                        FindAnnotationAt(page, hit, QuickAnnotationKind.Typewriter));
                    break;

                case ReaderTool.Comment:
                    e.Handled = true;
                    // Như Word: bấm ghi chú có sẵn = mở luồng của nó; chỗ trống = thẻ soạn ghi chú mới (Post / Cancel).
                    if (FindAnnotationAt(page, hit, QuickAnnotationKind.Comment) is { } existingNote)
                    {
                        SelectAnnotation(hit.Row, existingNote);
                        ShowCommentPopup(hit.Row, existingNote);
                    }
                    else ComposeNote(hit);
                    break;

                case ReaderTool.Callout:
                    e.Handled = true;
                    BeginCalloutPlacement(hit, e.GetPosition(ReaderContentHost));
                    break;

                case ReaderTool.Pencil:
                    e.Handled = true;
                    BeginInkDrag(hit);
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

                case ReaderTool.Squiggly:
                    e.Handled = true;
                    BeginHighlightDrag(hit, QuickAnnotationKind.Squiggly);
                    break;

                case ReaderTool.Eraser:
                    e.Handled = true;
                    if (PickAnnotation(page, hit) is { } toErase)
                    {
                        SelectAnnotation(hit.Row, toErase);
                        DeleteSelectedAnnotation();
                    }
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
            if (UpdateAnnotationMove(point) || UpdateShapeDrag(point) || UpdateShapeResize(point) || UpdateLineResize(point))
            {
                e.Handled = true;
                return;
            }
            if (UpdateInkDrag(point) || UpdateCalloutPlacement(point) || UpdateCalloutTipDrag(point))
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

            if (_linkPress is { } press && (point - press.Start).Length > 4)
                _linkPress = null;

            UpdateCommentHover(point);
        }

        private void ReaderContentHost_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (FinishAnnotationMove() || FinishShapeDrag(e.GetPosition(ReaderContentHost)) || FinishShapeResize() || FinishLineResize())
            {
                e.Handled = true;
                return;
            }
            if (FinishInkDrag() || ReleaseCalloutPlacement(e.GetPosition(ReaderContentHost)) || FinishCalloutTipDrag())
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
                return;
            }
            if (_linkPress is not { } press) return;
            _linkPress = null;
            _ = ActivateLinkAsync(press.Hit);
        }

        private async Task ActivateLinkAsync(PageHit hit)
        {
            var target = await PdfLinkService.FindAtAsync(hit.Row.SourcePath, hit.Row.PageNumber, hit.U, hit.V);
            if (target == null) return;
            if (target.PageNumber is { } page)
            {
                NavigateToSourcePage(hit.Row.SourcePath, page);
                return;
            }
            if (string.IsNullOrWhiteSpace(target.Uri) || !Uri.TryCreate(target.Uri, UriKind.Absolute, out var uri)) return;
            bool safeScheme = uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase);
            if (!safeScheme)
            {
                AppDialog.Show(this, $"This PDF link uses an unsupported protocol:\n{uri.Scheme}", "PDF link",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppDialog.Show(this, "Could not open the link:\n" + ex.Message, "PDF link", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ReaderContentHost_MouseLeave(object sender, MouseEventArgs e)
        {
            _linkPress = null;
            ReaderCommentHoverPopup.Visibility = Visibility.Collapsed;
        }

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

        // ── Pencil: freehand PDF Ink ───────────────────────────────────

        private sealed class InkDrag
        {
            public required PageRow Row { get; init; }
            public List<(double U, double V)> Points { get; } = new();
        }

        private InkDrag? _inkDrag;

        private void BeginInkDrag(PageHit hit)
        {
            if (!TryPageToLayer(hit.Row, hit.U, hit.V, out Point point)) return;
            _inkDrag = new InkDrag { Row = hit.Row };
            _inkDrag.Points.Add((hit.U, hit.V));
            ReaderInkPreview.Points.Clear();
            ReaderInkPreview.Points.Add(point);
            ReaderInkPreview.Visibility = Visibility.Visible;
            ReaderCommentHoverPopup.Visibility = Visibility.Collapsed;
            ReaderContentHost.CaptureMouse();
        }

        private bool UpdateInkDrag(Point pointInHost)
        {
            if (_inkDrag is not { } drag || !TryGetPagePoint(drag.Row, pointInHost, clamp: true, out var hit)) return _inkDrag != null;
            var previous = drag.Points[^1];
            if (Math.Abs(hit.U - previous.U) < .0008 && Math.Abs(hit.V - previous.V) < .0008) return true;
            if (!TryPageToLayer(drag.Row, hit.U, hit.V, out Point point)) return true;
            drag.Points.Add((hit.U, hit.V));
            ReaderInkPreview.Points.Add(point);
            return true;
        }

        private bool FinishInkDrag()
        {
            if (_inkDrag is not { } drag) return false;
            _inkDrag = null;
            ReaderInkPreview.Visibility = Visibility.Collapsed;
            ReaderInkPreview.Points.Clear();
            if (ReaderContentHost.IsMouseCaptured) ReaderContentHost.ReleaseMouseCapture();
            if (drag.Points.Count < 2) return true;
            const double pad = .003;
            var spec = new QuickAnnotationSpec(NewAnnotationName(), QuickAnnotationKind.Ink, drag.Row.PageNumber,
                Math.Max(0, drag.Points.Min(p => p.U) - pad), Math.Max(0, drag.Points.Min(p => p.V) - pad),
                Math.Min(1, drag.Points.Max(p => p.U) + pad), Math.Min(1, drag.Points.Max(p => p.V) + pad), "")
            {
                Format = PdfQuickAnnotationService.EncodeInkPoints(drag.Points),
                Color = "#D74B31"
            };
            CommitAnnotationChange(drag.Row, new QuickAnnotationChange(null, spec), "Add pencil stroke");
            return true;
        }

        private void CancelInkDrag()
        {
            if (_inkDrag == null) return;
            _inkDrag = null;
            ReaderInkPreview.Visibility = Visibility.Collapsed;
            ReaderInkPreview.Points.Clear();
            if (ReaderContentHost.IsMouseCaptured) ReaderContentHost.ReleaseMouseCapture();
        }

        // ── Callout: làm theo Foxit (đã mở Foxit thử) ─────────────────────
        // Bấm lần 1 = đầu mũi tên; rê chuột thấy đường dẫn + khung hộp chạy theo; bấm lần 2 (hoặc giữ chuột kéo
        // rồi thả) = chỗ đặt hộp chữ → gõ chữ luôn tại chỗ. Esc / đổi công cụ = bỏ. Bấm đúp callout có sẵn = sửa chữ
        // ngay trong hộp (không mở hộp thoại). Chọn callout: 8 grip hộp + 1 grip ở đầu mũi tên kéo được.

        private sealed class CalloutPlacement
        {
            public required PageRow Row { get; init; }
            public required double TipU { get; init; }
            public required double TipV { get; init; }
            public required Point Start { get; init; }
            public bool Pressed { get; set; } = true;
            public bool Dragged { get; set; }
        }

        private CalloutPlacement? _calloutPlacement;
        private const double CalloutDefaultWidthPoints = 150, CalloutDefaultHeightPoints = 24;

        private void BeginCalloutPlacement(PageHit hit, Point pointInHost)
        {
            ReaderCommentHoverPopup.Visibility = Visibility.Collapsed;
            if (_calloutPlacement is { } pending)
            {
                // Bấm lần 2 = đặt hộp (bấm sang trang khác thì kẹp về trang có mũi tên).
                FinishCalloutPlacement(pending, pointInHost);
                return;
            }
            if (!TryPageToLayer(hit.Row, hit.U, hit.V, out Point tip)) return;
            _calloutPlacement = new CalloutPlacement { Row = hit.Row, TipU = hit.U, TipV = hit.V, Start = pointInHost };
            ReaderInkPreview.Points.Clear();
            ReaderInkPreview.Points.Add(tip);
            ReaderInkPreview.Points.Add(tip);
            ReaderInkPreview.Visibility = Visibility.Visible;
            ReaderContentHost.CaptureMouse();
        }

        /// <summary>Đầu hộp (u, v trên trang) khi đặt ở điểm chuột: hộp nằm về phía chuột so với mũi tên, như Foxit.</summary>
        private (double U, double V) CalloutBoxOrigin(CalloutPlacement p, PageHit at, PdfPageGeometry geometry)
        {
            double w = CalloutDefaultWidthPoints / Math.Max(1, geometry.DisplayWidth);
            double h = CalloutDefaultHeightPoints / Math.Max(1, geometry.DisplayHeight);
            double u = at.U >= p.TipU ? at.U : at.U - w;
            double v = at.V - h / 2;
            return (Math.Clamp(u, 0, Math.Max(0, 1 - w)), Math.Clamp(v, 0, Math.Max(0, 1 - h)));
        }

        private bool UpdateCalloutPlacement(Point pointInHost)
        {
            if (_calloutPlacement is not { } p) return false;
            if (p.Pressed && (pointInHost - p.Start).Length > 6) p.Dragged = true;
            if (GetCachedPageAnnotations(p.Row)?.Geometry is not { } geometry || !TryGetPagePoint(p.Row, pointInHost, clamp: true, out var at)) return true;
            var (u, v) = CalloutBoxOrigin(p, at, geometry);
            double w = CalloutDefaultWidthPoints / geometry.DisplayWidth, h = CalloutDefaultHeightPoints / geometry.DisplayHeight;
            if (!TryPageToLayer(p.Row, u, v, out Point a) || !TryPageToLayer(p.Row, u + w, v + h, out Point b) ||
                !TryPageToLayer(p.Row, p.TipU, p.TipV, out Point tip)) return true;
            // Đường dẫn nối tới điểm gần nhất trên biên hộp — đúng cách AddCallout vẽ khi ghi.
            double left = Math.Min(a.X, b.X), right = Math.Max(a.X, b.X), top = Math.Min(a.Y, b.Y), bottom = Math.Max(a.Y, b.Y);
            var attach = new Point(Math.Clamp(tip.X, left, right), Math.Clamp(tip.Y, top, bottom));
            ReaderInkPreview.Points[1] = attach;
            Canvas.SetLeft(ReaderHighlightRubberBand, left);
            Canvas.SetTop(ReaderHighlightRubberBand, top);
            ReaderHighlightRubberBand.Width = right - left;
            ReaderHighlightRubberBand.Height = bottom - top;
            ReaderHighlightRubberBand.Visibility = Visibility.Visible;
            return true;
        }

        /// <summary>Thả chuột: đã kéo = đặt hộp tại chỗ thả; chỉ bấm = chờ cú bấm thứ 2 (chuột thả tự do, không giữ capture).</summary>
        private bool ReleaseCalloutPlacement(Point pointInHost)
        {
            if (_calloutPlacement is not { Pressed: true } p) return false;
            p.Pressed = false;
            if (p.Dragged) FinishCalloutPlacement(p, pointInHost);
            else if (ReaderContentHost.IsMouseCaptured) ReaderContentHost.ReleaseMouseCapture();
            return true;
        }

        private void FinishCalloutPlacement(CalloutPlacement p, Point pointInHost)
        {
            CancelCalloutPlacement();
            _ = OpenNewCalloutAsync(p, pointInHost);
        }

        private async Task OpenNewCalloutAsync(CalloutPlacement p, Point pointInHost)
        {
            var page = await LoadPageAnnotationsAsync(p.Row);
            if (page == null || _annotationEditor != null || !TryGetPagePoint(p.Row, pointInHost, clamp: true, out var at)) return;
            var (u, v) = CalloutBoxOrigin(p, at, page.Geometry);
            _annotationEditor = new AnnotationEditorState
            {
                Row = p.Row, Kind = QuickAnnotationKind.Callout, U = u, V = v, Geometry = page.Geometry,
                TipU = p.TipU, TipV = p.TipV, BoxWidthPoints = CalloutDefaultWidthPoints
            };
            ShowCalloutEditor("");
        }

        private void ShowCalloutEditor(string text)
        {
            var editor = ReaderAnnotationEditor;
            editor.Text = text;
            LoadFormatBar();
            ApplyEditorFormat();
            // Nền + viền giống hộp callout sẽ ghi (xanh nhạt kiểu Foxit), chữ tự xuống dòng theo bề ngang hộp.
            editor.Background = new SolidColorBrush(Color.FromRgb(0xEB, 0xF3, 0xF5));
            editor.TextWrapping = TextWrapping.Wrap;
            editor.MinHeight = 0;
            editor.Visibility = Visibility.Visible;
            UpdateFormatBarVisibility();
            UpdateSelectionVisual();
            PositionAnnotationEditor();
            editor.CaretIndex = editor.Text.Length;
            editor.Focus();
            _ = Dispatcher.InvokeAsync(() => { if (_annotationEditor != null) editor.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
        }

        private void CancelCalloutPlacement()
        {
            if (_calloutPlacement == null) return;
            _calloutPlacement = null;
            ReaderInkPreview.Visibility = Visibility.Collapsed;
            ReaderInkPreview.Points.Clear();
            ReaderHighlightRubberBand.Visibility = Visibility.Collapsed;
            if (ReaderContentHost.IsMouseCaptured) ReaderContentHost.ReleaseMouseCapture();
        }

        // Kéo grip ở đầu mũi tên của callout đang chọn: hộp đứng yên, chỉ điểm chỉ đổi.
        private sealed record CalloutTipDrag(PageRow Row, QuickAnnotationSpec Spec);
        private CalloutTipDrag? _calloutTipDrag;
        private (double U, double V) _calloutTipDragPoint;

        private void BeginCalloutTipDrag(PageRow row, QuickAnnotationSpec spec)
        {
            _calloutTipDrag = new CalloutTipDrag(row, spec);
            var tip = PdfQuickAnnotationService.DecodeCallout(spec.Format);
            _calloutTipDragPoint = (tip.TipU, tip.TipV);
            ReaderInkPreview.Points.Clear();
            ReaderInkPreview.Points.Add(default);
            ReaderInkPreview.Points.Add(default);
            ReaderInkPreview.Visibility = Visibility.Visible;
            ReaderContentHost.CaptureMouse();
        }

        private bool UpdateCalloutTipDrag(Point pointInHost)
        {
            if (_calloutTipDrag is not { } drag) return false;
            var s = drag.Spec;
            if (!TryGetPagePoint(drag.Row, pointInHost, clamp: true, out var at) ||
                !TryPageToLayer(drag.Row, at.U, at.V, out Point tip) ||
                !TryPageToLayer(drag.Row, s.U1, s.V1, out Point a) || !TryPageToLayer(drag.Row, s.U2, s.V2, out Point b)) return true;
            _calloutTipDragPoint = (at.U, at.V);
            ReaderInkPreview.Points[0] = tip;
            ReaderInkPreview.Points[1] = new Point(Math.Clamp(tip.X, Math.Min(a.X, b.X), Math.Max(a.X, b.X)), Math.Clamp(tip.Y, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y)));
            Place(GripLineA, tip.X, tip.Y);
            return true;
        }

        private bool FinishCalloutTipDrag()
        {
            if (_calloutTipDrag is not { } drag) return false;
            _calloutTipDrag = null;
            ReaderInkPreview.Visibility = Visibility.Collapsed;
            ReaderInkPreview.Points.Clear();
            if (ReaderContentHost.IsMouseCaptured) ReaderContentHost.ReleaseMouseCapture();
            var s = drag.Spec;
            var old = PdfQuickAnnotationService.DecodeCallout(s.Format);
            var (u, v) = _calloutTipDragPoint;
            if (Math.Abs(u - old.TipU) > 1e-6 || Math.Abs(v - old.TipV) > 1e-6)
            {
                var changed = Regenerated(s) with { Format = PdfQuickAnnotationService.EncodeCallout(u, v, old.TextFormat) };
                _selAnn = changed;
                CommitAnnotationChange(drag.Row, new QuickAnnotationChange(s, changed), "Move callout arrow");
            }
            UpdateSelectionVisual();
            return true;
        }

        // ── Ghi chú: popup kiểu Word ─────────────────────────────────────

        /// <summary>Toạ độ màn hình (đơn vị WPF) của 1 điểm trên trang — để đặt popup nổi.</summary>
        private bool TryPageToScreen(PageRow row, double u, double v, out Point screen)
        {
            screen = default;
            if (!TryPageToLayer(row, u, v, out Point anchor)) return false;
            Point devicePoint = ReaderInteractionLayer.PointToScreen(anchor);
            var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            screen = fromDevice.Transform(devicePoint);
            return true;
        }

        /// <summary>Công cụ Note bấm vào chỗ trống: thẻ soạn kiểu Word ngay cạnh điểm bấm; Post mới tạo ghi chú (Cancel = không tạo gì).</summary>
        private void ComposeNote(PageHit hit)
        {
            _commentPopup?.Close();
            ReaderCommentHoverPopup.Visibility = Visibility.Collapsed;
            if (!TryPageToScreen(hit.Row, hit.U, hit.V, out Point screen)) return;
            var popup = CommentPopup.Compose(this, screen, Environment.UserName);
            popup.Posted += text => _ = AddNoteAsync(hit, text);
            _commentPopup = popup;
        }

        private async Task AddNoteAsync(PageHit hit, string text)
        {
            var page = await LoadPageAnnotationsAsync(hit.Row);
            if (page == null) return;
            var spec = PdfQuickAnnotationService.WithMeasuredSize(new QuickAnnotationSpec(NewAnnotationName(), QuickAnnotationKind.Comment,
                hit.Row.PageNumber, hit.U, hit.V, hit.U, hit.V, text), page.Geometry);
            CommitAnnotationChange(hit.Row, new QuickAnnotationChange(null, spec), "Add note");
        }
        private async void AddInlineReply(CommentInfo parent, string text)
        {
            var row = _readerGroup?.Pages.FirstOrDefault(p => string.Equals(p.SourcePath, parent.Path, StringComparison.OrdinalIgnoreCase) && p.PageNumber == parent.Page);
            if (row == null) return;
            var page = await LoadPageAnnotationsAsync(row);
            var source = page?.Annotations.FirstOrDefault(a => a.Name == parent.Name);
            if (page == null || source == null) return;
            var reply = new QuickAnnotationSpec(NewAnnotationName(), QuickAnnotationKind.Reply, row.PageNumber,
                Math.Min(.95, source.U2 + .01), Math.Min(.95, source.V1 + .01), 0, 0, text) { Format = "R|" + source.Name };
            CommitAnnotationChange(row, new QuickAnnotationChange(null, PdfQuickAnnotationService.WithMeasuredSize(reply, page.Geometry)), "Reply to comment");
        }

        private CommentPopup? _commentPopup;

        /// <summary>Bấm icon Note (không kéo) = popup ngay tại chỗ, kiểu Word: tác giả/ngày/nội dung + toàn bộ
        /// reply (dàn phẳng theo thời gian, như CommentsPanel) + 1 ô Reply — khỏi cần mở panel Comments.</summary>
        private void ShowCommentPopup(PageRow row, QuickAnnotationSpec noteSpec, bool focusReply = false)
        {
            _commentPopup?.Close();
            ReaderCommentHoverPopup.Visibility = Visibility.Collapsed;
            var page = GetCachedPageAnnotations(row);
            var all = page?.Annotations ?? Array.Empty<QuickAnnotationSpec>();
            CommentInfo ToInfo(QuickAnnotationSpec s) => new(row.SourcePath, row.PageNumber, s.Name, s.Kind, s.Author, s.Date, s.Text, s.Resolved);
            var root = ToInfo(noteSpec);
            var replies = all.Where(a => a.Kind == QuickAnnotationKind.Reply && a.Format.StartsWith("R|", StringComparison.Ordinal) && a.Format[2..] == noteSpec.Name)
                .OrderBy(a => a.Date).Select(ToInfo).ToList();
            if (!TryPageToScreen(row, noteSpec.U2, noteSpec.V1, out Point screen)) return;
            var popup = CommentPopup.Show(this, screen, root, replies, focusReply);
            popup.ReplySubmitted += text => AddInlineReply(root, text);
            popup.EditRequested += (item, text) => EditInlineComment(item, text);
            popup.DeleteRequested += item =>
            {
                DeleteInlineComment(item, withReplies: item.Name == root.Name);
                if (item.Name == root.Name) SelectAnnotation(null, null);
            };
            popup.ResolvedToggled += async resolved => { if (EditHost != null) await EditHost.SetCommentResolvedAsync(root.Path, root.Page, root.Name, resolved); };
            _commentPopup = popup;
        }

        private async void EditInlineComment(CommentInfo info, string text)
        {
            var row = _readerGroup?.Pages.FirstOrDefault(p => string.Equals(p.SourcePath, info.Path, StringComparison.OrdinalIgnoreCase) && p.PageNumber == info.Page);
            var page = row == null ? null : await LoadPageAnnotationsAsync(row);
            var current = page?.Annotations.FirstOrDefault(a => a.Name == info.Name);
            if (row == null || current == null) return;
            var changed = current.Kind is QuickAnnotationKind.Comment or QuickAnnotationKind.Reply ? current with { Text = text } : Regenerated(current) with { Text = text };
            CommitAnnotationChange(row, new QuickAnnotationChange(current, changed), "Edit comment");
        }

        /// <summary>Xoá 1 mục; <paramref name="withReplies"/> = "Delete thread" của Word: gốc + mọi reply trong 1 bước Undo.</summary>
        private async void DeleteInlineComment(CommentInfo info, bool withReplies = false)
        {
            var row = _readerGroup?.Pages.FirstOrDefault(p => string.Equals(p.SourcePath, info.Path, StringComparison.OrdinalIgnoreCase) && p.PageNumber == info.Page);
            var page = row == null ? null : await LoadPageAnnotationsAsync(row);
            var current = page?.Annotations.FirstOrDefault(a => a.Name == info.Name);
            if (row == null || page == null || current == null || EditHost == null) return;
            var changes = new List<QuickAnnotationChange> { new(current, null) };
            if (withReplies)
                changes.AddRange(page.Annotations
                    .Where(a => a.Kind == QuickAnnotationKind.Reply && a.Format.StartsWith("R|", StringComparison.Ordinal) && a.Format[2..] == current.Name)
                    .Select(a => new QuickAnnotationChange(a, null)));
            _ = EditHost.ApplyAnnotationChangesAsync(row.SourcePath, changes, withReplies ? "Delete thread" : "Delete comment");
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
            public double TipU { get; init; }
            public double TipV { get; init; }
            /// <summary>Callout: bề ngang hộp (pt) — ô nhập xuống dòng đúng như chữ sẽ vẽ trong hộp.</summary>
            public double BoxWidthPoints { get; init; }
            public QuickAnnotationSpec? Existing { get; init; }
        }

        private AnnotationEditorState? _annotationEditor;
        private const double CommentIconPoints = 20;

        private async Task OpenAnnotationEditorAsync(PageHit hit, QuickAnnotationKind kind, QuickAnnotationSpec? existing)
        {
            ReaderCommentHoverPopup.Visibility = Visibility.Collapsed;
            var page = await LoadPageAnnotationsAsync(hit.Row);
            if (page == null || _annotationEditor != null) return;

            if (kind == QuickAnnotationKind.Callout && existing != null)
            {
                // Sửa chữ ngay trong hộp như Foxit (bấm đúp): ô nhập đè đúng hộp, callout gốc ẩn trong lúc sửa.
                var callout = PdfQuickAnnotationService.DecodeCallout(existing.Format);
                _textFormat = TextFormat.Decode(callout.TextFormat);
                _annotationEditor = new AnnotationEditorState
                {
                    Row = hit.Row, Kind = kind, U = existing.U1, V = existing.V1, Geometry = page.Geometry, Existing = existing,
                    TipU = callout.TipU, TipV = callout.TipV, BoxWidthPoints = (existing.U2 - existing.U1) * page.Geometry.DisplayWidth
                };
                AnnotationLayer.Edit.HiddenName = existing.Name;
                ReaderContinuousView.Redraw();
                ShowCalloutEditor(existing.Text);
                return;
            }

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
            if (state.Kind is QuickAnnotationKind.Typewriter or QuickAnnotationKind.Callout)
            {
                // Khớp cỡ chữ + lề 2pt của annotation sẽ ghi → chữ gõ nằm đúng chỗ chữ sau khi ghi.
                ReaderAnnotationEditor.FontSize = Math.Max(6, _textFormat.Size * pixelsPerPoint);
                if (state.Kind == QuickAnnotationKind.Callout)
                    ReaderAnnotationEditor.Width = Math.Max(60, state.BoxWidthPoints * pixelsPerPoint);
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

            string format = state.Kind == QuickAnnotationKind.Callout ? PdfQuickAnnotationService.EncodeCallout(state.TipU, state.TipV, _textFormat.Encode()) : state.Kind == QuickAnnotationKind.Typewriter ? _textFormat.Encode() : "";
            QuickAnnotationChange change;
            string description;
            string label = state.Kind == QuickAnnotationKind.Comment ? "note" : state.Kind == QuickAnnotationKind.Callout ? "callout" : "typewriter";
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
                // Callout: giữ nguyên hộp (người dùng có thể đã kéo-giãn bằng grip), chỉ xuống dòng lại chữ cho khớp.
                var edited = state.Kind switch
                {
                    QuickAnnotationKind.Comment => existing with { Text = text },
                    QuickAnnotationKind.Callout => Regenerated(existing) with { Text = text, Format = format },
                    _ => PdfQuickAnnotationService.WithMeasuredSize(Regenerated(existing) with { Text = text, Format = format }, state.Geometry)
                };
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
