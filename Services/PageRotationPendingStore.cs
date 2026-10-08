using System;
using System.Collections.Generic;
using System.Linq;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// "Turn left / right" on pages of an open file, kept IN MEMORY until the user saves (Ctrl+S), like the unsaved annotations: the viewer and the thumbnails show the page turned, Undo turns it back, the file
    /// is not touched, and Save writes the new /Rotate (<see cref="Apply"/>). Prints, exports and merges read the working copy, which has the rotation.
    /// </summary>
    internal static class PageRotationPendingStore
    {
        private sealed class FileState
        {
            public readonly Dictionary<int, int> Pages = new(); // page -> degrees clockwise (90, 180, 270)
            public int Version;
        }

        private static readonly Dictionary<string, FileState> Files = new(StringComparer.OrdinalIgnoreCase);

        public static event Action<string>? PendingChanged;

        private static string Key(string path) => AnnotationStore.Normalize(path);

        public static bool HasPending(string path) => Files.TryGetValue(Key(path), out var file) && file.Pages.Count > 0;
        public static int Version(string path) => Files.TryGetValue(Key(path), out var file) ? file.Version : 0;

        /// <summary>How far the page is turned (degrees clockwise, 0 when it is not).</summary>
        public static int Delta(string path, int pageNumber)
            => Files.TryGetValue(Key(path), out var file) && file.Pages.TryGetValue(pageNumber, out int degrees) ? degrees : 0;

        public static IReadOnlyDictionary<int, int> All(string path)
            => Files.TryGetValue(Key(path), out var file) ? new Dictionary<int, int>(file.Pages) : new Dictionary<int, int>();

        /// <summary>Turns the pages further by <paramref name="degrees"/>. Returns what each page had, to undo it.</summary>
        public static IReadOnlyDictionary<int, int> Rotate(string path, IEnumerable<int> pages, int degrees)
        {
            string key = Key(path);
            if (!Files.TryGetValue(key, out var file)) Files[key] = file = new FileState();
            var before = new Dictionary<int, int>();
            foreach (int page in pages)
            {
                int old = file.Pages.TryGetValue(page, out int d) ? d : 0;
                before[page] = old;
                int now = (((old + degrees) % 360) + 360) % 360;
                if (now == 0) file.Pages.Remove(page); else file.Pages[page] = now;
            }
            Finish(key, file);
            return before;
        }

        /// <summary>Puts the turns back as they were (Undo).</summary>
        public static void Restore(string path, IReadOnlyDictionary<int, int> before)
        {
            string key = Key(path);
            if (!Files.TryGetValue(key, out var file)) Files[key] = file = new FileState();
            foreach (var (page, degrees) in before)
                if (degrees == 0) file.Pages.Remove(page); else file.Pages[page] = degrees;
            Finish(key, file);
        }

        private static void Finish(string key, FileState file)
        {
            file.Version++;
            if (file.Pages.Count == 0) Files.Remove(key);
            AnnotationWorkingCopy.Forget(key);
            PendingChanged?.Invoke(key);
        }

        /// <summary>The file was written or closed: nothing is pending any more.</summary>
        public static void Clear(string path)
        {
            string key = Key(path);
            if (!Files.Remove(key)) return;
            AnnotationWorkingCopy.Forget(key);
            PendingChanged?.Invoke(key);
        }

        /// <summary>Writes the turns into the pages' /Rotate.</summary>
        public static void Apply(iText.Kernel.Pdf.PdfDocument doc, IReadOnlyDictionary<int, int> turns)
        {
            foreach (var (number, degrees) in turns)
            {
                if (number < 1 || number > doc.GetNumberOfPages()) continue;
                var page = doc.GetPage(number);
                page.SetRotation(PdfPageEditService.NormalizeRotation(page.GetRotation() + degrees));
            }
        }
    }
}
