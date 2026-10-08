using System;
using System.Collections.Generic;
using System.Linq;
using iText.IO.Font;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using XTPdfMergeApp.Services.Ocr;

namespace XTPdfMergeApp.Services.TextEdit
{
    /// <summary>
    /// Writes the new text of the edits (visible, the size, colour and baseline of the run it replaces) with an embedded Arial subset, so the letters are searchable and copyable
    /// (iText writes a correct ToUnicode). The old characters were removed before by <see cref="TextEditService"/> (a second, earlier step: redaction would take the new text too).
    /// Pages are upright (rotation 0); the run's page coordinates start at the top left of the crop box.
    /// </summary>
    internal static class TextEditWriter
    {
        public static void ApplyTo(PdfDocument doc, IReadOnlyList<TextEdit> edits)
        {
            var withText = edits.Where(e => e.NewText.Length > 0).ToList();
            if (withText.Count == 0) return;
            string? fontFile = PdfOcrWriter.FindFontFile();
            var fonts = new Dictionary<int, PdfFont>();
            PdfFont FontFor(int flags)
            {
                int key = flags & 18;
                if (fonts.TryGetValue(key, out var font)) return font;
                string? file = fontFile == null ? null : StyledFile(fontFile, key);
                font = file != null
                    ? PdfFontFactory.CreateFont(file, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED)
                    : PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA);
                return fonts[key] = font;
            }
            foreach (var group in withText.GroupBy(e => e.PageNumber))
            {
                var page = doc.GetPage(group.Key);
                var box = page.GetCropBox();
                var canvas = new PdfCanvas(page.NewContentStreamAfter(), page.GetResources(), doc);
                foreach (var edit in group)
                {
                    var run = edit.Original;
                    canvas.SaveState().BeginText()
                        .SetFontAndSize(FontFor(run.Flags), (float)run.Size)
                        .SetFillColor(new DeviceRgb((run.Color >> 16) & 255, (run.Color >> 8) & 255, run.Color & 255))
                        .MoveText(box.GetLeft() + run.OriginX, box.GetTop() - run.OriginY)
                        .ShowText(edit.NewText).EndText().RestoreState();
                }
            }
        }

        /// <summary>Arial, Arial Bold, Arial Italic or Arial Bold Italic next to the regular file (the regular one when the style is not installed).</summary>
        private static string? StyledFile(string regular, int flags)
        {
            if (!System.IO.Path.GetFileName(regular).Equals("arial.ttf", StringComparison.OrdinalIgnoreCase)) return regular;
            bool bold = (flags & 16) != 0, italic = (flags & 2) != 0;
            string name = bold && italic ? "arialbi.ttf" : bold ? "arialbd.ttf" : italic ? "ariali.ttf" : "arial.ttf";
            string styled = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(regular)!, name);
            return System.IO.File.Exists(styled) ? styled : regular;
        }
    }
}
