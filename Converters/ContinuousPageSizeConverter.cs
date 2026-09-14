using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace XTPdfMergeApp.Converters
{
    /// <summary>Nhân 2 số double (size * zoom) — dùng để tính Width/Height của khung "sizer" (Grid)
    /// bao quanh 1 trang trong chế độ Cuộn liên tục, theo ĐÚNG kích thước Image ĐÃ TỰ ĐO ĐƯỢC
    /// (ElementName binding tới ContinuousPageImage.ActualWidth/ActualHeight — xem
    /// ReaderContinuousPageTemplate) thay vì tính aspect ratio riêng một lần nữa từ bitmap.
    ///
    /// TỪNG dùng 1 converter tính riêng aspect = PixelHeight/PixelWidth của bitmap để suy Height —
    /// NHƯNG 2 phép tính độc lập (1 bên WPF tự đo Image qua Stretch=Uniform, 1 bên converter tự tính
    /// từ PixelWidth/PixelHeight) có thể lệch nhau (fallback placeholder aspect khi bitmap binding
    /// chưa kịp cập nhật, timing khác nhau giữa 2 binding...) — đúng bug đã gặp: Grid to hều nhưng ảnh
    /// hiển thị bé tí ở góc trên-trái. Sửa bằng cách bỏ hẳn phép tính song song, cho Grid LUÔN lấy
    /// đúng số Image đã đo, đảm bảo khớp nhau tuyệt đối bằng cấu trúc chứ không phải bằng 2 công thức
    /// hy vọng ra cùng 1 kết quả.</summary>
    public sealed class ContinuousPageSizeConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not double size || values[1] is not double zoom)
                return DependencyProperty.UnsetValue;
            return size * zoom;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
