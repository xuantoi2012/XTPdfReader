using System.Net;
using System.Security.Principal;
using Microsoft.AspNetCore.Authentication.Negotiate;
using XTReader.Printing;
using XTReader.Printing.Service;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "XTReaderPrintService");
int port = builder.Configuration.GetValue("Port", 18632);
builder.WebHost.ConfigureKestrel(server =>
{
    server.Listen(IPAddress.Loopback, port, endpoint => endpoint.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
    server.Limits.MaxRequestBodySize = 256L * 1024 * 1024 + 65536;
    server.Limits.MaxConcurrentConnections = 64;
    server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
builder.Services.AddAuthorization();
string root = builder.Configuration["SpoolRoot"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "XTReader", "PrintSpool");
string python = builder.Configuration["Python"] ?? Path.Combine(AppContext.BaseDirectory, "MuPdfRuntime", "python.exe");
if (!File.Exists(python)) throw new FileNotFoundException("Configure Python to the existing Reader MuPDF runtime", python);
builder.Services.AddSingleton(new JobStore(root));
using var spoolLock = new FileStream(Path.Combine(Path.GetFullPath(root), ".service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
builder.Services.AddSingleton<IDocumentConverter>(new MuPdfConverter(python, Path.Combine(AppContext.BaseDirectory, "ConvertDocument.py")));
builder.Services.AddSingleton(s => new IppPrinter(s.GetRequiredService<JobStore>(), s.GetRequiredService<IDocumentConverter>(), $"ipp://127.0.0.1:{port}/ipp/print"));
builder.Services.AddHostedService<ConversionWorker>();
builder.Services.AddHostedService<BrokerWorker>();
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)) { context.Response.StatusCode = 403; return; }
    await next();
});
app.UseAuthentication(); app.UseAuthorization();
app.MapPost("/ipp/print", async (HttpContext context, IppPrinter printer, ILogger<Program> log) =>
{
    IppRequest? request = null;
    byte[] reply;
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); deadline.CancelAfter(TimeSpan.FromMinutes(2));
    if (!context.Request.ContentType?.StartsWith("application/ipp", StringComparison.OrdinalIgnoreCase) ?? true) { context.Response.StatusCode = 415; return; }
    try
    {
        if (context.User.Identity is not WindowsIdentity identity || identity.User == null || identity.IsSystem || identity.IsAnonymous || identity.IsGuest)
        { context.Response.StatusCode = 403; return; }
        request = await IppProtocol.ReadAsync(context.Request.Body, deadline.Token);
        reply = await printer.HandleAsync(request, context.Request.Body, identity.User.Value, identity.Name, deadline.Token);
    }
    catch (IppException error) { using var response = new IppResponse(request?.RequestId ?? 0, error.Status, request?.Major ?? 2, request?.Minor ?? 0); response.Text(0x41, "status-message", error.Message); reply = response.Finish(); }
    catch (Exception error) when (error is EndOfStreamException or InvalidDataException or ArgumentException)
    { using var response = new IppResponse(request?.RequestId ?? 0, 0x0400); response.Text(0x41, "status-message", "Invalid request"); reply = response.Finish(); }
    catch (OperationCanceledException) { context.Abort(); return; }
    catch (Exception error) { log.LogError(error, "IPP request failed"); using var response = new IppResponse(request?.RequestId ?? 0, 0x0500); reply = response.Finish(); }
    context.Response.ContentType = "application/ipp"; await context.Response.Body.WriteAsync(reply, context.RequestAborted);
}).RequireAuthorization();
await app.RunAsync();
