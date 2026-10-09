using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// The printers of the print dialog: one compact card for each kind of sheet that is in the print range (A1, A3, A3 extended, a special size…; the color pages of a size are a card of their own),
/// each with its printer, paper, copies and collate. Only the sizes of the pages that will print are listed, and what was chosen for a size is remembered while the dialog is open.
/// The profiles (the choice of all cards kept under a name: A1 on the plotter, A3 on the big Canon…) are in <see cref="ProfileBar"/>, which the dialog puts in its footer.
/// </summary>
internal sealed class PrintBySizePanel : UserControl
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
        public required CheckBox Collate;
        public required TextBox Copies;
        public required TextBlock Status;
        public byte[]? DevMode;
        public RouteResult Result = new(RouteState.NotAssigned, null, 0, "");
    }

    private sealed record PaperItem(string Text, PaperChoice Choice, string Name)
    {
        public override string ToString() => Text;
    }

    private readonly List<Row> _rows = new();
    private readonly Dictionary<string, PrintAssignment> _memory = new(StringComparer.Ordinal);
    private readonly StackPanel _stack = new();
    private readonly ComboBox _profiles = new() { Height = 28, MinWidth = 150, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    private readonly string _defaultPrinter;
    private bool _loading;
    private string _profileName = "";

    /// <summary>The sizes, printers, papers, copies or collate changed.</summary>
    public event Action? Changed;

    /// <summary>Profile, Save, Save as…, Delete: the dialog puts this bar where it wants (its footer).</summary>
    public FrameworkElement ProfileBar { get; }

    internal IReadOnlyList<(string Class, ComboBox Printer, ComboBox Paper, CheckBox Skip, RouteResult Result)> RowsForTest
        => _rows.Select(r => (PrintSizePlan.Key(r.Group), r.Printers, r.Papers, r.Skip, r.Result)).ToList();
    internal IReadOnlyList<(string Class, ComboBox Printer, CheckBox Collate, TextBox Copies)> CardsForTest
        => _rows.Select(r => (PrintSizePlan.Key(r.Group), r.Printers, r.Collate, r.Copies)).ToList();
    internal ComboBox ProfileBox => _profiles;
    internal string SummaryText => _summary.Text;

    public PrintBySizePanel(string defaultPrinter)
    {
        _defaultPrinter = defaultPrinter;
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted");

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(new TextBlock { Text = "Profile", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        bar.Children.Add(_profiles);
        var save = AreaKit.Ghost("Save", 28);
        var saveAs = AreaKit.Ghost("Save as…", 28);
        var delete = AreaKit.Ghost("Delete", 28);
        foreach (var b in new[] { save, saveAs, delete }) { b.Margin = new Thickness(6, 0, 0, 0); bar.Children.Add(b); }
        ProfileBar = bar;

        Content = new StackPanel { Children = { _stack, _summary } };

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
        var last = PrintProfileStore.Last;
        if (last != null) { _loading = true; _profileName = last.Name; _profiles.SelectedItem = last.Name; _loading = false; }
    }

    // ── the sizes ────────────────────────────────────────────────────

    /// <summary>Shows one card for each of these groups (the sizes, and color, of the pages in the print range), keeping what was chosen for each.</summary>
    public void SetGroups(IReadOnlyList<PageSizeGroup> groups)
    {
        foreach (var row in _rows) Remember(row);
        _rows.Clear();
        _stack.Children.Clear();
        bool firstBuild = _memory.Count == 0;
        foreach (var group in groups)
        {
            var row = BuildRow(group);
            _stack.Children.Add(row.Card);
            if (_memory.TryGetValue(PrintSizePlan.Key(group), out var kept)) Restore(row, kept);
            else if (firstBuild && PrintProfileStore.Last is { } profile) Restore(row, profile.Entries.TryGetValue(PrintSizePlan.Key(group), out var fromProfile) ? fromProfile : new PrintAssignment()); // a size the profile does not know has no printer and is marked
            else RefreshCollate(row, true);
        }
        RefreshAll();
        Loc.ApplyTree(this);
    }

    /// <summary>The printer of the first card that prints, or null.</summary>
    public string? FirstPrinter() => _rows.Where(r => r.Skip.IsChecked != true).Select(r => r.Printers.SelectedItem as string).FirstOrDefault(p => !string.IsNullOrEmpty(p) && p != NoPrinter);

    /// <summary>The paper chosen for the card with this key (what the preview of its pages shows), or null when it is not decided yet.</summary>
    public PaperSize? PaperOf(string groupKey) => _rows.FirstOrDefault(r => PrintSizePlan.Key(r.Group) == groupKey)?.Result.Paper;

    private void Remember(Row row) => _memory[PrintSizePlan.Key(row.Group)] = AssignmentOf(row);

    private void Restore(Row row, PrintAssignment assignment)
    {
        _loading = true;
        row.Skip.IsChecked = assignment.Skip;
        if (assignment.Printer.Length > 0 && !row.Printers.Items.Contains(assignment.Printer)) row.Printers.Items.Add(assignment.Printer); // a printer this computer does not have: shown, and marked
        row.Printers.SelectedItem = assignment.Printer.Length > 0 ? assignment.Printer : NoPrinter;
        row.DevMode = assignment.DevMode.Length > 0 ? Convert.FromBase64String(assignment.DevMode) : null;
        row.Copies.Text = Math.Clamp(assignment.Copies, 1, 99).ToString();
        FillPapers(row, assignment);
        RefreshCollate(row, assignment.Collate == null);
        if (assignment.Collate is { } collate && row.Collate.IsEnabled) row.Collate.IsChecked = collate;
        _loading = false;
    }

    private Row BuildRow(PageSizeGroup group)
    {
        Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        var installed = PrinterCatalog.Installed;
        var printers = new ComboBox { Height = 28, Margin = new Thickness(0, 0, 6, 0) };
        printers.Items.Add(NoPrinter);
        foreach (string p in installed) printers.Items.Add(p);
        var papers = new ComboBox { Height = 28, Margin = new Thickness(0, 0, 8, 0) };
        var properties = AreaKit.Ghost("Properties…", 28);
        properties.Padding = new Thickness(10, 0, 10, 0);
        properties.ToolTip = Loc.T("The printer driver's own settings for this size (tray, quality, plotter options…), kept in the profile");
        var collate = new CheckBox { Content = "Collate", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var copies = new TextBox { Width = 44, Height = 28, Text = "1", VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, ToolTip = "Copies" };
        var skip = new CheckBox { Content = "Do not print", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(10, 0, 0, 0) };
        var status = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap };
        var card = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 10, 8), Margin = new Thickness(0, 0, 0, 8),
            BorderBrush = R("Ui.Border"), Background = R("Ui.Surface")
        };

        // title: "A3 (70 pages)", a small button that lists the pages, and "Do not print"
        var detail = AreaKit.Ghost("…", 22, 26);
        detail.Padding = new Thickness(6, 0, 6, 0);
        detail.Margin = new Thickness(8, 0, 0, 0);
        detail.ToolTip = "Which pages";
        var title = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(skip, Dock.Right);
        title.Children.Add(skip);
        var titleText = new StackPanel { Orientation = Orientation.Horizontal };
        titleText.Children.Add(new TextBlock { Text = $"{group.Name}{(group.Color ? " · color" : "")} ({group.Count} page{(group.Count == 1 ? "" : "s")})", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = group.Color ? Brushes.DarkOrange : R("Ui.Text") });
        titleText.Children.Add(new TextBlock { Text = $"  {group.Dims}", Opacity = 0.65, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
        if (PrintSizePlan.IsSpecial(group.Name)) titleText.Children.Add(new TextBlock { Text = "  special size", Foreground = Brushes.DarkOrange, FontWeight = FontWeights.SemiBold, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
        titleText.Children.Add(detail);
        title.Children.Add(titleText);
        var pagesPopup = new Popup { PlacementTarget = detail, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        var pagesText = new TextBlock { Text = PrintSizePlan.PageList(group.PageIndexes), TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
        pagesText.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
        var pagesBox = new Border { Padding = new Thickness(10, 8, 10, 8), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 4, 12, 12), Child = pagesText };
        pagesBox.SetResourceReference(Border.BackgroundProperty, "Ui.Surface");
        pagesBox.SetResourceReference(Border.BorderBrushProperty, "Ui.Border");
        pagesBox.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.25 };
        pagesPopup.Child = pagesBox;
        detail.Click += (_, _) => pagesPopup.IsOpen = !pagesPopup.IsOpen;

        // printer and Properties
        var printerLine = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(properties, Dock.Right);
        printerLine.Children.Add(properties);
        printerLine.Children.Add(printers);

        // paper, collate and copies
        var paperLine = new DockPanel();
        var copiesLabel = new TextBlock { Text = "Copies:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), FontSize = 12 };
        copiesLabel.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted");
        DockPanel.SetDock(copies, Dock.Right);
        DockPanel.SetDock(copiesLabel, Dock.Right);
        DockPanel.SetDock(collate, Dock.Right);
        paperLine.Children.Add(copies);
        paperLine.Children.Add(copiesLabel);
        paperLine.Children.Add(collate);
        paperLine.Children.Add(papers);

        card.Child = new StackPanel { Children = { title, printerLine, paperLine, status } };
        var row = new Row { Group = group, Card = card, Skip = skip, Printers = printers, Papers = papers, Properties = properties, Collate = collate, Copies = copies, Status = status };
        _rows.Add(row);

        printers.SelectionChanged += (_, _) => { if (_loading) return; row.DevMode = null; FillPapers(row, null); RefreshCollate(row, true); RefreshRow(row); RefreshSummary(); Remember(row); Changed?.Invoke(); };
        papers.SelectionChanged += (_, _) => { if (_loading) return; RefreshRow(row); RefreshSummary(); Remember(row); Changed?.Invoke(); };
        skip.Click += (_, _) => { RefreshRow(row); RefreshSummary(); Remember(row); Changed?.Invoke(); };
        collate.Click += (_, _) => { Remember(row); };
        copies.LostFocus += (_, _) => { copies.Text = CopiesOf(row).ToString(); Remember(row); };
        properties.Click += (_, _) => OpenProperties(row);
        _loading = true;
        printers.SelectedItem = installed.Contains(_defaultPrinter) ? _defaultPrinter : NoPrinter;
        FillPapers(row, null);
        _loading = false;
        return row;
    }

    private static int CopiesOf(Row row) => int.TryParse(row.Copies.Text, out int c) ? Math.Clamp(c, 1, 99) : 1;

    /// <summary>Collate of the card: it follows its printer's driver (the box is off when the driver cannot take it from us, and then only shows what the driver has).</summary>
    private void RefreshCollate(Row row, bool fromPrinterDefault)
    {
        if (row.Printers.SelectedItem is not string printer || printer == NoPrinter) { row.Collate.IsEnabled = false; row.Collate.IsChecked = false; return; }
        bool can = PrinterDriver.SupportsCollate(printer);
        bool honored = can && PrinterDriver.HonorsCollate(printer);
        row.Collate.IsEnabled = honored;
        row.Collate.ToolTip = !can ? "This printer's driver cannot collate copies"
            : honored ? "Same as the Collate setting of the printer's Properties"
            : "This driver keeps its own Collate setting: change it in Properties… (the box shows what the driver has)";
        if (!fromPrinterDefault) return;
        try { row.Collate.IsChecked = can && new PrinterSettings { PrinterName = printer }.Collate; }
        catch { row.Collate.IsChecked = false; }
    }

    /// <summary>The driver's own dialog for the card: it opens with the card's collate, copies and paper, and what it returns is shown in the card.</summary>
    private void OpenProperties(Row row)
    {
        if (row.Printers.SelectedItem is not string printer || printer == NoPrinter) return;
        var owner = Window.GetWindow(this);
        var start = PrinterDriver.WithSettings(printer, row.DevMode, row.Collate.IsEnabled ? row.Collate.IsChecked == true : null, CopiesOf(row), row.Result.Paper) ?? row.DevMode;
        var devMode = PrinterDriver.ShowDialog(owner == null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(owner).Handle, printer, start);
        if (devMode == null) return;
        row.DevMode = devMode;
        ShowFromDevMode(row, devMode);
        Remember(row);
    }

    /// <summary>What the driver says now (after its Properties dialog): copies and collate of the card follow it.</summary>
    internal void ShowFromDevMode(string groupKey, byte[] devMode)
    {
        if (_rows.FirstOrDefault(r => PrintSizePlan.Key(r.Group) == groupKey) is { } row) ShowFromDevMode(row, devMode);
    }

    private void ShowFromDevMode(Row row, byte[] devMode)
    {
        if (row.Printers.SelectedItem is not string printer || printer == NoPrinter) return;
        var (_, copies, collate) = PrinterDriver.Read(printer, devMode);
        row.Copies.Text = Math.Clamp((int)copies, 1, 99).ToString();
        if (PrinterDriver.SupportsCollate(printer)) row.Collate.IsChecked = collate;
    }

    /// <summary>The papers of the row's printer, and which one is chosen.</summary>
    private void FillPapers(Row row, PrintAssignment? assignment)
    {
        bool was = _loading;
        _loading = true;
        row.Papers.Items.Clear();
        row.Papers.Items.Add(new PaperItem(Loc.T(AutoPaper), PaperChoice.Auto, ""));
        row.Papers.Items.Add(new PaperItem(Loc.T(CustomPaper), PaperChoice.CustomPageSize, ""));
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
            DevMode = row.DevMode == null ? "" : Convert.ToBase64String(row.DevMode),
            Copies = CopiesOf(row), Collate = row.Collate.IsEnabled ? row.Collate.IsChecked == true : null
        };
    }

    private void RefreshRow(Row row)
    {
        row.Result = PrintRouting.Resolve(row.Group, AssignmentOf(row));
        var result = row.Result;
        string hint = PrintSizePlan.IsSpecial(row.Group.Name) && result.State is not RouteState.Exact and not RouteState.Skipped
            ? "  Special size: print it on a custom paper as big as the pages, or force it onto a paper of the printer." : "";
        string message = Loc.T(result.Message);
        row.Status.Text = result.State switch
        {
            RouteState.Exact => "✓ " + message,
            RouteState.Skipped => "— " + message,
            _ => "⚠ " + message + (hint.Length > 0 ? "  " + Loc.T(hint.Trim()) : "")
        };
        row.Status.Foreground = result.State == RouteState.Exact ? Brushes.SeaGreen : result.State == RouteState.Skipped ? Brushes.Gray : Brushes.DarkOrange;
        // "prints on A3" is not worth a line on every card: only what needs a look is written
        row.Status.Visibility = result.State is RouteState.Exact or RouteState.Skipped ? Visibility.Collapsed : Visibility.Visible;
        row.Card.BorderBrush = result.NeedsAttention ? Brushes.DarkOrange : TryFindResource("Ui.Border") as Brush ?? Brushes.Gray;
        row.Printers.IsEnabled = row.Papers.IsEnabled = row.Properties.IsEnabled = row.Copies.IsEnabled = row.Skip.IsChecked != true;
        if (row.Skip.IsChecked == true) row.Collate.IsEnabled = false; else RefreshCollate(row, false);
    }

    private void RefreshAll()
    {
        foreach (var row in _rows) RefreshRow(row);
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        int ok = _rows.Count(r => r.Result.State == RouteState.Exact), attention = _rows.Count(r => r.Result.NeedsAttention), skipped = _rows.Count(r => r.Result.State == RouteState.Skipped);
        _summary.Text = Loc.T($"{ok} of {_rows.Count} sizes match their paper") + (attention > 0 ? " · " + attention + Loc.T(" to look at") : "") + (skipped > 0 ? " · " + skipped + Loc.T(" left out") : "");
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

    /// <summary>Puts the profile's printer, paper, copies, collate and driver settings on every card it knows; a card it does not know is left with no printer and marked.</summary>
    private void ApplyProfile(string? name)
    {
        var profile = name == null ? null : PrintProfileStore.Get(name);
        if (profile == null) return;
        _profileName = profile.Name;
        PrintProfileStore.SetLast(profile.Name);
        foreach (var row in _rows)
        {
            profile.Entries.TryGetValue(PrintSizePlan.Key(row.Group), out var assignment);
            Restore(row, assignment ?? new PrintAssignment());
            Remember(row);
        }
        RefreshAll();
        Changed?.Invoke();
    }

    private void SaveProfile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            var prompt = new TextPromptWindow("Save profile", "Name of the profile (for example Office plotters)", _profileName, v => string.IsNullOrWhiteSpace(v) ? "Type a name." : null) { Owner = Window.GetWindow(this) };
            if (prompt.ShowDialog() != true || string.IsNullOrWhiteSpace(prompt.Value)) return;
            name = prompt.Value.Trim();
        }
        var existing = PrintProfileStore.Get(name);
        var profile = existing ?? new PrintProfile { Name = name };
        foreach (var row in _rows) profile.Entries[PrintSizePlan.Key(row.Group)] = AssignmentOf(row); // sizes not in this print range keep what the profile had
        PrintProfileStore.Set(profile);
        _profileName = profile.Name;
        FillProfiles();
        _summary.Text = Loc.T($"Profile \"{profile.Name}\" saved.");
    }

    internal void SaveProfileForTest(string name) => SaveProfile(name);

    // ── print ────────────────────────────────────────────────────────

    /// <summary>
    /// What to print: one job per card that has a printer and a paper. Cards with no printer, a missing printer or paper, or a paper that is not their own size are asked about first;
    /// null when the user must still decide something (or nothing is left to print).
    /// </summary>
    public IReadOnlyList<RoutedPrintJob>? TryBuildJobs(Window? owner)
    {
        RefreshAll();
        var unassigned = _rows.Where(r => r.Result.State == RouteState.NotAssigned).ToList();
        if (unassigned.Count > 0)
        {
            AppDialog.Show(owner, "These sizes have no printer: choose one, or tick \"Do not print\".\n\n" + string.Join("\n", unassigned.Select(r => "• " + PrintSizePlan.Label(r.Group) + " (" + r.Group.Dims + ")")),
                "Print by size", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        var cannot = _rows.Where(r => r.Result.State is RouteState.PrinterMissing or RouteState.NamedPaperMissing).ToList();
        if (cannot.Count > 0)
        {
            AppDialog.Show(owner, "These sizes cannot be printed as set:\n\n" + string.Join("\n", cannot.Select(r => $"• {PrintSizePlan.Label(r.Group)}: {r.Result.Message}")),
                "Print by size", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        var attention = _rows.Where(r => r.Result.State is RouteState.LargerPaper or RouteState.Shrunk).ToList();
        if (attention.Count > 0 && AppDialog.Show(owner, "These sizes will not print on a paper of their own size:\n\n" +
                string.Join("\n", attention.Select(r => $"• {PrintSizePlan.Label(r.Group)}: {r.Result.Message}")) + "\n\nPrint anyway?",
                "Print by size", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return null;

        var jobs = _rows.Where(r => r.Result.CanPrint && r.Printers.SelectedItem is string)
            .Select(r => new RoutedPrintJob(r.Group, (string)r.Printers.SelectedItem!, r.Result.Paper!, r.DevMode, CopiesOf(r), r.Collate.IsEnabled ? r.Collate.IsChecked == true : null)).ToList();
        if (jobs.Count == 0) { AppDialog.Show(owner, "Nothing to print: every size is left out.", "Print by size", MessageBoxButton.OK, MessageBoxImage.Information); return null; }
        if (_profileName.Length > 0) PrintProfileStore.SetLast(_profileName);
        return jobs;
    }
}
