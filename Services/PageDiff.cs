using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// So sánh 2 phiên bản của 1 trang: nét chỉ có ở bản cũ tô đỏ, chỉ có ở bản mới tô xanh, nét giống nhau xám — kiểu chồng bản vẽ.
    /// Mỗi điểm ảnh "có nét" nếu đủ tối; 1 điểm coi là trùng nếu bản kia có nét trong lân cận 1 điểm (bớt nhiễu do khử răng cưa).
    /// </summary>
    internal static class PageDiff
    {
        private const byte InkThreshold = 200;

        public sealed record Result(BitmapSource Overlay, double ChangedFraction, int OldOnly, int NewOnly, int Both);

        /// <summary>Chồng <paramref name="oldImage"/> và <paramref name="newImage"/> (bản mới được co giãn về cỡ bản cũ nếu khác cỡ).</summary>
        public static Result Overlay(BitmapSource oldImage, BitmapSource newImage)
        {
            int w = oldImage.PixelWidth, h = oldImage.PixelHeight;
            var oldInk = Ink(oldImage, w, h);
            var newInk = Ink(newImage, w, h);
            var oldNear = Dilate(oldInk, w, h);
            var newNear = Dilate(newInk, w, h);

            var pixels = new byte[w * h * 4];
            int oldOnly = 0, newOnly = 0, both = 0;
            for (int i = 0; i < w * h; i++)
            {
                byte b = 255, g = 255, r = 255;
                bool o = oldInk[i], n = newInk[i];
                if (o && n) { b = g = r = 110; both++; }
                else if (o && !newNear[i]) { b = 60; g = 60; r = 230; oldOnly++; }       // chỉ có ở bản cũ → đỏ
                else if (n && !oldNear[i]) { b = 230; g = 90; r = 40; newOnly++; }        // chỉ có ở bản mới → xanh
                else if (o || n) { b = g = r = 160; both++; }                              // lệch ≤ 1 điểm: coi là nét chung
                pixels[i * 4] = b; pixels[i * 4 + 1] = g; pixels[i * 4 + 2] = r; pixels[i * 4 + 3] = 255;
            }
            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            bitmap.Freeze();
            int total = Math.Max(1, oldOnly + newOnly + both);
            return new Result(bitmap, (oldOnly + newOnly) / (double)total, oldOnly, newOnly, both);
        }

        /// <summary>Điểm "có nét" (đủ tối) của ảnh sau khi co về w × h.</summary>
        private static bool[] Ink(BitmapSource source, int w, int h)
        {
            BitmapSource fitted = source;
            if (source.PixelWidth != w || source.PixelHeight != h)
                fitted = new TransformedBitmap(source, new ScaleTransform(w / (double)source.PixelWidth, h / (double)source.PixelHeight));
            var gray = new FormatConvertedBitmap(fitted, PixelFormats.Gray8, null, 0);
            int gw = gray.PixelWidth, gh = gray.PixelHeight;
            var data = new byte[gw * gh];
            gray.CopyPixels(data, gw, 0);
            var ink = new bool[w * h];
            for (int y = 0; y < Math.Min(h, gh); y++)
                for (int x = 0; x < Math.Min(w, gw); x++)
                    ink[y * w + x] = data[y * gw + x] < InkThreshold;
            return ink;
        }

        private static bool[] Dilate(bool[] ink, int w, int h)
        {
            var result = new bool[ink.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!ink[y * w + x]) continue;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && ny >= 0 && nx < w && ny < h) result[ny * w + nx] = true;
                        }
                }
            return result;
        }
    }
}
