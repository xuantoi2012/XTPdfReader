using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XTPdfMergeApp.Services
{
    /// <summary>Cách xử lý layer (OCG) khi ghép file.</summary>
    public enum MergeLayerMode
    {
        /// <summary>Giữ layer riêng theo từng file nguồn; bảng Layers của file kết quả hiện dạng cây: file → layer của file đó.</summary>
        Separate,
        /// <summary>Layer cùng tên (bỏ tiền tố xref "Xref|") giữa các file gộp thành 1.</summary>
        ByName,
        /// <summary>Chỉ các layer trong <see cref="MergeOptions.KeepLayers"/> được giữ riêng (gộp theo tên); mọi layer còn lại gộp vào 1 layer.</summary>
        KeepSome
    }

    /// <summary>
    /// Tuỳ chọn khi lưu file ghép (hộp thoại "Save merged file", docs/UI_REDESIGN.md P7).
    /// Chế độ layer: <see cref="MergeLayers"/> tắt = <see cref="MergeLayerMode.Separate"/>; bật và <see cref="KeepLayers"/> = null = <see cref="MergeLayerMode.ByName"/>;
    /// bật và có <see cref="KeepLayers"/> = <see cref="MergeLayerMode.KeepSome"/>.
    /// </summary>
    public sealed record MergeOptions(bool FileBookmarks, bool KeepBookmarks, bool MergeLayers, bool PageNumbers, bool Optimize,
        IReadOnlyList<string>? KeepLayers = null, string CollapseLayerName = MergeOptions.DefaultCollapseName, string KeepLayerPrefix = "",
        IReadOnlyDictionary<string, string>? LayerRenames = null)
    {
        public const string DefaultCollapseName = "Other layers";

        public static readonly MergeOptions Default = new(true, true, true, false, true);

        public MergeLayerMode LayerMode => !MergeLayers ? MergeLayerMode.Separate : KeepLayers != null ? MergeLayerMode.KeepSome : MergeLayerMode.ByName;
    }

    /// <summary>1 bookmark của file kết quả: trỏ tới trang <see cref="OutPage"/> (1-based trong file ghép).</summary>
    public sealed record MergeOutlineNode(string Title, int OutPage, IReadOnlyList<MergeOutlineNode> Children);

    /// <summary>
    /// Lên kế hoạch bookmark của file ghép từ danh sách trang (theo thứ tự) — dùng cho cả bản xem trước trong hộp thoại lẫn lúc ghi thật:
    /// mỗi đoạn trang liên tiếp của 1 file có 1 bookmark theo tên file; bookmark gốc của file (nếu giữ) lồng dưới bookmark đó, trỏ tới trang mới.
    /// </summary>
    public static class MergeOutlinePlanner
    {
        public static IReadOnlyList<MergeOutlineNode> Build(IReadOnlyList<(string SourcePath, int PageNumber)> pages, MergeOptions options)
        {
            var result = new List<MergeOutlineNode>();
            if (!options.FileBookmarks && !options.KeepBookmarks) return result;

            // trang nguồn → trang đầu tiên của nó trong file ghép (theo từng file)
            var firstOut = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < pages.Count; i++)
            {
                var (path, number) = pages[i];
                if (!firstOut.TryGetValue(path, out var map)) firstOut[path] = map = new Dictionary<int, int>();
                map.TryAdd(number, i + 1);
            }

            var keptFor = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // bookmark gốc chỉ lồng dưới đoạn đầu tiên của file
            int index = 0;
            while (index < pages.Count)
            {
                string path = pages[index].SourcePath;
                int start = index;
                while (index < pages.Count && string.Equals(pages[index].SourcePath, path, StringComparison.OrdinalIgnoreCase)) index++;

                var children = new List<MergeOutlineNode>();
                if (options.KeepBookmarks && keptFor.Add(path))
                {
                    try { children.AddRange(Convert(PdfOutlineService.ReadBookmarks(path), firstOut[path])); }
                    catch { /* file không đọc được bookmark: bỏ qua */ }
                }

                if (options.FileBookmarks)
                    result.Add(new MergeOutlineNode(Path.GetFileNameWithoutExtension(path), start + 1, children));
                else
                    result.AddRange(children);
            }
            return result;
        }

        private static IEnumerable<MergeOutlineNode> Convert(IReadOnlyList<PdfBookmarkNode> nodes, Dictionary<int, int> map)
        {
            foreach (var node in nodes)
            {
                var children = Convert(node.Children, map).ToList();
                if (node.PageNumber is int page && map.TryGetValue(page, out int outPage))
                    yield return new MergeOutlineNode(node.Title, outPage, children);
                else
                    foreach (var child in children) yield return child; // trang không có trong file ghép: giữ con, bỏ mục này
            }
        }
    }
}
