using System;
using System.Globalization;
using System.Linq;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// Look of a callout (Foxit style: a text box with an arrow to the point it comments on): colour/width of the leader line, arrow head
    /// (0 none, 1 open, 2 closed), box fill and box border ("-" = none). Stored after the tip point in the callout's /XTFormat, so callouts saved by
    /// older builds (no style) read back with <see cref="Legacy"/> = the old look without an arrow head.
    /// </summary>
    public sealed record CalloutStyle(string LineColor, double LineWidth, int Arrow, string Fill, string Border, double BorderWidth)
    {
        public const string None = "-";
        public static readonly double[] Widths = { 1, 1.5, 2, 3, 4, 6 };
        public static readonly string[] FillColors = { "#EBF3F5", "#FFF6C4", "#FFFFFF", "#DCEBFF", "#E4F5E1", "#FFE1E1" };

        /// <summary>The look of a new callout.</summary>
        public static CalloutStyle Default { get; } = new("#759DB8", 1.2, 1, "#EBF3F5", "#759DB8", 1.2);

        /// <summary>The look of callouts saved before styles existed.</summary>
        public static CalloutStyle Legacy { get; } = Default with { Arrow = 0 };

        public string Encode() => string.Join(":", LineColor, LineWidth.ToString("0.##", CultureInfo.InvariantCulture), Arrow.ToString(CultureInfo.InvariantCulture),
            Fill, Border, BorderWidth.ToString("0.##", CultureInfo.InvariantCulture));

        public static CalloutStyle Decode(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Legacy;
            var p = text.Split(':');
            if (p.Length < 6 || !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lw) ||
                !int.TryParse(p[2], out int arrow) || !double.TryParse(p[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double bw)) return Legacy;
            string Color(string c, string fallback) => c == None || c.StartsWith('#') ? c : fallback;
            return new CalloutStyle(Color(p[0], Default.LineColor) == None ? Default.LineColor : Color(p[0], Default.LineColor), Math.Clamp(lw, 0.25, 20), Math.Clamp(arrow, 0, 2),
                Color(p[3], Default.Fill), Color(p[4], Default.Border), Math.Clamp(bw, 0.25, 20));
        }
    }
}
