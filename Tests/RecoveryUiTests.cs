using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XTStyle.Controls;
using XTPdfMergeApp;
using XTPdfMergeApp.Domain;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Workspace;

internal static partial class Program
{
    static void TestRecoveryAndToolbarUi()
    {
        var reader = new ReaderWindow { Width = 760, Height = 640 };
        var app = Application.Current;
        var priorOwner = app.MainWindow;
        app.MainWindow = reader;
        Exception? failure = null;
        var frame = new DispatcherFrame();
        reader.Show();
        reader.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try
            {
                ((FrameworkElement)reader.FindName("StartPage")).Visibility = Visibility.Collapsed;
                var bar = (FrameworkElement)reader.FindName("ReaderToolbarBar");
                bar.Visibility = Visibility.Visible;
                reader.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var toolScroll = (ScrollViewer)reader.FindName("ReaderToolsScroll");
                var more = (XTButton)reader.FindName("ReaderToolbarMore");
                Check(more.Visibility == Visibility.Visible && more.ActualWidth > 0, "The real reader shows an accessible overflow button at its minimum width");
                more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(toolScroll.Clip is RectangleGeometry clipping && clipping.Bounds.Right <= toolScroll.ViewportWidth,
                    "Narrow toolbar clips at complete control boundaries");
                var menu = more.ContextMenu;
                Check(menu != null && menu.Items.OfType<MenuItem>().Any(i => (string)i.Header == "Squiggly") && menu.Items.OfType<MenuItem>().Any(i => (string)i.Header == "Print"), "Toolbar overflow includes compact markup tools and document commands");
                menu!.IsOpen = false;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bar.ActualWidth * 2), (int)Math.Ceiling(bar.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                bitmap.Render(bar);
                using (var png = File.Create(Path.Combine(Output, "toolbar-narrow.png")))
                { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(png); }

                string folder = Path.Combine(Output, "recovery-ui"); Directory.CreateDirectory(folder);
                string a = Path.Combine(folder, "a.pdf"), b = Path.Combine(folder, "b.pdf");
                foreach (string path in new[] { a, b })
                    using (var doc = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfWriter(path))) { doc.AddNewPage(); doc.AddNewPage(); }
                var workspace = new PdfWorkspace();
                var original = new WorkspaceDocument { SourcePath = a };
                original.Pages.Add(workspace.CreatePlacement(a, 1)); original.Pages.Add(workspace.CreatePlacement(a, 2)); original.SetBaseline(); original.Pages.RemoveAt(0);
                var note = new QuickAnnotationChange(null, new QuickAnnotationSpec("recovered-ui-note", QuickAnnotationKind.Comment, 2, .2, .2, .3, .3, "Restored pending note"));
                AnnotationStore.Apply(a, new[] { note });
                var state = SessionRecoveryStore.Capture(new[] { original }, new(), 0, 0);
                AnnotationStore.Forget(a);
                var restored = await reader.Session.RestoreSourcesAsync(state);
                var groupA = reader.Session.Documents.Single();
                Check(restored.Skipped.Count == 0 && groupA.Pages.Count == 1 && groupA.Pages[0].PageNumber == 2 && groupA.IsDirty,
                    "The actual session restores unsaved page structure after loading source PDFs");
                Check(AnnotationStore.Pending(a).Single().Add!.Text == "Restored pending note", "The actual session restores annotation edits without writing the source PDF");
                byte[] beforeA = File.ReadAllBytes(a), beforeB = File.ReadAllBytes(b);
                var closer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.ApplicationIdle, (_, _) =>
                {
                    foreach (Window dialog in app.Windows.OfType<Window>().ToArray()) if (dialog is XTPdfMergeApp.Controls.AppDialogWindow && dialog.IsVisible) dialog.Close();
                }, reader.Dispatcher);
                bool edited;
                try
                {
                    edited = await reader.Session.EditSourceFilesAsync(new[] { (a, (IReadOnlyCollection<int>)new[] { 1 }), (b, (IReadOnlyCollection<int>)new[] { 99 }) },
                        (path, pages) => PdfPageEditService.RotatePages(path, pages, 90), true);
                }
                finally { closer.Stop(); }
                Check(!edited && beforeA.SequenceEqual(File.ReadAllBytes(a)) && beforeB.SequenceEqual(File.ReadAllBytes(b)), "Actual multi-source page editing fails atomically when a later source edit is invalid");
                // Use annotation-only state to exercise the actual Save handler and scoped history.
                groupA.Pages.Insert(0, reader.Session.Workspace.CreatePlacement(a, 1)); groupA.SetBaseline();
                var groupB = new WorkspaceDocument { SourcePath = b }; groupB.Pages.Add(reader.Session.Workspace.CreatePlacement(b, 1)); groupB.SetBaseline(); reader.Session.Documents.Add(groupB);
                var bNote = new QuickAnnotationChange(null, note.Add! with { Name = "independent-b-note", PageNumber = 1 });
                reader.Session.Workspace.History.Execute(new AnnotationEditCommand("B note", b, new[] { bNote }));
                bool saved = await ((IReaderPageEditHost)reader.Session).SaveGroupAsync(groupA, false);
                Check(saved && !AnnotationStore.HasPending(a) && reader.Session.Workspace.History.CanUndo, "Saving A through the actual handler retains B history");
                await reader.Session.Workspace.History.UndoAsync();
                Check(!AnnotationStore.HasPending(b), "B annotations can still be undone after saving A");
                reader.Session.Workspace.History.Clear();
                AnnotationStore.Forget(a); AnnotationStore.Forget(b);
                foreach (var group in reader.Session.Documents) { group.SetAnnotationsDirty(false); group.SetBaseline(); }
                reader.Session.Documents.Clear();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                AnnotationStore.Forget(Path.Combine(Output, "recovery-ui", "a.pdf"));
                AnnotationStore.Forget(Path.Combine(Output, "recovery-ui", "b.pdf"));
                RecentFilesStore.Remove(Path.Combine(Output, "recovery-ui", "a.pdf"));
                RecentFilesStore.Remove(Path.Combine(Output, "recovery-ui", "b.pdf"));
                reader.Session.Documents.Clear();
                reader.Close(); app.MainWindow = priorOwner; frame.Continue = false;
            }
        }));
        Dispatcher.PushFrame(frame);
        if (failure != null) throw failure;
    }
}
