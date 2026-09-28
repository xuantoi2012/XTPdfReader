using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Services
{
    public enum PrintScale { FitToPaper, ActualSize, Custom }
    public enum PrintColor { Color, Grayscale, BlackLines }

    /// <summary>Yêu cầu in: các trang (theo thứ tự in), máy in, khổ giấy, số bản, tỉ lệ, màu.</summary>
    public sealed record PrintRequest(
        IReadOnlyList<(string SourcePath, int PageNumber)> Pages, string Printer, PaperSize Paper, int Copies,
        PrintScale Scale, int CustomPercent, PrintColor Color, bool AutoRotate);

    /// <summary>In PDF: vẽ từng trang bằng PDFium ở ~300 dpi (theo trạng thái layer đang xem) rồi gửi qua System.Drawing.Printing.</summary>
    public static class PdfPrintService
    {
        private const int Dpi = 300;
        private const int MaxPixels = 9000;

        /// <summary>Khổ trang (point, đã tính xoay) của 1 trang — để chọn hướng giấy và tỉ lệ.</summary>
        private static (double W, double H) PageSize(string path, int pageNumber)
        {
            var sizes = PdfThumbnailService.GetPageSizesAsync(path).GetAwaiter().GetResult();
            return sizes != null && pageNumber >= 1 && pageNumber <= sizes.Length && sizes[pageNumber - 1].Width > 0 ? sizes[pageNumber - 1] : (595, 842);
        }

        public static Task<bool> PrintAsync(PrintRequest request, IProgress<int>? progress = null) => Task.Run(() =>
        {
            var document = new PrintDocument();
            document.PrinterSettings.PrinterName = request.Printer;
            if (!document.PrinterSettings.IsValid) return false;
            document.PrinterSettings.Copies = (short)Math.Clamp(request.Copies, 1, 99);
            document.DefaultPageSettings.PaperSize = request.Paper;
            document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            document.DocumentName = "PDF Reader Pro";
            document.PrintController = new StandardPrintController();

            int index = 0;
            var sizeCache = new Dictionary<string, (double W, double H)>();
            (double W, double H) SizeOf(int i)
            {
                var (path, number) = request.Pages[i];
                string key = path + "|" + number;
                if (!sizeCache.TryGetValue(key, out var size)) sizeCache[key] = size = PageSize(path, number);
                return size;
            }

            document.QueryPageSettings += (_, e) =>
            {
                if (index >= request.Pages.Count) return;
                var (w, h) = SizeOf(index);
                if (request.AutoRotate) e.PageSettings.Landscape = w > h;
            };
            document.PrintPage += (_, e) =>
            {
                var (path, number) = request.Pages[index];
                var (wPt, hPt) = SizeOf(index);
                RectangleF area = e.MarginBounds.Width > 0 ? e.MarginBounds : e.PageBounds;
                double pageW = wPt / 72.0 * 100, pageH = hPt / 72.0 * 100; // trăm inch
                double scale = request.Scale switch
                {
                    PrintScale.ActualSize => 1.0,
                    PrintScale.Custom => Math.Clamp(request.CustomPercent, 5, 1000) / 100.0,
                    _ => Math.Min(area.Width / pageW, area.Height / pageH)
                };
                double drawW = pageW * scale, drawH = pageH * scale;
                int pixelsW = (int)Math.Clamp(drawW / 100.0 * Dpi, 200, MaxPixels);

                var source = PdfThumbnailService.RenderPageAsync(path, number - 1, pixelsW, layerToken: PdfLayerStateStore.GetToken(path))
                    .GetAwaiter().GetResult();
                if (source != null)
                {
                    using var bitmap = ToBitmap(source, request.Color);
                    var g = e.Graphics!;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    float x = area.Left + (float)((area.Width - drawW) / 2), y = area.Top + (float)((area.Height - drawH) / 2);
                    g.DrawImage(bitmap, x, y, (float)drawW, (float)drawH);
                }
                index++;
                progress?.Report(index);
                e.HasMorePages = index < request.Pages.Count;
            };
            document.Print();
            return true;
        });

        /// <summary>Ảnh WPF (BGRA) → System.Drawing.Bitmap, đổi màu theo chế độ in.</summary>
        internal static Bitmap ToBitmap(BitmapSource source, PrintColor color)
        {
            var converted = source.Format == System.Windows.Media.PixelFormats.Bgra32 || source.Format == System.Windows.Media.PixelFormats.Pbgra32 || source.Format == System.Windows.Media.PixelFormats.Bgr32
                ? source : new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth, h = converted.PixelHeight, stride = w * 4;
            var pixels = new byte[stride * h];
            converted.CopyPixels(pixels, stride, 0);
            if (color != PrintColor.Color)
            {
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    int gray = (pixels[i + 2] * 77 + pixels[i + 1] * 150 + pixels[i] * 29) >> 8;
                    if (color == PrintColor.BlackLines) gray = gray < 215 ? 0 : 255;
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)gray;
                    pixels[i + 3] = 255;
                }
            }
            var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < h; y++)
                    System.Runtime.InteropServices.Marshal.Copy(pixels, y * stride, data.Scan0 + y * data.Stride, stride);
            }
            finally { bitmap.UnlockBits(data); }
            return bitmap;
        }

        /// <summary>"1-12, 40, 55-60" → danh sách số trang 1-based (theo thứ tự nhập, bỏ trang ngoài [1, max]); null nếu sai cú pháp.</summary>
        public static List<int>? ParseRange(string text, int max)
        {
            var result = new List<int>();
            foreach (string part in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var bounds = part.Split('-', StringSplitOptions.TrimEntries);
                if (bounds.Length == 1 && int.TryParse(bounds[0], out int single)) { if (single >= 1 && single <= max) result.Add(single); }
                else if (bounds.Length == 2 && int.TryParse(bounds[0], out int a) && int.TryParse(bounds[1], out int b))
                {
                    if (a > b) (a, b) = (b, a);
                    for (int p = Math.Max(1, a); p <= Math.Min(max, b); p++) result.Add(p);
                }
                else return null;
            }
            return result;
        }
    }
}
