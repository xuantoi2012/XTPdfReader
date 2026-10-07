using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;

namespace XTReader.Printing;

public sealed record PrintJob(int Id, string OwnerSid, string OwnerName, string Title, string Format, int Copies,
    int State, string Reason, DateTimeOffset Created, string? PdfPath = null);

public sealed class JobStore
{
    private readonly object gate = new();
    private readonly Dictionary<int, PrintJob> jobs = new();
    private readonly HashSet<int> leased = new();
    private readonly Dictionary<int, CancellationTokenSource> converting = new();
    private int nextId;
    public string Root { get; }
    public string InstanceId { get; }
    public JobStore(string root)
    {
        Root = Path.GetFullPath(root); Directory.CreateDirectory(Root);
        var permissions = new DirectorySecurity();
        permissions.SetAccessRuleProtection(true, false);
        foreach (var owner in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct())
            permissions.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Root).SetAccessControl(permissions);
        string identityFile = Path.Combine(Root, ".identity");
        if (!File.Exists(identityFile)) File.WriteAllText(identityFile, Guid.NewGuid().ToString("N"));
        InstanceId = Guid.Parse(File.ReadAllText(identityFile)).ToString("N");
        foreach (string path in Directory.EnumerateFiles(Root, "*.json"))
        {
            // Fail startup on corrupt durable state rather than silently losing a job.
            var job = JsonSerializer.Deserialize<PrintJob>(File.ReadAllText(path)) ?? throw new InvalidDataException(path);
            nextId = Math.Max(nextId, job.Id);
            if (job.PdfPath != null && !Path.GetFullPath(job.PdfPath).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Job path outside spool root");
            if (job.State == 5)
            {
                if (File.Exists(job.PdfPath)) job = job with { State = 3, Reason = "job-queued" };
                else if (File.Exists(Path.Combine(Root, $"{job.Id}.input"))) job = job with { State = 4, Reason = "job-restarted" };
                else
                {
                    job = job with { State = 8, Reason = "job-data-error" };
                    string partial = Path.Combine(Root, $"{job.Id}.input.part");
                    if (File.Exists(partial)) File.Delete(partial);
                }
            }
            if (job.State == 3 && !File.Exists(job.PdfPath)) job = job with { State = 8, Reason = "document-access-error" };
            jobs.Add(job.Id, job);
        }
    }
    public PrintJob Create(string sid, string owner, string title, string format, int copies)
    {
        lock (gate)
        {
            if (jobs.Values.Count(j => j.State < 7) >= 500) throw new IppException(0x0507, "Queue full");
            var job = new PrintJob(checked(++nextId), sid, owner, title, format, copies, 4, "job-data-insufficient", DateTimeOffset.UtcNow);
            Save(job); return job;
        }
    }
    public PrintJob Get(int id, string sid)
    {
        lock (gate) return jobs.TryGetValue(id, out var job) && job.OwnerSid == sid ? job : throw new IppException(0x0406, "Job not found");
    }
    public IReadOnlyList<PrintJob> List(string sid) { lock (gate) return jobs.Values.Where(j => j.OwnerSid == sid).OrderBy(j => j.Id).ToArray(); }
    public IReadOnlyList<PrintJob> Snapshot() { lock (gate) return jobs.Values.OrderBy(j => j.Id).ToArray(); }
    public PrintJob Update(int id, Func<PrintJob, PrintJob> change) { lock (gate) { var job = change(jobs[id]); Save(job); return job; } }
    public void Cancel(int id, string sid)
    {
        CancellationTokenSource? cancel;
        lock (gate)
        {
            var job = Get(id, sid);
            if (job.State >= 7 || leased.Contains(id)) throw new IppException(0x0404, "Job already terminal or being delivered");
            Save(job with { State = 7, Reason = "job-canceled-by-user" });
            converting.TryGetValue(id, out cancel);
        }
        try { cancel?.Cancel(); } catch (ObjectDisposedException) { /* conversion just finished */ }
    }
    public void RegisterConversion(int id, CancellationTokenSource source)
    { lock (gate) { converting[id] = source; if (jobs[id].State == 7) source.Cancel(); } }
    public void EndConversion(int id) { lock (gate) converting.Remove(id); }
    public PrintJob? Lease(string sid)
    {
        lock (gate)
        {
            var job = jobs.Values.OrderBy(j => j.Id).FirstOrDefault(j => j.OwnerSid == sid && j.State == 3 && j.PdfPath != null && !leased.Contains(j.Id));
            if (job != null) leased.Add(job.Id);
            return job;
        }
    }
    public void Release(int id) { lock (gate) leased.Remove(id); }
    public void Acknowledge(int id, string sid)
    {
        lock (gate)
        {
            var job = Get(id, sid);
            if (!leased.Contains(id)) throw new InvalidOperationException("No active delivery");
            Save(job with { State = 9, Reason = "job-completed-successfully" }); leased.Remove(id);
        }
    }
    private void Save(PrintJob job)
    {
        string target = Path.Combine(Root, $"{job.Id}.json"), temporary = target + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, job); file.Flush(true); }
        File.Move(temporary, target, true); jobs[job.Id] = job;
    }
    public static string SafeTitle(string title)
    {
        string value = new(title.Normalize().Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c).ToArray());
        value = value.Trim(' ', '.'); if (value.Length == 0) value = "Document";
        if (value.Length > 80) value = value[..80];
        // Always append a job id at the call site; reserved device names then cannot be the basename.
        return value;
    }
}
