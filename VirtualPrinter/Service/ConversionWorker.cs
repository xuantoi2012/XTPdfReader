using XTReader.Printing;

namespace XTReader.Printing.Service;

public sealed class ConversionWorker(JobStore store, IppPrinter printer, IDocumentConverter converter, ILogger<ConversionWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var expired in store.Snapshot().Where(j => j.State == 4 && j.Reason == "job-data-insufficient" && DateTimeOffset.UtcNow - j.Created > TimeSpan.FromHours(1)))
                store.Update(expired.Id, j => j.State == 4 && j.Reason == "job-data-insufficient" ? j with { State = 8, Reason = "job-data-timeout" } : j);
            var candidates = store.Snapshot().Where(j => j.State == 4 && j.Reason is "job-queued" or "job-restarted").Take(2).ToArray();
            await Task.WhenAll(candidates.Select(job => ConvertAsync(job, stoppingToken)));
            await Task.Delay(250, stoppingToken);
        }
    }
    private async Task ConvertAsync(PrintJob job, CancellationToken token)
    {
        string output = Path.Combine(store.Root, $"{JobStore.SafeTitle(job.Title)}-{job.Id}.pdf"), stage = output + ".part";
        using var conversion = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            var current = store.Update(job.Id, j => j.State == 4 ? j with { State = 5, Reason = "job-transforming" } : j);
            if (current.State != 5) return;
            store.RegisterConversion(job.Id, conversion);
            if (File.Exists(stage)) File.Delete(stage);
            await converter.ConvertAsync(printer.InputPath(job.Id), stage, job.Format, job.Title, job.OwnerName, conversion.Token);
            var published = store.Update(job.Id, j =>
            {
                if (j.State == 7) return j;
                File.Move(stage, output, true);
                return j with { PdfPath = output, State = 3, Reason = "job-queued" };
            });
            if (published.State == 3) log.LogInformation("Job {JobId} converted for {Owner}; awaiting session agent", job.Id, job.OwnerSid);
        }
        catch (OperationCanceledException) when (conversion.IsCancellationRequested)
        { store.Update(job.Id, j => j.State == 7 ? j : j with { State = 4, Reason = "job-queued" }); }
        catch (Exception error)
        { store.Update(job.Id, j => j.State == 7 ? j : j with { State = 8, Reason = "document-format-error" }); log.LogError(error, "Job {JobId} conversion failed", job.Id); }
        finally { store.EndConversion(job.Id); if (File.Exists(stage)) File.Delete(stage); }
    }
}
