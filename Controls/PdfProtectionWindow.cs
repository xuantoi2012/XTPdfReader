using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XTStyle.Controls;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp.Controls;

/// <summary>Tool dialog đổi PDF standard encryption. Password chỉ sống trong control cho tới khi Apply/cancel.</summary>
internal sealed class PdfProtectionWindow : XTWindow
{
    private readonly PdfSecurityInfo _current;
    private readonly PasswordBox _currentOwner = new() { Height = 30 };
    private readonly PasswordBox _user = new() { Height = 30 };
    private readonly PasswordBox _userConfirm = new() { Height = 30 };
    private readonly PasswordBox _owner = new() { Height = 30 };
    private readonly PasswordBox _ownerConfirm = new() { Height = 30 };
    private readonly CheckBox _print = new() { Content = "Allow printing", IsChecked = true };
    private readonly CheckBox _copy = new() { Content = "Allow copying text and graphics", IsChecked = true };
    private readonly CheckBox _modify = new() { Content = "Allow modifying pages", IsChecked = false };
    private readonly CheckBox _annotate = new() { Content = "Allow adding and editing comments", IsChecked = true };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };

    internal PdfProtectionOptions? Result { get; private set; }

    private PdfProtectionWindow(PdfSecurityInfo current)
    {
        _current = current;
        Title = "Protect PDF";
        TitleBarMode = TitleBarMode.Tool;
        Width = 500;
        Height = 630;
        MinHeight = 460;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        Background = (Brush)Application.Current.FindResource("Ui.Bg");

        var content = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        content.Children.Add(new TextBlock { Text = current.IsEncrypted ? "Change protection" : "Protect this PDF", FontSize = 17, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock
        {
            Text = "Uses AES-256 encryption. Changing protection rewrites the PDF, so existing digital signatures will no longer validate.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 7, 0, 0)
        });

        if (current.IsEncrypted)
        {
            content.Children.Add(Label("Current owner password", 16));
            content.Children.Add(_currentOwner);
            content.Children.Add(new TextBlock { Text = "Required to change or remove the current restrictions.", FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0) });
        }

        content.Children.Add(Label("Open password (optional)", 16));
        content.Children.Add(_user);
        content.Children.Add(new TextBlock { Text = "Anyone opening the PDF must enter it. Leave blank to use restrictions without an open password.", FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(Label("Confirm open password", 8));
        content.Children.Add(_userConfirm);
        content.Children.Add(Label("Owner password", 16));
        content.Children.Add(_owner);
        content.Children.Add(new TextBlock { Text = "Keep this password. It is required to change permissions or remove protection later.", FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(Label("Confirm owner password", 8));
        content.Children.Add(_ownerConfirm);

        content.Children.Add(new TextBlock { Text = "Permissions for people opening with the open password", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) });
        foreach (var check in new[] { _print, _copy, _modify, _annotate })
        {
            check.Margin = new Thickness(0, 3, 0, 0);
            content.Children.Add(check);
        }
        content.Children.Add(_error);

        var apply = new XTButton { Text = "Apply protection", Width = 126, Height = 32, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        apply.Click += (_, _) => Apply();
        var remove = new XTButton { Text = "Remove protection", Width = 132, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsEnabled = current.IsEncrypted };
        remove.Click += (_, _) => Remove();
        var cancel = new XTButton { Text = "Cancel", Width = 84, Height = 32, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(apply); buttons.Children.Add(remove); buttons.Children.Add(cancel);
        content.Children.Add(buttons);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content };
        Content = scroll;
        Loaded += (_, _) => (current.IsEncrypted ? _currentOwner : _user).Focus();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { Apply(); e.Handled = true; } };
    }

    private static TextBlock Label(string text, double top) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 5) };

    private void Apply()
    {
        if (_user.Password != _userConfirm.Password) { Error("The open passwords do not match."); return; }
        if (string.IsNullOrWhiteSpace(_owner.Password)) { Error("Enter an owner password."); return; }
        if (_owner.Password != _ownerConfirm.Password) { Error("The owner passwords do not match."); return; }
        Result = new PdfProtectionOptions(_current.IsEncrypted ? _currentOwner.Password : null, _user.Password, _owner.Password,
            _print.IsChecked == true, _copy.IsChecked == true, _modify.IsChecked == true, _annotate.IsChecked == true);
        DialogResult = true;
    }

    private void Remove()
    {
        if (string.IsNullOrWhiteSpace(_currentOwner.Password)) { Error("Enter the current owner password."); return; }
        Result = new PdfProtectionOptions(_currentOwner.Password, "", "", false, false, false, false, RemoveProtection: true);
        DialogResult = true;
    }

    private void Error(string text) { _error.Text = text; _error.Visibility = Visibility.Visible; }

    internal static PdfProtectionOptions? Ask(Window owner, PdfSecurityInfo current)
    {
        var window = new PdfProtectionWindow(current) { Owner = owner };
        return window.ShowDialog() == true ? window.Result : null;
    }
}
