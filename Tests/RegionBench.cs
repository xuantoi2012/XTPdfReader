using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    /// <summary>--region-bench &lt;pdf&gt; &lt;pageIndex&gt; &lt;fullWidth&gt; &lt;rectHeight&gt; [yOffset]: end-to-end time of one visible-region render (the cost of one
    /// exact-size zoom step) with a warm page, for several rectangle heights up to <c>rectHeight</c>, so the way the cost grows with the area is visible.
    /// Measured 04/10 on a dense 165 MB CAD page: 1376 px wide, 8 px high 0.5 ms, 100 px 5.7 ms, 400 px 13 ms, 781 px 48 ms (the lower half of the page
    /// is much denser); LCD text, image-cache flags and the 8 ms progressive slice made no difference, and splitting the region into strips on one or
    /// two PDFium instances was slower than one render. Read-only; nothing is written.</summary>
    static void RegionBench(string pdf, int pageIndex, int fullWidth, int rectHeight, int yOffset = 40)
    {
        Console.WriteLine($"file={System.IO.Path.GetFileName(pdf)} page={pageIndex + 1} fullWidth={fullWidth} y={yOffset} pdfium instances={PdfiumPool.Count}");
        int fullHeight = (int)(fullWidth * 0.71);
        foreach (int height in new[] { 8, 100, 400, rectHeight }.Distinct().Where(h => h <= rectHeight).OrderBy(h => h))
        {
            var times = new List<double>();
            for (int i = 0; i < 24; i++)
            {
                var rect = new Int32Rect(0, yOffset + (i % 3) * 7, fullWidth, Math.Min(height, Math.Max(1, fullHeight - yOffset - 20)));
                var watch = Stopwatch.StartNew();
                var tiles = PdfThumbnailService.RenderPageTilesBatchAsync(pdf, pageIndex, fullWidth, fullHeight, new[] { rect }).GetAwaiter().GetResult();
                watch.Stop();
                if (tiles.Count != 1 || tiles[0] == null) { Console.WriteLine($"height {height}: render failed"); break; }
                if (i >= 4) times.Add(watch.Elapsed.TotalMilliseconds);
            }
            if (times.Count == 0) continue;
            times.Sort();
            Console.WriteLine($"{fullWidth}x{height,-4} median {times[times.Count / 2],6:0.0} ms  min {times[0],6:0.0}  p90 {times[(int)(times.Count * 0.9)],6:0.0}  max {times[^1],6:0.0}");
        }
    }
}
