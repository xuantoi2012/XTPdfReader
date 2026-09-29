using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>Chọn khổ trang trước khi khởi tạo PDF trắng; chưa đụng tới đường dẫn hay hộp Save As.</summary>
internal sealed class NewBlankPdfWindow : XTWindow
{
    internal readonly record struct PaperSize(string Label, double WidthPoints, double HeightPoints);

    private static readonly PaperSize[] Sizes =
    [
        new("A4 (210 × 297 mm)", 595.276, 841.890),
        new("A3 (297 × 420 mm)", 841.890, 1190.551),
        new("A5 (148 × 210 mm)", 419.528, 595.276),
        new("Letter (8.5 × 11 in)", 612, 792),
        new("Legal (8.5 × 14 in)", 612, 1008)
    ];

    private readonly ComboBox _sizeBox;
    private readonly ComboBox _orientationBox;
    internal PaperSize? SelectedSize { get; private set; }

    private NewBlankPdfWindow()
    {
        Title = "New blank PDF";
        TitleBarMode = TitleBarMode.Dialog;
        Width = 410;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;

        _sizeBox = new ComboBox { Height = 30, DisplayMemberPath = nameof(PaperSize.Label), ItemsSource = Sizes, SelectedIndex = 0 };
        _orientationBox = new ComboBox { Height = 30, Margin = new Thickness(0, 6, 0, 0), SelectedIndex = 0 };
        _orientationBox.Items.Add("Portrait");
        _orientationBox.Items.Add("Landscape");
        _orientationBox.PreviewKeyDown += OnKeyDown;

        var create = new XTButton { Text = "Create", Width = 88, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        create.Click += (_, _) => Accept();
        var cancel = new XTButton { Text = "Cancel", Width = 88, Height = 32, IsCancel = true };
        cancel.Click += (_, _) => DialogResult = false;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(create);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 22) };
        panel.Children.Add(new TextBlock { Text = "Page size", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(_sizeBox);
        panel.Children.Add(new TextBlock { Text = "Orientation", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) });
        panel.Children.Add(_orientationBox);
        panel.Children.Add(new TextBlock
        {
            Text = "The new document opens as Untitled. You choose its folder and name only when saving.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DimGray,
            FontSize = 12,
            Margin = new Thickness(0, 12, 0, 0)
        });
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => _sizeBox.Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Accept();
        e.Handled = true;
    }

    private void Accept()
    {
        if (_sizeBox.SelectedItem is not PaperSize selected) return;
        SelectedSize = _orientationBox.SelectedIndex == 1
            ? new PaperSize(selected.Label, selected.HeightPoints, selected.WidthPoints)
            : selected;
        DialogResult = true;
    }

    internal static PaperSize? Ask(Window owner)
    {
        var window = new NewBlankPdfWindow { Owner = owner };
        return window.ShowDialog() == true ? window.SelectedSize : null;
    }
}
