using System;

namespace XTPdfMergeApp.Services
{
    /// <summary>Toán thuần cho layout Cột thích ứng (số cột + độ rộng card + kích thước thumbnail
    /// tự động) của vùng danh sách tài liệu — không đụng WPF control, tách ra từ MainWindow để dễ
    /// đọc/kiểm tra độc lập.</summary>
    public static class AdaptiveLayoutMath
    {
        public readonly record struct Result(double AdaptiveDocumentWidth, double ThumbnailWidth, double ThumbnailHeight);

        public static double AvailableWidth(double viewportWidth) => Math.Max(180, viewportWidth - 20);

        public static Result Compute(
            double viewportWidth,
            int documentCount,
            bool autoThumbnailSize,
            double currentThumbnailWidth,
            double currentThumbnailHeight)
        {
            double available = AvailableWidth(viewportWidth);
            int count = Math.Max(1, documentCount);
            double desiredCell = autoThumbnailSize ? 300 : currentThumbnailWidth + 58;
            int columns = Math.Clamp((int)Math.Floor((available + 10) / (desiredCell + 10)), 1, count);
            double adaptiveWidth = Math.Max(180, (available - (columns - 1) * 10) / columns);

            double thumbnailWidth = currentThumbnailWidth;
            double thumbnailHeight = currentThumbnailHeight;
            if (autoThumbnailSize)
            {
                thumbnailWidth = Math.Clamp(adaptiveWidth - 56, 160, 400);
                thumbnailHeight = Math.Round(thumbnailWidth * 0.75);
            }

            return new Result(adaptiveWidth, thumbnailWidth, thumbnailHeight);
        }

        /// <summary>Khoảng cách quanh mỗi ô thumbnail ở Split/Focus mode — CỐ Ý nhỏ, vì user muốn
        /// xếp được CÀNG NHIỀU cột càng tốt, padding tối thiểu là đủ.</summary>
        public const double SplitItemGap = 8;

        public readonly record struct SplitPaneResult(double ContentWidth, double ThumbnailWidth, double ThumbnailHeight);

        /// <summary>Biến thể cho Split view/Focus mode: mỗi pane = nửa (hoặc toàn bộ, nếu Focus mode
        /// chỉ 1 pane) chiều rộng SplitHostGrid, trừ divider/margin/border/scrollbar.</summary>
        public static SplitPaneResult ComputeSplitPane(
            double splitHostActualWidth,
            bool singlePane,
            bool autoThumbnailSize,
            double currentThumbnailWidth,
            double currentThumbnailHeight)
        {
            double contentWidth = singlePane
                ? Math.Max(120, splitHostActualWidth - 34)
                : Math.Max(120, (splitHostActualWidth - 6) / 2 - 34);

            double thumbnailWidth = currentThumbnailWidth;
            double thumbnailHeight = currentThumbnailHeight;
            if (autoThumbnailSize)
            {
                int columns = Math.Max(1, (int)Math.Floor((contentWidth + SplitItemGap) / (280.0 + SplitItemGap)));
                double cellWidth = contentWidth / columns;
                thumbnailWidth = Math.Clamp(cellWidth - SplitItemGap, 160, 400);
                thumbnailHeight = Math.Round(thumbnailWidth * 0.75);
            }
            // Cỡ CỐ ĐỊNH (không auto) — KHÔNG tính trước "Columns" rồi chia đều contentWidth cho số
            // cột đó nữa: làm vậy MỖI Ô bị giãn rộng hơn thumbnailWidth thật cần, phần dư đáng lẽ đủ
            // xếp thêm 1 cột lại bị "ăn" vào việc giãn các cột hiện có — đúng bug "còn nhiều chỗ
            // trống mà không thêm cột" đã gặp. Để nguyên ThumbnailWidth, ItemWidth gán ĐÚNG
            // thumbnailWidth+gap (xem UpdateSplitPaneContentWidth) — VirtualizingWrapPanel tự xếp
            // CÀNG NHIỀU ô khít nhau càng tốt theo bề rộng thật đang có.

            return new SplitPaneResult(contentWidth, thumbnailWidth, thumbnailHeight);
        }

        public readonly record struct RowResult(double GroupsListWidth, double RowCardWidth);

        /// <summary>Chế độ Hàng: mỗi window chiếm trọn 1 hàng ngang, GroupsList rộng bằng viewport
        /// (trừ đệm), card bên trong lại trừ thêm chút cho margin/border riêng của nó.</summary>
        public static RowResult ComputeRowWidth(double viewportWidth)
        {
            double groupsListWidth = Math.Max(120, viewportWidth - 16);
            double rowCardWidth = Math.Max(120, groupsListWidth - 10);
            return new RowResult(groupsListWidth, rowCardWidth);
        }
    }
}
