using System;
using System.Globalization;
using System.Linq;

namespace XTPdfMergeApp.Services
{
    /// <summary>Font / size / colour / bold / italic of a Typewriter annotation. Stored in the annotation (/XTFormat) so it survives reopening; the last used one is remembered in the settings.</summary>
    public sealed record TextFormat(string Family, double Size, string Color, bool Bold, bool Italic)
    {
        public static readonly string[] Families = { "Arial", "Times New Roman", "Tahoma", "Segoe UI", "Calibri", "Verdana", "Courier New" };
        public static readonly double[] Sizes = { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 60, 72 };
        public static readonly string[] Colors = { "#000000", "#C0392B", "#D9640A", "#0F8B6D", "#2563EB", "#7C3AED", "#6B7280" };

        public static TextFormat Default { get; } = new("Arial", 12, "#000000", false, false);

        public string Encode()
            => string.Join("|", Family, Size.ToString("0.##", CultureInfo.InvariantCulture), Color, Bold ? "1" : "0", Italic ? "1" : "0");

        public static TextFormat Decode(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Default;
            var parts = text.Split('|');
            if (parts.Length < 5 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double size)) return Default;
            string family = Families.FirstOrDefault(f => f.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) ?? Default.Family;
            return new TextFormat(family, Math.Clamp(size, 4, 200), parts[2].StartsWith('#') ? parts[2] : Default.Color, parts[3] == "1", parts[4] == "1");
        }
    }
}
