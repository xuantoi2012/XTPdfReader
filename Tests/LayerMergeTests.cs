using System.IO;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Layer;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    static string MakeLayeredPdf(string path, params string[] layerNames)
    {
        using var doc = new PdfDocument(new PdfWriter(path));
        var page = doc.AddNewPage(new PageSize(400, 300));
        var canvas = new PdfCanvas(page);
        int x = 10;
        foreach (string name in layerNames)
        {
            canvas.BeginLayer(new PdfLayer(name, doc)).Rectangle(x, 10, 20, 20).Fill().EndLayer();
            x += 30;
        }
        return path;
    }

    static void TestLayerMerge()
    {
        string folder = System.IO.Path.Combine(Output, "layer-merge");
        Directory.CreateDirectory(folder);
        string a = MakeLayeredPdf(System.IO.Path.Combine(folder, "a.pdf"), "Walls", "Titleblock", "XrefA|Sig");
        string b = MakeLayeredPdf(System.IO.Path.Combine(folder, "b.pdf"), "Walls", "Titleblock", "XrefB|Sig", "Roads");
        var pages = new[] { (a, 1), (b, 1) };
        var baseOptions = new MergeOptions(false, false, true, false, false);

        string byName = System.IO.Path.Combine(folder, "by-name.pdf");
        Check(XTPdfMerger.TryMergePages(pages, byName, out var e1, options: baseOptions), "Merge by name: " + e1);
        var byNameInfo = PdfLayerService.ReadLayers(byName);
        Check(byNameInfo.Names.Count == 5, "Merging by name leaves 5 layers (Walls, Titleblock, 2 Sig, Roads), got " + byNameInfo.Names.Count);

        string separate = System.IO.Path.Combine(folder, "separate.pdf");
        Check(XTPdfMerger.TryMergePages(pages, separate, out var e2, options: baseOptions with { MergeLayers = false }), "Merge separate: " + e2);
        var sep = PdfLayerService.ReadLayers(separate);
        Check(sep.Names.Count == 7, "Separate mode keeps all 7 layers, got " + sep.Names.Count);
        Check(sep.Roots.Count == 2 && sep.Roots[0].Title == "a.pdf" && sep.Roots[0].Children.Count == 3
              && sep.Roots[1].Title == "b.pdf" && sep.Roots[1].Children.Count == 4,
            "Separate mode groups the layers by source file in the Layers tree");

        string keep = System.IO.Path.Combine(folder, "keep-some.pdf");
        var keepOptions = baseOptions with { KeepLayers = new[] { "Sig" }, CollapseLayerName = "Rest" };
        Check(keepOptions.LayerMode == MergeLayerMode.KeepSome, "KeepLayers selects the keep-some mode");
        Check(XTPdfMerger.TryMergePages(pages, keep, out var e3, options: keepOptions), "Merge keep-some: " + e3);
        var kept = PdfLayerService.ReadLayers(keep);
        Check(kept.Names.Count == 2 && kept.Names.Values.Contains("Sig") && kept.Names.Values.Contains("Rest"),
            "Keep-some leaves the chosen layer plus one collapsed layer, got " + string.Join(",", kept.Names.Values));

        // single source file: no tree is built, layers stay as in the source
        string single = System.IO.Path.Combine(folder, "single.pdf");
        Check(XTPdfMerger.TryMergePages(new[] { (a, 1) }, single, out var e4, options: baseOptions with { MergeLayers = false }), "Single-file separate: " + e4);
        Check(PdfLayerService.ReadLayers(single).Names.Count == 3, "Separate mode with one source keeps its 3 layers");

        // a source whose pages use a layer its /OCProperties never listed: the merged file must list it
        string orphanSrc = System.IO.Path.Combine(folder, "orphan-src.pdf");
        MakeLayeredPdf(System.IO.Path.Combine(folder, "orphan-full.pdf"), "Hidden1");
        using (var doc = new PdfDocument(new PdfReader(System.IO.Path.Combine(folder, "orphan-full.pdf")), new PdfWriter(orphanSrc)))
            doc.GetCatalog().GetPdfObject().Remove(PdfName.OCProperties);
        Check(PdfLayerOrphanService.Find(orphanSrc).Count == 1, "Merge fixture has one orphan layer");
        foreach (bool mergeByName in new[] { true, false })
        {
            string orphanOut = System.IO.Path.Combine(folder, mergeByName ? "orphan-merged-name.pdf" : "orphan-merged-sep.pdf");
            Check(XTPdfMerger.TryMergePages(new[] { (orphanSrc, 1), (a, 1) }, orphanOut, out var e5, options: baseOptions with { MergeLayers = mergeByName }), "Merge with an orphan source: " + e5);
            var merged = PdfLayerService.ReadLayers(orphanOut);
            Check(merged.Names.Values.Contains("Hidden1") && PdfLayerOrphanService.Find(orphanOut).Count == 0,
                $"Merged file lists the source's orphan layer (mergeByName={mergeByName}), got " + string.Join(",", merged.Names.Values));
        }
    }
}

internal static partial class Program
{
    static async Task TestLayerToggleRendersAsync()
    {
        string path = System.IO.Path.Combine(Output, "layer-merge", "toggle.pdf");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using (var doc = new PdfDocument(new PdfWriter(path)))
        {
            var page = doc.AddNewPage(new PageSize(200, 200));
            var canvas = new PdfCanvas(page);
            canvas.BeginLayer(new PdfLayer("Red", doc)).SetFillColorRgb(1, 0, 0).Rectangle(0, 0, 200, 200).Fill().EndLayer();
        }
        var info = PdfLayerService.ReadLayers(path);
        string id = info.Names.Keys.Single();
        async Task<bool> IsRedAsync()
        {
            var bmp = await PdfThumbnailService.RenderPageAsync(path, 0, 100);
            if (bmp != null && bmp.Format != System.Windows.Media.PixelFormats.Bgra32)
                bmp = new System.Windows.Media.Imaging.FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var px = new byte[4];
            bmp!.CopyPixels(new System.Windows.Int32Rect(50, 50, 1, 1), px, 4, 0);
            return px[2] > 200 && px[1] < 60; // BGRA
        }
        try
        {
            Check(await IsRedAsync(), "Layer toggle fixture renders red while the layer is visible");
            PdfLayerStateStore.SetHidden(path, new HashSet<string> { id }, info.DefaultHidden);
            await PdfThumbnailService.RetireDocumentAsync(path);
            Check(!await IsRedAsync(), "Hiding the layer changes the rendered page after retiring the document");
            PdfLayerStateStore.SetHidden(path, new HashSet<string>(), info.DefaultHidden);
            await PdfThumbnailService.RetireDocumentAsync(path);
            Check(await IsRedAsync(), "Showing the layer again restores the pixels");
        }
        finally { PdfLayerStateStore.Forget(path); }

        // Orphan layer: the page draws with an OCG that /OCProperties does not list (a merge that kept a stale layer list).
        string orphan = System.IO.Path.Combine(Output, "layer-merge", "orphan.pdf");
        using (var doc = new PdfDocument(new PdfWriter(orphan)))
        {
            var page = doc.AddNewPage(new PageSize(200, 200));
            new PdfCanvas(page).BeginLayer(new PdfLayer("Red", doc)).SetFillColorRgb(1, 0, 0).Rectangle(0, 0, 200, 200).Fill().EndLayer();
        }
        string orphanStripped = System.IO.Path.Combine(Output, "layer-merge", "orphan-stripped.pdf");
        using (var doc = new PdfDocument(new PdfReader(orphan), new PdfWriter(orphanStripped)))
            doc.GetCatalog().GetPdfObject().Remove(PdfName.OCProperties);
        Check(!PdfLayerService.ReadLayers(orphanStripped).HasLayers, "Orphan fixture: the layer list is empty");
        var found = PdfLayerOrphanService.Find(orphanStripped);
        Check(found.Count == 1 && found[0].Name == "Red", "Orphan layer used by the page is found, got " + found.Count);
        PdfLayerOrphanService.RegisterInPlace(orphanStripped);
        var registered = PdfLayerService.ReadLayers(orphanStripped);
        Check(registered.Names.Values.SequenceEqual(new[] { "Red" }) && PdfLayerOrphanService.Find(orphanStripped).Count == 0,
            "Registering adds the layer to the list and leaves no orphans");
        string orphanId = registered.Names.Keys.Single();
        async Task<bool> OrphanRedAsync()
        {
            var bmp = await PdfThumbnailService.RenderPageAsync(orphanStripped, 0, 100);
            if (bmp != null && bmp.Format != System.Windows.Media.PixelFormats.Bgra32)
                bmp = new System.Windows.Media.Imaging.FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var px = new byte[4];
            bmp!.CopyPixels(new System.Windows.Int32Rect(50, 50, 1, 1), px, 4, 0);
            return px[2] > 200 && px[1] < 60;
        }
        try
        {
            Check(await OrphanRedAsync(), "Registered orphan layer renders while visible");
            PdfLayerStateStore.SetHidden(orphanStripped, new HashSet<string> { orphanId }, registered.DefaultHidden);
            await PdfThumbnailService.RetireDocumentAsync(orphanStripped);
            Check(!await OrphanRedAsync(), "Hiding a registered orphan layer changes the page");
        }
        finally { PdfLayerStateStore.Forget(orphanStripped); }
    }
}

internal static partial class Program
{
    static void TestLayerEdit()
    {
        string folder = System.IO.Path.Combine(Output, "layer-edit");
        Directory.CreateDirectory(folder);
        string src = MakeLayeredPdf(System.IO.Path.Combine(folder, "src.pdf"), "Walls", "Walls2", "Roads");
        var before = PdfLayerService.ReadLayers(src);
        string Id(string name) => before.Names.First(kv => kv.Value == name).Key;

        string copy = System.IO.Path.Combine(folder, "copy.pdf");
        PdfLayerEditService.SaveCopy(src, copy, new[]
        {
            new LayerRename(Id("Walls2"), "Walls"),   // gộp Walls2 vào Walls
            new LayerRename(Id("Roads"), "Streets")   // đổi tên
        });
        var after = PdfLayerService.ReadLayers(copy);
        Check(after.Names.Count == 2 && after.Names.Values.Contains("Walls") && after.Names.Values.Contains("Streets"),
            "Layer edit merges Walls2 into Walls and renames Roads, got " + string.Join(",", after.Names.Values));
        Check(PdfLayerService.ReadLayers(src).Names.Count == 3, "Saving a layer-edited copy leaves the source untouched");

        // trang vẫn mở được và mọi nội dung layer còn tham chiếu layer còn tồn tại
        using (var doc = new PdfDocument(new PdfReader(copy)))
        {
            var props = doc.GetPage(1).GetPdfObject().GetAsDictionary(PdfName.Resources).GetAsDictionary(PdfName.Properties);
            var live = new HashSet<string>(after.Names.Keys);
            Check(props.KeySet().All(k => props.GetAsDictionary(k) is { } d && live.Contains(PdfLayerService.IdOf(d))),
                "Every page layer reference points to a layer that still exists after merging");
        }

        string inPlace = System.IO.Path.Combine(folder, "inplace.pdf");
        File.Copy(src, inPlace, overwrite: true);
        PdfLayerEditService.EditInPlace(inPlace, new[] { new LayerRename(Id("Roads"), "Walls") });
        Check(PdfLayerService.ReadLayers(inPlace).Names.Values.Count(n => n == "Walls") == 1 &&
              PdfLayerService.ReadLayers(inPlace).Names.Count == 2,
            "In-place layer edit appends the change to the file (3 layers, Roads merged into the first Walls → 2)");
    }
}

internal static partial class Program
{
    static void TestSheetInfoSurvivesMerge()
    {
        string folder = System.IO.Path.Combine(Output, "sheet-info");
        Directory.CreateDirectory(folder);
        string a = System.IO.Path.Combine(folder, "a.pdf"), b = System.IO.Path.Combine(folder, "b.pdf");
        foreach (var path in new[] { a, b })
            using (var doc = new PdfDocument(new PdfWriter(path))) { for (int i = 0; i < 3; i++) doc.AddNewPage(new PageSize(300, 200)); }

        var infoA = new List<XTSheetPageInfo?> { null, new() { No = "KT-01", Title = "Mặt bằng tầng 1", Scale = "1:100", Group = "Kiến trúc", Dwg = @"D:\P\a.dwg", Layout = "L1" },
                                                  new() { No = "KT-02", Title = "Mặt đứng", Group = "Kiến trúc" } };
        XTSheetPdfInfo.Write(a, infoA, new XTProjectPdfInfo { Name = "Dự án X", File = @"D:\P\x.xts" });
        XTSheetPdfInfo.Write(b, new XTSheetPageInfo?[] { new() { No = "KC-01", Title = "Móng" }, null, null }, new XTProjectPdfInfo { Name = "Dự án Y" });

        using (var doc = new PdfDocument(new PdfReader(a)))
        {
            var p2 = XTSheetPdfInfo.ReadPage(doc.GetPage(2));
            Check(p2 != null && p2.No == "KT-01" && p2.Title == "Mặt bằng tầng 1" && p2.Scale == "1:100" && p2.Layout == "L1", "Sheet info round-trips Vietnamese text on its page");
            Check(XTSheetPdfInfo.ReadPage(doc.GetPage(1)) == null, "Pages without a drawing (cover) carry no sheet info");
            Check(XTSheetPdfInfo.ReadProject(doc)?.Name == "Dự án X", "Project info round-trips in the catalog");
        }

        // đổi thứ tự khi ghép: b1, a3, a2
        string merged = System.IO.Path.Combine(folder, "merged.pdf");
        Check(XTPdfMerger.TryMergePages(new[] { (b, 1), (a, 3), (a, 2) }, merged, out var err), "Merge for sheet info: " + err);
        using (var doc = new PdfDocument(new PdfReader(merged)))
        {
            Check(XTSheetPdfInfo.ReadPage(doc.GetPage(1))?.No == "KC-01" && XTSheetPdfInfo.ReadPage(doc.GetPage(2))?.No == "KT-02"
                  && XTSheetPdfInfo.ReadPage(doc.GetPage(3))?.No == "KT-01",
                "Sheet info follows its page when the reader merges pages in a new order");
        }
    }
}

internal static partial class Program
{
    static void TestSheetMatch()
    {
        var target = new XTSheetPageInfo?[]
        {
            null,
            new() { No = "KT-01", Title = "Cũ" },
            new() { No = "kt-02" },
            new() { Dwg = @"D:\P\a.dwg", Layout = "L5" },
            new() { No = "KC-09" }
        };
        var revision = new Dictionary<int, XTSheetPageInfo>
        {
            [1] = new() { No = "KT-02" },            // khớp không phân biệt hoa thường
            [2] = new() { No = "KT-01", Title = "Mới" },
            [3] = new() { Dwg = @"d:\p\A.DWG", Layout = "l5" },  // khớp theo DWG + layout
            [4] = new() { No = "MEP-01" }            // chỉ có ở revision
        };
        var (pairs, unmatched) = XTSheetIndex.Match(target, revision);
        Check(pairs.Count == 3 && pairs.Contains((1, 2)) && pairs.Contains((2, 1)) && pairs.Contains((3, 3)),
            "Sheets match by number (any case) or by DWG + layout");
        Check(unmatched.SequenceEqual(new[] { 4 }), "Revision sheets without a match are reported");
        Check(XTSheetIndex.Key(new XTSheetPageInfo { Title = "chỉ có tên" }) == null, "A sheet with neither number nor DWG + layout cannot be matched");
    }
}

internal static partial class Program
{
    /// <summary>2 người sửa cùng file lần lượt: người thứ hai bị chen ngang giữa lúc đọc và lúc ghi → đọc lại và áp lại, cả 2 thay đổi cùng còn.</summary>
    static void TestSequentialInPlaceEdits()
    {
        string folder = System.IO.Path.Combine(Output, "sequential-save");
        Directory.CreateDirectory(folder);
        string path = MakeLayeredPdf(System.IO.Path.Combine(folder, "shared.pdf"), "A", "B", "C");
        int attempts = 0;
        PdfPageEditService.EditInPlace(path, doc =>
        {
            attempts++;
            if (attempts == 1)
            {
                // người kia lưu xong ở giữa: đổi tên layer A → "A2" bằng 1 lần ghi thật vào file
                PdfLayerEditService.EditInPlace(path, new[] { new LayerRename(PdfLayerService.ReadLayers(path).Names.First(kv => kv.Value == "A").Key, "A2") });
            }
            // thao tác của người thứ hai: đổi tên layer B → "B2" (tìm lại theo tên trên bản đang đọc)
            var ocgs = doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.OCProperties).GetAsArray(PdfName.OCGs);
            for (int i = 0; i < ocgs.Size(); i++)
                if (ocgs.GetAsDictionary(i) is { } d && d.GetAsString(PdfName.Name)?.ToUnicodeString() == "B")
                {
                    d.Put(PdfName.Name, new PdfString("B2"));
                    d.SetModified();
                }
        });
        var names = PdfLayerService.ReadLayers(path).Names.Values.OrderBy(n => n).ToList();
        Check(attempts == 2, "A save that was overtaken re-reads the file and applies its change again (attempts " + attempts + ")");
        Check(names.SequenceEqual(new[] { "A2", "B2", "C" }), "Both users' changes are in the file after sequential saves, got " + string.Join(",", names));
    }
}

internal static partial class Program
{
    static void TestXtSetRebuild()
    {
        string folder = System.IO.Path.Combine(Output, "xtset");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        string parts = System.IO.Path.Combine(folder, "Bộ hồ sơ.xtparts");
        Directory.CreateDirectory(parts);
        string Pdf(string name, int pages, params string[] layers)
        {
            string path = System.IO.Path.Combine(parts, name);
            using var doc = new PdfDocument(new PdfWriter(path));
            for (int i = 0; i < pages; i++)
            {
                var page = doc.AddNewPage(new PageSize(300, 200));
                var canvas = new PdfCanvas(page);
                int x = 10;
                foreach (string l in layers) { canvas.BeginLayer(new PdfLayer(l, doc)).Rectangle(x, 10, 20, 20).Fill().EndLayer(); x += 30; }
            }
            return path;
        }
        string cover = Pdf("cover.pdf", 1), toc = Pdf("toc.pdf", 1), div = Pdf("000_divider.pdf", 1);
        string a = Pdf("001_a.pdf", 2, "Walls", "CHUKY_A"), b = Pdf("002_b.pdf", 1, "Walls", "Roads");
        XTSheetPdfInfo.Write(a, new XTSheetPageInfo?[] { new() { No = "KT-01", Title = "Mặt bằng", Group = "Kiến trúc", Subset = "Tầng 1" }, new() { No = "KT-02", Title = "Mặt đứng", Group = "Kiến trúc", Subset = "Tầng 1" } }, new XTProjectPdfInfo());
        XTSheetPdfInfo.Write(b, new XTSheetPageInfo?[] { new() { No = "KC-01", Title = "Móng", Group = "Kết cấu" } }, new XTProjectPdfInfo());

        string json = """
        { "version": 1, "app": "XTToolbox", "projectName": "Dự án X", "output": "Bộ hồ sơ.pdf",
          "layers": { "mode": "KeepSome", "keep": [], "prefix": "CHUKY_", "collapse": "0" },
          "items": [
            { "kind": "cover", "pdf": "Bộ hồ sơ.xtparts/cover.pdf", "label": "Bìa" },
            { "kind": "toc", "pdf": "Bộ hồ sơ.xtparts/toc.pdf", "label": "Mục lục" },
            { "kind": "divider", "pdf": "Bộ hồ sơ.xtparts/000_divider.pdf", "label": "Kiến trúc" },
            { "kind": "dwg", "pdf": "Bộ hồ sơ.xtparts/001_a.pdf", "label": "a" },
            { "kind": "dwg", "pdf": "Bộ hồ sơ.xtparts/002_b.pdf", "label": "b" } ] }
        """;
        string xtset = System.IO.Path.Combine(folder, "Bộ hồ sơ.xtset");
        File.WriteAllText(xtset, json);

        var recipe = XTSetRebuild.Read(xtset);
        Check(recipe != null && recipe.Items.Count == 5 && XTSetRebuild.MissingParts(xtset, recipe).Count == 0, "A .xtset recipe is read and its parts are found");
        Check(XTSetRebuild.Rebuild(xtset, recipe!, out var err), "Rebuild: " + err);

        string output = XTSetRebuild.OutputPath(xtset, recipe!);
        using (var doc = new PdfDocument(new PdfReader(output)))
        {
            Check(doc.GetNumberOfPages() == 6, "Rebuild merges every page of every part (1+1+1+2+1 = 6), got " + doc.GetNumberOfPages());
            Check(XTSheetPdfInfo.ReadPage(doc.GetPage(5))?.No == "KT-02", "Rebuilt pages keep their sheet info");
            var titles = new List<string>();
            void Walk(iText.Kernel.Pdf.PdfOutline o, int depth) { foreach (var c in o.GetAllChildren()) { titles.Add(new string(' ', depth) + c.GetTitle()); Walk(c, depth + 1); } }
            Walk(doc.GetOutlines(false), 0);
            string tree = string.Join("|", titles);
            Check(tree == "Bìa|Mục lục|Kiến trúc| Tầng 1|  KT-01 - Mặt bằng|  KT-02 - Mặt đứng|Kết cấu| KC-01 - Móng".Replace("| KC-01", "|KC-01").Replace("|Kết cấu|KC-01", "|Kết cấu| KC-01"),
                "Rebuild builds the Hạng mục → Subset → Sheet bookmark tree, got " + tree);
            var names = PdfLayerService.ReadLayers(output).Names.Values.OrderBy(n => n).ToList();
            Check(names.SequenceEqual(new[] { "0", "CHUKY_A" }), "Rebuild applies the recipe's layer choice (keep CHUKY_*, rest into 0), got " + string.Join(",", names));
        }
    }
}

internal static partial class Program
{
    static void TestLayerRenameOnMerge()
    {
        string folder = System.IO.Path.Combine(Output, "layer-rename");
        Directory.CreateDirectory(folder);
        string a = MakeLayeredPdf(System.IO.Path.Combine(folder, "a.pdf"), "Walls", "Roads");
        string b = MakeLayeredPdf(System.IO.Path.Combine(folder, "b.pdf"), "Walls", "Titleblock");
        string output = System.IO.Path.Combine(folder, "renamed.pdf");
        var options = new MergeOptions(false, false, true, false, false, null, "Other", "", new Dictionary<string, string> { ["Walls"] = "Tường", ["Roads"] = "Đường" });
        Check(XTPdfMerger.TryMergePages(new[] { (a, 1), (b, 1) }, output, out var err, options: options), "Merge with renamed result layers: " + err);
        var names = PdfLayerService.ReadLayers(output).Names.Values.OrderBy(n => n).ToList();
        Check(names.SequenceEqual(new[] { "Titleblock", "Tường", "Đường" }.OrderBy(n => n)), "Result layers are renamed after merging by name, got " + string.Join(",", names));
    }
}

internal static partial class Program
{
    static void TestSaveHistory()
    {
        string folder = System.IO.Path.Combine(Output, "history");
        Directory.CreateDirectory(folder);
        string path = MakeLayeredPdf(System.IO.Path.Combine(folder, "h.pdf"), "A", "B");
        long original = new FileInfo(path).Length;
        string idA = PdfLayerService.ReadLayers(path).Names.First(kv => kv.Value == "A").Key;
        PdfLayerEditService.EditInPlace(path, new[] { new LayerRename(idA, "Tường") });
        PdfPageEditService.RotatePages(path, new[] { 1 }, 90);

        var history = XTHistory.Read(path);
        Check(history.Count == 2 && history[0].Action.StartsWith("Layers") && history[1].Action == "Rotated 1 page", "Saves with an action are listed in the file history (oldest first)");
        Check(history[0].User == XTHistory.CurrentUser && history[0].LengthBefore == original, "A history entry keeps who saved and the file length before the save");

        string before = System.IO.Path.Combine(folder, "before.pdf");
        XTHistory.SaveVersion(path, history[0].LengthBefore, before);
        Check(PdfLayerService.ReadLayers(before).Names.Values.OrderBy(n => n).SequenceEqual(new[] { "A", "B" }) && XTHistory.Read(before).Count == 0,
            "The version before a save can be recovered from the file");
        Check(PdfLayerService.ReadLayers(path).Names.Values.Contains("Tường"), "The current file keeps the change");
    }
}

internal static partial class Program
{
    static void TestPrintSizePlan()
    {
        const double pt = 72 / 25.4;
        (double, double) Mm(double w, double h) => (w * pt, h * pt);
        var pages = new List<(double, double)>();
        for (int i = 0; i < 12; i++) pages.Add(Mm(841, 594));   // A1 ngang
        for (int i = 0; i < 40; i++) pages.Add(Mm(297, 420));   // A3 đứng
        pages.Add(Mm(279, 216));                                // Letter ngang
        pages.Add(Mm(1500, 900));                               // khổ rất lớn

        var plotter = new[] { new PaperOption("A1", 594, 841), new PaperOption("A3", 297, 420), new PaperOption("A4", 210, 297) };
        var groups = PrintSizePlan.Build(pages, plotter);
        var a3 = groups.First(g => g.Name == "A3");
        var a1 = groups.First(g => g.Name == "A1");
        var letter = groups.First(g => g.Name == "Letter");
        var big = groups.First(g => g.Name != "A3" && g.Name != "A1" && g.Name != "Letter");
        Check(groups.Count == 4 && groups[0].Name == "A3" && a3.Count == 40 && a1.Count == 12, "Pages are grouped by paper size, biggest group first");
        Check(a1.Fit == SizeFit.Exact && a3.Fit == SizeFit.Exact, "A1 and A3 pages match the printer's A1 and A3 (landscape or portrait)");
        Check(letter.Fit == SizeFit.LargerPaper && letter.Paper!.Name == "A3", "Letter is not on the printer: it would print on the smallest larger paper (A3)");
        Check(big.Fit == SizeFit.TooBig && big.Paper!.Name == "A1" && big.ShrinkTo is > 0 and < 1, "A page bigger than every paper is reported with the paper it would shrink to");
        Check(PrintSizePlan.PageList(a1.PageIndexes) == "1-12" && PrintSizePlan.PageList(new[] { 0, 4, 6, 7, 8 }) == "1, 5, 7-9", "Page lists are compact for the Pages: box");
        Check(PrintSizePlan.Report(groups, "Plotter").Contains("Letter × 1") && PrintSizePlan.Report(groups, "Plotter").Contains("pages 53"), "The report names the sizes and page numbers");
        Check(PrintSizePlan.Build(pages, Array.Empty<PaperOption>()).All(g => g.Fit == SizeFit.TooBig && g.Paper == null), "A printer that lists no paper sizes cannot match anything");
    }
}

internal static partial class Program
{
    static void TestSheetRegisterCsv()
    {
        var rows = new[]
        {
            new RegisterRow(3, new XTSheetPageInfo { No = "KT-01", Title = "Mặt bằng, tầng \"1\"", Scale = "1:100", Group = "Kiến trúc", Subset = "Tầng 1", Dwg = @"D:\P\a.dwg", Layout = "L1" }, "A1"),
            new RegisterRow(4, new XTSheetPageInfo { No = "-02", Title = "=SUM(A1)" }, "A3")
        };
        string csv = XTSheetRegister.ToCsv(rows);
        var lines = csv.TrimEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        Check(csv.StartsWith("\uFEFF") && lines.Length == 3 && lines[0].Contains("\"Sheet number\""), "The register CSV has a BOM and a header row for Excel");
        Check(lines[1].Contains("\"Mặt bằng, tầng \"\"1\"\"\"") && lines[1].Contains("\"A1\"") && lines[1].Contains("\"3\""), "Commas, quotes and Vietnamese text are quoted correctly");
        Check(lines[2].Contains("\"'-02\"") && lines[2].Contains("\"'=SUM(A1)\""), "Cells that start with = + - @ are protected from being read as formulas");
    }
}

internal static partial class Program
{
    static void TestTitleBlockReader()
    {
        string folder = System.IO.Path.Combine(Output, "titleblock");
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "tb.pdf");
        var font = iText.Kernel.Font.PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA);
        using (var doc = new PdfDocument(new PdfWriter(path)))
        {
            for (int p = 0; p < 2; p++)
            {
                var page = doc.AddNewPage(new PageSize(842, 595)); // A4 ngang
                var c = new PdfCanvas(page);
                void Text(string t, double x, double y) => c.BeginText().SetFontAndSize(font, 12).MoveText(x, y).ShowText(t).EndText();
                Text("KT-0" + (p + 1), 842 * 0.80, 595 * 0.06);        // dưới phải: số hiệu
                Text("Mat bang tang " + (p + 1), 842 * 0.55, 595 * 0.10); // dưới: tên
                Text("1:100", 842 * 0.92, 595 * 0.02);                  // góc dưới phải: tỷ lệ
                if (p == 1) page.SetRotation(90);                       // trang 2 xoay 90° cw
            }
        }
        // vùng theo trang nhìn thấy (trang 1, không xoay): số hiệu = dải dưới phải
        var layout = new TitleBlockLayout
        {
            Number = new TitleBlockRegion(0.75, 0.88, 0.95, 0.96),
            Title = new TitleBlockRegion(0.50, 0.84, 0.74, 0.92),
            Scale = new TitleBlockRegion(0.90, 0.96, 1.0, 1.0)
        };
        var readings = TitleBlockReader.Read(path, new[] { 1 }, (_, _) => layout);
        Check(readings[0].Number == "KT-01" && readings[0].Title == "Mat bang tang 1" && readings[0].Scale == "1:100",
            $"Text in the marked title-block areas is read from the PDF text layer ({readings[0]})");

        // trang xoay 90°: cùng chữ nằm ở vị trí khác trên trang nhìn thấy → ToUserSpace phải xoay đúng
        using (var doc = new PdfDocument(new PdfReader(path)))
        {
            var rotated = doc.GetPage(2);
            // góc dưới phải của trang gốc nằm ở đâu khi nhìn thấy? (xoay 90° cw: dưới-phải gốc → dưới-trái nhìn thấy)
            var region = new TitleBlockRegion(0.0, 0.70, 0.30, 1.0);
            string text = TitleBlockReader.TextIn(rotated, region);
            Check(text.Contains("KT-02") || text.Contains("1:100"), "Regions follow the page rotation (/Rotate 90) when mapping to the PDF text, got \"" + text + "\"");
            Check(TitleBlockReader.SizeKey(doc.GetPage(1)) == "A4 landscape" && TitleBlockReader.SizeKey(rotated) == "A4 portrait", "The layout key is the visible paper size and orientation");
        }
        Check(TitleBlockReader.Read(path, new[] { 1 }, (_, _) => null)[0].Number == "", "A page with no layout is skipped, not guessed");
    }
}

internal static partial class Program
{
    static void TestPageMeasure()
    {
        Check(PageMeasure.ParseScale("1:100") == 100 && PageMeasure.ParseScale("TL 1/50") == 50 && PageMeasure.ParseScale("Tỷ lệ 1 : 200 (A1)") == 200
              && PageMeasure.ParseScale("2:1") == 0.5 && PageMeasure.ParseScale("1,5:300") == 200 && PageMeasure.ParseScale("abc") == null && PageMeasure.ParseScale("1:0") == null,
            "Scale text such as 1:100, 1/50, 'Tỷ lệ 1 : 200' is understood");
        // 100 mm trên giấy ở 1:100 = 10 000 mm = 10 m
        double points = 100 * 72 / 25.4;
        Check(Math.Abs(PageMeasure.RealLengthMm(points, 100) - 10_000) < 1e-6, "100 mm on paper at 1:100 is 10 m");
        Check(PageMeasure.FormatLength(10_000) == "10 m" && PageMeasure.FormatLength(850) == "850 mm" && PageMeasure.FormatLength(1_234_000) == "1.234 km", "Lengths are shown in mm, m or km");
        // 100 × 100 mm trên giấy ở 1:100 = 10 m × 10 m = 100 m²
        double sq = points * points;
        Check(Math.Abs(PageMeasure.RealAreaSquareMeters(sq, 100) - 100) < 1e-6 && PageMeasure.FormatArea(100) == "100 m²" && PageMeasure.FormatArea(25_000) == "2.5 ha", "Areas follow the scale squared");
    }
}

internal static partial class Program
{
    static System.Windows.Media.Imaging.BitmapSource InkImage(int w, int h, params (int X, int Y, int W, int H)[] rects)
    {
        var px = new byte[w * h]; Array.Fill(px, (byte)255);
        foreach (var (rx, ry, rw, rh) in rects) for (int y = ry; y < ry + rh; y++) for (int x = rx; x < rx + rw; x++) px[y * w + x] = 0;
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Gray8, null, px, w);
        bmp.Freeze();
        return bmp;
    }

    static void TestPageDiff()
    {
        var a = InkImage(200, 100, (20, 20, 40, 10));
        var same = PageDiff.Overlay(a, InkImage(200, 100, (20, 20, 40, 10)));
        Check(same.OldOnly == 0 && same.NewOnly == 0 && same.ChangedFraction == 0, "Identical pages show no difference");
        var shifted = PageDiff.Overlay(a, InkImage(200, 100, (21, 20, 40, 10)));
        Check(shifted.OldOnly == 0 && shifted.NewOnly == 0, "A 1-pixel shift (anti-aliasing noise) is not reported as a change");
        var added = PageDiff.Overlay(a, InkImage(200, 100, (20, 20, 40, 10), (120, 50, 30, 20)));
        Check(added.OldOnly == 0 && added.NewOnly == 30 * 20 && added.ChangedFraction > 0.4, "Strokes only in the new page are counted as added");
        var removed = PageDiff.Overlay(InkImage(200, 100, (20, 20, 40, 10), (120, 50, 30, 20)), a);
        Check(removed.NewOnly == 0 && removed.OldOnly == 30 * 20, "Strokes only in the old page are counted as removed");
        var scaled = PageDiff.Overlay(a, InkImage(400, 200, (40, 40, 80, 20)));
        Check(scaled.ChangedFraction < 0.1, "A new page rendered at another size is scaled to the old one before comparing");
    }
}

internal static partial class Program
{
    static void TestPageLabels()
    {
        string folder = System.IO.Path.Combine(Output, "page-labels");
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "labels.pdf");
        using (var doc = new PdfDocument(new PdfWriter(path))) { for (int i = 0; i < 4; i++) doc.AddNewPage(); }
        XTSheetPdfInfo.Write(path, new XTSheetPageInfo?[] { null, new() { No = "KT-01" }, new() { No = "KT-02" }, null }, new XTProjectPdfInfo());
        Check(XTPageLabels.WriteInPlace(path) == 2, "Page labels are written when pages have sheet numbers");
        using var read = new PdfDocument(new PdfReader(path));
        var labels = read.GetPageLabels();
        Check(labels != null && labels[0] == "1" && labels[1] == "KT-01" && labels[2] == "KT-02" && labels[3] == "4", "Pages show their sheet number as the page label (others keep the page number)");
        Check(XTHistory.Read(path).Any(h => h.Action.StartsWith("Page labels")), "Setting page labels adds a history line");
    }
}

internal static partial class Program
{
    static void TestSheetLinks()
    {
        string folder = System.IO.Path.Combine(Output, "sheet-links");
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "links.pdf");
        var font = iText.Kernel.Font.PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA);
        using (var doc = new PdfDocument(new PdfWriter(path)))
        {
            string[][] texts =
            {
                new[] { "Table of contents", "KT-02  Mat dung", "KT-03  Mat cat", "A1 is too short, KT-021 is another number" },
                new[] { "KT-02 title block", "see KT-03 for details" },
                new[] { "KT-03 title block" }
            };
            foreach (var lines in texts)
            {
                var page = doc.AddNewPage(new PageSize(595, 842));
                var canvas = new PdfCanvas(page);
                float y = 780;
                foreach (string line in lines) { canvas.BeginText().SetFontAndSize(font, 12).MoveText(50, y).ShowText(line).EndText(); y -= 24; }
            }
        }
        XTSheetPdfInfo.Write(path, new XTSheetPageInfo?[] { null, new() { No = "KT-02" }, new() { No = "KT-03" } }, new XTProjectPdfInfo());
        var infos = XTSheetIndex.Read(path);
        var links = XTSheetLinks.Find(path, infos);
        Check(links.Count == 3 && links.Count(l => l.Page == 1) == 2 && links.Count(l => l.Page == 2) == 1 && links.All(l => l.Page != l.TargetPage),
            $"Sheet numbers in the contents page and in a cross reference are found, own title blocks and longer numbers are not ({links.Count})");
        Check(links.Any(l => l.Page == 2 && l.TargetPage == 3 && l.Text == "KT-03"), "The cross reference 'see KT-03' on sheet KT-02 targets sheet KT-03");

        Check(XTSheetLinks.WriteInPlace(path, links) == 3, "The links are written to the file");
        using (var read = new PdfDocument(new PdfReader(path)))
        {
            var annots = read.GetPage(1).GetAnnotations().Where(a => PdfName.Link.Equals(a.GetSubtype())).ToList();
            Check(annots.Count == 2, "The contents page now has 2 link annotations");
            var target = annots.Select(a => a.GetPdfObject().GetAsDictionary(PdfName.A)?.GetAsArray(PdfName.D)?.Get(0)).First();
            Check(target is PdfIndirectReference || target is PdfDictionary, "A link jumps to a page through a GoTo destination");
        }
        Check(XTSheetLinks.WriteInPlace(path, XTSheetLinks.Find(path, infos)) == 0, "Running it again adds no duplicate links over existing ones");
    }
}

internal static partial class Program
{
    static async Task TestAnnotationConflictAsync()
    {
        string folder = System.IO.Path.Combine(Output, "annotation-conflict");
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "c.pdf");
        using (var doc = new PdfDocument(new PdfWriter(path))) { doc.AddNewPage(new PageSize(400, 300)); }
        var add = new QuickAnnotationSpec("conflict-note", QuickAnnotationKind.Comment, 1, .2, .2, .3, .3, "original");
        AnnotationWorkingCopy.WriteInPlace(path, new[] { new QuickAnnotationChange(null, add) });
        AnnotationStore.Forget(path);
        var original = (await AnnotationStore.GetAllAsync(path)).First(a => a.Name == "conflict-note");

        // người B sửa chữ trước
        AnnotationWorkingCopy.WriteInPlace(path, new[] { new QuickAnnotationChange(original, original with { Text = "B edited" }) });
        // người A (đang giữ bản cũ "original") sửa lên cùng chú thích
        string[]? raised = null;
        void Handler(string p, IReadOnlyList<string> names) => raised = names.ToArray();
        AnnotationWorkingCopy.Conflict += Handler;
        try { AnnotationWorkingCopy.WriteInPlace(path, new[] { new QuickAnnotationChange(original, original with { Text = "A edited" }) }, out var conflicts);
              Check(conflicts.Count == 1 && conflicts[0] == "conflict-note" && raised is { Length: 1 }, "Editing a comment that someone else already edited is reported as a conflict"); }
        finally { AnnotationWorkingCopy.Conflict -= Handler; }

        // sửa lên 1 chú thích chưa ai đụng tới: không báo
        var other = new QuickAnnotationSpec("untouched-note", QuickAnnotationKind.Comment, 1, .5, .5, .6, .6, "mine");
        AnnotationWorkingCopy.WriteInPlace(path, new[] { new QuickAnnotationChange(null, other) });
        AnnotationStore.Forget(path);
        var untouched = (await AnnotationStore.GetAllAsync(path)).First(a => a.Name == "untouched-note");
        AnnotationWorkingCopy.WriteInPlace(path, new[] { new QuickAnnotationChange(untouched, untouched with { Text = "mine v2" }) }, out var none);
        Check(none.Count == 0, "No conflict when nobody else changed the comment");
        AnnotationStore.Forget(path);
    }
}

internal static partial class Program
{
    static void TestPresence()
    {
        string folder = System.IO.Path.Combine(Output, "presence");
        Directory.CreateDirectory(folder);
        string pdf = MakeLayeredPdf(System.IO.Path.Combine(folder, "shared.pdf"), "A");
        XTPresence.Touch(pdf);
        Check(XTPresence.Others(pdf).Count == 0, "A Reader does not report itself as 'someone else'");
        // Reader khác (máy khác) đang mở: file dấu của họ
        string theirs = System.IO.Path.Combine(folder, ".shared.pdf.xtopen.lan@PC-02.4321");
        File.WriteAllText(theirs, "x");
        var others = XTPresence.Others(pdf);
        Check(others.Count == 1 && others[0] == "lan@PC-02", "A marker from another machine shows who has the file open (" + string.Join(",", others) + ")");
        File.SetLastWriteTimeUtc(theirs, DateTime.UtcNow.AddMinutes(-5));
        Check(XTPresence.Others(pdf).Count == 0, "A stale marker (no heartbeat) is ignored");
        File.SetLastWriteTimeUtc(theirs, DateTime.UtcNow.AddMinutes(-20));
        XTPresence.Others(pdf);
        Check(!File.Exists(theirs), "An abandoned marker is cleaned up");
        XTPresence.Remove(pdf);
        Check(Directory.GetFiles(folder, ".shared.pdf.xtopen.*").Length == 0, "Closing the file removes its own marker");
    }
}

internal static partial class Program
{
    static void TestWorkerJob()
    {
        var info = new System.Diagnostics.ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var child = System.Diagnostics.Process.Start(info)!;
        try
        {
            Check(XTPdfMergeApp.Services.WorkerJob.Assign(child), "A worker process can be assigned to the kill-on-close job");
            Check(XTPdfMergeApp.Services.WorkerJob.Contains(child), "The worker is reported as inside the job");
        }
        finally { try { child.Kill(); } catch { } }
    }
}

internal static partial class Program
{
    /// <summary>Thử trên file thật: tắt từng layer (tối đa N) và xem layer nào làm ảnh trang đổi — để biết bật/tắt layer có tác dụng với file đó không.</summary>
    static async Task LayerRealFileCheckAsync(string path, int page, int maxLayers)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var info = PdfLayerService.ReadLayers(path);
        Console.WriteLine($"layers: {info.Names.Count}, default hidden: {info.DefaultHidden.Count}, usage-controlled: {info.UsageControlled?.Count ?? 0}, read in {sw.ElapsedMilliseconds} ms");
        async Task<string> HashAsync()
        {
            var bmp = await PdfThumbnailService.RenderPageAsync(path, page - 1, 1400, layerToken: PdfLayerStateStore.GetToken(path));
            var px = new byte[bmp!.PixelWidth * bmp.PixelHeight * 4];
            var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            conv.CopyPixels(px, bmp.PixelWidth * 4, 0);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(px))[..16];
        }
        string baseline = await HashAsync();
        int changed = 0, tried = 0;
        // Only layers the page actually draws with (page + nested form XObject resources) can change it; scanning the first N of 700 would mostly miss them.
        var used = new HashSet<string>();
        using (var doc = new PdfDocument(new PdfReader(path)))
        {
            var seen = new HashSet<PdfDictionary>();
            void Walk(PdfDictionary? res)
            {
                if (res == null || !seen.Add(res)) return;
                if (res.GetAsDictionary(PdfName.Properties) is { } props)
                    foreach (var key in props.KeySet())
                        if (props.GetAsDictionary(key) is { } d)
                        {
                            string id = PdfLayerService.IdOf(d);
                            if (id.Length > 0) used.Add(id);
                            if (PdfName.OCMD.Equals(d.GetAsName(PdfName.Type)))
                            {
                                if (d.GetAsDictionary(PdfName.OCGs) is { } one) used.Add(PdfLayerService.IdOf(one));
                                if (d.GetAsArray(PdfName.OCGs) is { } many) for (int i = 0; i < many.Size(); i++) if (many.GetAsDictionary(i) is { } g) used.Add(PdfLayerService.IdOf(g));
                            }
                        }
                if (res.GetAsDictionary(PdfName.XObject) is { } xo)
                    foreach (var key in xo.KeySet()) if (xo.GetAsStream(key) is { } form) Walk(form.GetAsDictionary(PdfName.Resources));
            }
            Walk(doc.GetPage(page).GetPdfObject().GetAsDictionary(PdfName.Resources));
        }
        Console.WriteLine($"layers referenced by page {page}: {used.Count}");
        Console.WriteLine("  used ids: " + string.Join(", ", used.Take(25)) + " | names sample: " + string.Join(", ", info.Names.Keys.Take(3)) + " | in names: " + used.Count(info.Names.ContainsKey));
        using (var doc2 = new PdfDocument(new PdfReader(path)))
        {
            var ocp = doc2.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.OCProperties);
            var all = new HashSet<string>(); var ord = new HashSet<string>();
            if (ocp?.GetAsArray(PdfName.OCGs) is { } arr) for (int i = 0; i < arr.Size(); i++) all.Add(PdfLayerService.IdOf(arr.Get(i)));
            Console.WriteLine($"  /OCProperties /OCGs entries: {all.Count}; used ids in /OCGs: {used.Count(all.Contains)}");
            foreach (string id in used.Take(25))
            {
                var parts = id.Split(' ');
                var o = doc2.GetPdfObject(int.Parse(parts[0])) as PdfDictionary;
                Console.WriteLine($"  used {id}: name={o?.GetAsString(PdfName.Name)} type={o?.GetAsName(PdfName.Type)}");
            }
        }
        var candidates = info.Names.Where(kv => used.Contains(kv.Key)).ToList();
        foreach (var (id, name) in candidates.Take(maxLayers))
        {
            PdfLayerStateStore.SetHidden(path, new HashSet<string> { id }, info.DefaultHidden);
            await PdfThumbnailService.RetireDocumentAsync(path);
            string hash = await HashAsync();
            tried++;
            bool differs = hash != baseline;
            if (differs) changed++;
            Console.WriteLine($"  hide \"{name}\" ({id}): {(differs ? "page changes" : "no change")}{(info.UsageControlled?.Contains(id) == true ? "  [usage-controlled]" : "")}");
        }
        PdfLayerStateStore.Forget(path);
        Console.WriteLine($"{changed} of {tried} layers change page {page}; {sw.ElapsedMilliseconds} ms");
    }
}

internal static partial class Program
{
    static System.Windows.Media.Imaging.BitmapSource FormatBitmap(System.Windows.Media.PixelFormat format, int w, int h, byte value)
    {
        int bpp = format.BitsPerPixel / 8;
        var data = new byte[w * h * bpp]; Array.Fill(data, value);
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, format, null, data, w * bpp);
        bmp.Freeze();
        return bmp;
    }

    static void TestRegionReuseFormats()
    {
        foreach (var format in new[] { System.Windows.Media.PixelFormats.Bgr24, System.Windows.Media.PixelFormats.Bgra32, System.Windows.Media.PixelFormats.Pbgra32, System.Windows.Media.PixelFormats.Bgr32 })
        {
            // Mảnh cũ 0..400 × 0..200 (giá trị 77); khung mới dịch phải 100 điểm: chỉ thiếu dải x 400..500.
            var oldBounds = new System.Windows.Int32Rect(0, 0, 400, 200);
            var piece = new ViewportRegionReuse.Piece(oldBounds, FormatBitmap(format, 400, 200, 77));
            var target = new System.Windows.Int32Rect(100, 0, 400, 200);
            var plan = ViewportRegionReuse.Plan(target, new[] { piece });
            Check(plan.Length == 1 && plan[0].X == 400 && plan[0].Width == 100, $"{format}: an overlapping crop is reused and only the new band is planned");
            var fresh = new ViewportRegionReuse.Piece(new System.Windows.Int32Rect(400, 0, 100, 200), FormatBitmap(format, 100, 200, 200));
            var composed = ViewportRegionReuse.Compose(target, new[] { piece, fresh }, default);
            Check(composed.Format == format && composed.PixelWidth == 400 && composed.PixelHeight == 200, $"{format}: the composed crop keeps the pieces' pixel format");
            int bpp = format.BitsPerPixel / 8;
            var row = new byte[400 * bpp];
            composed.CopyPixels(new System.Windows.Int32Rect(0, 100, 400, 1), row, row.Length, 0);
            Check(row[0] == 77 && row[299 * bpp] == 77 && row[300 * bpp] == 200 && row[399 * bpp] == 200, $"{format}: reused pixels and the new band meet at the right column");
        }
        // Mảnh khác format với mảnh đầu bị bỏ qua (không ép chuyển đổi)
        var a = new ViewportRegionReuse.Piece(new System.Windows.Int32Rect(0, 0, 100, 100), FormatBitmap(System.Windows.Media.PixelFormats.Bgr24, 100, 100, 1));
        var b = new ViewportRegionReuse.Piece(new System.Windows.Int32Rect(100, 0, 100, 100), FormatBitmap(System.Windows.Media.PixelFormats.Bgra32, 100, 100, 2));
        var mixed = ViewportRegionReuse.Plan(new System.Windows.Int32Rect(0, 0, 200, 100), new[] { a, b });
        Check(mixed.Length == 1 && mixed[0].X == 100, "A piece in another pixel format than the first is not mixed in; its area is planned for a fresh render");
    }
}

internal static partial class Program
{
    /// <summary>Ảnh thật từ worker MuPDF (Bgr24): ghép "mảnh cũ dịch + dải mới" phải ra đúng ảnh vẽ thẳng cả khung — kể cả đường nối.</summary>
    static async Task TestRegionReuseWithMuPdfAsync()
    {
        if (!ExperimentalMuPdfViewport.CanRender("engine-probe.pdf", 5, "changed")) return; // PDFium test build: the MuPDF worker is not part of it
        string folder = System.IO.Path.Combine(Output, "region-reuse-mupdf");
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "detail.pdf");
        using (var doc = new PdfDocument(new PdfWriter(path)))
        {
            var page = doc.AddNewPage(new PageSize(842, 595));
            var canvas = new PdfCanvas(page);
            for (int i = 0; i < 120; i++) canvas.MoveTo(10 + i * 6.5, 10).LineTo(830 - i * 3.1, 585).Stroke();
        }
        int fullW = 4200, fullH = (int)(fullW * 595.0 / 842);
        var oldRect = new System.Windows.Int32Rect(600, 300, 1600, 900);
        var target = new System.Windows.Int32Rect(800, 300, 1600, 900);          // kéo ngang 200 điểm
        var oldImage = (await ExperimentalMuPdfViewport.RenderAsync(path, 0, fullW, fullH, new[] { oldRect }, default))[0]!;
        var band = new System.Windows.Int32Rect(2200, 300, 200, 900);
        var bandImage = (await ExperimentalMuPdfViewport.RenderAsync(path, 0, fullW, fullH, new[] { band }, default))[0]!;
        var direct = (await ExperimentalMuPdfViewport.RenderAsync(path, 0, fullW, fullH, new[] { target }, default))[0]!;
        Check(oldImage.Format == System.Windows.Media.PixelFormats.Bgr24, "MuPDF crops arrive as Bgr24 (the case that used to disable overlap reuse)");

        var pieces = new List<ViewportRegionReuse.Piece> { new(oldRect, oldImage), new(band, bandImage, band) };
        var plan = ViewportRegionReuse.Plan(target, new[] { pieces[0] });
        Check(plan.Length == 1 && plan[0].X == 2200 && plan[0].Width == 200, "A MuPDF crop that overlaps the next viewport is reused: only the 200 px band needs rendering");
        var composed = ViewportRegionReuse.Compose(target, pieces, default);
        var a = new byte[target.Width * target.Height * 3]; var b = new byte[a.Length];
        composed.CopyPixels(a, target.Width * 3, 0); direct.CopyPixels(b, target.Width * 3, 0);
        int different = 0;
        for (int i = 0; i < a.Length; i += 3) if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) different++;
        Check(composed.Format == direct.Format && different == 0, $"The reused crop plus the new band equals a direct render of the whole viewport pixel for pixel ({different} different pixels)");
        ExperimentalMuPdfViewport.Shutdown();
    }
}

internal static partial class Program
{
    /// <summary>Comment list export: sheet number and title of the page, reply-to author, quoting, formula protection, order.</summary>
    static void TestCommentExport()
    {
        var comments = new List<CommentInfo>
        {
            new(@"D:\P\b.pdf", 2, "r1", QuickAnnotationKind.Reply, "Lan", new DateTime(2026, 10, 7), "OK, fixed", true, "c2"),
            new(@"D:\P\b.pdf", 2, "c2", QuickAnnotationKind.Comment, "Nam", new DateTime(2026, 10, 6), "Dimension \"3,5\" is wrong\r\nsee KT-02", false),
            new(@"D:\P\a.pdf", 1, "c1", QuickAnnotationKind.Highlight, "Nam", null, "=HYPERLINK(\"x\")", false),
        };
        var rows = CommentExport.Build(comments, (path, page) => path.EndsWith("b.pdf") && page == 2 ? new XTSheetPageInfo { No = "KT-02", Title = "Mặt bằng" } : null);
        Check(rows.Count == 3 && rows[0].File == "a.pdf" && rows[1].Text.StartsWith("Dimension") && rows[2].ReplyTo == "Nam",
            "Rows are ordered by file, page, root comment before its replies, and a reply names the author it answers");
        Check(rows[1].Sheet == "KT-02" && rows[1].SheetTitle == "Mặt bằng" && rows[0].Sheet == "", "The sheet number and title come from the page's sheet info (empty when the page has none)");
        string csv = CommentExport.ToCsv(rows);
        var lines = csv.TrimEnd().Split("\r\n");
        Check(csv.StartsWith("\uFEFF") && lines.Length == 4 && lines[0].Contains("\"Sheet title\"") && lines[0].Contains("\"Reply to\""), "The CSV has a BOM and the header row");
        Check(lines[1].Contains("\"'=HYPERLINK(\"\"x\"\")\""), "A comment that starts with = is protected from being read as a formula");
        Check(lines[2].Contains("\"Dimension \"\"3,5\"\" is wrong see KT-02\"") && lines[2].Contains("\"KT-02\"") && lines[2].Contains("\"Open\""), "Quotes, commas and line breaks are kept in one cell; status is Open");
        Check(lines[3].Contains("\"Resolved\"") && lines[3].Contains("\"Nam\""), "A resolved reply shows its status and who it answers");
    }
}
