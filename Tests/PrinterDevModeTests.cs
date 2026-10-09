using System.IO;
using System.Drawing.Printing;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    /// <summary>Manual check with a real driver (LBP8630): does the DEVMODE we hand to the driver's Properties dialog carry our Collate?</summary>
    static void TestPrinterDevModeRoundTrip()
    {
        const string printer = "LBP8630";
        if (!PrinterSettings.InstalledPrinters.Cast<string>().Contains(printer)) { Console.WriteLine("DevMode check: " + printer + " not installed; skipped"); return; }
        var def = PrinterDriver.GetDefault(printer)!;
        Console.WriteLine($"default devmode bytes={def.Length}, dmSize={BitConverter.ToUInt16(def, 68)}, dmDriverExtra={BitConverter.ToUInt16(def, 70)}, dmFields=0x{BitConverter.ToUInt32(def, 72):X}, dmCollate={BitConverter.ToInt16(def, 62 + 0)}");
        foreach (bool collate in new[] { false, true })
        {
            var made = PrinterDriver.WithSettings(printer, def, collate, 1, null)!;
            int diff = 0, dmSize = BitConverter.ToUInt16(made, 68);
            for (int i = dmSize; i < Math.Min(made.Length, def.Length); i++) if (made[i] != def[i]) diff++;
            var round = PrinterDriver.Normalize(printer, made);
            var (_, _, back) = round == null ? (null, (short)0, false) : PrinterDriver.Read(printer, round);
            Console.WriteLine($"collate={collate}: made bytes={made.Length} (same size as default: {made.Length == def.Length}), private bytes changed vs default={diff}, after the driver round trip: len={round?.Length}, collate reads back as {back}");
        }
    }
}

internal static partial class Program
{
    /// <summary>Manual check with a real driver: where the graphics origin sits for a printer with a hard margin, with and without OriginAtMargins (preview controller: nothing is printed).</summary>
    static void TestPrinterOrigin()
    {
        foreach (string printer in new[] { "LBP8630", "RICOH MP 6503 PCL 6" })
        {
            if (!PrinterSettings.InstalledPrinters.Cast<string>().Contains(printer)) continue;
            foreach (bool atMargins in new[] { false, true })
            {
                var document = new PrintDocument();
                document.PrinterSettings.PrinterName = printer;
                document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
                document.OriginAtMargins = atMargins;
                document.PrintController = new PreviewPrintController();
                string line = "";
                document.PrintPage += (_, e) =>
                {
                    var t = e.Graphics!.Transform;
                    line = $"hard margin ({e.PageSettings.HardMarginX:0},{e.PageSettings.HardMarginY:0}) hundredths of an inch, graphics offset ({t.OffsetX:0.#},{t.OffsetY:0.#}), page bounds {e.PageBounds}";
                    e.HasMorePages = false;
                };
                try { document.Print(); } catch (Exception ex) { line = "error " + ex.Message; }
                Console.WriteLine($"{printer} OriginAtMargins={atMargins}: {line}");
            }
        }
    }
}

internal static partial class Program
{
    /// <summary>Manual check: can Collate be set through a PrintTicket (the driver's own converter writes its private settings)?</summary>
    static void TestPrintTicketCollate()
    {
        foreach (string printer in new[] { "LBP8630", "LBP9600C", "RICOH MP 6503 PCL 6", "Microsoft Print to PDF" })
        {
            if (!PrinterSettings.InstalledPrinters.Cast<string>().Contains(printer)) continue;
            try
            {
                using var server = new System.Printing.LocalPrintServer();
                using var queue = server.GetPrintQueue(printer);
                var results = new List<(System.Printing.Collation Want, byte[] Mode, System.Printing.Collation? Ticket, bool PublicCollate, bool AfterDriver)>();
                foreach (var want in new[] { System.Printing.Collation.Uncollated, System.Printing.Collation.Collated })
                {
                    var merged = queue.MergeAndValidatePrintTicket(queue.UserPrintTicket, new System.Printing.PrintTicket { Collation = want });
                    var converter = new System.Printing.Interop.PrintTicketConverter(printer, queue.ClientPrintSchemaVersion);
                    byte[] mode = converter.ConvertPrintTicketToDevMode(merged.ValidatedPrintTicket, System.Printing.Interop.BaseDevModeType.UserDefault);
                    var back = converter.ConvertDevModeToPrintTicket(mode);
                    bool publicCollate = PrinterDriver.Read(printer, mode).Collate;
                    var round = PrinterDriver.Normalize(printer, mode);
                    bool afterDriver = round != null && PrinterDriver.Read(printer, round).Collate;
                    results.Add((want, mode, back.Collation, publicCollate, afterDriver));
                    converter.Dispose();
                }
                Console.WriteLine($"{printer}: the driver says it supports Collation = [{string.Join(", ", queue.GetPrintCapabilities().CollationCapability)}], duplex = [{string.Join(", ", queue.GetPrintCapabilities().DuplexingCapability)}]");
                int diff = 0;
                var a = results[0].Mode; var b = results[1].Mode;
                for (int i = 0; i < Math.Min(a.Length, b.Length); i++) if (a[i] != b[i]) diff++;
                Console.WriteLine($"{printer}: bytes {a.Length}/{b.Length}, bytes that differ between Uncollated and Collated = {diff}");
                foreach (var r in results)
                    Console.WriteLine($"   want {r.Want}: ticket back = {r.Ticket}, devmode collate field = {r.PublicCollate}, after the driver's own merge = {r.AfterDriver}");
            }
            catch (Exception ex) { Console.WriteLine($"{printer}: error {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}

internal static partial class Program
{
    /// <summary>
    /// Manual research: which byte of the driver's private DEVMODE holds Collate? Every byte of the private part is changed (a few values each) and handed to the driver without a dialog; the byte that
    /// changes what the driver reports as Collate is the one. Result in results/collate-bytes-PRINTER.txt.
    /// </summary>
    static void TestFindCollateByte()
    {
        string name = Environment.GetEnvironmentVariable("XT_PRINTER") ?? "LBP8630";
        var def = PrinterDriver.GetDefault(name)!;
        int dmSize = BitConverter.ToUInt16(def, 68), extra = BitConverter.ToUInt16(def, 70);
        bool baseline = PrinterDriver.Read(name, PrinterDriver.Normalize(name, def)!).Collate;
        Console.WriteLine($"{name}: devmode {def.Length} bytes, public {dmSize}, private {extra}; the driver reports Collate={baseline} for its default");
        var found = new List<string>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int limit = int.TryParse(Environment.GetEnvironmentVariable("XT_LIMIT"), out int l) ? Math.Min(def.Length, dmSize + l) : def.Length;
        int start = int.TryParse(Environment.GetEnvironmentVariable("XT_START"), out int st) ? dmSize + st : dmSize;
        var baseRound = PrinterDriver.Normalize(name, def)!;
        for (int i = start; i < limit; i++)
        {
            foreach (int variant in (Environment.GetEnvironmentVariable("XT_FAST") == "1" ? new[] { -1, 1, 0 } : new[] { -1, 0, 1, 2, 0x80, 0xFF }))
            {
                var copy = (byte[])def.Clone();
                copy[i] = variant switch { -1 => (byte)(def[i] ^ 1), _ => (byte)variant };
                if (copy[i] == def[i]) continue;
                var round = PrinterDriver.Normalize(name, copy);
                if (round == null) continue;
                bool collate = PrinterDriver.Read(name, round).Collate;
                if (collate != baseline) { int changed = 0; for (int k = 0; k < round.Length && k < baseRound.Length; k++) if (round[k] != baseRound[k]) changed++; string hit = $"offset {i} (private +{i - dmSize}): {def[i]:X2} -> {copy[i]:X2} makes Collate={collate}; bytes of the driver result that differ from the default result: {changed}"; found.Add(hit); Console.WriteLine(hit); break; }
            }
            if (i % 500 == 0) Console.WriteLine($"  ...{i - dmSize}/{extra} bytes tried, {watch.Elapsed.TotalSeconds:0}s, found {found.Count}");
        }
        Directory.CreateDirectory(Output);
        File.WriteAllLines(System.IO.Path.Combine(Output, "collate-bytes-" + name + ".txt"), found.Prepend($"{name} default Collate={baseline}"));
        Console.WriteLine(found.Count == 0 ? "no single byte changes Collate" : string.Join("\n", found));
    }
}

internal static partial class Program
{
    /// <summary>With a real driver: Collate written by WithSettings is what the driver reports back (the Canon drivers through their private byte).</summary>
    static void TestCollateThroughDriver()
    {
        foreach (string printer in new[] { "LBP8630", "LBP9600C", "RICOH MP 6503 PCL 6", "LBP8780" })
        {
            if (!PrinterSettings.InstalledPrinters.Cast<string>().Contains(printer)) continue;
            bool honors = PrinterDriver.HonorsCollate(printer);
            var def = PrinterDriver.GetDefault(printer)!;
            var results = new List<string>();
            foreach (bool want in new[] { false, true, false })
            {
                var made = PrinterDriver.WithSettings(printer, def, want, 1, null)!;
                var round = PrinterDriver.Normalize(printer, made)!;
                results.Add($"want {want} -> driver says {PrinterDriver.Read(printer, round).Collate}");
            }
            Console.WriteLine($"{printer}: HonorsCollate={honors}; {string.Join("; ", results)}");
        }
    }
}
