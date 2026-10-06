using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    /// <summary>--mupdf-ipc-bench &lt;pdf&gt; &lt;pageNumber&gt;: round-trip time of one MuPDF worker render (request, render, pixel transfer, BitmapSource) for a
    /// full page (4608 px wide), a viewport crop (1376x781 of a 6000 px page) and a thumbnail (340 px). Every iteration uses a slightly different
    /// size so the managed raster cache cannot answer. Read-only.</summary>
    static void MuPdfIpcBench(string pdf, int pageNumber)
    {
        int page = pageNumber - 1;
        var cases = new (string Name, Func<int, (int FullWidth, int FullHeight, Int32Rect Rect)> Make)[]
        {
            ("full page 4608 wide", i => (4608 + i, 0, new Int32Rect(0, 0, 4608 + i, 0))),
            ("viewport crop 1376x781", i => (6000 + i, 4000, new Int32Rect(200, 300, 1376, 781))),
            ("thumbnail 340 wide", i => (340 + i, 0, new Int32Rect(0, 0, 340 + i, 0))),
        };
        foreach (var (name, make) in cases)
        {
            var times = new List<double>();
            for (int i = 0; i < 14; i++)
            {
                var (fullWidth, fullHeight, rect) = make(i);
                var watch = Stopwatch.StartNew();
                var result = ExperimentalMuPdfViewport.RenderAsync(pdf, page, fullWidth, fullHeight, new[] { rect }, default).GetAwaiter().GetResult();
                watch.Stop();
                if (result.Count != 1 || result[0] == null) { Console.WriteLine($"{name}: render failed"); break; }
                if (i >= 4) times.Add(watch.Elapsed.TotalMilliseconds);
            }
            if (times.Count == 0) continue;
            times.Sort();
            Console.WriteLine($"{name,-26} median {times[times.Count / 2],7:0.0} ms  min {times[0],7:0.0}  max {times[^1],7:0.0}");
        }
        ExperimentalMuPdfViewport.Shutdown();
    }
}
