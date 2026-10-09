using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>The details of one digital signature, opened by a click on its field (like Foxit, the signature validation status).</summary>
internal sealed class SignatureInfoWindow : XTWindow
{
    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    private SignatureInfoWindow(SignatureCheck c, int signatureCount)
    {
        Title = "Chữ ký số";
        TitleBarMode = TitleBarMode.Tool;
        Width = 480; SizeToContent = SizeToContent.Height; MaxHeight = 720;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        Background = Res("Ui.Bg");

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        Brush tone = c.IntegrityOk ? (c.ChainTrusted && c.CoversWholeFile ? Brushes.SeaGreen : Brushes.DarkOrange) : Brushes.IndianRed;
        string headline = !c.IntegrityOk ? "Chữ ký KHÔNG hợp lệ: văn bản đã bị sửa sau khi ký"
            : !c.CoversWholeFile ? "Chữ ký hợp lệ; sau khi ký văn bản có thêm nội dung (ví dụ chữ ký khác)"
            : c.ChainTrusted ? "Chữ ký hợp lệ, văn bản chưa bị sửa"
            : "Văn bản chưa bị sửa, nhưng chứng thư chưa được máy này tin cậy";
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = tone, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        head.Children.Add(new TextBlock { Text = headline, FontWeight = FontWeights.SemiBold, FontSize = 14.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 390 });
        root.Children.Add(head);

        var grid = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        int row = 0;
        void Add(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Foreground = Res("Ui.Muted"), Margin = new Thickness(0, 0, 8, 7), TextWrapping = TextWrapping.Wrap };
            var v = new TextBlock { Text = value, Margin = new Thickness(0, 0, 0, 7), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(l, row); Grid.SetRow(v, row); Grid.SetColumn(v, 1);
            grid.Children.Add(l); grid.Children.Add(v);
            row++;
        }
        Add("Người ký", c.Signer);
        Add("Cơ quan, tổ chức", c.Organization);
        Add("Thời gian ký", c.SignedAt?.ToString("dd/MM/yyyy HH:mm:ss"));
        Add("Dấu thời gian", c.HasTimestamp ? "Có (từ máy chủ dấu thời gian)" : "Không (thời gian lấy từ máy ký)");
        Add("Lý do", c.Reason);
        Add("Nơi ký", c.Location);
        Add("Nhà cung cấp chứng thư", c.Issuer);
        Add("Hiệu lực chứng thư", c.ValidFrom is { } from && c.ValidTo is { } to ? $"{from:dd/MM/yyyy} → {to:dd/MM/yyyy}" : "");
        Add("Thuật toán", c.Algorithm);
        Add("Thứ tự chữ ký", c.Revisions > 0 ? $"{c.Revision} / {signatureCount}" : "");
        Add("Tên trường ký", c.Field);
        Add("Ghi chú", c.Note);
        root.Children.Add(grid);

        var close = new XTButton { Text = "Đóng", Width = 90, Height = 32, IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        close.Click += (_, _) => Close();
        root.Children.Add(close);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
    }

    internal static void ShowFor(Window owner, SignatureCheck check, int count)
        => new SignatureInfoWindow(check, count) { Owner = owner }.ShowDialog();
}
