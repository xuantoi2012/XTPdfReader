using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Services
{
    public static partial class PdfThumbnailService
    {
        /// <summary>Render page <see cref="PageIndex"/> scaled to <see cref="FullWidth"/>×<see cref="FullHeight"/>, only <see cref="Region"/>.</summary>
        public sealed record MemoryRenderJob(int PageIndex, int FullWidth, int FullHeight, Int32Rect Region);

        /// <summary>
        /// Renders regions of pages of an in-memory PDF (annotation appearances) with annotations, on transparent bitmaps (BGRA, not
        /// premultiplied). The gate of one PDFium instance is taken per job, so visible page renders are never held up for long.
        /// </summary>
        public static async Task<IReadOnlyList<BitmapSource?>> RenderMemoryPagesAsync(byte[] pdf, IReadOnlyList<MemoryRenderJob> jobs,
            PdfRenderPriority priority = PdfRenderPriority.Visible, CancellationToken cancellationToken = default)
        {
            var results = new BitmapSource?[jobs.Count];
            if (_shuttingDown || jobs.Count == 0 || pdf.Length == 0) return results;
            Interlocked.Increment(ref _inFlightPublicCalls);
            var pdfium = PdfiumPool.Choose(_ => false, _ => false, reserveFirst: false);
            pdfium.AddLoad(1);
            var pin = GCHandle.Alloc(pdf, GCHandleType.Pinned);
            IntPtr document = IntPtr.Zero;
            try
            {
                pdfium.EnsureInitialized();
                using (await EnterPdfiumGateAsync(pdfium, priority, cancellationToken).ConfigureAwait(false))
                    unsafe { document = pdfium.LoadMemDocument((byte*)pin.AddrOfPinnedObject(), pdf.Length); }
                if (document == IntPtr.Zero) return results;

                for (int i = 0; i < jobs.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var job = jobs[i];
                    int w = job.Region.Width, h = job.Region.Height;
                    if (w <= 0 || h <= 0 || (long)w * h > MaxRenderPixels) continue;
                    using var native = await EnterPdfiumGateAsync(pdfium, priority, cancellationToken).ConfigureAwait(false);
                    IntPtr page = pdfium.LoadPage(document, job.PageIndex);
                    if (page == IntPtr.Zero) continue;
                    IntPtr bitmap = IntPtr.Zero;
                    try
                    {
                        bitmap = pdfium.BitmapCreate(w, h, 1);
                        if (bitmap == IntPtr.Zero) continue;
                        pdfium.BitmapFillRect(bitmap, 0, 0, w, h, 0x00000000);
                        pdfium.RenderPageBitmap(bitmap, page, -job.Region.X, -job.Region.Y, job.FullWidth, job.FullHeight, 0,
                            FpdfAnnot | FpdfRenderLimitedImageCache);
                        int stride = pdfium.BitmapGetStride(bitmap);
                        var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pdfium.BitmapGetBuffer(bitmap), stride * h, stride);
                        image.Freeze();
                        results[i] = image;
                    }
                    finally
                    {
                        if (bitmap != IntPtr.Zero) pdfium.BitmapDestroy(bitmap);
                        pdfium.ClosePage(page);
                    }
                }
                return results;
            }
            catch (OperationCanceledException)
            {
                return results;
            }
            finally
            {
                if (document != IntPtr.Zero)
                    using (await EnterPdfiumGateAsync(pdfium, PdfRenderPriority.Visible).ConfigureAwait(false))
                        pdfium.CloseDocument(document);
                pin.Free();
                pdfium.AddLoad(-1);
                Interlocked.Decrement(ref _inFlightPublicCalls);
            }
        }
    }
}
