using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace XTPdfMergeApp
{
    /// <summary>
    /// The property bars (text format, shape style, markup colour) float right beside the annotation they edit — above it, or below when
    /// there is no room — and follow it while it is moved, scrolled or zoomed. With no annotation selected (a tool is just armed) they sit
    /// in the top-left corner of the page area as the tool's defaults.
    /// </summary>
    public partial class ReaderWindow
    {
        private static readonly Thickness BarHome = new(14, 14, 0, 0);

        private void PositionFloatingBars()
        {
            // Several bars can be up at once (a shape and the text next to it, a selected markup with its colours and the copy bar): they are stacked
            // as one group beside the annotation instead of each sitting on the same spot.
            var bars = new[] { ShapeBar, TextFormatBar, MarkupColorBar, TextSelectionBar }.Where(bar => bar.Visibility == Visibility.Visible).ToList();
            if (bars.Count == 0) return;
            if (!TryGetAnnotationAnchor(out var anchor))
            {
                double top = BarHome.Top;
                foreach (var bar in bars)
                {
                    var home = new Thickness(BarHome.Left, Math.Round(top), 0, 0);
                    if (bar.Margin != home) bar.Margin = home;
                    top += BarSize(bar).Height + BarGap;
                }
                return;
            }
            double hostW = ReaderContentHost.ActualWidth > 0 ? ReaderContentHost.ActualWidth : 4000, hostH = ReaderContentHost.ActualHeight > 0 ? ReaderContentHost.ActualHeight : 4000;
            double total = bars.Sum(bar => BarSize(bar).Height) + BarGap * (bars.Count - 1);
            // A closed shape has "T" handles 12-30 px outside its sides: keep the group clear of them.
            double clearance = _selAnn != null && CanCarryText(_selAnn) && AnchorOverrideForTests == null ? 36 : 10;
            double y = anchor.Y - total - clearance;
            if (y < 6) y = anchor.Bottom + clearance;
            if (y + total > hostH - 6) y = Math.Max(6, Math.Min(hostH - total - 6, anchor.Y + 8)); // no room either side: tuck inside the annotation's top edge
            foreach (var bar in bars)
            {
                var size = BarSize(bar);
                double x = Math.Clamp(anchor.X, 6, Math.Max(6, hostW - size.Width - 6));
                var margin = new Thickness(Math.Round(x), Math.Round(y), 0, 0);
                if (bar.Margin != margin) bar.Margin = margin;
                y += size.Height + BarGap;
            }
        }

        private const double BarGap = 6;

        /// <summary>Use the laid-out size; calling Measure on every pass would invalidate the layout again and loop forever (LayoutUpdated -> place -> layout).</summary>
        private static Size BarSize(Border bar)
        {
            if (bar.ActualWidth <= 0 || bar.ActualHeight <= 0) bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return new Size(bar.ActualWidth > 0 ? bar.ActualWidth : bar.DesiredSize.Width, bar.ActualHeight > 0 ? bar.ActualHeight : bar.DesiredSize.Height);
        }

        /// <summary>Where the annotation being edited is, in page-area coordinates: the text editor while typing, else the selection box.</summary>
        /// <summary>Tests pin the anchor instead of driving a real selection.</summary>
        internal Rect? AnchorOverrideForTests;

        private bool TryGetAnnotationAnchor(out Rect rect)
        {
            rect = Rect.Empty;
            if (AnchorOverrideForTests is { } pinned) { rect = pinned; return true; }
            if (_annotationEditor != null && ReaderAnnotationEditor.Visibility == Visibility.Visible && ReaderAnnotationEditor.ActualWidth > 0)
            {
                rect = new Rect(Canvas.GetLeft(ReaderAnnotationEditor), Canvas.GetTop(ReaderAnnotationEditor), ReaderAnnotationEditor.ActualWidth, ReaderAnnotationEditor.ActualHeight);
                return !double.IsNaN(rect.X) && !double.IsNaN(rect.Y);
            }
            if (_selAnn != null && AnnotationSelectionBox.Visibility == Visibility.Visible)
            {
                rect = new Rect(Canvas.GetLeft(AnnotationSelectionBox), Canvas.GetTop(AnnotationSelectionBox), AnnotationSelectionBox.Width, AnnotationSelectionBox.Height);
                return !double.IsNaN(rect.X) && !double.IsNaN(rect.Y);
            }
            if (TextSelectionBar.Visibility == Visibility.Visible && _textSelectionAnchor is { } selected) { rect = selected; return true; }
            return false;
        }
    }
}
