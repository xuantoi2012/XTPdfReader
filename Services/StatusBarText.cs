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
                return $"2 files side by side · {total} pages";
            return loadingCount > 0
                ? $"{groupCount} file{(groupCount == 1 ? "" : "s")} added · {loadingCount} loading"
                : $"{groupCount} file{(groupCount == 1 ? "" : "s")} added";
        }

        public static string SelectedFile(int multiSelectedCount, string? selectedFileName, int selectedPageCount, int? selectedPageNumber)
        {
            if (multiSelectedCount > 1) return $"{multiSelectedCount} files selected";
            if (selectedFileName == null) return "No file selected";

            string pagePart = selectedPageCount > 1
                ? $" · {selectedPageCount} pages selected"
                : selectedPageNumber is { } pageNumber ? $" · page {pageNumber}" : "";
            return $"{selectedFileName}{pagePart}";
        }
    }
}
