using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Layer;
using XTPdfMergeApp;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Domain;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Workspace;

internal static partial class Program
{
    static void SavePng(FrameworkElement element, string name)
    {
        element.UpdateLayout();
        int w = (int)Math.Ceiling(element.ActualWidth), h = (int)Math.Ceiling(element.ActualHeight);
        if (w == 0 || h == 0) throw new Exception(name + " has no size");
        var bitmap = new RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(element);
        using var png = File.Create(System.IO.Path.Combine(Output, name + ".png"));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(png);
    }

    static Window Offscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000; window.Top = -32000; window.ShowActivated = false; window.ShowInTaskbar = false;
        window.Show(); window.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(150));
        return window;
    }

    /// <summary>Mở thật các cửa sổ / panel mới (dialog merge layer, Manage layers, đổi tên layer kết quả, tab Sheets, tab History) để bắt lỗi XAML/runtime và chụp PNG vào results/.</summary>
    static void TestUiSmoke()
    {
        if (Application.Current == null) CreateReaderTestApplication();
        string folder = System.IO.Path.Combine(Output, "ui-smoke");
        Directory.CreateDirectory(folder);
        string a = MakeLayeredPdf(System.IO.Path.Combine(folder, "a.pdf"), "Walls", "Titleblock", "XrefA|CHUKY_A");
        string b = MakeLayeredPdf(System.IO.Path.Combine(folder, "b.pdf"), "Walls", "Roads", "XrefB|CHUKY_A");
        XTSheetPdfInfo.Write(a, new XTSheetPageInfo?[] { new() { No = "KT-01", Title = "Mặt bằng tầng 1", Scale = "1:100", Group = "Kiến trúc", Subset = "Tầng 1", Dwg = a } }, new XTProjectPdfInfo { Name = "Dự án" });
        PdfLayerEditService.EditInPlace(b, new[] { new LayerRename(PdfLayerService.ReadLayers(b).Names.First(kv => kv.Value == "Roads").Key, "Đường") });

        // 1. Save merged file (3 chế độ layer + đổi tên)
        var save = (Window)new MergeSaveWindow(new[] { (a, 1), (b, 1) }, folder);
        Offscreen(save);
        Pump(TimeSpan.FromMilliseconds(600)); // phân tích layer chạy nền
        ((RadioButton)save.FindName("LayersKeepSomeRadio")).IsChecked = true;
        Pump(TimeSpan.FromMilliseconds(200));
        SavePng(save, "ui-merge-save-keepsome");
        var items = ((ListBox)save.FindName("KeepList")).Items;
        Check(items.Count >= 3, "The merge dialog lists the layers of the source files for 'keep chosen' (" + items.Count + ")");
        save.Close();

        // 2. Manage layers
        var info = PdfLayerService.ReadLayers(a);
        var manage = Offscreen(new ManageLayersWindow(a, info));
        SavePng(manage, "ui-manage-layers");
        manage.Close();

        // 3. Rename result layers
        var rename = Offscreen(new RenameResultLayersWindow(new[] { "Walls", "CHUKY_A", "0" }, null));
        SavePng(rename, "ui-rename-result-layers");
        rename.Close();

        // 4. Side panel: Sheets + History tabs
        var workspace = new PdfWorkspace();
        var group = new WorkspaceDocument { SourcePath = a };
        group.Pages.Add(workspace.CreatePlacement(a, 1));
        group.SetBaseline();
        PdfPageEditService.RotatePages(a, new[] { 1 }, 90); // có 1 dòng lịch sử
        AnnotationStore.Apply(a, new[] { new QuickAnnotationChange(null, new QuickAnnotationSpec("review-note-1", QuickAnnotationKind.Comment, 1, .2, .2, .3, .3, "Check the door width")) });
        var panel = new ReaderSidePanel { Width = 360, Height = 520 };
        var host = Offscreen(new Window { Content = panel, Width = 380, Height = 560 });
        host.Dispatcher.Invoke(() => { panel.SetCurrent(group, group.Pages[0]); panel.ShowPanel("Sheets"); }); // async tab loads need the dispatcher context
        Pump(TimeSpan.FromMilliseconds(500));
        SavePng(panel, "ui-tab-sheets");
        var sheets = (SheetsPanel)panel.FindName("SheetsView");
        var sheetList = (ListBox)((DockPanel)sheets.Content).Children.OfType<Grid>().First().Children.OfType<ListBox>().First();
        Check(sheetList.Items.Count == 1 && ((SheetRow)sheetList.Items[0]).Title.Contains("KT-01"), "The Sheets tab lists the drawings from the XT sheet info in the PDF");
        Pump(TimeSpan.FromMilliseconds(800));
        Check(((SheetRow)sheetList.Items[0]).Detail.Contains("1 open"), "The Sheets tab shows the unresolved comments of each drawing: " + ((SheetRow)sheetList.Items[0]).Detail);
        SavePng(panel, "ui-tab-sheets-comments");
        host.Dispatcher.Invoke(() => panel.ShowPanel("History"));
        Pump(TimeSpan.FromMilliseconds(500));
        SavePng(panel, "ui-tab-history");
        var historyList = (ListBox)((DockPanel)((HistoryPanel)panel.FindName("HistoryView")).Content).Children.OfType<Grid>().First().Children.OfType<ListBox>().First();
        Check(historyList.Items.Count == 1, "The History tab lists the saves recorded in the file (" + historyList.Items.Count + ")");
        AnnotationStore.Forget(a);
        host.Close();
    }
}

internal static partial class Program
{
    /// <summary>ReaderWindow thật: thanh tab (nhiều file, hẹp lại), ghép bằng kéo tab (MergeTabs) và thay sheet theo revision (ReplaceSheets), có Undo.</summary>
    static void TestUiTabsAndMerge()
    {
        if (Application.Current == null) CreateReaderTestApplication();
        string folder = System.IO.Path.Combine(Output, "ui-tabs");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        for (int i = 1; i <= 12; i++)
        {
            string p = MakeLayeredPdf(System.IO.Path.Combine(folder, $"Quyển {i:D2} - Thuyết minh bản vẽ thi công.pdf"), "Walls");
            XTSheetPdfInfo.Write(p, new XTSheetPageInfo?[] { new() { No = $"KT-{i:D2}", Title = "Bản vẽ " + i, Group = "Kiến trúc" } }, new XTProjectPdfInfo());
            paths.Add(p);
        }

        var reader = new ReaderWindow { Width = 1300, Height = 700 };
        var app = Application.Current;
        var priorOwner = app.MainWindow;
        app.MainWindow = reader;
        Exception? failure = null;
        var frame = new System.Windows.Threading.DispatcherFrame();
        reader.Show();
        reader.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try
            {
                ((FrameworkElement)reader.FindName("StartPage")).Visibility = Visibility.Collapsed;
                await reader.Session.OpenFilesInReaderAsync(paths);
                reader.UpdateLayout();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var tabs = (ListBox)reader.FindName("ReaderDocumentTabs");
                var list = (FrameworkElement)reader.FindName("ReaderTabsOverflowButton");
                var strip = (FrameworkElement)reader.FindName("ReaderDocumentTabsStrip");
                Check(reader.Session.Documents.Count == 12, "The real reader opened 12 files");
                Check(list.Visibility == Visibility.Visible && tabs.Items.Count < 12, $"With 12 files in 1300 px the tab list button appears and tabs are limited ({tabs.Items.Count} shown)");
                SavePng(strip, "ui-tabs-12-wide");
                reader.Width = 2200; reader.UpdateLayout();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Check(list.Visibility == Visibility.Collapsed && tabs.Items.Count == 12, "In a wide window all tabs fit and the list button is hidden (" + tabs.Items.Count + ")");
                SavePng(strip, "ui-tabs-12-very-wide");
                reader.Width = 1300; reader.UpdateLayout();

                // ghép bằng kéo tab: file 1 thả sau file 2 → bản nháp [file 2, file 1]
                var g1 = reader.Session.Documents[0]; var g2 = reader.Session.Documents[1];
                var merge = typeof(ReaderWindow).GetMethod("MergeTabs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                int before = reader.Session.Documents.Count;
                merge.Invoke(reader, new object[] { g1, g2, true });
                Check(reader.Session.Documents.Count == before + 1, "Dropping a tab onto another creates a draft window");
                var draft = reader.Session.Documents.Last();
                Check(draft.Pages.Count == 2 && draft.Pages[0].SourcePath == g2.Pages[0].SourcePath && draft.Pages[1].SourcePath == g1.Pages[0].SourcePath,
                    "The draft holds the target's pages first, then the dragged file's (drop on the right half = after)");
                await reader.Session.Workspace.History.UndoAsync();
                Check(!reader.Session.Documents.Contains(draft) || draft.Pages.Count == 0, "Undo removes the merge draft");

                // thay sheet theo revision: file 3 có KT-03; revision = file 3 bản mới (cùng số hiệu)
                string rev = MakeLayeredPdf(System.IO.Path.Combine(folder, "revision.pdf"), "Walls", "NewLayer");
                XTSheetPdfInfo.Write(rev, new XTSheetPageInfo?[] { new() { No = "KT-03", Title = "Bản vẽ 3 rev B", Group = "Kiến trúc" } }, new XTProjectPdfInfo());
                await reader.Session.OpenFilesInReaderAsync(new[] { rev });
                var target = reader.Session.Documents.First(g => g.SourcePath == paths[2]);
                var revision = reader.Session.Documents.First(g => g.SourcePath == rev);
                var oldRow = target.Pages[0];
                int replaced = ((IReaderPageEditHost)reader.Session).ReplaceSheets(target, new[] { (oldRow, revision, revision.Pages[0]) });
                Check(replaced == 1 && target.Pages.Count == 1 && target.Pages[0].SourcePath == rev, "ReplaceSheets swaps the page for the revision's page");
                await reader.Session.Workspace.History.UndoAsync();
                Check(target.Pages.Count == 1 && ReferenceEquals(target.Pages[0], oldRow), "One Undo restores the original sheet");

                // Khổ giấy trên thumbnail: file lẫn A3 + Letter → mỗi trang có SizeBadge
                string mixed = System.IO.Path.Combine(folder, "mixed-sizes.pdf");
                using (var mdoc = new PdfDocument(new PdfWriter(mixed)))
                {
                    mdoc.AddNewPage(new PageSize(842, 1190)); mdoc.AddNewPage(new PageSize(842, 1190)); mdoc.AddNewPage(new PageSize(612, 792));
                }
                await reader.Session.OpenFilesInReaderAsync(new[] { mixed });
                var mixedGroup = reader.Session.Documents.First(g => g.SourcePath == mixed);
                var sizeLoad = typeof(ReaderWindow).GetMethod("LoadContinuousPageSizesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                await (Task)sizeLoad.Invoke(reader, new object[] { mixedGroup })!;
                Check(mixedGroup.Pages.Select(p => p.SizeBadge).SequenceEqual(new[] { "A3", "A3", "Letter" }), "Thumbnails of a file with mixed paper sizes carry the size: " + string.Join(",", mixedGroup.Pages.Select(p => p.SizeBadge)));
                var plainGroup = reader.Session.Documents.First(g => g.SourcePath == paths[0]);
                await (Task)sizeLoad.Invoke(reader, new object[] { plainGroup })!;
                Check(plainGroup.Pages.All(p => p.SizeBadge == ""), "A file with a single paper size shows no size badges");

                // Measure: tỷ lệ của trang lấy từ thông tin sheet (/XTSheet /Scale)
                string scaled = MakeLayeredPdf(System.IO.Path.Combine(folder, "scaled.pdf"), "Walls");
                XTSheetPdfInfo.Write(scaled, new XTSheetPageInfo?[] { new() { No = "KT-50", Scale = "1:200" } }, new XTProjectPdfInfo());
                var scaleOf = typeof(ReaderWindow).GetMethod("ScaleOf", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var row = reader.Session.Workspace.CreatePlacement(scaled, 1);
                Check((double?)scaleOf.Invoke(reader, new object[] { row }) == 200, "The Measure tool takes the page scale from the sheet info (1:200)");
                var plain = reader.Session.Workspace.CreatePlacement(paths[0], 1);
                Check((double?)scaleOf.Invoke(reader, new object[] { plain }) == null, "A page without a scale reports 'unknown' (the tool then asks once)");
                Check(reader.FindName("ReaderMeasureToolButton") != null, "The ribbon has the Measure tool");

                reader.Session.Workspace.History.Clear();
                foreach (var group in reader.Session.Documents) { group.SetAnnotationsDirty(false); group.SetBaseline(); }
                reader.Session.Documents.Clear();
            }
            catch (Exception ex) { failure = ex; }
            finally { reader.Session.Documents.Clear(); reader.Close(); app.MainWindow = priorOwner; frame.Continue = false; }
        }));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        if (failure != null) throw failure;
    }
}

internal static partial class Program
{
    static void TestUiPrintSizes()
    {
        if (Application.Current == null) CreateReaderTestApplication();
        string folder = System.IO.Path.Combine(Output, "ui-print");
        Directory.CreateDirectory(folder);
        string pdf = System.IO.Path.Combine(folder, "mixed.pdf");
        using (var doc = new PdfDocument(new PdfWriter(pdf)))
        {
            const double pt = 72 / 25.4;
            foreach (var (w, h, n) in new[] { (594.0, 841.0, 3), (297.0, 420.0, 6), (216.0, 279.0, 1) })
                for (int i = 0; i < n; i++) doc.AddNewPage(new PageSize((float)(w * pt), (float)(h * pt)));
        }
        var pages = Enumerable.Range(1, 10).Select(n => (pdf, n)).ToList();
        var window = new PrintWindow(pages, 0);
        Offscreen(window);
        Pump(TimeSpan.FromMilliseconds(1200));
        SavePng(window, "ui-print-sizes");
        var note = (TextBlock)window.FindName("SizesNote");
        Check(note.Text.Contains("A3 × 6") && note.Text.Contains("A1 × 3") && note.Text.Contains("Letter × 1"), "The Print window summarises the page sizes of the file: " + note.Text);
        const double mm = 72 / 25.4;
        var groups = PrintSizePlan.Build(new[] { (594 * mm, 841 * mm), (594 * mm, 841 * mm), (297 * mm, 420 * mm), (216 * mm, 279 * mm) }, new[] { new PaperOption("A3", 297, 420), new PaperOption("A4", 210, 297) });
        var routing = Offscreen(new PrintRoutingWindow(groups, new System.Drawing.Printing.PrinterSettings().PrinterName));
        SavePng(routing, "ui-print-routing");
        Check(groups.Count == 3 && groups.Any(g => g.Fit == SizeFit.TooBig), "Print by size lists every paper size group");
        routing.Close();
        window.Close();
    }
}

internal static partial class Program
{
    static void TestUiReadSheetInfo()
    {
        if (Application.Current == null) CreateReaderTestApplication();
        TestTitleBlockReader(); // tạo results/titleblock/tb.pdf
        string path = System.IO.Path.Combine(Output, "titleblock", "tb.pdf");
        // 2 trang: trang 1 (A4 ngang, có vùng) + trang 2 (xoay)
        var window = new ReadSheetInfoWindow(path, new[] { 1, 2 }, 1);
        Offscreen(window);
        Pump(TimeSpan.FromMilliseconds(900));
        // khoanh vùng bằng cách đặt thẳng layout (mô phỏng người dùng kéo chuột) rồi đọc
        TitleBlockStore.Set("A4 landscape", new TitleBlockLayout
        {
            Number = new TitleBlockRegion(0.75, 0.88, 0.95, 0.96), Title = new TitleBlockRegion(0.50, 0.84, 0.74, 0.92), Scale = new TitleBlockRegion(0.90, 0.96, 1.0, 1.0)
        });
        window.Close();
        window = new ReadSheetInfoWindow(path, new[] { 1 }, 1);
        Offscreen(window);
        Pump(TimeSpan.FromMilliseconds(900));
        Task? reading = null;
        window.Dispatcher.Invoke(() => reading = window.ReadAllForTestAsync());
        for (int i = 0; i < 40 && !reading!.IsCompleted; i++) Pump(TimeSpan.FromMilliseconds(100));
        Check(reading!.IsCompleted, "Reading all pages finishes");
        Pump(TimeSpan.FromMilliseconds(200));
        SavePng(window, "ui-read-sheet-info");
        window.Close();

        // ghi vào file
        string copy = System.IO.Path.Combine(Output, "titleblock", "written.pdf");
        File.Copy(path, copy, overwrite: true);
        XTSheetInfoWriter.WriteInPlace(copy, new[] { (1, new XTSheetPageInfo { No = "KT-01", Title = "Mặt bằng", Scale = "1:100" }) });
        using var doc = new PdfDocument(new PdfReader(copy));
        var info = XTSheetPdfInfo.ReadPage(doc.GetPage(1));
        Check(info != null && info.No == "KT-01" && info.Title == "Mặt bằng" && XTSheetPdfInfo.ReadPage(doc.GetPage(2)) == null, "Read sheet info is written to the page's /XTSheet (other pages untouched)");
        Check(XTHistory.Read(copy).Any(h => h.Action.StartsWith("Sheet info")), "Writing sheet info adds a history line");
    }
}

internal static partial class Program
{
    static void TestUiCompare()
    {
        if (Application.Current == null) CreateReaderTestApplication();
        string folder = System.IO.Path.Combine(Output, "ui-compare");
        Directory.CreateDirectory(folder);
        string a = System.IO.Path.Combine(folder, "rev-A.pdf"), b = System.IO.Path.Combine(folder, "rev-B.pdf");
        MakeLayeredPdf(a, "Walls");
        MakeLayeredPdf(b, "Walls", "Roads");
        XTSheetPdfInfo.Write(a, new XTSheetPageInfo?[] { new() { No = "KT-01", Title = "Mặt bằng" } }, new XTProjectPdfInfo());
        XTSheetPdfInfo.Write(b, new XTSheetPageInfo?[] { new() { No = "KT-01", Title = "Mặt bằng" } }, new XTProjectPdfInfo());
        var pairs = ReaderWindow.BuildComparePairs(a, b);
        Check(pairs.Count == 1 && pairs[0].Label.Contains("KT-01"), "Compare matches the same sheet number in both versions");
        // không có thông tin sheet: cùng số trang → ghép theo thứ tự
        string c = MakeLayeredPdf(System.IO.Path.Combine(folder, "plain-1.pdf"), "A"), d = MakeLayeredPdf(System.IO.Path.Combine(folder, "plain-2.pdf"), "A", "B");
        Check(ReaderWindow.BuildComparePairs(c, d).Count == 1 && ReaderWindow.BuildComparePairs(c, d)[0].Label == "Page 1", "Without sheet info, files with the same page count are compared page by page");

        var window = new CompareWindow(a, b, pairs);
        Offscreen(window);
        for (int i = 0; i < 40 && pairs[0].Diff == null; i++) Pump(TimeSpan.FromMilliseconds(150));
        Pump(TimeSpan.FromMilliseconds(300));
        Check(pairs[0].Diff != null && pairs[0].Diff!.NewOnly > 0 && pairs[0].Diff!.OldOnly == 0, "The comparison finds the layer drawn only in the new version");
        SavePng(window, "ui-compare");
        window.Close();
    }
}
