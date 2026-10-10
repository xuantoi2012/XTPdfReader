using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using XTPdfMergeApp;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;

// Time from a zoom step or a pan to the moment the Reader shows sharp pixels for what is on screen.
// Run with XTPDF_NATIVE_WORKER=1; compare XTPDF_RENDER_THREADS=1 with the default.
internal static partial class Program
{
    static void BenchmarkZoomSharp(string path)
    {
        if (Application.Current == null) CreateReaderTestApplication();
        ReaderPerformanceProfile.Apply(ReaderPerformanceMode.Balance);
        var reader = new ReaderWindow { Width = 1500, Height = 950 };
        var app = Application.Current!; var prior = app.MainWindow; app.MainWindow = reader;
        Exception? failure = null; var frame = new DispatcherFrame(); reader.Show();
        reader.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try
            {
                await reader.Session.OpenFilesInReaderAsync(new[] { path });
                var group = reader.Session.Documents.Single();
                int pageIndex = int.TryParse(Environment.GetEnvironmentVariable("XTPDF_BENCH_PAGE"), out int p) ? p : group.Pages.Count / 2;
                await reader.ShowPageAsync(group, group.Pages[Math.Clamp(pageIndex, 0, group.Pages.Count - 1)]);
                var view = (ContinuousPdfView)reader.FindName("ReaderContinuousView");
                var zoomMode = typeof(ReaderWindow).GetField("_readerZoomMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                zoomMode.SetValue(reader, Enum.Parse(zoomMode.FieldType, "Manual"));
                await Task.Delay(2500);

                async Task<double> UntilSharp(int timeoutMs)
                {
                    var watch = Stopwatch.StartNew();
                    // Let the update timer run first so a request is pending before it is polled.
                    await Task.Delay(30);
                    while (watch.ElapsedMilliseconds < timeoutMs)
                    {
                        if (view.SharpAtCurrentZoom) return watch.Elapsed.TotalMilliseconds;
                        await Task.Delay(4);
                    }
                    return -1;
                }

                if (Environment.GetEnvironmentVariable("XTPDF_BENCH_MODE") == "scroll")
                {
                    // Scrolling to pages that have not been shown: time until what is visible is sharp.
                    double z0 = double.TryParse(Environment.GetEnvironmentVariable("XTPDF_BENCH_ZOOM"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double zz) ? zz : 1.0;
                    view.ZoomAt(z0, new Point(view.ViewportWidth / 2, 0));
                    await UntilSharp(8000);
                    int gap = int.TryParse(Environment.GetEnvironmentVariable("XTPDF_BENCH_GAP"), out int g) ? g : 700;
                    int jump = int.TryParse(Environment.GetEnvironmentVariable("XTPDF_BENCH_JUMP"), out int j) ? j : 1;
                    var start = group.Pages.Count / 2;
                    var scrollTimes = new List<double>();
                    for (int i = 1; i <= 14; i++)
                    {
                        int target = Math.Clamp(start + i * jump, 0, group.Pages.Count - 1);
                        view.ScrollToPage(target);
                        double ms = await UntilSharp(8000);
                        scrollTimes.Add(ms);
                        Console.WriteLine($"scroll to page {target + 1,3}: sharp after {(ms < 0 ? ">8000" : ms.ToString("0")),6} ms");
                        await Task.Delay(gap);
                    }
                    var ok = scrollTimes.Where(t => t >= 0).OrderBy(t => t).ToArray();
                    if (ok.Length > 0) Console.WriteLine($"median {ok[ok.Length / 2]:0} ms, p90 {ok[(int)(ok.Length * 0.9)]:0} ms, max {ok[^1]:0} ms");
                    return;
                }

                var centre = new Point(view.ViewportWidth / 2, view.ViewportHeight / 2);
                var rows = new List<string>();
                double zoom = view.Zoom;
                Console.WriteLine($"page index {pageIndex}, start zoom {zoom:0.00}, threads env = {Environment.GetEnvironmentVariable("XTPDF_RENDER_THREADS") ?? "default"}");
                // Zoom in by steps, then out again; each step starts from a settled view.
                foreach (double factor in new[] { 1.5, 1.5, 1.5, 1.5, 1.5, 1.8, 1.8, 1.8, 0.4, 0.4, 2.0, 2.0, 2.0, 2.0, 1.4, 1.4 })
                {
                    double target = Math.Clamp(zoom * factor, 0.1, 24);
                    view.ZoomAt(target, centre);
                    double ms = await UntilSharp(6000);
                    zoom = view.Zoom;
                    rows.Add($"zoom x{factor,-4:0.0#} -> {zoom,6:0.00}  sharp after {(ms < 0 ? ">6000" : ms.ToString("0")),6} ms");
                    await Task.Delay(900);
                }
                // Pan: a third of a viewport at a time, at the last zoom.
                var origin = view.ViewOffset;
                for (int i = 1; i <= 5; i++)
                {
                    view.RestoreViewOffset(new Point(origin.X, origin.Y + i * view.ViewportHeight * 0.33));
                    double ms = await UntilSharp(6000);
                    rows.Add($"pan step {i}                 sharp after {(ms < 0 ? ">6000" : ms.ToString("0")),6} ms");
                    await Task.Delay(500);
                }
                foreach (var row in rows) Console.WriteLine(row);
                var times = rows.Select(r => r.Split("after")[1].Replace("ms", "").Trim()).Where(t => double.TryParse(t, out _)).Select(double.Parse).OrderBy(x => x).ToArray();
                if (times.Length > 0)
                    Console.WriteLine($"median {times[times.Length / 2]:0} ms, p90 {times[(int)(times.Length * 0.9)]:0} ms, max {times[^1]:0} ms");
            }
            catch (Exception ex) { failure = ex; }
            finally { reader.Close(); app.MainWindow = prior; frame.Continue = false; }
        }));
        Dispatcher.PushFrame(frame);
        if (failure != null) throw failure;
    }
}
