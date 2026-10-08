using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using iText.IO.Font;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;

namespace XTPdfMergeApp.Services.Ocr
{
    /// <summary>
    /// Puts the words an OCR read into a PDF as an INVISIBLE text layer (text rendering mode 3) on top of the scan, like a scanner's "searchable PDF".
    /// The picture is untouched; Find, Select, Copy, the sheet-info reader and any other viewer see the text. Each word is placed on its own box,
    /// stretched to its width, and turned with the page so it follows a /Rotate 90 / 180 / 270 sheet.
    /// </summary>
    internal static class PdfOcrWriter
    {
        /// <summary>A font that has Vietnamese letters (Arial first); null when none is installed (the words are then written with Helvetica, without the accents).</summary>
        internal static string? FindFontFile()
        {
            string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach (string name in new[] { "arial.ttf", "segoeui.ttf", "tahoma.ttf", "times.ttf", "calibri.ttf" })
            {
                string path = Path.Combine(fonts, name);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        /// <summary>Copies <paramref name="inputPath"/> to <paramref name="outputPath"/> with the words added. Returns how many words were written.</summary>
        public static int Write(string inputPath, string outputPath, IEnumerable<OcrPageResult> pages)
        {
            int written = 0;
            using var reader = new PdfReader(inputPath);
            using var writer = new PdfWriter(outputPath);
            using var document = new PdfDocument(reader, writer);
            string? fontFile = FindFontFile();
            var font = fontFile != null
                ? PdfFontFactory.CreateFont(fontFile, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED)
                : PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA);

            foreach (var result in pages)
            {
                if (result.Skipped || result.Words.Count == 0 || result.PageIndex < 0 || result.PageIndex >= document.GetNumberOfPages()) continue;
                var page = document.GetPage(result.PageIndex + 1);
                var box = page.GetMediaBox();
                int rotation = ((page.GetRotation() % 360) + 360) % 360;
                double displayW = rotation % 180 == 0 ? box.GetWidth() : box.GetHeight(), displayH = rotation % 180 == 0 ? box.GetHeight() : box.GetWidth();
                double radians = rotation * Math.PI / 180;
                float cos = (float)Math.Round(Math.Cos(radians)), sin = (float)Math.Round(Math.Sin(radians));

                var canvas = new PdfCanvas(page.NewContentStreamAfter(), page.GetResources(), document);
                canvas.SaveState().BeginText().SetTextRenderingMode(PdfCanvasConstants.TextRenderingMode.INVISIBLE);
                foreach (var word in result.Words)
                {
                    string text = word.Text.Normalize(NormalizationForm.FormC);
                    double widthPt = (word.U2 - word.U1) * displayW, heightPt = (word.V2 - word.V1) * displayH;
                    if (text.Length == 0 || widthPt < 0.5 || heightPt < 0.5) continue;
                    float size = (float)Math.Clamp(heightPt * 0.8, 1, 400);
                    float natural = font.GetWidth(text, size);
                    if (natural <= 0) continue;
                    float scalePercent = (float)Math.Clamp(100 * widthPt / natural, 5, 1000);
                    // the baseline sits a little above the bottom of the box (letters like g, y, p hang below it)
                    var origin = TitleBlockReader.MapPoint(page, word.U1, word.V2 - 0.18 * (word.V2 - word.V1));
                    canvas.SetFontAndSize(font, size)
                        .SetHorizontalScaling(scalePercent)
                        .SetTextMatrix(cos, sin, -sin, cos, (float)origin.X, (float)origin.Y)
                        .ShowText(text);
                    written++;
                }
                canvas.EndText().RestoreState().Release();
            }
            document.GetDocumentInfo().SetKeywords("OCR: Tesseract (PDF Reader Pro)");
            return written;
        }

        /// <summary>The name of the searchable copy: "name (OCR).pdf" next to the original, numbered when it exists.</summary>
        public static string OutputPathFor(string inputPath)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(inputPath))!, stem = Path.GetFileNameWithoutExtension(inputPath);
            string path = Path.Combine(folder, stem + " (OCR).pdf");
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem} (OCR {n}).pdf");
            return path;
        }
    }
}
