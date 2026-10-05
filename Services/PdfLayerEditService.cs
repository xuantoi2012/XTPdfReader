using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using iText.Kernel.Pdf;

namespace XTPdfMergeApp.Services
{
    /// <summary>1 dòng của bảng quản lý layer: layer (id OCG) sẽ mang tên <see cref="NewName"/>. Nhiều layer cùng tên mới = gộp thành 1.</summary>
    public sealed record LayerRename(string OcgId, string NewName);

    /// <summary>
    /// Đổi tên / gộp layer (OCG) của 1 file đã có. Gộp = mọi tham chiếu tới các layer cùng tên mới được chuyển sang layer đầu tiên của nhóm
    /// (/Properties của trang và của Form XObject lồng nhau, /OC của XObject và chú thích, OCMD), rồi bỏ các layer thừa khỏi /OCGs và /D.
    /// Nội dung trang không bị đụng tới (BDC chỉ tham chiếu theo khoá resource).
    /// </summary>
    public static class PdfLayerEditService
    {
        private static readonly PdfName OC = new("OC");
        private static readonly PdfName OCGsKey = PdfName.OCGs;
        private static readonly PdfName AS = new("AS");
        private static readonly PdfName Locked = new("Locked");
        private static readonly PdfName RBGroups = new("RBGroups");

        /// <summary>Số layer sau khi áp <paramref name="edits"/> lên các layer hiện có (để xem trước).</summary>
        public static int CountAfter(IEnumerable<string> allIds, IReadOnlyList<LayerRename> edits, IReadOnlyDictionary<string, string> currentNames)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var renamed = edits.ToDictionary(e => e.OcgId, e => e.NewName.Trim(), StringComparer.Ordinal);
            foreach (string id in allIds)
                names.Add(renamed.TryGetValue(id, out var n) && n.Length > 0 ? n : currentNames.GetValueOrDefault(id, id));
            return names.Count;
        }

        /// <summary>Ghi thẳng vào <paramref name="path"/> (nối bản cập nhật vào cuối file, như các thao tác sửa trang).</summary>
        public static void EditInPlace(string path, IReadOnlyList<LayerRename> edits)
            => PdfPageEditService.EditInPlace(path, $"Layers: {edits.Count} renamed or merged", doc => Apply(doc, edits));

        /// <summary>Ghi bản sao đã đổi/gộp layer ra <paramref name="output"/>; file gốc không đổi.</summary>
        public static void SaveCopy(string path, string output, IReadOnlyList<LayerRename> edits)
        {
            PdfPermissionPolicy.EnsureAllowed(path, PdfPermissionOperation.Copy);
            PdfFileTransaction.Run(new[] { output }, (_, stage) =>
            {
                File.Copy(path, stage, overwrite: true);
                PdfPageEditService.EditInPlace(stage, doc => Apply(doc, edits), PdfPermissionOperation.Copy);
            });
        }

        /// <summary>Áp đổi tên / gộp lên <paramref name="doc"/> (mở ở append mode). Trả về số layer còn lại.</summary>
        public static int Apply(PdfDocument doc, IReadOnlyList<LayerRename> edits)
        {
            var ocProps = doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.OCProperties);
            var ocgs = ocProps?.GetAsArray(PdfName.OCGs);
            if (ocProps == null || ocgs == null) return 0;

            var info = PdfLayerService.ReadLayers(doc);
            var all = new List<PdfDictionary>();
            for (int i = 0; i < ocgs.Size(); i++)
                if (ocgs.GetAsDictionary(i) is { } d && PdfLayerService.IdOf(d).Length > 0) all.Add(d);

            // nhóm theo tên mới (giữ thứ tự /OCGs); layer không có dòng sửa giữ tên hiện tại và vẫn có thể là đích của 1 nhóm
            var wanted = edits.Where(e => e.NewName.Trim().Length > 0).ToDictionary(e => e.OcgId, e => e.NewName.Trim(), StringComparer.Ordinal);
            var groups = new Dictionary<string, List<PdfDictionary>>(StringComparer.Ordinal);
            foreach (var ocg in all)
            {
                string id = PdfLayerService.IdOf(ocg);
                string name = wanted.TryGetValue(id, out var n) ? n : info.Names.GetValueOrDefault(id, "");
                if (!groups.TryGetValue(name, out var list)) groups[name] = list = new List<PdfDictionary>();
                list.Add(ocg);
            }

            var replace = new Dictionary<PdfDictionary, PdfDictionary>();
            var visibleGroups = new HashSet<PdfDictionary>();
            foreach (var (name, members) in groups)
            {
                var canon = members[0];
                if (!string.Equals(canon.GetAsString(PdfName.Name)?.ToUnicodeString(), name, StringComparison.Ordinal))
                {
                    canon.Put(PdfName.Name, new PdfString(name, iText.IO.Font.PdfEncodings.UNICODE_BIG));
                    canon.SetModified();
                }
                // gộp: hiện nếu có ít nhất 1 layer thành viên đang hiện
                if (members.Any(m => !info.DefaultHidden.Contains(PdfLayerService.IdOf(m)))) visibleGroups.Add(canon);
                for (int i = 1; i < members.Count; i++) replace[members[i]] = canon;
            }
            if (replace.Count == 0) return all.Count;

            RemapPages(doc, replace);

            var kept = new PdfArray();
            for (int i = 0; i < ocgs.Size(); i++)
                if (ocgs.GetAsDictionary(i) is not { } d || !replace.ContainsKey(d)) kept.Add(ocgs.Get(i, false));
            ocProps.Put(PdfName.OCGs, kept);

            if (ocProps.GetAsDictionary(PdfName.D) is { } config)
            {
                foreach (var key in new[] { PdfName.ON, PdfName.OFF, Locked }) RemapFlat(config, key, replace);
                RemapOrder(config, replace);
                RemapAutoStates(config, replace);
                RemapGroups(config, replace);
                // /ON và /OFF giờ đúng theo trạng thái nhóm: nhóm có thành viên đang hiện thì hiện.
                var off = config.GetAsArray(PdfName.OFF);
                if (off != null)
                {
                    var newOff = new PdfArray();
                    for (int i = 0; i < off.Size(); i++)
                        if (off.GetAsDictionary(i) is not { } d || !visibleGroups.Contains(d)) newOff.Add(off.Get(i, false));
                    config.Put(PdfName.OFF, newOff);
                }
                config.SetModified();
            }
            ocProps.SetModified();
            doc.GetCatalog().GetPdfObject().SetModified();
            return kept.Size();
        }

        // ── Tham chiếu tới layer trong nội dung ───────────────────────

        private static void RemapPages(PdfDocument doc, Dictionary<PdfDictionary, PdfDictionary> replace)
        {
            var visited = new HashSet<PdfDictionary>();
            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
            {
                var pageDict = doc.GetPage(p).GetPdfObject();
                bool changed = RemapResources(pageDict.GetAsDictionary(PdfName.Resources), replace, visited);
                if (RemapOc(pageDict, replace)) changed = true;
                if (pageDict.GetAsArray(PdfName.Annots) is { } annots)
                    for (int i = 0; i < annots.Size(); i++)
                        if (annots.GetAsDictionary(i) is { } annot && RemapOc(annot, replace)) annot.SetModified();
                if (changed) pageDict.SetModified();
            }
        }

        /// <summary>Đổi /Properties và các Form XObject lồng nhau. Trả về true nếu có gì đổi (người gọi đánh dấu đối tượng sở hữu).</summary>
        private static bool RemapResources(PdfDictionary? resources, Dictionary<PdfDictionary, PdfDictionary> replace, HashSet<PdfDictionary> visited)
        {
            if (resources == null || !visited.Add(resources)) return false;
            bool changed = false;

            if (resources.GetAsDictionary(PdfName.Properties) is { } props)
            {
                foreach (var key in props.KeySet().ToList())
                {
                    if (props.GetAsDictionary(key) is not { } value) continue;
                    if (replace.TryGetValue(value, out var canon)) { props.Put(key, canon); changed = true; }
                    else if (PdfName.OCMD.Equals(value.GetAsName(PdfName.Type)) && RemapOcmd(value, replace)) value.SetModified();
                }
                if (changed) props.SetModified();
            }

            if (resources.GetAsDictionary(PdfName.XObject) is { } xobjects)
                foreach (var key in xobjects.KeySet().ToList())
                {
                    if (xobjects.GetAsStream(key) is not { } form) continue;
                    bool inner = RemapResources(form.GetAsDictionary(PdfName.Resources), replace, visited);
                    if (RemapOc(form, replace)) inner = true;
                    if (inner) form.SetModified();
                }

            if (changed) resources.SetModified();
            return changed;
        }

        /// <summary>/OC của trang / XObject / chú thích: trỏ tới OCG (hoặc OCMD).</summary>
        private static bool RemapOc(PdfDictionary owner, Dictionary<PdfDictionary, PdfDictionary> replace)
        {
            if (owner.GetAsDictionary(OC) is not { } oc) return false;
            if (replace.TryGetValue(oc, out var canon)) { owner.Put(OC, canon); return true; }
            return PdfName.OCMD.Equals(oc.GetAsName(PdfName.Type)) && RemapOcmd(oc, replace) && Mark(oc);
        }

        private static bool Mark(PdfDictionary d) { d.SetModified(); return true; }

        /// <summary>OCMD: /OCGs là 1 OCG hoặc mảng OCG.</summary>
        private static bool RemapOcmd(PdfDictionary ocmd, Dictionary<PdfDictionary, PdfDictionary> replace)
        {
            var single = ocmd.GetAsDictionary(OCGsKey);
            if (single != null && replace.TryGetValue(single, out var canon)) { ocmd.Put(OCGsKey, canon); return true; }
            if (ocmd.GetAsArray(OCGsKey) is not { } array) return false;
            bool changed = false;
            var result = new PdfArray();
            var seen = new HashSet<PdfDictionary>();
            for (int i = 0; i < array.Size(); i++)
            {
                var item = array.GetAsDictionary(i);
                if (item == null) { result.Add(array.Get(i, false)); continue; }
                var target = replace.TryGetValue(item, out var c) ? c : item;
                if (!ReferenceEquals(target, item)) changed = true;
                if (seen.Add(target)) result.Add(target); else changed = true;
            }
            if (changed) ocmd.Put(OCGsKey, result);
            return changed;
        }

        // ── Cấu hình mặc định /D ──────────────────────────────────────

        private static void RemapFlat(PdfDictionary config, PdfName key, Dictionary<PdfDictionary, PdfDictionary> replace)
        {
            if (config.GetAsArray(key) is not { } array) return;
            var seen = new HashSet<PdfDictionary>();
            var result = new PdfArray();
            for (int i = 0; i < array.Size(); i++)
            {
                var item = array.GetAsDictionary(i);
                if (item == null) { result.Add(array.Get(i, false)); continue; }
                var target = replace.TryGetValue(item, out var c) ? c : item;
                if (seen.Add(target)) result.Add(target);
            }
            config.Put(key, result);
        }

        private static void RemapGroups(PdfDictionary config, Dictionary<PdfDictionary, PdfDictionary> replace)
        {
            if (config.GetAsArray(RBGroups) is not { } groups) return;
            for (int g = 0; g < groups.Size(); g++)
            {
                if (groups.GetAsArray(g) is not { } group) continue;
                var seen = new HashSet<PdfDictionary>();
                var result = new PdfArray();
                for (int i = 0; i < group.Size(); i++)
                {
                    var item = group.GetAsDictionary(i);
                    var target = item != null && replace.TryGetValue(item, out var c) ? c : item;
                    if (target == null) result.Add(group.Get(i, false)); else if (seen.Add(target)) result.Add(target);
                }
                groups.Set(g, result);
            }
        }

        private static void RemapAutoStates(PdfDictionary config, Dictionary<PdfDictionary, PdfDictionary> replace)
        {
            if (config.GetAsArray(AS) is not { } states) return;
            for (int i = 0; i < states.Size(); i++)
                if (states.GetAsDictionary(i) is { } entry && entry.GetAsArray(OCGsKey) is { })
                {
                    RemapFlat(entry, OCGsKey, replace);
                    entry.SetModified();
                }
        }

        /// <summary>/Order: OCG → layer đích (mỗi mảng chỉ giữ 1 lần); mảng con (nhóm / con của layer) đệ quy; chuỗi nhãn giữ nguyên.</summary>
        private static void RemapOrder(PdfDictionary config, Dictionary<PdfDictionary, PdfDictionary> replace)
        {
            if (config.GetAsArray(PdfName.Order) is { } order) config.Put(PdfName.Order, RemapOrderArray(order, replace));
        }

        private static PdfArray RemapOrderArray(PdfArray array, Dictionary<PdfDictionary, PdfDictionary> replace)
        {
            var result = new PdfArray();
            var seen = new HashSet<PdfDictionary>();
            for (int i = 0; i < array.Size(); i++)
            {
                var item = array.Get(i, true);
                if (item is PdfDictionary ocg)
                {
                    var target = replace.TryGetValue(ocg, out var c) ? c : ocg;
                    if (seen.Add(target)) result.Add(target);
                    else if (i + 1 < array.Size() && array.Get(i + 1, true) is PdfArray && !(array.Get(i + 1, true) is PdfArray a && a.Size() > 0 && a.Get(0, true) is PdfString))
                        i++; // con của bản trùng bị bỏ cùng nó
                }
                else if (item is PdfArray sub) result.Add(RemapOrderArray(sub, replace));
                else result.Add(array.Get(i, false));
            }
            return result;
        }
    }
}
