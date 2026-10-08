using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace XTPdfMergeApp.Services.Ocr
{
    /// <summary>
    /// The text OCR read from scanned pages of an open file, kept IN MEMORY until the user saves (Ctrl+S), like the unsaved annotations in
    /// <see cref="AnnotationStore"/>: the tab shows the unsaved mark, Undo takes the text away, and the file is not touched. Save writes it into the file as an
    /// invisible text layer (<see cref="PdfOcrWriter.ApplyTo"/>). Until then Find, Select and the sheet-info reader look at a temporary working copy that has the text
    /// (<see cref="TextPathAsync"/>).
    /// </summary>
    internal static class OcrPendingStore
    {
        private sealed class FileState
        {
            public readonly Dictionary<int, IReadOnlyList<OcrWord>> Pages = new();
            public int Version;
        }

        private static readonly Dictionary<string, FileState> Files = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The file's pending text changed (raised with the normalised path): tabs refresh their unsaved mark, searches start again.</summary>
        public static event Action<string>? PendingChanged;

        private static string Key(string path) => AnnotationStore.Normalize(path);

        public static bool HasPending(string path) => Files.TryGetValue(Key(path), out var file) && file.Pages.Count > 0;

        public static bool HasPage(string path, int pageNumber) => Files.TryGetValue(Key(path), out var file) && file.Pages.ContainsKey(pageNumber);

        /// <summary>Changes with every edit of the file's pending text (a working copy is valid for one version).</summary>
        public static int Version(string path) => Files.TryGetValue(Key(path), out var file) ? file.Version : 0;

        /// <summary>The pending words by page number (1 based).</summary>
        public static IReadOnlyDictionary<int, IReadOnlyList<OcrWord>> Pending(string path)
            => Files.TryGetValue(Key(path), out var file) ? new Dictionary<int, IReadOnlyList<OcrWord>>(file.Pages) : new Dictionary<int, IReadOnlyList<OcrWord>>();

        /// <summary>The pending pages as OCR results (what the writer takes).</summary>
        public static IReadOnlyList<OcrPageResult> AsResults(string path)
            => Pending(path).Select(p => new OcrPageResult(p.Key - 1, false, p.Value)).OrderBy(r => r.PageIndex).ToList();

        /// <summary>Sets the words of pages (a null list removes a page's pending text). Returns what was there before, to undo it.</summary>
        public static IReadOnlyDictionary<int, IReadOnlyList<OcrWord>?> Set(string path, IReadOnlyDictionary<int, IReadOnlyList<OcrWord>?> pages)
        {
            string key = Key(path);
            if (!Files.TryGetValue(key, out var file)) Files[key] = file = new FileState();
            var before = new Dictionary<int, IReadOnlyList<OcrWord>?>();
            foreach (var (page, words) in pages)
            {
                before[page] = file.Pages.TryGetValue(page, out var old) ? old : null;
                if (words == null) file.Pages.Remove(page); else file.Pages[page] = words;
            }
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
            AnnotationWorkingCopy.Forget(key); // the copy with the old text is out of date
            PdfThumbnailService.NotifyTextChanged();
            PendingChanged?.Invoke(key);
        }

        /// <summary>The file to read text from: itself, or a working copy that has the pending OCR text (so Find, Select and the sheet-info reader see it).</summary>
        public static async Task<string> TextPathAsync(string path)
            => HasPending(path) || TextEdit.TextEditPendingStore.HasPending(path) ? await AnnotationWorkingCopy.GetAsync(path).ConfigureAwait(false) : path;
    }
}
