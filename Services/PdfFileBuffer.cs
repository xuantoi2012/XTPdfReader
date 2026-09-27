using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// Bước #0 tối ưu hiệu năng (phương án C): PDFium không đọc file trên ổ mạng trực tiếp mà đọc qua
    /// <see cref="PdfBlockCache"/> — bộ đệm cả file chia khối 256 KB, nạp dần bởi 1 luồng đọc nền. Lệnh đọc
    /// của trang đang xem được chen lên trước đọc nền.
    ///
    /// Vì sao không đọc hết file rồi FPDF_LoadMemDocument64 (thiết kế #0 đầu tiên): trong ~8 s đọc nền, lệnh
    /// đọc nhỏ của PDFium phải xếp hàng sau khối đọc nền trên cùng đường truyền. Đo bằng
    /// <c>PdfBench window</c> trên file thật 163 MB ở ổ P: (~21 MB/s, đã chạm trần băng thông, đọc song song
    /// không nhanh hơn): parse trang mới TB 1041–1154 ms khi không đọc nền, 1637–1739 ms (max 11–12 s) khi
    /// đọc nền kiểu cũ, 116–127 ms với bộ đệm khối.
    ///
    /// Giới hạn: file &gt; <see cref="MaxFileBytes"/> hoặc vượt tổng ngân sách <see cref="TotalBudgetBytes"/>
    /// thì không đệm — PDFium mở file như cũ (FPDF_LoadDocument), tránh ăn hết RAM trên máy yếu.
    /// </summary>
    public static class PdfFileBuffer
    {
        /// <summary>File lớn hơn mức này không đệm vào RAM.</summary>
        public const long MaxFileBytes = 500L * 1024 * 1024;

        /// <summary>Tổng RAM tối đa cho mọi file đang đệm: min(2 GB, 1/4 RAM máy).</summary>
        public static readonly long TotalBudgetBytes = Math.Min(2L * 1024 * 1024 * 1024,
            Math.Max(256L * 1024 * 1024, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4));

        private static readonly ConcurrentDictionary<string, PdfBlockCache> _entries = new(StringComparer.OrdinalIgnoreCase);
        private static long _reservedBytes;

        public static long ReservedBytes => Interlocked.Read(ref _reservedBytes);

        /// <summary>Bộ đệm của file (đường dẫn đã chuẩn hoá), đã tăng số tham chiếu — người gọi phải
        /// <see cref="PdfBlockCache.Release"/> khi xong (với PDFium: SAU FPDF_CloseDocument). Chưa có thì tạo và
        /// bắt đầu đọc nền. null = không đệm được (file quá lớn, hết ngân sách, không đọc được) → đọc file như cũ.</summary>
        public static PdfBlockCache? Acquire(string normalizedPath)
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

            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (_entries.TryGetValue(normalizedPath, out var existing))
                {
                    if (existing.Length == info.Length && existing.LastWriteUtc == info.LastWriteTimeUtc)
                    {
                        if (existing.TryAddRef()) return existing;
                        continue; // vừa bị bỏ giữa chừng — thử lại
                    }
                    Invalidate(normalizedPath); // file đổi trên ổ mạng (người khác lưu đè) → đọc lại
                }

                long length = info.Length;
                if (length <= 0 || length > MaxFileBytes) return null;
                if (Interlocked.Add(ref _reservedBytes, length) > TotalBudgetBytes)
                {
                    Interlocked.Add(ref _reservedBytes, -length);
                    return null;
                }

                PdfBlockCache created;
                try
                {
                    created = new PdfBlockCache(normalizedPath, length, info.LastWriteTimeUtc,
                        onFreed: () => Interlocked.Add(ref _reservedBytes, -length));
                }
                catch
                {
                    Interlocked.Add(ref _reservedBytes, -length);
                    return null;
                }

                if (!_entries.TryAdd(normalizedPath, created))
                {
                    created.Release(); // luồng khác vừa tạo trước — dùng bản đó
                    continue;
                }
                created.StartBackgroundRead();
                if (created.TryAddRef()) return created; // 1 tham chiếu của registry + 1 của người gọi
                // (vừa bị Invalidate ngay sau khi thêm — thử lại từ đầu)
            }
            return null;
        }

        /// <summary>File đang có bộ đệm khối (mọi bản PDFium đọc chung, file chỉ qua mạng 1 lần).</summary>
        public static bool IsBuffered(string normalizedPath) => _entries.ContainsKey(normalizedPath);

        /// <summary>File sắp bị ghi đè (xoay trang, annotation) hoặc đã đổi — bỏ bộ đệm cũ. Document PDFium
        /// đang dùng nó vẫn đọc được tới khi đóng (bộ nhớ chỉ trả khi mọi tham chiếu đã Release).</summary>
        public static void Invalidate(string normalizedPath)
        {
            if (_entries.TryRemove(normalizedPath, out var entry)) entry.Release();
        }

        /// <summary>Bỏ bộ đệm của các file không còn mở.</summary>
        public static void ReleaseExcept(Func<string, bool> isActive)
        {
            foreach (var key in _entries.Keys)
                if (!isActive(key)) Invalidate(key);
        }
    }

    /// <summary>
    /// Bộ đệm cả file chia khối <see cref="BlockSize"/>. Khối chưa có → đọc ngay khối đó (lệnh của trang đang
    /// xem); luồng nền lấp dần các khối còn thiếu theo thứ tự, mỗi lần 1 khối, và đứng chờ khi có lệnh đọc của
    /// trang đang chờ. Khối đã có thì không bao giờ đổi → đọc ra không cần khoá. Mỗi khối chỉ được đọc qua mạng
    /// 1 lần: khối đang được luồng khác đọc (luồng nền, hoặc bản PDFium/iText khác) thì chờ luồng đó.
    ///
    /// An toàn đa luồng: PDFium (dưới gate toàn cục) và iText (PdfLayerService, ngoài gate) có thể đọc cùng lúc.
    /// </summary>
    public sealed class PdfBlockCache
    {
        /// <summary>Cỡ khối — cũng là cỡ mỗi lần đọc nền. Đo trên ổ P: khối 256 KB cho độ trễ trang tốt nhất
        /// (khối nhỏ hơn: quá nhiều lần hỏi-đáp; lớn hơn: lệnh của trang chờ khối nền lâu hơn).</summary>
        public const int BlockSize = 256 * 1024;

        private readonly Func<bool, IBlockFile> _open;
        private readonly byte[] _data;
        // Trạng thái từng khối: Missing → Loading (đúng 1 luồng đang đọc) → Present (ghi bằng Volatile.Write SAU khi
        // chép dữ liệu, không bao giờ đổi nữa). Luồng cần khối đang Loading thì chờ luồng kia, không đọc trùng.
        private const int Missing = 0, Present = 1, Loading = 2;
        private readonly int[] _present;
        private int _presentCount;
        private readonly object _lock = new();
        private int _foregroundWaiting;
        private readonly IBlockFile _file;          // cho lệnh đọc của trang (đọc theo vị trí, không seek chung)
        private readonly CancellationTokenSource _stop = new();
        private readonly Action _onFreed;
        private int _refs = 1;

        internal PdfBlockCache(string path, long length, DateTime lastWriteUtc, Action onFreed)
            : this(length, lastWriteUtc, onFreed, sequential => new FileBlockFile(path, sequential))
        {
        }

        /// <param name="open">Mở 1 nguồn đọc (true = cho luồng nền, đọc tuần tự). PdfBench truyền bản đọc không
        /// qua cache Windows / giả lập ổ mạng để đo đúng code này.</param>
        internal PdfBlockCache(long length, DateTime lastWriteUtc, Action onFreed, Func<bool, IBlockFile> open)
        {
            Length = length;
            LastWriteUtc = lastWriteUtc;
            _onFreed = onFreed;
            _open = open;
            _data = GC.AllocateUninitializedArray<byte>(checked((int)length));
            _present = new int[(int)((length + BlockSize - 1) / BlockSize)];
            _file = open(false);
        }

        public long Length { get; }
        public DateTime LastWriteUtc { get; }
        public bool IsComplete => Volatile.Read(ref _presentCount) == _present.Length;

        internal bool TryAddRef()
        {
            while (true)
            {
                int refs = Volatile.Read(ref _refs);
                if (refs <= 0) return false;
                if (Interlocked.CompareExchange(ref _refs, refs + 1, refs) == refs) return true;
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _refs) != 0) return;
            _stop.Cancel();
            lock (_lock) Monitor.PulseAll(_lock);
            _file.Dispose();
            _onFreed();
        }

        /// <summary>Chép [position, position+count) sang bộ nhớ native (callback m_GetBlock của PDFium).</summary>
        public bool CopyTo(long position, IntPtr destination, int count)
        {
            if (!EnsureRange(position, count)) return false;
            System.Runtime.InteropServices.Marshal.Copy(_data, (int)position, destination, count);
            return true;
        }

        /// <summary>Chép [position, position+count) sang mảng (iText đọc /OCProperties).</summary>
        public bool CopyTo(long position, byte[] destination, int offset, int count)
        {
            if (!EnsureRange(position, count)) return false;
            Buffer.BlockCopy(_data, (int)position, destination, offset, count);
            return true;
        }

        private bool EnsureRange(long position, int count)
        {
            if (position < 0 || count < 0 || position + count > Length) return false;
            if (count == 0) return true;
            int first = (int)(position / BlockSize), last = (int)((position + count - 1) / BlockSize);
            for (int block = first; block <= last; block++)
                if (Volatile.Read(ref _present[block]) != Present && !ReadBlockNow(block)) return false;
            return true;
        }

        /// <summary>Lệnh của trang đang xem: đọc ngay khối còn thiếu (hoặc chờ luồng đang đọc nó), luồng nền
        /// đứng chờ tới khi xong.</summary>
        private bool ReadBlockNow(int block)
        {
            Interlocked.Increment(ref _foregroundWaiting);
            long start = Stopwatch.GetTimestamp();
            byte[]? scratch = null;
            try
            {
                if (!TryClaim(block, wait: true)) return Volatile.Read(ref _present[block]) == Present;
                scratch = ArrayPool<byte>.Shared.Rent(BlockSize);
                bool ok = false;
                try
                {
                    ok = ReadFromFile(_file, block, scratch);
                }
                finally
                {
                    Finish(block, ok ? scratch : null); // lỗi đọc: trả khối về Missing, luồng đang chờ tự thử đọc lại
                }
                if (ok) RenderDiagnostics.FileBlockRead.Record(start);
                return ok;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (scratch != null) ArrayPool<byte>.Shared.Return(scratch);
                if (Interlocked.Decrement(ref _foregroundWaiting) == 0)
                    lock (_lock) Monitor.PulseAll(_lock);
            }
        }

        /// <summary>Nhận quyền đọc khối (Missing → Loading). true = luồng này phải đọc rồi gọi <see cref="Finish"/>.
        /// false = khối đã có (sau khi chờ luồng khác đọc xong nếu <paramref name="wait"/>), hoặc — khi không chờ —
        /// đang có luồng khác đọc.</summary>
        private bool TryClaim(int block, bool wait)
        {
            lock (_lock)
            {
                while (true)
                {
                    int state = _present[block];
                    if (state == Present) return false;
                    if (state == Missing)
                    {
                        _present[block] = Loading;
                        return true;
                    }
                    if (!wait) return false;
                    Monitor.Wait(_lock); // Finish của luồng đang đọc sẽ PulseAll
                }
            }
        }

        private bool ReadFromFile(IBlockFile file, int block, byte[] scratch)
        {
            long offset = (long)block * BlockSize;
            int size = (int)Math.Min(BlockSize, Length - offset), read = 0;
            while (read < size)
            {
                int n = file.Read(offset + read, scratch.AsSpan(read, size - read));
                if (n <= 0) return false; // file bị cắt ngắn giữa chừng
                read += n;
            }
            return true;
        }

        /// <summary>Kết thúc lượt đọc đã <see cref="TryClaim"/>: có dữ liệu → Present, lỗi (null) → Missing.</summary>
        private void Finish(int block, byte[]? scratch)
        {
            long offset = (long)block * BlockSize;
            int size = (int)Math.Min(BlockSize, Length - offset);
            lock (_lock)
            {
                if (scratch != null)
                {
                    // Luồng này đang giữ Loading nên không ai khác ghi vùng này; người đọc chỉ đọc sau khi thấy Present.
                    Buffer.BlockCopy(scratch, 0, _data, (int)offset, size);
                    Volatile.Write(ref _present[block], Present);
                    _presentCount++;
                }
                else
                {
                    _present[block] = Missing;
                }
                Monitor.PulseAll(_lock);
            }
        }

        internal void StartBackgroundRead()
            => Task.Factory.StartNew(BackgroundRead, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        private void BackgroundRead()
        {
            long start = Stopwatch.GetTimestamp();
            var token = _stop.Token;
            byte[] scratch = new byte[BlockSize];
            try
            {
                using var handle = _open(true);
                for (int block = 0; block < _present.Length; block++)
                {
                    if (Volatile.Read(ref _present[block]) != Missing) continue; // trang đang xem đã nạp / đang đọc khối này
                    lock (_lock)
                    {
                        // Nhường trang đang xem: không chiếm đường truyền khi nó đang chờ khối.
                        while (Volatile.Read(ref _foregroundWaiting) > 0 && !token.IsCancellationRequested)
                            Monitor.Wait(_lock, 50);
                    }
                    if (token.IsCancellationRequested) return;
                    if (!TryClaim(block, wait: false)) continue;
                    bool ok = false;
                    try
                    {
                        ok = ReadFromFile(handle, block, scratch);
                    }
                    finally
                    {
                        Finish(block, ok ? scratch : null);
                    }
                    if (!ok) return;
                }
                RenderDiagnostics.FileBufferRead.Record(start);
            }
            catch
            {
                // Lỗi đọc nền (mạng rớt, file bị xoá…): dừng nền, lệnh của trang vẫn tự đọc khối khi cần.
            }
        }
    }

    /// <summary>Nguồn đọc theo vị trí của <see cref="PdfBlockCache"/>.</summary>
    internal interface IBlockFile : IDisposable
    {
        /// <summary>Đọc tối đa destination.Length byte tại offset; trả số byte đọc được (0 = hết file).</summary>
        int Read(long offset, Span<byte> destination);
    }

    internal sealed class FileBlockFile(string path, bool sequential) : IBlockFile
    {
        // FileShare.ReadWrite|Delete: không chặn các thao tác khác trên file (chúng tự đóng lease qua
        // SuspendDocumentAsync + Invalidate trước khi ghi).
        private readonly SafeFileHandle _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, sequential ? FileOptions.SequentialScan : FileOptions.RandomAccess);

        public int Read(long offset, Span<byte> destination) => RandomAccess.Read(_handle, destination, offset);
        public void Dispose() => _handle.Dispose();
    }
}
