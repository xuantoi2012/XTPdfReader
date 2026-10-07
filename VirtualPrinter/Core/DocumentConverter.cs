using System.Diagnostics;
using System.Text;

namespace XTReader.Printing;

public interface IDocumentConverter
{
    IReadOnlyList<string> Formats { get; }
    Task ConvertAsync(string input, string output, string format, string title, string author, CancellationToken token);
}

public sealed class MuPdfConverter(string python, string script) : IDocumentConverter
{
    public IReadOnlyList<string> Formats { get; } = ["application/pdf", "application/oxps", "application/vnd.ms-xpsdocument"];
    public async Task ConvertAsync(string input, string output, string format, string title, string author, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Path.GetFullPath(python)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "-I", Path.GetFullPath(script), input, output, format, title, author }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start converter");
        try { ConverterJob.Assign(process); } catch { if (!process.HasExited) process.Kill(true); throw; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(55));
        Task<string> error = ReadBoundedAsync(process.StandardError, token), outputLog = ReadBoundedAsync(process.StandardOutput, token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); throw; }
        if (process.ExitCode != 0) throw new InvalidDataException("Conversion failed: " + (await error)[..Math.Min(1000, (await error).Length)]);
        await outputLog;
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var captured = new StringBuilder(); char[] buffer = new char[2048]; int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
            if (captured.Length < 32768) captured.Append(buffer, 0, Math.Min(count, 32768 - captured.Length));
        return captured.ToString();
    }
}

public static class Spooling
{
    public static async Task CopyLimitedAsync(Stream input, Stream output, long maximum, CancellationToken token)
    {
        byte[] buffer = new byte[65536]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        { total += count; if (total > maximum) throw new IppException(0x0408, "Document exceeds size limit"); await output.WriteAsync(buffer.AsMemory(0, count), token); }
        if (total == 0) throw new IppException(0x0400, "Empty document");
    }
}
