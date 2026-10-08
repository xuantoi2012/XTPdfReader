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

/// <summary>One thing to print: the pages of one kind of sheet, the printer, the paper and the driver's own settings kept for that size.</summary>
internal sealed record RoutedPrintJob(PageSizeGroup Group, string Printer, PaperSize Paper, byte[]? DevMode = null);

/// <summary>
/// "Print by paper size": every kind of sheet of the file (A1, A3, A4, A3 extended, a special size…) goes to the printer, paper and driver settings that were set for it, so a whole set prints
/// without setting anything by hand. The settings are a named PROFILE ("Office": A1 on the plotter, A3 on the big Canon, A4 on the laser, A3 extended on the roll plotter) that is saved
/// and chosen next time. A size that has no printer, a printer that is not there, a paper that does not match or a special size is marked and has to be looked at before printing;
/// it can be forced onto a paper of the printer, printed on a custom paper as big as the pages, or left out.
/// </summary>
internal sealed class PrintRoutingWindow : XTWindow
{
    private const string AutoPaper = "Match automatically", CustomPaper = "Custom paper as big as the pages", NoPrinter = "(choose a printer)";

    private sealed class Row
    {
        public required PageSizeGroup Group;
        public required Border Card;
        public required CheckBox Skip;
        public required ComboBox Printers;
        public required ComboBox Papers;
        public required XTButton Properties;
        public required TextBlock Status;
        public byte[]? DevMode;
        public RouteResult Result = new(RouteState.NotAssigned, null, 0, "");
    }

    private sealed record PaperItem(string Text, PaperChoice Choice, string Name)
    {
        public override string ToString() => Text;
    }

    private readonly List<Row> _rows = new();
    private readonly ComboBox _profiles = new() { Height = 28, Width = 220, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = new();
    private readonly XTButton _print;
    private bool _loading;
    private string _profileName = "";

    public IReadOnlyList<RoutedPrintJob> Jobs { get; private set; } = Array.Empty<RoutedPrintJob>();

    internal IReadOnlyList<(string Class, ComboBox Printer, ComboBox Paper, CheckBox Skip, RouteResult Result)> RowsForTest
        => _rows.Select(r => (r.Group.Name, r.Printers, r.Papers, r.Skip, r.Result)).ToList();
    internal ComboBox ProfileBox => _profiles;
    internal string SummaryText => _summary.Text;

    public PrintRoutingWindow(IReadOnlyList<PageSizeGroup> groups, string defaultPrinter, string? profile = null)
    {
        Title = "Print by paper size";
        TitleBarMode = TitleBarMode.Dialog;
        Width = 900;
        Height = Math.Min(800, 250 + groups.Count * 132);
        MinHeight = 420;
        MinWidth = 760;
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
        var footer = AreaKit.Footer(this, _summary, _print, cancel);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var head = new StackPanel { Margin = new Thickness(20, 14, 20, 8) };
        head.Children.Add(AreaKit.Muted(this, "Choose the printer for each kind of sheet. Save the choice as a profile and the next set prints with one click. A sheet the printer has no paper for, or a special size, is marked: force it onto a paper, print it on a custom paper as big as the pages, or leave it out."));
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        bar.Children.Add(new TextBlock { Text = "Profile", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        bar.Children.Add(_profiles);
        var save = AreaKit.Ghost("Save", 28);
        var saveAs = AreaKit.Ghost("Save as…", 28);
        var delete = AreaKit.Ghost("Delete", 28);
        foreach (var b in new[] { save, saveAs, delete }) { b.Margin = new Thickness(8, 0, 0, 0); bar.Children.Add(b); }
        head.Children.Add(bar);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var stack = new StackPanel { Margin = new Thickness(20, 0, 20, 12) };
        foreach (var group in groups) stack.Children.Add(BuildRow(group, defaultPrinter).Card);
        root.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        FillProfiles();
        _profiles.SelectionChanged += (_, _) => { if (!_loading) ApplyProfile(_profiles.SelectedItem as string); };
        save.Click += (_, _) => SaveProfile(_profileName);
        saveAs.Click += (_, _) => SaveProfile(null);
        delete.Click += (_, _) =>
        {
            if (_profileName.Length == 0) return;
            PrintProfileStore.Remove(_profileName);
            _profileName = "";
            FillProfiles();
        };

        // the profile used last, else the printer that was chosen in the print dialog for every size
        var chosen = profile != null ? PrintProfileStore.Get(profile) : PrintProfileStore.Last;
        if (chosen != null) { _loading = true; _profileName = chosen.Name; _profiles.SelectedItem = chosen.Name; _loading = false; ApplyProfile(chosen.Name); }
        else RefreshAll();
    }

    // ── rows ─────────────────────────────────────────────────────────

    private Row BuildRow(PageSizeGroup group, string defaultPrinter)
    {
        Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        var installed = PrinterCatalog.Installed;
        var printers = new ComboBox { Height = 28, Width = 270, Margin = new Thickness(0, 6, 8, 0) };
        printers.Items.Add(NoPrinter);
        foreach (string p in installed) printers.Items.Add(p);
        var papers = new ComboBox { Height = 28, Width = 270, Margin = new Thickness(0, 6, 8, 0) };
        var properties = AreaKit.Ghost("Properties…", 28);
        properties.Margin = new Thickness(0, 6, 0, 0);
        properties.ToolTip = "The printer driver's own settings for this size (tray, quality, plotter options…), kept in the profile";
        var skip = new CheckBox { Content = "Do not print", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 6, 0, 0) };
        var status = new TextBlock { FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        var card = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 10), Margin = new Thickness(0, 0, 0, 8),
            BorderBrush = R("Ui.Border"), Background = R("Ui.Surface")
        };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = PrintSizePlan.Label(group), FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock { Text = $"   {group.Dims}", Opacity = 0.7 });
        if (PrintSizePlan.IsSpecial(group.Name)) title.Children.Add(new TextBlock { Text = "   special size", Foreground = Brushes.DarkOrange, FontWeight = FontWeights.SemiBold });
        var line = new StackPanel { Orientation = Orientation.Horizontal, Children = { printers, papers, properties, skip } };
        card.Child = new StackPanel
        {
            Children = { title, new TextBlock { Text = "pages " + PrintSizePlan.PageList(group.PageIndexes), FontSize = 11.5, Opacity = 0.7 }, line, status }
        };
        var row = new Row { Group = group, Card = card, Skip = skip, Printers = printers, Papers = papers, Properties = properties, Status = status };
        _rows.Add(row);

        printers.SelectionChanged += (_, _) => { if (_loading) return; row.DevMode = null; FillPapers(row, null); RefreshRow(row); RefreshSummary(); };
        papers.SelectionChanged += (_, _) => { if (_loading) return; RefreshRow(row); RefreshSummary(); };
        skip.Click += (_, _) => { RefreshRow(row); RefreshSummary(); };
        properties.Click += (_, _) =>
        {
            if (row.Printers.SelectedItem is not string printer || printer == NoPrinter) return;
            var devMode = PrinterDriver.ShowDialog(new System.Windows.Interop.WindowInteropHelper(this).Handle, printer, row.DevMode);
            if (devMode != null) row.DevMode = devMode;
        };
        _loading = true;
        printers.SelectedItem = installed.Contains(defaultPrinter) ? defaultPrinter : NoPrinter;
        FillPapers(row, null);
        _loading = false;
        return row;
    }

    /// <summary>The papers of the row's printer, and which one is chosen.</summary>
    private void FillPapers(Row row, PrintAssignment? assignment)
    {
        bool was = _loading;
        _loading = true;
        row.Papers.Items.Clear();
        row.Papers.Items.Add(new PaperItem(AutoPaper, PaperChoice.Auto, ""));
        row.Papers.Items.Add(new PaperItem(CustomPaper, PaperChoice.CustomPageSize, ""));
        if (row.Printers.SelectedItem is string printer && printer != NoPrinter && PrinterCatalog.PapersOf(printer) is { } papers)
            foreach (var paper in papers)
                row.Papers.Items.Add(new PaperItem($"{paper.PaperName}  ({paper.Width / 100.0 * 25.4:0} × {paper.Height / 100.0 * 25.4:0} mm)", PaperChoice.Named, paper.PaperName));
        PaperItem? selected = null;
        if (assignment != null)
            selected = row.Papers.Items.Cast<PaperItem>().FirstOrDefault(i => i.Choice == assignment.Paper && (assignment.Paper != PaperChoice.Named || string.Equals(i.Name, assignment.PaperName, StringComparison.OrdinalIgnoreCase)));
        row.Papers.SelectedItem = selected ?? row.Papers.Items[0];
        _loading = was;
    }

    private PrintAssignment AssignmentOf(Row row)
    {
        string printer = row.Printers.SelectedItem as string ?? "";
        var paper = row.Papers.SelectedItem as PaperItem ?? new PaperItem(AutoPaper, PaperChoice.Auto, "");
        return new PrintAssignment
        {
            Printer = printer == NoPrinter ? "" : printer, Paper = paper.Choice, PaperName = paper.Name, Skip = row.Skip.IsChecked == true,
            DevMode = row.DevMode == null ? "" : Convert.ToBase64String(row.DevMode)
        };
    }

    private void RefreshRow(Row row)
    {
        row.Result = PrintRouting.Resolve(row.Group, AssignmentOf(row));
        var result = row.Result;
        string hint = PrintSizePlan.IsSpecial(row.Group.Name) && result.State is not RouteState.Exact and not RouteState.Skipped
            ? "  Special size: print it on a custom paper as big as the pages, or force it onto a paper of the printer." : "";
        row.Status.Text = result.State switch
        {
            RouteState.Exact => "✓ " + result.Message,
            RouteState.Skipped => "— " + result.Message,
            _ => "⚠ " + result.Message + hint
        };
        row.Status.Foreground = result.State == RouteState.Exact ? Brushes.SeaGreen : result.State == RouteState.Skipped ? Brushes.Gray : Brushes.DarkOrange;
        row.Card.BorderBrush = result.NeedsAttention ? Brushes.DarkOrange : TryFindResource("Ui.Border") as Brush ?? Brushes.Gray;
        row.Printers.IsEnabled = row.Papers.IsEnabled = row.Properties.IsEnabled = row.Skip.IsChecked != true;
    }

    private void RefreshAll()
    {
        foreach (var row in _rows) RefreshRow(row);
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        int ok = _rows.Count(r => r.Result.State == RouteState.Exact), attention = _rows.Count(r => r.Result.NeedsAttention), skipped = _rows.Count(r => r.Result.State == RouteState.Skipped);
        _summary.Text = $"{ok} of {_rows.Count} sizes match their paper" + (attention > 0 ? $" · {attention} to look at" : "") + (skipped > 0 ? $" · {skipped} left out" : "");
    }

    // ── profiles ─────────────────────────────────────────────────────

    private void FillProfiles()
    {
        _loading = true;
        _profiles.Items.Clear();
        foreach (var p in PrintProfileStore.All) _profiles.Items.Add(p.Name);
        if (_profileName.Length > 0 && _profiles.Items.Contains(_profileName)) _profiles.SelectedItem = _profileName;
        _loading = false;
    }

    /// <summary>Puts the profile's printer, paper and driver settings on every size it knows; a size it does not know is left with no printer and marked.</summary>
    private void ApplyProfile(string? name)
    {
        var profile = name == null ? null : PrintProfileStore.Get(name);
        if (profile == null) return;
        _profileName = profile.Name;
        PrintProfileStore.SetLast(profile.Name);
        _loading = true;
        foreach (var row in _rows)
        {
            profile.Entries.TryGetValue(row.Group.Name, out var assignment);
            row.Skip.IsChecked = assignment?.Skip == true;
            if (assignment != null && assignment.Printer.Length > 0 && !row.Printers.Items.Contains(assignment.Printer))
                row.Printers.Items.Add(assignment.Printer); // the profile names a printer this computer does not have: shown, and marked
            row.Printers.SelectedItem = assignment != null && assignment.Printer.Length > 0 ? assignment.Printer : NoPrinter;
            row.DevMode = assignment is { DevMode.Length: > 0 } ? Convert.FromBase64String(assignment.DevMode) : null;
            FillPapers(row, assignment);
        }
        _loading = false;
        RefreshAll();
    }

    private void SaveProfile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            var prompt = new TextPromptWindow("Save profile", "Name of the profile (for example Office plotters)", _profileName, v => string.IsNullOrWhiteSpace(v) ? "Type a name." : null) { Owner = this };
            if (prompt.ShowDialog() != true || string.IsNullOrWhiteSpace(prompt.Value)) return;
            name = prompt.Value.Trim();
        }
        var existing = PrintProfileStore.Get(name);
        var profile = existing ?? new PrintProfile { Name = name };
        foreach (var row in _rows) profile.Entries[row.Group.Name] = AssignmentOf(row); // sizes not in this file keep what the profile had
        PrintProfileStore.Set(profile);
        _profileName = profile.Name;
        FillProfiles();
        _summary.Text = $"Profile \"{profile.Name}\" saved.";
    }

    internal void SaveProfileForTest(string name) => SaveProfile(name);

    // ── print ────────────────────────────────────────────────────────

    private void Accept()
    {
        RefreshAll();
        var unassigned = _rows.Where(r => r.Result.State == RouteState.NotAssigned).ToList();
        if (unassigned.Count > 0)
        {
            AppDialog.Show(this, "These sizes have no printer: choose one, or tick \"Do not print\".\n\n" + string.Join("\n", unassigned.Select(r => "• " + PrintSizePlan.Label(r.Group) + " (" + r.Group.Dims + ")")),
                "Print by size", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var cannot = _rows.Where(r => r.Result.State is RouteState.PrinterMissing or RouteState.NamedPaperMissing).ToList();
        if (cannot.Count > 0)
        {
            AppDialog.Show(this, "These sizes cannot be printed as set:\n\n" + string.Join("\n", cannot.Select(r => $"• {PrintSizePlan.Label(r.Group)}: {r.Result.Message}")),
                "Print by size", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var attention = _rows.Where(r => r.Result.State is RouteState.LargerPaper or RouteState.Shrunk).ToList();
        if (attention.Count > 0 && AppDialog.Show(this, "These sizes will not print on a paper of their own size:\n\n" +
                string.Join("\n", attention.Select(r => $"• {PrintSizePlan.Label(r.Group)}: {r.Result.Message}")) + "\n\nPrint anyway?",
                "Print by size", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var jobs = _rows.Where(r => r.Result.CanPrint && r.Printers.SelectedItem is string)
            .Select(r => new RoutedPrintJob(r.Group, (string)r.Printers.SelectedItem!, r.Result.Paper!, r.DevMode)).ToList();
        if (jobs.Count == 0) { AppDialog.Show(this, "Nothing to print: every size is left out.", "Print by size", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Jobs = jobs;
        if (_profileName.Length > 0) PrintProfileStore.SetLast(_profileName);
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
    }

    internal void AcceptForTest() => Accept();
}
