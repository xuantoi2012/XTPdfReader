using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using iText.Kernel.Pdf;
using XTPdfMergeApp.Services.Ocr;
using XTPdfMergeApp.Services.TextEdit;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// For reading a file WITH its unsaved annotation edits (print, export, merge, extract, save of a window): the file itself when
    /// there are none, otherwise a temporary copy with the edits written in (kept while the edits do not change, deleted on exit).
    /// The copy gets the file's current layer view so it prints/exports the same layers.
    /// </summary>
    internal static class AnnotationWorkingCopy
    {
        private static readonly Dictionary<string, (long Version, string Copy)> _copies = new(StringComparer.OrdinalIgnoreCase);
        private static string Folder => Path.Combine(Path.GetTempPath(), "XTPdfReader", "working");

        /// <summary>Unsaved annotations or unsaved OCR text.</summary>
        public static bool HasPendingEdits(string path) => AnnotationStore.HasPending(path) || OcrPendingStore.HasPending(path) || TextEditPendingStore.HasPending(path);

        public static async Task<string> GetAsync(string path)
        {
            if (!HasPendingEdits(path)) return path;
            string key = AnnotationStore.Normalize(path);
            long version = (AnnotationStore.Version(path) * 100003L + OcrPendingStore.Version(path)) * 100003L + TextEditPendingStore.Version(path);
            if (_copies.TryGetValue(key, out var existing) && existing.Version == version && File.Exists(existing.Copy)) return existing.Copy;

            var changes = AnnotationStore.Pending(path);
            var ocr = OcrPendingStore.AsResults(path);
            var textEdits = TextEditPendingStore.All(path);
            Directory.CreateDirectory(Folder);
            string copy = Path.Combine(Folder, $"{Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid():N}.pdf");
            await Task.Run(() => Write(path, copy, changes, ocr));
            if (textEdits.Count > 0) // the copy shows the edited text to Find, print and export: old characters out, then the new text in
            {
                await TextEditService.RemoveOldTextAsync(copy, textEdits).ConfigureAwait(false);
                await Task.Run(() => PdfPageEditService.EditInPlace(copy, doc => TextEditWriter.ApplyTo(doc, textEdits))).ConfigureAwait(false);
            }
            if (PdfThumbnailService.TryGetDocumentPassword(path) is { Length: > 0 } password)
                await PdfThumbnailService.SetDocumentPasswordAsync(copy, password);
            if (_copies.TryGetValue(key, out var old)) TryDelete(old.Copy);
            _copies[key] = (version, copy);
            PdfLayerStateStore.CopyState(path, copy);
            return copy;
        }

        /// <summary>(file, page) list with every file replaced by its working copy.</summary>
        public static async Task<List<(string SourcePath, int PageNumber)>> MapAsync(IEnumerable<(string SourcePath, int PageNumber)> pages)
        {
            var list = pages.ToList();
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in list.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase))
                map[path] = await GetAsync(path);
            return list.Select(p => (map[p.SourcePath], p.PageNumber)).ToList();
        }

        /// <summary>Writes <paramref name="changes"/> into <paramref name="path"/> itself (Save of annotations only): incremental update.</summary>
        public static void WriteInPlace(string path, IReadOnlyList<QuickAnnotationChange> changes) => WriteInPlace(path, changes, out _);

        /// <summary>Như trên; <paramref name="conflicts"/> = chú thích mà người khác vừa sửa nội dung (bản của ta ghi đè; bản của họ còn trong lịch sử file). Cũng bắn <see cref="Conflict"/>.</summary>
        public static void WriteInPlace(string path, IReadOnlyList<QuickAnnotationChange> changes, out IReadOnlyList<string> conflicts)
        {
            var found = new List<string>();
            var ocr = OcrPendingStore.AsResults(path);
            var textEdits = TextEditPendingStore.All(path);
            PdfPageEditService.EditInPlace(path, Describe(changes, ocr.Count, textEdits.Count), doc =>
            {
                found.Clear(); // lần ghi có thể chạy lại trên bản mới của file (người khác lưu chen vào)
                PdfQuickAnnotationService.ApplyChanges(doc, changes, found);
                if (ocr.Count > 0) PdfOcrWriter.ApplyTo(doc, ocr);
            }, ocr.Count > 0 || textEdits.Count > 0 ? PdfPermissionOperation.Modify : PdfPermissionOperation.Annotate);
            // The edited text goes in as further incremental updates: the old characters are taken off the page, then the new text is written.
            if (textEdits.Count > 0)
            {
                TextEditService.RemoveOldTextAsync(path, textEdits).GetAwaiter().GetResult();
                PdfPageEditService.EditInPlace(path, doc => TextEditWriter.ApplyTo(doc, textEdits));
            }
            conflicts = found.ToList();
            if (found.Count > 0) Conflict?.Invoke(path, found.ToList());
        }

        /// <summary>Người khác đã đổi nội dung (đường dẫn file, tên các chú thích) trước khi ta lưu thay đổi của mình lên cùng chú thích.</summary>
        public static event Action<string, IReadOnlyList<string>>? Conflict;

        /// <summary>Dòng lịch sử cho 1 lần lưu annotation: "Annotations: +thêm −xoá ~sửa".</summary>
        private static string Describe(IReadOnlyList<QuickAnnotationChange> changes, int ocrPages = 0, int textEdits = 0)
        {
            int added = changes.Count(c => c.Remove == null && c.Add != null);
            int removed = changes.Count(c => c.Add == null && c.Remove != null);
            int edited = changes.Count - added - removed;
            var parts = new List<string>();
            if (added > 0) parts.Add("+" + added);
            if (removed > 0) parts.Add("−" + removed);
            if (edited > 0) parts.Add("~" + edited);
            var extra = new List<string>();
            if (parts.Count > 0) extra.Add("Annotations " + string.Join(" ", parts));
            if (ocrPages > 0) extra.Add($"OCR text on {ocrPages} page{(ocrPages == 1 ? "" : "s")}");
            if (textEdits > 0) extra.Add($"{textEdits} text edit{(textEdits == 1 ? "" : "s")}");
            if (ocrPages > 0 || textEdits > 0) return string.Join(", ", extra);
            return "Annotations " + string.Join(" ", parts);
        }

        private static void Write(string path, string copy, IReadOnlyList<QuickAnnotationChange> changes, IReadOnlyList<OcrPageResult> ocr)
        {
            using var reader = PdfSecurityService.AuthorizedReaderFor(path, PdfPermissionOperation.Annotate);
            using var writer = new PdfWriter(copy);
            using var doc = new PdfDocument(reader, writer, new StampingProperties().UseAppendMode());
            PdfQuickAnnotationService.ApplyChanges(doc, changes);
            if (ocr.Count > 0) PdfOcrWriter.ApplyTo(doc, ocr);
        }

        public static void Forget(string path)
        {
            if (_copies.Remove(AnnotationStore.Normalize(path), out var copy)) TryDelete(copy.Copy);
        }

        public static void DeleteAll()
        {
            foreach (var (_, copy) in _copies) TryDelete(copy.Copy);
            _copies.Clear();
            try
            {
                if (Directory.Exists(Folder))
                    foreach (string file in Directory.GetFiles(Folder, "*.pdf")) TryDelete(file);
            }
            catch { /* temp folder */ }
        }

        private static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { /* still open by a render: left for the next start */ }
        }
    }
}
