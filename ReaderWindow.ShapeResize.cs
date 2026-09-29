using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using XTPdfMergeApp.Services;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp
{
    /// <summary>
    /// Resize handles on a selected Shape (Foxit style): 8 grips at the corners/edges of the bounding box, dragged
    /// directly — like drawing, the shape redraws live (<see cref="ShowShapePreview"/>) while the file's own
    /// annotation is hidden (<see cref="Controls.AnnotationLayer.EditState.HiddenName"/>), regenerated on release.
    /// Line/Arrow keep move-only for now (their 2 endpoints don't map onto a plain bounding box the same way).
    /// </summary>
    public partial class ReaderWindow
    {
        private Rectangle[] ShapeGrips => new[] { GripNW, GripN, GripNE, GripE, GripSE, GripS, GripSW, GripW };

        private sealed class ShapeResizeDrag
        {
            public required PageRow Row;
            public required QuickAnnotationSpec Spec;
            public required ShapeStyle Style;
            public required string Handle;
            public double U1, V1, U2, V2;
        }

        private ShapeResizeDrag? _shapeResizeDrag;

        /// <summary>Shows/positions the grips for the current selection — called from UpdateSelectionVisual whenever it runs.</summary>
        private void UpdateShapeGrips()
        {
            bool show = _shapeResizeDrag == null && _annotationEditor == null && _annMove == null
                && _selAnn is { Kind: QuickAnnotationKind.Shape } spec && !ShapeStyle.Decode(spec.Format).IsLine
                && _selRow is { } row && TryPageToLayer(row, spec.U1, spec.V1, out Point tl) && TryPageToLayer(row, spec.U2, spec.V2, out Point br);
            if (!show)
            {
                foreach (var g in ShapeGrips) g.Visibility = Visibility.Collapsed;
                return;
            }

            TryPageToLayer(_selRow!, ((QuickAnnotationSpec)_selAnn!).U1, ((QuickAnnotationSpec)_selAnn!).V1, out Point a);
            TryPageToLayer(_selRow!, ((QuickAnnotationSpec)_selAnn!).U2, ((QuickAnnotationSpec)_selAnn!).V2, out Point b);
            PlaceGrips(a, b);
            foreach (var g in ShapeGrips) g.Visibility = Visibility.Visible;
        }

        private void PlaceGrips(Point a, Point b)
        {
            double left = Math.Min(a.X, b.X), top = Math.Min(a.Y, b.Y), right = Math.Max(a.X, b.X), bottom = Math.Max(a.Y, b.Y);
            double midX = (left + right) / 2, midY = (top + bottom) / 2;
            Place(GripNW, left, top); Place(GripN, midX, top); Place(GripNE, right, top);
            Place(GripW, left, midY); Place(GripE, right, midY);
            Place(GripSW, left, bottom); Place(GripS, midX, bottom); Place(GripSE, right, bottom);
        }

        private static void Place(Rectangle grip, double cx, double cy)
        {
            Canvas.SetLeft(grip, cx - grip.Width / 2);
            Canvas.SetTop(grip, cy - grip.Height / 2);
        }

        private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_selAnn is not { Kind: QuickAnnotationKind.Shape } spec || _selRow is not { } row || sender is not Rectangle grip) return;
            e.Handled = true;
            _shapeResizeDrag = new ShapeResizeDrag
            {
                Row = row, Spec = spec, Style = ShapeStyle.Decode(spec.Format), Handle = (string)grip.Tag,
                U1 = spec.U1, V1 = spec.V1, U2 = spec.U2, V2 = spec.V2
            };
            Controls.AnnotationLayer.Edit.HiddenName = spec.Name;
            ReaderContentHost.CaptureMouse();
            ReaderContinuousView.Redraw();
        }

        /// <summary>Called from the shared PreviewMouseMove alongside move/draw drags.</summary>
        private bool UpdateShapeResize(Point pointInHost)
        {
            if (_shapeResizeDrag is not { } drag) return false;
            if (!TryGetPagePoint(drag.Row, pointInHost, clamp: true, out var current)) return true;
            const double min = 0.004; // ~a few points on a normal page — never lets the box invert or vanish
            if (drag.Handle.Contains('W')) drag.U1 = Math.Min(current.U, drag.U2 - min);
            if (drag.Handle.Contains('E')) drag.U2 = Math.Max(current.U, drag.U1 + min);
            if (drag.Handle.Contains('N')) drag.V1 = Math.Min(current.V, drag.V2 - min);
            if (drag.Handle.Contains('S')) drag.V2 = Math.Max(current.V, drag.V1 + min);

            if (!TryPageToLayer(drag.Row, drag.U1, drag.V1, out Point a) || !TryPageToLayer(drag.Row, drag.U2, drag.V2, out Point b)) return true;
            ShowShapePreview(drag.Style, a, b, ShapePixelsPerPoint(drag.Row));
            PlaceGrips(a, b);
            return true;
        }

        private bool FinishShapeResize()
        {
            if (_shapeResizeDrag is not { } drag) return false;
            _shapeResizeDrag = null;
            HideShapePreview();
            Controls.AnnotationLayer.Edit.HiddenName = null;
            if (ReaderContentHost.IsMouseCaptured) ReaderContentHost.ReleaseMouseCapture();

            var s = drag.Spec;
            if (Math.Abs(drag.U1 - s.U1) > 1e-9 || Math.Abs(drag.V1 - s.V1) > 1e-9 || Math.Abs(drag.U2 - s.U2) > 1e-9 || Math.Abs(drag.V2 - s.V2) > 1e-9)
            {
                var changed = Regenerated(s) with { U1 = drag.U1, V1 = drag.V1, U2 = drag.U2, V2 = drag.V2 };
                _selAnn = changed;
                CommitAnnotationChange(drag.Row, new QuickAnnotationChange(s, changed), "Resize " + ShapeStyle.Decode(s.Format).Type.ToLowerInvariant());
            }
            ReaderContinuousView.Redraw();
            UpdateSelectionVisual();
            return true;
        }
    }
}
