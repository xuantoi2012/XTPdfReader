using System.IO;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    private sealed class ReaderFlow(XTPdfMergeApp.ReaderWindow window, XTPdfMergeApp.Domain.PagePlacement row, string path)
    {
        private const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        public XTPdfMergeApp.ReaderWindow Window { get; } = window;
        public XTPdfMergeApp.Domain.PagePlacement Row { get; } = row;
        public string Path { get; } = path;
        public T Find<T>(string name) where T : class => (T)Window.FindName(name);
        public object? Call(string method, params object?[] args) => typeof(XTPdfMergeApp.ReaderWindow).GetMethod(method, Flags)!.Invoke(Window, args);
        public object? Field(string name) => typeof(XTPdfMergeApp.ReaderWindow).GetField(name, Flags)!.GetValue(Window);
        public async Task<PageAnnotations> PageAsync() => (await AnnotationStore.GetPageAsync(Path, 1))!;
        public async Task AddAsync(QuickAnnotationSpec spec, string what = "add")
        {
            await ((XTPdfMergeApp.IReaderPageEditHost)Window.Session).ApplyAnnotationChangesAsync(Path, new[] { new QuickAnnotationChange(null, spec) }, what);
            await Task.Delay(600);
        }
        public void Select(QuickAnnotationSpec spec) { Call("SelectAnnotation", Row, spec); Window.UpdateLayout(); }
        public object Hit(double u, double v) => Activator.CreateInstance(typeof(XTPdfMergeApp.ReaderWindow).GetNestedType("PageHit", Flags)!, Row, u, v)!;
    }

    /// <summary>Runs <paramref name="body"/> in a real (offscreen) reader window with a blank A4 page open, like the other flow tests.</summary>
    static void RunReaderFlow(string folderName, Func<ReaderFlow, Task> body)
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        string folder = System.IO.Path.Combine(Output, folderName);
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "page.pdf");
        using (var doc = new PdfDocument(new PdfWriter(path))) doc.AddNewPage(new PageSize(595, 842));
        var reader = new XTPdfMergeApp.ReaderWindow { Width = 1300, Height = 900, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
        var app = System.Windows.Application.Current;
        var priorOwner = app.MainWindow;
        app.MainWindow = reader;
        Exception? failure = null;
        var frame = new System.Windows.Threading.DispatcherFrame();
        reader.Show();
        reader.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try
            {
                ((System.Windows.FrameworkElement)reader.FindName("StartPage")).Visibility = System.Windows.Visibility.Collapsed;
                await reader.Session.OpenFilesInReaderAsync(new[] { path });
                await Task.Delay(1500);
                reader.UpdateLayout();
                var row = (XTPdfMergeApp.Domain.PagePlacement)typeof(XTPdfMergeApp.ReaderWindow).GetField("_readerPage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(reader)!;
                await body(new ReaderFlow(reader, row, path));
            }
            catch (Exception ex) { failure = ex; }
            finally { reader.Session.Documents.Clear(); reader.Close(); app.MainWindow = priorOwner; frame.Continue = false; }
        }));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        if (failure != null) throw new Exception(folderName + ": " + failure.Message, failure);
    }

    static QuickAnnotationSpec RectSpec(string name, double u1, double v1, double u2, double v2, ShapeStyle? style = null)
        => new(name, QuickAnnotationKind.Shape, 1, u1, v1, u2, v2, "") { Format = (style ?? new ShapeStyle(ShapeStyle.Rect, "#C0392B", 2, 0)).Encode() };

    /// <summary>Duplicate and Bring to front on the property bar.</summary>
    static void TestPropertyActions() => RunReaderFlow("property-actions", async f =>
    {
        await f.AddAsync(RectSpec("xt-a", 0.2, 0.2, 0.4, 0.3));
        await f.AddAsync(RectSpec("xt-b", 0.5, 0.2, 0.7, 0.3));
        var page = await f.PageAsync();
        var a = page.Annotations.Single(x => x.Name == "xt-a");
        Check(page.Annotations.Select(x => x.Name).SequenceEqual(new[] { "xt-a", "xt-b" }), "Two rectangles, a below b");

        f.Select(a);
        Check(f.Find<System.Windows.Controls.Border>("ShapeBar").Visibility == System.Windows.Visibility.Visible, "The shape bar is shown for the selected rectangle");

        f.Call("DuplicateSelection");
        await Task.Delay(1200);
        page = await f.PageAsync();
        var shapes = page.Annotations.Where(x => x.Kind == QuickAnnotationKind.Shape).ToList();
        var copy = shapes.Single(x => x.Name != "xt-a" && x.Name != "xt-b");
        Check(shapes.Count == 3 && Math.Abs(copy.U1 - (a.U1 + 12.0 / 595)) < 0.002 && Math.Abs(copy.V1 - (a.V1 + 12.0 / 842)) < 0.002, "Duplicate adds one copy 12 pt down and right of the rectangle");
        Check(f.Field("_selAnn") is QuickAnnotationSpec selected && selected.Name == copy.Name, "The copy is the selection");
        Check(copy.Format == a.Format, "The copy has the same style");
        f.Window.UpdateLayout();
        SavePng(f.Find<System.Windows.FrameworkElement>("ReaderContentHost"), "property-actions-duplicate");

        f.Select(shapes.Single(x => x.Name == "xt-a"));
        f.Call("BringSelectionToFront");
        await Task.Delay(1200);
        page = await f.PageAsync();
        Check(page.Annotations.Last(x => x.Kind == QuickAnnotationKind.Shape).Name == "xt-a", "Bring to front moves the rectangle to the end of the page annotations (top of the z-order)");
        Check(page.Annotations.Count(x => x.Kind == QuickAnnotationKind.Shape) == 3, "Bring to front does not add or lose annotations");
    });

    /// <summary>A shape with a text box is a group: duplicating copies both as a new group; bring to front keeps them together.</summary>
    static void TestPropertyActionsOnGroup() => RunReaderFlow("property-actions-group", async f =>
    {
        await f.AddAsync(RectSpec("xt-s", 0.3, 0.3, 0.5, 0.4));
        var shape = (await f.PageAsync()).Annotations.Single();
        f.Select(shape);
        await (Task)f.Call("BeginShapeTextAsync", f.Row, shape, "Right")!;
        await Task.Delay(300);
        f.Find<System.Windows.Controls.TextBox>("ReaderAnnotationEditor").Text = "Beam";
        f.Call("CommitAnnotationEditor", false);
        await Task.Delay(1500);
        var page = await f.PageAsync();
        var text = page.Annotations.Single(x => x.Kind == QuickAnnotationKind.Typewriter);
        var grouped = page.Annotations.Single(x => x.Kind == QuickAnnotationKind.Shape);
        Check(text.Group.Length > 0 && text.Group == grouped.Group, "A shape and its text box share a group");

        f.Select(grouped);
        f.Call("DuplicateSelection");
        await Task.Delay(1500);
        page = await f.PageAsync();
        var texts = page.Annotations.Where(x => x.Kind == QuickAnnotationKind.Typewriter).ToList();
        var shapes = page.Annotations.Where(x => x.Kind == QuickAnnotationKind.Shape).ToList();
        Check(texts.Count == 2 && shapes.Count == 2, "Duplicating a group copies the shape and its text");
        var newGroup = shapes.Single(x => x.Group != grouped.Group).Group;
        Check(newGroup.Length > 0 && texts.Count(x => x.Group == newGroup) == 1 && texts.Count(x => x.Group == grouped.Group) == 1, "The copies form their own group");

        f.Select(shapes.Single(x => x.Group == grouped.Group));
        f.Call("BringSelectionToFront");
        await Task.Delay(1500);
        page = await f.PageAsync();
        var order = page.Annotations.Where(x => x.Kind is QuickAnnotationKind.Shape or QuickAnnotationKind.Typewriter).Select(x => x.Group == grouped.Group).ToList();
        Check(order.Count == 4 && order[2] && order[3], "Bring to front moves the whole group (shape and text) to the top");
    });
}
