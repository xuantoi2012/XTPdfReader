namespace XTPdfMergeApp.Services
{
    /// <summary>Toán thuần cho nội dung hiển thị ở thanh trạng thái (số file, số trang đang chọn) —
    /// không đụng WPF control, tách ra từ MainWindow để dễ đọc/kiểm tra độc lập.</summary>
    public static class StatusBarText
    {
        /// <summary><paramref name="splitTotalPages"/> khác null khi đang ở chế độ chia đôi (Split view)
        /// và cả 2 bên đều có group — ưu tiên hiển thị dòng riêng cho chế độ này.</summary>
        public static string FileCount(int? splitTotalPages, int groupCount, int loadingCount)
        {
            if (splitTotalPages is { } total)
                return $"2 file đang chia đôi · {total} trang";
            return loadingCount > 0
                ? $"Đã thêm {groupCount} file · đang tải {loadingCount}"
                : $"Đã thêm {groupCount} file";
        }

        public static string SelectedFile(int multiSelectedCount, string? selectedFileName, int selectedPageCount, int? selectedPageNumber)
        {
            if (multiSelectedCount > 1) return $"Đã chọn {multiSelectedCount} file";
            if (selectedFileName == null) return "Chưa chọn file";

            string pagePart = selectedPageCount > 1
                ? $" · {selectedPageCount} trang đang chọn"
                : selectedPageNumber is { } pageNumber ? $" · trang {pageNumber}" : "";
            return $"Đang chọn: {selectedFileName}{pagePart}";
        }
    }
}
