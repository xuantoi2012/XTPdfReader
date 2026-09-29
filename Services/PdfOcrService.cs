using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using iText.IO.Font;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using IOPath = System.IO.Path;
using PdfRectangle = iText.Kernel.Geom.Rectangle;
using OcrRectangle = Windows.Foundation.Rect;

namespace XTPdfMergeApp.Services;

/// <summary>Tuỳ chọn OCR cho một bản sao PDF. PageNumber là 1-based.</summary>
internal sealed record PdfOcrOptions(string SourcePath, string OutputPath, IReadOnlyList<int> PageNumbers, string LanguageTag);

internal sealed record PdfOcrProgress(int CompletedPages, int TotalPages, int OcrPages, int SkippedTextPages, string Message);

internal sealed record PdfOcrResult(string OutputPath, int OcrPages, int SkippedTextPages, int RecognizedLines);

internal sealed record OcrLanguageInfo(string Tag, string DisplayName, bool IsInstalled);

/// <summary>
/// OCR cục bộ dùng Windows.Media.Ocr. PDF nguồn không bị sửa: tất cả thay đổi được ghi vào
/// tệp tạm và chỉ thay thế bản output sau khi mọi trang hoàn thành. Lớp text dùng chế độ
/// INVISIBLE nên hình/vector gốc không thay đổi nhưng vẫn search/copy được.
/// </summary>
internal static class PdfOcrService
{
    private const int TargetDpi = 180;
    private const int MaxRasterDimension = 9000;
    private const int MaxTilesPerPage = 36;
    private const int ExistingTextThreshold = 24;

    public static IReadOnlyList<OcrLanguageInfo> GetLanguages()
    {
        return new[]
        {
            DescribeLanguage("vi-VN", "Vietnamese"),
            DescribeLanguage("en-US", "English")
        };
    }

    private static OcrLanguageInfo DescribeLanguage(string tag, string name)
    {
        try
        {
            bool installed = OcrEngine.IsLanguageSupported(new Language(tag));
            return new OcrLanguageInfo(tag, installed ? name : name + " (not installed)", installed);
        }
        catch { return new OcrLanguageInfo(tag, name + " (not installed)", false); }
    }

    public static string DefaultOutputPath(string sourcePath)
    {
        string folder = IOPath.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory;
        string name = IOPath.GetFileNameWithoutExtension(sourcePath);
        return IOPath.Combine(folder, name + " - searchable.pdf");
    }

    public static async Task<PdfOcrResult> CreateSearchableCopyAsync(PdfOcrOptions options,
        IProgress<PdfOcrProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string sourcePath = IOPath.GetFullPath(options.SourcePath);
        string outputPath = IOPath.GetFullPath(options.OutputPath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("The source PDF no longer exists.", sourcePath);
        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OCR must create a new PDF; choose a different output file.");
        if (!Directory.Exists(IOPath.GetDirectoryName(outputPath)))
            throw new DirectoryNotFoundException("The output folder does not exist.");

        var language = new Language(options.LanguageTag);
        if (!OcrEngine.IsLanguageSupported(language))
            throw new InvalidOperationException($"Windows OCR language '{options.LanguageTag}' is not installed. Add it in Windows Settings > Time & language > Language & region > Language options > OCR.");
        OcrEngine? engine = OcrEngine.TryCreateFromLanguage(language);
        if (engine == null) throw new InvalidOperationException("Windows could not start the selected OCR language.");

        string tempPath = IOPath.Combine(IOPath.GetDirectoryName(outputPath)!, "." + IOPath.GetFileName(outputPath) + ".xtocr." + Guid.NewGuid().ToString("N") + ".tmp");
        int ocrPages = 0, skippedPages = 0, recognizedLines = 0;
        try
        {
            var readerProperties = new ReaderProperties();
            if (PdfThumbnailService.TryGetDocumentPassword(sourcePath) is { Length: > 0 } password)
                readerProperties.SetPassword(Encoding.UTF8.GetBytes(password));

            using var reader = new PdfReader(sourcePath, readerProperties);
            using var document = new PdfDocument(reader, new PdfWriter(tempPath, new WriterProperties().SetFullCompressionMode(true)));
            if (reader.IsEncrypted())
                throw new InvalidOperationException("OCR currently preserves neither user nor owner passwords. Remove protection first, then OCR the resulting copy.");

            var pageNumbers = NormalizePageNumbers(options.PageNumbers, document.GetNumberOfPages());
            if (pageNumbers.Count == 0) throw new InvalidOperationException("Choose at least one valid page to OCR.");
            PdfFont font = CreateUnicodeFont();
            int completed = 0;
            foreach (int pageNumber in pageNumbers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PdfPage page = document.GetPage(pageNumber);
                if (HasUsefulText(page))
                {
                    skippedPages++;
                    completed++;
                    progress?.Report(new PdfOcrProgress(completed, pageNumbers.Count, ocrPages, skippedPages,
                        $"Page {pageNumber} already has selectable text"));
                    continue;
                }

                progress?.Report(new PdfOcrProgress(completed, pageNumbers.Count, ocrPages, skippedPages,
                    $"Recognizing page {pageNumber}…"));
                int lines = await OcrPageAsync(sourcePath, pageNumber - 1, page, engine, font, cancellationToken).ConfigureAwait(false);
                recognizedLines += lines;
                ocrPages++;
                completed++;
                progress?.Report(new PdfOcrProgress(completed, pageNumbers.Count, ocrPages, skippedPages,
                    $"Recognized page {pageNumber}"));
            }

            document.Close();
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, outputPath, true);
            return new PdfOcrResult(outputPath, ocrPages, skippedPages, recognizedLines);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static List<int> NormalizePageNumbers(IReadOnlyList<int> values, int pageCount)
        => values.Where(page => page >= 1 && page <= pageCount).Distinct().OrderBy(page => page).ToList();

    private static bool HasUsefulText(PdfPage page)
    {
        string text = PdfTextExtractor.GetTextFromPage(page, new SimpleTextExtractionStrategy());
        return text.Count(char.IsLetterOrDigit) >= ExistingTextThreshold;
    }

    private static async Task<int> OcrPageAsync(string sourcePath, int pageIndex, PdfPage page, OcrEngine engine,
        PdfFont font, CancellationToken token)
    {
        PdfRectangle size = page.GetPageSizeWithRotation();
        if (size.GetWidth() <= 0 || size.GetHeight() <= 0) return 0;

        double pointsToPixels = TargetDpi / 72.0;
        int fullWidth = Math.Max(1, (int)Math.Ceiling(size.GetWidth() * pointsToPixels));
        int fullHeight = Math.Max(1, (int)Math.Ceiling(size.GetHeight() * pointsToPixels));
        int engineMaxDimension = checked((int)OcrEngine.MaxImageDimension);
        ScaleToTileBudget(ref fullWidth, ref fullHeight, Math.Max(512, Math.Min(engineMaxDimension, 2048)));

        int tileSize = Math.Max(512, Math.Min(engineMaxDimension, 2048));
        var canvas = new PdfCanvas(page.NewContentStreamAfter(), page.GetResources(), page.GetDocument());
        int lines = 0;
        for (int y = 0; y < fullHeight; y += tileSize)
        {
            int height = Math.Min(tileSize, fullHeight - y);
            for (int x = 0; x < fullWidth; x += tileSize)
            {
                token.ThrowIfCancellationRequested();
                int width = Math.Min(tileSize, fullWidth - x);
                BitmapSource? image = await PdfThumbnailService.RenderPageTileAsync(sourcePath, pageIndex, fullWidth, fullHeight,
                    new Int32Rect(x, y, width, height), token).ConfigureAwait(false);
                if (image == null) throw new InvalidOperationException($"Could not rasterize page {pageIndex + 1} for OCR.");
                using SoftwareBitmap bitmap = ToSoftwareBitmap(image);
                OcrResult result = await engine.RecognizeAsync(bitmap);
                foreach (OcrLine line in result.Lines)
                {
                    if (string.IsNullOrWhiteSpace(line.Text) || line.Words.Count == 0) continue;
                    var bounds = UnionBounds(line.Words);
                    if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                    AddInvisibleText(canvas, font, line.Text, x + bounds.X, y + bounds.Y, bounds.Width, bounds.Height,
                        fullWidth, fullHeight, size);
                    lines++;
                }
            }
        }
        return lines;
    }

    private static void ScaleToTileBudget(ref int width, ref int height, int tileSize)
    {
        width = Math.Min(width, MaxRasterDimension);
        height = Math.Min(height, MaxRasterDimension);
        while ((long)Math.Ceiling(width / (double)tileSize) * Math.Ceiling(height / (double)tileSize) > MaxTilesPerPage)
        {
            width = Math.Max(tileSize, (int)(width * 0.85));
            height = Math.Max(tileSize, (int)(height * 0.85));
        }
    }

    private static OcrRectangle UnionBounds(IReadOnlyList<OcrWord> words)
    {
        OcrRectangle first = words[0].BoundingRect;
        double left = first.X, top = first.Y, right = first.X + first.Width, bottom = first.Y + first.Height;
        for (int i = 1; i < words.Count; i++)
        {
            OcrRectangle rect = words[i].BoundingRect;
            left = Math.Min(left, rect.X);
            top = Math.Min(top, rect.Y);
            right = Math.Max(right, rect.X + rect.Width);
            bottom = Math.Max(bottom, rect.Y + rect.Height);
        }
        return new OcrRectangle(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static SoftwareBitmap ToSoftwareBitmap(BitmapSource source)
    {
        var converted = source.Format == System.Windows.Media.PixelFormats.Bgra32
            ? source : new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int stride = checked(converted.PixelWidth * 4);
        var bytes = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(bytes, stride, 0);
        return SoftwareBitmap.CreateCopyFromBuffer(CryptographicBuffer.CreateFromByteArray(bytes),
            BitmapPixelFormat.Bgra8, converted.PixelWidth, converted.PixelHeight, BitmapAlphaMode.Ignore);
    }

    private static void AddInvisibleText(PdfCanvas canvas, PdfFont font, string text, double x, double y, double width,
        double height, int fullWidth, int fullHeight, PdfRectangle pageSize)
    {
        float pdfX = (float)(x / fullWidth * pageSize.GetWidth());
        float pdfY = (float)(pageSize.GetHeight() - (y + height) / fullHeight * pageSize.GetHeight());
        float fontSize = (float)Math.Clamp(height / fullHeight * pageSize.GetHeight() * 0.82, 2, 72);
        canvas.SaveState();
        canvas.BeginText().SetFontAndSize(font, fontSize)
            .SetTextRenderingMode(PdfCanvasConstants.TextRenderingMode.INVISIBLE)
            .MoveText(pdfX, pdfY).ShowText(text.Trim()).EndText();
        canvas.RestoreState();
    }

    private static PdfFont CreateUnicodeFont()
    {
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (string name in new[] { "segoeui.ttf", "arial.ttf", "tahoma.ttf" })
        {
            string path = IOPath.Combine(fonts, name);
            if (File.Exists(path))
            {
                try { return PdfFontFactory.CreateFont(path, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED); }
                catch { /* try the next Windows font */ }
            }
        }
        throw new InvalidOperationException("A Unicode Windows font (Segoe UI, Arial, or Tahoma) is required to create a searchable Vietnamese PDF.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* temp cleanup is best effort */ }
    }
}
