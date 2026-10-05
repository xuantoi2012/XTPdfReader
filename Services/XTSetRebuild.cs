using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using iText.Kernel.Pdf;

namespace XTPdfMergeApp.Services
{
    /// <summary>1 thành phần của bộ hồ sơ trong file .xtset. Đặc tả: XTToolbox/docs/XT_SET_FILE.md.</summary>
    internal sealed class XTSetItem
    {
        public string Kind { get; set; } = "dwg";
        public string Pdf { get; set; } = "";
        public string Dwg { get; set; } = "";
        public string Label { get; set; } = "";
        public List<XTSheetPageInfo> Sheets { get; set; } = new();
    }

    internal sealed class XTSetLayers
    {
        public string Mode { get; set; } = "KeepSome";
        public List<string> Keep { get; set; } = new();
        public string Prefix { get; set; } = "";
        public string Collapse { get; set; } = "0";
    }

    internal sealed class XTSetRecipe
    {
        public int Version { get; set; }
        public string App { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string ProjectFile { get; set; } = "";
        public string Output { get; set; } = "";
        public XTSetLayers Layers { get; set; } = new();
        public List<XTSetItem> Items { get; set; } = new();
    }

    /// <summary>Đọc file .xtset và dựng lại (rebuild) PDF ghép từ các thành phần theo công thức.</summary>
    internal static class XTSetRebuild
    {
        public const string Extension = ".xtset";

        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

        public static bool IsSetFile(string path) => string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

        public static XTSetRecipe? Read(string xtsetPath)
        {
            try
            {
                var recipe = JsonSerializer.Deserialize<XTSetRecipe>(File.ReadAllText(xtsetPath), Json);
                return recipe is { Version: >= 1 } && recipe.Items.Count > 0 && recipe.Output.Length > 0 ? recipe : null;
            }
            catch { return null; }
        }

        /// <summary>Đường dẫn tuyệt đối của 1 đường dẫn trong recipe (tương đối so với thư mục chứa .xtset).</summary>
        public static string Resolve(string xtsetPath, string relative)
            => Path.GetFullPath(Path.IsPathRooted(relative) ? relative : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(xtsetPath)) ?? "", relative));

        public static string OutputPath(string xtsetPath, XTSetRecipe recipe) => Resolve(xtsetPath, recipe.Output);

        /// <summary>Các PDF thành phần không còn trên đĩa.</summary>
        public static List<string> MissingParts(string xtsetPath, XTSetRecipe recipe)
            => recipe.Items.Select(i => Resolve(xtsetPath, i.Pdf)).Where(p => !File.Exists(p)).ToList();

        /// <summary>Tuỳ chọn ghép theo công thức: layer như đã chọn lúc in; bookmark do <see cref="BuildOutline"/> dựng nên tắt bookmark theo file.</summary>
        public static MergeOptions OptionsFor(XTSetRecipe recipe, bool optimize = true)
        {
            bool separate = string.Equals(recipe.Layers.Mode, "Separate", StringComparison.OrdinalIgnoreCase);
            bool keepSome = string.Equals(recipe.Layers.Mode, "KeepSome", StringComparison.OrdinalIgnoreCase);
            return new MergeOptions(false, false, !separate, false, optimize,
                keepSome ? recipe.Layers.Keep : null,
                string.IsNullOrWhiteSpace(recipe.Layers.Collapse) ? "0" : recipe.Layers.Collapse,
                keepSome ? recipe.Layers.Prefix ?? "" : "");
        }

        /// <summary>Dựng lại PDF kết quả. false + <paramref name="error"/> nếu lỗi (file cũ giữ nguyên nhờ ghi qua file tạm).</summary>
        public static bool Rebuild(string xtsetPath, XTSetRecipe recipe, out string error, IProgress<(int Done, int Total)>? progress = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            var parts = new List<(XTSetItem Item, string Pdf, int Pages, IReadOnlyDictionary<int, XTSheetPageInfo> Infos)>();
            foreach (var item in recipe.Items)
            {
                string pdf = Resolve(xtsetPath, item.Pdf);
                if (!File.Exists(pdf)) { error = "Missing part: " + pdf; return false; }
                int pages;
                try
                {
                    using var doc = new PdfDocument(new PdfReader(pdf));
                    pages = doc.GetNumberOfPages();
                }
                catch (Exception ex) { error = $"Cannot read {Path.GetFileName(pdf)}: {ex.Message}"; return false; }
                parts.Add((item, pdf, pages, XTSheetIndex.Read(pdf)));
            }

            var pageList = parts.SelectMany(p => Enumerable.Range(1, p.Pages).Select(n => (p.Pdf, n))).ToList();
            var outline = BuildOutline(parts.Select(p => (p.Item, p.Pages, p.Infos)).ToList());
            string output = OutputPath(xtsetPath, recipe);
            return XTPdfMerger.TryMergePages(pageList, output, out error, null, progress: progress, cancellationToken: cancellationToken,
                options: OptionsFor(recipe), outline: outline);
        }

        // ── Bookmark từ thông tin sheet: Hạng mục → Subset → Sheet (giống XTBookmarkBuilder bên XTToolbox) ──

        private sealed class Node
        {
            public string Title = "";
            public int Page;
            public List<Node> Children = new();
            public MergeOutlineNode ToRecord() => new(Title, Page, Children.Select(c => c.ToRecord()).ToList());
        }

        /// <summary>Cây bookmark của file ghép. Tờ lót (divider) ngay trước 1 nhóm được dùng làm trang đích của nhóm đó.</summary>
        public static IReadOnlyList<MergeOutlineNode> BuildOutline(
            IReadOnlyList<(XTSetItem Item, int Pages, IReadOnlyDictionary<int, XTSheetPageInfo> Infos)> parts)
        {
            var roots = new List<Node>();
            Node? group = null, subset = null;
            string? lastGroup = null, lastSubset = null;
            var dividers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int page = 1;

            foreach (var (item, pages, infos) in parts)
            {
                switch (item.Kind.ToLowerInvariant())
                {
                    case "cover": roots.Add(new Node { Title = "Bìa", Page = page }); break;
                    case "toc": roots.Add(new Node { Title = "Mục lục", Page = page }); break;
                    case "divider": if (item.Label.Length > 0) dividers[item.Label] = page; break;
                    default:
                        for (int p = 1; p <= pages; p++)
                        {
                            if (infos.TryGetValue(p, out var info))
                            {
                                string g = info.Group ?? "", s = info.Subset ?? "";
                                if (!string.Equals(g, lastGroup ?? "", StringComparison.OrdinalIgnoreCase) || lastGroup == null)
                                {
                                    group = new Node { Title = g.Length > 0 ? g : "(Chưa phân Hạng mục)", Page = dividers.Remove(g, out int dp) ? dp : page + p - 1 };
                                    roots.Add(group);
                                    subset = null; lastGroup = g; lastSubset = null;
                                }
                                if (!string.Equals(s, lastSubset ?? "", StringComparison.OrdinalIgnoreCase) || lastSubset == null && s.Length > 0)
                                {
                                    subset = s.Length == 0 ? null : new Node { Title = s, Page = dividers.Remove(s, out int sp) ? sp : page + p - 1 };
                                    if (subset != null) (group?.Children ?? roots).Add(subset);
                                    lastSubset = s;
                                }
                                string no = string.IsNullOrWhiteSpace(info.No) ? "" : info.No + " - ";
                                string title = string.IsNullOrWhiteSpace(info.Title) ? info.Layout : info.Title;
                                (subset?.Children ?? group?.Children ?? roots).Add(new Node { Title = no + title, Page = page + p - 1 });
                            }
                        }
                        break;
                }
                page += pages;
            }
            return roots.Select(r => r.ToRecord()).ToList();
        }
    }
}
