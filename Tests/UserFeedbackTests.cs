using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using XTPdfMergeApp;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Domain;
using XTPdfMergeApp.Services;
using iText.Kernel.Pdf;

internal static partial class Program
{
    static void TestTabSharpRetention()
    {
        if (Application.Current == null) CreateReaderTestApplication();
        ReaderPerformanceProfile.Apply(ReaderPerformanceMode.MemorySaving);
        var view = new ContinuousPdfView { PrefetchPageCount = 0 };
        var host = new Window { Content = view, Width = 1000, Height = 600, ShowActivated = false,
            ShowInTaskbar = false, Left = -32000, Top = -32000 };
        var a = new PagePlacement { SourcePath = "pinned-a.pdf", PageNumber = 1, BaseWidth = 1000, AspectRatio = .6 };
        var b = new PagePlacement { SourcePath = "pinned-b.pdf", PageNumber = 1, BaseWidth = 1000, AspectRatio = .6 };
        int callsA = 0;
        view.PageRenderer = (row, width, _, _) => { if (ReferenceEquals(row, a)) callsA++; return Task.FromResult<BitmapSource?>(Bitmap(width, width * 3 / 5)); };
        try
        {
            host.Show(); host.UpdateLayout(); view.RetainTabPages(new[] { a, b });
            view.SetDocument(new[] { a }, 1); Pump(TimeSpan.FromMilliseconds(500));
            int rendered = callsA; var sharp = a.ReaderBitmap;
            Check(rendered > 0 && sharp != null, "First tab has a sharp image before switching");
            view.SetDocument(new[] { b }, 1); Pump(TimeSpan.FromMilliseconds(300));
            view.ApplyMemoryPressure(MemoryPressureState.Critical);
            Check(view.IsMemoryProtected(a.SourcePath, 1) && view.IsMemoryProtectedImage(sharp!), "Inactive tab's last sharp image survives critical trimming");
            Check(!view.NativeProtectedPages.Contains((a.SourcePath.ToUpperInvariant(), 1)), "An inactive tab pins its sharp image without pinning native document resources");
            view.SetDocument(new[] { a }, 1); Pump(TimeSpan.FromMilliseconds(300));
            Check(callsA == rendered, "Returning to an unchanged tab does not invoke the renderer again");
            view.SetDocument(new[] { b }, 1); view.InvalidatePages(row => ReferenceEquals(row, a), true);
            Check(a.ReaderBitmap == null && !view.IsMemoryProtectedImage(sharp!), "An edit invalidates the sharp image retained by an inactive tab");
            view.RetainTabPages(new[] { b });
            Check(!view.IsMemoryProtected(a.SourcePath, 1), "Closing a tab releases its memory protection");
        }
        finally { view.CancelAll(); host.Close(); }
    }

    static void TestFeedbackSession()
    {
        if (Application.Current == null) CreateReaderTestApplication();
        string path = Path.Combine(Output, "feedback-rotation.pdf");
        using (var pdf = new PdfDocument(new PdfWriter(path))) for (int i = 0; i < 8; i++) pdf.AddNewPage();
        string marker = Path.Combine(Output, ".feedback-rotation.pdf.xtopen.Tester@TestPC.9999999");
        File.WriteAllText(marker, "test presence");
        var reader = new ReaderWindow { Width = 1000, Height = 700, ShowActivated = false,
            ShowInTaskbar = false, Left = -32000, Top = -32000 };
        Exception? failure = null;
        var frame = new System.Windows.Threading.DispatcherFrame();
        reader.Show();
        reader.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await reader.Session.OpenFilesInReaderAsync(new[] { path });
                var group = reader.Session.Documents.Single();
                await (Task)typeof(ReaderWindow).GetMethod("PresenceTickAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(reader, null)!;
                if (AppSettings.ShowPresence)
                    Check(((TextBlock)reader.FindName("ReaderPresenceText")).Text.Contains("Tester@TestPC"), "Opening a shared file displays the other reader in the persistent status bar");
                await reader.ShowPageAsync(group, group.Pages[5]);
                ((IReaderPageEditHost)reader.Session).DeletePages(group, new[] { group.Pages[5] });
                Check(reader.CurrentPageIndex(group) == 5, "Deleting the current page keeps the nearest following page instead of jumping to page one");
                ((IReaderPageEditHost)reader.Session).DeletePages(group, new[] { group.Pages[0] });
                Check(reader.CurrentPageIndex(group) == 4, "Deleting an earlier page preserves the page currently being viewed");
                await reader.ShowPageAsync(group, group.Pages[^1]);
                ((IReaderPageEditHost)reader.Session).DeletePages(group, new[] { group.Pages[^1] });
                Check(reader.CurrentPageIndex(group) == group.Pages.Count - 1, "Deleting the last page selects the nearest preceding page");
                byte[] before = File.ReadAllBytes(path);
                using (var otherReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bool rotated = await reader.Session.EditSourceFilesAsync(new[] { (path, (IReadOnlyCollection<int>)new[] { 1 }) },
                        (p, pages) => PdfPageEditService.RotatePages(p, pages, 90), true, appendOnly: true);
                    Check(rotated, "Source rotation succeeds with another reader holding a handle that denies file replacement");
                }
                byte[] after = File.ReadAllBytes(path);
                Check(after.Length > before.Length && after.Take(before.Length).SequenceEqual(before), "Rotation appends an update while preserving every original byte");
                using var pdf = new PdfDocument(new PdfReader(path));
                Check(pdf.GetPage(1).GetRotation() == 90, "The appended rotation is readable from the saved PDF");
                var icon = (XTStyle.Controls.XTButton)reader.FindName("ReaderEraserToolButton");
                Check(Equals(icon.Icon, reader.FindResource("Ui.Icon.eraser")), "Eraser tool uses the eraser geometry");
                string second = Path.Combine(Output, "feedback-second.pdf");
                using (var other = new PdfDocument(new PdfWriter(second))) other.AddNewPage();
                await reader.Session.OpenFilesInReaderAsync(new[] { second });
                var tabs = (ListBox)reader.FindName("ReaderDocumentTabs");
                tabs.SelectedItem = group;
                await Task.Delay(300);
                await reader.ShowPageAsync(group, group.Pages[2]); await Task.Delay(300);
                var continuous = (ContinuousPdfView)reader.FindName("ReaderContinuousView");
                continuous.ScrollBy(0, 100); await Task.Delay(150);
                var offset = continuous.ViewOffset;
                tabs.SelectedItem = reader.Session.Documents.Single(g => g != group); await Task.Delay(300);
                tabs.SelectedItem = group; await Task.Delay(300);
                Check(reader.CurrentPageIndex(group) == 2, "Switching tabs restores the selected page after document rebinding");
                Check((continuous.ViewOffset - offset).Length < 1, "Switching tabs restores the precise scroll offset within the selected page");
                RecentFilesStore.Remove(second);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                foreach (var group in reader.Session.Documents) { group.SetBaseline(); group.SetAnnotationsDirty(false); }
                reader.Session.Documents.Clear(); reader.Close(); RecentFilesStore.Remove(path); frame.Continue = false;
                File.Delete(marker);
            }
        }));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        if (failure != null) throw failure;
    }

    static void TestTabRegionRetention()
    {
        if (Application.Current == null) CreateReaderTestApplication();
        var view = new ContinuousPdfView { PrefetchPageCount = 0 };
        var host = new Window { Content = view, Width = 1320, Height = 700, WindowStyle = WindowStyle.None,
            ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        var a = new PagePlacement { SourcePath = "tab-region-a.pdf", PageNumber = 1, BaseWidth = 1200, AspectRatio = .6 };
        var b = new PagePlacement { SourcePath = "tab-region-b.pdf", PageNumber = 1, BaseWidth = 1200, AspectRatio = .6 };
        int crops = 0;
        double zoom = ExperimentalMuPdfViewport.ThroughputMode ? 6 : 3;
        view.PageRenderer = (_, width, _, _) => Task.FromResult<BitmapSource?>(Bitmap(width, (int)(width * .6)));
        view.RegionRenderer = (row, _, _, rects, _, _) =>
        {
            if (ReferenceEquals(row, a)) crops++;
            return Task.FromResult(rects.Select(r => (BitmapSource?)RegionPattern(r)).ToList());
        };
        try
        {
            host.Show(); host.UpdateLayout(); view.RetainTabPages(new[] { a, b });
            view.SetDocument(new[] { a }, zoom); Pump(TimeSpan.FromMilliseconds(300));
            view.ScrollBy(320, 70); Pump(TimeSpan.FromMilliseconds(300));
            int rendered = crops; var offset = view.ViewOffset;
            Check(rendered > 0 && TryRenderedRegion(view, a, out _), "A deeply zoomed tab has a sharp viewport crop before switching");
            view.SetDocument(new[] { b }, 1); Pump(TimeSpan.FromMilliseconds(200));
            view.ApplyMemoryPressure(MemoryPressureState.Critical);
            ContinuousPdfView.ApplyRegionMemoryBudget(1);
            view.SetDocument(new[] { a }, zoom); view.RestoreViewOffset(offset); Pump(TimeSpan.FromMilliseconds(300));
            Check(crops == rendered && TryRenderedRegion(view, a, out _), "Returning to a deeply zoomed tab reuses its sharp crop after critical trimming");
        }
        finally { view.CancelAll(); host.Close(); ContinuousPdfView.ApplyRegionMemoryBudget(ContinuousPdfView.RegionCacheBudgetBytes); }
    }

    static async Task TestRealFeedbackPdfAsync(string source)
    {
        byte[] sourceHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source));
        string copy = Path.Combine(Output, "feedback-real-copy.pdf"); File.Copy(source, copy, true);
        var beforeImage = await PdfThumbnailService.RenderPageAsync(copy, 1, 1200, default, PdfRenderPriority.Visible);
        Check(beforeImage != null, "The real feedback PDF renders before rotation");
        byte[] original = File.ReadAllBytes(copy);
        using (await PdfThumbnailService.SuspendDocumentAsync(copy, TimeSpan.FromSeconds(5)))
        using (var externalReader = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            PdfPageEditService.RotatePages(copy, new[] { 2 }, 90);
        byte[] updated = File.ReadAllBytes(copy);
        Check(updated.Length > original.Length && updated.Take(original.Length).SequenceEqual(original), "Rotation of the real PDF preserves its original bytes with another reader present");
        using (var pdf = new PdfDocument(new PdfReader(copy)))
            Check(pdf.GetNumberOfPages() == 115 && pdf.GetPage(2).GetRotation() == 90, "The real PDF keeps all 115 pages and saves page two's rotation");
        var afterImage = await PdfThumbnailService.RenderPageAsync(copy, 1, 1200, default, PdfRenderPriority.Visible);
        Check(afterImage != null && beforeImage!.PixelWidth < beforeImage.PixelHeight && afterImage.PixelWidth > afterImage.PixelHeight,
            "The rotated real PDF reopens and renders with the updated page orientation");
        Check(sourceHash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source))), "The original network PDF remains unchanged throughout real-file testing");
        await ExperimentalMuPdfViewport.RetireAsync(copy);
    }
}
