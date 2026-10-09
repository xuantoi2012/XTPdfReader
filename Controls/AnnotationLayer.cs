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
            /// <summary>Other members of the moved annotation's group, dragged along.</summary>
            public HashSet<string> MoveExtra { get; } = new();
            public double MoveDU, MoveDV;
            public bool IsMoving(string name) => name == MoveName || MoveExtra.Contains(name);
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
            ObjectDeleteLayer.Draw(dc, row, page); // objects that wait to be removed are painted out, text edits that wait for Save drawn over them
            TextEditLayer.Draw(dc, row, page, dpi);
            if (live) DrawSignatureFields(dc, row, page);
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
                bool moving = live && Edit.IsMoving(spec.Name);
                DrawHighlight(dc, spec, page, moving ? Edit.MoveDU : 0, moving ? Edit.MoveDV : 0, bases);
            }
            foreach (var spec in annotations.Annotations)
            {
                // Reply (như Word): chỉ hiện trong popup của chú thích gốc/panel Comments — không có icon riêng trên trang.
                if (spec.Kind == QuickAnnotationKind.Reply || spec.Subtype == "Widget") continue; // Widgets are rendered on the source page by PDFium.
                if (IsMultiplyHighlight(spec) || live && spec.Name == Edit.HiddenName) continue;
                double du = 0, dv = 0;
                if (live && Edit.IsMoving(spec.Name)) { du = Edit.MoveDU; dv = Edit.MoveDV; }

                // The note icon is drawn here as vector art, not from a rendered PDF appearance: MuPDF places sticky-note (Text) annotations wrongly
                // on rotated pages (a landscape CAD sheet is usually a portrait page with /Rotate 90/270), which left the icon blank on real drawings.
                if (spec.Kind == QuickAnnotationKind.Comment)
                {
                    DrawNoteIcon(dc, spec, page, du, dv);
                    continue;
                }

                var image = AnnotationAppearance.Get(row.SourcePath, row.PageNumber, spec, geometry, ppp);
                if (image == null) continue;
                double u = spec.U1 + du, v = spec.V1 + dv;
                var rect = new Rect(page.X + (u + image.DU1) * page.Width, page.Y + (v + image.DV1) * page.Height,
                    (image.DU2 - image.DU1) * page.Width, (image.DV2 - image.DV1) * page.Height);
                dc.DrawImage(image.Bitmap, rect);
            }
            dc.Pop();
        }

        // ── Digital signature fields (like Foxit: a tinted box so they are easy to find; click = details) ──

        private static readonly Brush SignedFill = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0x2E, 0x8B, 0x57)));
        private static readonly Brush EmptyFill = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xE0, 0x8A, 0x1E)));
        private static readonly Pen SignedPen = new(Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0x2E, 0x8B, 0x57))), 1) { DashStyle = DashStyles.Solid };
        private static readonly Pen EmptyPen = new(Freeze(new SolidColorBrush(Color.FromArgb(0xC0, 0xE0, 0x8A, 0x1E))), 1) { DashStyle = DashStyles.Dash };

        private static void DrawSignatureFields(DrawingContext dc, PageRow row, Rect page)
        {
            foreach (var field in PdfSignatureFieldService.Of(row.SourcePath, row.PageNumber))
            {
                var r = new Rect(page.X + field.U1 * page.Width, page.Y + field.V1 * page.Height, (field.U2 - field.U1) * page.Width, (field.V2 - field.V1) * page.Height);
                dc.DrawRectangle(field.Signed ? SignedFill : EmptyFill, field.Signed ? SignedPen : EmptyPen, r);
            }
        }

        // ── Note icon (speech bubble) ─────────────────────────────────

        private static readonly Brush NoteFill = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x33)));
        private static readonly Brush NoteResolvedFill = Freeze(new SolidColorBrush(Color.FromRgb(0xC9, 0xCD, 0xD3)));
        private static readonly Brush NoteInk = Freeze(new SolidColorBrush(Color.FromRgb(0x73, 0x54, 0x00)));
        private static T Freeze<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }

        /// <summary>Speech bubble with three text lines in the annotation's rectangle (a fixed size on the page, so it scales with the zoom).</summary>
        private static void DrawNoteIcon(DrawingContext dc, QuickAnnotationSpec spec, Rect page, double du, double dv)
        {
            double x = page.X + (spec.U1 + du) * page.Width, y = page.Y + (spec.V1 + dv) * page.Height;
            double size = Math.Max(6, Math.Max((spec.U2 - spec.U1) * page.Width, (spec.V2 - spec.V1) * page.Height));
            double k = size / 20.0; // the artwork is drawn on a 20 x 20 grid
            var pen = new Pen(NoteInk, Math.Max(0.75, k)) { LineJoin = PenLineJoin.Round };
            var body = new StreamGeometry();
            using (var ctx = body.Open())
            {
                ctx.BeginFigure(new Point(x + 2 * k, y + 2 * k), true, true);
                ctx.LineTo(new Point(x + 18 * k, y + 2 * k), true, false);
                ctx.LineTo(new Point(x + 18 * k, y + 14 * k), true, false);
                ctx.LineTo(new Point(x + 9 * k, y + 14 * k), true, false);
                ctx.LineTo(new Point(x + 5 * k, y + 18 * k), true, false);
                ctx.LineTo(new Point(x + 6 * k, y + 14 * k), true, false);
                ctx.LineTo(new Point(x + 2 * k, y + 14 * k), true, false);
            }
            body.Freeze();
            dc.DrawGeometry(spec.Resolved ? NoteResolvedFill : NoteFill, pen, body);
            var linePen = new Pen(NoteInk, Math.Max(0.75, 1.2 * k)) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            foreach (double offset in new[] { 5.5, 8.5, 11.5 })
                dc.DrawLine(linePen, new Point(x + 5 * k, y + offset * k), new Point(x + 15 * k, y + offset * k));
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
