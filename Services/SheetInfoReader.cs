using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using iText.Kernel.Pdf;
using XTPdfMergeApp.Services.Ocr;

namespace XTPdfMergeApp.Services
{
    /// <summary>Where the sheet info is read from: the PDF's own text where there is some and OCR where there is none (Auto), the text only, or always OCR.</summary>
    internal enum SheetReadSource { Auto, TextLayer, Ocr }

    /// <summary>One field of one page: its text and whether it came from OCR (it may then need a look).</summary>
    internal sealed record SheetFieldRead(string Text, bool FromOcr)
    {
        public static readonly SheetFieldRead Empty = new("", false);
    }

    internal sealed record SheetPageReading(int Page, SheetFieldRead Number, SheetFieldRead Title, SheetFieldRead Scale, string SizeKey, bool HasAreas);

    internal sealed record SheetReadResult(IReadOnlyList<SheetPageReading> Pages, int OcrFields, bool OcrUnavailable);

    /// <summary>
    /// Reads sheet number / title / scale from the areas of the title block: from the PDF's text layer when the area has text, and (Auto / Ocr) by OCR of just
    /// that area when it has none, so a scan needs no whole-file OCR first and nothing is written to the file.
    /// </summary>
    internal static class SheetInfoReader
    {
        public static async Task<SheetReadResult> ReadAsync(string path, IReadOnlyList<int> pages, Func<string, TitleBlockLayout?> layoutFor, SheetReadSource source,
            IProgress<(int Done, int Total)>? progress, CancellationToken token)
        {
            // 1. the text layer (and the paper size of every page, to pick its areas)
            var rows = await Task.Run(() => ReadTextLayer(path, pages, layoutFor, source != SheetReadSource.Ocr), token).ConfigureAwait(false);

            // 2. OCR of the areas that have no text (or all of them)
            var requests = new List<OcrRegionRequest>();
            foreach (var row in rows)
            {
                foreach (var (key, region, text) in new[] { ("number", row.Layout?.Number, row.Number), ("title", row.Layout?.Title, row.Title), ("scale", row.Layout?.Scale, row.Scale) })
                {
                    if (region is not { IsEmpty: false }) continue;
                    if (source == SheetReadSource.TextLayer) continue;
                    if (source == SheetReadSource.Auto && text.Trim().Length > 0) continue;
                    requests.Add(new OcrRegionRequest(row.Page - 1, key, region.Left, region.Top, region.Right, region.Bottom));
                }
            }
            bool unavailable = requests.Count > 0 && !OcrService.IsAvailable();
            var answers = new Dictionary<(int, string), string>();
            if (requests.Count > 0 && !unavailable)
                foreach (var answer in await OcrService.ReadRegionsAsync(path, requests, new OcrOptions(), progress, token).ConfigureAwait(false))
                    answers[(answer.PageIndex + 1, answer.Key)] = Clean(answer.Text);

            SheetFieldRead Field(int page, string key, string layerText)
            {
                if (answers.TryGetValue((page, key), out var ocr) && ocr.Length > 0) return new SheetFieldRead(ocr, true);
                return new SheetFieldRead(source == SheetReadSource.Ocr && !answers.ContainsKey((page, key)) ? "" : layerText, false);
            }
            var result = rows.Select(r => new SheetPageReading(r.Page, Field(r.Page, "number", r.Number), Field(r.Page, "title", r.Title), Field(r.Page, "scale", r.Scale), r.SizeKey, r.Layout?.HasAny == true)).ToList();
            return new SheetReadResult(result, answers.Values.Count(v => v.Length > 0), unavailable);
        }

        private sealed record LayerRow(int Page, string SizeKey, TitleBlockLayout? Layout, string Number, string Title, string Scale);

        private static List<LayerRow> ReadTextLayer(string path, IReadOnlyList<int> pages, Func<string, TitleBlockLayout?> layoutFor, bool useText)
        {
            var result = new List<LayerRow>();
            var properties = new ReaderProperties();
            if (PdfThumbnailService.TryGetDocumentPassword(path) is { Length: > 0 } password) properties.SetPassword(Encoding.UTF8.GetBytes(password));
            using var doc = new PdfDocument(new PdfReader(path, properties));
            foreach (int number in pages)
            {
                if (number < 1 || number > doc.GetNumberOfPages()) continue;
                var page = doc.GetPage(number);
                string key = TitleBlockReader.SizeKey(page);
                var layout = layoutFor(key);
                string Safe(TitleBlockRegion? r) { try { return !useText || r == null || r.IsEmpty ? "" : Clean(TitleBlockReader.TextIn(page, r)); } catch { return ""; } }
                result.Add(new LayerRow(number, key, layout, Safe(layout?.Number), Safe(layout?.Title), Safe(layout?.Scale)));
            }
            return result;
        }

        /// <summary>One line: the whitespace squeezed, stray rule-line marks at the ends dropped.</summary>
        internal static string Clean(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim('|', '_', '~', '`', ' ');
    }
}
