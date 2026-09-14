using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace XTPdfMergeApp.Converters
{
    /// <summary>Khoảng cách dưới mỗi trang (Border.Margin trong ReaderContinuousPageTemplate) PHẢI
    /// scale theo zoom, KHÔNG được để cố định — ZoomContinuousAtPoint tính vị trí cuộn mới bằng công
    /// thức tỉ lệ thuần (offset_mới = offset_cũ × newZoom/oldZoom), giả định TOÀN BỘ nội dung theo
    /// chiều dọc (cả trang lẫn khoảng cách giữa các trang) đều scale đều theo đúng 1 tỉ lệ — nếu
    /// margin cố định không scale, giả định đó sai, và sai số càng lớn ở zoom CÀNG THẤP (trang co nhỏ
    /// nhưng margin vẫn nguyên, chiếm tỉ lệ ngày càng lớn so với trang) — đúng bug đã gặp: zoom nhỏ rồi
    /// zoom lại bị "bắt nhầm trang" vì lệch quá xa so với vị trí đáng lẽ đúng.</summary>
    public sealed class ContinuousPageMarginConverter : IValueConverter
    {
        private const double BaseMargin = 14;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double zoom = value is double d ? d : 1.0;
            return new Thickness(0, 0, 0, BaseMargin * zoom);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
