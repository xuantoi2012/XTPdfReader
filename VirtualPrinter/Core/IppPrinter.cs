namespace XTReader.Printing;

public sealed class IppPrinter(JobStore store, IDocumentConverter converter, string uri, long maxBytes = 256L * 1024 * 1024)
{
    private readonly SemaphoreSlim receiving = new(4);
    public string InputPath(int id) => Path.Combine(store.Root, $"{id}.input");
    public async Task<byte[]> HandleAsync(IppRequest request, Stream document, string sid, string userName, CancellationToken token)
    {
        using var response = new IppResponse(request.RequestId, major: request.Major, minor: request.Minor);
        int id = request.Integer("job-id", 0);
        if (id == 0 && Uri.TryCreate(request.Text("job-uri"), UriKind.Absolute, out var jobUri))
        { if (!jobUri.AbsoluteUri.StartsWith(uri + "/jobs/", StringComparison.Ordinal)) throw new IppException(0x0406, "Unknown job URI"); int.TryParse(jobUri.Segments.Last(), out id); }
        switch (request.Operation)
        {
            case 11: Capabilities(response); break;
            case 4: Validate(request); break;
            case 2:
            case 5:
                Validate(request);
                var job = store.Create(sid, userName, request.Text("job-name", "Document"), request.Text("document-format", request.Operation == 5 ? "application/octet-stream" : "application/pdf"), request.Integer("copies", 1));
                if (request.Operation == 2)
                {
                    try { await ReceiveAsync(job, document, token); }
                    catch { store.Update(job.Id, j => j.State >= 7 ? j : j with { State = 8, Reason = "job-data-error" }); throw; }
                }
                JobAttributes(response, store.Get(job.Id, sid)); break;
            case 6:
                var target = store.Get(id, sid);
                if (request.Find("last-document") is not { Tag: 0x22, Value.Length: 1 } last || last.Value[0] != 1)
                    throw new IppException(0x040B, "Only single-document jobs are supported");
                string format = request.Text("document-format", target.Format == "application/octet-stream" ? "application/pdf" : target.Format);
                if (!converter.Formats.Contains(format)) throw new IppException(0x040A, "Unsupported document format");
                if (target.Format != "application/octet-stream" && format != target.Format) throw new IppException(0x040A, "Document format differs from Create-Job");
                Validate(request);
                if (request.Find("copies") != null && request.Integer("copies", 1) != target.Copies) throw new IppException(0x040B, "Copies belong to Create-Job");
                target = target with { Format = format };
                await ReceiveAsync(target, document, token);
                JobAttributes(response, store.Get(id, sid)); break;
            case 8: store.Cancel(id, sid); break;
            case 9: JobAttributes(response, store.Get(id, sid)); break;
            case 10:
                string which = request.Text("which-jobs", "not-completed");
                if (which is not ("all" or "completed" or "not-completed")) throw new IppException(0x040B, "Unsupported which-jobs");
                foreach (var item in store.List(sid).Where(j => which == "all" || (which == "completed" ? j.State >= 7 : j.State < 7)).Take(Math.Clamp(request.Integer("limit", 100), 1, 500))) JobAttributes(response, item);
                break;
            default: throw new IppException(0x0501, "Operation not supported");
        }
        return response.Finish();
    }
    private void Validate(IppRequest request)
    {
        if ((request.Operation != 5 || request.Find("document-format") != null) && !converter.Formats.Contains(request.Text("document-format", "application/pdf"))) throw new IppException(0x040A, "Raster or unsupported document format");
        if (request.Integer("copies", 1) is < 1 or > 100) throw new IppException(0x040B, "Copies must be 1..100");
        if (request.Text("compression", "none") != "none") throw new IppException(0x040B, "Compressed transport unsupported");
        if (request.Find("job-name") is { } title && (title.Value.Length > 255 || title.Text.Any(char.IsControl))) throw new IppException(0x0400, "Invalid job-name");
        // Reject options the converter cannot implement; never silently print the wrong range/color.
        foreach (string name in new[] { "page-ranges", "number-up", "orientation-requested", "print-color-mode", "sides" })
        {
            var attribute = request.Find(name);
            if (attribute == null) continue;
            if (name == "number-up" && attribute.Integer == 1 || name == "sides" && attribute.Text == "one-sided" || name == "print-color-mode" && attribute.Text is "color" or "auto") continue;
            throw new IppException(0x040B, $"Unsupported print option: {name}");
        }
    }
    private async Task ReceiveAsync(PrintJob job, Stream document, CancellationToken token)
    {
        if (!receiving.Wait(0)) throw new IppException(0x0507, "Upload capacity exceeded; retry later");
        try { await ReceiveCoreAsync(job, document, token); } finally { receiving.Release(); }
    }
    private async Task ReceiveCoreAsync(PrintJob job, Stream document, CancellationToken token)
    {
        store.Update(job.Id, current => current.State == 4 && current.Reason == "job-data-insufficient"
            ? current with { State = 5, Reason = "job-incoming", Format = job.Format } : throw new IppException(0x0404, "Job already has a document"));
        string stage = InputPath(job.Id) + ".part";
        try
        {
            using (var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            { await Spooling.CopyLimitedAsync(document, file, maxBytes, token); file.Flush(true); }
            store.Update(job.Id, current =>
            {
                if (current.State == 7) throw new IppException(0x0404, "Job canceled");
                File.Move(stage, InputPath(job.Id));
                return current with { State = 4, Reason = "job-queued" };
            });
        }
        catch
        {
            if (File.Exists(stage)) File.Delete(stage);
            store.Update(job.Id, current => current.State == 7 ? current : current with { State = 8, Reason = "job-data-error" }); throw;
        }
    }
    public void JobAttributes(IppResponse response, PrintJob job)
    {
        response.Group(2).Integer(0x21, "job-id", job.Id).Text(0x45, "job-uri", uri + "/jobs/" + job.Id)
            .Text(0x42, "job-name", job.Title).Text(0x42, "job-originating-user-name", job.OwnerName)
            .Integer(0x23, "job-state", job.State).Text(0x44, "job-state-reasons", job.Reason)
            .Integer(0x21, "copies", job.Copies).Text(0x49, "document-format", job.Format);
    }
    private void Capabilities(IppResponse r)
    {
        r.Group(4).Text(0x45, "printer-uri-supported", uri).Text(0x44, "uri-authentication-supported", "negotiate").Text(0x44, "uri-security-supported", "none")
            .Text(0x42, "printer-name", "XT Reader").Text(0x41, "printer-make-and-model", "XT Reader Vector IPP")
            .Integer(0x23, "printer-state", 3).Text(0x44, "printer-state-reasons", "none").Boolean("printer-is-accepting-jobs", true)
            .Integer(0x21, "queued-job-count", store.Snapshot().Count(j => j.State < 7))
            .Text(0x44, "ipp-versions-supported", "1.1").Text(0x44, "", "2.0")
            .Text(0x47, "charset-configured", "utf-8").Text(0x47, "charset-supported", "utf-8")
            .Text(0x48, "natural-language-configured", "en").Text(0x48, "generated-natural-language-supported", "en");
        foreach (int operation in new[] { 2, 4, 5, 6, 8, 9, 10, 11 }) r.Integer(0x23, operation == 2 ? "operations-supported" : "", operation);
        for (int i = 0; i < converter.Formats.Count; i++) r.Text(0x49, i == 0 ? "document-format-supported" : "", converter.Formats[i]);
        r.Text(0x49, "document-format-default", "application/pdf").Boolean("multiple-document-jobs-supported", false)
            .Text(0x44, "sides-supported", "one-sided").Text(0x44, "sides-default", "one-sided")
            .Text(0x44, "print-color-mode-supported", "color").Text(0x44, "print-color-mode-default", "color").Boolean("color-supported", true)
            .Text(0x44, "media-supported", "iso_a4_210x297mm").Text(0x44, "", "na_letter_8.5x11in").Text(0x44, "media-default", "iso_a4_210x297mm")
            .Integer(0x21, "copies-default", 1).Value(0x33, "copies-supported", [0,0,0,1,0,0,0,100])
            .Value(0x32, "printer-resolution-supported", [0,0,2,88,0,0,2,88,3]).Value(0x32, "printer-resolution-default", [0,0,2,88,0,0,2,88,3]);
    }
}
