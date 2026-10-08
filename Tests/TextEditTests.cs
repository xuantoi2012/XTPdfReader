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
}
