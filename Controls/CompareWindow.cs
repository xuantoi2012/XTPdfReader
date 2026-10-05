using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>1 cặp trang cần so sánh: trang cũ, trang mới, nhãn (số hiệu + tên bản vẽ) và kết quả (% thay đổi) sau khi tính.</summary>
internal sealed class ComparePair : INotifyPropertyChanged
{
    private string _status = "…";

    public ComparePair(int oldPage, int newPage, string label) { OldPage = oldPage; NewPage = newPage; Label = label; }

    public int OldPage { get; }
    public int NewPage { get; }
    public string Label { get; }
    public string Status { get => _status; set { _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    public BitmapSource? OldImage { get; set; }
    public BitmapSource? NewImage { get; set; }
    public PageDiff.Result? Diff { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// "Compare versions": các bản vẽ khớp nhau giữa 2 file (theo số hiệu, hoặc theo thứ tự trang) với % thay đổi; chọn 1 bản vẽ để xem chồng
/// (đỏ = chỉ có ở bản cũ, xanh = chỉ có ở bản mới) hoặc xem riêng bản cũ / bản mới.
/// </summary>
internal sealed class CompareWindow : XTWindow
{
    private enum View { Overlay, Old, New }

    private readonly string _oldPath, _newPath;
    private readonly List<ComparePair> _pairs;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly ListBox _list;
    private readonly RadioButton _overlay, _old, _new;
    private readonly TextBlock _caption;
    private bool _closed;

    public CompareWindow(string oldPath, string newPath, IReadOnlyList<ComparePair> pairs)
    {
        _oldPath = oldPath;
        _newPath = newPath;
        _pairs = pairs.ToList();

        Title = "Compare versions";
        TitleBarMode = TitleBarMode.Dialog;
        Width = 1250;
        Height = 780;
        MinWidth = 900;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Closed += (_, _) => _closed = true;

        Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        var root = new DockPanel { Background = R("Ui.Surface") };

        _caption = new TextBlock { Margin = new Thickness(20, 12, 20, 6), Foreground = R("Ui.Muted"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel.SetDock(_caption, Dock.Top);
        root.Children.Add(_caption);

        _overlay = new RadioButton { Content = "Overlay", GroupName = "view", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
        _old = new RadioButton { Content = "Old", GroupName = "view", Margin = new Thickness(0, 0, 16, 0) };
        _new = new RadioButton { Content = "New", GroupName = "view", Margin = new Thickness(0, 0, 16, 0) };
        foreach (var radio in new[] { _overlay, _old, _new }) radio.Checked += (_, _) => ShowSelected();
        var legend = new TextBlock { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        legend.Inlines.Add(new System.Windows.Documents.Run("■ only in old") { Foreground = Brushes.Crimson });
        legend.Inlines.Add(new System.Windows.Documents.Run("   ■ only in new") { Foreground = Brushes.RoyalBlue });
        legend.Inlines.Add(new System.Windows.Documents.Run("   ■ unchanged") { Foreground = Brushes.Gray });
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 20, 8), Children = { _overlay, _old, _new, legend } };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        _list = new ListBox { Width = 330, BorderThickness = new Thickness(0, 1, 1, 0), Background = Brushes.Transparent, ItemsSource = _pairs, ItemTemplate = BuildTemplate() };
        _list.SetResourceReference(Control.BorderBrushProperty, "Ui.Border");
        _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "UiRowItem");
        _list.SelectionChanged += (_, _) => ShowSelected();
        DockPanel.SetDock(_list, Dock.Left);
        root.Children.Add(_list);

        root.Children.Add(new Border { Background = R("Ui.Panel"), BorderBrush = R("Ui.Border"), BorderThickness = new Thickness(0, 1, 0, 0), Child = _image });
        Content = root;
        Loaded += async (_, _) => await ComputeAllAsync();
    }

    private static DataTemplate BuildTemplate()
    {
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 4, 12, 4));
        var c0 = new FrameworkElementFactory(typeof(ColumnDefinition)); c0.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var c1 = new FrameworkElementFactory(typeof(ColumnDefinition)); c1.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        grid.AppendChild(c0); grid.AppendChild(c1);
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ComparePair.Label)));
        label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        grid.AppendChild(label);
        var status = new FrameworkElementFactory(typeof(TextBlock));
        status.SetValue(Grid.ColumnProperty, 1);
        status.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ComparePair.Status)));
        status.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        status.SetValue(TextBlock.FontSizeProperty, 11.5);
        status.SetValue(UIElement.OpacityProperty, 0.75);
        grid.AppendChild(status);
        return new DataTemplate { VisualTree = grid };
    }

    /// <summary>Vẽ lần lượt từng cặp trang (nền) và tính chồng; danh sách cập nhật % thay đổi dần, trang đang chọn hiện ngay khi xong.</summary>
    private async Task ComputeAllAsync()
    {
        _caption.Text = $"{System.IO.Path.GetFileName(_oldPath)}  →  {System.IO.Path.GetFileName(_newPath)} · {_pairs.Count} matched sheet(s)";
        if (_pairs.Count > 0) _list.SelectedIndex = 0;
        foreach (var pair in _pairs)
        {
            if (_closed) return;
            try
            {
                pair.OldImage = await PdfThumbnailService.RenderPageAsync(_oldPath, pair.OldPage - 1, 1600, layerToken: PdfLayerStateStore.GetToken(_oldPath));
                pair.NewImage = await PdfThumbnailService.RenderPageAsync(_newPath, pair.NewPage - 1, 1600, layerToken: PdfLayerStateStore.GetToken(_newPath));
                if (pair.OldImage == null || pair.NewImage == null) { pair.Status = "cannot render"; continue; }
                var old = pair.OldImage; var @new = pair.NewImage;
                pair.Diff = await Task.Run(() => PageDiff.Overlay(old, @new));
                pair.Status = pair.Diff.OldOnly + pair.Diff.NewOnly == 0 ? "identical" : $"{pair.Diff.ChangedFraction * 100:0.#}% changed";
            }
            catch (Exception ex) { pair.Status = "error"; _ = ex; }
            if (ReferenceEquals(_list.SelectedItem, pair)) ShowSelected();
        }
    }

    private void ShowSelected()
    {
        if (_list == null || _image == null) return;
        if (_list.SelectedItem is not ComparePair pair) { _image.Source = null; return; }
        _image.Source = _old.IsChecked == true ? pair.OldImage : _new.IsChecked == true ? pair.NewImage : pair.Diff?.Overlay;
    }
}
