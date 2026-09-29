using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>Hộp thoại nhỏ hỏi một số nguyên trong khoảng [min, max] (vd "Move to position…").</summary>
internal sealed class NumberPromptWindow : XTWindow
{
    private readonly TextBox _box;
    private readonly int _min, _max;

    public int? Value { get; private set; }

    public NumberPromptWindow(string title, string prompt, int min, int max, int initial)
    {
        _min = min;
        _max = max;
        Title = title;
        TitleBarMode = TitleBarMode.Dialog;
        Width = 340;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;

        _box = new TextBox { Text = initial.ToString(), Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        _box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
        };

        var ok = new XTButton { Text = "OK", Width = 84, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) => Accept();
        var cancel = new XTButton { Text = "Cancel", Width = 84, Height = 32, IsCancel = true };
        cancel.Click += (_, _) => DialogResult = false;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        panel.Children.Add(new TextBlock { Text = $"{prompt} ({min}–{max})", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    private void Accept()
    {
        if (!int.TryParse(_box.Text.Trim(), out int v) || v < _min || v > _max)
        {
            _box.BorderBrush = System.Windows.Media.Brushes.IndianRed;
            _box.Focus();
            _box.SelectAll();
            return;
        }
        Value = v;
        DialogResult = true;
    }

    /// <summary>Hiện hộp thoại; null nếu bấm Cancel.</summary>
    public static int? Ask(Window? owner, string title, string prompt, int min, int max, int initial)
    {
        var window = new NumberPromptWindow(title, prompt, min, max, initial) { Owner = owner };
        return window.ShowDialog() == true ? window.Value : null;
    }
}
