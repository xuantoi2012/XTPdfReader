using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XTPdfMergeApp.Services;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp
{
    /// <summary>
    /// Typewriter text boxes in the style of Edge's PDF viewer: while typing and while selected the box has a dashed outline, a grip on its left
    /// (drag = move) and a circle on its right (drag = fix the width, the text then wraps). A new empty box shows "Start typing here…".
    /// </summary>
    public partial class ReaderWindow
    {
        private enum TextChromeDrag { None, GripWhileEditing, Width }

        private TextChromeDrag _chromeDrag;
        private double _chromeWidthPreview, _gripStartU, _gripStartV, _gripEditStartU, _gripEditStartV;

        private static double DisplayWidthOf(PageRow row) => GetCachedPageAnnotations(row) is { } page ? page.Geometry.DisplayWidth : 0;

        private bool IsTextChrome(DependencyObject? source) => IsInside(source, TextGrip) || IsInside(source, TextWidthHandle) || IsShapeTextHandle(source);

        private static void SetCanvasIfChanged(UIElement element, double left, double top)
        {
            if (double.IsNaN(Canvas.GetLeft(element)) || Math.Abs(Canvas.GetLeft(element) - left) > 0.25) Canvas.SetLeft(element, left);
            if (double.IsNaN(Canvas.GetTop(element)) || Math.Abs(Canvas.GetTop(element) - top) > 0.25) Canvas.SetTop(element, top);
        }

        /// <summary>Where the text box being typed in (or the selected one) is, in page-area coordinates.</summary>
        private bool TryGetTextChromeAnchor(out Rect rect)
        {
            rect = Rect.Empty;
            if (_annotationEditor is { Kind: QuickAnnotationKind.Typewriter } && ReaderAnnotationEditor.Visibility == Visibility.Visible)
            {
                double w = double.IsNaN(ReaderAnnotationEditor.Width) ? ReaderAnnotationEditor.ActualWidth : ReaderAnnotationEditor.Width;
                if (w <= 0 || double.IsNaN(Canvas.GetLeft(ReaderAnnotationEditor)) || double.IsNaN(Canvas.GetTop(ReaderAnnotationEditor))) return false;
                rect = new Rect(Canvas.GetLeft(ReaderAnnotationEditor), Canvas.GetTop(ReaderAnnotationEditor), w, Math.Max(ReaderAnnotationEditor.ActualHeight, 16));
                return true;
            }
            if (_annotationEditor == null && _selAnn is { Kind: QuickAnnotationKind.Typewriter } && AnnotationSelectionBox.Visibility == Visibility.Visible)
            {
                const double pad = 3; // the selection box is drawn 3 px larger than the annotation
                double width = AnnotationSelectionBox.Width - 2 * pad;
                if (_chromeDrag == TextChromeDrag.Width && _selRow is { } row && TryPageToLayer(row, _selAnn.U1, _selAnn.V1, out var a) &&
                    TryPageToLayer(row, _selAnn.U1 + 1.0 / Math.Max(1, DisplayWidthOf(row)), _selAnn.V1, out var b))
                    width = _chromeWidthPreview * (b - a).Length;
                rect = new Rect(Canvas.GetLeft(AnnotationSelectionBox) + pad, Canvas.GetTop(AnnotationSelectionBox) + pad, Math.Max(10, width), AnnotationSelectionBox.Height - 2 * pad);
                return true;
            }
            return false;
        }

        private void UpdateTextChrome()
        {
            if (!TryGetTextChromeAnchor(out var r))
            {
                TextChromeBox.Visibility = TextGrip.Visibility = TextWidthHandle.Visibility = TextHint.Visibility = Visibility.Collapsed;
                AnnotationSelectionBox.Opacity = 1;
                return;
            }
            // The dashed outline replaces the translucent selection box for text.
            AnnotationSelectionBox.Opacity = _annotationEditor == null ? 0 : 1;
            TextChromeBox.Visibility = TextGrip.Visibility = TextWidthHandle.Visibility = Visibility.Visible;
            TextChromeBox.Width = r.Width + 4;
            TextChromeBox.Height = r.Height + 4;
            SetCanvasIfChanged(TextChromeBox, r.X - 2, r.Y - 2);
            SetCanvasIfChanged(TextGrip, Math.Max(2, r.X - 19), r.Y + r.Height / 2 - 14);
            SetCanvasIfChanged(TextWidthHandle, r.Right - 4, r.Y + r.Height / 2 - 6);

            bool hint = _annotationEditor is { Kind: QuickAnnotationKind.Typewriter, Existing: null } && ReaderAnnotationEditor.Text.Length == 0;
            TextHint.Visibility = hint ? Visibility.Visible : Visibility.Collapsed;
            if (hint)
            {
                TextHint.FontSize = ReaderAnnotationEditor.FontSize;
                TextHint.FontFamily = ReaderAnnotationEditor.FontFamily;
                SetCanvasIfChanged(TextHint, r.X + 3, r.Y + 2);
            }
        }

        private void ReaderAnnotationEditor_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateTextChrome();
            PositionFloatingBars();
        }

        // ── Drag: grip (move) and circle (width) ──────────────────────

        private void TextGrip_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            Point point = e.GetPosition(ReaderContentHost);
            if (_annotationEditor is { Kind: QuickAnnotationKind.Typewriter } state)
            {
                if (!TryGetPagePoint(state.Row, point, clamp: true, out var p)) return;
                _chromeDrag = TextChromeDrag.GripWhileEditing;
                _gripStartU = p.U; _gripStartV = p.V; _gripEditStartU = state.U; _gripEditStartV = state.V;
                ReaderContentHost.CaptureMouse();
                return;
            }
            // Selected, not typing: the same move as dragging the text itself (also undoable).
            if (_selAnn is { Kind: QuickAnnotationKind.Typewriter } spec && _selRow is { } row && TryGetPagePoint(row, point, clamp: true, out var hit))
                BeginAnnotationMove(hit, spec);
        }

        private void TextWidthHandle_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_annotationEditor is not { Kind: QuickAnnotationKind.Typewriter } && _selAnn is not { Kind: QuickAnnotationKind.Typewriter }) return;
            _chromeDrag = TextChromeDrag.Width;
            _chromeWidthPreview = _annotationEditor != null ? _textFormat.Width : 0;
            if (_chromeWidthPreview <= 0 && TryGetTextChromeAnchor(out var r) && TryChromePixelsPerPoint(out double ppp)) _chromeWidthPreview = r.Width / ppp;
            ReaderContentHost.CaptureMouse();
        }

        private bool TryChromePixelsPerPoint(out double ppp)
        {
            ppp = 1;
            var row = _annotationEditor?.Row ?? _selRow;
            double displayWidth = _annotationEditor != null ? _annotationEditor.Geometry.DisplayWidth : row == null ? 0 : DisplayWidthOf(row);
            if (row == null || displayWidth <= 0 || !TryPageToLayer(row, 0, 0, out var p0) || !TryPageToLayer(row, 1.0 / displayWidth, 0, out var p1)) return false;
            ppp = Math.Max(0.05, (p1 - p0).Length);
            return true;
        }

        private bool UpdateTextChromeDrag(Point point)
        {
            if (_chromeDrag == TextChromeDrag.None) return false;
            var row = _annotationEditor?.Row ?? _selRow;
            if (row == null || !TryGetPagePoint(row, point, clamp: true, out var cur)) return true;
            if (_chromeDrag == TextChromeDrag.GripWhileEditing && _annotationEditor is { } state)
            {
                state.U = Math.Clamp(_gripEditStartU + cur.U - _gripStartU, 0, 0.98);
                state.V = Math.Clamp(_gripEditStartV + cur.V - _gripStartV, 0, 0.98);
                PositionAnnotationEditor();
                return true;
            }
            if (_chromeDrag == TextChromeDrag.Width)
            {
                double left = _annotationEditor?.U ?? _selAnn?.U1 ?? 0;
                double displayWidth = _annotationEditor != null ? _annotationEditor.Geometry.DisplayWidth : DisplayWidthOf(row);
                if (displayWidth <= 0) return true;
                double width = Math.Clamp((cur.U - left) * displayWidth, 40, Math.Max(40, (1 - left) * displayWidth));
                _chromeWidthPreview = width;
                if (_annotationEditor != null)
                {
                    _textFormat = _textFormat with { Width = width };
                    PositionAnnotationEditor();
                }
                else UpdateTextChrome();
            }
            return true;
        }

        private bool FinishTextChromeDrag()
        {
            if (_chromeDrag == TextChromeDrag.None) return false;
            var kind = _chromeDrag;
            _chromeDrag = TextChromeDrag.None;
            if (ReaderContentHost.IsMouseCaptured) ReaderContentHost.ReleaseMouseCapture();
            if (kind == TextChromeDrag.Width && _annotationEditor == null && _selAnn is { Kind: QuickAnnotationKind.Typewriter } spec && _selRow is { } row &&
                GetCachedPageAnnotations(row) is { } page)
            {
                var format = TextFormat.Decode(spec.Format) with { Width = _chromeWidthPreview };
                var changed = PdfQuickAnnotationService.WithMeasuredSize(Regenerated(spec) with { Format = format.Encode() }, page.Geometry);
                _selAnn = changed;
                CommitAnnotationChange(row, new QuickAnnotationChange(spec, changed), "Resize text box");
            }
            UpdateTextChrome();
            PositionFloatingBars();
            return true;
        }
    }
}
