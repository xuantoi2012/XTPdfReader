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
        public object? Get(string member)
        {
            var type = typeof(XTPdfMergeApp.ReaderWindow);
            return type.GetProperty(member, Flags)?.GetValue(Window) ?? type.GetField(member, Flags)?.GetValue(Window);
        }
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
    static void RunReaderFlow(string folderName, Func<ReaderFlow, Task> body, string? copyFrom = null)
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        string folder = System.IO.Path.Combine(Output, folderName);
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "page.pdf");
        if (copyFrom != null) File.Copy(copyFrom, path, overwrite: true);
        else using (var doc = new PdfDocument(new PdfWriter(path))) doc.AddNewPage(new PageSize(595, 842));
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

    /// <summary>Dash style: old shape text stays solid, the bar changes the selected shape, and the file carries /BS /S /D plus a dashed appearance stream.</summary>
    static void TestShapeDashStyle() => RunReaderFlow("shape-dash", async f =>
    {
        Check(ShapeStyle.Decode("Rect|#C0392B|2|0").Dash == ShapeStyle.Solid && new ShapeStyle("Rect", "#C0392B", 2, 0).Encode() == "Rect|#C0392B|2|0", "Old shape text decodes as solid and a solid shape encodes as before");
        Check(ShapeStyle.Decode(new ShapeStyle("Oval", "#C0392B", 3, 0, ShapeStyle.Dotted).Encode()).Dash == ShapeStyle.Dotted, "A dotted shape survives encode / decode");

        await f.AddAsync(RectSpec("xt-solid", 0.1, 0.1, 0.4, 0.2, new ShapeStyle(ShapeStyle.Rect, "#C0392B", 3, 0)));
        await f.AddAsync(RectSpec("xt-dashed", 0.1, 0.3, 0.4, 0.4, new ShapeStyle(ShapeStyle.Rect, "#1F6FEB", 3, 0, ShapeStyle.Dashed)));
        await f.AddAsync(RectSpec("xt-dotted", 0.1, 0.5, 0.4, 0.6, new ShapeStyle(ShapeStyle.Oval, "#2E7D32", 3, 0, ShapeStyle.Dotted)));
        await f.AddAsync(RectSpec("xt-line", 0.5, 0.1, 0.9, 0.3, new ShapeStyle(ShapeStyle.Arrow, "#C0392B", 3, 0, ShapeStyle.Dashed)));

        var page = await f.PageAsync();
        var solid = page.Annotations.Single(x => x.Name == "xt-solid");
        f.Select(solid);
        var box = f.Find<System.Windows.Controls.ComboBox>("ShapeDashBox");
        Check(box.SelectedIndex == 0, "The bar shows Solid for a solid shape");
        box.SelectedIndex = ShapeStyle.Dashed;
        await Task.Delay(1500);
        page = await f.PageAsync();
        var changed = page.Annotations.Single(x => x.Name == solid.Name);
        Check(ShapeStyle.Decode(changed.Format).Dash == ShapeStyle.Dashed, "Choosing Dashed in the bar changes the selected rectangle");

        f.Select(page.Annotations.Single(x => x.Name == "xt-dotted"));
        Check(f.Find<System.Windows.Controls.ComboBox>("ShapeDashBox").SelectedIndex == ShapeStyle.Dotted, "Selecting a dotted oval shows Dotted in the bar");

        f.Window.UpdateLayout();
        SavePng(f.Find<System.Windows.FrameworkElement>("ReaderContentHost"), "shape-dash");

        // The edits are still pending (Save writes them); write them into a copy the way Save does.
        string saved = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f.Path)!, "saved.pdf");
        using (var writing = new PdfDocument(new PdfReader(f.Path), new PdfWriter(saved)))
            PdfQuickAnnotationService.ApplyChanges(writing, AnnotationStore.Pending(f.Path));
        using var doc = new PdfDocument(new PdfReader(saved));
        int dashed = 0;
        foreach (var annot in doc.GetPage(1).GetAnnotations())
        {
            var bs = annot.GetPdfObject().GetAsDictionary(PdfName.BS);
            if (bs?.GetAsName(PdfName.S) == PdfName.D && bs.GetAsArray(PdfName.D) is { } pattern && pattern.Size() == 2) dashed++;
        }
        Check(dashed == 4, "Four annotations carry /BS /S /D with a 2-number pattern (got " + dashed + ")");
    });

    /// <summary>Opacity: shapes and text carry it in their format, the bar changes it, and the file gets /CA and a transparent appearance.</summary>
    static void TestOpacity() => RunReaderFlow("opacity", async f =>
    {
        Check(ShapeStyle.Decode("Rect|#C0392B|2|0").Opacity == 100 && new ShapeStyle("Rect", "#C0392B", 2, 0).Encode() == "Rect|#C0392B|2|0", "Old shape text is fully opaque and encodes as before");
        Check(ShapeStyle.Decode(new ShapeStyle("Line", "#C0392B", 2, 0, ShapeStyle.Dotted, 50).Encode()) is { Opacity: 50, Dash: ShapeStyle.Dotted }, "Shape opacity and dash survive encode / decode");
        var plain = new TextFormat("Arial", 12, "#000000", false, false);
        Check(plain.Encode() == "Arial|12|#000000|0|0" && TextFormat.Decode(plain.Encode()).Opacity == 100, "Old text format is fully opaque and encodes as before");
        Check(TextFormat.Decode((plain with { Opacity = 25, Width = 80 }).Encode()) is { Opacity: 25, Width: 80 }, "Text opacity survives encode / decode");

        await f.AddAsync(RectSpec("xt-thick", 0.1, 0.2, 0.7, 0.3, new ShapeStyle(ShapeStyle.Line, "#C0392B", 6, 0)));
        await f.AddAsync(RectSpec("xt-half", 0.1, 0.4, 0.7, 0.5, new ShapeStyle(ShapeStyle.Line, "#C0392B", 6, 0, ShapeStyle.Solid, 50)));
        var text = new QuickAnnotationSpec("xt-text", QuickAnnotationKind.Typewriter, 1, 0.12, 0.05, 0.5, 0.1, "Faded text") { Format = (plain with { Size = 28, Opacity = 50 }).Encode() };
        await f.AddAsync(text);
        var page = await f.PageAsync();

        f.Select(page.Annotations.Single(x => x.Name == "xt-thick"));
        var box = f.Find<System.Windows.Controls.ComboBox>("ShapeOpacityBox");
        Check(box.SelectedIndex == 0, "The bar shows 100% for an opaque shape");
        box.SelectedIndex = Array.IndexOf(ShapeStyle.Opacities, 25);
        await Task.Delay(1500);
        page = await f.PageAsync();
        Check(ShapeStyle.Decode(page.Annotations.Single(x => x.Name == "xt-thick").Format).Opacity == 25, "Choosing 25% in the bar changes the selected shape");

        var typed = page.Annotations.Single(x => x.Kind == QuickAnnotationKind.Typewriter);
        f.Select(typed);
        var textBox = f.Find<System.Windows.Controls.ComboBox>("FmtOpacityBox");
        Check(textBox.IsVisible && textBox.SelectedIndex == Array.IndexOf(TextFormat.Opacities, 50), "The text bar shows the box's 50%");
        textBox.SelectedIndex = Array.IndexOf(TextFormat.Opacities, 75);
        await Task.Delay(1500);
        page = await f.PageAsync();
        Check(TextFormat.Decode(page.Annotations.Single(x => x.Kind == QuickAnnotationKind.Typewriter).Format).Opacity == 75, "Choosing 75% in the text bar changes the selected text box");

        f.Window.UpdateLayout();
        SavePng(f.Find<System.Windows.FrameworkElement>("ReaderContentHost"), "opacity");

        string saved = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f.Path)!, "saved.pdf");
        using (var writing = new PdfDocument(new PdfReader(f.Path), new PdfWriter(saved)))
            PdfQuickAnnotationService.ApplyChanges(writing, AnnotationStore.Pending(f.Path));
        using var doc = new PdfDocument(new PdfReader(saved));
        var alphas = doc.GetPage(1).GetAnnotations().Select(a => a.GetPdfObject().GetAsNumber(PdfName.CA)?.DoubleValue() ?? 1.0).OrderBy(v => v).ToList();
        Check(alphas.SequenceEqual(new[] { 0.25, 0.5, 0.75 }) || alphas.Count(v => v < 1) == 3, "Three annotations carry /CA (got " + string.Join(", ", alphas) + ")");
    });

    /// <summary>Fill colour of closed shapes: encoding, the bar, clicking inside a filled shape, /IC in the file.</summary>
    static void TestShapeFill() => RunReaderFlow("shape-fill", async f =>
    {
        Check(ShapeStyle.Decode("Rect|#C0392B|2|0").Fill == "" && new ShapeStyle("Rect", "#C0392B", 2, 0).Encode() == "Rect|#C0392B|2|0", "Old shape text has no fill and encodes as before");
        Check(ShapeStyle.Decode(new ShapeStyle("Oval", "#C0392B", 2, 0, ShapeStyle.Dashed, 50, "#FFF2A8").Encode()) is { Fill: "#FFF2A8", Opacity: 50, Dash: ShapeStyle.Dashed }, "Fill survives encode / decode with dash and opacity");
        Check(ShapeStyle.Decode("Line|#C0392B|2|0|0|100|#FFF2A8").Fill == "", "A line never keeps a fill");

        await f.AddAsync(RectSpec("xt-open", 0.1, 0.1, 0.4, 0.25));
        await f.AddAsync(RectSpec("xt-yellow", 0.1, 0.3, 0.4, 0.45, new ShapeStyle(ShapeStyle.Rect, "#C0392B", 2, 0, 0, 100, "#FFF2A8")));
        await f.AddAsync(RectSpec("xt-cloud", 0.5, 0.1, 0.9, 0.3, new ShapeStyle(ShapeStyle.Cloud, "#1F6FEB", 2, 0, 0, 60, "#BBDEFB")));
        await f.AddAsync(RectSpec("xt-oval", 0.5, 0.35, 0.8, 0.5, new ShapeStyle(ShapeStyle.Oval, "#2E7D32", 2, 0, 0, 100, "#C8E6C9")));
        await f.AddAsync(RectSpec("xt-arrow", 0.1, 0.55, 0.6, 0.7, new ShapeStyle(ShapeStyle.Arrow, "#C0392B", 3, 0)));
        var page = await f.PageAsync();

        var hitTest = typeof(XTPdfMergeApp.ReaderWindow).GetMethod("HitAnnotation", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        bool Hits(string name, double u, double v) => (bool)hitTest.Invoke(null, new[] { page.Annotations.Single(x => x.Name == name), page.Geometry, f.Hit(u, v), 4.0 })!;
        Check(!Hits("xt-open", 0.25, 0.175) && Hits("xt-yellow", 0.25, 0.375), "The inside of a filled rectangle is clickable, the inside of an open one is not");
        Check(Hits("xt-oval", 0.65, 0.425), "The inside of a filled oval is clickable");

        f.Select(page.Annotations.Single(x => x.Name == "xt-open"));
        var box = f.Find<System.Windows.Controls.ComboBox>("ShapeFillBox");
        Check(box.IsVisible && box.SelectedIndex == 0, "The bar shows 'No fill' for an open rectangle");
        box.SelectedIndex = Array.FindIndex(ShapeStyle.Fills, x => x.Hex == "#F8C9C4");
        await Task.Delay(1500);
        page = await f.PageAsync();
        Check(ShapeStyle.Decode(page.Annotations.Single(x => x.Name == "xt-open").Format).Fill == "#F8C9C4", "Choosing Red in the bar fills the selected rectangle");

        f.Select(page.Annotations.Single(x => x.Name == "xt-arrow"));
        Check(!f.Find<System.Windows.Controls.ComboBox>("ShapeFillBox").IsVisible, "The fill box is hidden for an arrow");
        f.Select(page.Annotations.Single(x => x.Name == "xt-cloud"));
        Check(f.Find<System.Windows.Controls.ComboBox>("ShapeFillBox").SelectedIndex == Array.FindIndex(ShapeStyle.Fills, x => x.Hex == "#BBDEFB"), "Selecting the cloud shows its blue fill");

        f.Window.UpdateLayout();
        SavePng(f.Find<System.Windows.FrameworkElement>("ReaderContentHost"), "shape-fill");

        string saved = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f.Path)!, "saved.pdf");
        using (var writing = new PdfDocument(new PdfReader(f.Path), new PdfWriter(saved)))
            PdfQuickAnnotationService.ApplyChanges(writing, AnnotationStore.Pending(f.Path));
        using var doc = new PdfDocument(new PdfReader(saved));
        int interior = doc.GetPage(1).GetAnnotations().Count(a => a.GetPdfObject().GetAsArray(PdfName.IC) is { } ic && ic.Size() == 3);
        Check(interior == 4, "Four annotations carry /IC (got " + interior + ")");
    });
}
