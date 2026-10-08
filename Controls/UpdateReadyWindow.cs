using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// A newer version exists. "Cập nhật ngay" downloads it (with a progress bar) and starts the setup; the app exits. "Để sau" just closes:
/// nothing was downloaded, and the next start offers the update again. Built like <see cref="AppDialogWindow"/> (same title bar, badge,
/// footer, buttons and theme resources) so it looks like any other window of the app, light or dark.
/// </summary>
internal sealed class UpdateReadyWindow : XTWindow
{
    private const string Hint = "~15 giây · cần quyền quản trị";

    private readonly UpdateInfo _info;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Border _notesBox = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 12, 0, 0) };
    private readonly StackPanel _progressArea = new() { Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Border _progressTrack = new() { Height = 6, CornerRadius = new CornerRadius(3) };
    private readonly Border _progressFill = new() { Width = 0, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3) };
    private readonly TextBlock _progressText = new() { Margin = new Thickness(0, 7, 0, 0) };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _hint = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Text = Hint };
    private readonly XTButton _later;
    private readonly XTButton _update;
    private bool _downloading;

    private UpdateReadyWindow(UpdateInfo info)
    {
        _info = info;
        Title = "PDF Reader Pro";
        TitleBarMode = TitleBarMode.Dialog;
        TitleIcon = TryFindResource("App.Icon.Logo");
        TitleIconBrush = new SolidColorBrush(Color.FromRgb(255, 112, 24));
        Width = Math.Min(480, SystemParameters.WorkArea.Width - 32);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12.5;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        SetResourceReference(BackgroundProperty, "Ui.Surface");
        SetResourceReference(ForegroundProperty, "Ui.Text");

        _later = Button("Để sau", primary: false);
        _update = Button("Cập nhật ngay", primary: true);
        _later.IsCancel = true;
        _update.IsDefault = true;
        _later.Click += (_, _) => Close();
        _update.Click += async (_, _) => await UpdateNowAsync();

        // Footer: the same strip as AppDialog (Ui.Bg, thin top border); the hint on the left, the buttons on the right.
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 12, 18, 12) };
        footer.SetResourceReference(Border.BackgroundProperty, "Ui.Bg");
        footer.SetResourceReference(Border.BorderBrushProperty, "Ui.Border");
        var footerGrid = new Grid();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted");
        footerGrid.Children.Add(_hint);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(actions, 1);
        actions.Children.Add(_later);
        _update.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(_update);
        footerGrid.Children.Add(actions);
        footer.Child = footerGrid;

        // Body: badge + heading + version line, then the notes (or the progress while downloading).
        var body = new Grid { Margin = new Thickness(18) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        var badge = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        badge.SetResourceReference(Border.BackgroundProperty, "Ui.AccentSoft");
        var icon = new System.Windows.Shapes.Path { Data = DownloadIcon(), Width = 20, Height = 20, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Ui.Accent");
        badge.Child = icon;
        body.Children.Add(badge);

        var text = new StackPanel();
        Grid.SetColumn(text, 1);
        var heading = new TextBlock { Text = "Có bản cập nhật mới", FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
        text.Children.Add(heading);
        var version = new TextBlock { Text = $"Phiên bản {info.Version} (bạn đang dùng {AppInfo.Version})" + (info.Size > 0 ? $" · {info.Size / 1048576.0:0.#} MB" : ""), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        version.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted");
        text.Children.Add(version);

        string notes = info.Notes.Replace("\r", "").Trim();
        int sha = notes.IndexOf("SHA-256:", StringComparison.OrdinalIgnoreCase); // the release body ends with the file hash; users do not need it here
        if (sha >= 0) notes = notes[..sha].Trim();
        _notesBox.SetResourceReference(Border.BackgroundProperty, "Ui.Bg");
        _notesBox.SetResourceReference(Border.BorderBrushProperty, "Ui.Border");
        var notesText = new TextBlock { Text = notes.Length > 0 ? notes : "Cải tiến và sửa lỗi.", TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
        notesText.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
        _notesBox.Child = new ScrollViewer
        {
            Content = notesText, MaxHeight = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        text.Children.Add(_notesBox);

        _progressTrack.SetResourceReference(Border.BackgroundProperty, "Ui.Chip");
        _progressFill.SetResourceReference(Border.BackgroundProperty, "Ui.Accent");
        _progressTrack.Child = _progressFill;
        _progressText.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted");
        _progressArea.Children.Add(_progressTrack);
        _progressArea.Children.Add(_progressText);
        text.Children.Add(_progressArea);

        _error.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Danger");
        text.Children.Add(_error);
        body.Children.Add(text);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        Loaded += (_, _) => _update.Focus();
        Closed += (_, _) => _cancel.Cancel(); // closing while it downloads cancels the download
    }

    private XTButton Button(string label, bool primary)
    {
        var button = new XTButton { Text = label, MinWidth = 80, Height = 30, Padding = new Thickness(12, 0, 12, 0) };
        if (TryFindResource(primary ? "UiPrimaryButton" : "UiGhostButton") is Style style) button.Style = style;
        return button;
    }

    /// <summary>A down arrow into a tray, 24 x 24 like the other dialog icons.</summary>
    private static Geometry DownloadIcon() => Geometry.Parse("M11,3 H13 V13.2 L16.6,9.6 L18,11 L12,17 L6,11 L7.4,9.6 L11,13.2 Z M4,19 H20 V21 H4 Z");

    private async System.Threading.Tasks.Task UpdateNowAsync()
    {
        if (_downloading) return;
        _downloading = true;
        _update.IsEnabled = false;
        _later.Text = "Hủy";
        _error.Visibility = Visibility.Collapsed;
        _notesBox.Visibility = Visibility.Collapsed;
        _progressArea.Visibility = Visibility.Visible;
        _hint.Text = "Đang tải bản cập nhật…";
        var progress = new Progress<double>(p =>
        {
            _progressFill.Width = Math.Max(0, _progressTrack.ActualWidth * p);
            _progressText.Text = $"Đang tải… {(int)Math.Round(p * 100)}%";
        });
        try
        {
            string setup = await AppUpdateService.DownloadAsync(_info, progress, _cancel.Token);
            _progressText.Text = "Đang mở trình cài đặt…";
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
        _update.IsEnabled = true;
        _later.Text = "Để sau";
        _progressArea.Visibility = Visibility.Collapsed;
        _notesBox.Visibility = Visibility.Visible;
        _hint.Text = Hint;
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
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
