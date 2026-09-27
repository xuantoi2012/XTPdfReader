using System;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// Quy đổi giữa toạ độ TRANG ĐANG HIỂN THỊ (chuẩn hoá 0..1, gốc trên-trái, đúng như bitmap PDFium
    /// render ra — tức CropBox đã xoay theo /Rotate) và toạ độ user space của PDF (point, gốc dưới-trái,
    /// chưa xoay). Toán thuần, không phụ thuộc iText/WPF.
    /// </summary>
    public readonly record struct PdfPageGeometry(double CropX, double CropY, double CropWidth, double CropHeight, int Rotation)
    {
        /// <summary>Kích thước trang như người dùng nhìn thấy (point) — đã đổi chiều nếu xoay 90/270.</summary>
        public double DisplayWidth => Rotation is 90 or 270 ? CropHeight : CropWidth;
        public double DisplayHeight => Rotation is 90 or 270 ? CropWidth : CropHeight;

        /// <summary>(u, v) chuẩn hoá trên trang hiển thị → điểm user space.</summary>
        public (double X, double Y) DisplayToUser(double u, double v)
        {
            // Trang hiển thị = trang gốc xoay THUẬN chiều kim đồng hồ Rotation độ; lùi lại về toạ độ
            // chuẩn hoá của trang gốc (du, dv — vẫn gốc trên-trái).
            (double du, double dv) = Rotation switch
            {
                90 => (v, 1 - u),
                180 => (1 - u, 1 - v),
                270 => (1 - v, u),
                _ => (u, v)
            };
            return (CropX + du * CropWidth, CropY + (1 - dv) * CropHeight);
        }

        /// <summary>Điểm user space → (u, v) chuẩn hoá trên trang hiển thị.</summary>
        public (double U, double V) UserToDisplay(double x, double y)
        {
            double du = CropWidth > 0 ? (x - CropX) / CropWidth : 0;
            double dv = CropHeight > 0 ? 1 - (y - CropY) / CropHeight : 0;
            return Rotation switch
            {
                90 => (1 - dv, du),
                180 => (1 - du, 1 - dv),
                270 => (dv, 1 - du),
                _ => (du, dv)
            };
        }

        /// <summary>Hình chữ nhật chuẩn hoá trên trang hiển thị (2 góc bất kỳ) → hình chữ nhật user space
        /// (Left, Bottom, Right, Top).</summary>
        public (double Left, double Bottom, double Right, double Top) DisplayRectToUser(double u1, double v1, double u2, double v2)
        {
            var a = DisplayToUser(u1, v1);
            var b = DisplayToUser(u2, v2);
            return (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        /// <summary>Hình chữ nhật user space → (U1, V1) trên-trái, (U2, V2) dưới-phải trên trang hiển thị.</summary>
        public (double U1, double V1, double U2, double V2) UserRectToDisplay(double left, double bottom, double right, double top)
        {
            var a = UserToDisplay(left, bottom);
            var b = UserToDisplay(right, top);
            return (Math.Min(a.U, b.U), Math.Min(a.V, b.V), Math.Max(a.U, b.U), Math.Max(a.V, b.V));
        }
    }
}
