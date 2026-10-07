using XTReader.Printing;
using XTReader.Printing.Agent;

string? Option(string name) => args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..];
string root = Option("--root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTPdfReader", "Printed"); // fixed app folder; the Reader's "Clean up printed files" works on the same path (Services/PrintedFilesService)
var sink = new ReaderPipeSink(Option("--reader"));
var agent = new SessionAgent(Option("--broker") ?? AgentWire.PipeName, root, sink);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
// Avoid two agents concurrently delivering the same user's jobs in one session.
using var mutex = new Mutex(true, "Local\\XTReader_PrintAgent", out bool first);
if (!first) return;
while (!stop.IsCancellationRequested)
{
    try { await agent.PollAsync(stop.Token); }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
    catch (Exception error)
    {
        Directory.CreateDirectory(root);
        await File.AppendAllTextAsync(Path.Combine(root, "agent.log"), $"{DateTimeOffset.UtcNow:O} {error.GetType().Name}: {error.Message}{Environment.NewLine}");
    }
    try { await Task.Delay(1000, stop.Token); } catch (OperationCanceledException) { break; }
}
