using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Services;

public static partial class PdfThumbnailService
{
    // The budget is cooperative, not a hard preemption deadline: PDFium chooses when
    // to invoke the pause callback. Parsing and individual image operations may exceed it.
    internal const int ProgressiveSliceMilliseconds = 8;
    internal const int NativePageCacheCapacity = 4;
    private static readonly PdfRenderGate _renderBufferSlots = new(2);
    private static readonly Dictionary<(IntPtr Document, int Index), NativePage> _nativePages = new();
    private static long _pageUseSequence;
    private static int _nativePageCount;
    private static long _pageLoads, _pageCacheHits, _progressiveYields, _cancelledRenders;
    private static long _maxSliceTicks;

    public static int CachedNativePageCount => Volatile.Read(ref _nativePageCount);
    public static long NativePageLoads => Interlocked.Read(ref _pageLoads);
    public static long NativePageCacheHits => Interlocked.Read(ref _pageCacheHits);
    public static long ProgressiveYields => Interlocked.Read(ref _progressiveYields);
    public static long CancelledRenders => Interlocked.Read(ref _cancelledRenders);
    public static double MaxNativeRenderSliceMilliseconds =>
        Interlocked.Read(ref _maxSliceTicks) * 1000d / Stopwatch.Frequency;

    // Only access the dictionary/refcounts while holding the global native gate.
    // A page has one progressive render context, so its operation gate stays held
    // across pauses while other pages may use the global gate between slices.
    private sealed class NativePage
    {
        public required IntPtr Document { get; init; }
        public required int Index { get; init; }
        public required IntPtr Handle { get; init; }
        public readonly PdfRenderGate OperationGate = new();
        public int Users;
        public bool KeepWarm;
        public long LastUse;
    }

    private static async Task<NativePage> AcquirePageAsync(PdfDocumentLease document, int index,
        PdfRenderPriority priority, CancellationToken token)
    {
        using var native = await EnterPdfiumGateAsync(priority, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var key = (document.Document, index);
        if (!_nativePages.TryGetValue(key, out var page))
        {
            long loadStart = Stopwatch.GetTimestamp();
            var handle = FPDF_LoadPage(document.Document, index);
            RenderDiagnostics.PageOpen.Record(loadStart);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("PDFium could not open the page.");
            page = new NativePage { Document = document.Document, Index = index, Handle = handle };
            _nativePages.Add(key, page);
            Interlocked.Increment(ref _pageLoads);
        }
        else Interlocked.Increment(ref _pageCacheHits);
        page.Users++;
        page.KeepWarm |= priority == PdfRenderPriority.Visible;
        page.LastUse = ++_pageUseSequence;
        TrimNativePages();
        return page;
    }

    private static async Task ReleasePageAsync(NativePage page)
    {
        using var native = await EnterPdfiumGateAsync().ConfigureAwait(false);
        page.Users--;
        page.LastUse = ++_pageUseSequence;
        TrimNativePages();
    }

    private static void TrimNativePages()
    {
        foreach (var page in _nativePages.Values.Where(p => p.Users == 0).OrderBy(p => p.LastUse).ToArray())
        {
            if (page.KeepWarm && _nativePages.Count <= NativePageCacheCapacity) continue;
            FPDF_ClosePage(page.Handle);
            _nativePages.Remove((page.Document, page.Index));
        }
        Volatile.Write(ref _nativePageCount, _nativePages.Count);
    }

    private static void RemoveDocumentPages(IntPtr document)
    {
        foreach (var page in _nativePages.Values.Where(p => p.Document == document).ToArray())
        {
            Debug.Assert(page.Users == 0);
            FPDF_ClosePage(page.Handle);
            _nativePages.Remove((page.Document, page.Index));
        }
        Volatile.Write(ref _nativePageCount, _nativePages.Count);
    }

    public static void ReleaseCachedPages()
    {
        Interlocked.Increment(ref _inFlightPublicCalls);
        _ = Task.Run(async () =>
        {
            try
            {
                using var native = await EnterPdfiumGateAsync().ConfigureAwait(false);
                foreach (var page in _nativePages.Values) page.KeepWarm = false;
                TrimNativePages();
            }
            finally { Interlocked.Decrement(ref _inFlightPublicCalls); }
        });
    }

    private static async Task<BitmapSource?> RenderPageProgressiveAsync(PdfDocumentLease document,
        int index, double requestedWidth, PdfRenderPriority priority, CancellationToken token)
    {
        var page = await AcquirePageAsync(document, index, priority, token).ConfigureAwait(false);
        bool locked = false;
        try
        {
            long queueStart = Stopwatch.GetTimestamp();
            await page.OperationGate.WaitAsync(priority, token).ConfigureAwait(false);
            RenderDiagnostics.PageQueue.Record(queueStart);
            locked = true;
            int width, height;
            using (var native = await EnterPdfiumGateAsync(priority, token).ConfigureAwait(false))
            {
                double pageWidth = FPDF_GetPageWidth(page.Handle), pageHeight = FPDF_GetPageHeight(page.Handle);
                if (pageWidth <= 0 || pageHeight <= 0 || !double.IsFinite(requestedWidth)) return null;
                double w = Math.Clamp(requestedWidth, 1, MaxRenderPixels);
                double h = w * pageHeight / pageWidth;
                double scale = Math.Min(1, Math.Sqrt(MaxRenderPixels / (w * h)));
                width = Math.Max(1, (int)Math.Floor(w * scale));
                height = Math.Max(1, (int)Math.Round(h * scale));
            }
            return await RenderRegionProgressiveAsync(page, width, height, new Int32Rect(0, 0, width, height),
                priority, token).ConfigureAwait(false);
        }
        finally
        {
            if (locked) page.OperationGate.Release();
            await ReleasePageAsync(page).ConfigureAwait(false);
        }
    }

    private static async Task RenderTilesProgressiveAsync(PdfDocumentLease document, int index,
        int fullWidth, int fullHeight, IReadOnlyList<Int32Rect> rectangles,
        List<BitmapSource?> results, CancellationToken token)
    {
        var page = await AcquirePageAsync(document, index, PdfRenderPriority.Visible, token).ConfigureAwait(false);
        bool locked = false;
        try
        {
            long queueStart = Stopwatch.GetTimestamp();
            await page.OperationGate.WaitAsync(PdfRenderPriority.Visible, token).ConfigureAwait(false);
            RenderDiagnostics.PageQueue.Record(queueStart);
            locked = true;
            for (int i = 0; i < rectangles.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (_shuttingDown) break;
                results[i] = await RenderRegionProgressiveAsync(page, fullWidth, fullHeight, rectangles[i],
                    PdfRenderPriority.Visible, token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (locked) page.OperationGate.Release();
            await ReleasePageAsync(page).ConfigureAwait(false);
        }
    }

    private static async Task<BitmapSource?> RenderRegionProgressiveAsync(NativePage page,
        int fullWidth, int fullHeight, Int32Rect rect, PdfRenderPriority priority, CancellationToken token)
    {
        int x = Math.Clamp(rect.X, 0, fullWidth - 1), y = Math.Clamp(rect.Y, 0, fullHeight - 1);
        int width = Math.Min(rect.Width, fullWidth - x), height = Math.Min(rect.Height, fullHeight - y);
        if (width <= 0 || height <= 0 || (long)width * height > MaxRenderPixels) return null;
        using var pause = new ProgressivePause(token);
        long bufferStart = Stopwatch.GetTimestamp();
        await _renderBufferSlots.WaitAsync(priority, token).ConfigureAwait(false);
        RenderDiagnostics.BufferQueue.Record(bufferStart);
        IntPtr bitmap = IntPtr.Zero, pixels = IntPtr.Zero;
        bool started = false;
        int stride = 0;
        try
        {
            int status;
            using (var native = await EnterPdfiumGateAsync(priority, token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                bitmap = FPDFBitmap_Create(width, height, 1);
                if (bitmap == IntPtr.Zero) return null;
                if (FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF) == 0) return null;
                pixels = FPDFBitmap_GetBuffer(bitmap);
                stride = FPDFBitmap_GetStride(bitmap);
                pause.BeginSlice();
                started = true;
                status = FPDF_RenderPageBitmap_Start(bitmap, page.Handle, -x, -y, fullWidth, fullHeight, 0,
                    FpdfLcdText | FpdfNoNativeText | FpdfRenderLimitedImageCache, pause.Pointer);
                pause.RecordSlice();
            }
            while (status == 1) // FPDF_RENDER_TOBECONTINUED
            {
                Interlocked.Increment(ref _progressiveYields);
                token.ThrowIfCancellationRequested();
                // The global gate is released, allowing other pages to run. A continuation
                // remains at its original priority; cancellation is checked before re-entry.
                await Task.Yield();
                using var native = await EnterPdfiumGateAsync(priority, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                pause.BeginSlice();
                status = FPDF_RenderPage_Continue(page.Handle, pause.Pointer);
                pause.RecordSlice();
            }
            token.ThrowIfCancellationRequested();
            if (status != 2 || pixels == IntPtr.Zero || stride <= 0) return null;
            // Snapshot only a completed image. WPF copies from the native buffer outside
            // the global PDFium gate; ownership stays here until that copy is complete.
            long copyStart = Stopwatch.GetTimestamp();
            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                pixels, checked(stride * height), stride);
            result.Freeze();
            RenderDiagnostics.BitmapCopy.Record(copyStart);
            return result;
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelledRenders);
            throw;
        }
        finally
        {
            try
            {
                using var native = await EnterPdfiumGateAsync(PdfRenderPriority.Visible).ConfigureAwait(false);
                if (started) FPDF_RenderPage_Close(page.Handle);
                if (bitmap != IntPtr.Zero) FPDFBitmap_Destroy(bitmap);
            }
            finally { _renderBufferSlots.Release(); }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PauseCallback(IntPtr self);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePause
    {
        public int Version;
        public IntPtr NeedToPauseNow;
        public IntPtr User;
    }

    private sealed class ProgressivePause : IDisposable
    {
        private readonly PauseCallback _callback;
        private readonly CancellationToken _token;
        private long _start, _deadline;
        public IntPtr Pointer { get; }
        public ProgressivePause(CancellationToken token)
        {
            _token = token;
            _callback = _ => _token.IsCancellationRequested || _shuttingDown || Stopwatch.GetTimestamp() >= _deadline ? 1 : 0;
            Pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativePause>());
            Marshal.StructureToPtr(new NativePause { Version = 1,
                NeedToPauseNow = Marshal.GetFunctionPointerForDelegate(_callback) }, Pointer, false);
        }
        public void BeginSlice()
        {
            _start = Stopwatch.GetTimestamp();
            _deadline = _start + Stopwatch.Frequency * ProgressiveSliceMilliseconds / 1000;
        }
        public void RecordSlice()
        {
            RenderDiagnostics.RasterSlice.Record(_start);
            long ticks = Stopwatch.GetTimestamp() - _start;
            long previous;
            while (ticks > (previous = Interlocked.Read(ref _maxSliceTicks)))
                if (Interlocked.CompareExchange(ref _maxSliceTicks, ticks, previous) == previous) break;
        }
        public void Dispose()
        {
            Marshal.FreeHGlobal(Pointer);
            GC.KeepAlive(_callback);
        }
    }

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FPDF_RenderPageBitmap_Start(IntPtr bitmap, IntPtr page,
        int x, int y, int width, int height, int rotation, int flags, IntPtr pause);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FPDF_RenderPage_Continue(IntPtr page, IntPtr pause);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_RenderPage_Close(IntPtr page);
}
