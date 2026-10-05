using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Navigation;

namespace XTPdfMergeApp.Services
{
    /// <summary>1 liên kết cần tạo: trên trang <see cref="Page"/>, vùng <see cref="Rect"/> (chữ là số hiệu) dẫn tới trang <see cref="TargetPage"/>.</summary>
    internal sealed record SheetLink(int Page, Rectangle Rect, int TargetPage, string Text);

    /// <summary>
    /// Mục lục và tham chiếu chéo bấm được: tìm số hiệu bản vẽ (từ thông tin sheet) trong chữ của các trang KHÁC trang của chính bản vẽ đó
    /// (dòng mục lục, "xem KT-05"…) rồi gắn liên kết nhảy tới bản vẽ. Chỉ chạy được khi trang có lớp chữ thật.
    /// </summary>
    internal static class XTSheetLinks
    {
        private const int MinNumberLength = 3; // số hiệu quá ngắn ("A1") dễ khớp nhầm

        /// <summary>Số hiệu (chuẩn hoá) → trang của bản vẽ; số hiệu trùng nhau hoặc quá ngắn bị bỏ.</summary>
        public static Dictionary<string, int> NumberToPage(IReadOnlyDictionary<int, XTSheetPageInfo> infos)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var duplicated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (page, info) in infos.OrderBy(kv => kv.Key))
            {
                string no = info.No.Trim();
                if (no.Length < MinNumberLength) continue;
                if (!map.TryAdd(no, page)) duplicated.Add(no);
            }
            foreach (string no in duplicated) map.Remove(no);
            return map;
        }

        public static List<SheetLink> Find(string path, IReadOnlyDictionary<int, XTSheetPageInfo> infos)
        {
            var result = new List<SheetLink>();
            var map = NumberToPage(infos);
            if (map.Count == 0) return result;
            var pattern = new Regex(@"(?<![\p{L}\p{N}])(?:" + string.Join("|", map.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)) + @")(?![\p{L}\p{N}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            var properties = new ReaderProperties();
            if (PdfThumbnailService.TryGetDocumentPassword(path) is { Length: > 0 } password)
                properties.SetPassword(Encoding.UTF8.GetBytes(password));
            using var doc = new PdfDocument(new PdfReader(path, properties));
            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
            {
                var chunks = new ChunkListener();
                try { new PdfCanvasProcessor(chunks).ProcessPageContent(doc.GetPage(p)); }
                catch { continue; } // trang không đọc được chữ: bỏ qua
                foreach (var chunk in chunks.Chunks)
                {
                    foreach (Match m in pattern.Matches(chunk.GetText()))
                    {
                        if (!map.TryGetValue(m.Value, out int target) || target == p) continue; // bản vẽ không tự liên kết tới chính nó
                        var rect = RectOf(chunk, m.Index, m.Length);
                        if (rect != null) result.Add(new SheetLink(p, rect, target, m.Value));
                    }
                }
            }
            return result;
        }

        /// <summary>Gắn liên kết vào <paramref name="doc"/> (append mode); bỏ qua chỗ đã có liên kết chồng lên. Trả về số liên kết đã thêm.</summary>
        public static int Apply(PdfDocument doc, IReadOnlyList<SheetLink> links)
        {
            int added = 0;
            foreach (var group in links.GroupBy(l => l.Page))
            {
                if (group.Key < 1 || group.Key > doc.GetNumberOfPages()) continue;
                var page = doc.GetPage(group.Key);
                var existing = page.GetAnnotations().Where(a => PdfName.Link.Equals(a.GetSubtype()))
                    .Select(a => a.GetRectangle()?.ToRectangle()).Where(r => r != null).ToList();
                foreach (var link in group)
                {
                    if (link.TargetPage < 1 || link.TargetPage > doc.GetNumberOfPages()) continue;
                    if (existing.Any(r => r!.Overlaps(link.Rect))) continue;
                    var annotation = new PdfLinkAnnotation(link.Rect);
                    annotation.SetAction(PdfAction.CreateGoTo(PdfExplicitDestination.CreateFit(doc.GetPage(link.TargetPage))));
                    annotation.SetBorder(new PdfArray(new[] { new PdfNumber(0), new PdfNumber(0), new PdfNumber(0) }));
                    annotation.SetFlags(PdfAnnotation.PRINT);
                    page.AddAnnotation(annotation);
                    existing.Add(link.Rect);
                    added++;
                }
                page.GetPdfObject().SetModified();
            }
            return added;
        }

        /// <summary>Ghi liên kết vào chính file (1 dòng lịch sử). Trả về số liên kết thêm được.</summary>
        public static int WriteInPlace(string path, IReadOnlyList<SheetLink> links)
        {
            int added = 0;
            if (links.Count == 0) return 0;
            PdfPageEditService.EditInPlace(path, $"Sheet links added ({links.Count})", doc => added = Apply(doc, links));
            return added;
        }

        // ── Đọc chữ kèm vị trí ────────────────────────────────────────

        private sealed class ChunkListener : IEventListener
        {
            public readonly List<TextRenderInfo> Chunks = new();
            public void EventOccurred(IEventData data, EventType type) { if (data is TextRenderInfo info) { info.PreserveGraphicsState(); Chunks.Add(info); } }
            public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };
        }

        /// <summary>Hình chữ nhật (không gian trang) của đoạn <paramref name="length"/> ký tự bắt đầu ở <paramref name="start"/> trong 1 khối chữ.</summary>
        private static Rectangle? RectOf(TextRenderInfo chunk, int start, int length)
        {
            var chars = chunk.GetCharacterRenderInfos();
            if (chars == null || start < 0 || start + length > chars.Count) return null;
            var first = chars[start];
            var last = chars[start + length - 1];
            var points = new[] { first.GetDescentLine().GetStartPoint(), first.GetAscentLine().GetStartPoint(), last.GetDescentLine().GetEndPoint(), last.GetAscentLine().GetEndPoint() };
            float x0 = points.Min(v => v.Get(0)), x1 = points.Max(v => v.Get(0)), y0 = points.Min(v => v.Get(1)), y1 = points.Max(v => v.Get(1));
            return x1 - x0 < 0.5f || y1 - y0 < 0.5f ? null : new Rectangle(x0, y0, x1 - x0, y1 - y0);
        }
    }
}
