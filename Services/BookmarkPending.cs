using System;
using System.Collections.Generic;
using System.Linq;
using iText.Kernel.Pdf;

namespace XTPdfMergeApp.Services
{
    /// <summary>One change of the bookmark tree that waits for Save: how it changes the tree the panel shows, and how it is written into the file.</summary>
    internal sealed record BookmarkEdit(string Description, Func<IReadOnlyList<PdfBookmarkNode>, IReadOnlyList<PdfBookmarkNode>> OnTree, Action<PdfDocument> Write);

    /// <summary>The same four changes the file gets (<see cref="PdfOutlineService"/>), done on the tree as the panel shows it. A tree node's path is its index among its siblings at every level.</summary>
    internal static class BookmarkModel
    {
        public static IReadOnlyList<PdfBookmarkNode> Add(IReadOnlyList<PdfBookmarkNode> tree, IReadOnlyList<int> parentPath, string title, int pageNumber)
            => Reindex(Edit(tree, parentPath, children => children.Append(new PdfBookmarkNode(title, pageNumber, Array.Empty<PdfBookmarkNode>(), Array.Empty<int>())).ToList()));

        public static IReadOnlyList<PdfBookmarkNode> Rename(IReadOnlyList<PdfBookmarkNode> tree, IReadOnlyList<int> nodePath, string title)
            => Reindex(EditNode(tree, nodePath, node => node with { Title = title }));

        public static IReadOnlyList<PdfBookmarkNode> Delete(IReadOnlyList<PdfBookmarkNode> tree, IReadOnlyList<int> nodePath)
        {
            if (nodePath.Count == 0) return tree;
            int index = nodePath[^1];
            return Reindex(Edit(tree, nodePath.Take(nodePath.Count - 1).ToList(), children => { var list = children.ToList(); if (index >= 0 && index < list.Count) list.RemoveAt(index); return list; }));
        }

        public static IReadOnlyList<PdfBookmarkNode> Move(IReadOnlyList<PdfBookmarkNode> tree, IReadOnlyList<int> nodePath, int delta)
        {
            if (nodePath.Count == 0 || delta == 0) return tree;
            int index = nodePath[^1];
            return Reindex(Edit(tree, nodePath.Take(nodePath.Count - 1).ToList(), children =>
            {
                var list = children.ToList();
                if (index < 0 || index >= list.Count) return list;
                int target = Math.Clamp(index + delta, 0, list.Count - 1);
                var item = list[index];
                list.RemoveAt(index);
                list.Insert(target, item);
                return list;
            }));
        }

        /// <summary>Changes the list of children at <paramref name="path"/> (empty = the top level).</summary>
        private static IReadOnlyList<PdfBookmarkNode> Edit(IReadOnlyList<PdfBookmarkNode> tree, IReadOnlyList<int> path, Func<IReadOnlyList<PdfBookmarkNode>, IReadOnlyList<PdfBookmarkNode>> change, int depth = 0)
        {
            if (depth == path.Count) return change(tree);
            int index = path[depth];
            if (index < 0 || index >= tree.Count) return tree;
            var list = tree.ToList();
            list[index] = list[index] with { Children = Edit(list[index].Children, path, change, depth + 1) };
            return list;
        }

        private static IReadOnlyList<PdfBookmarkNode> EditNode(IReadOnlyList<PdfBookmarkNode> tree, IReadOnlyList<int> path, Func<PdfBookmarkNode, PdfBookmarkNode> change)
        {
            if (path.Count == 0) return tree;
            return Edit(tree, path.Take(path.Count - 1).ToList(), children =>
            {
                int index = path[^1];
                if (index < 0 || index >= children.Count) return children;
                var list = children.ToList();
                list[index] = change(list[index]);
                return list;
            });
        }

        /// <summary>The paths of every node again, after nodes came, went or moved.</summary>
        public static IReadOnlyList<PdfBookmarkNode> Reindex(IReadOnlyList<PdfBookmarkNode> tree, IReadOnlyList<int>? prefix = null)
        {
            prefix ??= Array.Empty<int>();
            var result = new List<PdfBookmarkNode>(tree.Count);
            for (int i = 0; i < tree.Count; i++)
            {
                var path = prefix.Append(i).ToList();
                result.Add(tree[i] with { Path = path, Children = Reindex(tree[i].Children, path) });
            }
            return result;
        }
    }

    /// <summary>
    /// Bookmark edits (add, rename, delete, move) kept IN MEMORY until the user saves (Ctrl+S): the Bookmarks panel shows the changed tree at once, Undo takes the last one back, the tab shows the unsaved
    /// mark, and Save writes them into the file in the order they were made (<see cref="Apply"/>).
    /// </summary>
    internal static class BookmarkPendingStore
    {
        private sealed class FileState
        {
            public readonly List<BookmarkEdit> Edits = new();
            public int Version;
        }

        private static readonly Dictionary<string, FileState> Files = new(StringComparer.OrdinalIgnoreCase);

        public static event Action<string>? PendingChanged;

        private static string Key(string path) => AnnotationStore.Normalize(path);

        public static bool HasPending(string path) => Files.TryGetValue(Key(path), out var file) && file.Edits.Count > 0;
        public static int Version(string path) => Files.TryGetValue(Key(path), out var file) ? file.Version : 0;
        public static int Count(string path) => Files.TryGetValue(Key(path), out var file) ? file.Edits.Count : 0;

        public static void Add(string path, BookmarkEdit edit)
        {
            string key = Key(path);
            if (!Files.TryGetValue(key, out var file)) Files[key] = file = new FileState();
            file.Edits.Add(edit);
            Finish(key, file);
        }

        public static void Remove(string path, BookmarkEdit edit)
        {
            string key = Key(path);
            if (!Files.TryGetValue(key, out var file)) return;
            file.Edits.Remove(edit);
            Finish(key, file);
        }

        /// <summary>The tree as the panel shows it: the file's own tree with every pending edit done on it.</summary>
        public static IReadOnlyList<PdfBookmarkNode> Replay(string path, IReadOnlyList<PdfBookmarkNode> fromFile)
        {
            if (!Files.TryGetValue(Key(path), out var file)) return fromFile;
            var tree = fromFile;
            foreach (var edit in file.Edits.ToList()) tree = edit.OnTree(tree);
            return tree;
        }

        /// <summary>Writes every pending edit into the document, in order.</summary>
        public static void Apply(PdfDocument doc, IReadOnlyList<BookmarkEdit> edits)
        {
            foreach (var edit in edits) edit.Write(doc);
        }

        public static IReadOnlyList<BookmarkEdit> All(string path)
            => Files.TryGetValue(Key(path), out var file) ? file.Edits.ToList() : Array.Empty<BookmarkEdit>();

        private static void Finish(string key, FileState file)
        {
            file.Version++;
            if (file.Edits.Count == 0) Files.Remove(key);
            AnnotationWorkingCopy.Forget(key);
            PendingChanged?.Invoke(key);
        }

        public static void Clear(string path)
        {
            string key = Key(path);
            if (!Files.Remove(key)) return;
            AnnotationWorkingCopy.Forget(key);
            PendingChanged?.Invoke(key);
        }
    }
}
