using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// The small window shown while the app starts (also when a PDF is double-clicked and the app is not running yet). Same look as the
/// installer. Its animations are transform-only, so they keep moving while the UI thread is busy building the main window.
/// </summary>
public partial class StartupSplash : Window
{
    private bool _closing;

    public StartupSplash()
    {
        InitializeComponent();
        VersionText.Text = "v" + AppInfo.Version;
        // The card carries the shadow, so the backdrop inside is cut to its rounded corners by a clip instead of ClipToBounds.
        Inner.SizeChanged += (_, _) => Inner.Clip = new RectangleGeometry(new Rect(0, 0, Inner.ActualWidth, Inner.ActualHeight), 11, 11);
        Fill.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ThemeService.AccentSwatch(AppSettings.Accent)));
        Loaded += (_, _) => StartMotion();
    }

    private static bool Animated => SystemParameters.ClientAreaAnimation && RenderCapability.Tier > 0;

    internal void SetStatus(string text) => StatusText.Text = XTPdfMergeApp.Services.Loc.T(text);

    private void StartMotion()
    {
        if (!Animated) return;
        var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
        Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 0.95, TimeSpan.FromSeconds(2.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = sine });
        var breathe = new DoubleAnimation(0.94, 1.08, TimeSpan.FromSeconds(2.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = sine };
        GlowScale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
        GlowScale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
        // The bar eases towards ~92 % while the app loads; FadeOutAndClose completes it.
        FillScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0, 0.92, TimeSpan.FromSeconds(9)) { EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 3 } });
    }

    /// <summary>A short fade, then the window closes.</summary>
    internal void FadeOutAndClose()
    {
        if (_closing) return;
        _closing = true;
        if (!Animated) { Close(); return; }
        // Fill the bar, then fade the whole card (the shadow with it).
        var finish = new DoubleAnimation(FillScale.ScaleX, 1, TimeSpan.FromMilliseconds(140));
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160)) { BeginTime = TimeSpan.FromMilliseconds(140) };
        fade.Completed += (_, _) => Close();
        FillScale.BeginAnimation(ScaleTransform.ScaleXProperty, finish);
        Card.BeginAnimation(OpacityProperty, fade);
    }
}
