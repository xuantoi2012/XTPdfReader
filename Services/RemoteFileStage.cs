using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XTPdfMergeApp.Services;

/// <summary>
/// A PDF on a network drive is copied once to a local temp file, and the MuPDF worker reads that copy.
/// MuPDF reads a file by many small random reads (every page of a 277-page drawing set has its own size to look up); on a slow share one such
/// "metadata" request on a 162 MB file took 35 s, past the 30 s limit of the worker command, so the worker was killed and the file opened as an empty tab.
/// One sequential copy is a single pass over the wire; after it every read is local. The progress shows in the status bar ("Opening file").
/// The name of the copy carries the length and the time of the original, so a changed file is copied again; the original is never touched.
/// </summary>
internal static class RemoteFileStage
{
    private static readonly ConcurrentDictionary<string, Task<string>> Copies = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (long Done, long Total)> Progress = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> Ready = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> RemoteRoots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> Failed = new(StringComparer.OrdinalIgnoreCase);
    private static int _cleaned;

    private static string Folder => Path.Combine(Path.GetTempPath(), "PDFReaderPro", "remote");

    /// <summary>A path on a network drive (UNC or a mapped letter).</summary>
    public static bool IsRemote(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) && !full.StartsWith(@"\\?\", StringComparison.Ordinal)) return true;
            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return false;
            return RemoteRoots.GetOrAdd(root, r => new DriveInfo(r).DriveType == DriveType.Network);
        }
        catch { return false; }
    }

    /// <summary>The path the worker should open: the original for a local file, the local copy for a network file (copied now if needed).</summary>
    public static Task<string> ResolveAsync(string path, CancellationToken token = default)
    {
        if (!IsRemote(path)) return Task.FromResult(path);
        string full;
        FileInfo info;
        try { full = Path.GetFullPath(path); info = new FileInfo(full); if (!info.Exists) return Task.FromResult(path); }
        catch { return Task.FromResult(path); }

        string key = full + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        if (Ready.TryGetValue(key, out var done) && File.Exists(done)) return Task.FromResult(done);
        if (Failed.ContainsKey(key)) return Task.FromResult(path);
        var task = Copies.GetOrAdd(key, _ => CopyAsync(full, key, info));
        return WaitAsync(task, path, token);
    }

    private static async Task<string> WaitAsync(Task<string> task, string fallback, CancellationToken token)
    {
        try { return await task.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return fallback; } // no room / no access: read the file where it is, as before
    }

    /// <summary>The copy of this file if it is already made (never starts one): used by the calls that must name the document the worker holds.</summary>
    public static string Mapped(string path)
    {
        try
        {
            if (!IsRemote(path)) return path;
            string full = Path.GetFullPath(path);
            var info = new FileInfo(full);
            return info.Exists && Ready.TryGetValue(full + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks, out var copy) && File.Exists(copy) ? copy : path;
        }
        catch { return path; }
    }

    /// <summary>How much of the file has come across (0..1) while it is being copied; null when no copy is running.</summary>
    public static double? Fraction(string normalizedPath)
    {
        foreach (var (key, value) in Progress)
            if (key.StartsWith(normalizedPath + "|", StringComparison.OrdinalIgnoreCase))
                return value.Total <= 0 ? 0 : Math.Clamp((double)value.Done / value.Total, 0, 1);
        return null;
    }

    private static async Task<string> CopyAsync(string full, string key, FileInfo info)
    {
        await Task.Yield();
        Directory.CreateDirectory(Folder);
        CleanOld();
        long needed = info.Length + info.Length / 5 + 64L * 1024 * 1024;
        var drive = new DriveInfo(Path.GetPathRoot(Folder)!);
        if (drive.AvailableFreeSpace < needed) throw new IOException("Not enough room for a local copy.");

        string name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..24] + ".pdf";
        string target = Path.Combine(Folder, name), part = target + ".part";
        if (File.Exists(target) && new FileInfo(target).Length == info.Length)
        {
            Ready[key] = target;
            return target;
        }
        Progress[key] = (0, info.Length);
        try
        {
            await using (var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
            {
                var buffer = new byte[4 << 20];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    total += read;
                    Progress[key] = (total, info.Length);
                }
            }
            if (new FileInfo(part).Length != info.Length) throw new IOException("The copy is incomplete.");
            if (File.Exists(target)) File.Delete(target);
            File.Move(part, target);
            Ready[key] = target;
            return target;
        }
        catch
        {
            try { File.Delete(part); } catch { }
            Failed[key] = true; // read the file where it is from now on (as before this copy existed)
            throw;
        }
        finally { Progress.TryRemove(key, out _); }
    }

    /// <summary>Copies of files not used for two days are removed (once per run).</summary>
    private static void CleanOld()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) == 1) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(Folder))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-2)) { try { File.Delete(file); } catch { } }
        }
        catch { }
    }
}
