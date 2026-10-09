using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Threading.Tasks;
using Microsoft.Win32;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// Đóng dấu chữ (BẢN SAO, MẬT, ĐÃ DUYỆT…), đầu trang / chân trang / số trang / số văn bản, và che nội dung (redaction) cho nhiều trang một lần.
/// Kết quả là một file PDF mới; bản gốc không đổi. "Xem thử" vẽ đúng kết quả của trang đang xem.
/// </summary>
internal sealed class PageMarksPanel : UserControl
{
    private static readonly string[] Presets = { "BẢN SAO", "MẬT", "TUYỆT MẬT", "ĐÃ DUYỆT", "BẢN NHÁP", "KHẨN", "HỎA TỐC", "ĐÃ KÝ" };
    private static readonly (string Name, string Hex)[] Colors = { ("Đỏ", "#C0392B"), ("Xanh dương", "#1F4E9C"), ("Xám", "#808080"), ("Đen", "#000000"), ("Xanh lá", "#1E7B34") };

    private readonly string _path;
    private readonly int _pageCount;
    private readonly int _startPage;
    private readonly IAreaSurface _picker;
    private readonly TabControl _tabs = new();

    // watermark
    private readonly CheckBox _wmOn = new() { Content = "Đóng dấu chữ lên trang", IsChecked = true };
    private readonly ComboBox _wmText = new() { Height = 30, IsEditable = true };
    private readonly TextBox _wmSize = new() { Height = 30, Text = "80" };
    private readonly ComboBox _wmColor = new() { Height = 30 };
    private readonly TextBox _wmOpacity = new() { Height = 30, Text = "30" };
    private readonly ComboBox _wmAngle = new() { Height = 30 };
    private readonly ComboBox _wmPosition = new() { Height = 30 };
    private readonly CheckBox _wmBox = new() { Content = "Có khung viền quanh chữ (kiểu con dấu)" };

    // header / footer
    private readonly CheckBox _hfOn = new() { Content = "Đầu trang / chân trang", IsChecked = false };
    private readonly TextBox _hl = new() { Height = 28 }, _hc = new() { Height = 28 }, _hr = new() { Height = 28 };
    private readonly TextBox _fl = new() { Height = 28 }, _fc = new() { Height = 28 }, _fr = new() { Height = 28 };
    private readonly TextBox _hfSize = new() { Height = 28, Text = "10" }, _hfMargin = new() { Height = 28, Text = "24" }, _hfStart = new() { Height = 28, Text = "1" };

    // redaction
    private readonly List<RedactionArea> _areas = new();
    private readonly ListBox _areaList = new() { Height = 150 };
    private readonly ComboBox _dpi = new() { Height = 28 };

    // scope
    private readonly RadioButton _allPages = new() { Content = "Tất cả các trang", IsChecked = true };
    private readonly RadioButton _thisPage = new() { Content = "Chỉ trang đang xem" };
    private readonly RadioButton _range = new() { Content = "Các trang:" };
    private readonly TextBox _rangeBox = new() { Height = 28, Text = "1-" };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly XTButton _previewButton = new() { Text = "Xem thử trang này", Height = 32, Width = 150, Margin = new Thickness(0, 0, 8, 0) };

    internal event Action<PageMarkJob>? Applied;

    public PageMarksPanel(IAreaSurface surface, string path, int currentPage, int pageCount)
    {
        _picker = surface;
        _picker.PlaceMode = false;
        _path = path;
        _pageCount = Math.Max(1, pageCount);
        _startPage = Math.Clamp(currentPage, 1, _pageCount);
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        _tabs.Items.Add(new TabItem { Header = "Dấu chữ", Content = Scroll(BuildWatermarkTab()) });
        _tabs.Items.Add(new TabItem { Header = "Đầu / chân trang", Content = Scroll(BuildHeaderFooterTab()) });
        _tabs.Items.Add(new TabItem { Header = "Che nội dung", Content = Scroll(BuildRedactionTab()) });
        _tabs.SelectionChanged += (_, e) => { if (e.Source == _tabs) OnTabChanged(); };

        var scope = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        scope.Children.Add(new TextBlock { Text = "Áp dụng dấu chữ và đầu/chân trang cho", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
        foreach (var r in new[] { _allPages, _thisPage }) { r.Margin = new Thickness(0, 2, 0, 0); scope.Children.Add(r); }
        var rangeRow = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        DockPanel.SetDock(_range, Dock.Left); _range.VerticalAlignment = VerticalAlignment.Center; _range.Margin = new Thickness(0, 0, 8, 0);
        rangeRow.Children.Add(_range); rangeRow.Children.Add(_rangeBox);
        scope.Children.Add(rangeRow);
        _rangeBox.GotFocus += (_, _) => _range.IsChecked = true;

        _previewButton.Click += async (_, _) => await PreviewAsync();
        var apply = new XTButton { Text = "Áp dụng và lưu…", Width = 140, Height = 32, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        apply.Click += (_, _) => Accept();
        var cancel = new XTButton { Text = "Hủy", Width = 80, Height = 32, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(_previewButton); buttons.Children.Add(apply); buttons.Children.Add(cancel);

        var side = new DockPanel { Margin = new Thickness(14) };
        var bottom = new StackPanel();
        bottom.Children.Add(scope); bottom.Children.Add(_error); bottom.Children.Add(buttons);
        DockPanel.SetDock(bottom, Dock.Bottom);
        side.Children.Add(bottom); side.Children.Add(_tabs);
        Content = side;
        _picker.PageChanged += ShowAreaMarks;
    }

    /// <summary>Shows the pages in the reader; the panel is ready.</summary>
    public async Task StartAsync()
    {
        await _picker.SetPagesAsync(_path, Enumerable.Range(1, _pageCount).ToList(), _startPage);
        ShowAreaMarks();
    }

    public void Detach() { _picker.PageChanged -= ShowAreaMarks; _picker.ShowMarks(Array.Empty<(double, double, double, double)>()); }

    private static ScrollViewer Scroll(UIElement e) => new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = e };
    private static TextBlock Label(string text, double top = 10) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 4) };
    private static TextBlock Note(string text) => new() { Text = text, FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };

    private UIElement BuildWatermarkTab()
    {
        var p = new StackPanel { Margin = new Thickness(12) };
        p.Children.Add(_wmOn);
        p.Children.Add(Label("Nội dung (chọn mẫu hoặc gõ chữ riêng)"));
        foreach (var t in Presets) _wmText.Items.Add(t);
        _wmText.Text = "BẢN SAO";
        p.Children.Add(_wmText);
        var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (name, _) in Colors) _wmColor.Items.Add(name);
        _wmColor.SelectedIndex = 0;
        AddCell(row, 0, "Cỡ chữ", _wmSize); AddCell(row, 1, "Màu", _wmColor); AddCell(row, 2, "Độ đậm (%)", _wmOpacity);
        p.Children.Add(row);
        foreach (var a in new[] { "Chéo 45°", "Ngang", "Chéo 30°" }) _wmAngle.Items.Add(a);
        _wmAngle.SelectedIndex = 0;
        foreach (var a in new[] { "Giữa trang", "Phía trên", "Phía dưới" }) _wmPosition.Items.Add(a);
        _wmPosition.SelectedIndex = 0;
        var row2 = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        row2.ColumnDefinitions.Add(new ColumnDefinition()); row2.ColumnDefinitions.Add(new ColumnDefinition());
        AddCell(row2, 0, "Hướng chữ", _wmAngle); AddCell(row2, 1, "Vị trí", _wmPosition);
        p.Children.Add(row2);
        _wmBox.Margin = new Thickness(0, 10, 0, 0);
        p.Children.Add(_wmBox);
        p.Children.Add(Note("Dấu \"BẢN SAO\" chéo, đỏ, độ đậm 30% là kiểu thường dùng. Dấu \"ĐÃ DUYỆT\" nên chọn Ngang, có khung viền, đặt phía trên."));
        return p;
    }

    private static void AddCell(Grid grid, int column, string label, UIElement field)
    {
        var cell = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 6, 0, 0, 0) };
        cell.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 2) });
        cell.Children.Add(field);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private UIElement BuildHeaderFooterTab()
    {
        var p = new StackPanel { Margin = new Thickness(12) };
        p.Children.Add(_hfOn);
        p.Children.Add(Label("Đầu trang (trái · giữa · phải)"));
        p.Children.Add(Three(_hl, _hc, _hr));
        p.Children.Add(Label("Chân trang (trái · giữa · phải)"));
        p.Children.Add(Three(_fl, _fc, _fr));
        var quick = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        quick.Children.Add(Chip("Trang X/Y", () => { _fc.Text = "Trang {page}/{pages}"; }));
        quick.Children.Add(Chip("Số văn bản", () => { _hr.Text = "Số: {n:6}"; }));
        quick.Children.Add(Chip("Ngày in", () => { _fl.Text = "In ngày {date}"; }));
        quick.Children.Add(Chip("Tên file", () => { _hl.Text = "{file}"; }));
        quick.Children.Add(Chip("Xoá hết", () => { foreach (var t in new[] { _hl, _hc, _hr, _fl, _fc, _fr }) t.Text = ""; }));
        p.Children.Add(quick);
        var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition());
        AddCell(row, 0, "Cỡ chữ", _hfSize); AddCell(row, 1, "Cách mép (pt)", _hfMargin); AddCell(row, 2, "Số bắt đầu {n}", _hfStart);
        p.Children.Add(row);
        p.Children.Add(Note("Ký hiệu: {page} trang hiện tại, {pages} tổng số trang, {n} số thứ tự văn bản (bắt đầu từ số bên trên; {n:6} thêm số 0 cho đủ 6 chữ số, ví dụ 000125), {date} ngày, {time} giờ, {file} tên tệp, {user} người dùng."));
        _hl.TextChanged += (_, _) => _hfOn.IsChecked = true;
        foreach (var t in new[] { _hc, _hr, _fl, _fc, _fr }) t.TextChanged += (_, _) => { if (t.Text.Length > 0) _hfOn.IsChecked = true; };
        return p;
    }

    private static UIElement Three(TextBox a, TextBox b, TextBox c)
    {
        var g = new Grid();
        for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition());
        b.Margin = new Thickness(6, 0, 6, 0);
        Grid.SetColumn(a, 0); Grid.SetColumn(b, 1); Grid.SetColumn(c, 2);
        g.Children.Add(a); g.Children.Add(b); g.Children.Add(c);
        return g;
    }

    private static XTButton Chip(string text, Action click)
    {
        var b = new XTButton { Text = text, Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 6, 6) };
        b.Click += (_, _) => click();
        return b;
    }

    private UIElement BuildRedactionTab()
    {
        var p = new StackPanel { Margin = new Thickness(12) };
        p.Children.Add(new TextBlock
        {
            Text = "Kéo một khung trên trang bên trái để chọn phần cần che, rồi bấm một nút bên dưới. Phần bị che bị xoá thật khỏi tệp (không phải chỉ phủ ô đen).",
            TextWrapping = TextWrapping.Wrap
        });
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Chip("Che ở trang này", () => AddArea(AreaPages.This)));
        buttons.Children.Add(Chip("Che ở mọi trang", () => AddArea(AreaPages.All)));
        buttons.Children.Add(Chip("Che ở các trang đã nhập…", () => AddArea(AreaPages.Range)));
        p.Children.Add(buttons);
        p.Children.Add(Label("Các vùng sẽ che", 6));
        p.Children.Add(_areaList);
        var rm = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        rm.Children.Add(Chip("Bỏ vùng đã chọn", RemoveSelectedArea));
        rm.Children.Add(Chip("Bỏ hết", () => { _areas.Clear(); RefreshAreas(); }));
        p.Children.Add(rm);
        foreach (var d in new[] { "150 dpi (nhẹ)", "200 dpi", "300 dpi (nét)" }) _dpi.Items.Add(d);
        _dpi.SelectedIndex = 1;
        p.Children.Add(Label("Độ nét của trang bị che"));
        p.Children.Add(_dpi);
        p.Children.Add(Note("Trang có vùng che sẽ được chuyển thành ảnh, nên chữ trên trang đó không còn chọn/sao chép được (dùng OCR sau nếu cần). Trang không che giữ nguyên. Các trang để che dùng ô \"Các trang\" bên dưới."));
        p.Children.Add(Note("Lưu ý: dấu trang (bookmark), ghi chú và thông tin tài liệu của tệp vẫn còn; hãy kiểm tra trước khi gửi ra ngoài."));
        return p;
    }

    private enum AreaPages { This, All, Range }

    private void AddArea(AreaPages scope)
    {
        if (_picker.Area is not { } a || Math.Abs(a.U2 - a.U1) < 0.005 || Math.Abs(a.V2 - a.V1) < 0.005) { Error("Kéo một khung trên trang để chọn phần cần che."); return; }
        IEnumerable<int> pages;
        if (scope == AreaPages.This) pages = new[] { _picker.CurrentPage };
        else if (scope == AreaPages.All) pages = Enumerable.Range(1, _pageCount);
        else
        {
            var parsed = PdfPrintService.ParseRange(_rangeBox.Text, _pageCount);
            if (parsed == null || parsed.Count == 0) { Error("Các trang nhập chưa đúng (ví dụ 1-3,5)."); return; }
            pages = parsed;
        }
        double u1 = Math.Min(a.U1, a.U2), u2 = Math.Max(a.U1, a.U2), v1 = Math.Min(a.V1, a.V2), v2 = Math.Max(a.V1, a.V2);
        foreach (int page in pages) _areas.Add(new RedactionArea(page, u1, v1, u2, v2));
        _error.Visibility = Visibility.Collapsed;
        RefreshAreas();
    }

    private void RemoveSelectedArea()
    {
        if (_areaList.SelectedItem is ListBoxItem { Tag: IReadOnlyList<RedactionArea> group }) foreach (var g in group) _areas.Remove(g);
        RefreshAreas();
    }

    private void RefreshAreas()
    {
        _areaList.Items.Clear();
        foreach (var g in _areas.GroupBy(a => (a.U1, a.V1, a.U2, a.V2)))
        {
            var pages = g.Select(a => a.Page).OrderBy(x => x).ToList();
            string text = pages.Count == _pageCount && _pageCount > 1 ? "mọi trang" : pages.Count <= 6 ? "trang " + string.Join(", ", pages) : $"{pages.Count} trang ({pages[0]}…{pages[^1]})";
            _areaList.Items.Add(new ListBoxItem { Content = $"Vùng {g.Key.U1:P0}–{g.Key.U2:P0} × {g.Key.V1:P0}–{g.Key.V2:P0}: {text}", Tag = g.ToList() });
        }
        ShowAreaMarks();
    }

    private void ShowAreaMarks()
    {
        _picker.ShowMarks(_areas.Where(a => a.Page == _picker.CurrentPage).Select(a => (a.U1, a.V1, a.U2, a.V2)));
    }

    private void OnTabChanged() { }

    /// <summary>Draws the marks on the page being looked at (same code as the real run) and shows it in a small window.</summary>
    private async Task PreviewAsync()
    {
        if (!TryBuildJob("", out var job, out string error) || job == null) { Error(error); return; }
        if (job.Watermark == null && job.HeaderFooter == null) { Error("Bật dấu chữ hoặc đầu/chân trang để xem thử."); return; }
        _error.Visibility = Visibility.Collapsed;
        int page = Math.Max(1, _picker.CurrentPage);
        try
        {
            int running = (job.HeaderFooter?.StartNumber ?? 1) + Math.Max(0, job.Pages.OrderBy(x => x).ToList().IndexOf(page));
            string file = await PdfPageMarkService.PreviewAsync(job, page, running);
            var window = new MarkPreviewWindow(file) { Owner = Window.GetWindow(this) };
            window.Closed += (_, _) => { try { File.Delete(file); } catch { } };
            window.Show();
        }
        catch (Exception ex) { Error("Không xem thử được: " + ex.Message); }
    }

    private void Error(string text) { _error.Text = text; _error.Visibility = Visibility.Visible; }

    private bool TryBuildJob(string output, out PageMarkJob? job, out string error)
    {
        job = null; error = "";
        List<int> pages;
        if (_allPages.IsChecked == true) pages = Enumerable.Range(1, _pageCount).ToList();
        else if (_thisPage.IsChecked == true) pages = new List<int> { Math.Max(1, _picker.CurrentPage) };
        else
        {
            var parsed = PdfPrintService.ParseRange(_rangeBox.Text, _pageCount);
            if (parsed == null || parsed.Count == 0) { error = "Các trang nhập chưa đúng (ví dụ 1-3,5)."; return false; }
            pages = parsed;
        }

        WatermarkOptions? wm = null;
        if (_wmOn.IsChecked == true && !string.IsNullOrWhiteSpace(_wmText.Text))
        {
            if (!double.TryParse(_wmSize.Text, out double size) || size < 6 || size > 400) { error = "Cỡ chữ dấu chữ nên từ 6 đến 400."; return false; }
            if (!double.TryParse(_wmOpacity.Text, out double op) || op < 2 || op > 100) { error = "Độ đậm nên từ 2 đến 100 (%)."; return false; }
            double angle = _wmAngle.SelectedIndex switch { 1 => 0, 2 => 30, _ => 45 };
            wm = new WatermarkOptions(_wmText.Text.Trim(), size, Colors[Math.Max(0, _wmColor.SelectedIndex)].Hex, op / 100, angle,
                (MarkPosition)Math.Max(0, _wmPosition.SelectedIndex), _wmBox.IsChecked == true);
        }

        HeaderFooterOptions? hf = null;
        if (_hfOn.IsChecked == true)
        {
            if (!double.TryParse(_hfSize.Text, out double hs) || hs < 4 || hs > 72) { error = "Cỡ chữ đầu/chân trang nên từ 4 đến 72."; return false; }
            if (!double.TryParse(_hfMargin.Text, out double hm) || hm < 0 || hm > 200) { error = "Khoảng cách mép nên từ 0 đến 200."; return false; }
            if (!int.TryParse(_hfStart.Text, out int start) || start < 0) { error = "Số bắt đầu phải là số nguyên không âm."; return false; }
            hf = new HeaderFooterOptions(_hl.Text, _hc.Text, _hr.Text, _fl.Text, _fc.Text, _fr.Text, hs, hm, start, "#000000");
            if (hf.IsEmpty) hf = null;
        }

        int dpi = _dpi.SelectedIndex switch { 0 => 150, 2 => 300, _ => 200 };
        job = new PageMarkJob(_path, output, pages, wm, hf, _areas.ToList(), dpi);
        return true;
    }

    private void Accept()
    {
        if (!TryBuildJob("", out var probe, out string error) || probe == null) { Error(error); return; }
        if (probe.Watermark == null && probe.HeaderFooter == null && probe.Redactions.Count == 0) { Error("Chưa chọn gì để áp dụng: bật dấu chữ, nhập đầu/chân trang hoặc thêm vùng che."); return; }
        var save = new SaveFileDialog
        {
            Title = "Lưu bản đã xử lý", Filter = "PDF (*.pdf)|*.pdf", InitialDirectory = Path.GetDirectoryName(_path),
            FileName = Path.GetFileNameWithoutExtension(_path) + (probe.Redactions.Count > 0 ? "_redacted" : "_marked") + ".pdf", OverwritePrompt = true
        };
        if (save.ShowDialog(Window.GetWindow(this)) != true) return;
        if (string.Equals(Path.GetFullPath(save.FileName), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
        { Error("Hãy lưu thành tệp mới, không ghi đè bản gốc."); return; }
        Applied?.Invoke(probe with { OutputPath = save.FileName });
    }
}

/// <summary>A one-page picture of the result, so the marks can be judged before the real run.</summary>
internal sealed class MarkPreviewWindow : XTWindow
{
    private readonly PageAreaPicker _picker = new() { MinHeight = 360 };

    public MarkPreviewWindow(string file)
    {
        Title = "Xem thử";
        TitleBarMode = TitleBarMode.Tool;
        Width = 760; Height = 860; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Ui.Bg");
        _picker.Margin = new Thickness(12);
        Content = _picker;
        Loaded += async (_, _) => await _picker.SetPagesAsync(file, new[] { 1 }, 1);
    }
}
