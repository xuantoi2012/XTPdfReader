using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>One paper size of the file in the list of sizes: ticked = the tool works on its pages; the badge says whether its area / place is set.</summary>
internal sealed class PaperGroupRow : INotifyPropertyChanged
{
    private bool _checked = true;
    private bool _hasArea;

    public PaperGroupRow(PaperGroup group) => Group = group;

    public PaperGroup Group { get; }
    public string Key => Group.Key;
    public string Count => $"{Group.Count} page{(Group.Count == 1 ? "" : "s")}";
    public bool IsChecked { get => _checked; set { _checked = value; Raise(nameof(IsChecked)); } }
    public bool HasArea { get => _hasArea; set { _hasArea = value; Raise(nameof(HasArea)); Raise(nameof(Detail)); Raise(nameof(DetailBrush)); } }
    public string Detail => $"{Count}  ·  {(HasArea ? "set" : "not set yet")}";
    public Brush DetailBrush => HasArea ? Brushes.SeaGreen : Brushes.Gray;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The paper sizes of the open file (A1 landscape, A3 portrait…): tick the sizes to work on, click one to see its sample page and draw its own area. Areas and stamp places are kept
/// per size, because an A1 sheet and an A3 sheet carry the same thing in different places.
/// </summary>
internal sealed class PaperGroupList : Border
{
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Single, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly List<PaperGroupRow> _rows = new();
    private bool _loading;

    public event Action<PaperGroupRow>? GroupSelected;
    public event Action? CheckedChanged;

    public PaperGroupList()
    {
        BorderBrush = TryFindResource("Ui.Border") as Brush ?? Brushes.Gray;
        BorderThickness = new Thickness(1);
        MinHeight = 74;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "UiRowItem");
        _list.ItemTemplate = Template();
        _list.SelectionChanged += (_, _) => { if (!_loading && _list.SelectedItem is PaperGroupRow row) GroupSelected?.Invoke(row); };
        Child = _list;
    }

    public IReadOnlyList<PaperGroupRow> Rows => _rows;

    /// <summary>Selects the size with this key (what a click on it does).</summary>
    public void SelectKey(string key)
    {
        if (_rows.FirstOrDefault(r => r.Key == key) is { } row) _list.SelectedItem = row;
    }
    /// <summary>Selects the size that has this page (the reader moved to another page); false when none has it.</summary>
    public bool SelectPage(int page)
    {
        if (_rows.FirstOrDefault(r => r.Group.Pages.Any(x => x.Page == page)) is not { } row) return false;
        if (!ReferenceEquals(_list.SelectedItem, row)) _list.SelectedItem = row;
        return true;
    }
    public PaperGroupRow? Selected => _list.SelectedItem as PaperGroupRow;
    public IReadOnlyList<PaperGroupRow> Checked => _rows.Where(r => r.IsChecked).ToList();

    public void Load(IReadOnlyList<PaperGroup> groups, Func<string, bool> hasArea, int? pageToSelect = null)
    {
        _loading = true;
        foreach (var old in _rows) old.PropertyChanged -= Row_Changed;
        _rows.Clear();
        foreach (var group in groups)
        {
            var row = new PaperGroupRow(group) { HasArea = hasArea(group.Key) };
            row.PropertyChanged += Row_Changed;
            _rows.Add(row);
        }
        _list.ItemsSource = _rows;
        _list.Height = Math.Min(220, Math.Max(52, _rows.Count * 46 + 4));
        _loading = false;
        var start = _rows.FirstOrDefault(r => pageToSelect is { } p && r.Group.Pages.Any(x => x.Page == p)) ?? _rows.FirstOrDefault();
        if (start != null) _list.SelectedItem = start;
    }

    private void Row_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaperGroupRow.IsChecked)) CheckedChanged?.Invoke();
    }

    private static DataTemplate Template()
    {
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 3, 4, 3));
        foreach (var width in new[] { new GridLength(26), new GridLength(1, GridUnitType.Star) })
        {
            var column = new FrameworkElementFactory(typeof(ColumnDefinition));
            column.SetValue(ColumnDefinition.WidthProperty, width);
            grid.AppendChild(column);
        }
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetBinding(CheckBox.IsCheckedProperty, new System.Windows.Data.Binding(nameof(PaperGroupRow.IsChecked)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        grid.AppendChild(check);
        var texts = new FrameworkElementFactory(typeof(StackPanel));
        texts.SetValue(Grid.ColumnProperty, 1);
        var key = new FrameworkElementFactory(typeof(TextBlock));
        key.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PaperGroupRow.Key)));
        key.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        texts.AppendChild(key);
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PaperGroupRow.Detail)));
        detail.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(PaperGroupRow.DetailBrush)));
        detail.SetValue(TextBlock.FontSizeProperty, 11.5);
        texts.AppendChild(detail);
        grid.AppendChild(texts);
        return new DataTemplate { VisualTree = grid };
    }
}

/// <summary>Small builders so the area windows look like Read sheet info: the same buttons, headings and muted notes.</summary>
internal static class AreaKit
{
    public static XTButton Primary(string text, double minWidth = 0)
    {
        var button = new XTButton { Text = text, Height = 34, Padding = new Thickness(14, 0, 14, 0), MinWidth = minWidth };
        button.SetResourceReference(FrameworkElement.StyleProperty, "UiPrimaryButton");
        return button;
    }

    public static XTButton Ghost(string text, double height = 34, double minWidth = 0)
    {
        var button = new XTButton { Text = text, Height = height, Padding = new Thickness(12, 0, 12, 0), MinWidth = minWidth };
        button.SetResourceReference(FrameworkElement.StyleProperty, "UiGhostButton");
        return button;
    }

    public static TextBlock Heading(FrameworkElement owner, string text)
    {
        var block = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) };
        return block;
    }

    public static TextBlock Muted(FrameworkElement owner, string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted");
        return block;
    }

    public static TextBox Box(double height = 28)
        => new() { Height = height, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };

    /// <summary>The footer of a side panel: the status text (wrapping) and the buttons under it.</summary>
    public static Border PanelFooter(TextBlock status, params UIElement[] buttons)
    {
        status.TextWrapping = TextWrapping.Wrap;
        status.FontSize = 12;
        status.Margin = new Thickness(0, 0, 0, 8);
        status.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted");
        var row = new WrapPanel();
        foreach (var button in buttons) { if (button is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 8, 0); row.Children.Add(button); }
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 10, 14, 12), Child = new StackPanel { Children = { status, row } } };
        footer.SetResourceReference(Border.BorderBrushProperty, "Ui.Border");
        footer.SetResourceReference(Border.BackgroundProperty, "Ui.Panel");
        return footer;
    }

    /// <summary>The footer bar: status text on the left, the buttons on the right.</summary>
    public static Border Footer(FrameworkElement owner, TextBlock status, params UIElement[] buttons)
    {
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in buttons) right.Children.Add(button);
        DockPanel.SetDock(right, Dock.Right);
        status.VerticalAlignment = VerticalAlignment.Center;
        status.FontSize = 12;
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted");
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(20, 12, 20, 12), Child = new DockPanel { Children = { right, status } } };
        footer.SetResourceReference(Border.BorderBrushProperty, "Ui.Border");
        footer.SetResourceReference(Border.BackgroundProperty, "Ui.Panel");
        return footer;
    }
}
