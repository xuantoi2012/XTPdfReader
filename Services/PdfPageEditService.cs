using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using iText.Kernel.Pdf;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// Sửa THẲNG file PDF nguồn bằng iText (khác mô hình workspace — placement chỉ trỏ tới file):
    /// xoay trang, ghi annotation. Ghi kiểu incremental (append mode) nên nội dung cũ, layer (OCG),
    /// chữ ký số đã có đều giữ nguyên byte gốc, chỉ nối thêm phần thay đổi vào cuối file.
    ///
    /// Không tự lo handle PDFium: caller phải <see cref="PdfThumbnailService.SuspendDocumentAsync"/>
    /// trước khi gọi (Windows không cho ghi đè file PDFium đang mở).
    /// </summary>
    public static class PdfPageEditService
    {
        /// <summary>Cộng <paramref name="deltaDegrees"/> (bội số 90, âm = ngược chiều kim đồng hồ) vào
        /// /Rotate của các trang 1-based trong <paramref name="pageNumbers"/>.</summary>
        public static void RotatePages(string path, IReadOnlyCollection<int> pageNumbers, int deltaDegrees)
        {
            if (deltaDegrees % 90 != 0) throw new ArgumentOutOfRangeException(nameof(deltaDegrees));
            EditInPlace(path, doc =>
            {
                foreach (int pageNumber in pageNumbers)
                {
                    if (pageNumber < 1 || pageNumber > doc.GetNumberOfPages())
                        throw new ArgumentOutOfRangeException(nameof(pageNumbers), $"Page {pageNumber} does not exist.");
                    var page = doc.GetPage(pageNumber);
                    page.SetRotation(NormalizeRotation(page.GetRotation() + deltaDegrees));
                }
            });
        }

        internal static int NormalizeRotation(int degrees) => ((degrees % 360) + 360) % 360;

        /// <summary>Mở file, cho <paramref name="edit"/> sửa, rồi ghi đè lại chính file đó.</summary>
        public static void EditInPlace(string path, Action<PdfDocument> edit)
        {
            byte[] original = File.ReadAllBytes(path);
            string tempPath = path + ".xtedit.tmp";
            try
            {
                using (var reader = new PdfReader(new MemoryStream(original)))
                using (var writer = new PdfWriter(tempPath))
                using (var doc = new PdfDocument(reader, writer, new StampingProperties().UseAppendMode()))
                {
                    edit(doc);
                }

                CopyOverWithRetry(tempPath, path);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch { /* best effort */ }
            }
        }

        /// <summary>Handle PDFium đóng bất đồng bộ; thêm vài nhịp retry cho trường hợp handle vừa nhả
        /// hoặc antivirus/indexer đang giữ file tạm thời.</summary>
        private static void CopyOverWithRetry(string source, string destination)
        {
            const int attempts = 20;
            for (int i = 1; ; i++)
            {
                try
                {
                    File.Copy(source, destination, overwrite: true);
                    return;
                }
                catch (IOException) when (i < attempts)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }
}
