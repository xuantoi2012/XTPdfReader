using System.Windows;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp
{
    /// <summary>The Capture and Store buttons start XT Capture, the screen-capture program that is installed with the Reader and keeps running in the tray.</summary>
    public partial class ReaderWindow
    {
        private void ReaderCapture_Click(object sender, RoutedEventArgs e) => CaptureLauncher.Run("capture", this);
        private void ReaderCaptures_Click(object sender, RoutedEventArgs e) => CaptureLauncher.Run("store", this);
    }
}
