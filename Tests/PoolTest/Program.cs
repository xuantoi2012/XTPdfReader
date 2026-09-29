using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using iText.Kernel.Colors;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Layer;
using XTPdfMergeApp.Services;

// Chạy đúng code PdfThumbnailService/PdfiumPool của app (WPF thay bằng stub) — bước 3.
// dotnet run -c Release -- <K> <file-nho.pdf> <file-lon.pdf> [giây stress]
// Lưu ý: test đổi thời điểm ghi (mtime) của file nhỏ — dùng 1 BẢN SAO, đừng dùng file gốc.
static class P
{
    static int fails;
    static void Check(bool ok, string m) { Console.WriteLine((ok ? "PASS " : "FAIL ") + m); if (!ok) fails++; }

    static async Task<int> Main(string[] a)
    {
        int k;
        string small, big;
        int stressSeconds;
        if (a.Length == 0)
        {
            // CI và máy mới chưa có PDF mẫu vẫn chạy được toàn bộ kiểm tra.
            // Giữ chế độ có tham số để benchmark với đúng hồ sơ PDF thực tế.
            k = 4;
            small = MakeRenderFixture("fixture-small.pdf", 12);
            big = MakeRenderFixture("fixture-big.pdf", 16);
            stressSeconds = 5;
            Console.WriteLine("Không có tham số: dùng PDF fixture nội bộ (K=4, stress 5 giây).");
        }
        else if (a.Length >= 3)
        {
            k = int.Parse(a[0]);
            small = Path.GetFullPath(a[1]);
            big = Path.GetFullPath(a[2]);
            stressSeconds = a.Length > 3 ? int.Parse(a[3]) : 15;
        }
        else
        {
            Console.Error.WriteLine("Cách dùng: PoolTest <K> <file-nho.pdf> <file-lon.pdf> [giây stress]");
            return 2;
        }
        Environment.SetEnvironmentVariable("XTPDF_PDFIUM_INSTANCES", k.ToString());

        Check(PdfiumPool.Count == k, $"pool có {PdfiumPool.Count} bản (muốn {k})");

        // ── Tham chiếu: vẽ tuần tự (từng lệnh await xong mới gọi lệnh sau) ──
        int smallPages = await PdfThumbnailService.GetPageCountAsync(small);
        if (smallPages < 4)
        {
            Console.Error.WriteLine("File nhỏ phải có ít nhất 4 trang.");
            return 2;
        }
        var pages = Enumerable.Range(0, Math.Min(12, smallPages)).ToArray();
        var reference = new Dictionary<int, ulong>();
        foreach (int p in pages) reference[p] = (await PdfThumbnailService.RenderPageAsync(small, p, 800))!.Hash();
        Check(reference.Count == pages.Length, $"vẽ tuần tự {pages.Length} trang tham chiếu");

        // ── Vẽ đồng thời: mỗi trang 2 lần, 24 lệnh cùng lúc ──
        var results = await Task.WhenAll(pages.Concat(pages).Select(p => Task.Run(async () =>
            (p, h: (await PdfThumbnailService.RenderPageAsync(small, p, 800))?.Hash()))));
        Check(results.All(r => r.h == reference[r.p]), $"24 lệnh vẽ đồng thời: ảnh giống hệt tham chiếu ({results.Count(r => r.h == reference[r.p])}/24)");
        var used = PdfiumPool.Instances.Where(i => i.Completed > 0).Count();
        Check(k == 1 || used > 1, $"việc được chia cho {used}/{k} bản");
        Check(PdfiumPool.Instances.All(i => i.Load == 0), "xong việc: Load mọi bản về 0");

        // ── Tile batch đồng thời == tuần tự ──
        var rects = new[] { new Int32Rect(0, 0, 400, 300), new Int32Rect(400, 0, 400, 300), new Int32Rect(0, 300, 400, 300), new Int32Rect(400, 300, 400, 265) };
        var tileRef = new Dictionary<int, ulong[]>();
        foreach (int p in pages.Take(4))
            tileRef[p] = (await PdfThumbnailService.RenderPageTilesBatchAsync(small, p, 800, 565, rects)).Select(b => b?.Hash() ?? 0).ToArray();
        var tileRes = await Task.WhenAll(pages.Take(4).Concat(pages.Take(4)).Select(p => Task.Run(async () =>
            (p, h: (await PdfThumbnailService.RenderPageTilesBatchAsync(small, p, 800, 565, rects)).Select(b => b?.Hash() ?? 0).ToArray()))));
        Check(tileRef.Values.All(v => v.All(h => h != 0)) && tileRes.All(r => r.h.SequenceEqual(tileRef[r.p])), "tile batch đồng thời giống hệt tuần tự");

        // ── Trang đã parse: lần sau về đúng bản đã có page handle ──
        long hits0 = PdfThumbnailService.NativePageCacheHits;
        await PdfThumbnailService.RenderPageAsync(small, pages[3], 800);
        await PdfThumbnailService.RenderPageAsync(small, pages[3], 800);
        Check(PdfThumbnailService.NativePageCacheHits >= hits0 + 2, $"vẽ lại trang vừa vẽ: dùng lại page handle ({PdfThumbnailService.NativePageCacheHits - hits0} lần trúng)");
        Check(await PdfThumbnailService.GetPageAspectRatioAsync(small, 0) is > 0, "tỉ lệ trang");

        // ── Khổ giấy thật của mọi trang (bố cục Cuộn liên tục kiểu Chromium) ──
        string mixed = MakeMixedSizes();
        var sizes = await PdfThumbnailService.GetPageSizesAsync(mixed);
        Check(sizes is { Length: 3 } && Math.Abs(sizes[0].Width - 2384) < 1 && Math.Abs(sizes[0].Height - 1684) < 1 &&
              Math.Abs(sizes[1].Width - 1191) < 1 && Math.Abs(sizes[1].Height - 842) < 1 &&
              Math.Abs(sizes[2].Width - 842) < 1 && Math.Abs(sizes[2].Height - 1191) < 1,
            "GetPageSizesAsync: A1 ngang, A3 ngang, A3 dọc (/Rotate 90) đúng kích thước point" +
            (sizes == null ? "" : " — " + string.Join(", ", sizes.Select(z => $"{z.Width:F0}x{z.Height:F0}"))));

        // ── PDF mã hoá: báo đúng nguyên nhân, thử lại bằng mật khẩu chỉ ở RAM ──
        string protectedPdf = MakePasswordProtected();
        var locked = await PdfThumbnailService.TryGetPageCountAsync(protectedPdf);
        Check(locked.PageCount == 0 && locked.Failure == PdfOpenFailure.Password, "PDF mã hoá: nhận ra cần mật khẩu");
        await PdfThumbnailService.SetDocumentPasswordAsync(protectedPdf, "sai-mat-khau");
        var wrongPassword = await PdfThumbnailService.TryGetPageCountAsync(protectedPdf);
        Check(wrongPassword.PageCount == 0 && wrongPassword.Failure == PdfOpenFailure.Password, "PDF mã hoá: báo sai mật khẩu để cho thử lại");
        await PdfThumbnailService.SetDocumentPasswordAsync(protectedPdf, "reader-password");
        var unlocked = await PdfThumbnailService.TryGetPageCountAsync(protectedPdf);
        Check(unlocked.PageCount == 1 && unlocked.Failure == PdfOpenFailure.None, "PDF mã hoá: mở được sau khi nhập đúng mật khẩu");
        await PdfThumbnailService.ForgetDocumentPasswordAsync(protectedPdf);

        // ── Tốc độ: 12 trang file lớn cùng lúc ──
        int bigPages = await PdfThumbnailService.GetPageCountAsync(big);
        if (bigPages < 13)
        {
            Console.Error.WriteLine("File lớn phải có ít nhất 13 trang.");
            return 2;
        }
        await PdfThumbnailService.RenderPageAsync(big, 0, 256); // mở document, khởi động
        var sw = Stopwatch.StartNew();
        var bigRes = await Task.WhenAll(Enumerable.Range(1, 12).Select(p => Task.Run(() => PdfThumbnailService.RenderPageAsync(big, p, 1024))));
        double wall = sw.Elapsed.TotalMilliseconds;
        Check(bigRes.All(b => b != null), $"12 trang file lớn cùng lúc: {wall:F0} ms (K={k})");
        Console.WriteLine($"   việc đã làm theo bản: {string.Join(", ", PdfiumPool.Instances.Select(i => $"#{i.Index}={i.Completed}"))}; document đang mở: {PdfThumbnailService.CachedDocumentCount}; page handle: {PdfThumbnailService.CachedNativePageCount}");

        // ── Ghi file: SuspendDocumentAsync đóng document ở MỌI bản ──
        PdfThumbnailService.ReleaseUnusedDocuments(new[] { small }); // đóng file lớn ở mọi bản
        await Task.Delay(200);
        await Task.WhenAll(pages.Concat(pages).Select(p => Task.Run(() => PdfThumbnailService.RenderPageAsync(small, p, 300))));
        int openBefore = PdfThumbnailService.CachedDocumentCount;
        Check(k == 1 || openBefore > 1, $"file nhỏ đang mở ở {openBefore} bản");
        using (await PdfThumbnailService.SuspendDocumentAsync(small, TimeSpan.FromSeconds(5)))
        {
            Check(PdfThumbnailService.CachedDocumentCount == 0, $"Suspend: đóng document của file ở mọi bản (còn {PdfThumbnailService.CachedDocumentCount})");
            Check(await PdfThumbnailService.RenderPageAsync(small, 0, 300) == null, "trong lúc Suspend: không vẽ");
            // Ghi đè được file (Windows chặn nếu còn handle; ở đây kiểm tra không còn lease nào giữ bộ đệm)
            File.SetLastWriteTimeUtc(small, DateTime.UtcNow);
        }
        Check((await PdfThumbnailService.RenderPageAsync(small, pages[1], 800))?.Hash() == reference[pages[1]], "sau Suspend: vẽ lại đúng");

        // ── Đổi layer (Retire): đóng document để mở lại nhưng file không đổi → giữ bản file trong RAM ──
        Check(PdfFileBuffer.IsBuffered(small), "sau Suspend + vẽ lại: file lại được đệm");
        var countersBefore = PdfFileBuffer.Counters;
        await PdfThumbnailService.RetireDocumentAsync(small);
        Check(PdfFileBuffer.IsBuffered(small) && PdfFileBuffer.Counters.Edited == countersBefore.Edited,
            "Retire (đổi layer): không bỏ bản file trong RAM");
        using (await PdfThumbnailService.SuspendDocumentAsync(small, TimeSpan.FromSeconds(5)))
            Check(!PdfFileBuffer.IsBuffered(small) && PdfFileBuffer.Counters.Edited == countersBefore.Edited + 1,
                "Suspend (app sắp ghi file): bỏ bản file trong RAM");
        Check((await PdfThumbnailService.RenderPageAsync(small, pages[1], 800))?.Hash() == reference[pages[1]], "sau Retire/Suspend: vẽ lại đúng");

        // ── Layer: tắt 1 layer → mọi bản vẽ theo trạng thái mới ──
        string layers = MakeLayers();
        var info = PdfLayerService.ReadLayers(layers);
        string idA = info.Names.First(kv => kv.Value == "LAYER_A").Key;
        string Colour(BitmapSource b, int x) { int stride = b.Pixels.Length / b.PixelHeight; int o = 50 * stride + x * 4; return (b.Pixels[o + 2], b.Pixels[o + 1], b.Pixels[o]) switch { (255, 255, 255) => "-", (255, 0, 0) => "A", (0, 0, 255) => "B", _ => "?" }; }
        var before = await Task.WhenAll(Enumerable.Range(0, 2 * k).Select(_ => Task.Run(() => PdfThumbnailService.RenderPageAsync(layers, 0, 400))));
        Check(before.All(b => b != null && Colour(b, 50) == "A" && Colour(b, 150) == "B"), "layer mặc định: A, B hiện (mọi bản)");
        PdfLayerStateStore.SetHidden(layers, new HashSet<string> { idA }, info.DefaultHidden);
        await PdfThumbnailService.RetireDocumentAsync(layers);
        string token = PdfLayerStateStore.GetToken(layers);
        var after = await Task.WhenAll(Enumerable.Range(0, 2 * k).Select(_ => Task.Run(() => PdfThumbnailService.RenderPageAsync(layers, 0, 400, layerToken: token))));
        Check(after.All(b => b != null && Colour(b, 50) == "-" && Colour(b, 150) == "B"), $"tắt LAYER_A: mọi lệnh ({after.Length}) vẽ A ẩn, B hiện");

        // ── Layer trong file có /OCProperties là từ điển TRỰC TIẾP trong Catalog (file AutoCAD/pdfFactory) ──
        // Trước đây phần nối thêm rỗng (iText không ghi Catalog) nên tắt layer không có tác dụng.
        string direct = MakeDirectOcProperties();
        var dInfo = PdfLayerService.ReadLayers(direct);
        string dIdA = dInfo.Names.First(kv => kv.Value == "LAYER_A").Key;
        PdfLayerStateStore.SetHidden(direct, new HashSet<string> { dIdA }, dInfo.DefaultHidden);
        await PdfThumbnailService.RetireDocumentAsync(direct);
        var dAfter = await PdfThumbnailService.RenderPageAsync(direct, 0, 400, layerToken: PdfLayerStateStore.GetToken(direct));
        Check(dAfter != null && Colour(dAfter, 50) == "-" && Colour(dAfter, 150) == "B", "tắt LAYER_A trong file có /OCProperties trực tiếp: A ẩn, B hiện");

        // ── Stress: 16 luồng, huỷ ngẫu nhiên, tile, tỉ lệ trang, ReleaseCachedPages, Suspend ──
        var errors = new ConcurrentBag<string>();
        int renders = 0, cancelled = 0, suspends = 0;
        var stop = DateTime.UtcNow.AddSeconds(stressSeconds);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(t => Task.Run(async () =>
        {
            var r = new Random(t);
            while (DateTime.UtcNow < stop)
            {
                try
                {
                    int op = r.Next(100);
                    string file = r.Next(3) == 0 ? big : small;
                    int count = file == big ? bigPages : smallPages;
                    int page = r.Next(Math.Min(count, 20));
                    using var cts = new CancellationTokenSource();
                    if (r.Next(3) == 0) cts.CancelAfter(r.Next(1, 40));
                    if (op < 60)
                    {
                        var b = await PdfThumbnailService.RenderPageAsync(file, page, r.Next(100, 900), cts.Token,
                            (PdfRenderPriority)r.Next(3));
                        if (b == null) Interlocked.Increment(ref cancelled);
                        else
                        {
                            Interlocked.Increment(ref renders);
                            if (file == small && reference.TryGetValue(page, out _) && b.PixelWidth <= 0) errors.Add("bitmap rỗng");
                        }
                    }
                    else if (op < 80)
                    {
                        var tiles = await PdfThumbnailService.RenderPageTilesBatchAsync(file, page, 800, 565, rects, cts.Token);
                        Interlocked.Increment(ref renders);
                    }
                    else if (op < 90) await PdfThumbnailService.GetPageAspectRatioAsync(file, page, cts.Token);
                    else if (op < 95) PdfThumbnailService.ReleaseCachedPages();
                    else if (op < 97)
                    {
                        using (await PdfThumbnailService.SuspendDocumentAsync(small, TimeSpan.FromSeconds(5))) { Interlocked.Increment(ref suspends); await Task.Delay(5); }
                    }
                    else PdfThumbnailService.SetHotPages(new[] { (file, page + 1), (file, page + 2) });
                }
                catch (Exception ex) { errors.Add(ex.GetType().Name + ": " + ex.Message); }
            }
        })));
        Check(errors.IsEmpty, $"stress {stressSeconds}s, 16 luồng: {renders} lượt vẽ, {cancelled} null/huỷ, {suspends} suspend, lỗi: {errors.Count} {errors.FirstOrDefault()}");
        await Task.Delay(300);
        Check(PdfiumPool.Instances.All(i => i.Load == 0), "sau stress: Load mọi bản về 0");
        foreach (int p in pages)
            if ((await PdfThumbnailService.RenderPageAsync(small, p, 800))?.Hash() != reference[p]) { Check(false, $"sau stress trang {p} khác tham chiếu"); break; }
        Check(true, "sau stress: vẽ lại 12 trang vẫn giống hệt tham chiếu");
        Console.WriteLine($"   page handle đang giữ: {PdfThumbnailService.CachedNativePageCount} (tối đa ~{k}×(4 + 12 nóng))");
        Check(PdfThumbnailService.CachedNativePageCount <= k * (PdfThumbnailService.NativePageCacheCapacity + PdfThumbnailService.MaxHotPages), "page handle trong giới hạn");

        // ── Bảng Debug (bước 4): số liệu theo bản khớp với số tổng ──
        string summary = RenderDiagnostics.Summary;
        Console.WriteLine(summary.Substring(summary.IndexOf("PDFium:", StringComparison.Ordinal)));
        Check(PdfiumPool.Instances.Sum(i => Volatile.Read(ref i.CachedPages)) == PdfThumbnailService.CachedNativePageCount,
            "Debug: tổng page handle theo bản = số tổng");
        Check(PdfiumPool.Instances.Sum(i => Volatile.Read(ref i.OpenDocuments)) == PdfThumbnailService.CachedDocumentCount,
            $"Debug: tổng document theo bản = số document đang mở ({PdfThumbnailService.CachedDocumentCount})");
        Check(PdfiumPool.Instances.All(i => i.GateHeld.Count > 0 && i.PagesParsed > 0), "Debug: mọi bản có số liệu giữ gate và parse trang");

                PdfThumbnailService.PrepareForShutdown(TimeSpan.FromSeconds(5));
        Check(PdfThumbnailService.ActiveNativeCalls == 0, "shutdown: không còn lệnh PDFium nào chạy");
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails;
    }

    static string MakeMixedSizes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xtpooltest"); Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "mixed.pdf");
        using (var doc = new PdfDocument(new PdfWriter(f)))
        {
            doc.AddNewPage(new iText.Kernel.Geom.PageSize(2384, 1684));           // A1 ngang
            doc.AddNewPage(new iText.Kernel.Geom.PageSize(1191, 842));            // A3 ngang
            doc.AddNewPage(new iText.Kernel.Geom.PageSize(1191, 842)).SetRotation(90); // A3 ngang xoay 90 → hiện dọc
        }
        return f;
    }

    /// <summary>Tạo PDF vector cố định cho CI; không thay thế benchmark trên hồ sơ thật.</summary>
    static string MakeRenderFixture(string fileName, int pageCount)
    {
        string dir = Path.Combine(Path.GetTempPath(), "xtpooltest");
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, fileName);
        using var doc = new PdfDocument(new PdfWriter(f));
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            bool landscape = pageIndex % 2 == 0;
            float width = landscape ? 1191 : 842;
            float height = landscape ? 842 : 1191;
            var page = doc.AddNewPage(new iText.Kernel.Geom.PageSize(width, height));
            var canvas = new PdfCanvas(page);
            canvas.SetFillColor(new DeviceRgb(250, 251, 253)).Rectangle(0, 0, width, height).Fill();
            for (int i = 0; i < 80; i++)
            {
                byte shade = (byte)(80 + (i * 29 + pageIndex * 17) % 150);
                canvas.SetStrokeColor(new DeviceRgb(shade, (byte)(255 - shade / 2), (byte)(90 + shade / 3)))
                    .SetLineWidth((i % 3) + 0.5f)
                    .MoveTo(20, 20 + (i * 31) % (int)(height - 40))
                    .LineTo(width - 20, 20 + (i * 53 + pageIndex * 11) % (int)(height - 40))
                    .Stroke();
            }
        }
        return f;
    }

    static string MakePasswordProtected()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xtpooltest");
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "password-protected.pdf");
        byte[] user = System.Text.Encoding.UTF8.GetBytes("reader-password");
        byte[] owner = System.Text.Encoding.UTF8.GetBytes("owner-password");
        var properties = new WriterProperties().SetStandardEncryption(user, owner,
            EncryptionConstants.ALLOW_PRINTING, EncryptionConstants.ENCRYPTION_AES_128);
        using var doc = new PdfDocument(new PdfWriter(f, properties));
        doc.AddNewPage(new iText.Kernel.Geom.PageSize(400, 300));
        return f;
    }

    /// <summary>PDF viết tay: /OCProperties và /D là từ điển trực tiếp trong Catalog, như file AutoCAD xuất ra.</summary>
    static string MakeDirectOcProperties()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xtpooltest"); Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "direct-oc.pdf");
        string?[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R 6 0 R] /D << /Order [5 0 R 6 0 R] /OFF [] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 100] /Resources << /Properties << /OC1 5 0 R /OC2 6 0 R >> >> /Contents 4 0 R >>",
            null, // nội dung trang (stream) — dựng bên dưới
            "<< /Type /OCG /Name (LAYER_A) >>",
            "<< /Type /OCG /Name (LAYER_B) >>",
        };
        const string content = "/OC /OC1 BDC 1 0 0 rg 10 10 80 80 re f EMC /OC /OC2 BDC 0 0 1 rg 110 10 80 80 re f EMC";
        objects[3] = $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";
        var sb = new System.Text.StringBuilder("%PDF-1.5\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            string obj = objects[i] ?? throw new InvalidOperationException("Thiếu đối tượng PDF.");
            sb.Append($"{i + 1} 0 obj\n{obj}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(f, sb.ToString(), System.Text.Encoding.ASCII);
        return f;
    }

    static string MakeLayers()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xtpooltest"); Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "layers.pdf");
        using (var doc = new PdfDocument(new PdfWriter(f)))
        {
            var a = new PdfLayer("LAYER_A", doc); var b = new PdfLayer("LAYER_B", doc);
            var pg = doc.AddNewPage(new iText.Kernel.Geom.PageSize(400, 100)); var cv = new PdfCanvas(pg);
            cv.BeginLayer(a).SetFillColor(ColorConstants.RED).Rectangle(10, 10, 80, 80).Fill().EndLayer();
            cv.BeginLayer(b).SetFillColor(ColorConstants.BLUE).Rectangle(110, 10, 80, 80).Fill().EndLayer();
        }
        return f;
    }
}
