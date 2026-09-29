using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using iText.Kernel.Geom;
using Path = System.IO.Path;
using iText.Kernel.Pdf;

namespace XTPdfMergeApp.Services;

/// <summary>
/// Trang trắng cho "Insert → Blank page". Workspace chỉ tham chiếu trang của file nguồn, nên trang trắng là 1 file PDF 1 trang tạo
/// trong thư mục tạm (mỗi khổ giấy 1 file, dùng chung); placement trỏ tới nó và Save chép trang đó vào file kết quả như mọi trang khác.
/// Thư mục tạm bị xoá khi thoát app.
/// </summary>
internal static class BlankPageService
{
    private static readonly string Directory_ = Path.Combine(Path.GetTempPath(), "XTPdfBlank_" + Environment.ProcessId);
    private static readonly ConcurrentDictionary<string, string> _files = new();

    /// <summary>Kích thước (point, đã tính xoay) của 1 trang trong file nguồn; null nếu đọc không được.</summary>
    public static (double Width, double Height)? ReadPageSize(string path, int pageNumber)
    {
        try
        {
            using var reader = new PdfReader(path);
            using var document = new PdfDocument(reader);
            if (pageNumber < 1 || pageNumber > document.GetNumberOfPages()) return null;
            var size = document.GetPage(pageNumber).GetPageSizeWithRotation();
            return (size.GetWidth(), size.GetHeight());
        }
        catch { return null; }
    }

    /// <summary>Đường dẫn file PDF 1 trang trắng đúng khổ (tạo nếu chưa có).</summary>
    public static string GetBlankPdf(double widthPoints, double heightPoints)
    {
        double w = Math.Round(Math.Clamp(widthPoints, 72, 14400), 1);
        double h = Math.Round(Math.Clamp(heightPoints, 72, 14400), 1);
        string key = w.ToString("0.#", CultureInfo.InvariantCulture) + "x" + h.ToString("0.#", CultureInfo.InvariantCulture);
        return _files.GetOrAdd(key, k =>
        {
            System.IO.Directory.CreateDirectory(Directory_);
            string file = Path.Combine(Directory_, "blank_" + k + ".pdf");
            using var writer = new PdfWriter(file);
            using var document = new PdfDocument(writer);
            document.AddNewPage(new PageSize((float)w, (float)h));
            return file;
        });
    }

    /// <summary>PDF trắng riêng cho document Untitled. Không dùng cache theo khổ để có thể mở cùng lúc nhiều file mới cùng A4.</summary>
    public static string CreateUntitledPdf(double widthPoints, double heightPoints)
    {
        double w = Math.Round(Math.Clamp(widthPoints, 72, 14400), 1);
        double h = Math.Round(Math.Clamp(heightPoints, 72, 14400), 1);
        Directory.CreateDirectory(Directory_);
        string file = Path.Combine(Directory_, "Untitled_" + Guid.NewGuid().ToString("N") + ".pdf");
        using var writer = new PdfWriter(file);
        using var document = new PdfDocument(writer);
        document.AddNewPage(new PageSize((float)w, (float)h));
        return file;
    }

    public static bool IsBlankFile(string path)
        => path.StartsWith(Directory_, StringComparison.OrdinalIgnoreCase);

    public static void Cleanup()
    {
        try { if (System.IO.Directory.Exists(Directory_)) System.IO.Directory.Delete(Directory_, recursive: true); }
        catch { /* file tạm: bỏ qua */ }
    }
}
