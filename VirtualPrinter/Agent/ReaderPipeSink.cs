using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace XTReader.Printing.Agent;

public sealed class ReaderPipeSink(string? configuredReader, string pipeName = "XTPdfMergeApp_IncomingPdfPipe") : IReaderSink
{
    private DateTimeOffset lastStart;
    public async Task SendAsync(string path, CancellationToken token)
    {
        if (path.IndexOfAny(['\r', '\n']) >= 0) throw new InvalidDataException("Unsafe path");
        for (int attempt = 0; attempt < 25; attempt++)
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(500, token);
                VerifyReaderOwner(pipe);
                byte[] line = Encoding.UTF8.GetBytes(Path.GetFullPath(path) + "\n");
                await pipe.WriteAsync(line, token); await pipe.FlushAsync(token);
                return;
            }
            catch (TimeoutException)
            {
                if (DateTimeOffset.UtcNow - lastStart > TimeSpan.FromSeconds(30))
                {
                    string reader = FindReader() ?? throw new FileNotFoundException("Configure --reader=<installed Reader exe> for auto-start");
                    Process.Start(new ProcessStartInfo(reader) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(reader)! })?.Dispose();
                    lastStart = DateTimeOffset.UtcNow;
                }
                await Task.Delay(250, token);
            }
        }
        throw new TimeoutException("Reader did not expose its incoming pipe");
    }
    private string? FindReader()
    {
        if (configuredReader != null) return File.Exists(configuredReader) ? Path.GetFullPath(configuredReader) : null;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\XTPdfMergeApp.exe");
        string? registered = key?.GetValue(null) as string;
        if (registered != null && File.Exists(registered)) return Path.GetFullPath(registered);
        string sibling = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "XTPdfMergeApp.exe"));
        return File.Exists(sibling) ? sibling : null;
    }
    private static void VerifyReaderOwner(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint id)) throw new UnauthorizedAccessException("Cannot identify Reader pipe owner");
        using var server = Process.GetProcessById(checked((int)id));
        using var current = Process.GetCurrentProcess();
        if (server.SessionId != current.SessionId) throw new UnauthorizedAccessException("Reader pipe belongs to another session");
        using SafeProcessHandle process = OpenProcess(0x1000, false, id);
        if (process.IsInvalid || !OpenProcessToken(process, 8, out SafeAccessTokenHandle token)) throw new UnauthorizedAccessException("Cannot query Reader owner");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        using (var user = WindowsIdentity.GetCurrent())
            if (identity.User != user.User) throw new UnauthorizedAccessException("Reader pipe belongs to another user");
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint id);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
