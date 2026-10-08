using System.IO;
using System.Windows.Media.Imaging;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    /// <summary>Centre (as fractions of the picture) of the pixels that satisfy <paramref name="match"/>, and how many there are.</summary>
    static (double U, double V, int Count) Centroid(BitmapSource image, Func<byte, byte, byte, bool> match)
    {
        var bgra = image.Format == System.Windows.Media.PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);
        double sx = 0, sy = 0;
        int count = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * stride + x * 4;
                if (!match(pixels[i + 2], pixels[i + 1], pixels[i])) continue;
                sx += x; sy += y; count++;
            }
        return count == 0 ? (0, 0, 0) : (sx / count / w, sy / count / h, count);
    }

    /// <summary>Notes and shapes written to /Rotate 0/90/180/270 pages must show where they were put when the page is rendered with its annotations (print, export).</summary>
    static void TestRotatedPagePrintPlacement()
    {
        string folder = System.IO.Path.Combine(Output, "rotated-print");
        Directory.CreateDirectory(folder);
        foreach (int rotation in new[] { 0, 90, 180, 270 })
        {
            string blank = System.IO.Path.Combine(folder, $"blank{rotation}.pdf"), path = System.IO.Path.Combine(folder, $"r{rotation}.pdf");
            using (var doc = new PdfDocument(new PdfWriter(blank))) doc.AddNewPage(new PageSize(842, 1191)).SetRotation(rotation);
            double displayW = rotation is 90 or 270 ? 1191 : 842, displayH = rotation is 90 or 270 ? 842 : 1191;
            // a note and a red rectangle at known places of the DISPLAYED page
            var note = new QuickAnnotationSpec("n1", QuickAnnotationKind.Comment, 1, 0.6, 0.7, 0.6, 0.7, "note");
            var rect = new QuickAnnotationSpec("s1", QuickAnnotationKind.Shape, 1, 0.1, 0.1, 0.3, 0.2, "") { Format = new ShapeStyle(ShapeStyle.Rect, "#FF0000", 6, 0, 0, 100, "#FF0000").Encode() };
            using (var reader = new PdfReader(blank))
            using (var doc = new PdfDocument(reader, new PdfWriter(path)))
                PdfQuickAnnotationService.ApplyChanges(doc, new[] { new QuickAnnotationChange(null, note), new QuickAnnotationChange(null, rect) });

            var image = PdfThumbnailService.RenderPageAsync(path, 0, 1800, default, PdfRenderPriority.Visible, null, true).GetAwaiter().GetResult();
            Check(image != null, $"r{rotation}: rendered");
            if (image == null) continue;
            File.WriteAllBytes(System.IO.Path.Combine(folder, $"r{rotation}.png"), XTCapture.CaptureImaging.EncodePng(image));
            var red = Centroid(image, (r, g, b) => r > 200 && g < 60 && b < 60);
            var yellow = Centroid(image, (r, g, b) => r > 220 && g > 170 && g < 230 && b < 110);
            double iconW = 18.0 / displayW, iconH = 18.0 / displayH; // the icon box in page fractions (CommentIconSize points)
            Console.WriteLine($"r{rotation}: red centre {red.U:0.000},{red.V:0.000} (expected 0.200,0.150) n={red.Count}; yellow centre {yellow.U:0.000},{yellow.V:0.000} (expected about {0.6 + iconW / 2:0.000},{0.7 + iconH / 2:0.000}) n={yellow.Count}");
            Check(Math.Abs(red.U - 0.2) < 0.02 && Math.Abs(red.V - 0.15) < 0.02, $"r{rotation}: the rectangle is where it was put");
            Check(yellow.Count > 20 && Math.Abs(yellow.U - (0.6 + iconW / 2)) < 0.03 && Math.Abs(yellow.V - (0.7 + iconH / 2)) < 0.03, $"r{rotation}: the note icon is where it was put");
        }
    }
}
