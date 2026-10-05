using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using iText.IO.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Navigation;

namespace XTPdfMergeApp.Services
{
    /// <summary>1 bookmark. <see cref="PageNumber"/> 1-based trong file nguồn; null nếu mục không trỏ tới
    /// trang nào trong file (link web, đích hỏng…) — vẫn hiện nhưng bấm không nhảy.
    /// <see cref="Path"/> = chỉ số con tại mỗi cấp tính từ gốc (rỗng = mục gốc); dùng để định vị lại đúng
    /// mục này trong cây iText khi sửa/xoá — thứ tự khớp <see cref="PdfOutline.GetAllChildren"/> nên chỉ
    /// hợp lệ trong cùng 1 lần đọc, không giữ qua lần đọc lại (file có thể đã đổi).</summary>
    public sealed record PdfBookmarkNode(string Title, int? PageNumber, IReadOnlyList<PdfBookmarkNode> Children, IReadOnlyList<int> Path);

    /// <summary>Đọc/sửa cây outline (bookmark) bằng iText. Sửa dùng chung <see cref="PdfPageEditService.EditInPlace"/>
    /// (ghi kiểu incremental, không đụng byte cũ) — caller phải tự lo suspend PDFium trước khi gọi.</summary>
    public static class PdfOutlineService
    {
        public static IReadOnlyList<PdfBookmarkNode> ReadBookmarks(string path)
        {
            var properties = new ReaderProperties();
            if (PdfThumbnailService.TryGetDocumentPassword(path) is { Length: > 0 } password)
                properties.SetPassword(Encoding.UTF8.GetBytes(password));
            using var doc = new PdfDocument(new PdfReader(path, properties));
            return ReadBookmarks(doc);
        }

        public static IReadOnlyList<PdfBookmarkNode> ReadBookmarks(PdfDocument doc)
        {
            if (doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Outlines) == null)
                return Array.Empty<PdfBookmarkNode>();
            PdfOutline? root;
            try { root = doc.GetOutlines(false); }
            catch { return Array.Empty<PdfBookmarkNode>(); } // outline hỏng: coi như không có bookmark
            if (root == null) return Array.Empty<PdfBookmarkNode>();
            var names = doc.GetCatalog().GetNameTree(PdfName.Dests);
            return Convert(root.GetAllChildren(), doc, names, depth: 0, prefix: Array.Empty<int>());
        }

        private static List<PdfBookmarkNode> Convert(IList<PdfOutline> outlines, PdfDocument doc, PdfNameTree names, int depth, IReadOnlyList<int> prefix)
        {
            var result = new List<PdfBookmarkNode>();
            if (depth > 64) return result; // outline vòng lặp (file hỏng)
            for (int i = 0; i < outlines.Count; i++)
            {
                var outline = outlines[i];
                var path = prefix.Append(i).ToList();
                result.Add(new PdfBookmarkNode(
                    outline.GetTitle() ?? "",
                    ResolvePage(outline, doc, names),
                    Convert(outline.GetAllChildren(), doc, names, depth + 1, path),
                    path));
            }
            return result;
        }

        private static int? ResolvePage(PdfOutline outline, PdfDocument doc, PdfNameTree names)
        {
            try
            {
                // Đích nằm ở /Dest, hoặc trong hành động /A << /S /GoTo /D … >>.
                PdfObject? target = outline.GetContent().Get(PdfName.Dest);
                if (target == null && outline.GetContent().GetAsDictionary(PdfName.A) is { } action &&
                    PdfName.GoTo.Equals(action.GetAsName(PdfName.S)))
                    target = action.Get(PdfName.D);
                if (target == null) return null;

                var destination = PdfDestination.MakeDestination(target);
                if (destination.GetDestinationPage(names) is PdfDictionary page)
                {
                    int number = doc.GetPageNumber(page);
                    return number > 0 ? number : null;
                }
                // Đích dạng [số_trang /XYZ …] (tham chiếu trang bằng số — thường gặp ở đích "remote").
                if (destination.GetDestinationPage(names) is PdfNumber index)
                    return index.IntValue() + 1;
                return null;
            }
            catch
            {
                return null;
            }
        }

        // ── Sửa (Add / Rename / Delete) ─────────────────────────────────────

        /// <summary>Thêm 1 bookmark mới, trỏ tới <paramref name="pageNumber"/> (1-based), làm con cuối cùng của
        /// mục tại <paramref name="parentPath"/> (rỗng = thêm ở gốc, tức mục cấp 1 mới).
        /// Dựng thẳng /Outlines bằng PdfDictionary thô (không qua <see cref="PdfOutline.AddOutline(string)"/>):
        /// API cấp cao đó không đánh dấu đúng các object cũ cần ghi lại ở chế độ append — file vẫn được nối
        /// thêm byte (đối tượng mới có mặt trong file) nhưng /First,/Last của mục cha không trỏ tới nó nên
        /// đọc lại không thấy — đã kiểm chứng bằng Tests/Program.cs --add-bookmark trước khi đổi sang cách này.</summary>
        public static void AddBookmark(string path, IReadOnlyList<int> parentPath, string title, int pageNumber)
            => PdfPageEditService.EditInPlace(path, "Bookmarks edited", doc =>
            {
                if (pageNumber < 1 || pageNumber > doc.GetNumberOfPages())
                    throw new ArgumentOutOfRangeException(nameof(pageNumber));
                var root = GetOrCreateRoot(doc);
                var parentDict = Navigate(root, parentPath).GetContent();

                var item = (PdfDictionary)new PdfDictionary().MakeIndirect(doc);
                item.Put(PdfName.Title, new PdfString(title, PdfEncodings.UNICODE_BIG));
                item.Put(PdfName.Parent, parentDict);
                var dest = PdfExplicitDestination.CreateFit(doc.GetPage(pageNumber));
                item.Put(PdfName.Dest, dest.GetPdfObject());

                if (parentDict.GetAsDictionary(PdfName.Last) is { } last)
                {
                    last.Put(PdfName.Next, item);
                    last.SetModified();
                    item.Put(PdfName.Prev, last);
                }
                else parentDict.Put(PdfName.First, item);
                parentDict.Put(PdfName.Last, item);

                int count = parentDict.GetAsNumber(PdfName.Count)?.IntValue() ?? 0;
                parentDict.Put(PdfName.Count, new PdfNumber(Math.Abs(count) + 1));
                parentDict.SetModified();
                item.SetModified();
            });

        public static void RenameBookmark(string path, IReadOnlyList<int> nodePath, string newTitle)
            => PdfPageEditService.EditInPlace(path, "Bookmarks edited", doc =>
            {
                var root = GetOrCreateRoot(doc);
                var content = Navigate(root, nodePath).GetContent();
                content.Put(PdfName.Title, new PdfString(newTitle, PdfEncodings.UNICODE_BIG));
                content.SetModified();
            });

        /// <summary>Xoá mục và toàn bộ mục con của nó (mục con trở thành rác không ai trỏ tới trong file —
        /// vô hại, không hiện ra ở đâu cả — thay vì dựng lại toàn bộ cây con để xoá đúng nghĩa).</summary>
        public static void DeleteBookmark(string path, IReadOnlyList<int> nodePath)
            => PdfPageEditService.EditInPlace(path, "Bookmarks edited", doc =>
            {
                var root = GetOrCreateRoot(doc);
                var parentPath = nodePath.Take(nodePath.Count - 1).ToArray();
                var parentDict = Navigate(root, parentPath).GetContent();
                var node = Navigate(root, nodePath).GetContent();

                var prev = node.GetAsDictionary(PdfName.Prev);
                var next = node.GetAsDictionary(PdfName.Next);
                if (prev != null) { prev.Put(PdfName.Next, next); prev.SetModified(); } else if (next != null) parentDict.Put(PdfName.First, next); else parentDict.Remove(PdfName.First);
                if (next != null) { next.Put(PdfName.Prev, prev); next.SetModified(); } else if (prev != null) parentDict.Put(PdfName.Last, prev); else parentDict.Remove(PdfName.Last);

                int count = parentDict.GetAsNumber(PdfName.Count)?.IntValue() ?? 0;
                int newCount = Math.Max(0, Math.Abs(count) - 1);
                if (newCount == 0) parentDict.Remove(PdfName.Count); else parentDict.Put(PdfName.Count, new PdfNumber(newCount));
                parentDict.SetModified();
            });

        /// <summary>Moves one bookmark among its siblings. The node, all of its descendants, destination and
        /// custom outline metadata stay on the same indirect object; only the sibling linked list is rewired.
        /// Returns false when it is already at the requested edge.</summary>
        public static bool MoveBookmark(string path, IReadOnlyList<int> nodePath, int delta)
        {
            if (delta == 0 || nodePath.Count == 0) return false;
            bool moved = false;
            PdfPageEditService.EditInPlace(path, "Bookmarks edited", doc =>
            {
                var root = GetOrCreateRoot(doc);
                var parentPath = nodePath.Take(nodePath.Count - 1).ToArray();
                var parent = Navigate(root, parentPath);
                var siblings = parent.GetAllChildren().ToList();
                int oldIndex = nodePath[^1];
                if (oldIndex < 0 || oldIndex >= siblings.Count)
                    throw new InvalidOperationException("This bookmark no longer exists — refresh the Bookmarks panel and try again.");
                int newIndex = Math.Clamp(oldIndex + delta, 0, siblings.Count - 1);
                if (newIndex == oldIndex) return;

                var item = siblings[oldIndex];
                siblings.RemoveAt(oldIndex);
                siblings.Insert(newIndex, item);
                RelinkChildren(parent, siblings);
                moved = true;
            });
            return moved;
        }

        /// <summary>PDF outlines are an ordered doubly linked list (/First, /Last, /Prev, /Next). Relinking
        /// the existing dictionaries is safer than recreating outlines: actions, styles, colours and descendants
        /// supplied by Acrobat/Foxit remain untouched.</summary>
        private static void RelinkChildren(PdfOutline parent, IReadOnlyList<PdfOutline> children)
        {
            var parentObject = parent.GetContent();
            if (children.Count == 0)
            {
                parentObject.Remove(PdfName.First);
                parentObject.Remove(PdfName.Last);
                parentObject.SetModified();
                return;
            }

            parentObject.Put(PdfName.First, children[0].GetContent());
            parentObject.Put(PdfName.Last, children[^1].GetContent());
            parentObject.SetModified();
            for (int index = 0; index < children.Count; index++)
            {
                var child = children[index].GetContent();
                if (index == 0) child.Remove(PdfName.Prev);
                else child.Put(PdfName.Prev, children[index - 1].GetContent());
                if (index == children.Count - 1) child.Remove(PdfName.Next);
                else child.Put(PdfName.Next, children[index + 1].GetContent());
                child.SetModified();
            }
        }

        // updateOutlines=true: theo tài liệu iText, cần dùng true khi sắp SỬA cây outline trong lần gọi này
        // (false chỉ để đọc) — dùng false ở đây từng làm việc ghi "thành công" (file có nối byte mới) nhưng
        // cây outline đọc lại vẫn y như cũ, vì gốc /Outlines không được đánh dấu cần ghi lại.
        private static PdfOutline GetOrCreateRoot(PdfDocument doc)
            => doc.GetOutlines(true) ?? throw new InvalidOperationException("Could not read the bookmark tree.");

        private static PdfOutline Navigate(PdfOutline root, IReadOnlyList<int> path)
        {
            var current = root;
            foreach (int index in path)
            {
                var children = current.GetAllChildren();
                if (index < 0 || index >= children.Count)
                    throw new InvalidOperationException("This bookmark no longer exists — the file may have changed. Reopen the Bookmarks panel and try again.");
                current = children[index];
            }
            return current;
        }
    }
}
