using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace XTReader.Printing.Service;

public sealed class BrokerWorker(JobStore store, ILogger<BrokerWorker> log, IConfiguration configuration) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken token) => Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ListenAsync(token)));
    private async Task ListenAsync(CancellationToken token)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        while (!token.IsCancellationRequested)
        {
            int? leasedId = null;
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(configuration["BrokerPipe"] ?? AgentWire.PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
                await pipe.WaitForConnectionAsync(token);
                var computer = new StringBuilder(256);
                bool named = GetNamedPipeClientComputerName(pipe.SafePipeHandle, computer, (uint)computer.Capacity);
                int computerError = Marshal.GetLastWin32Error();
                // Local connections report ERROR_PIPE_LOCAL rather than a remote computer name.
                if (named || computerError != 229)
                    throw new UnauthorizedAccessException("Broker accepts local clients only");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(45));
                string command = await AgentWire.ReadAsync<string>(pipe, deadline.Token);
                if (command != "poll") throw new InvalidDataException("Unknown agent command");
                string? sid = null;
                pipe.RunAsClient(() => { using var identity = WindowsIdentity.GetCurrent(true); if (identity is { IsAnonymous: false, IsSystem: false, IsGuest: false }) sid = identity.User?.Value; });
                if (sid == null) throw new UnauthorizedAccessException("No user identity");
                var job = store.Lease(sid);
                if (job == null) { await AgentWire.WriteAsync<Delivery?>(pipe, null, deadline.Token); continue; }
                leasedId = job.Id;
                using var file = new FileStream(job.PdfPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                await AgentWire.WriteAsync(pipe, new Delivery(store.InstanceId, job.Id, job.Title, job.Copies, file.Length), deadline.Token);
                await file.CopyToAsync(pipe, deadline.Token); await pipe.FlushAsync(deadline.Token);
                int acknowledgedId = await AgentWire.ReadAsync<int>(pipe, deadline.Token);
                if (acknowledgedId != job.Id) throw new InvalidDataException("Wrong acknowledgement");
                store.Acknowledge(job.Id, sid);
                log.LogInformation("Job {JobId} handed to Reader session {Owner}", job.Id, sid);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) { log.LogWarning(error, "Agent delivery deferred"); await Task.Delay(500, token); }
            finally { if (leasedId is { } id) store.Release(id); }
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetNamedPipeClientComputerName(SafePipeHandle pipe, StringBuilder name, uint size);
}
