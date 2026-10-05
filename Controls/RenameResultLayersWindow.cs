using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// "Rename result layers": bảng "tên layer trong file kết quả → tên mới" xem trước từ cách gộp đang chọn trong hộp thoại Save merged file.
/// Chỉ đổi tên (không gộp thêm): hai layer không được trùng tên mới.
/// </summary>
internal sealed class RenameResultLayersWindow : XTWindow
{
    private readonly List<LayerEditRow> _rows;
    private readonly TextBlock _error;
    private readonly XTButton _ok;

    /// <summary>Tên hiện tại → tên mới, chỉ các dòng đã đổi.</summary>
    public IReadOnlyDictionary<string, string> Renames { get; private set; } = new Dictionary<string, string>();

    public RenameResultLayersWindow(IReadOnlyList<string> resultNames, IReadOnlyDictionary<string, string>? current)
    {
        _rows = resultNames.Select(n => new LayerEditRow(n, n, false, false) { NewName = current != null && current.TryGetValue(n, out var v) ? v : n }).ToList();

        Title = "Rename result layers";
        TitleBarMode = TitleBarMode.Dialog;
        Width = 560;
        Height = 560;
        MinWidth = 440;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        var root = new DockPanel { Background = R("Ui.Surface") };

        _error = new TextBlock { Foreground = Brushes.IndianRed, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _ok = new XTButton { Text = "OK", Width = 90, Height = 34, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        _ok.SetResourceReference(StyleProperty, "UiPrimaryButton");
        _ok.Click += (_, _) => Accept();
        var cancel = new XTButton { Text = "Cancel", Width = 90, Height = 34, IsCancel = true };
        cancel.SetResourceReference(StyleProperty, "UiGhostButton");
        cancel.Click += (_, _) => DialogResult = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _ok, cancel } };
        DockPanel.SetDock(buttons, Dock.Right);
        var footer = new Border
        {
            BorderBrush = R("Ui.Border"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(20, 12, 20, 12), Background = R("Ui.Panel"),
            Child = new DockPanel { Children = { buttons, _error } }
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var head = new TextBlock
        {
            Text = "These are the layers the merged file will contain. Type a new name to rename one; leave a row unchanged to keep its name.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 16, 20, 10), FontSize = 12, Foreground = R("Ui.Muted")
        };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var list = new ListBox { BorderThickness = new Thickness(0, 1, 0, 0), Background = Brushes.Transparent, BorderBrush = R("Ui.Border"), ItemsSource = _rows };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "UiRowItem");
        list.ItemTemplate = BuildTemplate();
        root.Children.Add(list);

        Content = root;
        foreach (var row in _rows) row.PropertyChanged += (_, _) => Validate();
        Validate();
    }

    private static DataTemplate BuildTemplate()
    {
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 3, 12, 3));
        for (int i = 0; i < 3; i++)
        {
            var column = new FrameworkElementFactory(typeof(ColumnDefinition));
            column.SetValue(ColumnDefinition.WidthProperty, i switch { 0 => new GridLength(1, GridUnitType.Star), 1 => GridLength.Auto, _ => new GridLength(1.2, GridUnitType.Star) });
            grid.AppendChild(column);
        }
        var original = new FrameworkElementFactory(typeof(TextBlock));
        original.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(LayerEditRow.OriginalName)));
        original.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(nameof(LayerEditRow.OriginalName)));
        original.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        original.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.AppendChild(original);
        var arrow = new FrameworkElementFactory(typeof(TextBlock));
        arrow.SetValue(TextBlock.TextProperty, "→");
        arrow.SetValue(Grid.ColumnProperty, 1);
        arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 10, 0));
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.AppendChild(arrow);
        var box = new FrameworkElementFactory(typeof(TextBox));
        box.SetValue(Grid.ColumnProperty, 2);
        box.SetValue(FrameworkElement.HeightProperty, 26.0);
        box.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
        box.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding(nameof(LayerEditRow.NewName))
        {
            Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
        });
        grid.AppendChild(box);
        return new DataTemplate { VisualTree = grid };
    }

    private void Validate()
    {
        var names = _rows.Select(r => r.NewName.Trim()).ToList();
        string? problem = names.Any(n => n.Length == 0) ? "A layer name cannot be empty."
            : names.GroupBy(n => n, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } dup ? $"Two layers would both be called \"{dup.Key}\"." : null;
        _error.Text = problem ?? "";
        _ok.IsEnabled = problem == null;
    }

    private void Accept()
    {
        Renames = _rows.Where(r => r.NewName.Trim() != r.OriginalName).ToDictionary(r => r.OriginalName, r => r.NewName.Trim(), StringComparer.Ordinal);
        DialogResult = true;
    }
}
