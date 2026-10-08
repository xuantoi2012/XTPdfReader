using System;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// A newer version exists. "Cập nhật ngay" downloads it (with a progress bar) and starts the setup; the app exits. "Để sau" just closes:
/// nothing was downloaded, and the next start offers the update again.
/// </summary>
public partial class UpdateReadyWindow : Window
{
    private readonly UpdateInfo _info;
    private readonly CancellationTokenSource _cancel = new();
    private bool _downloading;

    internal UpdateReadyWindow(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;
        Icon = (ImageSource)FindResource("App.Icon.Logo");
        VersionLine.Text = $"Phiên bản {info.Version} (bạn đang dùng {AppInfo.Version}) · {FormatSize(info.Size)}";
        string notes = info.Notes.Replace("\r", "").Trim();
        int sha = notes.IndexOf("SHA-256:", StringComparison.OrdinalIgnoreCase); // the release body ends with the file hash; users do not need it here
        if (sha >= 0) notes = notes[..sha].Trim();
        NotesText.Text = notes.Length > 0 ? notes : "Cải tiến và sửa lỗi.";
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !_downloading) Close(); };
        Closed += (_, _) => _cancel.Cancel();
    }

    private static string FormatSize(long bytes) => bytes <= 0 ? "" : $"{bytes / 1048576.0:0.#} MB";

    private void Dismiss_Click(object sender, RoutedEventArgs e) => Close();

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading) return;
        _downloading = true;
        UpdateButton.IsEnabled = false;
        CloseX.IsEnabled = false;
        LaterButton.Content = "Hủy";
        ErrorText.Visibility = Visibility.Collapsed;
        ProgressArea.Visibility = Visibility.Visible;
        NotesCard.Visibility = Visibility.Collapsed;
        HintText.Text = "Đang tải bản cập nhật…";
        var progress = new Progress<double>(p =>
        {
            ProgressFill.Width = Math.Max(0, (ProgressArea.ActualWidth - 2) * p);
            ProgressText.Text = $"Đang tải… {(int)Math.Round(p * 100)}%";
        });
        try
        {
            string setup = await AppUpdateService.DownloadAsync(_info, progress, _cancel.Token);
            ProgressText.Text = "Đang mở trình cài đặt…";
            if (AppUpdateService.StartSetup(setup))
            {
                Close();
                Application.Current.Shutdown();
                return;
            }
            Fail("Bạn đã từ chối quyền quản trị, nên chưa cập nhật. Bấm “Cập nhật ngay” để thử lại.");
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Fail("Không tải được bản cập nhật: " + ex.Message); }
    }

    private void Fail(string message)
    {
        _downloading = false;
        UpdateButton.IsEnabled = true;
        CloseX.IsEnabled = true;
        LaterButton.Content = "Để sau";
        ProgressArea.Visibility = Visibility.Collapsed;
        NotesCard.Visibility = Visibility.Visible;
        HintText.Text = "~15 giây · cần quyền quản trị";
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static UpdateReadyWindow? _open;

    /// <summary>Shows the offer (one window at a time).</summary>
    internal static void ShowFor(Window owner, UpdateInfo info)
    {
        if (!owner.IsVisible || Application.Current.Dispatcher.HasShutdownStarted) return;
        if (_open is { IsVisible: true }) { _open.Activate(); return; }
        _open = new UpdateReadyWindow(info) { Owner = owner };
        _open.Show();
    }
}
