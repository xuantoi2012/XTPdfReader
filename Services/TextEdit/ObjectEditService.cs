using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace XTPdfMergeApp.Services.TextEdit
{
    /// <summary>A drawn thing on a page: a line, a shape (any other vector path) or an image. Box in points of the displayed page, origin top left.</summary>
    internal sealed record PdfObjectRef(int PageNumber, string Kind, int Index, double X0, double Y0, double X1, double Y1, double PageWidth, double PageHeight)
    {
        public bool IsImage => Kind == "image";
    }

    /// <summary>
    /// Edit Object: finds the lines, shapes and images of a page (<c>TextEditWorker.py</c>, PyMuPDF drawings and image info) and removes them from the file. Used on drawings
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
                        double w = root.GetProperty("width").GetDouble(), h = root.GetProperty("height").GetDouble();
                        if (root.GetProperty("rotation").GetInt32() != 0) { error = "rotated"; break; }
                        foreach (var o in root.GetProperty("objects").EnumerateArray())
                        {
                            var box = o.GetProperty("bbox");
                            result.Add(new PdfObjectRef(pageNumber, o.GetProperty("kind").GetString() ?? "shape", o.GetProperty("index").GetInt32(),
                                box[0].GetDouble(), box[1].GetDouble(), box[2].GetDouble(), box[3].GetDouble(), w, h));
                        }
                        break;
                    case "error": error = root.GetProperty("message").GetString(); break;
                }
            }).ConfigureAwait(false);
            if (error == "rotated") throw new NotSupportedException("Object editing works on upright pages for now. Rotate the page back (Turn left / right), edit, then rotate again.");
            if (!answered) throw new InvalidOperationException(error ?? "The page could not be read.");
            return result;
        }

        /// <summary>Removes the objects from <paramref name="path"/> itself (an incremental update appended to the file).</summary>
        public static async Task DeleteAsync(string path, IReadOnlyList<PdfObjectRef> objects)
        {
            if (objects.Count == 0) return;
            string? error = null;
            bool done = false;
            await TextEditService.RunAsync(new
            {
                mode = "delete", path = Path.GetFullPath(path),
                objects = objects.Select(o => new { page = o.PageNumber, kind = o.Kind, index = o.Index, bbox = new[] { o.X0, o.Y0, o.X1, o.Y1 } }).ToList()
            }, root =>
            {
                switch (root.GetProperty("type").GetString())
                {
                    case "done": done = true; break;
                    case "error": error = root.GetProperty("message").GetString(); break;
                }
            }).ConfigureAwait(false);
            if (!done) throw new InvalidOperationException(error ?? "The objects could not be removed.");
        }

        /// <summary>The bytes appended to the end of a file after it had <paramref name="oldLength"/> bytes (what an incremental update added).</summary>
        public static byte[] ReadTail(string path, long oldLength)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(oldLength, SeekOrigin.Begin);
            var tail = new byte[Math.Max(0, stream.Length - oldLength)];
            int read = 0;
            while (read < tail.Length) { int n = stream.Read(tail, read, tail.Length - read); if (n <= 0) break; read += n; }
            return tail;
        }

        /// <summary>Takes an appended update off the end of the file (Undo of a removal).</summary>
        public static void Truncate(string path, long length)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            stream.SetLength(length);
            stream.Flush(true);
        }

        /// <summary>Puts the update back at the end of the file (Redo of a removal).</summary>
        public static void Append(string path, byte[] tail)
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(tail, 0, tail.Length);
            stream.Flush(true);
        }
    }
}
