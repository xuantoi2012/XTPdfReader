using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Word = Microsoft.Office.Interop.Word;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Directory.CreateDirectory(args[1]);
        if (args[0] == "cad-metadata")
        {
            var findings = new List<object>();
            foreach (var version in new[] { "2024", "2027" })
            foreach (var name in new[] { "acmgd.dll", "accoremgd.dll", "acdbmgd.dll" })
            {
                string path = $@"C:\Program Files\Autodesk\AutoCAD {version}\{name}";
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                var reader = pe.GetMetadataReader();
                foreach (var handle in reader.TypeDefinitions)
                {
                    var type = reader.GetTypeDefinition(handle);
                    var typeName = reader.GetString(type.Name);
                    if (typeName != "BeginDocumentEventArgs" && typeName != "PlotReactorManager") continue;
                    findings.Add(new { version, path, typeName,
                        properties = type.GetProperties().Select(h => reader.GetString(reader.GetPropertyDefinition(h).Name)).ToArray(),
                        events = type.GetEvents().Select(h => reader.GetString(reader.GetEventDefinition(h).Name)).ToArray(),
                        constructors = type.GetMethods().Select(h => reader.GetMethodDefinition(h)).Where(m => reader.GetString(m.Name)==".ctor").Select(m => m.GetParameters().Select(h => reader.GetString(reader.GetParameter(h).Name)).ToArray()).ToArray() });
                }
            }
            File.WriteAllText(Path.Combine(args[1], "cad-api-metadata.json"), JsonSerializer.Serialize(findings, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(findings));
            return;
        }
        var app = new Word.Application { Visible = false, DisplayAlerts = Word.WdAlertLevel.wdAlertsNone };
        Word.Document? doc = null;
        var records = new List<object>();
        string originalPrinter = app.ActivePrinter;
        string request = "";
        Word.ApplicationEvents4_DocumentBeforePrintEventHandler handler = (Word.Document document, ref bool cancel) =>
        {
            records.Add(new { request, activePrinter = app.ActivePrinter, document = document.Name, selectionStart = app.Selection.Start, selectionEnd = app.Selection.End,
                parameters = typeof(Word.ApplicationEvents4_DocumentBeforePrintEventHandler).GetMethod("Invoke")!.GetParameters().Select(p => new { p.Name, type = p.ParameterType.FullName }).ToArray() });
            cancel = true;
        };
        ((Word.ApplicationEvents4_Event)app).DocumentBeforePrint += handler;
        try
        {
            doc = app.Documents.Open(args[2], ReadOnly: true, Visible: true);
            app.ActivePrinter = "Microsoft Print to PDF";
            foreach (var (pages, copies) in new[] { ("5-9", 2), ("10-11", 1) })
            {
                request = $"Pages={pages}; Copies={copies}";
                doc.PrintOut(Background: false, Range: Word.WdPrintOutRange.wdPrintRangeOfPages, Pages: pages, Copies: copies, PrintToFile: true, OutputFileName: Path.Combine(args[1], "cancelled-should-not-exist.pdf"));
            }
            File.WriteAllText(Path.Combine(args[1], "word-before-print.json"), JsonSerializer.Serialize(new { app.Version, app.Build, records, cancelledOutputExists = File.Exists(Path.Combine(args[1], "cancelled-should-not-exist.pdf")) }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Events={records.Count}; cancellation output exists={File.Exists(Path.Combine(args[1], "cancelled-should-not-exist.pdf"))}");
            if (records.Count != 2) throw new Exception("Expected two DocumentBeforePrint events");
        }
        finally
        {
            ((Word.ApplicationEvents4_Event)app).DocumentBeforePrint -= handler;
            app.ActivePrinter = originalPrinter;
            doc?.Close(Word.WdSaveOptions.wdDoNotSaveChanges);
            app.Quit(Word.WdSaveOptions.wdDoNotSaveChanges);
            if (doc != null) Marshal.FinalReleaseComObject(doc);
            Marshal.FinalReleaseComObject(app);
        }
    }
}
