using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XTCapture
{
    /// <summary>
    /// Draws markup objects. One drawing routine serves the editing surface (over the frozen screen or the opened capture) and the flattened picture that is copied,
    /// saved as PNG or put in a PDF, so what is on screen is what comes out.
    /// </summary>
    internal sealed class MarkupRenderer
    {
        private BitmapSource? _original;
        private BitmapSource? _originalBgra;
        private readonly Dictionary<(Int32Rect, int), BitmapSource> _mosaic = new();

        // ── Measuring ───────────────────────────────────────────────────

        internal static Brush BrushFor(string hex)
        {
            Color color;
            try { color = (Color)ColorConverter.ConvertFromString(hex); }
            catch { color = Colors.Red; }
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        internal static FormattedText TextOf(string text, MarkupStyle style, Brush? brush = null)
        {
            var typeface = new Typeface(new FontFamily(style.Font), style.Italic ? FontStyles.Italic : FontStyles.Normal, style.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            var formatted = new FormattedText(text.Length == 0 ? " " : text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, style.FontSize, brush ?? BrushFor(style.Color), 1.0);
            if (style.Underline) formatted.SetTextDecorations(TextDecorations.Underline);
            return formatted;
        }

        /// <summary>Diameter of a numbered marker: it follows the font size.</summary>
        internal static double MarkerDiameter(MarkupStyle style) => style.FontSize * 1.7;

        /// <summary>The box an object occupies (what selecting it outlines and what hit-testing a text uses).</summary>
        internal static Rect Bounds(MarkupItem item)
        {
            switch (item.Kind)
            {
                case MarkupKind.Text:
                {
                    var formatted = TextOf(item.Text, item.Style);
                    return new Rect(item.X1, item.Y1, Math.Max(8, formatted.WidthIncludingTrailingWhitespace), formatted.Height);
                }
                case MarkupKind.Marker:
                {
                    double r = MarkerDiameter(item.Style) / 2;
                    return new Rect(item.X1 - r, item.Y1 - r, 2 * r, 2 * r);
                }
                case MarkupKind.Pen when item.Points is { Count: >= 2 } points:
                {
                    double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                    for (int i = 0; i + 1 < points.Count; i += 2)
                    {
                        minX = Math.Min(minX, points[i]); maxX = Math.Max(maxX, points[i]);
                        minY = Math.Min(minY, points[i + 1]); maxY = Math.Max(maxY, points[i + 1]);
                    }
                    return new Rect(minX, minY, maxX - minX, maxY - minY);
                }
                default:
                    return new Rect(new Point(Math.Min(item.X1, item.X2), Math.Min(item.Y1, item.Y2)), new Point(Math.Max(item.X1, item.X2), Math.Max(item.Y1, item.Y2)));
            }
        }

        // ── Drawing ─────────────────────────────────────────────────────

        /// <summary>Draws <paramref name="items"/> (bottom first). <paramref name="original"/> is the picture under them (a mosaic is made from it).
        /// <paramref name="hideId"/> is the text being typed, which the editing box shows instead.</summary>
        public void Draw(DrawingContext dc, IEnumerable<MarkupItem> items, BitmapSource? original, string? hideId = null)
        {
            if (!ReferenceEquals(original, _original))
            {
                _original = original;
                _originalBgra = null;
                _mosaic.Clear();
            }
            foreach (var item in items)
            {
                if (item.Id == hideId) continue;
                int opacity = Math.Clamp(item.Style.Opacity, 1, 100);
                bool faded = opacity < 100 && item.Kind != MarkupKind.Mosaic;
                if (faded) dc.PushOpacity(opacity / 100.0);
                DrawItem(dc, item);
                if (faded) dc.Pop();
            }
        }

        private void DrawItem(DrawingContext dc, MarkupItem item)
        {
            var style = item.Style;
            var brush = BrushFor(style.Color);
            switch (item.Kind)
            {
                case MarkupKind.Rectangle:
                case MarkupKind.Ellipse:
                {
                    var box = Bounds(item);
                    Brush? fill = style.Fill.Length > 0 ? BrushFor(style.Fill) : null;
                    if (item.Kind == MarkupKind.Rectangle) dc.DrawRectangle(fill, PenFor(style, brush), box);
                    else dc.DrawEllipse(fill, PenFor(style, brush), new Point(box.X + box.Width / 2, box.Y + box.Height / 2), box.Width / 2, box.Height / 2);
                    break;
                }
                case MarkupKind.Line:
                    dc.DrawLine(PenFor(style, brush), new Point(item.X1, item.Y1), new Point(item.X2, item.Y2));
                    break;
                case MarkupKind.Arrow:
                    DrawArrow(dc, item, style, brush);
                    break;
                case MarkupKind.Pen:
                    DrawPen(dc, item, style, brush);
                    break;
                case MarkupKind.Text:
                    dc.DrawText(TextOf(item.Text, style, brush), new Point(item.X1, item.Y1));
                    break;
                case MarkupKind.Marker:
                {
                    double d = MarkerDiameter(style);
                    dc.DrawEllipse(brush, null, new Point(item.X1, item.Y1), d / 2, d / 2);
                    var numberBrush = string.Equals(style.Color, "#FFFFFF", StringComparison.OrdinalIgnoreCase) || string.Equals(style.Color, "#F2C200", StringComparison.OrdinalIgnoreCase) ? Brushes.Black : Brushes.White;
                    var number = TextOf(item.Number.ToString(CultureInfo.InvariantCulture), style with { Bold = true, Underline = false }, numberBrush);
                    dc.DrawText(number, new Point(item.X1 - number.WidthIncludingTrailingWhitespace / 2, item.Y1 - number.Height / 2));
                    break;
                }
                case MarkupKind.Mosaic:
                    DrawMosaic(dc, item);
                    break;
            }
        }

        private static Pen PenFor(MarkupStyle style, Brush brush)
        {
            var pen = new Pen(brush, Math.Max(0.5, style.Width)) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var pattern = style.DashPattern();
            if (pattern.Length > 0)
            {
                pen.DashStyle = new DashStyle(pattern, 0);
                pen.DashCap = style.Dash == MarkupStyle.Dotted ? PenLineCap.Round : PenLineCap.Flat;
                if (style.Dash == MarkupStyle.Dashed) { pen.StartLineCap = pen.EndLineCap = PenLineCap.Flat; }
            }
            pen.Freeze();
            return pen;
        }

        private static void DrawArrow(DrawingContext dc, MarkupItem item, MarkupStyle style, Brush brush)
        {
            var start = new Point(item.X1, item.Y1);
            var end = new Point(item.X2, item.Y2);
            double dx = end.X - start.X, dy = end.Y - start.Y, length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1) return;
            double ux = dx / length, uy = dy / length, head = Math.Min(Math.Max(10, 4.5 * style.Width), length * 0.6), half = head * 0.42;
            var baseCentre = new Point(end.X - ux * head, end.Y - uy * head);
            dc.DrawLine(PenFor(style, brush), start, new Point(baseCentre.X + ux, baseCentre.Y + uy)); // the shaft ends inside the head
            var triangle = new StreamGeometry();
            using (var context = triangle.Open())
            {
                context.BeginFigure(end, true, true);
                context.LineTo(new Point(baseCentre.X - uy * half, baseCentre.Y + ux * half), true, false);
                context.LineTo(new Point(baseCentre.X + uy * half, baseCentre.Y - ux * half), true, false);
            }
            triangle.Freeze();
            dc.DrawGeometry(brush, new Pen(brush, 1) { LineJoin = PenLineJoin.Round }, triangle);
        }

        private static void DrawPen(DrawingContext dc, MarkupItem item, MarkupStyle style, Brush brush)
        {
            if (item.Points is not { Count: >= 2 } points) return;
            if (points.Count < 4)
            {
                dc.DrawEllipse(brush, null, new Point(points[0], points[1]), style.Width / 2, style.Width / 2); // a click is a dot
                return;
            }
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(points[0], points[1]), false, false);
                var rest = new List<Point>();
                for (int i = 2; i + 1 < points.Count; i += 2) rest.Add(new Point(points[i], points[i + 1]));
                context.PolyLineTo(rest, true, true);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, PenFor(style with { Dash = MarkupStyle.Solid }, brush), geometry);
        }

        private void DrawMosaic(DrawingContext dc, MarkupItem item)
        {
            if (_original == null) return;
            var box = Bounds(item);
            var wanted = new Int32Rect((int)Math.Floor(box.X), (int)Math.Floor(box.Y), (int)Math.Ceiling(box.Width), (int)Math.Ceiling(box.Height));
            int block = (int)Math.Clamp(Math.Round(item.Style.Width), 2, 200);
            _originalBgra ??= _original.Format == PixelFormats.Bgra32 ? _original : new FormatConvertedBitmap(_original, PixelFormats.Bgra32, null, 0);
            if (!_mosaic.TryGetValue((wanted, block), out var pixelated))
            {
                if (_mosaic.Count > 24) _mosaic.Clear();
                pixelated = Pixelate(_originalBgra, wanted, block)!;
                _mosaic[(wanted, block)] = pixelated;
            }
            if (pixelated != null) dc.DrawImage(pixelated, new Rect(Math.Max(0, wanted.X), Math.Max(0, wanted.Y), pixelated.PixelWidth, pixelated.PixelHeight));
        }

        /// <summary>The part of <paramref name="source"/> inside <paramref name="area"/> (clamped to the picture) as blocks of <paramref name="block"/> px, each the average of its pixels.
        /// Null when the area is outside the picture.</summary>
        internal static BitmapSource? Pixelate(BitmapSource source, Int32Rect area, int block)
        {
            int left = Math.Clamp(area.X, 0, source.PixelWidth), top = Math.Clamp(area.Y, 0, source.PixelHeight);
            int right = Math.Clamp(area.X + area.Width, 0, source.PixelWidth), bottom = Math.Clamp(area.Y + area.Height, 0, source.PixelHeight);
            int width = right - left, height = bottom - top;
            if (width < 1 || height < 1) return null;
            var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = width * 4;
            var pixels = new byte[stride * height];
            bgra.CopyPixels(new Int32Rect(left, top, width, height), pixels, stride, 0);
            block = Math.Max(1, block);
            // blocks are aligned to the picture's grid, so the same place always pixelates the same way
            for (int by = (top / block) * block; by < bottom; by += block)
            {
                for (int bx = (left / block) * block; bx < right; bx += block)
                {
                    int x0 = Math.Max(bx, left) - left, x1 = Math.Min(bx + block, right) - left, y0 = Math.Max(by, top) - top, y1 = Math.Min(by + block, bottom) - top;
                    long b = 0, g = 0, r = 0, a = 0;
                    int count = (x1 - x0) * (y1 - y0);
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++) { int i = y * stride + x * 4; b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2]; a += pixels[i + 3]; }
                    byte ab = (byte)(b / count), ag = (byte)(g / count), ar = (byte)(r / count), aa = (byte)(a / count);
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++) { int i = y * stride + x * 4; pixels[i] = ab; pixels[i + 1] = ag; pixels[i + 2] = ar; pixels[i + 3] = aa; }
                }
            }
            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }

        // ── The finished picture ────────────────────────────────────────

        /// <summary>The picture with the markup drawn on it (what Copy, Save PNG and Export to PDF give).</summary>
        public static BitmapSource Flatten(BitmapSource original, IEnumerable<MarkupItem> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return original;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(original, new Rect(0, 0, original.PixelWidth, original.PixelHeight));
                new MarkupRenderer().Draw(dc, list, original);
            }
            var target = new RenderTargetBitmap(original.PixelWidth, original.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var converted = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            return converted;
        }
    }
}
