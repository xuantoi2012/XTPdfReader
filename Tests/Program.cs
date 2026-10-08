using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Point = System.Windows.Point;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XTPdfMergeApp.Domain;
using XTPdfMergeApp.Services;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;

internal static partial class Program
{
    static readonly string Output = System.IO.Path.Combine(AppContext.BaseDirectory, "results");
    static int _checks;
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            Directory.CreateDirectory(Output);
            // The suite was written for the bounded-memory policy; the default tier (Balance, registry-backed) prefetches more and varies per machine.
            XTPdfMergeApp.Services.ReaderPerformanceProfile.Apply(XTPdfMergeApp.Services.ReaderPerformanceMode.MemorySaving);
            XTPdfMergeApp.Controls.ContinuousPdfView.WideRegions = false; XTPdfMergeApp.Controls.ContinuousPdfView.ZoomRateLimit = 0; XTPdfMergeApp.Controls.ContinuousPdfView.WarmAllPreviews = false; XTPdfMergeApp.Controls.ContinuousPdfView.SpeculateZoomSteps = false; // pre-rendering calls the page renderer in the background and would skew the request counts the viewer tests assert
            // NuGet places PDFium in a runtime-specific directory.
            var dll = Directory.GetFiles(AppContext.BaseDirectory, "pdfium.dll", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.Contains("win-x64", StringComparison.OrdinalIgnoreCase));
            foreach (var assembly in new[] { typeof(PdfThumbnailService).Assembly, typeof(Program).Assembly })
                NativeLibrary.SetDllImportResolver(assembly, (name, _, _) => name == "pdfium" ?
                    NativeLibrary.Load(dll ?? throw new DllNotFoundException("PDFium must not be used in this build")) : IntPtr.Zero);
            if (args.Contains("--mupdf-migration-check"))
            {
                ReaderPerformanceProfile.Apply(ReaderPerformanceMode.MemorySaving);
                TestMuPdfMigrationAsync().GetAwaiter().GetResult();
                Console.WriteLine($"PASS ({_checks} MuPDF migration checks)");
                return 0;
            }
            if (args.Contains("--user-feedback-check"))
            { TestTabSharpRetention(); TestTabRegionRetention(); TestFeedbackSession(); Console.WriteLine($"PASS ({_checks} user feedback checks)"); return 0; }
            int realFeedback = Array.IndexOf(args, "--feedback-real-pdf-check");
            if (realFeedback >= 0)
            { TestRealFeedbackPdfAsync(args[realFeedback + 1]).GetAwaiter().GetResult(); Console.WriteLine($"PASS ({_checks} real feedback PDF checks)"); return 0; }
            if (args.Contains("--adaptive-memory-check"))
            {
                TestAdaptiveMemory();
                Console.WriteLine($"PASS ({_checks} adaptive memory checks)");
                return 0;
            }
            if (args.Contains("--performance-settings-check"))
            { TestPerformanceSettings(); Console.WriteLine($"PASS ({_checks} performance settings checks)"); return 0; }
            if (args.Contains("--reader-memory-regression"))
            {
                ReaderPerformanceProfile.Apply(ReaderPerformanceMode.MemorySaving);
                TestReaderPreviewRetentionAsync().GetAwaiter().GetResult();
                TestReaderRenderHandoffAsync().GetAwaiter().GetResult(); TestCacheAndOwnership();
                TestContinuousScrollQuality(); TestReaderLargerCacheAsync().GetAwaiter().GetResult();
                TestReaderWarmImages(); TestVisiblePageRetriesAfterTransientFailure(); TestRegionReuseViewer();
                Console.WriteLine($"PASS ({_checks} reader memory regression checks)"); return 0;
            }
            int wideScroll = Array.IndexOf(args, "--wide-scroll-memory-profile");
            if (wideScroll >= 0)
            {
                int modeArg = Array.IndexOf(args, "--performance-mode");
                ProfileWideScrollMemory(args[wideScroll + 1], args[wideScroll + 2], args.Length > wideScroll + 3 ? int.Parse(args[wideScroll + 3]) : 200, args.Contains("--reader-cache"),
                    modeArg >= 0 ? Enum.Parse<ReaderPerformanceMode>(args[modeArg + 1]) : null);
                Console.WriteLine($"PASS ({_checks} wide scroll checks)"); return 0;
            }
            int adaptiveProfile = Array.IndexOf(args, "--adaptive-memory-profile");
            int reclaimProfile = Array.IndexOf(args, "--reclaim-memory-profile");
            if (reclaimProfile >= 0)
            {
                ProfileReclaimMemory(args[reclaimProfile + 1], args[reclaimProfile + 2], args[reclaimProfile + 3],
                    args.Length > reclaimProfile + 4 ? int.Parse(args[reclaimProfile + 4]) : 31,
                    args.Length > reclaimProfile + 5 && args[reclaimProfile + 5] == "neighbors");
                Console.WriteLine($"PASS ({_checks} reclamation experiment checks)");
                return 0;
            }
            if (adaptiveProfile >= 0)
            {
                ProfileAdaptiveMemory(args[adaptiveProfile + 1], args[adaptiveProfile + 2]);
                Console.WriteLine($"PASS ({_checks} adaptive memory profile checks)");
                return 0;
            }
            int tabsProfile = Array.IndexOf(args, "--multi-tab-profile");
            if (args.Contains("--preview-warm-lifecycle")) { TestPreviewWarmDocumentSwitch(); Console.WriteLine($"PASS ({_checks} preview warm checks)"); return 0; }
            int warmProfile = Array.IndexOf(args, "--warm-cache-profile");
            int regionProfile = Array.IndexOf(args, "--region-pan-profile");
            int layerReal = Array.IndexOf(args, "--layer-real");
            if (layerReal >= 0 && args.Contains("--register-orphans")) { var sw = System.Diagnostics.Stopwatch.StartNew(); PdfLayerOrphanService.RegisterInPlace(args[layerReal + 1]); Console.WriteLine($"registered orphan layers in {sw.ElapsedMilliseconds} ms"); }
            if (layerReal >= 0) { LayerRealFileCheckAsync(args[layerReal + 1], int.Parse(args[layerReal + 2]), int.Parse(args[layerReal + 3])).GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--worker-job-holder"))
            {
                // Test helper: keeps a child in the kill-on-close job and waits, so a test can kill this process and look for the orphan.
                var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ping.exe", "-n 300 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
                Console.WriteLine($"{child.Id} {XTPdfMergeApp.Services.WorkerJob.Assign(child)}");
                Thread.Sleep(Timeout.Infinite);
            }
            int onlyIndex = Array.IndexOf(args, "--test"); // --test <Name>: run one static test method (fast loop while developing)
            if (onlyIndex >= 0)
            {
                if (Application.Current == null) CreateReaderTestApplication();
                foreach (string name in args[onlyIndex + 1].Split(','))
                {
                    var method = typeof(Program).GetMethod(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                        ?? throw new ArgumentException("No test method " + name);
                    var result = method.Invoke(null, null);
                    if (result is Task task) task.GetAwaiter().GetResult();
                }
                Console.WriteLine($"Selected tests passed ({_checks}).");
                return 0;
            }
            if (args.Contains("--ui-smoke")) { TestUiSmoke(); TestUiTabsAndMerge(); TestPrintInbox(); TestPrintInboxView(); TestMergeLayoutModes(); TestPrintedFilesCleanup(); TestUiRibbonTitleAbout(); TestShapePropertiesFloating(); TestTypewriterTextBox(); TestTypewriterEditorChrome(); TestCalloutStyle(); TestSelectToolBar(); TestShapeTextGroup(); TestPropertyActions(); TestPropertyActionsOnGroup(); TestShapeDashStyle(); TestOpacity(); TestShapeFill(); TestArrowTextGroup(); TestTopTextGrowsUp(); TestDrawShapeAndNoteFlows(); TestNoteIconFixedSize(); TestBarsAndTypewriterFlow(); TestCommentPopupPlacement(); TestUiPrintSizes(); TestPrintSizeClasses(); TestPrintProfilesAndRouting(); TestPrintRoutingWindow(); TestPrintCollateFollowsDriver(); TestCaptureRegion(); TestCaptureHandles(); TestCaptureStore(); TestCaptureHotkey(); TestCaptureSettings(); TestCaptureAutostart(); TestCaptureInstanceChannel(); TestCaptureLauncherFindsExe(); TestScreenGrabber(); TestCaptureOverlay(); TestFloatingButton(); TestCaptureTrayFlow(); TestRotatedPagePrintPlacement(); TestOcrEndToEnd(); TestOcrWindow(); TestReadSheetInfoOnScan(); TestOcrPendingUntilSave(); TestTextEditPendingUntilSave(); TestEditTextTool(); TestEditObject(); TestEditObjectTool(); TestBatchFindInArea(); TestFindAreaPerPaperSize(); TestFindSignatureObjects(); TestEditObjectOnRotatedPage(); TestStampManyPages(); TestPageTurnWaitsForSave(); TestBookmarkEditsWaitForSave(); TestMarkupModel(); TestMarkupGeometry(); TestMarkupController(); TestMarkupRender(); TestMarkupToolbar(); TestCaptureOverlayMarkup(); TestCaptureStoreMarkup(); TestCaptureEditor(); TestCapturePdfExport(); TestUiReadSheetInfo(); TestUiCompare(); Console.WriteLine($"UI smoke passed ({_checks})."); return 0; }
            if (args.Contains("--layer-merge-only")) { TestLayerMerge(); TestLayerToggleRendersAsync().GetAwaiter().GetResult(); TestLayerEdit(); TestSheetInfoSurvivesMerge(); TestSheetMatch(); TestSequentialInPlaceEdits(); TestXtSetRebuild(); TestLayerRenameOnMerge(); TestSaveHistory(); TestPrintSizePlan(); TestSheetRegisterCsv(); TestCommentExport(); TestTitleBlockReader(); TestPageMeasure(); TestPageDiff(); TestPageLabels(); TestSheetLinks(); TestPresence(); TestWorkerJob(); TestRegionReuseFormats(); TestRegionReuseWithMuPdfAsync().GetAwaiter().GetResult(); TestAnnotationConflictAsync().GetAwaiter().GetResult(); Console.WriteLine($"Layer merge checks passed ({_checks})."); return 0; }
            if (regionProfile >= 0)
            {
                int manifest = Array.IndexOf(args, "--profile-sources");
                ProfileRegionPan(args[regionProfile + 1], bool.Parse(args[regionProfile + 2]), int.Parse(args[regionProfile + 3]), args[manifest + 1], args.Contains("--profile-quick"), args.Contains("--profile-quality"));
                return 0;
            }
            int ipcBench = Array.IndexOf(args, "--mupdf-ipc-bench");
            if (ipcBench >= 0) { MuPdfIpcBench(args[ipcBench + 1], int.Parse(args[ipcBench + 2])); return 0; }
            int regionBench = Array.IndexOf(args, "--region-bench");
            int viewportBench = Array.IndexOf(args, "--viewport-workers-bench");
            int muPdfCheck = Array.IndexOf(args, "--mupdf-viewport-check");
            int fastMuPdfCheck = Array.IndexOf(args, "--mupdf-fast-check");
            int throughputViewerCheck = Array.IndexOf(args, "--mupdf-throughput-viewer-check");
            if (throughputViewerCheck >= 0)
            {
                TestThroughputMuPdfViewer(args[throughputViewerCheck + 1]);
                Console.WriteLine($"PASS ({_checks} throughput viewer checks)");
                return 0;
            }
            if (fastMuPdfCheck >= 0)
            {
                TestFastMuPdfAsync(args[fastMuPdfCheck + 1], args[fastMuPdfCheck + 2]).GetAwaiter().GetResult();
                Console.WriteLine($"PASS ({_checks} fast MuPDF checks)");
                return 0;
            }
            int predictiveCheck = Array.IndexOf(args, "--mupdf-predictive-viewer-check");
            if (predictiveCheck >= 0)
            {
                TestPredictiveMuPdfViewer(args[predictiveCheck + 1]);
                Console.WriteLine($"PASS ({_checks} predictive viewport checks)");
                return 0;
            }
            if (muPdfCheck >= 0)
            {
                TestMuPdfViewportAsync(args[muPdfCheck + 1]).GetAwaiter().GetResult();
                Console.WriteLine($"PASS ({_checks} experimental MuPDF viewport checks)");
                return 0;
            }
            if (viewportBench >= 0)
            {
                BenchmarkViewportWorkersAsync(args[viewportBench + 1], int.Parse(args[viewportBench + 2]),
                    int.Parse(args[viewportBench + 3])).GetAwaiter().GetResult();
                return 0;
            }
            if (args.Contains("--viewport-workers-check"))
            {
                TestViewportWorkersAsync().GetAwaiter().GetResult();
                Console.WriteLine($"PASS ({_checks} viewport worker checks)");
                return 0;
            }
            if (regionBench >= 0)
            {
                RegionBench(args[regionBench + 1], int.Parse(args[regionBench + 2]), int.Parse(args[regionBench + 3]), int.Parse(args[regionBench + 4]), args.Length > regionBench + 5 ? int.Parse(args[regionBench + 5]) : 40);
                return 0;
            }
            int retentionProfile = Array.IndexOf(args, "--retention-profile");
            if (retentionProfile >= 0)
            {
                int manifest = Array.IndexOf(args, "--profile-sources");
                ProfileReaderRetention(args[retentionProfile + 1], int.Parse(args[retentionProfile + 2]), args[manifest + 1]);
                return 0;
            }
            if (warmProfile >= 0)
            {
                int manifest = Array.IndexOf(args, "--profile-sources");
                ProfileWarmCache(args[warmProfile + 1], bool.Parse(args[warmProfile + 2]), args[manifest + 1]);
                return 0;
            }
            int backgroundProfile = Array.IndexOf(args, "--background-heavy-profile");
            if (backgroundProfile >= 0)
            {
                int sourceManifest = Array.IndexOf(args, "--profile-sources");
                int tuningOptions = Array.IndexOf(args, "--profile-tuning");
                ProfileHeavyFilesInBackground(args.Length > backgroundProfile + 1 ? args[backgroundProfile + 1] : "default",
                    args.Length > backgroundProfile + 2 ? int.Parse(args[backgroundProfile + 2]) : 4,
                    sourceManifest >= 0 ? args[sourceManifest + 1] : null,
                    tuningOptions >= 0 ? args[tuningOptions + 1] : null,
                    coldDeepProbe: args.Contains("--cold-deep-check"));
                return 0;
            }
            int tabsLayout = Array.IndexOf(args, "--multi-tab-layout");
            if (tabsLayout >= 0)
            {
                TestMultipleTabLayout(args[tabsLayout + 1]);
                Console.WriteLine($"PASS ({_checks} multi-tab checks)");
                return 0;
            }
            if (tabsProfile >= 0)
            {
                ProfileMultipleTabs(args[tabsProfile + 1], args.Length > tabsProfile + 2 ? args[tabsProfile + 2] : "current");
                return 0;
            }
            int inspectArg = Array.IndexOf(args, "--inspect-file");
            int signatureArg = Array.IndexOf(args, "--signature-info");
            if (signatureArg >= 0)
            {
                var info = PdfSignatureService.ReadAsync(args[signatureArg + 1]).GetAwaiter().GetResult();
                Console.WriteLine($"Signatures: {info.SignatureCount}; Certified: {info.IsCertified}; Error: {info.Error ?? "none"}");
                return info.Error == null ? 0 : 1;
            }
            int signedRenderArg = Array.IndexOf(args, "--render-signed-file");
            if (signedRenderArg >= 0)
            {
                RenderSignedSampleAsync(args[signedRenderArg + 1]).GetAwaiter().GetResult();
                return 0;
            }
            if (args.Contains("--dialog-preview"))
            {
                TestAppDialogs(preview: true);
                return 0;
            }
            if (args.Contains("--scroll-quality"))
            {
                TestRegionReuseAsync().GetAwaiter().GetResult();
                TestRegionReuseViewer();
                TestReaderRenderHandoffAsync().GetAwaiter().GetResult();
                TestVisiblePageRetriesAfterTransientFailure();
                TestReaderLargerCacheAsync().GetAwaiter().GetResult();
                TestContinuousScrollQuality();
                TestReaderWarmImages(); TestCrossFade(); TestPresentWhenReady();
                Console.WriteLine($"PASS ({_checks} scroll quality checks)");
                return 0;
            }
            if (args.Contains("--pool-policy"))
            {
                TestPdfiumPoolPolicy();
                Console.WriteLine($"PASS ({_checks} pool policy checks)");
                return 0;
            }
            if (inspectArg >= 0)
            {
                string src = System.IO.Path.GetFullPath(args[inspectArg + 1]);
                int pageNum = int.Parse(args[inspectArg + 2]);
                byte[] bytes = File.ReadAllBytes(src);
                using (var doc2 = new PdfDocument(new PdfReader(new MemoryStream(bytes))))
                {
                    var pg = doc2.GetPage(pageNum);
                    foreach (var a in pg.GetAnnotations())
                    {
                        if (!PdfName.FreeText.Equals(a.GetSubtype())) continue;
                        Console.WriteLine("Rect: " + a.GetRectangle());
                        Console.WriteLine("Contents: " + a.GetContents());
                        var ap = a.GetPdfObject().GetAsDictionary(PdfName.AP)?.GetAsStream(PdfName.N);
                        Console.WriteLine("AP bytes: " + (ap?.GetBytes()?.Length ?? -1));
                        if (ap != null) Console.WriteLine("AP:\n" + System.Text.Encoding.ASCII.GetString(ap.GetBytes()));
                        Console.WriteLine("---");
                    }
                }
                using (var docForSize = new PdfDocument(new PdfReader(new MemoryStream(bytes))))
                {
                    var mb = docForSize.GetPage(pageNum).GetMediaBox();
                    Console.WriteLine($"MediaBox: {mb.GetLeft()},{mb.GetBottom()} .. {mb.GetRight()},{mb.GetTop()} (w={mb.GetWidth()} h={mb.GetHeight()})");
                }
                var job2 = new PdfThumbnailService.MemoryRenderJob(pageNum - 1, 1600, 1200, new Int32Rect(0, 0, 1600, 1200));
                var bmp2 = PdfThumbnailService.RenderMemoryPagesAsync(bytes, new[] { job2 }).GetAwaiter().GetResult()[0];
                if (bmp2 != null)
                {
                    string outPng = System.IO.Path.Combine(Output, "inspect-file.png");
                    using var stream2 = new FileStream(outPng, FileMode.Create);
                    var enc2 = new PngBitmapEncoder(); enc2.Frames.Add(BitmapFrame.Create(bmp2)); enc2.Save(stream2);
                    Console.WriteLine("Saved " + outPng);
                }
                return 0;
            }
            int viewportArg = Array.IndexOf(args, "--viewport-pdf");
            if (viewportArg >= 0)
            {
                string source = System.IO.Path.GetFullPath(args[viewportArg + 1]);
                int pageArg = Array.IndexOf(args, "--page");
                int page = pageArg >= 0 ? int.Parse(args[pageArg + 1]) - 1 : 0;
                CompareViewportRasterAsync(source, page).GetAwaiter().GetResult();
                Console.WriteLine(RenderDiagnostics.Summary);
                return 0;
            }
            bool backgroundRegression = args.Contains("--background-regression");
            if (backgroundRegression)
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
            bool baseline = args.Contains("--baseline");
            if (baseline && !HasBaselineRenderer())
            {
                Console.Error.WriteLine("Baseline renderer has not been generated.");
                return 1;
            }
            if (!baseline)
            {
                TestPdfiumPoolPolicy();
                TestReaderTuningAudit();
                TestHotDocumentRetention();
                TestIdleDocumentRetirementAsync().GetAwaiter().GetResult();
                TestReaderPreviewRetentionAsync().GetAwaiter().GetResult();
                TestCacheAndOwnership(); TestBulkPages(); TestPresentationQueue(); TestViewportScheduling(); TestRetainedRefinement(); TestViewportMotion(); TestTwoPageLayout(); TestReaderZoomMath(); TestPrintRasterPlan(); TestOutlineEditing(); TestSquigglyAnnotation(); TestInkAnnotation(); TestCalloutAnnotation(); TestReplyAnnotation(); TestAutoCadShxTextFiltered(); TestPdfLinksAsync().GetAwaiter().GetResult(); TestPdfSecurityAsync().GetAwaiter().GetResult(); TestPdfProtectionRewriteAsync().GetAwaiter().GetResult();
                TestGateAsync().GetAwaiter().GetResult();
                TestSignaturePresenceAsync().GetAwaiter().GetResult();
                TestWidgetRenderingAsync().GetAwaiter().GetResult();
                TestSaveSafetyAsync().GetAwaiter().GetResult(); TestLayerMerge(); TestLayerToggleRendersAsync().GetAwaiter().GetResult(); TestLayerEdit(); TestSheetInfoSurvivesMerge(); TestSheetMatch(); TestSequentialInPlaceEdits(); TestXtSetRebuild(); TestLayerRenameOnMerge(); TestSaveHistory(); TestPrintSizePlan(); TestSheetRegisterCsv(); TestCommentExport(); TestTitleBlockReader(); TestPageMeasure(); TestPageDiff(); TestPageLabels(); TestSheetLinks(); TestPresence(); TestWorkerJob(); TestRegionReuseFormats(); TestRegionReuseWithMuPdfAsync().GetAwaiter().GetResult(); TestAnnotationConflictAsync().GetAwaiter().GetResult();
                if (!backgroundRegression) TestAppDialogs();
                TestReaderRenderHandoffAsync().GetAwaiter().GetResult(); TestVisiblePageRetriesAfterTransientFailure(); TestPreviewWarmDocumentSwitch();
                TestRegionReuseAsync().GetAwaiter().GetResult(); TestRegionReuseViewer();
                TestContinuousScrollQuality(); TestReaderLargerCacheAsync().GetAwaiter().GetResult(); TestReaderWarmImages(); TestCrossFade(); TestPresentWhenReady();
                if (!backgroundRegression) TestRecoveryAndToolbarUi();
            }
            RunNativeAsync(baseline).GetAwaiter().GetResult();
            Console.WriteLine($"PASS ({_checks} checks) {(baseline ? "baseline" : "optimized")}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }

    static void TestPdfiumPoolPolicy()
    {
        const string setting = "XTPDF_PDFIUM_INSTANCES";
        string? previous = Environment.GetEnvironmentVariable(setting);
        int balanced = Math.Clamp(Environment.ProcessorCount / 2, 1, 2);
        try
        {
            Environment.SetEnvironmentVariable(setting, null);
            Check(PdfiumPool.DesiredCount == balanced, "Default pool caps replicas at two for balanced native memory");
            foreach (int count in new[] { -1, 0, 1, 2, 3, 4, 6, 8, 9 })
            {
                Environment.SetEnvironmentVariable(setting, count.ToString());
                Check(PdfiumPool.DesiredCount == Math.Clamp(count, 1, PdfiumPool.MaxInstances),
                    $"Explicit pool override {count} remains clamped to the supported range");
            }
            Environment.SetEnvironmentVariable(setting, "invalid");
            Check(PdfiumPool.DesiredCount == balanced, "Invalid pool override falls back to balanced default");
        }
        finally { Environment.SetEnvironmentVariable(setting, previous); }
    }

    static BitmapSource Bitmap(int width = 100, int height = 100)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    static bool HasBaselineRenderer()
    {
#if HAS_BASELINE
        return true;
#else
        return false;
#endif
    }

    static void TestCacheAndOwnership()
    {
        var cache = new BitmapMemoryCache<int>(80_000);
        cache.Set(1, Bitmap()); cache.Set(2, Bitmap());
        cache.TryGetValue(1, out _); cache.Set(3, Bitmap());
        Check(cache.ContainsKey(1) && !cache.ContainsKey(2) && cache.Bytes == 80_000, "Byte budget/LRU");
        cache.Set(1, Bitmap(50, 50));
        Check(cache.Bytes == 50_000, "Replacement byte accounting");
        Check(cache.TryFind(k => k == 3, out int match, out _) && match == 3 &&
            !cache.TryFind(k => k == 9, out _, out _), "Matching cache lookup distinguishes live images from misses");
        cache.Clear();
        cache.Set(1, Bitmap(), _ => true); cache.Set(2, Bitmap(), _ => true); cache.Set(3, Bitmap(), _ => true);
        Check(cache.Count == 3, "Visible tiles survive temporary budget overflow");
        cache.Trim(k => k == 3);
        Check(!cache.ContainsKey(1) && cache.ContainsKey(3) && cache.Bytes <= cache.BudgetBytes, "Previously pinned tiles become reclaimable");
        var row = new PagePlacement { SourcePath = "fixture.pdf", PageNumber = 1 };
        var weak = AssignTemporaryBitmap(row);
        Collect();
        Check(!weak.TryGetTarget(out _) && row.ReaderBitmap == null && row.Thumbnail == null, "Undo placement must not own bitmaps");
        Check(row.HasLoadedThumbnailOnce, "Progress survives eviction");
        var image = new Image();
        image.SetBinding(Image.SourceProperty, new Binding(nameof(PagePlacement.ReaderDisplayBitmap)) { Source = row });
        AssignTemporaryBitmap(row);
        Collect();
        Check(image.Source != null && row.ReaderBitmap != null, "Visible WPF binding keeps image alive after cache eviction");
        GC.KeepAlive(image);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference<BitmapSource> AssignTemporaryBitmap(PagePlacement row)
    {
        var bitmap = Bitmap(); row.ReaderBitmap = bitmap; row.Thumbnail = bitmap;
        return new WeakReference<BitmapSource>(bitmap);
    }
    static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    static void TestBulkPages()
    {
        var document = new WorkspaceDocument { SourcePath = "large.pdf" };
        int resets = 0; document.Pages.CollectionChanged += (_, _) => resets++;
        var sw = Stopwatch.StartNew();
        document.Pages.AddRange(Enumerable.Range(1, 10000).Select(p => new PagePlacement { SourcePath = document.SourcePath, PageNumber = p }));
        sw.Stop();
        Check(resets == 1 && document.Pages[9999].Index == 10000, "Bulk pages send one collection notification and assign indices");
        document.Pages[0].Thumbnail = Bitmap(); document.Pages[0].Thumbnail = null;
        Check(document.LoadedThumbnailCount == 1, "Loaded thumbnail count survives release");
        document.Pages.RemoveAt(0);
        Check(document.LoadedThumbnailCount == 0 && document.Pages[0].Index == 1, "Removal refreshes indices and progress");
        Check(!document.IsThumbnailLoading, "Lazy thumbnails do not leave a perpetual loading spinner");
        Console.WriteLine($"10,000 page rows, one bulk notification: {sw.Elapsed.TotalMilliseconds:F1} ms");
    }

    static void TestPrintRasterPlan()
    {
        // A3 ngang, actual size: standard in một/two dải nhỏ; CAD high = 600 DPI nhưng không dồn cả trang vào RAM.
        var standard = PdfPrintService.GetRasterPlan(1654, 1169, PrintQuality.Standard);
        var cadHigh = PdfPrintService.GetRasterPlan(1654, 1169, PrintQuality.CadHigh);
        Check(standard.EffectiveDpi == 300 && standard.FullWidth is > 4_900 and < 5_000, "Print standard A3 uses 300 DPI");
        Check(cadHigh.EffectiveDpi == 600 && cadHigh.FullWidth is > 9_900 and < 10_000 && cadHigh.BandHeight < cadHigh.FullHeight,
            "Print CAD high quality uses 600 DPI in memory-bounded bands");
        Check((long)cadHigh.FullWidth * cadHigh.BandHeight <= 12_000_000, "Print band stays within pixel memory budget");
        var oversized = PdfPrintService.GetRasterPlan(4600, 3300, PrintQuality.CadHigh); // A0
        Check(Math.Max(oversized.FullWidth, oversized.FullHeight) == 20_000 && oversized.EffectiveDpi < 600,
            "Print caps oversized sheets without distorting aspect ratio");
    }

    static async Task TestPdfLinksAsync()
    {
        string path = System.IO.Path.Combine(Output, "links.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            var first = document.AddNewPage(new PageSize(400, 300));
            var second = document.AddNewPage(new PageSize(400, 300));
            var web = new iText.Kernel.Pdf.Annot.PdfLinkAnnotation(new Rectangle(100, 100, 100, 100));
            web.SetAction(iText.Kernel.Pdf.Action.PdfAction.CreateURI("https://intranet.example.test/spec"));
            first.AddAnnotation(web);
            var goTo = new iText.Kernel.Pdf.Annot.PdfLinkAnnotation(new Rectangle(220, 100, 100, 100));
            goTo.SetAction(iText.Kernel.Pdf.Action.PdfAction.CreateGoTo(iText.Kernel.Pdf.Navigation.PdfExplicitDestination.CreateFit(second)));
            first.AddAnnotation(goTo);
            document.AddNamedDestination("section-2", iText.Kernel.Pdf.Navigation.PdfExplicitDestination.CreateFit(second).GetPdfObject());
            var namedGoTo = new iText.Kernel.Pdf.Annot.PdfLinkAnnotation(new Rectangle(100, 190, 100, 80));
            namedGoTo.SetAction(iText.Kernel.Pdf.Action.PdfAction.CreateGoTo("section-2"));
            first.AddAnnotation(namedGoTo);
        }
        var webTarget = await PdfLinkService.FindAtAsync(path, 1, .375, .5);
        var pageTarget = await PdfLinkService.FindAtAsync(path, 1, .675, .5);
        Check(webTarget?.Uri == "https://intranet.example.test/spec" && webTarget.PageNumber == null, "PDF URI link hit-test");
        Check(pageTarget?.PageNumber == 2 && pageTarget.Uri == null, "PDF GoTo link hit-test");
        var namedTarget = await PdfLinkService.FindAtAsync(path, 1, .375, .25);
        Check(namedTarget?.PageNumber == 2 && namedTarget.Uri == null, "PDF named GoTo link hit-test");
        AnnotationStore.Forget(path);

        string encryptedPath = System.IO.Path.Combine(Output, "encrypted-links.pdf");
        using (var writer = new PdfWriter(encryptedPath, new WriterProperties().SetStandardEncryption(
            System.Text.Encoding.UTF8.GetBytes("link-user"), System.Text.Encoding.UTF8.GetBytes("link-owner"),
            EncryptionConstants.ALLOW_COPY, EncryptionConstants.ENCRYPTION_AES_128)))
        using (var document = new PdfDocument(writer))
        {
            var first = document.AddNewPage(new PageSize(400, 300));
            var second = document.AddNewPage(new PageSize(400, 300));
            var goTo = new iText.Kernel.Pdf.Annot.PdfLinkAnnotation(new Rectangle(100, 100, 100, 100));
            goTo.SetAction(iText.Kernel.Pdf.Action.PdfAction.CreateGoTo(iText.Kernel.Pdf.Navigation.PdfExplicitDestination.CreateFit(second)));
            first.AddAnnotation(goTo);
        }
        await PdfThumbnailService.SetDocumentPasswordAsync(encryptedPath, "link-user");
        var encryptedTarget = await PdfLinkService.FindAtAsync(encryptedPath, 1, .375, .5);
        Check(encryptedTarget?.PageNumber == 2, "PDF link uses the in-memory password for encrypted documents");
        AnnotationStore.Forget(encryptedPath);
        await PdfThumbnailService.ForgetDocumentPasswordAsync(encryptedPath);

        string bookmarkPath = System.IO.Path.Combine(Output, "encrypted-bookmarks.pdf");
        using (var writer = new PdfWriter(bookmarkPath, new WriterProperties().SetStandardEncryption(
            System.Text.Encoding.UTF8.GetBytes("bookmark-user"), System.Text.Encoding.UTF8.GetBytes("bookmark-owner"),
            EncryptionConstants.ALLOW_COPY, EncryptionConstants.ENCRYPTION_AES_128)))
        using (var document = new PdfDocument(writer))
        {
            document.AddNewPage(new PageSize(400, 300));
            var second = document.AddNewPage(new PageSize(400, 300));
            document.GetOutlines(false).AddOutline("Second page")
                .AddDestination(iText.Kernel.Pdf.Navigation.PdfExplicitDestination.CreateFit(second));
        }
        await PdfThumbnailService.SetDocumentPasswordAsync(bookmarkPath, "bookmark-user");
        var bookmarks = PdfOutlineService.ReadBookmarks(bookmarkPath);
        Check(bookmarks.Count == 1 && bookmarks[0].Title == "Second page" && bookmarks[0].PageNumber == 2,
            "Bookmarks use the in-memory password for encrypted documents");
        await PdfThumbnailService.ForgetDocumentPasswordAsync(bookmarkPath);

        string layersPath = System.IO.Path.Combine(Output, "encrypted-layers.pdf");
        using (var writer = new PdfWriter(layersPath, new WriterProperties().SetStandardEncryption(
            System.Text.Encoding.UTF8.GetBytes("layer-user"), System.Text.Encoding.UTF8.GetBytes("layer-owner"),
            EncryptionConstants.ALLOW_COPY, EncryptionConstants.ENCRYPTION_AES_128)))
        using (var document = new PdfDocument(writer))
        {
            _ = new iText.Kernel.Pdf.Layer.PdfLayer("Secure layer", document);
            document.AddNewPage(new PageSize(400, 300));
        }
        await PdfThumbnailService.SetDocumentPasswordAsync(layersPath, "layer-user");
        var layers = PdfLayerService.ReadLayers(layersPath);
        Check(layers.Names.Values.Contains("Secure layer"), "Layers use the in-memory password for encrypted documents");
        await PdfThumbnailService.ForgetDocumentPasswordAsync(layersPath);

        string editablePath = System.IO.Path.Combine(Output, "encrypted-owner-edit.pdf");
        using (var writer = new PdfWriter(editablePath, new WriterProperties().SetStandardEncryption(
            System.Text.Encoding.UTF8.GetBytes("edit-user"), System.Text.Encoding.UTF8.GetBytes("edit-owner"),
            EncryptionConstants.ALLOW_COPY, EncryptionConstants.ENCRYPTION_AES_128)))
        using (var document = new PdfDocument(writer)) document.AddNewPage(new PageSize(400, 300));
        await PdfThumbnailService.SetDocumentPasswordAsync(editablePath, "edit-owner");
        PdfPageEditService.RotatePages(editablePath, new[] { 1 }, 90);
        var editProperties = new ReaderProperties().SetPassword(System.Text.Encoding.UTF8.GetBytes("edit-user"));
        using (var edited = new PdfDocument(new PdfReader(editablePath, editProperties)))
            Check(edited.GetPage(1).GetRotation() == 90, "Owner password supports incremental edits to encrypted PDF");
        await PdfThumbnailService.ForgetDocumentPasswordAsync(editablePath);
    }

    static async Task TestPdfSecurityAsync()
    {
        string path = System.IO.Path.Combine(Output, "security.pdf");
        using (var writer = new PdfWriter(path, new WriterProperties().SetStandardEncryption(
            System.Text.Encoding.UTF8.GetBytes("user-password"),
            System.Text.Encoding.UTF8.GetBytes("owner-password"),
            EncryptionConstants.ALLOW_PRINTING,
            EncryptionConstants.ENCRYPTION_AES_128)))
        using (var document = new PdfDocument(writer)) document.AddNewPage();

        await PdfThumbnailService.SetDocumentPasswordAsync(path, "user-password");
        var info = await PdfSecurityService.ReadAsync(path);
        Check(info.Error == null && info.IsEncrypted && !info.IsOwner,
            $"Security service reads encrypted user document (error={info.Error}, encrypted={info.IsEncrypted}, owner={info.IsOwner})");
        Check(info.CanPrint && !info.CanCopy && !info.CanModify && !info.CanAnnotate && !info.CanFillForms,
            "Security service reports declared PDF permissions");
        await PdfThumbnailService.ForgetDocumentPasswordAsync(path);
    }

    static void TestSquigglyAnnotation()
    {
        string path = System.IO.Path.Combine(Output, "squiggly.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            var page = document.AddNewPage(new PageSize(400, 300));
            var geometry = PdfQuickAnnotationService.GetGeometry(page);
            var spec = new QuickAnnotationSpec("test-squiggly", QuickAnnotationKind.Squiggly, 1, .1, .2, .8, .3, "Review this")
            {
                Format = PdfQuickAnnotationService.EncodeTextHighlight(new[] { (.1, .2, .8, .3) }),
                Color = "#ED1C24"
            };
            PdfQuickAnnotationService.AddGenerated(document, page, spec, new PdfQuickAnnotationService.FontSet());
            Check(page.GetAnnotations().Count == 1 && PdfName.Squiggly.Equals(page.GetAnnotations()[0].GetSubtype()), "Squiggly writes a standard PDF Squiggly annotation");
        }
        using (var document = new PdfDocument(new PdfReader(path)))
        {
            var page = document.GetPage(1);
            var annotations = PdfQuickAnnotationService.ReadAnnotations(page, PdfQuickAnnotationService.GetGeometry(page), 1);
            Check(annotations.Count == 1 && annotations[0].Kind == QuickAnnotationKind.Squiggly, "Squiggly annotations round-trip as editable markup");
            Check(PdfQuickAnnotationService.TextHighlightRects(annotations[0].Format).Count == 1, "Squiggly preserves its text line geometry");
        }
    }

    static void TestOutlineEditing()
    {
        string path = System.IO.Path.Combine(Output, "outline-editing.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            document.AddNewPage(new PageSize(400, 300));
            document.AddNewPage(new PageSize(400, 300));
            var root = document.GetOutlines(false);
            var first = root.AddOutline("First");
            first.AddDestination(iText.Kernel.Pdf.Navigation.PdfExplicitDestination.CreateFit(document.GetPage(1)));
            var child = first.AddOutline("Child");
            child.AddDestination(iText.Kernel.Pdf.Navigation.PdfExplicitDestination.CreateFit(document.GetPage(2)));
            var second = root.AddOutline("Second");
            second.AddDestination(iText.Kernel.Pdf.Navigation.PdfExplicitDestination.CreateFit(document.GetPage(2)));
        }

        Check(PdfOutlineService.MoveBookmark(path, new[] { 1 }, -1), "Bookmark move reports a real reorder");
        var moved = PdfOutlineService.ReadBookmarks(path);
        Check(moved.Select(x => x.Title).SequenceEqual(new[] { "Second", "First" }), "Bookmark move rewrites sibling order");
        Check(moved[1].Children.Count == 1 && moved[1].Children[0].Title == "Child" && moved[1].Children[0].PageNumber == 2,
            "Bookmark reorder preserves descendants and destinations");
        Check(!PdfOutlineService.MoveBookmark(path, new[] { 0 }, -1), "Bookmark move leaves the first sibling unchanged");
        PdfOutlineService.RenameBookmark(path, new[] { 0 }, "Renamed");
        Check(PdfOutlineService.ReadBookmarks(path)[0].Title == "Renamed", "Bookmark rename persists after reorder");

        // AddBookmark writes through PdfPageEditService.EditInPlace (append mode) onto a file that already
        // has an outline tree — this used to silently no-op (new object written to disk, but the parent's
        // /First·/Last never repointed at it, so it never showed up on read-back). Guard against regressing.
        PdfOutlineService.AddBookmark(path, Array.Empty<int>(), "Appended top-level", 2);
        var afterAdd = PdfOutlineService.ReadBookmarks(path);
        Check(afterAdd.Count == 3 && afterAdd[2].Title == "Appended top-level" && afterAdd[2].PageNumber == 2,
            "AddBookmark on a file that already has an outline tree is visible on read-back");
        PdfOutlineService.AddBookmark(path, afterAdd[2].Path, "Nested under appended", 1);
        var afterNestedAdd = PdfOutlineService.ReadBookmarks(path);
        Check(afterNestedAdd[2].Children.Count == 1 && afterNestedAdd[2].Children[0].Title == "Nested under appended",
            "AddBookmark under a non-root parent is visible on read-back");
        PdfOutlineService.DeleteBookmark(path, afterNestedAdd[2].Path);
        Check(PdfOutlineService.ReadBookmarks(path).Count == 2, "DeleteBookmark removes the node (and its child) from the tree");

        // Same AddBookmark path, but the very first bookmark ever added to a document with no outline at all
        // (a different code path inside GetOrCreateRoot — no pre-existing /Outlines dictionary to hang off of).
        string blankPath = System.IO.Path.Combine(Output, "outline-from-scratch.pdf");
        using (var document = new PdfDocument(new PdfWriter(blankPath))) document.AddNewPage(new PageSize(400, 300));
        PdfOutlineService.AddBookmark(blankPath, Array.Empty<int>(), "First ever bookmark", 1);
        var fromScratch = PdfOutlineService.ReadBookmarks(blankPath);
        Check(fromScratch.Count == 1 && fromScratch[0].Title == "First ever bookmark" && fromScratch[0].PageNumber == 1,
            "AddBookmark creates the outline tree from scratch on a file with none");
    }

    static void TestInkAnnotation()
    {
        string path = System.IO.Path.Combine(Output, "ink.pdf");
        var stroke = new[] { (.15, .2), (.3, .45), (.6, .35), (.8, .7) };
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            var page = document.AddNewPage(new PageSize(400, 300));
            var spec = new QuickAnnotationSpec("test-ink", QuickAnnotationKind.Ink, 1, .145, .195, .805, .705, "")
            {
                Format = PdfQuickAnnotationService.EncodeInkPoints(stroke),
                Color = "#D74B31"
            };
            PdfQuickAnnotationService.AddGenerated(document, page, spec, new PdfQuickAnnotationService.FontSet());
            Check(page.GetAnnotations().Count == 1 && PdfName.Ink.Equals(page.GetAnnotations()[0].GetSubtype()), "Pencil writes a standard PDF Ink annotation");
        }
        using (var document = new PdfDocument(new PdfReader(path)))
        {
            var page = document.GetPage(1);
            var annotations = PdfQuickAnnotationService.ReadAnnotations(page, PdfQuickAnnotationService.GetGeometry(page), 1);
            Check(annotations.Count == 1 && annotations[0].Kind == QuickAnnotationKind.Ink, "Pencil Ink round-trips as an editable annotation");
            Check(PdfQuickAnnotationService.InkPoints(annotations[0].Format).Count == stroke.Length, "Pencil preserves every stroke point");
        }
    }

    static void TestCalloutAnnotation()
    {
        string path = System.IO.Path.Combine(Output, "callout.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            var page = document.AddNewPage(new PageSize(400, 300));
            var spec = new QuickAnnotationSpec("test-callout", QuickAnnotationKind.Callout, 1, .42, .18, .42, .18, "Check this detail")
            {
                Format = PdfQuickAnnotationService.EncodeCallout(.2, .55, TextFormat.Default.Encode())
            };
            PdfQuickAnnotationService.AddGenerated(document, page, PdfQuickAnnotationService.WithMeasuredSize(spec, PdfQuickAnnotationService.GetGeometry(page)), new PdfQuickAnnotationService.FontSet());
            var annotation = page.GetAnnotations().Single();
            // 4 số = 2 điểm = 1 đoạn thẳng — đúng kiểu Foxit thật (đã mở Foxit vẽ thử để so: đường dẫn không có khúc gấp).
            Check(PdfName.FreeText.Equals(annotation.GetSubtype()) && annotation.GetPdfObject().GetAsArray(PdfName.CL)?.Size() == 4,
                "Callout writes FreeText plus a single straight leader line (no knee, matching real Foxit)");
            Check(annotation.GetPdfObject().GetAsArray(PdfName.C)?.Size() == 3 && annotation.GetPdfObject().GetAsArray(PdfName.IC)?.Size() == 3,
                "Callout has a border colour and a fill colour (Foxit-style box, not transparent)");
        }
        using (var document = new PdfDocument(new PdfReader(path)))
        {
            var page = document.GetPage(1);
            var annotation = PdfQuickAnnotationService.ReadAnnotations(page, PdfQuickAnnotationService.GetGeometry(page), 1).Single();
            Check(annotation.Kind == QuickAnnotationKind.Callout && annotation.Text == "Check this detail", "Callout round-trips as editable text");
            var leader = PdfQuickAnnotationService.DecodeCallout(annotation.Format);
            Check(Math.Abs(leader.TipU - .2) < .01 && Math.Abs(leader.TipV - .55) < .01, "Callout preserves its target point");
            // /Rect phủ cả mũi tên; hộp đọc lại phải là hộp chữ thật (bắt đầu ở .42,.18), không phình ra tới điểm chỉ.
            Check(Math.Abs(annotation.U1 - .42) < .005 && Math.Abs(annotation.V1 - .18) < .005, "Callout reloads its text box, not the box+arrow /Rect");

            // Dời tại chỗ (giữ appearance gốc): đọc lại thì cả hộp lẫn điểm chỉ đều dời theo.
            var moved = annotation.Translate(.1, .05);
            PdfQuickAnnotationService.ApplyChanges(document, new[] { new QuickAnnotationChange(annotation, moved) });
            var reread = PdfQuickAnnotationService.ReadAnnotations(page, PdfQuickAnnotationService.GetGeometry(page), 1).Single();
            var movedTip = PdfQuickAnnotationService.DecodeCallout(reread.Format);
            Check(Math.Abs(reread.U1 - .52) < .005 && Math.Abs(movedTip.TipU - .3) < .005 && Math.Abs(movedTip.TipV - .6) < .005,
                "Moving a callout moves its box and its arrow tip together");
        }

        // Trang xoay 90°/270° (thường gặp ở bản vẽ kỹ thuật khổ ngang lưu trong khung giấy dọc): DisplayRectToUser
        // hoán trục X/Y, nên nếu box/tip được đổi sang user-space TRƯỚC rồi mới tính min/max/kích thước AP, BBox
        // dựng theo user-space (đã hoán trục) không còn khớp width/height vẽ theo hiển thị (chưa hoán) nữa — PDF
        // tự co giãn lệch trục cho khớp /Rect khi hiển thị, chữ/đường dẫn bị bóp méo thành vệt ngắn không đọc
        // được. Từng thấy tận mắt trên file thật; bài dưới khoá lại bằng cách so AP đã tô đúng tỉ lệ hộp thật
        // (đo được biên chữ đúng như đã vẽ), không lệch trục.
        string rotatedPath = System.IO.Path.Combine(Output, "callout-rotated.pdf");
        using (var document = new PdfDocument(new PdfWriter(rotatedPath)))
        {
            var page = document.AddNewPage(new PageSize(600, 400));
            page.SetRotation(90);
            var spec = new QuickAnnotationSpec("test-callout-rot", QuickAnnotationKind.Callout, 1, .5, .3, .5, .3, "Hello world")
            {
                Format = PdfQuickAnnotationService.EncodeCallout(.15, .75, TextFormat.Default.Encode())
            };
            PdfQuickAnnotationService.AddGenerated(document, page, PdfQuickAnnotationService.WithMeasuredSize(spec, PdfQuickAnnotationService.GetGeometry(page)), new PdfQuickAnnotationService.FontSet());
            var annotation = page.GetAnnotations().Single();
            var ap = annotation.GetPdfObject().GetAsDictionary(PdfName.AP)!.GetAsStream(PdfName.N)!;
            var bbox = ap.GetAsArray(PdfName.BBox)!;
            var rect = annotation.GetRectangle().ToRectangle();
            double bboxW = bbox.GetAsNumber(2).DoubleValue(), bboxH = bbox.GetAsNumber(3).DoubleValue();
            // Trang xoay 90°: /Rect (user-space) có bề ngang/dọc HOÁN NHAU so với BBox (hệ hiển thị, chưa xoay) —
            // đúng ý; sai (lỗi cũ) là khi BBox lại đi theo đúng bề ngang/dọc của /Rect (không hoán).
            bool swappedAsExpected = Math.Abs(bboxW - rect.GetHeight()) < 1 && Math.Abs(bboxH - rect.GetWidth()) < 1;
            Check(swappedAsExpected, "Callout AP BBox stays in display-space units on a rotated page (not swapped to match /Rect)");
        }

        // Callout vẫn là 1 object (mũi tên + hộp cùng 1 FreeText, đúng kiểu Foxit) nhưng hộp giờ lấy kích thước
        // THẬT từ spec.U1..V2 (chỉnh tay được qua grip) thay vì tự co theo chữ mỗi lần vẽ — nên chữ phải word-wrap.
        string resizedPath = System.IO.Path.Combine(Output, "callout-resized.pdf");
        using (var document = new PdfDocument(new PdfWriter(resizedPath)))
        {
            var page = document.AddNewPage(new PageSize(400, 300));
            var geometry = PdfQuickAnnotationService.GetGeometry(page);
            var spec = new QuickAnnotationSpec("test-callout-resized", QuickAnnotationKind.Callout, 1, .1, .1,
                .1 + 90.0 / geometry.DisplayWidth, .1 + 60.0 / geometry.DisplayHeight, // hộp hẹp, ép phải xuống dòng
                "A long callout sentence that must wrap onto more than one line")
            {
                Format = PdfQuickAnnotationService.EncodeCallout(.6, .1, TextFormat.Default.Encode())
            };
            PdfQuickAnnotationService.AddGenerated(document, page, spec, new PdfQuickAnnotationService.FontSet());
            var annotation = page.GetAnnotations().Single();
            Check(PdfName.FreeText.Equals(annotation.GetSubtype()) && annotation.GetPdfObject().GetAsArray(PdfName.CL)?.Size() == 4,
                "Manually resized callout is still 1 FreeText object with its leader line — not split in two");
            var ap = annotation.GetPdfObject().GetAsDictionary(PdfName.AP)!.GetAsStream(PdfName.N)!;
            string content = System.Text.Encoding.ASCII.GetString(ap.GetBytes());
            int showTextCount = System.Text.RegularExpressions.Regex.Matches(content, @"\bTj\b").Count;
            Check(showTextCount > 1, "Callout text wraps onto multiple lines to fit a manually resized (narrow) box");
        }
    }

    static void TestReplyAnnotation()
    {
        string path = System.IO.Path.Combine(Output, "reply.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            var page = document.AddNewPage(new PageSize(400, 300));
            var geometry = PdfQuickAnnotationService.GetGeometry(page);
            PdfQuickAnnotationService.AddGenerated(document, page, PdfQuickAnnotationService.WithMeasuredSize(new QuickAnnotationSpec("parent", QuickAnnotationKind.Comment, 1, .2, .2, 0, 0, "Question"), geometry), new PdfQuickAnnotationService.FontSet());
            PdfQuickAnnotationService.AddGenerated(document, page, PdfQuickAnnotationService.WithMeasuredSize(new QuickAnnotationSpec("reply", QuickAnnotationKind.Reply, 1, .3, .2, 0, 0, "Answer") { Format = "R|parent" }, geometry), new PdfQuickAnnotationService.FontSet());
            var reply = page.GetAnnotations().First(a => a.GetName()?.ToUnicodeString() == "reply");
            Check(reply.GetPdfObject().Get(PdfName.IRT) != null && reply.GetPdfObject().GetAsName(PdfName.RT)?.GetValue() == "R", "Reply writes standard IRT parent relationship");
            // Word-style: chỉ 1 icon trên trang cho cả luồng — reply không tự vẽ icon riêng (Hidden), khác chú thích gốc.
            const int hiddenFlag = 2; // iText.Kernel.Pdf.Annot.PdfAnnotation.HIDDEN
            int flags = reply.GetPdfObject().GetAsNumber(PdfName.F)?.IntValue() ?? 0;
            Check((flags & hiddenFlag) != 0, "Reply is flagged Hidden so it draws no icon of its own on the page");
            var parent = page.GetAnnotations().First(a => a.GetName()?.ToUnicodeString() == "parent");
            int parentFlags = parent.GetPdfObject().GetAsNumber(PdfName.F)?.IntValue() ?? 0;
            Check((parentFlags & hiddenFlag) == 0, "The root comment (not a reply) still draws its one icon normally");
        }
    }

    /// <summary>Bản vẽ AutoCAD xuất PDF: chữ font SHX được thay bằng annotation /Square phủ đúng vùng chữ, tác giả
    /// cố định "AutoCAD SHX Text" — chỉ để tìm/copy chữ, không phải comment thật. 1 file bản vẽ kỹ thuật có thể có
    /// hàng nghìn cái (xem file thật 165MB: "Comments (4460)" trước khi lọc) — phải loại khỏi ReadAnnotations chứ
    /// không phải lọc ở panel, vì chúng còn Selectable (chặn đặt chú thích mới/làm chậm trang).</summary>
    static void TestAutoCadShxTextFiltered()
    {
        string path = System.IO.Path.Combine(Output, "autocad-shx.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            var page = document.AddNewPage(new PageSize(400, 300));

            void AddRawSquare(string name, string author)
            {
                var obj = new PdfDictionary();
                obj.Put(PdfName.Type, PdfName.Annot);
                obj.Put(PdfName.Subtype, PdfName.Square);
                obj.Put(PdfName.Rect, new PdfArray(new iText.Kernel.Geom.Rectangle(50, 50, 30, 10)));
                obj.Put(PdfName.NM, new PdfString(name));
                obj.Put(PdfName.T, new PdfString(author, iText.IO.Font.PdfEncodings.UNICODE_BIG));
                obj.Put(PdfName.Contents, new PdfString("1.72", iText.IO.Font.PdfEncodings.UNICODE_BIG));
                var annot = iText.Kernel.Pdf.Annot.PdfAnnotation.MakeAnnotation(obj);
                page.AddAnnotation(annot);
            }

            AddRawSquare("shx1", "AutoCAD SHX Text");
            AddRawSquare("shx2", "AutoCAD SHX Text");
            AddRawSquare("real", "condu"); // chú thích thật (hoặc Square app khác tạo) — không được lọc theo
        }
        using (var document = new PdfDocument(new PdfReader(path)))
        {
            var page = document.GetPage(1);
            var annotations = PdfQuickAnnotationService.ReadAnnotations(page, PdfQuickAnnotationService.GetGeometry(page), 1);
            Check(annotations.Count == 1 && annotations[0].Author == "condu",
                "AutoCAD SHX Text squares are filtered out of ReadAnnotations, a real Square annotation is not");
        }
    }

    static async Task TestPdfProtectionRewriteAsync()
    {
        string path = System.IO.Path.Combine(Output, "protect-rewrite.pdf");
        using (var writer = new PdfWriter(path))
        using (var document = new PdfDocument(writer))
        {
            document.AddNewPage(new PageSize(400, 300));
            document.AddNewPage(new PageSize(400, 300));
        }

        PdfSecurityService.ApplyProtection(path, new PdfProtectionOptions(
            CurrentOwnerPassword: null, UserPassword: "open-secret", OwnerPassword: "owner-secret",
            AllowPrint: true, AllowCopy: false, AllowModify: false, AllowAnnotate: true));
        await PdfThumbnailService.SetDocumentPasswordAsync(path, "open-secret");
        var userInfo = await PdfSecurityService.ReadAsync(path);
        Check(userInfo.Error == null && userInfo.IsEncrypted && !userInfo.IsOwner && userInfo.CanPrint && !userInfo.CanCopy && !userInfo.CanModify && userInfo.CanAnnotate,
            "Protection rewrite applies the user password and declared permissions");
        using (var opened = new PdfDocument(new PdfReader(path, new ReaderProperties().SetPassword(System.Text.Encoding.UTF8.GetBytes("open-secret")))))
            Check(opened.GetNumberOfPages() == 2, "Protection rewrite preserves PDF pages");

        PdfSecurityService.ApplyProtection(path, new PdfProtectionOptions(
            CurrentOwnerPassword: "owner-secret", UserPassword: "", OwnerPassword: "",
            AllowPrint: false, AllowCopy: false, AllowModify: false, AllowAnnotate: false, RemoveProtection: true));
        await PdfThumbnailService.ForgetDocumentPasswordAsync(path);
        var unprotected = await PdfSecurityService.ReadAsync(path);
        Check(unprotected.Error == null && !unprotected.IsEncrypted && unprotected.CanPrint && unprotected.CanCopy,
            "Protection rewrite removes encryption with the owner password");
    }

    static void TestPresentationQueue()
    {
        using var queue = new FramePresentationQueue(ex => throw ex);
        int displayed = 0;
        var jobs = Enumerable.Range(0, 12).Select(_ => queue.Enqueue(() => displayed++, default)).ToArray();
        queue.DrainFrame();
        Check(displayed > 0 && displayed <= 4 && displayed < 12, "Tile installation is bounded per frame");
        while (queue.PendingCount > 0) queue.DrainFrame();
        Check(displayed == 12 && jobs.All(t => t.IsCompletedSuccessfully), "Queued presentation completes");
        using var cancel = new CancellationTokenSource();
        var stale = queue.Enqueue(() => displayed++, cancel.Token);
        cancel.Cancel(); queue.DrainFrame();
        Check(displayed == 12 && stale.IsCompletedSuccessfully, "Obsolete frame work is skipped");
        var hidden = queue.Enqueue(() => displayed++, default);
        queue.Clear();
        Check(hidden.IsCompletedSuccessfully && queue.PendingCount == 0 && displayed == 12,
            "Hiding reader releases pending presentation closures");
    }

    static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    static void TestViewportScheduling()
    {
        int updates = 0, concurrent = 0, peak = 0;
        using var scheduler = new ViewportRenderScheduler(Dispatcher.CurrentDispatcher, async () =>
        {
            updates++; concurrent++; peak = Math.Max(peak, concurrent);
            await Task.Delay(25); concurrent--;
        });
        var pan = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(5) };
        pan.Tick += (_, _) => scheduler.Request(false);
        pan.Start(); Pump(TimeSpan.FromMilliseconds(220)); pan.Stop();
        Check(updates >= 2, "Continued pan does not starve rendering until mouse release");
        Pump(TimeSpan.FromMilliseconds(80));
        Check(peak == 1 && concurrent == 0, "Viewport updates never overlap");
        int before = updates;
        scheduler.Request(true); Pump(TimeSpan.FromMilliseconds(40));
        scheduler.Request(true); Pump(TimeSpan.FromMilliseconds(40));
        Check(updates == before, "Resolution changes debounce while preserving old image");
        Pump(TimeSpan.FromMilliseconds(160));
        Check(updates == before + 1, "Settled zoom renders once");
        scheduler.Request(false); scheduler.Cancel(); Pump(TimeSpan.FromMilliseconds(40));
        Check(updates == before + 1, "Canceled viewport timer does not revive hidden reader");
    }

    static async Task TestGateAsync()
    {
        var gate = new PdfRenderGate();
        await gate.WaitAsync(PdfRenderPriority.Visible);
        var background = gate.WaitAsync(PdfRenderPriority.Background);
        var visible = gate.WaitAsync(PdfRenderPriority.Visible);
        using var cancel = new CancellationTokenSource();
        var cancelled = gate.WaitAsync(PdfRenderPriority.Visible, cancel.Token);
        cancel.Cancel();
        try { await cancelled; throw new Exception("Canceled waiter completed"); } catch (OperationCanceledException) { _checks++; }
        gate.Release(); await visible.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!background.IsCompleted, "Visible job precedes earlier background job");
        gate.Release(); await background.WaitAsync(TimeSpan.FromSeconds(2)); gate.Release();
        // Simulate a scrollbar jump past hundreds of obsolete visible requests.
        await gate.WaitAsync(PdfRenderPriority.Visible);
        using var oldViewport = new CancellationTokenSource();
        var obsolete = Enumerable.Range(0, 499)
            .Select(_ => gate.WaitAsync(PdfRenderPriority.Visible, oldViewport.Token)).ToArray();
        oldViewport.Cancel();
        var destination = gate.WaitAsync(PdfRenderPriority.Visible);
        gate.Release();
        await destination.WaitAsync(TimeSpan.FromSeconds(2));
        try { await Task.WhenAll(obsolete).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { }
        Check(obsolete.All(t => t.IsCanceled), "Jump drops all 499 obsolete visible requests before destination enters");
        gate.Release();
        int active = 0, peak = 0;
        await Task.WhenAll(Enumerable.Range(0, 100).Select(async i =>
        {
            await gate.WaitAsync((PdfRenderPriority)(i % 3));
            int count = Interlocked.Increment(ref active); peak = Math.Max(peak, count);
            await Task.Yield(); Interlocked.Decrement(ref active); gate.Release();
        }));
        Check(peak == 1, "Concurrent requests preserve native serialization");
        var buffers = new PdfRenderGate(2);
        await buffers.WaitAsync(PdfRenderPriority.Visible); await buffers.WaitAsync(PdfRenderPriority.Visible);
        var third = buffers.WaitAsync(PdfRenderPriority.Visible);
        Check(!third.IsCompleted, "Only two progressive bitmap buffers can be active");
        buffers.Release(); await third; buffers.Release(); buffers.Release();
    }

    static string CreateFixture()
    {
        string path = System.IO.Path.Combine(Output, "fixture.pdf");
        using var pdf = new PdfDocument(new PdfWriter(path));
        for (int i = 0; i < 12; i++)
        {
            var page = pdf.AddNewPage(i % 2 == 0 ? PageSize.A4 : PageSize.A4.Rotate());
            if (i == 2) page.SetRotation(90);
            if (i == 3) page.SetCropBox(new Rectangle(20, 30, 500, 400));
            var canvas = new PdfCanvas(page);
            canvas.SetFillColorRgb(0.2f, 0.5f, 0.8f).Rectangle(30, 40, 150, 170).Fill();
            canvas.BeginText().SetFontAndSize(iText.Kernel.Font.PdfFontFactory.CreateFont(), 18)
                .MoveText(40, 260).ShowText($"PDFium regression page {i + 1}").EndText();
            canvas.SetLineWidth(0.3f);
            for (int j = 0; j < 1000; j++) canvas.MoveTo(10 + j % 500, 10).LineTo(j % 500, 550).Stroke();
        }
        return path;
    }

    static string Hash(BitmapSource bitmap)
    {
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    static async Task CompareViewportRasterAsync(string path, int page = 0)
    {
        // Warm, identical center crop containing vector paths in the fixture.
        double aspect = await PdfThumbnailService.GetPageAspectRatioAsync(path, page)
            ?? throw new Exception("Cannot read page geometry");
        const int fullWidth = 6400;
        int fullHeight = Math.Max(1, (int)Math.Round(fullWidth * aspect));
        int cropWidth = 1920, cropHeight = Math.Min(1280, fullHeight);
        int originX = (fullWidth - cropWidth) / 2, originY = (fullHeight - cropHeight) / 2;
        var reference = await PdfThumbnailService.RenderPageTileAsync(path, page, fullWidth, fullHeight,
            new Int32Rect(originX, originY, cropWidth, cropHeight));
        Check(reference != null, "Reference viewport is available");
        var measurements = new List<object>();
        foreach (int size in new[] { 640, 1280, 1920 })
        {
            var timings = new List<double>();
            var firstTimings = new List<double>();
            var stitchedPixels = new byte[cropWidth * cropHeight * 4];
            for (int pass = 0; pass < 4; pass++)
            {
                var outputs = new List<(Int32Rect Rect, BitmapSource Bitmap)>();
                var sw = Stopwatch.StartNew();
                double firstMs = 0;
                for (int y = 0; y < cropHeight; y += size)
                    for (int x = 0; x < cropWidth; x += size)
                    {
                        var local = new Int32Rect(x, y, Math.Min(size, cropWidth - x), Math.Min(size, cropHeight - y));
                        var bitmap = await PdfThumbnailService.RenderPageTileAsync(path, page, fullWidth, fullHeight,
                            new Int32Rect(x + originX, y + originY, local.Width, local.Height));
                        Check(bitmap != null, "Viewport subdivision render succeeds");
                        if (outputs.Count == 0) firstMs = sw.Elapsed.TotalMilliseconds;
                        outputs.Add((local, bitmap!));
                    }
                sw.Stop();
                if (pass > 0) { timings.Add(sw.Elapsed.TotalMilliseconds); firstTimings.Add(firstMs); }
                if (pass == 3)
                    foreach (var (rect, bitmap) in outputs)
                    {
                        var pixels = new byte[rect.Width * rect.Height * 4];
                        bitmap.CopyPixels(pixels, rect.Width * 4, 0);
                        for (int row = 0; row < rect.Height; row++)
                            Buffer.BlockCopy(pixels, row * rect.Width * 4, stitchedPixels,
                                ((rect.Y + row) * cropWidth + rect.X) * 4, rect.Width * 4);
                    }
            }
            var stitched = BitmapSource.Create(cropWidth, cropHeight, 96, 96, PixelFormats.Bgra32, null,
                stitchedPixels, cropWidth * 4);
            stitched.Freeze();
            var referencePixels = new byte[stitchedPixels.Length];
            reference!.CopyPixels(referencePixels, cropWidth * 4, 0);
            long totalError = 0; int maxError = 0, differentPixels = 0;
            for (int i = 0; i < stitchedPixels.Length; i += 4)
            {
                bool different = false;
                for (int c = 0; c < 4; c++)
                {
                    int error = Math.Abs(stitchedPixels[i + c] - referencePixels[i + c]);
                    totalError += error; maxError = Math.Max(maxError, error); different |= error != 0;
                }
                if (different) differentPixels++;
            }
            // Different clip origins can change PDFium antialiasing. Measure that
            // difference instead of assuming different subdivisions are pixel-identical.
            if (size == 1920) Check(Hash(stitched) == Hash(reference), "Identical viewport request is pixel-stable");
            timings.Sort(); firstTimings.Sort();
            measurements.Add(new { TileSize = size, MedianMilliseconds = timings[1], FirstRegionMilliseconds = firstTimings[1],
                DifferentPixels = differentPixels, MaxChannelDifference = maxError,
                MeanChannelDifference = totalError / (double)stitchedPixels.Length });
        }
        File.WriteAllText(System.IO.Path.Combine(Output, "viewport-raster.json"), JsonSerializer.Serialize(measurements));
        Console.WriteLine("Viewport raster: " + JsonSerializer.Serialize(measurements));
    }

    static void TestViewportMotion()
    {
        var tracker = new ViewportMotionTracker();
        var viewport = new Size(1000, 800);
        Check(!tracker.Update(new Point(0, 0), viewport, false), "First viewport does not cancel itself");
        Check(!tracker.Update(new Point(0, 100), viewport, false), "Small pan preserves in-progress tiles");
        Check(tracker.Update(new Point(0, 300), viewport, false), "Accumulated same-page pan invalidates obsolete work");
        Check(!tracker.Update(new Point(0, 310), viewport, false), "Movement anchor resets after invalidation");
        Check(tracker.Update(new Point(1000, 310), viewport, false), "Horizontal pan also invalidates");
        Check(!tracker.Update(new Point(5000, 5000), viewport, true), "Zoom establishes a new movement anchor");
    }

    static void TestTwoPageLayout()
    {
        var sizes = new[] { (100.0, 200.0), (100.0, 200.0), (80.0, 160.0) };
        var layout = new ContinuousPageLayout(sizes, 1.0, columns: 2);
        Check(layout.Columns == 2 && layout.Top(0) == layout.Top(1), "Two-page layout places a spread on one row");
        Check(layout.Top(2) > layout.Top(0) + layout.Height(0), "Two-page layout starts the next spread below the first");
        Check(layout.IndexAt(150, 40, 500) == 0 && layout.IndexAt(265, 40, 500) == 1,
            "Two-page layout hit testing distinguishes left and right pages");

        var viewport = new ContinuousViewport();
        viewport.SetViewportSize(500, 260);
        viewport.SetColumns(2);
        viewport.SetPages(sizes, 1.0);
        Check(viewport.HitTest(150, 40, out int left, out _, out _) && left == 0,
            "Two-page viewport hits the left page");
        Check(viewport.HitTest(265, 40, out int right, out _, out _) && right == 1,
            "Two-page viewport hits the right page");
    }

    static void TestReaderZoomMath()
    {
        const double step = 1.08;
        Check(Math.Abs(ReaderZoomMath.WheelZoom(1.0, 120, step, 0.05, 4.0) - step) < 0.0001,
            "One wheel notch applies one zoom step");
        Check(ReaderZoomMath.WheelZoom(1.0, 30, step, 0.05, 4.0) < step,
            "High-resolution wheel deltas zoom fractionally");
        Check(Math.Abs(ReaderZoomMath.WheelZoom(1.0, 30, step, 0.05, 4.0) - Math.Pow(step, 0.25)) < 0.0001,
            "Fractional wheel delta preserves Chromium-style smoothness");
        Check(Math.Abs(ReaderZoomMath.WheelZoom(1.0, -120, step, 0.05, 4.0) - (1.0 / step)) < 0.0001,
            "Negative wheel delta zooms out by one step");
        Check(Math.Abs(ReaderZoomMath.WheelZoom(4.0, 120, step, 0.05, 4.0) - 4.0) < 0.0001,
            "Wheel zoom respects maximum clamp");

        // Acceleration (calibrated on Foxit: 40 rapid notches, ~31 ms apart, were x2.1 after 7, x5.1 after 9, x20 after 13).
        var slow = new ReaderZoomMath.WheelZoomAccelerator();
        Check(Enumerable.Range(0, 6).All(i => slow.Next(1, 1, i * 800.0) == 1.0),
            "Isolated wheel notches (800 ms apart) are never accelerated");
        var fast = new ReaderZoomMath.WheelZoomAccelerator();
        var multipliers = Enumerable.Range(0, 12).Select(i => fast.Next(1, 1, i * 31.0)).ToArray();
        Check(multipliers[0] == 1.0 && multipliers[1] == 1.0 && multipliers[2] > 1.0 && multipliers[2] < 2.0,
            "The first two rapid notches keep the normal step, then the step grows");
        Check(Math.Abs(multipliers[9] - 7.0) < 1e-9 && multipliers.Skip(9).All(m => m <= ReaderZoomMath.WheelZoomAccelerator.MaxMultiplier + 1e-9),
            "Acceleration reaches and holds the maximum multiplier by the tenth notch");
        double equivalent9 = multipliers.Take(9).Sum();
        Check(equivalent9 > 28 && equivalent9 < 36, $"Nine rapid notches add up to about 1.5x the 21 notch-equivalents measured in Foxit (got {equivalent9:0.0})");
        var turn = new ReaderZoomMath.WheelZoomAccelerator();
        for (int i = 0; i < 8; i++) turn.Next(1, 1, i * 31.0);
        Check(turn.Next(1, -1, 8 * 31.0) == 1.0, "Reversing direction restarts the run");
        Check(turn.Next(1, -1, 8 * 31.0 + ReaderZoomMath.WheelZoomAccelerator.RunGapMilliseconds + 1) == 1.0, "A pause longer than the run gap restarts the run");
        var smooth = new ReaderZoomMath.WheelZoomAccelerator();
        double total = 0; for (int i = 0; i < 40; i++) total += smooth.Next(0.25, 1, i * 8.0);
        Check(total > 10, "Fractional (touchpad) deltas accumulate into the same run instead of resetting");
    }

    static void TestRetainedRefinement()
    {
        var root = new Canvas { Width = 20, Height = 10 };
        var old = new Canvas { Width = 20, Height = 10 };
        var next = new Canvas { Width = 20, Height = 10, Visibility = Visibility.Collapsed };
        old.Children.Add(new System.Windows.Shapes.Rectangle { Width = 20, Height = 10, Fill = Brushes.Red });
        root.Children.Add(old); root.Children.Add(next);
        RetainedTilePresentation.Begin(old, next);
        next.Children.Add(new System.Windows.Shapes.Rectangle { Width = 10, Height = 10, Fill = Brushes.Blue });
        root.Measure(new Size(20, 10)); root.Arrange(new Rect(0, 0, 20, 10)); root.UpdateLayout();
        var output = new RenderTargetBitmap(20, 10, 96, 96, PixelFormats.Pbgra32);
        output.Render(root);
        var bytes = new byte[20 * 10 * 4]; output.CopyPixels(bytes, 80, 0);
        Check(bytes[(5 * 20 + 5) * 4] == 255, "First refined tile is visible before its neighbors finish");
        Check(bytes[(5 * 20 + 15) * 4 + 2] == 255, "Unfinished region retains previous imagery");
        RetainedTilePresentation.Complete(old);
        Check(old.Children.Count == 0 && old.Visibility == Visibility.Collapsed, "Completed refinement releases old visual references");
        RetainedTilePresentation.Begin(next, old);
        Check(next.Children.Count == 1 && Panel.GetZIndex(old) > Panel.GetZIndex(next), "Repeated zoom keeps the newest completed pixels underneath");
    }

    static async Task RunNativeAsync(bool baseline)
    {
        string path = CreateFixture();
#if HAS_BASELINE
        Func<int, double, Task<BitmapSource?>> render = baseline
            ? (page, width) => Baseline.PdfThumbnailService.RenderPageAsync(path, page, width)
            : (page, width) => PdfThumbnailService.RenderPageAsync(path, page, width);
#else
        Func<int, double, Task<BitmapSource?>> render =
            (page, width) => PdfThumbnailService.RenderPageAsync(path, page, width);
#endif
        var first = await render(0, 512); Check(first != null && first.IsFrozen, "Native render returns frozen image");
        var hashes = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            var bitmap = (await render(i, 1024))!;
            hashes.Add(Hash(bitmap));
#if HAS_BASELINE
            double? aspect = baseline ? await Baseline.PdfThumbnailService.GetPageAspectRatioAsync(path, i)
                : await PdfThumbnailService.GetPageAspectRatioAsync(path, i);
#else
            double? aspect = await PdfThumbnailService.GetPageAspectRatioAsync(path, i);
#endif
            Check(aspect.HasValue && Math.Abs(aspect.Value - bitmap.PixelHeight / (double)bitmap.PixelWidth) < 0.002,
                "Page geometry matches rendered image (portrait/landscape/rotation/crop)");
        }
        var rects = new[] { new Int32Rect(0, 0, 640, 640), new Int32Rect(640, 0, 384, 640) };
#if HAS_BASELINE
        var tiles = baseline ? await Baseline.PdfThumbnailService.RenderPageTilesBatchAsync(path, 0, 1024, 1449, rects)
            : await PdfThumbnailService.RenderPageTilesBatchAsync(path, 0, 1024, 1449, rects);
#else
        var tiles = await PdfThumbnailService.RenderPageTilesBatchAsync(path, 0, 1024, 1449, rects);
#endif
        foreach (var tile in tiles) { Check(tile != null, "Batch renders tile"); hashes.Add(Hash(tile!)); }
        string reference = System.IO.Path.Combine(Output, "baseline-hashes.json");
        if (baseline) File.WriteAllText(reference, JsonSerializer.Serialize(hashes));
        else if (File.Exists(reference))
            Check(hashes.SequenceEqual(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(reference))!),
                "Pixel-identical page and tile rendering versus original service");
        // Warm up both pipelines before measuring identical 2200px renders; no test pixel copy
        // or hash allocation is inside the measurement interval.
        await render(0, 2200); Collect();
        long allocated = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 12; i++) Check(await render(i, 2200) != null, "Measured render succeeds");
        sw.Stop(); allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        var measurement = new { Mode = baseline ? "baseline" : "optimized", Renders = 12,
            Milliseconds = sw.Elapsed.TotalMilliseconds, ManagedAllocatedBytes = allocated };
        File.WriteAllText(System.IO.Path.Combine(Output, measurement.Mode + ".json"), JsonSerializer.Serialize(measurement, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(measurement));
        if (!baseline)
        {
            var gate = PdfiumInstance.Primary.Gate;
            await gate.WaitAsync(PdfRenderPriority.Visible);
            using var cts = new CancellationTokenSource();
            var pending = PdfThumbnailService.RenderPageTilesBatchAsync(path, 0, 1024, 1449, rects, cts.Token);
            cts.Cancel();
            var cancelled = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Check(cancelled.All(b => b == null), "Canceled native batch leaves gate queue without rendering");
            gate.Release();
            Check(await render(0, 512) != null, "Renderer still works after cancellation");
            using var midBatch = new CancellationTokenSource();
            var longBatch = PdfThumbnailService.RenderPageTilesBatchAsync(path, 0, 1024, 1449,
                Enumerable.Repeat(rects[0], 200).ToArray(), midBatch.Token);
            var deadline = Stopwatch.StartNew();
            while (PdfThumbnailService.ActiveNativeCalls == 0 && !longBatch.IsCompleted && deadline.ElapsedMilliseconds < 2000)
                await Task.Delay(1);
            midBatch.Cancel();
            var partial = await longBatch.WaitAsync(TimeSpan.FromSeconds(5));
            Check(partial.Count(b => b != null) < 200, "Active batch stops before rendering all remaining tiles");
            await gate.WaitAsync(PdfRenderPriority.Visible);
            var closing = PdfThumbnailService.RenderPageAsync(path, 0, 512);
            PdfThumbnailService.ReleaseUnusedDocuments(Array.Empty<string>());
            Check(PdfThumbnailService.CachedDocumentCount == 0, "Document removed from cache");
            Check(await closing.WaitAsync(TimeSpan.FromSeconds(2)) == null, "Closing document cancels its queued render");
            gate.Release();
            Check(await render(0, 512) != null, "Reopen after document retirement");
            long loads = PdfThumbnailService.NativePageLoads;
            for (int i = 0; i < 3; i++) await render(0, 512);
            Check(PdfThumbnailService.NativePageLoads == loads, "Repeated zoom/render reuses a loaded native page");
            for (int i = 0; i < 12; i++) await render(i, 256);
            Check(PdfThumbnailService.CachedNativePageCount <= PdfThumbnailService.NativePageCacheCapacity,
                "Native page LRU is bounded after requests complete");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => render(0, 1024)));
            Check(concurrent.All(b => b != null && Hash(b) == hashes[0]),
                "Concurrent requests for one page do not corrupt its progressive state");
            using var insideTile = new CancellationTokenSource();
            long yielded = PdfThumbnailService.ProgressiveYields;
            long cancelledCount = PdfThumbnailService.CancelledRenders;
            var large = PdfThumbnailService.RenderPageAsync(path, 0, 4000, insideTile.Token, PdfRenderPriority.Background);
            var wait = Stopwatch.StartNew();
            while (PdfThumbnailService.ProgressiveYields == yielded && !large.IsCompleted && wait.ElapsedMilliseconds < 5000)
                await Task.Delay(1);
            Check(PdfThumbnailService.ProgressiveYields > yielded, "A complex render yields within a single bitmap");
            var urgent = PdfThumbnailService.RenderPageAsync(path, 1, 64);
            Check(await Task.WhenAny(urgent, large) == urgent && await urgent != null,
                "Visible page can render between slices of background page");
            insideTile.Cancel();
            Check(await large.WaitAsync(TimeSpan.FromSeconds(5)) == null && PdfThumbnailService.CancelledRenders > cancelledCount,
                "Cancellation stops an already started progressive bitmap");
            Console.WriteLine($"Native pages={PdfThumbnailService.CachedNativePageCount}, page loads/hits={PdfThumbnailService.NativePageLoads}/{PdfThumbnailService.NativePageCacheHits}, progressive yields={PdfThumbnailService.ProgressiveYields}, max slice={PdfThumbnailService.MaxNativeRenderSliceMilliseconds:F1} ms");
            await CompareViewportRasterAsync(path);
            PdfThumbnailService.ReleaseCachedPages();
            wait.Restart();
            while (PdfThumbnailService.CachedNativePageCount != 0 && wait.ElapsedMilliseconds < 2000) await Task.Delay(5);
            Check(PdfThumbnailService.CachedNativePageCount == 0, "Hiding reader releases idle native pages");
        }
    }
}
