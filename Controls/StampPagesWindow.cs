using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// "Stamp pages": the owner's set of sample signatures and stamps (<see cref="StampLibrary"/>) in one list; pick one, click where it goes on a sample page, choose the pages, and the same
/// stamp is put at the same place (the same share of the page) on every chosen page, as one undoable change.
/// </summary>
internal sealed class StampPagesWindow : XTWindow
{
    private readonly string _path;
    private readonly int _pageCount;
    private int _samplePage;
    private double _pageWidth = 842, _pageHeight = 595;
    private readonly PageAreaPicker _picker = new() { MinHeight = 300 };
    private readonly ListBox _stamps = new() { Height = 220, SelectionMode = SelectionMode.Single };
    private readonly Slider _opacity = new() { Minimum = 20, Maximum = 100, Value = 100, Width = 160, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _nameDate = new() { Content = "Add my name and the date under it", Margin = new Thickness(0, 8, 0, 0) };
    private readonly RadioButton _thisPage, _allPages, _range;
    private readonly TextBox _rangeBox = new() { Width = 130, Height = 26, Margin = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "For example 1-3, 7" };
    private readonly TextBox _sampleBox = new() { Width = 46, Height = 24, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _apply = new() { Content = "Stamp the pages", Height = 28, Padding = new Thickness(14, 0, 14, 0), IsDefault = true };
    private readonly Button _close = new() { Content = "Close", Width = 90, Height = 28, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };

    public StampDefinition? Definition { get; private set; }
    public int Opacity { get; private set; } = 100;
    public bool AddNameAndDate { get; private set; }
    /// <summary>The pages to stamp (1 based) and where the stamp's top left corner goes, as fractions of the page.</summary>
    public IReadOnlyList<int> Pages { get; private set; } = Array.Empty<int>();
    public double U1 { get; private set; }
    public double V1 { get; private set; }

    internal PageAreaPicker Picker => _picker;
    internal ListBox StampList => _stamps;
    internal RadioButton AllPagesButton => _allPages;
    internal RadioButton RangeButton => _range;
    internal TextBox RangeBox => _rangeBox;
    internal Button ApplyButton => _apply;
    internal string StatusText => _status.Text;

    public StampPagesWindow(string path, int currentPage, int pageCount)
    {
        _path = path;
        _pageCount = Math.Max(1, pageCount);
        _samplePage = Math.Clamp(currentPage, 1, _pageCount);
        Title = "Stamp or sign many pages";
        TitleBarMode = TitleBarMode.Dialog;
        Width = 1000;
        Height = 740;
        MinWidth = 800;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

        var side = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        side.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(path), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = R("Ui.Text") });
        side.Children.Add(Label("1. Choose a signature or stamp"));
        side.Children.Add(_stamps);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var import = SmallButton("Add image…", "Add a scanned signature or seal (PNG, JPG) to your set");
        var create = SmallButton("New text stamp…", "A text stamp with a colour");
        var remove = SmallButton("Remove", "Take the chosen one out of your set");
        tools.Children.Add(import);
        tools.Children.Add(create);
        tools.Children.Add(remove);
        side.Children.Add(tools);
        var opacityRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        opacityRow.Children.Add(new TextBlock { Text = "Opacity", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = R("Ui.Text") });
        opacityRow.Children.Add(_opacity);
        side.Children.Add(opacityRow);
        side.Children.Add(_nameDate);
        side.Children.Add(Label("2. Click where it goes on the page"));
        side.Children.Add(Label("3. Pages"));
        _thisPage = new RadioButton { Content = $"This page ({_samplePage})", GroupName = "StampPages", Margin = new Thickness(0, 2, 0, 2) };
        _allPages = new RadioButton { Content = $"All {_pageCount} pages", GroupName = "StampPages", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
        _range = new RadioButton { Content = "Pages", GroupName = "StampPages", VerticalAlignment = VerticalAlignment.Center };
        _rangeBox.GotFocus += (_, _) => _range.IsChecked = true;
        side.Children.Add(_thisPage);
        side.Children.Add(_allPages);
        side.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0), Children = { _range, _rangeBox } });
        side.Children.Add(_status);

        var sampleBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var prev = new Button { Content = "◀", Width = 28, Height = 24 };
        var next = new Button { Content = "▶", Width = 28, Height = 24 };
        sampleBar.Children.Add(new TextBlock { Text = "Sample page:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Foreground = R("Ui.Text") });
        sampleBar.Children.Add(prev);
        sampleBar.Children.Add(_sampleBox);
        sampleBar.Children.Add(next);
        sampleBar.Children.Add(new TextBlock { Text = $"of {_pageCount}   (click or drag on the page to move the stamp)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Foreground = R("Ui.Text") });
        var pickerPanel = new DockPanel();
        DockPanel.SetDock(sampleBar, Dock.Top);
        pickerPanel.Children.Add(sampleBar);
        pickerPanel.Children.Add(_picker);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { _apply, _close } };
        var grid = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(pickerPanel, 1);
        Grid.SetRow(buttons, 1);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(side);
        grid.Children.Add(pickerPanel);
        grid.Children.Add(buttons);
        Content = new Border { Background = R("Ui.Surface"), Child = grid };

        FillStamps();
        _stamps.SelectionChanged += async (_, _) => await UpdatePlacementAsync();
        _picker.AreaChanged += () => _status.Text = "";
        _sampleBox.Text = _samplePage.ToString();
        prev.Click += async (_, _) => await GoToSampleAsync(_samplePage - 1);
        next.Click += async (_, _) => await GoToSampleAsync(_samplePage + 1);
        _sampleBox.LostFocus += async (_, _) => { if (int.TryParse(_sampleBox.Text, out int n)) await GoToSampleAsync(n); };
        _nameDate.Click += async (_, _) => await UpdatePlacementAsync();
        import.Click += (_, _) => ImportImage();
        create.Click += (_, _) =>
        {
            var definition = NewStampWindow.Ask(this);
            if (definition == null) return;
            StampLibrary.Add(definition);
            FillStamps(definition);
        };
        remove.Click += (_, _) =>
        {
            if (_stamps.SelectedItem is ListBoxItem { Tag: StampDefinition d } && StampLibrary.Mine.Contains(d)) { StampLibrary.Remove(d); FillStamps(); }
        };
        _apply.Click += (_, _) => Apply();
        Loaded += async (_, _) => await GoToSampleAsync(_samplePage);
        _status.Text = "Choose a signature or stamp, then click on the page.";
    }

    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 12, 0, 4), FontWeight = FontWeights.SemiBold };

    private static Button SmallButton(string text, string tip) => new() { Content = text, ToolTip = tip, Height = 26, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 6, 0) };

    private void FillStamps(StampDefinition? select = null)
    {
        _stamps.Items.Clear();
        foreach (var d in StampLibrary.Mine) _stamps.Items.Add(Item(d, "My set: "));
        foreach (var d in StampLibrary.Standard) _stamps.Items.Add(Item(d, ""));
        var target = select ?? StampLibrary.Mine.FirstOrDefault(d => d.IsImage) ?? StampLibrary.Mine.FirstOrDefault() ?? StampLibrary.Standard[0];
        foreach (ListBoxItem item in _stamps.Items) if (ReferenceEquals(item.Tag, target)) _stamps.SelectedItem = item;
    }

    private static ListBoxItem Item(StampDefinition d, string prefix)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (d.IsImage)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(d.ImagePath);
                bitmap.DecodePixelHeight = 40;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                row.Children.Add(new Image { Source = bitmap, Height = 26, Margin = new Thickness(0, 0, 8, 0) });
            }
            catch { /* the picture is gone: only the name is shown */ }
        }
        row.Children.Add(new TextBlock { Text = prefix + (d.IsImage ? d.Text : d.Text), VerticalAlignment = VerticalAlignment.Center, Foreground = d.IsImage ? null : new SolidColorBrush((Color)ColorConverter.ConvertFromString(d.Color.Length > 0 ? d.Color : "#C0392B")), FontWeight = d.IsImage ? FontWeights.Normal : FontWeights.Bold });
        return new ListBoxItem { Content = row, Tag = d };
    }

    private void ImportImage()
    {
        using var dlg = new System.Windows.Forms.OpenFileDialog { Title = "Add a signature or seal image", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp", Multiselect = true };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        StampDefinition? last = null;
        foreach (string file in dlg.FileNames)
        {
            try
            {
                last = new StampDefinition(StampDefinition.ImageKind, System.IO.Path.GetFileNameWithoutExtension(file), "", "", StampLibrary.ImportImage(file));
                StampLibrary.Add(last);
            }
            catch (Exception ex) { AppDialog.Show(this, "Could not add the image:\n" + ex.Message, "Add image", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        FillStamps(last);
    }

    private StampDefinition? Chosen => (_stamps.SelectedItem as ListBoxItem)?.Tag as StampDefinition;

    private string SubText() => _nameDate.IsChecked == true ? $"{Environment.UserName} · {DateTime.Now:dd/MM/yyyy}" : Chosen?.Sub ?? "";

    private async Task GoToSampleAsync(int page)
    {
        _samplePage = Math.Clamp(page, 1, _pageCount);
        _sampleBox.Text = _samplePage.ToString();
        var annotations = await AnnotationStore.GetPageAsync(_path, _samplePage);
        if (annotations != null) { _pageWidth = annotations.Geometry.DisplayWidth; _pageHeight = annotations.Geometry.DisplayHeight; }
        await _picker.ShowPageAsync(_path, _samplePage);
        await UpdatePlacementAsync();
    }

    /// <summary>The box of the chosen stamp on the sample page (its real size there) and, for a picture, the picture in it.</summary>
    internal Task UpdatePlacementAsync()
    {
        if (Chosen is not { } definition) return Task.CompletedTask;
        var (w, h) = PdfQuickAnnotationService.StampSize(definition, SubText(), _pageWidth);
        _picker.PlaceSize = (Math.Min(0.95, w / _pageWidth), Math.Min(0.95, h / _pageHeight));
        _picker.PlaceImage = definition.IsImage ? LoadPicture(definition.ImagePath) : null;
        var old = _picker.Area;
        // keep the corner where it was, or start at the bottom right
        double u1 = old?.U1 ?? 0.97 - _picker.PlaceSize.Value.W, v1 = old?.V1 ?? 0.96 - _picker.PlaceSize.Value.H;
        u1 = Math.Clamp(u1, 0, 1 - _picker.PlaceSize.Value.W);
        v1 = Math.Clamp(v1, 0, 1 - _picker.PlaceSize.Value.H);
        _picker.SetArea((u1, v1, u1 + _picker.PlaceSize.Value.W, v1 + _picker.PlaceSize.Value.H));
        return Task.CompletedTask;
    }

    private static BitmapImage? LoadPicture(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            return bitmap;
        }
        catch { return null; }
    }

    private List<int>? ChosenPages()
    {
        if (_thisPage.IsChecked == true) return new List<int> { _samplePage };
        if (_range.IsChecked == true) return OcrWindow.ParsePages(_rangeBox.Text, _pageCount)?.Select(p => p + 1).ToList();
        return Enumerable.Range(1, _pageCount).ToList();
    }

    private void Apply()
    {
        if (Chosen is not { } definition) { _status.Text = "Choose a signature or stamp first."; return; }
        if (_picker.Area is not { } area) { _status.Text = "Click on the page where the stamp goes."; return; }
        var pages = ChosenPages();
        if (pages == null) { _status.Text = $"The pages must be like 1-3, 7 (this file has {_pageCount})."; return; }
        Definition = definition;
        Opacity = (int)_opacity.Value;
        AddNameAndDate = _nameDate.IsChecked == true;
        Pages = pages;
        U1 = area.U1;
        V1 = area.V1;
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
    }

    internal void ApplyForTest() => Apply();
}
