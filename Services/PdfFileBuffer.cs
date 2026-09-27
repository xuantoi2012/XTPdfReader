using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// Bước #0 tối ưu hiệu năng: đọc tuần tự CẢ file PDF vào RAM một lần, để PDFium parse trang từ bộ nhớ
    /// (FPDF_LoadMemDocument64) thay vì tự seek/đọc từng khối nhỏ qua ổ mạng — mỗi lần seek qua SMB là 1
    /// vòng hỏi-đáp, dao động rất lớn theo tải mạng (đo thật trên ổ P: parse/trang mới 43 ms ↔ 449 ms).
    ///
    /// Đọc NỀN, không chặn lần mở đầu: trong lúc đang đọc, PdfThumbnailService vẫn mở file bằng
    /// FPDF_LoadDocument như cũ (trang đầu hiện nhanh như trước); đọc xong → <see cref="BufferReady"/> báo
    /// để lease cũ được thay bằng lease đọc từ RAM.
    ///
    /// Giới hạn: file &gt; <see cref="MaxFileBytes"/> hoặc vượt tổng ngân sách <see cref="TotalBudgetBytes"/>
    /// thì KHÔNG đệm (dùng cách đọc cũ) — tránh ăn hết RAM trên máy yếu.
    /// </summary>
    public static class PdfFileBuffer
    {
        /// <summary>File lớn hơn mức này không đọc vào RAM (theo đề xuất: ~500 MB).</summary>
        public const long MaxFileBytes = 500L * 1024 * 1024;

        /// <summary>Tổng RAM tối đa cho mọi file đang đệm: min(2 GB, 1/4 RAM máy).</summary>
        public static readonly long TotalBudgetBytes = Math.Min(2L * 1024 * 1024 * 1024,
            Math.Max(256L * 1024 * 1024, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4));

        private sealed class Entry
        {
            public required long Length { get; init; }
            public required DateTime LastWriteUtc { get; init; }
            public required Task<byte[]?> Task { get; init; }
            public CancellationTokenSource Cancel { get; } = new();
        }

        private static readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private static long _reservedBytes;

        /// <summary>Đọc xong 1 file (đường dẫn đã chuẩn hoá) — PdfThumbnailService thay lease đọc-từ-file bằng lease RAM.</summary>
        public static event Action<string>? BufferReady;

        public static long ReservedBytes => Interlocked.Read(ref _reservedBytes);

        /// <summary>Buffer đã đọc xong và còn khớp file trên đĩa (độ dài + thời điểm ghi), hoặc null. Nếu chưa có
        /// thì bắt đầu đọc nền (khi còn ngân sách).</summary>
        public static byte[]? TryGetOrStart(string normalizedPath)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(normalizedPath);
                if (!info.Exists) return null;
            }
            catch
            {
                return null;
            }

            if (_entries.TryGetValue(normalizedPath, out var entry))
            {
                if (entry.Length == info.Length && entry.LastWriteUtc == info.LastWriteTimeUtc)
                    return entry.Task.IsCompletedSuccessfully ? entry.Task.Result : null;
                Invalidate(normalizedPath); // file đổi trên ổ mạng (người khác lưu đè) → đọc lại
            }

            Start(normalizedPath, info);
            return null;
        }

        private static void Start(string path, FileInfo info)
        {
            long length = info.Length;
            if (length <= 0 || length > MaxFileBytes || length > int.MaxValue) return;
            if (Interlocked.Add(ref _reservedBytes, length) > TotalBudgetBytes)
            {
                Interlocked.Add(ref _reservedBytes, -length);
                return;
            }

            var cancel = new CancellationTokenSource();
            Entry? entry = null;
            entry = new Entry
            {
                Length = length,
                LastWriteUtc = info.LastWriteTimeUtc,
                Task = Task.Run(() => ReadAll(path, length, cancel.Token))
            };
            if (!_entries.TryAdd(path, entry))
            {
                Interlocked.Add(ref _reservedBytes, -length);
                return;
            }
            entry.Cancel.Token.Register(() => cancel.Cancel());
            _ = entry.Task.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result != null && _entries.TryGetValue(path, out var current) && ReferenceEquals(current, entry))
                    BufferReady?.Invoke(path);
                else if (!t.IsCompletedSuccessfully || t.Result == null)
                    Remove(path, entry);
            }, TaskScheduler.Default);
        }

        private static byte[]? ReadAll(string path, long length, CancellationToken token)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                // Pinned (POH): PDFium giữ con trỏ vào buffer suốt đời document, GC không được dời nó.
                var data = GC.AllocateUninitializedArray<byte>((int)length, pinned: true);
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                int read = 0;
                while (read < data.Length)
                {
                    token.ThrowIfCancellationRequested();
                    int n = fs.Read(data, read, Math.Min(4 << 20, data.Length - read)); // khối 4 MB, tuần tự
                    if (n <= 0) return null; // file bị cắt ngắn giữa chừng
                    read += n;
                }
                RenderDiagnostics.FileBufferRead.Record(start);
                return data;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>File sắp bị ghi đè (xoay trang, annotation) hoặc đã đổi — bỏ buffer cũ.</summary>
        public static void Invalidate(string normalizedPath)
        {
            if (_entries.TryGetValue(normalizedPath, out var entry)) Remove(normalizedPath, entry);
        }

        /// <summary>Bỏ buffer của các file không còn mở.</summary>
        public static void ReleaseExcept(Func<string, bool> isActive)
        {
            foreach (var key in _entries.Keys)
                if (!isActive(key)) Invalidate(key);
        }

        private static void Remove(string path, Entry entry)
        {
            if (!_entries.TryRemove(new System.Collections.Generic.KeyValuePair<string, Entry>(path, entry))) return;
            entry.Cancel.Cancel();
            Interlocked.Add(ref _reservedBytes, -entry.Length);
        }
    }
}
