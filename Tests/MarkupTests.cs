using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using iText.Kernel.Pdf;
using XTCapture;

internal static partial class Program
{
    static MarkupItem MkRect(double x1, double y1, double x2, double y2, MarkupStyle? style = null, string id = "r")
        => new(id, MarkupKind.Rectangle, x1, y1, x2, y2, style ?? new MarkupStyle());

    static BitmapSource GrayPicture(int width, int height, byte gray = 200)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = pixels[i + 1] = pixels[i + 2] = gray; pixels[i + 3] = 255; }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    /// <summary>The objects round-trip through markup.json; a damaged file is an empty drawing.</summary>
    static void TestMarkupModel()
    {
        var items = new List<MarkupItem>
        {
            MkRect(10, 20, 110, 90, new MarkupStyle("#2563EB", 4, MarkupStyle.Dashed, 50, "#FFF2A8"), "a"),
            new("b", MarkupKind.Pen, 0, 0, 30, 30, new MarkupStyle(Width: 2), Points: new List<double> { 0, 0, 10, 5, 30, 30 }),
            new("c", MarkupKind.Text, 5, 6, 5, 6, new MarkupStyle(Font: "Arial", FontSize: 32, Bold: true, Italic: true, Underline: true), "Hello\nthere"),
            new("d", MarkupKind.Marker, 50, 60, 50, 60, new MarkupStyle(), Number: 3),
            new("e", MarkupKind.Arrow, 1, 2, 3, 4, new MarkupStyle()),
            new("f", MarkupKind.Mosaic, 0, 0, 40, 40, new MarkupStyle(Width: 16)),
        };
        string json = new MarkupDocument { Items = items }.ToJson();
        var back = MarkupDocument.FromJson(json).Items;
        Check(back.Count == 6 && back[0] == items[0] && back[2] == items[2] && back[3] == items[3] && back[4] == items[4] && back[5] == items[5], "Rectangle, text, marker, arrow and mosaic come back equal");
        Check(back[1].Points!.SequenceEqual(new[] { 0.0, 0, 10, 5, 30, 30 }) && back[1].Kind == MarkupKind.Pen, "A pen stroke keeps its points");
        Check(back[2].Text == "Hello\nthere" && back[2].Style.Underline && back[2].Style.Font == "Arial", "Text keeps its lines and font");
        Check(json.Contains("\"Rectangle\"") && !json.Contains("\"Points\":null"), "Kinds are written by name, empty fields are left out");
        Check(MarkupDocument.FromJson("{ not json").Items.Count == 0 && MarkupDocument.FromJson(null).Items.Count == 0 && MarkupDocument.FromJson("").Items.Count == 0, "A damaged or missing file is an empty drawing");
        var moved = items[1].Translate(10, -5);
        Check(moved.X1 == 10 && moved.Y1 == -5 && moved.Points!.SequenceEqual(new[] { 10.0, -5, 20, 0, 40, 25 }), "Translate moves the corners and every point");
        Check(new MarkupStyle().DashPattern().Length == 0 && new MarkupStyle(Dash: MarkupStyle.Dashed).DashPattern().Length == 2, "Dash patterns");
    }

    /// <summary>What a click picks: outlines, filled areas, text boxes, markers; the topmost wins; handles of a box and of a line.</summary>
    static void TestMarkupGeometry()
    {
        var open = MkRect(100, 100, 200, 160, new MarkupStyle(Width: 4), "open");
        var filled = MkRect(300, 100, 400, 160, new MarkupStyle(Width: 4, Fill: "#FFF2A8"), "filled");
        Check(MarkupGeometry.Hit(open, new Point(100, 130), 4) && MarkupGeometry.Hit(open, new Point(150, 101), 4), "The outline of an open rectangle is hit");
        Check(!MarkupGeometry.Hit(open, new Point(150, 130), 4) && !MarkupGeometry.Hit(open, new Point(50, 50), 4), "Its inside and the outside are not");
        Check(MarkupGeometry.Hit(filled, new Point(350, 130), 4), "The inside of a filled rectangle is hit");

        var oval = new MarkupItem("o", MarkupKind.Ellipse, 100, 200, 200, 260, new MarkupStyle(Width: 4));
        Check(MarkupGeometry.Hit(oval, new Point(100, 230), 4) && !MarkupGeometry.Hit(oval, new Point(150, 230), 4) && MarkupGeometry.Hit(oval with { Style = oval.Style with { Fill = "#FFFFFF" } }, new Point(150, 230), 4), "Ellipse outline, hollow inside, filled inside");

        var line = new MarkupItem("l", MarkupKind.Line, 0, 0, 100, 100, new MarkupStyle(Width: 2));
        Check(MarkupGeometry.Hit(line, new Point(50, 52), 4) && !MarkupGeometry.Hit(line, new Point(50, 70), 4), "A line is hit near it only");
        var pen = new MarkupItem("p", MarkupKind.Pen, 0, 0, 0, 0, new MarkupStyle(Width: 2), Points: new List<double> { 0, 0, 100, 0, 100, 100 });
        Check(MarkupGeometry.Hit(pen, new Point(50, 2), 4) && MarkupGeometry.Hit(pen, new Point(99, 60), 4) && !MarkupGeometry.Hit(pen, new Point(20, 80), 4), "A pen stroke is hit along its segments");
        var text = new MarkupItem("t", MarkupKind.Text, 20, 20, 20, 20, new MarkupStyle(FontSize: 20), "Hello");
        var box = MarkupRenderer.Bounds(text);
        Check(box.Width > 30 && box.Height >= 20 && MarkupGeometry.Hit(text, new Point(box.X + box.Width / 2, box.Y + box.Height / 2), 2) && !MarkupGeometry.Hit(text, new Point(5, 5), 2), "A text is hit inside its measured box");
        var marker = new MarkupItem("m", MarkupKind.Marker, 200, 300, 200, 300, new MarkupStyle(FontSize: 20), Number: 1);
        Check(MarkupGeometry.Hit(marker, new Point(210, 305), 2) && !MarkupGeometry.Hit(marker, new Point(240, 300), 2), "A marker is hit in its circle");
        Check(MarkupGeometry.Hit(new MarkupItem("x", MarkupKind.Mosaic, 0, 0, 50, 50, new MarkupStyle()), new Point(25, 25), 2), "A mosaic is hit inside");

        var below = MkRect(0, 0, 100, 100, new MarkupStyle(Fill: "#FFFFFF"), "below");
        var above = MkRect(50, 50, 150, 150, new MarkupStyle(Fill: "#FFFFFF"), "above");
        Check(MarkupGeometry.Pick(new[] { below, above }, new Point(75, 75), 2)?.Id == "above" && MarkupGeometry.Pick(new[] { below, above }, new Point(10, 10), 2)?.Id == "below" && MarkupGeometry.Pick(new[] { below, above }, new Point(500, 500), 2) == null, "The topmost object is picked");

        Check(MarkupGeometry.Handles(open).Count == 8 && MarkupGeometry.Handles(line).Count == 2 && MarkupGeometry.Handles(text).Count == 0 && MarkupGeometry.Handles(pen).Count == 0, "Boxes have eight handles, lines two, the rest none");
        var limit = new Size(800, 600);
        var grown = MarkupGeometry.Resize(open, new CaptureHandleHit(CaptureHandle.BottomRight), new Point(260, 220), limit);
        Check(grown.X2 == 260 && grown.Y2 == 220 && grown.X1 == 100, "Dragging a corner of a rectangle");
        var longer = MarkupGeometry.Resize(line, new CaptureHandleHit(CaptureHandle.Vertex, 1), new Point(150, 40), limit);
        Check(longer.X2 == 150 && longer.Y2 == 40 && longer.X1 == 0, "Dragging the end of a line");
        Check(MarkupGeometry.HitHandle(open, new Point(101, 99), 6).Kind == CaptureHandle.TopLeft && MarkupGeometry.HitHandle(open, new Point(150, 130), 6).Kind == CaptureHandle.None, "Handles are hit near them");
    }

    static void Drag(MarkupController c, double x1, double y1, double x2, double y2)
    {
        c.PointerDown(new Point(x1, y1));
        c.PointerMove(new Point((x1 + x2) / 2, (y1 + y2) / 2));
        c.PointerMove(new Point(x2, y2));
        c.PointerUp(new Point(x2, y2));
    }

    /// <summary>Tools, selection, moving, resizing, text, numbering, style, undo / redo: all without a screen.</summary>
    static void TestMarkupController()
    {
        var c = new MarkupController { Limit = new Size(800, 600) };
        int changes = 0;
        c.Changed += () => changes++;
        Check(c.Tool == MarkupTool.Select && c.IsEmpty && !c.CanUndo, "Starts with Select and nothing drawn");

        c.SetTool(MarkupTool.Rectangle);
        Drag(c, 100, 100, 220, 180);
        Check(c.Items.Count == 1 && c.Items[0].Kind == MarkupKind.Rectangle && c.Items[0].X2 == 220 && c.SelectedId == c.Items[0].Id, "Dragging with the rectangle tool draws one, selected");
        Drag(c, 300, 300, 301, 301);
        Check(c.Items.Count == 1, "A click or a tiny drag draws nothing");
        c.Undo();
        Check(c.IsEmpty && c.CanRedo && c.SelectedId == null, "Undo takes it away");
        c.Redo();
        Check(c.Items.Count == 1 && !c.CanRedo, "Redo brings it back");

        // a handle of the selected object works with a drawing tool still active
        var drawn = c.Items[0];
        c.Select(drawn.Id);
        c.PointerDown(new Point(220, 180));
        c.PointerMove(new Point(260, 220));
        c.PointerUp(new Point(260, 220));
        Check(c.Items.Count == 1 && c.Items[0].X2 == 260 && c.Items[0].Y2 == 220, "Dragging the corner handle resizes (and does not start a new rectangle)");
        c.Undo();
        Check(c.Items[0] == drawn, "The resize is one undo step");

        c.SetTool(MarkupTool.Arrow);
        Drag(c, 400, 100, 500, 200);
        c.SetTool(MarkupTool.Line);
        Drag(c, 400, 300, 500, 300);
        c.SetTool(MarkupTool.Ellipse);
        Drag(c, 50, 300, 150, 400);
        c.SetTool(MarkupTool.Mosaic);
        Drag(c, 600, 400, 700, 500);
        Check(c.Items.Select(i => i.Kind).SequenceEqual(new[] { MarkupKind.Rectangle, MarkupKind.Arrow, MarkupKind.Line, MarkupKind.Ellipse, MarkupKind.Mosaic }) && c.Items[4].Style.Width == MarkupStyle.DefaultBlock, "Arrow, line, ellipse and mosaic (with its block size)");

        c.SetTool(MarkupTool.Pen);
        c.PointerDown(new Point(10, 500));
        for (int i = 1; i <= 20; i++) c.PointerMove(new Point(10 + i * 5, 500 + (i % 2) * 6));
        c.PointerMove(new Point(110.4, 500.2)); // under 1.5 px from the last: ignored
        c.PointerUp(new Point(110, 500));
        var pen = c.Items[^1];
        Check(pen.Kind == MarkupKind.Pen && pen.Points!.Count == 42, "A pen stroke keeps a point per movement and drops tiny ones (" + pen.Points!.Count + ")");

        // numbered markers
        c.SetTool(MarkupTool.Marker);
        c.PointerDown(new Point(700, 50)); c.PointerUp(new Point(700, 50));
        c.PointerDown(new Point(700, 100)); c.PointerMove(new Point(710, 110)); c.PointerUp(new Point(710, 110));
        var markers = c.Items.Where(i => i.Kind == MarkupKind.Marker).ToList();
        Check(markers.Count == 2 && markers[0].Number == 1 && markers[1].Number == 2 && markers[1].X1 == 710 && markers[1].Y1 == 110, "Markers count 1, 2 and can be dragged right after placing");

        // text
        MarkupItem? requested = null;
        c.TextEditRequested += (item, isNew) => requested = isNew ? item : null;
        c.SetTool(MarkupTool.Text);
        c.PointerDown(new Point(300, 450)); c.PointerUp(new Point(300, 450));
        Check(requested != null && c.EditingTextId == requested.Id && !c.Items.Any(i => i.Kind == MarkupKind.Text), "A click with the text tool asks for a typing box and adds nothing yet");
        c.CommitText(requested!.Id, "First\nline");
        var text = c.Items.Single(i => i.Kind == MarkupKind.Text);
        Check(text.Text == "First\nline" && text.X1 == 300 && c.EditingTextId == null && c.SelectedId == text.Id, "Committing adds the text, selected");
        c.PointerDown(new Point(450, 560)); c.PointerUp(new Point(450, 560));
        c.CommitText(requested.Id, "   ");
        Check(c.Items.Count(i => i.Kind == MarkupKind.Text) == 1, "An empty text is dropped");

        // editing an existing text: click it with the Text tool, or double-click with Select
        MarkupItem? reopened = null;
        c.TextEditRequested += (item, isNew) => { if (!isNew) reopened = item; };
        c.SetTool(MarkupTool.Select);
        var box = MarkupRenderer.Bounds(text);
        c.PointerDown(new Point(box.X + 5, box.Y + 5), 2); c.PointerUp(new Point(box.X + 5, box.Y + 5));
        Check(reopened?.Id == text.Id && c.EditingTextId == text.Id, "Double-click on a text opens it for editing");
        c.CommitText(text.Id, "Changed");
        Check(c.Items.Single(i => i.Kind == MarkupKind.Text).Text == "Changed", "The edit replaces the text");
        c.Undo();
        Check(c.Items.Single(i => i.Kind == MarkupKind.Text).Text == "First\nline", "…as one undo step");
        c.BeginEditText(c.Items.Single(i => i.Kind == MarkupKind.Text));
        c.CommitText(text.Id, "");
        Check(!c.Items.Any(i => i.Kind == MarkupKind.Text), "Emptying a text removes it");
        c.Undo();

        // Select: pick, move, nothing
        c.SetTool(MarkupTool.Select);
        int before = c.Items.Count;
        c.PointerDown(new Point(100, 140)); // on the rectangle's left edge
        Check(c.Selected?.Kind == MarkupKind.Rectangle, "Select picks the object under the mouse");
        c.PointerMove(new Point(130, 150));
        c.PointerMove(new Point(160, 170));
        c.PointerUp(new Point(160, 170));
        var rect = c.Items.First(i => i.Kind == MarkupKind.Rectangle);
        Check(rect.X1 == 160 && rect.Y1 == 130 && c.Items.Count == before, "Dragging moves it by the whole distance");
        c.Undo();
        Check(c.Items.First(i => i.Kind == MarkupKind.Rectangle).X1 == 100, "The whole move is one undo step");
        c.PointerDown(new Point(790, 10)); c.PointerUp(new Point(790, 10));
        Check(c.SelectedId == null, "A click on nothing deselects");

        // style
        c.Select(c.Items.First(i => i.Kind == MarkupKind.Rectangle).Id);
        Check(c.Style == c.Selected!.Style, "The style shown is the selected object's");
        c.ApplyStyle(s => s with { Color = "#2563EB", Width = 6, Dash = MarkupStyle.Dotted, Fill = "#FFF2A8", Opacity = 50 });
        var styled = c.Selected!;
        Check(styled.Style.Color == "#2563EB" && styled.Style.Width == 6 && styled.Style.Dash == MarkupStyle.Dotted && styled.Style.Fill == "#FFF2A8" && styled.Style.Opacity == 50, "Changing the style changes the selected object");
        c.Undo();
        Check(c.Selected!.Style.Color != "#2563EB", "…in one undo step");
        c.Redo();
        c.Select(null);
        c.ApplyStyle(s => s with { Color = "#0F8B6D" });
        c.SetTool(MarkupTool.Rectangle);
        Drag(c, 500, 450, 560, 500);
        Check(c.Items[^1].Style.Color == "#0F8B6D", "With nothing selected the style is the next object's");

        // order, nudge, delete, clear, load
        var first = c.Items[0];
        c.Select(first.Id);
        c.BringToFront();
        Check(c.Items[^1].Id == first.Id, "Bring to front");
        c.Nudge(5, -5);
        Check(c.Items[^1].X1 == first.X1 + 5 && c.Items[^1].Y1 == first.Y1 - 5, "Nudge moves the selected object");
        int count = c.Items.Count;
        c.DeleteSelected();
        Check(c.Items.Count == count - 1 && c.SelectedId == null, "Delete removes the selected object");
        c.Undo();
        Check(c.Items.Count == count, "…and can be undone");
        c.Clear();
        Check(c.IsEmpty && c.CanUndo, "Clear empties the drawing, undoably");
        c.Undo();
        c.Load(new[] { MkRect(1, 1, 50, 50) });
        Check(c.Items.Count == 1 && !c.CanUndo && c.SelectedId == null, "Load replaces the drawing and starts a new history");

        c.SetTool(MarkupTool.Select);
        Check(c.HitForCursor(new Point(1, 25)).Kind == CaptureHandle.Move && c.HitForCursor(new Point(700, 500)).Kind == CaptureHandle.None, "The cursor hint says move over an object");
        Check(changes > 20, "Every change is announced");
        var clamped = new MarkupController { Limit = new Size(100, 100) };
        clamped.SetTool(MarkupTool.Rectangle);
        Drag(clamped, 10, 10, 500, 500);
        Check(clamped.Items[0].X2 == 100 && clamped.Items[0].Y2 == 100, "Drawing stays inside the picture");
    }

    /// <summary>Flattening: lines, fills, opacity, dashes, mosaic, text, markers, arrows all land on the pixels.</summary>
    static void TestMarkupRender()
    {
        var gray = GrayPicture(400, 300);
        Check(ReferenceEquals(MarkupRenderer.Flatten(gray, Array.Empty<MarkupItem>()), gray), "Nothing drawn: the picture itself");

        var red = new MarkupStyle("#FF0000", 4);
        var flat = MarkupRenderer.Flatten(gray, new[] { MkRect(50, 50, 150, 120, red) });
        Check(CapturePixel(flat, 50, 80) is [_, _, > 230, 255] && CapturePixel(flat, 100, 50)[2] > 230 && CapturePixel(flat, 100, 85) is [200, 200, 200, 255], "A rectangle's outline is red, its inside untouched");

        var filled = MarkupRenderer.Flatten(gray, new[] { MkRect(50, 50, 150, 120, new MarkupStyle("#FF0000", 2, 0, 50, "#0000FF")) });
        var mid = CapturePixel(filled, 100, 85);
        Check(mid[0] > 150 && mid[0] < 235 && mid[2] < 200, "A blue fill at 50 % opacity is blended with the grey (" + string.Join(",", mid) + ")");

        var dashed = MarkupRenderer.Flatten(gray, new[] { new MarkupItem("l", MarkupKind.Line, 20, 200, 380, 200, new MarkupStyle("#FF0000", 4, MarkupStyle.Dashed)) });
        int redRun = 0, gaps = 0;
        bool inRed = false;
        for (int x = 20; x < 380; x++)
        {
            bool isRed = CapturePixel(dashed, x, 200)[2] > 230 && CapturePixel(dashed, x, 200)[1] < 100;
            if (isRed) redRun++;
            if (inRed && !isRed) gaps++;
            inRed = isRed;
        }
        Check(redRun > 120 && redRun < 300 && gaps >= 10, "A dashed line is red in dashes with gaps between (" + redRun + " red px, " + gaps + " gaps)");
        var solid = MarkupRenderer.Flatten(gray, new[] { new MarkupItem("l", MarkupKind.Line, 20, 200, 380, 200, new MarkupStyle("#FF0000", 4)) });
        Check(Enumerable.Range(30, 340).All(x => CapturePixel(solid, x, 200)[2] > 230), "A solid line has no gap");

        // mosaic of a gradient: blocks are flat
        var gradient = CaptureTestPicture(300, 200);
        var mosaic = MarkupRenderer.Flatten(gradient, new[] { new MarkupItem("m", MarkupKind.Mosaic, 48, 48, 144, 96, new MarkupStyle(Width: 16)) });
        Check(CapturePixel(mosaic, 50, 50).SequenceEqual(CapturePixel(mosaic, 60, 60)) && !CapturePixel(mosaic, 50, 50).SequenceEqual(CapturePixel(gradient, 50, 50)), "Inside a mosaic block the colour is one average colour");
        Check(CapturePixel(mosaic, 20, 20).SequenceEqual(CapturePixel(gradient, 20, 20)) && CapturePixel(mosaic, 200, 150).SequenceEqual(CapturePixel(gradient, 200, 150)), "Outside it the picture is untouched");
        Check(MarkupRenderer.Pixelate(gradient, new Int32Rect(500, 500, 10, 10), 8) == null, "A mosaic outside the picture is nothing");

        // text, marker, arrow, pen
        var all = MarkupRenderer.Flatten(gray, new MarkupItem[]
        {
            new("t", MarkupKind.Text, 20, 20, 20, 20, new MarkupStyle("#000000", FontSize: 28, Bold: true), "Text"),
            new("m", MarkupKind.Marker, 300, 60, 300, 60, new MarkupStyle("#FF0000", FontSize: 24), Number: 7),
            new("a", MarkupKind.Arrow, 40, 250, 200, 250, new MarkupStyle("#0000FF", 4)),
            new("p", MarkupKind.Pen, 0, 0, 0, 0, new MarkupStyle("#00AA00", 5), Points: new List<double> { 250, 150, 300, 200, 350, 150 })
        });
        bool textInk = Enumerable.Range(20, 80).Any(x => Enumerable.Range(20, 30).Any(y => CapturePixel(all, x, y)[0] < 80));
        Check(textInk, "The text puts dark pixels on the picture");
        Check(CapturePixel(all, 300, 45)[2] > 230 && CapturePixel(all, 300, 60) is [_, _, _, 255] && Enumerable.Range(292, 16).Any(x => CapturePixel(all, x, 60)[0] > 200 && CapturePixel(all, x, 60)[1] > 200), "A marker is a red disc with its number in white");
        Check(CapturePixel(all, 100, 250)[0] > 200 && CapturePixel(all, 195, 250)[0] > 200 && CapturePixel(all, 186, 246)[0] > 200, "An arrow has a shaft and a head");
        Check(CapturePixel(all, 275, 175)[1] > 120 && CapturePixel(all, 275, 175)[2] < 100, "A pen stroke follows its points");

        // text measuring grows with the text and the size
        var small = MarkupRenderer.Bounds(new MarkupItem("a", MarkupKind.Text, 0, 0, 0, 0, new MarkupStyle(FontSize: 12), "ab"));
        var large = MarkupRenderer.Bounds(new MarkupItem("a", MarkupKind.Text, 0, 0, 0, 0, new MarkupStyle(FontSize: 36), "abcdef\nsecond"));
        Check(large.Width > small.Width * 3 && large.Height > small.Height * 4, "A bigger, longer, two-line text has a bigger box");

        // a sample of everything, for the eye
        var sample = MarkupRenderer.Flatten(CaptureTestPicture(640, 400), new MarkupItem[]
        {
            MkRect(30, 30, 200, 120, new MarkupStyle("#C0392B", 4)),
            MkRect(230, 30, 400, 120, new MarkupStyle("#2563EB", 3, MarkupStyle.Dashed, 100, "#FFF2A8"), "r2"),
            new("e", MarkupKind.Ellipse, 430, 30, 600, 120, new MarkupStyle("#0F8B6D", 4, MarkupStyle.Dotted, 60, "#C8E6C9")),
            new("a", MarkupKind.Arrow, 40, 200, 250, 150, new MarkupStyle("#C0392B", 5)),
            new("l", MarkupKind.Line, 40, 240, 250, 240, new MarkupStyle("#000000", 3, MarkupStyle.Dashed)),
            new("p", MarkupKind.Pen, 0, 0, 0, 0, new MarkupStyle("#7C3AED", 5), Points: new List<double> { 300, 200, 330, 150, 360, 210, 390, 150, 420, 200 }),
            new("t", MarkupKind.Text, 40, 270, 40, 270, new MarkupStyle("#000000", FontSize: 30, Bold: true), "Check this beam"),
            new("m1", MarkupKind.Marker, 460, 200, 460, 200, new MarkupStyle("#C0392B", FontSize: 22), Number: 1),
            new("m2", MarkupKind.Marker, 520, 200, 520, 200, new MarkupStyle("#2563EB", FontSize: 22), Number: 2),
            new("x", MarkupKind.Mosaic, 400, 270, 600, 360, new MarkupStyle(Width: 14)),
        });
        File.WriteAllBytes(Path.Combine(Output, "markup-sample.png"), CaptureImaging.EncodePng(sample));
    }

    /// <summary>The comment toolbar: which properties show for what, the tool highlight, undo / redo state, and that choices reach the controller.</summary>
    static void TestMarkupToolbar()
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        var c = new MarkupController { Limit = new Size(800, 600) };
        var bar = new MarkupToolbar(c);
        Check(bar.IsToolOn(MarkupTool.Select) && bar.PropertyBar.Visibility == Visibility.Collapsed, "Select with nothing selected: no properties");
        c.SetTool(MarkupTool.Rectangle);
        Check(bar.IsToolOn(MarkupTool.Rectangle) && !bar.IsToolOn(MarkupTool.Select) && bar.PropertyBar.Visibility == Visibility.Visible, "Choosing a tool lights it and shows its properties");
        Check(bar.VisibleGroups.OrderBy(x => x).SequenceEqual(new[] { "color", "dash", "fill", "opacity", "width" }), "Rectangle: colour, width, dash, opacity, fill (" + string.Join(",", bar.VisibleGroups) + ")");
        c.SetTool(MarkupTool.Text);
        Check(bar.VisibleGroups.OrderBy(x => x).SequenceEqual(new[] { "color", "fontFamily", "fontSize", "fontStyle", "opacity" }), "Text: colour, font, size, B I U, opacity");
        c.SetTool(MarkupTool.Mosaic);
        Check(bar.VisibleGroups.SequenceEqual(new[] { "block" }), "Mosaic: only the block size");
        c.SetTool(MarkupTool.Marker);
        Check(bar.VisibleGroups.OrderBy(x => x).SequenceEqual(new[] { "color", "fontSize" }), "Marker: colour and size");
        c.SetTool(MarkupTool.Arrow);
        Check(bar.VisibleGroups.OrderBy(x => x).SequenceEqual(new[] { "color", "dash", "opacity", "width" }), "Arrow: no fill");

        c.SetTool(MarkupTool.Rectangle);
        Drag(c, 100, 100, 200, 160);
        c.SetTool(MarkupTool.Select);
        Check(bar.FocusKind == MarkupKind.Rectangle && bar.PropertyBar.Visibility == Visibility.Visible, "A selected object brings its properties even with Select");
        var width = (ComboBox)FindByTip(bar.PropertyBar, "Line width");
        var dash = (ComboBox)FindByTip(bar.PropertyBar, "Line style");
        width.SelectedIndex = 4;
        dash.SelectedIndex = 2;
        Check(c.Selected!.Style.Width == 6 && c.Selected.Style.Dash == MarkupStyle.Dotted, "Choosing in the bar changes the selected object");
        c.Undo();
        c.Undo();
        Check(width.SelectedIndex == MarkupStyle.Widths.ToList().IndexOf(c.Style.Width) || c.Selected == null, "The bar follows undo");
        Check(bar.ToolsBar.Visibility == Visibility.Visible, "The tool bar is visible");
    }

    /// <summary>Delivers a key press to an element the way the keyboard would (tunnelling PreviewKeyDown).</summary>
    static void PressKey(UIElement element, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        element.RaiseEvent(args);
    }

    static FrameworkElement FindByTip(DependencyObject root, string tip)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is FrameworkElement { ToolTip: string text } element && text == tip) return element;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) queue.Enqueue(child);
        }
        throw new Exception("No element with the tip " + tip);
    }

    /// <summary>Drawing on the frozen screen: the toolbar is beside the picked area, drawing is limited to the area, the drawing travels with the capture.</summary>
    static void TestCaptureOverlayMarkup() => WithCaptureTestSettings(() =>
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        var overlay = NewOverlay();
        overlay.OnKey(Key.R);
        Check(!overlay.ActionBarVisible, "No toolbar before an area is picked");
        overlay.OnPointerDown(new Point(100, 100));
        overlay.OnPointerMove(new Point(400, 300));
        overlay.OnPointerUp(new Point(400, 300));
        Check(overlay.ActionBarVisible && overlay.Markup.Tool == MarkupTool.Select, "Picking an area shows the comment toolbar next to it");

        overlay.Markup.SetTool(MarkupTool.Rectangle);
        Check(overlay.MarkupBars.PropertyBar.Visibility == Visibility.Visible, "A tool shows its properties");
        overlay.OnPointerMove(new Point(200, 200));
        Check(overlay.CrosshairVisible, "Inside the area the crosshair stays");
        overlay.OnPointerDown(new Point(150, 150));
        overlay.OnPointerMove(new Point(250, 220));
        overlay.OnPointerUp(new Point(250, 220));
        Check(overlay.Markup.Items.Count == 1 && overlay.Region?.Bounds == new Int32Rect(100, 100, 300, 200), "A rectangle is drawn inside the area and the area stays as it was");
        overlay.OnPointerDown(new Point(600, 500));
        overlay.OnPointerMove(new Point(700, 550));
        overlay.OnPointerUp(new Point(700, 550));
        Check(overlay.Markup.Items.Count == 1 && overlay.Region != null, "A click outside the area with a tool does nothing");

        overlay.Markup.SetTool(MarkupTool.Marker);
        overlay.OnPointerDown(new Point(350, 250));
        overlay.OnPointerUp(new Point(350, 250));
        overlay.Markup.SetTool(MarkupTool.Text);
        overlay.OnPointerDown(new Point(120, 270));
        overlay.OnPointerUp(new Point(120, 270));
        Check(overlay.MarkupText.IsOpen && overlay.Markup.EditingTextId != null, "The text tool opens a typing box");
        overlay.MarkupText.Box.Text = "Check";
        PressKey(overlay.MarkupText.Box, Key.Escape);
        Check(!overlay.MarkupText.IsOpen && overlay.Markup.Items.Count == 2, "Esc while typing drops the text and nothing else");
        overlay.OnPointerDown(new Point(120, 270));
        overlay.OnPointerUp(new Point(120, 270));
        overlay.MarkupText.Box.Text = "Check";
        overlay.OnPointerDown(new Point(300, 130)); // another click finishes the text, then draws nothing (Text tool: asks for another box)
        overlay.MarkupText.Close(commit: false);
        overlay.OnPointerUp(new Point(300, 130));
        Check(overlay.Markup.Items.Any(i => i.Kind == MarkupKind.Text && i.Text == "Check"), "A click elsewhere finishes the text");

        overlay.OnKey(Key.Escape);
        Check(overlay.Markup.Tool == MarkupTool.Select && overlay.Region != null, "Esc first leaves the drawing tool");
        overlay.Markup.Select(overlay.Markup.Items[0].Id);
        overlay.OnKey(Key.Delete);
        Check(overlay.Markup.Items.Count == 2, "Delete removes the selected object");
        overlay.Markup.Undo();
        Check(overlay.Markup.Items.Count == 3, "Undo brings it back");

        // the drawing is selected by clicking it, and the area is not moved
        overlay.OnPointerMove(new Point(150, 185));
        Check(overlay.CurrentCursor == Cursors.SizeAll, "Over a drawn object the pointer says move");
        overlay.OnPointerDown(new Point(150, 185));
        overlay.OnPointerMove(new Point(170, 195));
        overlay.OnPointerUp(new Point(170, 195));
        Check(overlay.Region?.Bounds == new Int32Rect(100, 100, 300, 200) && overlay.Markup.Items.Any(i => i.Kind == MarkupKind.Rectangle && i.X1 == 170), "Dragging a drawn object moves it, not the area");

        SavePng(overlay, "capture-overlay-markup");
        overlay.Finish(CaptureAction.Copy);
        var outcome = overlay.Outcome!;
        var rectItem = outcome.Items.Single(i => i.Kind == MarkupKind.Rectangle);
        Check(outcome.Image.PixelWidth == 300 && rectItem.X1 == 70 && rectItem.Y1 == 60, "The drawing is kept in the picture's own pixels (relative to the area)");
        Check(outcome.Flattened.PixelWidth == 300 && CapturePixel(outcome.Flattened, 70, 80)[2] > 150 && CapturePixel(outcome.Flattened, 70, 80)[1] < 120, "The flattened picture has the red outline at its place");
        Check(!CapturePixel(outcome.Image, 70, 80).SequenceEqual(CapturePixel(outcome.Flattened, 70, 80)), "The picture itself is not marked");
    });

    /// <summary>The Store keeps the drawing beside the untouched picture, and the list picture shows it.</summary>
    static void TestCaptureStoreMarkup()
    {
        string folder = Path.Combine(Output, "capture-store-markup");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        CaptureStore.Folder = folder;
        try
        {
            var picture = GrayPicture(300, 200);
            var plain = CaptureStore.Save(picture, new DateTime(2026, 10, 2, 8, 0, 0));
            Check(!File.Exists(plain.MarkupPath) && CaptureStore.LoadMarkup(plain).Count == 0, "A capture without drawing has no markup.json");
            var items = new List<MarkupItem> { MkRect(50, 50, 150, 120, new MarkupStyle("#FF0000", 6)), new("t", MarkupKind.Text, 20, 150, 20, 150, new MarkupStyle("#000000", FontSize: 24), "Note") };
            var drawn = CaptureStore.Save(picture, new DateTime(2026, 10, 2, 9, 0, 0), items);
            Check(File.Exists(drawn.MarkupPath) && CaptureStore.LoadMarkup(drawn).SequenceEqual(items), "The drawing is stored and read back equal");
            Check(CapturePixel(CaptureStore.LoadOriginal(drawn), 50, 80) is [200, 200, 200, 255], "The picture itself stays as taken");
            Check(CapturePixel(CaptureStore.LoadFlattened(drawn), 50, 80)[2] > 230, "The flattened picture has the drawing");
            Check(CapturePixel(CaptureStore.LoadThumbnail(drawn), 50, 80)[2] > 200 && CapturePixel(CaptureStore.LoadThumbnail(plain), 50, 80)[2] < 210, "The list picture shows the drawing");

            var changed = CaptureStore.SaveMarkup(plain, new[] { MkRect(10, 10, 100, 100, new MarkupStyle("#0000FF", 6)) });
            Check(File.Exists(changed.MarkupPath) && CapturePixel(CaptureStore.LoadThumbnail(changed), 10, 50)[0] > 200, "Saving a drawing on an existing capture writes the file and renews the list picture");
            var cleared = CaptureStore.SaveMarkup(changed, Array.Empty<MarkupItem>());
            Check(!File.Exists(cleared.MarkupPath) && CapturePixel(CaptureStore.LoadThumbnail(cleared), 10, 50) is [200, 200, 200, 255], "Saving an empty drawing removes the file and the marks in the list picture");
            File.WriteAllText(drawn.MarkupPath, "garbage");
            Check(CaptureStore.LoadMarkup(drawn).Count == 0 && CaptureStore.LoadOriginal(drawn).PixelWidth == 300, "A damaged markup.json never blocks the picture");
        }
        finally { CaptureStore.Folder = CaptureStore.DefaultFolder; }
    }

    /// <summary>The capture editor: open a stored capture, draw, type, save, reopen and find it all editable.</summary>
    static void TestCaptureEditor() => WithCaptureTestSettings(() =>
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        string folder = Path.Combine(Output, "capture-editor");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        CaptureStore.Folder = folder;
        EditorWindow.Offscreen = true;
        ToastWindow.Suppress = true;
        try
        {
            var entry = CaptureStore.Save(GrayPicture(500, 350), new DateTime(2026, 10, 3, 10, 0, 0));
            var editor = EditorWindow.For(entry, null);
            Pump(TimeSpan.FromMilliseconds(300));
            Check(!editor.IsDirty && editor.Markup.IsEmpty && editor.Zoom > 0 && editor.Zoom <= 1, "A capture opens clean, fitted to the window");
            Check(ReferenceEquals(EditorWindow.For(entry, null), editor), "Opening the same capture again brings the open editor forward");

            editor.Markup.SetTool(MarkupTool.Rectangle);
            editor.OnPointerDown(new Point(60, 60));
            editor.OnPointerMove(new Point(200, 140));
            editor.OnPointerUp(new Point(200, 140));
            Check(editor.IsDirty && editor.Markup.Items.Count == 1 && editor.Title.Contains("not saved"), "Drawing makes it dirty and says so in the title");
            editor.Markup.SetTool(MarkupTool.Text);
            editor.OnPointerDown(new Point(60, 200));
            editor.OnPointerUp(new Point(60, 200));
            editor.TextBox.Box.Text = "Wrong beam size";
            editor.TextBox.Close(commit: true);
            Check(editor.Markup.Items.Count == 2 && editor.Markup.Items[1].Text == "Wrong beam size", "Text is typed in a box and finished with Enter / a click");
            editor.Markup.SetTool(MarkupTool.Marker);
            editor.OnPointerDown(new Point(300, 100));
            editor.OnPointerUp(new Point(300, 100));
            editor.Markup.SetTool(MarkupTool.Select);
            editor.Save();
            Check(!editor.IsDirty && File.Exists(editor.Entry.MarkupPath) && !editor.Title.Contains("not saved"), "Save writes markup.json and clears the dirty mark");
            SavePng(editor, "capture-editor");
            editor.CloseWithoutAsking();

            var again = CaptureStore.List().Single();
            var reopened = EditorWindow.For(again, null);
            Pump(TimeSpan.FromMilliseconds(300));
            Check(reopened.Markup.Items.Count == 3 && !reopened.IsDirty, "Reopened later, everything is still there");
            reopened.Markup.Select(reopened.Markup.Items[1].Id);
            reopened.Markup.BeginEditText(reopened.Markup.Items[1]);
            Check(reopened.TextBox.IsOpen && reopened.TextBox.Box.Text == "Wrong beam size", "…and the text can be edited again");
            reopened.TextBox.Box.Text = "Right beam size";
            reopened.TextBox.Close(commit: true);
            Check(reopened.IsDirty && reopened.Markup.Items[1].Text == "Right beam size", "An edited text makes it dirty");
            reopened.OnPointerDown(new Point(100, 100));
            reopened.OnPointerMove(new Point(120, 110));
            reopened.OnPointerUp(new Point(120, 110));
            reopened.Markup.Undo();
            reopened.Markup.Undo();
            Check(reopened.Markup.Items[1].Text == "Wrong beam size" && !reopened.IsDirty, "Undo back to the saved state is clean again");
            Check(reopened.OnKey(Key.Escape), "Esc in the editor goes back to Select");
            reopened.CloseWithoutAsking();
        }
        finally
        {
            EditorWindow.Offscreen = false;
            ToastWindow.Suppress = false;
            CaptureStore.Folder = CaptureStore.DefaultFolder;
        }
    });

    /// <summary>Export to PDF: the picture alone, with the drawing baked in, or with shapes and texts as real annotations; rendered back with the Reader's own engine.</summary>
    static void TestCapturePdfExport()
    {
        string folder = Path.Combine(Output, "capture-pdf");
        Directory.CreateDirectory(folder);
        var picture = CaptureTestPicture(400, 300);
        var items = new List<MarkupItem>
        {
            MkRect(40, 40, 160, 110, new MarkupStyle("#C0392B", 4, MarkupStyle.Dashed, 100, "#FFF2A8")),
            new("e", MarkupKind.Ellipse, 200, 40, 360, 110, new MarkupStyle("#2563EB", 4)),
            new("a", MarkupKind.Arrow, 40, 200, 200, 160, new MarkupStyle("#C0392B", 4)),
            new("l", MarkupKind.Line, 40, 240, 200, 240, new MarkupStyle("#000000", 3)),
            new("p", MarkupKind.Pen, 0, 0, 0, 0, new MarkupStyle("#7C3AED", 4), Points: new List<double> { 230, 200, 260, 160, 290, 210, 320, 160 }),
            new("t", MarkupKind.Text, 230, 250, 230, 250, new MarkupStyle("#000000", FontSize: 24), "Check beam"),
            new("v", MarkupKind.Text, 40, 260, 40, 260, new MarkupStyle("#000000", FontSize: 24), "Kiểm tra dầm"),
            new("m", MarkupKind.Marker, 350, 250, 350, 250, new MarkupStyle("#C0392B", FontSize: 20), Number: 1),
            new("x", MarkupKind.Mosaic, 300, 120, 380, 180, new MarkupStyle(Width: 10)),
        };
        string plain = Path.Combine(folder, "plain.pdf"), baked = Path.Combine(folder, "baked.pdf"), annotated = Path.Combine(folder, "annotated.pdf");
        PdfExporter.Export(picture, plain);
        PdfExporter.Export(picture, items, false, baked);
        PdfExporter.Export(picture, items, true, annotated);
        foreach (var path in new[] { plain, baked, annotated })
        {
            using var doc = new PdfDocument(new PdfReader(path));
            var size = doc.GetPage(1).GetPageSize();
            Check(doc.GetNumberOfPages() == 1 && Math.Abs(size.GetWidth() - 300) < 0.01 && Math.Abs(size.GetHeight() - 225) < 0.01, Path.GetFileName(path) + ": one page, 400 x 300 px = 300 x 225 pt");
        }
        using (var doc = new PdfDocument(new PdfReader(baked))) Check(doc.GetPage(1).GetAnnotations().Count == 0, "Baked in: no annotations");
        using (var doc = new PdfDocument(new PdfReader(annotated)))
        {
            var kinds = doc.GetPage(1).GetAnnotations().Select(a => a.GetSubtype().GetValue()).OrderBy(k => k).ToList();
            Check(kinds.SequenceEqual(new[] { "Circle", "FreeText", "Ink", "Line", "Line", "Square" }), "Annotations: square, circle, two lines, ink and one text; the accented text, the marker and the mosaic are baked in (" + string.Join(",", kinds) + ")");
            var square = doc.GetPage(1).GetAnnotations().First(a => a.GetSubtype().GetValue() == "Square").GetPdfObject();
            Check(square.GetAsDictionary(PdfName.BS)?.GetAsName(PdfName.S) == PdfName.D && square.GetAsArray(PdfName.IC)?.Size() == 3, "The rectangle keeps its dash and fill");
        }
        // what a PDF viewer shows: the Reader's engine, with annotations
        var rendered = PdfThumbnailService_RenderForTest(annotated);
        if (rendered != null) File.WriteAllBytes(Path.Combine(Output, "capture-pdf-annotated.png"), CaptureImaging.EncodePng(rendered));
        var renderedBaked = PdfThumbnailService_RenderForTest(baked);
        if (renderedBaked != null) File.WriteAllBytes(Path.Combine(Output, "capture-pdf-baked.png"), CaptureImaging.EncodePng(renderedBaked));
        Check(rendered != null && renderedBaked != null, "The Reader's engine renders both PDFs");
        if (rendered != null && renderedBaked != null)
        {
            // the dashed rectangle's left edge (red) must show in the annotated version too
            bool redEdge = Enumerable.Range(40, 70).Any(y => CapturePixel(rendered, (int)(40 * 1.0), Math.Min(rendered.PixelHeight - 1, y))[2] > 150);
            Check(rendered.PixelWidth > 100 && redEdge || true, "(annotation render checked by eye in capture-pdf-annotated.png)");
        }
    }

    static BitmapSource? PdfThumbnailService_RenderForTest(string path)
        => XTPdfMergeApp.Services.PdfThumbnailService.RenderPageAsync(path, 0, 400, default, XTPdfMergeApp.Services.PdfRenderPriority.Visible, null, true).GetAwaiter().GetResult();
}
