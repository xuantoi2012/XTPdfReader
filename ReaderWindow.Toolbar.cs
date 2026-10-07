using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using XTStyle.Controls;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp;

public partial class ReaderWindow
{
    private bool _toolbarLayoutQueued;
    private void QueueToolbarLayout()
    {
        if (_toolbarLayoutQueued) return;
        _toolbarLayoutQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { _toolbarLayoutQueued = false; UpdateToolbarOverflow(); }));
    }
    private void InitializeToolbarOverflow()
    {
        ReaderToolsScroll.ScrollChanged += (_, _) => QueueToolbarLayout();
        ReaderToolbarBar.SizeChanged += (_, _) => QueueToolbarLayout();
        ReaderToolsScroll.PreviewMouseWheel += (_, e) =>
        {
            if (ReaderToolsScroll.ScrollableWidth <= 0) return;
            ReaderToolsScroll.ScrollToHorizontalOffset(ReaderToolsScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        Loaded += (_, _) => QueueToolbarLayout();
    }
    internal static bool ToolbarNeedsOverflow(double extent, double width) => extent > Math.Max(0, width - 24) + 1;
    private void UpdateToolbarOverflow()
    {
        var visibility = ToolbarNeedsOverflow(ReaderToolsScroll.ExtentWidth, ReaderToolbarBar.ActualWidth) ? Visibility.Visible : Visibility.Collapsed;
        if (ReaderToolbarMore.Visibility != visibility) ReaderToolbarMore.Visibility = visibility;
        if (ReaderToolsScroll.Content is not Panel root || ReaderToolsScroll.ViewportWidth <= 0) return;
        double width = ReaderToolsScroll.ViewportWidth;
        foreach (var group in root.Children.OfType<StackPanel>())
        {
            double left = group.TranslatePoint(new Point(), ReaderToolsScroll).X;
            foreach (var caption in group.Children.OfType<TextBlock>())
                caption.Opacity = left >= -.5 && left + group.ActualWidth <= width + .5 ? 1 : 0;
        }
        if (visibility != Visibility.Visible) { ReaderToolsScroll.Clip = null; return; }
        var whole = VisualTreeHelpers.FindVisualChildren<XTButton>(root).Where(b => b.ActualWidth > 0)
            .Select(b => (Left: b.TranslatePoint(new Point(), ReaderToolsScroll).X, Width: b.ActualWidth))
            .Where(b => b.Left >= -.5 && b.Left + b.Width <= width + .5).ToList();
        double first = whole.Count == 0 ? 0 : Math.Max(0, whole.Min(b => b.Left) - 2);
        double last = whole.Count == 0 ? 0 : Math.Min(width, whole.Max(b => b.Left + b.Width) + 2);
        // Keep complete controls at both viewport edges; the menu exposes every clipped command.
        ReaderToolsScroll.Clip = new RectangleGeometry(new Rect(first, 0, Math.Max(0, last - first), ReaderToolsScroll.ActualHeight));
    }
    internal int LastMoreToolsCount { get; private set; }

    /// <summary>"More tools": a popup that lists only the buttons the window is too narrow to show (cut off at the right edge), grouped like the ribbon.
    /// Own popup (not a ContextMenu): the menu clipped its last entry.</summary>
    private void ReaderToolbarMore_Click(object sender, RoutedEventArgs e)
    {
        double width = ReaderToolsScroll.ViewportWidth;
        bool Shown(XTButton b)
        {
            double left = b.TranslatePoint(new Point(), ReaderToolsScroll).X;
            return left >= -.5 && left + b.ActualWidth <= width + .5;
        }
        Popup? popup = null;
        var list = new StackPanel();
        foreach (var group in ((Panel)ReaderToolsScroll.Content).Children.OfType<StackPanel>())
        {
            var buttons = VisualTreeHelpers.FindVisualChildren<XTButton>(group).Where(b => b.Visibility == Visibility.Visible && !Shown(b)).ToList();
            if (buttons.Count == 0) continue;
            if (list.Children.Count > 0)
            {
                var line = new Border { Height = 1, Margin = new Thickness(6, 4, 6, 4) };
                line.SetResourceReference(Border.BackgroundProperty, "Ui.Border");
                list.Children.Add(line);
            }
            foreach (var button in buttons)
            {
                var captured = button;
                bool active = captured.Tag as string == "Active";
                var row = new Border { Height = 30, Padding = new Thickness(8, 0, 12, 0), CornerRadius = new CornerRadius(5), Background = Brushes.Transparent,
                    Cursor = System.Windows.Input.Cursors.Hand, ToolTip = captured.ToolTip, IsEnabled = captured.IsEnabled, Opacity = captured.IsEnabled ? 1 : 0.45 };
                var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (captured.Icon is Geometry icon)
                {
                    var path = new System.Windows.Shapes.Path { Data = icon };
                    path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, active ? "Ui.Accent" : "Ui.Text");
                    content.Children.Add(new Viewbox { Width = 16, Height = 16, Margin = new Thickness(0, 0, 10, 0), Child = new Canvas { Width = 24, Height = 24, Children = { path } } });
                }
                var label = new TextBlock { Text = (captured.Text ?? "").Replace('\n', ' '), VerticalAlignment = VerticalAlignment.Center };
                label.SetResourceReference(TextBlock.ForegroundProperty, active ? "Ui.Accent" : "Ui.Text");
                content.Children.Add(label);
                row.Child = content;
                row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "Ui.Hover");
                row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
                row.MouseLeftButtonUp += (_, _) =>
                {
                    if (popup != null) popup.IsOpen = false;
                    Dispatcher.BeginInvoke(() => captured.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, captured)), DispatcherPriority.Input);
                };
                list.Children.Add(row);
            }
        }
        if (list.Children.Count == 0) return;
        LastMoreToolsCount = list.Children.OfType<Border>().Count(b => b.Height == 30);
        var frame = new Border { Padding = new Thickness(6), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(8, 2, 8, 12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.18, Direction = 270 },
            Child = new ScrollViewer { MaxHeight = Math.Max(180, SystemParameters.WorkArea.Height - 160), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list } };
        frame.SetResourceReference(Border.BackgroundProperty, "Ui.Surface");
        frame.SetResourceReference(Border.BorderBrushProperty, "Ui.Border");
        popup = new Popup
        {
            PlacementTarget = ReaderToolbarMore, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            MinWidth = 210, Child = frame, IsOpen = true
        };
    }
}
