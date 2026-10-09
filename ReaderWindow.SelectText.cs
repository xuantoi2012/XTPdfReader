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
    /// Select: copy text (drag across words like any PDF viewer). Pictures of the screen are XT Capture's job (see ReaderWindow.Capture.cs).
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
                XTPdfMergeApp.Services.Growl.Info("No selectable text there — a scanned page has no text to select.", this);
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
                XTPdfMergeApp.Services.Growl.Success("Copied", this);
            }
            catch (Exception ex)
            {
                XTPdfMergeApp.Services.Growl.Info("Could not copy the text: " + ex.Message, this);
            }
        }
    }
}
