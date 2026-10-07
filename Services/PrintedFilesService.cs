using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XTPdfMergeApp.Services;

/// <summary>
/// The fixed folder the virtual printer's session agent saves printed PDFs into, and the clean-up of the ones nobody uses any more.
/// The agent (VirtualPrinter/Agent, a separate program) uses the same default path; keep both in step.
/// Only PDFs are removed. The small ".sent" receipts next to them stay: they stop the agent from re-delivering a job whose PDF was cleaned up.
/// </summary>
internal static class PrintedFilesService
{
    internal sealed record Candidate(string Path, long Length);

    internal static string Folder { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTPdfReader", "Printed");

    /// <summary>Printed PDFs that are not in <paramref name="inUse"/> (inbox pages, open tabs, merge windows).</summary>
    internal static List<Candidate> FindUnused(IEnumerable<string> inUse)
    {
        if (!Directory.Exists(Folder)) return [];
        var used = new HashSet<string>(inUse.Select(SafeFullPath).Where(p => p.Length > 0), StringComparer.OrdinalIgnoreCase);
        var result = new List<Candidate>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(Folder, "*.pdf", SearchOption.AllDirectories))
            {
                if (used.Contains(file)) continue;
                try { result.Add(new Candidate(file, new FileInfo(file).Length)); }
                catch { /* vanished meanwhile */ }
            }
        }
        catch { /* folder unreadable: nothing to offer */ }
        return result;
    }

    /// <summary>Send the files to the Recycle Bin (recoverable). Returns how many were removed; files in use by another program are skipped.</summary>
    internal static int Delete(IEnumerable<Candidate> files)
    {
        int removed = 0;
        foreach (var file in files)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                removed++;
            }
            catch { /* locked or already gone */ }
        }
        return removed;
    }

    internal static string FormatSize(long bytes)
        => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    private static string SafeFullPath(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch { return ""; }
    }
}
