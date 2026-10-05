using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// Đo trên bản vẽ theo tỷ lệ: khoảng cách trên trang (point) → độ dài thật. Tỷ lệ lấy từ thông tin sheet của trang (/XTSheet /Scale, vd "1:100"),
    /// nếu trang không có thì người dùng nhập. Mặc định đơn vị bản vẽ là mm (độ dài trên giấy × mẫu số = độ dài thật).
    /// </summary>
    internal static class PageMeasure
    {
        private static readonly Regex ScalePattern = new(@"(\d+(?:[.,]\d+)?)\s*[:/]\s*(\d+(?:[.,]\d+)?)", RegexOptions.Compiled);

        /// <summary>Mẫu số của tỷ lệ ("1:100" → 100, "1/50" → 50, "TL 1:200 (A1)" → 200, "2:1" → 0.5). null nếu không đọc được.</summary>
        public static double? ParseScale(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = ScalePattern.Match(text);
            if (!m.Success) return null;
            if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double paper)
                || !double.TryParse(m.Groups[2].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double real)
                || paper <= 0 || real <= 0) return null;
            return real / paper;
        }

        /// <summary>Độ dài thật (mm) của đoạn dài <paramref name="points"/> point trên giấy ở tỷ lệ 1:<paramref name="denominator"/>.</summary>
        public static double RealLengthMm(double points, double denominator) => points * 25.4 / 72 * denominator;

        /// <summary>Diện tích thật (m²) của vùng <paramref name="squarePoints"/> point² trên giấy ở tỷ lệ 1:<paramref name="denominator"/>.</summary>
        public static double RealAreaSquareMeters(double squarePoints, double denominator)
        {
            double mmPerPoint = 25.4 / 72 * denominator;
            return squarePoints * mmPerPoint * mmPerPoint / 1e6;
        }

        /// <summary>"12.35 m" từ 1 235 mm; dưới 1 m hiện mm ("850 mm"); trên 1 km hiện km.</summary>
        public static string FormatLength(double mm)
        {
            if (mm >= 1_000_000) return (mm / 1_000_000).ToString("0.###", CultureInfo.InvariantCulture) + " km";
            if (mm >= 1000) return (mm / 1000).ToString("0.###", CultureInfo.InvariantCulture) + " m";
            return mm.ToString("0.#", CultureInfo.InvariantCulture) + " mm";
        }

        public static string FormatArea(double squareMeters)
            => squareMeters >= 10_000 ? (squareMeters / 10_000).ToString("0.###", CultureInfo.InvariantCulture) + " ha" : squareMeters.ToString("0.###", CultureInfo.InvariantCulture) + " m²";
    }
}
