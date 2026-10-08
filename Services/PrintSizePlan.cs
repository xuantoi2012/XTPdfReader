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
                var (name, dims, extended) = Classify(a, b);
                // an elongated sheet (A3 extended) is one group whatever its length; the group keeps the longest so the paper chosen for it holds them all
                int at = groups.FindIndex(g => g.Name == name && (extended || Math.Abs(g.W - a) <= ToleranceMm && Math.Abs(g.H - b) <= ToleranceMm));
                if (at < 0) { groups.Add((name, dims, a, b, new List<int>())); at = groups.Count - 1; }
                else if (extended && b > groups[at].H) groups[at] = (name, $"{a:0} × {b:0} mm", Math.Max(groups[at].W, a), b, groups[at].Pages);
                groups[at].Pages.Add(i);
            }

            return groups.OrderByDescending(g => g.Pages.Count).ThenByDescending(g => g.W * g.H).Select(g =>
            {
                var (paper, fit, shrink) = Match(g.W, g.H, papers);
                return new PageSizeGroup(g.Name, g.Dims, g.W, g.H, g.Pages, paper, fit, shrink);
            }).ToList();
        }

        private static readonly (string Name, double Short, double Long)[] ASeries =
            { ("A0", 841, 1189), ("A1", 594, 841), ("A2", 420, 594), ("A3", 297, 420), ("A4", 210, 297), ("A5", 148, 210) };

        /// <summary>
        /// The kind of sheet: a standard size ("A3", "Letter"), an ELONGATED A size ("A3 extended": the short side of an A size, the long side longer, as drawings are made long to fit a profile) or a special one
        /// ("297×630"). Elongated sheets of different lengths are one kind: they print on the same plotter, on a custom length of paper.
        /// </summary>
        public static (string Name, string Dims, bool Extended) Classify(double shortMm, double longMm)
        {
            double a = Math.Min(shortMm, longMm), b = Math.Max(shortMm, longMm);
            var (name, dims) = PdfExportService.SizeName(a * 72 / 25.4, b * 72 / 25.4);
            if (!name.Contains('×')) return (name, dims, false);
            foreach (var (baseName, s, l) in ASeries)
                if (Math.Abs(a - s) <= 5 && b > l + 5 && b <= l * 8)
                    return (baseName + " extended", $"{a:0} × {b:0} mm", true);
            return (name, dims, false);
        }

        /// <summary>The sheet is not one of the standard sizes: it needs a decision (a custom paper, or forcing it onto a standard one).</summary>
        public static bool IsSpecial(string className) => className.Contains('×') || className.EndsWith("extended", StringComparison.Ordinal);

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
