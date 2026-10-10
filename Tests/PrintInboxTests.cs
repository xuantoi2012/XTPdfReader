using System.IO;
using System.Windows;
using System.Windows.Controls;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using XTPdfMergeApp;
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

internal static partial class Program
{
    /// <summary>Ribbon labels on one line, "More tools" lists only what does not fit, wide search box opens the palette under it, About icon exists.</summary>
    static void TestUiRibbonTitleAbout()
    {
        if (Application.Current == null) CreateReaderTestApplication();
        var reader = new ReaderWindow { Width = 1400, Height = 700, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
        var priorOwner = Application.Current.MainWindow;
        Application.Current.MainWindow = reader;
        try
        {
            reader.Show();
            ((FrameworkElement)reader.FindName("StartPage")).Visibility = Visibility.Collapsed;
            var bar = (FrameworkElement)reader.FindName("ReaderToolbarBar");
            bar.Visibility = Visibility.Visible; // the toolbar is hidden until a file is open
            reader.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(500));
            var more = (XTStyle.Controls.XTButton)reader.FindName("ReaderToolbarMore");
            var buttons = VisualTreeHelpers.FindVisualChildren<XTStyle.Controls.XTButton>(bar).Where(b => b != more && b.ActualWidth > 0).ToList();
            var labels = buttons.SelectMany(b => VisualTreeHelpers.FindVisualChildren<TextBlock>(b)).Where(t => !string.IsNullOrEmpty(t.Text)).ToList();
            Check(labels.Count > 15 && labels.All(t => t.ActualHeight < 18), "Every ribbon label is one full line (no wrapping or clipping)");
            var highlight = (RibbonSplit)reader.FindName("HighlightSplit");
            var highlightButton = VisualTreeHelpers.FindVisualChildren<XTStyle.Controls.XTButton>(highlight).First();
            Check(labels.Any(t => t.Text == highlightButton.Text) && labels.Any(t => t.Text == Loc.T("Rotate left")),
                "The selected highlight command and Rotate left labels are shown in full");
            Console.WriteLine($"bar {bar.ActualWidth}x{bar.ActualHeight} window {reader.ActualWidth} labels {labels.Count} first {labels.First().Text}");
            SavePng(bar, "ui-ribbon-wide");
            reader.BeginOpenWait();
            Check(ReferenceEquals(reader.Cursor, System.Windows.Input.Cursors.Wait) && reader.ForceCursor, "BeginOpenWait shows the wait cursor over the whole reader window");
            reader.EndOpenWait();
            Check(!ReferenceEquals(reader.Cursor, System.Windows.Input.Cursors.Wait), "EndOpenWait restores the normal cursor");
            Console.WriteLine($"ribbon at 1400: More tools {more.Visibility}");

            reader.Width = 900; reader.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(500));
            Check(more.Visibility == Visibility.Visible && more.Text == "More tools", "In a narrow window the 'More tools' button (icon + text) appears");
            more.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, more));
            Pump(TimeSpan.FromMilliseconds(150));
            int listed = reader.LastMoreToolsCount;
            Check(listed > 0 && listed < buttons.Count, $"More tools lists only the hidden buttons ({listed} of {buttons.Count})");
            SavePng(bar, "ui-ribbon-narrow");

            var search = (XTStyle.Controls.XTButton)reader.FindName("TitleSearchButton");
            var palette = (CommandPalette)reader.FindName("Palette");
            Check(search.ActualWidth >= 200 && !string.IsNullOrEmpty(search.Text), "The title-bar search box is wide and labelled");
            search.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, search));
            Pump(TimeSpan.FromMilliseconds(250));
            Check(palette.IsOpen, "Clicking the search box opens the command palette as a popup");
            Pump(TimeSpan.FromMilliseconds(250));
            SavePng(palette.Panel, "ui-palette");
            palette.Close();

            var settings = new SettingsWindow(() => Task.CompletedTask) { Owner = reader };
            settings.Show(); settings.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(300));
            Check(settings.ResizeMode == ResizeMode.NoResize && Math.Abs(settings.Width - 720) < 1, "Settings is a fixed-size 720 px window");
            SavePng((FrameworkElement)settings.Content, "ui-settings");
            settings.Close();
            Check(AboutWindow.VersionText().StartsWith("Version "), "About window reports a version: " + AboutWindow.VersionText());
            var about = new AboutWindow { Owner = reader };
            about.Show(); about.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(200));
            SavePng((FrameworkElement)about.Content, "ui-about");
            about.Close();
        }
        finally { reader.Close(); Application.Current.MainWindow = priorOwner; }
    }

    static void TestPrintedFilesCleanup()
    {
        string folder = System.IO.Path.Combine(Output, "printed-files");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(System.IO.Path.Combine(folder, "inst"));
        string saved = PrintedFilesService.Folder;
        PrintedFilesService.Folder = folder;
        try
        {
            string a = MakePlainPdf(System.IO.Path.Combine(folder, "inst", "a.pdf"), 1);
            string b = MakePlainPdf(System.IO.Path.Combine(folder, "inst", "b.pdf"), 1);
            File.WriteAllText(a + ".sent", "1");
            var unused = PrintedFilesService.FindUnused(new[] { a });
            Check(unused.Count == 1 && unused[0].Path == b, "Clean-up offers only printed PDFs that are not in use");
            Check(PrintedFilesService.FindUnused(new[] { a, b }).Count == 0, "Nothing is offered when every printed PDF is in use");
            int removed = PrintedFilesService.Delete(unused);
            Check(removed == 1 && !File.Exists(b) && File.Exists(a) && File.Exists(a + ".sent"), "Clean-up removes the unused PDF and keeps used ones and the delivery receipts");

            string oldFile = MakePlainPdf(System.IO.Path.Combine(folder, "inst", "old.pdf"), 1);
            string newFile = MakePlainPdf(System.IO.Path.Combine(folder, "inst", "new.pdf"), 1);
            File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-45));
            File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow.AddDays(-2));
            var aged = PrintedFilesService.FindUnused(new[] { a }, olderThanDays: 30);
            Check(aged.Count == 1 && aged[0].Path == oldFile, "With an age limit only unused PDFs older than that many days are offered");
            Check(PrintedFilesService.FindUnused(new[] { a }, olderThanDays: 0).Count == 2, "An age of 0 offers every unused PDF");
            Check(PrintedFilesService.FindUnused(new[] { a, oldFile }, olderThanDays: 30).Count == 0, "An old PDF that is still in use is never offered");
            Check(AppSettings.PrintedCleanupDays is >= 0 and <= 3650, "The retention setting stays inside 0..3650 days");
        }
        finally { PrintedFilesService.Folder = saved; }
    }
}

internal static partial class Program
{
    /// <summary>Shape tools: preview width equals the width written to the PDF, each tool remembers its own style, property bars float beside the annotation.</summary>
    static void TestShapePropertiesFloating()
    {
        if (Application.Current == null) CreateReaderTestApplication();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        var reader = new ReaderWindow { Width = 1300, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
        var priorOwner = Application.Current.MainWindow;
        Application.Current.MainWindow = reader;
        try
        {
            reader.Show();
            ((FrameworkElement)reader.FindName("StartPage")).Visibility = Visibility.Collapsed;
            reader.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(300));

            // preview = final: lw_px = Width x PageScale x pixelsPerPoint
            var style = new ShapeStyle(ShapeStyle.Rect, "#C0392B", 3, 0);
            typeof(ReaderWindow).GetMethod("ShowShapePreview", flags)!.Invoke(reader, new object[] { style, new System.Windows.Point(10, 10), new System.Windows.Point(200, 120), 0.5, 2.83 });
            double thickness = ((System.Windows.Shapes.Rectangle)reader.FindName("ReaderShapeRubber")).StrokeThickness;
            Check(Math.Abs(thickness - 3 * 2.83 * 0.5) < 0.001, $"Shape preview thickness {thickness:0.###} matches the written width x page scale");
            Check(Math.Abs(ShapeStyle.PageScale(2384) * 3 - 3 * 2384 / 842.0) < 0.001, "A1 sheets (2384 pt wide) write 2.83x thicker shapes, so the preview must scale too");

            // per-tool memory
            string saved = AppSettings.GetShapeStyleFor("Cloud"), savedLegacy = AppSettings.ShapeStyleSetting;
            try
            {
                AppSettings.SetShapeStyleFor("Cloud", new ShapeStyle("Cloud", "#2563EB", 6, 0).Encode());
                AppSettings.SetShapeStyleFor("Oval", new ShapeStyle("Oval", "#16A34A", 1, 0).Encode());
                var cloud = (ShapeStyle)typeof(ReaderWindow).GetMethod("ToolShapeStyle", flags)!.Invoke(null, new object[] { ShapeStyle.Cloud })!;
                var oval = (ShapeStyle)typeof(ReaderWindow).GetMethod("ToolShapeStyle", flags)!.Invoke(null, new object[] { ShapeStyle.Oval })!;
                Check(cloud.Color == "#2563EB" && cloud.Width == 6 && oval.Color == "#16A34A" && oval.Width == 1, "Each shape tool keeps its own colour and width");
            }
            finally { AppSettings.SetShapeStyleFor("Cloud", saved); AppSettings.ShapeStyleSetting = savedLegacy; AppSettings.SetShapeStyleFor("Oval", ""); }

            // floating bar sits above the selected annotation, follows it, and goes back home without a selection
            var shapeBar = (Border)reader.FindName("ShapeBar");
            shapeBar.Visibility = Visibility.Visible;
            reader.AnchorOverrideForTests = new Rect(300, 320, 200, 100);
            typeof(ReaderWindow).GetMethod("PositionFloatingBars", flags)!.Invoke(reader, null);
            Check(Math.Abs(shapeBar.Margin.Left - 300) < 1 && shapeBar.Margin.Top < 320 && shapeBar.Margin.Top > 320 - 80, $"The shape bar floats just above the annotation ({shapeBar.Margin.Left:0},{shapeBar.Margin.Top:0})");
            reader.AnchorOverrideForTests = new Rect(300, 10, 200, 100);
            typeof(ReaderWindow).GetMethod("PositionFloatingBars", flags)!.Invoke(reader, null);
            Check(shapeBar.Margin.Top >= 110, "Near the top edge the bar flips below the annotation");
            reader.AnchorOverrideForTests = null;
            typeof(ReaderWindow).GetMethod("PositionFloatingBars", flags)!.Invoke(reader, null);
            Check(shapeBar.Margin.Left == 14 && shapeBar.Margin.Top == 14, "With nothing selected the bar sits at the top-left as the tool default");
            shapeBar.Visibility = Visibility.Collapsed;

            // arming a tool shows no bar (it appears once there is a box to edit): see TestBarsAndTypewriterFlow
            var toolType = typeof(ReaderWindow).GetNestedType("ReaderTool", flags)!;
            typeof(ReaderWindow).GetMethod("SetReaderTool", flags)!.Invoke(reader, new[] { Enum.Parse(toolType, "Callout") });
            reader.UpdateLayout();
            Check(((Border)reader.FindName("TextFormatBar")).Visibility == Visibility.Collapsed, "Arming the Callout tool does not pop the format bar");
            typeof(ReaderWindow).GetMethod("SetReaderTool", flags)!.Invoke(reader, new[] { Enum.Parse(toolType, "Hand") });
        }
        finally { reader.Close(); Application.Current.MainWindow = priorOwner; }
    }
}
