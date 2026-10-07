using System;
using System.Globalization;
using System.Linq;

namespace XTPdfMergeApp.Services
{
    /// <summary>Font / size / colour / bold / italic / underline / alignment / box width of a Typewriter annotation (like the text box of Edge's PDF viewer).
    /// Stored in the annotation (/XTFormat) so it survives reopening; the last used one is remembered in the settings.
    /// <see cref="Width"/> = 0: the box grows with the text; &gt; 0: fixed width in points and the text wraps inside it.</summary>
    public sealed record TextFormat(string Family, double Size, string Color, bool Bold, bool Italic, bool Underline = false, int Align = 0, double Width = 0)
    {
        public static readonly string[] Families = { "Arial", "Times New Roman", "Tahoma", "Segoe UI", "Calibri", "Verdana", "Courier New" };
        public static readonly double[] Sizes = { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 60, 72 };
        public static readonly string[] Colors = { "#000000", "#C0392B", "#D9640A", "#0F8B6D", "#2563EB", "#7C3AED", "#6B7280" };

        public static TextFormat Default { get; } = new("Arial", 12, "#000000", false, false);

        public string Encode()
        {
            string text = string.Join("|", Family, Size.ToString("0.##", CultureInfo.InvariantCulture), Color, Bold ? "1" : "0", Italic ? "1" : "0");
            // The extra fields are written only when used, so formats saved by older builds read back and compare equal.
            return Underline || Align != 0 || Width > 0
                ? text + "|" + (Underline ? "1" : "0") + "|" + Align.ToString(CultureInfo.InvariantCulture) + "|" + Width.ToString("0.##", CultureInfo.InvariantCulture)
                : text;
        }

        public static TextFormat Decode(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Default;
            var parts = text.Split('|');
            if (parts.Length < 5 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double size)) return Default;
            string family = Families.FirstOrDefault(f => f.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) ?? Default.Family;
            bool underline = parts.Length > 5 && parts[5] == "1";
            int align = parts.Length > 6 && int.TryParse(parts[6], out int a) ? Math.Clamp(a, 0, 2) : 0;
            double width = parts.Length > 7 && double.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out double w) ? Math.Clamp(w, 0, 5000) : 0;
            return new TextFormat(family, Math.Clamp(size, 4, 200), parts[2].StartsWith('#') ? parts[2] : Default.Color, parts[3] == "1", parts[4] == "1", underline, align, width);
        }
    }
}
