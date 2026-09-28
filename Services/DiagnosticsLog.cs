using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace XTPdfMergeApp.Services;

/// <summary>
/// Ghi log debug ra file để đọc mà không cần mở cửa sổ Debug (F12). Chỉ chạy khi đặt biến môi trường
/// <c>XTPDF_DEBUG_LOG</c> (=1 → %TEMP%\XTPdfMergeApp_debug.log, hoặc là đường dẫn file). Mỗi lần mở app ghi đè file cũ.
/// Nội dung: dòng sự kiện chậm (parse trang, chờ gate, khối đọc gấp) kèm mốc thời gian từ lúc mở app,
/// và ảnh chụp toàn bộ báo cáo Debug mỗi 3 giây + lúc thoát.
/// </summary>
internal static class DiagnosticsLog
{
    private const double SlowMs = 300;

    private static readonly string? Path = ResolvePath();
    private static readonly object Gate = new();
    private static readonly long Start = Stopwatch.GetTimestamp();

    public static bool Enabled => Path != null;

    private static string? ResolvePath()
    {
        string? v = Environment.GetEnvironmentVariable("XTPDF_DEBUG_LOG");
        if (string.IsNullOrWhiteSpace(v) || v == "0") return null;
        return v == "1" ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "XTPdfMergeApp_debug.log") : v;
    }

    /// <summary>Tạo/ghi đè file log; trả về false nếu log tắt hoặc không ghi được.</summary>
    public static bool Begin()
    {
        if (Path == null) return false;
        try { File.WriteAllText(Path, $"XTPdfMergeApp debug log, bắt đầu {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}", Encoding.UTF8); return true; }
        catch { return false; }
    }

    public static int SnapshotSeconds => 3;

    public static void Snapshot(string label, string report)
    {
        if (Path == null) return;
        Write($"===== {label} @{Elapsed():0.0}s ====={Environment.NewLine}{report}");
    }

    /// <summary>Sự kiện chậm: chỉ ghi khi <paramref name="ms"/> ≥ 300 ms.</summary>
    public static void Slow(string what, double ms, string detail)
    {
        if (Path == null || ms < SlowMs) return;
        Write($"@{Elapsed():0.00}s  CHẬM {what} {ms:0} ms  {detail}");
    }

    public static void Event(string text)
    {
        if (Path == null) return;
        Write($"@{Elapsed():0.00}s  {text}");
    }

    private static double Elapsed() => (Stopwatch.GetTimestamp() - Start) / (double)Stopwatch.Frequency;

    private static void Write(string line)
    {
        try { lock (Gate) File.AppendAllText(Path!, line + Environment.NewLine, Encoding.UTF8); }
        catch { /* best-effort */ }
    }
}
