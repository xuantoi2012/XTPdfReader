using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Services;

internal static class ViewportRegionReuse
{
    internal readonly record struct Piece(Int32Rect Bounds, BitmapSource Bitmap, Int32Rect? Coverage = null)
    {
        internal Int32Rect CopyBounds => Intersect(Bounds, Coverage ?? Bounds);
    }

    // Native clip edges can truncate glyph coverage beyond a one-pixel antialias fringe.
    internal static Piece CachedPiece(Int32Rect bounds, BitmapSource bitmap, int fullWidth, int fullHeight, Int32Rect? target = null)
    {
        const int inset = 16;
        // Only borders inside the new crop form seams; unchanged outer borders need no inset.
        int left = bounds.X == 0 || (target.HasValue && bounds.X <= target.Value.X) ? bounds.X : bounds.X + inset;
        int top = bounds.Y == 0 || (target.HasValue && bounds.Y <= target.Value.Y) ? bounds.Y : bounds.Y + inset;
        int right = bounds.X + bounds.Width == fullWidth || (target.HasValue && bounds.X + bounds.Width >= target.Value.X + target.Value.Width)
            ? bounds.X + bounds.Width : bounds.X + bounds.Width - inset;
        int bottom = bounds.Y + bounds.Height == fullHeight || (target.HasValue && bounds.Y + bounds.Height >= target.Value.Y + target.Value.Height)
            ? bounds.Y + bounds.Height : bounds.Y + bounds.Height - inset;
        return new(bounds, bitmap, right > left && bottom > top
            ? new(left, top, right - left, bottom - top) : Int32Rect.Empty);
    }

    // PDFium walks page objects for each clip: combine nearby gaps into one native pass.
    internal static Int32Rect[] Plan(Int32Rect target, IReadOnlyList<Piece> sources)
    {
        var missing = Uncovered(target, sources);
        if (missing.Count == 0) return Array.Empty<Int32Rect>();
        int left = missing.Min(r => r.X), top = missing.Min(r => r.Y);
        int right = missing.Max(r => r.X + r.Width), bottom = missing.Max(r => r.Y + r.Height);
        var band = new Int32Rect(left, top, right - left, bottom - top);
        return missing.Count > 2 || (long)band.Width * band.Height * 2 > (long)target.Width * target.Height
            ? new[] { target } : new[] { band };
    }

    // PDFium crops are Bgra32; MuPDF workers deliver Bgr24 (opaque pages) or Pbgra32 (alpha). A composition keeps ONE format: converting every
    // piece to Bgra32 would cost more than re-rendering the overlap.
    private static bool IsSupportedFormat(PixelFormat format)
        => format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 || format == PixelFormats.Bgr32 || format == PixelFormats.Bgr24;

    internal static bool CanReuse(Piece piece) => piece.Bitmap.IsFrozen && IsSupportedFormat(piece.Bitmap.Format) &&
        piece.Bounds.Width > 0 && piece.Bounds.Height > 0 &&
        piece.Bitmap.PixelWidth == piece.Bounds.Width && piece.Bitmap.PixelHeight == piece.Bounds.Height;

    /// <summary>Format của lần ghép: format của mảnh dùng lại được ĐẦU TIÊN; mảnh khác format bị bỏ qua (Plan và Compose cùng quy tắc nên nhất quán).</summary>
    private static PixelFormat? DominantFormat(IReadOnlyList<Piece> pieces)
    {
        foreach (var piece in pieces) if (CanReuse(piece)) return piece.Bitmap.Format;
        return null;
    }

    private static bool Usable(Piece piece, PixelFormat? format) => format != null && CanReuse(piece) && piece.Bitmap.Format == format.Value;

    internal static Int32Rect Intersect(Int32Rect a, Int32Rect b)
    {
        int left = Math.Max(a.X, b.X), top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.X + a.Width, b.X + b.Width), bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return right > left && bottom > top ? new(left, top, right - left, bottom - top) : Int32Rect.Empty;
    }

    internal static Int32Rect WithGutter(Int32Rect rect, int fullWidth, int fullHeight)
    {
        const int gutter = 32;
        int left = Math.Max(0, rect.X - gutter), top = Math.Max(0, rect.Y - gutter);
        int right = Math.Min(fullWidth, rect.X + rect.Width + gutter), bottom = Math.Min(fullHeight, rect.Y + rect.Height + gutter);
        return new(left, top, right - left, bottom - top);
    }

    private static List<Int32Rect> Uncovered(Int32Rect target, IReadOnlyList<Piece> sources)
    {
        var missing = new List<Int32Rect> { target };
        var format = DominantFormat(sources);
        foreach (var source in sources)
        {
            if (!Usable(source, format)) continue;
            var next = new List<Int32Rect>();
            foreach (var rect in missing)
            {
                var cut = Intersect(rect, source.CopyBounds);
                if (cut.IsEmpty) { next.Add(rect); continue; }
                Add(rect.X, rect.Y, rect.Width, cut.Y - rect.Y);
                Add(rect.X, cut.Y + cut.Height, rect.Width, rect.Y + rect.Height - cut.Y - cut.Height);
                Add(rect.X, cut.Y, cut.X - rect.X, cut.Height);
                Add(cut.X + cut.Width, cut.Y, rect.X + rect.Width - cut.X - cut.Width, cut.Height);
            }
            missing = next;
            if (missing.Count == 0) break;

            void Add(int x, int y, int width, int height)
            {
                if (width > 0 && height > 0) next.Add(new(x, y, width, height));
            }
        }
        return missing;
    }

    internal static BitmapSource Compose(Int32Rect target, IReadOnlyList<Piece> pieces, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (target.Width <= 0 || target.Height <= 0 || Uncovered(target, pieces).Count != 0)
            throw new ArgumentException("Viewport pieces must cover the entire target.", nameof(pieces));
        var format = DominantFormat(pieces) ?? PixelFormats.Bgra32;
        int bytesPerPixel = format.BitsPerPixel / 8;
        int stride = checked(target.Width * bytesPerPixel), bytes = checked(stride * target.Height);
        // A transient native buffer avoids a large managed allocation on every pan.
        IntPtr pixels = Marshal.AllocHGlobal(bytes);
        try
        {
            foreach (var piece in pieces)
            {
                token.ThrowIfCancellationRequested();
                if (!Usable(piece, format)) continue;
                var clip = Intersect(target, piece.CopyBounds);
                if (clip.IsEmpty) continue;
                int offset = checked((clip.Y - target.Y) * stride + (clip.X - target.X) * bytesPerPixel);
                piece.Bitmap.CopyPixels(new Int32Rect(clip.X - piece.Bounds.X, clip.Y - piece.Bounds.Y, clip.Width, clip.Height),
                    IntPtr.Add(pixels, offset), bytes - offset, stride);
            }
            token.ThrowIfCancellationRequested();
            var bitmap = BitmapSource.Create(target.Width, target.Height, 96, 96, format, null, pixels, bytes, stride);
            bitmap.Freeze();
            return bitmap;
        }
        finally { Marshal.FreeHGlobal(pixels); }
    }
}
