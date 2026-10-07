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

    internal static string DefaultFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTPdfReader", "Printed");

    private static string? _folder;

    /// <summary>The folder in use: the Settings choice, else the default. (Tests set it directly.)</summary>
    internal static string Folder
    {
        get => _folder ?? (AppSettings.PrintedFolder is { Length: > 0 } custom ? custom : DefaultFolder);
        set => _folder = value;
    }

    /// <summary>The print agent is a separate program: it reads the chosen folder from this small file when it starts (and none = the default).</summary>
    internal static string ConfigFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTPdfReader", "printed-folder.txt");

    /// <summary>Writes (or removes) <see cref="ConfigFile"/> after the folder setting changed. A running agent keeps its old folder until it restarts.</summary>
    internal static void PublishFolder()
    {
        try
        {
            string custom = AppSettings.PrintedFolder;
            if (custom.Length == 0) { File.Delete(ConfigFile); return; }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ConfigFile)!);
            File.WriteAllText(ConfigFile, custom);
        }
        catch { /* the setting still applies to the Reader's own clean-up */ }
    }

    /// <summary>Printed PDFs that are not in <paramref name="inUse"/> (inbox pages, open tabs, merge windows) and were written at least
    /// <paramref name="olderThanDays"/> days ago (0 = any age).</summary>
    internal static List<Candidate> FindUnused(IEnumerable<string> inUse, int olderThanDays = 0, DateTime? now = null)
    {
        var cutoff = (now ?? DateTime.UtcNow).AddDays(-Math.Max(0, olderThanDays));
        if (!Directory.Exists(Folder)) return [];
        var used = new HashSet<string>(inUse.Select(SafeFullPath).Where(p => p.Length > 0), StringComparer.OrdinalIgnoreCase);
        var result = new List<Candidate>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(Folder, "*.pdf", SearchOption.AllDirectories))
            {
                if (used.Contains(file)) continue;
                try
                {
                    var info = new FileInfo(file);
                    if (olderThanDays > 0 && info.LastWriteTimeUtc > cutoff) continue;
                    result.Add(new Candidate(file, info.Length));
                }
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
