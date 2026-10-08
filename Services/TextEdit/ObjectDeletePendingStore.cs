using System;
using System.Collections.Generic;
using System.Linq;

namespace XTPdfMergeApp.Services.TextEdit
{
    /// <summary>
    /// Lines, shapes and images chosen for removal (Edit Object), kept IN MEMORY until the user saves, like <see cref="TextEditPendingStore"/> and the unsaved annotations: the tab shows the unsaved
    /// mark, Undo brings them back, the page shows them gone at once (<see cref="Controls.ObjectDeleteLayer"/>), and Save removes them from the file for real (<see cref="ObjectEditService.DeleteAsync"/>).
    /// </summary>
    internal static class ObjectDeletePendingStore
    {
        private sealed class FileState
        {
            public readonly Dictionary<int, List<PdfObjectRef>> Pages = new();
            public int Version;
        }

        private static readonly Dictionary<string, FileState> Files = new(StringComparer.OrdinalIgnoreCase);

        public static event Action<string>? PendingChanged;

        private static string Key(string path) => AnnotationStore.Normalize(path);

        public static bool HasPending(string path) => Files.TryGetValue(Key(path), out var file) && file.Pages.Count > 0;
        public static int Version(string path) => Files.TryGetValue(Key(path), out var file) ? file.Version : 0;
        public static int Count(string path) => Files.TryGetValue(Key(path), out var file) ? file.Pages.Values.Sum(l => l.Count) : 0;

        public static IReadOnlyList<PdfObjectRef> Page(string path, int pageNumber)
            => Files.TryGetValue(Key(path), out var file) && file.Pages.TryGetValue(pageNumber, out var list) ? list.ToList() : Array.Empty<PdfObjectRef>();

        public static IReadOnlyList<PdfObjectRef> All(string path)
            => Files.TryGetValue(Key(path), out var file) ? file.Pages.OrderBy(p => p.Key).SelectMany(p => p.Value).ToList() : Array.Empty<PdfObjectRef>();

        /// <summary>Marks the objects for removal. Returns the ones that were new (to take back on Undo).</summary>
        public static IReadOnlyList<PdfObjectRef> Add(string path, IEnumerable<PdfObjectRef> objects)
        {
            string key = Key(path);
            if (!Files.TryGetValue(key, out var file)) Files[key] = file = new FileState();
            var added = new List<PdfObjectRef>();
            foreach (var o in objects)
            {
                if (!file.Pages.TryGetValue(o.PageNumber, out var list)) file.Pages[o.PageNumber] = list = new List<PdfObjectRef>();
                if (list.Any(x => x.SameObject(o))) continue;
                list.Add(o);
                added.Add(o);
            }
            file.Version++;
            Raise(key);
            return added;
        }

        /// <summary>Takes the objects off the list (Undo).</summary>
        public static void Remove(string path, IEnumerable<PdfObjectRef> objects)
        {
            string key = Key(path);
            if (!Files.TryGetValue(key, out var file)) return;
            foreach (var o in objects)
                if (file.Pages.TryGetValue(o.PageNumber, out var list))
                {
                    list.RemoveAll(x => x.SameObject(o));
                    if (list.Count == 0) file.Pages.Remove(o.PageNumber);
                }
            file.Version++;
            if (file.Pages.Count == 0) Files.Remove(key);
            Raise(key);
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
            PendingChanged?.Invoke(key);
        }
    }

    /// <summary>What a long job is doing, for the status bar of the reader (a find over many pages, a save that removes objects): text and how far it is. Nothing here knows the window.</summary>
    internal static class TaskProgress
    {
        /// <summary>(text, fraction 0..1 or -1 when it cannot say). A null text means the job is over.</summary>
        public static event Action<string?, double>? Changed;

        public static void Report(string text, int done, int total) => Changed?.Invoke(text, total > 0 ? Math.Clamp((double)done / total, 0, 1) : -1);
        public static void Start(string text) => Changed?.Invoke(text, -1);
        public static void End() => Changed?.Invoke(null, 0);
    }
}
