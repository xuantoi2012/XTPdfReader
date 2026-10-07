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
    /// A selected rectangle / cloud / oval shows a small "T" at the middle of each side. Click one and a text box (like the Typewriter's) opens just outside
    /// that side; on Enter the text and the shape become one group: they move, delete and undo together.
    /// </summary>
    public partial class ReaderWindow
    {
        private const double ShapeTextGap = 6, ShapeTextSideWidth = 140;

        private static bool IsClosedShape(QuickAnnotationSpec spec)
            => spec.Kind == QuickAnnotationKind.Shape && ShapeStyle.Decode(spec.Format).Type is ShapeStyle.Rect or ShapeStyle.Cloud or ShapeStyle.Oval;

        private bool IsShapeTextHandle(DependencyObject? source)
            => IsInside(source, ShapeTTop) || IsInside(source, ShapeTRight) || IsInside(source, ShapeTBottom) || IsInside(source, ShapeTLeft);

        private void UpdateShapeTextHandles()
        {
            var handles = new[] { ShapeTTop, ShapeTRight, ShapeTBottom, ShapeTLeft };
            if (_selAnn is not { } spec || !IsClosedShape(spec) || _selRow is not { } row || _annotationEditor != null || _annMove is { Moved: true } ||
                !TryPageToLayer(row, spec.U1, spec.V1, out Point a) || !TryPageToLayer(row, spec.U2, spec.V2, out Point b))
            {
                foreach (var handle in handles) handle.Visibility = Visibility.Collapsed;
                return;
            }
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
            if (sender is not FrameworkElement { Tag: string side } || _selAnn is not { } shape || !IsClosedShape(shape) || _selRow is not { } row) return;
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
            double lineHeight = baseFormat.Size * 1.2 + 4, shapeWidth = (shape.U2 - shape.U1) * dw;
            double boxWidth = side is "Top" or "Bottom" ? Math.Max(90, shapeWidth) : ShapeTextSideWidth;
            double u, v;
            int align = 0;
            switch (side)
            {
                case "Top": u = shape.U1 + (shapeWidth - boxWidth) / 2 / dw; v = shape.V1 - (ShapeTextGap + lineHeight) / dh; align = 1; break;
                case "Bottom": u = shape.U1 + (shapeWidth - boxWidth) / 2 / dw; v = shape.V2 + ShapeTextGap / dh; align = 1; break;
                case "Left": u = shape.U1 - (ShapeTextGap + boxWidth) / dw; v = (shape.V1 + shape.V2) / 2 - lineHeight / 2 / dh; align = 2; break;
                default: u = shape.U2 + ShapeTextGap / dw; v = (shape.V1 + shape.V2) / 2 - lineHeight / 2 / dh; break;
            }
            u = Math.Clamp(u, 0, Math.Max(0, 1 - boxWidth / dw));
            v = Math.Clamp(v, 0, Math.Max(0, 1 - lineHeight / dh));
            SelectAnnotation(row, shape); // keep the shape selected while its text is typed
            await OpenAnnotationEditorAsync(new PageHit(row, u, v), QuickAnnotationKind.Typewriter, null, shape, baseFormat with { Width = boxWidth, Align = align });
        }
    }
}
