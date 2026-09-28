using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace XTPdfMergeApp.Services;

/// <summary>
/// 1 bản thư viện PDFium đã nạp: bảng hàm (con trỏ hàm lấy bằng NativeLibrary) + gate riêng. PDFium giữ trạng
/// thái toàn cục (bộ đệm font, bộ giải mã ảnh…) nên trong 1 bản mọi lệnh phải tuần tự qua <see cref="Gate"/>.
/// Nhiều bản sao file DLL (tên khác nhau) là nhiều module riêng, chạy song song được — xem
/// Tests/PdfBench/BAO-CAO-DA-LUONG-2026-09-27.md. Các bản do <see cref="PdfiumPool"/> quản lý.
///
/// Document, page, bitmap do bản nào tạo thì chỉ được đưa lại cho ĐÚNG bản đó.
/// </summary>
internal sealed unsafe class PdfiumInstance
{
    private readonly delegate* unmanaged[Cdecl]<void> _initLibrary;
    private readonly delegate* unmanaged[Cdecl]<void> _destroyLibrary;
    private readonly delegate* unmanaged[Cdecl]<byte*, byte*, IntPtr> _loadDocument;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr> _loadCustomDocument;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> _closeDocument;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int> _getPageCount;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, double*, double*, int> _getPageSizeByIndex;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr> _loadPage;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> _closePage;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, double> _getPageWidth;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, double> _getPageHeight;
    private readonly delegate* unmanaged[Cdecl]<int, int, int, IntPtr> _bitmapCreate;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> _bitmapDestroy;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, int, int, int, uint, int> _bitmapFillRect;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _bitmapGetBuffer;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int> _bitmapGetStride;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, int, int, int, int, IntPtr, int> _renderPageBitmapStart;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> _renderPageContinue;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> _renderPageClose;
    // Văn bản trang (tìm kiếm)
    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _textLoadPage;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> _textClosePage;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int> _textCountChars;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, int, ushort*, int> _textGetText;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, ushort*, uint, int, IntPtr> _textFindStart;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int> _textFindNext;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int> _textGetSchResultIndex;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int> _textGetSchCount;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> _textFindClose;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, int, int> _textCountRects;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, double, double, double, double, int> _textGetCharIndexAtPos;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, double*, double*, double*, double*, int> _textGetRect;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int> _pageGetRotation;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, float*, float*, float*, float*, int> _pageGetCropBox;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, float*, float*, float*, float*, int> _pageGetMediaBox;

    private readonly Lazy<bool> _initialized;

    private PdfiumInstance(int index, IntPtr library, string name)
    {
        Index = index;
        Name = name;
        IntPtr F(string name) => NativeLibrary.GetExport(library, name);
        _initLibrary = (delegate* unmanaged[Cdecl]<void>)F("FPDF_InitLibrary");
        _destroyLibrary = (delegate* unmanaged[Cdecl]<void>)F("FPDF_DestroyLibrary");
        _loadDocument = (delegate* unmanaged[Cdecl]<byte*, byte*, IntPtr>)F("FPDF_LoadDocument");
        _loadCustomDocument = (delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr>)F("FPDF_LoadCustomDocument");
        _closeDocument = (delegate* unmanaged[Cdecl]<IntPtr, void>)F("FPDF_CloseDocument");
        _getPageCount = (delegate* unmanaged[Cdecl]<IntPtr, int>)F("FPDF_GetPageCount");
        _getPageSizeByIndex = (delegate* unmanaged[Cdecl]<IntPtr, int, double*, double*, int>)F("FPDF_GetPageSizeByIndex");
        _loadPage = (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr>)F("FPDF_LoadPage");
        _closePage = (delegate* unmanaged[Cdecl]<IntPtr, void>)F("FPDF_ClosePage");
        _getPageWidth = (delegate* unmanaged[Cdecl]<IntPtr, double>)F("FPDF_GetPageWidth");
        _getPageHeight = (delegate* unmanaged[Cdecl]<IntPtr, double>)F("FPDF_GetPageHeight");
        _bitmapCreate = (delegate* unmanaged[Cdecl]<int, int, int, IntPtr>)F("FPDFBitmap_Create");
        _bitmapDestroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)F("FPDFBitmap_Destroy");
        _bitmapFillRect = (delegate* unmanaged[Cdecl]<IntPtr, int, int, int, int, uint, int>)F("FPDFBitmap_FillRect");
        _bitmapGetBuffer = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)F("FPDFBitmap_GetBuffer");
        _bitmapGetStride = (delegate* unmanaged[Cdecl]<IntPtr, int>)F("FPDFBitmap_GetStride");
        _renderPageBitmapStart = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, int, int, int, int, IntPtr, int>)F("FPDF_RenderPageBitmap_Start");
        _renderPageContinue = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)F("FPDF_RenderPage_Continue");
        _renderPageClose = (delegate* unmanaged[Cdecl]<IntPtr, void>)F("FPDF_RenderPage_Close");
        _textLoadPage = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)F("FPDFText_LoadPage");
        _textClosePage = (delegate* unmanaged[Cdecl]<IntPtr, void>)F("FPDFText_ClosePage");
        _textCountChars = (delegate* unmanaged[Cdecl]<IntPtr, int>)F("FPDFText_CountChars");
        _textGetText = (delegate* unmanaged[Cdecl]<IntPtr, int, int, ushort*, int>)F("FPDFText_GetText");
        _textFindStart = (delegate* unmanaged[Cdecl]<IntPtr, ushort*, uint, int, IntPtr>)F("FPDFText_FindStart");
        _textFindNext = (delegate* unmanaged[Cdecl]<IntPtr, int>)F("FPDFText_FindNext");
        _textGetSchResultIndex = (delegate* unmanaged[Cdecl]<IntPtr, int>)F("FPDFText_GetSchResultIndex");
        _textGetSchCount = (delegate* unmanaged[Cdecl]<IntPtr, int>)F("FPDFText_GetSchCount");
        _textFindClose = (delegate* unmanaged[Cdecl]<IntPtr, void>)F("FPDFText_FindClose");
        _textCountRects = (delegate* unmanaged[Cdecl]<IntPtr, int, int, int>)F("FPDFText_CountRects");
        _textGetCharIndexAtPos = (delegate* unmanaged[Cdecl]<IntPtr, double, double, double, double, int>)F("FPDFText_GetCharIndexAtPos");
        _textGetRect = (delegate* unmanaged[Cdecl]<IntPtr, int, double*, double*, double*, double*, int>)F("FPDFText_GetRect");
        _pageGetRotation = (delegate* unmanaged[Cdecl]<IntPtr, int>)F("FPDFPage_GetRotation");
        _pageGetCropBox = (delegate* unmanaged[Cdecl]<IntPtr, float*, float*, float*, float*, int>)F("FPDFPage_GetCropBox");
        _pageGetMediaBox = (delegate* unmanaged[Cdecl]<IntPtr, float*, float*, float*, float*, int>)F("FPDFPage_GetMediaBox");

        _initialized = new Lazy<bool>(() =>
        {
            _initLibrary();
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { _destroyLibrary(); }
                catch { }
            };
            return true;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private static readonly Lazy<PdfiumInstance> _primary = new(() =>
        // Cùng cách tìm file như [DllImport("pdfium")] trước đây (deps.json → runtimes/<rid>/native/pdfium.dll).
        new PdfiumInstance(0, NativeLibrary.Load("pdfium", typeof(PdfiumInstance).Assembly, null), "pdfium"),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Bản chính — pdfium.dll gốc.</summary>
    public static PdfiumInstance Primary => _primary.Value;

    /// <summary>Nạp bản sao đã có sẵn trên đĩa (tạo lúc build, xem XTPdfMergeApp.csproj). null = không có/không nạp được.</summary>
    internal static PdfiumInstance? TryLoadCopy(int index, string path)
    {
        try
        {
            return File.Exists(path) ? new PdfiumInstance(index, NativeLibrary.Load(path), Path.GetFileNameWithoutExtension(path)) : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PdfiumPool] Không nạp được {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>0 = bản chính.</summary>
    public int Index { get; }

    private int _load;
    /// <summary>Số việc (render/đếm trang…) đang giao cho bản này, kể cả đang chờ gate — để chia việc.</summary>
    public int Load => Volatile.Read(ref _load);
    internal void AddLoad(int delta) => Interlocked.Add(ref _load, delta);

    private long _completed;
    /// <summary>Tổng số việc bản này đã làm (bảng Debug).</summary>
    public long Completed => Interlocked.Read(ref _completed);
    internal void MarkCompleted() => Interlocked.Increment(ref _completed);

    // ── Số liệu cho bảng Debug (bước 4) ──
    /// <summary>Tên file thư viện (pdfium, pdfium_1…).</summary>
    public string Name { get; }
    /// <summary>Thời gian chờ để vào gate của bản này / thời gian giữ gate (≈ thời gian PDFium thật sự chạy).</summary>
    internal readonly RenderDiagnostics.Timing GateWait = new(), GateHeld = new();
    private long _pagesParsed;
    /// <summary>Số lần FPDF_LoadPage (parse trang) trên bản này.</summary>
    public long PagesParsed => Interlocked.Read(ref _pagesParsed);
    internal void MarkPageParsed() { Interlocked.Increment(ref _pagesParsed); Interlocked.Increment(ref _parsedSinceTrim); }
    private long _parsedSinceTrim;
    /// <summary>Số trang đã parse từ lần document của bản này được mở/thu hồi gần nhất — document giữ cache của chúng.</summary>
    internal long ParsedSinceTrim => Interlocked.Read(ref _parsedSinceTrim);
    internal void ResetParsedSinceTrim() => Interlocked.Exchange(ref _parsedSinceTrim, 0);
    /// <summary>Page handle đang giữ / document đang mở trên bản này (do PdfThumbnailService cập nhật).</summary>
    internal int CachedPages, OpenDocuments;
    private readonly long _createdTimestamp = Stopwatch.GetTimestamp();
    private long _lastSampleTimestamp, _lastSampleHeldTicks;

    /// <summary>Phần trăm thời gian bản này giữ gate: từ lần hỏi trước (bảng Debug làm mới mỗi giây) và cả phiên.</summary>
    internal (double Recent, double Session) SampleBusy()
    {
        long now = Stopwatch.GetTimestamp();
        double heldMs = GateHeld.TotalMilliseconds;
        long heldTicks = (long)(heldMs * Stopwatch.Frequency / 1000);
        long lastTs = Interlocked.Exchange(ref _lastSampleTimestamp, now);
        long lastHeld = Interlocked.Exchange(ref _lastSampleHeldTicks, heldTicks);
        if (lastTs == 0) lastTs = _createdTimestamp;
        double recent = now > lastTs ? (double)(heldTicks - lastHeld) / (now - lastTs) : 0;
        double session = now > _createdTimestamp ? heldMs * Stopwatch.Frequency / 1000 / (now - _createdTimestamp) : 0;
        return (Math.Clamp(recent, 0, 1), Math.Clamp(session, 0, 1));
    }

    /// <summary>Mọi lệnh gọi vào bản PDFium này phải giữ gate (thứ tự ưu tiên Visible/Thumbnail/Background).</summary>
    public PdfRenderGate Gate { get; } = new();

    /// <summary>FPDF_InitLibrary đúng 1 lần (FPDF_DestroyLibrary khi tiến trình thoát).</summary>
    public void EnsureInitialized() => _ = _initialized.Value;

    public IntPtr LoadDocument(string filePath)
    {
        byte[] path = Utf8(filePath);
        fixed (byte* p = path) return _loadDocument(p, null);
    }

    /// <summary>fileAccess = FPDF_FILEACCESS* (xem LayeredDocumentSource).</summary>
    public IntPtr LoadCustomDocument(IntPtr fileAccess) => _loadCustomDocument(fileAccess, null);
    public void CloseDocument(IntPtr document) => _closeDocument(document);
    public int GetPageCount(IntPtr document) => _getPageCount(document);

    public bool GetPageSizeByIndex(IntPtr document, int pageIndex, out double width, out double height)
    {
        double w, h;
        int ok = _getPageSizeByIndex(document, pageIndex, &w, &h);
        width = w;
        height = h;
        return ok != 0;
    }

    public IntPtr LoadPage(IntPtr document, int pageIndex) => _loadPage(document, pageIndex);
    public void ClosePage(IntPtr page) => _closePage(page);
    public double GetPageWidth(IntPtr page) => _getPageWidth(page);
    public double GetPageHeight(IntPtr page) => _getPageHeight(page);

    public IntPtr BitmapCreate(int width, int height, int alpha) => _bitmapCreate(width, height, alpha);
    public void BitmapDestroy(IntPtr bitmap) => _bitmapDestroy(bitmap);
    public bool BitmapFillRect(IntPtr bitmap, int left, int top, int width, int height, uint color)
        => _bitmapFillRect(bitmap, left, top, width, height, color) != 0;
    public IntPtr BitmapGetBuffer(IntPtr bitmap) => _bitmapGetBuffer(bitmap);
    public int BitmapGetStride(IntPtr bitmap) => _bitmapGetStride(bitmap);

    /// <summary>FPDF_RenderPageBitmap_Start — pause = IFSDK_PAUSE*. Trả trạng thái progressive (1 = còn tiếp, 2 = xong).</summary>
    public int RenderPageBitmapStart(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotation, int flags, IntPtr pause)
        => _renderPageBitmapStart(bitmap, page, x, y, width, height, rotation, flags, pause);
    public int RenderPageContinue(IntPtr page, IntPtr pause) => _renderPageContinue(page, pause);
    public void RenderPageClose(IntPtr page) => _renderPageClose(page);

    /// <summary>1 kết quả tìm: vị trí ký tự, đoạn trích quanh nó và các hình chữ nhật (user space: Left, Bottom, Right, Top).</summary>
    internal sealed record TextMatch(int Start, int Length, string Snippet, List<(double L, double B, double R, double T)> Rects);

    /// <summary>Hình học trang (CropBox hoặc MediaBox, /Rotate) để đổi toạ độ user space → toạ độ trang hiển thị.</summary>
    internal PdfPageGeometry GetPageGeometry(IntPtr page)
    {
        float l, b, r, t;
        bool ok = _pageGetCropBox(page, &l, &b, &r, &t) != 0 || _pageGetMediaBox(page, &l, &b, &r, &t) != 0;
        if (!ok || r <= l || t <= b) { l = 0; b = 0; r = (float)GetPageWidth(page); t = (float)GetPageHeight(page); }
        int rotation = ((_pageGetRotation(page) % 4) + 4) % 4 * 90;
        return new PdfPageGeometry(l, b, r - l, t - b, rotation);
    }

    /// <summary>Tìm <paramref name="query"/> trong văn bản của 1 trang (FPDF_MATCHCASE = 1, FPDF_MATCHWHOLEWORD = 2).
    /// <paramref name="charCount"/> = số ký tự của trang (0 = trang không có văn bản tìm được).</summary>
    internal List<TextMatch> FindText(IntPtr page, string query, uint flags, int maxMatches, out int charCount)
    {
        var result = new List<TextMatch>();
        charCount = 0;
        IntPtr text = _textLoadPage(page);
        if (text == IntPtr.Zero) return result;
        try
        {
            charCount = _textCountChars(text);
            if (charCount <= 0 || string.IsNullOrEmpty(query)) return result;
            var pattern = new ushort[query.Length + 1];
            for (int i = 0; i < query.Length; i++) pattern[i] = query[i];
            fixed (ushort* p = pattern)
            {
                IntPtr search = _textFindStart(text, p, flags, 0);
                if (search == IntPtr.Zero) return result;
                try
                {
                    while (result.Count < maxMatches && _textFindNext(search) != 0)
                    {
                        int start = _textGetSchResultIndex(search), length = _textGetSchCount(search);
                        var rects = new List<(double, double, double, double)>();
                        int rectCount = _textCountRects(text, start, length);
                        for (int r = 0; r < rectCount; r++)
                        {
                            double left, top, right, bottom;
                            if (_textGetRect(text, r, &left, &top, &right, &bottom) != 0)
                                rects.Add((left, Math.Min(top, bottom), right, Math.Max(top, bottom)));
                        }
                        result.Add(new TextMatch(start, length, Snippet(text, start, length, charCount), rects));
                    }
                }
                finally { _textFindClose(search); }
            }
        }
        finally { _textClosePage(text); }
        return result;
    }

    /// <summary>Text between two points of a page (user space) as line rectangles (Left, Bottom, Right, Top) — used by "Highlight text". Empty when there is no text at either point.</summary>
    internal List<(double L, double B, double R, double T)> SelectTextRects(IntPtr page, double ax, double ay, double bx, double by)
    {
        var result = new List<(double, double, double, double)>();
        IntPtr text = _textLoadPage(page);
        if (text == IntPtr.Zero) return result;
        try
        {
            int IndexAt(double x, double y)
            {
                foreach (double tolerance in new[] { 3.0, 10.0, 24.0 })
                {
                    int i = _textGetCharIndexAtPos(text, x, y, tolerance, tolerance);
                    if (i >= 0) return i;
                }
                return -1;
            }
            int a = IndexAt(ax, ay), b = IndexAt(bx, by);
            if (a < 0 || b < 0) return result;
            int start = Math.Min(a, b), count = Math.Abs(a - b) + 1;
            int rectCount = _textCountRects(text, start, count);
            for (int r = 0; r < rectCount; r++)
            {
                double left, top, right, bottom;
                if (_textGetRect(text, r, &left, &top, &right, &bottom) != 0)
                    result.Add((left, Math.Min(top, bottom), right, Math.Max(top, bottom)));
            }
        }
        finally { _textClosePage(text); }
        return result;
    }

    private string Snippet(IntPtr text, int start, int length, int total)
    {
        int from = Math.Max(0, start - 28), to = Math.Min(total, start + length + 40);
        var buffer = new ushort[to - from + 1];
        int got;
        fixed (ushort* p = buffer) got = _textGetText(text, from, to - from, p);
        var chars = new char[Math.Max(0, got - 1)];
        for (int i = 0; i < chars.Length; i++) chars[i] = buffer[i] is 10 or 13 or 9 ? ' ' : (char)buffer[i];
        string s = new string(chars).Trim();
        return (from > 0 ? "…" : "") + s + (to < total ? "…" : "");
    }

    private static byte[] Utf8(string value)
    {
        // Như [MarshalAs(UnmanagedType.LPUTF8Str)]: UTF-8, kết thúc bằng byte 0.
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, 0, value.Length, bytes, 0);
        return bytes;
    }
}

/// <summary>
/// K bản PDFium chạy song song: bản 0 là pdfium.dll gốc, bản i là pdfium_i.dll (bản sao file, tạo lúc build).
/// K = biến môi trường XTPDF_PDFIUM_INSTANCES nếu có (để so sánh), mặc định clamp(số nhân / 2, 1, 4). Bản sao nào
/// thiếu / không nạp được thì pool chạy với ít bản hơn (tối thiểu 1 — như trước bước 3).
/// </summary>
internal static class PdfiumPool
{
    public const int MaxInstances = 8;

    private static readonly Lazy<PdfiumInstance[]> _instances = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<PdfiumInstance> Instances => _instances.Value;
    public static int Count => _instances.Value.Length;

    /// <summary>Số bản mong muốn (trước khi biết bản sao có nạp được không).</summary>
    public static int DesiredCount
    {
        get
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("XTPDF_PDFIUM_INSTANCES"), out int forced))
                return Math.Clamp(forced, 1, MaxInstances);
            return Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        }
    }

    private static PdfiumInstance[] Create()
    {
        var list = new List<PdfiumInstance> { PdfiumInstance.Primary };
        int desired = DesiredCount;
        string? directory = desired > 1 ? LocatePrimaryDirectory() : null;
        for (int i = 1; i < desired && directory != null; i++)
        {
            var copy = PdfiumInstance.TryLoadCopy(i, Path.Combine(directory, CopyFileName(i)));
            if (copy == null) break;
            list.Add(copy);
        }
        Debug.WriteLine($"[PdfiumPool] {list.Count}/{desired} bản PDFium");
        return list.ToArray();
    }

    public static string CopyFileName(int index)
        => OperatingSystem.IsWindows() ? $"pdfium_{index}.dll" : OperatingSystem.IsMacOS() ? $"libpdfium_{index}.dylib" : $"libpdfium_{index}.so";

    /// <summary>Thư mục chứa thư viện gốc (bản sao nằm cạnh nó): gốc app, hoặc runtimes/&lt;rid&gt;/native.</summary>
    private static string? LocatePrimaryDirectory()
    {
        string file = OperatingSystem.IsWindows() ? "pdfium.dll" : OperatingSystem.IsMacOS() ? "libpdfium.dylib" : "libpdfium.so";
        string baseDir = AppContext.BaseDirectory;
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        foreach (var dir in new[]
                 {
                     baseDir,
                     Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"),
                     Path.Combine(baseDir, "runtimes", $"{os}-{arch}", "native"),
                 })
            if (File.Exists(Path.Combine(dir, file))) return dir;
        return null;
    }

    /// <summary>
    /// Chọn bản cho 1 việc: ít việc nhất; bản đã có trang này parse sẵn được ưu tiên hơn 1 bậc (parse lại tốn
    /// 20–450 ms/trang trên file thật), bản đã mở document được ưu tiên khi hoà. Hoà nữa → bản số nhỏ hơn.
    /// </summary>
    /// <param name="reserveFirst">Việc nền (thumbnail, tải trước): không được chọn bản #0 khi còn bản khác. Một lượt parse không
    /// ngắt được (đo: 1,2–3,4 s trên trang CAD thật), nên bản #0 luôn rảnh cho trang người dùng đang xem.</param>
    public static PdfiumInstance Choose(Func<PdfiumInstance, bool> hasPage, Func<PdfiumInstance, bool> hasDocument, bool reserveFirst = false)
    {
        var instances = _instances.Value;
        if (instances.Length == 1) return instances[0];
        PdfiumInstance best = instances[reserveFirst ? 1 : 0];
        int bestScore = int.MaxValue;
        foreach (var instance in instances)
        {
            if (reserveFirst && instance == instances[0]) continue;
            int score = instance.Load * 4 + (hasPage(instance) ? 0 : 3) + (hasDocument(instance) ? 0 : 1);
            if (score < bestScore)
            {
                best = instance;
                bestScore = score;
            }
        }
        return best;
    }
}

