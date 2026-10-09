using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// Phosphor icon variants (Resources/UiIcons.xaml): every Ui.Icon.X geometry is one shared frozen instance, so it can be mapped by
    /// reference to its duotone layer (Ui.Icon.X.Tint) and its bold weight (Ui.Icon.X.Bold). Built once, on first use.
    /// </summary>
    internal static class UiIconVariants
    {
        private static Dictionary<Geometry, Geometry>? _tint, _bold, _accent;

        /// <summary>
        /// The small part of an icon that is always drawn in the accent colour (null array = the whole icon). The numbers are the figures of the
        /// icon geometry (see Tests --icon-dump): e.g. the two inner lines of Note, the four corners of Capture, the + of Insert.
        /// Icons not listed here have no accent part.
        /// </summary>
        /// <summary>Icons without any colour layer: they stay one colour.</summary>
        private static readonly HashSet<string> NoSoftTint = new(StringComparer.Ordinal) { "actual", "rotl", "rotr" };

        private static readonly Dictionary<string, int[]?> AccentFigures = new(StringComparer.Ordinal)
        {
            ["select"] = null,
            ["capture"] = new[] { 0, 1, 2, 3 },
            ["captures"] = new[] { 2, 3 },
            ["note"] = new[] { 0, 1 },
            ["callout"] = new[] { 0, 1 },
            ["shape_rect"] = null, ["shape_oval"] = null, ["shape_cloud"] = null, ["shape_arrow"] = null, ["shape_line"] = null,
            ["insert"] = new[] { 3 },
            ["trash"] = new[] { 0, 1, 2 },
            ["pagerotl"] = new[] { 1 }, ["pagerotr"] = new[] { 1 },
            ["extract"] = new[] { 3 },
            ["eraser"] = null,
        };

        /// <summary>Below this size (px) an icon is drawn with the bold weight when there is one, so thin lines stay visible.</summary>
        public const double BoldBelow = 15;

        private static void Build()
        {
            if (_tint != null) return;
            var byKey = new Dictionary<string, Geometry>(StringComparer.Ordinal);
            void Scan(ResourceDictionary dictionary)
            {
                foreach (var nested in dictionary.MergedDictionaries) Scan(nested);
                foreach (var key in dictionary.Keys)
                    if (key is string name && name.StartsWith("Ui.Icon.", StringComparison.Ordinal) && dictionary[name] is Geometry geometry)
                        byKey[name] = geometry;
            }
            if (Application.Current != null) Scan(Application.Current.Resources);

            _tint = new Dictionary<Geometry, Geometry>(ReferenceEqualityComparer.Instance);
            _bold = new Dictionary<Geometry, Geometry>(ReferenceEqualityComparer.Instance);
            _accent = new Dictionary<Geometry, Geometry>(ReferenceEqualityComparer.Instance);
            foreach (var (name, figures) in AccentFigures)
            {
                if (!byKey.TryGetValue("Ui.Icon." + name, out var icon)) continue;
                var source = PathGeometry.CreateFromGeometry(icon);
                var part = new PathGeometry { FillRule = source.FillRule };
                for (int i = 0; i < source.Figures.Count; i++)
                    if (figures == null || Array.IndexOf(figures, i) >= 0) part.Figures.Add(source.Figures[i].Clone());
                part.Freeze();
                _accent[icon] = part;
            }
            foreach (var (name, geometry) in byKey)
            {
                string shortName = name.StartsWith("Ui.Icon.", StringComparison.Ordinal) ? name["Ui.Icon.".Length..] : name;
                // The icons with an accent part of their own (AccentFigures) and the ones that stay plain (NoSoftTint) do not get the soft body tint.
                if (!AccentFigures.ContainsKey(shortName) && !NoSoftTint.Contains(shortName) && byKey.TryGetValue(name + ".Tint", out var tint)) _tint[geometry] = tint;
                if (byKey.TryGetValue(name + ".Bold", out var bold)) _bold[geometry] = bold;
            }
        }

        public static Geometry? Tint(Geometry? icon)
        {
            if (icon == null) return null;
            Build();
            return _tint!.TryGetValue(icon, out var tint) ? tint : null;
        }

        /// <summary>The part of the icon drawn in the accent colour all the time (null when it has none).</summary>
        public static Geometry? Accent(Geometry? icon)
        {
            if (icon == null) return null;
            Build();
            return _accent!.TryGetValue(icon, out var part) ? part : null;
        }

        public static Geometry? ForSize(Geometry? icon, double size)
        {
            if (icon == null || size >= BoldBelow) return icon;
            Build();
            return _bold!.TryGetValue(icon, out var bold) ? bold : icon;
        }
    }

    /// <summary>Icon geometry → its duotone layer (null when the icon has none).</summary>
    public sealed class UiIconTintConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => UiIconVariants.Tint(value as Geometry);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>Icon geometry → its accent part (null when it has none).</summary>
    public sealed class UiIconAccentConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => UiIconVariants.Accent(value as Geometry);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>(icon geometry, size in px) → bold weight for small sizes. With one value, ConverterParameter is the size.</summary>
    public sealed class UiIconWeightConverter : IMultiValueConverter, IValueConverter
    {
        public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            => UiIconVariants.ForSize(values.Length > 0 ? values[0] as Geometry : null, values.Length > 1 && values[1] is double size ? size : 24);

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => UiIconVariants.ForSize(value as Geometry,
                double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out double size) ? size : 24);

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
