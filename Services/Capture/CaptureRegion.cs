using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Services.Capture
{
    internal enum CaptureShapeKind { Rectangle, Ellipse, Polygon }

    /// <summary>The area picked on the frozen screen, in the snapshot's own pixels: a rectangle, an ellipse (its bounding box) or a polygon.</summary>
    internal sealed record CaptureRegion(CaptureShapeKind Kind, IReadOnlyList<Point> Points)
    {
        public static CaptureRegion Rectangle(Point a, Point b) => new(CaptureShapeKind.Rectangle, new[] { a, b });
        public static CaptureRegion Ellipse(Point a, Point b) => new(CaptureShapeKind.Ellipse, new[] { a, b });
        public static CaptureRegion Polygon(IEnumerable<Point> points) => new(CaptureShapeKind.Polygon, points.ToList());

        /// <summary>Smallest whole-pixel box that holds the region (right / bottom exclusive).</summary>
        public Int32Rect Bounds
        {
            get
            {
                double left = Points.Min(p => p.X), top = Points.Min(p => p.Y), right = Points.Max(p => p.X), bottom = Points.Max(p => p.Y);
                int x = (int)Math.Floor(left), y = (int)Math.Floor(top);
                return new Int32Rect(x, y, Math.Max(1, (int)Math.Ceiling(right) - x), Math.Max(1, (int)Math.Ceiling(bottom) - y));
            }
        }

        /// <summary>A polygon needs three corners, the others two that are not on top of each other.</summary>
        public bool IsUsable => Kind == CaptureShapeKind.Polygon
            ? Points.Count >= 3 && Bounds is { Width: >= 3, Height: >= 3 }
            : Points.Count >= 2 && Bounds is { Width: >= 3, Height: >= 3 };

        public bool Contains(double x, double y)
        {
            switch (Kind)
            {
                case CaptureShapeKind.Rectangle:
                {
                    var b = Bounds;
                    return x >= b.X && x < b.X + b.Width && y >= b.Y && y < b.Y + b.Height;
                }
                case CaptureShapeKind.Ellipse:
                {
                    var b = Bounds;
                    double rx = b.Width / 2.0, ry = b.Height / 2.0, dx = (x - (b.X + rx)) / rx, dy = (y - (b.Y + ry)) / ry;
                    return dx * dx + dy * dy <= 1;
                }
                default:
                    return PolygonContains(Points, x, y);
            }
        }

        /// <summary>Even-odd test, so a self-crossing outline leaves its crossing loop empty (like most capture tools).</summary>
        internal static bool PolygonContains(IReadOnlyList<Point> polygon, double x, double y)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var a = polygon[i];
                var b = polygon[j];
                if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }

        /// <summary>The outline as a WPF geometry (overlay: the clear spot in the dimmed screen and the dashed border).</summary>
        public Geometry ToGeometry()
        {
            var b = Bounds;
            Geometry geometry = Kind switch
            {
                CaptureShapeKind.Rectangle => new RectangleGeometry(new Rect(b.X, b.Y, b.Width, b.Height)),
                CaptureShapeKind.Ellipse => new EllipseGeometry(new Rect(b.X, b.Y, b.Width, b.Height)),
                _ => PolygonGeometry(Points)
            };
            geometry.Freeze();
            return geometry;
        }

        internal static Geometry PolygonGeometry(IReadOnlyList<Point> points)
        {
            var figure = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = true };
            figure.Segments.Add(new PolyLineSegment(points.Skip(1).ToList(), true));
            return new PathGeometry(new[] { figure }) { FillRule = FillRule.EvenOdd };
        }
    }

    internal static class CaptureImaging
    {
        /// <summary>Cuts <paramref name="region"/> out of <paramref name="source"/> (a snapshot whose pixel (0,0) is at <paramref name="origin"/> of the region's coordinates).
        /// Pixels outside an ellipse / polygon become white or transparent. The result is Bgra32 in the region's bounding box, clamped to the source.</summary>
        public static BitmapSource Crop(BitmapSource source, CaptureRegion region, bool transparentOutside, Point origin = default)
        {
            var wanted = region.Bounds;
            var box = new Int32Rect(wanted.X - (int)origin.X, wanted.Y - (int)origin.Y, wanted.Width, wanted.Height);
            int left = Math.Clamp(box.X, 0, source.PixelWidth - 1), top = Math.Clamp(box.Y, 0, source.PixelHeight - 1);
            int right = Math.Clamp(box.X + box.Width, left + 1, source.PixelWidth), bottom = Math.Clamp(box.Y + box.Height, top + 1, source.PixelHeight);
            var clamped = new Int32Rect(left, top, right - left, bottom - top);

            var converted = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = clamped.Width * 4;
            var pixels = new byte[stride * clamped.Height];
            converted.CopyPixels(clamped, pixels, stride, 0);

            if (region.Kind != CaptureShapeKind.Rectangle)
            {
                for (int y = 0; y < clamped.Height; y++)
                {
                    for (int x = 0; x < clamped.Width; x++)
                    {
                        // the pixel's centre, in the region's own coordinates
                        if (region.Contains(clamped.X + x + origin.X + 0.5, clamped.Y + y + origin.Y + 0.5)) continue;
                        int i = y * stride + x * 4;
                        pixels[i] = pixels[i + 1] = pixels[i + 2] = transparentOutside ? (byte)0 : (byte)255;
                        pixels[i + 3] = transparentOutside ? (byte)0 : (byte)255;
                    }
                }
            }
            var result = BitmapSource.Create(clamped.Width, clamped.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }

        public static byte[] EncodePng(BitmapSource image)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new System.IO.MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
    }
}
