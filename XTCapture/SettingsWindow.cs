using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XTStyle.Controls;

namespace XTCapture
{
    /// <summary>XT Capture's settings: start with Windows, the corner button, the shortcut and what fills the area outside a circle / polygon.</summary>
    internal sealed class SettingsWindow : XTWindow
    {
        private static SettingsWindow? _current;
        internal static bool Offscreen { get; set; }

        private readonly TrayHost _host;
        private readonly CheckBox _autostart = new() { Content = "Start XT Capture with Windows", Margin = new Thickness(0, 0, 0, 10) };
        private readonly CheckBox _floating = new() { Content = "Show the capture button in the bottom right corner of the screen", Margin = new Thickness(0, 0, 0, 10) };
        private readonly CheckBox _transparent = new() { Content = "Circle and polygon captures: clear (transparent) outside the shape, not white", Margin = new Thickness(0, 0, 0, 10) };
        private readonly ComboBox _hotkey = new() { Width = 200, HorizontalAlignment = HorizontalAlignment.Left, Height = 28 };
        private readonly TextBlock _hotkeyStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        private bool _loading;

        internal CheckBox AutostartBox => _autostart;
        internal CheckBox FloatingBox => _floating;
        internal ComboBox HotkeyBox => _hotkey;
        internal string HotkeyMessage => _hotkeyStatus.Text;

        internal static SettingsWindow Open(TrayHost host)
        {
            if (_current == null)
            {
                _current = new SettingsWindow(host);
                if (Offscreen) { _current.WindowStartupLocation = WindowStartupLocation.Manual; _current.Left = _current.Top = -32000; }
            }
            _current.Show();
            _current.Activate();
            return _current;
        }

        private SettingsWindow(TrayHost host)
        {
            _host = host;
            Title = "XT Capture: Settings";
            TitleBarMode = TitleBarMode.Dialog;
            Width = 520;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            Closed += (_, _) => { if (ReferenceEquals(_current, this)) _current = null; };

            Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
            _hotkeyStatus.Foreground = R("Ui.Muted");
            foreach (string preset in CaptureHotkey.Presets) _hotkey.Items.Add(preset);

            var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 20) };
            panel.Children.Add(_autostart);
            panel.Children.Add(_floating);
            panel.Children.Add(new TextBlock { Text = "Capture shortcut", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 6), Foreground = R("Ui.Text") });
            panel.Children.Add(_hotkey);
            panel.Children.Add(_hotkeyStatus);
            panel.Children.Add(new Border { Height = 1, Background = R("Ui.Border"), Margin = new Thickness(0, 16, 0, 14) });
            panel.Children.Add(_transparent);
            var close = new Button { Content = "Close", Width = 90, Height = 28, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), IsDefault = true };
            close.Click += (_, _) => Close();
            panel.Children.Add(close);
            Content = new Border { Background = R("Ui.Surface"), Child = panel };

            Load();
            _autostart.Click += (_, _) => { if (!_loading) { _host.SetAutostart(_autostart.IsChecked == true); Load(); } };
            _floating.Click += (_, _) => { if (!_loading) _host.SetFloatingButton(_floating.IsChecked == true); };
            _transparent.Click += (_, _) => { if (!_loading) CaptureSettings.TransparentOutside = _transparent.IsChecked == true; };
            _hotkey.SelectionChanged += (_, _) =>
            {
                if (_loading || _hotkey.SelectedItem is not string choice) return;
                _host.SetHotkey(choice);
                Load();
            };
        }

        private void Load()
        {
            _loading = true;
            try
            {
                _autostart.IsChecked = Autostart.IsEnabled;
                _floating.IsChecked = CaptureSettings.ShowFloatingButton;
                _transparent.IsChecked = CaptureSettings.TransparentOutside;
                string current = CaptureHotkey.Parse(CaptureSettings.Hotkey)?.ToString() ?? CaptureHotkey.Presets[0];
                _hotkey.SelectedItem = _hotkey.Items.Cast<string>().FirstOrDefault(p => string.Equals(p, current, StringComparison.OrdinalIgnoreCase));
                _hotkeyStatus.Text = _host.Hotkey.Message;
            }
            finally { _loading = false; }
        }
    }
}
