using System.IO;
using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Services.TextEdit;

internal static partial class Program
{
    /// <summary>A PDF with a real text layer (Vietnamese letters, an embedded font, a drawn line) and a scan-like page without text.</summary>
    static string BuildTextEditPdf(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "text.pdf");
        using (var doc = new PdfDocument(new PdfWriter(path)))
        {
            var font = iText.Kernel.Font.PdfFontFactory.CreateFont(@"C:\Windows\Fonts\arial.ttf", iText.IO.Font.PdfEncodings.IDENTITY_H, iText.Kernel.Font.PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
            var page = doc.AddNewPage(PageSize.A4);
            var canvas = new PdfCanvas(page);
            canvas.BeginText().SetFontAndSize(font, 14).SetFillColor(new DeviceRgb(0, 0, 255)).MoveText(72, 700).ShowText("Số hiệu: BV-01").EndText();
            canvas.BeginText().SetFontAndSize(font, 12).SetFillColor(ColorConstants.BLACK).MoveText(72, 660).ShowText("Thiết kế đường lên núi").EndText();
            canvas.SetStrokeColor(ColorConstants.BLACK).MoveTo(72, 640).LineTo(400, 640).Stroke();
            doc.AddNewPage(PageSize.A4); // empty: no text layer
        }
        return path;
    }

    /// <summary>Edit Text end to end: the runs of a page, a pending edit (drawn, undone, redone), Find sees the new text, Ctrl+S removes the old characters and writes the new ones.</summary>
    static void TestTextEditPendingUntilSave()
    {
        if (!TextEditService.IsAvailable) { Console.WriteLine("Text edit: no embedded Python in this build; skipped"); return; }
        string source = BuildTextEditPdf(System.IO.Path.Combine(Output, "textedit-source"));

        RunReaderFlow("textedit", async f =>
        {
            string path = f.Path;
            var host = (XTPdfMergeApp.IReaderPageEditHost)f.Window.Session;
            var group = f.Window.Session.Documents[0];
            string before = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

            var page = await TextEditService.GetRunsAsync(path, 1);
            Check(page != null && page.Runs.Count == 2 && page.Rotation == 0 && Math.Abs(page.Width - 595) < 2, $"Page 1 has {page?.Runs.Count} text runs");
            var run = page!.Runs.First(r => r.Text.Contains("BV-01"));
            Check(run.Text == "Số hiệu: BV-01" && Math.Abs(run.Size - 14) < 0.1 && run.Color == 0x0000FF, $"The run keeps its text, size and colour (\"{run.Text}\", {run.Size}, {run.Color:X6})");
            Check(((await TextEditService.GetRunsAsync(path, 2))?.Runs.Count ?? -1) == 0, "A page without a text layer has no runs");

            var edit = new TextEdit(1, page.Width, page.Height, run, "Số hiệu: BV-02 (đã sửa)");
            await host.ApplyTextEditAsync(path, 1, run, edit, "Text edited");
            await Task.Delay(300);
            Check(TextEditPendingStore.HasPending(path) && group.IsDirty, "The edit is pending and the tab shows the unsaved mark");
            Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) == before, "The file on disk is not touched");

            var hits = new List<SearchHit>();
            await PdfThumbnailService.SearchAsync(path, "BV-02", false, false, (batch, _) => hits.AddRange(batch));
            Check(hits.Count == 1 && hits[0].Path == path, "Find finds the new text (in the working copy, reported on the file's own path)");
            var oldHits = new List<SearchHit>();
            await PdfThumbnailService.SearchAsync(path, "BV-01", false, false, (batch, _) => oldHits.AddRange(batch));
            Check(oldHits.Count == 0, "…and no longer the old text");
            var other = new List<SearchHit>();
            await PdfThumbnailService.SearchAsync(path, "lên núi", false, false, (batch, _) => other.AddRange(batch));
            Check(other.Count == 1, "The text next to it is untouched");

            host.Undo();
            await Task.Delay(300);
            Check(!TextEditPendingStore.HasPending(path) && !group.IsDirty, "Undo takes the edit away");
            host.Redo();
            await Task.Delay(300);
            Check(TextEditPendingStore.HasPending(path) && group.IsDirty, "Redo brings it back");

            bool saved = await host.SaveGroupAsync(group, saveAs: false);
            await Task.Delay(500);
            Check(saved && !TextEditPendingStore.HasPending(path) && !group.IsDirty, "Save writes it and clears the unsaved mark");
            using (var doc = new PdfDocument(new PdfReader(path)))
                Check(doc.GetNumberOfPages() == 2, "The pages are as they were");
            var text = new List<SearchHit>();
            await PdfThumbnailService.SearchAsync(path, "BV-02", false, false, (batch, _) => text.AddRange(batch));
            var gone = new List<SearchHit>();
            await PdfThumbnailService.SearchAsync(path, "BV-01", false, false, (batch, _) => gone.AddRange(batch));
            Check(text.Count == 1 && gone.Count == 0, "The saved file has the new text and not the old");
            var saved1 = await TextEditService.GetRunsAsync(path, 1);
            var again = saved1!.Runs.First(r => r.Text.Contains("BV-02"));
            Check(Math.Abs(again.Size - 14) < 0.1 && again.Color == 0x0000FF && Math.Abs(again.OriginY - run.OriginY) < 0.5 && Math.Abs(again.OriginX - run.OriginX) < 0.5, "The new text has the old size, colour and place");
            Check(saved1.Runs.Any(r => r.Text == "Thiết kế đường lên núi"), "The other text is intact");
            Check(XTHistory.Read(path).Any(h => h.Action.Contains("1 text edit")), "The file's history has a line for the edit");
        }, copyFrom: source);
    }

    /// <summary>Edit Text in the window: the ribbon button arms the tool, a click on text opens the box, Enter keeps the edit (shown on the page), a scan page gets a message instead.</summary>
    static void TestEditTextTool()
    {
        if (!TextEditService.IsAvailable) { Console.WriteLine("Edit Text tool: no embedded Python; skipped"); return; }
        string source = BuildTextEditPdf(System.IO.Path.Combine(Output, "edittool-source"));
        RunReaderFlow("edittool", async f =>
        {
            var button = f.Find<XTStyle.Controls.XTButton>("ReaderEditTextToolButton");
            Check(button != null && button.Text == "Edit Text", "The ribbon has an Edit Text button");
            button!.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(Equals(button.Tag, "Active"), "The button arms the tool");

            // the first line sits at 72, 700 (PDF) = 72/595, 1 - 700/842 of the page; click in the middle of "Số hiệu: BV-01"
            var hit = f.Hit(110.0 / 595, (842 - 703.0) / 842);
            await (Task)f.Call("BeginTextEditAsync", hit)!;
            f.Window.UpdateLayout();
            var layer = f.Find<System.Windows.Controls.Canvas>("ReaderInteractionLayer");
            var box = layer.Children.OfType<System.Windows.Controls.TextBox>().LastOrDefault();
            Check(box != null && box.Text == "Số hiệu: BV-01", $"A click on the text opens its box with the text (\"{box?.Text}\")");
            SavePng(f.Window, "edittool-box");
            box!.Text = "Số hiệu: KC-07";
            f.Call("CommitTextEdit");
            await Task.Delay(500);
            f.Window.UpdateLayout();
            Check(TextEditPendingStore.HasPending(f.Path) && TextEditPendingStore.Page(f.Path, 1)[0].NewText == "Số hiệu: KC-07", "Enter keeps the edit as a pending change");
            Check(!layer.Children.Contains(box), "The box is gone");
            SavePng(f.Window, "edittool-after");

            // typing the original text again takes the edit away
            await (Task)f.Call("BeginTextEditAsync", hit)!;
            var again = layer.Children.OfType<System.Windows.Controls.TextBox>().LastOrDefault();
            Check(again != null && again.Text == "Số hiệu: KC-07", "A second click shows the edited text");
            again!.Text = "Số hiệu: BV-01";
            f.Call("CommitTextEdit");
            await Task.Delay(300);
            Check(!TextEditPendingStore.HasPending(f.Path), "Typing the original text back leaves no edit");
        }, copyFrom: source);
    }

    /// <summary>A drawing: a frame, two lines inside it, a picture and a line of text.</summary>
    static string BuildObjectPdf(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "drawing.pdf");
        using (var doc = new PdfDocument(new PdfWriter(path)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            var canvas = new PdfCanvas(page);
            canvas.SetStrokeColor(ColorConstants.BLACK).SetLineWidth(1).Rectangle(100, 542, 300, 200).Stroke();      // frame: top 842-100=742..., y down 100..300
            canvas.SetStrokeColor(ColorConstants.RED).SetLineWidth(2).MoveTo(150, 692).LineTo(350, 692).Stroke();      // y down 150
            canvas.SetStrokeColor(ColorConstants.BLUE).SetLineWidth(1).MoveTo(150, 642).LineTo(350, 592).Stroke();     // diagonal
            var font = iText.Kernel.Font.PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA);
            canvas.BeginText().SetFontAndSize(font, 12).MoveText(160, 562).ShowText("inside text").EndText();
            var pixels = new byte[40 * 40 * 3];
            for (int i = 0; i < pixels.Length; i += 3) { pixels[i] = 0; pixels[i + 1] = 200; pixels[i + 2] = 0; }
            var image = new System.Windows.Media.Imaging.WriteableBitmap(40, 40, 96, 96, System.Windows.Media.PixelFormats.Rgb24, null);
            image.WritePixels(new System.Windows.Int32Rect(0, 0, 40, 40), pixels, 120, 0);
            using var png = new MemoryStream();
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image)); encoder.Save(png);
            canvas.AddImageFittedIntoRectangle(iText.IO.Image.ImageDataFactory.Create(png.ToArray()), new Rectangle(450, 672, 70, 70), false);   // y down 100..170
        }
        return path;
    }

    /// <summary>Edit Object end to end: pick a line, a frame edge (what it frames stays) and an image, remove them, Undo puts the file back byte for byte, Redo removes again.</summary>
    static void TestEditObject()
    {
        if (!TextEditService.IsAvailable) { Console.WriteLine("Edit Object: no embedded Python in this build; skipped"); return; }
        string source = BuildObjectPdf(System.IO.Path.Combine(Output, "editobject-source"));
        RunReaderFlow("editobject", async f =>
        {
            string path = f.Path;
            var host = (XTPdfMergeApp.IReaderPageEditHost)f.Window.Session;
            string Hash() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            string before = Hash();

            var onLine = await ObjectEditService.PickAsync(path, 1, 250, 150);
            Check(onLine.Count == 1 && onLine[0].Kind == "line", $"A click on the red line picks a line ({string.Join(",", onLine.Select(o => o.Kind))})");
            Check((await ObjectEditService.PickAsync(path, 1, 250, 120)).Count == 0, "The middle of a frame picks nothing");
            var onFrame = await ObjectEditService.PickAsync(path, 1, 100, 200);
            Check(onFrame.Count == 1 && onFrame[0].Kind == "shape", "The edge of the frame picks the frame");
            var onImage = await ObjectEditService.PickAsync(path, 1, 480, 130);
            Check(onImage.Count == 1 && onImage[0].IsImage, "A click on the picture picks the image");
            var area = await ObjectEditService.PickAreaAsync(path, 1, 90, 90, 410, 310);
            Check(area.Count == 3 && area.Count(o => o.Kind == "line") == 2 && area.Any(o => o.Kind == "shape"), $"An area takes the two lines and the frame ({area.Count} objects)");

            var group = f.Window.Session.Documents[0];
            await host.ApplyObjectDeleteAsync(path, new[] { onFrame[0], onImage[0] }, "Removed 2 objects");
            await Task.Delay(300);
            Check(ObjectDeletePendingStore.Count(path) == 2 && group.IsDirty, "Removing the frame and the picture only marks them: the tab shows the unsaved mark");
            Check(Hash() == before, "The file on disk is not touched");
            host.Undo();
            await Task.Delay(300);
            Check(!ObjectDeletePendingStore.HasPending(path) && !group.IsDirty, "Undo takes the mark away");
            host.Redo();
            await Task.Delay(300);
            Check(ObjectDeletePendingStore.Count(path) == 2, "Redo marks them again");

            Check(await host.SaveGroupAsync(group, saveAs: false), "Save removes them from the file");
            await Task.Delay(600);
            Check(!ObjectDeletePendingStore.HasPending(path) && !group.IsDirty, "…and clears the mark");
            Check(Hash() != before, "The file changed");
            var left = await ObjectEditService.PickAreaAsync(path, 1, 0, 0, 595, 842);
            Check(left.Count == 2 && left.All(o => o.Kind == "line"), $"Only the two lines are left ({left.Count}); what the frame framed stays");
            var runs = await TextEditService.GetRunsAsync(path, 1);
            Check(runs!.Runs.Count == 1 && runs.Runs[0].Text == "inside text", "The text inside is untouched");
            Check(XTHistory.Read(path).Any(h => h.Action.Contains("2 objects removed")), "The file's history has a line for the removal");
        }, copyFrom: source);
    }

    /// <summary>The Edit Object tool in the window: a click selects, Shift adds, Delete removes.</summary>
    static void TestEditObjectTool()
    {
        if (!TextEditService.IsAvailable) { Console.WriteLine("Edit Object tool: no embedded Python; skipped"); return; }
        string source = BuildObjectPdf(System.IO.Path.Combine(Output, "editobjecttool-source"));
        RunReaderFlow("editobjecttool", async f =>
        {
            var button = f.Find<XTStyle.Controls.XTButton>("ReaderEditObjectButton");
            Check(button != null && button.Text.StartsWith("Edit Object"), "The ribbon has an Edit Object button");
            f.Call("StartObjectTool", false);
            Check(Equals(button!.Tag, "Active"), "The tool is armed");
            await (Task)f.Call("BeginObjectPickAsync", f.Hit(250.0 / 595, 150.0 / 842))!;
            f.Window.UpdateLayout();
            var sel = f.Field("_objectSelection");
            Check(sel != null, "A click on the red line selects it");
            SavePng(f.Window, "editobject-selected");
            await (Task)f.Call("DeleteSelectedObjectsAsync")!;
            await Task.Delay(500);
            Check(ObjectDeletePendingStore.Count(f.Path) == 1, "Delete marks the line (painted out, removed from the file on Save)");
            f.Window.UpdateLayout();
            SavePng(f.Window, "editobject-pending");
            Check(await ((XTPdfMergeApp.IReaderPageEditHost)f.Window.Session).SaveGroupAsync(f.Window.Session.Documents[0], saveAs: false), "Save writes it");
            await Task.Delay(600);
            var left = await ObjectEditService.PickAreaAsync(f.Path, 1, 0, 0, 595, 842);
            Check(left.Count == 3 && f.Field("_objectSelection") == null, $"Delete removes it ({left.Count} objects left) and clears the selection");
        }, copyFrom: source);
    }

    /// <summary>Four pages with the sheet number in the same place; page 3 is a scan-like page without text.</summary>
    static string BuildBatchPdf(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "sheets.pdf");
        using (var doc = new PdfDocument(new PdfWriter(path)))
        {
            var font = iText.Kernel.Font.PdfFontFactory.CreateFont(@"C:\Windows\Fonts\arial.ttf", iText.IO.Font.PdfEncodings.IDENTITY_H, iText.Kernel.Font.PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
            for (int i = 1; i <= 4; i++)
            {
                var page = doc.AddNewPage(PageSize.A4);
                if (i == 3) continue;
                var canvas = new PdfCanvas(page);
                canvas.BeginText().SetFontAndSize(font, 14).MoveText(72, 700).ShowText($"Số hiệu: BV-0{i}").EndText();
                canvas.BeginText().SetFontAndSize(font, 12).MoveText(72, 400).ShowText("BV-99 outside the area").EndText();
            }
        }
        return path;
    }

    /// <summary>Find and replace in a drawn area over many pages: hits are listed, pages without text are counted, one Undo takes every replacement back.</summary>
    static void TestBatchFindInArea()
    {
        if (!TextEditService.IsAvailable) { Console.WriteLine("Batch find: no embedded Python; skipped"); return; }
        UseTestAreaStore("batchfind");
        string source = BuildBatchPdf(System.IO.Path.Combine(Output, "batchfind-source"));
        RunReaderFlow("batchfind", async f =>
        {
            string path = f.Path;
            var host = (XTPdfMergeApp.IReaderPageEditHost)f.Window.Session;
            var window = new XTPdfMergeApp.Controls.BatchFindWindow(path, 1, 4) { Left = -32000, Top = -32000, ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual };
            window.Show();
            await Task.Delay(1200);
            Check(window.Picker.Area == null, "No area is drawn at first");
            await window.FindAsync();
            Check(window.StatusText.Contains("Draw the area"), "Find without an area asks for one");

            window.Picker.DrawForTest((0.05, 0.12, 0.6, 0.22)); // the first line (y 142 of 842)
            window.FindBox.Text = "BV-";
            window.ReplaceBox.Text = "KC-";
            await window.FindAsync();
            Check(window.Hits.Count == 3 && window.Hits.Select(h => h.Page).SequenceEqual(new[] { 1, 2, 4 }), $"Three hits, pages 1, 2 and 4 ({string.Join(",", window.Hits.Select(h => h.Page))}) [{window.StatusText}]");
            Check(window.Hits.All(h => h.Text.StartsWith("Số hiệu: BV-0")) && window.Hits[0].After == "Số hiệu: KC-01", $"The list shows the text and what it becomes (\"{window.Hits.FirstOrDefault()?.After}\")");
            Check(window.StatusText.Contains("1 page has no text layer"), "The page without text is counted: " + window.StatusText);
            Check(window.Hits.All(h => !h.Text.Contains("outside")), "Text outside the area is not found");

            SavePng(window, "batchfind-window");
            window.Hits[1].IsChecked = false; // keep page 2 as it is
            window.ApplyForTest(delete: false);
            await Task.Delay(500);
            Check(window.Edits.Count == 2 && window.Edits.All(e => e.Edit != null), "Two checked hits become edits");
            await host.ApplyTextEditsAsync(path, window.Edits, window.Description);
            await Task.Delay(500);
            Check(TextEditPendingStore.All(path).Count == 2 && TextEditPendingStore.Page(path, 2).Count == 0, "Pages 1 and 4 are pending, page 2 is not");
            var hits = new List<SearchHit>();
            await PdfThumbnailService.SearchAsync(path, "KC-04", false, false, (batch, _) => hits.AddRange(batch));
            Check(hits.Count == 1 && hits[0].PageNumber == 4, "Find sees the replaced text on page 4");
            host.Undo();
            await Task.Delay(500);
            Check(!TextEditPendingStore.HasPending(path), "One Undo takes back every replacement");
            host.Redo();
            await Task.Delay(500);
            Check(TextEditPendingStore.All(path).Count == 2, "Redo brings them back");

            // delete the text instead
            window = new XTPdfMergeApp.Controls.BatchFindWindow(path, 1, 4) { Left = -32000, Top = -32000, ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual };
            window.Show();
            await Task.Delay(1000);
            window.Picker.DrawForTest((0.05, 0.12, 0.6, 0.22));
            window.RangeButton.IsChecked = true;
            window.RangeBox.Text = "2";
            await window.FindAsync();
            Check(window.Hits.Count == 1, "A page range is respected");
            window.ApplyForTest(delete: true);
            await Task.Delay(300);
            Check(window.Edits.Count == 1 && window.Edits[0].Edit!.NewText == "", "Delete empties the text");
            window.Close();
        }, copyFrom: source);
    }

    /// <summary>Areas and places go to a file of the test, never the real user's.</summary>
    static void UseTestAreaStore(string name)
    {
        AreaPresetStore.FilePath = System.IO.Path.Combine(Output, name + "-areas.json");
        try { File.Delete(AreaPresetStore.FilePath); } catch { }
    }

    /// <summary>Two A4 pages and two A3 pages, each with the sheet number at a different place; the A3 ones are drawn bigger.</summary>
    static string BuildTwoSizesPdf(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "sizes.pdf");
        using (var doc = new PdfDocument(new PdfWriter(path)))
        {
            var font = iText.Kernel.Font.PdfFontFactory.CreateFont(@"C:\Windows\Fonts\arial.ttf", iText.IO.Font.PdfEncodings.IDENTITY_H, iText.Kernel.Font.PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
            for (int i = 1; i <= 4; i++)
            {
                bool a3 = i > 2;
                var page = doc.AddNewPage(a3 ? PageSize.A3 : PageSize.A4);
                var canvas = new PdfCanvas(page);
                var (x, y) = a3 ? (100f, 1000f) : (72f, 700f);   // A3: further right and lower on the page
                canvas.BeginText().SetFontAndSize(font, a3 ? 20 : 14).MoveText(x, y).ShowText($"Số hiệu: BV-0{i}").EndText();
            }
        }
        return path;
    }

    /// <summary>Paper sizes are the base of the area tools: the pages are grouped by size, each size keeps its own area, and unticking a size leaves its pages out.</summary>
    static void TestFindAreaPerPaperSize()
    {
        if (!TextEditService.IsAvailable) { Console.WriteLine("Find area per size: no embedded Python; skipped"); return; }
        UseTestAreaStore("per-size");
        string source = BuildTwoSizesPdf(System.IO.Path.Combine(Output, "persize-source"));
        RunReaderFlow("persize", async f =>
        {
            string path = f.Path;
            var pages = await PaperSizeIndex.ReadAsync(path);
            var groups = PaperSizeIndex.Group(pages);
            Check(groups.Count == 2 && groups.Select(g => g.Key).OrderBy(k => k).SequenceEqual(new[] { "A3 portrait", "A4 portrait" }), $"Two paper sizes: {string.Join(", ", groups.Select(g => g.Label))}");

            var window = new XTPdfMergeApp.Controls.BatchFindWindow(path, 1, 4) { Left = -32000, Top = -32000, ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual };
            window.Show();
            await Task.Delay(1500);
            Check(window.Sizes.Rows.Count == 2 && window.Sizes.Selected?.Key == "A4 portrait", "The window lists both sizes and starts on the size of the current page");
            window.Picker.DrawForTest((0.05, 0.12, 0.6, 0.22));          // A4: y 142 of 842
            window.Sizes.SelectKey("A3 portrait");
            await Task.Delay(800);
            Check(window.Picker.Area == null, "Another size starts with no area of its own");
            window.Picker.DrawForTest((0.08, 0.12, 0.7, 0.2));           // A3: y 191 of 1191, x 100 of 842
            Check(AreaPresetStore.Get("find-text", "A4 portrait") is { U2: 0.6 } && AreaPresetStore.Get("find-text", "A3 portrait") is { U2: 0.7 }, "Each size remembers its own area in the store");
            window.Sizes.SelectKey("A4 portrait");
            await Task.Delay(800);
            Check(window.Picker.Area is { U2: 0.6 }, "Going back shows the A4 area again");

            window.FindBox.Text = "BV-";
            window.ReplaceBox.Text = "KC-";
            await window.FindAsync();
            Check(window.Hits.Select(h => h.Page).SequenceEqual(new[] { 1, 2, 3, 4 }), $"One Find reads both sizes with their own areas ({string.Join(",", window.Hits.Select(h => h.Page))})");
            window.Sizes.Rows.First(r => r.Key == "A3 portrait").IsChecked = false;
            await window.FindAsync();
            Check(window.Hits.Select(h => h.Page).SequenceEqual(new[] { 1, 2 }), "Unticking A3 leaves its pages out");
            SavePng(window, "findarea-window");
            window.Close();

            // a new window for the same file already knows both areas
            var again = new XTPdfMergeApp.Controls.BatchFindWindow(path, 3, 4) { Left = -32000, Top = -32000, ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual };
            again.Show();
            await Task.Delay(1500);
            Check(again.Sizes.Rows.All(r => r.HasArea) && again.Sizes.Selected?.Key == "A3 portrait" && again.Picker.Area is { U2: 0.7 }, $"A new window knows the areas of both sizes (rows {string.Join(",", again.Sizes.Rows.Select(r => r.Key + r.HasArea))}, selected {again.Sizes.Selected?.Key}, area {again.Picker.Area})");
            again.FindBox.Text = "BV-";
            await again.FindAsync();
            Check(again.Hits.Count == 4, "…and finds on all four pages without drawing again");
            again.Close();
        }, copyFrom: source);
    }

    /// <summary>The batch tools live in the reader's right-hand panel and draw their rectangle on the page being read.</summary>
    static void TestToolPanelOnReader()
    {
        UseTestAreaStore("toolpanel");
        string source = BuildTwoSizesPdf(System.IO.Path.Combine(Output, "toolpanel-source"));
        RunReaderFlow("toolpanel", async f =>
        {
            var window = f.Window;
            await Task.Delay(1200);
            f.Call("OpenStampPages");
            await Task.Delay(2500);
            Check((bool)f.Get("ToolPanelOpen")!, "The Stamp pages panel opens beside the page");
            var surface = (XTPdfMergeApp.Controls.ReaderAreaSurface)f.Get("_areaSurface")!;
            Check(surface.Active && surface.PlaceMode && surface.Area != null, "The stamp has a box on the page being read");
            Check(surface.CurrentPage == 1, "…on the page the reader shows (" + surface.CurrentPage + ")");
            SavePng(window, "toolpanel-stamp");
            f.Call("CloseToolPanel");
            Check(!(bool)f.Get("ToolPanelOpen")! && !surface.Active, "Closing the panel removes the box");
            f.Call("OpenBatchFind");
            await Task.Delay(2500);
            Check((bool)f.Get("ToolPanelOpen")! && surface.Active && !surface.PlaceMode, "Find in area opens in the same panel, drawing a free rectangle");
            surface.SetArea((0.05, 0.12, 0.6, 0.22));
            SavePng(window, "toolpanel-find");
            f.Call("CloseToolPanel");
        }, copyFrom: source);
    }

    /// <summary>Stamp pages: a stamp with an id, placed once per paper size, resizable (the corners keep its shape), put on many pages as one undoable change, with ids written into the file.</summary>
    static void TestStampManyPages()
    {
        UseTestAreaStore("stamp");
        string source = BuildTwoSizesPdf(System.IO.Path.Combine(Output, "stamppages-source"));
        RunReaderFlow("stamppages", async f =>
        {
            string path = f.Path;
            var host = (XTPdfMergeApp.IReaderPageEditHost)f.Window.Session;
            var window = new XTPdfMergeApp.Controls.StampPagesWindow(path, 1, 4) { Left = -32000, Top = -32000, ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual };
            window.Show();
            await Task.Delay(1800);
            Check(window.StampList.Items.Count >= 6 && window.StampList.SelectedItem != null, "The list has the stamps and one is chosen");
            Check(window.Sizes.Rows.Count == 2 && window.Picker.PlaceMode && window.Picker.Area != null, "The stamp has a box on the sample page, one place per paper size");
            var standard = StampLibrary.Standard.First(d => d.Id == "std-approved");
            Check(StampLibrary.Standard.Select(d => d.Id).Distinct().Count() == StampLibrary.Standard.Count && StampLibrary.Standard.All(d => d.Id.Length > 0), "Every stamp has its own id");
            var roundTrip = StampDefinition.Decode(standard.Encode(80, "x")).Definition;
            Check(roundTrip.Id == "std-approved" && roundTrip.Text == "APPROVED", "The id travels with the stamp when it is encoded into an annotation");

            var chosen = (StampDefinition)((System.Windows.Controls.ListBoxItem)window.StampList.SelectedItem).Tag;
            window.Picker.PlaceForTest(0.7, 0.9);
            var before = window.Picker.Area!.Value;
            window.Picker.ResizeForTest("SE", before.U2 + 0.1, before.V2 + 0.01);
            var after = window.Picker.Area!.Value;
            double shape(double u1, double v1, double u2, double v2) => (u2 - u1) / ((v2 - v1) * window.Picker.PageAspect);
            Check((after.U2 - after.U1) > (before.U2 - before.U1) * 1.2 && Math.Abs(shape(after.U1, after.V1, after.U2, after.V2) - shape(before.U1, before.V1, before.U2, before.V2)) < 0.02, "A corner makes the stamp bigger and keeps its shape");
            Check(AreaPresetStore.Get("stamp:" + chosen.Id, "A4 portrait") is { } saved && Math.Abs(saved.U2 - after.U2) < 1e-9, "The place and size are kept for this stamp and this paper size");
            SavePng(window, "stamppages-window");

            window.Sizes.SelectKey("A3 portrait");
            await Task.Delay(800);
            Check(window.Picker.Area != null && Math.Abs(window.Picker.Area.Value.U2 - after.U2) > 1e-6, "A3 has its own place (the default until it is placed)");
            window.RangeButton.IsChecked = true;
            window.RangeBox.Text = "2-4";
            window.ApplyForTest();
            Check(window.Definition != null && window.Placements.Select(p => p.Page).SequenceEqual(new[] { 2, 3, 4 }), "The window answers with the stamp and pages 2-4");
            var a4 = window.Placements.First(p => p.Page == 2);
            var a3 = window.Placements.First(p => p.Page == 3);
            Check(Math.Abs(a4.U2 - after.U2) < 1e-9 && Math.Abs(a3.U2 - a4.U2) > 1e-6 && window.Placements.First(p => p.Page == 4).U2 == a3.U2, "A4 pages get the A4 place, A3 pages the A3 place");

            await (Task)f.Call("StampPagesAsync", path, window.Panel)!;
            await Task.Delay(600);
            var pending = AnnotationStore.Pending(path);
            Check(pending.Count == 3 && pending.All(c => c.Add != null && c.Add.Kind == QuickAnnotationKind.Stamp), $"Three stamps wait for Save ({pending.Count})");
            Check(pending.All(c => c.Add!.Template == "stamp:" + chosen.Id) && pending.Select(c => c.Add!.Batch).Distinct().Count() == 1 && pending[0].Add!.Batch.StartsWith("batch-"), "Each carries the id of its stamp and the id of the batch");
            Check(pending.Select(c => c.Add!.PageNumber).OrderBy(n => n).SequenceEqual(new[] { 2, 3, 4 }), "…on pages 2, 3 and 4");
            window.Close();

            // written into the file with the ids, at the size that was chosen
            Check(await host.SaveGroupAsync(f.Window.Session.Documents[0], saveAs: false), "Save writes them");
            await Task.Delay(600);
            using (var doc = new PdfDocument(new PdfReader(path)))
            {
                var annots = Enumerable.Range(2, 3).Select(n => doc.GetPage(n).GetAnnotations().First(a => a is iText.Kernel.Pdf.Annot.PdfStampAnnotation)).ToList();
                var templates = annots.Select(a => a.GetPdfObject().GetAsString(new PdfName("XTTemplate"))?.ToUnicodeString()).ToList();
                var batches = annots.Select(a => a.GetPdfObject().GetAsString(new PdfName("XTBatch"))?.ToUnicodeString()).Distinct().ToList();
                Check(templates.All(t => t == "stamp:" + chosen.Id) && batches.Count == 1 && batches[0]!.StartsWith("batch-"), "The file keeps /XTTemplate and /XTBatch on every stamp (for select similar)");
                var r4 = annots[0].GetRectangle().ToRectangle();
                Check(Math.Abs(r4.GetWidth() / 595.0 - (a4.U2 - a4.U1)) < 0.003, $"The stamp is written at the size that was chosen ({r4.GetWidth():0} pt wide on A4)");
            }

            // read back: the stamp can be selected and has its resize squares
            var page2 = await AnnotationStore.GetPageAsync(path, 2);
            var spec = page2!.Annotations.First(a => a.Kind == QuickAnnotationKind.Stamp);
            Check(spec.Template == "stamp:" + chosen.Id && spec.Batch.StartsWith("batch-"), "The ids are read back from the file");
            var row2 = f.Window.Session.Documents[0].Pages.First(p => p.PageNumber == 2);
            f.Call("SelectAnnotation", row2, spec);
            f.Window.UpdateLayout();
            var grip = f.Find<System.Windows.Shapes.Rectangle>("GripSE");
            Check(grip.Visibility == System.Windows.Visibility.Visible, "A selected stamp shows the squares to resize it on the real page");

            // drag the corner like the mouse would: the stamp grows, keeps its shape and its ids
            var layer = f.Find<System.Windows.Controls.Canvas>("ReaderInteractionLayer");
            var centre = new System.Windows.Point(System.Windows.Controls.Canvas.GetLeft(grip) + grip.Width / 2, System.Windows.Controls.Canvas.GetTop(grip) + grip.Height / 2);
            var host2 = f.Find<System.Windows.FrameworkElement>("ReaderContentHost");
            var start = layer.TranslatePoint(centre, host2);
            var down = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = System.Windows.UIElement.PreviewMouseLeftButtonDownEvent };
            f.Call("Grip_MouseDown", grip, down);
            f.Call("UpdateShapeResize", new System.Windows.Point(start.X + 50, start.Y + 4));
            f.Call("FinishShapeResize");
            await Task.Delay(500);
            var resized = AnnotationStore.Pending(path).LastOrDefault(c => c.Add?.Name == spec.Name)?.Add;
            double widthBefore = spec.U2 - spec.U1, aspectBefore = (spec.U2 - spec.U1) * 595 / ((spec.V2 - spec.V1) * 842);
            Check(resized != null && resized.U2 - resized.U1 > widthBefore * 1.05, $"Dragging the corner on the real page makes the stamp bigger ({widthBefore:0.000} -> {(resized == null ? 0 : resized.U2 - resized.U1):0.000})");
            Check(resized != null && Math.Abs((resized.U2 - resized.U1) * 595 / ((resized.V2 - resized.V1) * 842) - aspectBefore) < 0.05, "…keeping its shape");
            Check(resized != null && resized.Template == spec.Template && resized.Batch == spec.Batch && resized.Name == spec.Name, "…and its ids");
        }, copyFrom: source);
    }

    /// <summary>Five pages that carry a signature in the same place: a hand-drawn curve (pages 1, 2), a pasted picture (page 3), the picture a little off (page 4), nothing (page 5).</summary>
    static string BuildSignaturePdf(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "signed.pdf");
        var pixels = new byte[60 * 24 * 3];
        for (int i = 0; i < pixels.Length; i += 3) { pixels[i] = 20; pixels[i + 1] = 20; pixels[i + 2] = 90; }
        var bitmap = new System.Windows.Media.Imaging.WriteableBitmap(60, 24, 96, 96, System.Windows.Media.PixelFormats.Rgb24, null);
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, 60, 24), pixels, 180, 0);
        using var png = new MemoryStream();
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); encoder.Save(png);
        var picture = iText.IO.Image.ImageDataFactory.Create(png.ToArray());
        using (var doc = new PdfDocument(new PdfWriter(path)))
        {
            var font = iText.Kernel.Font.PdfFontFactory.CreateFont(@"C:\Windows\Fonts\arial.ttf", iText.IO.Font.PdfEncodings.IDENTITY_H, iText.Kernel.Font.PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
            for (int i = 1; i <= 5; i++)
            {
                var page = doc.AddNewPage(PageSize.A4);
                var canvas = new PdfCanvas(page);
                canvas.BeginText().SetFontAndSize(font, 12).MoveText(300, 760).ShowText("P.TỔNG GIÁM ĐỐC").EndText();     // text above the signature, outside the area
                if (i <= 2) canvas.SetStrokeColor(ColorConstants.BLACK).SetLineWidth(2).MoveTo(300, 700).CurveTo(320, 640, 350, 620, 380, 700).CurveTo(400, 650, 420, 640, 450, 690).Stroke();
                else if (i == 3) canvas.AddImageFittedIntoRectangle(picture, new Rectangle(310, 650, 120, 48), false);
                else if (i == 4) canvas.AddImageFittedIntoRectangle(picture, new Rectangle(440, 646, 120, 48), false);     // mostly outside the area
            }
        }
        return path;
    }

    /// <summary>The owner's signature case: the app must take a signature as an OBJECT (a drawn curve or a picture), not as text: pick it, find it in an area on all pages, delete in one go, Undo.</summary>
    static void TestFindSignatureObjects()
    {
        if (!TextEditService.IsAvailable) { Console.WriteLine("Signature objects: no embedded Python; skipped"); return; }
        UseTestAreaStore("signature");
        string source = BuildSignaturePdf(System.IO.Path.Combine(Output, "signature-source"));
        RunReaderFlow("signature", async f =>
        {
            string path = f.Path;
            var host = (XTPdfMergeApp.IReaderPageEditHost)f.Window.Session;
            string Hash() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            string before = Hash();

            var onStroke = await ObjectEditService.PickAsync(path, 1, 300, 142);
            Check(onStroke.Count == 1 && onStroke[0].Kind == "shape", $"A hand-drawn curve is one shape; a click on its stroke picks it ({string.Join(",", onStroke.Select(o => o.Kind))})");
            Check((await ObjectEditService.PickAsync(path, 1, 300, 185)).Count == 0, "A click inside its box but off the stroke picks nothing");
            var onPicture = await ObjectEditService.PickAsync(path, 3, 360, 170);
            Check(onPicture.Count == 1 && onPicture[0].IsImage, "A pasted signature is an image");

            var window = new XTPdfMergeApp.Controls.BatchFindWindow(path, 1, 5) { Left = -32000, Top = -32000, ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual };
            window.Show();
            await Task.Delay(1200);
            window.ObjectsModeButton.IsChecked = true;
            await Task.Delay(300);
            window.Picker.DrawForTest((0.45, 0.12, 0.82, 0.30));
            await window.FindAsync();
            Check(window.Hits.Select(h => h.Page).SequenceEqual(new[] { 1, 2, 3 }), $"Mostly inside the area: pages 1, 2 and 3 ({string.Join(",", window.Hits.Select(h => h.Page))}); the text above is not an object");
            Check(window.Hits[0].Text.Contains("shape") && window.Hits[2].Text.Contains("image"), $"…each said as what it is (\"{window.Hits.FirstOrDefault()?.Text}\", \"{window.Hits.LastOrDefault()?.Text}\")");
            Check(window.Picker.MarkCount >= 1, "What was found is marked on the page drawn on");
            window.TouchBox.IsChecked = true;
            await window.FindAsync();
            Check(window.Hits.Select(h => h.Page).SequenceEqual(new[] { 1, 2, 3, 4 }), "With \"also everything that touches the area\" the picture that is mostly outside (page 4) is found too");
            SavePng(window, "signature-window");
            window.ApplyForTest(delete: true);
            await Task.Delay(300);
            Check(window.ObjectsToDelete.Count == 4, $"Four objects go ({window.ObjectsToDelete.Count})");
            window.Close();

            await host.ApplyObjectDeleteAsync(path, window.ObjectsToDelete, window.Description);
            await Task.Delay(300);
            Check(ObjectDeletePendingStore.Count(path) == 4 && Hash() == before, "They are marked in one go; the file is not touched until Save");
            host.Undo();
            await Task.Delay(300);
            Check(!ObjectDeletePendingStore.HasPending(path), "One Undo brings all four back");
            host.Redo();
            await Task.Delay(300);
            Check(await host.SaveGroupAsync(f.Window.Session.Documents[0], saveAs: false), "Save removes them");
            await Task.Delay(600);
            var left = await ObjectEditService.FindInAreaAsync(path, new[] { 1, 2, 3, 4, 5 }, 0, 0, 1, 1, false);
            Check(left.Count == 0, $"No drawn object is left on any page ({left.Count}: {string.Join(", ", left.Select(o => o.PageNumber + o.Kind + o.Index + "[" + (int)o.X0 + "," + (int)o.Y0 + "," + (int)o.X1 + "," + (int)o.Y1 + "]"))})");
            var runs = await TextEditService.GetRunsAsync(path, 2);
            Check(runs!.Runs.Any(r => r.Text.Contains("TỔNG")), "The text of the pages is untouched");
        }, copyFrom: source);
    }

    /// <summary>A page that is stored upright but has /Rotate 270 (a landscape CAD sheet): objects are found, picked and removed in the orientation the user sees.</summary>
    static void TestEditObjectOnRotatedPage()
    {
        if (!TextEditService.IsAvailable) { Console.WriteLine("Rotated page objects: no embedded Python; skipped"); return; }
        string folder = System.IO.Path.Combine(Output, "rotatedobjects-source");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string source = System.IO.Path.Combine(folder, "rotated.pdf");
        using (var doc = new PdfDocument(new PdfWriter(source)))
        {
            var page = doc.AddNewPage(PageSize.A4);
            page.SetRotation(270);
            var canvas = new PdfCanvas(page);
            canvas.SetStrokeColor(ColorConstants.BLACK).SetLineWidth(1).MoveTo(100, 100).LineTo(300, 100).Stroke();   // stored near the bottom left, displayed somewhere else
            canvas.SetStrokeColor(ColorConstants.RED).SetLineWidth(1).Rectangle(400, 500, 80, 40).Stroke();
        }
        RunReaderFlow("rotatedobjects", async f =>
        {
            string path = f.Path;
            var host = (XTPdfMergeApp.IReaderPageEditHost)f.Window.Session;
            string Hash() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            string before = Hash();
            var all = await ObjectEditService.FindInAreaAsync(path, new[] { 1 }, 0, 0, 1, 1, false);
            Check(all.Count == 2 && all.All(o => Math.Abs(o.PageWidth - 842) < 1 && Math.Abs(o.PageHeight - 595) < 1), $"Two objects, on a page 842 x 595 as it is displayed ({all.Count})");
            foreach (var o in all)
            {
                var again = await ObjectEditService.PickAsync(path, 1, o.Kind == "shape" ? o.X0 : (o.X0 + o.X1) / 2, (o.Y0 + o.Y1) / 2, 4); // an outline is hit on its stroke
                Check(again.Any(a => a.Kind == o.Kind && a.Index == o.Index), $"A click on the {o.Kind} as it is displayed picks it");
            }
            var line = all.First(o => o.Kind == "line");
            var area = await ObjectEditService.FindInAreaAsync(path, new[] { 1 }, (line.X0 - 5) / 842, (line.Y0 - 5) / 595, (line.X1 + 5) / 842, (line.Y1 + 5) / 595, false);
            Check(area.Count == 1 && area[0].Kind == "line", "An area drawn around it on the displayed page finds only that line");
            await host.ApplyObjectDeleteAsync(path, area, "Removed 1 line");
            await Task.Delay(300);
            Check(ObjectDeletePendingStore.Count(path) == 1 && Hash() == before, "It is marked; the file is not touched");
            Check(await host.SaveGroupAsync(f.Window.Session.Documents[0], saveAs: false), "Save removes it");
            await Task.Delay(600);
            var left = await ObjectEditService.FindInAreaAsync(path, new[] { 1 }, 0, 0, 1, 1, false);
            Check(left.Count == 1 && left[0].Kind == "shape", "The other object is left");
        }, copyFrom: source);
    }

    /// <summary>"Turn left / right" is an unsaved change: the page is shown turned (view and thumbnails), Undo turns it back, a working copy has the turn, Save writes the /Rotate.</summary>
    static void TestPageTurnWaitsForSave()
    {
        string folder = System.IO.Path.Combine(Output, "turn-source");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string source = System.IO.Path.Combine(folder, "two.pdf");
        using (var doc = new PdfDocument(new PdfWriter(source))) { doc.AddNewPage(PageSize.A4); doc.AddNewPage(PageSize.A4); }
        RunReaderFlow("turn", async f =>
        {
            string path = f.Path;
            var host = (XTPdfMergeApp.IReaderPageEditHost)f.Window.Session;
            var group = f.Window.Session.Documents[0];
            var view = f.Find<XTPdfMergeApp.Controls.ContinuousPdfView>("ReaderContinuousView");
            string Hash() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            string before = Hash();
            var (w0, h0) = view.DisplayBaseSize(0);
            Check(h0 > w0, "Page 1 is upright to start with");

            await host.RotatePagesAsync(new[] { group.Pages[0] }, 90);
            await Task.Delay(500);
            var (w1, h1) = view.DisplayBaseSize(0);
            Check(PageRotationPendingStore.Delta(path, 1) == 90 && group.IsDirty, "Turning the page is an unsaved change (the tab shows the mark)");
            Check(w1 > h1 && view.DisplayBaseSize(1).Height > view.DisplayBaseSize(1).Width, "Page 1 is shown turned, page 2 is not");
            Check(Hash() == before, "The file on disk is not touched");
            Check(await view.Dispatcher.InvokeAsync(() => view.TryPageToView(group.Pages[0], 0, 0, out var pt) && pt.X > 0) , "Points on the turned page still map to the view");
            var copy = await AnnotationWorkingCopy.GetAsync(path);
            using (var doc = new PdfDocument(new PdfReader(copy))) Check(doc.GetPage(1).GetRotation() == 90 && doc.GetPage(2).GetRotation() == 0, "A working copy (print, export, merge) has the turn");

            host.Undo();
            await Task.Delay(300);
            Check(PageRotationPendingStore.Delta(path, 1) == 0 && !group.IsDirty && view.DisplayBaseSize(0).Height > view.DisplayBaseSize(0).Width, "Undo turns it back");
            host.Redo();
            await Task.Delay(300);
            await host.RotatePagesAsync(new[] { group.Pages[0] }, 90);
            await Task.Delay(300);
            Check(PageRotationPendingStore.Delta(path, 1) == 180, "Turning again adds up (180)");

            Check(await host.SaveGroupAsync(group, saveAs: false), "Save writes the turn");
            await Task.Delay(600);
            using (var doc = new PdfDocument(new PdfReader(path))) Check(doc.GetPage(1).GetRotation() == 180 && doc.GetPage(2).GetRotation() == 0, "The file has /Rotate 180 on page 1 only");
            Check(!PageRotationPendingStore.HasPending(path) && !group.IsDirty, "…and nothing is pending any more");
            Check(XTHistory.Read(path).Any(h => h.Action.Contains("1 page turned")), "The file's history has a line for it");
        }, copyFrom: source);
    }

    static string Describe(IReadOnlyList<PdfBookmarkNode> tree, string indent = "")
        => string.Concat(tree.Select(n => $"{indent}{n.Title}@{n.PageNumber}[{string.Join(".", n.Path)}]\n" + Describe(n.Children, indent + " ")));

    /// <summary>Bookmark changes wait for Save: the panel's tree is the file's tree with them done, the model agrees with what the file gets, Undo takes one back, Save writes them in order.</summary>
    static void TestBookmarkEditsWaitForSave()
    {
        string folder = System.IO.Path.Combine(Output, "bookmarks-source");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string source = System.IO.Path.Combine(folder, "book.pdf");
        using (var doc = new PdfDocument(new PdfWriter(source)))
        {
            for (int i = 0; i < 4; i++) doc.AddNewPage();
            var root = doc.GetOutlines(true);
            var a = root.AddOutline("Chapter A"); a.AddDestination(iText.Kernel.Pdf.Navigation.PdfExplicitDestination.CreateFit(doc.GetPage(1)));
            a.AddOutline("A.1").AddDestination(iText.Kernel.Pdf.Navigation.PdfExplicitDestination.CreateFit(doc.GetPage(2)));
            var b = root.AddOutline("Chapter B"); b.AddDestination(iText.Kernel.Pdf.Navigation.PdfExplicitDestination.CreateFit(doc.GetPage(3)));
        }
        RunReaderFlow("bookmarks", async f =>
        {
            string path = f.Path;
            var host = (XTPdfMergeApp.IReaderPageEditHost)f.Window.Session;
            var group = f.Window.Session.Documents[0];
            string Hash() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            string before = Hash();
            var fileTree = PdfOutlineService.ReadBookmarks(path);

            var edits = new List<BookmarkEdit>
            {
                new("add", t => BookmarkModel.Add(t, new[] { 1 }, "B.1", 4), d => PdfOutlineService.ApplyAdd(d, new[] { 1 }, "B.1", 4)),
                new("rename", t => BookmarkModel.Rename(t, new[] { 0 }, "Part A"), d => PdfOutlineService.ApplyRename(d, new[] { 0 }, "Part A")),
                new("move", t => BookmarkModel.Move(t, new[] { 1 }, -1), d => PdfOutlineService.ApplyMove(d, new[] { 1 }, -1)),
                new("delete", t => BookmarkModel.Delete(t, new[] { 1, 0 }), d => PdfOutlineService.ApplyDelete(d, new[] { 1, 0 }))
            };
            foreach (var e in edits) await host.ApplyBookmarkEditAsync(path, e);
            await Task.Delay(300);
            Check(BookmarkPendingStore.Count(path) == 4 && group.IsDirty && Hash() == before, "Four bookmark edits are pending; the tab shows the mark and the file is not touched");
            var shown = BookmarkPendingStore.Replay(path, fileTree);
            Check(shown.Select(n => n.Title).SequenceEqual(new[] { "Chapter B", "Part A" }) && shown[0].Children.Count == 1 && shown[1].Children.Count == 0, $"The panel's tree has them done: {string.Join(", ", shown.Select(n => n.Title))}");

            host.Undo();
            await Task.Delay(200);
            var undone = BookmarkPendingStore.Replay(path, fileTree);
            Check(BookmarkPendingStore.Count(path) == 3 && undone[1].Children.Count == 1, "Undo takes the last one back (the sub-bookmark returns)");
            host.Redo();
            await Task.Delay(200);

            Check(await host.SaveGroupAsync(group, saveAs: false), "Save writes them");
            await Task.Delay(600);
            var written = PdfOutlineService.ReadBookmarks(path);
            Check(Describe(written) == Describe(shown), "The file's tree is the tree the panel showed:\n" + Describe(written));
            Check(!BookmarkPendingStore.HasPending(path) && !group.IsDirty, "Nothing is pending any more");
            Check(XTHistory.Read(path).Any(h => h.Action.Contains("4 bookmark edits")), "The file's history has a line for them");
        }, copyFrom: source);
    }
}
