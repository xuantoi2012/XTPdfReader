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

        /// <summary>
        /// Tăng tốc zoom khi lăn con lăn liên tục (Foxit làm vậy: đo 04/10 với 40 nấc cách nhau ~31 ms thì sau 9 nấc Foxit đã ×5,1 còn Reader
        /// chỉ ×2,0, vì Reader luôn ×1,08 mỗi nấc); từ 04/10 chủ ý nhanh hơn Foxit khoảng 1,5 lần theo yêu cầu người dùng. Một lượt lăn = các nấc cách nhau dưới <see cref="RunGapMilliseconds"/> và cùng chiều.
        /// Hai nấc đầu giữ ×1,08 (nấc đơn lẻ vẫn chính xác), sau đó mỗi nấc "nặng" thêm <see cref="RampPerNotch"/> nấc-tương-đương tới
        /// <see cref="MaxMultiplier"/>. Lăn mịn (bàn di chuột) cộng dồn theo phần nấc nên vẫn liên tục.
        /// </summary>
        public sealed class WheelZoomAccelerator
        {
            public const double RunGapMilliseconds = 160;
            public const double FreeNotches = 2;
            public const double RampPerNotch = 0.8;
            public const double MaxMultiplier = 7.0;

            private double _run, _lastMs = double.NegativeInfinity;
            private int _direction;

            /// <summary>Hệ số nhân cho nấc vừa tới: <paramref name="notches"/> = |Delta|/120, <paramref name="direction"/> = dấu của Delta.</summary>
            public double Next(double notches, int direction, double nowMs)
            {
                if (nowMs - _lastMs > RunGapMilliseconds || direction != _direction) _run = 0;
                _lastMs = nowMs; _direction = direction;
                _run += Math.Max(0, notches);
                return _run <= FreeNotches ? 1.0 : Math.Min(MaxMultiplier, 1.0 + RampPerNotch * (_run - FreeNotches));
            }
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
