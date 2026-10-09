using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// A ribbon split button: the big button runs the command last used from the group, the small arrow beside it opens the list of the others.
/// The commands themselves stay the ordinary (hidden) buttons the window already wires, so their Click handlers, Tag="Active" state and names are
/// untouched: this control only presents them (the active one is shown on the big button) and raises their Click.
/// </summary>
internal sealed class RibbonSplit : StackPanel
{
    private sealed record Item(XTButton Button, string Label);

    private readonly List<Item> _items = new();
    private readonly XTButton _main;
    private readonly Border _arrow;
    private readonly TextBlock _arrowGlyph;
    private string _key = "";
    private Item? _current;

    public RibbonSplit()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        _main = new XTButton { Style = (Style)Application.Current.FindResource("UiRibbonButton") };
        _main.Click += (_, _) => { if (_current != null) Run(_current); };
        _arrowGlyph = new TextBlock { Text = "▾", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
        _arrowGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted");
        _arrow = new Border { Width = 14, Height = 54, CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = _arrowGlyph, ToolTip = Loc.T("More in this group") };
        _arrow.MouseEnter += (_, _) => { _arrow.SetResourceReference(Border.BackgroundProperty, "Ui.Hover"); _arrowGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Accent"); };
        _arrow.MouseLeave += (_, _) => { _arrow.Background = Brushes.Transparent; _arrowGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted"); };
        _arrow.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenMenu(); };
        Children.Add(_main);
        Children.Add(_arrow);
    }

    /// <summary>The commands of the group (button, short label for the big button; null = the button's own text). The first one is the default.</summary>
    public void Bind(string key, params (XTButton Button, string? Label)[] items)
    {
        _key = key;
        foreach (var (button, label) in items)
        {
            var item = new Item(button, label ?? button.Text ?? "");
            _items.Add(item);
            DependencyPropertyDescriptor.FromProperty(FrameworkElement.TagProperty, typeof(FrameworkElement)).AddValueChanged(button, (_, _) => SyncActive());
        }
        string saved = AppSettings.GetUi("Split." + key, "");
        Show(_items.FirstOrDefault(i => i.Button.Name == saved) ?? _items[0]);
        SyncActive();
    }

    private void Show(Item item)
    {
        _current = item;
        _main.Icon = item.Button.Icon;
        _main.Text = item.Label;
        _main.ToolTip = item.Button.ToolTip;
    }

    /// <summary>The group's tool is on (Tag Active on any member): the big button shows it and lights up.</summary>
    private void SyncActive()
    {
        var active = _items.FirstOrDefault(i => i.Button.Tag as string == "Active");
        if (active != null && !ReferenceEquals(active, _current)) Show(active);
        _main.Tag = active != null ? "Active" : null;
    }

    private void Run(Item item)
    {
        AppSettings.SetUi("Split." + _key, item.Button.Name);
        if (!ReferenceEquals(item, _current)) Show(item);
        item.Button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, item.Button));
    }

    private void OpenMenu()
    {
        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.Bottom };
        foreach (var item in _items)
        {
            var captured = item;
            bool active = item.Button.Tag as string == "Active";
            var header = new TextBlock { Text = Loc.T((item.Button.Text ?? item.Label).Replace('\n', ' ')), VerticalAlignment = VerticalAlignment.Center };
            var row = new MenuItem { Header = header, ToolTip = item.Button.ToolTip, IsEnabled = item.Button.IsEnabled, FontWeight = ReferenceEquals(item, _current) ? FontWeights.SemiBold : FontWeights.Normal };
            if (item.Button.Icon is Geometry icon)
            {
                var path = new System.Windows.Shapes.Path { Data = icon };
                path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, active ? "Ui.Accent" : "Ui.Text");
                row.Icon = new Viewbox { Width = 16, Height = 16, Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
            }
            row.Click += (_, _) => Dispatcher.BeginInvoke(() => Run(captured), System.Windows.Threading.DispatcherPriority.Input);
            menu.Items.Add(row);
        }
        menu.IsOpen = true;
    }
}
