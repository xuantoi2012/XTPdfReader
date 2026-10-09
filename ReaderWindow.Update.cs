using System.Windows;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp;

public partial class ReaderWindow
{
    // The "Update" button of the title bar: visible only while AppUpdateService knows of a newer release.

    private void HookUpdateButton()
    {
        AppUpdateService.AvailableChanged += RefreshUpdateButton;
        RefreshUpdateButton();
    }

    private void RefreshUpdateButton()
    {
        var info = AppUpdateService.Available;
        UpdateTitleButton.Visibility = info is null ? Visibility.Collapsed : Visibility.Visible;
        if (info is null) return;
        UpdateTitleButton.ToolTip = Loc.T($"Update available: {info.Version} (you are using {AppInfo.Version}). Click to see it and update.");
    }

    private void UpdateTitle_Click(object sender, RoutedEventArgs e)
    {
        if (AppUpdateService.Available is { } info) UpdateReadyWindow.ShowFor(this, info);
    }
}
