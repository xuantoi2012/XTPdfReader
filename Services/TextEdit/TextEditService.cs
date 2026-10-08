using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using XTPdfMergeApp.Services.Ocr;

namespace XTPdfMergeApp.Services.TextEdit
{
    /// <summary>
    /// Real text editing. A python process (<c>TextEditWorker.py</c>, the embedded Python of the viewer) lists the text runs of a page and, on Save, removes the old
    /// characters from the page and writes the new ones at the same baseline with the same size and colour. Only pages with a real text layer have runs: a scan or a
    /// drawing whose letters are strokes has none, and the tool says so.
    /// </summary>
    internal static class TextEditService
    {
        internal static string WorkerScript => Environment.GetEnvironmentVariable("XTPDF_TEXTEDIT_WORKER") ?? Path.Combine(AppContext.BaseDirectory, "TextEditWorker.py");

        public static bool IsAvailable => OcrService.Python is { } python && File.Exists(python) && File.Exists(WorkerScript);

        private static readonly Dictionary<(string, int, int), PageTextRuns> Cache = new();

        /// <summary>The text runs of a page of the FILE (not counting pending edits). Cached until the file changes.</summary>
        public static async Task<PageTextRuns?> GetRunsAsync(string path, int pageNumber)
        {
            var key = (AnnotationStore.Normalize(path), pageNumber, PdfThumbnailService.FileChangeEpoch);
            if (Cache.TryGetValue(key, out var cached)) return cached;
            PageTextRuns? result = null;
            string? error = null;
            await RunAsync(new { mode = "runs", path = Path.GetFullPath(path), page = pageNumber, password = PdfThumbnailService.TryGetDocumentPassword(path) }, root =>
            {
                switch (root.GetProperty("type").GetString())
                {
                    case "runs":
                        var runs = new List<TextRun>();
                        foreach (var r in root.GetProperty("runs").EnumerateArray())
                        {
                            var box = r.GetProperty("bbox");
                            var origin = r.GetProperty("origin");
                            runs.Add(new TextRun(r.GetProperty("text").GetString() ?? "", box[0].GetDouble(), box[1].GetDouble(), box[2].GetDouble(), box[3].GetDouble(),
                                origin[0].GetDouble(), origin[1].GetDouble(), r.GetProperty("size").GetDouble(), r.GetProperty("color").GetInt32(),
                                r.GetProperty("font").GetString() ?? "", r.GetProperty("flags").GetInt32(), r.GetProperty("bg").GetInt32()));
                        }
                        result = new PageTextRuns(pageNumber, root.GetProperty("width").GetDouble(), root.GetProperty("height").GetDouble(), root.GetProperty("rotation").GetInt32(),
                            runs.Where(x => x.Text.Trim().Length > 0).ToList());
                        break;
                    case "error": error = root.GetProperty("message").GetString(); break;
                }
            }).ConfigureAwait(false);
            if (result == null) throw new InvalidOperationException(error ?? "The text of the page could not be read.");
            if (Cache.Count > 64) Cache.Clear();
            Cache[key] = result;
            return result;
        }

        /// <summary>The runs inside a rectangle (fractions of the page) on each of the pages, one worker call for all of them. A page without any text layer has <c>HasText</c> false.</summary>
        public static async Task<IReadOnlyList<(PageTextRuns Page, bool HasText)>> GetAreaRunsAsync(string path, IReadOnlyList<int> pages, double u1, double v1, double u2, double v2, Action<int>? progress = null)
        {
            var answers = new List<(PageTextRuns, bool)>();
            string? error = null;
            await RunAsync(new { mode = "area", path = Path.GetFullPath(path), pages, rect = new[] { u1, v1, u2, v2 }, password = PdfThumbnailService.TryGetDocumentPassword(path) }, root =>
            {
                switch (root.GetProperty("type").GetString())
                {
                    case "area":
                        var runs = new List<TextRun>();
                        foreach (var r in root.GetProperty("runs").EnumerateArray())
                        {
                            var box = r.GetProperty("bbox");
                            var origin = r.GetProperty("origin");
                            runs.Add(new TextRun(r.GetProperty("text").GetString() ?? "", box[0].GetDouble(), box[1].GetDouble(), box[2].GetDouble(), box[3].GetDouble(),
                                origin[0].GetDouble(), origin[1].GetDouble(), r.GetProperty("size").GetDouble(), r.GetProperty("color").GetInt32(),
                                r.GetProperty("font").GetString() ?? "", r.GetProperty("flags").GetInt32(), r.GetProperty("bg").GetInt32()));
                        }
                        int number = root.GetProperty("page").GetInt32();
                        answers.Add((new PageTextRuns(number, root.GetProperty("width").GetDouble(), root.GetProperty("height").GetDouble(), root.GetProperty("rotation").GetInt32(),
                            runs.Where(x => x.Text.Trim().Length > 0).ToList()), root.GetProperty("hasText").GetBoolean()));
                        progress?.Invoke(number);
                        break;
                    case "error": error = root.GetProperty("message").GetString(); break;
                }
            }).ConfigureAwait(false);
            if (answers.Count == 0 && error != null) throw new InvalidOperationException(error);
            return answers;
        }

        /// <summary>Removes the old characters of the edits from <paramref name="targetPath"/> itself (incremental update). The caller then writes the new text with <see cref="TextEditWriter"/>.</summary>
        public static async Task RemoveOldTextAsync(string targetPath, IReadOnlyList<TextEdit> edits)
        {
            if (edits.Count == 0) return;
            string? output = null;
            string? error = null;
            bool done = false;
            await RunAsync(new
            {
                mode = "apply", path = Path.GetFullPath(targetPath), inPlace = output == null, output = output == null ? null : Path.GetFullPath(output),
                edits = edits.Select(e => new
                {
                    page = e.PageNumber, bbox = new[] { e.Original.X0, e.Original.Y0, e.Original.X1, e.Original.Y1 }, origin = new[] { e.Original.OriginX, e.Original.OriginY },
                    size = e.Original.Size, color = e.Original.Color, flags = e.Original.Flags, text = e.NewText
                }).ToList()
            }, root =>
            {
                switch (root.GetProperty("type").GetString())
                {
                    case "done": done = true; break;
                    case "error": error = root.GetProperty("message").GetString(); break;
                }
            }).ConfigureAwait(false);
            if (!done) throw new InvalidOperationException(error ?? "The text could not be written.");
        }

        internal static async Task RunAsync(object job, Action<JsonElement> onMessage)
        {
            if (!IsAvailable) throw new InvalidOperationException("Text editing is not installed with this copy of the program (TextEditWorker.py or the embedded Python is missing).");
            string jobFile = Path.Combine(Path.GetTempPath(), "xtpdf-textedit-" + Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllTextAsync(jobFile, JsonSerializer.Serialize(job)).ConfigureAwait(false);
            var info = new ProcessStartInfo(OcrService.Python!)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            info.ArgumentList.Add("-u");
            info.ArgumentList.Add(WorkerScript);
            info.ArgumentList.Add(jobFile);
            if (OcrService.Packages is { } packages) info.Environment["PYTHONPATH"] = packages;
            info.Environment["PYTHONIOENCODING"] = "utf-8";
            try
            {
                using var process = Process.Start(info) ?? throw new InvalidOperationException("The text editing program did not start.");
                WorkerJob.Assign(process);
                var errors = process.StandardError.ReadToEndAsync();
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line) || !line.StartsWith('{')) continue;
                    using var message = JsonDocument.Parse(line);
                    onMessage(message.RootElement);
                }
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    string text = (await errors.ConfigureAwait(false)).Trim();
                    throw new InvalidOperationException("Text editing failed: " + (text.Length > 0 ? text.Split('\n').Last().Trim() : "exit code " + process.ExitCode));
                }
            }
            finally { try { File.Delete(jobFile); } catch { /* temp file */ } }
        }
    }
}
