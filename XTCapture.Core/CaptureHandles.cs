using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace XTCapture
{
    internal enum CaptureHandle { None, Move, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Vertex }

    internal readonly record struct CaptureHandleHit(CaptureHandle Kind, int Index = 0);

    /// <summary>Picking, resizing and moving the area after it was drawn: eight handles on a rectangle / circle, one per corner on a polygon, and dragging inside moves it.</summary>
    internal static class CaptureHandles
    {
        public const double MinimumSize = 4;

        /// <summary>The box of a rectangle / circle as doubles (the region's two corners, in either order).</summary>
        internal static Rect Box(CaptureRegion region)
        {
            double left = region.Points.Min(p => p.X), top = region.Points.Min(p => p.Y), right = region.Points.Max(p => p.X), bottom = region.Points.Max(p => p.Y);
            return new Rect(left, top, right - left, bottom - top);
        }

        /// <summary>The handles a region shows, in drawing order (the same points <see cref="HitTest"/> reacts to).</summary>
        public static IReadOnlyList<(CaptureHandleHit Hit, Point At)> Positions(CaptureRegion region)
        {
            if (region.Kind == CaptureShapeKind.Polygon)
                return region.Points.Select((p, i) => (new CaptureHandleHit(CaptureHandle.Vertex, i), p)).ToList();
            var b = Box(region);
            double cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
            return new List<(CaptureHandleHit, Point)>
            {
                (new(CaptureHandle.TopLeft), new Point(b.Left, b.Top)), (new(CaptureHandle.Top), new Point(cx, b.Top)), (new(CaptureHandle.TopRight), new Point(b.Right, b.Top)),
                (new(CaptureHandle.Right), new Point(b.Right, cy)), (new(CaptureHandle.BottomRight), new Point(b.Right, b.Bottom)), (new(CaptureHandle.Bottom), new Point(cx, b.Bottom)),
                (new(CaptureHandle.BottomLeft), new Point(b.Left, b.Bottom)), (new(CaptureHandle.Left), new Point(b.Left, cy))
            };
        }

        public static CaptureHandleHit HitTest(CaptureRegion region, Point p, double tolerance)
        {
            foreach (var (hit, at) in Positions(region))
                if (Math.Abs(p.X - at.X) <= tolerance && Math.Abs(p.Y - at.Y) <= tolerance) return hit;
            return region.Contains(p.X, p.Y) ? new CaptureHandleHit(CaptureHandle.Move) : default;
        }

        /// <summary>The region after the handle <paramref name="hit"/> was dragged to <paramref name="p"/>; <paramref name="limit"/> is the picture's size.</summary>
        public static CaptureRegion Resize(CaptureRegion region, CaptureHandleHit hit, Point p, Size limit)
        {
            double x = Math.Clamp(p.X, 0, limit.Width), y = Math.Clamp(p.Y, 0, limit.Height);
            if (region.Kind == CaptureShapeKind.Polygon)
            {
                if (hit.Kind != CaptureHandle.Vertex || hit.Index < 0 || hit.Index >= region.Points.Count) return region;
                var points = region.Points.ToList();
                points[hit.Index] = new Point(x, y);
                return CaptureRegion.Polygon(points);
            }
            var b = Box(region);
            double left = b.Left, top = b.Top, right = b.Right, bottom = b.Bottom;
            switch (hit.Kind)
            {
                case CaptureHandle.TopLeft: left = x; top = y; break;
                case CaptureHandle.Top: top = y; break;
                case CaptureHandle.TopRight: right = x; top = y; break;
                case CaptureHandle.Right: right = x; break;
                case CaptureHandle.BottomRight: right = x; bottom = y; break;
                case CaptureHandle.Bottom: bottom = y; break;
                case CaptureHandle.BottomLeft: left = x; bottom = y; break;
                case CaptureHandle.Left: left = x; break;
                default: return region;
            }
            // dragged past the opposite side: the box turns over, as in any drawing program
            if (left > right) (left, right) = (right, left);
            if (top > bottom) (top, bottom) = (bottom, top);
            if (right - left < MinimumSize) { if (hit.Kind is CaptureHandle.TopLeft or CaptureHandle.Left or CaptureHandle.BottomLeft) left = right - MinimumSize; else right = left + MinimumSize; }
            if (bottom - top < MinimumSize) { if (hit.Kind is CaptureHandle.TopLeft or CaptureHandle.Top or CaptureHandle.TopRight) top = bottom - MinimumSize; else bottom = top + MinimumSize; }
            var a = new Point(Math.Max(0, left), Math.Max(0, top));
            var c = new Point(Math.Min(limit.Width, right), Math.Min(limit.Height, bottom));
            return region.Kind == CaptureShapeKind.Ellipse ? CaptureRegion.Ellipse(a, c) : CaptureRegion.Rectangle(a, c);
        }

        /// <summary>The region shifted by (<paramref name="dx"/>, <paramref name="dy"/>), kept inside the picture.</summary>
        public static CaptureRegion Move(CaptureRegion region, double dx, double dy, Size limit)
        {
            double left = region.Points.Min(p => p.X), top = region.Points.Min(p => p.Y), right = region.Points.Max(p => p.X), bottom = region.Points.Max(p => p.Y);
            dx = Math.Clamp(dx, -left, Math.Max(-left, limit.Width - right));
            dy = Math.Clamp(dy, -top, Math.Max(-top, limit.Height - bottom));
            var moved = region.Points.Select(p => new Point(p.X + dx, p.Y + dy)).ToList();
            return region.Kind switch
            {
                CaptureShapeKind.Ellipse => CaptureRegion.Ellipse(moved[0], moved[1]),
                CaptureShapeKind.Polygon => CaptureRegion.Polygon(moved),
                _ => CaptureRegion.Rectangle(moved[0], moved[1])
            };
        }
    }
}
