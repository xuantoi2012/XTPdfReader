namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// Key của 3 cache ảnh (thumbnail, trang Viewer, tile) — đều kèm "phiên bản trạng thái layer" của file
    /// (<see cref="PdfLayerStateStore.GetToken"/>): bật/tắt layer → key mới (không lấy nhầm ảnh trạng thái
    /// khác); quay lại trạng thái cũ → đúng key cũ → trúng ảnh đã vẽ, không phải vẽ lại.
    /// </summary>
    public static class RenderCacheKeys
    {
        public static (string Path, int Page, string Layers) Thumbnail(string path, int page)
            => (path, page, PdfLayerStateStore.GetToken(path));

        public static (string Path, int Page, int Width, string Layers) ReaderPage(string path, int page, int width)
            => (path, page, width, PdfLayerStateStore.GetToken(path));

        /// <summary>Phần layer của key tile (các thành phần hình học do ReaderWindow tự tính).</summary>
        public static string TileLayers(string path) => PdfLayerStateStore.GetToken(path);
    }
}
