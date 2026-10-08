using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace XTPdfMergeApp.Controls;

/// <summary>Low-contrast ambient light. Transform-only animation, no blur or per-frame bitmap allocations.</summary>
public partial class AmbientBackdrop : UserControl
{
    public AmbientBackdrop()
    {
        InitializeComponent();
        Loaded += (_, _) => SetAnimation(SystemParameters.ClientAreaAnimation && RenderCapability.Tier > 0);
        Unloaded += (_, _) => SetAnimation(false);
    }

    private void SetAnimation(bool enabled)
    {
        Drift((TranslateTransform)LavenderLight.RenderTransform, 14, 8, 6, enabled);
        Drift((TranslateTransform)BlueLight.RenderTransform, -16, -10, 7, enabled);
        Drift((TranslateTransform)MintLight.RenderTransform, 10, -8, 8, enabled);
    }

    private static void Drift(TranslateTransform transform, double x, double y, double seconds, bool enabled)
    {
        DoubleAnimation? Motion(double distance) => enabled ? new DoubleAnimation(-distance, distance, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        } : null;
        transform.BeginAnimation(TranslateTransform.XProperty, Motion(x));
        transform.BeginAnimation(TranslateTransform.YProperty, Motion(y));
    }
}
