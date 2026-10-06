using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Services;

public static partial class PdfThumbnailService
{
    internal static bool IsDocumentSuspended(string path) => _shuttingDown || _suspendedDocuments.ContainsKey(NormalizePath(path));

    private static async Task<PdfOpenResult> MuPdfOpenAsync(string path)
    {
        if (IsDocumentSuspended(path)) return new(0, PdfOpenFailure.Unknown);
        try
        {
            var reply = await ExperimentalMuPdfViewport.CommandAsync(path, "metadata").ConfigureAwait(false);
            return new(reply.GetProperty("count").GetInt32(), PdfOpenFailure.None);
        }
        catch (Exception ex)
        {
            var failure = !File.Exists(path) ? PdfOpenFailure.File :
                ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ? PdfOpenFailure.Password : PdfOpenFailure.Format;
            return new(0, failure);
        }
    }

    private static async Task<(double Width, double Height)[]?> MuPdfSizesAsync(string path, CancellationToken token)
    {
        if (IsDocumentSuspended(path)) return null;
        try
        {
            var reply = await ExperimentalMuPdfViewport.CommandAsync(path, "metadata", token: token).ConfigureAwait(false);
            return reply.GetProperty("sizes").EnumerateArray().Select(s => (s[0].GetDouble(), s[1].GetDouble())).ToArray();
        }
        catch { return null; }
    }

    private static IReadOnlyList<(double U1, double V1, double U2, double V2)> MuPdfRects(JsonElement reply)
        => reply.GetProperty("rects").EnumerateArray().Select(r => (r[0].GetDouble(), r[1].GetDouble(), r[2].GetDouble(), r[3].GetDouble())).ToArray();

    private static async Task<(IReadOnlyList<(double U1, double V1, double U2, double V2)> Rects, string Text)?> MuPdfSelectAsync(
        string path, int pageNumber, double ax, double ay, double bx, double by)
    {
        try
        {
            var reply = await ExperimentalMuPdfViewport.CommandAsync(path, "select", pageNumber - 1, new { ax, ay, bx, by }).ConfigureAwait(false);
            return (MuPdfRects(reply), reply.GetProperty("text").GetString() ?? "");
        }
        catch { return null; }
    }

    private static async Task<SearchSummary?> MuPdfSearchAsync(string path, string query, bool matchCase, bool wholeWord,
        Action<IReadOnlyList<SearchHit>, int>? onBatch, CancellationToken token)
    {
        if (string.IsNullOrEmpty(query)) return null;
        try
        {
            var opened = await MuPdfOpenAsync(path).ConfigureAwait(false);
            if (opened.Failure != PdfOpenFailure.None) return null;
            int withoutText = 0;
            // Pages are searched in groups: one round trip per group, and the worker reads per-character boxes only on pages that can match.
            const int PagesPerRequest = 8;
            for (int first = 0; first < opened.PageCount; first += PagesPerRequest)
            {
                var reply = await ExperimentalMuPdfViewport.CommandAsync(path, "searchrange", first,
                    new { query, matchCase, wholeWord, count = PagesPerRequest }, token).ConfigureAwait(false);
                foreach (var pageReply in reply.GetProperty("pages").EnumerateArray())
                {
                    int page = pageReply.GetProperty("page").GetInt32();
                    if (!pageReply.GetProperty("hasText").GetBoolean()) withoutText++;
                    var hits = pageReply.GetProperty("matches").EnumerateArray().Select((m, i) =>
                        new SearchHit(path, page + 1, i, m.GetProperty("snippet").GetString() ?? "", MuPdfRects(m))).ToArray();
                    onBatch?.Invoke(hits, page + 1);
                }
            }
            return new(opened.PageCount, withoutText);
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
    }

    private static async Task<IReadOnlyList<BitmapSource?>> MuPdfMemoryAsync(byte[] pdf, IReadOnlyList<MemoryRenderJob> jobs,
        PdfRenderPriority priority, CancellationToken token)
    {
        var results = new BitmapSource?[jobs.Count];
        if (pdf.Length == 0 || jobs.Count == 0) return results;
        string temporary = Path.Combine(Path.GetTempPath(), "xtpdf-appearance-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            await File.WriteAllBytesAsync(temporary, pdf, token).ConfigureAwait(false);
            for (int i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                var batch = await ExperimentalMuPdfViewport.RenderAsync(temporary, job.PageIndex, job.FullWidth, job.FullHeight,
                    new[] { job.Region }, token, priority, withAnnotations: true, alpha: true).ConfigureAwait(false);
                results[i] = batch[0];
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await ExperimentalMuPdfViewport.RetireAsync(temporary).ConfigureAwait(false);
            try { File.Delete(temporary); } catch (IOException) { }
        }
        return results;
    }
}
