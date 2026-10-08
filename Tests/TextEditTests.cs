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

            Check(await host.DeleteObjectsAsync(path, new[] { onFrame[0], onImage[0] }, "Removed 2 objects"), "Removing the frame and the picture works");
            await Task.Delay(400);
            var left = await ObjectEditService.PickAreaAsync(path, 1, 0, 0, 595, 842);
            Check(left.Count == 2 && left.All(o => o.Kind == "line"), $"Only the two lines are left ({left.Count}); what the frame framed stays");
            var runs = await TextEditService.GetRunsAsync(path, 1);
            Check(runs!.Runs.Count == 1 && runs.Runs[0].Text == "inside text", "The text inside is untouched");
            string after = Hash();
            Check(after != before, "The file changed");

            host.Undo();
            await Task.Delay(800);
            Check(Hash() == before, "Undo gives the file back byte for byte");
            var back = await ObjectEditService.PickAreaAsync(path, 1, 0, 0, 595, 842);
            Check(back.Count == 4, $"…with all four objects ({back.Count})");
            host.Redo();
            await Task.Delay(800);
            Check(Hash() == after, "Redo makes the same file again");
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
            Check(window.Hits.Count == 3 && window.Hits.Select(h => h.Page).SequenceEqual(new[] { 1, 2, 4 }), $"Three hits, pages 1, 2 and 4 ({string.Join(",", window.Hits.Select(h => h.Page))})");
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

    /// <summary>Stamp pages: one stamp at the same place on the chosen pages, as one undoable change.</summary>
    static void TestStampManyPages()
    {
        string source = BuildBatchPdf(System.IO.Path.Combine(Output, "stamppages-source"));
        RunReaderFlow("stamppages", async f =>
        {
            string path = f.Path;
            var host = (XTPdfMergeApp.IReaderPageEditHost)f.Window.Session;
            var window = new XTPdfMergeApp.Controls.StampPagesWindow(path, 1, 4) { Left = -32000, Top = -32000, ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual };
            window.Show();
            await Task.Delay(1500);
            Check(window.StampList.Items.Count >= 6 && window.StampList.SelectedItem != null, "The list has the stamps and one is chosen");
            Check(window.Picker.PlaceSize != null && window.Picker.Area != null, "The stamp has a box on the sample page");
            window.Picker.PlaceForTest(0.75, 0.9);
            var area = window.Picker.Area!.Value;
            Check(area.U2 <= 1.0001 && area.V2 <= 1.0001 && area.U1 >= 0 && area.V1 >= 0, "The box stays on the page");
            SavePng(window, "stamppages-window");
            window.RangeButton.IsChecked = true;
            window.RangeBox.Text = "2-4";
            window.ApplyForTest();
            Check(window.Definition != null && window.Pages.SequenceEqual(new[] { 2, 3, 4 }), "The window answers with the stamp and pages 2-4");
            await (Task)f.Call("StampPagesAsync", path, window)!;
            await Task.Delay(600);
            var pending = AnnotationStore.Pending(path);
            Check(pending.Count == 3 && pending.All(c => c.Add != null && c.Add.Kind == QuickAnnotationKind.Stamp), $"Three stamps wait for Save ({pending.Count})");
            Check(pending.Select(c => Math.Round(c.Add!.U1, 3)).Distinct().Count() == 1 && pending.Select(c => Math.Round(c.Add!.V1, 3)).Distinct().Count() == 1, "…all at the same place");
            Check(pending.Select(c => c.Add!.PageNumber).OrderBy(n => n).SequenceEqual(new[] { 2, 3, 4 }), "…on pages 2, 3 and 4");
            host.Undo();
            await Task.Delay(400);
            Check(AnnotationStore.Pending(path).Count == 0, "One Undo takes all three back");
            window.Close();
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
                else if (i == 4) canvas.AddImageFittedIntoRectangle(picture, new Rectangle(400, 646, 120, 48), false);     // sticks out of the area
            }
        }
        return path;
    }

    /// <summary>The owner's signature case: the app must take a signature as an OBJECT (a drawn curve or a picture), not as text: pick it, find it in an area on all pages, delete in one go, Undo.</summary>
    static void TestFindSignatureObjects()
    {
        if (!TextEditService.IsAvailable) { Console.WriteLine("Signature objects: no embedded Python; skipped"); return; }
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
            window.Picker.DrawForTest((0.45, 0.12, 0.82, 0.30));
            await window.FindAsync();
            Check(window.Hits.Select(h => h.Page).SequenceEqual(new[] { 1, 2, 3 }), $"Completely inside the area: pages 1, 2 and 3 ({string.Join(",", window.Hits.Select(h => h.Page))}); the text above is not an object");
            Check(window.Hits[0].Text.Contains("shape") && window.Hits[2].Text.Contains("image"), $"…each said as what it is (\"{window.Hits.FirstOrDefault()?.Text}\", \"{window.Hits.LastOrDefault()?.Text}\")");
            Check(window.Picker.MarkCount >= 1, "What was found is marked on the page drawn on");
            window.TouchBox.IsChecked = true;
            await window.FindAsync();
            Check(window.Hits.Select(h => h.Page).SequenceEqual(new[] { 1, 2, 3, 4 }), "With \"also objects that touch the area\" the slightly shifted page 4 is found too");
            SavePng(window, "signature-window");
            window.ApplyForTest(delete: true);
            await Task.Delay(300);
            Check(window.ObjectsToDelete.Count == 4, $"Four objects go ({window.ObjectsToDelete.Count})");
            window.Close();

            Check(await host.DeleteObjectsAsync(path, window.ObjectsToDelete, window.Description), "They are removed in one go");
            await Task.Delay(500);
            var left = await ObjectEditService.FindInAreaAsync(path, new[] { 1, 2, 3, 4, 5 }, 0, 0, 1, 1, false);
            Check(left.Count == 0, $"No drawn object is left on any page ({left.Count}: {string.Join(", ", left.Select(o => o.PageNumber + o.Kind + o.Index + "[" + (int)o.X0 + "," + (int)o.Y0 + "," + (int)o.X1 + "," + (int)o.Y1 + "]"))})");
            var runs = await TextEditService.GetRunsAsync(path, 2);
            Check(runs!.Runs.Any(r => r.Text.Contains("TỔNG")), "The text of the pages is untouched");
            host.Undo();
            await Task.Delay(800);
            Check(Hash() == before, "One Undo gives the file back byte for byte");
        }, copyFrom: source);
    }
}
