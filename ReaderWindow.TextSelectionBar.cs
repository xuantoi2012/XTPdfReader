using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XTPdfMergeApp.Services;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp
{
    /// <summary>
    /// The Select tool like Foxit: the pointer is an I-beam only over text the app can select (an arrow elsewhere, e.g. on a scan), and after
    /// dragging across text a small bar offers Highlight / Underline / Strikethrough / Squiggly, a Note anchored at the text, and Copy.
    /// </summary>
    public partial class ReaderWindow
    {
        private Rect? _textSelectionAnchor;
        private static readonly TimeSpan WordCacheLife = TimeSpan.FromSeconds(30);
        private readonly Dictionary<(string Path, int Page), (DateTime At, Task<IReadOnlyList<(double U1, double V1, double U2, double V2)>?> Words)> _wordCache = new();

        // ── Pointer over text ─────────────────────────────────────────

        private Task<IReadOnlyList<(double U1, double V1, double U2, double V2)>?> WordsOf(PageRow row)
        {
            var key = (row.SourcePath, row.PageNumber);
            if (_wordCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < WordCacheLife) return cached.Words;
            if (_wordCache.Count > 60) _wordCache.Clear();
            var task = PdfThumbnailService.GetWordRectsAsync(row.SourcePath, row.PageNumber);
            _wordCache[key] = (DateTime.UtcNow, task);
            return task;
        }

        private void UpdateSelectCursor(Point pointInHost)
        {
            if (_readerTool != ReaderTool.Select || _textSelDrag != null) return;
            Cursor cursor = Cursors.Arrow;
            if (TryHitPage(pointInHost, out var hit))
            {
                var task = WordsOf(hit.Row);
                if (!task.IsCompleted) _ = task.ContinueWith(_ => Dispatcher.BeginInvoke(() => UpdateSelectCursor(Mouse.GetPosition(ReaderContentHost))));
                else if (task.IsCompletedSuccessfully)
                {
                    var words = task.Result;
                    if (words == null) cursor = Cursors.IBeam; // the engine cannot tell: keep the old "always I-beam"
                    else
                    {
                        double tolerance = 0.002;
                        foreach (var w in words)
                            if (hit.U >= w.U1 - tolerance && hit.U <= w.U2 + tolerance && hit.V >= w.V1 - tolerance && hit.V <= w.V2 + tolerance) { cursor = Cursors.IBeam; break; }
                    }
                }
            }
            if (!ReferenceEquals(ReaderContentHost.Cursor, cursor)) ReaderContentHost.Cursor = cursor;
        }

        // ── Highlight of the selection that follows scrolling and zoom ──

        private void RefreshTextSelectionVisuals()
        {
            if (_textSelection is not { } selection) return;
            double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
            for (int i = 0; i < selection.Rects.Count && i < _textSelectionVisuals.Count; i++)
            {
                var (u1, v1, u2, v2) = selection.Rects[i];
                var box = (System.Windows.Shapes.Rectangle)_textSelectionVisuals[i];
                if (!TryPageToLayer(selection.Row, u1, v1, out Point p1) || !TryPageToLayer(selection.Row, u2, v2, out Point p2)) { box.Visibility = Visibility.Collapsed; continue; }
                box.Visibility = Visibility.Visible;
                double x = Math.Min(p1.X, p2.X), y = Math.Min(p1.Y, p2.Y), w = Math.Abs(p2.X - p1.X), h = Math.Abs(p2.Y - p1.Y);
                if (Math.Abs(box.Width - w) > 0.25 || double.IsNaN(box.Width)) box.Width = w;
                if (Math.Abs(box.Height - h) > 0.25 || double.IsNaN(box.Height)) box.Height = h;
                SetCanvasIfChanged(box, x, y);
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x + w); bottom = Math.Max(bottom, y + h);
            }
            if (right < left) { _textSelectionAnchor = null; TextSelectionBar.Visibility = Visibility.Collapsed; return; }
            _textSelectionAnchor = new Rect(left, top, right - left, bottom - top);
            PositionFloatingBars();
        }

        private void ShowTextSelectionBar()
        {
            TextSelectionBar.Visibility = Visibility.Visible;
            PositionFloatingBars();
        }

        // ── Bar buttons ───────────────────────────────────────────────

        private void SelBarHighlight_Click(object sender, RoutedEventArgs e) => MarkSelection(QuickAnnotationKind.Highlight);
        private void SelBarUnderline_Click(object sender, RoutedEventArgs e) => MarkSelection(QuickAnnotationKind.Underline);
        private void SelBarStrike_Click(object sender, RoutedEventArgs e) => MarkSelection(QuickAnnotationKind.StrikeOut);
        private void SelBarSquiggly_Click(object sender, RoutedEventArgs e) => MarkSelection(QuickAnnotationKind.Squiggly);
        private void SelBarCopy_Click(object sender, RoutedEventArgs e) => CopySelectedText();

        private void MarkSelection(QuickAnnotationKind kind)
        {
            if (_textSelection is not { } selection || selection.Rects.Count == 0) return;
            if (!Controls.PdfPermissionDialog.Require(this, new[] { selection.Row.SourcePath }, PdfPermissionOperation.Annotate)) return;
            var rects = selection.Rects;
            var spec = new QuickAnnotationSpec(NewAnnotationName(), kind, selection.Row.PageNumber,
                rects.Min(r => r.U1), rects.Min(r => r.V1), rects.Max(r => r.U2), rects.Max(r => r.V2), "") { Format = PdfQuickAnnotationService.EncodeTextHighlight(rects) };
            CommitAnnotationChange(selection.Row, new QuickAnnotationChange(null, spec), "Add " + KindLabel(kind));
            ClearTextSelection();
        }

        /// <summary>A note anchored at the selected text: its icon sits at the end of the selection (no highlight is added), the text goes in the usual note card.</summary>
        private void SelBarNote_Click(object sender, RoutedEventArgs e)
        {
            if (_textSelection is not { } selection || selection.Rects.Count == 0) return;
            if (!Controls.PdfPermissionDialog.Require(this, new[] { selection.Row.SourcePath }, PdfPermissionOperation.Annotate)) return;
            var last = selection.Rects[^1];
            var hit = new PageHit(selection.Row, Math.Min(0.97, last.U2 + 0.004), Math.Max(0, last.V1 - 0.002));
            ClearTextSelection();
            ComposeNote(hit);
        }
    }
}
