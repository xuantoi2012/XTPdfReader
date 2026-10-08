using System;
using System.Windows.Media.Imaging;
using iText.IO.Image;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;

namespace XTCapture
{
    /// <summary>"Export to PDF": the picture as a one-page PDF (1 px = 0.75 pt, so the page shows it at its real size on a 100 % screen). iText loads only here.</summary>
    internal static class PdfExporter
    {
        internal const double PointsPerPixel = 0.75;

        public static void Export(BitmapSource image, string path)
        {
            float width = (float)(image.PixelWidth * PointsPerPixel), height = (float)(image.PixelHeight * PointsPerPixel);
            using var document = new PdfDocument(new PdfWriter(path));
            document.GetDocumentInfo().SetTitle(System.IO.Path.GetFileNameWithoutExtension(path)).SetCreator("XT Capture");
            var page = document.AddNewPage(new PageSize(width, height));
            new PdfCanvas(page).AddImageFittedIntoRectangle(ImageDataFactory.Create(CaptureImaging.EncodePng(image)), new Rectangle(0, 0, width, height), false);
        }
    }
}
