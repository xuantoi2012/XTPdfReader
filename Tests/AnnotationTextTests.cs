using System.IO;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    /// <summary>Typewriter text boxes (Edge style): fixed width wraps the text, alignment and underline are written, old formats still read.</summary>
    static void TestTypewriterTextBox()
    {
        string folder = System.IO.Path.Combine(Output, "typewriter-box");
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "t.pdf");
        using (var doc = new PdfDocument(new PdfWriter(path))) doc.AddNewPage(new PageSize(595, 842));

        Check(TextFormat.Decode("Arial|12|#000000|0|0") == new TextFormat("Arial", 12, "#000000", false, false), "An old five-field text format still decodes");
        Check(new TextFormat("Arial", 12, "#000000", false, false).Encode() == "Arial|12|#000000|0|0", "A plain format still encodes to the old string");
        var wide = new TextFormat("Arial", 12, "#2563EB", false, false, Underline: true, Align: 1, Width: 150);
        Check(TextFormat.Decode(wide.Encode()) == wide, "Underline, alignment and width survive encode/decode");

        string text = "This sentence is long enough to need several lines in a narrow box";
        string tempOut = System.IO.Path.Combine(folder, "t-out.pdf");
        using (var reader = new PdfReader(path))
        using (var doc = new PdfDocument(reader, new PdfWriter(tempOut)))
        {
            var geometry = PdfQuickAnnotationService.GetGeometry(doc.GetPage(1));
            var spec = new QuickAnnotationSpec("xt-1", QuickAnnotationKind.Typewriter, 1, 0.1, 0.1, 0.1, 0.1, text) { Format = wide.Encode() };
            spec = PdfQuickAnnotationService.WithMeasuredSize(spec, geometry);
            Check(Math.Abs((spec.U2 - spec.U1) * geometry.DisplayWidth - 150) < 0.5, "A fixed-width text box measures exactly its width");
            Check((spec.V2 - spec.V1) * geometry.DisplayHeight > 2 * 12 * 1.2, "The wrapped text makes the box taller than one line");
            PdfQuickAnnotationService.ApplyChanges(doc, new[] { new QuickAnnotationChange(null, spec) });
        }
        using (var doc = new PdfDocument(new PdfReader(tempOut)))
        {
            var page = doc.GetPage(1);
            var geometry = PdfQuickAnnotationService.GetGeometry(page);
            var read = PdfQuickAnnotationService.ReadAnnotations(page, geometry, 1).Single();
            Check(read.Kind == QuickAnnotationKind.Typewriter && read.Text == text, "The written text box reads back");
            Check(TextFormat.Decode(read.Format) == wide, "The format (width, centre, underline) is stored in the file");
            Check(Math.Abs((read.U2 - read.U1) * geometry.DisplayWidth - 150) < 0.5, "The annotation rectangle is the fixed width");
            var annot = page.GetAnnotations().Single();
            var ap = annot.GetNormalAppearanceObject() as PdfStream;
            string content = ap == null ? "" : System.Text.Encoding.Latin1.GetString(ap.GetBytes());
            Check(content.Contains(" re") == false && content.Contains(" l\n") || content.Contains(" l"), "An underline is drawn in the appearance");
        }
    }
}

internal static partial class Program
{
    /// <summary>End to end in the real reader: type a new text box, see the Edge-style chrome, drag its width circle, commit, read the saved box back.</summary>
    static void TestTypewriterEditorChrome()
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        string folder = System.IO.Path.Combine(Output, "typewriter-chrome");
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "page.pdf");
        using (var doc = new PdfDocument(new PdfWriter(path))) doc.AddNewPage(new PageSize(595, 842));

        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
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
                await reader.Session.OpenFilesInReaderAsync(new[] { path });                 await Task.Delay(1500);
                reader.UpdateLayout();
                var row = (XTPdfMergeApp.Domain.PagePlacement)typeof(XTPdfMergeApp.ReaderWindow).GetField("_readerPage", flags)!.GetValue(reader)!;
                var hitType = typeof(XTPdfMergeApp.ReaderWindow).GetNestedType("PageHit", flags)!;
                var hit = Activator.CreateInstance(hitType, row, 0.2, 0.2)!;
                reader.Activate(); reader.Focus();
                // A background test window has no keyboard focus; do not let the focus-lost rule close the box under test.
                var lost = (System.Windows.Input.KeyboardFocusChangedEventHandler)Delegate.CreateDelegate(typeof(System.Windows.Input.KeyboardFocusChangedEventHandler), reader,
                    typeof(XTPdfMergeApp.ReaderWindow).GetMethod("ReaderAnnotationEditor_LostKeyboardFocus", flags)!);
                ((System.Windows.Controls.TextBox)reader.FindName("ReaderAnnotationEditor")).LostKeyboardFocus -= lost;
                var open = typeof(XTPdfMergeApp.ReaderWindow).GetMethod("OpenAnnotationEditorAsync", flags)!;
                await (Task)open.Invoke(reader, new object?[] { hit, QuickAnnotationKind.Typewriter, null, null, null })!;                 await Task.Delay(300);
                reader.UpdateLayout(); 
                T Find<T>(string name) where T : class => (T)reader.FindName(name);
                var box = Find<System.Windows.Shapes.Rectangle>("TextChromeBox");
                var grip = Find<System.Windows.Controls.Border>("TextGrip");
                var handle = Find<System.Windows.Shapes.Ellipse>("TextWidthHandle");
                var hint = Find<System.Windows.Controls.TextBlock>("TextHint");
                var editor = Find<System.Windows.Controls.TextBox>("ReaderAnnotationEditor");
                var format = Find<System.Windows.Controls.Border>("TextFormatBar");
                Check(box.Visibility == System.Windows.Visibility.Visible && grip.Visibility == System.Windows.Visibility.Visible && handle.Visibility == System.Windows.Visibility.Visible,
                    $"A new text box shows the dashed outline, the grip and the width circle (box {box.Visibility}, editor {editor.Visibility} {editor.ActualWidth}x{editor.ActualHeight} at {Canvas_Left(editor)},{Canvas_Top(editor)}, editorState={typeof(XTPdfMergeApp.ReaderWindow).GetField("_annotationEditor", flags)!.GetValue(reader) != null})");
                Check(hint.Visibility == System.Windows.Visibility.Visible, "An empty new text box shows 'Start typing here…'");
                Check(Canvas_Left(grip) < Canvas_Left(editor) && Canvas_Left(handle) > Canvas_Left(editor), "The grip is on the left of the box and the circle on its right");
                Check(format.Visibility == System.Windows.Visibility.Visible && format.Margin.Top < Canvas_Top(editor) + 2, $"The format bar floats next to the box (bar top {format.Margin.Top:0}, box top {Canvas_Top(editor):0})");

                                editor.Text = "A text box that is made narrow so that it must wrap onto several lines";
                await Task.Delay(100);                 Check(hint.Visibility == System.Windows.Visibility.Collapsed, "The hint disappears once there is text");

                // drag the circle to u = 0.6 (the box starts at u = 0.2 => 0.4 x 595 pt wide)
                var toLayer = typeof(XTPdfMergeApp.ReaderWindow).GetMethod("TryPageToLayer", flags)!;
                object[] args = { row, 0.6, 0.2, null! };
                Check((bool)toLayer.Invoke(reader, args)!, "The page is on screen");
                var target = (System.Windows.Point)args[3];
                typeof(XTPdfMergeApp.ReaderWindow).GetField("_chromeDrag", flags)!.SetValue(reader, Enum.ToObject(typeof(XTPdfMergeApp.ReaderWindow).GetNestedType("TextChromeDrag", flags)!, 2));
                TextFormat fmt = TextFormat.Default;
                for (int attempt = 0; attempt < 20 && fmt.Width <= 0; attempt++) // the page may still be laying out on a busy machine
                {
                    typeof(XTPdfMergeApp.ReaderWindow).GetMethod("UpdateTextChromeDrag", flags)!.Invoke(reader, new object[] { target });
                    fmt = (TextFormat)typeof(XTPdfMergeApp.ReaderWindow).GetField("_textFormat", flags)!.GetValue(reader)!;
                    if (fmt.Width <= 0) await Task.Delay(250);
                }
                typeof(XTPdfMergeApp.ReaderWindow).GetMethod("FinishTextChromeDrag", flags)!.Invoke(reader, null);
                Check(Math.Abs(fmt.Width - 0.4 * 595) < 2, $"Dragging the circle sets the box width to about 238 pt (got {fmt.Width:0.#})");
                reader.UpdateLayout();
                Check(editor.TextWrapping == System.Windows.TextWrapping.Wrap, "A fixed-width box wraps the text it is typed in");

                typeof(XTPdfMergeApp.ReaderWindow).GetMethod("CommitAnnotationEditor", flags)!.Invoke(reader, new object?[] { false });
                await Task.Delay(1500);
                var saved = await AnnotationStore.GetPageAsync(path, 1);
                var spec = saved?.Annotations.FirstOrDefault(a => a.Kind == QuickAnnotationKind.Typewriter);
                Check(spec != null && TextFormat.Decode(spec.Format).Width > 200 && (spec.V2 - spec.V1) * saved!.Geometry.DisplayHeight > 2 * 12 * 1.2,
                    "The committed box keeps its fixed width and is taller than one line");
            }
            catch (Exception ex) { failure = ex; }
            finally { reader.Session.Documents.Clear(); reader.Close(); app.MainWindow = priorOwner; frame.Continue = false; }
        }));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        if (failure != null) throw new Exception("Typewriter chrome: " + failure.Message, failure);
    }

    static double Canvas_Left(System.Windows.UIElement e) => System.Windows.Controls.Canvas.GetLeft(e);
    static double Canvas_Top(System.Windows.UIElement e) => System.Windows.Controls.Canvas.GetTop(e);
}

internal static partial class Program
{
    /// <summary>Callouts: arrow head + line/box style are stored, old callouts read as before, the appearance draws the head.</summary>
    static void TestCalloutStyle()
    {
        string folder = System.IO.Path.Combine(Output, "callout-style");
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "c.pdf"), output = System.IO.Path.Combine(folder, "c-out.pdf");
        using (var doc = new PdfDocument(new PdfWriter(path))) doc.AddNewPage(new PageSize(595, 842));

        var legacy = PdfQuickAnnotationService.DecodeCallout("C|0.5,0.5|Arial|12|#000000|0|0");
        Check(legacy.Style == CalloutStyle.Legacy && legacy.Style.Arrow == 0 && legacy.TextFormat == "Arial|12|#000000|0|0", "A callout saved before styles keeps the old look (no arrow head) and its text format");
        var style = new CalloutStyle("#C0392B", 2, 2, "#FFF6C4", "#C0392B", 1.5);
        string encoded = PdfQuickAnnotationService.EncodeCallout(0.5, 0.5, "Arial|12|#000000|0|0", style);
        var back = PdfQuickAnnotationService.DecodeCallout(encoded);
        Check(back.Style == style && back.TextFormat == "Arial|12|#000000|0|0" && back.TipU == 0.5, "The callout style survives encode/decode next to the tip point and text format");
        Check(PdfQuickAnnotationService.EncodeCallout(0.5, 0.5, "Arial|12|#000000|0|0", CalloutStyle.Legacy) == "C|0.5,0.5|Arial|12|#000000|0|0", "The legacy look writes the same string as before");
        Check(new CalloutStyle("#000000", 1, 1, CalloutStyle.None, CalloutStyle.None, 1).Encode().Contains("-"), "'No fill / no border' is representable");

        using (var reader = new PdfReader(path))
        using (var doc = new PdfDocument(reader, new PdfWriter(output)))
        {
            var spec = new QuickAnnotationSpec("xt-c1", QuickAnnotationKind.Callout, 1, 0.4, 0.2, 0.7, 0.25, "Check this beam") { Format = encoded };
            PdfQuickAnnotationService.ApplyChanges(doc, new[] { new QuickAnnotationChange(null, spec) });
        }
        using (var doc = new PdfDocument(new PdfReader(output)))
        {
            var page = doc.GetPage(1);
            var read = PdfQuickAnnotationService.ReadAnnotations(page, PdfQuickAnnotationService.GetGeometry(page), 1).Single();
            Check(read.Kind == QuickAnnotationKind.Callout && PdfQuickAnnotationService.DecodeCallout(read.Format).Style == style, "The written callout reads back with its style");
            var annot = page.GetAnnotations().Single().GetPdfObject();
            Check(annot.GetAsName(new iText.Kernel.Pdf.PdfName("LE"))?.GetValue() == "ClosedArrow", "The callout declares its arrow head (/LE) for other viewers");
            string content = System.Text.Encoding.Latin1.GetString(((PdfStream)page.GetAnnotations().Single().GetNormalAppearanceObject()).GetBytes());
            Check(content.Contains("\nf\n") || content.Contains(" f\n") || content.Contains("\nf*"), "The closed arrow head is a filled triangle in the appearance");
        }
    }
}

internal static partial class Program
{
    /// <summary>Select tool: I-beam only over words, a selection bar after dragging across text, markup from the bar lands in the file.</summary>
    static void TestSelectToolBar()
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        string folder = System.IO.Path.Combine(Output, "select-bar");
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "text.pdf");
        using (var doc = new PdfDocument(new PdfWriter(path)))
        {
            var page = doc.AddNewPage(new PageSize(595, 842));
            var canvas = new iText.Kernel.Pdf.Canvas.PdfCanvas(page);
            canvas.BeginText().SetFontAndSize(iText.Kernel.Font.PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA), 24)
                .MoveText(100, 700).ShowText("Hello selectable world").EndText();
        }

        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
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
                var rowType = typeof(XTPdfMergeApp.ReaderWindow);
                var row = (XTPdfMergeApp.Domain.PagePlacement)rowType.GetField("_readerPage", flags)!.GetValue(reader)!;
                var toolType = rowType.GetNestedType("ReaderTool", flags)!;
                rowType.GetMethod("SetReaderTool", flags)!.Invoke(reader, new[] { Enum.Parse(toolType, "Select") });

                var words = await PdfThumbnailService.GetWordRectsAsync(path, 1);
                Check(words is { Count: 3 }, $"The worker reports the 3 words of the page ({words?.Count})");
                var host = (System.Windows.FrameworkElement)reader.FindName("ReaderContentHost");
                object[] a1 = { row, (words![0].U1 + words[0].U2) / 2, (words[0].V1 + words[0].V2) / 2, null! };
                object[] a2 = { row, 0.9, 0.9, null! };
                var toLayer = rowType.GetMethod("TryPageToLayer", flags)!;
                Check((bool)toLayer.Invoke(reader, a1)! && (bool)toLayer.Invoke(reader, a2)!, "The page is on screen");
                var update = rowType.GetMethod("UpdateSelectCursor", flags)!;
                update.Invoke(reader, new object[] { a1[3] });
                await Task.Delay(300);
                update.Invoke(reader, new object[] { a1[3] });
                Check(ReferenceEquals(host.Cursor, System.Windows.Input.Cursors.IBeam), "Over a word the pointer is an I-beam");
                update.Invoke(reader, new object[] { a2[3] });
                Check(ReferenceEquals(host.Cursor, System.Windows.Input.Cursors.Arrow), "Over empty paper the pointer is an arrow");

                // drag across the whole line
                var dragType = rowType.GetNestedType("TextSelDrag", flags)!;
                var drag = Activator.CreateInstance(dragType, row, words[0].U1 - 0.005, (words[0].V1 + words[0].V2) / 2)!;
                ((System.Windows.Shapes.Rectangle)reader.FindName("TextSelectDragRubber")).Width = 20;
                ((System.Windows.Shapes.Rectangle)reader.FindName("TextSelectDragRubber")).Height = 20;
                object[] endArgs = { row, words[2].U2 + 0.005, (words[2].V1 + words[2].V2) / 2, null! };
                toLayer.Invoke(reader, endArgs);
                await (Task)rowType.GetMethod("FinishTextSelectionDragAsync", flags)!.Invoke(reader, new object[] { drag, endArgs[3] })!;
                var bar = (System.Windows.Controls.Border)reader.FindName("TextSelectionBar");
                reader.UpdateLayout();
                Check(bar.Visibility == System.Windows.Visibility.Visible, "After selecting text the selection bar appears");
                Check(bar.Margin.Top < ((System.Windows.Point)a1[3]).Y, $"The bar sits above the selected line (bar top {bar.Margin.Top:0}, text y {((System.Windows.Point)a1[3]).Y:0})");

                rowType.GetMethod("SelBarUnderline_Click", flags)!.Invoke(reader, new object?[] { null, null });
                await Task.Delay(1500);
                var saved = await AnnotationStore.GetPageAsync(path, 1);
                var markup = saved?.Annotations.FirstOrDefault(x => x.Kind == QuickAnnotationKind.Underline);
                Check(markup != null && PdfQuickAnnotationService.TextHighlightRects(markup.Format).Count >= 1, "The Underline button writes an underline over the selected words");
                Check(bar.Visibility == System.Windows.Visibility.Collapsed, "The bar closes after the markup is made");
            }
            catch (Exception ex) { failure = ex; }
            finally { reader.Session.Documents.Clear(); reader.Close(); app.MainWindow = priorOwner; frame.Continue = false; }
        }));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        if (failure != null) throw new Exception("Select bar: " + failure.Message, failure);
    }
}

internal static partial class Program
{
    /// <summary>Rectangle with text: "T" handles on the sides, the typed text and the shape become one group that moves and deletes together.</summary>
    static void TestShapeTextGroup()
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        string folder = System.IO.Path.Combine(Output, "shape-group");
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "page.pdf");
        using (var doc = new PdfDocument(new PdfWriter(path))) doc.AddNewPage(new PageSize(595, 842));

        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        var type = typeof(XTPdfMergeApp.ReaderWindow);
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
                var row = (XTPdfMergeApp.Domain.PagePlacement)type.GetField("_readerPage", flags)!.GetValue(reader)!;
                var lost = (System.Windows.Input.KeyboardFocusChangedEventHandler)Delegate.CreateDelegate(typeof(System.Windows.Input.KeyboardFocusChangedEventHandler), reader, type.GetMethod("ReaderAnnotationEditor_LostKeyboardFocus", flags)!);
                ((System.Windows.Controls.TextBox)reader.FindName("ReaderAnnotationEditor")).LostKeyboardFocus -= lost;

                // a rectangle written through the same path as drawing one
                var style = new ShapeStyle(ShapeStyle.Rect, "#C0392B", 2, 0);
                var shape = new QuickAnnotationSpec("xt-shape", QuickAnnotationKind.Shape, 1, 0.3, 0.3, 0.6, 0.4, "") { Format = style.Encode() };
                await ((XTPdfMergeApp.IReaderPageEditHost)reader.Session).ApplyAnnotationChangesAsync(path, new[] { new QuickAnnotationChange(null, shape) }, "Draw rect");
                await Task.Delay(800);
                var page = await AnnotationStore.GetPageAsync(path, 1);
                shape = page!.Annotations.Single(a => a.Kind == QuickAnnotationKind.Shape);
                type.GetMethod("SelectAnnotation", flags)!.Invoke(reader, new object?[] { row, shape });
                reader.UpdateLayout();
                var top = (System.Windows.FrameworkElement)reader.FindName("ShapeTTop");
                Check(top.Visibility == System.Windows.Visibility.Visible && ((System.Windows.FrameworkElement)reader.FindName("ShapeTLeft")).Visibility == System.Windows.Visibility.Visible,
                    "A selected rectangle shows a T at the middle of its sides");

                await (Task)type.GetMethod("BeginShapeTextAsync", flags)!.Invoke(reader, new object?[] { row, shape, "Right" })!;
                await Task.Delay(300);
                var editor = (System.Windows.Controls.TextBox)reader.FindName("ReaderAnnotationEditor");
                Check(editor.Visibility == System.Windows.Visibility.Visible, "Clicking a T opens a text box");
                editor.Text = "Beam B1";
                type.GetMethod("CommitAnnotationEditor", flags)!.Invoke(reader, new object?[] { false });
                await Task.Delay(1500);
                page = await AnnotationStore.GetPageAsync(path, 1);
                var text = page!.Annotations.Single(a => a.Kind == QuickAnnotationKind.Typewriter);
                var grouped = page.Annotations.Single(a => a.Kind == QuickAnnotationKind.Shape);
                Check(text.Group.Length > 0 && text.Group == grouped.Group, "The text box and the rectangle share one group id");
                Check(text.U1 >= grouped.U2, "A box opened from the right side sits to the right of the shape");

                // move the group
                var hitType = type.GetNestedType("PageHit", flags)!;
                var startHit = Activator.CreateInstance(hitType, row, 0.45, 0.3)!;
                type.GetMethod("SelectAnnotation", flags)!.Invoke(reader, new object?[] { row, grouped });
                type.GetMethod("BeginAnnotationMove", flags)!.Invoke(reader, new object?[] { startHit, grouped });
                object[] toPoint = { row, 0.45, 0.5, null! };
                ((Func<bool>)(() => (bool)type.GetMethod("TryPageToLayer", flags)!.Invoke(reader, toPoint)!))();
                for (int i = 0; i < 20; i++) { type.GetMethod("UpdateAnnotationMove", flags)!.Invoke(reader, new object[] { toPoint[3] }); await Task.Delay(50); }
                type.GetMethod("FinishAnnotationMove", flags)!.Invoke(reader, null);
                await Task.Delay(1500);
                page = await AnnotationStore.GetPageAsync(path, 1);
                var movedText = page!.Annotations.Single(a => a.Kind == QuickAnnotationKind.Typewriter);
                var movedShape = page.Annotations.Single(a => a.Kind == QuickAnnotationKind.Shape);
                Check(movedShape.V1 > grouped.V1 + 0.05 && Math.Abs((movedText.V1 - text.V1) - (movedShape.V1 - grouped.V1)) < 0.003, "Moving the shape moves its text box by the same amount");

                // delete the group
                type.GetMethod("SelectAnnotation", flags)!.Invoke(reader, new object?[] { row, movedShape });
                type.GetMethod("DeleteSelectedAnnotation", flags)!.Invoke(reader, null);
                await Task.Delay(1500);
                page = await AnnotationStore.GetPageAsync(path, 1);
                Check(page!.Annotations.Count(a => a.Kind is QuickAnnotationKind.Shape or QuickAnnotationKind.Typewriter) == 0, "Deleting the shape deletes its text box too");
            }
            catch (Exception ex) { failure = ex; }
            finally { reader.Session.Documents.Clear(); reader.Close(); app.MainWindow = priorOwner; frame.Continue = false; }
        }));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        if (failure != null) throw new Exception("Shape group: " + failure.Message, failure);
    }
}

internal static partial class Program
{
    /// <summary>The owner's report: after drawing a shape it must stay selected with its bar and grips; a note must leave a visible icon.</summary>
    static void TestDrawShapeAndNoteFlows()
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        string folder = System.IO.Path.Combine(Output, "draw-flows");
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "page.pdf");
        using (var doc = new PdfDocument(new PdfWriter(path))) doc.AddNewPage(new PageSize(595, 842));

        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        var type = typeof(XTPdfMergeApp.ReaderWindow);
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
                var row = (XTPdfMergeApp.Domain.PagePlacement)type.GetField("_readerPage", flags)!.GetValue(reader)!;
                T F<T>(string name) where T : class => (T)reader.FindName(name);

                // ---- draw a rectangle exactly as the mouse flow does ----
                var toolType = type.GetNestedType("ReaderTool", flags)!;
                type.GetMethod("SelectShapeType", flags)!.Invoke(reader, new object[] { ShapeStyle.Rect });
                var dragType = type.GetNestedType("ShapeDrag", flags)!;
                var drag = Activator.CreateInstance(dragType, row, 0.3, 0.3)!;
                await (Task)type.GetMethod("CommitShapeAsync", flags)!.Invoke(reader, new object[] { drag, 0.6, 0.4 })!;
                await Task.Delay(1500);
                reader.UpdateLayout();
                var selected = type.GetField("_selAnn", flags)!.GetValue(reader) as QuickAnnotationSpec;
                Check(selected is { Kind: QuickAnnotationKind.Shape }, "A rectangle that was just drawn stays selected");
                var grips = new[] { "GripNW", "GripN", "GripNE", "GripE", "GripSE", "GripS", "GripSW", "GripW" };
                int visibleGrips = grips.Count(g => F<System.Windows.UIElement>(g).Visibility == System.Windows.Visibility.Visible);
                Check(visibleGrips == 8, $"The selected rectangle shows its 8 resize grips ({visibleGrips})");
                var bar = F<System.Windows.Controls.Border>("ShapeBar");
                Check(bar.Visibility == System.Windows.Visibility.Visible, "The shape bar (colour, width) is shown for the selected rectangle");
                SavePng((System.Windows.FrameworkElement)reader.FindName("ReaderContentHost"), "draw-flows-rect");

                // change the width from the bar
                var widthBox = F<System.Windows.Controls.ComboBox>("ShapeWidthBox");
                widthBox.SelectedIndex = 4; // 6 pt
                await Task.Delay(1500);
                var page = await AnnotationStore.GetPageAsync(path, 1);
                var shape = page!.Annotations.Single(a => a.Kind == QuickAnnotationKind.Shape);
                Check(Math.Abs(ShapeStyle.Decode(shape.Format).Width - 6) < 0.01, $"Choosing 6 pt in the bar rewrites the rectangle with that width ({ShapeStyle.Decode(shape.Format).Width})");

                // ---- an arrow ----
                type.GetMethod("SelectShapeType", flags)!.Invoke(reader, new object[] { ShapeStyle.Arrow });
                var arrowDrag = Activator.CreateInstance(dragType, row, 0.2, 0.6)!;
                await (Task)type.GetMethod("CommitShapeAsync", flags)!.Invoke(reader, new object[] { arrowDrag, 0.5, 0.7 })!;
                await Task.Delay(1500);
                reader.UpdateLayout();
                selected = type.GetField("_selAnn", flags)!.GetValue(reader) as QuickAnnotationSpec;
                Check(selected is { Kind: QuickAnnotationKind.Shape } && ShapeStyle.Decode(selected.Format).Type == ShapeStyle.Arrow, "An arrow that was just drawn stays selected");
                Check(F<System.Windows.UIElement>("GripLineA").Visibility == System.Windows.Visibility.Visible && F<System.Windows.UIElement>("GripLineB").Visibility == System.Windows.Visibility.Visible,
                    "The selected arrow shows grips on both ends");
                Check(bar.Visibility == System.Windows.Visibility.Visible, "The shape bar is shown for the selected arrow");
                SavePng((System.Windows.FrameworkElement)reader.FindName("ReaderContentHost"), "draw-flows-arrow");

                // ---- a note ----
                var hitType = type.GetNestedType("PageHit", flags)!;
                var hit = Activator.CreateInstance(hitType, row, 0.5, 0.2)!;
                await (Task)type.GetMethod("AddNoteAsync", flags)!.Invoke(reader, new object[] { hit, "Check the beam here" })!;
                await Task.Delay(1500);
                page = await AnnotationStore.GetPageAsync(path, 1);
                var note = page!.Annotations.SingleOrDefault(a => a.Kind == QuickAnnotationKind.Comment);
                Check(note != null && note.Text == "Check the beam here", "The note is stored on the page");
                var image = note == null ? null : AnnotationAppearance.Get(path, 1, note, page.Geometry, 1.5);
                Check(image != null && image.Bitmap.PixelWidth > 4, $"The note has an icon appearance to draw ({image?.Bitmap.PixelWidth}px)");
                reader.UpdateLayout(); await Task.Delay(500);
                SavePng((System.Windows.FrameworkElement)reader.FindName("ReaderContentHost"), "draw-flows-note");
            }
            catch (Exception ex) { failure = ex; }
            finally { reader.Session.Documents.Clear(); reader.Close(); app.MainWindow = priorOwner; frame.Continue = false; }
        }));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        if (failure != null) throw new Exception("Draw flows: " + failure.Message, failure);
    }
}

internal static partial class Program
{
    /// <summary>A note icon on an A1 sheet must be bigger than on A4, or it cannot be found on a busy drawing.</summary>
    static void TestNoteIconScalesWithPage()
    {
        string folder = System.IO.Path.Combine(Output, "note-scale");
        Directory.CreateDirectory(folder);
        double IconWidth(float pageWidth, float pageHeight)
        {
            string path = System.IO.Path.Combine(folder, $"p{pageWidth}.pdf"), output = System.IO.Path.Combine(folder, $"p{pageWidth}-out.pdf");
            using (var doc = new PdfDocument(new PdfWriter(path))) doc.AddNewPage(new PageSize(pageWidth, pageHeight));
            using (var reader = new PdfReader(path))
            using (var doc = new PdfDocument(reader, new PdfWriter(output)))
            {
                var geometry = PdfQuickAnnotationService.GetGeometry(doc.GetPage(1));
                var spec = PdfQuickAnnotationService.WithMeasuredSize(new QuickAnnotationSpec("xt-n", QuickAnnotationKind.Comment, 1, 0.5, 0.5, 0.5, 0.5, "note"), geometry);
                PdfQuickAnnotationService.ApplyChanges(doc, new[] { new QuickAnnotationChange(null, spec) });
            }
            using var check = new PdfDocument(new PdfReader(output));
            var page = check.GetPage(1);
            var read = PdfQuickAnnotationService.ReadAnnotations(page, PdfQuickAnnotationService.GetGeometry(page), 1).Single(a => a.Kind == QuickAnnotationKind.Comment);
            return (read.U2 - read.U1) * PdfQuickAnnotationService.GetGeometry(page).DisplayWidth;
        }
        double a4 = IconWidth(595, 842), a1 = IconWidth(2384, 1684);
        Check(Math.Abs(a4 - 20) < 0.6, $"A note icon on A4 is 20 pt ({a4:0.#})");
        Check(a1 > 2.5 * a4, $"A note icon on A1 is much bigger ({a1:0.#} pt) so it can be found on a busy drawing");
    }
}
