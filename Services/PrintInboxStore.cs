using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XTPdfMergeApp.Services;

/// <summary>
/// The page list of the Print inbox (the Merge screen's holding window for printed/plotted PDFs), kept across sessions in
/// %LOCALAPPDATA%\XTPdfReader\print-inbox.json. Only (path, page) pairs are stored; the PDFs themselves stay where the producer wrote them.
/// The file is the committed state: it changes when a PDF arrives, when the user removes a file from the inbox and when pages were
/// merged into a new PDF. Dragging pages into a draft window does not touch it, so closing the draft ("Done") gives the pages back.
/// </summary>
internal static class PrintInboxStore
{
    internal sealed record Entry(string Path, int Page);

    private static readonly object Gate = new();

    internal static string FilePath { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTPdfReader", "print-inbox.json");

    /// <summary>Saved pages whose file still exists, in saved order.</summary>
    internal static List<Entry> Load()
    {
        lock (Gate) return ReadAll().Where(e => File.Exists(e.Path)).ToList();
    }

    internal static void Add(IEnumerable<Entry> entries)
    {
        lock (Gate)
        {
            var all = ReadAll();
            var known = new HashSet<(string, int)>(all.Select(e => (e.Path.ToLowerInvariant(), e.Page)));
            foreach (var entry in entries)
                if (known.Add((entry.Path.ToLowerInvariant(), entry.Page))) all.Add(entry);
            WriteAll(all);
        }
    }

    /// <summary>Drop these (path, page) pairs from the committed list.</summary>
    internal static void Remove(IEnumerable<Entry> entries)
    {
        lock (Gate)
        {
            var drop = new HashSet<(string, int)>(entries.Select(e => (e.Path.ToLowerInvariant(), e.Page)));
            var all = ReadAll();
            int before = all.Count;
            all.RemoveAll(e => drop.Contains((e.Path.ToLowerInvariant(), e.Page)));
            if (all.Count != before) WriteAll(all);
        }
    }

    private static List<Entry> ReadAll()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return (JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? [])
                .Where(e => !string.IsNullOrWhiteSpace(e.Path) && e.Page > 0).ToList();
        }
        catch { return []; }
    }

    private static void WriteAll(List<Entry> entries)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch { /* the inbox is a convenience: a failed write must not disturb merging */ }
    }
}
