using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using iText.Kernel.Pdf;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>One page of the sheet-info window: its sheet number, title and scale (editable), where each came from, and a small picture for the list.</summary>
internal sealed class SheetReadRow : INotifyPropertyChanged
{
    private string _number = "", _title = "", _scale = "";
    private bool _numberOcr, _titleOcr, _scaleOcr, _read;
    private ImageSource? _thumbnail;
    private bool _thumbnailRequested;

    public SheetReadRow(int page, string sizeKey = "", double widthPt = 0, double heightPt = 0)
    {
        Page = page; SizeKey = sizeKey; WidthPt = widthPt; HeightPt = heightPt;
    }

    public int Page { get; }
    public string SizeKey { get; set; }
    public double WidthPt { get; }
    public double HeightPt { get; }

    /// <summary>The user typed in a field: re-reading keeps it (unless asked not to).</summary>
    public bool NumberEdited { get; private set; }
    public bool TitleEdited { get; private set; }
    public bool ScaleEdited { get; private set; }

    public string Number { get => _number; set { if (_number == value) return; _number = value; NumberEdited = true; _numberOcr = false; Refresh(nameof(Number)); } }
    public string Title { get => _title; set { if (_title == value) return; _title = value; TitleEdited = true; _titleOcr = false; Refresh(nameof(Title)); } }
    public string Scale { get => _scale; set { if (_scale == value) return; _scale = value; ScaleEdited = true; _scaleOcr = false; Refresh(nameof(Scale)); } }

    public bool NumberFromOcr => _numberOcr;
    public bool TitleFromOcr => _titleOcr;
    public bool ScaleFromOcr => _scaleOcr;
    public bool HasBeenRead => _read;

    public bool HasData => Number.Trim().Length > 0 || Title.Trim().Length > 0;

    /// <summary>Number and title on one line, for the list.</summary>
    public string Summary => (Number.Trim() + "  " + Title.Trim()).Trim();

    /// <summary>Grey: not read yet; red: read, nothing there; green: read from the PDF text; amber: read by OCR (worth a look); blue: typed by the user.</summary>
    public Brush StatusBrush => NumberEdited || TitleEdited || ScaleEdited ? Brushes.RoyalBlue
        : !_read ? Brushes.LightGray
        : !HasData ? Brushes.IndianRed
        : _numberOcr || _titleOcr || _scaleOcr ? Brushes.Orange : Brushes.SeaGreen;

    public ImageSource? Thumbnail { get => _thumbnail; set { _thumbnail = value; Changed(nameof(Thumbnail)); } }

    /// <summary>Sets what was read (not what the user typed): <paramref name="keepEdits"/> leaves a field the user changed alone.</summary>
    public void SetRead(SheetFieldRead number, SheetFieldRead title, SheetFieldRead scale, bool keepEdits)
    {
        if (!(keepEdits && NumberEdited)) { _number = number.Text; _numberOcr = number.FromOcr; NumberEdited = false; }
        if (!(keepEdits && TitleEdited)) { _title = title.Text; _titleOcr = title.FromOcr; TitleEdited = false; }
        if (!(keepEdits && ScaleEdited)) { _scale = scale.Text; _scaleOcr = scale.FromOcr; ScaleEdited = false; }
        _read = true;
        Refresh(nameof(Number)); Refresh(nameof(Title)); Refresh(nameof(Scale));
    }

    /// <summary>True the first time it is asked (the list loads a picture once per page).</summary>
    public bool TakeThumbnailRequest()
    {
        if (_thumbnailRequested) return false;
        _thumbnailRequested = true;
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    private void Refresh(string name)
    {
        Changed(name);
        Changed(nameof(Summary)); Changed(nameof(HasData)); Changed(nameof(StatusBrush));
        Changed(nameof(NumberFromOcr)); Changed(nameof(TitleFromOcr)); Changed(nameof(ScaleFromOcr));
    }
}

/// <summary>
/// "Read sheet info": the title block's areas (sheet number, title, scale) are drawn once on any page and kept per paper size; the program reads the text in
/// them on every page of that size, from the PDF's text layer or, where there is none, by OCR of just that area. Three columns: the pages (picture, what was
/// read, how), the page with its areas (zoom and pan, move and resize an area), and the fields of that page. Edit any field, then write the result to the file.
/// </summary>
internal sealed class ReadSheetInfoWindow : XTWindow
{
    private enum Field { Number, Title, Scale }

    private readonly string _path;
    private readonly IReadOnlyList<int> _pages;
    private readonly int _startPage;
    private readonly List<SheetReadRow> _rows = new();
    private readonly Dictionary<string, TitleBlockLayout> _layouts = new();
    private SheetReadRow? _current;

    // columns
    private readonly ListBox _pageList = new();
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    private readonly Grid _surface = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Canvas _overlay = new() { Background = Brushes.Transparent, Cursor = Cursors.Cross };
    private readonly ListBox _table = new();
    private readonly DockPanel _pagePanel = new();
    private readonly TextBlock _zoomText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0), MinWidth = 44, TextAlignment = TextAlignment.Center };

    // right column
    private readonly RadioButton _numberRadio, _titleRadio, _scaleRadio;
    private readonly TextBox _numberBox = new(), _titleBox = new(), _scaleBox = new();
    private readonly TextBlock _pageHeader = new() { FontWeight = FontWeights.SemiBold, FontSize = 15 };
    private readonly TextBlock _pageInfo = new() { Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 2, 0, 10) };
    private readonly TextBlock _sourceNote = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    // header / footer
    private readonly ComboBox _sourceBox = new() { Width = 220, Height = 28, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _keepEdits = new() { Content = "Keep what I typed", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly XTButton _readAll, _readPage, _applyButton;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress = new() { Width = 130, Height = 6, Margin = new Thickness(0, 0, 14, 0), Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton _pageView, _tableView;

    // preview state
    private double _pixelW = 1000, _pixelH = 700, _zoom = 1;
    private const double BaseWidth = 1000;
    private int _renderedWidth;
    private bool _fitMode = true;
    private int _loadVersion;
    private readonly DispatcherTimer _rerender = new() { Interval = TimeSpan.FromMilliseconds(220) };

    // area editing
    private enum Drag { None, Draw, Move, Resize, Pan }
    private Drag _drag;
    private Field _dragField;
    private Point _dragStart;
    private TitleBlockRegion? _dragOrigin;
    private string _resizeHandle = "";
    private Rectangle? _dragShape;
    private Point _panStart;
    private double _panX, _panY;

    private CancellationTokenSource? _cancel;
    private bool _busy;
    private bool _binding;
    private static readonly SemaphoreSlim ThumbnailGate = new(2);

    /// <summary>(page 1-based, info) to write after the user presses the write button.</summary>
    public IReadOnlyList<(int Page, XTSheetPageInfo Info)> Result { get; private set; } = Array.Empty<(int, XTSheetPageInfo)>();

    internal IReadOnlyList<SheetReadRow> Rows => _rows;
    internal SheetReadRow? CurrentRow => _current;
    internal ComboBox SourceBox => _sourceBox;
    internal double Zoom => _zoom;
    internal bool IsBusy => _busy;
    internal string StatusText => _status.Text;
    internal TextBox NumberBox => _numberBox;
    internal TextBox TitleBox => _titleBox;
    internal ListBox PageList => _pageList;
    internal bool TableVisible => _table.Visibility == Visibility.Visible;

    public ReadSheetInfoWindow(string path, IReadOnlyList<int> pages, int samplePage)
    {
        _path = path;
        _pages = pages;
        _startPage = pages.Contains(samplePage) ? samplePage : pages.FirstOrDefault(1);

        Title = "Read sheet info";
        TitleBarMode = TitleBarMode.Dialog;
        Width = 1320;
        Height = 820;
        MinWidth = 1040;
        MinHeight = 600;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        var root = new DockPanel { Background = R("Ui.Surface") };

        // ── footer
        _applyButton = new XTButton { Text = "Write sheet info to the file", Height = 34, Padding = new Thickness(14, 0, 14, 0), IsEnabled = false };
        _applyButton.SetResourceReference(StyleProperty, "UiPrimaryButton");
        _applyButton.Click += (_, _) => Apply();
        var cancel = new XTButton { Text = "Cancel", Width = 90, Height = 34, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.SetResourceReference(StyleProperty, "UiGhostButton");
        cancel.Click += (_, _) => { if (_busy) _cancel?.Cancel(); else DialogResult = false; };
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = R("Ui.Muted"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { _progress, cancel, _applyButton } };
        DockPanel.SetDock(buttons, Dock.Right);
        var footer = new Border
        {
            BorderBrush = R("Ui.Border"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(20, 12, 20, 12), Background = R("Ui.Panel"),
            Child = new DockPanel { Children = { buttons, _status } }
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // ── header
        _readAll = new XTButton { Text = "Read all pages", Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(14, 0, 0, 0) };
        _readAll.SetResourceReference(StyleProperty, "UiPrimaryButton");
        _readAll.Click += async (_, _) => await ReadAsync(_pages);
        _readPage = new XTButton { Text = "Read this page", Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(8, 0, 0, 0) };
        _readPage.SetResourceReference(StyleProperty, "UiGhostButton");
        _readPage.Click += async (_, _) => await ReadAsync(SelectedPages());
        foreach (string name in new[] { "Auto: PDF text, OCR where there is none", "PDF text only", "OCR always" }) _sourceBox.Items.Add(name);
        _sourceBox.SelectedIndex = 0;
        _sourceBox.ToolTip = "Where the text is read from";
        _pageView = new RadioButton { Content = "Page", GroupName = "view", IsChecked = true, Margin = new Thickness(18, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        _tableView = new RadioButton { Content = "Table", GroupName = "view", VerticalAlignment = VerticalAlignment.Center, ToolTip = "All pages in one table, to fix many at once" };
        _pageView.Checked += (_, _) => ShowView();
        _tableView.Checked += (_, _) => ShowView();
        var head = new StackPanel { Margin = new Thickness(20, 12, 20, 8) };
        head.Children.Add(new TextBlock
        {
            Text = "Draw the areas of the title block on any page (they are kept per paper size), then read. Drag an area to move it, its squares to resize it. Ctrl + wheel zooms, right-drag pans.",
            TextWrapping = TextWrapping.Wrap, Foreground = R("Ui.Muted"), FontSize = 12, Margin = new Thickness(0, 0, 0, 8)
        });
        head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = "Read from", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, _sourceBox, _readAll, _readPage, _keepEdits, _pageView, _tableView } });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        // ── body: pages | page | fields
        var body = new Grid { Margin = new Thickness(20, 0, 20, 14) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250), MinWidth = 190 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 360 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310), MinWidth = 260 });

        _pageList.BorderThickness = new Thickness(1);
        _pageList.BorderBrush = R("Ui.Border");
        _pageList.Background = Brushes.Transparent;
        _pageList.SelectionMode = SelectionMode.Extended;
        _pageList.ItemTemplate = BuildPageTemplate();
        ScrollViewer.SetHorizontalScrollBarVisibility(_pageList, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(_pageList, true);
        _pageList.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "UiRowItem");
        _pageList.SelectionChanged += (_, _) =>
        {
            var first = _pageList.SelectedItems.OfType<SheetReadRow>().OrderBy(r => r.Page).FirstOrDefault();
            if (_pageList.SelectedItems.Count == 1 && first != null && !ReferenceEquals(first, _current)) _ = ShowPageAsync(first);
            UpdateButtons();
        };
        Grid.SetColumn(_pageList, 0);
        body.Children.Add(_pageList);
        var splitter1 = new GridSplitter { Width = 8, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(splitter1, 1);
        body.Children.Add(splitter1);

        // the page: a zoom bar, then the scrolling page with its areas
        _surface.Children.Add(_image);
        _surface.Children.Add(_overlay);
        _surface.HorizontalAlignment = HorizontalAlignment.Left;
        _surface.VerticalAlignment = VerticalAlignment.Top;
        _surface.Margin = new Thickness(12);
        _surface.Background = Brushes.White;
        _surface.Width = _pixelW;
        _surface.Height = _pixelH;
        _scroll.Content = _surface;
        _overlay.MouseLeftButtonDown += Overlay_LeftDown;
        _overlay.MouseMove += Overlay_Move;
        _overlay.MouseLeftButtonUp += Overlay_LeftUp;
        _scroll.PreviewMouseRightButtonDown += Scroll_PanStart;
        _scroll.PreviewMouseDown += (s, e) => { if (e.ChangedButton == MouseButton.Middle) Scroll_PanStart(s, e); };
        _scroll.PreviewMouseMove += Scroll_PanMove;
        _scroll.PreviewMouseUp += Scroll_PanEnd;
        _scroll.PreviewMouseWheel += Scroll_Wheel;
        _scroll.SizeChanged += (_, _) => { if (_fitMode) FitToWindow(); };
        _rerender.Tick += async (_, _) => { _rerender.Stop(); await RenderCurrentAsync(); };

        var zoomBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        zoomBar.Children.Add(SmallButton("Fit", "Fit the page to the window (Ctrl+0)", () => FitToWindow()));
        zoomBar.Children.Add(SmallButton("100%", "Actual size of the sheet", () => SetZoomAround(ActualSizeZoom(), null)));
        zoomBar.Children.Add(SmallButton("−", "Zoom out", () => SetZoomAround(_zoom / 1.25, null)));
        zoomBar.Children.Add(_zoomText);
        zoomBar.Children.Add(SmallButton("+", "Zoom in", () => SetZoomAround(_zoom * 1.25, null)));
        zoomBar.Children.Add(SmallButton("◀", "Previous page (PgUp)", () => Step(-1)));
        zoomBar.Children.Add(SmallButton("▶", "Next page (PgDn)", () => Step(1)));
        DockPanel.SetDock(zoomBar, Dock.Top);
        var previewFrame = new Border { BorderBrush = R("Ui.Border"), BorderThickness = new Thickness(1), Background = R("Ui.Panel"), ClipToBounds = true, Child = _scroll };
        _pagePanel.Children.Add(zoomBar);
        _pagePanel.Children.Add(previewFrame);

        _table.BorderThickness = new Thickness(1);
        _table.BorderBrush = R("Ui.Border");
        _table.Background = Brushes.Transparent;
        _table.ItemTemplate = BuildTableTemplate();
        _table.Visibility = Visibility.Collapsed;
        ScrollViewer.SetHorizontalScrollBarVisibility(_table, ScrollBarVisibility.Disabled);
        _table.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "UiRowItem");
        var center = new Grid { Children = { _pagePanel, _table } };
        Grid.SetColumn(center, 2);
        body.Children.Add(center);
        var splitter2 = new GridSplitter { Width = 8, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(splitter2, 3);
        body.Children.Add(splitter2);

        // the fields of the page
        _numberRadio = new RadioButton { Content = "Sheet number", GroupName = "field", IsChecked = true, Foreground = Brushes.Crimson, FontWeight = FontWeights.SemiBold, ToolTip = "Draw this area (key 1)" };
        _titleRadio = new RadioButton { Content = "Title", GroupName = "field", Foreground = Brushes.RoyalBlue, FontWeight = FontWeights.SemiBold, ToolTip = "Draw this area (key 2)" };
        _scaleRadio = new RadioButton { Content = "Scale", GroupName = "field", Foreground = Brushes.SeaGreen, FontWeight = FontWeights.SemiBold, ToolTip = "Draw this area (key 3)" };
        foreach (var radio in new[] { _numberRadio, _titleRadio, _scaleRadio }) radio.Checked += (_, _) => DrawRegions();
        var fields = new StackPanel { Margin = new Thickness(14) };
        fields.Children.Add(_pageHeader);
        fields.Children.Add(_pageInfo);
        fields.Children.Add(FieldBlock(_numberRadio, _numberBox, Field.Number));
        fields.Children.Add(FieldBlock(_titleRadio, _titleBox, Field.Title));
        fields.Children.Add(FieldBlock(_scaleRadio, _scaleBox, Field.Scale));
        fields.Children.Add(_sourceNote);
        var right = new Border { BorderBrush = R("Ui.Border"), BorderThickness = new Thickness(1), Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = fields } };
        Grid.SetColumn(right, 4);
        body.Children.Add(right);
        root.Children.Add(body);

        Content = root;
        foreach (var box in new[] { _numberBox, _titleBox, _scaleBox }) box.TextChanged += FieldBox_TextChanged;
        PreviewKeyDown += Window_KeyDown;
        Loaded += async (_, _) => await InitializeAsync();
        Closing += (_, _) => _cancel?.Cancel();
        UpdateZoomText();
    }

    // ── Pieces ──────────────────────────────────────────────────────

    private XTButton SmallButton(string text, string tip, Action click)
    {
        var button = new XTButton { Text = text, Height = 26, MinWidth = 34, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 4, 0), ToolTip = tip };
        button.SetResourceReference(StyleProperty, "UiGhostButton");
        button.Click += (_, _) => click();
        return button;
    }

    private UIElement FieldBlock(RadioButton radio, TextBox box, Field field)
    {
        box.Height = 30;
        box.VerticalContentAlignment = VerticalAlignment.Center;
        box.Margin = new Thickness(0, 4, 0, 0);
        box.Tag = field;
        var clear = new TextBlock { Text = "✕ area", Cursor = Cursors.Hand, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Remove this area (for this paper size)", Foreground = Brushes.Gray };
        clear.MouseLeftButtonUp += (_, _) => SetRegion(field, null);
        DockPanel.SetDock(clear, Dock.Right);
        var header = new DockPanel { Children = { clear, radio } };
        return new StackPanel { Margin = new Thickness(0, 0, 0, 12), Children = { header, box } };
    }

    private DataTemplate BuildPageTemplate()
    {
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 3, 4, 3));
        foreach (var width in new[] { new GridLength(62), new GridLength(1, GridUnitType.Star), new GridLength(12) })
        {
            var column = new FrameworkElementFactory(typeof(ColumnDefinition));
            column.SetValue(ColumnDefinition.WidthProperty, width);
            grid.AppendChild(column);
        }
        var picture = new FrameworkElementFactory(typeof(Image));
        picture.SetValue(FrameworkElement.WidthProperty, 54.0);
        picture.SetValue(FrameworkElement.HeightProperty, 40.0);
        picture.SetValue(Image.StretchProperty, Stretch.Uniform);
        picture.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(SheetReadRow.Thumbnail)));
        picture.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler(Thumbnail_Loaded));
        grid.AppendChild(picture);
        var texts = new FrameworkElementFactory(typeof(StackPanel));
        texts.SetValue(Grid.ColumnProperty, 1);
        texts.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var page = new FrameworkElementFactory(typeof(TextBlock));
        page.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(SheetReadRow.Page)) { StringFormat = "Page {0}" });
        page.SetValue(TextBlock.FontSizeProperty, 11.0);
        page.SetValue(UIElement.OpacityProperty, 0.6);
        texts.AppendChild(page);
        var summary = new FrameworkElementFactory(typeof(TextBlock));
        summary.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(SheetReadRow.Summary)));
        summary.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        texts.AppendChild(summary);
        grid.AppendChild(texts);
        var dot = new FrameworkElementFactory(typeof(Ellipse));
        dot.SetValue(Grid.ColumnProperty, 2);
        dot.SetValue(FrameworkElement.WidthProperty, 9.0);
        dot.SetValue(FrameworkElement.HeightProperty, 9.0);
        dot.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding(nameof(SheetReadRow.StatusBrush)));
        grid.AppendChild(dot);
        return new DataTemplate { VisualTree = grid };
    }

    private static DataTemplate BuildTableTemplate()
    {
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 2, 4, 2));
        foreach (var width in new[] { new GridLength(40), new GridLength(1, GridUnitType.Star), new GridLength(2, GridUnitType.Star), new GridLength(60) })
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

    private async void Thumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SheetReadRow row } || !row.TakeThumbnailRequest()) return;
        await ThumbnailGate.WaitAsync();
        try { row.Thumbnail = await PdfThumbnailService.RenderPageAsync(_path, row.Page - 1, 120, default, PdfRenderPriority.Background); }
        catch { /* the picture stays empty */ }
        finally { ThumbnailGate.Release(); }
    }

    // ── Start ───────────────────────────────────────────────────────

    private async Task InitializeAsync()
    {
        // paper size and size in points of every page (one pass of the file)
        var sizes = new Dictionary<int, (string Key, double W, double H)>();
        await Task.Run(() =>
        {
            using var doc = new PdfDocument(new PdfReader(_path));
            foreach (int number in _pages)
            {
                if (number < 1 || number > doc.GetNumberOfPages()) continue;
                var page = doc.GetPage(number);
                var box = page.GetMediaBox();
                int rotation = ((page.GetRotation() % 360) + 360) % 360;
                bool turned = rotation % 180 != 0;
                sizes[number] = (TitleBlockReader.SizeKey(page), turned ? box.GetHeight() : box.GetWidth(), turned ? box.GetWidth() : box.GetHeight());
            }
        });
        foreach (int number in _pages)
        {
            var (key, w, h) = sizes.TryGetValue(number, out var s) ? s : ("", 0, 0);
            _rows.Add(new SheetReadRow(number, key, w, h));
        }
        _pageList.ItemsSource = _rows;
        _table.ItemsSource = _rows;
        foreach (var row in _rows) row.PropertyChanged += (_, _) => UpdateButtons();
        var start = _rows.FirstOrDefault(r => r.Page == _startPage) ?? _rows.FirstOrDefault();
        if (start != null)
        {
            _pageList.SelectedItem = start;
            _pageList.ScrollIntoView(start);
        }
        UpdateButtons();
    }

    // ── The page and its areas ──────────────────────────────────────

    private TitleBlockLayout LayoutFor(string key)
    {
        if (_layouts.TryGetValue(key, out var layout)) return layout;
        var stored = TitleBlockStore.Get(key);
        layout = stored == null ? new TitleBlockLayout() : new TitleBlockLayout { Number = stored.Number, Title = stored.Title, Scale = stored.Scale };
        _layouts[key] = layout;
        return layout;
    }

    private TitleBlockLayout CurrentLayout => LayoutFor(_current?.SizeKey ?? "");

    private async Task ShowPageAsync(SheetReadRow row)
    {
        _current = row;
        int version = ++_loadVersion;
        _pageHeader.Text = $"Page {row.Page}";
        _pageInfo.Text = row.SizeKey.Length == 0 ? "" : $"paper {row.SizeKey}" + (CurrentLayout.HasAny ? "  ·  areas set" : "  ·  draw the areas");
        BindFields();
        _image.Source = null;
        _renderedWidth = 0;
        _overlay.Children.Clear();
        var bitmap = await PdfThumbnailService.RenderPageAsync(_path, row.Page - 1, 1400, layerToken: PdfLayerStateStore.GetToken(_path));
        if (version != _loadVersion) return;
        if (bitmap == null) { _status.Text = "Could not draw page " + row.Page + "."; return; }
        SetBitmap(bitmap);
        if (_fitMode) FitToWindow(); else ApplyZoom(_zoom);
        DrawRegions();
        UpdateButtons();
    }

    private void SetBitmap(BitmapSource bitmap)
    {
        _image.Source = bitmap;
        _renderedWidth = bitmap.PixelWidth;
        _pixelW = BaseWidth;
        _pixelH = BaseWidth * bitmap.PixelHeight / Math.Max(1, bitmap.PixelWidth);
        _image.Width = _overlay.Width = _surface.Width = _pixelW;
        _image.Height = _overlay.Height = _surface.Height = _pixelH;
    }

    private async Task RenderCurrentAsync()
    {
        if (_current == null) return;
        int version = _loadVersion;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int wanted = (int)Math.Clamp(BaseWidth * _zoom * dpi, 800, 5200);
        if (wanted <= _renderedWidth * 1.15 && wanted >= _renderedWidth * 0.5) return; // sharp enough: neither blurry nor wasteful
        var bitmap = await PdfThumbnailService.RenderPageAsync(_path, _current.Page - 1, wanted, layerToken: PdfLayerStateStore.GetToken(_path));
        if (bitmap == null || version != _loadVersion) return;
        _image.Source = bitmap;
        _renderedWidth = bitmap.PixelWidth;
    }

    private double ActualSizeZoom() => _current is { WidthPt: > 0 } c ? c.WidthPt * 96.0 / 72.0 / BaseWidth : 1.0;

    internal void FitToWindow()
    {
        _fitMode = true;
        double w = Math.Max(50, _scroll.ActualWidth - 44), h = Math.Max(50, _scroll.ActualHeight - 44);
        ApplyZoom(Math.Min(w / _pixelW, h / _pixelH));
    }

    internal void SetZoomAround(double zoom, Point? anchorInSurface)
    {
        _fitMode = false;
        zoom = Math.Clamp(zoom, 0.1, 10);
        Point? before = anchorInSurface is { } a ? _surface.TranslatePoint(a, _scroll) : null;
        ApplyZoom(zoom);
        if (anchorInSurface is { } point && before is { } was)
        {
            _scroll.UpdateLayout();
            var after = _surface.TranslatePoint(point, _scroll);
            _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset + after.X - was.X);
            _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + after.Y - was.Y);
        }
    }

    private void ApplyZoom(double zoom)
    {
        _zoom = Math.Max(0.05, zoom);
        _surface.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        UpdateZoomText();
        DrawRegions(); // lines and handles keep their size on screen
        _rerender.Stop();
        _rerender.Start();
    }

    private void UpdateZoomText() => _zoomText.Text = Math.Round(_zoom / Math.Max(0.01, ActualSizeZoom()) * 100) + "%";

    private void Scroll_Wheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        SetZoomAround(_zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), e.GetPosition(_surface));
        e.Handled = true;
    }

    private void Scroll_PanStart(object sender, MouseEventArgs e)
    {
        _drag = Drag.Pan;
        _panStart = e.GetPosition(_scroll);
        _panX = _scroll.HorizontalOffset;
        _panY = _scroll.VerticalOffset;
        _scroll.CaptureMouse();
        _scroll.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void Scroll_PanMove(object sender, MouseEventArgs e)
    {
        if (_drag != Drag.Pan) return;
        var now = e.GetPosition(_scroll);
        _scroll.ScrollToHorizontalOffset(_panX - (now.X - _panStart.X));
        _scroll.ScrollToVerticalOffset(_panY - (now.Y - _panStart.Y));
    }

    private void Scroll_PanEnd(object sender, MouseButtonEventArgs e)
    {
        if (_drag != Drag.Pan || e.ChangedButton == MouseButton.Left) return;
        _drag = Drag.None;
        _scroll.ReleaseMouseCapture();
        _scroll.Cursor = null;
    }

    private void Step(int delta)
    {
        if (_current == null) return;
        int index = Math.Clamp(_rows.IndexOf(_current) + delta, 0, _rows.Count - 1);
        _pageList.SelectedItems.Clear();
        _pageList.SelectedItem = _rows[index];
        _pageList.ScrollIntoView(_rows[index]);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        switch (e.Key)
        {
            case Key.D1: _numberRadio.IsChecked = true; break;
            case Key.D2: _titleRadio.IsChecked = true; break;
            case Key.D3: _scaleRadio.IsChecked = true; break;
            case Key.PageUp: Step(-1); break;
            case Key.PageDown: Step(1); break;
            case Key.D0 when (Keyboard.Modifiers & ModifierKeys.Control) != 0: FitToWindow(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ── Areas: draw, move, resize ───────────────────────────────────

    private static Color ColorOf(Field field) => field switch { Field.Number => Colors.Crimson, Field.Title => Colors.RoyalBlue, _ => Colors.SeaGreen };
    private Field SelectedField => _titleRadio.IsChecked == true ? Field.Title : _scaleRadio.IsChecked == true ? Field.Scale : Field.Number;
    private static TitleBlockRegion? RegionOf(TitleBlockLayout layout, Field f) => f switch { Field.Number => layout.Number, Field.Title => layout.Title, _ => layout.Scale };

    private void SetRegion(Field field, TitleBlockRegion? region, bool save = true)
    {
        if (_current == null) return;
        var layout = CurrentLayout;
        switch (field)
        {
            case Field.Number: layout.Number = region; break;
            case Field.Title: layout.Title = region; break;
            default: layout.Scale = region; break;
        }
        if (save) TitleBlockStore.Set(_current.SizeKey, new TitleBlockLayout { Number = layout.Number, Title = layout.Title, Scale = layout.Scale });
        DrawRegions();
        if (save) _pageInfo.Text = $"paper {_current.SizeKey}" + (layout.HasAny ? "  ·  areas set" : "  ·  draw the areas");
        UpdateButtons();
    }

    private void DrawRegions()
    {
        _overlay.Children.Clear();
        if (_current == null) return;
        var layout = CurrentLayout;
        double stroke = 2.2 / _zoom, handle = 9 / _zoom;
        foreach (var field in new[] { Field.Number, Field.Title, Field.Scale })
        {
            if (RegionOf(layout, field) is not { IsEmpty: false } r) continue;
            var color = ColorOf(field);
            double x = r.Left * _pixelW, y = r.Top * _pixelH, w = (r.Right - r.Left) * _pixelW, h = (r.Bottom - r.Top) * _pixelH;
            var shape = new Rectangle { Width = w, Height = h, Stroke = new SolidColorBrush(color), StrokeThickness = stroke, Fill = new SolidColorBrush(Color.FromArgb(34, color.R, color.G, color.B)), IsHitTestVisible = false };
            Canvas.SetLeft(shape, x); Canvas.SetTop(shape, y);
            _overlay.Children.Add(shape);
            // what was read there, just under the area
            string text = TextOfField(_current, field);
            if (text.Length > 0)
            {
                bool ocr = FromOcr(_current, field);
                var tag = new Border
                {
                    Background = new SolidColorBrush(ocr ? Color.FromRgb(0xFF, 0xE8, 0xB8) : Color.FromRgb(0xE7, 0xF5, 0xEC)), BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(stroke * 0.6),
                    Padding = new Thickness(5 / _zoom, 1 / _zoom, 5 / _zoom, 1 / _zoom), IsHitTestVisible = false,
                    Child = new TextBlock { Text = text, FontSize = 13 / _zoom, Foreground = Brushes.Black, MaxWidth = Math.Max(w, 260 / _zoom), TextTrimming = TextTrimming.CharacterEllipsis }
                };
                Canvas.SetLeft(tag, x); Canvas.SetTop(tag, y + h + 2 / _zoom);
                _overlay.Children.Add(tag);
            }
            if (field == SelectedField)
                foreach (var (_, hx, hy) in HandlePoints(x, y, w, h))
                {
                    var square = new Rectangle { Width = handle, Height = handle, Fill = Brushes.White, Stroke = new SolidColorBrush(color), StrokeThickness = stroke * 0.8, IsHitTestVisible = false };
                    Canvas.SetLeft(square, hx - handle / 2); Canvas.SetTop(square, hy - handle / 2);
                    _overlay.Children.Add(square);
                }
        }
    }

    private static string TextOfField(SheetReadRow row, Field f) => f switch { Field.Number => row.Number, Field.Title => row.Title, _ => row.Scale };
    private static bool FromOcr(SheetReadRow row, Field f) => f switch { Field.Number => row.NumberFromOcr, Field.Title => row.TitleFromOcr, _ => row.ScaleFromOcr };

    private static IEnumerable<(string Name, double X, double Y)> HandlePoints(double x, double y, double w, double h)
    {
        yield return ("nw", x, y); yield return ("n", x + w / 2, y); yield return ("ne", x + w, y);
        yield return ("e", x + w, y + h / 2); yield return ("se", x + w, y + h); yield return ("s", x + w / 2, y + h);
        yield return ("sw", x, y + h); yield return ("w", x, y + h / 2);
    }

    private (Field Field, string Handle)? HitArea(Point p)
    {
        if (_current == null) return null;
        var layout = CurrentLayout;
        double tolerance = 8 / _zoom;
        // handles of the selected area first, then the insides (the last drawn on top)
        if (RegionOf(layout, SelectedField) is { IsEmpty: false } selected)
            foreach (var (name, hx, hy) in HandlePoints(selected.Left * _pixelW, selected.Top * _pixelH, (selected.Right - selected.Left) * _pixelW, (selected.Bottom - selected.Top) * _pixelH))
                if (Math.Abs(p.X - hx) <= tolerance && Math.Abs(p.Y - hy) <= tolerance) return (SelectedField, name);
        foreach (var field in new[] { Field.Scale, Field.Title, Field.Number })
            if (RegionOf(layout, field) is { IsEmpty: false } r && p.X >= r.Left * _pixelW && p.X <= r.Right * _pixelW && p.Y >= r.Top * _pixelH && p.Y <= r.Bottom * _pixelH) return (field, "");
        return null;
    }

    private static Cursor CursorForHandle(string name) => name switch
    {
        "nw" or "se" => Cursors.SizeNWSE, "ne" or "sw" => Cursors.SizeNESW, "n" or "s" => Cursors.SizeNS, "e" or "w" => Cursors.SizeWE, _ => Cursors.SizeAll
    };

    internal void OverlayDown(Point p)
    {
        if (_current == null) return;
        _dragStart = p;
        if (HitArea(p) is { } hit)
        {
            // pick that area's field (so its handles show), then move or resize it
            _dragField = hit.Field;
            if (SelectedField != hit.Field) (hit.Field == Field.Number ? _numberRadio : hit.Field == Field.Title ? _titleRadio : _scaleRadio).IsChecked = true;
            _dragOrigin = RegionOf(CurrentLayout, hit.Field);
            _resizeHandle = hit.Handle;
            _drag = hit.Handle.Length == 0 ? Drag.Move : Drag.Resize;
        }
        else
        {
            _dragField = SelectedField;
            _drag = Drag.Draw;
            _dragShape = new Rectangle { Stroke = new SolidColorBrush(ColorOf(_dragField)), StrokeThickness = 2.2 / _zoom, StrokeDashArray = new DoubleCollection { 4, 2 }, IsHitTestVisible = false };
            Canvas.SetLeft(_dragShape, p.X); Canvas.SetTop(_dragShape, p.Y);
            _overlay.Children.Add(_dragShape);
        }
    }

    internal void OverlayMove(Point p)
    {
        switch (_drag)
        {
            case Drag.Draw when _dragShape != null:
                Canvas.SetLeft(_dragShape, Math.Min(_dragStart.X, p.X));
                Canvas.SetTop(_dragShape, Math.Min(_dragStart.Y, p.Y));
                _dragShape.Width = Math.Abs(p.X - _dragStart.X);
                _dragShape.Height = Math.Abs(p.Y - _dragStart.Y);
                break;
            case Drag.Move when _dragOrigin != null:
            {
                var o = _dragOrigin;
                double dx = Math.Clamp((p.X - _dragStart.X) / _pixelW, -o.Left, 1 - o.Right), dy = Math.Clamp((p.Y - _dragStart.Y) / _pixelH, -o.Top, 1 - o.Bottom);
                SetRegion(_dragField, new TitleBlockRegion(o.Left + dx, o.Top + dy, o.Right + dx, o.Bottom + dy), save: false);
                break;
            }
            case Drag.Resize when _dragOrigin != null:
            {
                double u = Math.Clamp(p.X / _pixelW, 0, 1), v = Math.Clamp(p.Y / _pixelH, 0, 1);
                var o = _dragOrigin;
                double left = o.Left, top = o.Top, right = o.Right, bottom = o.Bottom;
                if (_resizeHandle.Contains('w')) left = u;
                if (_resizeHandle.Contains('e')) right = u;
                if (_resizeHandle.Contains('n')) top = v;
                if (_resizeHandle.Contains('s')) bottom = v;
                SetRegion(_dragField, new TitleBlockRegion(Math.Min(left, right), Math.Min(top, bottom), Math.Max(left, right), Math.Max(top, bottom)), save: false);
                break;
            }
            case Drag.None:
                _overlay.Cursor = HitArea(p) is { } over ? CursorForHandle(over.Handle) : Cursors.Cross;
                break;
        }
    }

    internal void OverlayUp(Point end)
    {
        if (_drag is not (Drag.Draw or Drag.Move or Drag.Resize)) return;
        var mode = _drag;
        _drag = Drag.None;
        if (mode == Drag.Draw)
        {
            if (_dragShape != null) _overlay.Children.Remove(_dragShape);
            _dragShape = null;
            double x0 = Math.Clamp(Math.Min(_dragStart.X, end.X) / _pixelW, 0, 1), x1 = Math.Clamp(Math.Max(_dragStart.X, end.X) / _pixelW, 0, 1);
            double y0 = Math.Clamp(Math.Min(_dragStart.Y, end.Y) / _pixelH, 0, 1), y1 = Math.Clamp(Math.Max(_dragStart.Y, end.Y) / _pixelH, 0, 1);
            var region = new TitleBlockRegion(x0, y0, x1, y1);
            if (region.IsEmpty) { DrawRegions(); _dragOrigin = null; return; }
            SetRegion(_dragField, region);
        }
        else if (RegionOf(CurrentLayout, _dragField) is { IsEmpty: false } placed) SetRegion(_dragField, placed); // keep the new place
        _dragOrigin = null;
        _status.Text = "Areas changed: press Read this page / Read all pages to read them again.";
    }

    private void Overlay_LeftDown(object sender, MouseButtonEventArgs e) { OverlayDown(e.GetPosition(_overlay)); if (_drag != Drag.None) _overlay.CaptureMouse(); e.Handled = true; }
    private void Overlay_Move(object sender, MouseEventArgs e) => OverlayMove(e.GetPosition(_overlay));
    private void Overlay_LeftUp(object sender, MouseButtonEventArgs e) { _overlay.ReleaseMouseCapture(); OverlayUp(e.GetPosition(_overlay)); }

    internal void SetAreaForTest(int field, TitleBlockRegion? region) => SetRegion(field == 0 ? Field.Number : field == 1 ? Field.Title : Field.Scale, region);
    internal void SetTableView(bool table) { (table ? _tableView : _pageView).IsChecked = true; ShowView(); }
    internal void ApplyForTest() => Apply();
    /// <summary>The areas of the current page's paper size (tests read them).</summary>
    internal TitleBlockLayout CurrentAreas => CurrentLayout;
    internal void SelectField(int index) => (index == 0 ? _numberRadio : index == 1 ? _titleRadio : _scaleRadio).IsChecked = true;
    internal Task ShowPageForTestAsync(int page) { var row = _rows.First(r => r.Page == page); _pageList.SelectedItems.Clear(); _pageList.SelectedItem = row; return ShowPageAsync(row); }

    // ── Fields of the page ──────────────────────────────────────────

    private void BindFields()
    {
        _binding = true;
        try
        {
            _numberBox.Text = _current?.Number ?? "";
            _titleBox.Text = _current?.Title ?? "";
            _scaleBox.Text = _current?.Scale ?? "";
        }
        finally { _binding = false; }
        RefreshFieldLook();
    }

    private void RefreshFieldLook()
    {
        if (_current == null) return;
        var amber = new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xCC));
        _numberBox.Background = _current.NumberFromOcr ? amber : null;
        _titleBox.Background = _current.TitleFromOcr ? amber : null;
        _scaleBox.Background = _current.ScaleFromOcr ? amber : null;
        var notes = new List<string>();
        if (_current.NumberFromOcr || _current.TitleFromOcr || _current.ScaleFromOcr) notes.Add("Amber: read by OCR, worth a look.");
        if (_current.NumberEdited || _current.TitleEdited || _current.ScaleEdited) notes.Add("Blue dot in the list: you typed here; reading again keeps it while \"Keep what I typed\" is on.");
        _sourceNote.Text = string.Join(" ", notes);
    }

    private void FieldBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_binding || _current == null || sender is not TextBox { Tag: Field field } box) return;
        switch (field)
        {
            case Field.Number: _current.Number = box.Text; break;
            case Field.Title: _current.Title = box.Text; break;
            default: _current.Scale = box.Text; break;
        }
        RefreshFieldLook();
        DrawRegions();
        UpdateButtons();
    }

    private void ShowView()
    {
        if (_pagePanel == null || _table == null) return;
        bool table = _tableView.IsChecked == true;
        _table.Visibility = table ? Visibility.Visible : Visibility.Collapsed;
        _pagePanel.Visibility = table ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── Reading ─────────────────────────────────────────────────────

    private IReadOnlyList<int> SelectedPages()
    {
        var selected = _pageList.SelectedItems.OfType<SheetReadRow>().Select(r => r.Page).OrderBy(n => n).ToList();
        return selected.Count > 0 ? selected : _current != null ? new[] { _current.Page } : Array.Empty<int>();
    }

    private void UpdateButtons()
    {
        int selected = _pageList.SelectedItems.Count;
        _readPage.Text = selected > 1 ? $"Read {selected} selected pages" : "Read this page";
        _readPage.IsEnabled = !_busy && _current != null;
        _readAll.IsEnabled = !_busy && _rows.Count > 0;
        _applyButton.IsEnabled = !_busy && _rows.Any(r => r.HasData);
    }

    internal Task ReadAllForTestAsync() => ReadAsync(_pages);
    internal Task ReadPagesForTestAsync(IReadOnlyList<int> pages) => ReadAsync(pages);

    private SheetReadSource Source => _sourceBox.SelectedIndex switch { 1 => SheetReadSource.TextLayer, 2 => SheetReadSource.Ocr, _ => SheetReadSource.Auto };

    private async Task ReadAsync(IReadOnlyList<int> pages)
    {
        if (_busy || pages.Count == 0 || _rows.Count == 0) return;
        // the areas of every paper size that is going to be read are remembered for next time
        foreach (var key in pages.Select(p => _rows.FirstOrDefault(r => r.Page == p)?.SizeKey).Where(k => !string.IsNullOrEmpty(k)).Distinct())
            if (LayoutFor(key!) is { HasAny: true } layout) TitleBlockStore.Set(key!, new TitleBlockLayout { Number = layout.Number, Title = layout.Title, Scale = layout.Scale });
        _busy = true;
        _cancel = new CancellationTokenSource();
        _progress.Visibility = Visibility.Visible;
        _progress.Value = 0;
        _status.Text = "Reading…";
        UpdateButtons();
        var missing = new HashSet<string>();
        try
        {
            var progress = new Progress<(int Done, int Total)>(p => { _progress.Maximum = Math.Max(1, p.Total); _progress.Value = p.Done; _status.Text = $"Reading by OCR… {p.Done} of {p.Total} pages"; });
            var result = await SheetInfoReader.ReadAsync(_path, pages, key => { var l = LayoutFor(key); if (!l.HasAny) lock (missing) missing.Add(key); return l; }, Source, progress, _cancel.Token);
            foreach (var reading in result.Pages)
                _rows.FirstOrDefault(r => r.Page == reading.Page)?.SetRead(reading.Number, reading.Title, reading.Scale, _keepEdits.IsChecked == true);
            BindFields();
            DrawRegions();
            int filled = result.Pages.Count(r => r.Number.Text.Length > 0 || r.Title.Text.Length > 0 || r.Scale.Text.Length > 0);
            _status.Text = $"{filled} of {result.Pages.Count} pages have text in the areas" +
                (result.OcrFields > 0 ? $" · {result.OcrFields} fields read by OCR (amber: worth a look)" : "") +
                (result.OcrUnavailable ? " · OCR is not installed with this copy of the program, only the PDF text was used" : "") +
                (filled == 0 && Source == SheetReadSource.TextLayer ? " · this PDF probably has no text layer: choose Auto or OCR" : "") +
                (missing.Count > 0 ? $" · no area drawn for {string.Join(", ", missing)}: open a page of that size and draw them" : "");
        }
        catch (OperationCanceledException) { _status.Text = "Cancelled."; }
        catch (Exception ex) { _status.Text = "Could not read the file: " + ex.Message; }
        finally
        {
            _busy = false;
            _progress.Visibility = Visibility.Collapsed;
            UpdateButtons();
        }
    }

    private void Apply()
    {
        Result = _rows.Where(r => r.HasData).Select(r => (r.Page, new XTSheetPageInfo { No = r.Number.Trim(), Title = r.Title.Trim(), Scale = r.Scale.Trim() })).ToList();
        try { DialogResult = true; } catch (InvalidOperationException) { /* shown without ShowDialog (tests) */ }
    }
}
