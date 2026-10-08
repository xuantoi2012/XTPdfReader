using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace XTCapture
{
    /// <summary>One XT Capture per user: a second start (the Reader's Capture button, a shortcut) passes its command to the running one through this pipe and exits.</summary>
    internal static class InstanceChannel
    {
        public static string PipeName { get; set; } = "XTCapture_Command_" + new string(Environment.UserName.Where(char.IsLetterOrDigit).ToArray());

        /// <summary>Sends <paramref name="command"/> ("capture", "store", "settings", "exit") to the running instance; false when none answers.</summary>
        public static bool Send(string command, int timeoutMs = 1500)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(timeoutMs);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(command);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Calls <paramref name="handler"/> (on a pool thread) for every command until the returned source is cancelled.</summary>
        public static CancellationTokenSource Listen(Action<string> handler)
        {
            var cancel = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                while (!cancel.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                        await server.WaitForConnectionAsync(cancel.Token).ConfigureAwait(false);
                        using var reader = new StreamReader(server);
                        string? line = await reader.ReadLineAsync(cancel.Token).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(line)) handler(line.Trim().ToLowerInvariant());
                    }
                    catch (OperationCanceledException) { break; }
                    catch { try { await Task.Delay(500, cancel.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; } }
                }
            });
            return cancel;
        }
    }
}
