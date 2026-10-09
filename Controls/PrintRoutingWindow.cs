using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>One thing to print: the pages of one kind of sheet, the printer, the paper and the driver's own settings kept for that size.</summary>
internal sealed record RoutedPrintJob(PageSizeGroup Group, string Printer, PaperSize Paper, byte[]? DevMode = null, int Copies = 1, bool? Collate = null);

/// <summary>
/// "Print by paper size" in a window of its own. The print dialog now carries the same thing inside it (<see cref="PrintBySizePanel"/>, beside the page range, so only the sizes of the pages that
/// print are listed); this window hosts that panel for the checks and for anything that wants the routing alone.
/// </summary>
internal sealed class PrintRoutingWindow : XTWindow
{
    private readonly PrintBySizePanel _panel;
    private readonly XTButton _print;

    public IReadOnlyList<RoutedPrintJob> Jobs { get; private set; } = Array.Empty<RoutedPrintJob>();

    internal IReadOnlyList<(string Class, ComboBox Printer, ComboBox Paper, CheckBox Skip, RouteResult Result)> RowsForTest => _panel.RowsForTest;
    internal ComboBox ProfileBox => _panel.ProfileBox;
    internal string SummaryText => _panel.SummaryText;

    public PrintRoutingWindow(IReadOnlyList<PageSizeGroup> groups, string defaultPrinter, string? profile = null)
    {
        Title = "Print by paper size";
        TitleBarMode = TitleBarMode.Dialog;
        Width = 560;
        Height = Math.Min(800, 250 + groups.Count * 150);
        MinHeight = 420;
        MinWidth = 480;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        var root = new DockPanel { Background = R("Ui.Surface") };

        _print = AreaKit.Primary("Print", 110);
        _print.IsDefault = true;
        _print.Click += (_, _) => Accept();
        var cancel = AreaKit.Ghost("Cancel", minWidth: 100);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(8, 0, 0, 0);
        cancel.Click += (_, _) => DialogResult = false;
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(20, 12, 20, 12), Child = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _print, cancel } } };
        footer.SetResourceReference(Border.BorderBrushProperty, "Ui.Border");
        footer.SetResourceReference(Border.BackgroundProperty, "Ui.Panel");
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var head = new StackPanel { Margin = new Thickness(20, 14, 20, 8) };
        head.Children.Add(AreaKit.Muted(this, "Choose the printer for each kind of sheet. Save the choice as a profile and the next set prints with one click. A sheet the printer has no paper for, or a special size, is marked: force it onto a paper, print it on a custom paper as big as the pages, or leave it out."));
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        _panel = new PrintBySizePanel(defaultPrinter);
        _panel.ProfileBar.Margin = new Thickness(0, 10, 0, 0);
        head.Children.Add(_panel.ProfileBar);
        _panel.SetGroups(groups);
        root.Children.Add(new ScrollViewer { Content = new Border { Padding = new Thickness(20, 0, 20, 12), Child = _panel }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    internal void SaveProfileForTest(string name) => _panel.SaveProfileForTest(name);

    private void Accept()
    {
        var jobs = _panel.TryBuildJobs(this);
        if (jobs == null) return;
        Jobs = jobs;
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
    }

    internal void AcceptForTest() => Accept();
}
