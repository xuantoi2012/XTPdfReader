using System;
using System.Globalization;
using System.Linq;

namespace XTPdfMergeApp.Services
{
    /// <summary>Drawing comment shapes (Foxit-style): rectangle, cloud (review revision cloud), oval, arrow, line. Encoded into the annotation as /XTShape.</summary>
    public sealed record ShapeStyle(string Type, string Color, double Width, int Corner, int Dash = 0)
    {
        public const string Rect = "Rect", Oval = "Oval", Cloud = "Cloud", Arrow = "Arrow", Line = "Line";
        public static readonly string[] Types = { Rect, Cloud, Oval, Arrow, Line };
        public static readonly double[] Widths = { 1, 2, 3, 4, 6 };
        public const int Solid = 0, Dashed = 1, Dotted = 2;
        public static readonly string[] DashNames = { "Solid", "Dashed", "Dotted" };

        public static ShapeStyle Default { get; } = new(Rect, "#C0392B", 2, 0);

        public bool IsLine => Type is Arrow or Line;

        /// <summary>The fifth field (dash) is written only when not solid, so styles of old files and solid shapes keep their old text.</summary>
        public string Encode()
        {
            string text = string.Join("|", Type, Color, Width.ToString("0.##", CultureInfo.InvariantCulture), Corner.ToString(CultureInfo.InvariantCulture));
            return Dash == Solid ? text : text + "|" + Dash.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The PDF dash array (user-space units) for a line of <paramref name="width"/>; empty for solid. Dots use a round cap, so the dot is the gap's mirror.</summary>
        public float[] DashPattern(double width) => Dash switch
        {
            Dashed => new[] { (float)(width * 4), (float)(width * 2.5) },
            Dotted => new[] { 0.01f, (float)(width * 2) },
            _ => Array.Empty<float>()
        };

        public static ShapeStyle Decode(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Default;
            var parts = text.Split('|');
            if (parts.Length < 4 || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double width) || !int.TryParse(parts[3], out int corner)) return Default;
            string type = Types.FirstOrDefault(t => t.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) ?? Rect;
            int dash = parts.Length > 4 && int.TryParse(parts[4], out int d) ? Math.Clamp(d, Solid, Dotted) : Solid;
            return new ShapeStyle(type, parts[1].StartsWith('#') ? parts[1] : Default.Color, Math.Clamp(width, 0.5, 20), Math.Clamp(corner, 0, 3), dash);
        }

        /// <summary>Points added around a line's bounding box so that its ends and the arrow head are not clipped by the annotation rectangle.</summary>
        public static double LinePad(double effectiveWidth) => Math.Max(8, 4 * effectiveWidth);

        /// <summary>Page-size factor so shapes stay visible on A1/A0 drawings (same idea as stamps).</summary>
        public static double PageScale(double displayWidthPoints) => Math.Clamp(displayWidthPoints / 842.0, 1.0, 4.0);
    }
}
