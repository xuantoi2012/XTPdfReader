using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace XTPdfMergeApp.Services
{
    /// <summary>1 kết quả tìm: trang (1-based), thứ tự trong trang, đoạn trích và các hình chữ nhật trên trang HIỂN THỊ (chuẩn hoá 0..1, gốc trên-trái).</summary>
    public sealed record SearchHit(string Path, int PageNumber, int IndexOnPage, string Snippet, IReadOnlyList<(double U1, double V1, double U2, double V2)> Rects);

    /// <summary>Tổng kết 1 lượt tìm trong 1 file.</summary>
    public sealed record SearchSummary(int TotalPages, int PagesWithoutText);

    public static partial class PdfThumbnailService
    {
        private const int SearchPagesPerGate = 12;
        private const int MaxMatchesPerPage = 200;

        /// <summary>
        /// Tìm chữ trong MỌI trang của 1 file bằng văn bản PDFium. Kết quả trả dần theo lô (<paramref name="onBatch"/>: các kết quả mới, số trang đã xét).
        /// Giữ gate PDFium theo từng lô nhỏ ở mức Background để vẽ trang đang xem không bị chặn. Trang không có văn bản tìm được (chữ vẽ bằng nét, bản scan)
        /// được đếm trong <see cref="SearchSummary.PagesWithoutText"/>. null = không mở được file.
        /// </summary>
        public static async Task<SearchSummary?> SearchAsync(string pdfPath, string query, bool matchCase, bool wholeWord,
            Action<IReadOnlyList<SearchHit>, int>? onBatch, CancellationToken cancellationToken = default)
        {
            if (_shuttingDown || string.IsNullOrEmpty(query)) return null;
            Interlocked.Increment(ref _inFlightPublicCalls);
            PdfiumInstance? pdfium = null;
            try
            {
                string normalized = NormalizePath(pdfPath);
                pdfium = ChooseInstance(normalized, -1);
                pdfium.AddLoad(1);
                using var usage = await AcquireDocumentAsync(normalized, pdfium, cancellationToken).ConfigureAwait(false);
                if (usage == null) return null;
                var lease = usage.Lease;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.RetiredToken);
                var token = linked.Token;

                uint flags = (matchCase ? 1u : 0u) | (wholeWord ? 2u : 0u);
                int total = lease.PageCount, withoutText = 0;
                for (int start = 0; start < total; start += SearchPagesPerGate)
                {
                    token.ThrowIfCancellationRequested();
                    int from = start, to = Math.Min(total, start + SearchPagesPerGate);
                    using var native = await EnterPdfiumGateAsync(lease.Pdfium, PdfRenderPriority.Background, token).ConfigureAwait(false);
                    var batch = new List<SearchHit>();
                    int noText = 0;
                    await Task.Run(() =>
                    {
                        for (int i = from; i < to; i++)
                        {
                            token.ThrowIfCancellationRequested();
                            IntPtr page = lease.Pdfium.LoadPage(lease.Document, i);
                            if (page == IntPtr.Zero) { noText++; continue; }
                            lease.Pdfium.MarkPageParsed();
                            try
                            {
                                var geometry = lease.Pdfium.GetPageGeometry(page);
                                var matches = lease.Pdfium.FindText(page, query, flags, MaxMatchesPerPage, out int chars);
                                if (chars <= 0) { noText++; continue; }
                                for (int m = 0; m < matches.Count; m++)
                                {
                                    var rects = matches[m].Rects.Select(r => geometry.UserRectToDisplay(r.L, r.B, r.R, r.T)).ToList();
                                    batch.Add(new SearchHit(pdfPath, i + 1, m, matches[m].Snippet, rects));
                                }
                            }
                            finally { lease.Pdfium.ClosePage(page); }
                        }
                    }, token).ConfigureAwait(false);
                    withoutText += noText;
                    onBatch?.Invoke(batch, to);
                }
                return new SearchSummary(total, withoutText);
            }
            catch (OperationCanceledException) { return null; }
            catch { return null; }
            finally
            {
                pdfium?.AddLoad(-1);
                Interlocked.Decrement(ref _inFlightPublicCalls);
            }
        }
    }
}
