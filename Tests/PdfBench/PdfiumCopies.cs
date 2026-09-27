using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using XTPdfMergeApp.Services;

/// <summary>
/// Bảng hàm PDFium của 1 bản thư viện đã nạp. Mỗi bản sao file pdfium.dll (libpdfium.so) mang TÊN KHÁC được
/// hệ điều hành nạp thành 1 module riêng, có trạng thái toàn cục riêng (bộ đệm font, bộ giải mã ảnh…). Vì vậy
/// N bản sao chạy song song THẬT trên N luồng trong cùng 1 tiến trình, miễn là mỗi document chỉ được dùng
/// bởi đúng bản đã mở nó.
/// </summary>
unsafe sealed class PdfiumApi
{
    public readonly delegate* unmanaged[Cdecl]<void> InitLibrary;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr> LoadCustomDocument;
    public readonly delegate* unmanaged[Cdecl]<void*, nuint, IntPtr, IntPtr> LoadMemDocument64;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, void> CloseDocument;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, int> GetPageCount;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr> LoadPage;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, void> ClosePage;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, double> GetPageWidth;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, double> GetPageHeight;
    public readonly delegate* unmanaged[Cdecl]<int, int, int, IntPtr> BitmapCreate;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, int, int, int, int, uint, int> BitmapFillRect;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr> BitmapGetBuffer;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, void> BitmapDestroy;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, int, int, int, int, void> RenderPageBitmap;

    public string Path { get; }

    PdfiumApi(string path)
    {
        Path = path;
        IntPtr h = NativeLibrary.Load(path);
        IntPtr F(string name) => NativeLibrary.GetExport(h, name);
        InitLibrary = (delegate* unmanaged[Cdecl]<void>)F("FPDF_InitLibrary");
        LoadCustomDocument = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)F("FPDF_LoadCustomDocument");
        LoadMemDocument64 = (delegate* unmanaged[Cdecl]<void*, nuint, IntPtr, IntPtr>)F("FPDF_LoadMemDocument64");
        CloseDocument = (delegate* unmanaged[Cdecl]<IntPtr, void>)F("FPDF_CloseDocument");
        GetPageCount = (delegate* unmanaged[Cdecl]<IntPtr, int>)F("FPDF_GetPageCount");
        LoadPage = (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr>)F("FPDF_LoadPage");
        ClosePage = (delegate* unmanaged[Cdecl]<IntPtr, void>)F("FPDF_ClosePage");
        GetPageWidth = (delegate* unmanaged[Cdecl]<IntPtr, double>)F("FPDF_GetPageWidth");
        GetPageHeight = (delegate* unmanaged[Cdecl]<IntPtr, double>)F("FPDF_GetPageHeight");
        BitmapCreate = (delegate* unmanaged[Cdecl]<int, int, int, IntPtr>)F("FPDFBitmap_Create");
        BitmapFillRect = (delegate* unmanaged[Cdecl]<IntPtr, int, int, int, int, uint, int>)F("FPDFBitmap_FillRect");
        BitmapGetBuffer = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)F("FPDFBitmap_GetBuffer");
        BitmapDestroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)F("FPDFBitmap_Destroy");
        RenderPageBitmap = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, int, int, int, int, void>)F("FPDF_RenderPageBitmap");
        InitLibrary();
    }

    static readonly Lazy<PdfiumApi> _default = new(() => new PdfiumApi(LocateOriginal()));
    /// <summary>Bản gốc — cùng module với các [DllImport("pdfium")] của PdfBench.</summary>
    public static PdfiumApi Default => _default.Value;

    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Lazy<PdfiumApi>> _copies = new();

    /// <summary>Bản sao thứ <paramref name="index"/> (≥ 1), chép sang thư mục tạm với tên riêng. Mỗi bản chỉ
    /// nạp 1 lần/tiến trình: chép đè lên file thư viện đang nạp sẽ làm hỏng code đang chạy (Linux: segfault).</summary>
    public static PdfiumApi LoadCopy(int index)
        => _copies.GetOrAdd(index, i => new Lazy<PdfiumApi>(() => CreateCopy(i))).Value;

    static PdfiumApi CreateCopy(int index)
    {
        string original = LocateOriginal();
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pdfbench-pdfium-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        string ext = System.IO.Path.GetExtension(original);
        string name = OperatingSystem.IsWindows() ? $"pdfium_copy{index}{ext}" : $"libpdfium_copy{index}{ext}";
        string copy = System.IO.Path.Combine(dir, name);
        File.Copy(original, copy, overwrite: true);
        return new PdfiumApi(copy);
    }

    public static void DeleteCopies()
    {
        // Windows không cho xoá DLL đang nạp; để lại thư mục tạm của lần chạy trước là vô hại.
        try { Directory.Delete(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pdfbench-pdfium-{Environment.ProcessId}"), true); } catch { }
    }

    static string LocateOriginal()
    {
        string file = OperatingSystem.IsWindows() ? "pdfium.dll" : OperatingSystem.IsMacOS() ? "libpdfium.dylib" : "libpdfium.so";
        string baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     System.IO.Path.Combine(baseDir, file),
                     System.IO.Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", file),
                     System.IO.Path.Combine(baseDir, "runtimes", (OperatingSystem.IsWindows() ? "win-" : "linux-") +
                         RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), "native", file),
                 })
            if (File.Exists(candidate)) return candidate;
        throw new FileNotFoundException("Không tìm thấy " + file + " trong " + baseDir);
    }
}

/// <summary>Đo thời gian chờ đọc file theo TỪNG LUỒNG (nhiều luồng dùng chung 1 bộ đệm). Số byte do
/// <see cref="CountingBlockFile"/> đếm (chỉ lần đọc thật).</summary>
sealed class ThreadTimedBlockFile(IBlockFile inner) : IBlockFile
{
    [ThreadStatic] public static long Ticks;
    [ThreadStatic] public static long Bytes;

    public int Read(long offset, Span<byte> destination)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int n = inner.Read(offset, destination);
        Ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        return n;
    }

    public void Dispose() => inner.Dispose();
}

sealed class CountingBlockFile(IBlockFile inner) : IBlockFile
{
    public int Read(long offset, Span<byte> destination)
    {
        int n = inner.Read(offset, destination);
        ThreadTimedBlockFile.Bytes += Math.Max(0, n);
        return n;
    }

    public void Dispose() => inner.Dispose();
}

/// <summary>
/// Thử nghiệm (chưa có trong app): khối đang được luồng khác đọc thì CHỜ luồng đó thay vì đọc lại. PdfBlockCache
/// luôn đọc theo offset căn khối nên khoá theo offset là đủ.
/// </summary>
sealed class InFlightDedupBlockFile(IBlockFile inner) : IBlockFile
{
    readonly Dictionary<(long, int), Task<byte[]?>> _inFlight = new();

    public int Read(long offset, Span<byte> destination)
    {
        var key = (offset, destination.Length);
        TaskCompletionSource<byte[]?>? mine = null;
        Task<byte[]?> task;
        lock (_inFlight)
        {
            if (!_inFlight.TryGetValue(key, out task!))
            {
                mine = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlight[key] = task = mine.Task;
            }
        }
        if (mine != null)
        {
            byte[]? data = null;
            try
            {
                var buf = new byte[destination.Length];
                int got = 0;
                while (got < buf.Length) { int n = inner.Read(offset + got, buf.AsSpan(got)); if (n <= 0) break; got += n; }
                if (got > 0) data = got == buf.Length ? buf : buf[..got];
            }
            finally
            {
                lock (_inFlight) _inFlight.Remove(key);
                mine.SetResult(data);
            }
        }
        var result = task.GetAwaiter().GetResult();
        if (result == null) return 0;
        result.CopyTo(destination);
        return result.Length;
    }

    public void Dispose() => inner.Dispose();
}
