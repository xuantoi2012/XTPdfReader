using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XTPdfMergeApp.Licensing;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// Sign in / create an account / see the trial or license / manage the PCs of the account. Opened at start-up when the app may not run yet
/// (the gate: closing it without a usable license ends the app) and from the About box.
/// </summary>
internal sealed class LicenseWindow : XTWindow
{
    private readonly bool _gate;
    private bool _creating;
    private readonly TextBlock _heading = new() { FontSize = 18, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Foreground = Brushes.DimGray };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBox _email = new() { Height = 30 };
    private readonly PasswordBox _password = new() { Height = 30 };
    private readonly TextBox _name = new() { Height = 30 };
    private readonly StackPanel _form = new();
    private readonly StackPanel _nameRow = new();
    private readonly StackPanel _devices = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly XTButton _primary = new() { Height = 32, Width = 150, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
    private readonly XTButton _switch = new() { Height = 32, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 0) };
    private readonly XTButton _signOut = new() { Text = "Sign out", Height = 32, Width = 96, Margin = new Thickness(0, 0, 8, 0) };
    private readonly XTButton _close = new() { Height = 32, Width = 96, IsCancel = true };

    private LicenseWindow(bool gate)
    {
        _gate = gate;
        Title = "PDF Reader Pro - License";
        TitleBarMode = TitleBarMode.Tool;
        Width = 480;
        Height = 560;
        MinHeight = 420;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = gate ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = gate;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        Background = (Brush)Application.Current.FindResource("Ui.Bg");

        _nameRow.Children.Add(Label("Your name"));
        _nameRow.Children.Add(_name);
        _form.Children.Add(Label("E-mail"));
        _form.Children.Add(_email);
        _form.Children.Add(_nameRow);
        _form.Children.Add(Label("Password"));
        _form.Children.Add(_password);

        var content = new StackPanel { Margin = new Thickness(24, 20, 24, 12) };
        content.Children.Add(_heading);
        content.Children.Add(_status);
        content.Children.Add(_form);
        content.Children.Add(_message);
        content.Children.Add(_devices);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 8, 24, 18) };
        buttons.Children.Add(_primary);
        buttons.Children.Add(_switch);
        buttons.Children.Add(_signOut);
        buttons.Children.Add(_close);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content });
        Content = root;

        _primary.Click += async (_, _) => await SubmitAsync();
        _switch.Click += (_, _) => { _creating = !_creating; _message.Visibility = Visibility.Collapsed; Refresh(); };
        _signOut.Click += (_, _) => { LicenseManager.SignOut(); _devices.Children.Clear(); Refresh(); };
        Refresh();
    }

    /// <summary>Runs the start-up gate. True when the app may start (licensed, in trial, or licensing off); false when the user gave up.</summary>
    internal static bool Gate()
    {
        if (LicenseManager.Current.AllowsUse) return true;
        new LicenseWindow(gate: true).ShowDialog();
        return LicenseManager.Current.AllowsUse;
    }

    internal static void ShowFor(Window owner) => new LicenseWindow(gate: false) { Owner = owner }.ShowDialog();

    private void Refresh()
    {
        var state = LicenseManager.Current;
        bool signedIn = state.Status is LicenseStatus.Active or LicenseStatus.Expired or LicenseStatus.NeedsOnline && state.Payload is not null;
        _form.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        _nameRow.Visibility = _creating ? Visibility.Visible : Visibility.Collapsed;
        _signOut.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        _switch.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        _primary.Visibility = signedIn && state.Status == LicenseStatus.Active ? Visibility.Collapsed : Visibility.Visible;
        _primary.Text = signedIn ? "Check again" : _creating ? "Create account" : "Sign in";
        _switch.Text = _creating ? "I have an account" : "Create account";
        _close.Text = _gate && !state.AllowsUse ? "Exit" : "Close";
        _heading.Text = signedIn ? "License" : _creating ? "Create your account" : "Sign in to PDF Reader Pro";
        _status.Text = signedIn
            ? $"{LicenseManager.Email}\n{state.Describe()}"
            : "A new account starts a free 15-day trial on up to 2 PCs. When it ends the app locks until a license is added to the account.";
        if (signedIn) _ = ShowDevicesAsync();
    }

    private async Task SubmitAsync()
    {
        _primary.IsEnabled = false;
        try
        {
            var state = LicenseManager.Current;
            if (state.Payload is not null && state.Status != LicenseStatus.SignedOut)
                await LicenseManager.RefreshAsync();
            else if (_creating)
            {
                if (_password.Password.Length < 8) { Show("Choose a password of at least 8 characters.", error: true); return; }
                Show(await LicenseManager.SignUpAsync(_email.Text, _password.Password, _name.Text), error: false);
                _creating = false;
                Refresh();
                return;
            }
            else
                await LicenseManager.SignInAsync(_email.Text, _password.Password);

            _password.Clear();
            _message.Visibility = Visibility.Collapsed;
            Refresh();
            if (_gate && LicenseManager.Current.AllowsUse) DialogResult = true;
        }
        catch (LicenseException ex)
        {
            Show(ex.Message, error: true);
            if (ex.Code == "DEVICE_LIMIT") ShowDeviceList(ex.Devices);
        }
        finally { _primary.IsEnabled = true; }
    }

    private async Task ShowDevicesAsync()
    {
        try { ShowDeviceList(await LicenseManager.DevicesAsync()); }
        catch (LicenseException) { _devices.Children.Clear(); }
    }

    private void ShowDeviceList(IReadOnlyList<LicenseDevice> devices)
    {
        _devices.Children.Clear();
        if (devices.Count == 0) return;
        _devices.Children.Add(new TextBlock { Text = $"PCs on this account (max {LicenseConfig.MaxDevices})", FontWeight = FontWeights.SemiBold });
        foreach (var device in devices)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var remove = new XTButton { Text = device.IsThisPc ? "This PC" : "Remove", Height = 28, Width = 84, IsEnabled = !device.IsThisPc };
            DockPanel.SetDock(remove, Dock.Right);
            remove.Click += async (_, _) => await RemoveAsync(device);
            row.Children.Add(remove);
            row.Children.Add(new TextBlock
            {
                Text = device.LastSeen == DateTimeOffset.MinValue ? device.Name : $"{device.Name}  -  last used {device.LastSeen.LocalDateTime:dd/MM/yyyy}",
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
            });
            _devices.Children.Add(row);
        }
    }

    private async Task RemoveAsync(LicenseDevice device)
    {
        if (MessageBox.Show(this, $"Remove \"{device.Name}\" from this account? That PC will need to sign in again.", "Remove PC", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            await LicenseManager.RemoveDeviceAsync(device.Id);
            // A sign-in that stopped at the limit can go on now.
            if (LicenseManager.Current.Payload is null) await LicenseManager.ClaimAgainAsync();
            _message.Visibility = Visibility.Collapsed;
            Refresh();
            if (LicenseManager.Current.Payload is null) ShowDeviceList(await LicenseManager.DevicesAsync());
            if (_gate && LicenseManager.Current.AllowsUse) DialogResult = true;
        }
        catch (LicenseException ex) { Show(ex.Message, error: true); }
    }

    private void Show(string text, bool error)
    {
        _message.Text = text;
        _message.Foreground = error ? Brushes.IndianRed : Brushes.SeaGreen;
        _message.Visibility = Visibility.Visible;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) };
}
