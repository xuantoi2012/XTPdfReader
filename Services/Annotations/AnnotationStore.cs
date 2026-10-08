using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace XTPdfMergeApp.Services
{
    /// <summary>Annotations of one page as shown: geometry + annotations in drawing order (file annotations with the unsaved edits applied).</summary>
    public sealed record PageAnnotations(PdfPageGeometry Geometry, IReadOnlyList<QuickAnnotationSpec> Annotations);

    /// <summary>
    /// Annotations kept in memory, like Foxit: what is in the file ("committed", read once per page) plus the unsaved edits
    /// ("pending", an ordered log). Every edit only changes memory and redraws at once; the file is written on Save
    /// (<see cref="PdfQuickAnnotationService.ApplyChanges"/> with <see cref="Pending"/>). Undo/Redo add or drop entries of the log.
    /// UI thread only (reads of the file run in the background and come back on the UI thread).
    /// </summary>
    internal static class AnnotationStore
    {
        private sealed class PageState
        {
            public PdfPageGeometry Geometry = new(0, 0, 612, 792, 0);
            public IReadOnlyList<QuickAnnotationSpec> Committed = Array.Empty<QuickAnnotationSpec>();
            public PageAnnotations? Effective;
            public bool Loaded;
            public Task<PageAnnotations?>? Loading;
        }

        private sealed class FileState(string path)
        {
            public readonly string Path = path;
            public readonly AnnotationSourceReader Reader = new(path);
            public readonly Dictionary<int, PageState> Pages = new();
            public readonly List<QuickAnnotationChange> Pending = new();
            /// <summary>Bumps when the file on disk is replaced — reads started before are dropped, file appearances re-rendered.</summary>
            public int Epoch;
            public int Version;
        }

        private static readonly Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Annotations of (normalized path, page) changed or their appearance is ready — redraw. Page 0 = every page of the file.</summary>
        public static event Action<string, int>? Changed;
        /// <summary>Unsaved edits of a file changed (dirty dot, Save).</summary>
        public static event Action<string>? PendingChanged;

        public static string Normalize(string path) => System.IO.Path.GetFullPath(path);

        private static FileState File(string path)
        {
            string key = Normalize(path);
            if (!_files.TryGetValue(key, out var file)) _files[key] = file = new FileState(key);
            return file;
        }

        internal static AnnotationSourceReader Reader(string path) => File(path).Reader;
        internal static int Epoch(string path) => File(path).Epoch;

        internal static void RaiseChanged(string path, int page) => Changed?.Invoke(Normalize(path), page);

        // ── Reading ───────────────────────────────────────────────────

        /// <summary>Annotations of the page as shown, or null while the page is being read (<see cref="Changed"/> fires when ready).</summary>
        public static PageAnnotations? TryGetPage(string path, int page)
        {
            var file = File(path);
            var state = PageOf(file, page);
            if (state.Loaded) return state.Effective ??= Compute(file, page, state);
            _ = Load(file, page, state);
            return null;
        }

        public static Task<PageAnnotations?> GetPageAsync(string path, int page)
        {
            var file = File(path);
            var state = PageOf(file, page);
            if (state.Loaded) return Task.FromResult<PageAnnotations?>(state.Effective ??= Compute(file, page, state));
            return Load(file, page, state);
        }

        /// <summary>Every annotation of the file as shown (Comments panel). The pages not read yet are read in one background pass.</summary>
        public static async Task<IReadOnlyList<QuickAnnotationSpec>> GetAllAsync(string path)
        {
            var file = File(path);
            int epoch = file.Epoch;
            var loaded = file.Pages.Where(p => p.Value.Loaded).Select(p => p.Key).ToHashSet();
            List<(int Page, PdfPageGeometry? Geometry, IReadOnlyList<QuickAnnotationSpec>? Annotations)> read;
            try
            {
                read = await Task.Run(() =>
                {
                    lock (file.Reader.Sync)
                    {
                        var doc = file.Reader.Document();
                        var list = new List<(int, PdfPageGeometry?, IReadOnlyList<QuickAnnotationSpec>?)>();
                        for (int page = 1; page <= doc.GetNumberOfPages(); page++)
                        {
                            if (loaded.Contains(page)) { list.Add((page, null, null)); continue; }
                            var pdfPage = doc.GetPage(page);
                            var geometry = PdfQuickAnnotationService.GetGeometry(pdfPage);
                            list.Add((page, geometry, PdfQuickAnnotationService.ReadAnnotations(pdfPage, geometry, page)));
                        }
                        return list;
                    }
                });
            }
            catch
            {
                return Array.Empty<QuickAnnotationSpec>();
            }
            if (!_files.TryGetValue(file.Path, out var current) || !ReferenceEquals(current, file) || file.Epoch != epoch)
                return Array.Empty<QuickAnnotationSpec>(); // replaced meanwhile: the caller refreshes on the change event

            var result = new List<QuickAnnotationSpec>();
            foreach (var (page, geometry, annotations) in read)
            {
                var state = PageOf(file, page);
                if (!state.Loaded && geometry is { } g && annotations != null)
                {
                    state.Geometry = g;
                    state.Committed = annotations;
                    state.Loaded = true;
                    state.Effective = null;
                }
                if (state.Loaded) result.AddRange((state.Effective ??= Compute(file, page, state)).Annotations);
            }
            return result;
        }

        private static PageState PageOf(FileState file, int page)
        {
            if (!file.Pages.TryGetValue(page, out var state)) file.Pages[page] = state = new PageState();
            return state;
        }

        private static Task<PageAnnotations?> Load(FileState file, int page, PageState state)
        {
            if (state.Loading != null) return state.Loading;
            int epoch = file.Epoch;
            var task = LoadCoreAsync(file, page, state, epoch);
            if (!task.IsCompleted) state.Loading = task;
            return task;
        }

        private static async Task<PageAnnotations?> LoadCoreAsync(FileState file, int page, PageState state, int epoch)
        {
            (PdfPageGeometry Geometry, IReadOnlyList<QuickAnnotationSpec> Annotations)? read = null;
            try
            {
                read = await Task.Run(() =>
                {
                    lock (file.Reader.Sync)
                    {
                        var doc = file.Reader.Document();
                        if (page < 1 || page > doc.GetNumberOfPages()) return ((PdfPageGeometry, IReadOnlyList<QuickAnnotationSpec>)?)null;
                        var pdfPage = doc.GetPage(page);
                        var geometry = PdfQuickAnnotationService.GetGeometry(pdfPage);
                        return (geometry, PdfQuickAnnotationService.ReadAnnotations(pdfPage, geometry, page));
                    }
                });
            }
            catch
            {
                // unreadable (file being replaced…): shown without annotations, read again next time
            }
            finally
            {
                state.Loading = null;
            }
            if (file.Epoch != epoch || !ReferenceEquals(PageOf(file, page), state)) return TryGetPage(file.Path, page);
            if (read is not { } value) return null;
            state.Geometry = value.Geometry;
            state.Committed = value.Annotations;
            state.Loaded = true;
            state.Effective = null;
            var effective = state.Effective = Compute(file, page, state);
            Changed?.Invoke(file.Path, page);
            return effective;
        }

        private static PageAnnotations Compute(FileState file, int page, PageState state)
        {
            var list = state.Committed.ToList();
            foreach (var change in file.Pending)
            {
                if (change.PageNumber != page) continue;
                int index = -1;
                if (change.Remove != null)
                {
                    index = list.FindIndex(a => a.Name == change.Remove.Name);
                    if (index >= 0) list.RemoveAt(index);
                }
                if (change.Add == null) continue;
                // An annotation of the file that is only moved keeps its place in /Annots. One rewritten from its spec (restyled, retyped, brought
                // to front) goes to the END, exactly where Save will put it, so the stacking on screen matches the saved file.
                if (index >= 0 && change.Add.ObjectNumber > 0) list.Insert(index, change.Add);
                else list.Add(change.Add);
            }
            return new PageAnnotations(state.Geometry, list);
        }

        /// <summary>A committed (file) annotation by object number — where its original appearance sits on the page.</summary>
        internal static QuickAnnotationSpec? Committed(string path, int page, int objectNumber, int generation)
        {
            var file = File(path);
            return file.Pages.TryGetValue(page, out var state)
                ? state.Committed.FirstOrDefault(a => a.ObjectNumber == objectNumber && a.Generation == generation)
                : null;
        }

        // ── Unsaved edits ─────────────────────────────────────────────

        public static bool HasPending(string path) => _files.TryGetValue(Normalize(path), out var file) && file.Pending.Count > 0;

        public static IReadOnlyList<QuickAnnotationChange> Pending(string path)
            => _files.TryGetValue(Normalize(path), out var file) ? file.Pending.ToList() : Array.Empty<QuickAnnotationChange>();

        /// <summary>Increases with every edit (cache key of a written copy with the edits).</summary>
        public static int Version(string path) => _files.TryGetValue(Normalize(path), out var file) ? file.Version : 0;

        public static void Apply(string path, IReadOnlyList<QuickAnnotationChange> changes)
        {
            if (changes.Count == 0) return;
            var file = File(path);
            file.Pending.AddRange(changes);
            Touched(file, changes);
        }

        /// <summary>Undo: drops exactly these entries of the log (Undo runs in reverse order, so they are the latest ones for their annotations).</summary>
        public static void Revert(string path, IReadOnlyList<QuickAnnotationChange> changes)
        {
            if (changes.Count == 0) return;
            var file = File(path);
            foreach (var change in changes.Reverse())
            {
                int index = file.Pending.LastIndexOf(change);
                if (index >= 0) file.Pending.RemoveAt(index);
            }
            Touched(file, changes);
        }

        private static void Touched(FileState file, IReadOnlyList<QuickAnnotationChange> changes)
        {
            file.Version++;
            foreach (int page in changes.Select(c => c.PageNumber).Distinct())
            {
                if (file.Pages.TryGetValue(page, out var state)) state.Effective = null;
                Changed?.Invoke(file.Path, page);
            }
            PendingChanged?.Invoke(file.Path);
        }

        /// <summary>The file on disk was replaced. <paramref name="keepPending"/> false (Save): the edits are now in the file.</summary>
        public static void FileRewritten(string path, bool keepPending)
        {
            if (!keepPending)
            {
                Ocr.OcrPendingStore.Clear(path); // the saved file has the OCR text
                TextEdit.TextEditPendingStore.Clear(path); // ...and the edited text
                TextEdit.ObjectDeletePendingStore.Clear(path); // ...and the objects are gone
                PageRotationPendingStore.Clear(path); // ...and the pages are turned
            }
            string key = Normalize(path);
            if (!_files.TryGetValue(key, out var file)) return;
            file.Reader.Close();
            file.Epoch++;
            file.Pages.Clear();
            bool hadPending = file.Pending.Count > 0;
            if (!keepPending) file.Pending.Clear();
            file.Version++;
            Changed?.Invoke(key, 0);
            if (hadPending && !keepPending) PendingChanged?.Invoke(key);
        }

        /// <summary>Close the reader before the file is written (it may hold the block cache / a handle).</summary>
        public static void ReleaseReader(string path)
        {
            if (_files.TryGetValue(Normalize(path), out var file)) file.Reader.Close();
        }

        /// <summary>The file is no longer open: forget everything (unsaved edits were already confirmed/discarded by the caller).</summary>
        public static void Forget(string path)
        {
            Ocr.OcrPendingStore.Clear(path);
            TextEdit.TextEditPendingStore.Clear(path);
            TextEdit.ObjectDeletePendingStore.Clear(path);
            PageRotationPendingStore.Clear(path);
            string key = Normalize(path);
            if (!_files.Remove(key, out var file)) return;
            file.Reader.Close();
            if (file.Pending.Count > 0) PendingChanged?.Invoke(key);
        }

        /// <summary>Pages were rotated in the file (/Rotate ± 90): unsaved edits on them are turned with the page, then the pages are read again.</summary>
        public static void PagesRotated(string path, IReadOnlyCollection<int> pages, int deltaDegrees)
        {
            var file = File(path);
            int steps = ((deltaDegrees / 90) % 4 + 4) % 4;
            if (steps != 0)
                for (int i = 0; i < file.Pending.Count; i++)
                {
                    var change = file.Pending[i];
                    if (!pages.Contains(change.PageNumber)) continue;
                    file.Pending[i] = new QuickAnnotationChange(Rotate(change.Remove, steps), Rotate(change.Add, steps));
                }
            file.Epoch++;
            foreach (int page in pages) file.Pages.Remove(page);
            file.Reader.Close();
            file.Version++;
            foreach (int page in pages) Changed?.Invoke(file.Path, page);
        }

        /// <summary>Display coordinates after the page turned <paramref name="steps"/> × 90° clockwise.</summary>
        private static QuickAnnotationSpec? Rotate(QuickAnnotationSpec? spec, int steps)
        {
            if (spec == null) return null;
            for (int s = 0; s < steps; s++)
            {
                string format = spec.Format;
                if (spec.Kind is QuickAnnotationKind.Highlight or QuickAnnotationKind.Underline or QuickAnnotationKind.StrikeOut or QuickAnnotationKind.Squiggly && format.StartsWith("T|", StringComparison.Ordinal))
                    format = PdfQuickAnnotationService.EncodeTextHighlight(PdfQuickAnnotationService.TextHighlightRects(format)
                        .Select(r => (1 - r.V2, r.U1, 1 - r.V1, r.U2)));
                else if (spec.Kind == QuickAnnotationKind.Ink && format.StartsWith("I|", StringComparison.Ordinal))
                    format = PdfQuickAnnotationService.EncodeInkPoints(PdfQuickAnnotationService.InkPoints(format).Select(p => (1 - p.V, p.U)));
                else if (spec.Kind == QuickAnnotationKind.Shape && ShapeStyle.Decode(format) is { IsLine: true } line)
                    format = (line with { Corner = line.Corner switch { 0 => 1, 1 => 3, 3 => 2, _ => 0 } }).Encode();
                spec = spec with { U1 = 1 - spec.V2, V1 = spec.U1, U2 = 1 - spec.V1, V2 = spec.U2, Format = format };
            }
            return spec;
        }
    }
}
