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
/// Ký số: chọn chứng thư (USB token của nhà cung cấp hiện trong kho chứng thư Windows, hoặc file .pfx), kéo một khung trên trang để đặt chữ ký, con dấu bên trái và tên cơ quan,
/// người ký, thời gian bên phải. Chỉ một người ký. Kết quả luôn là một file mới, bản gốc không đổi.
/// </summary>
internal sealed class SignPanel : UserControl
{
    private readonly string _path;
    private readonly int _pageCount;
    private readonly IAreaSurface _picker;
    private readonly int _startPage;
    private readonly ComboBox _certs = new() { Height = 30, MinWidth = 200 };
    private readonly TextBox _org = new() { Height = 30 };
    private readonly TextBox _seal = new() { Height = 30, IsReadOnly = true };
    private readonly ComboBox _reason = new() { Height = 30, IsEditable = true };
    private readonly TextBox _location = new() { Height = 30 };
    private readonly TextBox _tsa = new() { Height = 30 };
    private readonly CheckBox _revocation = new() { Content = "Nhúng thông tin thu hồi chứng thư (OCSP/CRL) để kiểm tra lâu dài", IsChecked = true };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    private readonly List<SigningCertificate> _list = new();

    internal event Action<DigitalSignRequest>? Applied;

    public SignPanel(IAreaSurface surface, string path, int currentPage, int pageCount)
    {
        _picker = surface;
        _picker.PlaceMode = false;
        _path = path;
        _pageCount = Math.Max(1, pageCount);
        _startPage = Math.Clamp(currentPage, 1, _pageCount);

        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var form = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        form.Children.Add(new TextBlock { Text = "Ký số văn bản", FontSize = 17, FontWeight = FontWeights.SemiBold });
        form.Children.Add(new TextBlock
        {
            Text = "Cắm USB token (ví dụ Viettel-CA) rồi chọn chứng thư. Khi ký, trình điều khiển token sẽ hỏi mã PIN. Văn bản gốc không bị thay đổi: chữ ký được lưu vào một tệp mới.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 6, 0, 0)
        });

        form.Children.Add(Label("Chứng thư số", 14));
        var certRow = new DockPanel();
        var refresh = new XTButton { Text = "Làm mới", Width = 72, Height = 30, Margin = new Thickness(6, 0, 0, 0) };
        var pfx = new XTButton { Text = "File .pfx…", Width = 82, Height = 30, Margin = new Thickness(6, 0, 0, 0) };
        refresh.Click += (_, _) => LoadCertificates(null);
        pfx.Click += (_, _) => PickPfx();
        DockPanel.SetDock(pfx, Dock.Right); DockPanel.SetDock(refresh, Dock.Right);
        certRow.Children.Add(pfx); certRow.Children.Add(refresh); certRow.Children.Add(_certs);
        form.Children.Add(certRow);
        form.Children.Add(Note("Nếu không thấy chứng thư: cắm token, cài phần mềm của nhà cung cấp rồi bấm Làm mới."));
        _certs.SelectionChanged += (_, _) => OnCertChanged();

        form.Children.Add(Label("Tên cơ quan, tổ chức", 12));
        form.Children.Add(_org);
        form.Children.Add(Label("Con dấu (ảnh PNG nền trong suốt)", 12));
        var sealRow = new DockPanel();
        var browse = new XTButton { Text = "Chọn…", Width = 72, Height = 30, Margin = new Thickness(6, 0, 0, 0) };
        var clear = new XTButton { Text = "Bỏ", Width = 52, Height = 30, Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += (_, _) => PickSeal();
        clear.Click += (_, _) => _seal.Text = "";
        DockPanel.SetDock(clear, Dock.Right); DockPanel.SetDock(browse, Dock.Right);
        sealRow.Children.Add(clear); sealRow.Children.Add(browse); sealRow.Children.Add(_seal);
        form.Children.Add(sealRow);

        form.Children.Add(Label("Lý do ký", 12));
        foreach (var r in new[] { "Ban hành văn bản", "Phê duyệt", "Xác nhận", "Sao y bản chính" }) _reason.Items.Add(r);
        _reason.Text = "Ban hành văn bản";
        form.Children.Add(_reason);
        form.Children.Add(Label("Nơi ký (tuỳ chọn)", 12));
        form.Children.Add(_location);

        form.Children.Add(Label("Máy chủ dấu thời gian (TSA, tuỳ chọn)", 12));
        form.Children.Add(_tsa);
        form.Children.Add(Note("Văn bản hành chính nên có dấu thời gian. Địa chỉ lấy từ nhà cung cấp chứng thư số; để trống nếu chưa có."));
        _revocation.Margin = new Thickness(0, 10, 0, 0);
        form.Children.Add(_revocation);

        form.Children.Add(_hint);
        form.Children.Add(_error);

        var sign = new XTButton { Text = "Ký và lưu…", Width = 112, Height = 32, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        sign.Click += (_, _) => Accept();
        var cancel = new XTButton { Text = "Hủy", Width = 84, Height = 32, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(sign); buttons.Children.Add(cancel);
        form.Children.Add(buttons);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = form };

        _org.Text = AppSettings.SignOrganization;
        _seal.Text = File.Exists(AppSettings.SignSealImage) ? AppSettings.SignSealImage : "";
        _location.Text = AppSettings.SignLocation;
        _tsa.Text = AppSettings.SignTsaUrl;
        _revocation.IsChecked = AppSettings.SignEmbedRevocation;
        LoadCertificates(AppSettings.SignThumbprint);

    }

    /// <summary>Shows the pages in the reader and puts a default signature box at the bottom right.</summary>
    public async Task StartAsync()
    {
        await _picker.SetPagesAsync(_path, Enumerable.Range(1, _pageCount).ToList(), _startPage);
        _picker.SetArea((0.52, 0.80, 0.96, 0.95));
    }

    public void Detach() { }

    private static TextBlock Label(string text, double top) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 5) };
    private static TextBlock Note(string text) => new() { Text = text, FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };

    private void LoadCertificates(string? select)
    {
        var keep = (_certs.SelectedItem as ComboBoxItem)?.Tag as SigningCertificate;
        _list.RemoveAll(c => !c.FromFile);
        _list.InsertRange(0, PdfDigitalSignService.ListStoreCertificates());
        Rebuild(select ?? keep?.Thumbprint);
    }

    private void Rebuild(string? select)
    {
        _certs.Items.Clear();
        foreach (var c in _list) _certs.Items.Add(new ComboBoxItem { Content = c.Display, Tag = c });
        _certs.SelectedItem = _certs.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((SigningCertificate)i.Tag).Thumbprint == select)
            ?? _certs.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _hint.Text = _list.Count == 0
            ? "Chưa thấy chứng thư số nào có khóa riêng. Kiểm tra token đã cắm và đã cài phần mềm nhà cung cấp, hoặc dùng file .pfx để thử."
            : "Kéo trên trang để vẽ khung chữ ký (kéo trong khung để di chuyển, kéo ô vuông để đổi cỡ). Dùng ◀ ▶ để chọn trang.";
    }

    private void OnCertChanged()
    {
        if ((_certs.SelectedItem as ComboBoxItem)?.Tag is SigningCertificate c && string.IsNullOrWhiteSpace(_org.Text)) _org.Text = c.Organization;
    }

    private void PickPfx()
    {
        var dlg = new OpenFileDialog { Filter = "Chứng thư (*.pfx;*.p12)|*.pfx;*.p12", Title = "Chọn file chứng thư" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var password = new PfxPasswordPrompt(Window.GetWindow(this)!).Ask();
        if (password == null) return;
        try
        {
            var cert = PdfDigitalSignService.LoadPfx(dlg.FileName, password);
            _list.RemoveAll(c => c.FromFile);
            _list.Add(cert);
            Rebuild(cert.Thumbprint);
        }
        catch (Exception ex) { Error("Không mở được file chứng thư: " + ex.Message); }
    }

    private void PickSeal()
    {
        var dlg = new OpenFileDialog { Filter = "Ảnh (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", Title = "Chọn ảnh con dấu" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) _seal.Text = dlg.FileName;
    }

    private void Error(string text) { _error.Text = text; _error.Visibility = Visibility.Visible; }

    private void Accept()
    {
        if ((_certs.SelectedItem as ComboBoxItem)?.Tag is not SigningCertificate cert) { Error("Chọn chứng thư số để ký."); return; }
        if (_picker.Area is not { } a || Math.Abs(a.U2 - a.U1) < 0.03 || Math.Abs(a.V2 - a.V1) < 0.02) { Error("Kéo một khung đủ lớn trên trang để đặt chữ ký."); return; }
        var save = new SaveFileDialog
        {
            Title = "Lưu văn bản đã ký", Filter = "PDF (*.pdf)|*.pdf",
            InitialDirectory = Path.GetDirectoryName(_path),
            FileName = Path.GetFileNameWithoutExtension(_path) + "_signed.pdf", OverwritePrompt = true
        };
        if (save.ShowDialog(Window.GetWindow(this)) != true) return;
        if (string.Equals(Path.GetFullPath(save.FileName), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
        { Error("Hãy lưu thành tệp mới, không ghi đè bản gốc."); return; }

        AppSettings.SignOrganization = _org.Text.Trim();
        AppSettings.SignSealImage = _seal.Text;
        AppSettings.SignLocation = _location.Text.Trim();
        AppSettings.SignTsaUrl = _tsa.Text.Trim();
        AppSettings.SignEmbedRevocation = _revocation.IsChecked == true;
        if (!cert.FromFile) AppSettings.SignThumbprint = cert.Thumbprint;

        var request = new DigitalSignRequest(_path, save.FileName, cert, _picker.CurrentPage,
            Math.Min(a.U1, a.U2), Math.Min(a.V1, a.V2), Math.Max(a.U1, a.U2), Math.Max(a.V1, a.V2),
            _org.Text.Trim(), string.IsNullOrWhiteSpace(_seal.Text) ? null : _seal.Text, _reason.Text.Trim(), _location.Text.Trim(),
            string.IsNullOrWhiteSpace(_tsa.Text) ? null : _tsa.Text.Trim(), _revocation.IsChecked == true);
        Applied?.Invoke(request);
    }
}

/// <summary>Hộp nhập mật khẩu file .pfx.</summary>
internal sealed class PfxPasswordPrompt : XTWindow
{
    private readonly PasswordBox _box = new() { Height = 30 };
    private string? _value;

    public PfxPasswordPrompt(Window owner)
    {
        Owner = owner; Title = "Mật khẩu chứng thư"; TitleBarMode = TitleBarMode.Tool;
        Width = 380; Height = 190; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        Background = (Brush)Application.Current.FindResource("Ui.Bg");
        var ok = new XTButton { Text = "OK", Width = 84, Height = 32, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => { _value = _box.Password; DialogResult = true; };
        var cancel = new XTButton { Text = "Hủy", Width = 84, Height = 32, IsCancel = true };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        row.Children.Add(ok); row.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Mật khẩu của file chứng thư", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(_box); panel.Children.Add(row);
        Content = panel;
        Loaded += (_, _) => _box.Focus();
    }

    public string? Ask() => ShowDialog() == true ? _value : null;
}
