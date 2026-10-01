using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using PageSize = iText.Kernel.Geom.PageSize;
using iText.Kernel.Pdf;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    static async Task TestWidgetRenderingAsync()
    {
        string path = Path.Combine(Output, "widget-appearance.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            var page = document.AddNewPage(new PageSize(300, 200));
            var appearance = new PdfStream(System.Text.Encoding.ASCII.GetBytes("q 0 0 1 rg 0 0 100 40 re f Q"));
            appearance.Put(PdfName.Type, PdfName.XObject);
            appearance.Put(PdfName.Subtype, PdfName.Form);
            appearance.Put(PdfName.BBox, new PdfArray(new float[] { 0, 0, 100, 40 }));
            appearance.MakeIndirect(document);
            var normal = new PdfDictionary(); normal.Put(PdfName.N, appearance);
            var field = new PdfDictionary();
            field.Put(PdfName.Type, PdfName.Annot); field.Put(PdfName.Subtype, PdfName.Widget);
            field.Put(PdfName.FT, PdfName.Sig); field.Put(PdfName.T, new PdfString("VisibleSignature"));
            field.Put(PdfName.Rect, new PdfArray(new float[] { 20, 20, 120, 60 }));
            field.Put(PdfName.AP, normal); field.Put(PdfName.F, new PdfNumber(4));
            field.Put(PdfName.P, page.GetPdfObject()); field.MakeIndirect(document);
            page.GetPdfObject().Put(PdfName.Annots, new PdfArray(field));
            var form = new PdfDictionary(); form.Put(PdfName.Fields, new PdfArray(field));
            document.GetCatalog().Put(PdfName.AcroForm, form);
        }
        var screen = await PdfThumbnailService.RenderPageAsync(path, 0, 600);
        Check(BluePixels(screen) > 14000, "Signature widget appearance is included in the page bitmap without editable annotations");
        var thumbnail = await PdfThumbnailService.RenderPageAsync(path, 0, 96);
        Check(BluePixels(thumbnail) > 250, "Signature widget is visible in thumbnails");
        var tile = await PdfThumbnailService.RenderPageTilesBatchAsync(path, 0, 600, 400, new[] { new Int32Rect(30, 270, 230, 110) });
        Check(BluePixels(tile[0]) > 14000, "Signature widget respects tile offsets");
        var print = await PdfThumbnailService.RenderPageTilesBatchAsync(path, 0, 600, 400, new[] { new Int32Rect(0, 0, 600, 400) }, withAnnotations: true);
        Check(BluePixels(print[0]) > 14000, "Printable signature widgets are included in print bands");
        var memory = await PdfThumbnailService.RenderMemoryPagesAsync(File.ReadAllBytes(path),
            new[] { new PdfThumbnailService.MemoryRenderJob(0, 600, 400, new Int32Rect(0, 0, 600, 400)) });
        Check(BluePixels(memory[0]) > 14000, "In-memory PDF rendering includes form widgets");
        for (int round = 0; round < 3; round++)
        {
            using (await PdfThumbnailService.SuspendDocumentAsync(path, TimeSpan.FromSeconds(3))) { }
            var images = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => PdfThumbnailService.RenderPageAsync(path, 0, 600)));
            Check(images.All(i => BluePixels(i) > 14000), "Form widgets survive concurrent rendering and native handle reopening, round " + round);
        }
        await PdfThumbnailService.RetireDocumentAsync(path);
    }

    static int BluePixels(BitmapSource? bitmap)
    {
        if (bitmap == null) return 0;
        int stride = bitmap.PixelWidth * 4, count = 0;
        var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i] > 180 && pixels[i + 1] < 80 && pixels[i + 2] < 80 && pixels[i + 3] > 150) count++;
        return count;
    }

    static async Task RenderSignedSampleAsync(string path)
    {
        var image = await PdfThumbnailService.RenderPageAsync(path, 2, 1200);
        if (image == null) throw new Exception("Signed PDF did not render");
        string output = Path.Combine(Output, "signed-page3.png");
        using var file = File.Create(output);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(file);
        Console.WriteLine("Signed page: " + output);
        await PdfThumbnailService.RetireDocumentAsync(path);
    }
}
