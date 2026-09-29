using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp
{
    /// <summary>Drawing comments (Foxit style): Shapes button with a small palette, drag on the page to draw, colour/width bar, hit-testing on the outline.</summary>
    public partial class ReaderWindow
    {
        private ShapeStyle _shapeStyle = ShapeStyle.Decode(AppSettings.ShapeStyleSetting);
        private bool _shapeBarBuilt, _shapeLoading;

        private sealed record ShapeDrag(PageRow Row, double StartU, double StartV);
        private ShapeDrag? _shapeDrag;

        // ── Drawing ───────────────────────────────────────────────────

        private void BeginShapeDrag(PageHit hit)
        {
            if (!TryPageToLayer(hit.Row, hit.U, hit.V, out Point start)) return;
            _ = LoadPageAnnotationsAsync(hit.Row); // warm the geometry
            _shapeDrag = new ShapeDrag(hit.Row, hit.U, hit.V);
            ReaderCommentHoverPopup.Visibility = Visibility.Collapsed;
            ReaderContentHost.CaptureMouse();
            ReaderShapeRubber.Visibility = _shapeStyle.IsLine ? Visibility.Collapsed : Visibility.Visible;
            ReaderShapeRubberLine.Visibility = _shapeStyle.IsLine ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(ReaderShapeRubber, start.X);
            Canvas.SetTop(ReaderShapeRubber, start.Y);
            ReaderShapeRubber.Width = ReaderShapeRubber.Height = 0;
            ReaderShapeRubberLine.X1 = ReaderShapeRubberLine.X2 = start.X;
            ReaderShapeRubberLine.Y1 = ReaderShapeRubberLine.Y2 = start.Y;
        }

        private bool UpdateShapeDrag(Point pointInHost)
        {
            if (_shapeDrag is not { } drag) return false;
            if (!TryGetPagePoint(drag.Row, pointInHost, clamp: true, out var current)) return true;
            if (!TryPageToLayer(drag.Row, drag.StartU, drag.StartV, out Point a) || !TryPageToLayer(drag.Row, current.U, current.V, out Point b)) return true;
            if (_shapeStyle.IsLine)
            {
                ReaderShapeRubberLine.X2 = b.X;
                ReaderShapeRubberLine.Y2 = b.Y;
            }
            else
            {
                Canvas.SetLeft(ReaderShapeRubber, Math.Min(a.X, b.X));
                Canvas.SetTop(ReaderShapeRubber, Math.Min(a.Y, b.Y));
                ReaderShapeRubber.Width = Math.Abs(a.X - b.X);
                ReaderShapeRubber.Height = Math.Abs(a.Y - b.Y);
            }
            return true;
        }

        private bool FinishShapeDrag(Point pointInHost)
        {
            if (_shapeDrag is not { } drag) return false;
            bool haveEnd = TryGetPagePoint(drag.Row, pointInHost, clamp: true, out var end);
            double size = _shapeStyle.IsLine
                ? Math.Max(Math.Abs(ReaderShapeRubberLine.X2 - ReaderShapeRubberLine.X1), Math.Abs(ReaderShapeRubberLine.Y2 - ReaderShapeRubberLine.Y1))
                : Math.Min(ReaderShapeRubber.Width, ReaderShapeRubber.Height);
            CancelShapeDrag();
            if (haveEnd && size >= 5) _ = CommitShapeAsync(drag, end.U, end.V);
            return true;
        }

        private void CancelShapeDrag()
        {
            if (_shapeDrag == null) return;
            _shapeDrag = null;
            ReaderShapeRubber.Visibility = ReaderShapeRubberLine.Visibility = Visibility.Collapsed;
            if (ReaderContentHost.IsMouseCaptured) ReaderContentHost.ReleaseMouseCapture();
        }

        private async Task CommitShapeAsync(ShapeDrag drag, double endU, double endV)
        {
            var page = await LoadPageAnnotationsAsync(drag.Row);
            if (page == null) return;
            double dw = page.Geometry.DisplayWidth, dh = page.Geometry.DisplayHeight;
            double u1 = Math.Min(drag.StartU, endU), u2 = Math.Max(drag.StartU, endU), v1 = Math.Min(drag.StartV, endV), v2 = Math.Max(drag.StartV, endV);
            var style = _shapeStyle;
            if (style.IsLine)
            {
                double pad = ShapeStyle.LinePad(style.Width * ShapeStyle.PageScale(dw));
                u1 = Math.Max(0, u1 - pad / dw); u2 = Math.Min(1, u2 + pad / dw);
                v1 = Math.Max(0, v1 - pad / dh); v2 = Math.Min(1, v2 + pad / dh);
                bool startLeft = drag.StartU <= endU, startTop = drag.StartV <= endV;
                style = style with { Corner = startTop ? (startLeft ? 0 : 1) : (startLeft ? 2 : 3) };
            }
            var spec = new QuickAnnotationSpec(NewAnnotationName(), QuickAnnotationKind.Shape, drag.Row.PageNumber, u1, v1, u2, v2, "") { Format = style.Encode() };
            CommitAnnotationChange(drag.Row, new QuickAnnotationChange(null, spec), "Draw " + style.Type.ToLowerInvariant());
            SetReaderTool(ReaderTool.Hand); // like Foxit: one shape per tool click
            SelectAnnotation(drag.Row, spec);
        }

        // ── Hit test (outline only, so a big rectangle does not swallow clicks on what is inside it) ──

        private QuickAnnotationSpec? PickAnnotation(PageAnnotations? page, PageHit hit)
        {
            if (page == null) return null;
            double ppp = 1;
            if (TryPageToLayer(hit.Row, hit.U, hit.V, out var p0) && TryPageToLayer(hit.Row, hit.U + 1.0 / Math.Max(1, page.Geometry.DisplayWidth), hit.V, out var p1))
                ppp = Math.Max(0.05, (p1 - p0).Length);
            double tolerance = 6 / ppp;
            return page.Annotations.Where(a => a.Selectable && HitAnnotation(a, page.Geometry, hit, tolerance)).OrderBy(a => (a.U2 - a.U1) * (a.V2 - a.V1)).FirstOrDefault();
        }

        private static bool HitAnnotation(QuickAnnotationSpec spec, PdfPageGeometry geometry, PageHit hit, double tolerance)
        {
            if (spec.Kind != QuickAnnotationKind.Shape) return spec.Contains(hit.U, hit.V);
            double dw = geometry.DisplayWidth, dh = geometry.DisplayHeight;
            double x = (hit.U - spec.U1) * dw, y = (hit.V - spec.V1) * dh, w = (spec.U2 - spec.U1) * dw, h = (spec.V2 - spec.V1) * dh;
            var style = ShapeStyle.Decode(spec.Format);
            double effective = style.Width * ShapeStyle.PageScale(dw);
            double t = tolerance + effective;
            if (x < -t || y < -t || x > w + t || y > h + t) return false;
            switch (style.Type)
            {
                case ShapeStyle.Line:
                case ShapeStyle.Arrow:
                {
                    double pad = ShapeStyle.LinePad(effective);
                    (double X, double Y)[] corners = { (pad, pad), (w - pad, pad), (pad, h - pad), (w - pad, h - pad) };
                    var a = corners[style.Corner];
                    var b = corners[3 - style.Corner];
                    double vx = b.X - a.X, vy = b.Y - a.Y, len2 = Math.Max(1e-9, vx * vx + vy * vy);
                    double k = Math.Clamp(((x - a.X) * vx + (y - a.Y) * vy) / len2, 0, 1);
                    double dx = x - (a.X + k * vx), dy = y - (a.Y + k * vy);
                    return Math.Sqrt(dx * dx + dy * dy) <= t;
                }
                case ShapeStyle.Oval:
                {
                    double a = Math.Max(1, w / 2), b = Math.Max(1, h / 2);
                    double r = Math.Sqrt(Math.Pow((x - a) / a, 2) + Math.Pow((y - b) / b, 2));
                    return Math.Abs(r - 1) * Math.Min(a, b) <= t;
                }
                default:
                    return !(x > t && y > t && x < w - t && y < h - t);
            }
        }

        // ── Colour / width bar ────────────────────────────────────────

        private void EnsureShapeBar()
        {
            if (_shapeBarBuilt) return;
            _shapeBarBuilt = true;
            foreach (double width in ShapeStyle.Widths)
                ShapeWidthBox.Items.Add(new ComboBoxItem { Content = width.ToString("0") + " pt", Tag = width, Focusable = false });
            foreach (string hex in TextFormat.Colors.Append("#FFFFFF").Where(c => c != "#FFFFFF"))
            {
                var swatch = new RadioButton
                {
                    GroupName = "ShapeColor", Tag = hex, Focusable = false, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Line colour",
                    Template = SwatchTemplate((Color)ColorConverter.ConvertFromString(hex))
                };
                swatch.Checked += (_, _) => { if (!_shapeLoading) { _shapeStyle = _shapeStyle with { Color = hex }; ShapeBarChanged(); } };
                ShapeColors.Children.Add(swatch);
            }
        }

        private void LoadShapeBar()
        {
            EnsureShapeBar();
            _shapeLoading = true;
            try
            {
                ShapeWidthBox.SelectedIndex = Math.Max(0, Array.FindIndex(ShapeStyle.Widths, w => Math.Abs(w - _shapeStyle.Width) < 0.01));
                foreach (RadioButton swatch in ShapeColors.Children)
                    swatch.IsChecked = string.Equals((string)swatch.Tag, _shapeStyle.Color, StringComparison.OrdinalIgnoreCase);
            }
            finally { _shapeLoading = false; }
        }

        private void ShapeWidth_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_shapeLoading || !_shapeBarBuilt || ShapeWidthBox.SelectedItem is not ComboBoxItem { Tag: double width }) return;
            _shapeStyle = _shapeStyle with { Width = width };
            ShapeBarChanged();
        }

        private void ShapeBarChanged()
        {
            AppSettings.ShapeStyleSetting = _shapeStyle.Encode();
            if (_selAnn is not { Kind: QuickAnnotationKind.Shape } spec || _selRow is not { } row) return;
            var old = ShapeStyle.Decode(spec.Format);
            var style = old with { Color = _shapeStyle.Color, Width = _shapeStyle.Width };
            if (style == old) return;
            double u1 = spec.U1, v1 = spec.V1, u2 = spec.U2, v2 = spec.V2;
            if (style.IsLine && GetCachedPageAnnotations(row)?.Geometry is { } geometry)
            {
                // The line ends stay where they are; only the padding around them changes with the width.
                double scale = ShapeStyle.PageScale(geometry.DisplayWidth);
                double delta = ShapeStyle.LinePad(style.Width * scale) - ShapeStyle.LinePad(old.Width * scale);
                u1 = Math.Max(0, u1 - delta / geometry.DisplayWidth); u2 = Math.Min(1, u2 + delta / geometry.DisplayWidth);
                v1 = Math.Max(0, v1 - delta / geometry.DisplayHeight); v2 = Math.Min(1, v2 + delta / geometry.DisplayHeight);
            }
            var changed = Regenerated(spec) with { U1 = u1, V1 = v1, U2 = u2, V2 = v2, Format = style.Encode() };
            _selAnn = changed;
            CommitAnnotationChange(row, new QuickAnnotationChange(spec, changed), "Change shape style");
        }
    }
}
