using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Converters
{
    /// <summary>Height của ContinuousPageImage khi CHƯA có bitmap (Thumbnail lẫn ReaderBitmap đều
    /// null) — trả về 1 chiều cao NATIVE ước lượng hợp lý thay vì để trống (Auto/NaN, Stretch=Uniform
    /// không có Source sẽ tính ra 0). Một khi ĐÃ có bitmap thật, trả về double.NaN (= Auto) để Image tự
    /// đo theo đúng tỉ lệ bitmap qua Stretch=Uniform như bình thường.
    ///
    /// LÝ DO CẦN CÁI NÀY: Grid (sizer, xem ReaderContinuousPageTemplate) lấy kích thước THẲNG từ
    /// Image.ActualHeight đã đo được (ContinuousPageSizeConverter) — nếu Image co về 0 lúc chưa tải
    /// ảnh, Grid CŨNG co về 0, hàng chục/hàng trăm trang chưa tải dồn hết vào 1 màn hình (đúng bug đã
    /// gặp: visibleCount=58-60 trong lúc bình thường chỉ nên ~3-5) — kéo theo TẤT CẢ đồng loạt gọi tải
    /// ảnh độ phân giải đầy đủ cùng lúc, sập cả hiệu năng lẫn RAM. Placeholder height giữ chỗ hợp lý
    /// ngăn hẳn kịch bản này, đúng cách các list ảo hoá hiện đại ước lượng kích thước item chưa tải.
    ///
    /// PHẢI ưu tiên PageRow.AspectRatio (tỉ lệ THẬT, lấy rẻ qua PDFium FPDF_GetPageWidth/Height —
    /// xem ReaderContinuousImage_Loaded/PdfThumbnailService.GetPageAspectRatioAsync) khi đã biết, chỉ
    /// fallback về hằng số ISO 216 (√2, ước lượng A-series) khi CHƯA kịp biết — CAD workflow có nhiều
    /// khổ giấy khác A4 (A0/A1/A3 ngang...), dùng CỐ ĐỊNH 1.4142 cho mọi trang khiến placeholder sai xa
    /// thực tế với các trang không phải A-series, và khi bitmap thật load xong, Image.ActualHeight nhảy
    /// đột ngột (vd co 1 nửa với trang ngang) — đúng nguyên nhân đã xác nhận qua log
    /// (%TEMP%\XTPdfMergeApp_ContinuousZoom.log): extentBefore/After của ScrollViewer nhảy vọt không tỉ
    /// lệ với zoom đúng vào những lúc các trang lần đầu hiện thực hoá và có bitmap.</summary>
    public sealed class ContinuousPagePlaceholderHeightConverter : IMultiValueConverter
    {
        private const double FallbackAspect = 1.4142; // tỉ lệ ISO 216 (A4...) — chỉ dùng khi CHƯA biết tỉ lệ thật

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[1] is not double baseWidth)
                return DependencyProperty.UnsetValue;

            if (values[0] is BitmapSource) return double.NaN;

            double aspect = values.Length > 2 && values[2] is double known ? known : FallbackAspect;
            return baseWidth * aspect;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
