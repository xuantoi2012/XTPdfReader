using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Services.TextEdit;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp.Controls
{
    /// <summary>
    /// Shows the text edits that wait for Ctrl+S (<see cref="TextEditPendingStore"/>) over the page image: the old run is covered with the colour of the page next to it and the
    /// new text is drawn at the same baseline, size and colour. Used by the viewer and by the thumbnails (through <see cref="AnnotationLayer"/>), so an edit shows everywhere at once.
    /// </summary>
    internal static class TextEditLayer
    {
        public static void Draw(DrawingContext dc, PageRow row, Rect page, double dpi)
        {
            if (page.Width <= 0 || page.Height <= 0) return;
            var edits = TextEditPendingStore.Page(row.SourcePath, row.PageNumber);
            if (edits.Count == 0) return;
            dc.PushClip(new RectangleGeometry(page));
            foreach (var edit in edits)
            {
                if (edit.PageWidth <= 0 || edit.PageHeight <= 0) continue;
                double sx = page.Width / edit.PageWidth, sy = page.Height / edit.PageHeight;
                var run = edit.Original;
                var cover = new Rect(page.X + (run.X0 - 1) * sx, page.Y + (run.Y0 - 0.5) * sy, (run.X1 - run.X0 + 2) * sx, Math.Max(1, (run.Y1 - run.Y0 + 1) * sy));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(run.Background >> 16), (byte)(run.Background >> 8), (byte)run.Background)), null, cover);
                if (edit.NewText.Length == 0) continue;
                var typeface = new Typeface(new FontFamily("Arial"), (run.Flags & 2) != 0 ? FontStyles.Italic : FontStyles.Normal,
                    (run.Flags & 16) != 0 ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                var brush = new SolidColorBrush(Color.FromRgb((byte)(run.Color >> 16), (byte)(run.Color >> 8), (byte)run.Color));
                var text = new FormattedText(edit.NewText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, Math.Max(1, run.Size * sy), brush, Math.Max(1, dpi));
                dc.DrawText(text, new Point(page.X + run.OriginX * sx, page.Y + run.OriginY * sy - text.Baseline));
            }
            dc.Pop();
        }
    }
}
