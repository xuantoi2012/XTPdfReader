using System.Windows;

namespace XTPdfMergeApp
{
    /// <summary>Small glue for ribbon buttons that call a private method with an argument (XAML Click can't pass one).</summary>
    public partial class ReaderWindow
    {
        private void RibbonExport_Click(object sender, RoutedEventArgs e) => OpenExport(preferFlatten: false);
        private void RibbonSettings_Click(object sender, RoutedEventArgs e) => ShowSettings(true);
    }
}
