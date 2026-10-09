using System.Windows;

namespace XTPdfMergeApp
{
    /// <summary>Small glue for ribbon buttons that call a private method with an argument (XAML Click can't pass one).</summary>
    public partial class ReaderWindow
    {
        private void RibbonExport_Click(object sender, RoutedEventArgs e) => OpenExport(preferFlatten: false);
        private void RibbonAbout_Click(object sender, RoutedEventArgs e) => Controls.AboutWindow.ShowFor(this);
        private void RibbonSettings_Click(object sender, RoutedEventArgs e) => ShowSettings(true);

        /// <summary>Gives each ribbon split button its commands (see Controls/RibbonSplit.cs). Called once after the window is built.</summary>
        private void InitRibbonSplits()
        {
            HighlightSplit.Bind("highlight", (ReaderHighlightTextButton, "Highlight"), (ReaderHighlightAreaButton, "Highlight"));
            MarkupSplit.Bind("markup", (ReaderUnderlineToolButton, null), (ReaderStrikethroughToolButton, null), (ReaderSquigglyToolButton, null));
            ShapesSplit.Bind("shapes", (ReaderShapeRectButton, null), (ReaderShapeCloudButton, null), (ReaderShapeOvalButton, null), (ReaderShapeArrowButton, null), (ReaderShapeLineButton, null));
            SignSplit.Bind("sign", (ReaderSignButton, null), (ReaderVerifySignButton, "Check"), (ReaderPageMarksButton, null));
        }
    }
}
