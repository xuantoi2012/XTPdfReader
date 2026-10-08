using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace XTCapture
{
    /// <summary>The few line icons of the capture bars (24 x 24 grid, drawn as strokes so they follow the text colour).</summary>
    internal static class CaptureIcons
    {
        public const string Capture = "M3,8 V3 H8 M16,3 H21 V8 M21,16 V21 H16 M8,21 H3 V16 M12,9.5 A2.5,2.5 0 1 0 12.01,9.5 Z";
        public const string Trash = "M4,7 H20 M9,7 V4 H15 V7 M6,7 L7,20 H17 L18,7 M10,11 V16 M14,11 V16";
        public const string Window ="M3,5 H21 V19 H3 Z M3,9 H21";
        public const string Rectangle = "M4,6 H20 V18 H4 Z";
        public const string Circle = "M12,4 A8,8 0 1 0 12.01,4 Z";
        public const string Polygon = "M5,18 L3,8 L12,3 L21,9 L17,19 Z";
        public const string Close = "M5,5 L19,19 M19,5 L5,19";
        public const string Copy = "M9,9 H20 V20 H9 Z M5,15 V4 H16";
        public const string Store = "M4,5 H20 V19 H4 Z M4,14 H9 L10.5,16.5 H13.5 L15,14 H20";
        public const string Pdf = "M6,3 H14 L19,8 V21 H6 Z M14,3 V8 H19";
        public const string Settings = "M4,7 H20 M4,12 H20 M4,17 H20 M9,5 V9 M15,10 V14 M8,15 V19";
        public const string Save = "M5,4 H16 L20,8 V20 H5 Z M8,4 V9 H15 V4 M8,20 V13 H17 V20";

        /// <summary>An icon as a 16 x 16 element drawn with <paramref name="brush"/>.</summary>
        public static FrameworkElement Create(string data, Brush brush, double size = 16)
        {
            var path = new Path
            {
                Data = Geometry.Parse(data), Stroke = brush, StrokeThickness = 1.7, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Fill = null, IsHitTestVisible = false
            };
            return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { path } }, IsHitTestVisible = false };
        }
    }
}
