using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XTPdfMergeApp.Services;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp.Controls
{
    /// <summary>
    /// Draws the annotations of a page over the page image (the page is rendered without annotations): each annotation is its
    /// own image (<see cref="AnnotationAppearance"/>), highlights are multiplied into the page pixels underneath (like a real
    /// highlighter / PDF viewers: black text stays black). Used by the viewer and by every thumbnail, so an edit shows everywhere at once.
    /// </summary>
    internal static class AnnotationLayer
    {
        /// <summary>Editing state of the viewer: annotation hidden while its text is being edited, annotation being dragged.</summary>
        internal sealed class EditState
        {
            public string? HiddenName;
            public string? MoveName;
            public double MoveDU, MoveDV;
        }

        public static readonly EditState Edit = new();

        /// <summary>A page image drawn under the annotations and the page region it covers (0..1 of the page).</summary>
        internal readonly record struct BaseImage(BitmapSource Bitmap, Rect Fraction);

        private static readonly ConditionalWeakTable<BitmapSource, Dictionary<string, BitmapSource>> _patches = new();

        /// <param name="page">Page frame in the drawing coordinates, in the page's own orientation (view rotation already applied by the caller).</param>
        /// <param name="dpi">Device pixels per DIP.</param>
        /// <param name="bases">Page images drawn underneath, coarse first (for the highlight multiply).</param>
        public static void Draw(DrawingContext dc, PageRow row, Rect page, double dpi, IReadOnlyList<BaseImage> bases, bool live = true)
        {
            if (page.Width <= 0 || page.Height <= 0) return;
            var annotations = AnnotationStore.TryGetPage(row.SourcePath, row.PageNumber);
            if (annotations == null || annotations.Annotations.Count == 0) return;
            var geometry = annotations.Geometry;
            double ppp = page.Width * dpi / Math.Max(1, geometry.DisplayWidth);
            dc.PushClip(new RectangleGeometry(page));
            // Highlights first: they are made from the page pixels underneath, so drawn later they would cover the
            // annotations below them. Everything else keeps the /Annots order on top.
            foreach (var spec in annotations.Annotations)
            {
                if (!IsMultiplyHighlight(spec) || live && spec.Name == Edit.HiddenName) continue;
                bool moving = live && spec.Name == Edit.MoveName;
                DrawHighlight(dc, spec, page, moving ? Edit.MoveDU : 0, moving ? Edit.MoveDV : 0, bases);
            }
            foreach (var spec in annotations.Annotations)
            {
                // Reply (như Word): chỉ hiện trong popup của chú thích gốc/panel Comments — không có icon riêng trên trang.
                if (spec.Kind == QuickAnnotationKind.Reply || spec.Subtype == "Widget") continue; // Widgets are rendered on the source page by PDFium.
                if (IsMultiplyHighlight(spec) || live && spec.Name == Edit.HiddenName) continue;
                double du = 0, dv = 0;
                if (live && spec.Name == Edit.MoveName) { du = Edit.MoveDU; dv = Edit.MoveDV; }

                var image = AnnotationAppearance.Get(row.SourcePath, row.PageNumber, spec, geometry, ppp);
                if (image == null) continue;
                double u = spec.U1 + du, v = spec.V1 + dv;
                var rect = new Rect(page.X + (u + image.DU1) * page.Width, page.Y + (v + image.DV1) * page.Height,
                    (image.DU2 - image.DU1) * page.Width, (image.DV2 - image.DV1) * page.Height);
                dc.DrawImage(image.Bitmap, rect);
            }
            dc.Pop();
        }

        // ── Highlights: multiply into the page pixels ─────────────────

        private static bool IsMultiplyHighlight(QuickAnnotationSpec spec)
            => spec.Kind == QuickAnnotationKind.Highlight && spec.Subtype is "" or "Highlight";

        private static void DrawHighlight(DrawingContext dc, QuickAnnotationSpec spec, Rect page, double du, double dv, IReadOnlyList<BaseImage> bases)
        {
            if (bases.Count == 0) return;
            var rects = PdfQuickAnnotationService.TextHighlightRects(spec.Format);
            if (rects.Count == 0) rects.Add((spec.U1, spec.V1, spec.U2, spec.V2));
            // Our highlighter: yellow, 85 %. Other apps: their /C and /CA.
            var color = spec.ObjectNumber > 0 && spec.Color.Length > 0 ? spec.Color : spec.Color.Length > 0 ? spec.Color : "#FFEB00";
            double opacity = spec.ObjectNumber > 0 ? spec.Opacity : 0.85;
            var (cr, cg, cb) = Parse(color);
            // result = base × (1 − a + a × colour), per channel
            double mr = 1 - opacity + opacity * cr, mg = 1 - opacity + opacity * cg, mb = 1 - opacity + opacity * cb;
            string tint = $"{mr:0.###},{mg:0.###},{mb:0.###}";

            foreach (var (ru1, rv1, ru2, rv2) in rects)
            {
                var r = new Rect(new Point(ru1 + du, rv1 + dv), new Point(ru2 + du, rv2 + dv));
                // Sharpest base image that covers the highlight (regions are drawn last = sharpest).
                BaseImage? source = null;
                for (int i = bases.Count - 1; i >= 0; i--)
                    if (bases[i].Fraction.Contains(r)) { source = bases[i]; break; }
                source ??= bases[0];
                var b = source.Value;
                double bx = (r.X - b.Fraction.X) / b.Fraction.Width * b.Bitmap.PixelWidth, by = (r.Y - b.Fraction.Y) / b.Fraction.Height * b.Bitmap.PixelHeight;
                double bw = r.Width / b.Fraction.Width * b.Bitmap.PixelWidth, bh = r.Height / b.Fraction.Height * b.Bitmap.PixelHeight;
                int x0 = Math.Clamp((int)Math.Floor(bx), 0, b.Bitmap.PixelWidth), y0 = Math.Clamp((int)Math.Floor(by), 0, b.Bitmap.PixelHeight);
                int x1 = Math.Clamp((int)Math.Ceiling(bx + bw), 0, b.Bitmap.PixelWidth), y1 = Math.Clamp((int)Math.Ceiling(by + bh), 0, b.Bitmap.PixelHeight);
                if (x1 <= x0 || y1 <= y0) continue;
                var patch = Patch(b.Bitmap, x0, y0, x1 - x0, y1 - y0, mr, mg, mb, tint);
                if (patch == null) continue;
                // Back to the page: the exact pixel cells of the base image, so the patch sits on its own pixels.
                var target = new Rect(
                    page.X + (b.Fraction.X + (double)x0 / b.Bitmap.PixelWidth * b.Fraction.Width) * page.Width,
                    page.Y + (b.Fraction.Y + (double)y0 / b.Bitmap.PixelHeight * b.Fraction.Height) * page.Height,
                    (double)(x1 - x0) / b.Bitmap.PixelWidth * b.Fraction.Width * page.Width,
                    (double)(y1 - y0) / b.Bitmap.PixelHeight * b.Fraction.Height * page.Height);
                dc.DrawImage(patch, target);
            }
        }

        private static BitmapSource? Patch(BitmapSource source, int x, int y, int w, int h, double mr, double mg, double mb, string tint)
        {
            string key = $"{x},{y},{w},{h}|{tint}";
            var cache = _patches.GetOrCreateValue(source);
            if (cache.TryGetValue(key, out var cached)) return cached;
            if ((long)w * h > 16_000_000) return null;
            try
            {
                BitmapSource bgra = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32 || source.Format == PixelFormats.Bgr32
                    ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                int stride = w * 4;
                var pixels = new byte[stride * h];
                bgra.CopyPixels(new Int32Rect(x, y, w, h), pixels, stride, 0);
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    pixels[i] = (byte)(pixels[i] * mb);
                    pixels[i + 1] = (byte)(pixels[i + 1] * mg);
                    pixels[i + 2] = (byte)(pixels[i + 2] * mr);
                    pixels[i + 3] = 255;
                }
                var patch = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                patch.Freeze();
                if (cache.Count > 64) cache.Clear();
                cache[key] = patch;
                return patch;
            }
            catch
            {
                return null;
            }
        }

        private static (double R, double G, double B) Parse(string hex)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                return (c.R / 255.0, c.G / 255.0, c.B / 255.0);
            }
            catch
            {
                return (1, 0.92, 0);
            }
        }
    }
}
