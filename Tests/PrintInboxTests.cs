using System.IO;
using System.Windows;
using System.Windows.Controls;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Domain;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Workspace;

internal static partial class Program
{
    static string MakePlainPdf(string path, int pages)
    {
        using var doc = new PdfDocument(new PdfWriter(path));
        for (int i = 0; i < pages; i++) doc.AddNewPage(new PageSize(300, 200));
        return path;
    }

    /// <summary>The Print inbox: one persistent window of every printed PDF, never part of Merge all, committed to disk only on arrival / removal / merge.</summary>
    static void TestPrintInbox()
    {
        string folder = System.IO.Path.Combine(Output, "print-inbox");
        Directory.CreateDirectory(folder);
        string storeFile = System.IO.Path.Combine(folder, "print-inbox.json");
        File.Delete(storeFile);
        string savedStore = PrintInboxStore.FilePath;
        PrintInboxStore.FilePath = storeFile;
        try
        {
            string a = MakePlainPdf(System.IO.Path.Combine(folder, "plot-a.pdf"), 3);
            string b = MakePlainPdf(System.IO.Path.Combine(folder, "plot-b.pdf"), 2);

            var session = new MergeDraftSession();
            session.Begin(Array.Empty<WorkspaceDocument>());
            Check(session.Inbox == null && !session.DisplayedDocuments.Any(), "A fresh session with an empty store has no inbox window");
            var inbox = session.AddIncomingPdf(a, 3);
            session.AddIncomingPdf(b, 2);
            session.AddIncomingPdf(a, 3); // redelivered path
            Check(inbox.Pages.Count == 5 && session.IsInbox(inbox), "Incoming PDFs add their pages to one inbox window; a redelivered path is ignored");
            Check(!session.WindowDocuments.Contains(inbox) && !session.TemporaryDocuments.Contains(inbox) && session.DisplayedDocuments.Contains(inbox),
                "The inbox is shown but is not a merge window, so Merge all never includes it");
            Check(PrintInboxStore.Load().Count == 5, "Arrivals are committed to the store");

            var target = session.CreateWindowDocument();
            session.MovePages(inbox, target, inbox.Pages.Take(2).ToList(), 0, copy: false);
            Check(inbox.Pages.Count == 3 && target.Pages.Count == 2 && session.WindowDocuments.Single() == target, "Dragging inbox pages into a window moves them");
            Check(PrintInboxStore.Load().Count == 5, "Moving pages into a draft does not change the committed inbox");

            var next = new MergeDraftSession();
            next.Begin(Array.Empty<WorkspaceDocument>());
            Check(next.Inbox is { Pages.Count: 5 }, "A new session (draft discarded) gets the inbox back with all 5 pages");
            Check(next.Inbox!.Pages[0].SourcePath == a && next.Inbox.Pages[3].SourcePath == b, "Inbox order is the arrival order");

            PrintInboxStore.Remove(new[] { new PrintInboxStore.Entry(a, 1), new PrintInboxStore.Entry(a, 2), new PrintInboxStore.Entry(a, 3) });
            File.Delete(b);
            Check(PrintInboxStore.Load().Count == 0, "Removed pages leave the store and pages of deleted files are skipped");
        }
        finally { PrintInboxStore.FilePath = savedStore; }
    }

    // The view awaits with the UI synchronization context (as in the app): start it from inside the dispatcher so the continuation returns there.
    static void ImportOnDispatcher(MergeView view, string path)
    {
        Task task = null!;
        Application.Current.Dispatcher.Invoke(() => task = view.ImportIncomingPdfAsync(path));
        CompleteWithDispatcher(task);
    }

    static void TestPrintInboxView()
    {
        if (Application.Current == null) CreateReaderTestApplication();
        string folder = System.IO.Path.Combine(Output, "print-inbox");
        Directory.CreateDirectory(folder);
        string savedStore = PrintInboxStore.FilePath;
        PrintInboxStore.FilePath = System.IO.Path.Combine(folder, "print-inbox-view.json");
        File.Delete(PrintInboxStore.FilePath);
        var view = new MergeView();
        var host = Offscreen(new Window { Content = view, Width = 1100, Height = 720, WindowStyle = WindowStyle.None });
        try
        {
            view.BeginSession(Array.Empty<WorkspaceDocument>());
            string a = MakePlainPdf(System.IO.Path.Combine(folder, "view-a.pdf"), 4);
            string b = MakePlainPdf(System.IO.Path.Combine(folder, "view-b.pdf"), 2);
            ImportOnDispatcher(view, a);
            ImportOnDispatcher(view, b);
            host.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(400));
            var button = (XTStyle.Controls.XTButton)view.FindName("InboxButton");
            Check(button.Text == "Inbox (6)", "The toolbar shows the inbox page count: " + button.Text);
            var windows = VisualTreeHelpers.FindVisualChildren<MergeMiniWindow>(view).ToList();
            Check(windows.Count == 1 && windows[0].IsInbox && windows[0].ShowFileNames, "The Merge canvas shows the inbox as a window with file-name captions");
            SavePng(view, "print-inbox-view");
            Check(!view.HasUnsavedDraft, "Receiving prints does not make the merge draft 'unsaved'");
            Check(view.CaptureDraft().Count == 0, "The inbox is not written into the session-recovery draft (it has its own store)");
        }
        finally { host.Close(); PrintInboxStore.FilePath = savedStore; }
    }
}
