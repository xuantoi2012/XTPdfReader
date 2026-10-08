using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Filter;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace XTPdfMergeApp.Services
{
    /// <summary>Vùng chữ (khung tên) theo phân số của trang NHÌN THẤY (0..1, gốc ở góc trên trái, đã tính /Rotate).</summary>
    internal sealed record TitleBlockRegion(double Left, double Top, double Right, double Bottom)
    {
        public bool IsEmpty => Right - Left < 0.002 || Bottom - Top < 0.002;
    }

    /// <summary>Các vùng của 1 khổ giấy: số hiệu, tên bản vẽ, tỷ lệ (mỗi vùng có thể chưa đặt).</summary>
    internal sealed class TitleBlockLayout
    {
        public TitleBlockRegion? Number { get; set; }
        public TitleBlockRegion? Title { get; set; }
        public TitleBlockRegion? Scale { get; set; }
        public bool HasAny => Number != null || Title != null || Scale != null;
    }

    /// <summary>Kết quả đọc 1 trang: chữ trong từng vùng (rỗng nếu vùng không có chữ).</summary>
    internal sealed record TitleBlockReading(int Page, string Number, string Title, string Scale);

    /// <summary>
    /// Đọc số hiệu / tên / tỷ lệ bản vẽ từ LỚP CHỮ của PDF trong các vùng người dùng đã khoanh (PDF xuất từ CAD có chữ thật).
    /// Không OCR: PDF scan (không có lớp chữ) cho kết quả rỗng — để bước OCR sau này lấp.
    /// </summary>
    internal static class TitleBlockReader
    {
        /// <summary>Điểm (fx, fy) — phân số của trang NHÌN THẤY, gốc ở góc trên trái — trong không gian trang gốc (chưa xoay).</summary>
        public static (double X, double Y) MapPoint(PdfPage page, double fx, double fy)
        {
            var box = page.GetMediaBox();
            double x0 = box.GetX(), y0 = box.GetY(), w = box.GetWidth(), h = box.GetHeight();
            int rotation = ((page.GetRotation() % 360) + 360) % 360;
            double dispW = rotation % 180 == 0 ? w : h, dispH = rotation % 180 == 0 ? h : w;
            double dx = fx * dispW, dy = fy * dispH;
            return rotation switch
            {
                90 => (x0 + dy, y0 + dx),
                180 => (x0 + w - dx, y0 + dy),
                270 => (x0 + w - dy, y0 + h - dx),
                _ => (x0 + dx, y0 + h - dy)
            };
        }

        /// <summary>Hình chữ nhật trong không gian trang gốc (chưa xoay) của vùng <paramref name="region"/> trên trang <paramref name="page"/>.</summary>
        public static Rectangle ToUserSpace(PdfPage page, TitleBlockRegion region)
        {
            var a = MapPoint(page, region.Left, region.Top);
            var b = MapPoint(page, region.Right, region.Bottom);
            return new Rectangle((float)Math.Min(a.X, b.X), (float)Math.Min(a.Y, b.Y), (float)Math.Abs(a.X - b.X), (float)Math.Abs(a.Y - b.Y));
        }

        /// <summary>Chữ trong vùng: các dòng nối bằng dấu cách, gọn khoảng trắng.</summary>
        public static string TextIn(PdfPage page, TitleBlockRegion region)
        {
            if (region.IsEmpty) return "";
            var rect = ToUserSpace(page, region);
            var strategy = new FilteredTextEventListener(new LocationTextExtractionStrategy(), new TextRegionEventFilter(rect));
            string text = PdfTextExtractor.GetTextFromPage(page, strategy);
            return string.Join(" ", text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0));
        }

        /// <summary>Đọc các trang <paramref name="pages"/> (1-based) của file; <paramref name="layoutFor"/> trả về vùng của khổ giấy trang đó (null = bỏ qua trang).</summary>
        public static List<TitleBlockReading> Read(string path, IReadOnlyList<int> pages, Func<int, PdfPage, TitleBlockLayout?> layoutFor)
        {
            var result = new List<TitleBlockReading>();
            var properties = new ReaderProperties();
            if (PdfThumbnailService.TryGetDocumentPassword(path) is { Length: > 0 } password)
                properties.SetPassword(Encoding.UTF8.GetBytes(password));
            using var doc = new PdfDocument(new PdfReader(path, properties));
            foreach (int number in pages)
            {
                if (number < 1 || number > doc.GetNumberOfPages()) continue;
                var page = doc.GetPage(number);
                var layout = layoutFor(number, page);
                if (layout == null || !layout.HasAny) { result.Add(new TitleBlockReading(number, "", "", "")); continue; }
                string Safe(TitleBlockRegion? r) { try { return r == null ? "" : TextIn(page, r); } catch { return ""; } }
                result.Add(new TitleBlockReading(number, Safe(layout.Number), Safe(layout.Title), Safe(layout.Scale)));
            }
            return result;
        }

        /// <summary>Tên khổ giấy của trang (cả khi xoay), để chọn bộ vùng — A1 ngang và A1 đứng dùng chung bộ vùng theo hướng nhìn thấy.</summary>
        public static string SizeKey(PdfPage page)
        {
            var box = page.GetMediaBox();
            int rotation = ((page.GetRotation() % 360) + 360) % 360;
            double w = rotation % 180 == 0 ? box.GetWidth() : box.GetHeight(), h = rotation % 180 == 0 ? box.GetHeight() : box.GetWidth();
            var (name, _) = PdfExportService.SizeName(w, h);
            return name + (w > h ? " landscape" : " portrait");
        }
    }

    /// <summary>Lưu các bộ vùng khung tên theo khổ giấy (%LocalAppData%\XTPdfReader\titleblock-regions.json) để lần sau khỏi khoanh lại.</summary>
    internal static class TitleBlockStore
    {
        private static readonly string FilePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTPdfReader", "titleblock-regions.json");
        private static Dictionary<string, TitleBlockLayout>? _layouts;

        public static TitleBlockLayout? Get(string sizeKey) => Load().TryGetValue(sizeKey, out var layout) ? layout : null;

        public static void Set(string sizeKey, TitleBlockLayout layout)
        {
            Load()[sizeKey] = layout;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_layouts, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch { /* không ghi được: chỉ nhớ trong phiên */ }
        }

        private static Dictionary<string, TitleBlockLayout> Load()
        {
            if (_layouts != null) return _layouts;
            try { _layouts = JsonSerializer.Deserialize<Dictionary<string, TitleBlockLayout>>(File.ReadAllText(FilePath)) ?? new(); }
            catch { _layouts = new(); }
            return _layouts;
        }
    }
}
