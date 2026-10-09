using System.IO;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    static async Task TestPageMarksAsync()
    {
        Directory.CreateDirectory(Output);
        string src = Path.Combine(Output, "mark-src.pdf");
        using (var doc = new PdfDocument(new PdfWriter(src)))
        {
            for (int i = 1; i <= 4; i++)
            {
                var page = doc.AddNewPage();
                if (i == 3) page.SetRotation(90);
                var canvas = new iText.Kernel.Pdf.Canvas.PdfCanvas(page);
                canvas.BeginText().SetFontAndSize(iText.Kernel.Font.PdfFontFactory.CreateFont(), 24).MoveText(100, 700).ShowText("SECRET-" + i).EndText();
            }
        }
        string TextOf(string path, int page)
        {
            using var d = new PdfDocument(new PdfReader(path));
            return PdfTextExtractor.GetTextFromPage(d.GetPage(page), new LocationTextExtractionStrategy());
        }

        string marked = Path.Combine(Output, "mark-out.pdf");
        var wm = new WatermarkOptions("BẢN SAO", 80, "#C0392B", 0.3, 45, MarkPosition.Center, true);
        var hf = new HeaderFooterOptions("", "", "Số: {n:6}", "", "Trang {page}/{pages}", "", 10, 24, 125, "#000000");
        int count = await PdfPageMarkService.ApplyAsync(new PageMarkJob(src, marked, new[] { 1, 2, 3, 4 }, wm, hf, Array.Empty<RedactionArea>()));
        Check(count == 4, "Marks written on all 4 pages");
        Check(TextOf(marked, 2).Contains("Trang 2/4") && TextOf(marked, 4).Contains("Trang 4/4"), "Page X/Y tokens are filled for each page");
        Check(TextOf(marked, 1).Contains("Số: 000125") && TextOf(marked, 4).Contains("Số: 000128"), "Document numbers run on with zero padding");
        Check(TextOf(marked, 1).Contains("SECRET-1"), "The original text stays");
        Check(TextOf(marked, 3).Contains("Trang 3/4"), "Header and footer text is written on a rotated page too");

        string some = Path.Combine(Output, "mark-range.pdf");
        await PdfPageMarkService.ApplyAsync(new PageMarkJob(src, some, new[] { 2 }, null, hf, Array.Empty<RedactionArea>()));
        Check(!TextOf(some, 1).Contains("Trang") && TextOf(some, 2).Contains("Trang 2/4"), "Only the chosen pages are marked");

        string red = Path.Combine(Output, "mark-redacted.pdf");
        // SECRET-2 sits near (100,700) of an A4 page: about 17%..45% across, 12%..16% down
        await PdfPageMarkService.ApplyAsync(new PageMarkJob(src, red, new[] { 1 }, null, null, new[] { new RedactionArea(2, 0.05, 0.10, 0.60, 0.20) }));
        Check(!TextOf(red, 2).Contains("SECRET"), "A redacted page no longer carries the hidden text");
        Check(TextOf(red, 1).Contains("SECRET-1") && TextOf(red, 4).Contains("SECRET-4"), "Pages without redaction keep their text");
        var bytes = File.ReadAllText(red, System.Text.Encoding.Latin1);
        Check(!bytes.Contains("SECRET-2"), "The redacted words are not left anywhere in the file");
    }
}
