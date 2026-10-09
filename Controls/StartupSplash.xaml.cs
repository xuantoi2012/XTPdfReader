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
        // The green segment glides back and forth along the track (track is 420 - 72 - 2 = 346 wide, segment 110).
        SegmentShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, 236, TimeSpan.FromSeconds(1.1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = sine });
    }

    /// <summary>A short fade, then the window closes.</summary>
    internal void FadeOutAndClose()
    {
        if (_closing) return;
        _closing = true;
        if (!Animated) { Close(); return; }
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => Close();
        Card.BeginAnimation(OpacityProperty, fade);
    }
}
