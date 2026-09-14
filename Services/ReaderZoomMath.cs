using System;

namespace XTPdfMergeApp.Services
{
    /// <summary>Toán thuần cho zoom/fit của Reader panel (fit width/page, scale hiển thị) —
    /// không đụng WPF control, tách ra từ MainWindow để dễ đọc/kiểm tra độc lập.</summary>
    public static class ReaderZoomMath
    {
        public static double Clamp(double zoom, double minZoom, double maxZoom)
            => Math.Clamp(zoom, minZoom, maxZoom);

        public static double WheelZoom(double baseZoom, int wheelDelta, double zoomStep, double minZoom, double maxZoom)
        {
            // Chromium-style wheel zoom keeps high-resolution trackpad deltas fractional
            // instead of promoting every tiny delta to a full mouse-wheel notch.
            double wheelSteps = Math.Clamp(wheelDelta / 120.0, -6.0, 6.0);
            return Clamp(baseZoom * Math.Pow(zoomStep, wheelSteps), minZoom, maxZoom);
        }

        /// <summary>Kích thước hiệu dụng của trang theo BASELINE (baseWidth x tỉ lệ trang thật),
        /// đã hoán đổi rộng/cao nếu đang xoay 90/270°.</summary>
        public static (double Width, double Height) EffectivePageSize(double baseWidth, double pageAspect, int rotationDegrees)
        {
            double baseHeight = baseWidth * pageAspect;
            bool swapped = rotationDegrees == 90 || rotationDegrees == 270;
            return swapped ? (baseHeight, baseWidth) : (baseWidth, baseHeight);
        }

        public static double FitWidthZoom(double viewportWidth, double effectiveWidth)
            => viewportWidth / effectiveWidth;

        public static double FitPageZoom(double viewportWidth, double viewportHeight, double effectiveWidth, double effectiveHeight)
            => Math.Min(viewportWidth / effectiveWidth, viewportHeight / effectiveHeight);
    }
}
