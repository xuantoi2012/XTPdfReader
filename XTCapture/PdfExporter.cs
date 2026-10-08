using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media.Imaging;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;

namespace XTCapture
{
    /// <summary>
    /// "Export to PDF": the capture as a one-page PDF (1 px = 0.75 pt, so the page shows it at its real size on a 100 % screen). By default the markup is part of the
    /// picture. With <c>keepAnnotations</c> the rectangles, circles, lines, arrows, pen strokes and (Latin) texts become real PDF annotations that stay editable in a PDF
    /// viewer; numbered markers, mosaics and texts the PDF base fonts cannot show are baked into the picture. iText loads only here.
    /// </summary>
    internal static class PdfExporter
    {
        internal const double PointsPerPixel = 0.75;

        public static void Export(BitmapSource image, string path) => Export(image, Array.Empty<MarkupItem>(), false, path);

        public static void Export(BitmapSource original, IReadOnlyList<MarkupItem> items, bool keepAnnotations, string path)
        {
            var native = keepAnnotations ? items.Where(CanBeAnnotation).ToList() : new List<MarkupItem>();
            var baked = items.Where(i => !native.Contains(i)).ToList();
            var picture = MarkupRenderer.Flatten(original, baked);

            float width = (float)(picture.PixelWidth * PointsPerPixel), height = (float)(picture.PixelHeight * PointsPerPixel);
            using var document = new PdfDocument(new PdfWriter(path));
            document.GetDocumentInfo().SetTitle(System.IO.Path.GetFileNameWithoutExtension(path)).SetCreator("XT Capture");
            var page = document.AddNewPage(new PageSize(width, height));
            new PdfCanvas(page).AddImageFittedIntoRectangle(ImageDataFactory.Create(CaptureImaging.EncodePng(picture)), new Rectangle(0, 0, width, height), false);
            foreach (var item in native) page.AddAnnotation(ToAnnotation(item, picture.PixelHeight));
        }

        /// <summary>Everything but numbered markers and mosaics; a text only when the PDF base font (Helvetica) has all its letters.</summary>
        internal static bool CanBeAnnotation(MarkupItem item) => item.Kind switch
        {
            MarkupKind.Marker or MarkupKind.Mosaic => false,
            MarkupKind.Text => item.Text.All(c => c is '\n' or '\r' || (c >= ' ' && c <= '~') || (c >= ' ' && c <= 'ÿ')),
            _ => true
        };

        private static float X(double px) => (float)(px * PointsPerPixel);
        private static float Y(double py, int imageHeight) => (float)((imageHeight - py) * PointsPerPixel);
        private static Color ColorOf(string hex)
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            return new DeviceRgb(c.R / 255f, c.G / 255f, c.B / 255f);
        }

        private static PdfAnnotation ToAnnotation(MarkupItem item, int imageHeight)
        {
            var style = item.Style;
            float line = (float)(Math.Max(0.5, style.Width) * PointsPerPixel);
            var box = MarkupRenderer.Bounds(item);
            PdfAnnotation annotation;
            switch (item.Kind)
            {
                case MarkupKind.Rectangle:
                case MarkupKind.Ellipse:
                {
                    var rect = new Rectangle(X(box.Left), Y(box.Bottom, imageHeight), X(box.Width), X(box.Height));
                    annotation = item.Kind == MarkupKind.Rectangle ? new PdfSquareAnnotation(Grow(rect, line / 2)) : new PdfCircleAnnotation(Grow(rect, line / 2));
                    if (style.Fill.Length > 0) annotation.GetPdfObject().Put(PdfName.IC, new PdfArray(ColorOf(style.Fill).GetColorValue()));
                    break;
                }
                case MarkupKind.Line:
                case MarkupKind.Arrow:
                {
                    float pad = Math.Max(8, 4 * line);
                    var rect = Grow(new Rectangle(X(box.Left), Y(box.Bottom, imageHeight), X(box.Width), X(box.Height)), pad);
                    var arrow = new PdfLineAnnotation(rect, new[] { X(item.X1), Y(item.Y1, imageHeight), X(item.X2), Y(item.Y2, imageHeight) });
                    if (item.Kind == MarkupKind.Arrow) arrow.SetLineEndingStyles(new PdfArray(new PdfObject[] { new PdfName("None"), new PdfName("ClosedArrow") }));
                    annotation = arrow;
                    break;
                }
                case MarkupKind.Pen:
                {
                    var points = item.Points ?? new List<double>();
                    var path = new float[points.Count];
                    for (int i = 0; i + 1 < points.Count; i += 2) { path[i] = X(points[i]); path[i + 1] = Y(points[i + 1], imageHeight); }
                    var rect = Grow(new Rectangle(X(box.Left), Y(box.Bottom, imageHeight), X(box.Width), X(box.Height)), line);
                    annotation = new PdfInkAnnotation(rect, new PdfArray(new PdfObject[] { new PdfArray(path) }));
                    break;
                }
                default: // Text
                {
                    var rect = new Rectangle(X(box.Left), Y(box.Bottom, imageHeight), X(box.Width) + 4, X(box.Height) + 2);
                    var free = new PdfFreeTextAnnotation(rect, new PdfString(item.Text));
                    var rgb = ColorOf(style.Color).GetColorValue();
                    free.SetDefaultAppearance(new PdfString(string.Create(CultureInfo.InvariantCulture, $"{rgb[0]:0.###} {rgb[1]:0.###} {rgb[2]:0.###} rg /Helv {style.FontSize * PointsPerPixel:0.##} Tf")));
                    free.GetPdfObject().Put(PdfName.BS, new PdfDictionary(new Dictionary<PdfName, PdfObject> { [PdfName.W] = new PdfNumber(0) }));
                    if (style.Opacity < 100) free.GetPdfObject().Put(PdfName.CA, new PdfNumber(style.Opacity / 100.0));
                    return free;
                }
            }
            annotation.SetColor(ColorOf(style.Color));
            var border = new PdfDictionary(new Dictionary<PdfName, PdfObject> { [PdfName.W] = new PdfNumber(line) });
            if (item.Kind != MarkupKind.Pen && style.Dash != MarkupStyle.Solid)
            {
                border.Put(PdfName.S, PdfName.D);
                border.Put(PdfName.D, new PdfArray(style.Dash == MarkupStyle.Dashed ? new[] { line * 4, line * 2.5f } : new[] { line * 0.01f + 0.01f, line * 2 }));
            }
            annotation.GetPdfObject().Put(PdfName.BS, border);
            if (style.Opacity < 100) annotation.GetPdfObject().Put(PdfName.CA, new PdfNumber(style.Opacity / 100.0));
            annotation.SetFlags(PdfAnnotation.PRINT);
            return annotation;
        }

        private static Rectangle Grow(Rectangle r, float by) => new(r.GetX() - by, r.GetY() - by, r.GetWidth() + 2 * by, r.GetHeight() + 2 * by);
    }
}
