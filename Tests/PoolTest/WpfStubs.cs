// Thay tối thiểu cho các kiểu WPF mà PdfThumbnailService dùng, để chạy đúng code của nó trên Linux.
using System;
namespace System.Windows
{
    public struct Int32Rect
    {
        public Int32Rect(int x, int y, int width, int height) { X = x; Y = y; Width = width; Height = height; }
        public int X { get; set; } public int Y { get; set; } public int Width { get; set; } public int Height { get; set; }
    }
}
namespace System.Windows.Media
{
    public readonly struct PixelFormat { }
    public static class PixelFormats { public static PixelFormat Bgra32 => default; }
}
namespace System.Windows.Media.Imaging
{
    public sealed class BitmapPalette { }
    public class BitmapSource
    {
        public int PixelWidth { get; private init; }
        public int PixelHeight { get; private init; }
        public byte[] Pixels { get; private init; } = Array.Empty<byte>();
        public static BitmapSource Create(int w, int h, double dpiX, double dpiY, System.Windows.Media.PixelFormat f,
            BitmapPalette? palette, IntPtr buffer, int bufferSize, int stride)
        {
            var px = new byte[bufferSize];
            System.Runtime.InteropServices.Marshal.Copy(buffer, px, 0, bufferSize);
            return new BitmapSource { PixelWidth = w, PixelHeight = h, Pixels = px };
        }
        public void Freeze() { }
        public ulong Hash()
        {
            ulong x = 1469598103934665603;
            foreach (var b in Pixels) x = (x ^ b) * 1099511628211;
            return x;
        }
    }
}
