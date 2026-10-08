using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace XTPdfMergeApp.Services.TextEdit
{
    /// <summary>A piece of text on a page (same font, size and colour on one line), in points of the page as displayed, origin top left.</summary>
    internal sealed record TextRun(string Text, double X0, double Y0, double X1, double Y1, double OriginX, double OriginY, double Size, int Color, string Font, int Flags, int Background)
    {
        public bool Contains(double x, double y) => x >= X0 - 1 && x <= X1 + 1 && y >= Y0 - 1 && y <= Y1 + 1;
        /// <summary>The same place on the page (an edited run keeps the place of the run it came from).</summary>
        public bool SamePlace(TextRun other) => Math.Abs(X0 - other.X0) < 0.5 && Math.Abs(Y0 - other.Y0) < 0.5 && Math.Abs(OriginY - other.OriginY) < 0.5;
    }

    /// <summary>The runs of a page and its size in points.</summary>
    internal sealed record PageTextRuns(int PageNumber, double Width, double Height, int Rotation, IReadOnlyList<TextRun> Runs);

    /// <summary>One replaced run: <see cref="Original"/> is what the file has, <see cref="NewText"/> what it will have.</summary>
    internal sealed record TextEdit(int PageNumber, double PageWidth, double PageHeight, TextRun Original, string NewText);

    /// <summary>
    /// Text edits kept IN MEMORY until the user saves (Ctrl+S), like <see cref="Ocr.OcrPendingStore"/>: the tab shows the unsaved mark, Undo takes the edit back, the viewer and thumbnails
    /// draw the new text over the old (<see cref="Controls.TextEditLayer"/>), and Save removes the old characters from the page for real (<see cref="TextEditService"/>).
    /// </summary>
    internal static class TextEditPendingStore
    {
        private sealed class FileState
        {
            public readonly Dictionary<int, List<TextEdit>> Pages = new();
            public int Version;
        }

        private static readonly Dictionary<string, FileState> Files = new(StringComparer.OrdinalIgnoreCase);

        public static event Action<string>? PendingChanged;

        private static string Key(string path) => AnnotationStore.Normalize(path);

        public static bool HasPending(string path) => Files.TryGetValue(Key(path), out var file) && file.Pages.Count > 0;
        public static int Version(string path) => Files.TryGetValue(Key(path), out var file) ? file.Version : 0;

        public static IReadOnlyList<TextEdit> Page(string path, int pageNumber)
            => Files.TryGetValue(Key(path), out var file) && file.Pages.TryGetValue(pageNumber, out var list) ? list.ToList() : Array.Empty<TextEdit>();

        public static IReadOnlyList<TextEdit> All(string path)
            => Files.TryGetValue(Key(path), out var file) ? file.Pages.OrderBy(p => p.Key).SelectMany(p => p.Value).ToList() : Array.Empty<TextEdit>();

        /// <summary>Makes the edit of the run's place the one given (null = take the edit away). Returns what was there, to undo it.</summary>
        public static TextEdit? Set(string path, int pageNumber, TextRun place, TextEdit? edit)
        {
            string key = Key(path);
            if (!Files.TryGetValue(key, out var file)) Files[key] = file = new FileState();
            if (!file.Pages.TryGetValue(pageNumber, out var list)) file.Pages[pageNumber] = list = new List<TextEdit>();
            int index = list.FindIndex(e => e.Original.SamePlace(place));
            var before = index >= 0 ? list[index] : null;
            if (index >= 0) list.RemoveAt(index);
            if (edit != null) list.Add(edit);
            if (list.Count == 0) file.Pages.Remove(pageNumber);
            file.Version++;
            if (file.Pages.Count == 0) Files.Remove(key);
            Raise(key);
            return before;
        }

        /// <summary>The file was written or closed: nothing is pending any more.</summary>
        public static void Clear(string path)
        {
            string key = Key(path);
            if (!Files.Remove(key)) return;
            Raise(key);
        }

        private static void Raise(string key)
        {
            AnnotationWorkingCopy.Forget(key);
            PdfThumbnailService.NotifyTextChanged();
            PendingChanged?.Invoke(key);
        }
    }
}
