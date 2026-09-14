using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace XTPdfMergeApp.Converters
{
    /// <summary>Đường dẫn file nguồn (SourcePath) → 1 màu cố định trong bảng màu — cùng 1 file luôn
    /// ra cùng 1 màu (băm theo đường dẫn, không phân biệt hoa/thường), khác file gần như chắc chắn
    /// khác màu. Dùng để tô viền/badge của trang được NHẬP từ file khác vào 1 window, giúp phân biệt
    /// nhiều nguồn khác nhau bằng mắt thay vì phải hover từng trang xem tooltip.</summary>
    public sealed class SourceFileColorConverter : IValueConverter
    {
        private static readonly Color[] Palette =
        {
            Color.FromRgb(0x0F, 0x6E, 0x56), // teal
            Color.FromRgb(0x99, 0x3C, 0x1D), // coral
            Color.FromRgb(0x53, 0x4A, 0xB7), // purple
            Color.FromRgb(0x99, 0x35, 0x56), // pink
            Color.FromRgb(0x18, 0x5F, 0xA5), // blue
            Color.FromRgb(0x3B, 0x6D, 0x11), // green
            Color.FromRgb(0x85, 0x4F, 0x0B), // amber đậm
            Color.FromRgb(0x79, 0x1F, 0x1F), // đỏ đậm
        };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string path || string.IsNullOrEmpty(path))
                return Brushes.Transparent;

            int hash = path.ToLowerInvariant().GetHashCode();
            int index = (int)((uint)hash % (uint)Palette.Length);
            var brush = new SolidColorBrush(Palette[index]);
            brush.Freeze();
            return brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
