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

    /// <summary>
    /// Tối ưu #3: trang "nóng" (đang hiện + đang tải trước theo hướng cuộn, do Viewer khai báo) GIỮ page handle
    /// đã parse dù vượt <see cref="NativePageCacheCapacity"/> — trước đây panel thumbnail + Viewer + tải trước
    /// dùng chung 4 chỗ nên trang vừa xem bị đóng rồi phải parse lại (43–449 ms/trang trên file thật qua ổ mạng).
    /// Giới hạn <see cref="MaxHotPages"/> để RAM native không phình với bản vẽ CAD nặng.
    /// </summary>
    internal const int MaxHotPages = 12;

    // ── Tối ưu #5: tạm dừng việc ưu tiên thấp khi người dùng đang zoom/pan ──────────────────────
    // Gate PDFium là 1 luồng; 1 lát progressive thực tế dài tới ~20–70 ms và parse trang không chia lát được.
    // Thumbnail/tải trước (Thumbnail/Background) đang giữ gate → vùng zoom mới phải chờ. Trong lúc tương tác
    // (và 250 ms sau lần cuối), các lệnh ưu tiên thấp đứng chờ NGOÀI gate — cả trước khi bắt đầu lẫn giữa các lát.
    internal const int InteractionHoldMilliseconds = 250;
    private static long _interactionUntil;

    /// <summary>Viewer gọi mỗi nhịp zoom/pan (không gọi khi chỉ cuộn — tải trước lúc cuộn là có chủ đích).</summary>
    public static void NoteInteraction()
        => Volatile.Write(ref _interactionUntil, Stopwatch.GetTimestamp() + Stopwatch.Frequency * InteractionHoldMilliseconds / 1000);

    private static async Task WaitForInteractionIdleAsync(PdfRenderPriority priority, CancellationToken token)
    {
        if (priority == PdfRenderPriority.Visible) return;
        while (true)
        {
            long remaining = Volatile.Read(ref _interactionUntil) - Stopwatch.GetTimestamp();
            if (remaining <= 0) return;
            Interlocked.Increment(ref _interactionDeferrals);
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, remaining * 1000.0 / Stopwatch.Frequency)), token).ConfigureAwait(false);
        }
    }

    private static long _interactionDeferrals;
    /// <summary>Số lần lệnh ưu tiên thấp phải nhường vì đang zoom/pan (bảng Debug).</summary>
    public static long InteractionDeferrals => Interlocked.Read(ref _interactionDeferrals);

    /// <summary>Trước khi parse trang (không chia lát được) — lệnh ưu tiên thấp chờ hết tương tác rồi mới vào gate.</summary>
    private static async Task<PdfiumGateLease> EnterGateAfterInteractionAsync(PdfiumInstance pdfium, PdfRenderPriority priority, CancellationToken token)
    {
        await WaitForInteractionIdleAsync(priority, token).ConfigureAwait(false);
        return await EnterPdfiumGateAsync(pdfium, priority, token).ConfigureAwait(false);
    }
    private static HashSet<(string Path, int Index)> _hotPages = new();

    /// <summary>Viewer khai báo các trang cần giữ đã-parse (số trang 1-based của file nguồn).</summary>
    public static void SetHotPages(IEnumerable<(string Path, int PageNumber)> pages)
    {
        var set = new HashSet<(string, int)>();
        foreach (var (path, pageNumber) in pages)
        {
            if (set.Count >= MaxHotPages) break;
            set.Add((NormalizePath(path).ToUpperInvariant(), pageNumber - 1));
        }
        Volatile.Write(ref _hotPages, set);
    }

    private static bool IsHot(NativePage page)
        => Volatile.Read(ref _hotPages).Contains((page.PathKey, page.Index));
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

    // Only access the dictionary/refcounts while holding the native gate. (Hiện chỉ có 1 bản PDFium; khi có
    // nhiều bản, mỗi bản cần bộ đệm page handle riêng dưới gate của nó.)
    // A page has one progressive render context, so its operation gate stays held
    // across pauses while other pages may use the native gate between slices.
    private sealed class NativePage
    {
        /// <summary>Bản PDFium đã mở page handle này.</summary>
        public required PdfiumInstance Pdfium { get; init; }
        public required IntPtr Document { get; init; }
        /// <summary>Đường dẫn chuẩn hoá (chữ hoa) — để so với tập trang nóng.</summary>
        public required string PathKey { get; init; }
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
        using var native = await EnterGateAfterInteractionAsync(document.Pdfium, priority, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var key = (document.Document, index);
        if (!_nativePages.TryGetValue(key, out var page))
        {
            long loadStart = Stopwatch.GetTimestamp();
            var handle = document.Pdfium.LoadPage(document.Document, index);
            RenderDiagnostics.PageOpen.Record(loadStart);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("PDFium could not open the page.");
            page = new NativePage { Pdfium = document.Pdfium, Document = document.Document, Index = index, Handle = handle,
                PathKey = NormalizePath(document.SourcePath).ToUpperInvariant() };
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
        using var native = await EnterPdfiumGateAsync(page.Pdfium).ConfigureAwait(false);
        page.Users--;
        page.LastUse = ++_pageUseSequence;
        TrimNativePages();
    }

    private static void TrimNativePages()
    {
        int hotCount = _nativePages.Values.Count(IsHot);
        foreach (var page in _nativePages.Values.Where(p => p.Users == 0).OrderBy(p => p.LastUse).ToArray())
        {
            if (IsHot(page)) continue; // #3: trang đang hiện / sắp hiện — giữ bản đã parse
            if (page.KeepWarm && _nativePages.Count - hotCount <= NativePageCacheCapacity) continue;
            page.Pdfium.ClosePage(page.Handle);
            _nativePages.Remove((page.Document, page.Index));
        }
        Volatile.Write(ref _nativePageCount, _nativePages.Count);
    }

    private static void RemoveDocumentPages(IntPtr document)
    {
        foreach (var page in _nativePages.Values.Where(p => p.Document == document).ToArray())
        {
            Debug.Assert(page.Users == 0);
            page.Pdfium.ClosePage(page.Handle);
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
                using var native = await EnterPdfiumGateAsync(PdfiumInstance.Primary).ConfigureAwait(false);
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
            // #5: chờ hết tương tác TRƯỚC khi lấy khoá trang — đã giữ khoá thì không dừng giữa chừng, nếu không vùng
            // zoom của CHÍNH trang này sẽ phải chờ theo.
            await WaitForInteractionIdleAsync(priority, token).ConfigureAwait(false);
            await page.OperationGate.WaitAsync(priority, token).ConfigureAwait(false);
            RenderDiagnostics.PageQueue.Record(queueStart);
            locked = true;
            int width, height;
            using (var native = await EnterPdfiumGateAsync(page.Pdfium, priority, token).ConfigureAwait(false))
            {
                double pageWidth = page.Pdfium.GetPageWidth(page.Handle), pageHeight = page.Pdfium.GetPageHeight(page.Handle);
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
        var pdfium = page.Pdfium;
        IntPtr bitmap = IntPtr.Zero, pixels = IntPtr.Zero;
        bool started = false;
        int stride = 0;
        try
        {
            int status;
            using (var native = await EnterPdfiumGateAsync(pdfium, priority, token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                bitmap = pdfium.BitmapCreate(width, height, 1);
                if (bitmap == IntPtr.Zero) return null;
                if (!pdfium.BitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF)) return null;
                pixels = pdfium.BitmapGetBuffer(bitmap);
                stride = pdfium.BitmapGetStride(bitmap);
                pause.BeginSlice();
                started = true;
                status = pdfium.RenderPageBitmapStart(bitmap, page.Handle, -x, -y, fullWidth, fullHeight, 0,
                    FpdfAnnot | FpdfLcdText | FpdfNoNativeText | FpdfRenderLimitedImageCache, pause.Pointer);
                pause.RecordSlice();
            }
            while (status == 1) // FPDF_RENDER_TOBECONTINUED
            {
                Interlocked.Increment(ref _progressiveYields);
                token.ThrowIfCancellationRequested();
                // The native gate is released, allowing other pages to run. A continuation
                // remains at its original priority; cancellation is checked before re-entry.
                await Task.Yield();
                using var native = await EnterPdfiumGateAsync(pdfium, priority, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                pause.BeginSlice();
                status = pdfium.RenderPageContinue(page.Handle, pause.Pointer);
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
                using var native = await EnterPdfiumGateAsync(pdfium, PdfRenderPriority.Visible).ConfigureAwait(false);
                if (started) pdfium.RenderPageClose(page.Handle);
                if (bitmap != IntPtr.Zero) pdfium.BitmapDestroy(bitmap);
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
}
