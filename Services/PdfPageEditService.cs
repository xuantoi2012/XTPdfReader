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

        /// <summary>Mở file, cho <paramref name="edit"/> sửa, rồi NỐI bản cập nhật (incremental update) vào cuối chính file đó.
        /// Không đọc cả file vào RAM, không chép lại phần cũ ra file tạm: file 165 MB chỉ được ghi thêm vài KB.</summary>
        public static void EditInPlace(string path, Action<PdfDocument> edit)
            => EditInPlace(path, edit, PdfPermissionOperation.Modify);

        internal static void EditInPlace(string path, Action<PdfDocument> edit, PdfPermissionOperation operation)
        {
            PdfPermissionPolicy.EnsureAllowed(path, operation);
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
            }
            finally
            {
                source.Close();
            }
            if (update.Length > 0) AppendWithRetry(path, length, update);
        }

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
                        throw new InvalidOperationException("The file was changed by another program while it was being saved. Nothing was written.");
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
