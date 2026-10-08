using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Services.TextEdit;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

internal sealed class BatchHit : INotifyPropertyChanged
{
    private bool _checked = true;
    private string _after = "";
    public int Page { get; init; }
    public PageTextRuns Source { get; init; } = null!;
    public TextRun Run { get; init; } = null!;
    /// <summary>The drawn objects of this page (objects mode).</summary>
    public IReadOnlyList<PdfObjectRef> Objects { get; init; } = Array.Empty<PdfObjectRef>();
    /// <summary>The text as the user sees it now (a pending edit counts), or what the objects are.</summary>
    public string Text { get; init; } = "";
    public bool IsChecked { get => _checked; set { _checked = value; Raise(nameof(IsChecked)); } }
    public string After { get => _after; set { _after = value; Raise(nameof(After)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// "Find in area": the user draws an area on a sample page (like the areas of Read sheet info), once for each paper size of the file, and the tool looks there on every chosen page.
/// It finds either TEXT (listed, then replaced or deleted as unsaved text edits) or DRAWN OBJECTS - a signature, a stamp, a line, an image - whatever they are made of (deleted from the file in one go).
/// The area of each paper size is remembered (<see cref="AreaPresetStore"/>), so the next file of the same sizes needs no drawing.
/// </summary>
internal sealed class BatchFindWindow : XTWindow
{
    private const string TextPurpose = "find-text", ObjectPurpose = "find-object";

    private readonly string _path;
    private readonly int _pageCount;
    private readonly int _startPage;
    private readonly PageAreaPicker _picker = new() { MinHeight = 280 };
    private readonly PaperGroupList _sizes = new();
    private readonly RadioButton _textMode = new() { Content = "Text", GroupName = "BatchWhat", IsChecked = true, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton _objectsMode = new() { Content = "Drawn object (signature, stamp, line, image)", GroupName = "BatchWhat", VerticalAlignment = VerticalAlignment.Center, ToolTip = "Finds pictures and strokes that lie in the area, whatever they are" };
    private readonly TextBox _find = AreaKit.Box();
    private readonly TextBox _replace = AreaKit.Box();
    private readonly CheckBox _matchCase = new() { Content = "Match case", Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox _touch = new() { Content = "Also everything that touches the area (rules, frames)", Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly RadioButton _allPages = new() { GroupName = "BatchPages", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _range = new() { Content = "Pages", GroupName = "BatchPages", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _rangeBox = new() { Width = 130, Height = 28, Margin = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "For example 1-3, 7" };
    private readonly TextBlock _status = new();
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Extended, BorderThickness = new Thickness(1) };
    private readonly XTButton _findButton = AreaKit.Primary("Find", 90);
    private readonly XTButton _replaceButton = AreaKit.Primary("Replace checked");
    private readonly XTButton _deleteButton = AreaKit.Ghost("Delete checked text");
    private readonly ObservableCollection<BatchHit> _hits = new();
    private readonly Dictionary<string, AreaPreset> _areas = new();
    private TextBlock _findLabel = null!, _replaceLabel = null!, _afterHeader = null!;
    private bool _busy, _loaded;

    /// <summary>What the user chose to do (set when the window closes with OK): the edits to put into the file's pending changes.</summary>
    public IReadOnlyList<(int Page, TextRun Place, TextEdit? Edit)> Edits { get; private set; } = Array.Empty<(int, TextRun, TextEdit?)>();
    public string Description { get; private set; } = "";
    /// <summary>Set instead of <see cref="Edits"/> in the objects mode: what to delete from the file (every object of the checked pages).</summary>
    public IReadOnlyList<PdfObjectRef> ObjectsToDelete { get; private set; } = Array.Empty<PdfObjectRef>();
    internal bool ObjectsMode => _objectsMode.IsChecked == true;
    private string Purpose => ObjectsMode ? ObjectPurpose : TextPurpose;

    internal PageAreaPicker Picker => _picker;
    internal PaperGroupList Sizes => _sizes;
    internal RadioButton ObjectsModeButton => _objectsMode;
    internal CheckBox TouchBox => _touch;
    internal TextBox FindBox => _find;
    internal TextBox ReplaceBox => _replace;
    internal RadioButton AllPagesButton => _allPages;
    internal RadioButton RangeButton => _range;
    internal TextBox RangeBox => _rangeBox;
    internal ObservableCollection<BatchHit> Hits => _hits;
    internal string StatusText => _status.Text;
    internal XTButton ReplaceButton => _replaceButton;
    internal XTButton DeleteButton => _deleteButton;

    public BatchFindWindow(string path, int currentPage, int pageCount)
    {
        _path = path;
        _pageCount = Math.Max(1, pageCount);
        _startPage = Math.Clamp(currentPage, 1, _pageCount);
        Title = "Find in area";
        TitleBarMode = TitleBarMode.Dialog;
        Width = 1240;
        Height = 820;
        MinWidth = 980;
        MinHeight = 600;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        var root = new DockPanel { Background = R("Ui.Surface") };

        // footer
        var close = AreaKit.Ghost("Close", minWidth: 90);
        close.IsCancel = true;
        close.Margin = new Thickness(8, 0, 0, 0);
        _deleteButton.Margin = new Thickness(8, 0, 0, 0);
        var footer = AreaKit.Footer(this, _status, _replaceButton, _deleteButton, close);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // header: what to find
        var head = new StackPanel { Margin = new Thickness(20, 12, 20, 8) };
        head.Children.Add(AreaKit.Muted(this, "Draw an area on a sample page of each paper size (drag; drag inside it to move it, its squares to resize it; Ctrl + wheel zooms, right-drag pans). The tool looks there on every chosen page. Areas are kept per paper size."));
        head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Children = { new TextBlock { Text = "Find", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center }, _textMode, _objectsMode } });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        // body: sizes + options | page ; results below
        var body = new Grid { Margin = new Thickness(20, 0, 20, 12) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300), MinWidth = 240 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 400 });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(190) });

        var side = new StackPanel { Margin = new Thickness(0, 0, 2, 0) };
        side.Children.Add(new TextBlock { Text = "Paper sizes in this file", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        side.Children.Add(_sizes);
        side.Children.Add(AreaKit.Muted(this, "Tick the sizes to search. Click one to draw its own area."));
        _findLabel = AreaKit.Heading(this, "Text to find");
        _replaceLabel = AreaKit.Heading(this, "Replace with");
        _findLabel.Margin = _replaceLabel.Margin = new Thickness(0, 12, 0, 2);
        side.Children.Add(_findLabel);
        side.Children.Add(_find);
        side.Children.Add(_replaceLabel);
        side.Children.Add(_replace);
        side.Children.Add(_matchCase);
        side.Children.Add(_touch);
        side.Children.Add(AreaKit.Heading(this, "Pages"));
        _allPages.Content = $"All pages of the ticked sizes";
        _rangeBox.GotFocus += (_, _) => _range.IsChecked = true;
        side.Children.Add(_allPages);
        side.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0), Children = { _range, _rangeBox } });
        _findButton.Margin = new Thickness(0, 14, 0, 0);
        _findButton.HorizontalAlignment = HorizontalAlignment.Left;
        side.Children.Add(_findButton);
        var sideScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = side };
        body.Children.Add(sideScroll);
        var splitter = new GridSplitter { Width = 8, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(splitter, 1);
        body.Children.Add(splitter);
        Grid.SetColumn(_picker, 2);
        body.Children.Add(_picker);

        // results
        _list.Background = Brushes.Transparent;
        _list.BorderBrush = R("Ui.Border");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "UiRowItem");
        _list.ItemTemplate = ResultTemplate();
        _list.ItemsSource = _hits;
        _afterHeader = new TextBlock { Text = "After \"Replace checked\"" };
        var header = new Grid { Margin = new Thickness(8, 0, 8, 3) };
        foreach (var w in new[] { new GridLength(34), new GridLength(60), new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star) })
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        foreach (var (text, column) in new[] { ("", 0), ("Page", 1), ("Found in the area", 2) })
        {
            var cell = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12 };
            Grid.SetColumn(cell, column);
            header.Children.Add(cell);
        }
        _afterHeader.FontWeight = FontWeights.SemiBold;
        _afterHeader.FontSize = 12;
        Grid.SetColumn(_afterHeader, 3);
        header.Children.Add(_afterHeader);
        var results = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        results.Children.Add(header);
        results.Children.Add(_list);
        Grid.SetRow(results, 2);
        Grid.SetColumnSpan(results, 3);
        body.Children.Add(results);
        root.Children.Add(body);
        Content = root;

        _textMode.Checked += (_, _) => SwitchMode();
        _objectsMode.Checked += (_, _) => SwitchMode();
        _find.TextChanged += (_, _) => RefreshAfter();
        _replace.TextChanged += (_, _) => RefreshAfter();
        _matchCase.Click += (_, _) => RefreshAfter();
        _sizes.GroupSelected += async row => await ShowGroupAsync(row);
        _picker.AreaChanged += OnAreaChanged;
        _findButton.Click += async (_, _) => await FindAsync();
        _replaceButton.Click += (_, _) => Apply(delete: false);
        _deleteButton.Click += (_, _) => Apply(delete: true);
        Loaded += async (_, _) => await InitializeAsync();
        _replaceButton.IsEnabled = _deleteButton.IsEnabled = false;
        _status.Text = "Draw the area, then press Find.";
    }

    private static DataTemplate ResultTemplate()
    {
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 2));
        foreach (var width in new[] { new GridLength(34), new GridLength(60), new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star) })
        {
            var column = new FrameworkElementFactory(typeof(ColumnDefinition));
            column.SetValue(ColumnDefinition.WidthProperty, width);
            grid.AppendChild(column);
        }
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetBinding(CheckBox.IsCheckedProperty, new System.Windows.Data.Binding(nameof(BatchHit.IsChecked)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        grid.AppendChild(check);
        FrameworkElementFactory Cell(string property, int column, double opacity = 1)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetValue(Grid.ColumnProperty, column);
            text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            text.SetValue(UIElement.OpacityProperty, opacity);
            text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(property));
            return text;
        }
        grid.AppendChild(Cell(nameof(BatchHit.Page), 1, 0.65));
        grid.AppendChild(Cell(nameof(BatchHit.Text), 2));
        grid.AppendChild(Cell(nameof(BatchHit.After), 3));
        return new DataTemplate { VisualTree = grid };
    }

    // ── sizes and areas ──────────────────────────────────────────────

    private async Task InitializeAsync()
    {
        IReadOnlyList<PaperGroup> groups;
        try { groups = PaperSizeIndex.Group(await PaperSizeIndex.ReadAsync(_path)); }
        catch (Exception ex) { _status.Text = "The pages could not be read: " + ex.Message; return; }
        LoadAreas(groups.Select(g => g.Key));
        _sizes.Load(groups, key => _areas.ContainsKey(key), _startPage);
        _loaded = true;
    }

    private void LoadAreas(IEnumerable<string> keys)
    {
        _areas.Clear();
        foreach (string key in keys.Distinct().ToList())
            if (AreaPresetStore.Get(Purpose, key) is { } preset) _areas[key] = preset;
    }

    private async Task ShowGroupAsync(PaperGroupRow row)
    {
        var pages = row.Group.Pages.Select(p => p.Page).ToList();
        _picker.SetPageSize(row.Group.Pages[0].WidthPt);
        _picker.SetArea(_areas.TryGetValue(row.Key, out var area) ? (area.U1, area.V1, area.U2, area.V2) : null);
        int start = pages.Contains(_startPage) ? _startPage : pages[0];
        await _picker.SetPagesAsync(_path, pages, start);
    }

    private void OnAreaChanged()
    {
        if (_sizes.Selected is not { } row) return;
        if (_picker.Area is { } a)
        {
            var preset = new AreaPreset(a.U1, a.V1, a.U2, a.V2);
            _areas[row.Key] = preset;
            AreaPresetStore.Set(Purpose, row.Key, preset);
            row.HasArea = true;
        }
        else
        {
            _areas.Remove(row.Key);
            AreaPresetStore.Set(Purpose, row.Key, null);
            row.HasArea = false;
        }
    }

    private void SwitchMode()
    {
        if (_findLabel == null) return; // while the window is being built
        bool objects = ObjectsMode;
        var textOnly = objects ? Visibility.Collapsed : Visibility.Visible;
        _find.Visibility = _replace.Visibility = _findLabel.Visibility = _replaceLabel.Visibility = _matchCase.Visibility = textOnly;
        _touch.Visibility = objects ? Visibility.Visible : Visibility.Collapsed;
        _replaceButton.Visibility = textOnly;
        _deleteButton.Text = objects ? "Delete checked objects" : "Delete checked text";
        if (objects) { _deleteButton.SetResourceReference(StyleProperty, "UiPrimaryButton"); } else { _deleteButton.SetResourceReference(StyleProperty, "UiGhostButton"); }
        _afterHeader.Text = objects ? "What happens" : "After \"Replace checked\"";
        _hits.Clear();
        _picker.ShowMarks(Array.Empty<(double, double, double, double)>());
        _replaceButton.IsEnabled = _deleteButton.IsEnabled = false;
        if (_loaded)
        {
            LoadAreas(_sizes.Rows.Select(r => r.Key));
            foreach (var row in _sizes.Rows) row.HasArea = _areas.ContainsKey(row.Key);
            if (_sizes.Selected is { } selected) _picker.SetArea(_areas.TryGetValue(selected.Key, out var area) ? (area.U1, area.V1, area.U2, area.V2) : null);
        }
        _status.Text = objects ? "Draw the area around the signature or stamp (what is mostly inside counts), then press Find." : "Draw the area, then press Find.";
    }

    // ── find ─────────────────────────────────────────────────────────

    internal static string ReplacedText(string text, string find, string replace, bool matchCase)
        => find.Length == 0 ? replace : text.Replace(find, replace, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    internal static bool Matches(string text, string find, bool matchCase)
        => find.Length == 0 || text.Contains(find, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private void RefreshAfter()
    {
        if (ObjectsMode) return;
        foreach (var hit in _hits) hit.After = ReplacedText(hit.Text, _find.Text, _replace.Text, _matchCase.IsChecked == true);
    }

    /// <summary>The pages to search: those of the ticked sizes, and when "Pages" is chosen only the listed ones. null = the list is not valid.</summary>
    private List<(PaperGroupRow Row, List<int> Pages)>? ChosenPages()
    {
        HashSet<int>? only = null;
        if (_range.IsChecked == true)
        {
            var parsed = OcrWindow.ParsePages(_rangeBox.Text, _pageCount);
            if (parsed == null) return null;
            only = parsed.Select(p => p + 1).ToHashSet();
        }
        return _sizes.Checked.Select(r => (r, r.Group.Pages.Select(p => p.Page).Where(p => only == null || only.Contains(p)).ToList())).Where(x => x.Item2.Count > 0).ToList();
    }

    internal async Task FindAsync()
    {
        if (_busy) return;
        var groups = ChosenPages();
        if (groups == null) { _status.Text = $"The pages must be like 1-3, 7 (this file has {_pageCount})."; return; }
        if (groups.Count == 0) { _status.Text = "Tick at least one paper size that has pages in the list."; return; }
        var missing = groups.Where(g => !_areas.ContainsKey(g.Row.Key)).Select(g => g.Row.Key).ToList();
        var withArea = groups.Where(g => _areas.ContainsKey(g.Row.Key)).ToList();
        if (withArea.Count == 0) { _status.Text = "Draw the area first: click a paper size, then drag on its page."; return; }
        _busy = true;
        _findButton.IsEnabled = false;
        _hits.Clear();
        _status.Text = "Looking on " + withArea.Sum(g => g.Pages.Count) + " pages…";
        try
        {
            string note = missing.Count > 0 ? $" No area for {string.Join(", ", missing)} (skipped)." : "";
            if (ObjectsMode) await FindObjectsAsync(withArea, note); else await FindTextAsync(withArea, note);
        }
        catch (Exception ex) { _status.Text = "Could not read the pages: " + ex.Message; }
        finally { _busy = false; _findButton.IsEnabled = true; }
    }

    private async Task FindTextAsync(List<(PaperGroupRow Row, List<int> Pages)> groups, string note)
    {
        int noText = 0, rotated = 0;
        bool matchCase = _matchCase.IsChecked == true;
        var found = new List<BatchHit>();
        foreach (var (row, pages) in groups)
        {
            var a = _areas[row.Key];
            var answers = await TextEditService.GetAreaRunsAsync(_path, pages, a.U1, a.V1, a.U2, a.V2);
            foreach (var (page, hasText) in answers)
            {
                if (!hasText) { noText++; continue; }
                if (page.Rotation != 0) { rotated++; continue; }
                var pending = TextEditPendingStore.Page(_path, page.PageNumber);
                foreach (var run in page.Runs)
                {
                    string shown = pending.FirstOrDefault(e => e.Original.SamePlace(run))?.NewText ?? run.Text;
                    if (!Matches(shown, _find.Text, matchCase)) continue;
                    found.Add(new BatchHit { Page = page.PageNumber, Source = page, Run = run, Text = shown, After = ReplacedText(shown, _find.Text, _replace.Text, matchCase) });
                }
            }
        }
        foreach (var hit in found.OrderBy(h => h.Page)) _hits.Add(hit);
        var notes = new List<string>();
        if (noText > 0) notes.Add($"{noText} page{(noText == 1 ? " has" : "s have")} no text layer (scans or strokes: use Drawn object)");
        if (rotated > 0) notes.Add($"{rotated} rotated page{(rotated == 1 ? " was" : "s were")} skipped");
        _status.Text = $"{_hits.Count} found on {_hits.Select(h => h.Page).Distinct().Count()} page(s)." + (notes.Count > 0 ? " " + string.Join("; ", notes) + "." : "") + note;
        _replaceButton.IsEnabled = _deleteButton.IsEnabled = _hits.Count > 0;
    }

    private async Task FindObjectsAsync(List<(PaperGroupRow Row, List<int> Pages)> groups, string note)
    {
        var found = new List<PdfObjectRef>();
        int searched = 0;
        foreach (var (row, pages) in groups)
        {
            var a = _areas[row.Key];
            searched += pages.Count;
            found.AddRange(await ObjectEditService.FindInAreaAsync(_path, pages, a.U1, a.V1, a.U2, a.V2, _touch.IsChecked == true));
        }
        foreach (var group in found.GroupBy(o => o.PageNumber).OrderBy(g => g.Key))
        {
            var kinds = group.GroupBy(o => o.Kind).Select(k => $"{k.Count()} {k.Key}{(k.Count() == 1 ? "" : "s")}");
            _hits.Add(new BatchHit { Page = group.Key, Objects = group.ToList(), Text = string.Join(", ", kinds), After = "deleted from the file" });
        }
        int empty = searched - _hits.Count;
        _status.Text = $"Found on {_hits.Count} page(s), {found.Count} object(s)." + (empty > 0 ? $" {empty} page{(empty == 1 ? " has" : "s have")} nothing in the area." : "") + note;
        _replaceButton.IsEnabled = false;
        _deleteButton.IsEnabled = _hits.Count > 0;
        // show what was found on the page drawn on
        int shown = _picker.CurrentPage;
        _picker.ShowMarks(found.Where(o => o.PageNumber == shown).Select(o => (o.X0 / o.PageWidth, o.Y0 / o.PageHeight, o.X1 / o.PageWidth, o.Y1 / o.PageHeight)));
    }

    // ── act ──────────────────────────────────────────────────────────

    private void Apply(bool delete)
    {
        if (ObjectsMode)
        {
            var objects = _hits.Where(h => h.IsChecked).SelectMany(h => h.Objects).ToList();
            if (objects.Count == 0) { _status.Text = "Check at least one page."; return; }
            ObjectsToDelete = objects;
            Description = $"Removed {objects.Count} object{(objects.Count == 1 ? "" : "s")} on {objects.Select(o => o.PageNumber).Distinct().Count()} page(s) (saved to file)";
            try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
            return;
        }
        bool matchCase = _matchCase.IsChecked == true;
        var items = new List<(int, TextRun, TextEdit?)>();
        foreach (var hit in _hits.Where(h => h.IsChecked))
        {
            string text = delete ? ReplacedText(hit.Text, _find.Text, "", matchCase) : ReplacedText(hit.Text, _find.Text, _replace.Text, matchCase);
            if (delete && _find.Text.Length == 0) text = "";
            var edit = text == hit.Run.Text ? null : new TextEdit(hit.Page, hit.Source.Width, hit.Source.Height, hit.Run, text);
            if (edit == null && text == hit.Text) continue; // nothing changes
            items.Add((hit.Page, hit.Run, edit));
        }
        if (items.Count == 0) { _status.Text = "Nothing to change in the checked lines."; return; }
        Edits = items;
        Description = delete ? $"Deleted text in {items.Count} place{(items.Count == 1 ? "" : "s")}" : $"Replaced text in {items.Count} place{(items.Count == 1 ? "" : "s")}";
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
    }

    internal void ApplyForTest(bool delete) => Apply(delete);
}
