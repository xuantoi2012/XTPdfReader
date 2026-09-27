using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using XTPdfMergeApp.Services;

/// <summary>
/// scroll — cuộn liên tục qua các "màn hình" mỗi màn N trang MỚI. Với mỗi trang: parse (FPDF_LoadPage), vẽ
/// ảnh xem trước 340 px (#1), rồi vẽ ảnh nét ở độ rộng trang trên màn hình. So sánh:
///   1 tiến trình — như app: PDFium chỉ chạy trên 1 nhân tại 1 thời điểm (gate), N trang lần lượt;
///   N tiến trình — N worker THẬT (mỗi cái 1 bản PDFium, khởi động sẵn, nhận lệnh qua pipe, trả bitmap qua
///                  memory-mapped file), mỗi worker 1 trang của màn hình, chạy song song trên N nhân.
/// Tách thời gian mỗi trang thành: chờ đọc file (I/O), parse CPU, vẽ CPU — để biết nút thắt là mạng hay CPU.
/// </summary>
static unsafe class ScrollBench
{
    const int PreviewWidth = 340;   // ThumbnailCache.RenderThumbnailWidthPx
    const int AppFlags = 0x01 | 0x02 | 0x04 | 0x200;

    sealed record PageTimes(double IoMs, double ParseMs, double PreviewMs, double SharpMs, double CopyMs,
        long PreviewDoneTs, long SharpDoneTs, long IoBytes);

    /// <summary>1 bản PDFium + nguồn đọc file. "ram": cả file nằm sẵn trong RAM (chỉ đo CPU). "net": đọc theo khối
    /// 256 KB qua PdfBlockCache của app, KHÔNG qua cache Windows, không đọc nền (chỉ đo lệnh đọc của trang).</summary>
    sealed class Engine : IDisposable
    {
        readonly PdfiumApi _api;
        readonly byte[]? _mem;
        readonly LayeredDocumentSource? _source;
        public IntPtr Doc { get; }
        public int PageCount { get; }

        /// <summary>Tự có nguồn đọc riêng (1 tiến trình / mỗi worker tiến trình).</summary>
        public Engine(string path, bool ram, double netLatency, double netMBps)
            : this(PdfiumApi.Default, ram ? LoadAll(path) : null, ram ? null : NewCache(path, netLatency, netMBps), ownsCache: true)
        {
        }

        /// <summary>Dùng chung nguồn đọc với các Engine khác (N bản PDFium trong 1 tiến trình).</summary>
        public Engine(PdfiumApi api, byte[]? sharedMem, PdfBlockCache? sharedCache)
            : this(api, sharedMem, sharedCache, ownsCache: false)
        {
        }

        Engine(PdfiumApi api, byte[]? mem, PdfBlockCache? cache, bool ownsCache)
        {
            _api = api;
            _mem = mem;
            if (mem != null)
            {
                fixed (byte* p = mem) Doc = api.LoadMemDocument64(p, (nuint)mem.Length, IntPtr.Zero); // mảng pinned
            }
            else
            {
                if (!ownsCache && !cache!.TryAddRef()) throw new InvalidOperationException("Bộ đệm đã bị huỷ.");
                _source = new LayeredDocumentSource(cache!, Array.Empty<byte>()); // giữ 1 tham chiếu của cache
                Doc = api.LoadCustomDocument(_source.FileAccessPointer, IntPtr.Zero);
            }
            if (Doc == IntPtr.Zero) throw new InvalidOperationException("Không mở được PDF.");
            PageCount = api.GetPageCount(Doc);
        }

        public static byte[] LoadAll(string path)
        {
            var mem = GC.AllocateUninitializedArray<byte>((int)new FileInfo(path).Length, pinned: true);
            using (var fs = File.OpenRead(path)) fs.ReadExactly(mem);
            return mem;
        }

        /// <param name="dedup">Thử nghiệm: luồng cần khối đang được luồng khác đọc thì chờ, không đọc lại.</param>
        public static PdfBlockCache NewCache(string path, double netLatency, double netMBps, bool dedup = false)
        {
            long len = new FileInfo(path).Length;
            var link = new WindowBench.SimLink(netLatency, netMBps);
            return new PdfBlockCache(len, DateTime.MinValue, () => { }, _ =>
            {
                IBlockFile file = new CountingBlockFile(new WindowBench.RawBlockFile(path, len, link, new WindowBench.Counter()));
                return new ThreadTimedBlockFile(dedup ? new InFlightDedupBlockFile(file) : file);
            }); // không đọc nền
        }

        static (double Ms, long Bytes) IoSnapshot => (ThreadTimedBlockFile.Ticks * 1000.0 / Stopwatch.Frequency, ThreadTimedBlockFile.Bytes);

        public double PageAspect(int index)
        {
            IntPtr pg = _api.LoadPage(Doc, index);
            double a = _api.GetPageHeight(pg) / _api.GetPageWidth(pg);
            _api.ClosePage(pg);
            return a;
        }

        /// <summary>Parse + ảnh xem trước + ảnh nét cho 1 trang; nếu có sink thì chép ảnh nét vào đó (IPC).
        /// hash != null: trả về mã băm ảnh nét (kiểm tra N bản PDFium vẽ đúng như 1 bản).</summary>
        public PageTimes Render(int index, int sharpWidth, MemoryMappedViewAccessor? sink, Action<ulong>? hash = null)
        {
            var io0 = IoSnapshot;
            long t0 = Stopwatch.GetTimestamp();
            IntPtr page = _api.LoadPage(Doc, index);
            long t1 = Stopwatch.GetTimestamp();
            var io1 = IoSnapshot;
            double aspect = _api.GetPageHeight(page) / _api.GetPageWidth(page);

            Raster(page, PreviewWidth, (int)Math.Round(PreviewWidth * aspect), null, null);
            long t2 = Stopwatch.GetTimestamp();
            var io2 = IoSnapshot;

            double copyMs = Raster(page, sharpWidth, (int)Math.Round(sharpWidth * aspect), sink, hash);
            long t3 = Stopwatch.GetTimestamp();
            var io3 = IoSnapshot;
            _api.ClosePage(page);

            double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
            double ioParse = io1.Ms - io0.Ms, ioPreview = io2.Ms - io1.Ms, ioSharp = io3.Ms - io2.Ms;
            return new PageTimes(io3.Ms - io0.Ms, Ms(t0, t1) - ioParse, Ms(t1, t2) - ioPreview,
                Ms(t2, t3) - ioSharp - copyMs, copyMs, t2, t3, io3.Bytes - io0.Bytes);
        }

        double Raster(IntPtr page, int w, int h, MemoryMappedViewAccessor? sink, Action<ulong>? hash)
        {
            IntPtr bmp = _api.BitmapCreate(w, h, 0);
            _api.BitmapFillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
            _api.RenderPageBitmap(bmp, page, 0, 0, w, h, 0, AppFlags);
            double copyMs = 0;
            byte* pixels = (byte*)_api.BitmapGetBuffer(bmp);
            if (sink != null)
            {
                long c0 = Stopwatch.GetTimestamp();
                long bytes = Math.Min((long)w * h * 4, sink.Capacity);
                byte* dst = null;
                sink.SafeMemoryMappedViewHandle.AcquirePointer(ref dst);
                try { Buffer.MemoryCopy(pixels, dst, sink.Capacity, bytes); }
                finally { sink.SafeMemoryMappedViewHandle.ReleasePointer(); }
                copyMs = (Stopwatch.GetTimestamp() - c0) * 1000.0 / Stopwatch.Frequency;
            }
            if (hash != null)
            {
                ulong x = 1469598103934665603;
                ulong* q = (ulong*)pixels; long n = (long)w * h * 4 / 8;
                for (long i = 0; i < n; i++) x = (x ^ q[i]) * 1099511628211;
                hash(x);
            }
            _api.BitmapDestroy(bmp);
            return copyMs;
        }

        public void Dispose()
        {
            _api.CloseDocument(Doc);
            _source?.Dispose();
            GC.KeepAlive(_mem);
        }
    }

    // ── Tiến trình worker ───────────────────────────────────────────────────

    /// <summary>
    /// Vùng nhớ chia sẻ cho bitmap giữa app và worker. Windows: bộ nhớ chia sẻ có tên (CreateNew/OpenExisting),
    /// không có file trên đĩa nên không có tranh chấp FileShare giữa 2 tiến trình. Linux/macOS (.NET không hỗ
    /// trợ map có tên): file tạm, CẢ HAI bên mở với FileShare.ReadWrite rồi map từ FileStream đó.
    /// </summary>
    static class SharedBitmap
    {
        public static MemoryMappedFile Create(string name, long bytes)
        {
            if (OperatingSystem.IsWindows()) return MemoryMappedFile.CreateNew(name, bytes);
            var fs = new FileStream(TempPath(name), FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            return MemoryMappedFile.CreateFromFile(fs, null, bytes, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
        }

        public static MemoryMappedFile Open(string name, long bytes)
        {
            if (OperatingSystem.IsWindows()) return MemoryMappedFile.OpenExisting(name);
            var fs = new FileStream(TempPath(name), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            return MemoryMappedFile.CreateFromFile(fs, null, bytes, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
        }

        public static void Delete(string name)
        {
            if (OperatingSystem.IsWindows()) return; // map có tên tự mất khi handle cuối cùng đóng
            try { File.Delete(TempPath(name)); } catch { }
        }

        static string TempPath(string name) => Path.Combine(Path.GetTempPath(), name + ".bmp");
    }

    /// <summary>scroll-worker &lt;file&gt; &lt;ram|net&gt; &lt;latency&gt; &lt;MBps&gt; &lt;tên vùng nhớ&gt; &lt;số byte&gt;
    /// — stdin: "page i width" | "quit"; stdout: "ready" rồi mỗi trang 1 dòng kết quả.</summary>
    public static int Worker(string[] a)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        using var engine = new Engine(a[0], a[1] == "ram", double.Parse(a[2], CultureInfo.InvariantCulture), double.Parse(a[3], CultureInfo.InvariantCulture));
        using var mmf = SharedBitmap.Open(a[4], long.Parse(a[5]));
        using var view = mmf.CreateViewAccessor();
        engine.Render(0, 256, null); // khởi động: nạp font/bảng dùng chung như 1 worker đã chạy sẵn trong pool
        Console.Out.WriteLine("ready " + engine.PageCount);
        Console.Out.Flush();
        string? line;
        while ((line = Console.In.ReadLine()) != null && line != "quit")
        {
            var p = line.Split(' ');
            var t = engine.Render(int.Parse(p[1]), int.Parse(p[2]), view);
            Console.Out.WriteLine(string.Join(' ', t.IoMs, t.ParseMs, t.PreviewMs, t.SharpMs, t.CopyMs, t.PreviewDoneTs, t.SharpDoneTs, t.IoBytes));
            Console.Out.Flush();
        }
        return 0;
    }

    sealed class WorkerProcess : IDisposable
    {
        readonly Process _p;
        readonly string _mapName;
        public MemoryMappedFile Mmf { get; }

        public WorkerProcess(string file, string mode, double lat, double mbps, long mmfBytes)
        {
            _mapName = $"pdfbench-{Environment.ProcessId}-{Guid.NewGuid():N}";
            Mmf = SharedBitmap.Create(_mapName, mmfBytes);
            string self = Environment.ProcessPath!;
            var psi = new ProcessStartInfo(self) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
            if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                psi.ArgumentList.Add(typeof(ScrollBench).Assembly.Location);
            foreach (var arg in new[] { "scroll-worker", file, mode, lat.ToString(CultureInfo.InvariantCulture),
                         mbps.ToString(CultureInfo.InvariantCulture), _mapName, mmfBytes.ToString() })
                psi.ArgumentList.Add(arg);
            _p = Process.Start(psi)!;
        }

        public void WaitReady()
        {
            string? l = _p.StandardOutput.ReadLine();
            if (l == null || !l.StartsWith("ready")) throw new InvalidOperationException("Worker không khởi động được: " + l);
        }

        public void Send(int page, int width) { _p.StandardInput.WriteLine($"page {page} {width}"); _p.StandardInput.Flush(); }

        public PageTimes Receive()
        {
            var v = _p.StandardOutput.ReadLine()!.Split(' ');
            double D(int i) => double.Parse(v[i], CultureInfo.InvariantCulture);
            return new PageTimes(D(0), D(1), D(2), D(3), D(4), long.Parse(v[5]), long.Parse(v[6]), long.Parse(v[7]));
        }

        public void Dispose()
        {
            try { _p.StandardInput.WriteLine("quit"); _p.StandardInput.Flush(); _p.WaitForExit(5000); } catch { }
            if (!_p.HasExited) _p.Kill();
            Mmf.Dispose();
            SharedBitmap.Delete(_mapName);
        }
    }

    // ── Đo ──────────────────────────────────────────────────────────────────

    public static void Run(string path, string[] opts)
    {
        int visible = Opt(opts, "--visible", 3), screens = Opt(opts, "--screens", 5);
        int viewportH = Opt(opts, "--viewport", 880); // màn 1080 trừ ribbon/tab/thanh trạng thái
        double lat = -1, mbps = 0;
        int ni = Array.IndexOf(opts, "--netsim");
        if (ni >= 0 && ni + 2 < opts.Length) { lat = double.Parse(opts[ni + 1]); mbps = double.Parse(opts[ni + 2]); }
        string[] modes = opts.Contains("--ram-only") ? new[] { "ram" } : opts.Contains("--net-only") ? new[] { "net" } : new[] { "ram", "net" };

        int pageCount; double aspect;
        using (var probe = new Engine(path, false, -1, 0))
        {
            pageCount = probe.PageCount;
            aspect = probe.PageAspect(0);
        }
        // Độ rộng ảnh nét: N trang xếp dọc vừa khung nhìn, lượng tử lên bội 256 px như Viewer (512–2304).
        int sharpW = Opt(opts, "--width", Math.Clamp((int)Math.Ceiling(viewportH / (double)visible / aspect / 256.0) * 256, 512, 2304));
        long mmfBytes = (long)sharpW * (long)Math.Ceiling(sharpW * aspect) * 4;

        Console.WriteLine($"File {new FileInfo(path).Length / 1048576.0:F0} MB, {pageCount} trang. Mỗi màn {visible} trang MỚI, {screens} màn/kịch bản.");
        Console.WriteLine($"Mỗi trang: parse + ảnh xem trước {PreviewWidth} px + ảnh nét {sharpW} px. Máy có {Environment.ProcessorCount} nhân logic.");
        Console.WriteLine(lat >= 0 ? $"Giả lập mạng {lat} ms/lần nhảy, {mbps} MB/s — N worker mỗi cái 1 đường {mbps / visible:F1} MB/s (chia đều băng thông)."
            : OperatingSystem.IsWindows() ? "net: đọc KHÔNG qua cache Windows (mỗi lần đọc đi qua mạng thật)." : "Linux: net đọc qua cache hệ điều hành (chỉ để thử code).");

        int cursor = 0;
        int[] NextPages() { var r = new int[visible * screens]; for (int i = 0; i < r.Length; i++) r[i] = 1 + (cursor++ % Math.Max(1, pageCount - 1)); return r; }

        foreach (var mode in modes)
        {
            // RAM: cùng các trang cho 2 kịch bản (không có I/O nên so công bằng). net: 2 dải trang khác nhau để
            // kịch bản sau không hưởng cache phía máy chủ của kịch bản trước.
            int[] serialPages = NextPages();
            int[] parallelPages = mode == "ram" ? serialPages : NextPages();
            int[] copiesPages = mode == "ram" ? serialPages : NextPages();
            var serialHash = new System.Collections.Concurrent.ConcurrentDictionary<int, ulong>();
            Console.WriteLine($"\n══ {(mode == "ram" ? "RAM: cả file đã nằm trong RAM (chỉ CPU) — như app sau khi PdfBlockCache nạp nền xong" : "net: đọc theo khối 256 KB qua PdfBlockCache của app, chưa có gì trong bộ đệm — như vài giây đầu sau khi mở. N tiến trình: mỗi worker tự đọc riêng; N bản/1 TT: 1 bộ đệm chung" + (opts.Contains("--no-dedup") ? "" : ", khối đang đọc dở thì chờ, không đọc trùng (thử nghiệm)"))} ══");

            // 1 tiến trình: như app — PDFium tuần tự, ưu tiên ảnh xem trước của cả màn rồi mới tới ảnh nét.
            var serialAll = new List<double>(); var serialPreview = new List<double>(); var serialTimes = new List<PageTimes>();
            using (var engine = new Engine(path, mode == "ram", lat, mbps))
            {
                engine.Render(0, 256, null);
                for (int s = 0; s < screens; s++)
                {
                    long start = Stopwatch.GetTimestamp();
                    var pages = serialPages.Skip(s * visible).Take(visible).ToArray();
                    // Mỗi trang parse + xem trước + nét liền nhau. "Đủ ảnh xem trước" = tổng parse + xem trước của cả
                    // màn, vì app xếp ảnh xem trước của mọi trang lên trước ảnh nét (#1).
                    foreach (int p in pages) serialTimes.Add(engine.Render(p, sharpW, null, h => serialHash[p] = h));
                    double previewSum = serialTimes.Skip(serialTimes.Count - visible).Sum(t => t.IoMs + t.ParseMs + t.PreviewMs);
                    serialPreview.Add(previewSum);
                    serialAll.Add((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
                }
            }

            // N tiến trình worker, khởi động sẵn.
            var workers = Enumerable.Range(0, visible).Select(_ => new WorkerProcess(path, mode, lat, lat >= 0 ? mbps / visible : 0, mmfBytes)).ToList();
            var parAll = new List<double>(); var parPreview = new List<double>(); var parTimes = new List<PageTimes>(); var receiveCopy = new List<double>();
            try
            {
                var bootSw = Stopwatch.StartNew();
                foreach (var w in workers) w.WaitReady();
                Console.WriteLine($"   Khởi động {visible} worker (chạy 1 lần khi mở app): {bootSw.Elapsed.TotalMilliseconds:F0} ms");
                var sink = new byte[mmfBytes];
                for (int s = 0; s < screens; s++)
                {
                    var pages = parallelPages.Skip(s * visible).Take(visible).ToArray();
                    long start = Stopwatch.GetTimestamp();
                    for (int i = 0; i < pages.Length; i++) workers[i].Send(pages[i], sharpW);
                    var results = new PageTimes[pages.Length];
                    Parallel.For(0, pages.Length, i => results[i] = workers[i].Receive());
                    // App nhận bitmap: chép từ memory-mapped file sang bộ nhớ của app (như chép vào WriteableBitmap).
                    long c0 = Stopwatch.GetTimestamp();
                    foreach (var w in workers.Take(pages.Length))
                        using (var view = w.Mmf.CreateViewAccessor()) view.ReadArray(0, sink, 0, sink.Length);
                    long end = Stopwatch.GetTimestamp();
                    receiveCopy.Add((end - c0) * 1000.0 / Stopwatch.Frequency);
                    parTimes.AddRange(results);
                    parPreview.Add((results.Max(r => r.PreviewDoneTs) - start) * 1000.0 / Stopwatch.Frequency);
                    parAll.Add((end - start) * 1000.0 / Stopwatch.Frequency);
                }
            }
            finally { foreach (var w in workers) w.Dispose(); }

            // N bản PDFium trong 1 tiến trình, N luồng, dùng chung 1 nguồn đọc (1 bộ đệm, 1 luồng đọc mạng).
            var cpAll = new List<double>(); var cpPreview = new List<double>(); var cpTimes = new List<PageTimes>();
            int hashChecked = 0, hashMismatch = 0;
            {
                var loadSw = Stopwatch.StartNew();
                byte[]? sharedMem = mode == "ram" ? Engine.LoadAll(path) : null;
                long memBefore = PrivateBytes();
                var apis = Enumerable.Range(1, visible).Select(PdfiumApi.LoadCopy).ToArray();
                PdfBlockCache? sharedCache = mode == "ram" ? null : Engine.NewCache(path, lat, mbps, dedup: !opts.Contains("--no-dedup"));
                var engines = new Engine[visible];
                Parallel.For(0, visible, i => { engines[i] = new Engine(apis[i], sharedMem, sharedCache); engines[i].Render(0, 256, null); });
                Console.WriteLine($"   Nạp {visible} bản PDFium + mở file (1 lần khi mở app): {loadSw.Elapsed.TotalMilliseconds:F0} ms");
                try
                {
                    for (int s = 0; s < screens; s++)
                    {
                        var pages = copiesPages.Skip(s * visible).Take(visible).ToArray();
                        long start = Stopwatch.GetTimestamp();
                        var results = new PageTimes[pages.Length];
                        Parallel.For(0, pages.Length, new ParallelOptions { MaxDegreeOfParallelism = pages.Length }, i =>
                            results[i] = engines[i].Render(pages[i], sharpW, null, h =>
                            {
                                if (!serialHash.TryGetValue(pages[i], out ulong expected)) return;
                                Interlocked.Increment(ref hashChecked);
                                if (expected != h) Interlocked.Increment(ref hashMismatch);
                            }));
                        long end = Stopwatch.GetTimestamp();
                        cpTimes.AddRange(results);
                        cpPreview.Add((results.Max(r => r.PreviewDoneTs) - start) * 1000.0 / Stopwatch.Frequency);
                        cpAll.Add((end - start) * 1000.0 / Stopwatch.Frequency);
                    }
                    Console.WriteLine($"   RAM thêm khi dùng {visible} bản PDFium (sau khi vẽ xong, không tính dữ liệu file): {(PrivateBytes() - memBefore) / 1048576.0:F0} MB");
                }
                finally
                {
                    foreach (var e in engines) e?.Dispose();
                    sharedCache?.Release();
                }
            }

            Breakdown("1 tiến trình", serialTimes);
            Breakdown($"{visible} tiến trình", parTimes);
            Breakdown($"{visible} bản/1 TT", cpTimes);
            Console.WriteLine("   * lần vẽ đầu của trang gồm cả phần PDFium parse muộn (block/Form XObject, giải mã ảnh).");
            Console.WriteLine($"   Chép bitmap từ worker về app: TB {receiveCopy.Average() / visible:F1} ms/trang (đã tính trong thời gian {visible} tiến trình)");
            Console.WriteLine($"\n   Thời gian hiện đủ {visible} trang mới của 1 màn:");
            Stat("1 tiến trình — đủ ảnh xem trước", serialPreview);
            Stat($"{visible} tiến trình — đủ ảnh xem trước", parPreview);
            Stat("1 tiến trình — đủ ảnh nét", serialAll);
            Stat($"{visible} tiến trình — đủ ảnh nét", parAll);
            Stat($"{visible} bản PDFium/1 TT — đủ ảnh xem trước", cpPreview);
            Stat($"{visible} bản PDFium/1 TT — đủ ảnh nét", cpAll);
            Console.WriteLine($"   → {visible} tiến trình nhanh hơn: ảnh xem trước ×{serialPreview.Average() / parPreview.Average():F2}, ảnh nét ×{serialAll.Average() / parAll.Average():F2}");
            Console.WriteLine($"   → {visible} bản PDFium/1 TT nhanh hơn: ảnh xem trước ×{serialPreview.Average() / cpPreview.Average():F2}, ảnh nét ×{serialAll.Average() / cpAll.Average():F2}");
            if (hashChecked > 0)
                Console.WriteLine(hashMismatch == 0 ? $"   Ảnh nét do {visible} bản PDFium vẽ song song giống hệt từng bit ảnh do 1 bản vẽ ({hashChecked} trang)."
                    : $"   !! {hashMismatch}/{hashChecked} trang vẽ song song KHÁC ảnh do 1 bản vẽ.");
        }
    }

    static long PrivateBytes()
    {
        GC.Collect(); GC.WaitForPendingFinalizers();
        using var p = Process.GetCurrentProcess();
        return OperatingSystem.IsWindows() ? p.PrivateMemorySize64 : p.WorkingSet64;
    }

    static void Breakdown(string label, List<PageTimes> t)
    {
        double io = t.Average(x => x.IoMs), parse = t.Average(x => x.ParseMs), pre = t.Average(x => x.PreviewMs), sharp = t.Average(x => x.SharpMs);
        double total = io + parse + pre + sharp;
        Console.WriteLine($"   {label,-14} mỗi trang TB: chờ đọc file {io,6:F0} ms ({io / total:P0}) | parse CPU {parse,6:F0} ms ({parse / total:P0}) | " +
                          $"vẽ xem trước* {pre,5:F0} ms | vẽ nét {sharp,6:F0} ms ({(pre + sharp) / total:P0}) | đọc {t.Average(x => x.IoBytes) / 1024:F0} KB");
    }

    static void Stat(string label, List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        Console.WriteLine($"   {label,-44} TB {v.Average(),6:F0}  trung vị {s[s.Count / 2],6:F0}  max {s[^1],6:F0} ms");
    }

    static int Opt(string[] o, string name, int def) { int i = Array.IndexOf(o, name); return i >= 0 && i + 1 < o.Length ? int.Parse(o[i + 1]) : def; }
}
