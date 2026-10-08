using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XTCapture
{
    internal enum MarkupKind { Rectangle, Ellipse, Arrow, Line, Pen, Text, Marker, Mosaic }

    /// <summary>
    /// How an object looks. The same choices as the shapes and text of the PDF Reader (colour, width, solid / dashed / dotted, opacity, fill, font),
    /// kept as plain data so a capture can be edited again later. <see cref="Width"/> is the line width in pixels; for a mosaic it is the block size.
    /// </summary>
    internal sealed record MarkupStyle(
        string Color = "#C0392B", double Width = 3, int Dash = 0, int Opacity = 100, string Fill = "",
        string Font = "Segoe UI", double FontSize = 20, bool Bold = false, bool Italic = false, bool Underline = false)
    {
        public const int Solid = 0, Dashed = 1, Dotted = 2;
        public static readonly string[] Colors = { "#C0392B", "#D9640A", "#F2C200", "#0F8B6D", "#2563EB", "#7C3AED", "#000000", "#FFFFFF" };
        public static readonly double[] Widths = { 1, 2, 3, 4, 6, 8 };
        public static readonly int[] Opacities = { 100, 75, 50, 25 };
        public static readonly double[] FontSizes = { 12, 14, 16, 20, 24, 32, 48, 64 };
        public static readonly string[] Fonts = { "Segoe UI", "Arial", "Times New Roman", "Tahoma", "Calibri", "Verdana", "Courier New" };
        public static readonly (string Name, string Hex)[] Fills =
        {
            ("No fill", ""), ("White", "#FFFFFF"), ("Yellow", "#FFF2A8"), ("Red", "#F8C9C4"), ("Green", "#C8E6C9"), ("Blue", "#BBDEFB"), ("Purple", "#E1BEE7"), ("Grey", "#D9D9D9")
        };
        public const double DefaultBlock = 12;

        /// <summary>The dash pattern in line widths (WPF's unit), empty for solid. Dots are round, so the dot is a point and the gap is two widths.</summary>
        public double[] DashPattern() => Dash switch { Dashed => new[] { 4.0, 2.5 }, Dotted => new[] { 0.01, 2.0 }, _ => Array.Empty<double>() };
    }

    /// <summary>
    /// One thing drawn on a capture, in pixels of the picture. A rectangle / circle / mosaic is its two opposite corners (X1,Y1)-(X2,Y2); an arrow / line is its start
    /// and end; a pen stroke is <see cref="Points"/> (x, y, x, y, ...); a text has its top left corner at (X1,Y1); a numbered marker has its centre at (X1,Y1).
    /// </summary>
    internal sealed record MarkupItem(string Id, MarkupKind Kind, double X1, double Y1, double X2, double Y2, MarkupStyle Style, string Text = "", int Number = 0, List<double>? Points = null)
    {
        public static string NewId() => Guid.NewGuid().ToString("N")[..10];

        public bool IsBox => Kind is MarkupKind.Rectangle or MarkupKind.Ellipse or MarkupKind.Mosaic;
        public bool IsLine => Kind is MarkupKind.Arrow or MarkupKind.Line;

        /// <summary>The item moved by (dx, dy).</summary>
        public MarkupItem Translate(double dx, double dy)
            => this with { X1 = X1 + dx, Y1 = Y1 + dy, X2 = X2 + dx, Y2 = Y2 + dy, Points = Points?.Select((v, i) => v + (i % 2 == 0 ? dx : dy)).ToList() };
    }

    /// <summary>The contents of <c>markup.json</c>: everything drawn on one capture, bottom first.</summary>
    internal sealed class MarkupDocument
    {
        public int V { get; set; } = 1;
        public List<MarkupItem> Items { get; set; } = new();

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public string ToJson() => JsonSerializer.Serialize(this, Options);

        /// <summary>The document in <paramref name="json"/>; an empty one when the text is missing or unreadable (a damaged markup file never blocks the picture).</summary>
        public static MarkupDocument FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new MarkupDocument();
            try { return JsonSerializer.Deserialize<MarkupDocument>(json, Options) ?? new MarkupDocument(); }
            catch (JsonException) { return new MarkupDocument(); }
        }
    }
}
