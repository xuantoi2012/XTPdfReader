using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using iText.Kernel.Pdf;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// Quy tắc gộp layer (OCG) khi ghép: từ tên layer gốc suy ra (khoá gộp, tên hiển thị). Hai layer cùng khoá là một.
    /// Một policy dùng cho cả lần ghép (nó nhớ file nguồn hiện tại và layer đầu tiên của mỗi khoá).
    /// </summary>
    internal sealed class LayerMergePolicy
    {
        private readonly MergeLayerMode _mode;
        private readonly HashSet<string> _keep;
        private readonly string _collapseName;
        private readonly string _keepPrefix;
        private readonly IReadOnlyDictionary<string, string>? _renames;
        private readonly string _prefix;          // kiểu cũ: chỉ giữ layer có tên rút gọn bắt đầu bằng prefix
        private readonly string _collapseOthersTo;

        // Separate: layer canonical của từng file nguồn theo thứ tự xuất hiện → dựng cây /Order.
        private readonly List<string> _scopeOrder = new();
        private readonly Dictionary<string, List<PdfDictionary>> _byScope = new(StringComparer.OrdinalIgnoreCase);

        private LayerMergePolicy(MergeLayerMode mode, IEnumerable<string>? keep, string collapseName, string prefix, string collapseOthersTo, string keepPrefix = "",
            IReadOnlyDictionary<string, string>? renames = null)
        {
            _mode = mode;
            _keep = new HashSet<string>(keep ?? Array.Empty<string>(), StringComparer.Ordinal);
            _collapseName = string.IsNullOrWhiteSpace(collapseName) ? MergeOptions.DefaultCollapseName : collapseName.Trim();
            _keepPrefix = keepPrefix ?? "";
            _renames = renames is { Count: > 0 } ? renames : null;
            _prefix = prefix;
            _collapseOthersTo = collapseOthersTo;
        }

        /// <summary>File nguồn đang được xử lý (Separate dùng để tách layer theo file).</summary>
        public string Scope { get; set; } = "";

        /// <summary>Layer gộp chung của KeepSome (null nếu chưa có layer nào rơi vào đó).</summary>
        public PdfDictionary? CollapseTarget { get; private set; }

        public bool IsSeparate => _mode == MergeLayerMode.Separate;
        public bool IsKeepSome => _mode == MergeLayerMode.KeepSome;

        /// <summary>null = không xử lý layer. <paramref name="options"/> (nếu có) quyết định; không thì dùng cờ cũ.</summary>
        public static LayerMergePolicy? Create(bool mergeLayersByName, string prefix, string collapseOthersTo, MergeOptions? options)
        {
            if (options != null)
                return new LayerMergePolicy(options.LayerMode, options.KeepLayers, options.CollapseLayerName, "", "", options.KeepLayerPrefix, options.LayerRenames);
            return mergeLayersByName ? new LayerMergePolicy(MergeLayerMode.ByName, null, "", prefix, collapseOthersTo) : null;
        }

        /// <summary>Tên rút gọn: phần sau dấu "|" cuối cùng (layer của xref/block có dạng "TenXref|TenLayer").</summary>
        public static string ShortName(string rawName)
        {
            int idx = rawName.LastIndexOf('|');
            return idx >= 0 ? rawName[(idx + 1)..] : rawName;
        }

        /// <summary>(Khoá gộp, tên cần ghi lên OCG).</summary>
        public (string Key, string Display) Resolve(string rawName)
        {
            switch (_mode)
            {
                case MergeLayerMode.Separate:
                    return (Scope + "\u0001" + rawName, rawName); // cùng tên ở file khác = layer khác; cùng tên trong 1 file = 1
                case MergeLayerMode.KeepSome:
                {
                    string shortName = ShortName(rawName);
                    bool keep = _keep.Contains(shortName) ||
                                (_keepPrefix.Length > 0 && shortName.StartsWith(_keepPrefix, StringComparison.OrdinalIgnoreCase));
                    return keep ? (shortName, shortName) : ("\u0002" + _collapseName, _collapseName);
                }
                default:
                {
                    string shortName = ShortName(rawName);
                    if (_prefix.Length > 0 && shortName.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)) return (shortName, shortName);
                    if (_prefix.Length > 0 && _collapseOthersTo.Length > 0) return (_collapseOthersTo, _collapseOthersTo);
                    return (rawName, rawName); // không lọc theo prefix: gộp theo đúng tên gốc
                }
            }
        }

        /// <summary>Gọi khi 1 layer canonical mới được tạo.</summary>
        public void OnNewCanonical(PdfDictionary ocg)
        {
            if (IsKeepSome && CollapseTarget == null && ocg.GetAsString(PdfName.Name)?.ToUnicodeString() == _collapseName) CollapseTarget = ocg;
            if (!IsSeparate) return;
            if (!_byScope.TryGetValue(Scope, out var list))
            {
                _byScope[Scope] = list = new List<PdfDictionary>();
                _scopeOrder.Add(Scope);
            }
            list.Add(ocg);
        }

        /// <summary>Đổi tên các layer của file kết quả theo bảng người dùng chọn (tên hiện tại → tên mới).</summary>
        private void RenameResultLayers(PdfDictionary ocProps)
        {
            if (_renames == null || ocProps.GetAsArray(PdfName.OCGs) is not { } all) return;
            for (int i = 0; i < all.Size(); i++)
            {
                if (all.GetAsDictionary(i) is not { } ocg) continue;
                string name = ocg.GetAsString(PdfName.Name)?.ToUnicodeString() ?? "";
                if (_renames.TryGetValue(name, out var renamed) && renamed.Length > 0)
                {
                    ocg.Put(PdfName.Name, new PdfString(renamed, iText.IO.Font.PdfEncodings.UNICODE_BIG));
                    ocg.SetModified();
                }
            }
        }

        /// <summary>Sau khi gộp: KeepSome → layer gộp chung luôn bật; Separate → /Order thành cây file → layer.</summary>
        public void Finish(PdfDocument outDoc, IReadOnlyDictionary<PdfDictionary, PdfDictionary> replace)
        {
            var ocProps = outDoc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.OCProperties);
            var d = ocProps?.GetAsDictionary(PdfName.D);
            if (ocProps == null || d == null) return;
            RenameResultLayers(ocProps);

            if (IsKeepSome && CollapseTarget != null && d.GetAsArray(PdfName.OFF) is { } off)
            {
                // Bucket gộp chứa layer của nhiều file: không để 1 layer nguồn đang tắt làm tắt cả bucket.
                var kept = new PdfArray();
                for (int i = 0; i < off.Size(); i++)
                    if (!ReferenceEquals(off.GetAsDictionary(i), CollapseTarget)) kept.Add(off.Get(i));
                d.Put(PdfName.OFF, kept);
            }

            if (IsSeparate && _scopeOrder.Count >= 2) BuildTree(ocProps, d, replace);
        }

        private void BuildTree(PdfDictionary ocProps, PdfDictionary d, IReadOnlyDictionary<PdfDictionary, PdfDictionary> replace)
        {
            var all = ocProps.GetAsArray(PdfName.OCGs);
            if (all == null) return;
            var present = new HashSet<PdfDictionary>();
            for (int i = 0; i < all.Size(); i++) if (all.GetAsDictionary(i) is { } o) present.Add(o);

            var order = new PdfArray();
            var placed = new HashSet<PdfDictionary>();
            foreach (string scope in _scopeOrder)
            {
                var group = new PdfArray();
                group.Add(new PdfString(Path.GetFileName(scope), iText.IO.Font.PdfEncodings.UNICODE_BIG));
                foreach (var ocg in _byScope[scope])
                {
                    var target = replace.TryGetValue(ocg, out var c) ? c : ocg;
                    if (present.Contains(target) && placed.Add(target)) group.Add(target);
                }
                if (group.Size() > 1) order.Add(group);
            }
            // Layer có trong /OCGs nhưng không thuộc file nào (không được trang nào tham chiếu): để ở gốc, không để mất khỏi bảng.
            for (int i = 0; i < all.Size(); i++)
                if (all.GetAsDictionary(i) is { } o && !placed.Contains(o)) order.Add(o);
            d.Put(PdfName.Order, order);
        }
    }
}
