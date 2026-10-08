using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace XTCapture
{
    /// <summary>Which object is under a point, the handles of a selected object, and how dragging a handle changes it.</summary>
    internal static class MarkupGeometry
    {
        // ── Picking ─────────────────────────────────────────────────────

        public static bool Hit(MarkupItem item, Point p, double tolerance)
        {
            double reach = tolerance + item.Style.Width / 2;
            switch (item.Kind)
            {
                case MarkupKind.Rectangle:
                {
                    var box = MarkupRenderer.Bounds(item);
                    if (item.Style.Fill.Length > 0) { var grown = box; grown.Inflate(reach, reach); return grown.Contains(p); }
                    var outer = box; outer.Inflate(reach, reach);
                    var inner = box; inner.Inflate(-reach, -reach);
                    return outer.Contains(p) && (inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(p));
                }
                case MarkupKind.Ellipse:
                {
                    var box = MarkupRenderer.Bounds(item);
                    double a = Math.Max(1, box.Width / 2), b = Math.Max(1, box.Height / 2);
                    double r = Math.Sqrt(Math.Pow((p.X - (box.X + a)) / a, 2) + Math.Pow((p.Y - (box.Y + b)) / b, 2));
                    return item.Style.Fill.Length > 0 ? (r - 1) * Math.Min(a, b) <= reach : Math.Abs(r - 1) * Math.Min(a, b) <= reach;
                }
                case MarkupKind.Arrow:
                case MarkupKind.Line:
                    return DistanceToSegment(p, new Point(item.X1, item.Y1), new Point(item.X2, item.Y2)) <= reach + (item.Kind == MarkupKind.Arrow ? 3 : 0);
                case MarkupKind.Pen:
                {
                    if (item.Points is not { Count: >= 2 } points) return false;
                    if (points.Count < 4) return Math.Abs(p.X - points[0]) <= reach && Math.Abs(p.Y - points[1]) <= reach;
                    for (int i = 0; i + 3 < points.Count; i += 2)
                        if (DistanceToSegment(p, new Point(points[i], points[i + 1]), new Point(points[i + 2], points[i + 3])) <= reach) return true;
                    return false;
                }
                case MarkupKind.Text:
                {
                    var box = MarkupRenderer.Bounds(item);
                    box.Inflate(tolerance, tolerance);
                    return box.Contains(p);
                }
                case MarkupKind.Marker:
                {
                    double d = Math.Sqrt(Math.Pow(p.X - item.X1, 2) + Math.Pow(p.Y - item.Y1, 2));
                    return d <= MarkupRenderer.MarkerDiameter(item.Style) / 2 + tolerance;
                }
                case MarkupKind.Mosaic:
                    return MarkupRenderer.Bounds(item).Contains(p);
                default:
                    return false;
            }
        }

        /// <summary>The topmost object at <paramref name="p"/> (the last one drawn), or null.</summary>
        public static MarkupItem? Pick(IReadOnlyList<MarkupItem> items, Point p, double tolerance)
        {
            for (int i = items.Count - 1; i >= 0; i--)
                if (Hit(items[i], p, tolerance)) return items[i];
            return null;
        }

        internal static double DistanceToSegment(Point p, Point a, Point b)
        {
            double vx = b.X - a.X, vy = b.Y - a.Y, length2 = vx * vx + vy * vy;
            double k = length2 < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / length2, 0, 1);
            double dx = p.X - (a.X + k * vx), dy = p.Y - (a.Y + k * vy);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // ── Handles ─────────────────────────────────────────────────────

        /// <summary>A box (rectangle, circle, mosaic) has the eight handles of a capture area; an arrow / line has its two ends; the rest are only moved.</summary>
        public static IReadOnlyList<(CaptureHandleHit Hit, Point At)> Handles(MarkupItem item)
        {
            if (item.IsBox) return CaptureHandles.Positions(CaptureRegion.Rectangle(new Point(item.X1, item.Y1), new Point(item.X2, item.Y2)));
            if (item.IsLine) return new List<(CaptureHandleHit, Point)> { (new(CaptureHandle.Vertex, 0), new Point(item.X1, item.Y1)), (new(CaptureHandle.Vertex, 1), new Point(item.X2, item.Y2)) };
            return Array.Empty<(CaptureHandleHit, Point)>();
        }

        /// <summary>The handle at <paramref name="p"/>, or none.</summary>
        public static CaptureHandleHit HitHandle(MarkupItem item, Point p, double tolerance)
        {
            foreach (var (hit, at) in Handles(item))
                if (Math.Abs(p.X - at.X) <= tolerance && Math.Abs(p.Y - at.Y) <= tolerance) return hit;
            return default;
        }

        /// <summary>The item after its handle was dragged to <paramref name="p"/>.</summary>
        public static MarkupItem Resize(MarkupItem item, CaptureHandleHit hit, Point p, Size limit)
        {
            if (item.IsLine && hit.Kind == CaptureHandle.Vertex)
                return hit.Index == 0 ? item with { X1 = p.X, Y1 = p.Y } : item with { X2 = p.X, Y2 = p.Y };
            if (!item.IsBox) return item;
            var region = CaptureRegion.Rectangle(new Point(item.X1, item.Y1), new Point(item.X2, item.Y2));
            var resized = CaptureHandles.Resize(region, hit, p, limit);
            var box = CaptureHandles.Box(resized);
            return item with { X1 = box.Left, Y1 = box.Top, X2 = box.Right, Y2 = box.Bottom };
        }
    }
}
