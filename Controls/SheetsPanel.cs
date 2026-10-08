using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp.Controls;

/// <summary>1 bản vẽ trong danh sách Sheets: trang của window và thông tin XT (/XTSheet) của trang đó.</summary>
public sealed class SheetRow : System.ComponentModel.INotifyPropertyChanged
{
    private int _open, _resolved;
    internal SheetRow(PageRow row, int pageNumber, XTSheetPageInfo info)
    {
        Row = row;
        PageNumber = pageNumber;
        Info = info;
    }

    internal PageRow Row { get; }
    internal XTSheetPageInfo Info { get; }
    public int PageNumber { get; }
    public string Title => (Info.No.Length > 0 ? Info.No + "  " : "") + (Info.Title.Length > 0 ? Info.Title : Info.Layout);
    /// <summary>Số góp ý chưa xử lý / đã xử lý của bản vẽ (nạp nền sau khi mở tab).</summary>
    public int OpenComments { get => _open; internal set { _open = value; Notify(); } }
    public int ResolvedComments { get => _resolved; internal set { _resolved = value; Notify(); } }
    public string Detail => string.Join(" · ", new[]
    {
        string.Join(" › ", new[] { Info.Group, Info.Subset }.Where(s => s.Length > 0)), Info.Scale, "p." + PageNumber,
        _open > 0 ? $"{_open} open" : "", _resolved > 0 ? $"{_resolved} resolved" : ""
    }.Where(s => s.Length > 0));
    public System.Windows.FontWeight DetailWeight => _open > 0 ? FontWeights.SemiBold : FontWeights.Normal;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Notify()
    {
        PropertyChanged?.Invoke(this, new(nameof(Detail)));
        PropertyChanged?.Invoke(this, new(nameof(DetailWeight)));
    }
    public string DwgPath => Info.Dwg;
    public bool HasDwg => Info.Dwg.Length > 0 && File.Exists(Info.Dwg);
}

/// <summary>
/// Tab Sheets của panel trái: danh sách bản vẽ của file đang xem lấy từ thông tin sheet mà XT_PRINT / XT_SHEETS ghi vào PDF
/// (số hiệu, tên, tỷ lệ, Hạng mục, Subset). Lọc theo chữ, bấm để tới trang, "Open DWG", tách file theo Hạng mục / Subset.
/// </summary>
public sealed class SheetsPanel : UserControl
{
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly TextBlock _empty;
    private readonly XTStyle.Controls.XTButton _splitButton;
    private readonly XTStyle.Controls.XTButton _readButton;
    private List<SheetRow> _all = new();
    private int _generation;
    private bool _selecting;

    private static readonly Dictionary<string, (long Length, DateTime Stamp, IReadOnlyDictionary<int, XTSheetPageInfo> Infos)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public SheetsPanel()
    {
        Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;

        _search = new TextBox { Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 8), ToolTip = "Filter by sheet number, title, group or scale; type “open” to see the sheets with unresolved comments" };
        _search.TextChanged += (_, _) => Rebuild();

        _splitButton = new XTStyle.Controls.XTButton { Text = "Split by…", Height = 28, Margin = new Thickness(12, 0, 12, 8), HorizontalAlignment = HorizontalAlignment.Left };
        _splitButton.SetResourceReference(StyleProperty, "UiGhostButton");
        _splitButton.Click += SplitButton_Click;

        var exportButton = new XTStyle.Controls.XTButton { Text = "Export list…", Height = 28, Margin = new Thickness(0, 0, 12, 8), ToolTip = "Save the drawing register (number, title, scale, group, paper size, page, DWG) as a CSV file for Excel" };
        exportButton.SetResourceReference(StyleProperty, "UiGhostButton");
        exportButton.Click += async (_, _) => await ExportRegisterAsync();
        _splitButton.Margin = new Thickness(12, 0, 8, 8);
        _readButton = new XTStyle.Controls.XTButton { Text = "Read info…", Height = 28, Margin = new Thickness(0, 0, 8, 8), ToolTip = "Read sheet numbers and titles from the text in the title block (for PDFs not plotted by XT_PRINT)" };
        _readButton.SetResourceReference(StyleProperty, "UiGhostButton");
        _readButton.Click += (_, _) => ReadInfoRequested?.Invoke();
        var labelsButton = new XTStyle.Controls.XTButton { Text = "Page labels", Height = 28, Margin = new Thickness(0, 0, 8, 8), ToolTip = "Write the sheet numbers as the PDF's page labels, so any viewer shows KT-05 instead of page 37" };
        labelsButton.SetResourceReference(StyleProperty, "UiGhostButton");
        labelsButton.Click += (_, _) => PageLabelsRequested?.Invoke();
        var linksButton = new XTStyle.Controls.XTButton { Text = "Link numbers", Height = 28, Margin = new Thickness(0, 0, 8, 8), ToolTip = "Make sheet numbers clickable: contents lines and references such as “see KT-05” jump to that sheet" };
        linksButton.SetResourceReference(StyleProperty, "UiGhostButton");
        linksButton.Click += (_, _) => LinkNumbersRequested?.Invoke();
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, Children = { _splitButton, exportButton, labelsButton, linksButton, _readButton } };
        var top = new StackPanel { Children = { _search, buttons } };
        DockPanel.SetDock(top, Dock.Top);

        _list = new ListBox { BorderThickness = new Thickness(0, 1, 0, 0), Background = Brushes.Transparent, SelectionMode = SelectionMode.Single };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SetResourceReference(Control.BorderBrushProperty, "Ui.Border");
        _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "UiRowItem");
        _list.ItemTemplate = BuildTemplate();
        _list.SelectionChanged += (_, _) =>
        {
            if (!_selecting && _list.SelectedItem is SheetRow sheet) PageActivated?.Invoke(sheet.Row);
        };
        _list.ContextMenu = BuildMenu();
        _list.ContextMenuOpening += (_, e) => { if (_list.SelectedItem == null) e.Handled = true; };

        _empty = new TextBlock
        {
            Margin = new Thickness(14), TextWrapping = TextWrapping.Wrap, Foreground = R("Ui.Muted"), Visibility = Visibility.Collapsed,
            Text = "This file has no XT sheet information. Plot it with the current XT_PRINT / XT_SHEETS, or use “Read info…” to read the sheet numbers and titles from the text in its title blocks."
        };

        var root = new DockPanel();
        root.Children.Add(top);
        root.Children.Add(new Grid { Children = { _list, _empty } });
        Content = root;
        ShowEmpty(false);
    }

    /// <summary>Chọn 1 bản vẽ → tới trang đó.</summary>
    internal event Action<PageRow>? PageActivated;
    /// <summary>Tách file: mỗi phần (nhãn, các trang) thành 1 file.</summary>
    internal event Action<IReadOnlyList<(string Label, IReadOnlyList<PageRow> Pages)>>? SplitRequested;
    /// <summary>Mở "Read sheet info" (đọc số hiệu / tên từ chữ trong khung tên).</summary>
    internal event Action? ReadInfoRequested;
    /// <summary>A page command (OCR, Read sheet info) for the chosen sheets, from the right-click menu.</summary>
    internal event Action<PageCommand, IReadOnlyList<PageRow>>? PageActionRequested;
    /// <summary>Ghi số hiệu bản vẽ làm nhãn trang của file.</summary>
    internal event Action? PageLabelsRequested;
    /// <summary>Gắn liên kết bấm được cho số hiệu bản vẽ trong chữ của các trang khác.</summary>
    internal event Action? LinkNumbersRequested;
    /// <summary>Số bản vẽ (null = file không có thông tin sheet).</summary>
    internal event Action<int?>? CountChanged;

    private static DataTemplate BuildTemplate()
    {
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 4, 12, 4));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(SheetRow.Title)));
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        stack.AppendChild(title);
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(SheetRow.Detail)));
        detail.SetBinding(TextBlock.FontWeightProperty, new System.Windows.Data.Binding(nameof(SheetRow.DetailWeight)));
        detail.SetValue(TextBlock.FontSizeProperty, 11.0);
        detail.SetValue(UIElement.OpacityProperty, 0.7);
        detail.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        stack.AppendChild(detail);
        return new DataTemplate { VisualTree = stack };
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open DWG" };
        open.Click += (_, _) => OpenDwg();
        var copy = new MenuItem { Header = "Copy sheet number" };
        copy.Click += (_, _) => { if (_list.SelectedItem is SheetRow s && s.Info.No.Length > 0) try { Clipboard.SetText(s.Info.No); } catch { } };
        var ocr = new MenuItem { Header = "OCR this sheet…" };
        ocr.Click += (_, _) => { if (_list.SelectedItems.OfType<SheetRow>().Select(r => r.Row).ToList() is { Count: > 0 } rows) PageActionRequested?.Invoke(PageCommand.Ocr, rows); };
        var read = new MenuItem { Header = "Read sheet info of this sheet…" };
        read.Click += (_, _) => { if (_list.SelectedItems.OfType<SheetRow>().Select(r => r.Row).ToList() is { Count: > 0 } rows) PageActionRequested?.Invoke(PageCommand.ReadSheetInfo, rows); };
        menu.Items.Add(open);
        menu.Items.Add(copy);
        menu.Items.Add(new Separator());
        menu.Items.Add(ocr);
        menu.Items.Add(read);
        menu.Opened += (_, _) =>
        {
            open.IsEnabled = (_list.SelectedItem as SheetRow)?.HasDwg == true;
            int n = _list.SelectedItems.Count;
            ocr.Header = n > 1 ? $"OCR {n} sheets…" : "OCR this sheet…";
            read.Header = n > 1 ? $"Read sheet info of {n} sheets…" : "Read sheet info of this sheet…";
        };
        return menu;
    }

    private void OpenDwg()
    {
        if (_list.SelectedItem is not SheetRow { HasDwg: true } sheet) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(sheet.DwgPath) { UseShellExecute = true }); }
        catch { }
    }

    private void ShowEmpty(bool empty)
    {
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _search.IsEnabled = _splitButton.IsEnabled = !empty;
        _readButton.IsEnabled = true;

    }

    /// <summary>Nạp bản vẽ của window <paramref name="group"/> (null = chưa mở file).</summary>
    internal async Task SetGroupAsync(DocumentGroup? group)
    {
        int generation = ++_generation;
        if (group == null || group.Pages.Count == 0)
        {
            _all = new List<SheetRow>();
            ShowEmpty(true);
            CountChanged?.Invoke(null);
            return;
        }
        var pages = group.Pages.ToList();
        var infoBySource = await Task.Run(() =>
            pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(path => path, ReadCached, StringComparer.OrdinalIgnoreCase));
        if (generation != _generation) return;

        var rows = new List<SheetRow>();
        for (int i = 0; i < pages.Count; i++)
            if (infoBySource[pages[i].SourcePath].TryGetValue(pages[i].PageNumber, out var info))
                rows.Add(new SheetRow(pages[i], i + 1, info));
        _all = rows;
        ShowEmpty(rows.Count == 0);
        Rebuild();
        CountChanged?.Invoke(rows.Count == 0 ? null : rows.Count);
        _ = LoadCommentCountsAsync(rows, generation);
    }

    /// <summary>Góp ý (chưa xử lý / đã xử lý) của từng bản vẽ, đọc nền từ chú thích của file — để thấy bản vẽ nào còn góp ý chưa xử lý ("open" lọc được trong ô tìm).</summary>
    private async Task LoadCommentCountsAsync(List<SheetRow> rows, int generation)
    {
        try
        {
            foreach (var group in rows.GroupBy(r => r.Row.SourcePath, StringComparer.OrdinalIgnoreCase))
            {
                var all = await AnnotationStore.GetAllAsync(group.Key);
                if (generation != _generation) return;
                var byPage = all.Where(a => a.Selectable && a.Kind != QuickAnnotationKind.Reply).GroupBy(a => a.PageNumber).ToDictionary(g => g.Key);
                foreach (var row in group)
                    if (byPage.TryGetValue(row.Row.PageNumber, out var list))
                    {
                        row.ResolvedComments = list.Count(a => a.Resolved);
                        row.OpenComments = list.Count(a => !a.Resolved);
                    }
            }
        }
        catch { /* chú thích không đọc được: giữ danh sách không kèm số góp ý */ }
    }

    private static IReadOnlyDictionary<int, XTSheetPageInfo> ReadCached(string path)
    {
        try
        {
            var info = new FileInfo(path);
            lock (Cache)
                if (Cache.TryGetValue(path, out var hit) && hit.Length == info.Length && hit.Stamp == info.LastWriteTimeUtc) return hit.Infos;
            var infos = XTSheetIndex.Read(path);
            lock (Cache) Cache[path] = (info.Length, info.LastWriteTimeUtc, infos);
            return infos;
        }
        catch { return new Dictionary<int, XTSheetPageInfo>(); }
    }

    /// <summary>Trang đang xem đổi: chọn dòng tương ứng (không báo lại).</summary>
    internal void SelectRow(PageRow? row)
    {
        var match = row == null ? null : _list.Items.OfType<SheetRow>().FirstOrDefault(s => ReferenceEquals(s.Row, row));
        if (ReferenceEquals(_list.SelectedItem, match)) return;
        _selecting = true;
        try { _list.SelectedItem = match; if (match != null) _list.ScrollIntoView(match); }
        finally { _selecting = false; }
    }

    private void Rebuild()
    {
        string filter = _search.Text.Trim();
        _list.ItemsSource = filter.Length == 0 ? _all
            : _all.Where(s => s.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) || s.Detail.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Bảng kê bản vẽ của file đang xem → CSV (kèm khổ giấy từng trang).</summary>
    private async Task ExportRegisterAsync()
    {
        if (_all.Count == 0) return;
        var sizes = new Dictionary<string, (double Width, double Height)[]?>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in _all.Select(s => s.Row.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { sizes[path] = await PdfThumbnailService.GetPageSizesAsync(path); } catch { sizes[path] = null; }
        }
        var rows = _all.Select(s =>
        {
            var arr = sizes.GetValueOrDefault(s.Row.SourcePath);
            string size = arr != null && s.Row.PageNumber - 1 < arr.Length && arr[s.Row.PageNumber - 1].Width > 0
                ? PdfExportService.SizeName(arr[s.Row.PageNumber - 1].Width, arr[s.Row.PageNumber - 1].Height).Name : "";
            return new RegisterRow(s.PageNumber, s.Info, size);
        }).ToList();

        string first = _all[0].Row.SourcePath;
        using var dialog = new System.Windows.Forms.SaveFileDialog
        {
            Title = "Save the drawing register", Filter = "CSV (*.csv)|*.csv", DefaultExt = "csv",
            FileName = Path.GetFileNameWithoutExtension(first) + " - drawing register.csv",
            InitialDirectory = Path.GetDirectoryName(first) is { } dir && Directory.Exists(dir) ? dir : ""
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        try
        {
            File.WriteAllText(dialog.FileName, XTSheetRegister.ToCsv(rows), new System.Text.UTF8Encoding(false));
            XTStyle.Controls.XTGrowl.Success($"Saved {rows.Count} sheets to {Path.GetFileName(dialog.FileName)}", Window.GetWindow(this));
        }
        catch (Exception ex) { AppDialog.Show(Window.GetWindow(this), "Could not save the register:\n" + ex.Message, "Drawing register", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SplitButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = _splitButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var byGroup = new MenuItem { Header = "Hạng mục (group)" };
        byGroup.Click += (_, _) => Split(s => s.Info.Group);
        var bySubset = new MenuItem { Header = "Subset" };
        bySubset.Click += (_, _) => Split(s => s.Info.Subset);
        var byDwg = new MenuItem { Header = "Source DWG" };
        byDwg.Click += (_, _) => Split(s => Path.GetFileNameWithoutExtension(s.Info.Dwg));
        menu.Items.Add(byGroup);
        menu.Items.Add(bySubset);
        menu.Items.Add(byDwg);
        menu.IsOpen = true;
    }

    /// <summary>Gom các bản vẽ liền nhau (theo thứ tự trong file) có cùng khoá thành 1 phần; trang không có thông tin sheet đi theo phần đứng trước.</summary>
    private void Split(Func<SheetRow, string> key)
    {
        var parts = new List<(string Label, List<PageRow> Pages)>();
        foreach (var sheet in _all)
        {
            string label = key(sheet) is { Length: > 0 } k ? k : "(none)";
            if (parts.Count == 0 || !string.Equals(parts[^1].Label, label, StringComparison.OrdinalIgnoreCase)) parts.Add((label, new List<PageRow>()));
            parts[^1].Pages.Add(sheet.Row);
        }
        if (parts.Count < 2) return;
        SplitRequested?.Invoke(parts.Select(p => (p.Label, (IReadOnlyList<PageRow>)p.Pages)).ToList());
    }
}
