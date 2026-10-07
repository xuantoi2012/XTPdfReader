using System;
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
    /// A selected rectangle / cloud / oval shows a small "T" at the middle of each side; a selected line / arrow shows one beyond each end. Click one and a text
    /// box (like the Typewriter's) opens just outside that side or end; on Enter the text and the shape become one group: they move, delete and undo together.
    /// </summary>
    public partial class ReaderWindow
    {
        private const double ShapeTextGap = 6, ShapeTextSideWidth = 140;

        private static bool IsClosedShape(QuickAnnotationSpec spec)
            => spec.Kind == QuickAnnotationKind.Shape && ShapeStyle.Decode(spec.Format).Type is ShapeStyle.Rect or ShapeStyle.Cloud or ShapeStyle.Oval;

        private static bool IsLineShape(QuickAnnotationSpec spec) => spec.Kind == QuickAnnotationKind.Shape && ShapeStyle.Decode(spec.Format).IsLine;

        /// <summary>Shapes that can carry a text box: closed shapes (4 sides) and lines / arrows (2 ends).</summary>
        private static bool CanCarryText(QuickAnnotationSpec spec) => IsClosedShape(spec) || IsLineShape(spec);

        private bool IsShapeTextHandle(DependencyObject? source)
            => IsInside(source, ShapeTTop) || IsInside(source, ShapeTRight) || IsInside(source, ShapeTBottom) || IsInside(source, ShapeTLeft);

        private void UpdateShapeTextHandles()
        {
            var handles = new[] { ShapeTTop, ShapeTRight, ShapeTBottom, ShapeTLeft };
            if (_selAnn is not { } spec || !CanCarryText(spec) || _selRow is not { } row || _annotationEditor != null || _annMove is { Moved: true } ||
                !TryPageToLayer(row, spec.U1, spec.V1, out Point a) || !TryPageToLayer(row, spec.U2, spec.V2, out Point b))
            {
                foreach (var handle in handles) handle.Visibility = Visibility.Collapsed;
                return;
            }
            if (IsLineShape(spec))
            {
                // Line / arrow: one T beyond each end, along the line (the left / right controls are re-tagged "Tail" / "Head").
                var (tail, head) = GetLineEndpointsPixel(row, spec, ShapeStyle.Decode(spec.Format));
                ShapeTLeft.Tag = "Tail"; ShapeTRight.Tag = "Head";
                ShapeTTop.Visibility = ShapeTBottom.Visibility = Visibility.Collapsed;
                ShapeTLeft.Visibility = ShapeTRight.Visibility = Visibility.Visible;
                (double dx, double dy) = (tail.X - head.X, tail.Y - head.Y);
                double length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy)), ux = dx / length, uy = dy / length;
                const double beyond = 30; // clear of the end grips
                SetCanvasIfChanged(ShapeTLeft, tail.X + ux * beyond - 9, tail.Y + uy * beyond - 9);
                SetCanvasIfChanged(ShapeTRight, head.X - ux * beyond - 9, head.Y - uy * beyond - 9);
                return;
            }
            ShapeTLeft.Tag = "Left"; ShapeTRight.Tag = "Right";
            double left = Math.Min(a.X, b.X), right = Math.Max(a.X, b.X), top = Math.Min(a.Y, b.Y), bottom = Math.Max(a.Y, b.Y);
            double cx = (left + right) / 2, cy = (top + bottom) / 2;
            foreach (var handle in handles) handle.Visibility = Visibility.Visible;
            SetCanvasIfChanged(ShapeTTop, cx - 9, top - 30);
            SetCanvasIfChanged(ShapeTBottom, cx - 9, bottom + 12);
            SetCanvasIfChanged(ShapeTLeft, left - 30, cy - 9);
            SetCanvasIfChanged(ShapeTRight, right + 12, cy - 9);
        }

        private async void ShapeT_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement { Tag: string side } || _selAnn is not { } shape || !CanCarryText(shape) || _selRow is not { } row) return;
            await BeginShapeTextAsync(row, shape, side);
        }

        /// <summary>Opens a Typewriter box next to one side of the shape: above / below (as wide as the shape, centred) or left / right (fixed width).</summary>
        private async Task BeginShapeTextAsync(PageRow row, QuickAnnotationSpec shape, string side)
        {
            if (!Controls.PdfPermissionDialog.Require(this, new[] { row.SourcePath }, PdfPermissionOperation.Annotate)) return;
            var page = await LoadPageAnnotationsAsync(row);
            if (page == null) return;
            double dw = page.Geometry.DisplayWidth, dh = page.Geometry.DisplayHeight;
            var baseFormat = TextFormat.Decode(AppSettings.TypewriterFormat);
            double lineHeight = baseFormat.Size * 1.2 + 4;
            // The box goes next to a rectangle (the shape's own bounds) or, for a line / arrow, next to one of its ends (a zero-size rectangle at that end,
            // on the side the line points to).
            double u1 = shape.U1, v1 = shape.V1, u2 = shape.U2, v2 = shape.V2;
            bool lineEnd = side is "Tail" or "Head";
            if (lineEnd)
            {
                var style = ShapeStyle.Decode(shape.Format);
                double w = (shape.U2 - shape.U1) * dw, h = (shape.V2 - shape.V1) * dh, pad = ShapeStyle.LinePad(style.Width * ShapeStyle.PageScale(dw));
                (double X, double Y)[] corners = { (pad, pad), (w - pad, pad), (pad, h - pad), (w - pad, h - pad) };
                var tail = corners[style.Corner]; var head = corners[3 - style.Corner];
                var end = side == "Tail" ? tail : head; var other = side == "Tail" ? head : tail;
                double dx = end.X - other.X, dy = end.Y - other.Y;
                u1 = u2 = shape.U1 + end.X / dw; v1 = v2 = shape.V1 + end.Y / dh;
                side = Math.Abs(dx) >= Math.Abs(dy) ? (dx >= 0 ? "Right" : "Left") : (dy >= 0 ? "Bottom" : "Top");
            }
            double shapeWidth = (u2 - u1) * dw;
            double boxWidth = !lineEnd && side is "Top" or "Bottom" ? Math.Max(90, shapeWidth) : ShapeTextSideWidth;
            double u, v;
            int align = 0;
            switch (side)
            {
                case "Top": u = u1 + (shapeWidth - boxWidth) / 2 / dw; v = v1 - (ShapeTextGap + lineHeight) / dh; align = 1; break;
                case "Bottom": u = u1 + (shapeWidth - boxWidth) / 2 / dw; v = v2 + ShapeTextGap / dh; align = 1; break;
                case "Left": u = u1 - (ShapeTextGap + boxWidth) / dw; v = (v1 + v2) / 2 - lineHeight / 2 / dh; align = 2; break;
                default: u = u2 + ShapeTextGap / dw; v = (v1 + v2) / 2 - lineHeight / 2 / dh; break;
            }
            u = Math.Clamp(u, 0, Math.Max(0, 1 - boxWidth / dw));
            v = Math.Clamp(v, 0, Math.Max(0, 1 - lineHeight / dh));
            SelectAnnotation(row, shape); // keep the shape selected while its text is typed
            // Above a rectangle / line end the box grows upward from just over the shape (its bottom edge stays put).
            double? anchorBottom = side == "Top" ? Math.Clamp(v1 - ShapeTextGap / dh, 0, 1) : null;
            if (anchorBottom is { } bottom) v = Math.Max(0, bottom - lineHeight / dh);
            _nextEditorAnchorBottomV = anchorBottom;
            await OpenAnnotationEditorAsync(new PageHit(row, u, v), QuickAnnotationKind.Typewriter, null, shape, baseFormat with { Width = boxWidth, Align = align });
        }
    }
}
