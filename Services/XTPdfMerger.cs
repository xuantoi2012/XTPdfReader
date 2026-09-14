// NuGet: dotnet add package itext
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using iText.Kernel.Pdf;
using iText.Kernel.Utils;

namespace XTPdfMergeApp.Services
{
    public static class XTPdfMerger
    {
        public static bool TryMerge(
            IReadOnlyList<string> inputPaths,
            string outputPath,
            out string errorMessage,
            Action<string>? log = null,
            bool mergeLayersByName = true,
            string mergeLayersNamePrefix = "",
            string collapseOtherLayersTo = "")
        {
            errorMessage = "";

            if (inputPaths == null || inputPaths.Count == 0)
            { errorMessage = "Không có file nào để ghép."; return false; }

            if (inputPaths.Count == 1)
            {
                try { File.Copy(inputPaths[0], outputPath, overwrite: true); return true; }
                catch (Exception ex) { errorMessage = ex.Message; return false; }
            }

            // Log kích thước từng file đầu vào để verify PlotEngine đã tạo đúng
            foreach (var p in inputPaths)
            {
                long sz = File.Exists(p) ? new FileInfo(p).Length : -1;
                log?.Invoke($"\n[Merge] Input: {Path.GetFileName(p)} ({sz} bytes)");
            }

            try
            {
                var writerProps = new WriterProperties()
                    .SetPdfVersion(PdfVersion.PDF_1_7);

                using var writer = new PdfWriter(outputPath, writerProps);
                using var outDoc = new PdfDocument(writer);
                var merger = new PdfMerger(outDoc);

                // Gộp layer (OCG) cùng tên giữa các file nguồn — canonicalByName/
                // replace tích luỹ dần qua TỪNG file được merge. Xác định layer
                // nào-là-layer-nào bằng cách đối chiếu ĐÚNG TRANG + ĐÚNG KHOÁ
                // RESOURCE giữa trang nguồn (inDoc, tên CHƯA bị đổi) và trang vừa
                // copy sang (outDoc) — KHÔNG dựa vào vị trí trong mảng
                // /OCProperties/OCGs như bản trước: mảng đó không đảm bảo giữ
                // đúng thứ tự khai báo của inDoc khi copy (đã xác nhận gây gộp
                // NHẦM layer trên file thật nhiều layer/khung tên phức tạp — xem
                // CollectOcgNamePairs).
                var canonicalByName = new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);
                var replace = new Dictionary<PdfDictionary, PdfDictionary>();

                foreach (var path in inputPaths)
                {
                    if (!File.Exists(path))
                    {
                        log?.Invoke($"\n[Merge] Skip (not found): {path}");
                        continue;
                    }

                    try
                    {
                        using var reader = new PdfReader(path);
                        using var inDoc = new PdfDocument(reader);

                        int pages = inDoc.GetNumberOfPages();
                        int pageOffsetBefore = outDoc.GetNumberOfPages();
                        log?.Invoke($"\n[Merge] Adding: {Path.GetFileName(path)} ({pages}p)");

                        merger.Merge(inDoc, 1, pages);

                        if (mergeLayersByName)
                        {
                            var visited = new HashSet<PdfDictionary>();
                            for (int pg = 1; pg <= pages; pg++)
                            {
                                var outRes = outDoc.GetPage(pageOffsetBefore + pg).GetPdfObject().GetAsDictionary(PdfName.Resources);
                                var inRes = inDoc.GetPage(pg).GetPdfObject().GetAsDictionary(PdfName.Resources);
                                CollectOcgNamePairs(outRes, inRes, mergeLayersNamePrefix, collapseOtherLayersTo,
                                    canonicalByName, replace, visited,
                                    msg => log?.Invoke($"\n[Merge]   [layer] {Path.GetFileName(path)} p{pg}: {msg}"));
                            }
                        }
                    }
                    catch (Exception exInner)
                    {
                        // Log chi tiết lỗi từng file
                        log?.Invoke($"\n[Merge] ✗ {Path.GetFileName(path)}: {exInner.GetType().Name}: {exInner.Message}");
                        if (exInner.InnerException != null)
                            log?.Invoke($"\n[Merge]   Inner: {exInner.InnerException.Message}");
                        throw; // re-throw để outer catch bắt
                    }
                }

                if (mergeLayersByName && replace.Count > 0)
                {
                    ApplyOcgDedup(outDoc, replace);
                    log?.Invoke($"\n[Merge] Đã gộp {replace.Count} layer trùng tên giữa các file.");
                }

                log?.Invoke($"\n[Merge] ✅ Output: {outputPath}");
                return true;
            }
            catch (Exception ex)
            {
                // Log full details để debug
                string detail = $"{ex.GetType().Name}: {ex.Message}";
                if (ex.InnerException != null)
                    detail += $" | Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";

                errorMessage = detail;
                log?.Invoke($"\n[Merge] ✗ {detail}");
                log?.Invoke($"\n[Merge] StackTrace: {ex.StackTrace?.Split('\n')[0]}");
                return false;
            }
        }

        /// <summary>
        /// Ghép theo TỪNG TRANG riêng lẻ, thứ tự tuỳ ý (có thể xen kẽ giữa
        /// nhiều file nguồn, VD trang 2 file A → trang 1 file B → trang 1 file
        /// A) — dùng khi user tự kéo-thả sắp xếp trang trước khi ghép (khác
        /// TryMerge chỉ ghép NGUYÊN file theo thứ tự file). pageNumber 1-based
        /// (trang 1 = trang đầu).
        ///
        /// Mở SẴN 1 PdfReader/PdfDocument cho MỖI file nguồn PHÂN BIỆT (không
        /// mở lại mỗi lần gặp 1 trang của file đó) — cần thiết vì thứ tự trang
        /// cuối cùng có thể quay lại nhiều lần cùng 1 file (không liên tục),
        /// mở/đóng liên tục vừa chậm vừa dễ lỗi.
        /// </summary>
        /// <param name="progress">Báo (Đã ghép, Tổng) sau MỖI trang — gọi từ Task.Run nên caller tự lo
        /// marshal về UI thread (vd dùng System.Progress&lt;T&gt;, tự làm việc đó khi Report).</param>
        /// <param name="cancellationToken">Kiểm tra giữa MỖI trang (không kiểm tra giữa chừng 1 trang —
        /// granularity đủ dùng vì merger.Merge từng trang vốn đã nhanh). Huỷ giữa chừng thì file output
        /// dở dang bị xoá, trả về false với errorMessage rỗng (phân biệt với lỗi thật).</param>
        public static bool TryMergePages(
            IReadOnlyList<(string SourcePath, int PageNumber)> pages,
            string outputPath,
            out string errorMessage,
            Action<string>? log = null,
            bool mergeLayersByName = true,
            string mergeLayersNamePrefix = "",
            string collapseOtherLayersTo = "",
            IProgress<(int Done, int Total)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            errorMessage = "";

            if (pages == null || pages.Count == 0)
            { errorMessage = "Không có trang nào để ghép."; return false; }

            var readers = new Dictionary<string, (PdfReader Reader, PdfDocument Doc)>(StringComparer.OrdinalIgnoreCase);
            bool cancelled = false;

            try
            {
                foreach (var path in pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(path))
                    { errorMessage = "File nguồn không tồn tại: " + path; return false; }

                    var reader = new PdfReader(path);
                    readers[path] = (reader, new PdfDocument(reader));
                }

                var writerProps = new WriterProperties().SetPdfVersion(PdfVersion.PDF_1_7);
                // using block RIÊNG (không phải "using var" tới hết method) — cần writer/outDoc đóng
                // HẲN (flush, nhả file lock) TRƯỚC khi có thể xoá file dở dang lúc bị huỷ giữa chừng.
                using (var writer = new PdfWriter(outputPath, writerProps))
                using (var outDoc = new PdfDocument(writer))
                {
                    var merger = new PdfMerger(outDoc);

                    var canonicalByName = new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);
                    var replace = new Dictionary<PdfDictionary, PdfDictionary>();
                    // visited DÙNG CHUNG cho MỌI trang — 1 file nguồn có thể góp
                    // nhiều trang không liên tục, XObject dùng chung (VD khung tên
                    // lặp lại mỗi trang) chỉ cần xử lý 1 lần.
                    var visited = new HashSet<PdfDictionary>();

                    log?.Invoke($"\n[Merge] Ghép {pages.Count} trang từ {readers.Count} file...");

                    int done = 0;
                    foreach (var (path, pageNumber) in pages)
                    {
                        if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }

                        var inDoc = readers[path].Doc;
                        if (pageNumber < 1 || pageNumber > inDoc.GetNumberOfPages())
                        {
                            errorMessage = $"Số trang không hợp lệ: {Path.GetFileName(path)} trang {pageNumber}.";
                            return false;
                        }

                        int pageIndexInOut = outDoc.GetNumberOfPages() + 1;
                        merger.Merge(inDoc, pageNumber, pageNumber);

                        if (mergeLayersByName)
                        {
                            var outRes = outDoc.GetPage(pageIndexInOut).GetPdfObject().GetAsDictionary(PdfName.Resources);
                            var inRes = inDoc.GetPage(pageNumber).GetPdfObject().GetAsDictionary(PdfName.Resources);
                            CollectOcgNamePairs(outRes, inRes, mergeLayersNamePrefix, collapseOtherLayersTo,
                                canonicalByName, replace, visited,
                                msg => log?.Invoke($"\n[Merge]   [layer] {Path.GetFileName(path)} p{pageNumber}: {msg}"));
                        }

                        done++;
                        progress?.Report((done, pages.Count));
                    }

                    if (!cancelled && mergeLayersByName && replace.Count > 0)
                    {
                        ApplyOcgDedup(outDoc, replace);
                        log?.Invoke($"\n[Merge] Đã gộp {replace.Count} layer trùng tên giữa các trang.");
                    }
                }

                if (cancelled)
                {
                    TryDeleteFile(outputPath);
                    return false;
                }

                log?.Invoke($"\n[Merge] ✅ Output: {outputPath}");
                return true;
            }
            catch (Exception ex)
            {
                string detail = $"{ex.GetType().Name}: {ex.Message}";
                if (ex.InnerException != null)
                    detail += $" | Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";

                errorMessage = detail;
                log?.Invoke($"\n[Merge] ✗ {detail}");
                TryDeleteFile(outputPath);
                return false;
            }
            finally
            {
                foreach (var (reader, doc) in readers.Values)
                {
                    try { doc.Close(); } catch { }
                }
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best effort — file dở dang, không quan trọng nếu không xoá được ngay */ }
        }

        // ── OCG (layer) dedup — xem ghi chú ở TryMerge ──────────────────────────

        private static bool IsOcg(PdfDictionary d) => PdfName.OCG.Equals(d.GetAsName(PdfName.Type));

        /// <summary>
        /// Đối chiếu layer (OCG) giữa trang NGUỒN (inRes, tên CHƯA bị iText đổi
        /// hậu tố) và trang VỪA COPY SANG (outRes) — dựa trên đúng KHOÁ resource
        /// (VD "/OC1") mà 2 bên CHẮC CHẮN giống nhau (copy resource dict chỉ đổi
        /// object VALUE, không đổi tên KEY), nên khỏi cần đoán thứ tự. Đệ quy
        /// xuống Form XObject lồng nhau (block/xref) — layer đôi khi nằm trong
        /// resource riêng của 1 XObject chứ không phải top-level page resources.
        ///
        /// So khớp namePrefix theo TÊN RÚT GỌN (ShortLayerName — phần sau dấu
        /// "|" cuối cùng) chứ không phải tên gốc: layer xuất từ xref/block có
        /// dạng "TenXref|CHUKY_..." (xem AutoCAD layer naming), so khớp theo
        /// tên gốc ĐẦY ĐỦ sẽ KHÔNG BAO GIỜ khớp "CHUKY_" vì tiền tố xref đứng
        /// trước — đã xác nhận đây là lý do lần trước lọc theo prefix không
        /// có tác dụng gì trên file thật.
        ///
        /// namePrefix rỗng = gộp mọi layer trùng tên (không rút gọn tên,
        /// không collapse). Có namePrefix: layer khớp → gộp + ĐỔI TÊN còn lại
        /// đúng tên rút gọn (VD "CHUKY_PHANBUILOI"); layer KHÔNG khớp → nếu
        /// collapseOtherLayersTo có giá trị (VD "0"), TẤT CẢ gộp chung vào 1
        /// layer duy nhất mang đúng tên đó — để bảng Layers cuối cùng chỉ còn
        /// vài layer chữ ký + 1 layer gộp chung, nhìn phát biết ngay chữ ký ở
        /// đâu (xem PrintSettings.MergeLayersNamePrefix/CollapseOtherLayersTo).
        /// </summary>
        private static void CollectOcgNamePairs(
            PdfDictionary? outResources, PdfDictionary? inResources, string namePrefix, string collapseOthersTo,
            Dictionary<string, PdfDictionary> canonicalByName, Dictionary<PdfDictionary, PdfDictionary> replace,
            HashSet<PdfDictionary> visited, Action<string>? log = null)
        {
            if (outResources == null || inResources == null) return;

            var outProps = outResources.GetAsDictionary(PdfName.Properties);
            var inProps = inResources.GetAsDictionary(PdfName.Properties);
            if (outProps != null && inProps != null)
            {
                foreach (var key in outProps.KeySet())
                {
                    var outOcg = outProps.GetAsDictionary(key);
                    var inOcg = inProps.GetAsDictionary(key);
                    if (outOcg == null || inOcg == null) continue;
                    if (!IsOcg(inOcg)) continue; // bỏ qua /Properties không phải OCG (VD OCMD, marked-content khác)

                    string rawName = inOcg.GetAsString(PdfName.Name)?.ToUnicodeString() ?? "";
                    if (string.IsNullOrEmpty(rawName)) continue;
                    string shortName = ShortLayerName(rawName);

                    string canonicalKey;
                    if (!string.IsNullOrEmpty(namePrefix) && shortName.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                        canonicalKey = shortName;
                    else if (!string.IsNullOrEmpty(namePrefix) && !string.IsNullOrEmpty(collapseOthersTo))
                        canonicalKey = collapseOthersTo;
                    else
                        canonicalKey = rawName; // không bật lọc theo prefix — giữ hành vi cũ: gộp theo đúng tên gốc

                    if (canonicalByName.TryGetValue(canonicalKey, out var canon))
                    {
                        if (!ReferenceEquals(canon, outOcg))
                        {
                            replace[outOcg] = canon;
                            log?.Invoke($"'{rawName}' (key={key}) → GỘP vào '{canonicalKey}' (đã có sẵn).");
                        }
                    }
                    else
                    {
                        if (!string.Equals(rawName, canonicalKey, StringComparison.Ordinal))
                            outOcg.Put(PdfName.Name, new PdfString(canonicalKey));
                        canonicalByName[canonicalKey] = outOcg;
                        log?.Invoke($"'{rawName}' (key={key}) → GIỮ RIÊNG, canonical mới = '{canonicalKey}'.");
                    }
                }
            }

            var outXo = outResources.GetAsDictionary(PdfName.XObject);
            var inXo = inResources.GetAsDictionary(PdfName.XObject);
            if (outXo != null && inXo != null)
            {
                foreach (var key in outXo.KeySet())
                {
                    var outStream = outXo.GetAsStream(key);
                    var inStream = inXo.GetAsStream(key);
                    if (outStream == null || inStream == null) continue;
                    if (!visited.Add(outStream)) continue; // tránh duyệt lại/vòng lặp

                    var outRes = outStream.GetAsDictionary(PdfName.Resources);
                    var inRes = inStream.GetAsDictionary(PdfName.Resources);
                    CollectOcgNamePairs(outRes, inRes, namePrefix, collapseOthersTo, canonicalByName, replace, visited, log);
                }
            }
        }

        /// <summary>Phần tên sau dấu "|" cuối cùng — layer thuộc xref/block có tên dạng "TenXref|TenLayer", chỉ TenLayer mới là tên thật user đặt.</summary>
        private static string ShortLayerName(string rawName)
        {
            int idx = rawName.LastIndexOf('|');
            return idx >= 0 ? rawName[(idx + 1)..] : rawName;
        }

        /// <summary>
        /// Áp dụng việc gộp: đổi tham chiếu OCG ở /Resources/Properties của
        /// TỪNG TRANG (khoá resource, VD "/OC1", trỏ tới OCG nào) từ bản
        /// trùng sang bản canonical — KHÔNG đụng nội dung content stream
        /// (toán tử BDC/EMC chỉ tham chiếu theo khoá resource, tự động theo
        /// đúng OCG mới mà không cần sửa). Sau đó loại các OCG trùng khỏi
        /// /OCProperties/OCGs và các mảng ON/OFF/Order.
        /// </summary>
        private static void ApplyOcgDedup(PdfDocument outDoc, Dictionary<PdfDictionary, PdfDictionary> replace)
        {
            for (int p = 1; p <= outDoc.GetNumberOfPages(); p++)
            {
                var page = outDoc.GetPage(p);
                var resources = page.GetPdfObject().GetAsDictionary(PdfName.Resources);
                var props = resources?.GetAsDictionary(PdfName.Properties);
                if (props == null) continue;

                foreach (var key in new List<PdfName>(props.KeySet()))
                {
                    var val = props.GetAsDictionary(key);
                    if (val != null && replace.TryGetValue(val, out var canon))
                        props.Put(key, canon);
                }
            }

            var ocProps = outDoc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.OCProperties);
            var ocgs = ocProps?.GetAsArray(PdfName.OCGs);
            if (ocProps == null || ocgs == null) return;

            var newOcgs = new PdfArray();
            for (int i = 0; i < ocgs.Size(); i++)
            {
                var d = ocgs.GetAsDictionary(i);
                if (d != null && replace.ContainsKey(d)) continue;
                newOcgs.Add(ocgs.Get(i));
            }
            ocProps.Put(PdfName.OCGs, newOcgs);

            var d2 = ocProps.GetAsDictionary(PdfName.D);
            if (d2 != null)
            {
                RemapOcgArray(d2, PdfName.ON, replace);
                RemapOcgArray(d2, PdfName.OFF, replace);
                RemapOcgArray(d2, PdfName.Order, replace);
            }
        }

        private static void RemapOcgArray(PdfDictionary d, PdfName key, Dictionary<PdfDictionary, PdfDictionary> replace)
        {
            var arr = d.GetAsArray(key);
            if (arr == null) return;

            var seen = new HashSet<PdfDictionary>();
            var result = new PdfArray();
            for (int i = 0; i < arr.Size(); i++)
            {
                var item = arr.GetAsDictionary(i);
                if (item == null) { result.Add(arr.Get(i)); continue; }
                var canon = replace.TryGetValue(item, out var c) ? c : item;
                if (seen.Add(canon)) result.Add(canon);
            }
            d.Put(key, result);
        }
    }
}
