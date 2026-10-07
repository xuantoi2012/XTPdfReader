using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    static void TestPerformanceSettings()
    {
        if (Application.Current == null) CreateReaderTestApplication();
        var page = new SettingsPage(); page.Reload();
        var box = (ComboBox)page.FindName("PerformanceModeBox");
        Check(box.Items.Count == 3 && box.SelectedIndex == (int)AppSettings.PerformanceMode, "Settings restores the persisted performance selection");
        Check(box.Items.Cast<ComboBoxItem>().Select(i => (string)i.Tag).SequenceEqual(new[] { "MemorySaving", "Balance", "Maximum" }), "Settings offers the three runtime profiles");
        Check(!string.IsNullOrWhiteSpace(((TextBlock)page.FindName("PerformanceModeHint")).Text), "Settings explains the performance/memory tradeoff");
        var host = new Window { Content = page, Width = 940, Height = 560, ShowActivated = false, ShowInTaskbar = false,
            Left = -32000, Top = -32000, WindowStyle = WindowStyle.None };
        try
        {
            host.Show();
            host.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(100));
            var bitmap = new RenderTargetBitmap(940, 560, 96, 96, PixelFormats.Pbgra32); bitmap.Render(page);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(Output, "performance-settings.png")); encoder.Save(output);
            Check(box.ActualWidth > 0 && box.ActualHeight > 0, "Performance selector is visible in the settings layout");
        }
        finally { host.Close(); }
        // No selection is changed: this test never overwrites the user's registry preference.
    }
}
