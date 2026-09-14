using System.Windows;
using System.Windows.Controls;

namespace XTPdfMergeApp.Services;

internal static class RetainedTilePresentation
{
    // Both canvases remain visible: new opaque tiles cover only the region they
    // have finished. The old resolution fills the rest until refinement completes.
    public static void Begin(Canvas previous, Canvas next)
    {
        next.Children.Clear();
        next.Opacity = 1;
        next.Visibility = Visibility.Visible;
        Panel.SetZIndex(previous, 0);
        Panel.SetZIndex(next, 1);
    }

    public static void Complete(Canvas previous)
    {
        previous.Visibility = Visibility.Collapsed;
        previous.Children.Clear();
    }
}
