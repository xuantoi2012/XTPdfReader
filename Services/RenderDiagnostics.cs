using System;
using System.Diagnostics;
using System.Threading;

namespace XTPdfMergeApp.Services;

internal static class RenderDiagnostics
{
    internal sealed class Timing
    {
        private long _count, _ticks, _max;
        public void Record(long start) => AddTicks(Stopwatch.GetTimestamp() - start);
        public void AddMilliseconds(double ms) => AddTicks((long)(ms * Stopwatch.Frequency / 1000));
        private void AddTicks(long ticks)
        {
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _ticks, ticks);
            long previous;
            while (ticks > (previous = Interlocked.Read(ref _max)))
                if (Interlocked.CompareExchange(ref _max, ticks, previous) == previous) break;
        }
        public long Count => Interlocked.Read(ref _count);
        public double TotalMilliseconds => Interlocked.Read(ref _ticks) * 1000d / Stopwatch.Frequency;
        public double MaxMilliseconds => Interlocked.Read(ref _max) * 1000d / Stopwatch.Frequency;
        public override string ToString() => $"{TotalMilliseconds / Math.Max(1, Count):0.0}/{MaxMilliseconds:0.0}";
    }
    internal static readonly Timing DocumentOpen = new(), NativeWait = new(), PageOpen = new(), RasterSlice = new(),
        BitmapCopy = new(), PresentationWait = new(), PresentationWork = new(), PageQueue = new(), BufferQueue = new(),
        FileBufferRead = new(), FileBlockRead = new();
    public static string Summary =>
        $"Timing avg/max ms (session): gate {NativeWait}, page queue {PageQueue}, buffer {BufferQueue}\n" +
        $"Doc open {DocumentOpen}, page load/parse {PageOpen}, raster slice {RasterSlice}, WPF copy {BitmapCopy}\n" +
        $"UI queue {PresentationWait}, UI apply {PresentationWork}\n" +
        $"File → RAM: đọc nền xong cả file {FileBufferRead}, khối 256 KB trang cần gấp {FileBlockRead} ({FileBlockRead.Count} lần), " +
        $"đang giữ {PdfFileBuffer.ReservedBytes / 1048576.0:0} MB\n" +
        PdfiumPoolSummary;

    /// <summary>Bước 3: số bản PDFium chạy song song và số việc mỗi bản đã làm / đang giữ.</summary>
    private static string PdfiumPoolSummary
    {
        get
        {
            try
            {
                var instances = PdfiumPool.Instances;
                return $"PDFium: {instances.Count}/{PdfiumPool.DesiredCount} bản song song — việc đã làm (đang giữ): " +
                       string.Join(", ", System.Linq.Enumerable.Select(instances, i => $"#{i.Index} {i.Completed} ({i.Load})"));
            }
            catch (Exception ex)
            {
                return "PDFium: " + ex.Message;
            }
        }
    }
    // Disk tracing is opt-in. Frame-critical UI paths should not perform synchronous
    // file writes in ordinary operation or during an uninstrumented benchmark.
    public static bool TraceEnabled { get; } = Environment.GetEnvironmentVariable("XTPDF_RENDER_TRACE") == "1";
}
