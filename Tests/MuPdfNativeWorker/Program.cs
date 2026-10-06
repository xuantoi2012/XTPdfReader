using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// Single document/list, cloned native contexts; no parallel document access.
int threadCount = args.Length == 0 ? 2 : Math.Clamp(int.Parse(args[0]), 1, 2);
IntPtr session = IntPtr.Zero;
string? identity = null;
var output = Console.OpenStandardOutput();
try
{
    string? line;
    while ((line = Console.ReadLine()) != null)
    {
        try
        {
            using var request = JsonDocument.Parse(line);
            var root = request.RootElement;
            string path = root.GetProperty("path").GetString()!;
            if (root.GetProperty("page").GetInt32() != 0) throw new ArgumentException("Page one only");
            int fullWidth = root.GetProperty("fullWidth").GetInt32();
            int fullHeight = root.GetProperty("fullHeight").GetInt32();
            var rect = root.GetProperty("rect").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            if (rect.Length != 4 || fullWidth <= 0 || fullHeight <= 0 || rect[0] < 0 || rect[1] < 0 ||
                rect[2] <= 0 || rect[3] <= 0 || (long)rect[0] + rect[2] > fullWidth ||
                (long)rect[1] + rect[3] > fullHeight || (long)rect[2] * rect[3] * 3 > 128 * 1024 * 1024)
                throw new ArgumentException("Invalid viewport");
            var file = new FileInfo(path);
            string stamp = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            var watch = Stopwatch.StartNew();
            if (identity != stamp)
            {
                Native.Close(session);
                session = IntPtr.Zero;
                session = Native.Open(path, threadCount);
                if (session == IntPtr.Zero) throw new InvalidOperationException(Native.Error(session));
                identity = stamp;
            }
            double prepareMs = watch.Elapsed.TotalMilliseconds;
            byte[] pixels = new byte[checked(rect[2] * rect[3] * 3)];
            watch.Restart();
            if (Native.Render(session, fullWidth, fullHeight, rect[0], rect[1], rect[2], rect[3], pixels) != 0)
                throw new InvalidOperationException(Native.Error(session));
            double renderMs = watch.Elapsed.TotalMilliseconds;
            WriteHeader(new { width = rect[2], height = rect[3], stride = rect[2] * 3,
                length = pixels.Length, prepareMs, renderMs, threads = threadCount,
                backend = "MuPDF 1.28.2 shared-list native", privateMiB = Process.GetCurrentProcess().PrivateMemorySize64 / 1048576d });
            output.Write(pixels);
            output.Flush();
        }
        catch (Exception ex)
        {
            WriteHeader(new { error = ex.Message });
            output.Flush();
        }
    }
}
finally
{
    Native.Close(session);
}

void WriteHeader(object value)
{
    output.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n"));
}

static class Native
{
    [DllImport("xtmupdfviewport", EntryPoint = "mv_open", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int threads);
    [DllImport("xtmupdfviewport", EntryPoint = "mv_close", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Close(IntPtr session);
    [DllImport("xtmupdfviewport", EntryPoint = "mv_render", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Render(IntPtr session, int fullWidth, int fullHeight,
        int x, int y, int width, int height, [Out] byte[] output);
    [DllImport("xtmupdfviewport", EntryPoint = "mv_error", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ErrorPointer(IntPtr session);
    internal static string Error(IntPtr session) => Marshal.PtrToStringUTF8(ErrorPointer(session)) ?? "Native render failed";
}
