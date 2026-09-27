using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using XTPdfMergeApp.Services;

/// <summary>
/// Đo 2 câu hỏi sau bước #0, trên ổ mạng thật:
///   readtest — tốc độ đọc tuần tự theo cỡ khối × số luồng song song (đọc KHÔNG qua cache Windows).
///   window   — parse trang mới TRONG LÚC đang đọc nền cả file (khoảng ~8 s với file 163 MB ở 21 MB/s).
///              Kịch bản D chạy đúng code của app (PdfBlockCache + LayeredDocumentSource, link từ Services/).
/// </summary>
static unsafe class WindowBench
{
    // FILE_FLAG_NO_BUFFERING: bỏ qua cache của Windows để mỗi lần đo đều đi qua mạng thật (không phụ thuộc
    // lần chạy trước). Yêu cầu offset/cỡ đọc/địa chỉ buffer bội số của sector — dùng 64 KB cho chắc.
    const FileOptions NoBuffering = (FileOptions)0x20000000;
    const int Align = 64 * 1024;
    static bool IsWindows => OperatingSystem.IsWindows();

    static SafeFileHandle OpenRaw(string path)
        => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            IsWindows ? NoBuffering : FileOptions.None);

    /// <summary>Đọc [offset, offset+count) vào dest, căn theo Align (đọc thừa rồi bỏ phần thừa).</summary>
    static void ReadAligned(SafeFileHandle h, long fileLen, long offset, int count, byte* dest, byte* scratch, int scratchSize)
    {
        long start = offset / Align * Align;
        long end = Math.Min(((offset + count + Align - 1) / Align) * Align, (fileLen + Align - 1) / Align * Align);
        long copied = 0;
        for (long pos = start; pos < end; pos += scratchSize)
        {
            int want = (int)Math.Min(scratchSize, end - pos);
            int got = RandomAccess.Read(h, new Span<byte>(scratch, want), pos);
            long from = Math.Max(pos, offset), to = Math.Min(pos + got, offset + count);
            if (to > from)
            {
                Buffer.MemoryCopy(scratch + (from - pos), dest + (from - offset), count - (from - offset), to - from);
                copied += to - from;
            }
            if (got < want) break;
        }
    }

    // ── readtest ────────────────────────────────────────────────────────────

    public static void ReadTest(string path)
    {
        long len = new FileInfo(path).Length;
        Console.WriteLine($"File {len / 1048576.0:F0} MB — đọc tuần tự {(IsWindows ? "KHÔNG qua cache Windows (FILE_FLAG_NO_BUFFERING)" : "(Linux: có cache)")}");
        Console.WriteLine("Mỗi tổ hợp đọc 1 đoạn 16 MB KHÁC nhau của file (tránh đọc lại vùng đã có trong cache máy chủ).");
        Console.WriteLine($"   {"Khối",-8}{"1 luồng",12}{"2 luồng",12}{"4 luồng",12}");
        const long region = 16L * 1024 * 1024;
        long cursor = 0;
        foreach (int chunkMb in new[] { 1, 4, 16 })
        {
            Console.Write($"   {chunkMb + " MB",-8}");
            foreach (int streams in new[] { 1, 2, 4 })
            {
                if (cursor + region > len) cursor = 0;
                double mbps = ReadRegion(path, len, cursor, region, chunkMb * 1024 * 1024, streams);
                cursor += region;
                Console.Write($"{mbps,9:F0} MB/s");
            }
            Console.WriteLine();
        }
        Console.WriteLine("→ Nếu nhiều luồng / khối lớn nhanh hơn rõ rệt: đường truyền bị giới hạn bởi độ trễ, đọc song song có ích.");
        Console.WriteLine("→ Nếu mọi ô ~ như nhau: đã chạm trần băng thông mạng/máy chủ, đọc song song không giúp gì.");
    }

    static double ReadRegion(string path, long len, long start, long count, int chunk, int streams)
    {
        var sw = Stopwatch.StartNew();
        long per = (count / streams + Align - 1) / Align * Align;
        Parallel.For(0, streams, new ParallelOptions { MaxDegreeOfParallelism = streams }, s =>
        {
            using var h = OpenRaw(path);
            byte* buf = (byte*)NativeMemory.AlignedAlloc((nuint)chunk, Align);
            try
            {
                long from = start + s * per, to = Math.Min(start + count, from + per);
                for (long pos = from; pos < to && pos < len; pos += chunk)
                    RandomAccess.Read(h, new Span<byte>(buf, (int)Math.Min(chunk, to - pos + Align - 1) / Align * Align), pos);
            }
            finally { NativeMemory.AlignedFree(buf); }
        });
        return count / 1048576.0 / sw.Elapsed.TotalSeconds;
    }

    // ── window ──────────────────────────────────────────────────────────────

    public static void Window(string path, string[] opts)
    {
        int blockKb = Opt(opts, "--block", 256);
        int bgChunkKb = Opt(opts, "--bgkb", 1024);
        int pages = Opt(opts, "--pages", 20);
        double netLatency = -1, netMBps = 0;
        int ni = Array.IndexOf(opts, "--netsim");
        if (ni >= 0 && ni + 2 < opts.Length) { netLatency = double.Parse(opts[ni + 1]); netMBps = double.Parse(opts[ni + 2]); }
        long len = new FileInfo(path).Length;
        Native.FPDF_InitLibrary();
        Console.WriteLine($"File {len / 1048576.0:F0} MB. Parse {pages} trang mới/kịch bản, mỗi kịch bản 1 dải trang KHÁC nhau. Khối đệm {blockKb} KB, khối đọc nền {bgChunkKb} KB.");
        Console.WriteLine(netLatency >= 0 ? $"Giả lập mạng: {netLatency} ms/lần nhảy vị trí, {netMBps} MB/s dùng CHUNG cho mọi luồng."
            : IsWindows ? "Đọc KHÔNG qua cache Windows — mỗi lần đọc đi qua mạng thật." : "Linux: có cache hệ điều hành (chỉ để thử code, số không đại diện).");

        int pageCount;
        {
            using var probe = new SourceFile(path, len, 0, false, netLatency, netMBps, 0);
            IntPtr d = Native.FPDF_LoadCustomDocument(probe.Pointer, null);
            pageCount = Native.FPDF_GetPageCount(d);
            Native.FPDF_CloseDocument(d);
        }
        int range = Math.Max(1, Math.Min(pages, pageCount / 4));
        var scenarios = new (string Name, int BlockKb, bool Background)[]
        {
            ("A. Như trước #0 (PDFium đọc kiểu cũ, không đọc nền)", 0, false),
            ("B. #0 cũ TRONG lúc đọc nền (đọc kiểu cũ + luồng nền đọc hết file, tranh băng thông)", 0, true),
            ($"C. Đề xuất: bộ đệm theo khối {blockKb} KB, trang đang xem ưu tiên hơn luồng nền", blockKb, true),
        };
        for (int s = 0; s < scenarios.Length; s++)
        {
            var sc = scenarios[s];
            using var src = new SourceFile(path, len, sc.BlockKb * 1024, sc.Background, netLatency, netMBps, bgChunkKb * 1024);
            var openSw = Stopwatch.StartNew();
            IntPtr doc = Native.FPDF_LoadCustomDocument(src.Pointer, null);
            double openMs = openSw.Elapsed.TotalMilliseconds;
            var times = new List<double>();
            int first = s * range;
            for (int p = first; p < Math.Min(pageCount, first + range); p++)
            {
                var sw = Stopwatch.StartNew();
                Native.FPDF_ClosePage(Native.FPDF_LoadPage(doc, p));
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            Native.FPDF_CloseDocument(doc);
            double bgDone = src.StopBackground();
            var sorted = times.OrderBy(x => x).ToList();
            Console.WriteLine($"\n{sc.Name}");
            Console.WriteLine($"   Mở file (FPDF_LoadCustomDocument): {openMs:F0} ms");
            Console.WriteLine($"   Parse trang mới: TB {times.Average(),5:F0} ms, trung vị {sorted[sorted.Count / 2],5:F0}, max {sorted[^1],5:F0} ms (n={times.Count})");
            Console.WriteLine($"   Đọc qua mạng: {src.ForegroundReads} lần cho trang đang xem ({src.ForegroundBytes / 1024} KB)" +
                              (sc.Background ? $", luồng nền đã đọc {src.BackgroundBytes / 1048576} MB" + (bgDone > 0 ? $" (xong cả file sau {bgDone / 1000:F1} s)" : " (chưa xong khi đo xong)") : ""));
        }
        AppCache(path, len, pageCount, 3 * range, range, netLatency, netMBps);
    }

    /// <summary>D: đúng đường đọc của app sau phương án C — PdfBlockCache (khối và lượt đọc nền
    /// PdfBlockCache.BlockSize) + LayeredDocumentSource + FPDF_LoadCustomDocument. Chỉ thay nguồn đọc file
    /// bằng bản không qua cache Windows (và giả lập mạng nếu có --netsim) để so được với A/B/C.</summary>
    static void AppCache(string path, long len, int pageCount, int first, int range, double netLatency, double netMBps)
    {
        var link = new SimLink(netLatency, netMBps);
        var fg = new Counter(); var bg = new Counter();
        var clock = Stopwatch.StartNew();
        long bgDoneBefore = RenderDiagnostics.FileBufferRead.Count;
        var cache = new PdfBlockCache(len, DateTime.MinValue, () => { },
            sequential => new RawBlockFile(path, len, link, sequential ? bg : fg));
        cache.StartBackgroundRead();
        var times = new List<double>();
        double openMs;
        using (var source = new LayeredDocumentSource(cache, Array.Empty<byte>())) // nhận tham chiếu của cache
        {
            var openSw = Stopwatch.StartNew();
            IntPtr doc = Native.FPDF_LoadCustomDocument(source.FileAccessPointer, null);
            openMs = openSw.Elapsed.TotalMilliseconds;
            for (int p = first; p < Math.Min(pageCount, first + range); p++)
            {
                var sw = Stopwatch.StartNew();
                Native.FPDF_ClosePage(Native.FPDF_LoadPage(doc, p));
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            Native.FPDF_CloseDocument(doc);
        }
        bool bgDone = RenderDiagnostics.FileBufferRead.Count > bgDoneBefore;
        var sorted = times.OrderBy(x => x).ToList();
        Console.WriteLine($"\nD. Code của app (PdfBlockCache: khối {PdfBlockCache.BlockSize / 1024} KB, đọc nền {PdfBlockCache.BlockSize / 1024} KB/lượt, trang ưu tiên)");
        Console.WriteLine($"   Mở file (FPDF_LoadCustomDocument): {openMs:F0} ms");
        if (times.Count == 0) { Console.WriteLine("   (file quá ít trang cho kịch bản D)"); return; }
        Console.WriteLine($"   Parse trang mới: TB {times.Average(),5:F0} ms, trung vị {sorted[sorted.Count / 2],5:F0}, max {sorted[^1],5:F0} ms (n={times.Count})");
        Console.WriteLine($"   Đọc qua mạng: {fg.Reads} lần cho trang đang xem ({fg.Bytes / 1024} KB), luồng nền đã đọc {bg.Bytes / 1048576} MB" +
                          (bgDone ? " (xong cả file)" : $" (chưa xong khi đo xong, {clock.Elapsed.TotalSeconds:F1} s)"));
    }

    sealed class Counter { public long Reads, Bytes; }

    /// <summary>Giả lập 1 đường truyền dùng chung: mỗi lần nhảy vị trí tốn latency ms, băng thông mbps chung.</summary>
    sealed class SimLink(double latency, double mbps)
    {
        readonly object _link = new();
        long _lastEnd = -1;

        public void Charge(long pos, long size)
        {
            if (latency < 0) return;
            double wait = pos != Interlocked.Exchange(ref _lastEnd, pos + size) ? latency : 0;
            Spin(wait);
            lock (_link) Spin(size / (mbps * 1048576.0) * 1000); // truyền: dùng chung băng thông
        }
    }

    /// <summary>Nguồn đọc cho PdfBlockCache của app: không qua cache Windows, có giả lập mạng.</summary>
    sealed class RawBlockFile : IBlockFile
    {
        readonly SafeFileHandle _h; readonly long _len; readonly SimLink _link; readonly Counter _counter;
        readonly byte* _scratch; const int ScratchSize = 1024 * 1024;

        public RawBlockFile(string path, long len, SimLink link, Counter counter)
        {
            _h = OpenRaw(path); _len = len; _link = link; _counter = counter;
            _scratch = (byte*)NativeMemory.AlignedAlloc(ScratchSize, Align);
        }

        public int Read(long offset, Span<byte> destination)
        {
            int n = (int)Math.Min(destination.Length, _len - offset);
            if (n <= 0) return 0;
            Interlocked.Increment(ref _counter.Reads); Interlocked.Add(ref _counter.Bytes, n);
            _link.Charge(offset, n);
            fixed (byte* dst = destination) lock (this) ReadAligned(_h, _len, offset, n, dst, _scratch, ScratchSize);
            return n;
        }

        public void Dispose() { _h.Dispose(); NativeMemory.AlignedFree(_scratch); }
    }

    static void Spin(double ms)
    {
        long until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);
        while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(50);
    }

    static int Opt(string[] o, string name, int def) { int i = Array.IndexOf(o, name); return i >= 0 && i + 1 < o.Length ? int.Parse(o[i + 1]) : def; }

    /// <summary>
    /// Nguồn đọc cho FPDF_LoadCustomDocument.
    /// blockSize = 0: chuyển nguyên yêu cầu nhỏ của PDFium xuống file (như FPDF_LoadDocument, nhưng không qua cache).
    /// blockSize &gt; 0: mọi yêu cầu được nới thành các khối căn lề blockSize, giữ trong bộ đệm cả file; khối đã có thì
    /// trả ngay. background: 1 luồng đọc tuần tự phần còn thiếu (khối bgChunk), nhường cho yêu cầu của trang đang xem.
    /// </summary>
    sealed class SourceFile : IDisposable
    {
        readonly string _path; readonly long _len; readonly int _block, _bgChunk;
        readonly double _latency, _mbps;
        readonly SafeFileHandle _fg;
        readonly Native.GetBlock _cb;
        readonly byte* _scratch; readonly int _scratchSize = 4 * 1024 * 1024;
        readonly byte[]? _cache; readonly bool[]? _have;
        readonly object _cacheLock = new();
        readonly object _link = new();       // giả lập: 1 đường truyền dùng chung
        long _lastEnd = -1;
        volatile int _fgWaiting;
        Thread? _bg; volatile bool _stop; double _bgDoneMs = -1;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        public long ForegroundReads, ForegroundBytes, BackgroundBytes;
        public IntPtr Pointer { get; }

        public SourceFile(string path, long len, int block, bool background, double latency, double mbps, int bgChunk)
        {
            _path = path; _len = len; _block = block; _latency = latency; _mbps = mbps; _bgChunk = Math.Max(Align, bgChunk);
            _fg = OpenRaw(path);
            _scratch = (byte*)NativeMemory.AlignedAlloc((nuint)_scratchSize, Align);
            if (block > 0 || background) { _cache = new byte[len]; _have = new bool[(len + BlockSize - 1) / BlockSize]; }
            _cb = GetBlock;
            Pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Native.FileAccess>());
            Marshal.StructureToPtr(new Native.FileAccess { FileLen = (nuint)len, GetBlock = Marshal.GetFunctionPointerForDelegate(_cb) }, Pointer, false);
            if (background) { _bg = new Thread(BackgroundRead) { IsBackground = true }; _bg.Start(); }
        }

        int BlockSize => _block > 0 ? _block : 256 * 1024; // B: bộ đệm chỉ dùng cho luồng nền

        void Simulate(long pos, long size, bool foreground)
        {
            if (_latency < 0) return;
            double wait = (pos != _lastEnd ? _latency : 0);
            _lastEnd = pos + size;
            Spin(wait);
            lock (_link) Spin(size / (_mbps * 1048576.0) * 1000); // truyền: dùng chung băng thông
        }

        int GetBlock(IntPtr param, CULong posC, IntPtr buf, CULong sizeC)
        {
            long pos = (long)posC.Value; int size = (int)sizeC.Value;
            try
            {
                if (_block == 0)
                {
                    // Kiểu cũ: yêu cầu nhỏ của PDFium đi thẳng xuống file. (B: luồng nền chỉ tranh băng thông.)
                    Interlocked.Increment(ref ForegroundReads); Interlocked.Add(ref ForegroundBytes, size);
                    Simulate(pos, size, true);
                    lock (_fg) ReadAligned(_fg, _len, pos, size, (byte*)buf, _scratch, _scratchSize);
                    return 1;
                }
                long firstBlock = pos / _block, lastBlock = (pos + size - 1) / _block;
                for (long b = firstBlock; b <= lastBlock; b++)
                {
                    bool have; lock (_cacheLock) have = _have![b];
                    if (have) continue;
                    _fgWaiting++;
                    try
                    {
                        long off = b * _block; int n = (int)Math.Min(_block, _len - off);
                        Interlocked.Increment(ref ForegroundReads); Interlocked.Add(ref ForegroundBytes, n);
                        Simulate(off, n, true);
                        fixed (byte* dst = &_cache![off]) lock (_fg) ReadAligned(_fg, _len, off, n, dst, _scratch, _scratchSize);
                        lock (_cacheLock) _have![b] = true;
                    }
                    finally { _fgWaiting--; }
                }
                fixed (byte* src = &_cache![pos]) Buffer.MemoryCopy(src, (void*)buf, size, size);
                return 1;
            }
            catch { return 0; }
        }

        void BackgroundRead()
        {
            using var h = OpenRaw(_path);
            byte* scratch = (byte*)NativeMemory.AlignedAlloc((nuint)_bgChunk, Align);
            try
            {
                for (long off = 0; off < _len && !_stop; off += _bgChunk)
                {
                    while (_fgWaiting > 0 && !_stop) Thread.Sleep(1); // nhường trang đang xem
                    int n = (int)Math.Min(_bgChunk, _len - off);
                    Simulate(off, n, false);
                    fixed (byte* dst = &_cache![off]) ReadAligned(h, _len, off, n, dst, scratch, _bgChunk);
                    Interlocked.Add(ref BackgroundBytes, n);
                    lock (_cacheLock)
                        for (long b = off / BlockSize; b <= (off + n - 1) / BlockSize; b++)
                            if ((b + 1) * BlockSize <= off + n || (b + 1) * BlockSize >= _len) _have![b] = true;
                }
                if (!_stop) _bgDoneMs = _clock.Elapsed.TotalMilliseconds;
            }
            finally { NativeMemory.AlignedFree(scratch); }
        }

        public double StopBackground()
        {
            if (_bg == null) return 0;
            _stop = true; _bg.Join();
            return _bgDoneMs;
        }

        public void Dispose()
        {
            StopBackground();
            Marshal.FreeHGlobal(Pointer); NativeMemory.AlignedFree(_scratch); _fg.Dispose(); GC.KeepAlive(_cb);
        }
    }
}
