using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    /// <summary>The text as the user sees it now (a pending edit counts).</summary>
    public string Text { get; init; } = "";
    public bool IsChecked { get => _checked; set { _checked = value; Raise(nameof(IsChecked)); } }
    public string After { get => _after; set { _after = value; Raise(nameof(After)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// "Find and replace in an area": the user draws an area on a page (like the areas of Read sheet info), chooses the pages, and the text inside that area on every chosen page is listed.
/// The checked hits are replaced by the new text or deleted; the edits wait in the open file like any text edit (Undo, Ctrl+S). Pages without a text layer (scans) are counted and skipped.
/// </summary>
internal sealed class BatchFindWindow : XTWindow
{
    private readonly string _path;
    private readonly int _pageCount;
    private int _samplePage;
    private readonly PageAreaPicker _picker = new() { MinHeight = 300 };
    private readonly TextBox _find = new() { Height = 26, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Leave empty to take every piece of text in the area" };
    private readonly TextBox _replace = new() { Height = 26, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly RadioButton _textMode = new() { Content = "Text", GroupName = "BatchWhat", IsChecked = true, Margin = new Thickness(0, 0, 14, 0) };
    private readonly RadioButton _objectsMode = new() { Content = "Drawn object (signature, stamp, line, image)", GroupName = "BatchWhat", ToolTip = "Finds pictures and strokes that lie in the area, whatever they are" };
    private readonly CheckBox _touch = new() { Content = "Also objects that only touch the area", Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly CheckBox _matchCase = new() { Content = "Match case", Margin = new Thickness(0, 6, 0, 0) };
    private readonly RadioButton _thisPage, _allPages, _range;
    private readonly TextBox _rangeBox = new() { Width = 130, Height = 26, Margin = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "For example 1-3, 7" };
    private readonly TextBox _sampleBox = new() { Width = 46, Height = 24, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ListView _list = new() { SelectionMode = SelectionMode.Extended };
    private readonly Button _findButton = new() { Content = "Find", Width = 90, Height = 28, IsDefault = true, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _replaceButton = new() { Content = "Replace checked", Height = 28, Padding = new Thickness(12, 0, 12, 0), IsEnabled = false };
    private readonly Button _deleteButton = new() { Content = "Delete checked text", Height = 28, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
    private readonly Button _close = new() { Content = "Close", Width = 90, Height = 28, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ObservableCollection<BatchHit> _hits = new();
    private bool _busy;
    private TextBlock _findLabel = null!, _replaceLabel = null!;

    /// <summary>What the user chose to do (set when the window closes with OK): the edits to put into the file's pending changes.</summary>
    public IReadOnlyList<(int Page, TextRun Place, TextEdit? Edit)> Edits { get; private set; } = Array.Empty<(int, TextRun, TextEdit?)>();
    public string Description { get; private set; } = "";
    /// <summary>Set instead of <see cref="Edits"/> in the objects mode: what to delete from the file (every page of the checked hits).</summary>
    public IReadOnlyList<PdfObjectRef> ObjectsToDelete { get; private set; } = Array.Empty<PdfObjectRef>();
    internal bool ObjectsMode => _objectsMode.IsChecked == true;

    internal PageAreaPicker Picker => _picker;
    internal RadioButton ObjectsModeButton => _objectsMode;
    internal CheckBox TouchBox => _touch;
    internal TextBox FindBox => _find;
    internal TextBox ReplaceBox => _replace;
    internal RadioButton AllPagesButton => _allPages;
    internal RadioButton RangeButton => _range;
    internal TextBox RangeBox => _rangeBox;
    internal ObservableCollection<BatchHit> Hits => _hits;
    internal string StatusText => _status.Text;
    internal Button ReplaceButton => _replaceButton;
    internal Button DeleteButton => _deleteButton;

    public BatchFindWindow(string path, int currentPage, int pageCount)
    {
        _path = path;
        _pageCount = Math.Max(1, pageCount);
        _samplePage = Math.Clamp(currentPage, 1, _pageCount);
        Title = "Find and replace in an area";
        TitleBarMode = TitleBarMode.Dialog;
        Width = 1040;
        Height = 760;
        MinWidth = 820;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

        var side = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        side.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(path), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = R("Ui.Text") });
        side.Children.Add(new TextBlock { Text = "1. Draw the area on the page (drag). Ctrl + wheel zooms.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = R("Ui.Text") });
        side.Children.Add(Label("2. What to find in the area"));
        side.Children.Add(new StackPanel { Children = { _textMode, _objectsMode } });
        _findLabel = Label("Text to find");
        _replaceLabel = Label("Replace with");
        side.Children.Add(_findLabel);
        side.Children.Add(_find);
        side.Children.Add(_replaceLabel);
        side.Children.Add(_replace);
        side.Children.Add(_matchCase);
        side.Children.Add(_touch);
        side.Children.Add(Label("3. Pages"));
        _thisPage = new RadioButton { Content = $"This page ({_samplePage})", GroupName = "BatchPages", Margin = new Thickness(0, 2, 0, 2) };
        _allPages = new RadioButton { Content = $"All {_pageCount} pages", GroupName = "BatchPages", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
        _range = new RadioButton { Content = "Pages", GroupName = "BatchPages", VerticalAlignment = VerticalAlignment.Center };
        _rangeBox.GotFocus += (_, _) => _range.IsChecked = true;
        side.Children.Add(_thisPage);
        side.Children.Add(_allPages);
        side.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0), Children = { _range, _rangeBox } });
        side.Children.Add(_findButton);
        side.Children.Add(_status);

        var sampleBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var prev = new Button { Content = "◀", Width = 28, Height = 24 };
        var next = new Button { Content = "▶", Width = 28, Height = 24 };
        sampleBar.Children.Add(new TextBlock { Text = "Page to draw on:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Foreground = R("Ui.Text") });
        sampleBar.Children.Add(prev);
        sampleBar.Children.Add(_sampleBox);
        sampleBar.Children.Add(next);
        sampleBar.Children.Add(new TextBlock { Text = $"of {_pageCount}   (the area is a share of the page, so it fits pages of the same size)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Foreground = R("Ui.Text") });
        var pickerPanel = new DockPanel();
        DockPanel.SetDock(sampleBar, Dock.Top);
        pickerPanel.Children.Add(sampleBar);
        pickerPanel.Children.Add(_picker);

        BuildList();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { _replaceButton, _deleteButton, _close } };

        var grid = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(210) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(pickerPanel, 1);
        Grid.SetRow(_list, 1);
        Grid.SetColumnSpan(_list, 2);
        _list.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(buttons, 2);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(side);
        grid.Children.Add(pickerPanel);
        grid.Children.Add(_list);
        grid.Children.Add(buttons);
        Content = new Border { Background = R("Ui.Surface"), Child = grid };

        _list.ItemsSource = _hits;
        _sampleBox.Text = _samplePage.ToString();
        prev.Click += async (_, _) => await GoToSampleAsync(_samplePage - 1);
        next.Click += async (_, _) => await GoToSampleAsync(_samplePage + 1);
        _sampleBox.LostFocus += async (_, _) => { if (int.TryParse(_sampleBox.Text, out int n)) await GoToSampleAsync(n); };
        _findButton.Click += async (_, _) => await FindAsync();
        _replaceButton.Click += (_, _) => Apply(delete: false);
        _deleteButton.Click += (_, _) => Apply(delete: true);
        _textMode.Checked += (_, _) => SwitchMode();
        _objectsMode.Checked += (_, _) => SwitchMode();
        _replace.TextChanged += (_, _) => RefreshAfter();
        _find.TextChanged += (_, _) => RefreshAfter();
        Loaded += async (_, _) => await GoToSampleAsync(_samplePage);
        _status.Text = "Draw the area, then press Find.";
    }

    private void SwitchMode()
    {
        bool objects = ObjectsMode;
        var textOnly = objects ? Visibility.Collapsed : Visibility.Visible;
        _find.Visibility = _replace.Visibility = _findLabel.Visibility = _replaceLabel.Visibility = _matchCase.Visibility = textOnly;
        _touch.Visibility = objects ? Visibility.Visible : Visibility.Collapsed;
        _replaceButton.Visibility = textOnly;
        _deleteButton.Content = objects ? "Delete checked objects" : "Delete checked text";
        _hits.Clear();
        _picker.ShowMarks(Array.Empty<(double, double, double, double)>());
        _replaceButton.IsEnabled = _deleteButton.IsEnabled = false;
        _status.Text = objects ? "Draw the area around the signature or stamp, then press Find." : "Draw the area, then press Find.";
    }

    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 12, 0, 4), FontWeight = FontWeights.SemiBold };

    private void BuildList()
    {
        var view = new GridView();
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(CheckBox.IsCheckedProperty, new System.Windows.Data.Binding(nameof(BatchHit.IsChecked)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        view.Columns.Add(new GridViewColumn { Header = "", Width = 34, CellTemplate = new DataTemplate { VisualTree = check } });
        view.Columns.Add(new GridViewColumn { Header = "Page", Width = 60, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(BatchHit.Page)) });
        view.Columns.Add(new GridViewColumn { Header = "Found in the area", Width = 330, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(BatchHit.Text)) });
        view.Columns.Add(new GridViewColumn { Header = "After \"Replace checked\"", Width = 330, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(BatchHit.After)) });
        _list.View = view;
    }

    private async Task GoToSampleAsync(int page)
    {
        _samplePage = Math.Clamp(page, 1, _pageCount);
        _sampleBox.Text = _samplePage.ToString();
        await _picker.ShowPageAsync(_path, _samplePage);
    }

    private List<int>? ChosenPages()
    {
        if (_thisPage.IsChecked == true) return new List<int> { _samplePage };
        if (_range.IsChecked == true) return OcrWindow.ParsePages(_rangeBox.Text, _pageCount)?.Select(p => p + 1).ToList();
        return Enumerable.Range(1, _pageCount).ToList();
    }

    internal static string ReplacedText(string text, string find, string replace, bool matchCase)
        => find.Length == 0 ? replace : text.Replace(find, replace, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    internal static bool Matches(string text, string find, bool matchCase)
        => find.Length == 0 || text.Contains(find, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private void RefreshAfter()
    {
        foreach (var hit in _hits) hit.After = ReplacedText(hit.Text, _find.Text, _replace.Text, _matchCase.IsChecked == true);
    }

    internal async Task FindAsync()
    {
        if (_busy) return;
        if (_picker.Area is not { } area) { _status.Text = "Draw the area on the page first (drag with the mouse)."; return; }
        var pages = ChosenPages();
        if (pages == null) { _status.Text = $"The pages must be like 1-3, 7 (this file has {_pageCount})."; return; }
        _busy = true;
        _findButton.IsEnabled = false;
        _hits.Clear();
        if (ObjectsMode) { await FindObjectsAsync(area, pages); return; }
        _status.Text = "Reading the text of " + pages.Count + " page" + (pages.Count == 1 ? "" : "s") + "…";
        try
        {
            var answers = await TextEditService.GetAreaRunsAsync(_path, pages, area.U1, area.V1, area.U2, area.V2);
            int noText = 0, rotated = 0;
            bool matchCase = _matchCase.IsChecked == true;
            foreach (var (page, hasText) in answers.OrderBy(a => a.Page.PageNumber))
            {
                if (!hasText) { noText++; continue; }
                if (page.Rotation != 0) { rotated++; continue; }
                var pending = TextEditPendingStore.Page(_path, page.PageNumber);
                foreach (var run in page.Runs)
                {
                    string shown = pending.FirstOrDefault(e => e.Original.SamePlace(run))?.NewText ?? run.Text;
                    if (!Matches(shown, _find.Text, matchCase)) continue;
                    _hits.Add(new BatchHit { Page = page.PageNumber, Source = page, Run = run, Text = shown, After = ReplacedText(shown, _find.Text, _replace.Text, matchCase) });
                }
            }
            var notes = new List<string>();
            if (noText > 0) notes.Add($"{noText} page{(noText == 1 ? " has" : "s have")} no text layer (scans or strokes: use Edit Object)");
            if (rotated > 0) notes.Add($"{rotated} rotated page{(rotated == 1 ? " was" : "s were")} skipped");
            _status.Text = $"{_hits.Count} found on {_hits.Select(h => h.Page).Distinct().Count()} page(s)." + (notes.Count > 0 ? " " + string.Join("; ", notes) + "." : "");
            _replaceButton.IsEnabled = _deleteButton.IsEnabled = _hits.Count > 0;
        }
        catch (Exception ex) { _status.Text = "Could not read the pages: " + ex.Message; }
        finally { _busy = false; _findButton.IsEnabled = true; }
    }

    private async Task FindObjectsAsync((double U1, double V1, double U2, double V2) area, List<int> pages)
    {
        _status.Text = "Looking for drawn objects on " + pages.Count + " page" + (pages.Count == 1 ? "" : "s") + "…";
        try
        {
            var rotated = new List<int>();
            var found = await ObjectEditService.FindInAreaAsync(_path, pages, area.U1, area.V1, area.U2, area.V2, _touch.IsChecked == true, rotated);
            foreach (var group in found.GroupBy(o => o.PageNumber).OrderBy(g => g.Key))
            {
                var kinds = group.GroupBy(o => o.Kind).Select(k => $"{k.Count()} {k.Key}{(k.Count() == 1 ? "" : "s")}");
                _hits.Add(new BatchHit { Page = group.Key, Objects = group.ToList(), Text = string.Join(", ", kinds), After = "deleted from the file" });
            }
            int empty = pages.Count - rotated.Count - _hits.Count;
            var notes = new List<string>();
            if (empty > 0) notes.Add($"{empty} page{(empty == 1 ? " has" : "s have")} nothing in the area");
            if (rotated.Count > 0) notes.Add($"{rotated.Count} rotated page{(rotated.Count == 1 ? " was" : "s were")} skipped");
            _status.Text = $"Found on {_hits.Count} page(s), {found.Count} object(s)." + (notes.Count > 0 ? " " + string.Join("; ", notes) + "." : "");
            _replaceButton.IsEnabled = false;
            _deleteButton.IsEnabled = _hits.Count > 0;
            // show what was found on the page drawn on
            _picker.ShowMarks(found.Where(o => o.PageNumber == _samplePage).Select(o => (o.X0 / o.PageWidth, o.Y0 / o.PageHeight, o.X1 / o.PageWidth, o.Y1 / o.PageHeight)));
        }
        catch (Exception ex) { _status.Text = "Could not read the pages: " + ex.Message; }
        finally { _busy = false; _findButton.IsEnabled = true; }
    }

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
