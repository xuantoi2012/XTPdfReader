using System;
using System.Windows;

namespace XTPdfMergeApp.Services
{
    /// <summary>Đổi theme Light/Dark của XTStyle cho cả app (trước đây nằm trong cửa sổ ghép; giờ cửa sổ
    /// đọc là cửa sổ chính nên áp ngay lúc khởi động).</summary>
    internal static class ThemeService
    {
        public static bool IsDark { get; private set; }

        public static void ApplySaved()
            => Apply(string.Equals(MergeAppSettingsStore.GetTheme(), "Dark", StringComparison.OrdinalIgnoreCase));

        public static void Apply(bool darkTheme)
        {
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            for (int i = dictionaries.Count - 1; i >= 0; i--)
            {
                string source = dictionaries[i].Source?.OriginalString ?? string.Empty;
                if (source.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
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
            IsDark = darkTheme;
        }
    }
}
