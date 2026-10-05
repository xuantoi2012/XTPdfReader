using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using iText.Kernel.Pdf;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>1 dòng kết quả đọc: số trang và số hiệu / tên / tỷ lệ (sửa được trước khi ghi).</summary>
internal sealed class SheetReadRow : INotifyPropertyChanged
{
    private string _number, _title, _scale;

    public SheetReadRow(int page, string number, string title, string scale, string note)
    {
        Page = page; _number = number; _title = title; _scale = scale; Note = note;
    }

    public int Page { get; }
    public string Note { get; }
    public string Number { get => _number; set { _number = value; Changed(nameof(Number)); } }
    public string Title { get => _title; set { _title = value; Changed(nameof(Title)); } }
    public string Scale { get => _scale; set { _scale = value; Changed(nameof(Scale)); } }
    public bool HasData => Number.Trim().Length > 0 || Title.Trim().Length > 0;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>
/// "Read sheet info": người dùng khoanh vùng số hiệu / tên / tỷ lệ trên 1 trang mẫu (lưu theo khổ giấy), app đọc chữ trong vùng đó cho mọi trang cùng khổ,
/// hiện bảng để sửa, rồi ghi thành thông tin sheet (/XTSheet) vào file — từ đó tab Sheets, tìm theo số hiệu, thay revision… chạy được trên PDF bất kỳ.
/// Chỉ đọc lớp chữ của PDF (xuất từ CAD); bản scan chưa có lớp chữ cần OCR (sau).
/// </summary>
internal sealed class ReadSheetInfoWindow : XTWindow
{
    private enum Field { Number, Title, Scale }

    private readonly string _path;
    private readonly IReadOnlyList<int> _pages;
    private readonly int _samplePage;
    private readonly TitleBlockLayout _layout = new();
    private string _sizeKey = "";
    private readonly Canvas _overlay = new();
    private readonly Image _image = new();
    private readonly RadioButton _numberRadio, _titleRadio, _scaleRadio;
    private readonly ListBox _rows;
    private readonly TextBlock _status;
    private readonly XTButton _readButton, _applyButton;
    private Point? _dragStart;
    private Rectangle? _dragShape;
    private double _pixelW = 1, _pixelH = 1;
    private List<SheetReadRow> _results = new();

    /// <summary>(trang 1-based, thông tin) cần ghi vào file sau khi bấm Apply.</summary>
    public IReadOnlyList<(int Page, XTSheetPageInfo Info)> Result { get; private set; } = Array.Empty<(int, XTSheetPageInfo)>();

    public ReadSheetInfoWindow(string path, IReadOnlyList<int> pages, int samplePage)
    {
        _path = path;
        _pages = pages;
        _samplePage = samplePage;

        Title = "Read sheet info from the PDF text";
        TitleBarMode = TitleBarMode.Dialog;
        Width = 1100;
        Height = 720;
        MinWidth = 900;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        var root = new DockPanel { Background = R("Ui.Surface") };

        // chân
        _applyButton = new XTButton { Text = "Write sheet info to the file", Height = 34, Padding = new Thickness(14, 0, 14, 0), IsEnabled = false };
        _applyButton.SetResourceReference(StyleProperty, "UiPrimaryButton");
        _applyButton.Click += (_, _) => Apply();
        var cancel = new XTButton { Text = "Cancel", Width = 90, Height = 34, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.SetResourceReference(StyleProperty, "UiGhostButton");
        cancel.Click += (_, _) => DialogResult = false;
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = R("Ui.Muted"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { cancel, _applyButton } };
        DockPanel.SetDock(buttons, Dock.Right);
        var footer = new Border
        {
            BorderBrush = R("Ui.Border"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(20, 12, 20, 12), Background = R("Ui.Panel"),
            Child = new DockPanel { Children = { buttons, _status } }
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // đầu: chọn trường + đọc
        _numberRadio = new RadioButton { Content = "Sheet number", GroupName = "field", IsChecked = true, Margin = new Thickness(0, 0, 14, 0), Foreground = Brushes.Crimson };
        _titleRadio = new RadioButton { Content = "Title", GroupName = "field", Margin = new Thickness(0, 0, 14, 0), Foreground = Brushes.RoyalBlue };
        _scaleRadio = new RadioButton { Content = "Scale", GroupName = "field", Margin = new Thickness(0, 0, 14, 0), Foreground = Brushes.SeaGreen };
        _readButton = new XTButton { Text = "Read all pages", Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(8, 0, 0, 0) };
        _readButton.SetResourceReference(StyleProperty, "UiPrimaryButton");
        _readButton.Click += async (_, _) => await ReadAllAsync();
        var head = new StackPanel { Margin = new Thickness(20, 14, 20, 8) };
        head.Children.Add(new TextBlock
        {
            Text = "1. Pick a field, then drag a rectangle around that text in the title block of the sample page.   2. Read all pages.   3. Fix any wrong cell and write the result to the file.",
            TextWrapping = TextWrapping.Wrap, Foreground = R("Ui.Muted"), FontSize = 12, Margin = new Thickness(0, 0, 0, 8)
        });
        head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _numberRadio, _titleRadio, _scaleRadio, _readButton } });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        // trái: trang mẫu + vùng; phải: bảng kết quả
        var grid = new Grid { Margin = new Thickness(20, 0, 20, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 340 });

        _overlay.Background = Brushes.Transparent;
        _overlay.Cursor = Cursors.Cross;
        _overlay.MouseLeftButtonDown += Overlay_MouseDown;
        _overlay.MouseMove += Overlay_MouseMove;
        _overlay.MouseLeftButtonUp += Overlay_MouseUp;
        var surface = new Grid { Children = { _image, _overlay } };
        var preview = new Border
        {
            BorderBrush = R("Ui.Border"), BorderThickness = new Thickness(1), Background = R("Ui.Panel"), ClipToBounds = true,
            Child = new Viewbox { Stretch = Stretch.Uniform, Child = surface }
        };
        Grid.SetColumn(preview, 0);
        grid.Children.Add(preview);

        _rows = new ListBox { BorderThickness = new Thickness(1), Background = Brushes.Transparent, BorderBrush = R("Ui.Border"), ItemTemplate = BuildRowTemplate() };
        ScrollViewer.SetHorizontalScrollBarVisibility(_rows, ScrollBarVisibility.Disabled);
        _rows.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "UiRowItem");
        var table = new DockPanel();
        var header = new TextBlock { Text = "Result (edit any cell)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(header, Dock.Top);
        table.Children.Add(header);
        table.Children.Add(_rows);
        Grid.SetColumn(table, 2);
        grid.Children.Add(table);
        root.Children.Add(grid);

        Content = root;
        Loaded += async (_, _) => await LoadSampleAsync();
    }

    private static DataTemplate BuildRowTemplate()
    {
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 2, 4, 2));
        foreach (var width in new[] { new GridLength(34), new GridLength(1, GridUnitType.Star), new GridLength(2, GridUnitType.Star), new GridLength(54) })
        {
            var column = new FrameworkElementFactory(typeof(ColumnDefinition));
            column.SetValue(ColumnDefinition.WidthProperty, width);
            grid.AppendChild(column);
        }
        var page = new FrameworkElementFactory(typeof(TextBlock));
        page.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(SheetReadRow.Page)));
        page.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        page.SetValue(UIElement.OpacityProperty, 0.65);
        grid.AppendChild(page);
        FrameworkElementFactory Box(string property, int column)
        {
            var box = new FrameworkElementFactory(typeof(TextBox));
            box.SetValue(Grid.ColumnProperty, column);
            box.SetValue(FrameworkElement.HeightProperty, 26.0);
            box.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
            box.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
            box.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding(property)
            {
                Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
            });
            return box;
        }
        grid.AppendChild(Box(nameof(SheetReadRow.Number), 1));
        grid.AppendChild(Box(nameof(SheetReadRow.Title), 2));
        grid.AppendChild(Box(nameof(SheetReadRow.Scale), 3));
        return new DataTemplate { VisualTree = grid };
    }

    // ── Trang mẫu và vùng ───────────────────────────────────────────

    private async Task LoadSampleAsync()
    {
        var bitmap = await PdfThumbnailService.RenderPageAsync(_path, _samplePage - 1, 1400, layerToken: PdfLayerStateStore.GetToken(_path));
        if (bitmap == null) { _status.Text = "Could not render the sample page."; return; }
        _image.Source = bitmap;
        _pixelW = bitmap.PixelWidth; _pixelH = bitmap.PixelHeight;
        _image.Width = _pixelW; _image.Height = _pixelH;
        _overlay.Width = _pixelW; _overlay.Height = _pixelH;

        // khổ giấy của trang mẫu + vùng đã lưu cho khổ đó
        await Task.Run(() =>
        {
            using var doc = new PdfDocument(new PdfReader(_path));
            _sizeKey = TitleBlockReader.SizeKey(doc.GetPage(_samplePage));
        });
        if (TitleBlockStore.Get(_sizeKey) is { } saved)
        {
            _layout.Number = saved.Number; _layout.Title = saved.Title; _layout.Scale = saved.Scale;
        }
        DrawRegions();
        _status.Text = $"Sample page {_samplePage} · paper {_sizeKey}" + (_layout.HasAny ? " · areas restored from last time" : " · draw the areas");
    }

    private Brush BrushFor(Field field) => field switch { Field.Number => Brushes.Crimson, Field.Title => Brushes.RoyalBlue, _ => Brushes.SeaGreen };
    private Field Selected => _titleRadio.IsChecked == true ? Field.Title : _scaleRadio.IsChecked == true ? Field.Scale : Field.Number;
    private TitleBlockRegion? RegionOf(Field f) => f switch { Field.Number => _layout.Number, Field.Title => _layout.Title, _ => _layout.Scale };

    private void DrawRegions()
    {
        _overlay.Children.Clear();
        foreach (var field in new[] { Field.Number, Field.Title, Field.Scale })
        {
            if (RegionOf(field) is not { IsEmpty: false } r) continue;
            var shape = new Rectangle
            {
                Width = (r.Right - r.Left) * _pixelW, Height = (r.Bottom - r.Top) * _pixelH, Stroke = BrushFor(field), StrokeThickness = 3,
                Fill = new SolidColorBrush(Color.FromArgb(40, ((SolidColorBrush)BrushFor(field)).Color.R, ((SolidColorBrush)BrushFor(field)).Color.G, ((SolidColorBrush)BrushFor(field)).Color.B))
            };
            Canvas.SetLeft(shape, r.Left * _pixelW);
            Canvas.SetTop(shape, r.Top * _pixelH);
            _overlay.Children.Add(shape);
        }
    }

    private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(_overlay);
        _dragShape = new Rectangle { Stroke = BrushFor(Selected), StrokeThickness = 3, StrokeDashArray = new DoubleCollection { 4, 2 } };
        _overlay.Children.Add(_dragShape);
        _overlay.CaptureMouse();
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || _dragShape == null) return;
        var p = e.GetPosition(_overlay);
        Canvas.SetLeft(_dragShape, Math.Min(start.X, p.X));
        Canvas.SetTop(_dragShape, Math.Min(start.Y, p.Y));
        _dragShape.Width = Math.Abs(p.X - start.X);
        _dragShape.Height = Math.Abs(p.Y - start.Y);
    }

    private void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not { } start) return;
        var end = e.GetPosition(_overlay);
        _overlay.ReleaseMouseCapture();
        _dragStart = null;
        _dragShape = null;
        double x0 = Math.Clamp(Math.Min(start.X, end.X) / _pixelW, 0, 1), x1 = Math.Clamp(Math.Max(start.X, end.X) / _pixelW, 0, 1);
        double y0 = Math.Clamp(Math.Min(start.Y, end.Y) / _pixelH, 0, 1), y1 = Math.Clamp(Math.Max(start.Y, end.Y) / _pixelH, 0, 1);
        var region = new TitleBlockRegion(x0, y0, x1, y1);
        if (!region.IsEmpty)
            switch (Selected)
            {
                case Field.Number: _layout.Number = region; break;
                case Field.Title: _layout.Title = region; break;
                default: _layout.Scale = region; break;
            }
        DrawRegions();
    }

    // ── Đọc và ghi ──────────────────────────────────────────────────

    internal Task ReadAllForTestAsync() => ReadAllAsync();

    private async Task ReadAllAsync()
    {
        if (!_layout.HasAny) { _status.Text = "Draw at least one area first."; return; }
        TitleBlockStore.Set(_sizeKey, new TitleBlockLayout { Number = _layout.Number, Title = _layout.Title, Scale = _layout.Scale });
        _readButton.IsEnabled = false;
        _status.Text = "Reading…";
        var missing = new HashSet<string>();
        List<TitleBlockReading> readings;
        try
        {
            readings = await Task.Run(() => TitleBlockReader.Read(_path, _pages, (number, page) =>
            {
                string key = TitleBlockReader.SizeKey(page);
                var layout = key == _sizeKey ? _layout : TitleBlockStore.Get(key);
                if (layout == null) lock (missing) missing.Add(key);
                return layout;
            }));
        }
        catch (Exception ex) { _status.Text = "Could not read the file: " + ex.Message; _readButton.IsEnabled = true; return; }

        _results = readings.Select(r => new SheetReadRow(r.Page, r.Number, r.Title, r.Scale, "")).ToList();
        foreach (var row in _results) row.PropertyChanged += (_, _) => UpdateApply();
        _rows.ItemsSource = _results;
        int filled = _results.Count(r => r.HasData);
        _status.Text = $"{filled} of {_results.Count} pages have text in the areas" +
            (filled == 0 ? " — this PDF probably has no text layer (a scan or text converted to lines); OCR is needed." : "") +
            (missing.Count > 0 ? $" · no area set for: {string.Join(", ", missing)} (open a page of that size once and draw its areas)." : "");
        _readButton.IsEnabled = true;
        UpdateApply();
    }

    private void UpdateApply() => _applyButton.IsEnabled = _results.Any(r => r.HasData);

    private void Apply()
    {
        Result = _results.Where(r => r.HasData).Select(r => (r.Page, new XTSheetPageInfo { No = r.Number.Trim(), Title = r.Title.Trim(), Scale = r.Scale.Trim() })).ToList();
        DialogResult = true;
    }
}
