using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XTPdfMergeApp.Services
{
    /// <summary>How the paper of a size is chosen on its printer.</summary>
    public enum PaperChoice
    {
        /// <summary>The printer's paper that matches the pages (the exact size, else the smallest larger one).</summary>
        Auto,
        /// <summary>A paper of the printer the user named (forcing the pages onto it, fitted by the print scale).</summary>
        Named,
        /// <summary>A custom paper as big as the pages (a plotter's roll, an elongated A3): the driver is asked for exactly that size.</summary>
        CustomPageSize
    }

    /// <summary>Where the pages of one kind of sheet (A1, A3, A3 extended…) go: printer, paper, and the driver settings kept for it (tray, quality, plotter options).</summary>
    public sealed record PrintAssignment
    {
        public string Printer { get; init; } = "";
        public PaperChoice Paper { get; init; } = PaperChoice.Auto;
        public string PaperName { get; init; } = "";
        /// <summary>The printer driver's DEVMODE for this size (base 64), from its Properties dialog; "" = the printer's default.</summary>
        public string DevMode { get; init; } = "";
        /// <summary>True: pages of this size are not printed by this profile.</summary>
        public bool Skip { get; init; }
        /// <summary>Copies of this size.</summary>
        public int Copies { get; init; } = 1;
        /// <summary>Collate of this size; null = what the printer itself has (or the driver does not take it from us).</summary>
        public bool? Collate { get; init; }

        /// <summary>Print this size in grayscale.</summary>
        public bool Gray { get; init; }
    }

    /// <summary>A named set of assignments: "Office: A1 on the plotter, A3 on the big Canon, A4 on the laser, A3 extended on the roll plotter". Chosen once, then a whole set prints without setting anything by hand.</summary>
    public sealed class PrintProfile
    {
        public string Name { get; set; } = "";
        public Dictionary<string, PrintAssignment> Entries { get; set; } = new();
    }

    /// <summary>The profiles, kept in %LocalAppData%\XTPdfReader\print-profiles.json (tests point <see cref="FilePath"/> elsewhere).</summary>
    public static class PrintProfileStore
    {
        private sealed class Data
        {
            public string Last { get; set; } = "";
            public List<PrintProfile> Profiles { get; set; } = new();
        }

        private static string _filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTPdfReader", "print-profiles.json");
        private static Data? _data;

        internal static string FilePath
        {
            get => _filePath;
            set { _filePath = value; _data = null; }
        }

        private static Data Load()
        {
            if (_data != null) return _data;
            try { _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data(); }
            catch { _data = new Data(); }
            return _data;
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Load(), new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch { /* not written: kept for this session */ }
        }

        public static IReadOnlyList<PrintProfile> All => Load().Profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        public static PrintProfile? Get(string name) => Load().Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));

        /// <summary>The profile used last (null when none).</summary>
        public static PrintProfile? Last => Get(Load().Last);

        public static void Set(PrintProfile profile)
        {
            var data = Load();
            data.Profiles.RemoveAll(p => string.Equals(p.Name, profile.Name, StringComparison.CurrentCultureIgnoreCase));
            data.Profiles.Add(profile);
            data.Last = profile.Name;
            Save();
        }

        public static void Remove(string name)
        {
            var data = Load();
            data.Profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (string.Equals(data.Last, name, StringComparison.CurrentCultureIgnoreCase)) data.Last = "";
            Save();
        }

        public static void SetLast(string name)
        {
            Load().Last = name;
            Save();
        }
    }

    /// <summary>What printing one kind of sheet under an assignment would do.</summary>
    public enum RouteState
    {
        /// <summary>The exact paper (or a custom paper as big as the pages).</summary>
        Exact,
        /// <summary>No exact paper: the pages go on a larger one.</summary>
        LargerPaper,
        /// <summary>Bigger than the printer's biggest paper: shrunk.</summary>
        Shrunk,
        /// <summary>The paper the user named is not on this printer.</summary>
        NamedPaperMissing,
        /// <summary>The printer is not installed (or not reachable).</summary>
        PrinterMissing,
        /// <summary>Nothing is set for this size.</summary>
        NotAssigned,
        /// <summary>Skipped on purpose.</summary>
        Skipped
    }

    /// <summary>The outcome for one group: the paper to use and a sentence the user can read.</summary>
    internal sealed record RouteResult(RouteState State, PaperSize? Paper, double Shrink, string Message)
    {
        /// <summary>Printing would work but not on the paper the sheet is: the user should look at it.</summary>
        public bool NeedsAttention => State is RouteState.LargerPaper or RouteState.Shrunk or RouteState.NamedPaperMissing or RouteState.PrinterMissing or RouteState.NotAssigned;
        public bool CanPrint => Paper != null && State is RouteState.Exact or RouteState.LargerPaper or RouteState.Shrunk;
    }

    /// <summary>The papers of the installed printers (read once: asking a network printer takes a moment).</summary>
    internal static class PrinterCatalog
    {
        private static readonly Dictionary<string, List<PaperSize>?> Papers = new(StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<string> Installed => PrinterSettings.InstalledPrinters.Cast<string>().ToList();

        public static List<PaperSize>? PapersOf(string printer)
        {
            if (Papers.TryGetValue(printer, out var cached)) return cached;
            List<PaperSize>? list = null;
            try
            {
                var settings = new PrinterSettings { PrinterName = printer };
                if (settings.IsValid) list = settings.PaperSizes.Cast<PaperSize>().ToList();
            }
            catch { /* not reachable */ }
            return Papers[printer] = list;
        }

        public static void Forget() => Papers.Clear();
    }

    /// <summary>Works out, for a group of pages and an assignment, which paper to use and whether that is what the sheet is.</summary>
    internal static class PrintRouting
    {
        public static RouteResult Resolve(PageSizeGroup group, PrintAssignment? assignment)
        {
            if (assignment == null || assignment.Printer.Length == 0) return new RouteResult(RouteState.NotAssigned, null, 0, "No printer is set for this size.");
            if (assignment.Skip) return new RouteResult(RouteState.Skipped, null, 0, "Not printed.");
            var papers = PrinterCatalog.PapersOf(assignment.Printer);
            if (papers == null) return new RouteResult(RouteState.PrinterMissing, null, 0, $"\"{assignment.Printer}\" is not installed or cannot be reached.");

            double shortMm = Math.Min(group.WidthMm, group.HeightMm), longMm = Math.Max(group.WidthMm, group.HeightMm);
            switch (assignment.Paper)
            {
                case PaperChoice.CustomPageSize:
                {
                    var custom = new PaperSize($"XT {shortMm:0}x{longMm:0}", (int)Math.Round(shortMm / 25.4 * 100), (int)Math.Round(longMm / 25.4 * 100));
                    return new RouteResult(RouteState.Exact, custom, 1, $"custom paper {shortMm:0} × {longMm:0} mm");
                }
                case PaperChoice.Named:
                {
                    var named = papers.FirstOrDefault(p => string.Equals(p.PaperName, assignment.PaperName, StringComparison.OrdinalIgnoreCase));
                    if (named == null) return new RouteResult(RouteState.NamedPaperMissing, null, 0, $"\"{assignment.Printer}\" has no paper called \"{assignment.PaperName}\".");
                    double pw = Math.Min(named.Width, named.Height) / 100.0 * 25.4, pl = Math.Max(named.Width, named.Height) / 100.0 * 25.4;
                    bool same = Math.Abs(pw - shortMm) <= 3 && Math.Abs(pl - longMm) <= 3;
                    if (same) return new RouteResult(RouteState.Exact, named, 1, $"prints on {named.PaperName}");
                    double fit = Math.Min(pw / shortMm, pl / longMm);
                    return fit >= 1
                        ? new RouteResult(RouteState.LargerPaper, named, 1, $"forced onto {named.PaperName} (bigger than the pages)")
                        : new RouteResult(RouteState.Shrunk, named, fit, $"forced onto {named.PaperName}: the pages shrink to {fit * 100:0}%");
                }
                default:
                {
                    var options = papers.Select(p => new PaperOption(p.PaperName, p.Width / 100.0 * 25.4, p.Height / 100.0 * 25.4)).ToList();
                    var (match, fit, shrink) = PrintSizePlan.Match(shortMm, longMm, options);
                    var paper = papers.FirstOrDefault(p => p.PaperName == match?.Name);
                    if (paper == null) return new RouteResult(RouteState.Shrunk, null, 0, "This printer lists no usable paper.");
                    return fit switch
                    {
                        SizeFit.Exact => new RouteResult(RouteState.Exact, paper, 1, $"prints on {paper.PaperName}"),
                        SizeFit.LargerPaper => new RouteResult(RouteState.LargerPaper, paper, 1, $"no paper of this size: prints on the larger {paper.PaperName}"),
                        _ => new RouteResult(RouteState.Shrunk, paper, shrink, $"bigger than every paper: shrinks to {shrink * 100:0}% on {paper.PaperName}")
                    };
                }
            }
        }
    }
}
