using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
#pragma warning disable CS8500

// Đo đúng các lệnh PDFium mà XTPdfMergeApp gọi (PdfThumbnailService / .Progressive), cùng cờ vẽ, cùng
// kích thước tile 640 px, cùng lát progressive 8 ms, cùng độ rộng ảnh Viewer (lượng tử 256 px, 512–2304)
// và thumbnail 340 px — để tách được thời gian của PDFium khỏi thời gian WPF.
static class Bench
{
    const int FpdfAnnot = 0x01, FpdfLcdText = 0x02, FpdfNoNativeText = 0x04, FpdfLimitedImageCache = 0x200;
    const int AppFlags = FpdfAnnot | FpdfLcdText | FpdfNoNativeText | FpdfLimitedImageCache;
    const int TileSize = 640;             // ReaderWindow.ReaderTileSizePx
    const double ReaderBaseWidth = 2200;  // ReaderWindow.ReaderRenderWidthPx
    const int ThumbWidth = 340;           // ThumbnailCache.RenderThumbnailWidthPx
    const int SliceMs = 8;                // PdfThumbnailService.ProgressiveSliceMilliseconds
    static int _flags = AppFlags;

    static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length >= 2 && args[0] == "make")
        {
            int pages = args.Length > 2 ? int.Parse(args[2]) : 200;
            int segments = args.Length > 3 ? int.Parse(args[3]) : 60000;
            bool image = args.Length <= 4 || args[4] != "noimage";
            Synthetic.Make(args[1], pages, segments, image);
            return 0;
        }
        if (args.Length >= 2 && args[0] == "bench") return Run(args[1], args.Skip(2).ToArray());
        Console.WriteLine("dotnet run -c Release -- bench <file.pdf> [--screen 1920x1080] [--dpi 1.0]");
        Console.WriteLine("dotnet run -c Release -- make <out.pdf> [pages=200] [segmentsPerPage=60000]");
        return 1;
    }

    // ── Đo ──────────────────────────────────────────────────────────────────

    static int Run(string path, string[] opts)
    {
        int screenW = 1920, screenH = 1080; double dpi = 1.0;
        bool memMode = opts.Contains("--mem");
        double netLatency = -1, netMBps = 0;
        int ni = Array.IndexOf(opts, "--netsim");
        if (ni >= 0 && ni + 2 < opts.Length) { netLatency = double.Parse(opts[ni + 1]); netMBps = double.Parse(opts[ni + 2]); }
        for (int i = 0; i + 1 < opts.Length; i++)
        {
            if (opts[i] == "--screen") { var p = opts[i + 1].Split('x'); screenW = int.Parse(p[0]); screenH = int.Parse(p[1]); }
            if (opts[i] == "--dpi") dpi = double.Parse(opts[i + 1]);
        }
        // Vùng xem trang của Viewer ≈ màn hình trừ ribbon/panel trái/tab/thanh trạng thái.
        int viewW = (int)((screenW - 230 - 40) * dpi), viewH = (int)((screenH - 200) * dpi);
        int readerWidth = Quantize(Math.Max(640, (screenW - 230 - 40)) * dpi);

        var fi = new FileInfo(path);
        Console.WriteLine($"File: {fi.Name}  {fi.Length / 1048576.0:F1} MB");
        Console.WriteLine($"Giả lập: màn hình {screenW}x{screenH}, DPI x{dpi}, vùng xem {viewW}x{viewH}px, ảnh Viewer fit-width {readerWidth}px");
        Native.FPDF_InitLibrary();
        if (netLatency >= 0) Console.WriteLine($"Giả lập ổ mạng: {netLatency} ms mỗi lần nhảy vị trí đọc, {netMBps} MB/s");
        Console.WriteLine(memMode
            ? "Chế độ mở: --mem (đọc tuần tự CẢ file vào RAM rồi FPDF_LoadMemDocument64 — như app sau bước #0)"
            : "Chế độ mở: file (FPDF_LoadDocument, PDFium tự seek/đọc từng phần — như app trước bước #0)");

        // 0. Kiểu đọc I/O của PDFium khi đọc thẳng từ file (mỗi lần đọc = 1 vòng hỏi-đáp nếu là ổ mạng)
        IoPattern(path, Math.Min(20, 20));

        // 1. MỞ FILE → TRANG ĐẦU HIỆN (đúng thứ tự app: đếm trang → trang 1 cho Viewer → 4 thumbnail đầu)
        Section("1. Mở file tới khi trang đầu render xong (lạnh)");
        var sw = Stopwatch.StartNew();
        byte[]? memory = null;
        double tRead = 0;
        if (memMode)
        {
            memory = ReadWholeFile(path);
            tRead = Ms(sw);
            Row($"Đọc tuần tự cả file vào RAM ({memory.Length / 1048576.0:F0} MB, {memory.Length / 1048576.0 / Math.Max(tRead, 1) * 1000:F0} MB/s)", tRead);
            sw.Restart();
        }
        NetSimFile? sim = null;
        if (memMode && netLatency >= 0) { double simRead = netLatency + memory!.Length / (netMBps * 1048576.0) * 1000; tRead = simRead; Row("  (giả lập mạng: đọc tuần tự cả file)", simRead); }
        if (!memMode && netLatency >= 0) sim = new NetSimFile(path, netLatency, netMBps);
        IntPtr doc = memMode ? Native.FPDF_LoadMemDocument64(ref memory![0], (nuint)memory.Length, null)
            : sim != null ? Native.FPDF_LoadCustomDocument(sim.Pointer, null)
            : Native.FPDF_LoadDocument(path, null);
        double tOpen = Ms(sw) + tRead;
        if (doc == IntPtr.Zero) { Console.WriteLine("Không mở được file, lỗi PDFium " + Native.FPDF_GetLastError()); return 2; }
        sw.Restart(); int pageCount = Native.FPDF_GetPageCount(doc); double tCount = Ms(sw);
        Row(memMode ? "FPDF_LoadMemDocument64 (cộng cả thời gian đọc file)" : "FPDF_LoadDocument", tOpen);
        Row($"FPDF_GetPageCount ({pageCount} trang)", tCount);
        var first = LoadAndRender(doc, 0, readerWidth, progressive: true);
        Row("Trang 1: FPDF_LoadPage (parse nội dung)", first.Parse);
        Row($"Trang 1: vẽ progressive {readerWidth}px ({first.Slices} lát, lát dài nhất {first.MaxSlice:F1} ms)", first.Render);
        Row("⇒ TỪ LÚC MỞ TỚI TRANG ĐẦU XONG (chỉ phần PDFium)", tOpen + tCount + first.Parse + first.Render, bold: true);
        var warm = new List<double>();
        for (int p = 0; p < Math.Min(4, pageCount); p++)
        {
            var r = LoadAndRender(doc, p, ThumbWidth, progressive: true, reuse: p == 0 ? first.Page : IntPtr.Zero);
            warm.Add(r.Parse + r.Render);
            if (p != 0) Native.FPDF_ClosePage(r.Page);
        }
        Row("4 thumbnail đầu (app làm ngay sau khi mở, tuần tự qua gate)", warm.Sum());

        // 2. ZOOM trang 1 (trang đã parse sẵn — app giữ page handle trong cache)
        Section("2. Zoom trang 1 (tile 640px quanh tâm vùng xem, trang đã parse)");
        Console.WriteLine($"   {"Zoom",-6}{"Rộng trang px",14}{"Số tile",9}{"Tile đầu",10}{"Đủ vùng xem",13}{"Cả trang 1 ảnh",16}");
        double pageAspect = Native.FPDF_GetPageHeight(first.Page) / Native.FPDF_GetPageWidth(first.Page);
        foreach (double zoom in new[] { 0.5, 1.0, 2.0, 4.0 })
        {
            int fullW = (int)(ReaderBaseWidth * zoom * dpi), fullH = (int)(fullW * pageAspect);
            var tiles = VisibleTiles(fullW, fullH, viewW, viewH);
            var times = tiles.Select(t => RenderRegion(first.Page, fullW, fullH, t).Total).ToList();
            string whole = (long)fullW * fullH <= 24_000_000
                ? $"{RenderRegion(first.Page, fullW, fullH, (0, 0, fullW, fullH)).Total,12:F0} ms" : "  (>24MP, app không vẽ)";
            Console.WriteLine($"   {zoom * 100,4:F0}% {fullW,14}{tiles.Count,9}{times[0],8:F0} ms{times.Sum(),10:F0} ms{whole,16}");
        }

        // 2b. Cờ FPDF_RENDER_LIMITEDIMAGECACHE (app đang bật): mỗi lần vẽ (mỗi tile) giải mã lại ảnh raster?
        Section("2b. Cờ FPDF_RENDER_LIMITEDIMAGECACHE (app đang bật): vẽ lại cả trang fit-width 3 lần");
        {
            int h = (int)(readerWidth * pageAspect);
            _flags = AppFlags;
            var with = Enumerable.Range(0, 3).Select(_ => RenderRegion(first.Page, readerWidth, h, (0, 0, readerWidth, h)).Total).ToList();
            _flags = AppFlags & ~FpdfLimitedImageCache;
            var without = Enumerable.Range(0, 3).Select(_ => RenderRegion(first.Page, readerWidth, h, (0, 0, readerWidth, h)).Total).ToList();
            _flags = AppFlags;
            Console.WriteLine($"   Có cờ (như app):  {string.Join(" / ", with.Select(v => v.ToString("F0")))} ms");
            Console.WriteLine($"   Bỏ cờ:            {string.Join(" / ", without.Select(v => v.ToString("F0")))} ms  (lần 1 còn giải mã, lần 2-3 dùng ảnh đã cache)");
        }

        // 2c. Chi phí vẽ theo độ phân giải (trang đã parse): ảnh xem trước độ phân giải thấp có rẻ hơn nhiều không?
        Section("2c. Vẽ cả trang 1 ở các độ rộng (trang đã parse) — đánh giá 'hiện ảnh thấp trước'");
        foreach (int w in new[] { 256, 512, 1024, 1792, 2304 })
        {
            int h = (int)(w * pageAspect);
            var t = Enumerable.Range(0, 2).Select(_ => RenderRegion(first.Page, w, h, (0, 0, w, h)).Total).Min();
            Row($"{w} px", t);
        }

        // 2d. Zoom 200%: vẽ vùng xem thành 1 ảnh vs chia tile 640px
        Section("2d. Zoom 200%: cả vùng xem 1 lần vs tile 640px");
        {
            int fullW = (int)(ReaderBaseWidth * 2 * dpi), fullH = (int)(fullW * pageAspect);
            var tiles = VisibleTiles(fullW, fullH, viewW, viewH);
            int x0 = tiles.Min(t => t.X), y0 = tiles.Min(t => t.Y), x1 = tiles.Max(t => t.X + t.W), y1 = tiles.Max(t => t.Y + t.H);
            Row($"1 vùng {x1 - x0}x{y1 - y0}px", RenderRegion(first.Page, fullW, fullH, (x0, y0, x1 - x0, y1 - y0)).Total);
            Row($"{tiles.Count} tile 640px (cùng vùng)", tiles.Sum(t => RenderRegion(first.Page, fullW, fullH, t).Total));
        }

        // 3. CUỘN LIÊN TỤC: trang kế tiếp chưa parse (lạnh) vs đã parse (page cache của app chỉ giữ 4 trang rảnh)
        int scrollPages = Math.Min(pageCount, 20);
        Section($"3. Cuộn liên tục qua {scrollPages} trang (ảnh fit-width {readerWidth}px mỗi trang)");
        var coldParse = new List<double>(); var coldRender = new List<double>(); var handles = new List<IntPtr>();
        for (int p = 0; p < scrollPages; p++)
        {
            var r = LoadAndRender(doc, p, readerWidth, progressive: true, reuse: p == 0 ? first.Page : IntPtr.Zero);
            if (p > 0) { coldParse.Add(r.Parse); coldRender.Add(r.Render); }
            handles.Add(r.Page);
        }
        Stat("Parse trang mới (FPDF_LoadPage)", coldParse);
        Stat("Vẽ trang mới", coldRender);
        Stat("Tổng mỗi trang mới (parse+vẽ)", coldParse.Zip(coldRender, (a, b) => a + b).ToList());
        var warmRender = handles.Skip(1).Select(h => RenderRegion(h, readerWidth, (int)(readerWidth * pageAspect), (0, 0, readerWidth, (int)(readerWidth * pageAspect))).Total).ToList();
        Stat("Vẽ lại trang đã parse (page handle còn giữ)", warmRender);
        var thumbs = handles.Skip(1).Select(h => RenderRegion(h, ThumbWidth, (int)(ThumbWidth * pageAspect), (0, 0, ThumbWidth, (int)(ThumbWidth * pageAspect))).Total).ToList();
        Stat($"Thumbnail {ThumbWidth}px trang đã parse", thumbs);
        foreach (var h in handles) Native.FPDF_ClosePage(h);

        // 4. Đoán tốc độ cuộn: 1 màn hình fit-width chứa bao nhiêu trang, cần bao lâu
        Section("4. Suy ra");
        double perPage = coldParse.Count > 0 ? coldParse.Average() + coldRender.Average() : 0;
        Console.WriteLine($"   Mỗi trang mới cần ~{perPage:F0} ms PDFium (parse {coldParse.DefaultIfEmpty().Average():F0} + vẽ {coldRender.DefaultIfEmpty().Average():F0}).");
        Console.WriteLine($"   Parse chiếm {(perPage > 0 ? coldParse.DefaultIfEmpty().Average() / perPage * 100 : 0):F0}% — phần này KHÔNG chia lát progressive được (chặn gate PDFium toàn cục).");

        Native.FPDF_CloseDocument(doc);
        GC.KeepAlive(memory);
        sim?.Dispose();
        return 0;
    }

    /// <summary>File đọc qua FPDF_LoadCustomDocument có cộng độ trễ mạng: mỗi lần PDFium đọc KHÔNG nối tiếp lần
    /// trước = 1 vòng hỏi-đáp (latency), cộng thời gian truyền theo băng thông — mô phỏng SMB khi Windows chưa cache.</summary>
    sealed unsafe class NetSimFile : IDisposable
    {
        readonly FileStream _fs; readonly Native.GetBlock _cb; readonly double _latencyMs, _mbps; long _lastEnd = -1;
        public IntPtr Pointer { get; }
        public NetSimFile(string path, double latencyMs, double mbps)
        {
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
            _latencyMs = latencyMs; _mbps = mbps;
            _cb = (_, posC, buf, sizeC) =>
            {
                long pos = (long)posC.Value, size = (long)sizeC.Value;
                double wait = (pos != _lastEnd ? _latencyMs : 0) + size / (_mbps * 1048576.0) * 1000;
                _lastEnd = pos + size;
                var until = Stopwatch.GetTimestamp() + (long)(wait * Stopwatch.Frequency / 1000);
                while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(50);
                _fs.Position = pos;
                return _fs.ReadAtLeast(new Span<byte>((void*)buf, (int)size), (int)size, false) == (int)size ? 1 : 0;
            };
            Pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Native.FileAccess>());
            Marshal.StructureToPtr(new Native.FileAccess { FileLen = (nuint)_fs.Length, GetBlock = Marshal.GetFunctionPointerForDelegate(_cb) }, Pointer, false);
        }
        public void Dispose() { Marshal.FreeHGlobal(Pointer); _fs.Dispose(); GC.KeepAlive(_cb); }
    }

    /// <summary>Đọc tuần tự 1 lượt, khối lớn — cùng cách app dùng ở bước #0 (PdfFileBuffer).</summary>
    static byte[] ReadWholeFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        var data = GC.AllocateUninitializedArray<byte>(checked((int)fs.Length), pinned: true);
        int read = 0;
        while (read < data.Length)
        {
            int n = fs.Read(data, read, Math.Min(4 << 20, data.Length - read));
            if (n <= 0) throw new IOException("File bị cắt ngắn khi đang đọc.");
            read += n;
        }
        return data;
    }

    /// <summary>Đếm số lần + cỡ khối PDFium đòi đọc khi mở file và parse N trang đầu (qua FPDF_LoadCustomDocument
    /// với callback đếm — đúng mẫu truy cập của parser PDFium).</summary>
    static unsafe void IoPattern(string path, int pages)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
        long calls = 0, bytes = 0, seeks = 0, lastEnd = -1;
        Native.GetBlock cb = (_, posC, buf, sizeC) =>
        {
            long pos = (long)posC.Value, size = (long)sizeC.Value;
            calls++; bytes += size;
            if (pos != lastEnd) seeks++;
            lastEnd = pos + size;
            fs.Position = pos;
            return fs.ReadAtLeast(new Span<byte>((void*)buf, (int)size), (int)size, false) == (int)size ? 1 : 0;
        };
        var access = new Native.FileAccess { FileLen = (nuint)fs.Length, GetBlock = Marshal.GetFunctionPointerForDelegate(cb) };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<Native.FileAccess>());
        Marshal.StructureToPtr(access, ptr, false);
        IntPtr doc = Native.FPDF_LoadCustomDocument(ptr, null);
        long openCalls = calls, openBytes = bytes, openSeeks = seeks;
        int n = doc == IntPtr.Zero ? 0 : Math.Min(pages, Native.FPDF_GetPageCount(doc));
        for (int i = 0; i < n; i++) Native.FPDF_ClosePage(Native.FPDF_LoadPage(doc, i));
        if (doc != IntPtr.Zero) Native.FPDF_CloseDocument(doc);
        Marshal.FreeHGlobal(ptr); GC.KeepAlive(cb);
        Section("0. Kiểu đọc I/O của PDFium khi đọc thẳng từ file (không phụ thuộc chế độ mở ở trên)");
        Console.WriteLine($"   Mở document:        {openCalls,7} lần đọc, {openSeeks,6} lần nhảy vị trí, {openBytes / 1024.0,9:F0} KB");
        Console.WriteLine($"   Parse {n} trang đầu:  {calls - openCalls,7} lần đọc, {seeks - openSeeks,6} lần nhảy vị trí, {(bytes - openBytes) / 1024.0,9:F0} KB" +
                          $"  (TB {(calls - openCalls) / Math.Max(1.0, n):F0} lần đọc/trang, khối TB {(bytes - openBytes) / Math.Max(1.0, calls - openCalls) / 1024:F1} KB)");
        Console.WriteLine("   → Qua ổ mạng, mỗi lần nhảy vị trí là 1 vòng hỏi-đáp SMB nếu Windows chưa cache đoạn đó.");
    }

    static int Quantize(double width) => (int)Math.Clamp(Math.Ceiling(width / 256) * 256, 512, 2304);

    static List<(int X, int Y, int W, int H)> VisibleTiles(int fullW, int fullH, int viewW, int viewH)
    {
        // Vùng xem đặt ở tâm trang; tile xếp từ tâm ra như ReaderWindow.
        int x0 = Math.Max(0, (fullW - viewW) / 2), y0 = Math.Max(0, (fullH - viewH) / 2);
        int x1 = Math.Min(fullW, x0 + viewW), y1 = Math.Min(fullH, y0 + viewH);
        var tiles = new List<(int, int, int, int)>();
        for (int y = y0 / TileSize * TileSize; y < y1; y += TileSize)
            for (int x = x0 / TileSize * TileSize; x < x1; x += TileSize)
                tiles.Add((x, y, Math.Min(TileSize, fullW - x), Math.Min(TileSize, fullH - y)));
        double cx = (x0 + x1) / 2.0, cy = (y0 + y1) / 2.0;
        return tiles.OrderBy(t => Math.Pow(t.Item1 + t.Item3 / 2.0 - cx, 2) + Math.Pow(t.Item2 + t.Item4 / 2.0 - cy, 2)).ToList();
    }

    record struct PageResult(IntPtr Page, double Parse, double Render, int Slices, double MaxSlice);

    static PageResult LoadAndRender(IntPtr doc, int index, int width, bool progressive, IntPtr reuse = default)
    {
        var sw = Stopwatch.StartNew();
        IntPtr page = reuse != IntPtr.Zero ? reuse : Native.FPDF_LoadPage(doc, index);
        double parse = reuse != IntPtr.Zero ? 0 : Ms(sw);
        int height = (int)(width * Native.FPDF_GetPageHeight(page) / Native.FPDF_GetPageWidth(page));
        var r = RenderRegion(page, width, height, (0, 0, width, height), progressive);
        return new PageResult(page, parse, r.Total, r.Slices, r.MaxSlice);
    }

    static (double Total, int Slices, double MaxSlice) RenderRegion(IntPtr page, int fullW, int fullH, (int X, int Y, int W, int H) rect, bool progressive = true)
    {
        var sw = Stopwatch.StartNew();
        IntPtr bmp = Native.FPDFBitmap_Create(rect.W, rect.H, 1);
        Native.FPDFBitmap_FillRect(bmp, 0, 0, rect.W, rect.H, 0xFFFFFFFF);
        int slices = 0; double maxSlice = 0;
        if (!progressive)
        {
            Native.FPDF_RenderPageBitmap(bmp, page, -rect.X, -rect.Y, fullW, fullH, 0, _flags);
        }
        else
        {
            using var pause = new Pause();
            pause.Begin();
            int status = Native.FPDF_RenderPageBitmap_Start(bmp, page, -rect.X, -rect.Y, fullW, fullH, 0, _flags, pause.Pointer);
            slices++; maxSlice = pause.Elapsed;
            while (status == 1)
            {
                pause.Begin();
                status = Native.FPDF_RenderPage_Continue(page, pause.Pointer);
                slices++; maxSlice = Math.Max(maxSlice, pause.Elapsed);
            }
            Native.FPDF_RenderPage_Close(page);
        }
        Native.FPDFBitmap_Destroy(bmp);
        return (Ms(sw), slices, maxSlice);
    }

    sealed class Pause : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NeedToPause(IntPtr self);
        readonly NeedToPause _cb; long _start, _deadline;
        public IntPtr Pointer { get; }
        public Pause()
        {
            _cb = _ => Stopwatch.GetTimestamp() >= _deadline ? 1 : 0;
            Pointer = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteInt32(Pointer, 1);
            Marshal.WriteIntPtr(Pointer, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_cb));
            Marshal.WriteIntPtr(Pointer, IntPtr.Size * 2, IntPtr.Zero);
        }
        public void Begin() { _start = Stopwatch.GetTimestamp(); _deadline = _start + Stopwatch.Frequency * SliceMs / 1000; }
        public double Elapsed => (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;
        public void Dispose() { Marshal.FreeHGlobal(Pointer); GC.KeepAlive(_cb); }
    }

    static double Ms(Stopwatch sw) => sw.Elapsed.TotalMilliseconds;
    static void Section(string title) => Console.WriteLine("\n" + title);
    static void Row(string label, double ms, bool bold = false) => Console.WriteLine($"   {label,-66}{ms,9:F0} ms{(bold ? "  ◄" : "")}");
    static void Stat(string label, List<double> v)
    {
        if (v.Count == 0) return;
        var s = v.OrderBy(x => x).ToList();
        Console.WriteLine($"   {label,-46} TB {v.Average(),6:F0}  trung vị {s[s.Count / 2],6:F0}  max {s[^1],6:F0} ms  (n={v.Count})");
    }
}

static class Native
{
    const string L = "pdfium";
    [DllImport(L)] public static extern void FPDF_InitLibrary();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetBlock(IntPtr param, CULong position, IntPtr buf, CULong size);
    [StructLayout(LayoutKind.Sequential)] public struct FileAccess { public CULongWrap FileLenRaw; public IntPtr GetBlock; public IntPtr Param; public nuint FileLen { set => FileLenRaw = new CULongWrap(value); } }
    [StructLayout(LayoutKind.Sequential)] public struct CULongWrap { public CULong V; public CULongWrap(nuint v) => V = new CULong(v); }
    [DllImport(L)] public static extern IntPtr FPDF_LoadCustomDocument(IntPtr access, [MarshalAs(UnmanagedType.LPUTF8Str)] string? pw);
    [DllImport(L)] public static extern IntPtr FPDF_LoadMemDocument64(ref byte data, nuint size, [MarshalAs(UnmanagedType.LPUTF8Str)] string? pw);
    [DllImport(L)] public static extern IntPtr FPDF_LoadDocument([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string? pw);
    [DllImport(L)] public static extern uint FPDF_GetLastError();
    [DllImport(L)] public static extern void FPDF_CloseDocument(IntPtr d);
    [DllImport(L)] public static extern int FPDF_GetPageCount(IntPtr d);
    [DllImport(L)] public static extern IntPtr FPDF_LoadPage(IntPtr d, int i);
    [DllImport(L)] public static extern void FPDF_ClosePage(IntPtr p);
    [DllImport(L)] public static extern double FPDF_GetPageWidth(IntPtr p);
    [DllImport(L)] public static extern double FPDF_GetPageHeight(IntPtr p);
    [DllImport(L)] public static extern IntPtr FPDFBitmap_Create(int w, int h, int alpha);
    [DllImport(L)] public static extern void FPDFBitmap_FillRect(IntPtr b, int l, int t, int w, int h, uint c);
    [DllImport(L)] public static extern void FPDFBitmap_Destroy(IntPtr b);
    [DllImport(L)] public static extern void FPDF_RenderPageBitmap(IntPtr b, IntPtr p, int x, int y, int w, int h, int rot, int flags);
    [DllImport(L)] public static extern int FPDF_RenderPageBitmap_Start(IntPtr b, IntPtr p, int x, int y, int w, int h, int rot, int flags, IntPtr pause);
    [DllImport(L)] public static extern int FPDF_RenderPage_Continue(IntPtr p, IntPtr pause);
    [DllImport(L)] public static extern void FPDF_RenderPage_Close(IntPtr p);
}
