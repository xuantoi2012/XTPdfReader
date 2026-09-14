using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using XTPdfMergeApp.Domain;

namespace XTPdfMergeApp.Services
{
    /// <summary>Toán thuần cho vị trí chèn khi kéo-thả (trang trong 1 window, cả window giữa các
    /// window khác, trang giữa các window) và tự cuộn khi con trỏ sát mép — không đụng WPF control,
    /// chỉ nhận bounds/point đã đo sẵn từ visual tree. Tách ra từ MainWindow để dễ đọc/kiểm tra
    /// độc lập.</summary>
    public static class DropPositionMath
    {
        /// <summary>Vị trí chèn (0..bounds.Count) dựa vào so sánh toạ độ điểm thả với điểm giữa
        /// mỗi item dọc theo 1 trục (X nếu <paramref name="horizontal"/>, ngược lại Y).</summary>
        public static int FindInsertIndex(IReadOnlyList<Rect> itemBounds, Point point, bool horizontal)
        {
            for (int i = 0; i < itemBounds.Count; i++)
            {
                var b = itemBounds[i];
                double mid = horizontal ? b.X + b.Width / 2 : b.Y + b.Height / 2;
                double coord = horizontal ? point.X : point.Y;
                if (coord < mid) return i;
            }
            return itemBounds.Count;
        }

        /// <summary>Biến thể cho panel WRAP theo chiều ngang (nhiều thumbnail/hàng, tự xuống hàng —
        /// chế độ Hàng và Chia đôi): so theo Y trước — chỉ so theo X khi điểm thả nằm gần đúng "hàng"
        /// của item đó, để tránh nhảy hàng sai khi kéo ngang qua item. Khác <see cref="FindInsertIndex"/>
        /// (dùng cho panel 1 dải/1 dòng duy nhất, vd chế độ Cột) vốn chỉ so 1 trục.</summary>
        public static int FindInsertIndexWrapped(IReadOnlyList<Rect> itemBounds, Point point)
        {
            if (itemBounds.Count == 0) return 0;

            double medianHeight = itemBounds
                .Where(b => b.Height > 0)
                .Select(b => b.Height)
                .OrderBy(h => h)
                .ElementAtOrDefault(itemBounds.Count / 2);
            if (medianHeight <= 0) medianHeight = itemBounds.Max(b => Math.Max(1, b.Height));
            double rowTolerance = Math.Max(8, medianHeight * 0.45);

            // So với centerY của phần tử ĐẦU TIÊN trong hàng đang gom (mốc cố định), KHÔNG so với
            // trung bình cộng dồn của cả hàng — trung bình cộng dồn "trôi" dần theo từng phần tử
            // mới thêm vào, có thể khiến 2-3 hàng thật bị gộp nhầm thành 1 hàng logic (kéo dài liên
            // tiếp cứ chưa vượt tolerance so với lần trôi trước là vẫn được gộp), khiến sau đó chỉ
            // so trục X trong hàng gộp sai nên đổi Y không còn tác dụng — đúng bug thực tế gặp phải
            // (kéo qua ~260px theo Y nhưng vị trí chèn đứng yên). Item đã sort theo Top nên chỉ cần
            // so với hàng cuối cùng (đang mở), không cần dò lại toàn bộ rows.
            var rows = new List<List<(int Index, Rect Bounds)>>();
            foreach (var item in itemBounds.Select((bounds, index) => (Index: index, Bounds: bounds))
                                           .OrderBy(item => item.Bounds.Top)
                                           .ThenBy(item => item.Bounds.Left))
            {
                double centerY = item.Bounds.Top + item.Bounds.Height / 2;
                List<(int Index, Rect Bounds)>? row = null;
                if (rows.Count > 0)
                {
                    var lastRow = rows[^1];
                    double anchorCenterY = lastRow[0].Bounds.Top + lastRow[0].Bounds.Height / 2;
                    if (Math.Abs(anchorCenterY - centerY) <= rowTolerance) row = lastRow;
                }

                if (row == null)
                {
                    row = new List<(int Index, Rect Bounds)>();
                    rows.Add(row);
                }

                row.Add(item);
            }

            rows = rows
                .Select(row => row.OrderBy(item => item.Bounds.Left).ToList())
                .OrderBy(row => row.Average(item => item.Bounds.Top + item.Bounds.Height / 2))
                .ToList();

            int rowIndex = rows.Count - 1;
            for (int i = 0; i < rows.Count; i++)
            {
                double rowTop = rows[i].Min(item => item.Bounds.Top);
                double rowBottom = rows[i].Max(item => item.Bounds.Bottom);
                if (point.Y < rowTop)
                {
                    rowIndex = i;
                    break;
                }

                if (point.Y <= rowBottom)
                {
                    rowIndex = i;
                    break;
                }

                if (i + 1 < rows.Count)
                {
                    double nextTop = rows[i + 1].Min(item => item.Bounds.Top);
                    if (point.Y < (rowBottom + nextTop) / 2)
                    {
                        rowIndex = i;
                        break;
                    }
                }
            }

            var targetRow = rows[rowIndex];
            foreach (var item in targetRow)
            {
                if (point.X < item.Bounds.Left + item.Bounds.Width / 2)
                    return item.Index;
            }

            return targetRow[^1].Index + 1;
        }

        /// <summary>Kéo-thả 1 window vào GIỮA các window khác là no-op nếu vị trí chèn trùng với
        /// vị trí hiện tại hoặc ngay sau nó.</summary>
        public static bool IsReorderNoOp(int oldIndex, int insertAt, int count)
        {
            if (oldIndex < 0) return true;
            insertAt = Math.Clamp(insertAt, 0, count);
            return insertAt == oldIndex || insertAt == oldIndex + 1;
        }

        /// <summary>Kéo-thả 1 nhóm trang NGAY TRONG CÙNG 1 window là no-op nếu thứ tự cuối cùng sau
        /// khi chèn giống hệt thứ tự hiện tại (dù insertAt khác 0 do khối trang đang di chuyển đứng
        /// trước vị trí chèn).</summary>
        internal static bool IsSameGroupPageDropNoOp(IList<PagePlacement> current, IReadOnlyCollection<PagePlacement> draggedPages, int insertAt)
        {
            if (draggedPages.Count == 0) return true;

            insertAt = Math.Clamp(insertAt, 0, current.Count);

            var movingSet = new HashSet<PagePlacement>(draggedPages);
            if (!movingSet.All(current.Contains)) return false;

            var movingIndices = draggedPages
                .Select(page => current.IndexOf(page))
                .Where(i => i >= 0)
                .OrderBy(i => i)
                .ToList();
            if (movingIndices.Count != draggedPages.Count) return false;

            bool contiguousBlock = movingIndices.Count == 1 ||
                movingIndices.Zip(movingIndices.Skip(1), (a, b) => b == a + 1).All(v => v);
            if (contiguousBlock && insertAt >= movingIndices[0] && insertAt <= movingIndices[^1] + 1)
                return true;

            var movingInOrder = current.Where(movingSet.Contains).ToList();
            var withoutMoving = current.Where(p => !movingSet.Contains(p)).ToList();
            int removedBefore = current.Take(insertAt).Count(movingSet.Contains);
            int adjustedInsertAt = Math.Clamp(insertAt - removedBefore, 0, withoutMoving.Count);

            withoutMoving.InsertRange(adjustedInsertAt, movingInOrder);
            return current.SequenceEqual(withoutMoving);
        }

        /// <summary>Offset cuộn mới nếu con trỏ đang sát mép (trong khoảng <paramref name="edge"/> px)
        /// trong lúc kéo-thả, hoặc null nếu chưa sát mép (KHÔNG nên cuộn — gọi ScrollTo* dù cùng
        /// giá trị vẫn có thể bắn ScrollChanged thừa, ăn theo là quét/prefetch thumbnail vô ích).</summary>
        public static double? EdgeAutoScrollOffset(double pos, double viewportExtent, double currentOffset, double edge = 36, double step = 22)
        {
            if (pos < edge) return currentOffset - step;
            if (pos > viewportExtent - edge) return currentOffset + step;
            return null;
        }
    }
}
