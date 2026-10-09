using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// Áp giao diện: nền sáng/tối (XTStyle + token của app, Resources/UiTokens.*.xaml) rồi màu nhấn của người dùng ghi đè các brush accent.
    /// Mọi control dùng DynamicResource nên đổi ngay khi đang chạy.
    /// </summary>
    internal static class ThemeService
    {
        public static bool IsDark { get; private set; }

        /// <summary>Màu nhấn: (sáng, tối, nền nút chính khi tối). Nền nút chính khi sáng = màu sáng.</summary>
        private static readonly (string Name, string Light, string Dark, string DarkButton)[] Accents =
        {
            ("Orange", "#E8590C", "#FF8A4C", "#E8590C"),
            ("Blue",   "#2563EB", "#6EA8FF", "#3D7BEA"),
            ("Green",  "#0F8B6D", "#3FCFA6", "#1F9E7F"),
            ("Purple", "#7C4DFF", "#B49BFF", "#6A4FE0"),
            ("Red",    "#D9463B", "#FF7A6E", "#D24A3F"),
        };

        public static string AccentSwatch(string name)
        {
            foreach (var a in Accents)
                if (a.Name == name) return a.Light;
            return Accents[0].Light;
        }

        private sealed class AccentDictionary : ResourceDictionary { }

        private static bool _watchingSystem;

        /// <summary>Đọc cài đặt (Theme, Accent) và áp dụng; gọi lúc khởi động và mỗi khi người dùng đổi cài đặt giao diện.</summary>
        public static void ApplySaved()
        {
            string theme = AppSettings.Theme;
            bool dark = theme == "Dark" || (theme == "System" && AppSettings.SystemPrefersDark());
            Apply(dark);
            WatchSystemTheme(theme == "System");
        }

        public static void Apply(bool darkTheme)
        {
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            for (int i = dictionaries.Count - 1; i >= 0; i--)
            {
                string source = dictionaries[i].Source?.OriginalString ?? string.Empty;
                if (dictionaries[i] is AccentDictionary ||
                    source.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                    source.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase))
                    dictionaries.RemoveAt(i);
            }

            dictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(darkTheme
                    ? "pack://application:,,,/XTStyle;component/Themes/Dark.xaml"
                    : "pack://application:,,,/XTStyle;component/Themes/Light.xaml",
                    UriKind.Absolute)
            });
            // Token của thiết kế mới (Resources/UiTokens.*.xaml) gộp SAU dictionary của XTStyle: ghi đè brush XT* và thêm brush Ui.*.
            dictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(darkTheme
                    ? "pack://application:,,,/XTPdfMergeApp;component/Resources/UiTokens.Dark.xaml"
                    : "pack://application:,,,/XTPdfMergeApp;component/Resources/UiTokens.Light.xaml",
                    UriKind.Absolute)
            });
            IsDark = darkTheme;
            dictionaries.Add(BuildAccent(AppSettings.Accent, darkTheme));
        }

        // ── Màu nhấn ──────────────────────────────────────────────────

        private static ResourceDictionary BuildAccent(string name, bool dark)
        {
            var entry = Accents[0];
            foreach (var a in Accents)
                if (a.Name == name) entry = a;

            Color accent = Parse(dark ? entry.Dark : entry.Light);
            Color button = Parse(dark ? entry.DarkButton : entry.Light);
            Color surface = dark ? Parse("#20242B") : Colors.White;
            Color soft = dark ? Blend(accent, surface, 0.28) : Blend(accent, Colors.White, 0.12);
            Color buttonHover = dark ? Blend(button, Colors.White, 0.85) : Blend(button, Colors.Black, 0.86);
            Color hoverAccent = dark ? Blend(accent, Colors.White, 0.8) : Blend(accent, Colors.White, 0.85);

            var d = new AccentDictionary();
            void Put(string key, Color c) => d[key] = Frozen(c);
            Put("Ui.Accent", accent);
            Put("Ui.Brand", accent);
            Put("Ui.AccentSoft", soft);
            Put("XTSelectionBlue", accent);
            Put("XTSelectionBackground", soft);
            Put("XTHoverBlue", hoverAccent);
            Put("XTPrimaryButtonBackground", button);
            Put("XTPrimaryButtonHoverBackground", buttonHover);
            return d;
        }

        private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        /// <summary>a·t + b·(1−t) theo từng kênh.</summary>
        private static Color Blend(Color a, Color b, double t)
            => Color.FromRgb((byte)Math.Round(a.R * t + b.R * (1 - t)), (byte)Math.Round(a.G * t + b.G * (1 - t)), (byte)Math.Round(a.B * t + b.B * (1 - t)));

        private static SolidColorBrush Frozen(Color c)
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }

        // ── Theo giao diện Windows ────────────────────────────────────

        private static void WatchSystemTheme(bool watch)
        {
            if (watch == _watchingSystem) return;
            _watchingSystem = watch;
            if (watch) SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
            else SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        }

        private static void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General) return;
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (AppSettings.Theme == "System") ApplySaved();
            }));
        }
    }
}
