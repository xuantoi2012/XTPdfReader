using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using iText.IO.Font;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Extgstate;

namespace XTPdfMergeApp.Services;

internal enum MarkPosition { Center, Top, Bottom }

/// <summary>A big text over the page: BẢN SAO, MẬT, ĐÃ DUYỆT… Angle is counter-clockwise degrees as the reader sees it.</summary>
internal sealed record WatermarkOptions(string Text, double FontSize, string ColorHex, double Opacity, double AngleDegrees, MarkPosition Position, bool Box);

/// <summary>
/// Text in the margins. Tokens: {page}, {pages}, {n} or {n:6} (running number across the pages that get marks, StartNumber upward, zero padded to 6),
/// {date}, {time}, {file}, {user}. "Trang {page}/{pages}" is the usual page number.
/// </summary>
internal sealed record HeaderFooterOptions(string HeaderLeft, string HeaderCenter, string HeaderRight,
    string FooterLeft, string FooterCenter, string FooterRight, double FontSize, double Margin, int StartNumber, string ColorHex)
{
    public bool IsEmpty => new[] { HeaderLeft, HeaderCenter, HeaderRight, FooterLeft, FooterCenter, FooterRight }.All(string.IsNullOrWhiteSpace);
}

/// <summary>A black box on a page, as fractions of the displayed page (top-left origin).</summary>
internal sealed record RedactionArea(int Page, double U1, double V1, double U2, double V2);

internal sealed record PageMarkJob(string SourcePath, string OutputPath, IReadOnlyList<int> Pages,
    WatermarkOptions? Watermark, HeaderFooterOptions? HeaderFooter, IReadOnlyList<RedactionArea> Redactions, int RedactionDpi = 200)
{
    /// <summary>Preview of one page copied out of a bigger file: the page/total/running numbers it should show.</summary>
    public (int Page, int Total, int Running)? PreviewNumbers { get; init; }
}

/// <summary>
/// Watermark / stamp text, headers, footers, page and document numbers, and redaction, written into a NEW copy of the PDF.
/// Redaction really removes what is under the box: the redacted pages are drawn as pictures with the box burned into the pixels (text on those pages is no longer selectable; run OCR after if needed).
/// </summary>
internal static class PdfPageMarkService
{
    public static async Task<int> ApplyAsync(PageMarkJob job, CancellationToken cancel = default)
    {
        string stage = job.OutputPath + ".marking.tmp";
        string work = stage + ".src";
        try
        {
            string source = job.SourcePath;
            var redactedPages = job.Redactions.Select(r => r.Page).Distinct().OrderBy(x => x).ToList();
            if (redactedPages.Count > 0)
            {
                var pictures = new Dictionary<int, (byte[] Jpeg, double W, double H)>();
                foreach (int page in redactedPages)
                {
                    cancel.ThrowIfCancellationRequested();
                    pictures[page] = await RasterizeAsync(job.SourcePath, page, job.RedactionDpi, job.Redactions.Where(r => r.Page == page).ToList());
                }
                await Task.Run(() => ReplaceWithPictures(job.SourcePath, work, pictures));
                source = work;
            }
            int marked = await Task.Run(() => Mark(source, stage, job), cancel);
            if (File.Exists(job.OutputPath)) File.Delete(job.OutputPath);
            File.Move(stage, job.OutputPath);
            return marked;
        }
        finally
        {
            foreach (var f in new[] { stage, work }) try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    /// <summary>A one-page PDF with the marks (no redaction) to look at before writing the real file.</summary>
    public static async Task<string> PreviewAsync(PageMarkJob job, int page, int running)
    {
        string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xt-mark-preview-" + Guid.NewGuid().ToString("N")[..8] + ".pdf");
        string single = temp + ".src";
        try
        {
            await Task.Run(() =>
            {
                int total;
                using (var src = new PdfDocument(Reader(job.SourcePath)))
                {
                    total = src.GetNumberOfPages();
                    using var one = new PdfDocument(new PdfWriter(single));
                    src.CopyPagesTo(page, page, one);
                }
                var small = job with { OutputPath = temp, Pages = new[] { 1 }, Redactions = Array.Empty<RedactionArea>(), PreviewNumbers = (page, total, running) };
                Mark(single, temp, small);
            });
            return temp;
        }
        finally { try { File.Delete(single); } catch { } }
    }

    // ── redaction ────────────────────────────────────────────────────

    private static async Task<(byte[] Jpeg, double W, double H)> RasterizeAsync(string path, int page, int dpi, IReadOnlyList<RedactionArea> areas)
    {
        double wPt, hPt;
        using (var doc = new PdfDocument(Reader(path)))
        {
            var size = doc.GetPage(page).GetPageSizeWithRotation();
            wPt = size.GetWidth(); hPt = size.GetHeight();
        }
        int px = (int)Math.Clamp(wPt / 72.0 * dpi, 600, 7000);
        var bitmap = await PdfThumbnailService.RenderPageAsync(path, page - 1, px, default, PdfRenderPriority.Visible, null, true)
                     ?? throw new IOException($"Không vẽ được trang {page} để xoá nội dung.");
        return await Task.Run(() =>
        {
            var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var pixels = new byte[stride * h];
            bgra.CopyPixels(pixels, stride, 0);
            foreach (var a in areas)
            {
                int x1 = Math.Clamp((int)Math.Floor(Math.Min(a.U1, a.U2) * w) - 1, 0, w), x2 = Math.Clamp((int)Math.Ceiling(Math.Max(a.U1, a.U2) * w) + 1, 0, w);
                int y1 = Math.Clamp((int)Math.Floor(Math.Min(a.V1, a.V2) * h) - 1, 0, h), y2 = Math.Clamp((int)Math.Ceiling(Math.Max(a.V1, a.V2) * h) + 1, 0, h);
                for (int y = y1; y < y2; y++)
                    for (int x = x1; x < x2; x++)
                    {
                        int i = y * stride + x * 4;
                        pixels[i] = 0; pixels[i + 1] = 0; pixels[i + 2] = 0; pixels[i + 3] = 255;
                    }
            }
            var burned = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(burned, PixelFormats.Bgr24, null, 0)));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return (ms.ToArray(), wPt, hPt);
        });
    }

    private static void ReplaceWithPictures(string source, string output, Dictionary<int, (byte[] Jpeg, double W, double H)> pictures)
    {
        using var doc = new PdfDocument(Reader(source), new PdfWriter(output));
        foreach (var (page, picture) in pictures.OrderBy(p => p.Key))
        {
            doc.RemovePage(page);
            var created = doc.AddNewPage(page, new PageSize((float)picture.W, (float)picture.H));
            var canvas = new PdfCanvas(created);
            canvas.AddImageFittedIntoRectangle(ImageDataFactory.Create(picture.Jpeg), new Rectangle(0, 0, (float)picture.W, (float)picture.H), false);
        }
        // The outline and the document information are left as they are; names that point at a removed page are the user's to check.
    }

    // ── marks ────────────────────────────────────────────────────────

    private static int Mark(string source, string output, PageMarkJob job)
    {
        bool anyMark = job.Watermark != null || (job.HeaderFooter is { IsEmpty: false });
        using var doc = new PdfDocument(Reader(source), new PdfWriter(output));
        if (!anyMark) return 0;
        var font = PdfFontFactory.CreateFont(FontPath(), PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
        int total = doc.GetNumberOfPages();
        string file = System.IO.Path.GetFileName(job.SourcePath);
        int index = 0;
        foreach (int number in job.Pages.Distinct().OrderBy(x => x).Where(p => p >= 1 && p <= total))
        {
            var page = doc.GetPage(number);
            var canvas = new PdfCanvas(page);
            if (job.Watermark is { } wm) DrawWatermark(canvas, page, font, wm);
            if (job.HeaderFooter is { IsEmpty: false } hf)
            {
                var shown = job.PreviewNumbers ?? (number, total, hf.StartNumber + index);
                DrawHeaderFooter(canvas, page, font, hf, shown.Item1, shown.Item2, shown.Item3, file);
            }
            index++;
        }
        return index;
    }

    public static string ExpandTokens(string text, int page, int pages, int running, string file)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = Regex.Replace(text, @"\{n(?::(\d+))?\}", m => running.ToString(m.Groups[1].Success ? new string('0', int.Parse(m.Groups[1].Value)) : "0"));
        return text.Replace("{page}", page.ToString()).Replace("{pages}", pages.ToString())
            .Replace("{date}", DateTime.Now.ToString("dd/MM/yyyy")).Replace("{time}", DateTime.Now.ToString("HH:mm"))
            .Replace("{file}", file).Replace("{user}", Environment.UserName);
    }

    private static void DrawWatermark(PdfCanvas canvas, PdfPage page, PdfFont font, WatermarkOptions wm)
    {
        if (string.IsNullOrWhiteSpace(wm.Text)) return;
        var (dispW, dispH, rotation) = Geometry(page);
        double size = Math.Max(4, wm.FontSize);
        double width = font.GetWidth(wm.Text, (float)size);
        double u = 0.5, v = wm.Position switch { MarkPosition.Top => 0.12, MarkPosition.Bottom => 0.88, _ => 0.5 };
        var (cx, cy) = ToPage(page, u, v);
        double angle = (wm.AngleDegrees + rotation) * Math.PI / 180.0;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        var color = ParseColor(wm.ColorHex);
        canvas.SaveState();
        canvas.SetExtGState(new PdfExtGState().SetFillOpacity((float)Math.Clamp(wm.Opacity, 0.02, 1)).SetStrokeOpacity((float)Math.Clamp(wm.Opacity, 0.02, 1)));
        canvas.ConcatMatrix(cos, sin, -sin, cos, cx, cy);
        canvas.SetFillColor(color).SetStrokeColor(color);
        canvas.BeginText().SetFontAndSize(font, (float)size).MoveText(-width / 2, -size * 0.35).ShowText(wm.Text).EndText();
        if (wm.Box)
        {
            double pad = size * 0.3;
            canvas.SetLineWidth((float)Math.Max(1.5, size / 14));
            canvas.Rectangle(-width / 2 - pad, -size * 0.35 - pad, width + 2 * pad, size * 0.9 + 2 * pad).Stroke();
        }
        canvas.RestoreState();
    }

    private static void DrawHeaderFooter(PdfCanvas canvas, PdfPage page, PdfFont font, HeaderFooterOptions o, int number, int total, int running, string file)
    {
        var (dispW, dispH, rotation) = Geometry(page);
        double size = Math.Max(4, o.FontSize), margin = Math.Max(0, o.Margin);
        double top = dispH - margin - size, bottom = margin;
        var color = ParseColor(o.ColorHex);
        void Put(string template, double x, double y, int align)
        {
            string text = ExpandTokens(template, number, total, running, file);
            if (text.Length == 0) return;
            double width = font.GetWidth(text, (float)size);
            double left = align switch { 0 => x, 1 => x - width / 2, _ => x - width };
            var (px, py) = ToPage(page, left / dispW, 1 - y / dispH);
            double angle = rotation * Math.PI / 180.0, cos = Math.Cos(angle), sin = Math.Sin(angle);
            canvas.SaveState();
            canvas.ConcatMatrix(cos, sin, -sin, cos, px, py);
            canvas.SetFillColor(color).BeginText().SetFontAndSize(font, (float)size).MoveText(0, 0).ShowText(text).EndText();
            canvas.RestoreState();
        }
        Put(o.HeaderLeft, margin, top, 0); Put(o.HeaderCenter, dispW / 2, top, 1); Put(o.HeaderRight, dispW - margin, top, 2);
        Put(o.FooterLeft, margin, bottom, 0); Put(o.FooterCenter, dispW / 2, bottom, 1); Put(o.FooterRight, dispW - margin, bottom, 2);
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static PdfReader Reader(string path)
    {
        var props = new ReaderProperties();
        if (PdfThumbnailService.TryGetDocumentPassword(path) is { Length: > 0 } password) props.SetPassword(System.Text.Encoding.UTF8.GetBytes(password));
        return new PdfReader(path, props);
    }

    private static (double W, double H, int Rotation) Geometry(PdfPage page)
    {
        var box = page.GetPageSize();
        int rotation = ((page.GetRotation() % 360) + 360) % 360;
        return rotation % 180 == 0 ? (box.GetWidth(), box.GetHeight(), rotation) : (box.GetHeight(), box.GetWidth(), rotation);
    }

    /// <summary>A point on the displayed page (u 0..1 from the left, v 0..1 from the top) in the page's own coordinates.</summary>
    internal static (double X, double Y) ToPage(PdfPage page, double u, double v)
    {
        var box = page.GetPageSize();
        var (dispW, dispH, rotation) = Geometry(page);
        double X = u * dispW, Yup = (1 - v) * dispH;
        var (x, y) = rotation switch
        {
            90 => (dispH - Yup, X),
            180 => (dispW - X, dispH - Yup),
            270 => (Yup, dispW - X),
            _ => (X, Yup),
        };
        return (box.GetLeft() + x, box.GetBottom() + y);
    }

    private static iText.Kernel.Colors.Color ParseColor(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            return new DeviceRgb(Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..6], 16));
        }
        catch { return ColorConstants.RED; }
    }

    private static string FontPath()
    {
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var name in new[] { "arial.ttf", "segoeui.ttf", "tahoma.ttf" })
        {
            string p = System.IO.Path.Combine(fonts, name);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("Không tìm thấy phông Arial trong Windows.");
    }
}
