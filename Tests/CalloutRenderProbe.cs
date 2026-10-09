using System.IO;
using System.Windows.Media.Imaging;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    static async Task CalloutRenderProbeAsync()
    {
        Directory.CreateDirectory(Output);
        TestCalloutAnnotation();
        string path = Path.Combine(Output, "callout.pdf");
        var bmp = await PdfThumbnailService.RenderPageAsync(path, 0, 800, default, PdfRenderPriority.Visible, null, true);
        if (bmp == null) { Console.WriteLine("render failed"); return; }
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(Output, "callout-render.png")); enc.Save(fs);
        Console.WriteLine("rendered " + Path.Combine(Output, "callout-render.png"));
    }
}
