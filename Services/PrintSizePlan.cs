using System;
using System.Collections.Generic;
using System.Linq;

namespace XTPdfMergeApp.Services
{
    /// <summary>1 khổ giấy mà máy in có (mm).</summary>
    internal sealed record PaperOption(string Name, double WidthMm, double HeightMm);

    /// <summary>Trang có vừa khổ giấy máy in không.</summary>
    internal enum SizeFit
    {
        /// <summary>Máy in có đúng khổ này.</summary>
        Exact,
        /// <summary>Không có đúng khổ nhưng có khổ lớn hơn chứa được trang nguyên kích thước (in vào giấy lớn hơn).</summary>
        LargerPaper,
        /// <summary>Trang lớn hơn mọi khổ giấy của máy in: chỉ in được khi thu nhỏ.</summary>
        TooBig
    }

    /// <summary>Các trang cùng 1 khổ và khổ giấy máy in phù hợp nhất cho chúng.</summary>
    internal sealed record PageSizeGroup(string Name, string Dims, double WidthMm, double HeightMm, IReadOnlyList<int> PageIndexes,
        PaperOption? Paper, SizeFit Fit, double ShrinkTo)
    {
        public int Count => PageIndexes.Count;
    }

    /// <summary>
    /// Gom trang của 1 lần in theo khổ giấy và đối chiếu với danh sách khổ của máy in — để báo trước (A1 × 12 · Letter × 1 …) trang nào
    /// máy in không có khổ tương ứng, thay vì lỗi lúc in. Thuần tính toán, không phụ thuộc giao diện.
    /// </summary>
    internal static class PrintSizePlan
    {
        private const double ToleranceMm = 3;

        /// <summary>Kích thước trang (point) theo thứ tự trang → các nhóm khổ, nhóm đông trang nhất trước.</summary>
        public static IReadOnlyList<PageSizeGroup> Build(IReadOnlyList<(double Width, double Height)> pagePoints, IReadOnlyList<PaperOption> papers)
        {
            var groups = new List<(string Name, string Dims, double W, double H, List<int> Pages)>();
            for (int i = 0; i < pagePoints.Count; i++)
            {
                double a = Math.Min(pagePoints[i].Width, pagePoints[i].Height) * 25.4 / 72, b = Math.Max(pagePoints[i].Width, pagePoints[i].Height) * 25.4 / 72;
                var (name, dims) = PdfExportService.SizeName(pagePoints[i].Width, pagePoints[i].Height);
                int at = groups.FindIndex(g => g.Name == name && Math.Abs(g.W - a) <= ToleranceMm && Math.Abs(g.H - b) <= ToleranceMm);
                if (at < 0) { groups.Add((name, dims, a, b, new List<int>())); at = groups.Count - 1; }
                groups[at].Pages.Add(i);
            }

            return groups.OrderByDescending(g => g.Pages.Count).ThenByDescending(g => g.W * g.H).Select(g =>
            {
                var (paper, fit, shrink) = Match(g.W, g.H, papers);
                return new PageSizeGroup(g.Name, g.Dims, g.W, g.H, g.Pages, paper, fit, shrink);
            }).ToList();
        }

        /// <summary>Khổ giấy máy in tốt nhất cho trang <paramref name="shortMm"/> × <paramref name="longMm"/> (cạnh ngắn, cạnh dài).</summary>
        public static (PaperOption? Paper, SizeFit Fit, double ShrinkTo) Match(double shortMm, double longMm, IReadOnlyList<PaperOption> papers)
        {
            var normalized = papers.Where(p => p.WidthMm > 0 && p.HeightMm > 0)
                .Select(p => (Paper: p, Short: Math.Min(p.WidthMm, p.HeightMm), Long: Math.Max(p.WidthMm, p.HeightMm))).ToList();
            if (normalized.Count == 0) return (null, SizeFit.TooBig, 0);

            var exact = normalized.Where(p => Math.Abs(p.Short - shortMm) <= ToleranceMm && Math.Abs(p.Long - longMm) <= ToleranceMm)
                .OrderBy(p => Math.Abs(p.Short - shortMm) + Math.Abs(p.Long - longMm)).FirstOrDefault();
            if (exact.Paper != null) return (exact.Paper, SizeFit.Exact, 1);

            var larger = normalized.Where(p => p.Short >= shortMm - ToleranceMm && p.Long >= longMm - ToleranceMm)
                .OrderBy(p => p.Short * p.Long).FirstOrDefault();
            if (larger.Paper != null) return (larger.Paper, SizeFit.LargerPaper, 1);

            var biggest = normalized.OrderByDescending(p => p.Short * p.Long).First();
            return (biggest.Paper, SizeFit.TooBig, Math.Min(biggest.Short / shortMm, biggest.Long / longMm));
        }

        /// <summary>"1, 5, 7-9" từ chỉ số trang 0-based (hiện số trang 1-based) — dùng cho ô Pages: của hộp thoại in.</summary>
        public static string PageList(IReadOnlyList<int> zeroBased)
        {
            var sorted = zeroBased.OrderBy(i => i).Select(i => i + 1).ToList();
            var parts = new List<string>();
            for (int i = 0; i < sorted.Count; )
            {
                int j = i;
                while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
                parts.Add(j > i ? $"{sorted[i]}-{sorted[j]}" : sorted[i].ToString());
                i = j + 1;
            }
            return string.Join(", ", parts);
        }

        /// <summary>Dòng mô tả 1 nhóm: "A1 × 12".</summary>
        public static string Label(PageSizeGroup g) => $"{g.Name} × {g.Count}";

        public static string Status(PageSizeGroup g) => g.Fit switch
        {
            SizeFit.Exact => $"printer has {g.Paper!.Name}",
            SizeFit.LargerPaper => $"no exact paper, prints on {g.Paper!.Name}",
            _ => g.Paper == null ? "this printer lists no paper sizes"
                : $"larger than any paper ({g.Paper.Name} is the biggest): shrinks to {g.ShrinkTo * 100:0}%"
        };

        /// <summary>Báo cáo văn bản để gửi cho người phụ trách máy in.</summary>
        public static string Report(IReadOnlyList<PageSizeGroup> groups, string printer)
            => $"Page sizes for printer \"{printer}\":\n" + string.Join("\n", groups.Select(g =>
                $"{Label(g)} ({g.Dims}) — {Status(g)}; pages {PageList(g.PageIndexes)}"));
    }
}
