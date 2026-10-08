using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace XTCapture
{
    /// <summary>The Reader's look: light / dark (its own choice, or Windows') and its accent colour, read from the Reader's settings so both programs match.</summary>
    internal static class CaptureTheme
    {
        private static readonly (string Name, string Light, string Dark)[] Accents =
        {
            ("Orange", "#E8590C", "#FF8A4C"), ("Blue", "#2563EB", "#6EA8FF"), ("Green", "#0F8B6D", "#3FCFA6"), ("Purple", "#7C4DFF", "#B49BFF"), ("Red", "#D9463B", "#FF7A6E")
        };

        private sealed class AccentDictionary : ResourceDictionary { }

        public static bool IsDark { get; private set; }

        public static void Apply()
        {
            string theme = CaptureSettings.ReaderTheme;
            Apply(theme == "Dark" || (theme == "System" && SystemPrefersDark()), CaptureSettings.ReaderAccent);
        }

        public static void Apply(bool dark, string accentName)
        {
            if (Application.Current == null) return;
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            for (int i = dictionaries.Count - 1; i >= 0; i--)
            {
                string source = dictionaries[i].Source?.OriginalString ?? string.Empty;
                if (dictionaries[i] is AccentDictionary || source.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) || source.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase))
                    dictionaries.RemoveAt(i);
            }
            dictionaries.Add(new ResourceDictionary { Source = new Uri(dark ? "pack://application:,,,/XTStyle;component/Themes/Dark.xaml" : "pack://application:,,,/XTStyle;component/Themes/Light.xaml", UriKind.Absolute) });
            dictionaries.Add(new ResourceDictionary { Source = new Uri(dark ? "pack://application:,,,/XTCapture;component/Resources/UiTokens.Dark.xaml" : "pack://application:,,,/XTCapture;component/Resources/UiTokens.Light.xaml", UriKind.Absolute) });
            IsDark = dark;
            var entry = Accents.FirstOrDefault(a => a.Name == accentName, Accents[0]);
            var accent = (Color)ColorConverter.ConvertFromString(dark ? entry.Dark : entry.Light);
            var brush = new SolidColorBrush(accent);
            brush.Freeze();
            var d = new AccentDictionary();
            d["Ui.Accent"] = brush;
            d["XTSelectionBlue"] = brush;
            d["XTPrimaryButtonBackground"] = brush;
            dictionaries.Add(d);
        }

        private static bool SystemPrefersDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch { return false; }
        }
    }
}
