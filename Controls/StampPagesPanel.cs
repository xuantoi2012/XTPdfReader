using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// "Stamp pages" as a panel beside the page view: the owner's set of signatures and stamps (<see cref="StampLibrary"/>, each with a name: whose signature it is) shown as thumbnails with the name below, like the pages.
/// Pick one, choose where it goes on the page you are reading (click, drag, resize the corners; the sample page can be any page of that paper size), tick the sizes and pages, and the same stamp is put at that place on every one of them
/// as one undoable change. The place is remembered per stamp and paper size, so an A1 sheet and an A3 sheet each keep their own.
/// </summary>
internal sealed class StampPagesPanel : UserControl
{
    private readonly string _path;
    private readonly int _pageCount;
    private readonly int _startPage;
    private readonly IAreaSurface _picker;
    private readonly PaperGroupList _sizes = new();
    private readonly ListBox _stamps = new() { SelectionMode = SelectionMode.Single, BorderThickness = new Thickness(1), Height = 232 };
    private readonly TextBox _nameBox = AreaKit.Box();
    private readonly Slider _opacity = new() { Minimum = 20, Maximum = 100, Value = 100, VerticalAlignment = VerticalAlignment.Center, Width = 170 };
    private readonly CheckBox _nameDate = new() { Content = "Add my name and the date under it", Margin = new Thickness(0, 8, 0, 0) };
    private readonly RadioButton _allPages = new() { GroupName = "StampPages", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _range = new() { Content = "Pages", GroupName = "StampPages", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _rangeBox = new() { Width = 130, Height = 28, Margin = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "For example 1-3, 7" };
    private readonly TextBlock _status = new();
    private readonly XTButton _apply = AreaKit.Primary("Stamp the pages");
    private readonly Dictionary<string, AreaPreset> _places = new();
    private bool _loaded, _filling;

    public StampDefinition? Definition { get; private set; }
    public int StampOpacity { get; private set; } = 100;
    public bool AddNameAndDate { get; private set; }
    /// <summary>Every page to stamp and where the stamp goes on it (fractions of the page), one entry per page.</summary>
    public IReadOnlyList<(int Page, double U1, double V1, double U2, double V2)> Placements { get; private set; } = Array.Empty<(int, double, double, double, double)>();

    internal IAreaSurface Picker => _picker;
    internal PaperGroupList Sizes => _sizes;
    internal ListBox StampList => _stamps;
    internal TextBox NameBox => _nameBox;
    internal RadioButton AllPagesButton => _allPages;
    internal RadioButton RangeButton => _range;
    internal TextBox RangeBox => _rangeBox;
    internal XTButton ApplyButton => _apply;
    internal string StatusText => _status.Text;

    /// <summary>The stamp, its look and the pages are chosen and the user pressed the button: the host puts the stamps into the file.</summary>
    public event Action<StampPagesPanel>? Applied;

    public StampPagesPanel(IAreaSurface surface, string path, int currentPage, int pageCount)
    {
        _picker = surface;
        _picker.PlaceMode = true;
        _path = path;
        _pageCount = Math.Max(1, pageCount);
        _startPage = Math.Clamp(currentPage, 1, _pageCount);
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        var root = new DockPanel();

        _apply.Margin = new Thickness(0);
        var footer = AreaKit.PanelFooter(_status, _apply);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var side = new StackPanel { Margin = new Thickness(0, 0, 2, 0) };
        side.Children.Add(new TextBlock { Text = "Signatures and stamps", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        _stamps.Background = Brushes.Transparent;
        _stamps.BorderBrush = R("Ui.Border");
        ScrollViewer.SetHorizontalScrollBarVisibility(_stamps, ScrollBarVisibility.Disabled);
        var wrap = new FrameworkElementFactory(typeof(WrapPanel));
        _stamps.ItemsPanel = new ItemsPanelTemplate(wrap);
        _stamps.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "UiRowItem");
        side.Children.Add(_stamps);
        var tools = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var import = AreaKit.Ghost("Add image…", 28);
        import.ToolTip = Loc.T("Add a scanned signature or seal (PNG, JPG) to your set");
        var create = AreaKit.Ghost("New text stamp…", 28);
        var remove = AreaKit.Ghost("Remove", 28);
        remove.ToolTip = Loc.T("Take the chosen one out of your set");
        foreach (var b in new[] { import, create, remove }) { b.Margin = new Thickness(0, 0, 6, 4); tools.Children.Add(b); }
        side.Children.Add(tools);
        side.Children.Add(AreaKit.Heading(this, "Name (whose signature)"));
        side.Children.Add(_nameBox);
        var opacityRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        opacityRow.Children.Add(new TextBlock { Text = "Opacity", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        opacityRow.Children.Add(_opacity);
        side.Children.Add(opacityRow);
        side.Children.Add(_nameDate);
        side.Children.Add(AreaKit.Heading(this, "Paper sizes in this file"));
        side.Children.Add(_sizes);
        var copyPlace = AreaKit.Ghost("Use this place on all sizes", 28);
        copyPlace.Margin = new Thickness(0, 6, 0, 0);
        copyPlace.HorizontalAlignment = HorizontalAlignment.Left;
        copyPlace.ToolTip = Loc.T("The same share of the page, on every size");
        side.Children.Add(copyPlace);
        side.Children.Add(AreaKit.Heading(this, "Pages"));
        _allPages.Content = Loc.T("All pages of the ticked sizes");
        _rangeBox.GotFocus += (_, _) => _range.IsChecked = true;
        side.Children.Add(_allPages);
        side.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0), Children = { _range, _rangeBox } });
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14, 12, 14, 12), Content = side });
        Content = root;

        _stamps.SelectionChanged += async (_, _) => await OnStampChangedAsync();
        _sizes.GroupSelected += async row => await ShowGroupAsync(row);
        _picker.AreaChanged += OnPlaceChanged;
        _picker.PageChanged += OnSurfacePageChanged;
        _nameDate.Click += async (_, _) => await OnStampChangedAsync();
        _nameBox.LostFocus += (_, _) => CommitName();
        _nameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitName(); e.Handled = true; } };
        import.Click += (_, _) => ImportImages();
        create.Click += (_, _) =>
        {
            var definition = NewStampWindow.Ask(Window.GetWindow(this)!);
            if (definition == null) return;
            StampLibrary.Add(definition);
            FillStamps(StampLibrary.Mine.Last());
        };
        remove.Click += (_, _) =>
        {
            if (Chosen is { } d && StampLibrary.Mine.Any(m => m.Id == d.Id)) { StampLibrary.Remove(StampLibrary.Mine.First(m => m.Id == d.Id)); FillStamps(); }
            else _status.Text = "Only your own stamps can be removed.";
        };
        copyPlace.Click += (_, _) => CopyPlaceToAll();
        _apply.Click += (_, _) => Apply();
        _status.Text = Loc.T("Choose a signature or stamp, then click the page to place it.");
    }

    // ── the stamps ───────────────────────────────────────────────────

    private StampDefinition? Chosen => (_stamps.SelectedItem as ListBoxItem)?.Tag as StampDefinition;

    private string SubText() => _nameDate.IsChecked == true ? $"{Environment.UserName} · {DateTime.Now:dd/MM/yyyy}" : Chosen?.Sub ?? "";

    private void FillStamps(StampDefinition? select = null)
    {
        _filling = true;
        string? keep = (select ?? Chosen)?.Id;
        _stamps.Items.Clear();
        foreach (var d in StampLibrary.Mine) _stamps.Items.Add(Tile(d));
        foreach (var d in StampLibrary.Standard) _stamps.Items.Add(Tile(d));
        var target = _stamps.Items.OfType<ListBoxItem>().FirstOrDefault(i => keep != null && ((StampDefinition)i.Tag).Id == keep)
                     ?? _stamps.Items.OfType<ListBoxItem>().FirstOrDefault(i => ((StampDefinition)i.Tag).IsImage)
                     ?? _stamps.Items.OfType<ListBoxItem>().FirstOrDefault();
        _filling = false;
        _stamps.SelectedItem = target;
        if (target != null && ReferenceEquals(target, _stamps.SelectedItem)) _ = OnStampChangedAsync();
    }

    /// <summary>A stamp as a thumbnail with its name under it, like a page.</summary>
    private static ListBoxItem Tile(StampDefinition d)
    {
        FrameworkElement picture;
        if (d.IsImage && LoadPicture(d.ImagePath, 120) is { } bitmap) picture = new Image { Source = bitmap, Stretch = Stretch.Uniform, Margin = new Thickness(4) };
        else
        {
            Color color;
            try { color = (Color)ColorConverter.ConvertFromString(d.Color.Length > 0 ? d.Color : "#C0392B"); } catch { color = Colors.IndianRed; }
            picture = new TextBlock { Text = d.IsImage ? "?" : d.Text, FontWeight = FontWeights.Bold, FontSize = 12, Foreground = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4) };
        }
        var frame = new Border { Width = 92, Height = 58, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Background = Brushes.White, Child = picture };
        frame.SetResourceReference(Border.BorderBrushProperty, "Ui.Border");
        var name = new TextBlock { Text = d.Text, FontSize = 11.5, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Width = 92, Margin = new Thickness(0, 3, 0, 0), ToolTip = d.Text };
        var tile = new StackPanel { Width = 96, Margin = new Thickness(2), Children = { frame, name } };
        return new ListBoxItem { Content = tile, Tag = d, ToolTip = d.IsImage ? "Your signature / seal: " + d.Text : d.Text };
    }

    private static BitmapImage? LoadPicture(string path, int decodeWidth = 0)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    private void ImportImages()
    {
        using var dlg = new System.Windows.Forms.OpenFileDialog { Title = "Add a signature or seal image", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp", Multiselect = true };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        StampDefinition? last = null;
        foreach (string file in dlg.FileNames)
        {
            try
            {
                StampLibrary.Add(new StampDefinition(StampDefinition.ImageKind, System.IO.Path.GetFileNameWithoutExtension(file), "", "", StampLibrary.ImportImage(file)));
                last = StampLibrary.Mine.Last();
            }
            catch (Exception ex) { AppDialog.Show(Window.GetWindow(this), "Could not add the image:\n" + ex.Message, "Add image", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        FillStamps(last);
        _status.Text = Loc.T("Type whose signature it is in the Name box.");
        _nameBox.Focus();
        _nameBox.SelectAll();
    }

    private void CommitName()
    {
        if (_filling || Chosen is not { } d || !StampLibrary.Mine.Any(m => m.Id == d.Id)) return;
        string name = _nameBox.Text.Trim();
        if (name.Length == 0 || name == d.Text) return;
        if (StampLibrary.Rename(d, name) is { } renamed) FillStamps(renamed);
    }

    private async Task OnStampChangedAsync()
    {
        if (_filling) return;
        var d = Chosen;
        _nameBox.IsEnabled = d != null && StampLibrary.Mine.Any(m => m.Id == d.Id);
        _nameBox.Text = d?.Text ?? "";
        _places.Clear();
        if (d != null)
            foreach (var row in _sizes.Rows)
                if (AreaPresetStore.Get("stamp:" + d.Id, row.Key) is { } preset) _places[row.Key] = preset;
        foreach (var row in _sizes.Rows) row.HasArea = _places.ContainsKey(row.Key);
        if (_sizes.Selected is { } selected) await ShowGroupAsync(selected);
    }

    // ── sizes and places ─────────────────────────────────────────────

    /// <summary>Reads the paper sizes of the file and fills the lists; the host calls it once the panel is shown.</summary>
    public async Task StartAsync()
    {
        IReadOnlyList<PaperGroup> groups;
        try { groups = PaperSizeIndex.Group(await PaperSizeIndex.ReadAsync(_path)); }
        catch (Exception ex) { _status.Text = "The pages could not be read: " + ex.Message; return; }
        _sizes.Load(groups, _ => false, _startPage);
        _loaded = true;
        FillStamps();
    }

    /// <summary>The reader moved to a page of another paper size: that size becomes the chosen one (with its own place).</summary>
    private void OnSurfacePageChanged()
    {
        if (_loaded && _picker.CurrentPage > 0) _sizes.SelectPage(_picker.CurrentPage);
    }

    /// <summary>The panel is closed: the surface stops listening.</summary>
    public void Detach()
    {
        _picker.AreaChanged -= OnPlaceChanged;
        _picker.PageChanged -= OnSurfacePageChanged;
    }

    /// <summary>Where the stamp goes on a page of this size when the user has not placed it: the bottom right corner, at its natural size.</summary>
    private AreaPreset DefaultPlace(PaperGroup group)
    {
        var page = group.Pages[0];
        var (w, h) = PdfQuickAnnotationService.StampSize(Chosen!, SubText(), page.WidthPt);
        double fw = Math.Min(0.9, w / page.WidthPt), fh = Math.Min(0.9, h / page.HeightPt);
        return new AreaPreset(0.97 - fw, 0.96 - fh, 0.97, 0.96);
    }

    private AreaPreset PlaceFor(PaperGroup group) => _places.TryGetValue(group.Key, out var p) ? p : DefaultPlace(group);

    private async Task ShowGroupAsync(PaperGroupRow row)
    {
        if (Chosen is not { } definition) return;
        var page = row.Group.Pages[0];
        var (w, h) = PdfQuickAnnotationService.StampSize(definition, SubText(), page.WidthPt);
        _picker.PlaceAspect = w / Math.Max(1, h); // the stamp's own width / height
        _picker.PlaceImage = definition.IsImage ? LoadPicture(definition.ImagePath) : null;
        _picker.SetPageSize(page.WidthPt);
        var place = PlaceFor(row.Group);
        _picker.SetArea((place.U1, place.V1, place.U2, place.V2));
        var pages = row.Group.Pages.Select(p => p.Page).ToList();
        if (_picker.CurrentPath != _path || !pages.Contains(_picker.CurrentPage) || !_picker.SamePages(pages))
            await _picker.SetPagesAsync(_path, pages, pages.Contains(_startPage) ? _startPage : pages[0]);
    }

    private void OnPlaceChanged()
    {
        if (Chosen is not { } d || _sizes.Selected is not { } row || _picker.Area is not { } a) return;
        var preset = new AreaPreset(a.U1, a.V1, a.U2, a.V2);
        _places[row.Key] = preset;
        AreaPresetStore.Set("stamp:" + d.Id, row.Key, preset);
        row.HasArea = true;
    }

    private void CopyPlaceToAll()
    {
        if (Chosen is not { } d || _picker.Area is not { } a) { _status.Text = "Place the stamp on a page first."; return; }
        var preset = new AreaPreset(a.U1, a.V1, a.U2, a.V2);
        foreach (var row in _sizes.Rows)
        {
            _places[row.Key] = preset;
            AreaPresetStore.Set("stamp:" + d.Id, row.Key, preset);
            row.HasArea = true;
        }
        _status.Text = Loc.T("The same place is now set for every paper size.");
    }

    // ── act ──────────────────────────────────────────────────────────

    private void Apply()
    {
        if (Chosen is not { } definition) { _status.Text = "Choose a signature or stamp first."; return; }
        HashSet<int>? only = null;
        if (_range.IsChecked == true)
        {
            var parsed = OcrWindow.ParsePages(_rangeBox.Text, _pageCount);
            if (parsed == null) { _status.Text = $"The pages must be like 1-3, 7 (this file has {_pageCount})."; return; }
            only = parsed.Select(p => p + 1).ToHashSet();
        }
        var list = new List<(int, double, double, double, double)>();
        foreach (var row in _sizes.Checked)
        {
            var place = PlaceFor(row.Group);
            foreach (var page in row.Group.Pages.Where(p => only == null || only.Contains(p.Page)))
                list.Add((page.Page, place.U1, place.V1, place.U2, place.V2));
        }
        if (list.Count == 0) { _status.Text = "No page to stamp: tick a paper size (and check the page list)."; return; }
        Definition = definition;
        StampOpacity = (int)_opacity.Value;
        AddNameAndDate = _nameDate.IsChecked == true;
        Placements = list.OrderBy(p => p.Item1).ToList();
        Applied?.Invoke(this);
    }

    internal void ApplyForTest() => Apply();
}
