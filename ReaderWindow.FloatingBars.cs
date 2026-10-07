using System;
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
            PlaceBar(TextFormatBar);
            PlaceBar(MarkupColorBar);
            PlaceBar(ShapeBar);
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
            return false;
        }

        private void PlaceBar(Border bar)
        {
            if (bar.Visibility != Visibility.Visible) return;
            if (!TryGetAnnotationAnchor(out var anchor)) { bar.Margin = BarHome; return; }
            bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double bw = bar.DesiredSize.Width, bh = bar.DesiredSize.Height;
            double hostW = ReaderContentHost.ActualWidth > 0 ? ReaderContentHost.ActualWidth : 4000, hostH = ReaderContentHost.ActualHeight > 0 ? ReaderContentHost.ActualHeight : 4000;
            double x = Math.Clamp(anchor.X, 6, Math.Max(6, hostW - bw - 6));
            double y = anchor.Y - bh - 10;
            if (y < 6) y = anchor.Bottom + 10;
            if (y + bh > hostH - 6) y = Math.Max(6, Math.Min(hostH - bh - 6, anchor.Y + 8)); // no room either side: tuck inside the annotation's top edge
            bar.Margin = new Thickness(x, y, 0, 0);
        }
    }
}
