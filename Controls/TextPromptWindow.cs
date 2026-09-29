using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>Hộp thoại nhỏ hỏi một dòng chữ (vd tên View layer). <paramref name="validate"/> trả thông báo lỗi hoặc null nếu hợp lệ.</summary>
internal sealed class TextPromptWindow : XTWindow
{
    private readonly TextBox _box;
    private readonly TextBlock _error;
    private readonly Func<string, string?>? _validate;

    public string? Value { get; private set; }

    public TextPromptWindow(string title, string prompt, string initial, Func<string, string?>? validate)
    {
        _validate = validate;
        Title = title;
        TitleBarMode = TitleBarMode.Dialog;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;

        _box = new TextBox { Text = initial, Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        _box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
        };
        _error = new TextBlock { Foreground = System.Windows.Media.Brushes.IndianRed, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };

        var ok = new XTButton { Text = "OK", Width = 84, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) => Accept();
        var cancel = new XTButton { Text = "Cancel", Width = 84, Height = 32, IsCancel = true };
        cancel.Click += (_, _) => DialogResult = false;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_box);
        panel.Children.Add(_error);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    private void Accept()
    {
        string text = _box.Text.Trim();
        string? problem = text.Length == 0 ? "Enter a name." : _validate?.Invoke(text);
        if (problem != null)
        {
            _error.Text = problem;
            _error.Visibility = Visibility.Visible;
            _box.Focus();
            _box.SelectAll();
            return;
        }
        Value = text;
        DialogResult = true;
    }

    /// <summary>Hiện hộp thoại; null nếu bấm Cancel.</summary>
    public static string? Ask(Window? owner, string title, string prompt, string initial = "", Func<string, string?>? validate = null)
    {
        var window = new TextPromptWindow(title, prompt, initial, validate) { Owner = owner };
        return window.ShowDialog() == true ? window.Value : null;
    }
}
