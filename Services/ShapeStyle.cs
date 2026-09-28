using System;
using System.Globalization;
using System.Linq;

namespace XTPdfMergeApp.Services
{
    /// <summary>Drawing comment shapes (Foxit-style): rectangle, cloud (review revision cloud), oval, arrow, line. Encoded into the annotation as /XTShape.</summary>
    public sealed record ShapeStyle(string Type, string Color, double Width, int Corner)
    {
        public const string Rect = "Rect", Oval = "Oval", Cloud = "Cloud", Arrow = "Arrow", Line = "Line";
        public static readonly string[] Types = { Rect, Cloud, Oval, Arrow, Line };
        public static readonly double[] Widths = { 1, 2, 3, 4, 6 };

        public static ShapeStyle Default { get; } = new(Rect, "#C0392B", 2, 0);

        public bool IsLine => Type is Arrow or Line;

        public string Encode() => string.Join("|", Type, Color, Width.ToString("0.##", CultureInfo.InvariantCulture), Corner.ToString(CultureInfo.InvariantCulture));

        public static ShapeStyle Decode(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Default;
            var parts = text.Split('|');
            if (parts.Length < 4 || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double width) || !int.TryParse(parts[3], out int corner)) return Default;
            string type = Types.FirstOrDefault(t => t.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) ?? Rect;
            return new ShapeStyle(type, parts[1].StartsWith('#') ? parts[1] : Default.Color, Math.Clamp(width, 0.5, 20), Math.Clamp(corner, 0, 3));
        }

        /// <summary>Points added around a line's bounding box so that its ends and the arrow head are not clipped by the annotation rectangle.</summary>
        public static double LinePad(double effectiveWidth) => Math.Max(8, 4 * effectiveWidth);

        /// <summary>Page-size factor so shapes stay visible on A1/A0 drawings (same idea as stamps).</summary>
        public static double PageScale(double displayWidthPoints) => Math.Clamp(displayWidthPoints / 842.0, 1.0, 4.0);
    }
}
