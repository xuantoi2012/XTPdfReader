using System.Diagnostics;
using System.IO;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Services.TextEdit;

internal static partial class Program
{
    /// <summary>Not part of the suite: times the everyday edits on a big real drawing set. Run with --test ProfileBigFile and XTPDF_BIG=path to a copy of the file.</summary>
    static void ProfileBigFile()
    {
        string? big = Environment.GetEnvironmentVariable("XTPDF_BIG");
        if (big == null || !File.Exists(big)) { Console.WriteLine("ProfileBigFile: set XTPDF_BIG to a copy of a big PDF"); return; }
        Console.WriteLine($"PROFILE file {new FileInfo(big).Length / 1048576} MB");
        RunReaderFlow("profile-big", async f =>
        {
            var sw = Stopwatch.StartNew();
            string path = f.Path;
            var host = (XTPdfMergeApp.IReaderPageEditHost)f.Window.Session;
            void Lap(string what) { Console.WriteLine($"PROFILE {what}: {sw.ElapsedMilliseconds} ms"); sw.Restart(); }
            Lap("open + first page shown");
            var page1 = await AnnotationStore.GetPageAsync(path, 1);
            Lap($"read annotations of page 1 ({page1?.Annotations.Count})");
            var page271 = await AnnotationStore.GetPageAsync(path, 271);
            Lap($"read annotations of page 271 ({page271?.Annotations.Count})");
            await f.AddAsync(RectSpec("prof-a", 0.2, 0.2, 0.3, 0.25), "add rectangle");
            Lap("add a rectangle on page 1 (apply + redraw)");
            await f.AddAsync(RectSpec("prof-b", 0.4, 0.2, 0.5, 0.25), "add rectangle");
            Lap("add a second rectangle");
            var spec = new QuickAnnotationSpec("prof-note", QuickAnnotationKind.Comment, 1, 0.6, 0.3, 0.62, 0.32, "a note");
            await ((XTPdfMergeApp.IReaderPageEditHost)f.Window.Session).ApplyAnnotationChangesAsync(path, new[] { new QuickAnnotationChange(null, spec) }, "note");
            await Task.Delay(50);
            Lap("add a note");
            var runs = await TextEditService.GetRunsAsync(path, 271);
            Lap($"text runs of page 271 ({runs?.Runs.Count})");
            var found = await ObjectEditService.FindInAreaAsync(path, new[] { 271 }, 0.6, 0.88, 1.0, 1.0, false);
            Lap($"find objects in the title block of page 271 ({found.Count})");
            var many = await ObjectEditService.FindInAreaAsync(path, Enumerable.Range(1, 60).ToList(), 0.6, 0.88, 1.0, 1.0, false);
            Lap($"find objects in the title block of 60 pages ({many.Count})");
            await host.ApplyObjectDeleteAsync(path, found.Take(20).ToList(), "delete");
            await Task.Delay(50);
            Lap("mark 20 objects for removal");
            var group = f.Window.Session.Documents[0];
            await host.SaveGroupAsync(group, saveAs: false);
            Lap("Save (2 rectangles, a note and 20 objects)");
        }, copyFrom: big);
    }
}
