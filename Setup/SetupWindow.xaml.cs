using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PdfReaderSetup;

/// <summary>
/// The install / uninstall screen. The work runs on a background thread and is held back so that it never gets ahead of
/// <c>elapsed / minimum duration</c>: the bar always shows what is really done, a quick copy still takes ~15 s, and cancelling
/// (the X) really stops the work. Before the install commits (<see cref="Installer.CommitFraction"/>) a cancel leaves nothing behind.
/// </summary>
public partial class SetupWindow : Window
{
    private readonly Options _options;
    private readonly bool _uninstall;
    private DateTime _started = DateTime.UtcNow;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancel = new();
    private double _real;
    private string _realStage = "Đang chuẩn bị…";
    private bool _workDone, _finished, _cancelled, _begun;
    private string? _error;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };

    internal SetupWindow(Options options)
    {
        InitializeComponent();
        _options = options;
        _uninstall = options.Uninstall;
        VersionText.Text = Installer.PeekVersion(options.Layout, options.InstallerPath, options.Uninstall) is { } version ? "PDF Reader Pro v" + version : "PDF Reader Pro";
        Loaded += async (_, _) =>
        {
            ShowWelcome();
            if (options.Launch) await BeginAsync(); // an update was already agreed to in the Reader
        };
        Closing += OnClosing;
        SizeChanged += (_, _) => RoundSurface();
        Loaded += (_, _) => RoundSurface();
    }

    /// <summary>The window is transparent with the card inside it; the backdrop inside the card is cut to the card's rounded corners.</summary>
    private void RoundSurface()
    {
        if (Surface.ActualWidth <= 0) return;
        Surface.Clip = new RectangleGeometry(new Rect(0, 0, Surface.ActualWidth, Surface.ActualHeight), 9, 9);
    }

    /// <summary>The title row is the handle to move the window (it has no system caption any more).</summary>
    private void Surface_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.GetPosition(Surface).Y <= 42 && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase && e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    /// <summary>The first screen: what this is and one button. Nothing is touched until the user presses it.</summary>
    private void ShowWelcome()
    {
        _begun = false;
        TitleText.Text = _uninstall ? "Gỡ cài đặt PDF Reader Pro" : "Chào mừng đến với PDF Reader Pro";
        SubtitleText.Text = _uninstall ? "PDF Reader Pro sẽ được gỡ khỏi máy này. Tài liệu PDF của bạn không bị xóa." : "Trình đọc và chỉnh sửa PDF nhanh, gọn cho công việc hằng ngày.";
        Feature1.Text = _uninstall ? "" : "•  Xem trang nhanh, cuộn mượt không giật";
        Feature2.Text = _uninstall ? "" : "•  Ghép (merge) nhiều file nhanh gọn";
        Feature3.Text = _uninstall ? "" : "•  Chỉnh sửa chữ, đối tượng và chú thích thuận tiện";
        FeaturesPanel.Visibility = _uninstall ? Visibility.Collapsed : Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Collapsed;
        ActionButton.Content = _uninstall ? "Gỡ cài đặt" : "Cài đặt";
        ActionButton.IsEnabled = true;
        ActionButton.Visibility = Visibility.Visible;
        ActionButton.Focus();
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_begun)
        {
            Close(); // "Hủy": OnClosing asks, and ignores it once the install can no longer be undone
            return;
        }
        await BeginAsync();
    }

    private static bool Animated => SystemParameters.ClientAreaAnimation && RenderCapability.Tier > 0;

    /// <summary>Slow breathing light behind the logo and a highlight sliding along the bar (skipped when Windows animations are off).</summary>
    private void StartMotion()
    {
        if (!Animated) return;
        var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
        Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 0.95, TimeSpan.FromSeconds(3.5)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = sine });
        var breathe = new DoubleAnimation(0.94, 1.08, TimeSpan.FromSeconds(3.5)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = sine };
        GlowScale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
        GlowScale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
        SheenShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-90, 430, TimeSpan.FromSeconds(1.7)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } });
    }

    /// <summary>One flash along the full bar, the sheen stops, and the green badge pops onto the logo.</summary>
    private void PlayFinish()
    {
        SheenShift.BeginAnimation(TranslateTransform.XProperty, null);
        SheenShift.X = -90;
        if (!Animated) { BadgeScale.ScaleX = BadgeScale.ScaleY = 1; return; }
        var flash = new DoubleAnimationUsingKeyFrames();
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(0.6, KeyTime.FromPercent(0.25), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1.0), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        flash.Duration = TimeSpan.FromMilliseconds(700);
        FinishFlash.BeginAnimation(OpacityProperty, flash);
        var pop = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { BeginTime = TimeSpan.FromMilliseconds(120), EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 }, FillBehavior = FillBehavior.HoldEnd };
        BadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        BadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>Checks that the app is not running (asking to close it), then starts the work.</summary>
    private async Task BeginAsync()
    {
        _begun = true;
        string dir = _options.Layout.InstallDir;
        // An update is started by the Reader itself, which is closing at that moment: give it time before asking anything.
        if (_options.Launch) await Task.Run(() => Installer.WaitForExit(dir, TimeSpan.FromSeconds(8)));
        // After an update the user has already closed the Reader; a leftover helper is closed by the install itself.
        if (_options.WaitPid == 0 && await Task.Run(() => Installer.IsRunning(dir)))
        {
            bool close = ConfirmDialog.Ask(this, _uninstall ? "Gỡ cài đặt PDF Reader Pro" : "Cài đặt PDF Reader Pro",
                "PDF Reader Pro đang chạy. Cần đóng ứng dụng để tiếp tục.\nHãy lưu công việc đang làm trước khi đóng.",
                "Đóng ứng dụng và tiếp tục", "Hủy", actionIsPrimary: true);
            if (!close) { ShowWelcome(); return; }
        }
        Start();
    }

    private void Start()
    {
        TitleText.Text = _uninstall ? "Đang gỡ cài đặt" : "Đang cài đặt";
        SubtitleText.Text = _uninstall ? "Đang dọn dẹp PDF Reader Pro." : "Chuẩn bị không gian làm việc của bạn.";
        FeaturesPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ActionButton.Content = _uninstall ? "Đang gỡ…" : "Hủy";
        ActionButton.IsEnabled = !_uninstall;
        ActionButton.Background = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
        ActionButton.Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0x2B, 0x62));
        _started = DateTime.UtcNow;
        StartMotion();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        var token = _cancel.Token;
        Task.Run(() =>
        {
            try
            {
                void Report(double fraction, string stage)
                {
                    // A cancel is honoured only before the point of no return (and never for an uninstall).
                    bool cancellable = !_uninstall && fraction <= Installer.CommitFraction;
                    if (cancellable) token.ThrowIfCancellationRequested();
                    // Hold the work back to the pace of the bar, so what it shows is what is really done.
                    double wait = fraction * _options.Seconds - (DateTime.UtcNow - _started).TotalSeconds;
                    if (wait > 0)
                    {
                        if (cancellable && token.WaitHandle.WaitOne(TimeSpan.FromSeconds(wait))) token.ThrowIfCancellationRequested();
                        else if (!cancellable) Thread.Sleep(TimeSpan.FromSeconds(wait));
                    }
                    lock (_gate) { _real = fraction; _realStage = stage; }
                }
                if (_uninstall) Installer.Uninstall(_options.Layout, Report);
                else Installer.Install(_options.Layout, _options.InstallerPath, Report, token);
            }
            catch (OperationCanceledException) { _cancelled = true; }
            catch (Exception ex) { _error = ex.Message; }
            lock (_gate) { _workDone = true; if (_error is null && !_cancelled) _real = 1.0; }
        });
    }

    private void Tick()
    {
        double real; string stage; bool workDone;
        lock (_gate) { real = _real; stage = _realStage; workDone = _workDone; }
        if (_cancelled && workDone) { _finished = true; _timer.Stop(); Close(); return; }
        if (_error is not null) { Fail(_error); return; }
        // Never ahead of the clock either (the work is held back already; this is the guard if it is not).
        double shown = Math.Min(real, Math.Min(1.0, (DateTime.UtcNow - _started).TotalSeconds / _options.Seconds));
        Fill.Width = Math.Max(0, (Track.ActualWidth - 2) * shown);
        PercentText.Text = $"{(int)Math.Round(shown * 100)}%";
        bool cancelling = _cancel.IsCancellationRequested && real < Installer.CommitFraction;
        StageText.Text = cancelling ? "Đang hủy…" : stage;
        // After the point of no return (install) or at all (uninstall) the X would only leave a half-done job.
        CloseX.IsEnabled = !_uninstall && real < Installer.CommitFraction && !cancelling;
        if (!_uninstall) ActionButton.IsEnabled = CloseX.IsEnabled;
        if (workDone && shown >= 1.0 - 1e-9) Finish();
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        _timer.Stop();
        Fill.Width = Track.ActualWidth - 2;
        PercentText.Text = "100%";
        StageText.Text = "Hoàn tất";
        PlayFinish();
        TitleText.Text = _uninstall ? "Đã gỡ cài đặt" : "Đã cài đặt xong";
        SubtitleText.Text = _uninstall ? "PDF Reader Pro đã được gỡ khỏi máy này." : "PDF Reader Pro đã sẵn sàng. Bạn có thể mở ngay.";
        LaunchButton.Visibility = _uninstall ? Visibility.Collapsed : Visibility.Visible;
        ActionButton.Visibility = Visibility.Collapsed;
        Buttons.Visibility = Visibility.Visible;
        CloseX.IsEnabled = true;
        if (_options.Launch && !_uninstall) Launch_Click(this, new RoutedEventArgs());
        else if (_uninstall) CloseButton.Focus();
        else LaunchButton.Focus();
    }

    private void Fail(string message)
    {
        _finished = true;
        _timer.Stop();
        TitleText.Text = _uninstall ? "Không gỡ được" : "Không cài được";
        SubtitleText.Text = message;
        StageText.Text = "Đã dừng";
        SheenShift.BeginAnimation(TranslateTransform.XProperty, null);
        LaunchButton.Visibility = Visibility.Collapsed;
        ActionButton.Visibility = Visibility.Collapsed;
        Buttons.Visibility = Visibility.Visible;
        CloseX.IsEnabled = true;
    }

    /// <summary>The X, Alt+F4 and Esc-style closes all come here: ask while the work can still be undone, ignore it when it cannot.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_finished || !_begun) return;
        e.Cancel = true;
        double real;
        lock (_gate) real = _real;
        if (_uninstall || _cancel.IsCancellationRequested || real >= Installer.CommitFraction || !CloseX.IsEnabled) return;
        bool stop = ConfirmDialog.Ask(this, "Dừng cài đặt?",
            "PDF Reader Pro chưa được cài xong. Nếu thoát bây giờ, máy của bạn sẽ không thay đổi gì.",
            "Thoát cài đặt", "Tiếp tục cài đặt", actionIsPrimary: false);
        lock (_gate) { if (stop && _real < Installer.CommitFraction) _cancel.Cancel(); } // the dialog may have been open while the install crossed the point of no return
    }

    private void CloseX_Click(object sender, RoutedEventArgs e) => Close();

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Through explorer so the Reader starts with the user's normal rights, not this elevated process's.
            string exe = Path.Combine(_options.Layout.InstallDir, Layout.ExeName);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = true });
        }
        catch { /* the shortcut still works */ }
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
