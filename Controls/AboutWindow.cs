using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>About box: name, version, what the app is for and the components it is built on. Opened from the info icon in the title bar.</summary>
internal sealed class AboutWindow : XTWindow
{
    private static readonly (string Name, string Use)[] Components =
    [
        ("MuPDF / PyMuPDF", "page rendering, text search, layers (AGPL-3.0, internal use)"),
        ("iText 9", "page editing, merging, bookmarks (AGPL-3.0, internal use)"),
        ("Velopack", "installation and updates"),
        ("VirtualizingWrapPanel", "thumbnail grids (MIT)")
    ];

    internal static string VersionText()
    {
        var assembly = typeof(AboutWindow).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string version = (informational ?? assembly.GetName().Version?.ToString() ?? "1.0.0").Split('+')[0];
        return $"Version {version}";
    }

    public AboutWindow()
    {
        Title = "About PDF Reader Pro";
        TitleBarMode = TitleBarMode.Tool;
        Width = 520;
        Height = 470;
        MinWidth = 420;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        Background = (Brush)Application.Current.FindResource("Ui.Bg");

        var logo = new Viewbox { Width = 56, Height = 56, Child = new Canvas
        {
            Width = 24, Height = 24,
            Children = { new System.Windows.Shapes.Path { Data = (Geometry)Application.Current.FindResource("App.Icon.Logo"), Fill = (Brush)Application.Current.FindResource("Ui.Accent") } }
        } };
        TextBlock Text(string text, double size, FontWeight weight, string brush, Thickness? margin = null)
            => new() { Text = text, FontSize = size, FontWeight = weight, Foreground = (Brush)Application.Current.FindResource(brush), TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) };

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(28, 26, 28, 12) };
        head.Children.Add(logo);
        var titles = new StackPanel { Margin = new Thickness(16, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Text("PDF Reader Pro", 22, FontWeights.SemiBold, "Ui.Text"));
        titles.Children.Add(Text(VersionText(), 12.5, FontWeights.Normal, "Ui.Muted", new Thickness(0, 2, 0, 0)));
        head.Children.Add(titles);

        var body = new StackPanel { Margin = new Thickness(28, 0, 28, 0) };
        body.Children.Add(Text("A PDF reader built for drawing sets: fast viewing of large CAD sheets, layer control, merging printed and plotted PDFs, " +
            "comments and team-safe saving.", 13, FontWeights.Normal, "Ui.Text"));
        body.Children.Add(Text("Built on", 12, FontWeights.SemiBold, "Ui.Muted", new Thickness(0, 18, 0, 6)));
        foreach (var (name, use) in Components)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Width = 150, Foreground = (Brush)Application.Current.FindResource("Ui.Text") });
            row.Children.Add(new TextBlock { Text = use, Foreground = (Brush)Application.Current.FindResource("Ui.Muted"), TextWrapping = TextWrapping.Wrap, MaxWidth = 300 });
            body.Children.Add(row);
        }
        body.Children.Add(Text("Internal use. The AGPL components above must be reviewed again before the app is given to anyone outside the company.",
            11.5, FontWeights.Normal, "Ui.Muted", new Thickness(0, 16, 0, 0)));

        var copy = new XTButton { Text = "Copy version info", Height = 32, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText($"PDF Reader Pro, {VersionText()}, {Environment.OSVersion}, .NET {Environment.Version}"); XTGrowl.Success("Version info copied", this); }
            catch { /* clipboard busy */ }
        };
        var close = new XTButton { Text = "Close", Width = 84, Height = 32, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(28, 14, 28, 20) };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(buttons);
        root.Children.Add(head);
        root.Children.Add(body);
        Content = root;
    }

    internal static void ShowFor(Window owner) => new AboutWindow { Owner = owner }.ShowDialog();
}
