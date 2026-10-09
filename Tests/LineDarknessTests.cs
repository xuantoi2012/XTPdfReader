using System.IO;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    /// <summary>Manual check (needs a real drawing set in Downloads): renders page 9 in the real reader window and saves the picture, to compare the darkness of thin lines.</summary>
    static void TestLineDarkness()
    {
        string? source = Directory.GetFiles(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "03. QUYEN 2.2*.pdf").FirstOrDefault();
        if (source == null) { Console.WriteLine("Line darkness: no sample drawing set; skipped"); return; }
        RunReaderFlow("linedark", async f =>
        {
            f.Window.Width = 1100; f.Window.Height = 800;
            await Task.Delay(1500);
            await (Task)f.Call("NavigateReaderToIndexAsync", 8)!;
            await Task.Delay(6000);
            SavePng(f.Window, "linedark-" + (Environment.GetEnvironmentVariable("XTPDF_MIN_LINE_PX") ?? "default"));
        }, copyFrom: source);
    }
}
