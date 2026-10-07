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

internal static partial class Program
{
    /// <summary>Merge screen layout: Columns / Rows with at most 3 slots visible, scrolling for the rest; ticked windows only; window order = merge order.</summary>
    static void TestMergeLayoutModes()
    {
        if (Application.Current == null) CreateReaderTestApplication();
        string folder = System.IO.Path.Combine(Output, "merge-layout");
        Directory.CreateDirectory(folder);
        var workspace = new PdfWorkspace();
        WorkspaceDocument Make(int index)
        {
            string path = MakePlainPdf(System.IO.Path.Combine(folder, $"w{index}.pdf"), 2);
            var doc = new WorkspaceDocument { SourcePath = path };
            for (int p = 1; p <= 2; p++) doc.Pages.Add(workspace.CreatePlacement(path, p));
            doc.SetBaseline();
            return doc;
        }
        string savedLayout = AppSettings.MergeLayoutMode;
        var view = new MergeView();
        var host = Offscreen(new Window { Content = view, Width = 1240, Height = 800, WindowStyle = WindowStyle.None });
        try
        {
            view.BeginSession(Enumerable.Range(1, 5).Select(Make).ToList());
            view.ChooseLayout("Columns"); host.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(250));
            var slots = view.ShownSlots;
            Check(view.WindowCount == 5 && slots.Count == 5, "Five open files get five windows");
            Check(Math.Abs(slots[0].Width - slots[3].Width) < 0.5 && slots[0].Top == slots[3].Top, "Columns: every window has the same fixed size, side by side");
            Check(slots[2].Right <= view.ViewSize.Width + 0.5 && slots[3].Left >= view.ViewSize.Width - 1, "Columns: exactly 3 fit the view, the 4th starts beyond it");
            Check(view.CanvasSize.Width > view.ViewSize.Width && view.CanvasSize.Height <= view.ViewSize.Height + 1, "Columns: the rest scrolls sideways");

            view.ChooseLayout("Rows"); host.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(250));
            slots = view.ShownSlots;
            Check(Math.Abs(slots[0].Width - slots[4].Width) < 0.5 && slots[1].Top > slots[0].Bottom - 0.5, "Rows: one window per row, stacked");
            Check(slots[2].Bottom <= view.ViewSize.Height + 0.5 && slots[3].Top >= view.ViewSize.Height - 1, "Rows: exactly 3 rows fit the view, the 4th starts below it");
            Check(view.CanvasSize.Height > view.ViewSize.Height && view.CanvasSize.Width <= view.ViewSize.Width + 1, "Rows: the rest scrolls down");

            view.SetAllWindowsShown(false); host.UpdateLayout();
            Check(view.ShownWindowCount == 0 && !view.TickedDocuments().Any(), "Deselect all: nothing is shown or merged");
            view.SetAllWindowsShown(true); host.UpdateLayout();
            Check(view.ShownWindowCount == 5 && view.TickedDocuments().Count() == 5, "Select all: every window is shown and merged");

            // two windows are stretched to fill the height in Rows mode
            var two = new MergeView();
            var host2 = Offscreen(new Window { Content = two, Width = 1240, Height = 800, WindowStyle = WindowStyle.None });
            try
            {
                two.BeginSession(Enumerable.Range(6, 2).Select(Make).ToList());
                two.ChooseLayout("Rows"); host2.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(250));
                var rows = two.ShownSlots;
                Check(rows.Count == 2 && rows[1].Bottom > two.ViewSize.Height - 25 && two.CanvasSize.Height <= two.ViewSize.Height + 1, "Rows with 2 windows: stretched evenly over the height, no scrolling");
                SavePng(two, "merge-layout-rows-2");
            }
            finally { host2.Close(); }
            SavePng(view, "merge-layout-rows-5");
        }
        finally { host.Close(); AppSettings.MergeLayoutMode = savedLayout; }

        // window order is merge order
        var session = new MergeDraftSession();
        var docs = Enumerable.Range(11, 3).Select(Make).ToList();
        session.Begin(docs);
        var first = session.WindowDocuments.First();
        session.MoveDocument(first, 2);
        Check(session.WindowDocuments.Last() == first && session.WindowDocuments.First() != first, "Moving a window to another slot changes the merge order");
        session.Undo();
        Check(session.WindowDocuments.First() == first, "Undo restores the window order");
    }
}
