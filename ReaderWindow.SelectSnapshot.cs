using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XTPdfMergeApp.Services;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp
{
    /// <summary>
    /// Select (copy text, drag across words like any PDF viewer) and SnapShot (drag a rectangle, the picture of that
    /// area goes to the clipboard — page + visible annotations, exactly what is on screen).
    /// </summary>
    public partial class ReaderWindow
    {
        // ── Select: copy text ────────────────────────────────────────────

        private sealed record TextSelDrag(PageRow Row, double StartU, double StartV);
        private sealed record TextSelection(PageRow Row, string Text, IReadOnlyList<(double U1, double V1, double U2, double V2)> Rects);

        private TextSelDrag? _textSelDrag;
        private TextSelection? _textSelection;
        private readonly List<UIElement> _textSelectionVisuals = new();

        private void BeginTextSelectionDrag(PageHit hit)
        {
            ClearTextSelection();
            if (!TryPageToLayer(hit.Row, hit.U, hit.V, out Point start)) return;
            _textSelDrag = new TextSelDrag(hit.Row, hit.U, hit.V);
            ReaderCommentHoverPopup.Visibility = Visibility.Collapsed;
            ReaderContentHost.CaptureMouse();
            Canvas.SetLeft(TextSelectDragRubber, start.X);
            Canvas.SetTop(TextSelectDragRubber, start.Y);
            TextSelectDragRubber.Width = TextSelectDragRubber.Height = 0;
            TextSelectDragRubber.Visibility = Visibility.Visible;
        }

        private void UpdateTextSelectionDrag(TextSelDrag drag, Point pointInHost)
        {
            if (!TryGetPagePoint(drag.Row, pointInHost, clamp: true, out var current)) return;
            if (!TryPageToLayer(drag.Row, drag.StartU, drag.StartV, out Point a) ||
                !TryPageToLayer(drag.Row, current.U, current.V, out Point b)) return;
            Canvas.SetLeft(TextSelectDragRubber, Math.Min(a.X, b.X));
            Canvas.SetTop(TextSelectDragRubber, Math.Min(a.Y, b.Y));
            TextSelectDragRubber.Width = Math.Abs(a.X - b.X);
            TextSelectDragRubber.Height = Math.Abs(a.Y - b.Y);
        }

        private async Task FinishTextSelectionDragAsync(TextSelDrag drag, Point pointInHost)
        {
            bool haveEnd = TryGetPagePoint(drag.Row, pointInHost, clamp: true, out var end);
            bool bigEnough = TextSelectDragRubber.Width >= 3 && TextSelectDragRubber.Height >= 3;
            _textSelDrag = null;
            TextSelectDragRubber.Visibility = Visibility.Collapsed;
            if (ReaderContentHost.IsMouseCaptured) ReaderContentHost.ReleaseMouseCapture();
            if (!haveEnd || !bigEnough) return;

            var page = await LoadPageAnnotationsAsync(drag.Row);
            if (page == null) return;
            var geometry = page.Geometry;
            var (ax, ay, _, _) = geometry.DisplayRectToUser(drag.StartU, drag.StartV, drag.StartU, drag.StartV);
            var (bx, by, _, _) = geometry.DisplayRectToUser(end.U, end.V, end.U, end.V);
            var result = await PdfThumbnailService.SelectTextWithStringAsync(drag.Row.SourcePath, drag.Row.PageNumber, ax, ay, bx, by);
            if (result is not { } value || value.Rects.Count == 0 || string.IsNullOrEmpty(value.Text.Trim()))
            {
                XTStyle.Controls.XTGrowl.Info("No selectable text there — a scanned page has no text to select.", this);
                return;
            }
            if (_readerTool != ReaderTool.Select) return; // tool changed while the text lookup ran

            _textSelection = new TextSelection(drag.Row, value.Text.Trim(), value.Rects);
            foreach (var _ in value.Rects)
            {
                var box = new System.Windows.Shapes.Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x55, 0x25, 0x63, 0xEB)), IsHitTestVisible = false };
                ReaderInteractionLayer.Children.Add(box);
                _textSelectionVisuals.Add(box);
            }
            RefreshTextSelectionVisuals();
            ShowTextSelectionBar();
        }

        /// <summary>Drops the highlighted selection (tool change, Escape, new drag, click elsewhere).</summary>
        private void ClearTextSelection()
        {
            foreach (var box in _textSelectionVisuals) ReaderInteractionLayer.Children.Remove(box);
            _textSelectionVisuals.Clear();
            _textSelection = null;
            _textSelectionAnchor = null;
            TextSelectionBar.Visibility = Visibility.Collapsed;
        }

        private void CopySelectedText()
        {
            if (_textSelection is not { } selection) return;
            if (!Controls.PdfPermissionDialog.Require(this, new[] { selection.Row.SourcePath }, PdfPermissionOperation.Copy)) return;
            try
            {
                Clipboard.SetText(selection.Text);
                XTStyle.Controls.XTGrowl.Success("Copied", this);
            }
            catch (Exception ex)
            {
                XTStyle.Controls.XTGrowl.Info("Could not copy the text: " + ex.Message, this);
            }
        }

        // ── SnapShot: picture of an area, to the clipboard (page + visible annotations, exactly like on screen) ──

        private sealed record SnapshotDrag(PageRow Row, double StartU, double StartV);
        private SnapshotDrag? _snapshotDrag;

        private void BeginSnapshotDrag(PageHit hit)
        {
            if (!Controls.PdfPermissionDialog.Require(this, new[] { hit.Row.SourcePath }, PdfPermissionOperation.Copy)) return;
            if (!TryPageToLayer(hit.Row, hit.U, hit.V, out Point start)) return;
            _snapshotDrag = new SnapshotDrag(hit.Row, hit.U, hit.V);
            ReaderCommentHoverPopup.Visibility = Visibility.Collapsed;
            ReaderContentHost.CaptureMouse();
            Canvas.SetLeft(ReaderSnapshotRubber, start.X);
            Canvas.SetTop(ReaderSnapshotRubber, start.Y);
            ReaderSnapshotRubber.Width = ReaderSnapshotRubber.Height = 0;
            ReaderSnapshotRubber.Visibility = Visibility.Visible;
        }

        private void UpdateSnapshotDrag(SnapshotDrag drag, Point pointInHost)
        {
            if (!TryGetPagePoint(drag.Row, pointInHost, clamp: true, out var current)) return;
            if (!TryPageToLayer(drag.Row, drag.StartU, drag.StartV, out Point a) ||
                !TryPageToLayer(drag.Row, current.U, current.V, out Point b)) return;
            Canvas.SetLeft(ReaderSnapshotRubber, Math.Min(a.X, b.X));
            Canvas.SetTop(ReaderSnapshotRubber, Math.Min(a.Y, b.Y));
            ReaderSnapshotRubber.Width = Math.Abs(a.X - b.X);
            ReaderSnapshotRubber.Height = Math.Abs(a.Y - b.Y);
        }

        private void CancelSnapshotDrag()
        {
            if (_snapshotDrag == null) return;
            _snapshotDrag = null;
            ReaderSnapshotRubber.Visibility = Visibility.Collapsed;
            if (ReaderContentHost.IsMouseCaptured) ReaderContentHost.ReleaseMouseCapture();
        }

        /// <summary>Renders the viewport (<see cref="ContinuousPdfView.Surface"/>: exactly the pixels on screen, page + annotations,
        /// bounded to the window — never the whole document) and crops to the dragged rectangle.</summary>
        private void FinishSnapshotDrag(SnapshotDrag drag, Point pointInHost)
        {
            bool haveEnd = TryGetPagePoint(drag.Row, pointInHost, clamp: true, out var end);
            bool bigEnough = ReaderSnapshotRubber.Width >= 6 && ReaderSnapshotRubber.Height >= 6;
            CancelSnapshotDrag();
            if (!haveEnd || !bigEnough) return;
            if (!ReaderContinuousView.TryPageToView(drag.Row, Math.Min(drag.StartU, end.U), Math.Min(drag.StartV, end.V), out Point p1) ||
                !ReaderContinuousView.TryPageToView(drag.Row, Math.Max(drag.StartU, end.U), Math.Max(drag.StartV, end.V), out Point p2))
                return;

            var surface = ReaderContinuousView.Surface;
            var rect = new Rect(p1, p2);
            rect.Intersect(new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
            if (rect.Width < 2 || rect.Height < 2) return;

            try
            {
                double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                int pixelWidth = Math.Max(1, (int)Math.Round(surface.ActualWidth * dpi));
                int pixelHeight = Math.Max(1, (int)Math.Round(surface.ActualHeight * dpi));
                var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                rtb.Render(surface);

                int x = Math.Clamp((int)Math.Round(rect.X * dpi), 0, pixelWidth - 1);
                int y = Math.Clamp((int)Math.Round(rect.Y * dpi), 0, pixelHeight - 1);
                int w = Math.Clamp((int)Math.Round(rect.Width * dpi), 1, pixelWidth - x);
                int h = Math.Clamp((int)Math.Round(rect.Height * dpi), 1, pixelHeight - y);
                var cropped = new CroppedBitmap(rtb, new Int32Rect(x, y, w, h));
                Clipboard.SetImage(cropped);
                XTStyle.Controls.XTGrowl.Success("Copied to clipboard", this);
            }
            catch (Exception ex)
            {
                XTStyle.Controls.XTGrowl.Info("Could not copy the picture: " + ex.Message, this);
            }
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) SetReaderTool(ReaderTool.Hand);
        }
    }
}
