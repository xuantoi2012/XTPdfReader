using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace XTPdfMergeApp.Services;

/// <summary>Prepare every PDF before committing. Retain backups if rollback itself fails.</summary>
internal static class PdfFileTransaction
{
    private sealed record Entry(string Target, string Stage, string Backup, bool Existed, long Length, DateTime Stamp);

    public static void Run(IReadOnlyList<string> targets, Action<string, string> prepare)
        => RunAsync(targets, (target, stage) => { prepare(target, stage); return Task.CompletedTask; }).GetAwaiter().GetResult();

    public static async Task RunAsync(IReadOnlyList<string> targets, Func<string, string, Task> prepare)
    {
        var entries = new List<Entry>();
        var committed = new List<Entry>();
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string target in targets.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(target);
                string stage = Path.Combine(info.DirectoryName!, "." + info.Name + ".xtwrite." + Guid.NewGuid().ToString("N") + ".tmp");
                var entry = new Entry(target, stage, stage + ".bak", info.Exists, info.Exists ? info.Length : 0,
                    info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue);
                entries.Add(entry);
                await prepare(target, stage).ConfigureAwait(false);
                if (!File.Exists(stage)) throw new IOException("No PDF was prepared for " + Path.GetFileName(target));
            }
            // Check all targets before the first replacement, then again immediately before each replacement.
            foreach (var entry in entries) CheckUnchanged(entry);
            foreach (var entry in entries)
            {
                CheckUnchanged(entry);
                if (entry.Existed) File.Replace(entry.Stage, entry.Target, entry.Backup, ignoreMetadataErrors: true);
                else File.Move(entry.Stage, entry.Target);
                committed.Add(entry);
            }
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<string>();
            foreach (var entry in committed.AsEnumerable().Reverse())
            {
                try
                {
                    if (entry.Existed) File.Move(entry.Backup, entry.Target, overwrite: true);
                    else File.Delete(entry.Target);
                }
                catch (Exception rollback)
                {
                    retained.Add(entry.Backup);
                    rollbackErrors.Add($"{entry.Target}: {rollback.Message}. Original backup: {entry.Backup}");
                }
            }
            if (rollbackErrors.Count > 0) throw new IOException(error.Message + "\nCould not restore all files:\n" + string.Join("\n", rollbackErrors), error);
            throw;
        }
        finally
        {
            foreach (var entry in entries)
            {
                TryDelete(entry.Stage);
                if (!retained.Contains(entry.Backup)) TryDelete(entry.Backup);
            }
        }
    }

    private static void CheckUnchanged(Entry entry)
    {
        var current = new FileInfo(entry.Target);
        if (current.Exists != entry.Existed || current.Exists && (current.Length != entry.Length || current.LastWriteTimeUtc != entry.Stamp))
            throw new IOException("The file changed while the PDF was being prepared: " + entry.Target);
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
