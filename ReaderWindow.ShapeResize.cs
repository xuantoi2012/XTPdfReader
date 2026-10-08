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
        private Rectangle[] LineGrips => new[] { GripLineA, GripLineB };

        private sealed class ShapeResizeDrag
        {
            public required PageRow Row;
            public required QuickAnnotationSpec Spec;
            public required ShapeStyle Style;
            public required string Handle;
            public double U1, V1, U2, V2;
        }

        private ShapeResizeDrag? _shapeResizeDrag;

        /// <summary>A stamp or signature made by this app (its definition travels with it), so it can be drawn again at another size.</summary>
        private static bool IsResizableStamp(QuickAnnotationSpec spec)
            => spec.Kind == QuickAnnotationKind.Stamp && (spec.Format.Contains('\u001f') || spec.Text.Contains('\u001f'));

        /// <summary>Shows/positions the grips for the current selection — called from UpdateSelectionVisual whenever it runs.</summary>
        private void UpdateShapeGrips()
        {
            if (_shapeResizeDrag != null || _lineResizeDrag != null || _annotationEditor != null || _annMove != null ||
                _selAnn is not { } selected || _selRow is not { } row || selected.Kind is not (QuickAnnotationKind.Shape or QuickAnnotationKind.Callout) && !IsResizableStamp(selected))
            {
                foreach (var g in ShapeGrips) g.Visibility = Visibility.Collapsed;
                foreach (var g in LineGrips) g.Visibility = Visibility.Collapsed;
                return;
            }

            // Callout (hộp chữ có đường dẫn baked-in): dùng lại đúng 8 grip hộp của Shape — kéo-giãn hộp, đường dẫn
            // tự nối lại vào điểm chỉ cũ (không đổi) khi AddCallout vẽ lại appearance từ U1..V2 mới.
            var style = selected.Kind == QuickAnnotationKind.Shape ? ShapeStyle.Decode(selected.Format) : ShapeStyle.Default;
            if (!style.IsLine && TryPageToLayer(row, selected.U1, selected.V1, out Point a) &&
                TryPageToLayer(row, selected.U2, selected.V2, out Point b))
            {
                PlaceGrips(a, b);
                foreach (var g in ShapeGrips) g.Visibility = Visibility.Visible;
            }
            else foreach (var g in ShapeGrips) g.Visibility = Visibility.Collapsed;

            if (style.IsLine)
            {
                var (tail, head) = GetLineEndpointsPixel(row, selected, style);
                Place(GripLineA, tail.X, tail.Y);
                Place(GripLineB, head.X, head.Y);
                foreach (var g in LineGrips) g.Visibility = Visibility.Visible;
            }
            else foreach (var g in LineGrips) g.Visibility = Visibility.Collapsed;

            // Callout: thêm 1 grip ở đầu mũi tên (như Foxit) — kéo để đổi điểm chỉ, hộp đứng yên.
            if (selected.Kind == QuickAnnotationKind.Callout)
            {
                var tip = PdfQuickAnnotationService.DecodeCallout(selected.Format);
                if (TryPageToLayer(row, tip.TipU, tip.TipV, out Point tipPx))
                {
                    Place(GripLineA, tipPx.X, tipPx.Y);
                    GripLineA.Visibility = Visibility.Visible;
                }
            }
        }

        /// <summary>Where a Line/Arrow's 2 real endpoints sit on screen — same corners+pad math as drawing/hit-testing, just
        /// converted to display-layer pixels instead of PDF user space.</summary>
        private (Point Tail, Point Head) GetLineEndpointsPixel(PageRow row, QuickAnnotationSpec spec, ShapeStyle style)
        {
            var geometry = GetCachedPageAnnotations(row)?.Geometry ?? new PdfPageGeometry(0, 0, 612, 792, 0);
            double dw = geometry.DisplayWidth, dh = geometry.DisplayHeight;
            double w = (spec.U2 - spec.U1) * dw, h = (spec.V2 - spec.V1) * dh;
            double pad = ShapeStyle.LinePad(style.Width * ShapeStyle.PageScale(dw));
            (double X, double Y)[] corners = { (pad, pad), (w - pad, pad), (pad, h - pad), (w - pad, h - pad) };
            var s = corners[style.Corner];
            var e = corners[3 - style.Corner];
            TryPageToLayer(row, spec.U1 + s.X / dw, spec.V1 + s.Y / dh, out Point tail);
            TryPageToLayer(row, spec.U1 + e.X / dw, spec.V1 + e.Y / dh, out Point head);
            return (tail, head);
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
            if (_selAnn is not { } spec || _selRow is not { } row || sender is not Rectangle grip ||
                spec.Kind is not (QuickAnnotationKind.Shape or QuickAnnotationKind.Callout) && !IsResizableStamp(spec)) return;
            e.Handled = true;
            // Callout không có ShapeStyle thật (format là "C|tipU,tipV|..." ) — chỉ cần 1 kiểu bất kỳ để vẽ khung xem trước lúc kéo.
            var style = spec.Kind == QuickAnnotationKind.Shape ? ShapeStyle.Decode(spec.Format) : new ShapeStyle(ShapeStyle.Rect, "#5B9BD5", 1.2, 0);
            _shapeResizeDrag = new ShapeResizeDrag
            {
                Row = row, Spec = spec, Style = style, Handle = (string)grip.Tag,
                U1 = spec.U1, V1 = spec.V1, U2 = spec.U2, V2 = spec.V2
            };
            Controls.AnnotationLayer.Edit.HiddenName = spec.Name;
            HoldDragCursor(grip.Tag is "NW" or "SE" ? Cursors.SizeNWSE : grip.Tag is "NE" or "SW" ? Cursors.SizeNESW : grip.Tag is "N" or "S" ? Cursors.SizeNS : Cursors.SizeWE);
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
            if (drag.Spec.Kind == QuickAnnotationKind.Stamp && drag.Handle.Length == 2 && GetCachedPageAnnotations(drag.Row)?.Geometry is { } geometry)
            {
                // a signature keeps its shape: the corner decides the width, the height follows
                double aspect = (drag.Spec.U2 - drag.Spec.U1) * geometry.DisplayWidth / Math.Max(1e-6, (drag.Spec.V2 - drag.Spec.V1) * geometry.DisplayHeight);
                double height = (drag.U2 - drag.U1) * geometry.DisplayWidth / aspect / geometry.DisplayHeight;
                if (drag.Handle.Contains('N')) drag.V1 = drag.V2 - height; else drag.V2 = drag.V1 + height;
            }

            if (!TryPageToLayer(drag.Row, drag.U1, drag.V1, out Point a) || !TryPageToLayer(drag.Row, drag.U2, drag.V2, out Point b)) return true;
            ShowShapePreview(drag.Style, a, b, ShapePixelsPerPoint(drag.Row), ShapePageScale(drag.Row));
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
            ApplyToolCursor();

            var s = drag.Spec;
            if (Math.Abs(drag.U1 - s.U1) > 1e-9 || Math.Abs(drag.V1 - s.V1) > 1e-9 || Math.Abs(drag.U2 - s.U2) > 1e-9 || Math.Abs(drag.V2 - s.V2) > 1e-9)
            {
                var changed = Regenerated(s) with { U1 = drag.U1, V1 = drag.V1, U2 = drag.U2, V2 = drag.V2 };
                _selAnn = changed;
                string kind = s.Kind == QuickAnnotationKind.Callout ? "callout" : s.Kind == QuickAnnotationKind.Stamp ? "stamp" : ShapeStyle.Decode(s.Format).Type.ToLowerInvariant();
                CommitAnnotationChange(drag.Row, new QuickAnnotationChange(s, changed), "Resize " + kind);
            }
            ReaderContinuousView.Redraw();
            UpdateSelectionVisual();
            return true;
        }

        // ── Line/Arrow: 2 endpoint grips, dragged freely (not tied to a rectangle) ─────

        private sealed class LineResizeDrag
        {
            public required PageRow Row;
            public required QuickAnnotationSpec Spec;
            public required ShapeStyle Style;
            public required bool DraggingTail; // true = GripLineA (tail) moves, false = GripLineB (head/arrow tip) moves
            public required double FixedU, FixedV; // the OTHER endpoint, unchanged through the drag
            public double U1, V1, U2, V2;
            public int Corner;
        }

        private LineResizeDrag? _lineResizeDrag;

        private void GripLine_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_selAnn is { Kind: QuickAnnotationKind.Callout } callout && _selRow is { } calloutRow && sender == GripLineA)
            {
                e.Handled = true;
                BeginCalloutTipDrag(calloutRow, callout);
                return;
            }
            if (_selAnn is not { Kind: QuickAnnotationKind.Shape } spec || _selRow is not { } row || sender is not Rectangle grip) return;
            var style = ShapeStyle.Decode(spec.Format);
            if (!style.IsLine) return;
            e.Handled = true;

            var geometry = GetCachedPageAnnotations(row)?.Geometry ?? new PdfPageGeometry(0, 0, 612, 792, 0);
            double dw = geometry.DisplayWidth, dh = geometry.DisplayHeight;
            double w = (spec.U2 - spec.U1) * dw, h = (spec.V2 - spec.V1) * dh;
            double pad = ShapeStyle.LinePad(style.Width * ShapeStyle.PageScale(dw));
            (double X, double Y)[] corners = { (pad, pad), (w - pad, pad), (pad, h - pad), (w - pad, h - pad) };
            bool draggingTail = (string)grip.Tag == "LineA";
            var fixedLocal = draggingTail ? corners[3 - style.Corner] : corners[style.Corner]; // the endpoint that stays put

            HoldDragCursor(Cursors.Cross);
            _lineResizeDrag = new LineResizeDrag
            {
                Row = row, Spec = spec, Style = style, DraggingTail = draggingTail,
                FixedU = spec.U1 + fixedLocal.X / dw, FixedV = spec.V1 + fixedLocal.Y / dh,
                U1 = spec.U1, V1 = spec.V1, U2 = spec.U2, V2 = spec.V2, Corner = style.Corner
            };
            Controls.AnnotationLayer.Edit.HiddenName = spec.Name;
            ReaderContentHost.CaptureMouse();
            ReaderContinuousView.Redraw();
        }

        /// <summary>Called from the shared PreviewMouseMove alongside the box resize and move/draw drags.</summary>
        private bool UpdateLineResize(Point pointInHost)
        {
            if (_lineResizeDrag is not { } drag) return false;
            if (!TryGetPagePoint(drag.Row, pointInHost, clamp: true, out var current)) return true;

            // "Dragged" plays the role CommitShapeAsync calls the drag-start point; "fixed" is the drag-end point —
            // same corner formula either way, so tail and head both go through it, just swapped.
            double draggedU = current.U, draggedV = current.V, fixedU = drag.FixedU, fixedV = drag.FixedV;
            bool draggedIsTop = draggedV <= fixedV, draggedIsLeft = draggedU <= fixedU;
            drag.Corner = draggedIsTop ? (draggedIsLeft ? 0 : 1) : (draggedIsLeft ? 2 : 3);
            if (!drag.DraggingTail) drag.Corner = 3 - drag.Corner; // dragging the head: roles of "start"/"end" flip

            double u1 = Math.Min(draggedU, fixedU), u2 = Math.Max(draggedU, fixedU), v1 = Math.Min(draggedV, fixedV), v2 = Math.Max(draggedV, fixedV);
            var geometry = GetCachedPageAnnotations(drag.Row)?.Geometry ?? new PdfPageGeometry(0, 0, 612, 792, 0);
            double dw = geometry.DisplayWidth, dh = geometry.DisplayHeight;
            double pad = ShapeStyle.LinePad(drag.Style.Width * ShapeStyle.PageScale(dw));
            drag.U1 = Math.Max(0, u1 - pad / dw); drag.U2 = Math.Min(1, u2 + pad / dw);
            drag.V1 = Math.Max(0, v1 - pad / dh); drag.V2 = Math.Min(1, v2 + pad / dh);

            if (!TryPageToLayer(drag.Row, draggedU, draggedV, out Point draggedPx) || !TryPageToLayer(drag.Row, fixedU, fixedV, out Point fixedPx)) return true;
            var (a, b) = drag.DraggingTail ? (draggedPx, fixedPx) : (fixedPx, draggedPx); // a = tail, b = head (arrow tip)
            ShowShapePreview(drag.Style, a, b, ShapePixelsPerPoint(drag.Row), ShapePageScale(drag.Row));
            Place(drag.DraggingTail ? GripLineA : GripLineB, draggedPx.X, draggedPx.Y);
            Place(drag.DraggingTail ? GripLineB : GripLineA, fixedPx.X, fixedPx.Y);
            return true;
        }

        private bool FinishLineResize()
        {
            if (_lineResizeDrag is not { } drag) return false;
            _lineResizeDrag = null;
            HideShapePreview();
            Controls.AnnotationLayer.Edit.HiddenName = null;
            if (ReaderContentHost.IsMouseCaptured) ReaderContentHost.ReleaseMouseCapture();
            ApplyToolCursor();

            var s = drag.Spec;
            var newStyle = drag.Style with { Corner = drag.Corner };
            var changed = Regenerated(s) with { U1 = drag.U1, V1 = drag.V1, U2 = drag.U2, V2 = drag.V2, Format = newStyle.Encode() };
            _selAnn = changed;
            CommitAnnotationChange(drag.Row, new QuickAnnotationChange(s, changed), "Resize " + newStyle.Type.ToLowerInvariant());
            ReaderContinuousView.Redraw();
            UpdateSelectionVisual();
            return true;
        }
    }
}
