using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>1 dòng của hộp thoại quản lý layer: tên hiện tại và tên mới (gõ trùng tên layer khác = gộp 2 layer).</summary>
internal sealed class LayerEditRow : INotifyPropertyChanged
{
    private string _newName;

    public LayerEditRow(string id, string name, bool hidden, bool locked)
    {
        Id = id;
        OriginalName = name;
        _newName = name;
        Hidden = hidden;
        Locked = locked;
    }

    public string Id { get; }
    public string OriginalName { get; }
    public bool Hidden { get; }
    public bool Locked { get; }
    public string Flags => (Hidden ? "hidden" : "") + (Hidden && Locked ? ", " : "") + (Locked ? "locked" : "");

    public string NewName
    {
        get => _newName;
        set
        {
            if (_newName == value) return;
            _newName = value;
            PropertyChanged?.Invoke(this, new(nameof(NewName)));
            PropertyChanged?.Invoke(this, new(nameof(Changed)));
        }
    }

    public bool Changed => !string.Equals(_newName.Trim(), OriginalName, StringComparison.Ordinal);
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// "Manage layers" của 1 file đang mở: đổi tên layer, gộp nhiều layer thành 1 (đặt cùng tên mới), bỏ tiền tố xref.
/// Kết quả: <see cref="Edits"/> + ghi vào chính file (<see cref="SaveInPlace"/>) hay ra bản sao (<see cref="OutputPath"/>).
/// </summary>
internal sealed class ManageLayersWindow : XTWindow
{
    private readonly string _path;
    private readonly PdfLayerInfo _info;
    private readonly List<LayerEditRow> _rows;
    private readonly ListBox _list;
    private readonly TextBlock _summary;
    private readonly XTButton _saveButton, _copyButton;

    public IReadOnlyList<LayerRename> Edits { get; private set; } = Array.Empty<LayerRename>();
    public bool SaveInPlace { get; private set; }
    public string OutputPath { get; private set; } = "";

    public ManageLayersWindow(string path, PdfLayerInfo info)
    {
        _path = path;
        _info = info;
        _rows = info.Names.Select(kv => new LayerEditRow(kv.Key, kv.Value, info.DefaultHidden.Contains(kv.Key), info.Locked.Contains(kv.Key))).ToList();

        Title = "Manage layers";
        TitleBarMode = TitleBarMode.Dialog;
        Width = 640;
        Height = 620;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var root = new DockPanel { Background = R("Ui.Surface") };

        // chân: nút
        _saveButton = new XTButton { Text = "Save to this file", Height = 34, Padding = new Thickness(14, 0, 14, 0) };
        _saveButton.SetResourceReference(StyleProperty, "UiPrimaryButton");
        _saveButton.Click += (_, _) => Accept(inPlace: true);
        _copyButton = new XTButton { Text = "Save as copy…", Height = 34, Margin = new Thickness(0, 0, 8, 0) };
        _copyButton.SetResourceReference(StyleProperty, "UiGhostButton");
        _copyButton.Click += (_, _) => Accept(inPlace: false);
        var cancel = new XTButton { Text = "Cancel", Width = 90, Height = 34, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.SetResourceReference(StyleProperty, "UiGhostButton");
        cancel.Click += (_, _) => DialogResult = false;
        _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = R("Ui.Muted"), FontSize = 12 };
        var footerButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _copyButton, _saveButton } };
        var footer = new Border
        {
            BorderBrush = R("Ui.Border"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(20, 12, 20, 12),
            Background = R("Ui.Panel"),
            Child = new DockPanel { Children = { DockTo(footerButtons, System.Windows.Controls.Dock.Right), _summary } }
        };
        DockPanel.SetDock(footer, System.Windows.Controls.Dock.Bottom);
        root.Children.Add(footer);

        // đầu: mô tả + công cụ
        var head = new StackPanel { Margin = new Thickness(20, 16, 20, 8) };
        DockPanel.SetDock(head, System.Windows.Controls.Dock.Top);
        head.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = path });
        head.Children.Add(new TextBlock
        {
            Text = "Type a new name to rename a layer. Layers that end up with the same name are merged into one.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10), Foreground = R("Ui.Muted"), FontSize = 12
        });
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(Tool("Merge selected…", "Give all selected layers one name (they become a single layer)", MergeSelected_Click));
        tools.Children.Add(Tool("Remove “Xref|” prefix", "Keep only the part after the last “|” in every layer name", StripPrefix_Click));
        tools.Children.Add(Tool("Reset", "Undo every change in this window", Reset_Click));
        head.Children.Add(tools);
        root.Children.Add(head);

        // danh sách
        _list = new ListBox
        {
            SelectionMode = SelectionMode.Extended, BorderThickness = new Thickness(0, 1, 0, 0), Background = Brushes.Transparent,
            BorderBrush = R("Ui.Border"), ItemsSource = _rows, Margin = new Thickness(0),
            ItemTemplate = BuildRowTemplate()
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "UiRowItem");
        root.Children.Add(_list);

        Content = root;
        foreach (var row in _rows) row.PropertyChanged += (_, _) => UpdateSummary();
        UpdateSummary();
    }

    private Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private static UIElement DockTo(UIElement element, Dock dock) { DockPanel.SetDock(element, dock); return element; }

    private XTButton Tool(string text, string tip, RoutedEventHandler click)
    {
        var button = new XTButton { Text = text, ToolTip = tip, Margin = new Thickness(0, 0, 8, 0) };
        button.SetResourceReference(StyleProperty, "UiGhostButton");
        button.Click += click;
        return button;
    }

    private static DataTemplate BuildRowTemplate()
    {
        // Cột: tên hiện tại | mũi tên | ô tên mới | cờ (hidden/locked)
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 3, 12, 3));
        for (int i = 0; i < 4; i++)
        {
            var column = new FrameworkElementFactory(typeof(ColumnDefinition));
            column.SetValue(ColumnDefinition.WidthProperty, i switch { 0 => new GridLength(1, GridUnitType.Star), 1 => GridLength.Auto, 2 => new GridLength(1.2, GridUnitType.Star), _ => new GridLength(70) });
            grid.AppendChild(column);
        }

        var original = new FrameworkElementFactory(typeof(TextBlock));
        original.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(LayerEditRow.OriginalName)));
        original.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        original.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        original.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(nameof(LayerEditRow.OriginalName)));
        grid.AppendChild(original);

        var arrow = new FrameworkElementFactory(typeof(TextBlock));
        arrow.SetValue(TextBlock.TextProperty, "→");
        arrow.SetValue(Grid.ColumnProperty, 1);
        arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 10, 0));
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted");
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

        var flags = new FrameworkElementFactory(typeof(TextBlock));
        flags.SetValue(Grid.ColumnProperty, 3);
        flags.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(LayerEditRow.Flags)));
        flags.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        flags.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        flags.SetValue(TextBlock.FontSizeProperty, 11.5);
        flags.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted");
        grid.AppendChild(flags);

        return new DataTemplate { VisualTree = grid };
    }

    // ── Công cụ ─────────────────────────────────────────────────────

    private void MergeSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _list.SelectedItems.Cast<LayerEditRow>().ToList();
        if (selected.Count < 2)
        {
            AppDialog.Show(this, "Select two or more layers (Ctrl+click or Shift+click) to merge them.", "Merge layers", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string? name = TextPromptWindow.Ask(this, "Merge layers", $"Name of the merged layer ({selected.Count} layers):", selected[0].NewName.Trim());
        if (string.IsNullOrWhiteSpace(name)) return;
        foreach (var row in selected) row.NewName = name.Trim();
    }

    private void StripPrefix_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.NewName = LayerMergePolicy.ShortName(row.NewName);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.NewName = row.OriginalName;
    }

    private void UpdateSummary()
    {
        var edits = CurrentEdits();
        int after = PdfLayerEditService.CountAfter(_rows.Select(r => r.Id), edits, _info.Names);
        bool changed = edits.Count > 0;
        _summary.Text = changed ? $"{_rows.Count} layers → {after}" : $"{_rows.Count} layers";
        _saveButton.IsEnabled = _copyButton.IsEnabled = changed;
    }

    private List<LayerRename> CurrentEdits()
        => _rows.Where(r => r.Changed && r.NewName.Trim().Length > 0).Select(r => new LayerRename(r.Id, r.NewName.Trim())).ToList();

    // ── Lưu ─────────────────────────────────────────────────────────

    private void Accept(bool inPlace)
    {
        var edits = CurrentEdits();
        if (edits.Count == 0) return;
        if (inPlace)
        {
            if (AppDialog.Show(this, $"Write the layer changes into \"{Path.GetFileName(_path)}\"?\nThis changes the file itself and cannot be undone here.", "Manage layers",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }
        else
        {
            using var dlg = new System.Windows.Forms.SaveFileDialog
            {
                Title = "Save a copy with the layer changes", Filter = "PDF (*.pdf)|*.pdf", DefaultExt = "pdf",
                FileName = Path.GetFileNameWithoutExtension(_path) + " - layers.pdf",
                InitialDirectory = Path.GetDirectoryName(_path) is { } dir && Directory.Exists(dir) ? dir : ""
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            string output = Path.GetFullPath(dlg.FileName);
            if (string.Equals(output, Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
            {
                AppDialog.Show(this, "Choose a different file name, or use “Save to this file”.", "Manage layers", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            OutputPath = output;
        }
        Edits = edits;
        SaveInPlace = inPlace;
        DialogResult = true;
    }
}
