using System;
using System.Windows;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Services.TextEdit;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp.Controls
{
    /// <summary>
    /// Shows the objects that wait to be removed (<see cref="ObjectDeletePendingStore"/>) gone: their own strokes are painted over in the colour of the paper (an image: its box), so what lies next to
    /// them is not touched. Used by the viewer and the thumbnails through <see cref="AnnotationLayer"/>. The paper is taken to be white.
    /// </summary>
    internal static class ObjectDeleteLayer
    {
        private static readonly Brush Paper = Freeze(new SolidColorBrush(Colors.White));
        private static T Freeze<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }

        public static void Draw(DrawingContext dc, PageRow row, Rect page)
        {
            if (page.Width <= 0 || page.Height <= 0) return;
            var objects = ObjectDeletePendingStore.Page(row.SourcePath, row.PageNumber);
            if (objects.Count == 0) return;
            dc.PushClip(new RectangleGeometry(page));
            foreach (var o in objects)
            {
                if (o.PageWidth <= 0 || o.PageHeight <= 0) continue;
                double sx = page.Width / o.PageWidth, sy = page.Height / o.PageHeight;
                if (o.IsImage || o.Paths.Count == 0)
                {
                    dc.DrawRectangle(Paper, null, new Rect(page.X + o.X0 * sx, page.Y + o.Y0 * sy, Math.Max(1, (o.X1 - o.X0) * sx), Math.Max(1, (o.Y1 - o.Y0) * sy)));
                    continue;
                }
                // a little wider than the stroke so no edge of it shows
                var pen = new Pen(Paper, Math.Max(2.5, (o.Width * 1.5 + 3.0) * Math.Min(sx, sy))) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    foreach (var line in o.Paths)
                    {
                        if (line.Count < 2) continue;
                        ctx.BeginFigure(new Point(page.X + line[0].X * sx, page.Y + line[0].Y * sy), false, false);
                        for (int i = 1; i < line.Count; i++) ctx.LineTo(new Point(page.X + line[i].X * sx, page.Y + line[i].Y * sy), true, false);
                    }
                }
                geometry.Freeze();
                dc.DrawGeometry(null, pen, geometry);
            }
            dc.Pop();
        }
    }
}
