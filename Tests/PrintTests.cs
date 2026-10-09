using System.Drawing.Printing;
using System.IO;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    private const string PdfPrinter = "Microsoft Print to PDF";

    static bool HasPdfPrinter() => PrinterSettings.InstalledPrinters.Cast<string>().Contains(PdfPrinter);

    static void UseTestPrintProfiles(string name)
    {
        PrintProfileStore.FilePath = System.IO.Path.Combine(Output, name + "-print-profiles.json");
        try { File.Delete(PrintProfileStore.FilePath); } catch { }
    }

    /// <summary>Sheets are told apart as standard, elongated (A3 extended) and special; elongated ones of any length are one kind.</summary>
    static void TestPrintSizeClasses()
    {
        const double pt = 72 / 25.4;
        (double, double) Mm(double w, double h) => (w * pt, h * pt);
        Check(PrintSizePlan.Classify(297, 420) is { Name: "A3", Extended: false }, "297 x 420 is A3");
        Check(PrintSizePlan.Classify(630, 297) is { Name: "A3 extended", Extended: true }, "297 x 630 (either way round) is A3 extended");
        Check(PrintSizePlan.Classify(210, 891) is { Name: "A4 extended", Extended: true }, "210 x 891 is A4 extended");
        Check(PrintSizePlan.Classify(297, 300) is { Extended: false } c && c.Name.Contains('×'), "297 x 300 is a special size, not an A3");
        Check(PrintSizePlan.Classify(1300, 900) is { Extended: false } big && big.Name.Contains('×'), "A big odd sheet is special");
        Check(PrintSizePlan.IsSpecial("A3 extended") && PrintSizePlan.IsSpecial("297×300") && !PrintSizePlan.IsSpecial("A3") && !PrintSizePlan.IsSpecial("Letter"), "Extended and odd sizes are special, A and Letter are not");

        var pages = new List<(double, double)> { Mm(594, 841), Mm(297, 630), Mm(297, 840), Mm(420, 297), Mm(297, 630), Mm(210, 297) };
        var groups = PrintSizePlan.Build(pages, new[] { new PaperOption("A3", 297, 420), new PaperOption("A4", 210, 297), new PaperOption("A1", 594, 841) });
        var extended = groups.Single(g => g.Name == "A3 extended");
        Check(extended.Count == 3 && Math.Abs(Math.Max(extended.WidthMm, extended.HeightMm) - 840) < 1, "Elongated A3 sheets of different lengths are one group that keeps the longest");
        Check(groups.Count == 4 && extended.Fit != SizeFit.Exact, "…and it has no exact paper on a printer that lists only A sizes");
    }

    /// <summary>Profiles are saved by name and read back; the paper of a size is worked out (exact, larger, custom, forced, missing printer) with a message to show.</summary>
    static void TestPrintProfilesAndRouting()
    {
        UseTestPrintProfiles("profiles");
        var profile = new PrintProfile { Name = "Office" };
        profile.Entries["A1"] = new PrintAssignment { Printer = "Plotter 1" };
        profile.Entries["A3 extended"] = new PrintAssignment { Printer = "Plotter 2", Paper = PaperChoice.CustomPageSize, DevMode = Convert.ToBase64String(new byte[] { 1, 2, 3 }) };
        PrintProfileStore.Set(profile);
        PrintProfileStore.FilePath = PrintProfileStore.FilePath; // forces a re-read from the file
        var back = PrintProfileStore.Get("office");
        Check(back != null && back.Entries.Count == 2 && back.Entries["A3 extended"].Paper == PaperChoice.CustomPageSize && back.Entries["A3 extended"].DevMode == "AQID", "A profile is kept on disk with its assignments and driver settings");
        Check(PrintProfileStore.Last?.Name == "Office", "The last profile used is remembered");
        PrintProfileStore.Remove("Office");
        Check(PrintProfileStore.All.Count == 0 && PrintProfileStore.Last == null, "A profile can be deleted");

        const double pt = 72 / 25.4;
        var groups = PrintSizePlan.Build(new[] { (210 * pt, 297 * pt), (297 * pt, 630 * pt), (594 * pt, 841 * pt) }, new[] { new PaperOption("A4", 210, 297) });
        var a4 = groups.First(g => g.Name == "A4");
        var ext = groups.First(g => g.Name == "A3 extended");
        Check(PrintRouting.Resolve(a4, null).State == RouteState.NotAssigned, "No assignment: the size has no printer");
        Check(PrintRouting.Resolve(a4, new PrintAssignment { Printer = "x", Skip = true }).State == RouteState.Skipped, "Skipped on purpose");
        Check(PrintRouting.Resolve(a4, new PrintAssignment { Printer = "No such printer 42" }).State == RouteState.PrinterMissing, "A printer that is not installed is reported");
        if (!HasPdfPrinter()) { Console.WriteLine("Print routing: no Microsoft Print to PDF here; the paper checks are skipped"); return; }
        var exact = PrintRouting.Resolve(a4, new PrintAssignment { Printer = PdfPrinter });
        Check(exact.State == RouteState.Exact && exact.Paper != null && exact.Paper.PaperName.Contains("A4"), $"A4 pages find A4 on the printer ({exact.Message})");
        var custom = PrintRouting.Resolve(ext, new PrintAssignment { Printer = PdfPrinter, Paper = PaperChoice.CustomPageSize });
        Check(custom.State == RouteState.Exact && custom.Paper != null && Math.Abs(custom.Paper.Height / 100.0 * 25.4 - 630) < 1 && Math.Abs(custom.Paper.Width / 100.0 * 25.4 - 297) < 1, "A custom paper as big as the pages is asked for exactly that size");
        var auto = PrintRouting.Resolve(ext, new PrintAssignment { Printer = PdfPrinter });
        Check(auto.State is RouteState.Shrunk or RouteState.LargerPaper && auto.NeedsAttention, $"An elongated sheet on automatic paper needs attention ({auto.Message})");
        var missing = PrintRouting.Resolve(a4, new PrintAssignment { Printer = PdfPrinter, Paper = PaperChoice.Named, PaperName = "Paper that does not exist" });
        Check(missing.State == RouteState.NamedPaperMissing, "A named paper the printer does not have is reported");
        var a4Paper = PrinterCatalog.PapersOf(PdfPrinter)!.First(p => p.PaperName.Contains("A4"));
        var forced = PrintRouting.Resolve(groups.First(g => g.Name == "A1"), new PrintAssignment { Printer = PdfPrinter, Paper = PaperChoice.Named, PaperName = a4Paper.PaperName });
        Check(forced.State == RouteState.Shrunk && forced.Shrink is > 0 and < 1 && forced.Message.Contains("shrink"), $"A sheet forced onto a smaller paper says how much it shrinks ({forced.Message})");
    }

    /// <summary>"Print by paper size" window: every kind of sheet is a row, a profile fills the rows and is saved, sizes with no printer or no matching paper are marked.</summary>
    static void TestPrintRoutingWindow()
    {
        if (!HasPdfPrinter()) { Console.WriteLine("Print routing window: no Microsoft Print to PDF here; skipped"); return; }
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        UseTestPrintProfiles("routing");
        const double pt = 72 / 25.4;
        var groups = PrintSizePlan.Build(new[] { (210 * pt, 297 * pt), (210 * pt, 297 * pt), (297 * pt, 420 * pt), (297 * pt, 630 * pt) }, new[] { new PaperOption("A4", 210, 297) });
        var window = Offscreen(new PrintRoutingWindow(groups, PdfPrinter));
        var rows = ((PrintRoutingWindow)window).RowsForTest;
        Check(rows.Count == 3 && rows.Select(r => r.Class).OrderBy(x => x).SequenceEqual(new[] { "A3", "A3 extended", "A4" }), "One row for each kind of sheet");
        Check(rows.All(r => (string)r.Printer.SelectedItem! == PdfPrinter) && rows.First(r => r.Class == "A4").Result.State == RouteState.Exact, "Without a profile every size starts on the printer chosen in the dialog; A4 matches its paper");
        var ext = rows.First(r => r.Class == "A3 extended");
        Check(ext.Result.NeedsAttention, "The elongated sheet is marked: no paper of its own");
        // choose the custom paper for it and save the profile
        ext.Paper.SelectedIndex = 1;
        Pump(TimeSpan.FromMilliseconds(100));
        Check(((PrintRoutingWindow)window).RowsForTest.First(r => r.Class == "A3 extended").Result.State == RouteState.Exact, "A custom paper as big as the pages clears the mark");
        ((PrintRoutingWindow)window).SaveProfileForTest("Test office");
        Check(PrintProfileStore.Get("Test office") is { } saved && saved.Entries["A3 extended"].Paper == PaperChoice.CustomPageSize && saved.Entries["A4"].Printer == PdfPrinter, "Save keeps the printer and paper of every size in a profile");
        SavePng(window, "ui-print-routing");
        window.Close();

        // next time the profile is applied by itself; a size the profile does not know has no printer and says so
        var groupsMore = PrintSizePlan.Build(new[] { (210 * pt, 297 * pt), (420 * pt, 594 * pt), (297 * pt, 630 * pt) }, new[] { new PaperOption("A4", 210, 297) });
        var again = Offscreen(new PrintRoutingWindow(groupsMore, PdfPrinter));
        var rows2 = ((PrintRoutingWindow)again).RowsForTest;
        Check(((PrintRoutingWindow)again).ProfileBox.SelectedItem as string == "Test office", "The last profile is chosen when the window opens");
        Check(rows2.First(r => r.Class == "A4").Result.State == RouteState.Exact && rows2.First(r => r.Class == "A3 extended").Result.State == RouteState.Exact, "…and sets the printer and paper of the sizes it knows");
        var a2 = rows2.First(r => r.Class == "A2");
        Check(a2.Result.State == RouteState.NotAssigned && a2.Printer.SelectedItem as string == "(choose a printer)", "A size the profile has no entry for is marked: no printer");
        a2.Skip.IsChecked = true;
        Check(a2.Skip.IsChecked == true, "It can be left out");
        again.Close();
    }

    /// <summary>The Collate box of Ctrl+P agrees with the printer: it shows the printer's own setting, is off when the driver cannot collate, writes into the driver settings, and follows them after Properties.</summary>
    static void TestPrintCollateFollowsDriver()
    {
        // a virtual printer whose driver can collate is better to prove the round trip; otherwise Microsoft Print to PDF checks the "cannot" side
        var installed = PrinterSettings.InstalledPrinters.Cast<string>().ToList();
        string printer = new[] { "PDF24", "PDF Report Writer", PdfPrinter }.FirstOrDefault(p => installed.Contains(p) && PrinterDriver.SupportsCollate(p))
                         ?? new[] { PdfPrinter }.FirstOrDefault(installed.Contains) ?? "";
        if (printer.Length == 0) { Console.WriteLine("Collate: no virtual printer here; skipped"); return; }
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        string folder = System.IO.Path.Combine(Output, "print-collate");
        Directory.CreateDirectory(folder);
        string pdf = System.IO.Path.Combine(folder, "two.pdf");
        using (var doc = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfWriter(pdf))) { doc.AddNewPage(); doc.AddNewPage(); }
        var window = new PrintWindow(new[] { (pdf, 1), (pdf, 2) }, 0);
        Offscreen(window);
        Pump(TimeSpan.FromMilliseconds(1500));
        var panel = (PrintBySizePanel)((System.Windows.Controls.Border)window.FindName("BySizeHost")!).Child;
        var card = panel.CardsForTest.First();
        card.Printer.SelectedItem = printer;
        Pump(TimeSpan.FromMilliseconds(300));
        bool can = PrinterDriver.SupportsCollate(printer);
        Console.WriteLine($"Collate: {printer} driver {(can ? "can" : "cannot")} collate");
        Check(card.Collate.IsEnabled == (can && PrinterDriver.HonorsCollate(printer)), "The card's box is on only when the driver takes Collate from us");
        var settings = new PrinterSettings { PrinterName = printer };
        Check(!card.Collate.IsEnabled || card.Collate.IsChecked == settings.Collate, "It starts as the printer's own default");

        // the driver's own dialog changes it: the card follows
        var devMode = PrinterDriver.WithSettings(printer, null, can ? !(card.Collate.IsChecked == true) : null, 5, null)!;
        panel.ShowFromDevMode(card.Class, devMode);
        Check(card.Copies.Text == "5" && (!can || card.Collate.IsChecked == PrinterDriver.Read(printer, devMode).Collate), "After Properties the card shows what the driver says");
        window.Close();
    }
}
