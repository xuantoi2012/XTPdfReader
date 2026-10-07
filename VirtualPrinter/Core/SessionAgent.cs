using System.IO.Pipes;
using System.Security.Principal;

namespace XTReader.Printing;

public interface IReaderSink
{
    Task SendAsync(string path, CancellationToken token);
}

public sealed class SessionAgent(string brokerPipe, string root, IReaderSink reader)
{
    public async Task<bool> PollAsync(CancellationToken token)
    {
        using var pipe = new NamedPipeClientStream(".", brokerPipe, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await pipe.ConnectAsync(1500, token);
        await AgentWire.WriteAsync(pipe, "poll", token);
        var delivery = await AgentWire.ReadAsync<Delivery?>(pipe, token);
        if (delivery == null) return false;
        if (!Guid.TryParseExact(delivery.InstanceId, "N", out _) || delivery.JobId < 1 || delivery.Copies is < 1 or > 100 || delivery.Length is < 1 or > 256L * 1024 * 1024)
            throw new InvalidDataException("Invalid delivery descriptor");
        string folder = Path.Combine(Path.GetFullPath(root), delivery.InstanceId);
        Directory.CreateDirectory(folder);
        string baseName = $"{JobStore.SafeTitle(delivery.Title)}-{delivery.JobId}";
        string received = Path.Combine(folder, baseName + ".received");
        using (var file = new FileStream(received, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
        {
            byte[] buffer = new byte[65536]; long remaining = delivery.Length;
            while (remaining > 0)
            {
                int read = await pipe.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                if (read == 0) throw new EndOfStreamException("Incomplete delivery");
                await file.WriteAsync(buffer.AsMemory(0, read), token); remaining -= read;
            }
            file.Flush(true);
        }
        try
        {
            for (int copy = 1; copy <= delivery.Copies; copy++)
            {
                string output = Path.Combine(folder, baseName + (delivery.Copies > 1 ? $"-copy-{copy}" : "") + ".pdf");
                string marker = output + ".sent";
                if (File.Exists(marker)) continue;
                // Retries never replace a PDF that may already be open in Reader.
                if (!File.Exists(output)) File.Copy(received, output);
                await reader.SendAsync(output, token);
                using var receipt = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                receipt.WriteByte(1); receipt.Flush(true);
            }
            await AgentWire.WriteAsync(pipe, delivery.JobId, token);
            return true;
        }
        finally { if (File.Exists(received)) File.Delete(received); }
    }
}
