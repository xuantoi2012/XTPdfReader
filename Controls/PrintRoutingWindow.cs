using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>Việc in đã chọn cho 1 nhóm khổ: máy in + khổ giấy sẽ dùng (tìm theo khổ trang) — hoặc bỏ qua nhóm này.</summary>
internal sealed record RoutedPrintJob(PageSizeGroup Group, string Printer, PaperSize Paper);

/// <summary>
/// "Print by size": mỗi khổ giấy của file in trên máy in riêng (A1/A0 sang plotter, A3/A4 sang máy laser…), mỗi nhóm tự lấy đúng khổ giấy của máy in đó
/// (đúng khổ → khổ lớn gần nhất → khổ lớn nhất, thu nhỏ). Một nhóm có thể bỏ qua.
/// </summary>
internal sealed class PrintRoutingWindow : XTWindow
{
    private const string Skip = "(do not print)";

    private sealed class Row
    {
        public required PageSizeGroup Group;
        public required ComboBox Printers;
        public required TextBlock Status;
    }

    private readonly List<Row> _rows = new();
    private readonly IReadOnlyList<string> _installed;

    public IReadOnlyList<RoutedPrintJob> Jobs { get; private set; } = Array.Empty<RoutedPrintJob>();

    public PrintRoutingWindow(IReadOnlyList<PageSizeGroup> groups, string defaultPrinter)
    {
        _installed = PrinterSettings.InstalledPrinters.Cast<string>().ToList();

        Title = "Print by paper size";
        TitleBarMode = TitleBarMode.Dialog;
        Width = 760;
        Height = Math.Min(760, 200 + groups.Count * 118);
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        var root = new DockPanel { Background = R("Ui.Surface") };

        var ok = new XTButton { Text = "Print", Width = 100, Height = 34, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.SetResourceReference(StyleProperty, "UiPrimaryButton");
        ok.Click += (_, _) => Accept();
        var cancel = new XTButton { Text = "Cancel", Width = 100, Height = 34, IsCancel = true };
        cancel.SetResourceReference(StyleProperty, "UiGhostButton");
        cancel.Click += (_, _) => DialogResult = false;
        var footer = new Border
        {
            BorderBrush = R("Ui.Border"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(20, 12, 20, 12), Background = R("Ui.Panel"),
            Child = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var head = new TextBlock
        {
            Text = "Choose the printer for each paper size. Every group is printed on that printer's paper that matches the pages (or the closest larger one).",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 16, 20, 10), FontSize = 12, Foreground = R("Ui.Muted")
        };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var stack = new StackPanel { Margin = new Thickness(20, 0, 20, 12) };
        foreach (var group in groups)
        {
            var combo = new ComboBox { Height = 28, Width = 300, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            combo.Items.Add(Skip);
            foreach (string printer in _installed) combo.Items.Add(printer);
            combo.SelectedItem = _installed.Contains(defaultPrinter) ? defaultPrinter : _installed.FirstOrDefault() ?? Skip;
            var status = new TextBlock { FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            var row = new Row { Group = group, Printers = combo, Status = status };
            combo.SelectionChanged += (_, _) => Refresh(row);
            _rows.Add(row);

            var card = new Border
            {
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 10), Margin = new Thickness(0, 0, 0, 8),
                BorderBrush = R("Ui.Border"), Background = R("Ui.Surface"),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = $"{PrintSizePlan.Label(group)}   ({group.Dims})", FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = "pages " + PrintSizePlan.PageList(group.PageIndexes), FontSize = 11.5, Opacity = 0.7 },
                        combo, status
                    }
                }
            };
            stack.Children.Add(card);
            Refresh(row);
        }
        root.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    /// <summary>Khổ giấy của máy in <paramref name="printer"/> cho nhóm này và kết quả khớp.</summary>
    internal static (PaperSize? Paper, SizeFit Fit, double Shrink) PaperFor(PageSizeGroup group, string printer)
    {
        var settings = new PrinterSettings { PrinterName = printer };
        if (!settings.IsValid) return (null, SizeFit.TooBig, 0);
        var papers = settings.PaperSizes.Cast<PaperSize>().ToList();
        var options = papers.Select(p => new PaperOption(p.PaperName, p.Width / 100.0 * 25.4, p.Height / 100.0 * 25.4)).ToList();
        var (match, fit, shrink) = PrintSizePlan.Match(Math.Min(group.WidthMm, group.HeightMm), Math.Max(group.WidthMm, group.HeightMm), options);
        return (papers.FirstOrDefault(p => p.PaperName == match?.Name), fit, shrink);
    }

    private void Refresh(Row row)
    {
        string? printer = row.Printers.SelectedItem as string;
        if (printer == null || printer == Skip) { row.Status.Text = "Not printed."; row.Status.Foreground = Brushes.Gray; return; }
        var (paper, fit, shrink) = PaperFor(row.Group, printer);
        if (paper == null) { row.Status.Text = "This printer lists no usable paper."; row.Status.Foreground = Brushes.IndianRed; return; }
        row.Status.Text = fit switch
        {
            SizeFit.Exact => $"✓ prints on {paper.PaperName}",
            SizeFit.LargerPaper => $"⚠ no exact paper — prints on the larger {paper.PaperName}",
            _ => $"⚠ larger than every paper — shrinks to {shrink * 100:0}% on {paper.PaperName}"
        };
        row.Status.Foreground = fit == SizeFit.Exact ? Brushes.SeaGreen : Brushes.DarkOrange;
    }

    private void Accept()
    {
        var jobs = new List<RoutedPrintJob>();
        foreach (var row in _rows)
        {
            if (row.Printers.SelectedItem is not string printer || printer == Skip) continue;
            var (paper, _, _) = PaperFor(row.Group, printer);
            if (paper != null) jobs.Add(new RoutedPrintJob(row.Group, printer, paper));
        }
        if (jobs.Count == 0) { AppDialog.Show(this, "Choose a printer for at least one paper size.", "Print by size", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Jobs = jobs;
        DialogResult = true;
    }
}
