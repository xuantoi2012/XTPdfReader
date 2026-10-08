using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services.Capture;
using XTStyle.Controls;

namespace XTPdfMergeApp
{
    /// <summary>
    /// Screen capture: the screen is frozen and dimmed (<see cref="CaptureOverlayWindow"/>), the user picks a window or an area, and the picture is
    /// kept as a one-page PDF in the Captures store (<see cref="CaptureLibrary"/>). Copy puts it on the clipboard; Edit opens it in a tab so text,
    /// notes and shapes can be added, now or later.
    /// </summary>
    public partial class ReaderWindow
    {
        private bool _capturing;

        private void ReaderCapture_Click(object sender, RoutedEventArgs e) => _ = StartCaptureAsync();
        private void ReaderCaptures_Click(object sender, RoutedEventArgs e) => ShowCaptureLibrary();

        private void ShowCaptureLibrary()
            => CaptureLibraryWindow.ShowFor(this, OpenCapture,
                () => _ = StartCaptureAsync(),
                () => _groups.Select(g => g.SourcePath).ToList());

        private void OpenCapture(string path)
        {
            if (EditHost == null) return;
            ShowStart(false);
            _ = EditHost.OpenPathsAsync(new[] { path });
        }

        internal async Task StartCaptureAsync()
        {
            if (_capturing) return;
            _capturing = true;
            try
            {
                // Let the button (or the Captures window that was hidden) repaint before the screen is frozen.
                await Task.Delay(250);
                var snapshot = ScreenGrabber.Snapshot();
                var overlay = new CaptureOverlayWindow(snapshot, VisualTreeHelper.GetDpi(this).DpiScaleX);
                overlay.ShowDialog();
                if (overlay.Outcome is { } outcome) await HandleCaptureAsync(outcome);
            }
            catch (Exception ex)
            {
                XTGrowl.Info("The capture failed: " + ex.Message, this);
            }
            finally
            {
                _capturing = false;
                Activate();
            }
        }

        /// <summary>Stores the picture; Copy also puts it on the clipboard, Edit opens it in a tab.</summary>
        internal async Task<string?> HandleCaptureAsync(CaptureOutcome outcome)
        {
            string path;
            try { path = await Task.Run(() => CaptureLibrary.Save(outcome.Image)); }
            catch (Exception ex)
            {
                XTGrowl.Info("Could not save the capture: " + ex.Message, this);
                return null;
            }
            if (outcome.Action == CaptureAction.Copy)
            {
                try
                {
                    CaptureClipboard.Copy(outcome.Image);
                    XTGrowl.Success("Copied. Kept in Captures.", this);
                }
                catch (Exception ex) { XTGrowl.Info("Saved in Captures, but the clipboard is busy: " + ex.Message, this); }
            }
            else OpenCapture(path);
            return path;
        }
    }
}
