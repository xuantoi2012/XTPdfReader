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
    /// <summary>"More tools": the popup lists only the buttons the window is too narrow to show (cut off at the right edge), grouped like the ribbon.</summary>
    private void ReaderToolbarMore_Click(object sender, RoutedEventArgs e)
    {
        double width = ReaderToolsScroll.ViewportWidth;
        bool Shown(XTButton b)
        {
            double left = b.TranslatePoint(new Point(), ReaderToolsScroll).X;
            return left >= -.5 && left + b.ActualWidth <= width + .5;
        }
        var menu = new ContextMenu { PlacementTarget = ReaderToolbarMore, Placement = PlacementMode.Bottom, MaxHeight = Math.Max(180, SystemParameters.WorkArea.Height - 120) };
        foreach (var group in ((Panel)ReaderToolsScroll.Content).Children.OfType<StackPanel>())
        {
            var buttons = VisualTreeHelpers.FindVisualChildren<XTButton>(group).Where(b => b.Visibility == Visibility.Visible && !Shown(b)).ToList();
            if (buttons.Count == 0) continue;
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            foreach (var button in buttons)
            {
                var item = new MenuItem { Header = (button.Text ?? "").Replace('\n', ' '), IsEnabled = button.IsEnabled,
                    ToolTip = button.ToolTip, IsCheckable = button.Tag as string == "Active", IsChecked = button.Tag as string == "Active" };
                if (button.Icon is Geometry icon) item.Icon = new System.Windows.Shapes.Path { Data = icon, Width = 16, Height = 16, Stretch = Stretch.Uniform, Fill = (Brush)FindResource("Ui.Text") };
                item.Click += (_, _) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                menu.Items.Add(item);
            }
        }
        if (menu.Items.Count == 0) return;
        ReaderToolbarMore.ContextMenu = menu;
        menu.IsOpen = true;
    }
}
