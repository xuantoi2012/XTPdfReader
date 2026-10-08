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

        // ── Drawing (xem trước bằng hình thật — màu/nét/mũi tên như lúc ghi — không phải khung chữ nhật rồi mới hiện) ──

        /// <summary>Pixel/point của trang đang hiển thị — dùng để cỡ nét xem trước khớp với cỡ nét PDF sẽ ghi.</summary>
        private double ShapePixelsPerPoint(PageRow row)
        {
            if (GetCachedPageAnnotations(row)?.Geometry is not { } g || g.DisplayWidth <= 0) return 1;
            if (!TryPageToLayer(row, 0, 0, out var p0) || !TryPageToLayer(row, 1.0 / g.DisplayWidth, 0, out var p1)) return 1;
            return Math.Max(0.05, (p1 - p0).Length);
        }

        /// <summary>The page-size factor the PDF writer applies to line widths and arrow heads (A1/A0 sheets get thicker lines); the preview must use it too.</summary>
        private double ShapePageScale(PageRow row)
            => GetCachedPageAnnotations(row)?.Geometry is { } g && g.DisplayWidth > 0 ? ShapeStyle.PageScale(g.DisplayWidth) : 1;

        /// <summary>The remembered colour/width of one shape tool (each tool keeps its own); falls back to the old shared setting.</summary>
        private static ShapeStyle ToolShapeStyle(string type)
        {
            string saved = AppSettings.GetShapeStyleFor(type);
            var style = ShapeStyle.Decode(saved.Length > 0 ? saved : AppSettings.ShapeStyleSetting);
            return style with { Type = type, Corner = 0 };
        }

        private static Color ParseWpfColor(string hex)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return Colors.Red; }
        }

        /// <summary>Hiện hình xem trước giữa 2 điểm góc (toạ độ ReaderInteractionLayer) — dùng chung cho vẽ mới và resize.</summary>
        private void ShowShapePreview(ShapeStyle style, Point a, Point b, double ppp, double pageScale = 1)
        {
            var brush = new SolidColorBrush(ParseWpfColor(style.Color)) { Opacity = style.Opacity / 100.0 };
            double lw = Math.Max(1, style.Width * pageScale * ppp); // same effective width as the PDF appearance (Width x PageScale points)
            bool oval = style.Type == ShapeStyle.Oval;
            bool rectLike = !style.IsLine && !oval;

            ReaderShapeRubber.Visibility = rectLike ? Visibility.Visible : Visibility.Collapsed;
            ReaderShapeOvalPreview.Visibility = oval ? Visibility.Visible : Visibility.Collapsed;
            ReaderShapeRubberLine.Visibility = style.IsLine ? Visibility.Visible : Visibility.Collapsed;
            ReaderShapeArrowHead.Visibility = style.Type == ShapeStyle.Arrow ? Visibility.Visible : Visibility.Collapsed;

            if (rectLike)
            {
                ReaderShapeRubber.Stroke = brush;
                ReaderShapeRubber.StrokeThickness = lw;
                ApplyDash(ReaderShapeRubber, style);
                ReaderShapeRubber.Fill = FillBrush(style);
                // Cloud: góc bo lớn thay cho các nét lượn thật (xấp xỉ, hình thật vẫn vẽ đúng lúc ghi vào PDF).
                double radius = style.Type == ShapeStyle.Cloud ? Math.Max(6, 10 * pageScale * ppp * 0.7) : 0;
                ReaderShapeRubber.RadiusX = ReaderShapeRubber.RadiusY = radius;
                Canvas.SetLeft(ReaderShapeRubber, Math.Min(a.X, b.X));
                Canvas.SetTop(ReaderShapeRubber, Math.Min(a.Y, b.Y));
                ReaderShapeRubber.Width = Math.Abs(a.X - b.X);
                ReaderShapeRubber.Height = Math.Abs(a.Y - b.Y);
            }
            else if (oval)
            {
                ReaderShapeOvalPreview.Stroke = brush;
                ReaderShapeOvalPreview.StrokeThickness = lw;
                ApplyDash(ReaderShapeOvalPreview, style);
                ReaderShapeOvalPreview.Fill = FillBrush(style);
                Canvas.SetLeft(ReaderShapeOvalPreview, Math.Min(a.X, b.X));
                Canvas.SetTop(ReaderShapeOvalPreview, Math.Min(a.Y, b.Y));
                ReaderShapeOvalPreview.Width = Math.Abs(a.X - b.X);
                ReaderShapeOvalPreview.Height = Math.Abs(a.Y - b.Y);
            }
            else
            {
                ReaderShapeRubberLine.Stroke = brush;
                ReaderShapeRubberLine.StrokeThickness = lw;
                ApplyDash(ReaderShapeRubberLine, style);
                ReaderShapeRubberLine.X1 = a.X;
                ReaderShapeRubberLine.Y1 = a.Y;
                if (style.Type == ShapeStyle.Arrow) UpdateArrowHead(a, b, brush, lw, 10 * pageScale * ppp);
                else { ReaderShapeRubberLine.X2 = b.X; ReaderShapeRubberLine.Y2 = b.Y; }
            }
        }

        private static Brush FillBrush(ShapeStyle style)
            => style.HasFill ? new SolidColorBrush(ParseWpfColor(style.Fill)) { Opacity = style.Opacity / 100.0 } : Brushes.Transparent;

        /// <summary>The preview's dash (WPF counts dash lengths in line widths, like the PDF pattern built from the same factors).</summary>
        private static void ApplyDash(System.Windows.Shapes.Shape shape, ShapeStyle style)
        {
            var pattern = style.DashPattern(1);
            shape.StrokeDashArray = pattern.Length == 0 ? null : new DoubleCollection(pattern.Select(v => (double)v));
            shape.StrokeDashCap = style.Dash == ShapeStyle.Dotted ? PenLineCap.Round : PenLineCap.Flat;
        }

        /// <summary>Đầu mũi tên (tam giác) + rút ngắn đường thẳng để không đè lên đầu mũi tên — cùng công thức lúc ghi vào PDF.</summary>
        private void UpdateArrowHead(Point start, Point end, Brush brush, double lw, double minHead)
        {
            double dx = end.X - start.X, dy = end.Y - start.Y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) { ReaderShapeRubberLine.X2 = end.X; ReaderShapeRubberLine.Y2 = end.Y; return; }
            double ux = dx / len, uy = dy / len;
            double head = Math.Min(Math.Max(minHead, 5 * lw), len * 0.6), half = head * 0.4;
            double bx = end.X - ux * head, by = end.Y - uy * head;
            ReaderShapeArrowHead.Fill = brush;
            ReaderShapeArrowHead.Points = new PointCollection { end, new Point(bx - uy * half, by + ux * half), new Point(bx + uy * half, by - ux * half) };
            ReaderShapeRubberLine.X2 = bx + ux;
            ReaderShapeRubberLine.Y2 = by + uy;
        }

        private void HideShapePreview()
            => ReaderShapeRubber.Visibility = ReaderShapeOvalPreview.Visibility = ReaderShapeRubberLine.Visibility = ReaderShapeArrowHead.Visibility = Visibility.Collapsed;

        private void BeginShapeDrag(PageHit hit)
        {
            if (!TryPageToLayer(hit.Row, hit.U, hit.V, out Point start)) return;
            _ = LoadPageAnnotationsAsync(hit.Row); // warm the geometry
            _shapeDrag = new ShapeDrag(hit.Row, hit.U, hit.V);
            ReaderCommentHoverPopup.Visibility = Visibility.Collapsed;
            ReaderContentHost.CaptureMouse();
            ShowShapePreview(_shapeStyle, start, start, ShapePixelsPerPoint(hit.Row), ShapePageScale(hit.Row));
        }

        private bool UpdateShapeDrag(Point pointInHost)
        {
            if (_shapeDrag is not { } drag) return false;
            if (!TryGetPagePoint(drag.Row, pointInHost, clamp: true, out var current)) return true;
            if (!TryPageToLayer(drag.Row, drag.StartU, drag.StartV, out Point a) || !TryPageToLayer(drag.Row, current.U, current.V, out Point b)) return true;
            ShowShapePreview(_shapeStyle, a, b, ShapePixelsPerPoint(drag.Row), ShapePageScale(drag.Row));
            return true;
        }

        private bool FinishShapeDrag(Point pointInHost)
        {
            if (_shapeDrag is not { } drag) return false;
            bool haveEnd = TryGetPagePoint(drag.Row, pointInHost, clamp: true, out var end);
            double size = _shapeStyle.IsLine
                ? Math.Max(Math.Abs(ReaderShapeRubberLine.X2 - ReaderShapeRubberLine.X1), Math.Abs(ReaderShapeRubberLine.Y2 - ReaderShapeRubberLine.Y1))
                : _shapeStyle.Type == ShapeStyle.Oval ? Math.Min(ReaderShapeOvalPreview.Width, ReaderShapeOvalPreview.Height)
                : Math.Min(ReaderShapeRubber.Width, ReaderShapeRubber.Height);
            CancelShapeDrag();
            if (haveEnd && size >= 5) _ = CommitShapeAsync(drag, end.U, end.V);
            return true;
        }

        private void CancelShapeDrag()
        {
            if (_shapeDrag == null) return;
            _shapeDrag = null;
            HideShapePreview();
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
                    return style.HasFill ? (r - 1) * Math.Min(a, b) <= t : Math.Abs(r - 1) * Math.Min(a, b) <= t;
                }
                default:
                    return style.HasFill || !(x > t && y > t && x < w - t && y < h - t);
            }
        }

        // ── Colour / width bar ────────────────────────────────────────

        private void EnsureShapeBar()
        {
            if (_shapeBarBuilt) return;
            _shapeBarBuilt = true;
            foreach (double width in ShapeStyle.Widths)
                ShapeWidthBox.Items.Add(new ComboBoxItem { Content = width.ToString("0") + " pt", Tag = width, Focusable = false });
            for (int i = 0; i < ShapeStyle.DashNames.Length; i++)
                ShapeDashBox.Items.Add(new ComboBoxItem { Content = ShapeStyle.DashNames[i], Tag = i, Focusable = false });
            foreach (var (name, hex) in ShapeStyle.Fills)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 12, Height = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                    Fill = hex.Length > 0 ? new SolidColorBrush(ParseWpfColor(hex)) : Brushes.Transparent,
                    Stroke = Brushes.Gray, StrokeThickness = 1
                });
                row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
                ShapeFillBox.Items.Add(new ComboBoxItem { Content = row, Tag = hex, Focusable = false });
            }
            foreach (int opacity in ShapeStyle.Opacities)
                ShapeOpacityBox.Items.Add(new ComboBoxItem { Content = opacity + "%", Tag = opacity, Focusable = false });
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
                ShapeFillBox.Visibility = (_selAnn is { Kind: QuickAnnotationKind.Shape } picked ? ShapeStyle.Decode(picked.Format).IsLine : _shapeStyle.IsLine) ? Visibility.Collapsed : Visibility.Visible; // arrows and lines enclose nothing
                ShapeFillBox.SelectedIndex = Math.Max(0, Array.FindIndex(ShapeStyle.Fills, f => string.Equals(f.Hex, _shapeStyle.Fill, StringComparison.OrdinalIgnoreCase)));
                ShapeOpacityBox.SelectedIndex = Math.Max(0, Array.IndexOf(ShapeStyle.Opacities, _shapeStyle.Opacity));
                ShapeDashBox.SelectedIndex = Math.Clamp(_shapeStyle.Dash, 0, ShapeStyle.DashNames.Length - 1);
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

        private void ShapeDash_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_shapeLoading || !_shapeBarBuilt || ShapeDashBox.SelectedItem is not ComboBoxItem { Tag: int dash }) return;
            _shapeStyle = _shapeStyle with { Dash = dash };
            ShapeBarChanged();
        }

        private void ShapeFill_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_shapeLoading || !_shapeBarBuilt || ShapeFillBox.SelectedItem is not ComboBoxItem { Tag: string fill }) return;
            _shapeStyle = _shapeStyle with { Fill = fill };
            ShapeBarChanged();
        }

        private void ShapeOpacity_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_shapeLoading || !_shapeBarBuilt || ShapeOpacityBox.SelectedItem is not ComboBoxItem { Tag: int opacity }) return;
            _shapeStyle = _shapeStyle with { Opacity = opacity };
            ShapeBarChanged();
        }

        private void ShapeBarChanged()
        {
            AppSettings.ShapeStyleSetting = _shapeStyle.Encode();
            // Each shape tool remembers its own colour/width: store under the type of the selected shape, else of the active tool.
            string type = _selAnn is { Kind: QuickAnnotationKind.Shape } picked ? ShapeStyle.Decode(picked.Format).Type : _shapeStyle.Type;
            AppSettings.SetShapeStyleFor(type, (_shapeStyle with { Type = type, Corner = 0 }).Encode());
            if (_selAnn is not { Kind: QuickAnnotationKind.Shape } spec || _selRow is not { } row) return;
            var old = ShapeStyle.Decode(spec.Format);
            var style = old with { Color = _shapeStyle.Color, Width = _shapeStyle.Width, Dash = _shapeStyle.Dash, Opacity = _shapeStyle.Opacity, Fill = old.IsLine ? "" : _shapeStyle.Fill };
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
