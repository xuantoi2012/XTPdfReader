using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XTPdfMergeApp.Services.Ocr
{
    /// <summary>A word read from a scan, as fractions (0..1, origin top left) of the DISPLAYED page, so it fits any /Rotate.</summary>
    internal sealed record OcrWord(string Text, double U1, double V1, double U2, double V2);

    /// <summary>The words of one page. <see cref="Skipped"/>: the page already had text. <see cref="Error"/>: the page could not be read (the others still were).</summary>
    internal sealed record OcrPageResult(int PageIndex, bool Skipped, IReadOnlyList<OcrWord> Words, string? Error = null);

    internal sealed record OcrOptions(string Language = "vie", int Dpi = 300, bool SkipPagesWithText = true);

    /// <summary>One rectangle of a page to read (fractions of the displayed page). <see cref="Key"/> tells the answers apart (for example "number").</summary>
    internal sealed record OcrRegionRequest(int PageIndex, string Key, double U1, double V1, double U2, double V2);

    /// <summary>The text read in one rectangle (empty when nothing readable is there).</summary>
    internal sealed record OcrRegionResult(int PageIndex, string Key, string Text);

    /// <summary>
    /// Reads the text of scanned pages with Tesseract and the Vietnamese + Latin data of tessdata_fast.
    /// It runs in its own native process (or <c>OcrWorker.py</c> when native is unavailable) so rendering never waits for it, and it
    /// only reads the file. <see cref="PdfOcrWriter"/> turns the words into an invisible text layer.
    /// </summary>
    internal static class OcrService
    {
        internal static string WorkerScript => Environment.GetEnvironmentVariable("XTPDF_OCR_WORKER") ?? Path.Combine(AppContext.BaseDirectory, "OcrWorker.py");
        internal static string TessdataFolder => Environment.GetEnvironmentVariable("XTPDF_TESSDATA") ?? Path.Combine(AppContext.BaseDirectory, "tessdata");

        internal static string? Python => ExperimentalMuPdfViewport.RuntimeSetting("XTPDF_MUPDF_PYTHON");
        internal static string? Packages => ExperimentalMuPdfViewport.RuntimeSetting("XTPDF_MUPDF_PACKAGES");
        internal static string? NativeWorker => Environment.GetEnvironmentVariable("XTPDF_NATIVE_OCR") == "0" ||
            Environment.GetEnvironmentVariable("XTPDF_OCR_WORKER") != null ? null : ExperimentalMuPdfViewport.XtNativeWorker;

        /// <summary>A worker and every requested language model are installed.</summary>
        internal static bool IsAvailable(string language = "vie")
            => (NativeWorker != null || Python is { } python && File.Exists(python) && File.Exists(WorkerScript)) &&
                language.Split('+', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } languages &&
                languages.All(l => Path.GetFileName(l) == l && File.Exists(Path.Combine(TessdataFolder, l + ".traineddata")));

        /// <summary>Reads <paramref name="pages"/> (zero based; null = every page) of <paramref name="pdfPath"/>. <paramref name="progress"/> gets (pages finished, pages asked).</summary>
        public static async Task<IReadOnlyList<OcrPageResult>> RunAsync(string pdfPath, IReadOnlyList<int>? pages, OcrOptions options, IProgress<(int Done, int Total)>? progress, CancellationToken token)
        {
            var results = new List<OcrPageResult>();
            int total = pages?.Count ?? 0, done = 0;
            await ExecuteAsync(new
            {
                path = Path.GetFullPath(pdfPath), pages, language = options.Language, dpi = options.Dpi, tessdata = TessdataFolder, skipText = options.SkipPagesWithText,
                password = PdfThumbnailService.TryGetDocumentPassword(pdfPath)
            }, options.Language, root =>
            {
                switch (root.GetProperty("type").GetString())
                {
                    case "start":
                        total = root.GetProperty("pages").GetInt32();
                        progress?.Report((0, total));
                        break;
                    case "page":
                        results.Add(ParsePage(root));
                        progress?.Report((++done, total));
                        break;
                    case "error":
                        if (!root.TryGetProperty("page", out _)) throw new InvalidOperationException(root.GetProperty("message").GetString());
                        results.Add(new OcrPageResult(root.GetProperty("page").GetInt32(), false, Array.Empty<OcrWord>(), root.GetProperty("message").GetString()));
                        progress?.Report((++done, total));
                        break;
                }
            }, results, token).ConfigureAwait(false);
            return results.OrderBy(r => r.PageIndex).ToList();
        }

        /// <summary>
        /// Reads only the given rectangles (for example the sheet number, title and scale boxes of a title block): much faster than a whole page
        /// (about 0.05 s a rectangle) and nothing has to be written to the file. A rectangle on a page that cannot be read gives an empty text.
        /// <paramref name="progress"/> gets (pages finished, pages asked).
        /// </summary>
        public static async Task<IReadOnlyList<OcrRegionResult>> ReadRegionsAsync(string pdfPath, IReadOnlyList<OcrRegionRequest> requests, OcrOptions options, IProgress<(int Done, int Total)>? progress, CancellationToken token)
        {
            var answers = new List<OcrRegionResult>();
            if (requests.Count == 0) return answers;
            int total = requests.Select(r => r.PageIndex).Distinct().Count(), done = 0;
            progress?.Report((0, total));
            var marker = new List<OcrPageResult>(); // only to tell "no answer at all" from "answers"
            await ExecuteAsync(new
            {
                mode = "regions", path = Path.GetFullPath(pdfPath), language = options.Language, dpi = options.Dpi, tessdata = TessdataFolder,
                password = PdfThumbnailService.TryGetDocumentPassword(pdfPath),
                requests = requests.Select(r => new { page = r.PageIndex, key = r.Key, rect = new[] { r.U1, r.V1, r.U2, r.V2 } }).ToList()
            }, options.Language, root =>
            {
                switch (root.GetProperty("type").GetString())
                {
                    case "regions":
                        int page = root.GetProperty("page").GetInt32();
                        foreach (var entry in root.GetProperty("texts").EnumerateObject()) answers.Add(new OcrRegionResult(page, entry.Name, entry.Value.GetString() ?? ""));
                        marker.Add(new OcrPageResult(page, false, Array.Empty<OcrWord>()));
                        progress?.Report((++done, total));
                        break;
                    case "error":
                        if (!root.TryGetProperty("page", out _)) throw new InvalidOperationException(root.GetProperty("message").GetString());
                        marker.Add(new OcrPageResult(root.GetProperty("page").GetInt32(), false, Array.Empty<OcrWord>(), root.GetProperty("message").GetString()));
                        progress?.Report((++done, total));
                        break;
                }
            }, marker, token).ConfigureAwait(false);
            return answers;
        }

        /// <summary>Runs the worker with <paramref name="job"/> (as JSON) and hands every JSON line it prints to <paramref name="onMessage"/>.</summary>
        private static async Task ExecuteAsync<T>(object job, string language, Action<JsonElement> onMessage, List<T> answers, CancellationToken token)
        {
            if (!IsAvailable(language)) throw new InvalidOperationException("OCR is not installed with this copy of the program (the worker or language data is missing).");
            string jobFile = Path.Combine(Path.GetTempPath(), "xtpdf-ocr-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                await File.WriteAllTextAsync(jobFile, JsonSerializer.Serialize(job), token).ConfigureAwait(false);

                string? native = NativeWorker;
                var info = new ProcessStartInfo(native ?? Python!)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                if (native != null) info.ArgumentList.Add("--ocr");
                else { info.ArgumentList.Add("-u"); info.ArgumentList.Add(WorkerScript); }
                info.ArgumentList.Add(jobFile);
                if (native == null && Packages is { } packages) info.Environment["PYTHONPATH"] = packages;
                info.Environment["PYTHONIOENCODING"] = "utf-8";

                string errors = "";
                using var process = Process.Start(info) ?? throw new InvalidOperationException("The OCR program did not start.");
                WorkerJob.Assign(process); // the app closing, however it closes, ends it too
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* keep the normal priority */ }
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) errors += e.Data + Environment.NewLine; };
                process.BeginErrorReadLine();
                using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ } });
                bool completed = false;
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line) || !line.StartsWith('{')) continue;
                    using var message = JsonDocument.Parse(line);
                    if (message.RootElement.GetProperty("type").GetString() == "done") completed = true;
                    onMessage(message.RootElement);
                }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0 || !completed) throw new InvalidOperationException("OCR failed: " + (errors.Trim().Length > 0 ? errors.Trim().Split('\n').Last().Trim() : "exit code " + process.ExitCode));
            }
            finally { try { File.Delete(jobFile); } catch { /* temp file */ } }
        }

        private static OcrPageResult ParsePage(JsonElement root)
        {
            var words = new List<OcrWord>();
            foreach (var w in root.GetProperty("words").EnumerateArray())
                words.Add(new OcrWord(w[0].GetString() ?? "", w[1].GetDouble(), w[2].GetDouble(), w[3].GetDouble(), w[4].GetDouble()));
            return new OcrPageResult(root.GetProperty("page").GetInt32(), root.GetProperty("skipped").GetBoolean(), words);
        }
    }
}
