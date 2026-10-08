using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace XTPdfMergeApp.Services.TextEdit
{
    /// <summary>
    /// A drawn thing on a page: a line, a shape (any other vector path) or an image. Box in points of the displayed page, origin top left. <see cref="Paths"/> are its strokes (curves flattened)
    /// in the same points, <see cref="Width"/> the stroke width: they let the page show it gone (white strokes over it) while it waits to be removed from the file on Save.
    /// </summary>
    internal sealed record PdfObjectRef(int PageNumber, string Kind, int Index, double X0, double Y0, double X1, double Y1, double PageWidth, double PageHeight)
    {
        public bool IsImage => Kind == "image";
        public double Width { get; init; }
        public IReadOnlyList<IReadOnlyList<(double X, double Y)>> Paths { get; init; } = Array.Empty<IReadOnlyList<(double, double)>>();
        public bool SameObject(PdfObjectRef other) => PageNumber == other.PageNumber && Kind == other.Kind && Index == other.Index;
    }

    /// <summary>
    /// Edit Object: finds the lines, shapes and images of a page (<c>TextEditWorker.py</c>, PyMuPDF drawings and image info) and, on Save, removes them from the file. Used on drawings
    /// whose letters are strokes and on scans, where there is no text to edit.
    /// </summary>
    internal static class ObjectEditService
    {
        /// <summary>The objects under a point (smallest first). <paramref name="x"/>, <paramref name="y"/> in points of the displayed page.</summary>
        public static async Task<IReadOnlyList<PdfObjectRef>> PickAsync(string path, int pageNumber, double x, double y, double tolerance = 2.5)
            => await QueryAsync(new { mode = "pick", path = Path.GetFullPath(path), page = pageNumber, x, y, tol = tolerance }, pageNumber);

        /// <summary>The objects that lie completely inside a rectangle (points of the displayed page).</summary>
        public static async Task<IReadOnlyList<PdfObjectRef>> PickAreaAsync(string path, int pageNumber, double x0, double y0, double x1, double y1)
            => await QueryAsync(new { mode = "pickArea", path = Path.GetFullPath(path), page = pageNumber, rect = new[] { x0, y0, x1, y1 } }, pageNumber);

        private static IEnumerable<PdfObjectRef> Parse(JsonElement root, int number)
        {
            double w = root.GetProperty("width").GetDouble(), h = root.GetProperty("height").GetDouble();
            foreach (var o in root.GetProperty("objects").EnumerateArray())
            {
                var box = o.GetProperty("bbox");
                var paths = new List<IReadOnlyList<(double X, double Y)>>();
                if (o.TryGetProperty("paths", out var lines))
                    foreach (var line in lines.EnumerateArray())
                        paths.Add(line.EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble())).ToList());
                yield return new PdfObjectRef(number, o.GetProperty("kind").GetString() ?? "shape", o.GetProperty("index").GetInt32(),
                    box[0].GetDouble(), box[1].GetDouble(), box[2].GetDouble(), box[3].GetDouble(), w, h)
                { Width = o.TryGetProperty("width", out var width) ? width.GetDouble() : 0, Paths = paths };
            }
        }

        /// <summary>The objects whose centre is in a rectangle (fractions of the page) on each of the pages, in one worker call that reports each page as it is done.
        /// <paramref name="progress"/> gets (pages done, pages asked).</summary>
        public static async Task<IReadOnlyList<PdfObjectRef>> FindInAreaAsync(string path, IReadOnlyList<int> pages, double u1, double v1, double u2, double v2, bool touch, List<int>? rotated = null,
            IProgress<(int Done, int Total)>? progress = null)
        {
            var result = new List<PdfObjectRef>();
            string? error = null;
            bool answered = false;
            int done = 0;
            await TextEditService.RunAsync(new { mode = "areaObjects", path = Path.GetFullPath(path), pages, rect = new[] { u1, v1, u2, v2 }, touch }, root =>
            {
                switch (root.GetProperty("type").GetString())
                {
                    case "areaObjects":
                        answered = true;
                        result.AddRange(Parse(root, root.GetProperty("page").GetInt32()));
                        progress?.Report((++done, pages.Count));
                        break;
                    case "error": error = root.GetProperty("message").GetString(); break;
                }
            }).ConfigureAwait(false);
            if (!answered) throw new InvalidOperationException(error ?? "The pages could not be read.");
            return result;
        }

        private static async Task<IReadOnlyList<PdfObjectRef>> QueryAsync(object job, int pageNumber)
        {
            var result = new List<PdfObjectRef>();
            string? error = null;
            bool answered = false;
            await TextEditService.RunAsync(job, root =>
            {
                switch (root.GetProperty("type").GetString())
                {
                    case "pick":
                        answered = true;
                        result.AddRange(Parse(root, pageNumber));
                        break;
                    case "error": error = root.GetProperty("message").GetString(); break;
                }
            }).ConfigureAwait(false);
            if (!answered) throw new InvalidOperationException(error ?? "The page could not be read.");
            return result;
        }

        /// <summary>Removes the objects from <paramref name="path"/> itself (an incremental update appended to the file). <paramref name="progress"/> gets (pages done, pages to do).</summary>
        public static async Task DeleteAsync(string path, IReadOnlyList<PdfObjectRef> objects, IProgress<(int Done, int Total)>? progress = null)
        {
            if (objects.Count == 0) return;
            string? error = null;
            bool done = false;
            await TextEditService.RunAsync(new
            {
                mode = "delete", path = Path.GetFullPath(path),
                objects = objects.Select(o => new { page = o.PageNumber, kind = o.Kind, index = o.Index }).ToList()
            }, root =>
            {
                switch (root.GetProperty("type").GetString())
                {
                    case "progress": progress?.Report((root.GetProperty("done").GetInt32(), root.GetProperty("total").GetInt32())); break;
                    case "done": done = true; break;
                    case "error": error = root.GetProperty("message").GetString(); break;
                }
            }).ConfigureAwait(false);
            if (!done) throw new InvalidOperationException(error ?? "The objects could not be removed.");
        }
    }
}
