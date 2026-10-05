using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
            EditInPlace(path, pageNumbers.Count == 1 ? "Rotated 1 page" : $"Rotated {pageNumbers.Count} pages", doc =>
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

        /// <summary>Mở file, cho <paramref name="edit"/> sửa, rồi NỐI bản cập nhật (incremental update) vào cuối chính file đó.
        /// Không đọc cả file vào RAM, không chép lại phần cũ ra file tạm: file 165 MB chỉ được ghi thêm vài KB.</summary>
        public static void EditInPlace(string path, Action<PdfDocument> edit)
            => EditInPlace(path, edit, PdfPermissionOperation.Modify);

        /// <summary>Như trên, kèm 1 dòng lịch sử <paramref name="action"/> (ai, lúc nào, làm gì) ghi trong cùng lần lưu — xem <see cref="XTHistory"/>.</summary>
        public static void EditInPlace(string path, string action, Action<PdfDocument> edit)
            => EditInPlace(path, action, edit, PdfPermissionOperation.Modify);

        internal static void EditInPlace(string path, Action<PdfDocument> edit, PdfPermissionOperation operation)
            => EditInPlace(path, null, edit, operation);

        /// <summary>Có người khác vừa lưu chen vào file nên lần lưu của ta đã đọc lại bản mới và áp lại (đường dẫn file).</summary>
        internal static event Action<string>? ConflictRetried;

        internal static void EditInPlace(string path, string? action, Action<PdfDocument> edit, PdfPermissionOperation operation)
        {
            PdfPermissionPolicy.EnsureAllowed(path, operation);
            // Người khác (máy khác, cùng thư mục mạng) vừa lưu vào file giữa lúc ta đọc và lúc ta ghi: đọc lại bản mới và áp lại đúng thao tác
            // của mình lên đó, rồi nối tiếp. Nhờ vậy 2 người lưu lần lượt thì cả hai thay đổi đều nằm trong file.
            for (int attempt = 1; ; attempt++)
            {
                var update = new MemoryStream();
                long length;
                var source = new SharedFileSource(path);
                try
                {
                    length = source.Length();
                    // Append mode chép nguyên các byte cũ ra writer rồi mới ghi phần cập nhật: TailStream bỏ phần chép, giữ phần cập nhật.
                    var properties = new ReaderProperties();
                    if (PdfThumbnailService.TryGetDocumentPassword(path) is { Length: > 0 } password)
                        properties.SetPassword(Encoding.UTF8.GetBytes(password));
                    using var doc = new PdfDocument(new PdfReader(source, properties).SetUnethicalReading(true), new PdfWriter(new TailStream(length, update)),
                        new StampingProperties().UseAppendMode());
                    edit(doc);
                    if (action != null) XTHistory.Append(doc, action, length);
                }
                finally
                {
                    source.Close();
                }
                if (update.Length == 0) return;
                try { AppendWithRetry(path, length, update); return; }
                catch (FileChangedException) when (attempt < MaxConflictRetries) { ConflictRetried?.Invoke(path); /* đọc lại bản mới */ }
            }
        }

        private const int MaxConflictRetries = 6;

        /// <summary>File đã bị người / chương trình khác ghi thêm giữa lúc đọc và lúc ghi. Chưa ghi gì.</summary>
        internal sealed class FileChangedException()
            : InvalidOperationException("The file was changed by another program while it was being saved. Nothing was written.");

        /// <summary>Ghi phần cập nhật vào cuối file. Handle PDFium đóng bất đồng bộ, antivirus/indexer có thể giữ file một nhịp: thử lại.
        /// Ghi hỏng giữa chừng thì cắt file về đúng độ dài cũ.</summary>
        private static void AppendWithRetry(string path, long expectedLength, MemoryStream update)
        {
            const int attempts = 20;
            for (int i = 1; ; i++)
            {
                try
                {
                    using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                    if (file.Length != expectedLength)
                        throw new FileChangedException();
                    file.Seek(0, SeekOrigin.End);
                    try
                    {
                        update.Position = 0;
                        update.CopyTo(file);
                        file.Flush(flushToDisk: true);
                    }
                    catch
                    {
                        try { file.SetLength(expectedLength); } catch { /* reported by the rethrow */ }
                        throw;
                    }
                    return;
                }
                catch (IOException) when (i < attempts)
                {
                    Thread.Sleep(100);
                }
            }
        }

        /// <summary>Đầu ra của 1 lần ghi append mode: bỏ <c>skip</c> byte đầu (bản chép nội dung cũ), giữ phần sau vào <c>tail</c>.</summary>
        private sealed class TailStream(long skip, Stream tail) : Stream
        {
            private long _position;

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _position;
            public override long Position { get => _position; set => throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count)
            {
                long end = _position + count;
                if (end > skip)
                {
                    int from = (int)Math.Max(0, skip - _position);
                    tail.Write(buffer, offset + from, count - from);
                }
                _position = end;
            }

            public override void WriteByte(byte value)
            {
                if (_position >= skip) tail.WriteByte(value);
                _position++;
            }

            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
