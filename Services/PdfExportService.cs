using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace XTPdfMergeApp.Services
{
    public enum SplitMode { None, PageSize, EveryN, Bookmark, Ranges }

    /// <summary>1 file sẽ được tạo khi Export: nhãn, khổ (nếu tách theo khổ giấy), các trang (theo thứ tự) và tên file.</summary>
    public sealed class ExportPart
    {
        public required string Label { get; init; }
        public string Dims { get; init; } = "";
        public required IReadOnlyList<(string Path, int Page)> Pages { get; init; }
        public required string FileName { get; set; }
        public bool Enabled { get; set; } = true;
        public string PagesText => Pages.Count == 1 ? "1 page" : Pages.Count + " pages";
    }

    /// <summary>Lên kế hoạch tách file (theo khổ giấy / mỗi N trang / theo bookmark cấp 1 / theo danh sách khoảng trang) và thực hiện Export.</summary>
    public static class PdfExportService
    {
        private static readonly (string Name, double W, double H)[] IsoSizes =
        {
            ("A0", 841, 1189), ("A1", 594, 841), ("A2", 420, 594), ("A3", 297, 420), ("A4", 210, 297), ("A5", 148, 210),
            ("Letter", 216, 279), ("Legal", 216, 356), ("Tabloid", 279, 432), ("ANSI C", 432, 559), ("ANSI D", 559, 864), ("ANSI E", 864, 1118)
        };

        /// <summary>Tên khổ giấy (A0…A5 hoặc "W × H mm") của trang có kích thước point (đã xoay).</summary>
        public static (string Name, string Dims) SizeName(double widthPt, double heightPt)
        {
            double a = Math.Min(widthPt, heightPt) * 25.4 / 72, b = Math.Max(widthPt, heightPt) * 25.4 / 72;
            foreach (var (name, w, h) in IsoSizes)
                if (Math.Abs(a - w) <= 5 && Math.Abs(b - h) <= 5) return (name, $"{w:0} × {h:0} mm");
            return ($"{a:0}×{b:0}", $"{a:0} × {b:0} mm");
        }

        public static async Task<IReadOnlyList<ExportPart>> PlanAsync(IReadOnlyList<(string Path, int Page)> pages, string baseName,
            SplitMode mode, int everyN, string rangesText)
        {
            string Name(string suffix) => Sanitize(baseName + (suffix.Length > 0 ? " - " + suffix : "")) + ".pdf";
            var parts = new List<ExportPart>();
            if (pages.Count == 0) return parts;

            switch (mode)
            {
                case SplitMode.PageSize:
                {
                    var sizes = new Dictionary<string, (double W, double H)[]?>(StringComparer.OrdinalIgnoreCase);
                    foreach (string path in pages.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                        sizes[path] = await PdfThumbnailService.GetPageSizesAsync(path);
                    var groups = new List<(string Name, string Dims, List<(string, int)> Pages)>();
                    foreach (var (path, page) in pages)
                    {
                        var arr = sizes[path];
                        (string name, string dims) = arr != null && page - 1 < arr.Length && arr[page - 1].W > 0
                            ? SizeName(arr[page - 1].W, arr[page - 1].H) : ("Unknown", "");
                        var group = groups.FirstOrDefault(g => g.Name == name);
                        if (group.Pages == null) groups.Add(group = (name, dims, new List<(string, int)>()));
                        group.Pages.Add((path, page));
                    }
                    foreach (var g in groups)
                        parts.Add(new ExportPart { Label = g.Name, Dims = g.Dims, Pages = g.Pages, FileName = Name(g.Name) });
                    break;
                }
                case SplitMode.EveryN:
                {
                    int n = Math.Max(1, everyN);
                    for (int i = 0; i < pages.Count; i += n)
                    {
                        var chunk = pages.Skip(i).Take(n).ToList();
                        parts.Add(new ExportPart { Label = $"Pages {i + 1}–{i + chunk.Count}", Pages = chunk, FileName = Name($"{i + 1}-{i + chunk.Count}") });
                    }
                    break;
                }
                case SplitMode.Bookmark:
                {
                    var starts = new List<(int Index, string Title)>();
                    foreach (string path in pages.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        IReadOnlyList<PdfBookmarkNode> marks;
                        try { marks = await Task.Run(() => PdfOutlineService.ReadBookmarks(path)); }
                        catch { continue; }
                        foreach (var mark in marks)
                        {
                            if (mark.PageNumber is not int number) continue;
                            int index = pages.ToList().FindIndex(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase) && p.Page == number);
                            if (index >= 0) starts.Add((index, mark.Title));
                        }
                    }
                    starts = starts.OrderBy(s => s.Index).GroupBy(s => s.Index).Select(g => g.First()).ToList();
                    if (starts.Count == 0 || starts[0].Index > 0) starts.Insert(0, (0, "Front"));
                    for (int i = 0; i < starts.Count; i++)
                    {
                        int from = starts[i].Index, to = i + 1 < starts.Count ? starts[i + 1].Index : pages.Count;
                        if (to <= from) continue;
                        parts.Add(new ExportPart { Label = starts[i].Title, Pages = pages.Skip(from).Take(to - from).ToList(), FileName = Name($"{parts.Count + 1:00} {starts[i].Title}") });
                    }
                    break;
                }
                case SplitMode.Ranges:
                {
                    int k = 0;
                    foreach (string segment in rangesText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var numbers = PdfPrintService.ParseRange(segment.EndsWith('-') ? segment + pages.Count : segment, pages.Count);
                        if (numbers == null || numbers.Count == 0) continue;
                        k++;
                        parts.Add(new ExportPart { Label = segment, Pages = numbers.Select(n => pages[n - 1]).ToList(), FileName = Name($"part {k} ({segment.Replace(" ", "")})") });
                    }
                    break;
                }
                default:
                    parts.Add(new ExportPart { Label = "All pages", Pages = pages, FileName = Name("export") });
                    break;
            }
            return parts;
        }

        public static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }

        /// <summary>Tên layer đang tắt (ở ít nhất 1 file) theo trạng thái xem hiện tại của các file này.</summary>
        public static HashSet<string> CurrentHiddenNames(IEnumerable<string> paths)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var hidden = PdfLayerStateStore.GetHiddenOverride(path, out _);
                if (hidden == null) continue;
                try
                {
                    var info = PdfLayerService.ReadLayers(path);
                    foreach (string id in hidden)
                        if (info.Names.TryGetValue(id, out var name)) names.Add(name);
                }
                catch { }
            }
            return names;
        }

        /// <summary>Ghi các file. Trả về (số file đã ghi, thông báo lỗi đầu tiên hoặc "").</summary>
        public static async Task<(int Written, string Error)> ExportAsync(IReadOnlyList<ExportPart> parts, string folder, bool flatten,
            IReadOnlySet<string> hiddenNames, bool optimize, IProgress<(int Done, int Total)>? progress = null)
        {
            var enabled = parts.Where(p => p.Enabled).ToList();
            int prepared = 0;
            try
            {
                var byOutput = enabled.ToDictionary(part => Path.GetFullPath(Path.Combine(folder, part.FileName)), StringComparer.OrdinalIgnoreCase);
                await PdfFileTransaction.RunAsync(byOutput.Keys.ToList(), (output, stage) => Task.Run(() =>
                {
                    var part = byOutput[output];
                    progress?.Report((prepared, enabled.Count));
                    var options = new MergeOptions(false, true, true, false, optimize);
                    if (!XTPdfMerger.TryMergePages(part.Pages.Select(p => (p.Path, p.Page)).ToList(), stage, out var error, null, true, options: options))
                        throw new IOException(error);
                    string temp = stage + ".flat.tmp";
                    try
                    {
                        if (flatten)
                        {
                            if (!PdfLayerFlattener.Flatten(stage, temp, hiddenNames))
                                throw new IOException("Could not flatten layers in \"" + part.FileName + "\". No files were exported. Choose Keep layers if you want an editable copy.");
                            File.Move(temp, stage, overwrite: true);
                        }
                        else if (hiddenNames.Count > 0) PdfLayerService.SetDefaultVisibilityByName(stage, hiddenNames);
                        prepared++;
                    }
                    finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
                }));
            }
            catch (Exception ex) { return (0, ex.Message); }
            progress?.Report((enabled.Count, enabled.Count));
            return (enabled.Count, "");
        }
    }
}
