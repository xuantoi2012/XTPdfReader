using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>The Merge screen in its own window (mockup 6): one child window per open file, layouts, dock, merge. Closing only hides it; the state is kept until the app exits.</summary>
internal sealed class MergeWindow : XTWindow
{
    private bool _reallyClose;

    public MergeView View { get; } = new();

    public MergeWindow()
    {
        Title = "PDF Reader Pro — Merge files";
        TitleBarMode = TitleBarMode.Full;
        Width = 1240;
        Height = 800;
        MinWidth = 900;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TitleIcon = Application.Current.FindResource("App.Icon.Logo");
        TitleIconBrush = (Brush)Application.Current.FindResource("Ui.Accent");
        Background = (Brush)Application.Current.FindResource("Ui.Bg");
        Content = View;
    }

    /// <summary>Really closes (application exit).</summary>
    public void CloseForReal()
    {
        _reallyClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}
