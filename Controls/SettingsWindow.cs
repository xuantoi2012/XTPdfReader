using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>Settings in its own window (there are only a few options, so a pane is not needed). Changes apply immediately.</summary>
internal sealed class SettingsWindow : XTWindow
{
    public SettingsWindow(Func<Task> clearCache)
    {
        Title = "Settings";
        TitleBarMode = TitleBarMode.Tool;
        Width = 720;
        Height = 620;
        ResizeMode = ResizeMode.NoResize; // fixed size: one scrolling page
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        var page = new SettingsPage { ClearCacheRequested = clearCache };
        page.Reload();
        Content = page;
        Background = (Brush)Application.Current.FindResource("Ui.Bg");
    }
}
